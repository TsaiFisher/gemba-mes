using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.MFG.ViewModels;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// WipReportController（manual-test-plan §12.4「WIP 報表未納入自動化」）。
///
/// 這張表是 5-6 報表對帳的其中一項，先前**零覆蓋**。它的失效方式全都是「少一列」
/// 而不是報錯：狀態集合寫錯就漏掉在製工單、廠別條件寫錯就把別廠的在製品算進本廠，
/// 兩者在畫面上都只是「數字比預期小／大」，沒有任何地方會叫。
/// </summary>
public class WipReportControllerTests : TestBase
{
    private const string PlantId = "P001";
    private const string Other   = "P002";

    private readonly Mock<IUserContext>       _user = new();
    private readonly Mock<IPermissionService> _perm = new();
    private readonly Mock<IExportService>     _export = new();
    private readonly Mock<IDateTimeService>   _dt = new();
    private readonly Mock<IUomService>        _uom = new();
    private readonly Mock<ICurrencyService>   _currency;
    private readonly Mock<Microsoft.Extensions.Localization.IStringLocalizer<ProjectZSharedResource>> _localizer = new();

    public WipReportControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _user.Setup(u => u.PlantId).Returns(PlantId);
        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);
        _uom.Setup(u => u.GetAllDecimalPlacesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>());
        _currency = GetMockCurrencyService();
        _dt.Setup(d => d.Now).Returns(new DateTime(2026, 6, 30, 8, 0, 0, DateTimeKind.Utc));
        _export.Setup(e => e.ToExcel(It.IsAny<ExportRequest>())).Returns(new byte[] { 1 });
    }

    // 材料腿刻意用真的讀取器而非 mock：這一腿的失效方式是「查錯來源」，
    // mock 掉的話驗到的是測試自己給的數字（見 W2-F7）。
    private WipReportController Build() => new(
        _context, _user.Object, _perm.Object, _export.Object, _dt.Object,
        _uom.Object, _currency.Object, _localizer.Object,
        new WorkOrderIssuedMaterialCostReader(_context));

    private static List<WipReportRowVm> RowsOf(IActionResult result)
        => Assert.IsAssignableFrom<List<WipReportRowVm>>(Assert.IsType<ViewResult>(result).Model);

    // ── 納入條件 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_NotSearched_ReturnsEmpty_WithoutTouchingData()
    {
        SeedWo("WO-1", WorkOrderStatus.InProgress);

        var rows = RowsOf(await Build().Index(searchProduct: null, searched: false));

        Assert.Empty(rows);
    }

    /// <summary>只有 Released／InProgress 算在製——Planned 還沒投產、Completed 已經入庫。</summary>
    [Theory]
    [InlineData(WorkOrderStatus.Planned,    false)]
    [InlineData(WorkOrderStatus.Released,   true)]
    [InlineData(WorkOrderStatus.InProgress, true)]
    [InlineData(WorkOrderStatus.Completed,  false)]
    [InlineData(WorkOrderStatus.Closed,     false)]
    [InlineData(WorkOrderStatus.Void,       false)]
    public async Task Index_OnlyReleasedAndInProgressAreWip(WorkOrderStatus status, bool included)
    {
        SeedWo("WO-1", status);

        var rows = RowsOf(await Build().Index(null, searched: true));

        Assert.Equal(included ? 1 : 0, rows.Count);
    }

    [Fact]
    public async Task Index_ExcludesOtherPlants()
    {
        // 別廠的在製品混進來的話，WIP 金額會比該廠的帳多一截，而兩邊都不會報錯。
        SeedWo("WO-MINE",  WorkOrderStatus.InProgress);
        SeedWo("WO-OTHER", WorkOrderStatus.InProgress, plantId: Other);

        var rows = RowsOf(await Build().Index(null, searched: true));

        Assert.Equal("WO-MINE", Assert.Single(rows).WorkOrderId);
    }

    [Fact]
    public async Task Index_FiltersByProduct()
    {
        SeedWo("WO-A", WorkOrderStatus.InProgress, product: "FG-A");
        SeedWo("WO-B", WorkOrderStatus.InProgress, product: "FG-B");

        var rows = RowsOf(await Build().Index("FG-B", searched: true));

        Assert.Equal("WO-B", Assert.Single(rows).WorkOrderId);
    }

    // ── 金額 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_SumsThreeCostLegs_AndDerivesWipValueAndVariance()
    {
        SeedWo("WO-1", WorkOrderStatus.InProgress,
            stdMat: 100m, stdLab: 20m, stdOvh: 30m,
            actLab: 25m, actOvh: 35m);
        SeedPostedIssue("WO-1", "MI-1", qty: 11m, unitCost: 10m);   // 材料 110

        var row = Assert.Single(RowsOf(await Build().Index(null, searched: true)));

        Assert.Equal(150m, row.StdTotalCost);
        Assert.Equal(170m, row.ActTotalCost);
        Assert.Equal(170m, row.WipValue);          // WIP 金額＝實際成本
        Assert.Equal(20m,  row.CostVariance);      // 差異＝實際 − 標準
    }

    /// <summary>
    /// 材料腿必須取「已過帳發料淨值」，不是 <c>WorkOrder.ActualMaterialCost</c>。
    ///
    /// 那個欄位只在**完工入庫**時回寫，而本報表只收在製工單——兩者互斥，
    /// 於是每一張在製工單的材料恆為 0：料已離開倉庫、成品還沒生出來，
    /// 那筆錢在畫面上憑空消失，而「成本差異」會變成 100% 有利差異（W2-F7）。
    /// 月結快照那條路早就改成讀庫存異動了，畫面沒跟上，同一個 WIP 兩個答案。
    /// </summary>
    [Fact]
    public async Task Index_MaterialLeg_ComesFromPostedIssues_NotTheCompletionOnlyField()
    {
        // 欄位是髒的（完工才會寫，在製工單上出現代表資料有問題），發料才是真的
        SeedWo("WO-1", WorkOrderStatus.Released, actMat: 999_999m);
        SeedPostedIssue("WO-1", "MI-1", qty: 12m, unitCost: 283.5m);   // 3,402
        SeedPostedIssue("WO-1", "MX-1", qty: 2m,  unitCost: 283.5m, direction: 1);   // 退料 −567

        var row = Assert.Single(RowsOf(await Build().Index(null, searched: true)));

        Assert.Equal(12m * 283.5m - 2m * 283.5m, row.WipValue);
    }

    /// <summary>後沖料（單號慣例「{工單}-BF」）沒有發退料單，仍必須算進材料腿。</summary>
    [Fact]
    public async Task Index_MaterialLeg_IncludesBackflushMovements()
    {
        SeedWo("WO-1", WorkOrderStatus.InProgress);
        SeedMovement("WO-1-BF", qty: 5m, unitCost: 40m, direction: -1);

        var row = Assert.Single(RowsOf(await Build().Index(null, searched: true)));

        Assert.Equal(200m, row.WipValue);
    }

    [Fact]
    public async Task Index_ZeroCostWorkOrder_IsStillListed()
    {
        // 成本全 0 的在製工單（費率沒設、料還沒發）仍必須出現在表上——
        // 濾掉的話「工單投產了卻不在 WIP 表」不會有任何提示。
        SeedWo("WO-1", WorkOrderStatus.InProgress);

        var row = Assert.Single(RowsOf(await Build().Index(null, searched: true)));
        Assert.Equal(0m, row.WipValue);
    }

    // ── 排序與其他 ────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_OrdersByPlannedEndThenWorkOrderId()
    {
        SeedWo("WO-C", WorkOrderStatus.InProgress, end: new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc));
        SeedWo("WO-A", WorkOrderStatus.InProgress, end: new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc));
        SeedWo("WO-B", WorkOrderStatus.InProgress, end: new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc));

        var rows = RowsOf(await Build().Index(null, searched: true));

        Assert.Equal(new[] { "WO-B", "WO-C", "WO-A" }, rows.Select(r => r.WorkOrderId));
    }

    [Fact]
    public async Task Index_JoinsProductUom_AndSurvivesMissingProduct()
    {
        // 品號主檔被刪掉的舊工單仍要列出來（LEFT JOIN），否則報表會無聲少一列。
        _context.Products.Add(new Product { ProductCode = "FG-A", ProductName = "A", StockUom = "PCS" });
        _context.SaveChanges();
        SeedWo("WO-A", WorkOrderStatus.InProgress, product: "FG-A");
        SeedWo("WO-X", WorkOrderStatus.InProgress, product: "GONE");

        var rows = RowsOf(await Build().Index(null, searched: true));

        Assert.Equal("PCS", rows.Single(r => r.WorkOrderId == "WO-A").UomCode);
        Assert.Null(rows.Single(r => r.WorkOrderId == "WO-X").UomCode);
    }

    [Fact]
    public async Task Index_NoReadPermission_Forbidden()
    {
        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(false);

        Assert.IsType<ForbidResult>(await Build().Index(null, searched: true));
    }

    [Fact]
    public async Task Export_NoPrintPermission_Forbidden()
    {
        _perm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(false);

        Assert.IsType<ForbidResult>(await Build().Export(null));
    }

    /// <summary>匯出與畫面必須是同一份資料——兩邊各自組查詢是報表對帳最常見的分歧來源。</summary>
    [Fact]
    public async Task Export_UsesTheSameRowsAsTheScreen()
    {
        SeedWo("WO-1", WorkOrderStatus.InProgress, actMat: 10m);
        SeedWo("WO-2", WorkOrderStatus.Planned);          // 不該進匯出
        ExportRequest? captured = null;
        _export.Setup(e => e.ToExcel(It.IsAny<ExportRequest>()))
               .Callback<ExportRequest>(r => captured = r)
               .Returns(new byte[] { 1 });

        Assert.IsType<FileContentResult>(await Build().Export(null));

        Assert.NotNull(captured);
        var exported = captured!.Rows.ToList();
        Assert.Equal("WO-1", Assert.Single(exported)[0]);
    }

    // ── 輔助 ──────────────────────────────────────────────────────────────

    /// <summary>一張已過帳的發料單（或退料單，direction=1）＋對應的庫存異動。</summary>
    private void SeedPostedIssue(string woId, string slipId, decimal qty, decimal unitCost, int direction = -1)
    {
        _context.MaterialSlips.Add(new MaterialSlip
        {
            SlipId = slipId, SlipNo = slipId, Category = MaterialSlipCategory.InternalIssue,
            Status = MaterialSlipStatus.Posted, SourceDocType = "WorkOrder", SourceDocId = woId,
            PlantId = PlantId,
        });
        _context.SaveChanges();
        SeedMovement(slipId, qty, unitCost, direction);
    }

    private void SeedMovement(string docNo, decimal qty, decimal unitCost, int direction)
    {
        _context.StockMovements.Add(new SmartFactory.MES.Modules.INV.Models.StockMovement
        {
            MovementId = Guid.NewGuid(), TypeCode = direction == -1 ? "IO" : "IR",
            TypeCategory = "Production", DocNo = docNo, DocLineNo = 1,
            MovementDate = new DateOnly(2026, 7, 2), PlantId = PlantId,
            ProductCode = "MAT-A", LocationCode = "L1", LotNo = "", UomCode = "PC",
            Qty = qty, StockUomCode = "PC", StockQty = qty, Direction = direction,
            UnitCost = unitCost, TotalCost = qty * unitCost,
        });
        _context.SaveChanges();
    }

    private void SeedWo(
        string id, WorkOrderStatus status, string product = "FG-A", string? plantId = null,
        decimal stdMat = 0m, decimal stdLab = 0m, decimal stdOvh = 0m,
        decimal actMat = 0m, decimal actLab = 0m, decimal actOvh = 0m,
        DateTime? end = null)
    {
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = id, ProductionBatchId = "B-1", ProductCode = product, RoutingId = "",
            Quantity = 10m, Status = status, PlantId = plantId ?? PlantId,
            PlannedStartDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            PlannedEndDate   = end ?? new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
            StandardMaterialCost = stdMat, StandardLaborCost = stdLab, StandardOverheadCost = stdOvh,
            ActualMaterialCost   = actMat, ActualLaborCost   = actLab, ActualOverheadCost   = actOvh,
        });
        _context.SaveChanges();
    }
}
