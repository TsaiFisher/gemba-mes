using SmartFactory.MES.Tests.Shared;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using Moq;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// 備料單揀料移入備料倉（StageAsync）：狀態流轉、PickedQty 回寫、來源儲位守門，
/// 以及「預留不在移庫時釋放」這條紅線（釋放要等到領料過帳）。
/// </summary>
public class MaterialStagingStageTests : TestBase
{
    private readonly InventoryReservationService _reservations;
    private readonly Mock<IInventoryPostingService> _posting = new();
    private readonly Mock<SmartFactory.MES.Services.IDateTimeService> _dt = new();
    private readonly MaterialStagingService _svc;

    private const string PlantId     = "P001";
    private const string ProductCode = "MAT001";
    private const string StagingId   = "IM2607-00001";

    private static readonly DateOnly LocalToday = new(2026, 7, 15);

    public MaterialStagingStageTests()
    {
        _context.CurrentPlantId = PlantId;
        _dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(LocalToday);
        _reservations = new InventoryReservationService(_context);

        // 過帳引擎以 mock 代替（真實庫存異動由 INV 過帳整合測試涵蓋）：
        // 逐行回呼 SetQtyPutAway 模擬實際移轉量，並呼叫 SetDocPosted。
        _posting
            .Setup(p => p.PostSamePlantTransferAsync(
                It.IsAny<PostSamePlantTransferRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PostSamePlantTransferRequest, CancellationToken>((r, _) =>
            {
                foreach (var line in r.Lines) line.SetQtyPutAway(line.StockQty);
                r.SetDocPosted();
            })
            .Returns(Task.CompletedTask);

        _svc = new MaterialStagingService(_context, _reservations, _posting.Object, _dt.Object);
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

    private MaterialStagingOrder NewOrder(string? fromLocation = "A-01", string? stagingLocation = "STG-01") => new()
    {
        StagingId           = StagingId,
        PlantId             = PlantId,
        SourceType          = "Manual",
        Status              = "Draft",
        CreatedBy           = "user1",
        StagingWhCode       = "STG",
        StagingLocationCode = stagingLocation,
        Lines =
        {
            new MaterialStagingLine
            {
                StagingId        = StagingId,
                LineNo           = 1,
                ProductCode      = ProductCode,
                UomCode          = "EA",
                RequiredQty      = 50m,
                FromWarehouseCode = "RAW",
                FromLocationCode = fromLocation,
            }
        }
    };

    [Fact]
    public async Task Stage_ConfirmedOrder_MovesToStagingAndWritesPickedQty()
    {
        var order = NewOrder();
        _context.MaterialStagingOrders.Add(order);
        await _context.SaveChangesAsync();
        await _svc.ConfirmAsync(order, "user1");

        await _svc.StageAsync(order, "picker1");

        Assert.Equal("Completed", order.Status);
        Assert.Equal("picker1", order.StagedBy);
        Assert.NotNull(order.StagedAt);
        Assert.NotNull(order.CompletedAt);
        Assert.Equal(50m, order.Lines.Single().PickedQty);

        _posting.Verify(p => p.PostSamePlantTransferAsync(
            It.Is<PostSamePlantTransferRequest>(r =>
                r.DocNo == StagingId &&
                r.Lines.Single().FromLocationCode == "A-01" &&
                r.Lines.Single().ToLocationCode == "STG-01" &&
                r.Lines.Single().StockQty == 50m),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Stage_DoesNotReleaseReservation_UntilIssue()
    {
        // 紅線：料只是換儲位、仍在廠內未投入，預留必須撐到領料過帳才退場，
        // 否則備妥到領用之間會出現空窗，別的工單可以把備好的料搶走。
        var order = NewOrder();
        _context.MaterialStagingOrders.Add(order);
        await _context.SaveChangesAsync();
        await _svc.ConfirmAsync(order, "user1");

        await _svc.StageAsync(order, "picker1");

        var reserved = await _reservations.GetReservedBySourceAsync(
            ReservationSourceType.MaterialStagingOrder, StagingId, ProductCode);
        Assert.Equal(50m, reserved);
    }

    [Fact]
    public async Task Stage_DraftOrder_Throws()
    {
        var order = NewOrder();
        _context.MaterialStagingOrders.Add(order);
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.StageAsync(order, "picker1"));
    }

    [Fact]
    public async Task Stage_LineWithoutSourceLocation_Throws()
    {
        // 半套移庫會讓帳面料件散在兩處，之後領料要從哪個儲位扣就對不起來
        var order = NewOrder(fromLocation: null);
        _context.MaterialStagingOrders.Add(order);
        await _context.SaveChangesAsync();
        await _svc.ConfirmAsync(order, "user1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.StageAsync(order, "picker1"));
        PzAssert.Message("Inv_StagingLinesNoSourceFmt", ex.Message);

        _posting.Verify(p => p.PostSamePlantTransferAsync(
            It.IsAny<PostSamePlantTransferRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Stage_WithoutStagingLocation_Throws()
    {
        var order = NewOrder(stagingLocation: null);
        _context.MaterialStagingOrders.Add(order);
        await _context.SaveChangesAsync();
        await _svc.ConfirmAsync(order, "user1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.StageAsync(order, "picker1"));
        PzAssert.Message("Inv_StagingNoLocationFmt", ex.Message);
    }
}
