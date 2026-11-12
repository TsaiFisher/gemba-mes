using SmartFactory.MES.Modules._Shared;
using System.Reflection;
using System.Security.Claims;
using SmartFactory.MES.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.RPS.Services;

/// <summary>執行一支工具並攤平成可匯出的表格。</summary>
public readonly record struct ReportToolRunResult(
    string[] Headers,
    IReadOnlyList<object?[]> Rows,
    DateRange? ResolvedRange);

/// <summary>
/// 以 <c>ToolKey</c> 執行工具、把結果攤平成 <c>ExportRequest</c> 的共用路徑（§16.1 S-5）。
///
/// <para><b>S-5 的重點是「共用」</b>：排程產生的 Excel 與畫面按鈕匯出的 Excel 必須走
/// 同一段組裝程式碼，否則兩邊欄位會隨時間漂移，而**沒有任何測試會發現**——
/// 兩份各自都「正確」，只是不一樣。所以攤平邏輯只有這一份。</para>
///
/// <para><b>型別如何還原：</b><c>IReportTool&lt;TReq,TResp&gt;</c> 是泛型契約，而排程拿到的
/// 只有一個字串 <c>ToolKey</c>。請求型別取自 <c>ReportToolCatalog</c> 的靜態映射
/// （<c>RequestType</c>），**不是使用者資料**——§7.0.1-c 禁止的是 <c>Type.GetType()</c>
/// 那種「由輸入決定載入什麼型別」，不是反射本身。</para>
///
/// <para><b>執行本身不再走反射</b>（2026-09-14）：資料列改由
/// <c>IReportToolDescriptor.GetExportRowsAsync</c> 取得。原先以反射叫 <c>ExecuteAsync</c>
/// 再把回應當 <c>IEnumerable</c> 攤平，等於**由這裡猜「資料列在哪」**——回傳包裝型別的
/// 工具就會落進「不是集合」那一支而靜默產出空白報表。現在由各工具自己回答。</para>
/// </summary>
public interface IReportToolRunner
{
    /// <summary>
    /// 依 <paramref name="paramsJson"/> 還原請求並執行。
    /// 參數解不開時**拋例外，不得靜默用預設值執行**（§7.0.1-c）：
    /// 那會產出一份看起來正常、內容卻是預設期間的報表。
    /// </summary>
    /// <param name="timeZoneId">
    /// 這一份報表該用哪個時區解算「今天／昨天／本月」。
    ///
    /// <para>⚠️ <b>刻意做成必填而不是 <c>= null</c> 的選擇性參數</b>，理由與
    /// <c>IReportToolDescriptor.GetExportRowsAsync</c>／<c>Permission</c> 同一個：
    /// 給了預設值，下一個呼叫端就會**繼承到一個碰巧對一半的行為**——
    /// 有 HTTP 上下文時 null 是對的，背景服務裡 null 一律退到 Asia/Taipei，
    /// 而兩者在單一時區部署下結果完全相同，測不出來也看不出來（§10.3 TZ-3）。
    /// 每個呼叫端必須明講自己是哪一種。</para>
    ///
    /// <para>排程路徑傳 <c>ReportScheduleTimeZoneResolver</c> 解出的廠別時區；
    /// 畫面與 LLM 路徑傳 <c>null</c>，由 <c>IUserContext.TimeZoneId</c> 供應。</para>
    /// </param>
    Task<ReportToolRunResult> RunAsync(
        string toolKey, string paramsJson, ClaimsPrincipal? user,
        string? timeZoneId, CancellationToken ct = default);
}

/// <summary>參數 JSON 與目前工具契約對不起來。訊息會進 <c>ReportRun.ErrorMessage</c>。</summary>
public class ReportParamsInvalidException : Exception
{
    public ReportParamsInvalidException(string toolKey, string detail)
        : base(PzText.Get("Rps_ParamsInvalidFmt", toolKey, detail)) { }
}

/// <inheritdoc />
public class ReportToolRunner : IReportToolRunner
{
    /// <summary>與 <c>SavedQueryService</c> 同一組設定：列舉存名稱不存序數，且不啟用任何多型處理。</summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IReportToolCatalog _catalog;
    private readonly IServiceProvider _services;
    private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;
    private readonly ApplicationDbContext _db;

    public ReportToolRunner(
        IReportToolCatalog catalog,
        IServiceProvider services,
        IStringLocalizer<ProjectZReportHeaderResource> headers,
        ApplicationDbContext db)
    {
        _catalog  = catalog;
        _services = services;
        _headers  = headers;
        _db       = db;
    }

    public async Task<ReportToolRunResult> RunAsync(
        string toolKey, string paramsJson, ClaimsPrincipal? user,
        string? timeZoneId, CancellationToken ct = default)
    {
        if (_catalog.Find(toolKey) is not { } descriptor)
            throw new ReportParamsInvalidException(toolKey, PzText.Get("Rps_ToolGone"));

        var request = Deserialize(descriptor, paramsJson);

        // 時區注入在這一點，而不是在每支工具裡：工具拿到的是一個已經知道自己該用哪個時區的請求。
        // 用 ??= 而不是直接覆寫——呼叫端在 ParamsJson 裡顯式指定過的時區應該贏過排程推得的那個，
        // 「誰顯式講了就聽誰的」比「後寫的贏」容易在半年後看懂。
        request.TimeZoneId ??= timeZoneId;

        // 廠別同理，而且理由更強：ReportRequestBase.PlantId 的契約寫的是
        // 「null ＝ 依執行 scope 的授權範圍（ApplicationDbContext.CurrentPlantId）」，
        // 但十六支工具沒有一支照著做——它們一律 `request.PlantId ?? ""` 之後拿去字面比對，
        // 於是 null 變成一個比不到任何廠的值。
        //
        // 2026-09-17 實測到的症狀：從排程畫面建立的排程（ParamsJson 是 {}）產出的報表
        // **格式正確、狀態成功、零筆**。派工器在呼叫這裡之前已經把
        // db.CurrentPlantId 設成 definition.PlantId（§7.8 S4），所以答案一直都在手上，
        // 只是沒有人把它交給請求。
        //
        // 修在這一點而不是逐支工具：契約本來就寫在基底類別上，讓十六支各自記得退回
        // CurrentPlantId，等於把正確性押在「下一個寫工具的人會記得」——
        // 那正是 GetExportRowsAsync 當初要離開的那種設計。
        // 🚨 有使用者身分（＝互動路徑，目前只有自然語言查詢）時，參數不得指定別的廠別。
        //    PlantId 不在給模型的 schema 裡，正常不會出現；出現就是模型被提問誘導填的（安全評估 2026-10-07 #2）。
        //    ??= 只補空值、不覆寫，而略過全域廠別過濾的工具（TrialBalance 等）是照這個值查的——
        //    放行等於讓使用者用一句話查到沒有權限的廠別。QA 端已先剔除，這裡是第二道。
        //    排程路徑 user 為 null：派工器已把 CurrentPlantId 設成排程的廠別，不受影響。
        if (user is not null && request.PlantId is { } asked &&
            !string.Equals(asked, _db.CurrentPlantId, StringComparison.Ordinal))
            throw new ReportParamsInvalidException(toolKey, PzText.Get("Rps_PlantNotCurrentFmt", asked));

        request.PlantId ??= _db.CurrentPlantId;

        // 「資料列在哪」由工具自己回答（IReportToolDescriptor.GetExportRowsAsync），
        // 不再由這裡把回應當 IEnumerable 猜。猜的那個版本會讓回傳包裝型別的工具
        // （InventoryValuation／SlowMovingInventory）靜默產出空白報表，見該契約成員的說明。
        var items = await descriptor.GetExportRowsAsync(request, user, ct);

        // 欄序、欄頭與取值全部由工具宣告（IReportToolDescriptor.GetExportColumns），
        // 畫面匯出走的是同一份。標題在此刻解析——派工器在呼叫前已把
        // CurrentUICulture 設成該收件人的語系，所以同一份報表的兩個語系版本
        // 會拿到各自的欄頭，而不是兩份都印英文屬性名。
        var columns = descriptor.GetExportColumns(request);
        var (headers, rows) = Project(columns, items, _headers);
        return new ReportToolRunResult(headers, rows, await ResolvedRangeOfAsync(descriptor, request, ct));
    }

    private static ReportRequestBase Deserialize(IReportToolDescriptor descriptor, string paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson))
            return (ReportRequestBase)Activator.CreateInstance(descriptor.RequestType)!;

        try
        {
            var request = JsonSerializer.Deserialize(paramsJson, descriptor.RequestType, Json);
            return request as ReportRequestBase
                ?? throw new ReportParamsInvalidException(descriptor.ToolKey, PzText.Get("Rps_ParamsEmpty"));
        }
        catch (JsonException ex)
        {
            throw new ReportParamsInvalidException(descriptor.ToolKey, ex.Message);
        }
    }

    /// <summary>
    /// 執行紀錄（<c>ReportRun.ResolvedFrom／ResolvedTo</c>）與收件匣顯示的查詢期間。
    ///
    /// <para>2026-10-07 之前這裡只回 <c>AbsolutePeriod</c>，而排程存的一律是相對期間
    /// （<c>{"period":"Yesterday"}</c>）→ 絕對期間恆為 null → **每一筆執行紀錄的期間都是空的**
    /// （ProjectZ 庫 19 筆全空），收件匣的期間欄也從沒顯示過。人工驗收 TZ-3 要比對的正是這兩欄。</para>
    ///
    /// <para>用與工具相同的請求（時區、廠別都已在上面注入）再解一次，所以記下的就是工具實際查的區間。</para>
    /// </summary>
    private async Task<DateRange?> ResolvedRangeOfAsync(
        IReportToolDescriptor descriptor, ReportRequestBase request, CancellationToken ct)
    {
        if (request.AbsolutePeriod is { } absolute) return absolute;
        if (!descriptor.RequiresPeriod) return null;

        var resolver = _services.GetService(typeof(IRelativePeriodResolver)) as IRelativePeriodResolver;
        return resolver is null ? null : await resolver.ResolveAsync(request, ct: ct);
    }

    /// <summary>
    /// 依工具宣告的欄位把資料列投影成標題列＋資料列。
    ///
    /// <para>2026-09-15 之前這裡是 <c>Flatten</c>：以反射取 VM 的公開屬性當欄位、
    /// **屬性名當標題**，欄序取自 <c>MetadataToken</c>。那個版本的兩個問題都不會報錯——
    /// 標題是 `ProductCode` 這種英文屬性名（連 R-7 認真產出的兩個語系版本也都是英文），
    /// 而欄位集合是「VM 剛好有哪些屬性」，與畫面匯出的欄位並不一致。</para>
    ///
    /// <para>現在兩邊共用工具宣告的同一份欄位，所以**不可能**再各自漂移。</para>
    /// </summary>
    internal static (string[] Headers, IReadOnlyList<object?[]> Rows) Project(
        IReadOnlyList<ReportExportColumn> columns,
        IReadOnlyList<object> source,
        IStringLocalizer<ProjectZReportHeaderResource> headers)
    {
        // 零筆是合法結果（條件下確實沒有資料）。**標題仍要輸出**——
        // 一份有欄頭、零資料列的檔案看得出「查過了但沒有資料」，
        // 連欄頭都沒有的空白檔則與「產出失敗」長得一模一樣。
        var headerRow = columns.Select(c => headers[c.HeaderKey].Value).ToArray();

        var items = source.Where(x => x is not null).ToList();
        var rows = items
            .Select(item => columns.Select(c => c.ValueOf(item!)).ToArray())
            .ToList();

        return (headerRow, rows);
    }
}

/// <summary>
/// <see cref="ReportToolRunResult"/> → <c>ExportRequest</c>。
/// 獨立成擴充方法讓畫面端也能用同一條路徑（S-5 的「共用」要求）。
/// </summary>
public static class ReportToolRunResultExtensions
{
    public static ExportRequest ToExportRequest(this ReportToolRunResult result, string title)
        => new() { Title = title, Headers = result.Headers, Rows = result.Rows };
}

/// <summary>
/// 畫面端的匯出組裝（§16.1 S-5）。
///
/// <para>畫面已經有查詢結果在手上（Controller 剛用工具查完），不需要再跑一次
/// <c>ReportToolRunner.RunAsync</c>；它需要的只是「把這些列照工具宣告的欄位排出來」。
/// 排程端與畫面端因此走的是<b>同一份欄位宣告與同一段投影程式碼</b>——
/// 這正是 S-5 要的「共用」：兩邊各寫一份的話，兩份都會是對的，只是不一樣，
/// 而**沒有任何測試會發現**。</para>
/// </summary>
public static class ReportToolExportExtensions
{
    /// <param name="title">檔案標題。畫面端沿用各報表現行的雙語標題，排程端用排程名稱。</param>
    public static ExportRequest ToExportRequest<TRow>(
        this IReportToolDescriptor tool,
        ReportRequestBase request,
        IEnumerable<TRow> rows,
        string title,
        IStringLocalizer<ProjectZReportHeaderResource> headers)
    {
        var columns = tool.GetExportColumns(request);
        var (header, body) = ReportToolRunner.Project(
            columns, rows.Cast<object>().ToList(), headers);

        return new ExportRequest { Title = title, Headers = header, Rows = body };
    }
}
