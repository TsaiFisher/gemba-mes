using Microsoft.AspNetCore.Mvc;
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
/// FPI 首件檢查閘口（mfg-domain.md §26）。
///
/// 守門的關鍵在於**以事實判定、不以派工狀態判定**：舊版只擋 <c>PendingFPI</c>，
/// 而該狀態只能經備料（SetupDone）進入 → 不做備料直接開工即可繞過（§26.1 缺口 A）。
/// 因此本檔對「Scheduled 直接開工」與「PendingFPI 開工」兩種起始狀態都要斷言被擋，
/// 只驗其中一種會漏掉真正的缺陷。
/// </summary>
public class FpiGateTests : TestBase
{
    private const string PlantId = "P001";
    private const string WorkOrderId = "WO-FPI-1";

    private readonly DispatchController _ctrl;
    private readonly FpiGateService _gate;

    public FpiGateTests()
    {
        _context.CurrentPlantId = PlantId;

        var mockPerm = new Mock<IPermissionService>();
        mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);

        var mockLoc = new Mock<IStringLocalizer<ProjectZSharedResource>>();
        mockLoc.Setup(x => x[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, k));

        // 物料閘口預設不擋（None），使本檔的失敗一定來自 FPI 而非缺料
        var mockSysParam = new Mock<IPlantParamService>();
        mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgMaterialGate, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("None");

        _gate = new FpiGateService(_context);
        _ctrl = new DispatchController(
            mockLoc.Object,
            new Mock<IInventoryReservationService>().Object,
            mockSysParam.Object,
            new Mock<IWorkOrderMaterialReadinessService>().Object,
            new ProductionCostAccrualService(_context),
            new Mock<IStagingSourceService>().Object,
            PassThroughUom.Create(),
            _gate,
            MfgSimplified(),
            new OperationSequenceGuard(_context, MfgSimplified()))
        {
            ControllerContext = GetControllerContextWithServices(permService: mockPerm.Object)
        };
    }

    // ── 種子 ─────────────────────────────────────────────────────────────────

    private async Task<Dispatch> SeedAsync(bool requiresFpi, DispatchStatus status, int operationSeq = 1)
    {
        _context.WorkOrderOperations.Add(new WorkOrderOperation
        {
            WorkOrderId   = WorkOrderId,
            OperationSeq  = operationSeq,
            OperationName = "OP",
            WorkCenterId  = "WC1",
            RequiresFpi   = requiresFpi,
        });
        var d = new Dispatch
        {
            DispatchId   = $"D-FPI-{operationSeq}",
            WorkOrderId  = WorkOrderId,
            OperationSeq = operationSeq,
            PlantId      = PlantId,
            Quantity     = 10m,
            Status       = status,
            MachineCode  = "MC01",
        };
        _context.Dispatches.Add(d);
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = WorkOrderId, ProductionBatchId = "B1", ProductCode = "P-1",
            RoutingId = "R1", Quantity = 10m, PlantId = PlantId,
            Status = WorkOrderStatus.Released,
        });
        await _context.SaveChangesAsync();
        return d;
    }

    private async Task SeedFpiOrderAsync(string dispatchId, string status)
    {
        _context.InspectionOrders.Add(new InspectionOrder
        {
            InspectionType = "FPI",
            OrderType      = InspectionOrderType.FPI,
            PlantId        = PlantId,
            SourceDocId    = dispatchId,
            ProductCode    = "P-1",
            LotSize        = 10m,
            Status         = status,
        });
        await _context.SaveChangesAsync();
    }

    private static bool IsSuccess(IActionResult result)
    {
        dynamic data = Assert.IsType<JsonResult>(result).Value!;
        return (bool)data.GetType().GetProperty("success")!.GetValue(data)!;
    }

    // ── 閘口判定（服務層）────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_OperationDoesNotRequireFpi_NotBlocked()
    {
        var d = await SeedAsync(requiresFpi: false, DispatchStatus.Scheduled);

        var r = await _gate.EvaluateAsync(d);

        Assert.False(r.RequiresFpi);
        Assert.False(r.IsBlocked);
    }

    [Fact]
    public async Task Evaluate_RequiresFpiWithoutPassedOrder_Blocked()
    {
        var d = await SeedAsync(requiresFpi: true, DispatchStatus.Scheduled);

        var r = await _gate.EvaluateAsync(d);

        Assert.True(r.RequiresFpi);
        Assert.False(r.HasPassed);
        Assert.True(r.IsBlocked);
    }

    /// <summary>Pending（尚未判定）的首件單不算通過——否則開了單就等於放行。</summary>
    [Fact]
    public async Task Evaluate_PendingFpiOrder_StillBlocked()
    {
        var d = await SeedAsync(requiresFpi: true, DispatchStatus.Scheduled);
        await SeedFpiOrderAsync(d.DispatchId, "Pending");

        Assert.True((await _gate.EvaluateAsync(d)).IsBlocked);
    }

    [Fact]
    public async Task Evaluate_FailedFpiOrder_StillBlocked()
    {
        var d = await SeedAsync(requiresFpi: true, DispatchStatus.Scheduled);
        await SeedFpiOrderAsync(d.DispatchId, "Fail");

        Assert.True((await _gate.EvaluateAsync(d)).IsBlocked);
    }

    [Fact]
    public async Task Evaluate_PassedFpiOrder_NotBlocked()
    {
        var d = await SeedAsync(requiresFpi: true, DispatchStatus.Scheduled);
        await SeedFpiOrderAsync(d.DispatchId, "Pass");

        var r = await _gate.EvaluateAsync(d);

        Assert.True(r.RequiresFpi);
        Assert.True(r.HasPassed);
        Assert.False(r.IsBlocked);
    }

    /// <summary>
    /// 早期資料只有 InspectionType 字串、OrderType 為 null；漏比對會讓已通過首件的派工被誤擋。
    /// </summary>
    [Fact]
    public async Task Evaluate_LegacyOrderWithoutOrderType_CountsAsPassed()
    {
        var d = await SeedAsync(requiresFpi: true, DispatchStatus.Scheduled);
        _context.InspectionOrders.Add(new InspectionOrder
        {
            InspectionType = "FPI", OrderType = null, PlantId = PlantId,
            SourceDocId = d.DispatchId, ProductCode = "P-1", LotSize = 10m, Status = "Pass",
        });
        await _context.SaveChangesAsync();

        Assert.False((await _gate.EvaluateAsync(d)).IsBlocked);
    }

    /// <summary>閘口逐派工判定：他張派工的 Pass 不得解鎖本張。</summary>
    [Fact]
    public async Task Evaluate_PassedOrderOfAnotherDispatch_StillBlocked()
    {
        var d = await SeedAsync(requiresFpi: true, DispatchStatus.Scheduled);
        await SeedFpiOrderAsync("D-OTHER", "Pass");

        Assert.True((await _gate.EvaluateAsync(d)).IsBlocked);
    }

    // ── 開工守門（桌機入口）──────────────────────────────────────────────────

    /// <summary>
    /// §26.1 缺口 A 的迴歸守門：不做備料、派工仍在 Scheduled 就直接開工。
    /// 舊版只擋 PendingFPI，此路徑可完全繞過首件檢查。
    /// </summary>
    [Fact]
    public async Task StartSession_ScheduledSkippingSetup_RequiresFpi_IsBlocked()
    {
        var d = await SeedAsync(requiresFpi: true, DispatchStatus.Scheduled);

        var result = await _ctrl.StartSession(d.DispatchId, "MC01", null);

        Assert.False(IsSuccess(result));
        Assert.Equal(DispatchStatus.Scheduled, _context.Dispatches.Single().Status);
        Assert.Empty(_context.ProductionSessions);
    }

    [Fact]
    public async Task StartSession_PendingFpi_IsBlocked()
    {
        var d = await SeedAsync(requiresFpi: true, DispatchStatus.PendingFPI);

        Assert.False(IsSuccess(await _ctrl.StartSession(d.DispatchId, "MC01", null)));
        Assert.Empty(_context.ProductionSessions);
    }

    [Fact]
    public async Task StartSession_AfterFpiPassed_IsAllowed()
    {
        var d = await SeedAsync(requiresFpi: true, DispatchStatus.ReadyToStart);
        await SeedFpiOrderAsync(d.DispatchId, "Pass");

        Assert.True(IsSuccess(await _ctrl.StartSession(d.DispatchId, "MC01", null)));
        Assert.Equal(DispatchStatus.InProgress, _context.Dispatches.Single().Status);
        Assert.Single(_context.ProductionSessions);
    }

    /// <summary>不要求首件的工序不得因本次修改而被擋（既有行為迴歸）。</summary>
    [Fact]
    public async Task StartSession_OperationWithoutFpi_IsAllowed()
    {
        var d = await SeedAsync(requiresFpi: false, DispatchStatus.Scheduled);

        Assert.True(IsSuccess(await _ctrl.StartSession(d.DispatchId, "MC01", null)));
        Assert.Equal(DispatchStatus.InProgress, _context.Dispatches.Single().Status);
    }

    // ── 備料完成 DoneSetup（桌機入口，§26.1 缺口 B）────────────────────────────────────

    private async Task SeedOpenSetupAsync(string dispatchId)
    {
        _context.ProductionSetups.Add(new ProductionSetup
        {
            SetupId      = $"S-{dispatchId}",
            DispatchId   = dispatchId,
            WorkOrderId  = WorkOrderId,
            Status       = ProductionSetupStatus.Open,
            SetupStartAt = new DateTime(2026, 7, 27, 7, 0, 0, DateTimeKind.Utc),
            PlantId      = PlantId,
        });
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// 桌機端原本一律設 ReadyToStart、完全不看 RequiresFpi（手機端才有此判定），
    /// 是 §26.1 缺口 B 的第一列。此測試守住兩端語意一致。
    /// </summary>
    [Fact]
    public async Task SetupDone_RequiresFpi_GoesToPendingFpi()
    {
        var d = await SeedAsync(requiresFpi: true, DispatchStatus.SetupInProgress);
        await SeedOpenSetupAsync(d.DispatchId);

        Assert.True(IsSuccess(await _ctrl.DoneSetup(d.DispatchId)));
        Assert.Equal(DispatchStatus.PendingFPI, _context.Dispatches.Single().Status);
    }

    [Fact]
    public async Task SetupDone_NoFpiRequired_GoesToReadyToStart()
    {
        var d = await SeedAsync(requiresFpi: false, DispatchStatus.SetupInProgress);
        await SeedOpenSetupAsync(d.DispatchId);

        Assert.True(IsSuccess(await _ctrl.DoneSetup(d.DispatchId)));
        Assert.Equal(DispatchStatus.ReadyToStart, _context.Dispatches.Single().Status);
    }

    /// <summary>已通過首件者重跑備料不應再退回 PendingFPI（否則會二次卡住）。</summary>
    [Fact]
    public async Task SetupDone_RequiresFpiButAlreadyPassed_GoesToReadyToStart()
    {
        var d = await SeedAsync(requiresFpi: true, DispatchStatus.SetupInProgress);
        await SeedFpiOrderAsync(d.DispatchId, "Pass");
        await SeedOpenSetupAsync(d.DispatchId);

        Assert.True(IsSuccess(await _ctrl.DoneSetup(d.DispatchId)));
        Assert.Equal(DispatchStatus.ReadyToStart, _context.Dispatches.Single().Status);
    }

    // ── ApplyResult：Pass/Fail 的派工狀態異動（桌機／手機共用同一份）────────────

    [Theory]
    [InlineData(true,  DispatchStatus.ReadyToStart)]
    [InlineData(false, DispatchStatus.OnHold)]
    public async Task ApplyResult_SetsDispatchStatus(bool passed, DispatchStatus expected)
    {
        var d = await SeedAsync(requiresFpi: true, DispatchStatus.PendingFPI);
        var now = new DateTime(2026, 7, 27, 8, 0, 0, DateTimeKind.Utc);

        _gate.ApplyResult(d, passed, now);

        Assert.Equal(expected, d.Status);
        Assert.Equal(now, d.UpdatedAt);
    }
}
