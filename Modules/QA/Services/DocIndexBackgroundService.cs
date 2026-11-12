using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Modules.QA.Contracts;

namespace SmartFactory.MES.Modules.QA.Services;

/// <summary>
/// 啟動時做一次手冊索引的增量重建（規格 §10.3）。
///
/// <para><b>為什麼是啟動時而不是排程：</b>手冊是隨程式一起部署的靜態檔，
/// 只有在「換版」的時候才會變——而換版必然伴隨重啟。定時輪詢磁碟是為一個
/// 不會自己改變的東西付固定成本。</para>
///
/// <para>增量的成本接近零：內容沒變的檔案只讀檔算 SHA256 就跳過
/// （實測 134 檔全略過約 0.1 秒），所以「每次啟動都跑」不需要任何開關來省錢；
/// <see cref="RebuildOnStartupKey"/> 存在的理由是排查問題時能關掉，不是效能。</para>
///
/// <para><b>本服務不注入 <c>IUserContext</c>，也不該注入。</b>背景服務沒有 HttpContext，
/// 那些屬性全是 null，任何依賴它的過濾都會靜默變成「查不到任何資料」。
/// 索引建置本來就是全域行為，不屬於任何使用者。</para>
/// </summary>
public class DocIndexBackgroundService : BackgroundService
{
    /// <summary>設為 false 可停用啟動時重建（排查用，非效能考量）。預設 true。</summary>
    public const string RebuildOnStartupKey = "Features:QaAssistant:RebuildIndexOnStartup";

    /// <summary><c>QaTurns</c> 的保留天數（§11.3 的稽核語料）。</summary>
    public const string TurnRetentionDaysKey = "Features:QaAssistant:TurnRetentionDays";

    /// <summary>預設保留 90 天，與 <c>Reports:RetentionDays</c> 同一個量級。</summary>
    public const int DefaultTurnRetentionDays = 90;

    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DocIndexBackgroundService> _log;

    public DocIndexBackgroundService(
        IServiceScopeFactory scopes,
        IConfiguration configuration,
        ILogger<DocIndexBackgroundService> log)
    {
        _scopes = scopes;
        _configuration = configuration;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue(RebuildOnStartupKey, true))
        {
            _log.LogInformation(
                "手冊索引啟動重建已停用（{Key}=false）；檢索仍會使用資料庫中既有的索引，" +
                "手冊若已更新，答案會停留在舊版。", RebuildOnStartupKey);
            return;
        }

        try
        {
            using var scope = _scopes.CreateScope();
            var builder = scope.ServiceProvider.GetRequiredService<IDocIndexBuilder>();

            var result = await builder.RebuildAsync(stoppingToken);

            // 沒有對應畫面的頁面（模組目錄、手冊首頁）是正常的，但數量異常變多
            // 代表反查 HelpDocResolver 失敗——那會讓那些頁面被當成公開內容。
            if (result.PagesWithoutMenuKey > 0)
                _log.LogInformation(
                    "其中 {Count} 頁沒有對應畫面（模組目錄與手冊首頁屬正常）。",
                    result.PagesWithoutMenuKey);

            await TrimTurnsAsync(scope.ServiceProvider, stoppingToken);
            await ImportExtractsAsync(scope.ServiceProvider, stoppingToken);
            await EmbedAsync(builder, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 應用程式正在關閉，不是錯誤。
        }
        catch (Exception ex)
        {
            // **這個 catch 是必要的，不是防禦性冗贅。**
            // .NET 的 BackgroundServiceExceptionBehavior 預設是 StopHost：
            // ExecuteAsync 逸出任何例外會**讓整個應用程式關閉**。
            // 手冊索引是選用功能的附屬品，它失敗不該讓 ERP 開不起來。
            //
            // 最可能的成因是 Features:QaAssistant:Enabled=true 但 migration 未套用
            // （Database:AutoMigrate=false），此時 DocChunks 資料表不存在。
            _log.LogError(ex,
                "手冊索引重建失敗，Q&A 檢索將查不到任何內容。" +
                "常見成因：已啟用 Q&A 但 QaDbContext 的 migration 未套用（Database:AutoMigrate=false）。");
        }
    }

    /// <summary>
    /// 修剪 <c>QaTurns</c> 的保留期（§11.3）。
    ///
    /// <para><b>為什麼在啟動時做，而不是像 <c>AuditLogRetentionJobService</c> 那樣開一個每日排程：</b>
    /// 這張表的成長率有硬性上界。每一列都對應一次 CPU 推論，而 §11.2 實測一題 45–53 秒
    /// 且 <c>InferenceGate</c> 只放行少數並行——單機一天寫不到兩千列，一年不到七十萬列，
    /// 對一張只有等值查詢的表而言不構成任何問題。為它多開一個 <c>BackgroundService</c>
    /// （多一條每日觸發、多一個要驗「跨凌晨真的會跑」的路徑，見六支排程從未執行那次）
    /// 買到的東西不成比例。</para>
    ///
    /// <para>⚠️ <b>代價要講清楚</b>：連續數月不重啟的部署不會修剪。這是知情的取捨——
    /// 上界算得出來，所以「沒修剪」的後果是可預期的資料量，不是失控。</para>
    ///
    /// <para>失敗不中止整輪，理由與擷取物匯入相同：這是附屬品，不該讓索引重建陪葬。</para>
    /// </summary>
    private async Task TrimTurnsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var days = _configuration.GetValue<int?>(TurnRetentionDaysKey);
        if (days is <= 0) return;      // 明確設成 0 或負數＝不修剪（保留全部稽核語料）

        try
        {
            var deleted = await sp.GetRequiredService<IQaTurnStore>()
                .TrimAsync(DateTime.UtcNow.AddDays(-(days ?? DefaultTurnRetentionDays)), ct);

            if (deleted > 0)
                _log.LogInformation("NL 查詢稽核修剪完成：刪除 {Deleted} 列（保留 {Days} 天）。",
                    deleted, days ?? DefaultTurnRetentionDays);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 應用程式正在關閉。
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "NL 查詢稽核修剪失敗，舊列會留著（不影響查詢與追問）。");
        }
    }

    /// <summary>
    /// 匯入擷取物 manifest（§10.11.9）。**排在補嵌入之前**——新匯入的段落要在同一輪拿到向量，
    /// 否則它們得等到下一次啟動才進得了語意通道。
    ///
    /// <para><b>正式機的正常結果是「什麼都沒做」</b>：manifest 不進 publish 產物，
    /// 目錄不存在時匯入器直接返回且一列都不刪（擷取物索引隨 DB 出貨，§10.11.6）。
    /// 所以這裡沒有開關——沒有東西可匯入時它本來就不做事。</para>
    ///
    /// <para>失敗不中止整輪：手冊索引此時已經寫完，而補嵌入還沒跑。
    /// 讓一份寫壞的 manifest 連帶吃掉手冊的向量，是把局部錯誤放大成全域降級。
    /// 但**一定要記成 Error**——擷取物沒進去的症狀是「規則類問題答不出來」，
    /// 那和「手冊裡本來就沒寫」在使用者眼中完全一樣。</para>
    /// </summary>
    private async Task ImportExtractsAsync(IServiceProvider sp, CancellationToken ct)
    {
        try
        {
            await sp.GetRequiredService<IExtractManifestImporter>().ImportAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 應用程式正在關閉。
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "擷取物 manifest 匯入失敗，規則類問題將答不出來（手冊檢索不受影響）。" +
                "常見成因：某份 manifest 的 slug／lang／sourceHash 格式不合法。");
        }
    }

    /// <summary>
    /// 補嵌入。**失敗只降級、不中止**：詞彙通道不需要任何模型，
    /// 推論服務沒開時它必須照常運作——把嵌入失敗升級成整個索引失敗，
    /// 等於讓一個選用相依決定必要功能的存亡。
    ///
    /// <para>但降級一定要出聲（§17.8）：只有語意通道消失時，
    /// 症狀是「答得沒有以前準」，沒有這則 log 就完全查不出原因。</para>
    /// </summary>
    private async Task EmbedAsync(IDocIndexBuilder builder, CancellationToken ct)
    {
        try
        {
            var (embedded, pending) = await builder.EmbedPendingAsync(ct);

            if (embedded > 0)
                _log.LogInformation("手冊索引補嵌入完成：{Embedded} 塊。", embedded);

            if (pending > 0)
                _log.LogWarning(
                    "尚有 {Pending} 塊沒有向量，語意檢索對這些內容不會生效。", pending);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 應用程式正在關閉。已寫入的向量會留著，下次啟動接續。
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "手冊索引補嵌入失敗，Q&A 將只有詞彙檢索可用（詞彙通道不受影響）。" +
                "常見成因：推論服務未啟動、{UrlKey} 指錯、或 {ModelKey} 指定的模型尚未 ollama pull。",
                OllamaClient.UrlKey, OllamaClient.EmbeddingModelKey);
        }
    }
}
