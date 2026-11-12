using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.QCR.Controllers;
using SmartFactory.MES.Modules.QCR.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QCR;

/// <summary>
/// 桌機 FPI 首件登錄（mfg-domain.md §26.2-②）。
///
/// 桌機端原本完全沒有 FPI 路徑：檢驗單無建立入口、<c>Submit</c> 也只有 Fail→OnHold
/// 而無 Pass→解鎖分支 → 派工一旦進 PendingFPI 就永遠出不來（§26.1 缺口 B）。
/// 本檔守住補齊後的兩條路徑（新登錄 SubmitFpi、既有待檢單 Submit）都能正確解鎖。
/// </summary>
public class InspectionOrderFpiTests : TestBase
{
    private const string PlantId = "P001";
    private const string DispatchId = "D-FPI-W1";
    private const string WorkOrderId = "WO-FPI-W1";

    private readonly InspectionOrderController _ctrl;

    public InspectionOrderFpiTests()
    {
        _context.CurrentPlantId = PlantId;

        var mockPerm = new Mock<IPermissionService>();
        mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);

        var mockLoc = new Mock<IStringLocalizer<ProjectZSharedResource>>();
        mockLoc.Setup(x => x[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, k));
        mockLoc.Setup(x => x[It.IsAny<string>(), It.IsAny<object[]>()])
               .Returns<string, object[]>((k, _) => new LocalizedString(k, k));

        var mockDateTime = new Mock<IDateTimeService>();
        mockDateTime.Setup(d => d.Now).Returns(new DateTime(2026, 7, 27, 8, 0, 0, DateTimeKind.Utc));

        var mockSampling = new Mock<ISamplingPlanService>();
        mockSampling.Setup(s => s.GetDecisionAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<decimal>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SamplingDecision(true, 4, 3, "Normal"));

        _ctrl = new InspectionOrderController(
            mockLoc.Object,
            new Mock<IExportService>().Object,
            mockDateTime.Object,
            new Mock<IIqcInspectionService>().Object,
            new FpiGateService(_context),
            mockSampling.Object)
        {
            ControllerContext = GetControllerContextWithServices(permService: mockPerm.Object)
        };
    }

    private async Task<Dispatch> SeedAsync(DispatchStatus status = DispatchStatus.PendingFPI)
    {
        _context.WorkOrderOperations.Add(new WorkOrderOperation
        {
            WorkOrderId = WorkOrderId, OperationSeq = 1, OperationName = "OP",
            WorkCenterId = "WC1", RequiresFpi = true,
        });
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = WorkOrderId, ProductionBatchId = "B1", ProductCode = "P-1",
            RoutingId = "R1", Quantity = 10m, PlantId = PlantId, Status = WorkOrderStatus.Released,
        });
        var d = new Dispatch
        {
            DispatchId = DispatchId, WorkOrderId = WorkOrderId, OperationSeq = 1,
            PlantId = PlantId, Quantity = 10m, Status = status, MachineCode = "MC01",
        };
        _context.Dispatches.Add(d);
        await _context.SaveChangesAsync();
        return d;
    }

    private static bool IsSuccess(IActionResult result)
    {
        dynamic data = Assert.IsType<JsonResult>(result).Value!;
        return (bool)data.GetType().GetProperty("success")!.GetValue(data)!;
    }

    private FpiSubmitVm Vm(int failQty) => new()
    {
        DispatchId = DispatchId, WorkOrderId = WorkOrderId, OperationSeq = 1,
        ProductCode = "P-1", LotSize = 10m, VerificationLevel = 4,
        SampleQty = 3, FailQty = failQty,
    };

    // ── 待檢清單 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 清單以事實判定（工序 RequiresFpi 且無 Pass 單），不以 PendingFPI 狀態判定——
    /// 否則未經備料的派工永遠不會出現在清單上，品管無從登錄。
    /// </summary>
    [Fact]
    public async Task PendingFpi_ListsScheduledDispatchThatSkippedSetup()
    {
        await SeedAsync(DispatchStatus.Scheduled);

        var rows = Assert.IsAssignableFrom<IReadOnlyList<FpiPendingRow>>(
            Assert.IsType<ViewResult>(await _ctrl.PendingFpi()).Model);

        Assert.Equal(DispatchId, Assert.Single(rows).DispatchId);
    }

    [Fact]
    public async Task PendingFpi_ExcludesDispatchThatAlreadyPassed()
    {
        await SeedAsync(DispatchStatus.ReadyToStart);
        _context.InspectionOrders.Add(new InspectionOrder
        {
            InspectionType = "FPI", OrderType = InspectionOrderType.FPI, PlantId = PlantId,
            SourceDocId = DispatchId, ProductCode = "P-1", LotSize = 10m, Status = "Pass",
        });
        await _context.SaveChangesAsync();

        var rows = Assert.IsAssignableFrom<IReadOnlyList<FpiPendingRow>>(
            Assert.IsType<ViewResult>(await _ctrl.PendingFpi()).Model);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task PendingFpi_ExcludesOperationNotRequiringFpi()
    {
        _context.WorkOrderOperations.Add(new WorkOrderOperation
        {
            WorkOrderId = "WO-X", OperationSeq = 1, OperationName = "OP",
            WorkCenterId = "WC1", RequiresFpi = false,
        });
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = "WO-X", ProductionBatchId = "B1", ProductCode = "P-2",
            RoutingId = "R1", Quantity = 5m, PlantId = PlantId, Status = WorkOrderStatus.Released,
        });
        _context.Dispatches.Add(new Dispatch
        {
            DispatchId = "D-X", WorkOrderId = "WO-X", OperationSeq = 1,
            PlantId = PlantId, Quantity = 5m, Status = DispatchStatus.Scheduled,
        });
        await _context.SaveChangesAsync();

        var rows = Assert.IsAssignableFrom<IReadOnlyList<FpiPendingRow>>(
            Assert.IsType<ViewResult>(await _ctrl.PendingFpi()).Model);

        Assert.Empty(rows);
    }

    // ── SubmitFpi：新登錄 ────────────────────────────────────────────────────

    [Fact]
    public async Task SubmitFpi_Pass_CreatesPassedOrderAndUnlocksDispatch()
    {
        await SeedAsync();

        Assert.True(IsSuccess(await _ctrl.SubmitFpi(Vm(failQty: 0))));

        var order = Assert.Single(_context.InspectionOrders.ToList());
        Assert.Equal("Pass", order.Status);
        Assert.Equal("FPI", order.InspectionType);
        Assert.Equal(InspectionOrderType.FPI, order.OrderType);
        Assert.Equal(DispatchId, order.SourceDocId);
        Assert.Equal(DispatchStatus.ReadyToStart, _context.Dispatches.Single().Status);
    }

    [Fact]
    public async Task SubmitFpi_Fail_PutsDispatchOnHold()
    {
        await SeedAsync();

        Assert.True(IsSuccess(await _ctrl.SubmitFpi(Vm(failQty: 1))));

        Assert.Equal("Fail", _context.InspectionOrders.Single().Status);
        Assert.Equal(DispatchStatus.OnHold, _context.Dispatches.Single().Status);
    }

    /// <summary>已通過者不得重複登錄——否則會重複計入抽樣狀態並把 OnHold 的派工誤解鎖。</summary>
    [Fact]
    public async Task SubmitFpi_AlreadyPassed_IsRejected()
    {
        await SeedAsync();
        _context.InspectionOrders.Add(new InspectionOrder
        {
            InspectionType = "FPI", OrderType = InspectionOrderType.FPI, PlantId = PlantId,
            SourceDocId = DispatchId, ProductCode = "P-1", LotSize = 10m, Status = "Pass",
        });
        await _context.SaveChangesAsync();

        Assert.False(IsSuccess(await _ctrl.SubmitFpi(Vm(failQty: 0))));
        Assert.Single(_context.InspectionOrders.ToList());
    }

    /// <summary>已有 Pending 首件單時補完它，不另開一張（與手機端同語意）。</summary>
    [Fact]
    public async Task SubmitFpi_ExistingPendingOrder_IsCompletedInPlace()
    {
        await SeedAsync();
        _context.InspectionOrders.Add(new InspectionOrder
        {
            InspectionOrderId = "IO-PENDING", InspectionType = "FPI",
            OrderType = InspectionOrderType.FPI, PlantId = PlantId,
            SourceDocId = DispatchId, ProductCode = "P-1", LotSize = 10m, Status = "Pending",
        });
        await _context.SaveChangesAsync();

        Assert.True(IsSuccess(await _ctrl.SubmitFpi(Vm(failQty: 0))));

        var order = Assert.Single(_context.InspectionOrders.ToList());
        Assert.Equal("IO-PENDING", order.InspectionOrderId);
        Assert.Equal("Pass", order.Status);
    }

    // ── Submit：既有待檢單走明細面板提交 ──────────────────────────────────────

    /// <summary>
    /// §26.1 缺口 B 的核心迴歸：舊版 Submit 只有 Fail→OnHold，FPI 判 Pass 後派工仍卡在
    /// PendingFPI（桌機沒有任何解鎖路徑）。
    /// </summary>
    [Fact]
    public async Task Submit_FpiPass_UnlocksDispatch()
    {
        await SeedAsync();
        _context.InspectionOrders.Add(new InspectionOrder
        {
            InspectionOrderId = "IO-1", InspectionType = "FPI",
            OrderType = InspectionOrderType.FPI, PlantId = PlantId,
            SourceDocId = DispatchId, ProductCode = "P-1", LotSize = 10m, Status = "Pending",
        });
        await _context.SaveChangesAsync();

        await _ctrl.Submit("IO-1", new InspectionResultSubmitVm { ActualSampleQty = 3, FailQty = 0 });

        Assert.Equal("Pass", _context.InspectionOrders.Single().Status);
        Assert.Equal(DispatchStatus.ReadyToStart, _context.Dispatches.Single().Status);
    }

    [Fact]
    public async Task Submit_FpiFail_PutsDispatchOnHold()
    {
        await SeedAsync();
        _context.InspectionOrders.Add(new InspectionOrder
        {
            InspectionOrderId = "IO-1", InspectionType = "FPI",
            OrderType = InspectionOrderType.FPI, PlantId = PlantId,
            SourceDocId = DispatchId, ProductCode = "P-1", LotSize = 10m, Status = "Pending",
        });
        await _context.SaveChangesAsync();

        await _ctrl.Submit("IO-1", new InspectionResultSubmitVm { ActualSampleQty = 3, FailQty = 2 });

        Assert.Equal(DispatchStatus.OnHold, _context.Dispatches.Single().Status);
    }

    /// <summary>IPQC 既有行為不得被 FPI 分支影響：只有生產中的派工才因不良轉 OnHold。</summary>
    [Fact]
    public async Task Submit_IpqcFail_KeepsExistingBehaviour()
    {
        await SeedAsync(DispatchStatus.InProgress);
        _context.InspectionOrders.Add(new InspectionOrder
        {
            InspectionOrderId = "IO-2", InspectionType = "IPQC",
            OrderType = InspectionOrderType.IPQC, PlantId = PlantId,
            SourceDocId = DispatchId, ProductCode = "P-1", LotSize = 10m, Status = "Pending",
        });
        await _context.SaveChangesAsync();

        await _ctrl.Submit("IO-2", new InspectionResultSubmitVm { ActualSampleQty = 3, FailQty = 1 });

        Assert.Equal(DispatchStatus.OnHold, _context.Dispatches.Single().Status);
    }

    /// <summary>IPQC 判 Pass 不得動到派工狀態（FPI 分支誤攔會讓生產中的派工被打回 ReadyToStart）。</summary>
    [Fact]
    public async Task Submit_IpqcPass_DoesNotTouchDispatchStatus()
    {
        await SeedAsync(DispatchStatus.InProgress);
        _context.InspectionOrders.Add(new InspectionOrder
        {
            InspectionOrderId = "IO-3", InspectionType = "IPQC",
            OrderType = InspectionOrderType.IPQC, PlantId = PlantId,
            SourceDocId = DispatchId, ProductCode = "P-1", LotSize = 10m, Status = "Pending",
        });
        await _context.SaveChangesAsync();

        await _ctrl.Submit("IO-3", new InspectionResultSubmitVm { ActualSampleQty = 3, FailQty = 0 });

        Assert.Equal(DispatchStatus.InProgress, _context.Dispatches.Single().Status);
    }
}
