using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.PUR.Controllers;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Reporting;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;
using Xunit;
using SmartFactory.MES.Tests.RPS;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// §6.1 前置作業二：PUR 報表特徵測試（characterization test）。
///
/// 與 <c>OrdReportCharacterizationTests</c> 同一個目的：這些測試**不主張現況是對的**，
/// 只鎖住現況，好讓 <c>BuildQueryAsync</c> 抽成 <c>IReportTool</c> 之後數字一字不差。
/// 報表算錯不會 crash，只會靜默吐出不同筆數——沒有測試就沒有人會發現。
///
/// <para><b>日期一律相對於今天構造</b>：抽出前的 <c>BuildQueryAsync</c> 用的是
/// <c>DateTime.Today</c>（伺服器時區）。寫死絕對日期的話這些測試會在某一天自己變紅，
/// 而那個紅與重構無關。抽出後 Controller 仍顯式傳 <c>AsOfDate = DateTime.Today</c>
/// 以維持畫面行為，所以同一組斷言在兩側都成立——**這正是特徵測試要證明的事**。</para>
/// </summary>
public class PurReportCharacterizationTests : TestBase
{
    private readonly Mock<IUserContext>       _user = new();
    private readonly Mock<IPermissionService> _perm = new();
    private readonly Mock<IDateTimeService>   _dt   = new();

    private const string PlantId = "P001";

    /// <summary>基準日＝抽出前 <c>BuildQueryAsync</c> 用的那一個，測試自己也用它算預期值。</summary>
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    public PurReportCharacterizationTests()
    {
        _context.CurrentPlantId = PlantId;
        _user.Setup(u => u.PlantId).Returns(PlantId);
        _user.Setup(u => u.UserId).Returns("user1");

        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);

        _dt.Setup(d => d.Now).Returns(DateTime.Today.AddHours(9));
        _dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(Today);
    }

    /// <summary>
    /// 工具用真的 <see cref="RelativePeriodResolver"/> 而非 mock：Controller→Tool→Resolver
    /// 這條路徑本身就是重構要保住的東西，換成替身等於不測它。
    /// </summary>
    private RelativePeriodResolver Resolver() => new(_dt.Object, _context);

    private PurchaseOverdueController NewPurchaseOverdue()
        => new(_context, _user.Object, _perm.Object,
               GetMockExportService().Object, _dt.Object,
               new PurchaseOverdueTool(_context, Resolver(), _dt.Object),
               GetMockLocalizer<ProjectZSharedResource>().Object,
               ReportHeaderLocalizer.Create())
           { ControllerContext = GetControllerContextWithServices() };

    private static T ModelOf<T>(IActionResult result) where T : class
        => Assert.IsType<T>(Assert.IsType<ViewResult>(result).Model);

    private void AddPurchaseOrder(
        string id, DateOnly orderDate, string supplier,
        DocumentApprovalStatus status, string createdBy,
        params (string Product, DateOnly? Delivery, decimal OrderQty, decimal ReceivedQty, bool IsClosed)[] lines)
    {
        var po = new PurchaseOrder
        {
            PurchaseOrderId = id,
            OrderDate       = orderDate,
            SupplierCode    = supplier,
            ApprovalStatus  = status,
            PlantId         = PlantId,
            CreatedBy       = createdBy,
        };
        _context.PurchaseOrders.Add(po);

        var lineNo = 1;
        foreach (var l in lines)
        {
            _context.PurchaseOrderLines.Add(new PurchaseOrderLine
            {
                PurchaseOrderId = id,
                LineNo          = lineNo++,
                ProductCode     = l.Product,
                DeliveryDate    = l.Delivery,
                OrderQty        = l.OrderQty,
                ReceivedQty     = l.ReceivedQty,
                IsClosed        = l.IsClosed,
                UomCode         = "PC",
            });
        }
        _context.SaveChanges();
    }

    // ════════════════════════════════════════════════════════════════
    // PurchaseOverdue（採購逾期跟催）
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 逾期天數＝今天減預交日、排序為逾期天數降冪、`PendingQty` 為計算屬性。
    /// 同時鎖住「預交日區間**兩端都含**」。
    /// </summary>
    [Fact]
    public async Task PurchaseOverdue_逾期天數與降冪排序()
    {
        AddPurchaseOrder("PO-01", Today.AddDays(-40), "S001", DocumentApprovalStatus.Approved, "u-buyer",
            ("PRD-A", Today.AddDays(-10), 100m, 30m, false));
        AddPurchaseOrder("PO-02", Today.AddDays(-40), "S002", DocumentApprovalStatus.Approved, "u-buyer",
            ("PRD-B", Today.AddDays(-3), 50m, 0m, false));

        var result = await NewPurchaseOverdue().Index(
            Today.AddDays(-30).ToString("yyyy-MM-dd"), Today.ToString("yyyy-MM-dd"),
            null, null, minOverdueDays: 1, searched: true);
        var rows = ModelOf<List<PurchaseOverdueRowVm>>(result);

        Assert.Equal(2, rows.Count);

        Assert.Equal("PO-01", rows[0].PurchaseOrderId);
        Assert.Equal(10,      rows[0].OverdueDays);
        Assert.Equal(70m,     rows[0].PendingQty);      // 100 − 30

        Assert.Equal("PO-02", rows[1].PurchaseOrderId);
        Assert.Equal(3,       rows[1].OverdueDays);
        Assert.Equal(50m,     rows[1].PendingQty);
    }

    /// <summary>
    /// 預交日剛好落在區間端點要列入（<c>&gt;= start &amp;&amp; &lt;= end</c>）；
    /// 落在區間外不列入。方向錯了只會少幾列，畫面上沒有任何訊號。
    /// </summary>
    [Fact]
    public async Task PurchaseOverdue_預交日區間含端點()
    {
        AddPurchaseOrder("PO-START", Today.AddDays(-40), "S001", DocumentApprovalStatus.Approved, "u-buyer",
            ("PRD-A", Today.AddDays(-20), 10m, 0m, false));   // ＝ start
        AddPurchaseOrder("PO-END", Today.AddDays(-40), "S001", DocumentApprovalStatus.Approved, "u-buyer",
            ("PRD-A", Today.AddDays(-5), 10m, 0m, false));    // ＝ end
        AddPurchaseOrder("PO-OUT", Today.AddDays(-40), "S001", DocumentApprovalStatus.Approved, "u-buyer",
            ("PRD-A", Today.AddDays(-21), 10m, 0m, false));   // start 前一天

        var result = await NewPurchaseOverdue().Index(
            Today.AddDays(-20).ToString("yyyy-MM-dd"), Today.AddDays(-5).ToString("yyyy-MM-dd"),
            null, null, minOverdueDays: 1, searched: true);
        var rows = ModelOf<List<PurchaseOverdueRowVm>>(result);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.PurchaseOrderId == "PO-START");
        Assert.Contains(rows, r => r.PurchaseOrderId == "PO-END");
    }

    /// <summary>
    /// 三個排除條件各自生效：已收足（<c>ReceivedQty &gt;= OrderQty</c>）、
    /// 已結案（<c>IsClosed</c>）、草稿單。任何一條失效都會讓報表多出不該追的列。
    /// </summary>
    [Fact]
    public async Task PurchaseOverdue_收足與結案與草稿都不列()
    {
        AddPurchaseOrder("PO-KEEP", Today.AddDays(-40), "S001", DocumentApprovalStatus.Approved, "u-buyer",
            ("PRD-A", Today.AddDays(-10), 100m, 99m, false));
        AddPurchaseOrder("PO-FULL", Today.AddDays(-40), "S001", DocumentApprovalStatus.Approved, "u-buyer",
            ("PRD-A", Today.AddDays(-10), 100m, 100m, false));   // 收足
        AddPurchaseOrder("PO-CLOSED", Today.AddDays(-40), "S001", DocumentApprovalStatus.Approved, "u-buyer",
            ("PRD-A", Today.AddDays(-10), 100m, 0m, true));      // 已結案
        AddPurchaseOrder("PO-DRAFT", Today.AddDays(-40), "S001", DocumentApprovalStatus.Draft, "u-buyer",
            ("PRD-A", Today.AddDays(-10), 100m, 0m, false));     // 草稿

        var result = await NewPurchaseOverdue().Index(
            Today.AddDays(-30).ToString("yyyy-MM-dd"), Today.ToString("yyyy-MM-dd"),
            null, null, minOverdueDays: 1, searched: true);
        var rows = ModelOf<List<PurchaseOverdueRowVm>>(result);

        Assert.Single(rows);
        Assert.Equal("PO-KEEP", rows[0].PurchaseOrderId);
    }

    /// <summary>
    /// <c>minOverdueDays</c> 的門檻是 <c>&gt;=</c>（不是 <c>&gt;</c>），
    /// 且小於 1 的輸入會被夾到 1——夾的那一步若消失，逾期 0 天（今天到期）的列會混進來。
    /// </summary>
    [Fact]
    public async Task PurchaseOverdue_門檻含等於且小於1夾到1()
    {
        AddPurchaseOrder("PO-5", Today.AddDays(-40), "S001", DocumentApprovalStatus.Approved, "u-buyer",
            ("PRD-A", Today.AddDays(-5), 10m, 0m, false));
        AddPurchaseOrder("PO-0", Today.AddDays(-40), "S001", DocumentApprovalStatus.Approved, "u-buyer",
            ("PRD-A", Today, 10m, 0m, false));   // 今天到期 ⇒ 逾期 0 天

        var from = Today.AddDays(-30).ToString("yyyy-MM-dd");
        var to   = Today.ToString("yyyy-MM-dd");

        // 門檻 5：逾期剛好 5 天的那一列要列入（>= 而非 >）
        var exactly = ModelOf<List<PurchaseOverdueRowVm>>(
            await NewPurchaseOverdue().Index(from, to, null, null, minOverdueDays: 5, searched: true));
        Assert.Single(exactly);
        Assert.Equal("PO-5", exactly[0].PurchaseOrderId);

        // 門檻 0 ⇒ 夾到 1 ⇒ 逾期 0 天那列仍不列入
        var clamped = ModelOf<List<PurchaseOverdueRowVm>>(
            await NewPurchaseOverdue().Index(from, to, null, null, minOverdueDays: 0, searched: true));
        Assert.Single(clamped);
        Assert.Equal("PO-5", clamped[0].PurchaseOrderId);
    }

    /// <summary>供應商與品號都是**精確比對**（不是模糊），改成 Contains 會多帶出別的供應商。</summary>
    [Fact]
    public async Task PurchaseOverdue_供應商與品號為精確比對()
    {
        AddPurchaseOrder("PO-A", Today.AddDays(-40), "S001", DocumentApprovalStatus.Approved, "u-buyer",
            ("PRD-A", Today.AddDays(-10), 10m, 0m, false));
        AddPurchaseOrder("PO-B", Today.AddDays(-40), "S0011", DocumentApprovalStatus.Approved, "u-buyer",
            ("PRD-AA", Today.AddDays(-10), 10m, 0m, false));

        var from = Today.AddDays(-30).ToString("yyyy-MM-dd");
        var to   = Today.ToString("yyyy-MM-dd");

        var bySupplier = ModelOf<List<PurchaseOverdueRowVm>>(
            await NewPurchaseOverdue().Index(from, to, "S001", null, 1, searched: true));
        Assert.Single(bySupplier);
        Assert.Equal("PO-A", bySupplier[0].PurchaseOrderId);

        var byProduct = ModelOf<List<PurchaseOverdueRowVm>>(
            await NewPurchaseOverdue().Index(from, to, null, "PRD-A", 1, searched: true));
        Assert.Single(byProduct);
        Assert.Equal("PO-A", byProduct[0].PurchaseOrderId);
    }

    /// <summary>
    /// 採購員欄位取建單人的 <c>UserName</c>；查無對應使用者時**退回 UserId 而非空白**。
    /// 這條分支只有在使用者被刪掉之後才走得到，正是沒人會手動驗到的那一種。
    /// </summary>
    [Fact]
    public async Task PurchaseOverdue_採購員取顯示名查無退回Id()
    {
        _context.Users.Add(new ApplicationUser { Id = "u-buyer", UserName = "陳採購" });
        _context.SaveChanges();

        AddPurchaseOrder("PO-KNOWN", Today.AddDays(-40), "S001", DocumentApprovalStatus.Approved, "u-buyer",
            ("PRD-A", Today.AddDays(-10), 10m, 0m, false));
        AddPurchaseOrder("PO-GHOST", Today.AddDays(-40), "S001", DocumentApprovalStatus.Approved, "u-gone",
            ("PRD-A", Today.AddDays(-5), 10m, 0m, false));

        var rows = ModelOf<List<PurchaseOverdueRowVm>>(
            await NewPurchaseOverdue().Index(
                Today.AddDays(-30).ToString("yyyy-MM-dd"), Today.ToString("yyyy-MM-dd"),
                null, null, 1, searched: true));

        Assert.Equal("陳採購", rows.Single(r => r.PurchaseOrderId == "PO-KNOWN").Buyer);
        Assert.Equal("u-gone", rows.Single(r => r.PurchaseOrderId == "PO-GHOST").Buyer);
    }

    /// <summary>未按查詢（<c>searched=false</c>）時回空清單，不得直接跑查詢。</summary>
    [Fact]
    public async Task PurchaseOverdue_未查詢時不跑查詢()
    {
        AddPurchaseOrder("PO-01", Today.AddDays(-40), "S001", DocumentApprovalStatus.Approved, "u-buyer",
            ("PRD-A", Today.AddDays(-10), 10m, 0m, false));

        var rows = ModelOf<List<PurchaseOverdueRowVm>>(
            await NewPurchaseOverdue().Index(null, null, null, null, 1, searched: false));

        Assert.Empty(rows);
    }

    /// <summary>閘門動詞是 <c>CanRead</c>（2026-08-14 裁決）。抽出後工具要照抄這一個。</summary>
    [Fact]
    public async Task PurchaseOverdue_閘門動詞為CanRead()
    {
        _perm.Setup(p => p.CanRead("PurchaseOverdue")).ReturnsAsync(false);

        var result = await NewPurchaseOverdue().Index(null, null, null, null, 1, searched: true);

        Assert.IsType<ForbidResult>(result);
    }
}
