using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 委外簡化 B3（mfg-domain.md §19.7）：投產時依唯一有效報價自動建立「已確認」的委外採購單。
/// 每個案例都看「建了什麼」與「沒建什麼」——挑不出唯一廠商時不得退回舊的 PUR 採購單路徑。
/// </summary>
public class OutsourceAutoPoTests : TestBase
{
    private const string PlantId = "P001";
    private static readonly DateOnly Today = new(2026, 5, 25);

    private readonly Mock<IDocNumberService> _mockDocNo = new();
    private readonly Mock<IDateTimeService> _mockDt = new();
    private readonly Mock<IPlantParamService> _mockParam = new();
    private readonly Mock<IApprovalService> _mockApproval = new();
    private readonly Mock<IUserContext> _mockUser = new();

    public OutsourceAutoPoTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockDt.Setup(d => d.Now).Returns(new DateTime(2026, 5, 25, 8, 0, 0, DateTimeKind.Utc));
        _mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(Today);
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns("usr1");
        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string?>()))
                     .ReturnsAsync((DocumentApprovalConfig?)null);
        var seq = 0;
        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(),
                      It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync((string type, string _, DateOnly? __, CancellationToken ___) => $"{type}-{++seq:D4}");
        _mockParam.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
    }

    private void SetAutoPo(string? v) =>
        _mockParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgOutsourceAutoPo, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(v);

    private WorkOrderReleaseService MakeService() => new(
        _context, _mockApproval.Object, _mockParam.Object, _mockDocNo.Object,
        new InventoryReservationService(_context), _mockUser.Object, _mockDt.Object,
        new OutsourceQuotationResolver(_context));

    private void SeedOutsourceWo(string woId, string? supplier = null)
    {
        _context.Products.Add(new Product { ProductCode = "FG1", ProductName = "FG1", StockUom = "PC" });
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = woId, ProductionBatchId = "B1", ProductCode = "FG1", RoutingId = "",
            Quantity = 10, Status = WorkOrderStatus.Planned, IsOutsource = true,
            OutsourceSupplierCode = supplier,
            PlannedStartDate = new DateTime(2026, 5, 26), PlannedEndDate = new DateTime(2026, 6, 5),
            PlantId = PlantId,
            Operations =
            [
                new WorkOrderOperation
                {
                    WorkOrderId = woId, OperationSeq = 1, OperationName = "OS", WorkCenterId = "",
                    IsOutsource = true, BatchQty = 1
                }
            ]
        });
        _context.Dispatches.Add(new Dispatch
        {
            DispatchId = $"{woId}-1", WorkOrderId = woId, OperationSeq = 1, Quantity = 10,
            Status = DispatchStatus.Pending, IsOutsource = true, PlantId = PlantId
        });
    }

    private void AddQuote(string id, string supplier, string? product, decimal price, int leadDays = 7)
        => _context.OutsourceQuotations.Add(new OutsourceQuotation
        {
            QuotationId = id, SupplierCode = supplier, ProductCode = product, UnitPrice = price,
            CurrencyCode = "TWD", UomCode = "PC", LeadDays = leadDays, IsActive = true,
            EffectiveFrom = Today.AddDays(-30)
        });

    [Fact]
    public async Task 參數開_唯一報價廠商_建已確認委外單並回填工單與委外建議()
    {
        SetAutoPo("true");
        SeedOutsourceWo("WO1");
        AddQuote("Q1", "SUP-A", "FG1", 12.5m);
        AddQuote("Q2", "SUP-A", "FG1", 99m);   // 同廠商多筆不影響「唯一廠商」判定
        await _context.SaveChangesAsync();

        var r = await MakeService().AutoReleaseAsync("WO1");

        Assert.Equal(WorkOrderReleaseOutcome.Released, r.Outcome);
        var po = Assert.Single(await _context.OutsourcePurchaseOrders.Include(p => p.Lines).ToListAsync());
        Assert.Equal(OutsourcePoStatus.Confirmed, po.Status);
        Assert.Equal("SUP-A", po.SupplierCode);
        var line = Assert.Single(po.Lines);
        Assert.Equal(("WO1", 1, 10m), (line.SourceWorkOrderId, line.OperationSeq, line.Qty));
        var demand = Assert.Single(await _context.OutsourceDemands.ToListAsync());
        Assert.Equal(demand.DemandId, line.DemandId);
        Assert.Equal(10m, demand.TransferredQty);
        var wo = await _context.WorkOrders.SingleAsync(w => w.WorkOrderId == "WO1");
        Assert.Equal((po.OutsourcePurchaseOrderId, "SUP-A"), (wo.OutsourcePurchaseOrderId, wo.OutsourceSupplierCode));
        Assert.Empty(await _context.PurchaseOrders.ToListAsync());
    }

    [Fact]
    public async Task 參數開_工單未指定廠商且兩家報價_不建任何單據()
    {
        SetAutoPo("true");
        SeedOutsourceWo("WO2");
        AddQuote("Q1", "SUP-A", "FG1", 10m);
        AddQuote("Q2", "SUP-B", "FG1", 11m);
        await _context.SaveChangesAsync();

        await MakeService().AutoReleaseAsync("WO2");

        Assert.Empty(await _context.OutsourcePurchaseOrders.ToListAsync());
        Assert.Empty(await _context.PurchaseOrders.ToListAsync());
        Assert.Null((await _context.WorkOrders.SingleAsync(w => w.WorkOrderId == "WO2")).OutsourcePurchaseOrderId);
    }

    [Fact]
    public async Task 參數開_工單指定廠商但無適用報價_不建單也不退回舊採購單()
    {
        SetAutoPo("true");
        SeedOutsourceWo("WO3", supplier: "SUP-C");
        AddQuote("Q1", "SUP-C", "OTHER", 10m);   // 報給別的品號＝不適用
        await _context.SaveChangesAsync();

        await MakeService().AutoReleaseAsync("WO3");

        Assert.Empty(await _context.OutsourcePurchaseOrders.ToListAsync());
        Assert.Empty(await _context.PurchaseOrders.ToListAsync());
    }

    [Fact]
    public async Task 參數開_工單指定廠商_用該廠商報價_工單無預計交期時依報價前置天數()
    {
        SetAutoPo("true");
        SeedOutsourceWo("WO4", supplier: "SUP-B");
        AddQuote("Q1", "SUP-A", "FG1", 10m);
        AddQuote("Q2", "SUP-B", null, 8m, leadDays: 3);   // 廠商通用報價也算適用
        await _context.SaveChangesAsync();

        await MakeService().AutoReleaseAsync("WO4");

        var po = Assert.Single(await _context.OutsourcePurchaseOrders.Include(p => p.Lines).ToListAsync());
        Assert.Equal("SUP-B", po.SupplierCode);
        Assert.Equal(8m, po.Lines.Single().UnitPrice);
        Assert.Equal(Today.AddDays(3), po.ExpectedDeliveryDate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task 參數關_維持舊行為_有廠商才建PUR採購單(string? flag)
    {
        SetAutoPo(flag);
        SeedOutsourceWo("WO5", supplier: "SUP-A");
        AddQuote("Q1", "SUP-A", "FG1", 10m);
        await _context.SaveChangesAsync();

        await MakeService().AutoReleaseAsync("WO5");

        Assert.Empty(await _context.OutsourcePurchaseOrders.ToListAsync());
        Assert.Single(await _context.PurchaseOrders.ToListAsync());
    }
}
