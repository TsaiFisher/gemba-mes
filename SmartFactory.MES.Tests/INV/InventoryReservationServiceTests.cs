using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Models;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// InventoryReservationService 單元測試：廠級可用量、建立/釋放預留。
/// </summary>
public class InventoryReservationServiceTests : TestBase
{
    private readonly InventoryReservationService _svc;
    private const string PlantId     = "P001";
    private const string ProductCode  = "MAT001";
    private static readonly DateTime RequiredDate = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    public InventoryReservationServiceTests()
    {
        _context.CurrentPlantId = PlantId;
        _svc = new InventoryReservationService(_context);
        SeedInventory(100m);
    }

    private void SeedInventory(decimal qty)
    {
        _context.ProductCategories.Add(new ProductCategory { CategoryCode = "RAW", CategoryName = "原料", IsActive = true });
        _context.Products.Add(new Product { ProductCode = ProductCode, CategoryCode = "RAW", IsActive = true });
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = ProductCode,
            LocationCode = "A-01", LotNo = "", QtyOnHand = qty
        });
        _context.SaveChanges();
    }

    // ── GetPlantAvailableQtyAsync ───────────────────────────────────────

    [Fact]
    public async Task GetPlantAvailableQty_NoReservations_ShouldReturnOnHand()
    {
        var qty = await _svc.GetPlantAvailableQtyAsync(PlantId, ProductCode);
        Assert.Equal(100m, qty);
    }

    [Fact]
    public async Task GetPlantAvailableQty_ExcludesQuarantineWarehouseStock()
    {
        // 品質隔離：待驗倉（WarehouseType.Quarantine）庫存不應計入可用量，一般倉不受影響
        _context.Warehouses.Add(new Warehouse { WhCode = "QC01", WhName = "待驗倉", PlantId = PlantId, WarehouseType = WarehouseType.Quarantine });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "QC01", LocationCode = "Q-01", LocationName = "Q-01 位" });
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = ProductCode, LocationCode = "Q-01", LotNo = "", QtyOnHand = 50m
        });
        await _context.SaveChangesAsync();

        var qty = await _svc.GetPlantAvailableQtyAsync(PlantId, ProductCode);
        Assert.Equal(100m, qty);  // 一般倉 100，待驗倉 50 不計入
    }

    [Fact]
    public async Task GetPlantAvailableQty_WithActiveReservation_ShouldDeduct()
    {
        _context.Set<InventoryReservation>().Add(new InventoryReservation
        {
            PlantId = PlantId, ProductCode = ProductCode, ReservedQty = 30m,
            SourceType = ReservationSourceType.MrpRunLine, SourceDocId = "MRP-001",
            RequiredDate = RequiredDate, Status = ReservationStatus.Active
        });
        await _context.SaveChangesAsync();

        var qty = await _svc.GetPlantAvailableQtyAsync(PlantId, ProductCode);
        Assert.Equal(70m, qty);
    }

    [Fact]
    public async Task GetPlantAvailableQty_IgnoresReleasedAndConsumed()
    {
        _context.Set<InventoryReservation>().AddRange(
            new InventoryReservation { PlantId = PlantId, ProductCode = ProductCode, ReservedQty = 20m,
                SourceType = ReservationSourceType.MrpRunLine, SourceDocId = "MRP-001",
                RequiredDate = RequiredDate, Status = ReservationStatus.Released },
            new InventoryReservation { PlantId = PlantId, ProductCode = ProductCode, ReservedQty = 30m,
                SourceType = ReservationSourceType.WorkOrderMaterial, SourceDocId = "WO-001",
                RequiredDate = RequiredDate, Status = ReservationStatus.Consumed }
        );
        await _context.SaveChangesAsync();

        var qty = await _svc.GetPlantAvailableQtyAsync(PlantId, ProductCode);
        Assert.Equal(100m, qty);  // Released/Consumed 不扣
    }

    [Fact]
    public async Task GetPlantAvailableQty_NeverBelowZero()
    {
        _context.Set<InventoryReservation>().Add(new InventoryReservation
        {
            PlantId = PlantId, ProductCode = ProductCode, ReservedQty = 200m,
            SourceType = ReservationSourceType.MrpRunLine, SourceDocId = "MRP-001",
            RequiredDate = RequiredDate, Status = ReservationStatus.Active
        });
        await _context.SaveChangesAsync();

        var qty = await _svc.GetPlantAvailableQtyAsync(PlantId, ProductCode);
        Assert.Equal(0m, qty);
    }

    // ── ReserveAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task Reserve_ShouldPersistReservation_AndDeductAvailableQty()
    {
        var r = await _svc.ReserveAsync(PlantId, ProductCode, 40m,
            ReservationSourceType.MrpRunLine, "MRP-001", 1, RequiredDate);

        Assert.True(r.Id > 0);
        Assert.Equal(ReservationStatus.Active, r.Status);

        var avail = await _svc.GetPlantAvailableQtyAsync(PlantId, ProductCode);
        Assert.Equal(60m, avail);
    }

    [Fact]
    public async Task Reserve_Multiple_ShouldAccumulateDeduction()
    {
        await _svc.ReserveAsync(PlantId, ProductCode, 30m,
            ReservationSourceType.MrpRunLine, "MRP-001", 1, RequiredDate);
        await _svc.ReserveAsync(PlantId, ProductCode, 50m,
            ReservationSourceType.WorkOrderMaterial, "WO-001", null, RequiredDate);

        var avail = await _svc.GetPlantAvailableQtyAsync(PlantId, ProductCode);
        Assert.Equal(20m, avail);
    }

    // ── ReleaseBySourceAsync ────────────────────────────────────────────

    [Fact]
    public async Task ReleaseBySource_ShouldMarkAllActiveReleased()
    {
        await _svc.ReserveAsync(PlantId, ProductCode, 30m,
            ReservationSourceType.MrpRunLine, "MRP-001", 1, RequiredDate);
        await _svc.ReserveAsync(PlantId, ProductCode, 20m,
            ReservationSourceType.MrpRunLine, "MRP-001", 2, RequiredDate);

        var released = await _svc.ReleaseBySourceAsync(ReservationSourceType.MrpRunLine, "MRP-001");

        Assert.Equal(2, released);
        var avail = await _svc.GetPlantAvailableQtyAsync(PlantId, ProductCode);
        Assert.Equal(100m, avail);
    }

    [Fact]
    public async Task ReleaseBySource_ShouldNotAffectOtherSources()
    {
        await _svc.ReserveAsync(PlantId, ProductCode, 30m,
            ReservationSourceType.MrpRunLine, "MRP-001", 1, RequiredDate);
        await _svc.ReserveAsync(PlantId, ProductCode, 20m,
            ReservationSourceType.WorkOrderMaterial, "WO-001", null, RequiredDate);

        await _svc.ReleaseBySourceAsync(ReservationSourceType.MrpRunLine, "MRP-001");

        var avail = await _svc.GetPlantAvailableQtyAsync(PlantId, ProductCode);
        Assert.Equal(80m, avail);  // WO-001 仍保留 20
    }

    // ── ReleaseBySourceLineAsync ────────────────────────────────────────

    [Fact]
    public async Task ReleaseBySourceLine_ShouldReleaseOnlyMatchedLine()
    {
        await _svc.ReserveAsync(PlantId, ProductCode, 30m,
            ReservationSourceType.MrpRunLine, "MRP-001", 1, RequiredDate);
        await _svc.ReserveAsync(PlantId, ProductCode, 20m,
            ReservationSourceType.MrpRunLine, "MRP-001", 2, RequiredDate);

        var released = await _svc.ReleaseBySourceLineAsync(ReservationSourceType.MrpRunLine, "MRP-001", 1);

        Assert.Equal(1, released);
        var avail = await _svc.GetPlantAvailableQtyAsync(PlantId, ProductCode);
        Assert.Equal(80m, avail);  // Line2 的 20 仍保留
    }

    // ── §24.4 供給端在途（Supply peg）────────────────────────────────────

    private void SeedApprovedPo(string poId, decimal orderQty, decimal receivedQty, DateOnly delivery, bool closed = false)
    {
        _context.PurchaseOrders.Add(new PurchaseOrder
        {
            PurchaseOrderId = poId, PlantId = PlantId,
            ApprovalStatus = SmartFactory.MES.Models.DocumentApprovalStatus.Approved
        });
        _context.PurchaseOrderLines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = poId, LineNo = 1, ProductCode = ProductCode,
            OrderQty = orderQty, ReceivedQty = receivedQty, IsClosed = closed, DeliveryDate = delivery,
            StockQty = orderQty, ReceivedStockQty = receivedQty,   // 同單位；在途以庫存單位計
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task GetAvailableInTransit_SumsOpenApprovedPoLinesWithinDate()
    {
        SeedApprovedPo("PO-1", 50m, 10m, new DateOnly(2026, 7, 5));   // 在途 40
        var avail = await _svc.GetAvailableInTransitAsync(PlantId, ProductCode, new DateOnly(2026, 7, 10));
        Assert.Equal(40m, avail);
    }

    /// <summary>
    /// 採購單位與庫存單位不同（2 箱＝24 個）：在途要回庫存單位，才能與 GetPlantAvailableQtyAsync 相加。
    /// 原本取 OrderQty − ReceivedQty（採購單位）會回 1（2026-10-27 修正）。
    /// </summary>
    [Fact]
    public async Task GetAvailableInTransit_UsesStockUnits_WhenPurchaseUomDiffers()
    {
        _context.PurchaseOrders.Add(new PurchaseOrder
        {
            PurchaseOrderId = "PO-BOX", PlantId = PlantId,
            ApprovalStatus = SmartFactory.MES.Models.DocumentApprovalStatus.Approved
        });
        _context.PurchaseOrderLines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO-BOX", LineNo = 1, ProductCode = ProductCode, DeliveryDate = new DateOnly(2026, 7, 5),
            OrderQty = 2m, UomCode = "BOX", ReceivedQty = 1m,
            StockQty = 24m, StockUomCode = "PC", ReceivedStockQty = 12m,
        });
        _context.SaveChanges();

        var avail = await _svc.GetAvailableInTransitAsync(PlantId, ProductCode, new DateOnly(2026, 7, 10));

        Assert.Equal(12m, avail);
    }

    /// <summary>批次版必須與單筆版逐筆相等（缺貨分析、庫存餘額用批次，投產料閘用單筆）。</summary>
    [Fact]
    public async Task GetPlantAvailability_Batch_MatchesSingle()
    {
        // 待驗倉、有效預留、已釋放預留、超額預留（下限 0）都放進來
        _context.Warehouses.Add(new Warehouse { WhCode = "QC01", WhName = "待驗倉", PlantId = PlantId, WarehouseType = WarehouseType.Quarantine });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "QC01", LocationCode = "Q-01", LocationName = "Q-01 位" });
        _context.Inventories.Add(new Inventory { PlantId = PlantId, ProductCode = ProductCode, LocationCode = "Q-01", LotNo = "", QtyOnHand = 50m });
        _context.Inventories.Add(new Inventory { PlantId = PlantId, ProductCode = "OVER", LocationCode = "Q-01", LotNo = "", QtyOnHand = 5m });
        _context.Set<InventoryReservation>().AddRange(
            new InventoryReservation { PlantId = PlantId, ProductCode = ProductCode, ReservedQty = 30m, SourceDocId = "A", RequiredDate = RequiredDate, Status = ReservationStatus.Active },
            new InventoryReservation { PlantId = PlantId, ProductCode = ProductCode, ReservedQty = 99m, SourceDocId = "B", RequiredDate = RequiredDate, Status = ReservationStatus.Released },
            new InventoryReservation { PlantId = PlantId, ProductCode = "OVER", ReservedQty = 7m, SourceDocId = "C", RequiredDate = RequiredDate, Status = ReservationStatus.Active });
        await _context.SaveChangesAsync();

        var codes = await _context.Inventories.Select(i => i.ProductCode).Distinct().ToListAsync();
        codes.Add("NO-STOCK");
        var batch = await _svc.GetPlantAvailabilityAsync(PlantId, codes);
        foreach (var c in codes)
            Assert.Equal(await _svc.GetPlantAvailableQtyAsync(PlantId, c), batch[c].Available);
        Assert.Equal((100m, 30m, 70m), (batch[ProductCode].OnHand, batch[ProductCode].Reserved, batch[ProductCode].Available));
        Assert.Equal(0m, batch["OVER"].Available);   // 待驗不計、預留 7 → 下限 0
        Assert.Equal(0m, batch["NO-STOCK"].OnHand);
    }

    [Fact]
    public async Task GetAvailableInTransit_ExcludesLinesAfterByDate()
    {
        SeedApprovedPo("PO-1", 50m, 0m, new DateOnly(2026, 7, 20));   // 到貨晚於 byDate
        var avail = await _svc.GetAvailableInTransitAsync(PlantId, ProductCode, new DateOnly(2026, 7, 10));
        Assert.Equal(0m, avail);
    }

    [Fact]
    public async Task GetAvailableInTransit_DeductsExistingSupplyPeg()
    {
        SeedApprovedPo("PO-1", 50m, 0m, new DateOnly(2026, 7, 5));    // 在途 50
        await _svc.PegSupplyAsync(PlantId, ProductCode, 30m,
            ReservationSourceType.WorkOrderMaterial, "WO-1", null, RequiredDate,
            "PurchaseOrder", "PO-1", 1, new DateOnly(2026, 7, 5));

        var avail = await _svc.GetAvailableInTransitAsync(PlantId, ProductCode, new DateOnly(2026, 7, 10));
        Assert.Equal(20m, avail);   // 50 − 已 peg 30
    }

    [Fact]
    public async Task PegSupply_DoesNotAffectOnHandAvailableQty()
    {
        // Supply peg 不影響在手可用量（它對應尚未入庫的量）
        await _svc.PegSupplyAsync(PlantId, ProductCode, 30m,
            ReservationSourceType.WorkOrderMaterial, "WO-1", null, RequiredDate,
            "PurchaseOrder", "PO-1", 1, new DateOnly(2026, 7, 5));

        var onHandAvail = await _svc.GetPlantAvailableQtyAsync(PlantId, ProductCode);
        Assert.Equal(100m, onHandAvail);
    }

    [Fact]
    public async Task ConvertSupplyOnArrival_TurnsSupplyPegIntoOnHand()
    {
        SeedApprovedPo("PO-1", 50m, 0m, new DateOnly(2026, 7, 5));
        await _svc.PegSupplyAsync(PlantId, ProductCode, 30m,
            ReservationSourceType.WorkOrderMaterial, "WO-1", null, RequiredDate,
            "PurchaseOrder", "PO-1", 1, new DateOnly(2026, 7, 5));

        var converted = await _svc.ConvertSupplyOnArrivalAsync("PurchaseOrder", "PO-1", 1);

        Assert.Equal(1, converted);
        // 轉為 OnHand 後：不再計入可用在途，且以 OnHand 預留扣減在手可用量
        var inTransit = await _svc.GetAvailableInTransitAsync(PlantId, ProductCode, new DateOnly(2026, 7, 10));
        Assert.Equal(50m, inTransit);   // peg 已轉走，在途回到 50
        var onHandAvail = await _svc.GetPlantAvailableQtyAsync(PlantId, ProductCode);
        Assert.Equal(70m, onHandAvail); // 100 − 轉為 OnHand 的 30
    }

    [Fact]
    public async Task RevertSupplyOnUnreceive_RestoresOnHandPegBackToSupply()
    {
        SeedApprovedPo("PO-1", 50m, 0m, new DateOnly(2026, 7, 5));
        await _svc.PegSupplyAsync(PlantId, ProductCode, 30m,
            ReservationSourceType.WorkOrderMaterial, "WO-1", null, RequiredDate,
            "PurchaseOrder", "PO-1", 1, new DateOnly(2026, 7, 5));
        await _svc.ConvertSupplyOnArrivalAsync("PurchaseOrder", "PO-1", 1);   // 到貨：Supply→OnHand

        var reverted = await _svc.RevertSupplyOnUnreceiveAsync("PurchaseOrder", "PO-1", 1);

        Assert.Equal(1, reverted);
        var peg = _context.Set<InventoryReservation>()
            .Single(r => r.SupplyDocId == "PO-1" && r.ProductCode == ProductCode);
        Assert.Equal(ReservationCategory.Supply, peg.Category);   // 還原為在途
        // 在手可用量回到 100（OnHand 預留已還原為 Supply 不扣在手）
        Assert.Equal(100m, await _svc.GetPlantAvailableQtyAsync(PlantId, ProductCode));
    }

    [Fact]
    public async Task RevertSupplyOnUnreceive_DoesNotTouchNativeOnHandReservation()
    {
        // 一般 OnHand 預留（SupplyDocType=null）不應被回沖誤動
        await _svc.ReserveAsync(PlantId, ProductCode, 40m,
            ReservationSourceType.MrpRunLine, "MRP-001", 1, RequiredDate);

        var reverted = await _svc.RevertSupplyOnUnreceiveAsync("PurchaseOrder", "PO-1", 1);

        Assert.Equal(0, reverted);
        Assert.Equal(60m, await _svc.GetPlantAvailableQtyAsync(PlantId, ProductCode));  // 仍扣 40
    }

    // ── §24.4 委外在途（Outsource supply）────────────────────────────────

    private void SeedConfirmedOpo(string opoId, decimal qty, DateOnly expDelivery)
    {
        _context.OutsourcePurchaseOrders.Add(new OutsourcePurchaseOrder
        {
            OutsourcePurchaseOrderId = opoId, PlantId = PlantId,
            Status = OutsourcePoStatus.Confirmed, ExpectedDeliveryDate = expDelivery
        });
        _context.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
        {
            OutsourcePurchaseOrderId = opoId, LineNo = 1, ProductCode = ProductCode, Qty = qty
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task GetAvailableOutsourceInTransit_SumsOpenConfirmedOpoWithinDate()
    {
        SeedConfirmedOpo("OPO-1", 40m, new DateOnly(2026, 7, 5));
        var avail = await _svc.GetAvailableOutsourceInTransitAsync(PlantId, ProductCode, new DateOnly(2026, 7, 10));
        Assert.Equal(40m, avail);
    }

    [Fact]
    public async Task GetAvailableOutsourceInTransit_ExcludesDraftAndLateDelivery()
    {
        SeedConfirmedOpo("OPO-1", 40m, new DateOnly(2026, 7, 20));   // 應交日晚於 byDate
        _context.OutsourcePurchaseOrders.Add(new OutsourcePurchaseOrder
        {
            OutsourcePurchaseOrderId = "OPO-2", PlantId = PlantId,
            Status = OutsourcePoStatus.Draft, ExpectedDeliveryDate = new DateOnly(2026, 7, 1)
        });
        _context.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
        {
            OutsourcePurchaseOrderId = "OPO-2", LineNo = 1, ProductCode = ProductCode, Qty = 30m
        });
        _context.SaveChanges();

        var avail = await _svc.GetAvailableOutsourceInTransitAsync(PlantId, ProductCode, new DateOnly(2026, 7, 10));
        Assert.Equal(0m, avail);
    }

    [Fact]
    public async Task GetAvailableOutsourceInTransit_DeductsPostedReceiptAndPeg()
    {
        SeedConfirmedOpo("OPO-1", 50m, new DateOnly(2026, 7, 5));
        // 已收 20（Posted 委外進貨）
        _context.OutsourceReceipts.Add(new OutsourceReceipt
        {
            OutsourceReceiptId = "OR-1", OutsourcePurchaseOrderId = "OPO-1",
            PlantId = PlantId, Status = SmartFactory.MES.Modules.INV.Models.DocStatus.Posted
        });
        _context.OutsourceReceiptLines.Add(new OutsourceReceiptLine
        {
            OutsourceReceiptId = "OR-1", LineNo = 1, OpoLineNo = 1,
            ProductCode = ProductCode, ReceiptQty = 20m
        });
        _context.SaveChanges();
        // 已 peg 委外在途 10
        await _svc.PegSupplyAsync(PlantId, ProductCode, 10m,
            ReservationSourceType.WorkOrderMaterial, "WO-1", null, RequiredDate,
            "OutsourcePurchaseOrder", "OPO-1", 1, new DateOnly(2026, 7, 5));

        var avail = await _svc.GetAvailableOutsourceInTransitAsync(PlantId, ProductCode, new DateOnly(2026, 7, 10));
        Assert.Equal(20m, avail);   // 50 − 已收 20 − 已 peg 10
    }

    // ── §24.2a 出貨消耗／作廢回鎖訂單列預留 ─────────────────────────────

    private void SeedSoDemand(decimal orderQty, decimal shipped = 0m, bool closed = false)
    {
        _context.SalesOrderLines.Add(new SmartFactory.MES.Modules.ORD.Models.SalesOrderLine
        {
            SalesOrderId = "SO-1", LineNo = 1, ProductCode = ProductCode,
            OrderQty = orderQty, StockQty = orderQty, ShippedStockQty = shipped, IsClosed = closed,
        });
        _context.MrpRunLines.Add(new MrpRunLine
        {
            MrpRunId = "MRP-1", LineSeq = 1, SalesOrderId = "SO-1", SalesOrderLineSeq = 1,
            ProductCode = ProductCode, RequiredQty = orderQty, RequiredDate = RequiredDate,
        });
        _context.MrpRunLines.Add(new MrpRunLine     // 同一次 MRP、另一張訂單的需求
        {
            MrpRunId = "MRP-1", LineSeq = 2, SalesOrderId = "SO-2", SalesOrderLineSeq = 1,
            ProductCode = ProductCode, RequiredQty = 5m, RequiredDate = RequiredDate,
        });
        _context.SaveChanges();
    }

    private decimal ActiveFor(int lineSeq) => _context.InventoryReservations
        .Where(r => r.SourceType == ReservationSourceType.MrpRunLine && r.SourceDocId == "MRP-1"
                 && r.SourceLineSeq == lineSeq && r.Status == ReservationStatus.Active)
        .Sum(r => r.ReservedQty);

    [Fact]
    public async Task ConsumeForSalesOrderLine_ReducesOnlyThatLinesDemandReservation()
    {
        SeedSoDemand(10m);
        await _svc.ReserveAsync(PlantId, ProductCode, 10m, ReservationSourceType.MrpRunLine, "MRP-1", 1, RequiredDate);
        await _svc.ReserveAsync(PlantId, ProductCode, 5m, ReservationSourceType.MrpRunLine, "MRP-1", 2, RequiredDate);

        var consumed = await _svc.ConsumeForSalesOrderLineAsync("SO-1", 1, ProductCode, 6m);

        Assert.Equal(6m, consumed);
        Assert.Equal(4m, ActiveFor(1));
        Assert.Equal(5m, ActiveFor(2));   // 別張訂單的鎖量不能動
        // 鎖量跟著出貨少 6（在手由過帳扣）：同一批貨不會對廠級可用量扣兩次
        Assert.Equal(100m - 9m, await _svc.GetPlantAvailableQtyAsync(PlantId, ProductCode));
    }

    [Fact]
    public async Task ConsumeForSalesOrderLine_MoreThanLocked_FullyConsumed_ReturnsLockedQty()
    {
        SeedSoDemand(10m);
        await _svc.ReserveAsync(PlantId, ProductCode, 3m, ReservationSourceType.MrpRunLine, "MRP-1", 1, RequiredDate);

        Assert.Equal(3m, await _svc.ConsumeForSalesOrderLineAsync("SO-1", 1, ProductCode, 8m));
        Assert.Equal(0m, ActiveFor(1));
        Assert.Contains(_context.InventoryReservations, r => r.SourceLineSeq == 1 && r.Status == ReservationStatus.Consumed);
    }

    [Fact]
    public async Task ConsumeForSalesOrderLine_NoMrpDemand_NoOp()
    {
        Assert.Equal(0m, await _svc.ConsumeForSalesOrderLineAsync("SO-X", 1, ProductCode, 5m));
    }

    [Fact]
    public async Task RestoreForSalesOrderLine_CappedByOpenQtyMinusAlreadyLocked()
    {
        // 訂購 10、作廢回沖後已出 2、仍鎖 5 → 最多再鎖 10 − 2 − 5 = 3
        SeedSoDemand(10m, shipped: 2m);
        await _svc.ReserveAsync(PlantId, ProductCode, 5m, ReservationSourceType.MrpRunLine, "MRP-1", 1, RequiredDate);

        var restored = await _svc.RestoreForSalesOrderLineAsync(PlantId, "SO-1", 1, ProductCode, 8m);

        Assert.Equal(3m, restored);
        Assert.Equal(8m, ActiveFor(1));
    }

    [Fact]
    public async Task RestoreForSalesOrderLine_ClosedLine_DoesNotLock()
    {
        SeedSoDemand(10m, closed: true);

        Assert.Equal(0m, await _svc.RestoreForSalesOrderLineAsync(PlantId, "SO-1", 1, ProductCode, 4m));
        Assert.Equal(0m, ActiveFor(1));
    }

    // ── §24.2a 出貨不得拿走保留給其他需求的成品（2026-10-14 驗收補） ────────────

    private static (string, string?, int?, decimal) Ship(string? so, int? line, decimal qty) => (ProductCode, so, line, qty);

    [Fact]
    public async Task EnsureShipment_NoReservations_KeepsLocationOnlyBehaviour()
    {
        // 沒有任何預留：不另外擋（儲位庫存不足由過帳本身處理）
        await _svc.EnsureShipmentWithinFreeStockAsync(PlantId, new[] { Ship(null, null, 150m) });
    }

    [Fact]
    public async Task EnsureShipment_OtherOrdersReserved_BlocksBeyondFreeStock_MessageNamesQuantities()
    {
        SeedSoDemand(10m);
        await _svc.ReserveAsync(PlantId, ProductCode, 30m, ReservationSourceType.MrpRunLine, "MRP-1", 2, RequiredDate);   // SO-2

        await _svc.EnsureShipmentWithinFreeStockAsync(PlantId, new[] { Ship("SO-9", 1, 70m) });   // 100 − 30 剛好
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.EnsureShipmentWithinFreeStockAsync(PlantId, new[] { Ship("SO-9", 1, 71m) }));
        Assert.Contains(ProductCode, ex.Message);
        Assert.Contains("71", ex.Message);
        Assert.Contains("70", ex.Message);
        Assert.Contains("30", ex.Message);
    }

    [Fact]
    public async Task EnsureShipment_OwnOrderLinesReservation_CanBeShipped()
    {
        SeedSoDemand(40m);
        await _svc.ReserveAsync(PlantId, ProductCode, 30m, ReservationSourceType.MrpRunLine, "MRP-1", 1, RequiredDate);   // SO-1 自己的
        await _svc.ReserveAsync(PlantId, ProductCode, 20m, ReservationSourceType.MrpRunLine, "MRP-1", 2, RequiredDate);   // SO-2

        await _svc.EnsureShipmentWithinFreeStockAsync(PlantId, new[] { Ship("SO-1", 1, 80m) });   // 100 − 別人的 20
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.EnsureShipmentWithinFreeStockAsync(PlantId, new[] { Ship("SO-1", 1, 81m) }));
    }

    [Fact]
    public async Task EnsureShipment_LinesOfSameProductAreSummed()
    {
        SeedSoDemand(10m);
        await _svc.ReserveAsync(PlantId, ProductCode, 30m, ReservationSourceType.MrpRunLine, "MRP-1", 2, RequiredDate);

        // 同一張出貨單兩行同品號、各 40：合計 80 > 可出 70
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.EnsureShipmentWithinFreeStockAsync(PlantId, new[] { Ship("SO-9", 1, 40m), Ship("SO-9", 2, 40m) }));
    }
}
