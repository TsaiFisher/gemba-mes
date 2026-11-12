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
/// 稽核修正回歸測試：MRP「重跑」不應對同一執行單重複建立請購單
/// （MrpRunController.CreatePurchaseRequestFromDemandsAsync + PurchaseRequest.SourceMrpRunId 去重）。
/// 使用真實 MrpEngine + 真實 PurchaseService（皆對同一 InMemory DbContext 運作），
/// 端到端驗證行為而非 mock 掉待驗證的關鍵邏輯。
/// </summary>
public class MrpRunPurchaseRequestDedupTests : TestBase
{
    private const string PlantId = "P001";
    private const string UserId  = "user1";
    private static readonly DateTime Today = new(2026, 5, 25, 8, 0, 0, DateTimeKind.Utc);

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

    /// <summary>建立一張採購模式（P）需求列的 MRP 執行單（Draft），不需途程即可觸發 PurchaseDemands。</summary>
    private string AddRun(string product, decimal qty, DateTime due)
    {
        const string runId = "MRP-0001";
        _context.Products.Add(new Product { ProductCode = product, ProductName = product, StockUom = "PC", MrpMode = "P" });
        var run = new MrpRun { MrpRunId = runId, PlantId = PlantId, Status = MrpRunStatus.Draft };
        run.Lines.Add(new MrpRunLine { MrpRunId = runId, LineSeq = 1, ProductCode = product, RequiredQty = qty, RequiredDate = due });
        _context.MrpRuns.Add(run);
        _context.SaveChanges();
        return runId;
    }

    [Fact]
    public async Task ReRun_Twice_DoesNotCreateSecondPurchaseRequest()
    {
        var runId = AddRun("RM", 100m, Today.AddDays(30));
        var ctrl = BuildController();

        await ctrl.Edit(runId, new MrpRun { MrpRunId = runId }, "", doExecute: "1");
        var afterFirst = await _context.PurchaseRequests.Include(p => p.Lines)
            .Where(p => p.SourceMrpRunId == runId).ToListAsync();
        Assert.Single(afterFirst);
        Assert.Equal(100m, afterFirst[0].Lines.Sum(l => l.RequestQty));

        // 重跑：需求不變（同樣 100），舊行為會再建一張重複請購單
        await ctrl.ReRun(runId);
        var afterSecond = await _context.PurchaseRequests.Include(p => p.Lines)
            .Where(p => p.SourceMrpRunId == runId).ToListAsync();

        Assert.Single(afterSecond);                                            // 仍只有一張，覆寫重建而非新增第二張
        Assert.Equal(afterFirst[0].PurchaseRequestId, afterSecond[0].PurchaseRequestId);
        Assert.Equal(100m, afterSecond[0].Lines.Sum(l => l.RequestQty));
    }

    [Fact]
    public async Task ReRun_AfterExistingPrSubmittedForApproval_DoesNotOverwriteOrDuplicate()
    {
        var runId = AddRun("RM", 100m, Today.AddDays(30));
        var ctrl = BuildController();

        await ctrl.Edit(runId, new MrpRun { MrpRunId = runId }, "", doExecute: "1");
        var pr = await _context.PurchaseRequests.SingleAsync(p => p.SourceMrpRunId == runId);
        pr.ApprovalStatus = DocumentApprovalStatus.PendingApproval;   // 模擬使用者已將請購單送審
        await _context.SaveChangesAsync();

        await ctrl.ReRun(runId);

        var all = await _context.PurchaseRequests.Where(p => p.SourceMrpRunId == runId).ToListAsync();
        Assert.Single(all);                                                    // 沒有建立第二張重複請購單
        Assert.Equal(DocumentApprovalStatus.PendingApproval, all[0].ApprovalStatus); // 既有單狀態未被覆寫
    }

    [Fact]
    public async Task ReRun_AfterExistingPrPartiallyOrdered_DoesNotOverwriteOrDuplicate()
    {
        var runId = AddRun("RM", 100m, Today.AddDays(30));
        var ctrl = BuildController();

        await ctrl.Edit(runId, new MrpRun { MrpRunId = runId }, "", doExecute: "1");
        var pr = await _context.PurchaseRequests.Include(p => p.Lines)
            .SingleAsync(p => p.SourceMrpRunId == runId);
        pr.Lines.First().OrderedQty = 40m;   // 模擬明細已部分轉採購
        await _context.SaveChangesAsync();

        await ctrl.ReRun(runId);

        var all = await _context.PurchaseRequests.Where(p => p.SourceMrpRunId == runId).ToListAsync();
        Assert.Single(all);                  // 沒有建立第二張重複請購單，既有已轉採購明細未被覆寫
    }
}
