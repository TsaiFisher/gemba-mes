using Moq;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// A2：完工人工/製費吸收分錄（§16.3）DB-backed 測試。
/// 驗證 ProductionCompletionService 完工時產生「借存貨、貸人工/製費已分配」的平衡分錄，
/// 且無轉換成本（人工+製費=0）時不過帳。
/// </summary>
public class ProductionCompletionJournalTests : TestBase
{
    private const string PlantId = "P001";
    private const string WoId    = "WO-A2-1";

    private readonly Mock<IJournalEntryService> _journal = new();
    private IList<JournalLineRequest>? _capturedLines;

    public ProductionCompletionJournalTests()
    {
        _context.CurrentPlantId = PlantId;

        _journal
            .Setup(j => j.CreateAutoEntryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(),
                It.IsAny<string>(), It.IsAny<IList<JournalLineRequest>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, DateOnly, string, IList<JournalLineRequest>, string?, CancellationToken>(
                (_, _, _, _, lines, _, _) => _capturedLines = lines)
            .ReturnsAsync(new JournalEntry());
    }

    [Fact]
    public async Task Complete_PostsAbsorptionJournal_BalancedAndSplit()
    {
        SeedProduct("FG01");
        SeedWorkOrder(material: 1000m, labor: 500m, overhead: 200m);
        SeedDispatch(qtyDone: 10m);

        var svc = BuildService();
        await svc.CompleteAsync(WoId, PlantId, "WH1", "L1");

        Assert.NotNull(_capturedLines);
        var lines = _capturedLines!;

        // 借存貨 = 人工 + 製費（轉換成本加值）
        var inv = lines.Single(l => l.AccountCode == StandardAccounts.RawMaterialInventoryCode);
        Assert.Equal(700m, inv.Debit);

        var labor = lines.Single(l => l.AccountCode == StandardAccounts.DirectLaborAbsorbedCode);
        Assert.Equal(500m, labor.Credit);

        var oh = lines.Single(l => l.AccountCode == StandardAccounts.MfgOverheadAbsorbedCode);
        Assert.Equal(200m, oh.Credit);

        // 借貸平衡
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
    }

    [Fact]
    public async Task Complete_OperationOutsourceCost_InUnitCostButNotInAbsorptionJournal()
    {
        // §19.7 C：工序委外的加工費已於委外收貨時借存貨、帶出料本來就在存貨科目——
        // 只進成品單位成本與成本結算，不得再進人工/製費吸收分錄（否則存貨科目重複入帳）。
        SeedProduct("FG05");
        SeedWorkOrder(material: 1000m, labor: 500m, overhead: 200m);
        (await _context.WorkOrders.FindAsync(WoId))!.ActualOutsourceCost = 300m;
        _context.SaveChanges();
        SeedDispatch(qtyDone: 10m);

        await BuildService().CompleteAsync(WoId, PlantId, "WH1", "L1");

        var fg = _context.StockMovements.Local.Single(m => m.DocNo == WoId);
        Assert.Equal(200m, fg.UnitCost);                                   // (1000+500+200+300) ÷ 10
        Assert.Equal(700m, _capturedLines!.Single(l => l.AccountCode == StandardAccounts.RawMaterialInventoryCode).Debit);
        var cost = await _context.WorkOrderActualCosts.FindAsync(WoId);
        Assert.Equal((300m, 2000m), (cost!.ActualOutsource, cost.ActualTotal));
    }

    [Fact]
    public async Task Complete_NoConversionCost_DoesNotPostJournal()
    {
        SeedProduct("FG02");
        SeedWorkOrder(material: 1000m, labor: 0m, overhead: 0m);
        SeedDispatch(qtyDone: 10m);

        var svc = BuildService();
        await svc.CompleteAsync(WoId, PlantId, "WH1", "L1");

        Assert.Null(_capturedLines);
        _journal.Verify(j => j.CreateAutoEntryAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<string>(),
            It.IsAny<IList<JournalLineRequest>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Complete_OnlyLabor_OmitsOverheadLine()
    {
        SeedProduct("FG03");
        SeedWorkOrder(material: 0m, labor: 300m, overhead: 0m);
        SeedDispatch(qtyDone: 5m);

        var svc = BuildService();
        await svc.CompleteAsync(WoId, PlantId, "WH1", "L1");

        Assert.NotNull(_capturedLines);
        Assert.DoesNotContain(_capturedLines!, l => l.AccountCode == StandardAccounts.MfgOverheadAbsorbedCode);
        Assert.Equal(300m, _capturedLines!.Single(l => l.AccountCode == StandardAccounts.RawMaterialInventoryCode).Debit);
    }

    [Fact]
    public async Task Complete_MixedIssueMethods_ActualIncludesPushPullMaterial()
    {
        // D1-A：全料標準 1000（後沖 400 + Push 600）。後沖料於完工累計 400；Push 料已於
        // 生產中經發料單過帳 600（種子 StockMovement）→ ActualMaterialCost = 400 + 600 = 1000，
        // 完工品單位成本涵蓋全部材料；差異以全料基準 = 0。
        SeedProduct("FG04");
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId          = WoId,
            ProductCode          = "FG04",
            Quantity             = 10m,
            Status               = WorkOrderStatus.Completed,
            PlantId              = PlantId,
            StandardMaterialCost = 1000m,
            Operations =
            [
                new WorkOrderOperation
                {
                    WorkOrderId = WoId, OperationSeq = 10, WorkCenterId = "WC1",
                    Materials =
                    [
                        new WorkOrderMaterial
                        {
                            WorkOrderId = WoId, OperationSeq = 10, BomSeq = 1,
                            ProductCode = "RM-BF", UomCode = "PCS",
                            RequiredQty = 10m, UnitCost = 40m,
                            IssueMethod = MaterialIssueMethod.Backflush,
                            BackflushTiming = BackflushTiming.AtCompletion,
                        },
                        new WorkOrderMaterial
                        {
                            WorkOrderId = WoId, OperationSeq = 10, BomSeq = 2,
                            ProductCode = "RM-PUSH", UomCode = "PCS",
                            RequiredQty = 10m, UnitCost = 60m,
                            IssueMethod = MaterialIssueMethod.Push,
                        },
                    ],
                },
            ],
        });
        // Push 料已於生產中發料：MaterialSlip（已過帳）+ Production 領料異動 600
        _context.MaterialSlips.Add(new MaterialSlip
        {
            SlipId = WoId + "-S1", SourceDocType = "WorkOrder", SourceDocId = WoId,
            Status = MaterialSlipStatus.Posted, PlantId = PlantId,
        });
        _context.StockMovements.Add(new StockMovement
        {
            DocNo = WoId + "-S1", DocLineNo = 1, TypeCode = "IO", TypeCategory = "Production",
            PlantId = PlantId, ProductCode = "RM-PUSH", LocationCode = "L1", LotNo = "",
            StockQty = 10m, Qty = 10m, StockUomCode = "PCS", UomCode = "PCS",
            Direction = -1, UnitCost = 60m, TotalCost = 600m,
            MovementDate = new DateOnly(2026, 6, 15),
        });
        _context.SaveChanges();
        SeedDispatch(qtyDone: 10m);

        var svc = BuildService();
        await svc.CompleteAsync(WoId, PlantId, "WH1", "L1");

        var wo = await _context.WorkOrders.FindAsync(WoId);
        Assert.Equal(1000m, wo!.ActualMaterialCost);   // 後沖 400 + Push 600
        Assert.Equal(0m,    wo.MaterialVarianceCost);  // 1000 − 全料標準 1000

        var cost = await _context.WorkOrderActualCosts.FindAsync(WoId);
        Assert.Equal(1000m, cost!.StandardMaterial);   // 全料基準
        Assert.Equal(0m,    cost.VarMaterial);
    }

    // ── 輔助 ──────────────────────────────────────────────────────────────

    // ── §24.2a 產出留給原需求 ─────────────────────────────────────────────

    [Fact]
    public async Task Complete_MrpSourcedWo_LocksOutputToSourceDemand_CappedByGrossMinusAlreadyLocked()
    {
        SeedProduct("FG01");
        SeedWorkOrder(material: 0m, labor: 0m, overhead: 0m);
        var wo = (await _context.WorkOrders.FindAsync(WoId))!;
        (wo.GrossDemandQty, wo.SourceMrpRunId, wo.SourceMrpRunLineSeq) = (12m, "MRP-1", 1);
        _context.MrpRunLines.Add(new MrpRunLine { MrpRunId = "MRP-1", LineSeq = 1, ProductCode = "FG01", RequiredQty = 12m });
        // 投產時已把 4 個在手成品鎖給原需求：產出只需再鎖 12 − 4 = 8，雖然做了 10
        _context.InventoryReservations.Add(new InventoryReservation
        {
            PlantId = PlantId, ProductCode = "FG01", ReservedQty = 4m,
            SourceType = ReservationSourceType.MrpRunLine, SourceDocId = "MRP-1", SourceLineSeq = 1,
        });
        _context.SaveChanges();
        SeedDispatch(qtyDone: 10m);

        await BuildService(new InventoryReservationService(_context)).CompleteAsync(WoId, PlantId, "WH1", "L1");

        var locked = _context.InventoryReservations
            .Where(r => r.SourceType == ReservationSourceType.MrpRunLine && r.SourceDocId == "MRP-1"
                     && r.Status == ReservationStatus.Active)
            .Sum(r => r.ReservedQty);
        Assert.Equal(12m, locked);
    }

    [Fact]
    public async Task Complete_ManualWo_NoGrossDemand_LocksNothing()
    {
        SeedProduct("FG01");
        SeedWorkOrder(material: 0m, labor: 0m, overhead: 0m);   // 手動工單：沒有毛需求、沒有來源
        SeedDispatch(qtyDone: 10m);

        await BuildService(new InventoryReservationService(_context)).CompleteAsync(WoId, PlantId, "WH1", "L1");

        Assert.Empty(_context.InventoryReservations.Where(r => r.ProductCode == "FG01"));
    }

    private ProductionCompletionService BuildService(IInventoryReservationService? realReservations = null)
    {
        var posting = new Mock<IInventoryPostingService>();
        posting
            .Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .Returns<PostDocSimplifiedRequest, CancellationToken>((req, _) =>
            {
                // 替身的契約：過帳除了回寫數量與狀態，還會留下庫存異動——完工成本
                // 現在是從那些異動加總出來的（取實際出庫成本，不是工單快照價）。
                // 替身若不產生異動，後沖成本會被算成 0 而測試仍然「綠」。
                //
                // 這裡以呼叫端傳入的單價作為異動成本，只是把成本當成本測試的輸入；
                // 「出庫該用哪個成本」由 Flows/MfgCostBasisFlowTests 以真實過帳服務
                // 與真實庫存釘住，本測試釘的是「後沖與 Push/Pull 兩條來源都要計入」。
                foreach (var l in req.Lines)
                {
                    l.SetQtyPutAway(l.Qty);
                    _context.StockMovements.Add(new StockMovement
                    {
                        DocNo = req.DocNo, DocLineNo = l.LineNo,
                        TypeCode = req.TypeCode, TypeCategory = req.TypeCategory,
                        MovementDate = req.MovementDate, PlantId = req.PlantId,
                        ProductCode = l.ProductCode, LocationCode = l.LocationCode,
                        LotNo = l.LotNo, UomCode = l.UomCode, StockUomCode = l.StockUomCode,
                        Qty = l.Qty, StockQty = l.Qty, Direction = -1,
                        UnitCost = l.UnitPrice, TotalCost = (l.UnitPrice ?? 0m) * l.Qty,
                    });
                }
                req.SetDocPosted();
                return Task.CompletedTask;
            });

        var reservations = new Mock<IInventoryReservationService>();
        reservations
            .Setup(r => r.ReleaseBySourceAsync(It.IsAny<ReservationSourceType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var dt = new Mock<IDateTimeService>();
        dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(new DateOnly(2026, 6, 15));

        return new ProductionCompletionService(
            _context, posting.Object, realReservations ?? reservations.Object, dt.Object, _journal.Object);
    }

    private void SeedProduct(string code)
    {
        _context.Products.Add(new SmartFactory.MES.Modules.BAS.Models.Product
        {
            ProductCode = code,
            ProductName = code,
            StockUom    = "PCS",
        });
        _context.SaveChanges();
    }

    private void SeedWorkOrder(decimal material, decimal labor, decimal overhead)
    {
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId         = WoId,
            ProductCode         = "FG01",
            Quantity            = 10m,
            Status              = WorkOrderStatus.Completed,
            PlantId             = PlantId,
            ActualMaterialCost  = material,
            ActualLaborCost     = labor,
            ActualOverheadCost  = overhead,
            StandardMaterialCost = material,
            StandardLaborCost    = labor,
            StandardOverheadCost = overhead,
        });
        _context.SaveChanges();
    }

    private void SeedDispatch(decimal qtyDone)
    {
        _context.Dispatches.Add(new Dispatch
        {
            DispatchId   = WoId + "-D1",
            WorkOrderId  = WoId,
            OperationSeq = 10,
            QtyDone      = qtyDone,
            PlantId      = PlantId,
        });
        _context.SaveChanges();
    }
}
