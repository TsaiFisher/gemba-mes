using SmartFactory.MES.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// MrpEngine 單元測試（mfg-domain.md §5.1 / §11 Phase B 驗收標準）。
/// IDocNumberService.NextAsync 依賴 PostgreSQL UPSERT，InMemory 下改用 Mock；
/// IInventoryReservationService 用真實實作（對 InMemory DB），以驗證 Fstock 扣預留。
/// </summary>
public class MrpEngineTests : TestBase
{
    private readonly Mock<IDocNumberService> _mockDocNo = new();
    private readonly Mock<IDateTimeService> _mockDt = new();
    private readonly Mock<IPlantParamService> _mockSysParam = new();
    private readonly MrpEngine _engine;
    private const string PlantId = "P001";
    private static readonly DateTime Today = new(2026, 5, 25, 8, 0, 0, DateTimeKind.Utc);

    public MrpEngineTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockDt.Setup(d => d.Now).Returns(Today);

        var batchNo = 0;
        _mockDocNo
            .Setup(d => d.NextAsync(MrpEngine.BatchDocType, PlantId, It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => $"PB-{++batchNo:D4}");

        // 非簡化模式（完整 MRP 路徑）；對齊 MrpEngine 讀 InvSimplifiedMode 的判斷
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("false");

        var reservations = new InventoryReservationService(_context);
        _engine = new MrpEngine(_context, reservations, _mockDocNo.Object, _mockDt.Object, _mockSysParam.Object, Mock.Of<IWorkOrderReleaseService>());
    }

    // ── 測試資料建構 helpers ─────────────────────────────────────────────────

    private void AddProduct(string code, string mrpMode, string? routingId = null, int leadTimeDays = 0, decimal standardCost = 0,
        decimal safetyStock = 0, decimal? minLotSize = null, decimal? lotMultiple = null)
        => _context.Products.Add(new Product
        {
            ProductCode = code, ProductName = code, StockUom = "PC",
            MrpMode = mrpMode, RoutingId = routingId, LeadTimeDays = leadTimeDays,
            StandardCost = standardCost, SafetyStock = safetyStock,
            MinLotSize = minLotSize, LotMultiple = lotMultiple
        });

    /// <summary>建立單工序途程，BOM 由 (品號,用量) 列表帶入。</summary>
    private void AddRouting(string routingId, string productCode, int bufferDays,
        params (string mat, decimal qty)[] boms)
    {
        var op = new RoutingOperation
        {
            RoutingId = routingId, OperationSeq = 10, OperationName = "OP", WorkCenterId = "WC1",
            TM_SETUP = 0, TM_UNIT = 0, CD_BOR = OperationMode.WholeOrder, QT_BOR = 1, UomCode = "PC"
        };
        var seq = 0;
        foreach (var (mat, qty) in boms)
            op.Boms.Add(new RoutingBom { RoutingId = routingId, OperationSeq = 10, BomSeq = ++seq, ProductCode = mat, Quantity = qty, UomCode = "PC" });

        _context.Routings.Add(new Routing
        {
            RoutingId = routingId, ProductCode = productCode, PlantId = PlantId,
            BatchSize = 1, BufferDays = bufferDays, IsActive = true,
            Operations = new List<RoutingOperation> { op }
        });
    }

    /// <summary>建立單工序途程，且該工序標記為委外（工序委外）。</summary>
    private void AddOutsourceOpRouting(string routingId, string productCode, params (string mat, decimal qty)[] boms)
    {
        AddRouting(routingId, productCode, 0, boms);
        _context.Routings.Local.First(r => r.RoutingId == routingId).Operations.First().IsOutsource = true;
    }

    /// <summary>建立單工序途程，用料可帶損耗率／下角率（2026-07-20 新增下角率驗證用）。</summary>
    private void AddRoutingWithRates(string routingId, string productCode,
        params (string mat, decimal qty, decimal scrap, decimal offcut)[] boms)
    {
        var op = new RoutingOperation
        {
            RoutingId = routingId, OperationSeq = 10, OperationName = "OP", WorkCenterId = "WC1",
            TM_SETUP = 0, TM_UNIT = 0, CD_BOR = OperationMode.WholeOrder, QT_BOR = 1, UomCode = "PC"
        };
        var seq = 0;
        foreach (var (mat, qty, scrap, offcut) in boms)
            op.Boms.Add(new RoutingBom
            {
                RoutingId = routingId, OperationSeq = 10, BomSeq = ++seq,
                ProductCode = mat, Quantity = qty, ScrapRate = scrap, OffcutRate = offcut, UomCode = "PC"
            });

        _context.Routings.Add(new Routing
        {
            RoutingId = routingId, ProductCode = productCode, PlantId = PlantId,
            BatchSize = 1, BufferDays = 0, IsActive = true,
            Operations = new List<RoutingOperation> { op }
        });
    }

    private string AddRun(params (string product, decimal qty, DateTime due)[] lines)
    {
        const string runId = "MRP-0001";
        var run = new MrpRun { MrpRunId = runId, PlantId = PlantId, Status = MrpRunStatus.Draft };
        var seq = 0;
        foreach (var (product, qty, due) in lines)
            run.Lines.Add(new MrpRunLine { MrpRunId = runId, LineSeq = ++seq, ProductCode = product, RequiredQty = qty, RequiredDate = due });
        _context.MrpRuns.Add(run);
        return runId;
    }

    private void AddInventory(string product, decimal onHand)
        => _context.Inventories.Add(new Inventory { PlantId = PlantId, ProductCode = product, LocationCode = "L1", LotNo = "", QtyOnHand = onHand });

    private int _poSeq;

    /// <summary>建立一張已核准、未關閉的採購單行（供 §21.3 PO Scheduled Receipts 測試）。</summary>
    private void AddApprovedPo(string product, decimal orderQty, DateOnly deliveryDate, decimal receivedQty = 0m)
    {
        var poId = $"PO-{++_poSeq:D4}";
        _context.PurchaseOrders.Add(new PurchaseOrder
        {
            PurchaseOrderId = poId,
            PlantId = PlantId,
            SupplierCode = "S1",
            CreatedBy = "tester",
            OrderDate = deliveryDate,
            ApprovalStatus = DocumentApprovalStatus.Approved,
            Lines = new List<PurchaseOrderLine>
            {
                new()
                {
                    PurchaseOrderId = poId, LineNo = 1, ProductCode = product,
                    OrderQty = orderQty, ReceivedQty = receivedQty,
                    StockQty = orderQty, ReceivedStockQty = receivedQty,   // 同單位；預定收料以庫存單位計
                    DeliveryDate = deliveryDate, IsClosed = false
                }
            }
        });
    }

    // ── 測試 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunMps_ThreeLevelBom_BuildsWorkOrderChainAndPurchaseDemand()
    {
        // FG(M) → SA1(M) → SA2(M) → RM(P)，三層自製工單 + 一筆採購需求
        AddProduct("FG", "M", "R_FG");
        AddProduct("SA1", "M", "R_SA1");
        AddProduct("SA2", "M", "R_SA2");
        AddProduct("RM", "P");
        AddRouting("R_FG", "FG", 0, ("SA1", 1));
        AddRouting("R_SA1", "SA1", 0, ("SA2", 1));
        AddRouting("R_SA2", "SA2", 0, ("RM", 2));
        var runId = AddRun(("FG", 10, Today.AddDays(365)));
        await _context.SaveChangesAsync();

        var result = await _engine.RunAsync(runId);

        Assert.Equal(1, result.BatchCount);
        Assert.Equal(3, result.WorkOrderCount);            // FG + SA1 + SA2
        Assert.Equal(3, result.DispatchCount);             // 每工單一工序

        var wos = await _context.WorkOrders.AsNoTracking().ToListAsync();
        var fg = wos.Single(w => w.ProductCode == "FG");
        var sa1 = wos.Single(w => w.ProductCode == "SA1");
        var sa2 = wos.Single(w => w.ProductCode == "SA2");

        Assert.Null(fg.ParentWorkOrderId);                 // 成品為頂層
        Assert.Equal(fg.WorkOrderId, sa1.ParentWorkOrderId);
        Assert.Equal(sa1.WorkOrderId, sa2.ParentWorkOrderId);
        Assert.All(wos, w => Assert.Equal(result.CreatedBatchIds[0], w.ProductionBatchId)); // 同一生產批

        // FG 工單用料指向子工單 SA1；UomCode 帶入
        var fgMat = await _context.WorkOrderMaterials.AsNoTracking().FirstAsync(m => m.WorkOrderId == fg.WorkOrderId);
        Assert.Equal(sa1.WorkOrderId, fgMat.ChildWorkOrderId);
        Assert.Equal("PC", fgMat.UomCode);

        // RM 為採購：SA2 用料 SourceMode=Purchase、無子工單，且列入採購需求
        var rmMat = await _context.WorkOrderMaterials.AsNoTracking().FirstAsync(m => m.ProductCode == "RM");
        Assert.Equal(MrpMode.Purchase, rmMat.SourceMode);
        Assert.Null(rmMat.ChildWorkOrderId);
        Assert.Equal(20m, rmMat.RequiredQty);               // 10 × 2
        Assert.Contains(result.PurchaseDemands, d => d.ProductCode == "RM");
    }

    [Fact]
    public async Task RunMps_TopLevelPurchase_AppliesLotSizing()
    {
        // MinMultiple 政策：淨需求 120 → 至少 100、100 的倍數不足時捨入至 150
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgLotPolicy, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("MinMultiple");
        AddProduct("RM", "P", minLotSize: 100, lotMultiple: 50);
        var runId = AddRun(("RM", 120, Today.AddDays(30)));
        await _context.SaveChangesAsync();

        var result = await _engine.RunAsync(runId);

        var line = await _context.MrpRunLines.AsNoTracking().SingleAsync();
        Assert.Equal(120m, line.PurchaseQty);   // line 欄位維持記錄真實淨需求
        Assert.Equal(150m, result.PurchaseDemands.Single(d => d.ProductCode == "RM").Qty); // 建議採購量已批量化
    }

    [Fact]
    public async Task RunMps_BomMaterialPurchase_AppliesOwnLotSizing_IndependentOfParent()
    {
        // FG(M) 無批量設定 → 依淨需求原量投產 10；BOM 用料 RM(P) 有自己的批量規則，
        // 建議採購量應依 RM 自己的 MinLotSize/LotMultiple 批量化，不受 FG 影響
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgLotPolicy, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("MinMultiple");
        AddProduct("FG", "M", "R_FG");
        AddProduct("RM", "P", minLotSize: 30, lotMultiple: 10);
        AddRouting("R_FG", "FG", 0, ("RM", 1));
        var runId = AddRun(("FG", 10, Today.AddDays(30)));
        await _context.SaveChangesAsync();

        var result = await _engine.RunAsync(runId);

        var rmMat = await _context.WorkOrderMaterials.AsNoTracking().FirstAsync(m => m.ProductCode == "RM");
        Assert.Equal(10m, rmMat.RequiredQty);    // 用料真實需求不受批量化影響（發料/成本追溯用）
        Assert.Equal(30m, result.PurchaseDemands.Single(d => d.ProductCode == "RM").Qty); // max(10,30)=30
    }

    [Fact]
    public async Task RunMps_BomSubLevelMake_AppliesOwnLotSizing_NotCompoundedToGrandchild()
    {
        // FG(M，無批量) → SA(M，批量 15/5) → RM(P，無批量)
        // SA 應依自己的批量規則把 10 捨入到 15；RM 的採購建議 = SA 實際投產量(15) × 1，
        // 不是 SA 淨需求(10)，也不會在 RM 這一階被「再批量化」一次（RM 無批量設定）
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgLotPolicy, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("MinMultiple");
        AddProduct("FG", "M", "R_FG");
        AddProduct("SA", "M", "R_SA", minLotSize: 15, lotMultiple: 5);
        AddProduct("RM", "P");
        AddRouting("R_FG", "FG", 0, ("SA", 1));
        AddRouting("R_SA", "SA", 0, ("RM", 1));
        var runId = AddRun(("FG", 10, Today.AddDays(30)));
        await _context.SaveChangesAsync();

        var result = await _engine.RunAsync(runId);

        var saWo = await _context.WorkOrders.AsNoTracking().SingleAsync(w => w.ProductCode == "SA");
        Assert.Equal(15m, saWo.Quantity);        // max(10,15)=15，15 已是 5 的倍數

        var fgMat = await _context.WorkOrderMaterials.AsNoTracking().SingleAsync(m => m.ProductCode == "SA");
        Assert.Equal(10m, fgMat.RequiredQty);    // FG 對 SA 的真實需求仍是 10，不受 SA 自己的批量影響

        var saMat = await _context.WorkOrderMaterials.AsNoTracking().SingleAsync(m => m.ProductCode == "RM");
        Assert.Equal(15m, saMat.RequiredQty);    // SA 實際投產 15 → 需要 15 個 RM（真實用料需求，非重複批量化）
        Assert.Equal(15m, result.PurchaseDemands.Single(d => d.ProductCode == "RM").Qty); // RM 無批量設定，維持 15
    }

    [Fact]
    public async Task RunMps_WorkOrderMaterial_SnapshotsUnitCostFromProductStandardCost()
    {
        // 用料 RM 標準成本 5 → 工單建立時 WorkOrderMaterial.UnitCost 快照為 5（mfg-domain.md §16.2）
        AddProduct("FG", "M", "R_FG");
        AddProduct("RM", "P", standardCost: 5m);
        AddRouting("R_FG", "FG", 0, ("RM", 2));
        var runId = AddRun(("FG", 4, Today.AddDays(365)));
        await _context.SaveChangesAsync();

        await _engine.RunAsync(runId);

        var rmMat = await _context.WorkOrderMaterials.AsNoTracking().FirstAsync(m => m.ProductCode == "RM");
        Assert.Equal(5m, rmMat.UnitCost);        // 快照自 Product.StandardCost
        Assert.Equal(8m, rmMat.RequiredQty);     // 4 × 2
    }

    // ── 下角率：全外加法，與損耗率同層加總（2026-07-20 新增）─────────────────

    [Fact]
    public async Task RunMps_OffcutRate_AddsAlongsideScrapRateInWorkOrderMaterial()
    {
        AddProduct("FG", "M", "R_FG");
        AddProduct("RM", "P");
        // 工序派工量 10 × 用量 2 = 20；× (1 + 30%下角 + 10%損耗) = 28
        AddRoutingWithRates("R_FG", "FG", ("RM", 2m, 10m, 30m));
        var runId = AddRun(("FG", 10, Today.AddDays(365)));
        await _context.SaveChangesAsync();

        await _engine.RunAsync(runId);

        var rmMat = await _context.WorkOrderMaterials.AsNoTracking().SingleAsync(m => m.ProductCode == "RM");
        Assert.Equal(28m, rmMat.RequiredQty);
    }

    // ── 需求量依 UOM 小數位無條件進位（整數件不可能發 0.25 片）─────────────────

    [Fact]
    public async Task RunMps_RequiredQty_CeilsToUomDecimalPlaces()
    {
        _context.Uoms.Add(new Uom { UomCode = "PC", UomName = "PCS", DecimalPlaces = 0 });
        AddProduct("FG", "M", "R_FG");
        AddProduct("RM", "P");
        // 105 × 1 × (1 + 5% 損耗) = 110.25；PC 為 0 位 → 進位 111
        AddRoutingWithRates("R_FG", "FG", ("RM", 1m, 5m, 0m));
        var runId = AddRun(("FG", 105, Today.AddDays(365)));
        await _context.SaveChangesAsync();

        await _engine.RunAsync(runId);

        var rmMat = await _context.WorkOrderMaterials.AsNoTracking().SingleAsync(m => m.ProductCode == "RM");
        Assert.Equal(111m, rmMat.RequiredQty);
    }

    [Fact]
    public async Task RunMps_RequiredQty_KeepsFractionWhenUomAllowsDecimals()
    {
        _context.Uoms.Add(new Uom { UomCode = "PC", UomName = "KG", DecimalPlaces = 4 });
        AddProduct("FG", "M", "R_FG");
        AddProduct("RM", "P");
        AddRoutingWithRates("R_FG", "FG", ("RM", 1m, 5m, 0m));
        var runId = AddRun(("FG", 105, Today.AddDays(365)));
        await _context.SaveChangesAsync();

        await _engine.RunAsync(runId);

        var rmMat = await _context.WorkOrderMaterials.AsNoTracking().SingleAsync(m => m.ProductCode == "RM");
        Assert.Equal(110.25m, rmMat.RequiredQty);
    }

    [Fact]
    public async Task RunMps_RequiredQty_UnchangedWhenUomNotConfigured()
    {
        // UOM 主檔查無設定 → 不進位（避免主檔缺漏時竄改需求量）
        AddProduct("FG", "M", "R_FG");
        AddProduct("RM", "P");
        AddRoutingWithRates("R_FG", "FG", ("RM", 1m, 5m, 0m));
        var runId = AddRun(("FG", 105, Today.AddDays(365)));
        await _context.SaveChangesAsync();

        await _engine.RunAsync(runId);

        var rmMat = await _context.WorkOrderMaterials.AsNoTracking().SingleAsync(m => m.ProductCode == "RM");
        Assert.Equal(110.25m, rmMat.RequiredQty);
    }

    [Fact]
    public async Task RunJ_WholeProductOutsource_OffcutRateAppliedToSendMaterials()
    {
        // 整品委外：送外料同樣套下角率／損耗率（RunJ 路徑，與 RunMps 共用 RoutingBom.ApplyLossRates）
        AddProduct("FG", "S", "R_FG");
        AddProduct("RM", "P");
        AddRoutingWithRates("R_FG", "FG", ("RM", 1m, 0m, 25m));   // 10 × 1 × 1.25 = 12.5
        var runId = AddRun(("FG", 10, Today.AddDays(365)));
        await _context.SaveChangesAsync();

        var result = await _engine.RunAsync(runId);

        var demand = Assert.Single(result.OutsourceDemands);
        var sendMat = Assert.Single(demand.Materials);
        Assert.Equal("RM", sendMat.ProductCode);
        Assert.Equal(12.5m, sendMat.Qty);
    }

    [Fact]
    public async Task RunMps_StartDateInPast_FlagsPredictedLate()
    {
        // 緩衝 30 天、交期僅 5 天後 → 倒推落在今天之前 → 轉正推且超期 → PredictedLate
        AddProduct("FG", "M", "R_FG");
        AddRouting("R_FG", "FG", bufferDays: 30);
        var runId = AddRun(("FG", 1, Today.AddDays(5)));
        await _context.SaveChangesAsync();

        var result = await _engine.RunAsync(runId);

        var batchId = result.CreatedBatchIds[0];
        Assert.Contains(batchId, result.PredictedLateBatchIds);
        var batch = await _context.ProductionBatches.AsNoTracking().FirstAsync(b => b.BatchId == batchId);
        Assert.True(batch.IsPredictedLate);

        var wo = await _context.WorkOrders.AsNoTracking().FirstAsync();
        Assert.Equal(Today.Date, wo.PlannedStartDate.Date);          // 正推從今天開始
    }

    [Fact]
    public async Task RunMps_AmpleLeadTime_NotPredictedLate()
    {
        AddProduct("FG", "M", "R_FG");
        AddRouting("R_FG", "FG", bufferDays: 30);
        var runId = AddRun(("FG", 1, Today.AddDays(100)));    // 充裕交期
        await _context.SaveChangesAsync();

        var result = await _engine.RunAsync(runId);

        Assert.Empty(result.PredictedLateBatchIds);
        var batch = await _context.ProductionBatches.AsNoTracking().FirstAsync();
        Assert.False(batch.IsPredictedLate);
    }

    [Fact]
    public async Task PreCheck_CyclicBom_ThrowsAndProducesNothing()
    {
        // X 用料含自身（Make）→ 預檢偵測循環用料 → 整張中止（全有全無）
        AddProduct("X", "M", "R_X");
        AddRouting("R_X", "X", 0, ("X", 1));
        var runId = AddRun(("X", 1, Today.AddDays(365)));
        await _context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<MrpExpansionValidationException>(() => _engine.RunAsync(runId));

        var failed = Assert.Single(ex.FailedLines);
        PzAssert.Message("Mfg_BomCycleFmt", failed.Error);
        Assert.False(await _context.WorkOrders.AnyAsync());
        Assert.False(await _context.ProductionBatches.AnyAsync());
        // 引擎拋出後由呼叫端回滾交易並回填 ValidationError；執行單維持 Draft
        var run = await _context.MrpRuns.AsNoTracking().FirstAsync(r => r.MrpRunId == runId);
        Assert.Equal(MrpRunStatus.Draft, run.Status);
    }

    [Fact]
    public async Task PreCheck_AnyLineInvalid_WholeRunAborted()
    {
        // NOROUTE 自製但無途程 → 即使 OK 有效途程，整張也不產生（全有全無）
        AddProduct("NOROUTE", "M");
        AddProduct("OK", "M", "R_OK");
        AddRouting("R_OK", "OK", 0);
        var runId = AddRun(
            ("NOROUTE", 5, Today.AddDays(365)),
            ("OK", 3, Today.AddDays(365)));
        await _context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<MrpExpansionValidationException>(() => _engine.RunAsync(runId));

        var failed = Assert.Single(ex.FailedLines);
        Assert.Equal("NOROUTE", failed.ProductCode);
        Assert.False(await _context.WorkOrders.AnyAsync());       // OK 也不展開
        Assert.False(await _context.ProductionBatches.AnyAsync());
    }

    [Fact]
    public async Task PreCheck_MissingLineProduct_WholeRunAborted()
    {
        // 需求列品號 GHOSTLINE 不存在（原為警示略過）→ 現整張中止
        AddProduct("FG", "M", "R_FG");
        AddRouting("R_FG", "FG", 0);
        var runId = AddRun(
            ("GHOSTLINE", 2, Today.AddDays(365)),
            ("FG", 1, Today.AddDays(365)));
        await _context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<MrpExpansionValidationException>(() => _engine.RunAsync(runId));

        var failed = Assert.Single(ex.FailedLines);
        Assert.Contains("GHOSTLINE", failed.Error);
        Assert.False(await _context.WorkOrders.AnyAsync());
    }

    [Fact]
    public async Task PreCheck_MissingMaterialProduct_WholeRunAborted()
    {
        // 用料 GHOST 不存在 → 預檢攔截、整張中止
        AddProduct("FG", "M", "R_FG");
        AddRouting("R_FG", "FG", 0, ("GHOST", 1));   // 不建立 GHOST 品號
        var runId = AddRun(("FG", 1, Today.AddDays(365)));
        await _context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<MrpExpansionValidationException>(() => _engine.RunAsync(runId));

        var failed = Assert.Single(ex.FailedLines);
        Assert.Contains("GHOST", failed.Error);
        Assert.Equal(0, await _context.WorkOrders.CountAsync());
    }

    [Fact]
    public async Task Fstock_DeductsAvailableAcrossLines_AndPersistsReservation()
    {
        // 庫存 12；兩張 FG(M) 需求各 10，依交期先後處理：
        //   早交期行先取 10（全滿足、不展開），晚交期行剩 2、需自製 8
        AddProduct("FG", "M", "R_FG");
        AddRouting("R_FG", "FG", 0);
        AddInventory("FG", 12);
        var runId = AddRun(
            ("FG", 10, Today.AddDays(40)),   // LineSeq 1（晚）
            ("FG", 10, Today.AddDays(20)));  // LineSeq 2（早）→ 先處理
        await _context.SaveChangesAsync();

        var result = await _engine.RunAsync(runId);

        var lines = await _context.MrpRunLines.AsNoTracking().OrderBy(l => l.LineSeq).ToListAsync();
        var early = lines.Single(l => l.LineSeq == 2);
        var late = lines.Single(l => l.LineSeq == 1);

        Assert.Equal(10m, early.StockQty);     // 先到先取
        Assert.Equal(0m, early.MakeQty);
        Assert.Equal(2m, late.StockQty);       // 剩餘庫存
        Assert.Equal(8m, late.MakeQty);

        Assert.Equal(1, result.WorkOrderCount);
        Assert.Equal(8m, (await _context.WorkOrders.AsNoTracking().FirstAsync()).Quantity);

        // 預留已持久化，且可用量歸零（12 onHand − 12 reserved）
        var reserved = await _context.InventoryReservations.AsNoTracking()
            .Where(r => r.ProductCode == "FG" && r.Status == ReservationStatus.Active)
            .SumAsync(r => r.ReservedQty);
        Assert.Equal(12m, reserved);

        var available = await new InventoryReservationService(_context).GetPlantAvailableQtyAsync(PlantId, "FG");
        Assert.Equal(0m, available);
    }

    [Fact]
    public async Task Fstock_SafetyStock_NotConsumedByNetting()
    {
        // 庫存 12，安全庫存 3：淨算只能動用 12-3=9，需求 10 應留 1 缺口轉自製
        AddProduct("FG", "M", "R_FG", safetyStock: 3);
        AddRouting("R_FG", "FG", 0);
        AddInventory("FG", 12);
        var runId = AddRun(("FG", 10, Today.AddDays(20)));
        await _context.SaveChangesAsync();

        await _engine.RunAsync(runId);

        var line = await _context.MrpRunLines.AsNoTracking().SingleAsync();
        Assert.Equal(9m, line.StockQty);
        Assert.Equal(1m, line.MakeQty);
    }

    [Fact]
    public async Task Fstock_SafetyStock_ZeroBehavesAsBeforeUnaffected()
    {
        // 安全庫存 0（未設定）：行為應與既有測試一致，全額可用不受影響
        AddProduct("FG", "M", "R_FG", safetyStock: 0);
        AddRouting("R_FG", "FG", 0);
        AddInventory("FG", 12);
        var runId = AddRun(("FG", 10, Today.AddDays(20)));
        await _context.SaveChangesAsync();

        await _engine.RunAsync(runId);

        var line = await _context.MrpRunLines.AsNoTracking().SingleAsync();
        Assert.Equal(10m, line.StockQty);
        Assert.Equal(0m, line.MakeQty);
    }

    [Fact]
    public async Task InTransitPo_NotDoubleClaimedAcrossTwoDemandLinesInSameRun()
    {
        // 稽核修正：onHand=0，一張已核准 PO 僅 50 件在途；同一次展開內兩筆需求各要 50（合計 100）。
        // 舊行為（bug）：兩筆需求各自查到「全額 50」在途量，皆判定滿足，總計虛報滿足 100（實際僅 50）。
        // 修正後：先到先得（依交期排序），第一筆吃滿 50 件在途、第二筆只剩 0、須轉採購缺口 50。
        AddProduct("FG", "P");
        var due = Today.AddDays(30);
        AddApprovedPo("FG", 50m, DateOnly.FromDateTime(due.AddDays(-5)));
        var runId = AddRun(
            ("FG", 50m, due.AddDays(1)),   // LineSeq 1（晚交期，後處理）
            ("FG", 50m, due));             // LineSeq 2（早交期，先處理）
        await _context.SaveChangesAsync();

        var result = await _engine.RunAsync(runId);

        var lines = await _context.MrpRunLines.AsNoTracking().OrderBy(l => l.LineSeq).ToListAsync();
        var early = lines.Single(l => l.LineSeq == 2);
        var late = lines.Single(l => l.LineSeq == 1);

        Assert.Equal(50m, early.StockQty);      // 先到先取，吃滿在途 PO 全部 50 件
        Assert.Equal(0m, early.PurchaseQty);
        Assert.Equal(0m, late.StockQty);        // 在途已被前一筆認領完畢，這筆拿不到
        Assert.Equal(50m, late.PurchaseQty);    // 缺口轉採購需求，不會被誤判為已滿足

        Assert.Equal(50m, result.PurchaseDemands.Where(d => d.ProductCode == "FG").Sum(d => d.Qty));
    }

    [Fact]
    public async Task Fstock_MixedOnHandAndInTransit_OnlyReservesOnHandPortionAsOnHandCategory()
    {
        // onHand=3、在途 PO=10、需求=8：取用 3(on-hand)+5(在途) 滿足全部 8。
        // 只有 on-hand 取用的 3 應被記為 Category=OnHand 預留；在途取用的 5 純屬展開內認領追蹤，
        // 不應誤記為 on-hand 消耗（否則會污染 GetPlantAvailableQtyAsync 對後續需求列的 on-hand 判斷）。
        AddProduct("FG", "P");
        AddInventory("FG", 3m);
        var due = Today.AddDays(30);
        AddApprovedPo("FG", 10m, DateOnly.FromDateTime(due.AddDays(-5)));
        var runId = AddRun(("FG", 8m, due));
        await _context.SaveChangesAsync();

        await _engine.RunAsync(runId);

        var line = await _context.MrpRunLines.AsNoTracking().SingleAsync();
        Assert.Equal(8m, line.StockQty);     // 全數由 on-hand + 在途滿足
        Assert.Equal(0m, line.PurchaseQty);

        var reserved = await _context.InventoryReservations.AsNoTracking()
            .Where(r => r.ProductCode == "FG" && r.Status == ReservationStatus.Active
                     && r.Category == ReservationCategory.OnHand)
            .SumAsync(r => r.ReservedQty);
        Assert.Equal(3m, reserved);           // 只有 on-hand 取用部分入帳，非全部 8

        var available = await new InventoryReservationService(_context).GetPlantAvailableQtyAsync(PlantId, "FG");
        Assert.Equal(0m, available);          // 3 onHand − 3 reserved
    }

    [Fact]
    public async Task Fstock_SafetyStock_NotBreachedByOnHandWhenInTransitCoversRest()
    {
        // 稽核修正：安全庫存 80、onHand=100、在途 PO=200、需求=220。
        // 舊 bug：安全庫存只從「加總 pool」扣除，分配 on-hand 這一桶時卻仍以 available(100) 為上限，
        // 會把 on-hand 全數 100 吃光（因為在途 200 足以覆蓋剩餘需求），使實體在手庫存跌破安全庫存 80。
        // 修正後：on-hand 這一桶本身要先扣安全庫存，最多只能取 100-80=20，其餘 200 缺口全部改認領在途 PO。
        AddProduct("FG", "M", "R_FG", safetyStock: 80m);
        AddRouting("R_FG", "FG", 0);
        AddInventory("FG", 100m);
        var due = Today.AddDays(30);
        AddApprovedPo("FG", 200m, DateOnly.FromDateTime(due.AddDays(-5)));
        var runId = AddRun(("FG", 220m, due));
        await _context.SaveChangesAsync();

        await _engine.RunAsync(runId);

        var line = await _context.MrpRunLines.AsNoTracking().SingleAsync();
        Assert.Equal(220m, line.StockQty);   // on-hand(20) + 在途(200) 剛好滿足全部需求
        Assert.Equal(0m, line.MakeQty);      // 無缺口，不須轉自製

        var reserved = await _context.InventoryReservations.AsNoTracking()
            .Where(r => r.ProductCode == "FG" && r.Status == ReservationStatus.Active
                     && r.Category == ReservationCategory.OnHand)
            .SumAsync(r => r.ReservedQty);
        Assert.Equal(20m, reserved);         // 只認領 on-hand 20，不可吃光 100

        var available = await new InventoryReservationService(_context).GetPlantAvailableQtyAsync(PlantId, "FG");
        Assert.Equal(80m, available);        // 100 onHand − 20 reserved = 剛好維持在安全庫存水位
    }

    // ── 庫存模式（inv_simplified_mode）對在途調整的影響 ────────────────────────
    // 完整模式：核准≠過帳，QtyOnHand 不含 GR 待上架／GS 待下架，MrpEngine 必須補這段差額。
    // 簡化模式：核准即過帳，QtyOnHand 已即時反映，補差額會造成重複計算。

    /// <summary>建立一張已核准、已確認但尚未完成上架的進貨單（GR 待上架在途量）。</summary>
    private void AddPendingPutAwayGr(string product, decimal stockQty, decimal qtyPutAway = 0m)
    {
        var grId = $"PE-{Guid.NewGuid():N}"[..12];
        _context.GoodsReceipts.Add(new GoodsReceipt
        {
            GoodsReceiptId = grId, PlantId = PlantId, SupplierCode = "S1", CreatedBy = "tester",
            ApprovalStatus = DocumentApprovalStatus.Approved, Status = DocStatus.Confirmed,
            Lines = new List<GoodsReceiptLine>
            {
                new() { GoodsReceiptId = grId, LineNo = 1, ProductCode = product, StockQty = stockQty, QtyPutAway = qtyPutAway }
            }
        });
    }

    /// <summary>建立一張已核准、已確認但尚未完成下架的出貨單（GS 待下架在途量）。</summary>
    private void AddPendingPickGs(string product, decimal stockQty, decimal qtyPutAway = 0m)
    {
        var gsId = $"GS-{Guid.NewGuid():N}"[..12];
        _context.GoodsShipments.Add(new GoodsShipment
        {
            GoodsShipmentId = gsId, PlantId = PlantId, CompanyCode = "C1", CreatedBy = "tester",
            ApprovalStatus = DocumentApprovalStatus.Approved, Status = DocStatus.Confirmed,
            Lines = new List<GoodsShipmentLine>
            {
                new() { GoodsShipmentId = gsId, LineNo = 1, ProductCode = product, StockQty = stockQty, QtyPutAway = qtyPutAway }
            }
        });
    }

    [Fact]
    public async Task InTransit_FullMode_PendingPutAwayCountsAsSupply()
    {
        // 完整模式：onHand=0，一張已核准 GR 有 50 待上架 → QtyOnHand 尚未增加，
        // MrpEngine 需補上這 50 才不會誤發重複請購。
        AddProduct("RM", "P");
        AddPendingPutAwayGr("RM", 50m);
        var runId = AddRun(("RM", 50m, Today.AddDays(30)));
        await _context.SaveChangesAsync();

        var result = await _engine.RunAsync(runId);

        var line = await _context.MrpRunLines.AsNoTracking().SingleAsync();
        Assert.Equal(50m, line.StockQty);
        Assert.Equal(0m, line.PurchaseQty);
        Assert.Empty(result.PurchaseDemands.Where(d => d.ProductCode == "RM"));
    }

    [Fact]
    public async Task InTransit_SimplifiedMode_PendingPutAwayNotCounted()
    {
        // 簡化模式：GR 核准即過帳，QtyOnHand 已含該批量。此處 onHand=0 代表確實還沒有貨，
        // 若仍把 GR 待上架 50 算成供給，會虛報滿足、漏開請購。
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");
        AddProduct("RM", "P");
        AddPendingPutAwayGr("RM", 50m);
        var runId = AddRun(("RM", 50m, Today.AddDays(30)));
        await _context.SaveChangesAsync();

        var result = await _engine.RunAsync(runId);

        var line = await _context.MrpRunLines.AsNoTracking().SingleAsync();
        Assert.Equal(0m, line.StockQty);
        Assert.Equal(50m, line.PurchaseQty);
        Assert.Equal(50m, result.PurchaseDemands.Where(d => d.ProductCode == "RM").Sum(d => d.Qty));
    }

    [Fact]
    public async Task InTransit_FullMode_PendingPickDeductedFromSupply()
    {
        // 完整模式：onHand=100 但其中 30 已被已核准出貨單鎖定待下架（庫存尚未減少），
        // 可用量應為 70，需求 100 須有 30 缺口轉採購。
        AddProduct("RM", "P");
        AddInventory("RM", 100m);
        AddPendingPickGs("RM", 30m);
        var runId = AddRun(("RM", 100m, Today.AddDays(30)));
        await _context.SaveChangesAsync();

        await _engine.RunAsync(runId);

        var line = await _context.MrpRunLines.AsNoTracking().SingleAsync();
        Assert.Equal(70m, line.StockQty);
        Assert.Equal(30m, line.PurchaseQty);
    }

    [Fact]
    public async Task InTransit_SimplifiedMode_PendingPickNotDeducted()
    {
        // 簡化模式：出貨核准即過帳，onHand=100 已是扣除後的結果；
        // 若再扣一次待下架 30 等於重複扣減，會虛報缺口。
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");
        AddProduct("RM", "P");
        AddInventory("RM", 100m);
        AddPendingPickGs("RM", 30m);
        var runId = AddRun(("RM", 100m, Today.AddDays(30)));
        await _context.SaveChangesAsync();

        await _engine.RunAsync(runId);

        var line = await _context.MrpRunLines.AsNoTracking().SingleAsync();
        Assert.Equal(100m, line.StockQty);
        Assert.Equal(0m, line.PurchaseQty);
    }

    [Fact]
    public async Task InTransit_FullMode_PartiallyPutAwayGr_OnlyRemainderCounts()
    {
        // 已上架部分（QtyPutAway）已進 QtyOnHand，只有 StockQty − QtyPutAway 是尚未反映的在途量。
        AddProduct("RM", "P");
        AddInventory("RM", 20m);                    // 已上架的 20 件
        AddPendingPutAwayGr("RM", 50m, qtyPutAway: 20m);
        var runId = AddRun(("RM", 50m, Today.AddDays(30)));
        await _context.SaveChangesAsync();

        await _engine.RunAsync(runId);

        var line = await _context.MrpRunLines.AsNoTracking().SingleAsync();
        Assert.Equal(50m, line.StockQty);           // onHand 20 + 未上架 30
        Assert.Equal(0m, line.PurchaseQty);
    }

    [Fact]
    public async Task RunJ_OutsourceProduct_CreatesDispatchWithoutMachine()
    {
        // 委外成品：建立工單 + 單一委外工序/派工（無機台），並產生委外採購需求
        AddProduct("OEM", "S", "R_OEM");
        AddRouting("R_OEM", "OEM", 0, ("RM", 1));
        AddProduct("RM", "P", standardCost: 7m);
        var runId = AddRun(("OEM", 5, Today.AddDays(365)));
        await _context.SaveChangesAsync();

        var result = await _engine.RunAsync(runId);

        Assert.Equal(1, result.BatchCount);
        Assert.Equal(1, result.WorkOrderCount);
        var dispatch = await _context.Dispatches.AsNoTracking().SingleAsync();
        Assert.True(dispatch.IsOutsource);
        Assert.Null(dispatch.MachineCode);

        // 委外工單頭：IsOutsource=true、應交日有值（mfg-domain.md §19.2）
        var wo = await _context.WorkOrders.AsNoTracking().SingleAsync();
        Assert.True(wo.IsOutsource);
        Assert.NotNull(wo.OutsourceExpectedDeliveryDate);

        // 送外料 RM 的 UnitCost 快照自 Product.StandardCost（mfg-domain.md §16.2）
        var rmMat = await _context.WorkOrderMaterials.AsNoTracking().FirstAsync(m => m.ProductCode == "RM");
        Assert.Equal(7m, rmMat.UnitCost);

        // 整品委外(S) 改走委外建議通道（mfg-domain.md §4.1），並攜帶送外料 RM
        var osDemand = result.OutsourceDemands.Single(d => d.ProductCode == "OEM" && d.Reason.Contains("委外"));
        Assert.Contains(osDemand.Materials, m => m.ProductCode == "RM");

        var lineMode = (await _context.MrpRunLines.AsNoTracking().FirstAsync()).Mode;
        Assert.Equal(MrpMode.Outsource, lineMode);
    }

    [Fact]
    public async Task Run_RoutingOperationOutsourceFlag_IsIgnored_NoOutsourceDemandNoOutsourceDispatch()
    {
        // 途程工序委外已停用（mfg-domain.md §19.7）：途程上殘留的委外勾選當廠內工序展開，
        // 不得產生委外建議或委外派工——否則又回到「收回把成品記進庫存」的第三條路。
        AddProduct("FG", "M", "R_FG");
        AddOutsourceOpRouting("R_FG", "FG", ("RM", 2));
        AddProduct("RM", "P");
        var runId = AddRun(("FG", 3, Today.AddDays(365)));
        await _context.SaveChangesAsync();

        var result = await _engine.RunAsync(runId);

        Assert.DoesNotContain(result.OutsourceDemands, d => d.ProductCode == "FG");
        var wo = await _context.WorkOrders.SingleAsync(w => w.ProductCode == "FG");
        Assert.False(wo.IsOutsource);
        Assert.All(await _context.Dispatches.Where(d => d.WorkOrderId == wo.WorkOrderId).ToListAsync(),
                   d => Assert.False(d.IsOutsource));
        Assert.Contains(await _context.WorkOrderMaterials.ToListAsync(),
                        m => m.WorkOrderId == wo.WorkOrderId && m.ProductCode == "RM" && m.RequiredQty == 6m);
    }

    [Fact]
    public async Task Run_AlreadyExecuted_Throws()
    {
        AddProduct("FG", "P");
        var runId = AddRun(("FG", 1, Today.AddDays(30)));
        await _context.SaveChangesAsync();

        await _engine.RunAsync(runId);   // 第一次：Draft → Executed
        await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.RunAsync(runId));
    }

    [Fact]
    public async Task ReRun_NothingStarted_ClearsPriorAndRebuilds()
    {
        // 庫存 3、需求 5：首展 → 鎖定 3、自製 2（1 批 1 工單）。重跑應釋放舊預留並重建（不重複預留）。
        AddProduct("FG", "M", "R_FG");
        AddRouting("R_FG", "FG", 0);
        AddInventory("FG", 3);
        var runId = AddRun(("FG", 5, Today.AddDays(365)));
        await _context.SaveChangesAsync();

        await _engine.RunAsync(runId);
        var firstBatch = (await _context.ProductionBatches.AsNoTracking().SingleAsync()).BatchId;

        var result = await _engine.ReRunAsync(runId);

        // 舊批已刪、重建一張新批（採番續號）；工單數不累積
        var batches = await _context.ProductionBatches.AsNoTracking().ToListAsync();
        Assert.Single(batches);
        Assert.NotEqual(firstBatch, batches[0].BatchId);
        Assert.Equal(1, result.WorkOrderCount);
        Assert.Equal(1, await _context.WorkOrders.AsNoTracking().CountAsync());

        // 預留不重複：仍為 3（釋放舊 3 + 重留 3），可用量 0
        var reserved = await _context.InventoryReservations.AsNoTracking()
            .Where(r => r.ProductCode == "FG" && r.Status == ReservationStatus.Active)
            .SumAsync(r => r.ReservedQty);
        Assert.Equal(3m, reserved);
    }

    [Fact]
    public async Task ReRun_WorkOrderInProgress_ThrowsBlocked()
    {
        AddProduct("FG", "M", "R_FG");
        AddRouting("R_FG", "FG", 0);
        var runId = AddRun(("FG", 5, Today.AddDays(365)));
        await _context.SaveChangesAsync();
        await _engine.RunAsync(runId);

        // 模擬投產：將工單轉 InProgress
        var wo = await _context.WorkOrders.FirstAsync();
        wo.Status = WorkOrderStatus.InProgress;
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<MrpReRunBlockedException>(() => _engine.ReRunAsync(runId));
    }

    [Fact]
    public async Task ReRun_OutsourceDemandAlreadyTransferred_ThrowsFriendly()
    {
        // 委外需求已轉委外採購單＝對外承諾 → 重跑須被守門擋下（可讀訊息），
        // 而非刪除工單時炸 DbUpdateException（OutsourceDemand→WorkOrder 為 Restrict FK）。
        // 尚未轉單者不再阻擋（判準式守門），改由 ReRun 一併刪除重建，見
        // MrpReRunEvaluationTests.ReRun_WithUntransferredOutsourceDemand_DeletesItAndRebuilds。
        AddProduct("FG", "M", "R_FG");
        AddRouting("R_FG", "FG", 0);
        var runId = AddRun(("FG", 5, Today.AddDays(365)));
        await _context.SaveChangesAsync();
        await _engine.RunAsync(runId);

        var wo = await _context.WorkOrders.AsNoTracking().FirstAsync();
        _context.OutsourceDemands.Add(new OutsourceDemand
        {
            DemandId = "OD-0001", WorkOrderId = wo.WorkOrderId, ProductCode = "FG",
            DemandQty = 5, TransferredQty = 5, RequiredDate = DateOnly.FromDateTime(Today), PlantId = PlantId
        });
        await _context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.ReRunAsync(runId));
        PzAssert.ContainsMessage("Mfg_RerunOutsourceOrderedFmt", ex.Message);
    }

    [Fact]
    public async Task ReRun_DispatchHasSession_ThrowsFriendly()
    {
        // 派工已有上工紀錄（ProductionSession→Dispatch 為 Restrict FK）→ 守門擋下重跑
        AddProduct("FG", "M", "R_FG");
        AddRouting("R_FG", "FG", 0);
        var runId = AddRun(("FG", 5, Today.AddDays(365)));
        await _context.SaveChangesAsync();
        await _engine.RunAsync(runId);

        var dispatch = await _context.Dispatches.AsNoTracking().FirstAsync();
        _context.ProductionSessions.Add(new ProductionSession
        {
            SessionId = "PS-0001", DispatchId = dispatch.DispatchId,
            WorkOrderId = dispatch.WorkOrderId, StartAt = Today, PlantId = PlantId
        });
        await _context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.ReRunAsync(runId));
        PzAssert.ContainsMessage("Mfg_RerunDispatchStartedFmt", ex.Message);
    }

    [Fact]
    public async Task ReRun_NotExecuted_Throws()
    {
        AddProduct("FG", "M", "R_FG");
        AddRouting("R_FG", "FG", 0);
        var runId = AddRun(("FG", 1, Today.AddDays(365)));
        await _context.SaveChangesAsync();   // 仍為 Draft

        await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.ReRunAsync(runId));
    }

    [Fact]
    public async Task RunMps_WithCalendar_PerOperationBackward_IntermediateIssueFloatsAfterWoStart()
    {
        // FG(自製,2 工序)：op10 用 RM(採購)、op20(後段) 用 SA(自製)。
        // 驗證逐工序倒推 + 庫出浮動：SA 由後段工序消耗 → SA 交期 = op20 計畫開工日，
        // 晚於 FG 工單投產日（op10 開工日），不再塌縮到工單頭（mfg-domain.md §5.1）。
        AddProduct("FG", "M", "R_FG");
        AddProduct("SA", "M", "R_SA");
        AddProduct("RM", "P");
        AddProduct("SARM", "P");

        _context.Routings.Add(new Routing
        {
            RoutingId = "R_FG", ProductCode = "FG", PlantId = PlantId,
            BatchSize = 1, BufferDays = 0, IsActive = true,
            Operations = new List<RoutingOperation>
            {
                new() { RoutingId = "R_FG", OperationSeq = 10, OperationName = "OP10", WorkCenterId = "WC1",
                        TM_SETUP = 0, TM_UNIT = 10, UomCode = "PC",
                        Boms = { new RoutingBom { RoutingId = "R_FG", OperationSeq = 10, BomSeq = 1, ProductCode = "RM", Quantity = 1, UomCode = "PC" } } },
                new() { RoutingId = "R_FG", OperationSeq = 20, OperationName = "OP20", WorkCenterId = "WC2",
                        TM_SETUP = 0, TM_UNIT = 30, UomCode = "PC",
                        Boms = { new RoutingBom { RoutingId = "R_FG", OperationSeq = 20, BomSeq = 1, ProductCode = "SA", Quantity = 1, UomCode = "PC" } } },
            }
        });
        AddRouting("R_SA", "SA", 0, ("SARM", 1m));

        var due = new DateTime(2026, 6, 1, 17, 0, 0, DateTimeKind.Utc);
        var runId = AddRun(("FG", 10m, due));
        await _context.SaveChangesAsync();

        var engine = new MrpEngine(_context, new InventoryReservationService(_context),
            _mockDocNo.Object, _mockDt.Object, _mockSysParam.Object, Mock.Of<IWorkOrderReleaseService>(), new FakeDailyCalendar());
        await engine.RunAsync(runId);

        var fgWo = await _context.WorkOrders.AsNoTracking().FirstAsync(w => w.ProductCode == "FG");
        var saWo = await _context.WorkOrders.AsNoTracking().FirstAsync(w => w.ProductCode == "SA");
        var op10 = await _context.Dispatches.AsNoTracking().FirstAsync(d => d.WorkOrderId == fgWo.WorkOrderId && d.OperationSeq == 10);
        var op20 = await _context.Dispatches.AsNoTracking().FirstAsync(d => d.WorkOrderId == fgWo.WorkOrderId && d.OperationSeq == 20);

        // 工序級計畫日寫入，且後段 op20 晚於 op10（逐工序倒推）
        Assert.NotNull(op10.PlannedStartDate);
        Assert.NotNull(op20.PlannedStartDate);
        Assert.True(op10.PlannedStartDate < op20.PlannedStartDate);

        // 庫出浮動：SA 交期(PlannedEndDate) = 消耗它的 op20 計畫開工日，晚於 FG 工單投產日
        Assert.Equal(op20.PlannedStartDate!.Value, saWo.PlannedEndDate);
        Assert.True(saWo.PlannedEndDate > fgWo.PlannedStartDate);

        // FG 工單投產日 = 首序 op10 計畫開工日（buffer = 0）
        Assert.Equal(op10.PlannedStartDate!.Value, fgWo.PlannedStartDate);
    }
}

/// <summary>測試用工作日曆：每日 09:00–17:00（540–1020 分）固定時段，無假日。</summary>
internal sealed class FakeDailyCalendar : IWorkCalendarService
{
    private static readonly IReadOnlyList<(int Start, int End)> Day = new[] { (540, 1020) };
    public Task<decimal> GetWorkHoursAsync(string? machineCode, string? workCenterId, string? plantId, DateOnly date)
        => Task.FromResult(8m);
    public Task<IReadOnlyList<(int Start, int End)>> GetSegmentsAsync(
        string? machineCode, string? workCenterId, string? plantId, DateOnly date)
        => Task.FromResult(Day);
}
