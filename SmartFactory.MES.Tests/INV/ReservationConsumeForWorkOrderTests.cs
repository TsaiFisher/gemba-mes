using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// 領料消耗與工單結案釋放（Phase 0 修補）：發料已扣在手庫存，對應預留必須等額退場，
/// 否則「庫存已減、預留仍在」會對同一批料重複扣減廠級可用量。
/// </summary>
public class ReservationConsumeForWorkOrderTests : TestBase
{
    private readonly InventoryReservationService _svc;
    private const string PlantId     = "P001";
    private const string ProductCode = "MAT001";
    private const string WorkOrderId = "WO-001";

    public ReservationConsumeForWorkOrderTests()
    {
        _context.CurrentPlantId = PlantId;
        _svc = new InventoryReservationService(_context);

        _context.ProductCategories.Add(new ProductCategory
            { CategoryCode = "RAW", CategoryName = "原料", IsActive = true });
        _context.Products.Add(new Product
            { ProductCode = ProductCode, CategoryCode = "RAW", IsActive = true });
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = ProductCode,
            LocationCode = "A-01", LotNo = "", QtyOnHand = 100m
        });
        _context.SaveChanges();
    }

    private void AddStagingOrder(string stagingId, string? sourceWorkOrderId, decimal qty,
        string sourceType = "WorkOrder", string? headerSourceId = WorkOrderId)
    {
        _context.MaterialStagingOrders.Add(new MaterialStagingOrder
        {
            StagingId = stagingId, PlantId = PlantId, Status = "Completed", CreatedBy = "u1",
            SourceType = sourceType, SourceId = headerSourceId,
            Lines =
            {
                new MaterialStagingLine
                {
                    StagingId = stagingId, LineNo = 1, ProductCode = ProductCode,
                    UomCode = "EA", RequiredQty = qty, SourceWorkOrderId = sourceWorkOrderId,
                }
            }
        });
        _context.InventoryReservations.Add(new InventoryReservation
        {
            PlantId = PlantId, ProductCode = ProductCode, ReservedQty = qty,
            Status = ReservationStatus.Active, Category = ReservationCategory.OnHand,
            SourceType = ReservationSourceType.MaterialStagingOrder,
            SourceDocId = stagingId, SourceLineSeq = 1, RequiredDate = DateTime.UtcNow,
        });
        _context.SaveChanges();
    }

    private void AddWorkOrderReservation(decimal qty)
    {
        _context.InventoryReservations.Add(new InventoryReservation
        {
            PlantId = PlantId, ProductCode = ProductCode, ReservedQty = qty,
            Status = ReservationStatus.Active, Category = ReservationCategory.OnHand,
            SourceType = ReservationSourceType.WorkOrderMaterial,
            SourceDocId = WorkOrderId, RequiredDate = DateTime.UtcNow,
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task Consume_ConsumesStagingReservationFirst()
    {
        // 備料單接手的鎖量對應的就是已備妥、實際被領走的那批料；
        // 先扣工單預留會讓備料單的鎖量變成沒有庫存對應的孤兒。
        AddStagingOrder("IM-1", WorkOrderId, 30m);
        AddWorkOrderReservation(20m);

        var consumed = await _svc.ConsumeForWorkOrderAsync(WorkOrderId, ProductCode, 30m);

        Assert.Equal(30m, consumed);
        Assert.Equal(0m, await _svc.GetReservedBySourceAsync(
            ReservationSourceType.MaterialStagingOrder, "IM-1", ProductCode));
        Assert.Equal(20m, await _svc.GetReservedBySourceAsync(
            ReservationSourceType.WorkOrderMaterial, WorkOrderId, ProductCode));
    }

    [Fact]
    public async Task Consume_FallsBackToWorkOrderReservation()
    {
        AddStagingOrder("IM-1", WorkOrderId, 10m);
        AddWorkOrderReservation(40m);

        var consumed = await _svc.ConsumeForWorkOrderAsync(WorkOrderId, ProductCode, 25m);

        Assert.Equal(25m, consumed);
        Assert.Equal(0m, await _svc.GetReservedBySourceAsync(
            ReservationSourceType.MaterialStagingOrder, "IM-1", ProductCode));
        Assert.Equal(25m, await _svc.GetReservedBySourceAsync(
            ReservationSourceType.WorkOrderMaterial, WorkOrderId, ProductCode));
    }

    [Fact]
    public async Task Consume_MoreThanReserved_ConsumesWhatExists()
    {
        // 超領／未預留的補領屬正常業務，不拋例外，只消耗得到的部分
        AddWorkOrderReservation(10m);

        var consumed = await _svc.ConsumeForWorkOrderAsync(WorkOrderId, ProductCode, 50m);

        Assert.Equal(10m, consumed);
    }

    [Fact]
    public async Task Consume_BatchStagingOrder_OnlyTouchesOwnLines()
    {
        // 生產批備料單一張橫跨多張工單：只能扣掛本工單的明細行，別張工單的鎖量不能動
        AddStagingOrder("IM-BATCH", sourceWorkOrderId: "WO-OTHER", qty: 30m,
            sourceType: "ProductionBatch", headerSourceId: "PB-001");

        var consumed = await _svc.ConsumeForWorkOrderAsync(WorkOrderId, ProductCode, 30m);

        Assert.Equal(0m, consumed);
        Assert.Equal(30m, await _svc.GetReservedBySourceAsync(
            ReservationSourceType.MaterialStagingOrder, "IM-BATCH", ProductCode));
    }

    [Fact]
    public async Task ReleaseWorkOrderStaging_ReleasesOnlyThisWorkOrder()
    {
        AddStagingOrder("IM-1", WorkOrderId, 30m);
        AddStagingOrder("IM-BATCH", sourceWorkOrderId: "WO-OTHER", qty: 15m,
            sourceType: "ProductionBatch", headerSourceId: "PB-001");

        var released = await _svc.ReleaseWorkOrderStagingAsync(WorkOrderId);

        Assert.Equal(1, released);
        Assert.Equal(0m, await _svc.GetReservedBySourceAsync(
            ReservationSourceType.MaterialStagingOrder, "IM-1", ProductCode));
        Assert.Equal(15m, await _svc.GetReservedBySourceAsync(
            ReservationSourceType.MaterialStagingOrder, "IM-BATCH", ProductCode));
    }
}
