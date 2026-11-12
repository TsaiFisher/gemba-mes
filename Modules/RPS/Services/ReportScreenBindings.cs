namespace SmartFactory.MES.Modules.RPS.Services;

/// <summary>
/// 一支報表工具對應到「哪個畫面、吃哪些查詢參數」。
/// </summary>
/// <param name="Controller">目標 Controller 名稱，組成 <c>/{Controller}/Index</c>。</param>
/// <param name="PeriodStyle">
/// 該畫面用什麼形狀表達期間。非 <see cref="ReportPeriodStyle.None"/> 時常用查詢
/// **必須**指定 <c>RelativePeriod</c>（§7.9-1）：存絕對日期的「上月銷售排行」
/// 下個月點開還是上個月的資料。
///
/// <para>原本這裡是一個 bool，於是「吃期間」與「吃 fromDate／toDate」被當成同一件事，
/// 財報那兩張畫面（吃會計期間代碼）就只能整個排除在常用查詢之外。</para>
/// </param>
/// <param name="FilterKeys">
/// 允許存進常用查詢並回填到網址的查詢參數名——**白名單，不是說明文件**。
/// 存檔與執行兩端都依此過濾：ParamsJson 是資料庫內容，若原封不動展開成查詢字串，
/// 任何能寫到這張表的路徑就等於能對目標畫面注入任意參數。
/// </param>
/// <param name="ParamMap">
/// 工具參數名（<c>TRequest</c> 屬性的 camelCase）→ 畫面查詢列欄位名（<see cref="FilterKeys"/> 之一）。
///
/// <para><b>這兩套名字本來就不一樣，而且不會統一。</b>工具參數描述的是查詢語意
/// （<c>customerCode</c>／<c>idleDaysThreshold</c>），畫面查詢列的名字是網址參數
/// （<c>searchCustomer</c>／<c>idleDays</c>），後者還受既有畫面的歷史包袱約束。
/// 硬把其中一套改成另一套，動到的是六張已上線報表的網址。</para>
///
/// <para>需要這張對照表的只有一件事：**Phase 3 把一次自然語言查詢另存成常用查詢**
/// （§十三 Phase 3）——NL 那一側拿到的是工具參數，而常用查詢存的是畫面欄位。
/// 沒有對照表就只能把條件全部丟掉，只留期間。</para>
///
/// <para>⚠️ <b>對不上的參數一律丟掉，但必須告訴使用者丟了哪些</b>（見
/// <c>ReportScreenBindings.ToSavedQuery</c>）：常用查詢的入口是報表畫面，
/// 畫面表達不了的條件本來就帶不過去；靜默丟掉的話，使用者會以為那張常用查詢
/// 等於他剛才問的那一題，而它其實範圍更寬。</para>
/// </param>
/// <remarks>
/// <c>readonly record struct</c> 而非 <c>record</c>：R5b 掃 <c>Modules.*</c> 下所有
/// <c>class</c> 的非可空實值型別屬性並要求雙語驗證資源鍵。<c>UsesDateRange</c> 是繫結
/// 設定、不是表單欄位，不該為它造一個假的欄位名鍵；值型別不在該規則的掃描範圍內。
/// </remarks>
public readonly record struct ReportScreenBinding(
    string Controller,
    ReportPeriodStyle PeriodStyle,
    IReadOnlyList<string> FilterKeys,
    IReadOnlyDictionary<string, string>? ParamMap = null)
{
    /// <summary>
    /// 畫面是否吃期間（任何形狀）。存檔端只需要知道「要不要逼使用者選相對期間」，
    /// 不需要知道是日期還是會計期間，所以保留這個問法而不是讓每個呼叫端自己比對列舉。
    /// </summary>
    public bool UsesDateRange => PeriodStyle != ReportPeriodStyle.None;
}

/// <summary>
/// 畫面表達期間的形狀。決定 <c>SavedQueryService.BuildRunUrlAsync</c> 把解算出來的
/// <c>DateRange</c> 串成哪些查詢參數。
///
/// <para><b>為什麼不是「都轉成日期就好」</b>：財報的期間是<b>會計期間</b>，
/// 與日曆月可以不同（<c>AccountingPeriod</c> 自訂起訖）。把它攤成 fromDate／toDate
/// 之後畫面讀不到——那兩張畫面的服務簽章吃的是期間代碼，不是日期區間。</para>
/// </summary>
public enum ReportPeriodStyle
{
    /// <summary>畫面不吃期間（庫存類現況報表）。</summary>
    None,

    /// <summary><c>fromDate</c>／<c>toDate</c>（yyyy-MM-dd）。</summary>
    DateRange,

    /// <summary><c>fromPeriod</c>／<c>toPeriod</c>（會計期間代碼 yyyyMM）。</summary>
    FiscalRange,

    /// <summary><c>asOfPeriod</c>（單一截止會計期間）；資產負債表是累計到某一期，沒有起期。</summary>
    FiscalAsOf,

    /// <summary>
    /// <c>year</c>（西元年）。採購金額分析那一類「選一個年度、看 12 個月」的畫面。
    ///
    /// <para>⚠️ 這一類的常用查詢<b>只該搭年度類的相對期間</b>（<c>ThisYear</c>／<c>LastYear</c>）。
    /// 存成「上月」也跑得動——取解算區間落在的年份——但使用者存的時候想的是「上個月」，
    /// 點開卻得到一整年，而畫面上沒有任何訊號說它被放大了。<c>_SavedQueries</c>
    /// 因此只對這個形狀提供年度選項，理由與財報那兩張的 <c>fiscalOnly</c> 相同。</para>
    /// </summary>
    Year,
}

/// <summary>
/// 工具鍵 → 畫面繫結。
///
/// 為什麼不放進 <c>IReportToolDescriptor</c>：那是工具契約，排程與 LLM 也會用，
/// 而「畫面叫什麼、查詢列有哪些欄位」純粹是 MVC 這一側的事，不該滲進工具契約。
///
/// 新增報表工具時務必同步在此登錄——否則常用查詢會存得下去卻執行不了。
/// <c>SavedQueryServiceTests</c> 有一支測試釘住「每支已登錄的工具都要有繫結」。
/// </summary>
public static class ReportScreenBindings
{
    private static readonly Dictionary<string, ReportScreenBinding> Map = new(StringComparer.Ordinal)
    {
        ["SalesRanking"] = new("SalesRanking", ReportPeriodStyle.DateRange,
            ["groupBy"],
            Param(("groupBy", "groupBy"))),

        ["OrderOverdue"] = new("OrderOverdue", ReportPeriodStyle.DateRange,
            ["searchCustomer", "searchProduct"],
            Param(("customerCode", "searchCustomer"), ("productCode", "searchProduct"))),

        ["ReturnRate"] = new("ReturnRate", ReportPeriodStyle.DateRange,
            ["searchProduct"],
            Param(("productCode", "searchProduct"))),

        ["InventoryValuation"] = new("InventoryValuation", ReportPeriodStyle.None,
            ["searchProduct", "searchWarehouse", "sortOrder", "pageSize"],
            // ⚠️ sortOrder／pageSize 刻意**不在** ParamMap 裡：它們是畫面自己的呈現選項，
            // 而工具那一側對應的 sortBy／pageSize 根本沒送給模型（QaToolSchemaBuilder.Excluded）。
            // 白名單容得下它們，是因為使用者在畫面上存查詢時可以指定。
            Param(("keyword", "searchProduct"), ("warehouseCode", "searchWarehouse"))),

        ["StockShortage"] = new("StockShortage", ReportPeriodStyle.None,
            ["searchProduct"],
            Param(("keyword", "searchProduct"))),

        ["SlowMovingInventory"] = new("SlowMovingInventory", ReportPeriodStyle.None,
            ["idleDays", "searchProduct"],
            Param(("idleDaysThreshold", "idleDays"), ("productCode", "searchProduct"))),

        // ── 2026-09-21 補登錄 ───────────────────────────────────────────
        // 工具早就抽好了（ReportToolCatalog 共 17 支），但繫結一直停在首批六支，
        // 所以那些畫面連「我的常用查詢」都不會長出來——partial 的第一件事就是
        // Find(toolKey)，查無繫結直接整塊不渲染，沒有任何訊號。

        ["DeliveryRate"] = new("DeliveryRate", ReportPeriodStyle.DateRange,
            ["searchCustomer"],
            Param(("customerCode", "searchCustomer"))),

        ["PurchaseOverdue"] = new("PurchaseOverdue", ReportPeriodStyle.DateRange,
            ["searchSupplier", "searchProduct", "minOverdueDays"],
            Param(("supplierCode", "searchSupplier"), ("productCode", "searchProduct"),
                  ("minOverdueDays", "minOverdueDays"))),

        ["DefectRate"] = new("DefectRate", ReportPeriodStyle.DateRange,
            ["searchProduct"],
            Param(("productKeyword", "searchProduct"))),

        // 2026-09-23 新增的委外價格分析。工具與畫面同批建立，所以這裡從第一天就有繫結
        // ——上面那批補登錄的註解說的正是漏掉它的後果。
        ["OutsourcePrice"] = new("OutsourcePrice", ReportPeriodStyle.DateRange,
            ["searchSupplier", "searchProduct"],
            Param(("supplierCode", "searchSupplier"), ("productCode", "searchProduct"))),

        ["SupplierDeliveryRate"] = new("SupplierDeliveryRate", ReportPeriodStyle.DateRange,
            ["searchSupplier", "source"],
            Param(("supplierCode", "searchSupplier"), ("source", "source"))),

        ["PurchasePrice"] = new("PurchasePrice", ReportPeriodStyle.DateRange,
            ["searchProduct", "searchSupplier"],
            Param(("productCode", "searchProduct"), ("supplierCode", "searchSupplier"))),

        // 年度型畫面：期間串成 year=，可選的相對期間由 _SavedQueries 收斂成本年／去年。
        ["PurchaseAmount"] = new("PurchaseAmount", ReportPeriodStyle.Year,
            ["groupBy", "source"],
            Param(("groupByDimension", "groupBy"), ("source", "source"))),

        ["MrpAchievement"] = new("MrpAchievement", ReportPeriodStyle.None,
            ["mrpRunId", "searchProduct"],
            Param(("mrpRunId", "mrpRunId"), ("productCode", "searchProduct"))),

        ["WorkOrderProgress"] = new("WorkOrderProgress", ReportPeriodStyle.DateRange,
            ["searchProduct", "filterStatus"],
            Param(("productKeyword", "searchProduct"), ("status", "filterStatus"))),

        // 以下四支的 TRequest 只有 ReportRequestBase 的期間，沒有自己的篩選參數，
        // 故無 ParamMap；畫面自己的呈現選項（dimension／差異別勾選）仍可由使用者存。
        ["OutsourcePerformance"] = new("OutsourcePerformance", ReportPeriodStyle.DateRange,
            []),

        ["GrossMargin"] = new("GrossMargin", ReportPeriodStyle.DateRange,
            []),

        ["MarginAnalysis"] = new("MarginAnalysis", ReportPeriodStyle.DateRange,
            ["dimension"]),

        ["ContributionMargin"] = new("ContributionMargin", ReportPeriodStyle.DateRange,
            ["matVar", "laborVar", "ohVar"]),

        // 財報兩張吃會計期間代碼而不是日期，故 PeriodStyle 是 Fiscal*：
        // 解算出來的 DateRange 會再被換回 AccountingPeriod.PeriodId 才串進網址
        // （見 SavedQueryService.AppendPeriodAsync）。2026-09-21 之前它們刻意不在此表，
        // 因為當時只有一種形狀，硬串 fromDate/toDate 上去那兩個參數畫面根本不讀，
        // 期間會靜默失效而使用者拿到畫面預設期間的數字。
        //
        // ⚠️ 這兩張的常用查詢**只該搭會計期間類的相對期間**（ThisFiscalPeriod／LastFiscalPeriod）。
        // 存成「上月」也跑得動——落在哪一期就用哪一期——但那是日曆月對應回來的期間，
        // 在自訂期間起訖的廠別會與使用者以為的「上一個會計期間」不同。
        ["BalanceSheet"] = new("BalanceSheet", ReportPeriodStyle.FiscalAsOf,
            []),

        ["ProfitLoss"] = new("ProfitLoss", ReportPeriodStyle.FiscalRange,
            []),

        // 試算表同樣吃會計期間區間；「顯示零餘額科目」是畫面自己的呈現選項，
        // 工具那一側的 includeZeroRows 不進 ParamMap（與 InventoryValuation 的
        // sortOrder／pageSize 同一個理由）。
        ["TrialBalance"] = new("TrialBalance", ReportPeriodStyle.FiscalRange,
            ["includeZeroRows"]),
    };

    public static IReadOnlyDictionary<string, ReportScreenBinding> All => Map;

    public static ReportScreenBinding? Find(string? toolKey)
        => toolKey is not null && Map.TryGetValue(toolKey, out var b) ? b : null;

    private static IReadOnlyDictionary<string, string> Param(params (string Tool, string Filter)[] pairs)
        => pairs.ToDictionary(p => p.Tool, p => p.Filter, StringComparer.Ordinal);
}
