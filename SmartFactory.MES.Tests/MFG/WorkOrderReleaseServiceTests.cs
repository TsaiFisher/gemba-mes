using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// WorkOrderReleaseService（mfg-domain.md §24）：net-at-release 重算、硬料閘、在途 peg。
/// </summary>
public class WorkOrderReleaseServiceTests : TestBase
{
    private const string PlantId = "P001";
    private const string Fg = "FG-1";
    private const string Mat = "MAT-1";
    private static readonly DateTime PlannedStart = new(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IApprovalService> _approval = new();
    private readonly Mock<IPlantParamService> _sysParam = new();
    private readonly Mock<IDocNumberService> _docNo = new();
    private readonly Mock<IUserContext> _user = new();
    private readonly InventoryReservationService _reservations;

    public WorkOrderReleaseServiceTests()
    {
        _context.CurrentPlantId = PlantId;
        _user.Setup(u => u.PlantId).Returns(PlantId);
        _user.Setup(u => u.UserId).Returns("usr1");
        _approval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string?>()))
                 .ReturnsAsync((DocumentApprovalConfig?)null);   // 直接核准
        _sysParam.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _docNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync("MI2026000001");
        _reservations = new InventoryReservationService(_context);
    }

    private WorkOrderReleaseService MakeService() =>
        new(_context, _approval.Object, _sysParam.Object, _docNo.Object, _reservations, _user.Object,
            Timezone.TimezoneTestKit.FixedLocalDate(new DateOnly(2026, 7, 1)).Object);

    private void SeedInventory(string product, decimal qty)
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = product, LocationCode = "A-01", LotNo = "", QtyOnHand = qty
        });
        _context.SaveChanges();
    }

    private WorkOrder SeedWo(decimal gross, decimal plannedQty, decimal matRequired,
        MaterialIssueMethod issue = MaterialIssueMethod.Push, decimal matUnitCost = 0m)
    {
        var wo = new WorkOrder
        {
            WorkOrderId = "WO-1", ProductionBatchId = "B-1", ProductCode = Fg, RoutingId = "",
            Quantity = plannedQty, GrossDemandQty = gross,
            SourceMrpRunId = "MRP-1", SourceMrpRunLineSeq = 1,
            Status = WorkOrderStatus.Planned, PlantId = PlantId,
            PlannedStartDate = PlannedStart, PlannedEndDate = PlannedStart.AddDays(5)
        };
        var op = new WorkOrderOperation
        {
            WorkOrderId = "WO-1", OperationSeq = 10, OperationName = "OP10", WorkCenterId = "WC1"
        };
        op.Materials.Add(new WorkOrderMaterial
        {
            WorkOrderId = "WO-1", OperationSeq = 10, BomSeq = 1, ProductCode = Mat,
            RequiredQty = matRequired, UomCode = "PC", IssueMethod = issue, UnitCost = matUnitCost
        });
        wo.Operations.Add(op);
        _context.WorkOrders.Add(wo);
        _context.WorkOrderOperations.Add(op);
        _context.SaveChanges();
        return wo;
    }

    private void SeedApprovedPo(string poId, decimal orderQty, DateOnly delivery)
    {
        _context.PurchaseOrders.Add(new PurchaseOrder
        {
            PurchaseOrderId = poId, PlantId = PlantId,
            ApprovalStatus = DocumentApprovalStatus.Approved
        });
        _context.PurchaseOrderLines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = poId, LineNo = 1, ProductCode = Mat,
            OrderQty = orderQty, ReceivedQty = 0m, IsClosed = false, DeliveryDate = delivery,
            StockQty = orderQty,   // 同單位；在途以庫存單位計
        });
        _context.SaveChanges();
    }

    private void SeedConfirmedOpo(string opoId, decimal qty, DateOnly expDelivery)
    {
        _context.OutsourcePurchaseOrders.Add(new OutsourcePurchaseOrder
        {
            OutsourcePurchaseOrderId = opoId, PlantId = PlantId,
            Status = OutsourcePoStatus.Confirmed, ExpectedDeliveryDate = expDelivery
        });
        _context.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
        {
            OutsourcePurchaseOrderId = opoId, LineNo = 1, ProductCode = Mat, Qty = qty
        });
        _context.SaveChanges();
    }

    // ── net-at-release 重算（§24.2）──────────────────────────────────────

    [Fact]
    public async Task Preview_RecalcsNetAtRelease_ReturnsNeedsConfirm()
    {
        SeedInventory(Fg, 20m);     // 成品可用 20
        SeedInventory(Mat, 999m);   // 料件充足（不擋）
        SeedWo(gross: 100m, plannedQty: 70m, matRequired: 5m);

        var preview = await MakeService().PreviewAsync("WO-1");

        Assert.Equal(WorkOrderReleaseOutcome.NeedsConfirm, preview.Outcome);
        Assert.Equal(70m, preview.PlannedQty);
        Assert.Equal(80m, preview.RecalculatedQty);   // 100 − 20
    }

    // ── 重算為 0：不需生產，不得投產（2026-10-13）─────────────────────────────

    [Fact]
    public async Task Preview_FinishedStockCoversGross_NotNeeded()
    {
        SeedInventory(Fg, 200m);    // 成品可用 200 ≥ 毛需求 10 → 重算 0
        SeedInventory(Mat, 999m);
        SeedWo(gross: 10m, plannedQty: 10m, matRequired: 20m);

        var preview = await MakeService().PreviewAsync("WO-1");

        Assert.Equal(WorkOrderReleaseOutcome.NotNeeded, preview.Outcome);
        Assert.Equal((10m, 0m), (preview.PlannedQty, preview.RecalculatedQty));
    }

    [Fact]
    public async Task Apply_RecalcToZero_EvenConfirmed_DoesNotRelease_NothingRescaled_NoSlip()
    {
        // 原本人工確認後照樣投產：工單量、用料縮成 0，Push 發料單每一行都是 0，派工報工上限 0 → 工單永遠卡住
        SeedInventory(Fg, 200m);
        SeedInventory(Mat, 999m);
        SeedWo(gross: 10m, plannedQty: 10m, matRequired: 20m);

        var r = await MakeService().ApplyAsync("WO-1", confirmed: true);

        Assert.Equal(WorkOrderReleaseOutcome.NotNeeded, r.Outcome);
        var wo = _context.WorkOrders.Include(w => w.Operations).ThenInclude(o => o.Materials).Single(w => w.WorkOrderId == "WO-1");
        Assert.Equal(WorkOrderStatus.Planned, wo.Status);
        Assert.Equal(10m, wo.Quantity);
        Assert.Equal(20m, wo.Operations.Single().Materials.Single().RequiredQty);
        Assert.Empty(_context.MaterialSlips.Where(s => s.SourceDocId == "WO-1"));
    }

    // ── §24.2a 拿來抵的庫存留給原需求 ──────────────────────────────────────

    private decimal LockedForSourceDemand() => _context.InventoryReservations
        .Where(r => r.SourceType == ReservationSourceType.MrpRunLine && r.SourceDocId == "MRP-1"
                 && r.SourceLineSeq == 1 && r.Status == ReservationStatus.Active && r.ProductCode == Fg)
        .Sum(r => r.ReservedQty);

    [Fact]
    public async Task Apply_RecalcToZero_LocksCoveringStockToSourceDemand_Idempotent()
    {
        // 重算為 0 不投產：抵掉需求的那 10 個成品要鎖給原需求，否則隨時被別張出貨用掉、原訂單落空
        SeedInventory(Fg, 200m);
        SeedInventory(Mat, 999m);
        SeedWo(gross: 10m, plannedQty: 10m, matRequired: 20m);

        await MakeService().ApplyAsync("WO-1", confirmed: true);
        Assert.Equal(10m, LockedForSourceDemand());

        // 再按一次投產：已經鎖夠了，不得疊加
        var again = await MakeService().ApplyAsync("WO-1", confirmed: true);
        Assert.Equal(WorkOrderReleaseOutcome.NotNeeded, again.Outcome);
        Assert.Equal(10m, LockedForSourceDemand());
    }

    [Fact]
    public async Task Apply_PartialStock_LocksNettedStock_MakesTheRest()
    {
        SeedInventory(Fg, 20m);     // 抵 20、做 80
        SeedInventory(Mat, 999m);
        SeedWo(gross: 100m, plannedQty: 70m, matRequired: 5m);

        var r = await MakeService().ApplyAsync("WO-1", confirmed: true);

        Assert.Equal(WorkOrderReleaseOutcome.Released, r.Outcome);
        Assert.Equal(80m, _context.WorkOrders.Find("WO-1")!.Quantity);
        Assert.Equal(20m, LockedForSourceDemand());
    }

    [Fact]
    public async Task Apply_StockAlreadyLockedForDemand_CountsAsOwn_NoExtraLock()
    {
        // MRP 展開時已把 15 個在手軟預留給這筆需求：可用＝自由 5 + 本單 15，只需再鎖 min(5, 100−15)=5
        SeedInventory(Fg, 20m);
        SeedInventory(Mat, 999m);
        SeedWo(gross: 100m, plannedQty: 70m, matRequired: 5m);
        await _reservations.ReserveAsync(PlantId, Fg, 15m, ReservationSourceType.MrpRunLine, "MRP-1", 1, PlannedStart);

        await MakeService().ApplyAsync("WO-1", confirmed: true);

        Assert.Equal(80m, _context.WorkOrders.Find("WO-1")!.Quantity);
        Assert.Equal(20m, LockedForSourceDemand());
    }

    [Fact]
    public async Task ReleaseBatch_ParentNotNeeded_SkipsParentAndItsChild_ReleasesTheRest()
    {
        SeedBatch();
        SeedInventory(Fg, 200m);     // 成品 FG-1 可用 200
        SeedInventory(Mat, 999m);
        SeedWo(gross: 10m, plannedQty: 10m, matRequired: 5m);           // WO-1：成品工單，重算 0
        void Wo(string id, string product, string? parent)
        {
            var wo = new WorkOrder
            {
                WorkOrderId = id, ProductionBatchId = "B-1", ProductCode = product, RoutingId = "",
                Quantity = 5m, GrossDemandQty = 0m, ParentWorkOrderId = parent,
                Status = WorkOrderStatus.Planned, PlantId = PlantId,
                PlannedStartDate = PlannedStart, PlannedEndDate = PlannedStart.AddDays(5),
            };
            var op = new WorkOrderOperation { WorkOrderId = id, OperationSeq = 10, OperationName = "OP10", WorkCenterId = "WC1" };
            op.Materials.Add(new WorkOrderMaterial { WorkOrderId = id, OperationSeq = 10, BomSeq = 1, ProductCode = Mat, RequiredQty = 1m, UomCode = "PC" });
            wo.Operations.Add(op);
            _context.WorkOrders.Add(wo);
            _context.WorkOrderOperations.Add(op);
        }
        Wo("WO-CHILD", "SEMI-1", parent: "WO-1");   // WO-1 的半成品子工單：父工單不生產，它也不該生產
        Wo("WO-OTHER", "OTHER-1", parent: null);    // 不相干的工單照常投產
        _context.SaveChanges();

        var result = await MakeService().ReleaseBatchAsync("B-1", confirmed: false);

        Assert.Equal(BatchReleaseOutcome.Released, result.Outcome);
        Assert.Equal(1, result.Released);
        Assert.Equal(new[] { "WO-CHILD", "WO-1" }.OrderBy(x => x), result.Skipped.OrderBy(x => x));
        Assert.Equal(WorkOrderStatus.Planned, _context.WorkOrders.Find("WO-1")!.Status);
        Assert.Equal(WorkOrderStatus.Planned, _context.WorkOrders.Find("WO-CHILD")!.Status);
        Assert.Equal(WorkOrderStatus.Released, _context.WorkOrders.Find("WO-OTHER")!.Status);
        Assert.Equal(10m, LockedForSourceDemand());   // 跳過的成品工單：抵掉的庫存同樣鎖給原需求
    }

    [Fact]
    public async Task Apply_Confirmed_AppliesRecalculatedQuantity()
    {
        SeedInventory(Fg, 50m);     // 成品可用 50 → 重算 50
        SeedInventory(Mat, 999m);
        SeedWo(gross: 100m, plannedQty: 70m, matRequired: 5m);

        var result = await MakeService().ApplyAsync("WO-1", confirmed: true);

        Assert.Equal(WorkOrderReleaseOutcome.Released, result.Outcome);
        var wo = _context.WorkOrders.Find("WO-1")!;
        Assert.Equal(WorkOrderStatus.Released, wo.Status);
        Assert.Equal(50m, wo.Quantity);
    }

    [Fact]
    public async Task Apply_RecalcChanged_WithoutConfirm_DoesNotApply()
    {
        SeedInventory(Fg, 20m);
        SeedInventory(Mat, 999m);
        SeedWo(gross: 100m, plannedQty: 70m, matRequired: 5m);

        var result = await MakeService().ApplyAsync("WO-1", confirmed: false);

        Assert.Equal(WorkOrderReleaseOutcome.NeedsConfirm, result.Outcome);
        var wo = _context.WorkOrders.Find("WO-1")!;
        Assert.Equal(WorkOrderStatus.Planned, wo.Status);   // 未套用
        Assert.Equal(70m, wo.Quantity);
    }

    // ── 硬料閘（§24.3）──────────────────────────────────────────────────

    [Fact]
    public async Task Apply_MaterialShortageNoInTransit_ReturnsBlocked()
    {
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 10m);   // gross=0 → 不重算
        // 無料、無在途 → 擋住

        var result = await MakeService().ApplyAsync("WO-1", confirmed: true);

        Assert.Equal(WorkOrderReleaseOutcome.MaterialBlocked, result.Outcome);
        Assert.Contains(Mat, result.ShortageProducts);
        Assert.Equal(WorkOrderStatus.Planned, _context.WorkOrders.Find("WO-1")!.Status);
    }

    [Fact]
    public async Task Apply_OwnWorkOrderMaterialReservation_NotBlocked()
    {
        // MRP 展開時已為本工單鎖住這批料：自身預留必須加回，否則工單被自己的預留擋在料閘外。
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 10m);
        SeedInventory(Mat, 10m);
        _context.InventoryReservations.Add(new InventoryReservation
        {
            PlantId = PlantId, ProductCode = Mat, ReservedQty = 10m,
            Status = ReservationStatus.Active, SourceType = ReservationSourceType.WorkOrderMaterial,
            SourceDocId = "WO-1", RequiredDate = PlannedStart
        });
        _context.SaveChanges();

        var result = await MakeService().ApplyAsync("WO-1", confirmed: true);

        Assert.NotEqual(WorkOrderReleaseOutcome.MaterialBlocked, result.Outcome);
    }

    [Fact]
    public async Task Apply_OtherWorkOrderReservation_StillBlocked()
    {
        // 只加回自身預留：料被別張工單鎖走時仍須擋單。
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 10m);
        SeedInventory(Mat, 10m);
        _context.InventoryReservations.Add(new InventoryReservation
        {
            PlantId = PlantId, ProductCode = Mat, ReservedQty = 10m,
            Status = ReservationStatus.Active, SourceType = ReservationSourceType.WorkOrderMaterial,
            SourceDocId = "WO-OTHER", RequiredDate = PlannedStart
        });
        _context.SaveChanges();

        var result = await MakeService().ApplyAsync("WO-1", confirmed: true);

        Assert.Equal(WorkOrderReleaseOutcome.MaterialBlocked, result.Outcome);
    }

    [Fact]
    public async Task Apply_InTransitCovers_ReleasesAndPegsSupply()
    {
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 10m);
        SeedApprovedPo("PO-1", 10m, DateOnly.FromDateTime(PlannedStart.AddDays(-1)));   // 在途足量、早於庫出日

        var result = await MakeService().ApplyAsync("WO-1", confirmed: true);

        Assert.Equal(WorkOrderReleaseOutcome.Released, result.Outcome);
        Assert.True(result.MaterialRisk);
        // 建立 Category=Supply 的 peg
        var peg = _context.Set<InventoryReservation>()
            .SingleOrDefault(r => r.Category == ReservationCategory.Supply && r.ProductCode == Mat);
        Assert.NotNull(peg);
        Assert.Equal(10m, peg!.ReservedQty);
    }

    [Fact]
    public async Task Apply_OutsourceInTransitCovers_ReleasesAndPegsOutsourceSupply()
    {
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 10m);
        // 無料、無採購在途，但有委外在途足量、應交日早於庫出日
        SeedConfirmedOpo("OPO-1", 10m, DateOnly.FromDateTime(PlannedStart.AddDays(-1)));

        var result = await MakeService().ApplyAsync("WO-1", confirmed: true);

        Assert.Equal(WorkOrderReleaseOutcome.Released, result.Outcome);
        Assert.True(result.MaterialRisk);
        var peg = _context.Set<InventoryReservation>()
            .SingleOrDefault(r => r.Category == ReservationCategory.Supply
                               && r.SupplyDocType == "OutsourcePurchaseOrder"
                               && r.ProductCode == Mat);
        Assert.NotNull(peg);
        Assert.Equal("OPO-1", peg!.SupplyDocId);
        Assert.Equal(10m, peg.ReservedQty);
    }

    [Fact]
    public async Task Apply_BackflushMaterial_NotBlockedByHardGate()
    {
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 10m, issue: MaterialIssueMethod.Backflush);
        // 無料但 Backflush → 不納硬閘 → 放行

        var result = await MakeService().ApplyAsync("WO-1", confirmed: true);

        Assert.Equal(WorkOrderReleaseOutcome.Released, result.Outcome);
    }

    // ── 簡化模式自動投產（§25.3 AutoReleaseAsync）──────────────────────────
    //
    // 這組測試的重點是「跳過了什麼、保留了什麼」：跳過的部分若沒跳（例如硬料閘仍擋單）
    // 簡化模式等於沒開；保留的部分若沒保留（成本快照/領料單/委外 PO）會**靜默**產生錯帳。

    [Fact]
    public async Task AutoRelease_MaterialShortage_StillReleases_ButFlagsShortage()
    {
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 10m);   // 無料、無在途：完整模式會 MaterialBlocked

        var result = await MakeService().AutoReleaseAsync("WO-1");

        Assert.Equal(WorkOrderReleaseOutcome.Released, result.Outcome);
        Assert.Contains(Mat, result.ShortageProducts);          // 不擋單但據實回報
        var wo = _context.WorkOrders.Find("WO-1")!;
        Assert.Equal(WorkOrderStatus.Released, wo.Status);
        Assert.Equal(DocumentApprovalStatus.Approved, wo.ApprovalStatus);
        // 料況據實回填，不得一律標 Ready（否則畫面與實際不符）
        Assert.Equal(MaterialReadinessStatus.Shortage, wo.MaterialReadinessStatus);
    }

    /// <summary>§25.3-①：跳過淨量重算——暫定量必須原封不動，不得被 net-at-release 改寫。</summary>
    [Fact]
    public async Task AutoRelease_DoesNotApplyNetAtReleaseRecalc()
    {
        SeedInventory(Fg, 20m);     // 完整模式會把 70 重算成 80
        SeedInventory(Mat, 999m);
        SeedWo(gross: 100m, plannedQty: 70m, matRequired: 5m);

        var result = await MakeService().AutoReleaseAsync("WO-1");

        Assert.Equal(WorkOrderReleaseOutcome.Released, result.Outcome);
        Assert.Equal(70m, result.PlannedQty);
        Assert.Equal(70m, result.RecalculatedQty);
        Assert.Equal(70m, _context.WorkOrders.Find("WO-1")!.Quantity);
    }

    /// <summary>§25.3-④：標準成本快照必須保留，否則成本差異分析與 CST 成本鏈全空。</summary>
    [Fact]
    public async Task AutoRelease_TakesStandardCostSnapshot()
    {
        SeedInventory(Mat, 999m);
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m, matUnitCost: 3m);

        await MakeService().AutoReleaseAsync("WO-1");

        Assert.Equal(15m, _context.WorkOrders.Find("WO-1")!.StandardMaterialCost);   // 5 × 3
    }

    /// <summary>§25.3-⑥：Push 料領料單必須自動產生，否則簡化模式上線即產生錯誤料帳。</summary>
    [Fact]
    public async Task AutoRelease_PushMaterial_CreatesMaterialSlip()
    {
        SeedInventory(Mat, 999m);
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m);

        await MakeService().AutoReleaseAsync("WO-1");

        var slip = _context.MaterialSlips.Include(s => s.Lines)
            .SingleOrDefault(s => s.SourceDocType == "WorkOrder" && s.SourceDocId == "WO-1");
        Assert.NotNull(slip);
        Assert.Equal(MaterialSlipStatus.Draft, slip!.Status);
        Assert.Equal(Mat, Assert.Single(slip.Lines).ProductCode);
    }

    [Fact]
    public async Task AutoRelease_PushMaterialWithZeroQty_NotOnSlip()
    {
        SeedInventory(Mat, 999m);
        var wo = SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m);
        var op = wo.Operations.Single();
        var zero = new WorkOrderMaterial { WorkOrderId = "WO-1", OperationSeq = 10, BomSeq = 2, ProductCode = "MAT-0",
                                           RequiredQty = 0m, UomCode = "PC", IssueMethod = MaterialIssueMethod.Push };
        op.Materials.Add(zero);
        _context.SaveChanges();

        await MakeService().AutoReleaseAsync("WO-1");

        var slip = _context.MaterialSlips.Include(s => s.Lines).Single(s => s.SourceDocId == "WO-1");
        Assert.Equal(Mat, Assert.Single(slip.Lines).ProductCode);   // 0 量的那一行不開
    }

    /// <summary>Backflush 料不產生領料單（與完整模式一致）。</summary>
    [Fact]
    public async Task AutoRelease_BackflushMaterial_CreatesNoMaterialSlip()
    {
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m, issue: MaterialIssueMethod.Backflush);

        await MakeService().AutoReleaseAsync("WO-1");

        Assert.Empty(_context.MaterialSlips.Where(s => s.SourceDocId == "WO-1"));
    }

    /// <summary>§25.3-③：在途 peg 保留——多張工單不得各自把同一批在途算成自己的。</summary>
    [Fact]
    public async Task AutoRelease_InTransit_StillPegsSupply()
    {
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 10m);
        SeedApprovedPo("PO-1", 10m, DateOnly.FromDateTime(PlannedStart.AddDays(-1)));

        var result = await MakeService().AutoReleaseAsync("WO-1");

        Assert.Equal(WorkOrderReleaseOutcome.Released, result.Outcome);
        Assert.True(result.MaterialRisk);
        var peg = _context.Set<InventoryReservation>()
            .SingleOrDefault(r => r.Category == ReservationCategory.Supply && r.ProductCode == Mat);
        Assert.NotNull(peg);
        Assert.Equal(10m, peg!.ReservedQty);
    }

    /// <summary>§25.3-⑧：委外工單漏建採購單 → 委外流程整條斷掉（無收貨對象、無應付）。</summary>
    [Fact]
    public async Task AutoRelease_OutsourceWorkOrder_CreatesPurchaseOrder()
    {
        var wo = SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m);
        wo.IsOutsource = true;
        wo.OutsourceSupplierCode = "S1";
        wo.OutsourceExpectedDeliveryDate = DateOnly.FromDateTime(PlannedStart.AddDays(5));
        _context.SaveChanges();

        var result = await MakeService().AutoReleaseAsync("WO-1");

        Assert.Equal(WorkOrderReleaseOutcome.Released, result.Outcome);
        var poId = _context.WorkOrders.Find("WO-1")!.OutsourcePurchaseOrderId;
        Assert.False(string.IsNullOrEmpty(poId));
        var po = _context.PurchaseOrders.Include(p => p.Lines).Single(p => p.PurchaseOrderId == poId);
        Assert.Equal("S1", po.SupplierCode);
        var line = Assert.Single(po.Lines);
        Assert.Equal(10m, line.OrderQty);
        // 在途以庫存單位計：原本沒填 StockQty（恆 0），這張單的在途在投產料閘與缺貨分析都會消失（2026-10-27）
        Assert.Equal(10m, line.StockQty);
        // 委外工單的料在供應商端，不產生本廠領料單（與完整模式一致）
        Assert.Empty(_context.MaterialSlips.Where(s => s.SourceDocId == "WO-1"));
    }

    [Fact]
    public async Task AutoRelease_NotPlanned_DoesNothing()
    {
        var wo = SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m);
        wo.Status = WorkOrderStatus.Released;
        _context.SaveChanges();

        var result = await MakeService().AutoReleaseAsync("WO-1");

        Assert.Equal(WorkOrderReleaseOutcome.NotPlanned, result.Outcome);
        Assert.Empty(_context.MaterialSlips.Where(s => s.SourceDocId == "WO-1"));   // 不重複建領料單
    }

    [Fact]
    public async Task AutoRelease_UnknownWorkOrder_ReturnsNotFound()
    {
        var result = await MakeService().AutoReleaseAsync("WO-NOPE");

        Assert.Equal(WorkOrderReleaseOutcome.NotFound, result.Outcome);
    }

    // ── 整批投產（§24.1 all-or-nothing）────────────────────────────────────

    private void SeedBatch(string batchId = "B-1") =>
        _context.ProductionBatches.Add(new ProductionBatch
        {
            BatchId = batchId, ProductCode = Fg, Quantity = 10m,
            Status = ProductionBatchStatus.Planned, PlantId = PlantId,
            PlannedEndDate = PlannedStart.AddDays(5),
        });

    [Fact]
    public async Task ReleaseBatch_AllReady_ReleasesEntireBatch()
    {
        SeedBatch();
        SeedInventory(Mat, 999m);                          // 料件充足
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m);
        _context.SaveChanges();

        var result = await MakeService().ReleaseBatchAsync("B-1", confirmed: false);

        Assert.Equal(BatchReleaseOutcome.Released, result.Outcome);
        Assert.Equal(1, result.Released);
        Assert.Equal(WorkOrderStatus.Released, _context.WorkOrders.Find("WO-1")!.Status);
    }

    [Fact]
    public async Task ReleaseBatch_OneBlocked_NothingReleased()
    {
        SeedBatch();
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 10m);   // 無料、無在途 → 缺料
        _context.SaveChanges();

        var result = await MakeService().ReleaseBatchAsync("B-1", confirmed: false);

        Assert.Equal(BatchReleaseOutcome.MaterialBlocked, result.Outcome);
        Assert.Contains(result.Blocked, b => b.WorkOrderId == "WO-1" && b.Shortages.Contains(Mat));
        Assert.Equal(WorkOrderStatus.Planned, _context.WorkOrders.Find("WO-1")!.Status);   // 未投產
    }

    [Fact]
    public async Task ReleaseBatch_NoPlannedWorkOrders_ReturnsNothingToRelease()
    {
        SeedBatch();
        _context.SaveChanges();

        var result = await MakeService().ReleaseBatchAsync("B-1", confirmed: false);

        Assert.Equal(BatchReleaseOutcome.NothingToRelease, result.Outcome);
    }

    // ── 簽核判定與金額門檻（mfg-domain §18.2）──────────────────────────────
    //
    // 這組測試存在的理由：門檻原本是死碼——分支只在「沒有簽核組態」時執行，
    // 而沒有組態時下一行的守衛必定成立並直接核准，所以 mfg_wo_approval_threshold
    // 設任何值都無法改變結果，且不會有任何錯誤訊息。修好之後必須有測試釘住
    // 「低於門檻走快速通道」這條路，否則同一個分支再度被改死也一樣不會有人發現。

    /// <summary>設定簽核組態；預設是「真的要簽」（IsApprovalRequired=true + 範本）。</summary>
    private void SetupApprovalConfig(bool required = true, string? templateId = "TMPL-1")
    {
        _approval.Setup(a => a.GetApprovalConfig("WorkOrder", PlantId))
                 .ReturnsAsync(new DocumentApprovalConfig
                 {
                     DocumentType = "WorkOrder", PlantId = PlantId,
                     IsApprovalRequired = required, ApprovalTemplateId = templateId
                 });
        _approval.Setup(a => a.SubmitForApproval(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                 .ReturnsAsync(new ApprovalRequest { Id = "REQ-1" });
    }

    private void SetupThreshold(string? value) =>
        _sysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgWoApprovalThreshold,
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(value);

    [Fact]
    public async Task Apply_ApprovalConfigured_NoThreshold_Submits()
    {
        SetupApprovalConfig();
        SeedInventory(Mat, 999m);
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m, matUnitCost: 3m);   // 標準成本 15

        var result = await MakeService().ApplyAsync("WO-1", confirmed: true);

        Assert.Equal(WorkOrderReleaseOutcome.Submitted, result.Outcome);
        var wo = _context.WorkOrders.Find("WO-1")!;
        Assert.Equal(DocumentApprovalStatus.PendingApproval, wo.ApprovalStatus);
        Assert.Equal("REQ-1", wo.ApprovalRequestId);
    }

    /// <summary>低於門檻＝快速通道自動核准。修好之前這條路走不到。</summary>
    [Fact]
    public async Task Apply_ApprovalConfigured_CostBelowThreshold_ApprovesDirectly()
    {
        SetupApprovalConfig();
        SetupThreshold("20");
        SeedInventory(Mat, 999m);
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m, matUnitCost: 3m);   // 15 ≤ 20

        var result = await MakeService().ApplyAsync("WO-1", confirmed: true);

        Assert.Equal(WorkOrderReleaseOutcome.Released, result.Outcome);
        Assert.Equal(DocumentApprovalStatus.Approved, _context.WorkOrders.Find("WO-1")!.ApprovalStatus);
        _approval.Verify(a => a.SubmitForApproval(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>等於門檻不送審——門檻語意是「超過才要簽」。</summary>
    [Fact]
    public async Task Apply_ApprovalConfigured_CostEqualsThreshold_ApprovesDirectly()
    {
        SetupApprovalConfig();
        SetupThreshold("15");
        SeedInventory(Mat, 999m);
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m, matUnitCost: 3m);   // 15 = 15

        var result = await MakeService().ApplyAsync("WO-1", confirmed: true);

        Assert.Equal(WorkOrderReleaseOutcome.Released, result.Outcome);
    }

    [Fact]
    public async Task Apply_ApprovalConfigured_CostAboveThreshold_Submits()
    {
        SetupApprovalConfig();
        SetupThreshold("10");
        SeedInventory(Mat, 999m);
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m, matUnitCost: 3m);   // 15 > 10

        var result = await MakeService().ApplyAsync("WO-1", confirmed: true);

        Assert.Equal(WorkOrderReleaseOutcome.Submitted, result.Outcome);
    }

    /// <summary>
    /// IsApprovalRequired=false 但範本還留著（SQL 直接寫入的組態就會長這樣）→ 不簽核。
    /// 本處原本只看範本，是全站唯一漏看這個旗標的呼叫端。
    /// </summary>
    [Fact]
    public async Task Apply_ConfigNotRequiredButTemplateLeftOver_ApprovesDirectly()
    {
        SetupApprovalConfig(required: false);
        SeedInventory(Mat, 999m);
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m, matUnitCost: 3m);

        var result = await MakeService().ApplyAsync("WO-1", confirmed: true);

        Assert.Equal(WorkOrderReleaseOutcome.Released, result.Outcome);
        _approval.Verify(a => a.SubmitForApproval(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// 沒有簽核組態時，門檻再低也不送審——沒有範本就無處可送。
    /// 這是刻意保留的行為，不是又一個死分支：要讓大額工單走簽核，得先設組態。
    /// </summary>
    [Fact]
    public async Task Apply_NoConfig_ThresholdExceeded_StillApprovesDirectly()
    {
        SetupThreshold("1");
        SeedInventory(Mat, 999m);
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m, matUnitCost: 3m);   // 15 > 1

        var result = await MakeService().ApplyAsync("WO-1", confirmed: true);

        // 仍直接核准，但回報「本該送審卻無流程」讓畫面警示（A3，原本完全無聲）
        Assert.Equal(WorkOrderReleaseOutcome.ReleasedNoApprovalFlow, result.Outcome);
        Assert.True(result.Outcome.IsReleased());
        Assert.Equal(DocumentApprovalStatus.Approved, _context.WorkOrders.Find("WO-1")!.ApprovalStatus);
    }

    [Fact]
    public async Task Apply_NoConfig_BelowThreshold_PlainReleased()
    {
        SetupThreshold("100");
        SeedInventory(Mat, 999m);
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m, matUnitCost: 3m);   // 15 ≤ 100

        var result = await MakeService().ApplyAsync("WO-1", confirmed: true);

        Assert.Equal(WorkOrderReleaseOutcome.Released, result.Outcome);
    }

    // ── 門檻的等價類別與邊界（第 2 節補測）─────────────────────────────────

    [Theory]
    [InlineData(null,       null)]
    [InlineData("",         null)]
    [InlineData("0",        null)]      // 0＝沒設
    [InlineData("-1",       null)]      // 負數＝沒設（否則每張都超過）
    [InlineData("abc",      null)]
    [InlineData(" 1500 ",   "1500")]    // 前後空白
    [InlineData("1,500",    "1500")]    // 千分位
    [InlineData("10.5",     "10.5")]
    [InlineData("0.000001", "0.000001")] // 最小正數
    public void ParseThreshold_EquivalenceClasses(string? raw, string? expected)
    {
        Assert.Equal(expected == null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            WorkOrderReleaseService.ParseThreshold(raw));
    }

    /// <summary>門檻是「超過才送審」：等於不送（上面已測）、多一個最小單位就送。</summary>
    [Fact]
    public async Task Apply_CostJustAboveThreshold_Submits()
    {
        SetupApprovalConfig();
        SetupThreshold("14.999999");
        SeedInventory(Mat, 999m);
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m, matUnitCost: 3m);   // 15 > 14.999999

        Assert.Equal(WorkOrderReleaseOutcome.Submitted, (await MakeService().ApplyAsync("WO-1", confirmed: true)).Outcome);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    public async Task Apply_InvalidThreshold_TreatedAsUnset_Submits(string raw)
    {
        SetupApprovalConfig();
        SetupThreshold(raw);
        SeedInventory(Mat, 999m);
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m, matUnitCost: 3m);

        Assert.Equal(WorkOrderReleaseOutcome.Submitted, (await MakeService().ApplyAsync("WO-1", confirmed: true)).Outcome);
    }

    // ── §18.2 強制送審條件（A9）────────────────────────────────────────────

    private void SetupFlag(string key, string? value) =>
        _sysParam.Setup(s => s.GetAsync(key, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(value);

    [Theory]
    [InlineData("true",  WorkOrderReleaseOutcome.Submitted)]
    [InlineData(null,    WorkOrderReleaseOutcome.Released)]   // 未設定＝舊行為（只看門檻）
    [InlineData("false", WorkOrderReleaseOutcome.Released)]
    public async Task Apply_ReworkOrder_ForcedApproval_IgnoresThresholdFastPath(string? flag, WorkOrderReleaseOutcome expected)
    {
        SetupApprovalConfig();
        SetupThreshold("100");                                    // 15 ≤ 100：沒有強制條件時走快速通道
        SetupFlag(ProjectZSystemParamKeys.MfgWoApprovalRework, flag);
        SeedInventory(Mat, 999m);
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m, matUnitCost: 3m).Type = WorkOrderType.Rework;
        _context.SaveChanges();

        Assert.Equal(expected, (await MakeService().ApplyAsync("WO-1", confirmed: true)).Outcome);
    }

    [Fact]
    public async Task Apply_ReworkFlagOn_NormalOrder_NotForced()
    {
        SetupApprovalConfig();
        SetupThreshold("100");
        SetupFlag(ProjectZSystemParamKeys.MfgWoApprovalRework, "true");
        SetupFlag(ProjectZSystemParamKeys.MfgWoApprovalOutsource, "true");
        SeedInventory(Mat, 999m);
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m, matUnitCost: 3m);   // 一般自製

        Assert.Equal(WorkOrderReleaseOutcome.Released, (await MakeService().ApplyAsync("WO-1", confirmed: true)).Outcome);
    }

    /// <summary>延期＝工單預計完工晚於生產批需求日（MRP 寫入的訂單交期）；同日不算延期。</summary>
    [Theory]
    [InlineData(1,  WorkOrderReleaseOutcome.Submitted)]
    [InlineData(0,  WorkOrderReleaseOutcome.Released)]
    [InlineData(-1, WorkOrderReleaseOutcome.Released)]
    public async Task Apply_OnDelayFlag_ComparesPlannedEndWithBatchDueDate(int woEndMinusDueDays, WorkOrderReleaseOutcome expected)
    {
        SetupApprovalConfig();
        SetupThreshold("100");
        SetupFlag(ProjectZSystemParamKeys.MfgWoApprovalOnDelay, "true");
        SeedInventory(Mat, 999m);
        var wo = SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m, matUnitCost: 3m);
        _context.ProductionBatches.Add(new ProductionBatch
        {
            BatchId = "B-1", MrpRunId = "MRP-1", MrpRunLineSeq = 1, ProductCode = Fg, Quantity = 10, PlantId = PlantId,
            PlannedEndDate = wo.PlannedEndDate.AddDays(-woEndMinusDueDays),
        });
        _context.SaveChanges();

        Assert.Equal(expected, (await MakeService().ApplyAsync("WO-1", confirmed: true)).Outcome);
    }

    [Fact]
    public async Task Apply_NoConfig_ForcedCondition_ReleasedWithWarning()
    {
        SetupFlag(ProjectZSystemParamKeys.MfgWoApprovalRework, "true");
        SeedInventory(Mat, 999m);
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m, matUnitCost: 3m).Type = WorkOrderType.Rework;
        _context.SaveChanges();

        Assert.Equal(WorkOrderReleaseOutcome.ReleasedNoApprovalFlow,
            (await MakeService().ApplyAsync("WO-1", confirmed: true)).Outcome);
    }

    // ── 簡化模式也遵守明確設定的門檻與強制條件（A4）─────────────────────────

    [Theory]
    [InlineData(null,  WorkOrderReleaseOutcome.Released)]   // 沒設門檻：§25 直接核准
    [InlineData("100", WorkOrderReleaseOutcome.Released)]   // 15 ≤ 100
    [InlineData("10",  WorkOrderReleaseOutcome.Submitted)]  // 15 > 10：送審
    public async Task AutoRelease_ApprovalConfigured_HonoursExplicitThreshold(string? threshold, WorkOrderReleaseOutcome expected)
    {
        SetupApprovalConfig();
        SetupThreshold(threshold);
        SeedInventory(Mat, 999m);
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m, matUnitCost: 3m);

        var result = await MakeService().AutoReleaseAsync("WO-1");

        Assert.Equal(expected, result.Outcome);
        var wo = _context.WorkOrders.Find("WO-1")!;
        Assert.Equal(WorkOrderStatus.Released, wo.Status);
        Assert.Equal(expected == WorkOrderReleaseOutcome.Submitted ? DocumentApprovalStatus.PendingApproval
            : DocumentApprovalStatus.Approved, wo.ApprovalStatus);
        // Push 領料單只在直接核准時產生；送審者等核准回寫再建
        Assert.Equal(expected == WorkOrderReleaseOutcome.Submitted ? 0 : 1, _context.MaterialSlips.Count());
    }

    [Fact]
    public async Task AutoRelease_ForcedCondition_Submits()
    {
        SetupApprovalConfig();
        SetupFlag(ProjectZSystemParamKeys.MfgWoApprovalRework, "true");
        SeedInventory(Mat, 999m);
        SeedWo(gross: 0m, plannedQty: 10m, matRequired: 5m, matUnitCost: 3m).Type = WorkOrderType.Rework;
        _context.SaveChanges();

        Assert.Equal(WorkOrderReleaseOutcome.Submitted, (await MakeService().AutoReleaseAsync("WO-1")).Outcome);
    }
}
