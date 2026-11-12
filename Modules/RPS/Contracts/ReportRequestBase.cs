using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.RPS.Contracts;

/// <summary>
/// 期間的滾動視窗語意（§5.2.1）。
///
/// **存查詢一定要存這個，不可存絕對日期**：存成 <c>2026-07-01~2026-07-31</c> 的
/// 「上月銷售排行」，下個月點開還是七月的資料——那正好是「常用查詢」的反面。
/// 排程報表同理，會每個月寄出同一份七月資料。
/// </summary>
public enum RelativePeriod
{
    Today,
    Yesterday,
    ThisWeek,
    LastWeek,
    ThisMonth,
    LastMonth,
    /// <summary>日曆季（1–3／4–6／7–9／10–12）。財務類報表請改用 <see cref="ThisFiscalPeriod"/>。</summary>
    ThisQuarter,
    LastQuarter,
    /// <summary>會計期間（`AccountingPeriod` 表），與日曆月／季可能不同。</summary>
    ThisFiscalPeriod,
    LastFiscalPeriod,
    ThisYear,
    LastYear,
    Last7Days,
    Last30Days,
    Last90Days,
    MonthToDate,
    QuarterToDate,
    YearToDate,
}

/// <summary>
/// 日期基準。與 <see cref="RelativePeriod"/> **正交**：相對期間解決日期算術，
/// 但「以訂單日還是出貨日計算」的歧義依然存在（§5.2.1-5）。
/// </summary>
public enum DateBasis
{
    /// <summary>由各工具自行決定其慣用基準（＝維持該報表現行行為）。</summary>
    Default = 0,
    OrderDate,
    ShipDate,
    InvoiceDate,
    MovementDate,
}

/// <summary>
/// 解算後的絕對區間，**兩端都含**（對齊既有報表 <c>&gt;= start &amp;&amp; &lt;= end</c> 的慣例）。
///
/// 刻意做成 <c>record struct</c>：R5b 只掃 <c>class</c>，值型別不會被要求為
/// <c>From</c>/<c>To</c> 補驗證資源鍵（它們不是使用者輸入欄位，是解算結果）。
/// </summary>
public readonly record struct DateRange(DateOnly From, DateOnly To)
{
    public bool Contains(DateOnly d) => d >= From && d <= To;

    public override string ToString() => $"{From:yyyy-MM-dd}~{To:yyyy-MM-dd}";
}

/// <summary>
/// 所有報表工具共用的請求基底（§5.2）。讓排程設定與未來的 LLM 參數解析只需學一種模式。
///
/// 本類別刻意為 <c>abstract</c>；具體子類別會繼承下列屬性，其中
/// <see cref="DateBasis"/> 與 <see cref="UseLocalCurrency"/> 是非可空實值型別，
/// 依 R5b 需在 `ProjectZValidationResource` 有雙語鍵（已補）。
/// </summary>
public abstract class ReportRequestBase
{
    /// <summary>
    /// 廠別。<c>null</c> ＝ 依執行 scope 的授權範圍（`ApplicationDbContext.CurrentPlantId`），
    /// **不是**「查全部廠」。跨廠彙總須走 §5.5 T1 的顯式授權範圍參數，不可裸用 `IgnoreQueryFilters`。
    /// </summary>
    public string? PlantId { get; set; }

    /// <summary>滾動視窗期間。與 <see cref="AbsolutePeriod"/> 互斥，且**優先**。</summary>
    public RelativePeriod? Period { get; set; }

    /// <summary>
    /// 明確起訖。僅供畫面手動查詢與既有排程匯入使用；
    /// **儲存為常用查詢／排程時不得使用**（見 <see cref="RelativePeriod"/> 的說明）。
    /// </summary>
    public DateRange? AbsolutePeriod { get; set; }

    public DateBasis DateBasis { get; set; } = DateBasis.Default;

    public string? GroupBy { get; set; }

    public int? TopN { get; set; }

    /// <summary>對齊既有 `AmountLocal` 慣例（§P1 單據金額本幣收斂）。</summary>
    public bool UseLocalCurrency { get; set; }

    public string? SortBy { get; set; }

    /// <summary>
    /// 計算基準日（「逾期幾天」「呆滯幾天」相對於哪一天）。
    ///
    /// <c>null</c> ＝ 由工具走 <c>IDateTimeService.GetLocalDate()</c> 取時區感知的今天，
    /// 這是**新呼叫端（排程／LLM）應走的路徑**。
    /// 既有畫面則顯式傳入 <c>DateTime.Today</c> 以維持現行行為——
    /// 那個歷史語意留在 Controller 邊界，不滲進工具（見 <see cref="LegacyQueryPeriod"/>）。
    /// </summary>
    public DateOnly? AsOfDate { get; set; }

    /// <summary>
    /// 明確時區。排程沒有 HTTP 上下文，不傳會退回行程預設，跨時區廠別的「今天」會差一天。
    /// </summary>
    public string? TimeZoneId { get; set; }

    /// <summary>分頁頁碼（1 起算）。<c>null</c> ＝ 不分頁（排程輸出整份）。</summary>
    public int? PageNumber { get; set; }

    /// <summary>每頁筆數。<c>null</c> ＝ 不分頁。</summary>
    public int? PageSize { get; set; }

    /// <summary>
    /// 兩個期間欄位是否恰好給了一個。<c>[NotMapped]</c> 讓 R5b 略過
    /// （這是衍生判斷，不是使用者輸入欄位）。
    /// </summary>
    [NotMapped]
    public bool HasExactlyOnePeriod => Period.HasValue ^ AbsolutePeriod.HasValue;
}
