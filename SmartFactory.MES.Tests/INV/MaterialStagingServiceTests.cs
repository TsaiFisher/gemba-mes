using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using Moq;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// MaterialStagingService 單元測試：確認鎖庫存、可用量不足拋例外、作廢釋放預留。
/// </summary>
public class MaterialStagingServiceTests : TestBase
{
    private readonly InventoryReservationService _reservations;
    private readonly MaterialStagingService _svc;
    private const string PlantId     = "P001";
    private const string ProductCode  = "MAT001";

    public MaterialStagingServiceTests()
    {
        _context.CurrentPlantId = PlantId;
        _reservations = new InventoryReservationService(_context);
        _svc = new MaterialStagingService(_context, _reservations,
            new Mock<IInventoryPostingService>().Object, GetMockDateTimeService().Object);
        SeedData();
    }

    private void SeedData()
    {
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

    private MaterialStagingOrder CreateDraftOrder(decimal requiredQty = 50m) => new()
    {
        StagingId  = "IM2606-00001",
        PlantId    = PlantId,
        SourceType = "Manual",
        Status     = "Draft",
        CreatedBy  = "user1",
        Lines      =
        {
            new MaterialStagingLine
            {
                StagingId   = "IM2606-00001",
                LineNo      = 1,
                ProductCode = ProductCode,
                UomCode     = "EA",
                RequiredQty = requiredQty,
            }
        }
    };

    // ── ConfirmAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_Draft_ShouldCreateReservationAndTransitionStatus()
    {
        var order = CreateDraftOrder(50m);
        _context.MaterialStagingOrders.Add(order);
        await _context.SaveChangesAsync();

        await _svc.ConfirmAsync(order, "user1");

        Assert.Equal("Confirmed", order.Status);
        Assert.Equal("user1", order.ConfirmedBy);
        Assert.NotNull(order.ConfirmedAt);

        var line = order.Lines.First();
        Assert.NotNull(line.ReservationId);

        var avail = await _reservations.GetPlantAvailableQtyAsync(PlantId, ProductCode);
        Assert.Equal(50m, avail);
    }

    [Fact]
    public async Task Confirm_InsufficientStock_ShouldThrowAndNotChangeStatus()
    {
        var order = CreateDraftOrder(200m);  // 超過在庫 100
        _context.MaterialStagingOrders.Add(order);
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ConfirmAsync(order, "user1"));

        // 狀態應保持 Draft（transaction rollback）
        Assert.Equal("Draft", order.Status);
        // 庫存不應有殘留預留
        var avail = await _reservations.GetPlantAvailableQtyAsync(PlantId, ProductCode);
        Assert.Equal(100m, avail);
    }

    [Fact]
    public async Task Confirm_AlreadyConfirmed_ShouldThrow()
    {
        var order = CreateDraftOrder();
        order.Status = "Confirmed";
        _context.MaterialStagingOrders.Add(order);
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ConfirmAsync(order, "user1"));
    }

    [Fact]
    public async Task Confirm_EmptyLines_ShouldThrow()
    {
        var order = new MaterialStagingOrder
        {
            StagingId = "IM2606-00002", PlantId = PlantId,
            SourceType = "Manual", Status = "Draft", CreatedBy = "user1"
        };
        _context.MaterialStagingOrders.Add(order);
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ConfirmAsync(order, "user1"));
    }

    // ── 工單備料單：MRP 預留轉移（mfg-domain.md §21.5）────────────────────

    private MaterialStagingOrder CreateWorkOrderStaging(string workOrderId, decimal requiredQty)
    {
        var order = CreateDraftOrder(requiredQty);
        order.StagingId = "IM2606-00010";
        order.SourceType = "WorkOrder";
        order.SourceId   = workOrderId;
        foreach (var l in order.Lines) l.StagingId = order.StagingId;
        return order;
    }

    private void SeedWorkOrderReservation(string workOrderId, decimal qty)
    {
        _context.InventoryReservations.Add(new InventoryReservation
        {
            PlantId = PlantId, ProductCode = ProductCode, ReservedQty = qty,
            Status = ReservationStatus.Active, SourceType = ReservationSourceType.WorkOrderMaterial,
            SourceDocId = workOrderId, RequiredDate = DateTime.UtcNow
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task Confirm_WorkOrderStaging_OwnMrpReservationCountsAsAvailable()
    {
        // MRP 已為工單鎖住全部在手 100：自由量 0，但備料單就是要備這張工單的料 → 應可確認。
        SeedWorkOrderReservation("WO-9", 100m);
        var order = CreateWorkOrderStaging("WO-9", 100m);
        _context.MaterialStagingOrders.Add(order);
        await _context.SaveChangesAsync();

        await _svc.ConfirmAsync(order, "user1");

        Assert.Equal("Confirmed", order.Status);
    }

    [Fact]
    public async Task Confirm_WorkOrderStaging_TransfersReservation_NoDoubleLock()
    {
        SeedWorkOrderReservation("WO-10", 100m);
        var order = CreateWorkOrderStaging("WO-10", 60m);
        _context.MaterialStagingOrders.Add(order);
        await _context.SaveChangesAsync();

        await _svc.ConfirmAsync(order, "user1");

        // 備料單鎖 60，工單預留同額扣為 40 → 總鎖量仍 100，自由量仍 0（未重複鎖）
        var woReserved = await _reservations.GetReservedBySourceAsync(
            ReservationSourceType.WorkOrderMaterial, "WO-10", ProductCode);
        Assert.Equal(40m, woReserved);
        Assert.Equal(0m, await _reservations.GetPlantAvailableQtyAsync(PlantId, ProductCode));

        // 工單視角：轉移後備料單那 60 仍算「為這張工單保留的量」
        Assert.Equal(100m, await _reservations.GetReservedForWorkOrderAsync("WO-10", ProductCode));
    }

    [Fact]
    public async Task Confirm_ManualStaging_DoesNotTouchWorkOrderReservation()
    {
        // 非工單來源的備料單不得挪用工單預留：自由量只剩 0 → 應擋下。
        SeedWorkOrderReservation("WO-11", 100m);
        var order = CreateDraftOrder(10m);
        _context.MaterialStagingOrders.Add(order);
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ConfirmAsync(order, "user1"));

        Assert.Equal(100m, await _reservations.GetReservedBySourceAsync(
            ReservationSourceType.WorkOrderMaterial, "WO-11", ProductCode));
    }

    // ── VoidAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task Void_Draft_ShouldTransitionToVoid()
    {
        var order = CreateDraftOrder();
        _context.MaterialStagingOrders.Add(order);
        await _context.SaveChangesAsync();

        await _svc.VoidAsync(order);

        Assert.Equal("Void", order.Status);
    }

    [Fact]
    public async Task Void_Confirmed_ShouldReleaseReservationsAndVoid()
    {
        var order = CreateDraftOrder(60m);
        _context.MaterialStagingOrders.Add(order);
        await _context.SaveChangesAsync();

        await _svc.ConfirmAsync(order, "user1");
        var availBefore = await _reservations.GetPlantAvailableQtyAsync(PlantId, ProductCode);
        Assert.Equal(40m, availBefore);

        await _svc.VoidAsync(order);

        Assert.Equal("Void", order.Status);
        var availAfter = await _reservations.GetPlantAvailableQtyAsync(PlantId, ProductCode);
        Assert.Equal(100m, availAfter);
    }

    [Fact]
    public async Task Void_Completed_ShouldThrow()
    {
        var order = CreateDraftOrder();
        order.Status = "Completed";
        _context.MaterialStagingOrders.Add(order);
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.VoidAsync(order));
    }
}
