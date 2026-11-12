using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.ORD.Controllers;
using SmartFactory.MES.Modules.ORD.Reporting;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Services;
using Xunit;
using SmartFactory.MES.Tests.RPS;
using SmartFactory.MES.Modules.RPS.Contracts;

namespace SmartFactory.MES.Tests.ORD;

/// <summary>
/// §6.1 前置作業二：ORD 報表特徵測試（characterization test）。
///
/// 這些測試**不主張現況是對的**，只鎖住現況。目的單一：Phase 0-B 把各 Controller 的
/// 私有 <c>BuildQueryAsync</c> 抽成注入式 <c>IReportTool</c> 之後，數字必須一字不差。
///
/// 為什麼非做不可（§15.3）：這三支報表的測試覆蓋實測為 0。報表算錯**不會 crash**，
/// 只會靜默吐出不同金額，而報表的消費者不會逐筆核對——沒有測試就沒有人會發現。
///
/// 因此每一個「會改變輸出的判斷分支」都各有一條斷言：日期邊界的含端點與否、
/// Draft 是否計入、篩掉零值列的門檻、排序方向、以及 ViewModel 的計算屬性。
/// </summary>
public class OrdReportCharacterizationTests : TestBase
{
    private readonly Mock<IUserContext>       _user = new();
    private readonly Mock<IPermissionService> _perm = new();
    private readonly Mock<IDateTimeService>   _dt   = new();

    /// <summary>
    /// 本檔的基準「今天」。**必須與 <c>_dt</c> 替身回的日期同一天**：報表讀的是
    /// <c>IDateTimeService.GetLocalDate()</c>（廠別當地日），種資料若用系統 <c>DateTime.Today</c>，
    /// 兩者在任何非 UTC 廠別下都不一樣，測試會以「查無資料」的形式失敗。
    /// </summary>
    private static readonly DateOnly Today = new(2026, 5, 14);

    private const string PlantId = "P001";

    public OrdReportCharacterizationTests()
    {
        _context.CurrentPlantId = PlantId;
        _user.Setup(u => u.PlantId).Returns(PlantId);
        _user.Setup(u => u.UserId).Returns("user1");

        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);

        _dt.Setup(d => d.Now).Returns(Today.ToDateTime(new TimeOnly(9, 0)));
        _dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(Today);
    }

    // ────────────────────────────────────────────────────────────────
    // 共用建構
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 工具用真的 <see cref="RelativePeriodResolver"/> 而非 mock：Controller→Tool→Resolver
    /// 這條路徑本身就是重構要保住的東西，換成替身等於不測它。
    /// </summary>
    private RelativePeriodResolver Resolver() => new(_dt.Object, _context);

    private SalesRankingController NewSalesRanking()
        => new(_context, _user.Object, _perm.Object,
               GetMockExportService().Object, _dt.Object,
               new SalesRankingTool(_context, Resolver()),
               GetMockLocalizer<ProjectZSharedResource>().Object,
               ReportHeaderLocalizer.Create())
           { ControllerContext = GetControllerContextWithServices() };

    private OrderOverdueController NewOrderOverdue()
        => new(_context, _user.Object, _perm.Object,
               GetMockExportService().Object, _dt.Object,
               new OrderOverdueTool(_context, Resolver(), _dt.Object),
               GetMockLocalizer<ProjectZSharedResource>().Object,
               ReportHeaderLocalizer.Create())
           { ControllerContext = GetControllerContextWithServices() };

    private ReturnRateController NewReturnRate()
        => new(_context, _user.Object, _perm.Object,
               GetMockExportService().Object, _dt.Object,
               new ReturnRateTool(_context, Resolver()),
               GetMockLocalizer<ProjectZSharedResource>().Object,
               ReportHeaderLocalizer.Create())
           { ControllerContext = GetControllerContextWithServices() };

    private DeliveryRateController NewDeliveryRate()
        => new(_context, _user.Object, _perm.Object,
               GetMockExportService().Object, _dt.Object,
               new DeliveryRateTool(_context, Resolver(), _dt.Object),
               GetMockLocalizer<ProjectZSharedResource>().Object,
               ReportHeaderLocalizer.Create())
           { ControllerContext = GetControllerContextWithServices() };

    private static T ModelOf<T>(IActionResult result) where T : class
        => Assert.IsType<T>(Assert.IsType<ViewResult>(result).Model);

    private void AddSalesOrder(
        string id, DateOnly orderDate, string company,
        DocumentApprovalStatus status,
        params (string Product, decimal Amount, decimal OrderQty, decimal ShippedQty, DateOnly? Delivery)[] lines)
    {
        var so = new SalesOrder
        {
            SalesOrderId   = id,
            OrderDate      = orderDate,
            CompanyCode    = company,
            ApprovalStatus = status,
            PlantId        = PlantId,
            CreatedBy      = "user1",
        };
        var lineNo = 1;
        foreach (var l in lines)
        {
            so.Lines.Add(new SalesOrderLine
            {
                SalesOrderId = id,
                LineNo       = lineNo++,
                ProductCode  = l.Product,
                LineAmount   = l.Amount,
                OrderQty     = l.OrderQty,
                ShippedQty   = l.ShippedQty,
                DeliveryDate = l.Delivery,
                UomCode      = "PC",
            });
        }
        _context.SalesOrders.Add(so);
        _context.SaveChanges();
    }

    // ════════════════════════════════════════════════════════════════
    // SalesRanking（銷售排行）
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 依客戶彙總：金額加總、訂單數取 distinct SalesOrderId、明細筆數取 count、
    /// 名次由 1 起算且依金額降冪。同時鎖住 Draft 與區間外訂單**不計入**。
    /// </summary>
    [Fact]
    public async Task SalesRanking_GroupByCustomer_鎖住彙總與名次()
    {
        AddSalesOrder("SO-01", new DateOnly(2026, 5, 10), "C001", DocumentApprovalStatus.Approved,
            ("PRD-A", 1000m, 10, 0, null), ("PRD-B", 500m, 5, 0, null));
        AddSalesOrder("SO-02", new DateOnly(2026, 5, 20), "C002", DocumentApprovalStatus.Approved,
            ("PRD-A", 2000m, 20, 0, null));
        // Draft 不計入
        AddSalesOrder("SO-03", new DateOnly(2026, 5, 15), "C003", DocumentApprovalStatus.Draft,
            ("PRD-A", 9999m, 99, 0, null));
        // 區間外不計入
        AddSalesOrder("SO-04", new DateOnly(2026, 4, 30), "C004", DocumentApprovalStatus.Approved,
            ("PRD-A", 777m, 7, 0, null));

        var result = await NewSalesRanking().Index("2026-05-01", "2026-05-31", "Customer", searched: true);
        var rows   = ModelOf<List<SalesRankingRowVm>>(result);

        Assert.Equal(2, rows.Count);

        Assert.Equal(1,      rows[0].Rank);
        Assert.Equal("C002", rows[0].GroupKey);
        Assert.Equal(2000m,  rows[0].TotalAmount);
        Assert.Equal(1,      rows[0].OrderCount);
        Assert.Equal(1,      rows[0].LineCount);

        Assert.Equal(2,      rows[1].Rank);
        Assert.Equal("C001", rows[1].GroupKey);
        Assert.Equal(1500m,  rows[1].TotalAmount);   // 1000 + 500
        Assert.Equal(1,      rows[1].OrderCount);    // 兩行同一張單 → distinct 後為 1
        Assert.Equal(2,      rows[1].LineCount);
    }

    /// <summary>
    /// 依品項彙總：同一品號跨單累加，OrderCount 為「涉及的訂單張數」而非明細數。
    /// </summary>
    [Fact]
    public async Task SalesRanking_GroupByProduct_跨單累加且訂單數取distinct()
    {
        AddSalesOrder("SO-01", new DateOnly(2026, 5, 10), "C001", DocumentApprovalStatus.Approved,
            ("PRD-A", 1000m, 10, 0, null), ("PRD-B", 500m, 5, 0, null));
        AddSalesOrder("SO-02", new DateOnly(2026, 5, 20), "C002", DocumentApprovalStatus.Approved,
            ("PRD-A", 2000m, 20, 0, null));

        var rows = ModelOf<List<SalesRankingRowVm>>(
            await NewSalesRanking().Index("2026-05-01", "2026-05-31", "Product", searched: true));

        Assert.Equal(2, rows.Count);
        Assert.Equal("PRD-A", rows[0].GroupKey);
        Assert.Equal(3000m,   rows[0].TotalAmount);
        Assert.Equal(2,       rows[0].OrderCount);
        Assert.Equal(2,       rows[0].LineCount);
        Assert.Equal("PRD-B", rows[1].GroupKey);
        Assert.Equal(500m,    rows[1].TotalAmount);
    }

    /// <summary>日期區間**兩端都含**（&gt;= start &amp;&amp; &lt;= end）。抽 IReportTool 時最容易改掉的一行。</summary>
    [Fact]
    public async Task SalesRanking_日期區間兩端皆含端點()
    {
        AddSalesOrder("SO-01", new DateOnly(2026, 5, 10), "C001", DocumentApprovalStatus.Approved,
            ("PRD-A", 100m, 1, 0, null));
        AddSalesOrder("SO-02", new DateOnly(2026, 5, 20), "C002", DocumentApprovalStatus.Approved,
            ("PRD-A", 200m, 1, 0, null));

        var rows = ModelOf<List<SalesRankingRowVm>>(
            await NewSalesRanking().Index("2026-05-10", "2026-05-20", "Customer", searched: true));

        Assert.Equal(2, rows.Count);   // 起日與迄日當天的單都要在
    }

    /// <summary>
    /// 未給（或給了無法解析的）迄日時，迄日退回「今天」——**未來日期的訂單仍會靜默消失**。
    /// 這一半是刻意保留的現況：要不要讓報表看得到未來單，是另一個獨立決議。
    /// </summary>
    [Fact]
    public async Task SalesRanking_迄日空白時退回今天且未來訂單被排除()
    {
        var today = Today;

        AddSalesOrder("SO-PAST", today.AddDays(-1), "C001", DocumentApprovalStatus.Approved,
            ("PRD-A", 100m, 1, 0, null));
        AddSalesOrder("SO-FUTURE", today.AddDays(30), "C002", DocumentApprovalStatus.Approved,
            ("PRD-A", 999m, 1, 0, null));

        var rows = ModelOf<List<SalesRankingRowVm>>(
            await NewSalesRanking().Index(null, null, "Customer", searched: true));

        Assert.Single(rows);
        Assert.Equal("C001", rows[0].GroupKey);
    }

    /// <summary>
    /// 起日留白**不再是「不限起日」**（2026-09-21 起）：原本會被解析成 0001-01-01，
    /// 等於每次查詢都掃遍全部歷史，而畫面上沒有任何訊號說它查了多久的資料。
    /// 現在退回近 <c>ReportDateDefaults.DefaultDays</c> 天。
    ///
    /// <para>這是刻意的行為變更，所以用一筆「剛好落在窗口外」的資料釘住——
    /// 只驗筆數的話，預設天數被誰改成 3650 也不會有人發現。</para>
    /// </summary>
    [Fact]
    public async Task SalesRanking_起日空白時只取近三十天而非全部歷史()
    {
        var today = Today;

        AddSalesOrder("SO-IN", today.AddDays(-(ReportDateDefaults.DefaultDays - 1)), "C001",
            DocumentApprovalStatus.Approved, ("PRD-A", 100m, 1, 0, null));
        AddSalesOrder("SO-OUT", today.AddDays(-ReportDateDefaults.DefaultDays), "C002",
            DocumentApprovalStatus.Approved, ("PRD-A", 999m, 1, 0, null));

        var rows = ModelOf<List<SalesRankingRowVm>>(
            await NewSalesRanking().Index(null, null, "Customer", searched: true));

        Assert.Single(rows);
        Assert.Equal("C001", rows[0].GroupKey);
    }

    /// <summary>未按查詢鈕時一律回空清單，且**不得**碰資料庫查詢。</summary>
    [Fact]
    public async Task SalesRanking_未查詢時回空清單()
    {
        AddSalesOrder("SO-01", new DateOnly(2026, 5, 10), "C001", DocumentApprovalStatus.Approved,
            ("PRD-A", 100m, 1, 0, null));

        var rows = ModelOf<List<SalesRankingRowVm>>(
            await NewSalesRanking().Index("2026-05-01", "2026-05-31", "Customer", searched: false));

        Assert.Empty(rows);
    }

    /// <summary>讀取閘門動詞為 <c>CanRead</c>（Phase 0-A 已統一）。</summary>
    [Fact]
    public async Task SalesRanking_閘門動詞為CanRead()
    {
        _perm.Setup(p => p.CanRead("SalesRanking")).ReturnsAsync(false);

        var result = await NewSalesRanking().Index(null, null, "Customer", searched: true);

        Assert.IsType<ForbidResult>(result);
    }

    // ════════════════════════════════════════════════════════════════
    // OrderOverdue（訂單逾期跟催）
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 逾期天數 = 今天 − 預交日，且**只留 &gt; 0 者**（預交日剛好是今天不算逾期），
    /// 依逾期天數降冪。未出量為計算屬性 OrderQty − ShippedQty。
    ///
    /// 基準日走 <c>IDateTimeService.GetLocalDate()</c>（2026-09-21 起；原本是伺服器時區的
    /// <c>DateTime.Today</c>，廠別時區不同時整批逾期天數會差一天）。
    /// </summary>
    [Fact]
    public async Task OrderOverdue_逾期天數與門檻與排序()
    {
        var today = Today;

        AddSalesOrder("SO-01", today.AddDays(-40), "C001", DocumentApprovalStatus.Approved,
            ("PRD-A", 0m, 10, 4, today.AddDays(-10)),   // 逾期 10 天、未出 6
            ("PRD-B", 0m, 5,  0, today.AddDays(-3)),    // 逾期 3 天、未出 5
            ("PRD-C", 0m, 8,  0, today),                // 逾期 0 天 → 排除
            ("PRD-D", 0m, 7,  7, today.AddDays(-20)),   // 已足額出貨 → 排除
            ("PRD-E", 0m, 9,  0, null));                // 無預交日 → 排除

        var rows = ModelOf<List<OrderOverdueRowVm>>(
            await NewOrderOverdue().Index(
                today.AddDays(-30).ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"),
                null, null, searched: true));

        Assert.Equal(2, rows.Count);

        Assert.Equal("PRD-A", rows[0].ProductCode);
        Assert.Equal(10,      rows[0].OverdueDays);
        Assert.Equal(6m,      rows[0].PendingQty);

        Assert.Equal("PRD-B", rows[1].ProductCode);
        Assert.Equal(3,       rows[1].OverdueDays);
        Assert.Equal(5m,      rows[1].PendingQty);
    }

    /// <summary>Draft 訂單不進逾期跟催。</summary>
    [Fact]
    public async Task OrderOverdue_Draft訂單不計入()
    {
        var today = Today;

        AddSalesOrder("SO-DRAFT", today.AddDays(-40), "C001", DocumentApprovalStatus.Draft,
            ("PRD-A", 0m, 10, 0, today.AddDays(-10)));

        var rows = ModelOf<List<OrderOverdueRowVm>>(
            await NewOrderOverdue().Index(
                today.AddDays(-30).ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"),
                null, null, searched: true));

        Assert.Empty(rows);
    }

    /// <summary>客戶／品號篩選在 SQL 端套用，兩者可疊加。</summary>
    [Fact]
    public async Task OrderOverdue_客戶與品號篩選()
    {
        var today = Today;
        var from  = today.AddDays(-30).ToString("yyyy-MM-dd");
        var to    = today.ToString("yyyy-MM-dd");

        AddSalesOrder("SO-01", today.AddDays(-40), "C001", DocumentApprovalStatus.Approved,
            ("PRD-A", 0m, 10, 0, today.AddDays(-10)));
        AddSalesOrder("SO-02", today.AddDays(-40), "C002", DocumentApprovalStatus.Approved,
            ("PRD-A", 0m, 10, 0, today.AddDays(-10)),
            ("PRD-B", 0m, 10, 0, today.AddDays(-10)));

        var byCustomer = ModelOf<List<OrderOverdueRowVm>>(
            await NewOrderOverdue().Index(from, to, "C002", null, searched: true));
        Assert.Equal(2, byCustomer.Count);
        Assert.All(byCustomer, r => Assert.Equal("C002", r.CompanyCode));

        var both = ModelOf<List<OrderOverdueRowVm>>(
            await NewOrderOverdue().Index(from, to, "C002", "PRD-B", searched: true));
        Assert.Single(both);
        Assert.Equal("PRD-B", both[0].ProductCode);
    }

    /// <summary>
    /// 讀取閘門動詞是 <c>CanRead</c>（2026-08-14 裁決，**覆寫** 2026-08-05 的 §8.1-終）。
    /// 這是 7 支不含金額的報表之一（另有 StockShortage／DefectRate／WorkOrderProgress／
    /// DeliveryRate／ReturnRate／PurchaseOverdue）。
    ///
    /// 舊裁決把這 7 支留在 <c>CanCreate</c>，理由是「改成 <c>CanRead</c> 是放寬」——
    /// 那個事實沒有變，但它被當成保留的理由是錯的：報表根本沒有「新增」語意，
    /// <c>CanCreate</c> 只是被借來當一個比較窄的門。借用的代價有三：
    /// 權限維護畫面得長出一個名不副實的 Create 勾選框才能授予檢視權；
    /// 「有其他權限必先有 Read」這條全站不變式在這 7 支身上不成立；
    /// 而收窄本身無法逐角色調整。可見範圍改由 <c>MenuPermission</c> 資料表達
    /// （與 2026-08-04 機密分級同一原則），動詞回歸單一語意。
    ///
    /// 本測試鎖住的是**方向**：日後有人把它改回 <c>CanCreate</c>，這裡會紅——
    /// 那是收緊，一樣不能無聲發生。
    /// </summary>
    [Fact]
    public async Task OrderOverdue_閘門動詞為CanRead()
    {
        _perm.Setup(p => p.CanRead("OrderOverdue")).ReturnsAsync(false);
        _perm.Setup(p => p.CanCreate("OrderOverdue")).ReturnsAsync(true);

        Assert.IsType<ForbidResult>(
            await NewOrderOverdue().Index(null, null, null, null, searched: true));

        // 反向：只有 CanRead 就進得去（原本這個組合會被擋在門外）
        _perm.Setup(p => p.CanRead("OrderOverdue")).ReturnsAsync(true);
        _perm.Setup(p => p.CanCreate("OrderOverdue")).ReturnsAsync(false);

        Assert.IsType<ViewResult>(
            await NewOrderOverdue().Index(null, null, null, null, searched: true));
    }

    // ════════════════════════════════════════════════════════════════
    // ReturnRate（退貨率分析）
    // ════════════════════════════════════════════════════════════════

    private void AddShipment(string id, DateOnly date, string company, DocStatus status,
                             params (string Product, decimal Qty)[] lines)
    {
        var gs = new GoodsShipment
        {
            GoodsShipmentId = id, ShipDate = date, CompanyCode = company,
            Status = status, PlantId = PlantId, CreatedBy = "user1",
        };
        var no = 1;
        foreach (var l in lines)
            gs.Lines.Add(new GoodsShipmentLine
            {
                GoodsShipmentId = id, LineNo = no++,
                ProductCode = l.Product, ShipQty = l.Qty, UomCode = "PC",
            });
        _context.GoodsShipments.Add(gs);
        _context.SaveChanges();
    }

    private void AddReturn(string id, DateOnly date, string company, DocStatus status,
                           params (string Product, decimal Qty)[] lines)
    {
        var sr = new SalesReturn
        {
            SalesReturnId = id, ReturnDate = date, CompanyCode = company,
            Status = status, PlantId = PlantId, CreatedBy = "user1",
        };
        var no = 1;
        foreach (var l in lines)
            sr.Lines.Add(new SalesReturnLine
            {
                SalesReturnId = id, LineNo = no++,
                ProductCode = l.Product, ReturnQty = l.Qty, UomCode = "PC",
            });
        _context.SalesReturns.Add(sr);
        _context.SaveChanges();
    }

    /// <summary>
    /// 退貨率 = 退貨量 / 出貨量 × 100，四捨五入到小數 2 位；
    /// 分組鍵是（品號, 客戶）配對；依退貨率降冪。
    /// </summary>
    [Fact]
    public async Task ReturnRate_比率計算與四捨五入與排序()
    {
        AddShipment("GS-01", new DateOnly(2026, 5, 10), "C001", DocStatus.Posted,
            ("PRD-A", 100m), ("PRD-B", 3m));
        AddReturn("SR-01", new DateOnly(2026, 5, 12), "C001", DocStatus.Posted,
            ("PRD-A", 3m), ("PRD-B", 1m));

        var rows = ModelOf<List<ReturnRateRowVm>>(
            await NewReturnRate().Index("2026-05-01", "2026-05-31", null, searched: true));

        Assert.Equal(2, rows.Count);

        // 1/3 = 33.333…% → 33.33
        Assert.Equal("PRD-B", rows[0].ProductCode);
        Assert.Equal(33.33m,  rows[0].ReturnRate);

        Assert.Equal("PRD-A", rows[1].ProductCode);
        Assert.Equal(3.00m,   rows[1].ReturnRate);
        Assert.Equal(100m,    rows[1].ShippedQty);
        Assert.Equal(3m,      rows[1].ReturnedQty);
    }

    /// <summary>
    /// 鍵取「出貨 ∪ 退貨」的聯集——**沒有出貨紀錄、只有退貨的組合也會列出**，
    /// 且因出貨量 0 使退貨率算成 0%（ReturnRate 的 <c>ShippedQty &gt; 0</c> 保護）。
    /// 這是現況：一筆跨期或跨廠對不上的退貨會以「0% 退貨率」混在清單裡，不是被濾掉。
    /// </summary>
    [Fact]
    public async Task ReturnRate_只有退貨無出貨時仍列出且比率為零()
    {
        AddReturn("SR-01", new DateOnly(2026, 5, 12), "C002", DocStatus.Posted, ("PRD-Z", 5m));

        var rows = ModelOf<List<ReturnRateRowVm>>(
            await NewReturnRate().Index("2026-05-01", "2026-05-31", null, searched: true));

        Assert.Single(rows);
        Assert.Equal("PRD-Z", rows[0].ProductCode);
        Assert.Equal(0m,      rows[0].ShippedQty);
        Assert.Equal(5m,      rows[0].ReturnedQty);
        Assert.Equal(0m,      rows[0].ReturnRate);
    }

    /// <summary>只計 <c>Posted</c> 的出貨與退貨單。</summary>
    [Fact]
    public async Task ReturnRate_未過帳單據不計入()
    {
        AddShipment("GS-DRAFT", new DateOnly(2026, 5, 10), "C001", DocStatus.Draft, ("PRD-A", 100m));
        AddReturn("SR-DRAFT",   new DateOnly(2026, 5, 12), "C001", DocStatus.Draft, ("PRD-A", 3m));

        var rows = ModelOf<List<ReturnRateRowVm>>(
            await NewReturnRate().Index("2026-05-01", "2026-05-31", null, searched: true));

        Assert.Empty(rows);
    }

    /// <summary>品號篩選是在彙總「之後」於記憶體套用，結果與在 SQL 端篩一致。</summary>
    [Fact]
    public async Task ReturnRate_品號篩選()
    {
        AddShipment("GS-01", new DateOnly(2026, 5, 10), "C001", DocStatus.Posted,
            ("PRD-A", 100m), ("PRD-B", 50m));
        AddReturn("SR-01", new DateOnly(2026, 5, 12), "C001", DocStatus.Posted,
            ("PRD-A", 3m), ("PRD-B", 5m));

        var rows = ModelOf<List<ReturnRateRowVm>>(
            await NewReturnRate().Index("2026-05-01", "2026-05-31", "PRD-A", searched: true));

        Assert.Single(rows);
        Assert.Equal("PRD-A", rows[0].ProductCode);
    }

    /// <summary>同 OrderOverdue：閘門動詞為 <c>CanRead</c>（2026-08-14 裁決）。</summary>
    [Fact]
    public async Task ReturnRate_閘門動詞為CanRead()
    {
        _perm.Setup(p => p.CanRead("ReturnRate")).ReturnsAsync(false);
        _perm.Setup(p => p.CanCreate("ReturnRate")).ReturnsAsync(true);

        Assert.IsType<ForbidResult>(
            await NewReturnRate().Index(null, null, null, searched: true));

        _perm.Setup(p => p.CanRead("ReturnRate")).ReturnsAsync(true);
        _perm.Setup(p => p.CanCreate("ReturnRate")).ReturnsAsync(false);

        Assert.IsType<ViewResult>(
            await NewReturnRate().Index(null, null, null, searched: true));
    }

    // ════════════════════════════════════════════════════════════════
    // DeliveryRate（訂單交期達成率）
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 出貨日**當天**算準時（<c>ShipDate &lt;= DeliveryDate</c>），晚一天算延遲；
    /// 依客戶分組、達成率降冪。把 <c>&lt;=</c> 改成 <c>&lt;</c> 會讓所有「剛好準時」的
    /// 出貨變成延遲，而那是最常見的一種。
    /// </summary>
    [Fact]
    public async Task DeliveryRate_出貨當日算準時且依達成率降冪()
    {
        // 母體是訂單明細行，所以要出清（ShippedQty >= OrderQty）才會拿出貨日去比對。
        AddSalesOrder("SO-A", new DateOnly(2026, 5, 1), "C001", DocumentApprovalStatus.Approved,
            ("PRD-A", 100m, 10, 10, new DateOnly(2026, 5, 10)),
            ("PRD-B", 100m, 10, 10, new DateOnly(2026, 5, 10)));
        AddSalesOrder("SO-B", new DateOnly(2026, 5, 1), "C002", DocumentApprovalStatus.Approved,
            ("PRD-A", 100m, 10, 10, new DateOnly(2026, 5, 10)));

        // C001：一筆剛好當天（準時）、一筆晚一天（延遲）⇒ 50%
        AddShipmentFor("GS-1", new DateOnly(2026, 5, 10), "C001", DocStatus.Posted, ("SO-A", 1));
        AddShipmentFor("GS-2", new DateOnly(2026, 5, 11), "C001", DocStatus.Posted, ("SO-A", 2));
        // C002：準時 ⇒ 100%
        AddShipmentFor("GS-3", new DateOnly(2026, 5, 9), "C002", DocStatus.Posted, ("SO-B", 1));

        var rows = ModelOf<List<DeliveryRateRowVm>>(
            await NewDeliveryRate().Index("2026-05-01", "2026-05-31", null, searched: true));

        Assert.Equal(2, rows.Count);
        Assert.Equal("C002", rows[0].CompanyCode);
        Assert.Equal(100m,   rows[0].OnTimeRate);
        Assert.Equal("C001", rows[1].CompanyCode);
        Assert.Equal(2,      rows[1].TotalLines);
        Assert.Equal(1,      rows[1].OnTimeLines);
        Assert.Equal(1,      rows[1].LateLines);
        Assert.Equal(50m,    rows[1].OnTimeRate);
    }

    /// <summary>
    /// 對不到訂單明細的出貨列**不再稀釋分母**（2026-09-21 口徑變更的副產物）。
    ///
    /// <para>舊版母體是出貨列，用左外聯把孤兒出貨也算進 TotalLines，於是
    /// <c>OnTimeLines + LateLines ≠ TotalLines</c>，達成率被一批無從判斷的列拉低。
    /// 新版母體是訂單明細行，孤兒出貨根本不在母體裡——它本來就不對應任何一個交期承諾。</para>
    /// </summary>
    [Fact]
    public async Task DeliveryRate_對不到訂單的出貨列不進母體()
    {
        AddSalesOrder("SO-A", new DateOnly(2026, 5, 1), "C001", DocumentApprovalStatus.Approved,
            ("PRD-A", 100m, 10, 10, new DateOnly(2026, 5, 10)));

        AddShipmentFor("GS-1", new DateOnly(2026, 5, 10), "C001", DocStatus.Posted, ("SO-A", 1));
        AddShipmentFor("GS-2", new DateOnly(2026, 5, 10), "C001", DocStatus.Posted, (null, null));

        var rows = ModelOf<List<DeliveryRateRowVm>>(
            await NewDeliveryRate().Index("2026-05-01", "2026-05-31", null, searched: true));

        var c001 = Assert.Single(rows);
        Assert.Equal(1, c001.TotalLines);
        Assert.Equal(1, c001.OnTimeLines);
        Assert.Equal(0, c001.LateLines);
        Assert.Equal(100m, c001.OnTimeRate);
        // 母體換邊之後，兩者相加恆等於總數——不再有「算不出結果卻佔著分母」的列。
        Assert.Equal(c001.TotalLines, c001.OnTimeLines + c001.LateLines);
    }

    /// <summary>
    /// **預交日**區間兩端都含（2026-09-21 起期間基準由出貨日改為預交日）；
    /// 未過帳（非 <c>Posted</c>）的出貨不能用來判定準時。
    /// </summary>
    [Fact]
    public async Task DeliveryRate_預交日區間含端點且僅計已過帳出貨()
    {
        AddSalesOrder("SO-IN", new DateOnly(2026, 5, 1), "C001", DocumentApprovalStatus.Approved,
            ("PRD-A", 100m, 10, 10, new DateOnly(2026, 5, 1)),    // 起日端點
            ("PRD-B", 100m, 10, 10, new DateOnly(2026, 5, 31)));  // 迄日端點
        AddSalesOrder("SO-OUT", new DateOnly(2026, 5, 1), "C001", DocumentApprovalStatus.Approved,
            ("PRD-C", 100m, 10, 10, new DateOnly(2026, 6, 1)));   // 區間外

        AddShipmentFor("GS-1", new DateOnly(2026, 5, 1),  "C001", DocStatus.Posted, ("SO-IN", 1));
        AddShipmentFor("GS-2", new DateOnly(2026, 5, 31), "C001", DocStatus.Posted, ("SO-IN", 2));
        AddShipmentFor("GS-3", new DateOnly(2026, 6, 1),  "C001", DocStatus.Posted, ("SO-OUT", 1));

        var rows = ModelOf<List<DeliveryRateRowVm>>(
            await NewDeliveryRate().Index("2026-05-01", "2026-05-31", null, searched: true));

        Assert.Equal(2, Assert.Single(rows).TotalLines);
    }

    /// <summary>
    /// <b>本次口徑變更的核心案例</b>：已過預交日、貨還沒出的訂單要算遲交。
    ///
    /// <para>舊版母體只有已過帳的出貨，這一行完全不在分母裡——於是訂單逾期跟催表
    /// 列著它、交期達成率卻是 100%。與採購側是同一個毛病。</para>
    /// </summary>
    [Fact]
    public async Task DeliveryRate_已逾期未出的訂單算遲交而不是消失()
    {
        // 基準日 Today = 2026-05-14；預交日 5/10 已過，一件都沒出。
        AddSalesOrder("SO-LATE", new DateOnly(2026, 5, 1), "C001", DocumentApprovalStatus.Approved,
            ("PRD-A", 100m, 10, 0, new DateOnly(2026, 5, 10)));

        var rows = ModelOf<List<DeliveryRateRowVm>>(
            await NewDeliveryRate().Index("2026-05-01", "2026-05-31", null, searched: true));

        var c001 = Assert.Single(rows);
        Assert.Equal(1, c001.TotalLines);
        Assert.Equal(0, c001.OnTimeLines);
        Assert.Equal(1, c001.LateLines);
        Assert.Equal(0m, c001.OnTimeRate);
    }

    /// <summary>還沒到預交日就還沒有結果，列入會讓達成率隨「開了幾張未來的單」浮動。</summary>
    [Fact]
    public async Task DeliveryRate_未到期的未出訂單不列入母體()
    {
        // 基準日 Today = 2026-05-14；預交日 5/20 還沒到。
        AddSalesOrder("SO-FUTURE", new DateOnly(2026, 5, 1), "C001", DocumentApprovalStatus.Approved,
            ("PRD-A", 100m, 10, 0, new DateOnly(2026, 5, 20)));

        var rows = ModelOf<List<DeliveryRateRowVm>>(
            await NewDeliveryRate().Index("2026-05-01", "2026-05-31", null, searched: true));

        Assert.Empty(rows);
    }

    /// <summary>分批出貨以**出齊**那一天判定：只看第一次會讓「首批準時、尾批拖很久」變成準時。</summary>
    [Fact]
    public async Task DeliveryRate_分批出貨以出齊那一天判定()
    {
        AddSalesOrder("SO-SPLIT", new DateOnly(2026, 5, 1), "C001", DocumentApprovalStatus.Approved,
            ("PRD-A", 100m, 10, 10, new DateOnly(2026, 5, 10)));

        AddShipmentFor("GS-EARLY", new DateOnly(2026, 5, 5),  "C001", DocStatus.Posted, ("SO-SPLIT", 1));
        AddShipmentFor("GS-LATE",  new DateOnly(2026, 5, 13), "C001", DocStatus.Posted, ("SO-SPLIT", 1));

        var rows = ModelOf<List<DeliveryRateRowVm>>(
            await NewDeliveryRate().Index("2026-05-01", "2026-05-31", null, searched: true));

        var c001 = Assert.Single(rows);
        Assert.Equal(1, c001.LateLines);
        Assert.Equal(0, c001.OnTimeLines);
    }

    /// <summary>客戶為精確比對，改成 Contains 會把 C0011 一起帶出來。</summary>
    [Fact]
    public async Task DeliveryRate_客戶為精確比對()
    {
        AddSalesOrder("SO-A", new DateOnly(2026, 5, 1), "C001", DocumentApprovalStatus.Approved,
            ("PRD-A", 100m, 10, 0, new DateOnly(2026, 5, 10)));

        AddShipmentFor("GS-1", new DateOnly(2026, 5, 10), "C001",  DocStatus.Posted, ("SO-A", 1));
        AddShipmentFor("GS-2", new DateOnly(2026, 5, 10), "C0011", DocStatus.Posted, ("SO-A", 1));

        var rows = ModelOf<List<DeliveryRateRowVm>>(
            await NewDeliveryRate().Index("2026-05-01", "2026-05-31", "C001", searched: true));

        Assert.Equal("C001", Assert.Single(rows).CompanyCode);
    }

    /// <summary>閘門動詞是 <c>CanRead</c>。抽出後工具要照抄這一個。</summary>
    [Fact]
    public async Task DeliveryRate_閘門動詞為CanRead()
    {
        _perm.Setup(p => p.CanRead("DeliveryRate")).ReturnsAsync(false);

        Assert.IsType<ForbidResult>(
            await NewDeliveryRate().Index(null, null, null, searched: true));
    }

    /// <summary>出貨單與明細；<c>SalesOrderId = null</c> 代表刻意製造「對不到訂單」的那一列。</summary>
    private void AddShipmentFor(
        string id, DateOnly shipDate, string company, DocStatus status,
        params (string? SalesOrderId, int? SoLineNo)[] lines)
    {
        _context.GoodsShipments.Add(new GoodsShipment
        {
            GoodsShipmentId = id,
            ShipDate        = shipDate,
            CompanyCode     = company,
            Status          = status,
            PlantId         = PlantId,
            CreatedBy       = "user1",
        });

        var lineNo = 1;
        foreach (var l in lines)
        {
            _context.GoodsShipmentLines.Add(new GoodsShipmentLine
            {
                GoodsShipmentId = id,
                LineNo          = lineNo++,
                SalesOrderId    = l.SalesOrderId,
                SoLineNo        = l.SoLineNo,
                ProductCode     = "PRD-A",
                ShipQty         = 1m,
                UomCode         = "PC",
            });
        }
        _context.SaveChanges();
    }
}
