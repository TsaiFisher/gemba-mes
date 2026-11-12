using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 重工工單建立（工單列表「建立重工」與 NCR 處置「重工」共用）：複製工序與用料、用料依重工數量等比換算。
/// 2026-10-15 前 NCR 路徑只建表頭（投產後排不出派工、發不了料），工單列表路徑則照抄原工單的用料總量。
/// </summary>
public class ReworkWorkOrderServiceTests : TestBase
{
    private const string PlantId = "P001";
    private readonly ReworkWorkOrderService _svc;

    public ReworkWorkOrderServiceTests()
    {
        _context.CurrentPlantId = PlantId;
        var uom = new Mock<IUomService>();
        uom.Setup(u => u.RoundQtyAsync(It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync((string? _, decimal q, CancellationToken _) => Math.Round(q, 2, MidpointRounding.AwayFromZero));
        _svc = new ReworkWorkOrderService(_context, uom.Object);

        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = "WO-ORIG", ProductCode = "FG", RoutingId = "RT", Quantity = 10,
            Status = WorkOrderStatus.Completed, ProductionBatchId = "B1", PlantId = PlantId,
        });
        _context.WorkOrderOperations.Add(new WorkOrderOperation
        {
            WorkOrderId = "WO-ORIG", OperationSeq = 10, OperationName = "加工", WorkCenterId = "WC",
            UnitTime = 2, RequiresFpi = true,
        });
        _context.WorkOrderMaterials.AddRange(
            new WorkOrderMaterial { WorkOrderId = "WO-ORIG", OperationSeq = 10, BomSeq = 1, ProductCode = "RM1",
                                    RequiredQty = 20, UomCode = "EA", IssueMethod = MaterialIssueMethod.Push, IssuedQty = 20 },
            new WorkOrderMaterial { WorkOrderId = "WO-ORIG", OperationSeq = 10, BomSeq = 2, ProductCode = "RM2",
                                    RequiredQty = 7,  UomCode = "EA", IssueMethod = MaterialIssueMethod.Pull, IssuedQty = 7, ReturnedQty = 1 });
        _context.SaveChanges();
    }

    private async Task<WorkOrder> CreateAsync(decimal qty, string product = "FG")
    {
        var orig = await _context.WorkOrders.AsNoTracking().SingleAsync(w => w.WorkOrderId == "WO-ORIG");
        var rw = await _svc.AddAsync(orig, "RW-1", qty, product, DateTime.UtcNow.AddDays(3));
        await _context.SaveChangesAsync();
        return rw;
    }

    [Fact]
    public async Task 表頭_重工類型_連原工單_未釋出草稿_沿用批號與途程()
    {
        var rw = await CreateAsync(3);
        Assert.Equal(WorkOrderType.Rework, rw.Type);
        Assert.Equal("WO-ORIG", rw.ReworkFromWorkOrderId);
        Assert.Equal(WorkOrderStatus.Planned, rw.Status);
        Assert.Equal(Models.DocumentApprovalStatus.Draft, rw.ApprovalStatus);
        Assert.Equal(("B1", "RT", 3m), (rw.ProductionBatchId, rw.RoutingId, rw.Quantity));
    }

    [Fact]
    public async Task 複製工序_含首件檢查旗標()
    {
        await CreateAsync(3);
        var op = await _context.WorkOrderOperations.SingleAsync(o => o.WorkOrderId == "RW-1");
        Assert.Equal((10, "WC", 2m, true), (op.OperationSeq, op.WorkCenterId, op.UnitTime, op.RequiresFpi));
    }

    [Fact]
    public async Task 用料依重工數量等比換算_已發已退量從零起算_發料方式沿用()
    {
        await CreateAsync(3);   // 3 / 10
        var mats = await _context.WorkOrderMaterials.Where(m => m.WorkOrderId == "RW-1")
            .OrderBy(m => m.BomSeq).ToListAsync();
        Assert.Equal(2, mats.Count);
        Assert.Equal((6m, 0m, 0m, MaterialIssueMethod.Push), (mats[0].RequiredQty, mats[0].IssuedQty, mats[0].ReturnedQty, mats[0].IssueMethod));
        Assert.Equal((2.1m, 0m, 0m, MaterialIssueMethod.Pull), (mats[1].RequiredQty, mats[1].IssuedQty, mats[1].ReturnedQty, mats[1].IssueMethod));
    }

    [Fact]
    public async Task 只勾部分工序_只複製那些工序與其下的用料()
    {
        _context.WorkOrderOperations.Add(new WorkOrderOperation
            { WorkOrderId = "WO-ORIG", OperationSeq = 20, OperationName = "組裝", WorkCenterId = "WC2", UnitTime = 1 });
        _context.WorkOrderMaterials.Add(new WorkOrderMaterial
            { WorkOrderId = "WO-ORIG", OperationSeq = 20, BomSeq = 3, ProductCode = "RM3", RequiredQty = 5, UomCode = "EA" });
        await _context.SaveChangesAsync();

        var orig = await _context.WorkOrders.AsNoTracking().SingleAsync(w => w.WorkOrderId == "WO-ORIG");
        await _svc.AddAsync(orig, "RW-1", 4, "FG", DateTime.UtcNow.AddDays(3), operationSeqs: new[] { 20 });
        await _context.SaveChangesAsync();

        Assert.Equal(new[] { 20 }, await _context.WorkOrderOperations.Where(o => o.WorkOrderId == "RW-1")
            .Select(o => o.OperationSeq).ToListAsync());
        var mat = await _context.WorkOrderMaterials.SingleAsync(m => m.WorkOrderId == "RW-1");
        Assert.Equal(("RM3", 2m), (mat.ProductCode, mat.RequiredQty));   // 5 × 4/10
    }

    [Fact]
    public async Task 品號與原工單不同_不複製工序與用料()
    {
        await CreateAsync(2, product: "SUB");
        Assert.False(await _context.WorkOrderOperations.AnyAsync(o => o.WorkOrderId == "RW-1"));
        Assert.False(await _context.WorkOrderMaterials.AnyAsync(m => m.WorkOrderId == "RW-1"));
    }

    [Fact]
    public async Task 原工單的用料不受影響()
    {
        await CreateAsync(3);
        var orig = await _context.WorkOrderMaterials.Where(m => m.WorkOrderId == "WO-ORIG")
            .OrderBy(m => m.BomSeq).Select(m => new { m.RequiredQty, m.IssuedQty }).ToListAsync();
        Assert.Equal(new[] { (20m, 20m), (7m, 7m) }, orig.Select(o => (o.RequiredQty, o.IssuedQty)));
    }

    [Theory]
    [InlineData(WorkOrderType.Rework, MaterialSlipCategory.ReworkIssue,   "MaterialSlip_RI")]
    [InlineData(WorkOrderType.Normal, MaterialSlipCategory.InternalIssue, "MaterialSlip_MI")]
    public void 投產自動開的Push發料單_重工工單開重工發料(WorkOrderType type, MaterialSlipCategory cat, string docType)
        => Assert.Equal((cat, docType), MaterialSlipFlow.PushIssueFor(type));

    // ── 不良品本身列為用料（2026-10-16，§16.4b）────────────────────────────

    /// <summary>
    /// 不良品已入庫時，重工工單要把它本身發進來——否則完工多出一份良品、不良品仍留在庫存，
    /// 且完工時沒有「原值」可回存貨。掛在第一道重做工序、BomSeq 0、Push（投產自動開的重工發料一起發）、
    /// 庫存品（不另外生產或採購），標準單價取本廠現行庫存單位成本。
    /// </summary>
    [Fact]
    public async Task 不良品已入庫_另加一行不良品本身的Push用料_數量等於重工數量()
    {
        _context.Products.Add(new Modules.BAS.Models.Product { ProductCode = "FG", StockUom = "PC", StandardCost = 999m });
        _context.Inventories.Add(new Modules.INV.Models.Inventory
        {
            PlantId = PlantId, ProductCode = "FG", LocationCode = "QC1", LotNo = "", QtyOnHand = 5, UnitCost = 700m,
        });
        _context.SaveChanges();

        var orig = await _context.WorkOrders.AsNoTracking().SingleAsync(w => w.WorkOrderId == "WO-ORIG");
        await _svc.AddAsync(orig, "RW-1", 3, "FG", DateTime.UtcNow.AddDays(3), includeDefective: true);
        await _context.SaveChangesAsync();

        var fg = await _context.WorkOrderMaterials.SingleAsync(m => m.WorkOrderId == "RW-1" && m.ProductCode == "FG");
        Assert.Equal((10, 0, 3m, "PC"), (fg.OperationSeq, fg.BomSeq, fg.RequiredQty, fg.UomCode));
        Assert.Equal(MaterialIssueMethod.Push, fg.IssueMethod);
        Assert.Equal(MrpMode.StockOnly, fg.SourceMode);
        Assert.Equal(700m, fg.UnitCost);
        // 原 BOM 的料照常等比複製
        Assert.Equal(3, await _context.WorkOrderMaterials.CountAsync(m => m.WorkOrderId == "RW-1"));
    }

    [Fact]
    public async Task 不良品不在庫_預設不加不良品本身的用料()
    {
        await CreateAsync(3);
        Assert.False(await _context.WorkOrderMaterials.AnyAsync(m => m.WorkOrderId == "RW-1" && m.ProductCode == "FG"));
    }

    [Fact]
    public async Task 品號與原工單不同_即使勾了不良品在庫也不加用料()
    {
        var orig = await _context.WorkOrders.AsNoTracking().SingleAsync(w => w.WorkOrderId == "WO-ORIG");
        await _svc.AddAsync(orig, "RW-1", 2, "SUB", DateTime.UtcNow.AddDays(3), includeDefective: true);
        await _context.SaveChangesAsync();
        Assert.False(await _context.WorkOrderMaterials.AnyAsync(m => m.WorkOrderId == "RW-1"));
    }
}
