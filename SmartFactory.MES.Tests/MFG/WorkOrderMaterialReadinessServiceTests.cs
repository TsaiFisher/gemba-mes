using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// §21.5 物料備齊確認閘口服務測試：可用量、Backflush 略過、下階半成品完工、整體狀態寫回。
/// </summary>
public class WorkOrderMaterialReadinessServiceTests : TestBase
{
    private const string PlantId = "P001";
    private readonly WorkOrderMaterialReadinessService _svc;

    public WorkOrderMaterialReadinessServiceTests()
    {
        // WorkOrder/Inventory 皆 IPlantEntity，需設 CurrentPlantId 否則 Global Query Filter 濾空。
        _context.CurrentPlantId = PlantId;
        _svc = new WorkOrderMaterialReadinessService(_context, new InventoryReservationService(_context));
    }

    private void SeedInventory(string product, decimal qty)
        => _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = product,
            LocationCode = "A-01", LotNo = "", QtyOnHand = qty
        });

    private WorkOrder NewWo(string id, params WorkOrderMaterial[] mats)
    {
        var op = new WorkOrderOperation { WorkOrderId = id, OperationSeq = 1, WorkCenterId = "WC1" };
        foreach (var m in mats) { m.WorkOrderId = id; m.OperationSeq = 1; op.Materials.Add(m); }
        return new WorkOrder
        {
            WorkOrderId = id, ProductCode = "FG", RoutingId = "R1",
            ProductionBatchId = "B1", PlantId = PlantId, Quantity = 10m,
            Status = WorkOrderStatus.Released,
            Operations = { op }
        };
    }

    [Fact]
    public async Task Check_AllAvailable_ReturnsReady_AndPersists()
    {
        SeedInventory("PUR-OK", 100m);
        var wo = NewWo("WO-1",
            new WorkOrderMaterial { BomSeq = 1, ProductCode = "PUR-OK", RequiredQty = 50m, UomCode = "EA", IssueMethod = MaterialIssueMethod.Push },
            new WorkOrderMaterial { BomSeq = 2, ProductCode = "BF", RequiredQty = 999m, UomCode = "EA", IssueMethod = MaterialIssueMethod.Backflush });
        _context.WorkOrders.Add(wo);
        await _context.SaveChangesAsync();

        var result = await _svc.CheckAsync("WO-1");

        Assert.Equal(MaterialReadinessStatus.Ready, result.Overall);
        Assert.All(result.Lines, l => Assert.Equal(MaterialLineReadiness.Ready, l.State));
        // 寫回
        var reloaded = await _context.WorkOrders.FindAsync("WO-1");
        Assert.Equal(MaterialReadinessStatus.Ready, reloaded!.MaterialReadinessStatus);
    }

    [Fact]
    public async Task Check_PurchasedShortage_ReturnsShortage()
    {
        SeedInventory("PUR-SHORT", 10m);   // 需求 50，可用 10 → 缺
        var wo = NewWo("WO-2",
            new WorkOrderMaterial { BomSeq = 1, ProductCode = "PUR-SHORT", RequiredQty = 50m, UomCode = "EA", IssueMethod = MaterialIssueMethod.Push });
        _context.WorkOrders.Add(wo);
        await _context.SaveChangesAsync();

        var result = await _svc.CheckAsync("WO-2");

        Assert.Equal(MaterialReadinessStatus.Shortage, result.Overall);
        Assert.Equal(MaterialLineReadiness.Shortage, Assert.Single(result.Lines).State);
    }

    [Fact]
    public async Task Check_ActiveReservationReducesAvailable_CausesShortage()
    {
        SeedInventory("PUR-RES", 60m);
        _context.InventoryReservations.Add(new InventoryReservation
        {
            PlantId = PlantId, ProductCode = "PUR-RES", ReservedQty = 40m,
            Status = ReservationStatus.Active, SourceType = ReservationSourceType.MrpRunLine,
            SourceDocId = "OTHER", RequiredDate = DateTime.UtcNow
        });
        var wo = NewWo("WO-3",
            new WorkOrderMaterial { BomSeq = 1, ProductCode = "PUR-RES", RequiredQty = 50m, UomCode = "EA", IssueMethod = MaterialIssueMethod.Push });
        _context.WorkOrders.Add(wo);
        await _context.SaveChangesAsync();

        // 可用 = 60 − 40 = 20 < 50 → 缺
        var result = await _svc.CheckAsync("WO-3");
        Assert.Equal(MaterialReadinessStatus.Shortage, result.Overall);
    }

    [Fact]
    public async Task Check_OwnWorkOrderReservation_CountsAsCovered_NotShortage()
    {
        // MRP 展開時已為本工單鎖住全部在手量：若不把自身預留加回，本工單會被自己的預留判為缺料。
        SeedInventory("PUR-OWN", 50m);
        _context.InventoryReservations.Add(new InventoryReservation
        {
            PlantId = PlantId, ProductCode = "PUR-OWN", ReservedQty = 50m,
            Status = ReservationStatus.Active, SourceType = ReservationSourceType.WorkOrderMaterial,
            SourceDocId = "WO-OWN", RequiredDate = DateTime.UtcNow
        });
        var wo = NewWo("WO-OWN",
            new WorkOrderMaterial { BomSeq = 1, ProductCode = "PUR-OWN", RequiredQty = 50m, UomCode = "EA", IssueMethod = MaterialIssueMethod.Push });
        _context.WorkOrders.Add(wo);
        await _context.SaveChangesAsync();

        var result = await _svc.CheckAsync("WO-OWN");

        // 自由量 0 ＋ 自身預留 50 = 50 ≥ 50 → 備齊
        Assert.Equal(MaterialReadinessStatus.Ready, result.Overall);
        Assert.Equal(50m, Assert.Single(result.Lines).AvailableQty);
    }

    [Fact]
    public async Task Check_StagingOrderOfThisWorkOrder_CountsAsCovered()
    {
        // 備料單確認時工單預留已轉移給備料單；工單仍應視為備齊（否則備完料反而變缺料）。
        SeedInventory("PUR-STAGE", 50m);
        _context.MaterialStagingOrders.Add(new MaterialStagingOrder
        {
            StagingId = "IM-1", PlantId = PlantId, SourceType = "WorkOrder", SourceId = "WO-STAGE",
            Status = "Confirmed", CreatedBy = "u1"
        });
        _context.InventoryReservations.Add(new InventoryReservation
        {
            PlantId = PlantId, ProductCode = "PUR-STAGE", ReservedQty = 50m,
            Status = ReservationStatus.Active, SourceType = ReservationSourceType.MaterialStagingOrder,
            SourceDocId = "IM-1", RequiredDate = DateTime.UtcNow
        });
        var wo = NewWo("WO-STAGE",
            new WorkOrderMaterial { BomSeq = 1, ProductCode = "PUR-STAGE", RequiredQty = 50m, UomCode = "EA", IssueMethod = MaterialIssueMethod.Push });
        _context.WorkOrders.Add(wo);
        await _context.SaveChangesAsync();

        var result = await _svc.CheckAsync("WO-STAGE");

        Assert.Equal(MaterialReadinessStatus.Ready, result.Overall);
    }

    [Fact]
    public async Task Check_StagingOrderOfAnotherWorkOrder_StillShortage()
    {
        // 別張工單的備料單鎖走的量不算自己的。
        SeedInventory("PUR-STAGE2", 50m);
        _context.MaterialStagingOrders.Add(new MaterialStagingOrder
        {
            StagingId = "IM-2", PlantId = PlantId, SourceType = "WorkOrder", SourceId = "WO-ELSE",
            Status = "Confirmed", CreatedBy = "u1"
        });
        _context.InventoryReservations.Add(new InventoryReservation
        {
            PlantId = PlantId, ProductCode = "PUR-STAGE2", ReservedQty = 50m,
            Status = ReservationStatus.Active, SourceType = ReservationSourceType.MaterialStagingOrder,
            SourceDocId = "IM-2", RequiredDate = DateTime.UtcNow
        });
        var wo = NewWo("WO-STAGE2",
            new WorkOrderMaterial { BomSeq = 1, ProductCode = "PUR-STAGE2", RequiredQty = 50m, UomCode = "EA", IssueMethod = MaterialIssueMethod.Push });
        _context.WorkOrders.Add(wo);
        await _context.SaveChangesAsync();

        var result = await _svc.CheckAsync("WO-STAGE2");

        Assert.Equal(MaterialReadinessStatus.Shortage, result.Overall);
    }

    [Fact]
    public async Task Check_OtherWorkOrderReservation_StillShortage()
    {
        // 只加回「自身」預留：別張工單鎖走的量仍必須扣減。
        SeedInventory("PUR-OTHER-WO", 50m);
        _context.InventoryReservations.Add(new InventoryReservation
        {
            PlantId = PlantId, ProductCode = "PUR-OTHER-WO", ReservedQty = 50m,
            Status = ReservationStatus.Active, SourceType = ReservationSourceType.WorkOrderMaterial,
            SourceDocId = "WO-SOMEONE-ELSE", RequiredDate = DateTime.UtcNow
        });
        var wo = NewWo("WO-OWN2",
            new WorkOrderMaterial { BomSeq = 1, ProductCode = "PUR-OTHER-WO", RequiredQty = 50m, UomCode = "EA", IssueMethod = MaterialIssueMethod.Push });
        _context.WorkOrders.Add(wo);
        await _context.SaveChangesAsync();

        var result = await _svc.CheckAsync("WO-OWN2");

        Assert.Equal(MaterialReadinessStatus.Shortage, result.Overall);
    }

    [Fact]
    public async Task Check_OwnReservationPartial_ShortageByRemainder()
    {
        // MRP 只鎖到在手 110（需求 110.25）→ 仍缺 0.25，自身預留加回不得把缺口抹平。
        SeedInventory("PUR-PART", 110m);
        _context.InventoryReservations.Add(new InventoryReservation
        {
            PlantId = PlantId, ProductCode = "PUR-PART", ReservedQty = 110m,
            Status = ReservationStatus.Active, SourceType = ReservationSourceType.WorkOrderMaterial,
            SourceDocId = "WO-PART", RequiredDate = DateTime.UtcNow
        });
        var wo = NewWo("WO-PART",
            new WorkOrderMaterial { BomSeq = 1, ProductCode = "PUR-PART", RequiredQty = 110.25m, UomCode = "EA", IssueMethod = MaterialIssueMethod.Push });
        _context.WorkOrders.Add(wo);
        await _context.SaveChangesAsync();

        var result = await _svc.CheckAsync("WO-PART");

        Assert.Equal(MaterialReadinessStatus.Shortage, result.Overall);
        Assert.Equal(110m, Assert.Single(result.Lines).AvailableQty);
    }

    [Fact]
    public async Task Check_ChildSubassemblyNotCompleted_PendingSubassembly()
    {
        // 子工單未完工
        var child = NewWo("WO-CHILD");
        child.Status = WorkOrderStatus.InProgress;
        _context.WorkOrders.Add(child);

        var wo = NewWo("WO-4",
            new WorkOrderMaterial { BomSeq = 1, ProductCode = "SEMI", RequiredQty = 5m, UomCode = "EA", IssueMethod = MaterialIssueMethod.Push, ChildWorkOrderId = "WO-CHILD" });
        _context.WorkOrders.Add(wo);
        await _context.SaveChangesAsync();

        var result = await _svc.CheckAsync("WO-4");
        Assert.Equal(MaterialReadinessStatus.Shortage, result.Overall);
        Assert.Equal(MaterialLineReadiness.PendingSubassembly, Assert.Single(result.Lines).State);
    }

    [Fact]
    public async Task Check_ChildSubassemblyCompleted_Ready()
    {
        var child = NewWo("WO-CHILD2");
        child.Status = WorkOrderStatus.Completed;
        _context.WorkOrders.Add(child);

        var wo = NewWo("WO-5",
            new WorkOrderMaterial { BomSeq = 1, ProductCode = "SEMI", RequiredQty = 5m, UomCode = "EA", IssueMethod = MaterialIssueMethod.Push, ChildWorkOrderId = "WO-CHILD2" });
        _context.WorkOrders.Add(wo);
        await _context.SaveChangesAsync();

        var result = await _svc.CheckAsync("WO-5");
        Assert.Equal(MaterialReadinessStatus.Ready, result.Overall);
    }
}
