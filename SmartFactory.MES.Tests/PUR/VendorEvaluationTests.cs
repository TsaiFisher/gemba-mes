using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Controllers;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>供應商評分卡計算純函式測試（pur-domain.md §6.2）。</summary>
public class VendorEvaluationCalculatorTests
{
    [Fact]
    public void PeriodOf_季度與年度期間()
    {
        Assert.Equal((new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30)),
            VendorEvaluationCalculator.PeriodOf(2026, 2));
        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)),
            VendorEvaluationCalculator.PeriodOf(2026, null));
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31)),
            VendorEvaluationCalculator.PeriodOf(2026, 4));
    }

    [Fact]
    public void QualityScore_退件率公式與邊界()
    {
        Assert.Equal(95m,  VendorEvaluationCalculator.QualityScore(100, 5));
        Assert.Equal(100m, VendorEvaluationCalculator.QualityScore(100, 0));
        Assert.Equal(0m,   VendorEvaluationCalculator.QualityScore(100, 150)); // 不良超過進貨 → 下限 0
        Assert.Null(VendorEvaluationCalculator.QualityScore(0, 0));            // 無進貨 → 無資料
    }

    [Fact]
    public void DeliveryScore_OTD公式與邊界()
    {
        Assert.Equal(80m, VendorEvaluationCalculator.DeliveryScore(10, 8));
        Assert.Null(VendorEvaluationCalculator.DeliveryScore(0, 0));
    }

    [Fact]
    public void Total_加權平均與權重總和為零退回預設()
    {
        // 預設權重 30/30/20/20
        Assert.Equal(74m, VendorEvaluationCalculator.Total(SrmWeights.Default, 100, 80, 60, 40));
        // 自訂權重
        Assert.Equal(20m, VendorEvaluationCalculator.Total(new SrmWeights(10, 10, 40, 40), 100, 100, 0, 0));
        // 權重全 0 → 退回預設
        Assert.Equal(74m, VendorEvaluationCalculator.Total(new SrmWeights(0, 0, 0, 0), 100, 80, 60, 40));
    }

    [Fact]
    public void ResultOf_與_StatusOf_門檻映射()
    {
        Assert.Equal(EvaluationResult.Pass,        VendorEvaluationCalculator.ResultOf(80m));
        Assert.Equal(EvaluationResult.Conditional, VendorEvaluationCalculator.ResultOf(79.99m));
        Assert.Equal(EvaluationResult.Conditional, VendorEvaluationCalculator.ResultOf(60m));
        Assert.Equal(EvaluationResult.Fail,        VendorEvaluationCalculator.ResultOf(59.99m));

        Assert.Equal(SupplierQualificationStatus.Qualified, VendorEvaluationCalculator.StatusOf(EvaluationResult.Pass));
        Assert.Equal(SupplierQualificationStatus.Probation, VendorEvaluationCalculator.StatusOf(EvaluationResult.Conditional));
        Assert.Equal(SupplierQualificationStatus.Suspended, VendorEvaluationCalculator.StatusOf(EvaluationResult.Fail));
    }

    [Fact]
    public void 總分_無資料的項目不計入加權_權重在剩下的重新分配()
    {
        var w = new SrmWeights(30, 30, 20, 20);

        // 品質無資料：分母剩 30+20+20=70 → (80*30 + 60*20 + 40*20)/70
        Assert.Equal(62.86m, VendorEvaluationCalculator.Total(w, null, 80m, 60m, 40m));

        // 兩個自動項都無資料：只剩人工項 → (60*20+40*20)/40 = 50
        Assert.Equal(50m, VendorEvaluationCalculator.Total(w, null, null, 60m, 40m));

        // 都有資料時與舊行為一致：(90*30 + 80*30 + 60*20 + 40*20)/100
        Assert.Equal(71m, VendorEvaluationCalculator.Total(w, 90m, 80m, 60m, 40m));
    }

    [Fact]
    public void 總分_無資料不等於滿分()
    {
        var w = new SrmWeights(30, 30, 20, 20);

        // 這是本次修正的核心：舊版把 null 當 100 分，於是這兩種情況分數完全一樣——
        // 一個是「沒有往來」，另一個是「零不良、全準時」。
        var noData  = VendorEvaluationCalculator.Total(w, null, null, 50m, 50m);
        var perfect = VendorEvaluationCalculator.Total(w, 100m, 100m, 50m, 50m);

        Assert.NotEqual(noData, perfect);
        Assert.Equal(50m, noData);
        Assert.Equal(80m, perfect);
    }

    [Fact]
    public void 總分_權重全落在無資料項目時回零而不是除以零()
    {
        // 只設了品質與交期權重，而兩者都無資料：沒有任何可用依據。
        var w = new SrmWeights(50, 50, 0, 0);

        Assert.Equal(0m, VendorEvaluationCalculator.Total(w, null, null, 100m, 100m));
    }
}

/// <summary>
/// 供應商評分卡整合測試：自動分數（NCR/OTD 跨廠彙總）、Save 後端重算、
/// 期間重複檢查、送簽核准回寫 Company.QualificationStatus、非草稿保護。
/// </summary>
public class VendorEvaluationControllerTests : TestBase
{
    private readonly Mock<IDateTimeService>    _mockDt       = new();
    private readonly Mock<ISystemParamService> _mockParam    = new();
    private readonly Mock<IApprovalService>    _mockApproval = new();

    private const string PlantId = "P001";

    public VendorEvaluationControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockDt.Setup(d => d.Now).Returns(DateTime.Today);
        _mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(DateOnly.FromDateTime(DateTime.Today));
        _mockParam.Setup(p => p.GetAsync(It.IsAny<string>())).ReturnsAsync((string?)null); // 預設權重
        _context.Companies.Add(new Company
        {
            CompanyCode = "S1", CompanyName = "供應商一", IsSupplier = true, IsActive = true,
        });
        _context.SaveChanges();
    }

    private VendorEvaluationScoreService BuildScoreService() =>
        new(_context, new OtdFactService(_context), _mockParam.Object, _mockDt.Object);

    private VendorEvaluationController BuildController()
    {
        var ctrl = new VendorEvaluationController(
            BuildScoreService(), GetMockExportService().Object, _mockDt.Object,
            _mockApproval.Object, GetMockLocalizer<ProjectZSharedResource>().Object);
        ctrl.ControllerContext = GetControllerContextWithServices(userId: "U1");
        return ctrl;
    }

    // ── 自動分數 ─────────────────────────────────────────────────

    private void SeedQualityData()
    {
        // P001 廠進貨 100（過帳）＋ P002 廠進貨 100（過帳，跨廠彙總）
        var gr1 = new GoodsReceipt
        {
            GoodsReceiptId = "GR1", SupplierCode = "S1", PlantId = PlantId,
            ReceiptDate = new DateOnly(2026, 2, 10), Status = DocStatus.Posted,
        };
        gr1.Lines.Add(new GoodsReceiptLine { GoodsReceiptId = "GR1", LineNo = 1, ProductCode = "PX", ReceiptQty = 100 });
        var gr2 = new GoodsReceipt
        {
            GoodsReceiptId = "GR2", SupplierCode = "S1", PlantId = "P002",
            ReceiptDate = new DateOnly(2026, 3, 5), Status = DocStatus.Posted,
        };
        gr2.Lines.Add(new GoodsReceiptLine { GoodsReceiptId = "GR2", LineNo = 1, ProductCode = "PX", ReceiptQty = 100 });
        // 草稿進貨不計
        var gr3 = new GoodsReceipt
        {
            GoodsReceiptId = "GR3", SupplierCode = "S1", PlantId = PlantId,
            ReceiptDate = new DateOnly(2026, 2, 20), Status = DocStatus.Draft,
        };
        gr3.Lines.Add(new GoodsReceiptLine { GoodsReceiptId = "GR3", LineNo = 1, ProductCode = "PX", ReceiptQty = 999 });
        _context.GoodsReceipts.AddRange(gr1, gr2, gr3);

        // NCR：GR1 不良 10（計入）、作廢 NCR 不計、非進貨來源不計
        _context.NonConformanceReports.AddRange(
            new NonConformanceReport
            {
                NcrId = "NCR1", SourceType = "GoodsReceipt", SourceId = "GR1",
                ProductCode = "PX", Qty = 10, PlantId = PlantId, CreatedBy = "U1",
            },
            new NonConformanceReport
            {
                NcrId = "NCR2", SourceType = "GoodsReceipt", SourceId = "GR1",
                ProductCode = "PX", Qty = 50, PlantId = PlantId, CreatedBy = "U1",
                NcrStatus = SmartFactory.MES.Models.NcrStatus.Void,
            },
            new NonConformanceReport
            {
                NcrId = "NCR3", SourceType = "ProductionReport", SourceId = "RPT1",
                ProductCode = "PX", Qty = 7, PlantId = PlantId, CreatedBy = "U1",
            });
        _context.SaveChanges();
    }

    [Fact]
    public async Task 品質分數_NCR不良量_跨廠彙總_排除作廢與非進貨來源()
    {
        SeedQualityData();
        var basis = await BuildScoreService().ComputeAsync("S1", 2026, null);

        Assert.Equal(200m, basis.ReceiptQty);         // GR1+GR2（跨廠），草稿 GR3 不計
        Assert.Equal(10m,  basis.NgQty);              // 僅 NCR1
        Assert.Equal(95m,  basis.QualityScore);       // (1-10/200)*100
    }

    [Fact]
    public async Task 品質分數_納入未開NCR的IQC判退量()
    {
        SeedQualityData();

        // GR2 驗退 6 件但沒開 NCR——2026-09-21 之前這 6 件完全不計，品質仍是 95 分。
        // 開不開 NCR 是人工判斷，小額、當場退掉的不良往往不會留下 NCR。
        _context.InspectionOrders.Add(new SmartFactory.MES.Models.InspectionOrder
        {
            InspectionOrderId = "IQC-2", InspectionType = "IQC", SourceDocId = "GR2",
            ProductCode = "PX", LotSize = 100, SampleSize = 8, ActualSampleQty = 8,
            FailQty = 6, Status = "Fail", PlantId = "P002",
        });
        _context.SaveChanges();

        var basis = await BuildScoreService().ComputeAsync("S1", 2026, null);

        Assert.Equal(200m, basis.ReceiptQty);
        Assert.Equal(16m,  basis.NgQty);              // NCR1 的 10 ＋ IQC-2 的 6
        Assert.Equal(92m,  basis.QualityScore);       // (1-16/200)*100
    }

    [Fact]
    public async Task 品質分數_同一張進貨已開NCR時不重複計算檢驗判退()
    {
        SeedQualityData();

        // GR1 已有 NCR1（10 件）。IQC 判退 → 開 NCR 是常見流程，兩邊都會留紀錄，
        // 直接相加會把同一批不良算兩次、品質分數被雙重扣分。
        _context.InspectionOrders.Add(new SmartFactory.MES.Models.InspectionOrder
        {
            InspectionOrderId = "IQC-1", InspectionType = "IQC", SourceDocId = "GR1",
            ProductCode = "PX", LotSize = 100, SampleSize = 8, ActualSampleQty = 8,
            FailQty = 10, Status = "Fail", PlantId = PlantId,
        });
        _context.SaveChanges();

        var basis = await BuildScoreService().ComputeAsync("S1", 2026, null);

        Assert.Equal(10m, basis.NgQty);               // 仍是 10，不是 20
        Assert.Equal(95m, basis.QualityScore);
    }

    [Fact]
    public async Task 品質分數_檢驗合格不計入不良量()
    {
        SeedQualityData();

        _context.InspectionOrders.Add(new SmartFactory.MES.Models.InspectionOrder
        {
            InspectionOrderId = "IQC-PASS", InspectionType = "IQC", SourceDocId = "GR2",
            ProductCode = "PX", LotSize = 100, SampleSize = 8, ActualSampleQty = 8,
            FailQty = 0, Status = "Pass", PlantId = "P002",
        });
        _context.SaveChanges();

        var basis = await BuildScoreService().ComputeAsync("S1", 2026, null);

        Assert.Equal(10m, basis.NgQty);
    }

    [Fact]
    public async Task 交期分數_PO與委外雙來源()
    {
        // PO 來源：過帳進貨對 POL 預交日（逾期）
        var po = new PurchaseOrder
        {
            PurchaseOrderId = "PO1", SupplierCode = "S1", PlantId = PlantId,
            OrderDate = new DateOnly(2026, 1, 5), ApprovalStatus = DocumentApprovalStatus.Approved,
        };
        po.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO1", LineNo = 1, ProductCode = "PX",
            OrderQty = 10, DeliveryDate = new DateOnly(2026, 1, 10),
        });
        _context.PurchaseOrders.Add(po);
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = "GR1", SupplierCode = "S1", PlantId = PlantId,
            ReceiptDate = new DateOnly(2026, 1, 15), Status = DocStatus.Posted, // 逾期 5 天
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = "GR1", LineNo = 1, ProductCode = "PX",
            PurchaseOrderId = "PO1", PoLineNo = 1, ReceiptQty = 10,
        });
        _context.GoodsReceipts.Add(gr);

        // 委外來源：已確認收貨對 OPO 表頭預交日（準時）。
        // 新口徑要判斷「交清了沒」，所以訂購量與收貨量都得有明細行；
        // Status 也必須明給——OutsourcePoStatus 預設是 Draft，草稿委外單不計入母體。
        _context.OutsourcePurchaseOrders.Add(new OutsourcePurchaseOrder
        {
            OutsourcePurchaseOrderId = "OPO1", SupplierCode = "S1", PlantId = PlantId,
            OrderDate = new DateOnly(2026, 1, 5), ExpectedDeliveryDate = new DateOnly(2026, 1, 20),
            Status = OutsourcePoStatus.Confirmed,
        });
        _context.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
        {
            OutsourcePurchaseOrderId = "OPO1", LineNo = 1, ProductCode = "PX", Qty = 10,
        });
        _context.OutsourceReceipts.Add(new OutsourceReceipt
        {
            OutsourceReceiptId = "OR1", OutsourcePurchaseOrderId = "OPO1", SupplierCode = "S1",
            PlantId = PlantId, ReceiptDate = new DateOnly(2026, 1, 18), Status = DocStatus.Confirmed,
        });
        _context.OutsourceReceiptLines.Add(new OutsourceReceiptLine
        {
            OutsourceReceiptId = "OR1", LineNo = 1, ProductCode = "PX", ReceiptQty = 10,
        });
        _context.SaveChanges();

        var basis = await BuildScoreService().ComputeAsync("S1", 2026, 1);

        Assert.Equal(2, basis.OtdTotalLines);
        Assert.Equal(1, basis.OtdOnTimeLines);
        Assert.Equal(50m, basis.DeliveryScore);
        Assert.Equal(10m, basis.ReceiptQty);    // 交期測試中的過帳進貨也構成品質分母
    }

    // ── Save：後端重算＋Fail 改善措施＋期間重複 ─────────────────────

    [Fact]
    public async Task Save_無資料期間_自動項不計入加權()
    {
        // 2026-09-21 行為變更。舊版把「期間無進貨／無到料」當成 100 分帶進加權，
        // 於是這個案例拿到 (100*30+100*30+10*20+10*20)/100 = 64 分、判定 Conditional——
        // 一個從沒往來的供應商，只因為沒有資料就贏過真正拿 10 分的人工評價。
        //
        // 新版把算不出來的項目排除、權重在剩下的重新分配：(10*20+10*20)/(20+20) = 10，
        // 落到 Fail，因此必須填後續處理（那正是這種案子該走的流程）。
        var ctrl = BuildController();
        var result = await ctrl.Save(new VendorEvaluation
        {
            SupplierCode = "S1", EvaluationYear = 2026, EvaluationQuarter = 1,
            EvaluationDate = new DateOnly(2026, 4, 1),
            PriceScore = 10, ServiceScore = 10,
            FollowUpAction = "首次往來，待實際交易後重評",
        });

        Assert.IsType<ContentResult>(result);   // 成功回 toast script
        var saved = Assert.Single(_context.VendorEvaluations.ToList());

        // 分數欄仍存 100 當佔位（欄位是非可空的），但旗標才是判讀依據
        Assert.Equal(100m, saved.QualityScore);
        Assert.Equal(100m, saved.DeliveryScore);
        Assert.False(saved.QualityHasData);
        Assert.False(saved.DeliveryHasData);

        Assert.Equal(10m, saved.TotalScore);
        Assert.Equal(EvaluationResult.Fail, saved.ResultStatus);
        Assert.Equal("U1", saved.Evaluator);
    }

    [Fact]
    public async Task Save_不合格未填改善措施_擋下()
    {
        // 權重改為價格/服務為主 → 低分致 Fail
        _mockParam.Setup(p => p.GetAsync(ProjectZSystemParamKeys.SrmWeightQuality)).ReturnsAsync("10");
        _mockParam.Setup(p => p.GetAsync(ProjectZSystemParamKeys.SrmWeightDelivery)).ReturnsAsync("10");
        _mockParam.Setup(p => p.GetAsync(ProjectZSystemParamKeys.SrmWeightPrice)).ReturnsAsync("40");
        _mockParam.Setup(p => p.GetAsync(ProjectZSystemParamKeys.SrmWeightService)).ReturnsAsync("40");

        var ctrl = BuildController();
        var vm = new VendorEvaluation
        {
            SupplierCode = "S1", EvaluationYear = 2026, EvaluationQuarter = 1,
            EvaluationDate = new DateOnly(2026, 4, 1),
            PriceScore = 0, ServiceScore = 0,   // 總分 20 → Fail
        };

        var result = await ctrl.Save(vm);

        Assert.IsType<PartialViewResult>(result);           // 驗證失敗回表單
        Assert.False(ctrl.ModelState.IsValid);
        Assert.Empty(_context.VendorEvaluations.ToList());

        // 補上改善措施後可存，結果 Fail
        vm.FollowUpAction = "要求供應商提出品質改善計畫";
        ctrl.ModelState.Clear();
        var retry = await ctrl.Save(vm);
        Assert.IsType<ContentResult>(retry);
        Assert.Equal(EvaluationResult.Fail, Assert.Single(_context.VendorEvaluations.ToList()).ResultStatus);
    }

    [Fact]
    public async Task Save_同廠商同期間重複_擋下()
    {
        _context.VendorEvaluations.Add(new VendorEvaluation
        {
            SupplierCode = "S1", EvaluationYear = 2026, EvaluationQuarter = null,
            EvaluationDate = new DateOnly(2026, 12, 31), Evaluator = "U1", CreatedBy = "U1",
        });
        _context.SaveChanges();

        var ctrl = BuildController();
        var result = await ctrl.Save(new VendorEvaluation
        {
            SupplierCode = "S1", EvaluationYear = 2026, EvaluationQuarter = null,
            EvaluationDate = new DateOnly(2026, 12, 31),
            PriceScore = 80, ServiceScore = 80,
        });

        Assert.IsType<PartialViewResult>(result);
        Assert.Single(_context.VendorEvaluations.ToList());
    }

    // ── 送簽與資格狀態連動 ───────────────────────────────────────

    private VendorEvaluation SeedDraftEval(EvaluationResult result, decimal total)
    {
        var eval = new VendorEvaluation
        {
            SupplierCode = "S1", EvaluationYear = 2026, EvaluationQuarter = 2,
            EvaluationDate = new DateOnly(2026, 7, 1), Evaluator = "U1", CreatedBy = "U1",
            TotalScore = total, ResultStatus = result,
            FollowUpAction = result == EvaluationResult.Fail ? "改善計畫" : null,
        };
        _context.VendorEvaluations.Add(eval);
        _context.SaveChanges();
        return eval;
    }

    [Fact]
    public async Task Submit_無簽核流程_直接核准並回寫資格狀態()
    {
        var eval = SeedDraftEval(EvaluationResult.Fail, 40);
        _mockApproval.Setup(a => a.GetApprovalConfig("VendorEvaluation", null))
            .ReturnsAsync((DocumentApprovalConfig?)null);

        var result = await BuildController().Submit(eval.Id);

        Assert.IsType<JsonResult>(result);
        Assert.Equal(DocumentApprovalStatus.Approved, eval.ApprovalStatus);
        var company = _context.Companies.Single(c => c.CompanyCode == "S1");
        Assert.Equal(SupplierQualificationStatus.Suspended, company.QualificationStatus);
    }

    [Fact]
    public async Task Submit_有簽核流程_轉待審不回寫()
    {
        var eval = SeedDraftEval(EvaluationResult.Pass, 90);
        _mockApproval.Setup(a => a.GetApprovalConfig("VendorEvaluation", null))
            .ReturnsAsync(new DocumentApprovalConfig { ApprovalTemplateId = "T1" });
        _mockApproval.Setup(a => a.SubmitForApproval(eval.Id.ToString(), "VendorEvaluation", "T1", "U1"))
            .ReturnsAsync(new ApprovalRequest { Id = "AR1" });

        await BuildController().Submit(eval.Id);

        Assert.Equal(DocumentApprovalStatus.PendingApproval, eval.ApprovalStatus);
        Assert.Equal("AR1", eval.ApprovalRequestId);
        Assert.Equal(SupplierQualificationStatus.Unqualified,
            _context.Companies.Single(c => c.CompanyCode == "S1").QualificationStatus);
    }

    [Fact]
    public async Task 簽核同步Contributor_核准時回寫資格狀態()
    {
        var eval = SeedDraftEval(EvaluationResult.Conditional, 70);
        eval.ApprovalStatus = DocumentApprovalStatus.PendingApproval;
        eval.ApprovalRequestId = "AR1";
        _context.SaveChanges();

        var sync = new VendorEvaluationApprovalSync();
        await sync.SyncAsync(_context, eval.Id.ToString(), ApprovalStatus.Approved);
        await _context.SaveChangesAsync();

        Assert.Equal(DocumentApprovalStatus.Approved, eval.ApprovalStatus);
        Assert.Equal(SupplierQualificationStatus.Probation,
            _context.Companies.Single(c => c.CompanyCode == "S1").QualificationStatus);
    }

    [Fact]
    public async Task Delete_非草稿_擋下()
    {
        var eval = SeedDraftEval(EvaluationResult.Pass, 90);
        eval.ApprovalStatus = DocumentApprovalStatus.Approved;
        _context.SaveChanges();

        var result = await BuildController().Delete(eval.Id);

        var json = Assert.IsType<JsonResult>(result);
        Assert.Contains("false", System.Text.Json.JsonSerializer.Serialize(json.Value));
        Assert.Single(_context.VendorEvaluations.ToList());
    }
}
