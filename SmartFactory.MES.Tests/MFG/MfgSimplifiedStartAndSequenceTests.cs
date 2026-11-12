using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 製造簡化模式 Phase 2（mfg-domain.md §25.4）：派工 <c>Pending</c> 直接開工＋工序順序守門。
///
/// 兩項都必須成對驗證兩種模式——關閉時要與既有行為<b>逐項相同</b>（Pending 不能開工、
/// 順序守門不能生效），否則等於偷偷改了所有既有部署的現場流程。
///
/// 判定一律走 <see cref="IMfgSimplifiedModeService"/> / <see cref="IOperationSequenceGuard"/>，
/// 桌機與手機共用同一份實作；§26 的 FPI 閘口就是因為兩端各寫一份而漂移。
/// </summary>
public class MfgSimplifiedStartAndSequenceTests : TestBase
{
    private const string PlantId = "P001";
    private const string WorkOrderId = "WO-S-1";

    public MfgSimplifiedStartAndSequenceTests()
    {
        _context.CurrentPlantId = PlantId;
    }

    // ── 建構 ─────────────────────────────────────────────────────────────────

    private DispatchController MakeController(bool simplified)
    {
        var mockPerm = new Mock<IPermissionService>();
        mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);

        var mockLoc = new Mock<IStringLocalizer<ProjectZSharedResource>>();
        mockLoc.Setup(x => x[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, k));
        mockLoc.Setup(x => x[It.IsAny<string>(), It.IsAny<object[]>()])
               .Returns<string, object[]>((k, _) => new LocalizedString(k, k));

        // 物料閘口關掉，讓失敗一定來自狀態守門或順序守門而非缺料
        var mockSysParam = new Mock<IPlantParamService>();
        mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgMaterialGate, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("None");

        var mode = MfgSimplified(simplified);
        return new DispatchController(
            mockLoc.Object,
            new Mock<IInventoryReservationService>().Object,
            mockSysParam.Object,
            new Mock<IWorkOrderMaterialReadinessService>().Object,
            new ProductionCostAccrualService(_context),
            new Mock<IStagingSourceService>().Object,
            PassThroughUom.Create(),
            new FpiGateService(_context),
            mode,
            new OperationSequenceGuard(_context, mode))
        {
            ControllerContext = GetControllerContextWithServices(
                permService: mockPerm.Object,
                configureServices: s =>
                {
                    s.AddSingleton(new Mock<IReportEtaNotifier>().Object);
                    s.AddSingleton(new Mock<IToolingShotService>().Object);
                })
        };
    }

    private static OperationSequenceGuard Guard(SmartFactory.MES.Data.ApplicationDbContext db, bool simplified)
        => new(db, MfgSimplified(simplified));

    // ── 種子 ─────────────────────────────────────────────────────────────────

    private void SeedWorkOrder() =>
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = WorkOrderId, ProductionBatchId = "B1", ProductCode = "P-1",
            RoutingId = "R1", Quantity = 10m, PlantId = PlantId, Status = WorkOrderStatus.Released,
        });

    private async Task<Dispatch> SeedDispatchAsync(
        string id, DispatchStatus status, int operationSeq = 10,
        decimal qty = 10m, decimal qtyDone = 0m, string? prevDispatchId = null)
    {
        _context.WorkOrderOperations.Add(new WorkOrderOperation
        {
            WorkOrderId = WorkOrderId, OperationSeq = operationSeq,
            OperationName = "OP", WorkCenterId = "WC1",
        });
        var d = new Dispatch
        {
            DispatchId = id, WorkOrderId = WorkOrderId, OperationSeq = operationSeq,
            PlantId = PlantId, Quantity = qty, QtyDone = qtyDone, Status = status,
            MachineCode = "MC01", PrevDispatchId = prevDispatchId,
        };
        _context.Dispatches.Add(d);
        await _context.SaveChangesAsync();
        return d;
    }

    private async Task SeedSessionAsync(string sessionId, string dispatchId)
    {
        _context.ProductionSessions.Add(new ProductionSession
        {
            SessionId = sessionId, DispatchId = dispatchId, WorkOrderId = WorkOrderId,
            Status = ProductionSessionStatus.Open, StartAt = DateTime.UtcNow, PlantId = PlantId,
        });
        await _context.SaveChangesAsync();
    }

    private static bool IsSuccess(IActionResult result)
    {
        dynamic data = Assert.IsType<JsonResult>(result).Value!;
        return (bool)data.GetType().GetProperty("success")!.GetValue(data)!;
    }

    private static string Message(IActionResult result)
    {
        dynamic data = Assert.IsType<JsonResult>(result).Value!;
        return (string)data.GetType().GetProperty("message")!.GetValue(data)!;
    }

    // ══════════════════════════════════════════════════════════════════════
    // §25.4-① 開工放行狀態
    // ══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(DispatchStatus.Pending, false)]        // 完整模式：Pending 不可開工（既有行為）
    [InlineData(DispatchStatus.Scheduled, true)]
    [InlineData(DispatchStatus.ReadyToStart, true)]
    [InlineData(DispatchStatus.InProgress, true)]
    [InlineData(DispatchStatus.Closed, false)]
    [InlineData(DispatchStatus.OnHold, false)]
    public async Task CanStartSession_FullMode(DispatchStatus status, bool expected)
    {
        Assert.Equal(expected, await MfgSimplified(false).CanStartSessionAsync(status, "P001"));
    }

    [Theory]
    [InlineData(DispatchStatus.Pending, true)]         // 簡化模式：額外放行 Pending
    [InlineData(DispatchStatus.Scheduled, true)]
    [InlineData(DispatchStatus.InProgress, true)]
    [InlineData(DispatchStatus.Closed, false)]         // 其餘狀態不因簡化模式而放寬
    [InlineData(DispatchStatus.OnHold, false)]
    [InlineData(DispatchStatus.PendingFPI, false)]
    public async Task CanStartSession_SimplifiedMode(DispatchStatus status, bool expected)
    {
        Assert.Equal(expected, await MfgSimplified(true).CanStartSessionAsync(status, "P001"));
    }

    /// <summary>現場待辦清單：不納入 Pending 的話，開工放行了也看不到這張派工。</summary>
    [Fact]
    public async Task ActionableDispatchStatuses_SimplifiedIncludesPending_FullDoesNot()
    {
        Assert.DoesNotContain(DispatchStatus.Pending, await MfgSimplified(false).ActionableDispatchStatusesAsync("P001"));
        Assert.Contains(DispatchStatus.Pending, await MfgSimplified(true).ActionableDispatchStatusesAsync("P001"));
    }

    [Fact]
    public async Task StartSession_PendingDispatch_RejectedInFullMode()
    {
        SeedWorkOrder();
        await SeedDispatchAsync("D-P", DispatchStatus.Pending);

        var result = await MakeController(simplified: false).StartSession("D-P", null, null);

        Assert.False(IsSuccess(result));
        Assert.Equal("DispatchCannotStart", Message(result));
        Assert.Empty(_context.ProductionSessions);
    }

    [Fact]
    public async Task StartSession_PendingDispatch_AllowedInSimplifiedMode()
    {
        SeedWorkOrder();
        await SeedDispatchAsync("D-P", DispatchStatus.Pending);

        var result = await MakeController(simplified: true).StartSession("D-P", null, null);

        Assert.True(IsSuccess(result));
        Assert.Single(_context.ProductionSessions);
        Assert.Equal(DispatchStatus.InProgress, (await _context.Dispatches.FindAsync("D-P"))!.Status);
    }

    /// <summary>簡化模式不得順帶放寬其他狀態——Closed 派工仍不可開工。</summary>
    [Fact]
    public async Task StartSession_ClosedDispatch_StillRejectedInSimplifiedMode()
    {
        SeedWorkOrder();
        await SeedDispatchAsync("D-C", DispatchStatus.Closed);

        var result = await MakeController(simplified: true).StartSession("D-C", null, null);

        Assert.False(IsSuccess(result));
        Assert.Empty(_context.ProductionSessions);
    }

    /// <summary>
    /// §18.2 投產前審核：送審中／被退回的工單不得開工。Draft（預設值，§18.2 前的舊工單與
    /// 無簽核設定者）與 Approved 照常放行——兩腿都斷言，否則把整個開工擋死也會過。
    /// </summary>
    [Theory]
    [InlineData(DocumentApprovalStatus.PendingApproval, false)]
    [InlineData(DocumentApprovalStatus.Rejected,        false)]
    [InlineData(DocumentApprovalStatus.Draft,           true)]
    [InlineData(DocumentApprovalStatus.Approved,        true)]
    public async Task StartSession_FullMode_GatedByWorkOrderApproval(DocumentApprovalStatus approval, bool allowed)
    {
        SeedWorkOrder();
        await SeedDispatchAsync("D-S", DispatchStatus.Scheduled);
        (await _context.WorkOrders.FindAsync(WorkOrderId))!.ApprovalStatus = approval;
        await _context.SaveChangesAsync();

        var result = await MakeController(simplified: false).StartSession("D-S", null, null);

        Assert.Equal(allowed, IsSuccess(result));
        if (!allowed) Assert.Equal("WorkOrderNotApprovedCannotStart", Message(result));
    }

    /// <summary>
    /// 開工原本完全不看工單狀態：撤回到已規劃的工單，先前已排程的派工照樣能開工（2026-10-20 實證）。
    /// 已投產與生產中放行，其餘一律擋，兩種模式相同。
    /// </summary>
    [Theory]
    [InlineData(WorkOrderStatus.Planned,    false, false)]
    [InlineData(WorkOrderStatus.Planned,    true,  false)]
    [InlineData(WorkOrderStatus.Completed,  false, false)]
    [InlineData(WorkOrderStatus.Released,   false, true)]
    [InlineData(WorkOrderStatus.InProgress, true,  true)]
    public async Task StartSession_GatedByWorkOrderStatus(WorkOrderStatus woStatus, bool simplified, bool allowed)
    {
        SeedWorkOrder();
        await SeedDispatchAsync("D-W", DispatchStatus.Scheduled);
        (await _context.WorkOrders.FindAsync(WorkOrderId))!.Status = woStatus;
        await _context.SaveChangesAsync();

        var result = await MakeController(simplified).StartSession("D-W", null, null);

        Assert.Equal(allowed, IsSuccess(result));
        if (!allowed) Assert.Equal("WorkOrderNotReleasedCannotStart", Message(result));
    }

    // ══════════════════════════════════════════════════════════════════════
    // §25.4-② 工序順序守門
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SequenceGuard_FullMode_NeverBlocks()
    {
        SeedWorkOrder();
        await SeedDispatchAsync("D-10", DispatchStatus.InProgress, operationSeq: 10, qtyDone: 0m);
        var op20 = await SeedDispatchAsync("D-20", DispatchStatus.InProgress, operationSeq: 20,
            prevDispatchId: "D-10");

        // 前工序 0 件、本工序想報 5 件——完整模式由排程保證順序，不加這道守門
        var r = await Guard(_context, simplified: false).CheckAsync(op20, 5m);

        Assert.False(r.Blocked);
    }

    [Fact]
    public async Task SequenceGuard_SimplifiedMode_BlocksWhenPrevOperationBehind()
    {
        SeedWorkOrder();
        await SeedDispatchAsync("D-10", DispatchStatus.InProgress, operationSeq: 10, qtyDone: 3m);
        var op20 = await SeedDispatchAsync("D-20", DispatchStatus.InProgress, operationSeq: 20,
            prevDispatchId: "D-10");

        var r = await Guard(_context, simplified: true).CheckAsync(op20, 5m);   // 0 + 5 > 3

        Assert.True(r.Blocked);
        Assert.Equal(3m, r.PrevQtyDone);
        Assert.Equal(5m, r.Attempted);
    }

    [Fact]
    public async Task SequenceGuard_SimplifiedMode_AllowsUpToPrevQtyDone()
    {
        SeedWorkOrder();
        await SeedDispatchAsync("D-10", DispatchStatus.InProgress, operationSeq: 10, qtyDone: 5m);
        var op20 = await SeedDispatchAsync("D-20", DispatchStatus.InProgress, operationSeq: 20,
            qtyDone: 2m, prevDispatchId: "D-10");

        // 2（已報）+ 3 = 5，剛好等於前工序完成量 → 放行
        Assert.False((await Guard(_context, simplified: true).CheckAsync(op20, 3m)).Blocked);
        // 再多 1 件就超過 → 擋下
        Assert.True((await Guard(_context, simplified: true).CheckAsync(op20, 4m)).Blocked);
    }

    /// <summary>首道工序沒有前置可比，一律放行（否則簡化模式下什麼都報不了）。</summary>
    [Fact]
    public async Task SequenceGuard_FirstOperation_NotBlocked()
    {
        SeedWorkOrder();
        var op10 = await SeedDispatchAsync("D-10", DispatchStatus.InProgress, operationSeq: 10);

        Assert.False((await Guard(_context, simplified: true).CheckAsync(op10, 10m)).Blocked);
    }

    /// <summary>前置派工查不到（資料異常）時不擋——守門本身不該變成停產原因。</summary>
    [Fact]
    public async Task SequenceGuard_PrevDispatchMissing_NotBlocked()
    {
        SeedWorkOrder();
        var op20 = await SeedDispatchAsync("D-20", DispatchStatus.InProgress, operationSeq: 20,
            prevDispatchId: "D-GONE");

        Assert.False((await Guard(_context, simplified: true).CheckAsync(op20, 5m)).Blocked);
    }

    // ── 報工入口（桌機）：守門確實接上 ────────────────────────────────────────

    [Fact]
    public async Task CreateReport_SimplifiedMode_RejectedWhenPrevOperationBehind()
    {
        SeedWorkOrder();
        await SeedDispatchAsync("D-10", DispatchStatus.InProgress, operationSeq: 10, qtyDone: 2m);
        await SeedDispatchAsync("D-20", DispatchStatus.InProgress, operationSeq: 20, prevDispatchId: "D-10");
        await SeedSessionAsync("S-20", "D-20");

        var result = await MakeController(simplified: true)
            .CreateReport("D-20", "S-20", qtyOk: 5m, qtyNg: 0m, containerCode: null);

        Assert.False(IsSuccess(result));
        Assert.Equal("ReportPrevOperationShortage", Message(result));
        Assert.Empty(_context.ProductionReports);
        Assert.Equal(0m, (await _context.Dispatches.FindAsync("D-20"))!.QtyDone);
    }

    [Fact]
    public async Task CreateReport_FullMode_SamePayloadStillAccepted()
    {
        SeedWorkOrder();
        await SeedDispatchAsync("D-10", DispatchStatus.InProgress, operationSeq: 10, qtyDone: 2m);
        await SeedDispatchAsync("D-20", DispatchStatus.InProgress, operationSeq: 20, prevDispatchId: "D-10");
        await SeedSessionAsync("S-20", "D-20");

        var result = await MakeController(simplified: false)
            .CreateReport("D-20", "S-20", qtyOk: 5m, qtyNg: 0m, containerCode: null);

        Assert.True(IsSuccess(result));
        Assert.Equal(5m, (await _context.Dispatches.FindAsync("D-20"))!.QtyDone);
    }

    [Fact]
    public async Task CreateReport_SimplifiedMode_AcceptedWhenPrevOperationAhead()
    {
        SeedWorkOrder();
        await SeedDispatchAsync("D-10", DispatchStatus.InProgress, operationSeq: 10, qtyDone: 8m);
        await SeedDispatchAsync("D-20", DispatchStatus.InProgress, operationSeq: 20, prevDispatchId: "D-10");
        await SeedSessionAsync("S-20", "D-20");

        var result = await MakeController(simplified: true)
            .CreateReport("D-20", "S-20", qtyOk: 5m, qtyNg: 0m, containerCode: null);

        Assert.True(IsSuccess(result));
        Assert.Equal(5m, (await _context.Dispatches.FindAsync("D-20"))!.QtyDone);
    }
}
