using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 重跑逐品號對帳（§10-5 P1）。核心案例＝品號由「採購」改為「自製」後重跑：
/// 既有已核准請購單不可整張覆寫，但該行必須被指認為 ModeChanged 並可結案退場，
/// 而不是像舊行為那樣整張跳過、只留一句當次網頁回應的警示。
/// </summary>
public class MrpPurchaseReconciliationTests : TestBase
{
    private const string PlantId = "P001";
    private const string UserId  = "user1";
    private static readonly DateTime Today = new(2026, 5, 25, 8, 0, 0, DateTimeKind.Utc);
    private const string RunId = "MRP-0001";

    private readonly Mock<IUserContext> _mockUser = new();
    private readonly Mock<IDateTimeService> _mockDt = new();
    private readonly Mock<IPlantParamService> _mockSysParam = new();
    private readonly Mock<IUomService> _mockUom = new();
    private readonly Mock<ICurrencyService> _mockCurrency = new();

    private int _prSeq;
    private int _batchSeq;

    private MrpRunController BuildController()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns(UserId);
        _mockDt.Setup(d => d.Now).Returns(Today);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("false");

        _mockUom.Setup(u => u.RoundQtyAsync(It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? _, decimal qty, CancellationToken __) => qty);
        _mockUom.Setup(u => u.ConvertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string __, string ___, decimal qty, CancellationToken ____) => qty);

        var docNo = new Mock<IDocNumberService>();
        docNo.Setup(d => d.NextAsync(MrpEngine.BatchDocType, It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => $"PB-{++_batchSeq:D4}");
        docNo.Setup(d => d.NextAsync("PurchaseRequest", It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => $"PR-{++_prSeq:D4}");

        var reservations = new InventoryReservationService(_context);
        var engine = new MrpEngine(_context, reservations, docNo.Object, _mockDt.Object, _mockSysParam.Object, Mock.Of<IWorkOrderReleaseService>());
        var purchaseService = new SmartFactory.MES.Services.PurchaseService(
            _context, docNo.Object, _mockDt.Object, _mockUom.Object, _mockCurrency.Object, reservations);

        var execution = new MrpRunExecutionService(
            _context, engine, purchaseService, _mockSysParam.Object, _mockUser.Object, _mockDt.Object);
        var ctrl = new MrpRunController(
            GetMockLocalizer<ProjectZSharedResource>().Object, engine, docNo.Object, execution);
        ctrl.ControllerContext = GetControllerContextWithServices(
            userId: UserId, userContext: _mockUser.Object,
            sharedLocalizer: GetMockLocalizer<ProjectZSharedResource>().Object);
        return ctrl;
    }

    private void EnableAutoClose()
        => _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgMrpAutoCloseObsoletePr, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("true");

    /// <summary>PA2103 起初為採購件（無途程），需求列 100。</summary>
    private void SeedPurchasedProduct()
    {
        _context.Products.Add(new Product
        {
            ProductCode = "PA2103", ProductName = "PA2103", StockUom = "PC", MrpMode = "P"
        });
        var run = new MrpRun { MrpRunId = RunId, PlantId = PlantId, Status = MrpRunStatus.Draft };
        run.Lines.Add(new MrpRunLine
        {
            MrpRunId = RunId, LineSeq = 1, ProductCode = "PA2103",
            RequiredQty = 100m, RequiredDate = Today.AddDays(30)
        });
        _context.MrpRuns.Add(run);
        _context.SaveChanges();
    }

    /// <summary>把 PA2103 改為自製（補途程），模擬使用者更正主檔。</summary>
    private async Task SwitchToMakeAsync()
    {
        _context.Routings.Add(new Routing
        {
            RoutingId = "R_PA", ProductCode = "PA2103", PlantId = PlantId,
            BatchSize = 1, BufferDays = 0, IsActive = true,
            Operations = new List<RoutingOperation>
            {
                new()
                {
                    RoutingId = "R_PA", OperationSeq = 10, OperationName = "OP", WorkCenterId = "WC1",
                    TM_SETUP = 0, TM_UNIT = 0, CD_BOR = OperationMode.WholeOrder, QT_BOR = 1, UomCode = "PC"
                }
            }
        });
        var p = await _context.Products.FirstAsync(x => x.ProductCode == "PA2103");
        p.MrpMode = "M";
        p.RoutingId = "R_PA";
        await _context.SaveChangesAsync();
    }

    private async Task ApproveExistingPrAsync(decimal orderedQty = 0m)
    {
        var pr = await _context.PurchaseRequests.Include(p => p.Lines)
            .SingleAsync(p => p.SourceMrpRunId == RunId);
        pr.ApprovalStatus = DocumentApprovalStatus.Approved;
        if (orderedQty > 0) pr.Lines.First().OrderedQty = orderedQty;
        await _context.SaveChangesAsync();
    }

    // ── 行級來源指紋 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FirstRun_StampsLineLevelSourceMrpRunId()
    {
        SeedPurchasedProduct();
        var ctrl = BuildController();

        await ctrl.Edit(RunId, new MrpRun { MrpRunId = RunId }, "", doExecute: "1");

        var line = await _context.PurchaseRequestLines.AsNoTracking().SingleAsync();
        Assert.Equal(RunId, line.SourceMrpRunId);
    }

    // ── 核心案例：採購 → 自製 ────────────────────────────────────────────────

    [Fact]
    public async Task ReRun_AfterModeChangedToMake_ReportsModeChangedVarianceOnApprovedPr()
    {
        SeedPurchasedProduct();
        var ctrl = BuildController();
        await ctrl.Edit(RunId, new MrpRun { MrpRunId = RunId }, "", doExecute: "1");
        await ApproveExistingPrAsync();          // 請購單已核准、未轉採購
        await SwitchToMakeAsync();               // 品號改自製

        var result = await GetResultAsync(ctrl.ReRun(RunId));

        var v = Assert.Single(result.PurchaseVariances);
        Assert.Equal(MrpPurchaseVarianceKind.ModeChanged, v.Kind);
        Assert.Equal("PA2103", v.ProductCode);
        Assert.Equal(100m, v.PreviousQty);
        Assert.Equal(0m, v.CurrentQty);
        // 未開自動結案 → 標為可結案退場，且訊息點出接手的工單
        Assert.Equal(MrpPurchaseVarianceDisposition.PendingClose, v.Disposition);
        var woId = (await _context.WorkOrders.AsNoTracking().FirstAsync()).WorkOrderId;
        Assert.Contains(woId, v.Description);
    }

    [Fact]
    public async Task ReRun_AfterModeChanged_WithAutoCloseEnabled_ClosesObsoleteLine()
    {
        SeedPurchasedProduct();
        EnableAutoClose();
        var ctrl = BuildController();
        await ctrl.Edit(RunId, new MrpRun { MrpRunId = RunId }, "", doExecute: "1");
        await ApproveExistingPrAsync();
        await SwitchToMakeAsync();

        var result = await GetResultAsync(ctrl.ReRun(RunId));

        Assert.Equal(MrpPurchaseVarianceDisposition.AutoClosed,
            Assert.Single(result.PurchaseVariances).Disposition);
        var line = await _context.PurchaseRequestLines.AsNoTracking().SingleAsync();
        Assert.True(line.IsClosed);              // 結案退場，而非刪除——保留簽核軌跡
        Assert.Equal(DocumentApprovalStatus.Approved,
            (await _context.PurchaseRequests.AsNoTracking().SingleAsync()).ApprovalStatus);
    }

    [Fact]
    public async Task ReRun_AfterModeChanged_WhenLineAlreadyOrdered_RequiresManualHandling()
    {
        SeedPurchasedProduct();
        EnableAutoClose();                       // 即使開了自動結案也不得動已轉採購的行
        var ctrl = BuildController();
        await ctrl.Edit(RunId, new MrpRun { MrpRunId = RunId }, "", doExecute: "1");
        await ApproveExistingPrAsync(orderedQty: 40m);
        await SwitchToMakeAsync();

        var result = await GetResultAsync(ctrl.ReRun(RunId));

        var v = Assert.Single(result.PurchaseVariances);
        Assert.Equal(MrpPurchaseVarianceDisposition.ManualRequired, v.Disposition);
        Assert.False((await _context.PurchaseRequestLines.AsNoTracking().SingleAsync()).IsClosed);
    }

    [Fact]
    public async Task ReRun_PendingApproval_DoesNotAutoCloseEvenWhenEnabled()
    {
        SeedPurchasedProduct();
        EnableAutoClose();
        var ctrl = BuildController();
        await ctrl.Edit(RunId, new MrpRun { MrpRunId = RunId }, "", doExecute: "1");
        var pr = await _context.PurchaseRequests.SingleAsync(p => p.SourceMrpRunId == RunId);
        pr.ApprovalStatus = DocumentApprovalStatus.PendingApproval;
        await _context.SaveChangesAsync();
        await SwitchToMakeAsync();

        var result = await GetResultAsync(ctrl.ReRun(RunId));

        Assert.Equal(MrpPurchaseVarianceDisposition.ManualRequired,
            Assert.Single(result.PurchaseVariances).Disposition);
        Assert.False((await _context.PurchaseRequestLines.AsNoTracking().SingleAsync()).IsClosed);
    }

    // ── 數量變動 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReRun_QuantityDecreased_ReportsInformationalVarianceWithoutChangingLine()
    {
        SeedPurchasedProduct();
        var ctrl = BuildController();
        await ctrl.Edit(RunId, new MrpRun { MrpRunId = RunId }, "", doExecute: "1");
        await ApproveExistingPrAsync();

        // 需求列縮量 100 → 60
        var mrpLine = await _context.MrpRunLines.FirstAsync(l => l.MrpRunId == RunId);
        mrpLine.RequiredQty = 60m;
        await _context.SaveChangesAsync();

        var result = await GetResultAsync(ctrl.ReRun(RunId));

        var v = Assert.Single(result.PurchaseVariances);
        Assert.Equal(MrpPurchaseVarianceKind.Decreased, v.Kind);
        Assert.Equal(MrpPurchaseVarianceDisposition.Informational, v.Disposition);
        Assert.Equal(100m, v.PreviousQty);
        Assert.Equal(60m, v.CurrentQty);
        // 已核准單據不自動改量
        Assert.Equal(100m, (await _context.PurchaseRequestLines.AsNoTracking().SingleAsync()).RequestQty);
    }

    [Fact]
    public async Task ReRun_UnchangedDemand_ProducesNoVariance()
    {
        SeedPurchasedProduct();
        var ctrl = BuildController();
        await ctrl.Edit(RunId, new MrpRun { MrpRunId = RunId }, "", doExecute: "1");
        await ApproveExistingPrAsync();

        var result = await GetResultAsync(ctrl.ReRun(RunId));

        Assert.Empty(result.PurchaseVariances);
    }

    // ── 草稿仍走覆寫重建 ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReRun_DraftPr_AfterModeChanged_ClearsLinesByRebuild()
    {
        // 草稿且未轉採購 → MRP 仍完全擁有，直接覆寫重建；需求消失即清空明細，不產生差異項
        SeedPurchasedProduct();
        var ctrl = BuildController();
        await ctrl.Edit(RunId, new MrpRun { MrpRunId = RunId }, "", doExecute: "1");
        await SwitchToMakeAsync();

        var result = await GetResultAsync(ctrl.ReRun(RunId));

        Assert.Empty(result.PurchaseVariances);
        Assert.Empty(await _context.PurchaseRequestLines.AsNoTracking().ToListAsync());
        Assert.Single(await _context.PurchaseRequests.AsNoTracking().ToListAsync());   // 未重複建單
    }

    private static async Task<MrpRunResult> GetResultAsync(Task<Microsoft.AspNetCore.Mvc.IActionResult> action)
    {
        var view = Assert.IsType<Microsoft.AspNetCore.Mvc.PartialViewResult>(await action);
        return Assert.IsType<MrpRunResult>(view.Model);
    }

    // ── P2：差異項落地 ───────────────────────────────────────────────────────

    [Fact]
    public async Task ReRun_PersistsOpenVarianceRecord()
    {
        SeedPurchasedProduct();
        var ctrl = BuildController();
        await ctrl.Edit(RunId, new MrpRun { MrpRunId = RunId }, "", doExecute: "1");
        await ApproveExistingPrAsync();
        await SwitchToMakeAsync();

        await GetResultAsync(ctrl.ReRun(RunId));

        var rec = await _context.MrpPurchaseVarianceRecords.AsNoTracking().SingleAsync();
        Assert.Equal(MrpVarianceStatus.Open, rec.Status);
        Assert.Equal(MrpPurchaseVarianceKind.ModeChanged, rec.Kind);
        Assert.Equal(RunId, rec.MrpRunId);
        Assert.Equal("PA2103", rec.ProductCode);
    }

    [Fact]
    public async Task ReRun_AutoClosed_PersistsResolvedVarianceRecord()
    {
        SeedPurchasedProduct();
        EnableAutoClose();
        var ctrl = BuildController();
        await ctrl.Edit(RunId, new MrpRun { MrpRunId = RunId }, "", doExecute: "1");
        await ApproveExistingPrAsync();
        await SwitchToMakeAsync();

        await GetResultAsync(ctrl.ReRun(RunId));

        var rec = await _context.MrpPurchaseVarianceRecords.AsNoTracking().SingleAsync();
        Assert.Equal(MrpVarianceStatus.Resolved, rec.Status);
        Assert.NotNull(rec.ResolvedAt);
    }

    [Fact]
    public async Task ReRun_Twice_DoesNotAccumulateDuplicateOpenVariances()
    {
        SeedPurchasedProduct();
        var ctrl = BuildController();
        await ctrl.Edit(RunId, new MrpRun { MrpRunId = RunId }, "", doExecute: "1");
        await ApproveExistingPrAsync();
        await SwitchToMakeAsync();

        await GetResultAsync(ctrl.ReRun(RunId));
        await GetResultAsync(ctrl.ReRun(RunId));

        // 差異由最新一次展開推導，重跑不累積歷史雜訊
        Assert.Single(await _context.MrpPurchaseVarianceRecords.AsNoTracking()
            .Where(v => v.Status == MrpVarianceStatus.Open).ToListAsync());
    }

}
