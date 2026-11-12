using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Data;
using SmartFactory.MES.Modules.QA.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.QA;

/// <summary>
/// QA＝手冊 Q&amp;A 助理（規格 §十、Phase 1）。**選用模組**，由
/// <c>Features:QaAssistant:Enabled</c> 控制，預設關閉（§17.2）。
///
/// <para>註冊走 <c>IAppModule</c>，由 <c>Program.cs</c> 的組件掃描自動載入——<b>不改 Program.cs</b>。
/// 掃描用的是 <c>AppDomain.CurrentDomain.GetAssemblies()</c>（只掃已載入的組件），
/// 所以本模組必須留在主組件內；拆成獨立 csproj 會因 .NET 延遲載入而靜默不註冊（§17.11）。</para>
///
/// <para><b>S1 階段不需要任何模型、不需要 Ollama、不需要 pgvector</b>（§10.9）：
/// 這一段只做手冊解析、切塊、雙語拆分、雙字切詞與詞彙檢索。</para>
/// </summary>
public class QAModule : IAppModule
{
    public const string EnabledKey = "Features:QaAssistant:Enabled";

    /// <summary>
    /// <see cref="RegisterServices"/> 當下的判定結果，供 <see cref="Configure"/> 使用。
    ///
    /// <b>不可在 Configure 裡重讀組態。</b><c>Program.cs</c> 對每個模組保留同一個實例、
    /// 先跑完全部 <c>RegisterServices</c> 再跑 <c>Configure</c>，而這兩個階段拿到的
    /// <c>IConfiguration</c> 是不同物件（<c>builder.Configuration</c> vs <c>app.Configuration</c>）。
    /// 在 <c>WebApplicationFactory</c> 底下用 <c>ConfigureAppConfiguration</c> 追加來源時，
    /// 新來源只在後者可見——於是 Configure 以為功能開著、去解析一個從未註冊的 QaDbContext，
    /// 應用程式**啟動即崩潰**（實際踩到過，2026-08-05）。
    /// </summary>
    private bool _registered;

    internal static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue(EnabledKey, false);

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // 解析器本身沒有外部相依，關燈時註冊也無害——但它同時是離線工具（重建索引）的入口，
        // 留著可以讓「功能關閉但想先建索引看看」這件事不必改組態。
        services.AddSingleton<IManualDocParser, ManualDocParser>();

        _registered = IsEnabled(configuration);
        if (!_registered) return;

        services.AddDbContext<QaDbContext>((sp, opt) =>
        {
            opt.UseNpgsql(ResolveConnectionString(sp, configuration),
                o => o.MigrationsHistoryTable(QaDbContext.MigrationsHistoryTable));
        });

        services.AddScoped<IDocIndexBuilder, DocIndexBuilder>();
        services.AddScoped<IExtractManifestImporter, ExtractManifestImporter>();
        services.AddScoped<IManualSearchService, ManualSearchService>();

        // Phase 2 的第一個單元：把報表工具組成 function-calling schema，並在**組 schema 的
        // 階段**套用 L1 權限過濾（§8.4、§11.1）。目前呼叫端只有測試——Phase 2 的
        // 提問管線是下一步。之所以現在就進 DI：相依是 RPS 的 IReportToolAccessBatch，
        // 跨模組解析要靠啟動時的整合測試才抓得到問題，晚註冊等於晚一步發現。
        services.AddScoped<IQaToolSchemaBuilder, QaToolSchemaBuilder>();
        services.AddScoped<IDocVectorStore, DocVectorStore>();

        // 推論服務（§17.5）。具名 client 的逾時與 OllamaClient 的錯誤訊息共用
        // ResolveTimeout，否則會出現「實際 100 秒斷線、訊息說 180 秒」的診斷矛盾。
        services.AddHttpClient(OllamaClient.HttpClientName, c =>
        {
            c.BaseAddress = OllamaClient.ResolveBaseUrl(configuration);
            c.Timeout = OllamaClient.ResolveTimeout(configuration);
        });

        // 資料出境閘門（§17.11.8）。**singleton 且在此刻判定一次**：具名 client 的
        // BaseAddress 也是啟動時決定的（上面幾行），兩者必須看到同一個 URL。
        // 每次呼叫重讀組態的話，會出現「閘門依新 URL 放行／擋下，而 HttpClient 還在打舊的」
        // 這種對不起來的形狀——症狀是稽核紀錄與實際去向不一致，最難查。
        // ⚠️ 刻意用工廠**閉包捕捉這個 configuration**，而不是讓 DI 注入 IConfiguration：
        // RegisterServices 與 Configure 兩階段拿到的是不同物件（見 _registered 的註解），
        // 而 BaseAddress 用的是這一份。注入的話，測試以 ConfigureAppConfiguration 追加來源時
        // 閘門會判到一個 HttpClient 從未使用的 URL。
        services.AddSingleton<IInferenceEgressPolicy>(sp =>
            new InferenceEgressPolicy(configuration,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<InferenceEgressPolicy>()));

        // 推論並發閘門（§11.2 C1／C2／C5）。**singleton 是它的全部意義**——
        // 名額計數散在多個實例上就等於沒有限制，而症狀不是錯誤而是「偶爾特別慢」。
        services.AddSingleton<IInferenceGate, InferenceGate>();

        // 一個實作、三個介面。**不要分成三個實例**——那會讓組態在三個地方各讀一次，
        // 而它們對「現在用的是哪顆模型」的答案必須一致（§10.7 的 EmbeddingModel 欄位靠這個）。
        services.AddSingleton<OllamaClient>();
        services.AddSingleton<ILlmClient>(sp => sp.GetRequiredService<OllamaClient>());
        services.AddSingleton<IEmbeddingClient>(sp => sp.GetRequiredService<OllamaClient>());
        services.AddSingleton<IOllamaDiagnostics>(sp => sp.GetRequiredService<OllamaClient>());

        // 第四個介面、同一個實例。分開的理由見 ILlmToolClient 的註解：
        // ILlmClient 的契約是「成功回傳即有可用答案」，而帶工具時空 content 是正常結果。
        services.AddSingleton<ILlmToolClient>(sp => sp.GetRequiredService<OllamaClient>());

        // 第五個介面、同一個實例（§11.2 C4 串流）。分開的理由見 ILlmStreamClient 的註解：
        // 併進 ILlmClient 會讓既有的測試替身全部編不過，而它們量的與串流無關。
        services.AddSingleton<ILlmStreamClient>(sp => sp.GetRequiredService<OllamaClient>());

        // 追問狀態與 NL 查詢稽核（§11.3）。scoped：它寫的是 QaDbContext。
        services.AddScoped<IQaTurnStore, QaTurnStore>();

        // Phase 2 的提問管線（§9.1）。scoped：它經 IReportToolRunner 執行工具，
        // 而工具相依 ApplicationDbContext。
        services.AddScoped<IQaToolQueryService, QaToolQueryService>();

        // 啟動時增量重建索引。只在功能開啟時註冊——關燈的部署連這個服務都不存在，
        // 不會有「背景服務每次啟動都對一張不存在的表發查詢」這種噪音。
        services.AddHostedService<DocIndexBackgroundService>();

        // 擷取物只讀畫面（§10.11.11）。**View 位置一定要註冊**，否則 Razor 只會去找
        // /Views/QaExtract/Index.cshtml，回一個「找不到檢視」的執行期錯誤——建置照樣過。
        services.Configure<RazorViewEngineOptions>(opt =>
        {
            opt.ViewLocationFormats.Add("/Modules/QA/Views/{1}/{0}" + RazorViewEngine.ViewExtension);
        });
    }

    public void Configure(WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<QAModule>();

        // §17.8：關閉與降級都要出聲。靜默停用會讓「怎麼問都沒反應」查不出原因。
        if (!_registered)
        {
            log.LogInformation("手冊 Q&A 助理未啟用（{Key}=false），QaDbContext 與索引皆不建立。", EnabledKey);

            // 兩階段組態不一致：功能實際上沒註冊，但現在讀到的是「開啟」。
            // 多半是把開關寫在只有 app 階段可見的來源（測試常見）。不出聲的話，
            // 表徵只會是「開關開了卻完全沒反應」。
            if (IsEnabled(app.Configuration))
                log.LogWarning(
                    "{Key} 在服務註冊階段為 false、在應用程式階段為 true，功能維持關閉。" +
                    "請把開關放在啟動即可見的來源（環境變數 Features__QaAssistant__Enabled 或 appsettings）。",
                    EnabledKey);

            return;
        }

        if (!app.Configuration.GetValue("Database:AutoMigrate", true))
        {
            log.LogWarning("手冊 Q&A 助理已啟用，但 Database:AutoMigrate=false，" +
                           "{Table} 不會自動建立，需手動執行 migration。", QaDbContext.MigrationsHistoryTable);
            return;
        }

        using var scope = app.Services.CreateScope();
        var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        if (qa.Database.GetPendingMigrations().Any())   // 理由同 Program.cs 的 Migrate()
            qa.Database.Migrate();
        log.LogInformation("手冊 Q&A 助理已啟用，{Table} 已套用至最新。", QaDbContext.MigrationsHistoryTable);

        // 現在就把出境閘門建起來，讓它的判定進啟動日誌（§17.8）。singleton 是延遲建立的，
        // 不碰它的話，「Phase 2 已停用」這件事要等到第一個人提問失敗才會有人知道。
        _ = app.Services.GetRequiredService<IInferenceEgressPolicy>();
    }

    /// <summary>
    /// 取 QaDbContext 的連線字串：優先用顯式覆寫（可指向另一台有 GPU 的機器），
    /// 否則沿用主連線。
    ///
    /// <para>刻意從 <c>DbContextOptions&lt;ApplicationDbContext&gt;</c> 取回，而不是自己讀
    /// <c>ConnectionStrings:DefaultConnection</c>：正式環境的連線字串是由 Program.cs 經
    /// Security DLL **解密**後才交給 EF 的，直接讀組態會拿到密文，
    /// 而失敗形狀是啟動時的連線錯誤——看起來像資料庫掛了。</para>
    /// </summary>
    private static string ResolveConnectionString(IServiceProvider sp, IConfiguration configuration)
    {
        var explicitCs = configuration["Features:QaAssistant:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(explicitCs)) return explicitCs;

        var inherited = sp.GetRequiredService<DbContextOptions<ApplicationDbContext>>()
            .Extensions.OfType<RelationalOptionsExtension>()
            .FirstOrDefault()?.ConnectionString;

        if (string.IsNullOrWhiteSpace(inherited))
            throw new InvalidOperationException(
                "無法沿用主連線字串建立 QaDbContext。請設定 Features:QaAssistant:ConnectionString，" +
                "或關閉 " + EnabledKey + "。");

        return inherited;
    }
}
