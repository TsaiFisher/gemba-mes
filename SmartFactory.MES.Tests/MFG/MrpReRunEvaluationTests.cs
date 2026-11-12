using SmartFactory.MES.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 重跑前置可行性評估（mfg-domain.md §10-5 分級閘門）。
/// 驗證判準是「下游是否已產生不可回收的外部承諾或實體足跡」而非「單據是否已核准」：
/// 已核准但未被消化的請購單只列為殘留項（重跑照常執行），實體足跡才阻擋重跑。
/// </summary>
public class MrpReRunEvaluationTests : TestBase
{
    private readonly Mock<IDocNumberService> _mockDocNo = new();
    private readonly Mock<IDateTimeService> _mockDt = new();
    private readonly Mock<IPlantParamService> _mockSysParam = new();
    private readonly MrpEngine _engine;
    private const string PlantId = "P001";
    private static readonly DateTime Today = new(2026, 5, 25, 8, 0, 0, DateTimeKind.Utc);

    public MrpReRunEvaluationTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockDt.Setup(d => d.Now).Returns(Today);

        var batchNo = 0;
        _mockDocNo
            .Setup(d => d.NextAsync(MrpEngine.BatchDocType, PlantId, It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => $"PB-{++batchNo:D4}");
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("false");

        _engine = new MrpEngine(_context, new InventoryReservationService(_context),
            _mockDocNo.Object, _mockDt.Object, _mockSysParam.Object, Mock.Of<IWorkOrderReleaseService>());
    }

    /// <summary>建立並展開一張自製需求的 MRP 執行單，回傳執行單號。</summary>
    private async Task<string> ExpandedRunAsync()
    {
        const string runId = "MRP-0001";
        _context.Products.Add(new Product
        {
            ProductCode = "FG", ProductName = "FG", StockUom = "PC", MrpMode = "M", RoutingId = "R_FG"
        });
        _context.Routings.Add(new Routing
        {
            RoutingId = "R_FG", ProductCode = "FG", PlantId = PlantId,
            BatchSize = 1, BufferDays = 0, IsActive = true,
            Operations = new List<RoutingOperation>
            {
                new()
                {
                    RoutingId = "R_FG", OperationSeq = 10, OperationName = "OP", WorkCenterId = "WC1",
                    TM_SETUP = 0, TM_UNIT = 0, CD_BOR = OperationMode.WholeOrder, QT_BOR = 1, UomCode = "PC"
                }
            }
        });
        var run = new MrpRun { MrpRunId = runId, PlantId = PlantId, Status = MrpRunStatus.Draft };
        run.Lines.Add(new MrpRunLine
        {
            MrpRunId = runId, LineSeq = 1, ProductCode = "FG",
            RequiredQty = 5, RequiredDate = Today.AddDays(365)
        });
        _context.MrpRuns.Add(run);
        await _context.SaveChangesAsync();

        await _engine.RunAsync(runId);
        return runId;
    }

    private void AddPurchaseRequest(string runId, DocumentApprovalStatus status, decimal orderedQty)
    {
        _context.PurchaseRequests.Add(new PurchaseRequest
        {
            PurchaseRequestId = "PR-0001", SourceMrpRunId = runId, PlantId = PlantId,
            RequestDate = DateOnly.FromDateTime(Today), RequestedBy = "u1", ApprovalStatus = status,
            Lines =
            {
                new PurchaseRequestLine
                {
                    PurchaseRequestId = "PR-0001", LineNo = 1, ProductCode = "PA2103",
                    RequestQty = 10, OrderedQty = orderedQty
                }
            }
        });
    }

    // ── L1：下游零消化 ───────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_NothingDownstream_IsClean()
    {
        var runId = await ExpandedRunAsync();

        var eval = await _engine.EvaluateReRunAsync(runId);

        Assert.Equal(MrpReRunLevel.Clean, eval.Level);
        Assert.True(eval.CanReRun);
        Assert.Empty(eval.Impediments);
    }

    [Fact]
    public async Task Evaluate_DraftPurchaseRequestNotOrdered_IsNotResidual()
    {
        // 草稿且未轉採購的請購單由去重邏輯直接覆寫重建，不算殘留項
        var runId = await ExpandedRunAsync();
        AddPurchaseRequest(runId, DocumentApprovalStatus.Draft, orderedQty: 0m);
        await _context.SaveChangesAsync();

        var eval = await _engine.EvaluateReRunAsync(runId);

        Assert.Equal(MrpReRunLevel.Clean, eval.Level);
        Assert.Empty(eval.Impediments);
    }

    // ── L2：請購單殘留（重跑仍可執行）─────────────────────────────────────────

    [Fact]
    public async Task Evaluate_ApprovedPurchaseRequest_IsResidualButStillReRunnable()
    {
        // PA2103 由採購改自製的情境：請購單已核准但未轉採購 → 重跑照常執行，
        // 但該單不會被自動收斂，須列為殘留項提示人工結案退場。
        var runId = await ExpandedRunAsync();
        AddPurchaseRequest(runId, DocumentApprovalStatus.Approved, orderedQty: 0m);
        await _context.SaveChangesAsync();

        var eval = await _engine.EvaluateReRunAsync(runId);

        Assert.Equal(MrpReRunLevel.Residual, eval.Level);
        Assert.True(eval.CanReRun);                       // 關鍵：已核准不等於阻擋
        var item = Assert.Single(eval.Residuals);
        Assert.Equal(MrpReRunImpedimentKind.PurchaseRequestApproved, item.Kind);
        Assert.Equal("PR-0001", item.DocumentId);
    }

    [Fact]
    public async Task Evaluate_PendingApprovalPurchaseRequest_IsResidual()
    {
        var runId = await ExpandedRunAsync();
        AddPurchaseRequest(runId, DocumentApprovalStatus.PendingApproval, orderedQty: 0m);
        await _context.SaveChangesAsync();

        var eval = await _engine.EvaluateReRunAsync(runId);

        Assert.True(eval.CanReRun);
        Assert.Equal(MrpReRunImpedimentKind.PurchaseRequestPendingApproval,
            Assert.Single(eval.Residuals).Kind);
    }

    [Fact]
    public async Task Evaluate_OrderedPurchaseRequest_IsResidual()
    {
        var runId = await ExpandedRunAsync();
        AddPurchaseRequest(runId, DocumentApprovalStatus.Approved, orderedQty: 4m);
        await _context.SaveChangesAsync();

        var eval = await _engine.EvaluateReRunAsync(runId);

        Assert.True(eval.CanReRun);
        var item = Assert.Single(eval.Residuals);
        Assert.Equal(MrpReRunImpedimentKind.PurchaseRequestOrdered, item.Kind);
        Assert.Contains("PA2103", item.Description);
    }

    // ── L3：實體足跡／外部承諾 ───────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_WorkOrderInProgress_IsBlocked()
    {
        var runId = await ExpandedRunAsync();
        var wo = await _context.WorkOrders.FirstAsync();
        wo.Status = WorkOrderStatus.InProgress;
        await _context.SaveChangesAsync();

        var eval = await _engine.EvaluateReRunAsync(runId);

        Assert.Equal(MrpReRunLevel.Blocked, eval.Level);
        Assert.False(eval.CanReRun);
        var item = Assert.Single(eval.Blocking);
        Assert.Equal(MrpReRunImpedimentKind.WorkOrderStarted, item.Kind);
        Assert.Equal(nameof(WorkOrderStatus.InProgress), item.Detail);
    }

    [Fact]
    public async Task Evaluate_DispatchReportedQty_IsBlocked()
    {
        // 派工已回報產量＝實體足跡。工單狀態仍為 Planned，僅靠既有的工單狀態守門抓不到。
        var runId = await ExpandedRunAsync();
        var dispatch = await _context.Dispatches.FirstAsync();
        dispatch.QtyDone = 3m;
        await _context.SaveChangesAsync();

        var eval = await _engine.EvaluateReRunAsync(runId);

        Assert.False(eval.CanReRun);
        Assert.Equal(MrpReRunImpedimentKind.DispatchReported, Assert.Single(eval.Blocking).Kind);
    }

    [Fact]
    public async Task ReRun_DispatchReportedQty_Throws()
    {
        var runId = await ExpandedRunAsync();
        var dispatch = await _context.Dispatches.FirstAsync();
        dispatch.QtyDone = 3m;
        await _context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.ReRunAsync(runId));
        PzAssert.ContainsMessage("Mfg_RerunDispatchReportedFmt", ex.Message);
    }

    [Fact]
    public async Task Evaluate_OutsourceDemandNotTransferred_IsResidualNotBlocked()
    {
        // 判準式守門（P2）：尚未轉委外採購單者只是 Restrict FK 引用，重跑時一併刪除重建即可，
        // 不再比照「存在即擋」把整次重跑攔下。
        var runId = await ExpandedRunAsync();
        var wo = await _context.WorkOrders.AsNoTracking().FirstAsync();
        _context.OutsourceDemands.Add(new OutsourceDemand
        {
            DemandId = "OD-0001", WorkOrderId = wo.WorkOrderId, ProductCode = "FG",
            DemandQty = 5, TransferredQty = 0, RequiredDate = DateOnly.FromDateTime(Today), PlantId = PlantId
        });
        await _context.SaveChangesAsync();

        var eval = await _engine.EvaluateReRunAsync(runId);

        Assert.True(eval.CanReRun);
        Assert.Equal(MrpReRunImpedimentKind.OutsourceDemandPending, Assert.Single(eval.Residuals).Kind);
    }

    [Fact]
    public async Task ReRun_WithUntransferredOutsourceDemand_DeletesItAndRebuilds()
    {
        var runId = await ExpandedRunAsync();
        var wo = await _context.WorkOrders.AsNoTracking().FirstAsync();
        _context.OutsourceDemands.Add(new OutsourceDemand
        {
            DemandId = "OD-0001", WorkOrderId = wo.WorkOrderId, ProductCode = "FG",
            DemandQty = 5, TransferredQty = 0, RequiredDate = DateOnly.FromDateTime(Today), PlantId = PlantId
        });
        await _context.SaveChangesAsync();

        await _engine.ReRunAsync(runId);

        // 舊委外需求隨舊工單一併清除（否則刪工單會撞 Restrict FK）
        Assert.Empty(await _context.OutsourceDemands.AsNoTracking().ToListAsync());
        Assert.Single(await _context.WorkOrders.AsNoTracking().ToListAsync());
    }

    // ── 封存（L3）─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Archive_VoidsUnstartedBatchAndWorkOrders_WithoutTouchingDownstream()
    {
        var runId = await ExpandedRunAsync();

        var voided = await _engine.ArchiveAsync(runId);

        Assert.Single(voided);
        Assert.Equal(MrpRunStatus.Archived,
            (await _context.MrpRuns.AsNoTracking().SingleAsync()).Status);
        Assert.All(await _context.WorkOrders.AsNoTracking().ToListAsync(),
            w => Assert.Equal(WorkOrderStatus.Void, w.Status));
        Assert.All(await _context.ProductionBatches.AsNoTracking().ToListAsync(),
            b => Assert.Equal(ProductionBatchStatus.Void, b.Status));
    }

    [Fact]
    public async Task Archive_KeepsStartedBatchIntact()
    {
        var runId = await ExpandedRunAsync();
        var wo = await _context.WorkOrders.FirstAsync();
        wo.Status = WorkOrderStatus.InProgress;      // 已投產：整批保持原狀
        await _context.SaveChangesAsync();

        var voided = await _engine.ArchiveAsync(runId);

        Assert.Empty(voided);
        Assert.Equal(MrpRunStatus.Archived,
            (await _context.MrpRuns.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(WorkOrderStatus.InProgress,
            (await _context.WorkOrders.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(ProductionBatchStatus.Planned,
            (await _context.ProductionBatches.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Archive_ThenReRun_IsRejected()
    {
        var runId = await ExpandedRunAsync();
        await _engine.ArchiveAsync(runId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.ReRunAsync(runId));
    }

    [Fact]
    public async Task Evaluate_OutsourceDemandTransferred_IsBlockedAsTransferred()
    {
        var runId = await ExpandedRunAsync();
        var wo = await _context.WorkOrders.AsNoTracking().FirstAsync();
        _context.OutsourceDemands.Add(new OutsourceDemand
        {
            DemandId = "OD-0002", WorkOrderId = wo.WorkOrderId, ProductCode = "FG",
            DemandQty = 5, TransferredQty = 5, RequiredDate = DateOnly.FromDateTime(Today), PlantId = PlantId
        });
        await _context.SaveChangesAsync();

        var eval = await _engine.EvaluateReRunAsync(runId);

        Assert.False(eval.CanReRun);
        Assert.Equal(MrpReRunImpedimentKind.OutsourceDemandTransferred, Assert.Single(eval.Blocking).Kind);
    }

    // ── 評估本身唯讀 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_DoesNotMutateAnything()
    {
        var runId = await ExpandedRunAsync();
        AddPurchaseRequest(runId, DocumentApprovalStatus.Approved, orderedQty: 0m);
        await _context.SaveChangesAsync();

        var woBefore = await _context.WorkOrders.AsNoTracking().CountAsync();
        var batchBefore = await _context.ProductionBatches.AsNoTracking().CountAsync();

        await _engine.EvaluateReRunAsync(runId);

        Assert.Empty(_context.ChangeTracker.Entries().Where(e => e.State != EntityState.Unchanged));
        Assert.Equal(woBefore, await _context.WorkOrders.AsNoTracking().CountAsync());
        Assert.Equal(batchBefore, await _context.ProductionBatches.AsNoTracking().CountAsync());
        Assert.Equal(MrpRunStatus.Executed,
            (await _context.MrpRuns.AsNoTracking().SingleAsync(r => r.MrpRunId == runId)).Status);
    }
}
