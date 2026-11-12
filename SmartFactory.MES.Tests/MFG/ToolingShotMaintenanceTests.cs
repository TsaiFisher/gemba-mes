using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 模具打次累計＋保養排程（mfg-domain.md §14.1 第二階段）。
/// 走完整 DispatchController.CreateReport action 驗證回寫路徑，
/// 另直接呼叫 ToolingShotService 驗證 HO 歸零與邊界行為。
/// </summary>
public class ToolingShotMaintenanceTests : TestBase
{
    private readonly DispatchController _ctrl;
    private readonly ToolingShotService _svc;
    private const string PlantId = "P001";

    public ToolingShotMaintenanceTests()
    {
        _context.CurrentPlantId = PlantId;

        var mockPerm = new Mock<IPermissionService>();
        mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);

        var mockLoc = new Mock<IStringLocalizer<ProjectZSharedResource>>();
        mockLoc.Setup(x => x[It.IsAny<string>()])
               .Returns<string>(k => new LocalizedString(k, k));
        mockLoc.Setup(x => x[It.IsAny<string>(), It.IsAny<object[]>()])
               .Returns<string, object[]>((k, _) => new LocalizedString(k, k));

        var userCtx = new Mock<IUserContext>();
        userCtx.Setup(u => u.UserId).Returns("User1");
        userCtx.Setup(u => u.PlantId).Returns(PlantId);

        var loc = new Mock<IStringLocalizer>();
        loc.Setup(x => x[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, k));
        loc.Setup(x => x[It.IsAny<string>(), It.IsAny<object[]>()])
           .Returns<string, object[]>((k, _) => new LocalizedString(k, k));
        var locFactory = new Mock<IStringLocalizerFactory>();
        locFactory.Setup(f => f.Create(It.IsAny<Type>())).Returns(loc.Object);

        _svc = new ToolingShotService(_context, userCtx.Object, locFactory.Object);

        _ctrl = new DispatchController(
            mockLoc.Object,
            new Mock<IInventoryReservationService>().Object,
            new Mock<IPlantParamService>().Object,
            new Mock<IWorkOrderMaterialReadinessService>().Object,
            new ProductionCostAccrualService(_context),
            new Mock<IStagingSourceService>().Object,
            PassThroughUom.Create(),
            new FpiGateService(_context),
            MfgSimplified(),
            new OperationSequenceGuard(_context, MfgSimplified()))
        {
            ControllerContext = GetControllerContextWithServices(
                permService: mockPerm.Object,
                configureServices: s =>
                {
                    s.AddSingleton(new Mock<IReportEtaNotifier>().Object);
                    s.AddSingleton<IToolingShotService>(_svc);
                })
        };
    }

    // ── seeding ───────────────────────────────────────────────────────────────

    private async Task<Tooling> SeedTooling(string code, int? cavity = 4,
        int? pmInterval = null, decimal accumulated = 0m)
    {
        var t = new Tooling
        {
            ToolingCode      = code,
            ToolingName      = code,
            CavityCount      = cavity,
            PmIntervalShots  = pmInterval,
            AccumulatedShots = accumulated,
            IsActive         = true,
        };
        _context.Toolings.Add(t);
        await _context.SaveChangesAsync();
        return t;
    }

    private async Task<WorkCenterMachine> SeedMachine(string machineCode = "MC01",
        MachineStatus status = MachineStatus.Available)
    {
        var wc = new WorkCenter { WorkCenterCode = "WC01", WorkCenterName = "WC01", PlantId = PlantId };
        if (await _context.Set<WorkCenter>().FindAsync("WC01") == null)
            _context.Add(wc);
        var m = new WorkCenterMachine
        {
            WorkCenterCode = "WC01",
            MachineCode    = machineCode,
            MachineName    = machineCode,
            MachineStatus  = status,
        };
        _context.WorkCenterMachines.Add(m);
        await _context.SaveChangesAsync();
        return m;
    }

    private async Task<Dispatch> SeedDispatch(string id, DispatchStatus status,
        decimal qty = 100m, decimal qtyDone = 0m, string? toolingCode = null,
        string? machineCode = "MC01", string workOrderId = "WO001")
    {
        var d = new Dispatch
        {
            DispatchId   = id,
            WorkOrderId  = workOrderId,
            OperationSeq = 1,
            PlantId      = PlantId,
            Quantity     = qty,
            QtyDone      = qtyDone,
            Status       = status,
            MachineCode  = machineCode,
            ToolingCode  = toolingCode,
        };
        _context.Dispatches.Add(d);
        await _context.SaveChangesAsync();
        return d;
    }

    private async Task SeedSession(string sessionId, string dispatchId)
    {
        _context.ProductionSessions.Add(new ProductionSession
        {
            SessionId   = sessionId,
            DispatchId  = dispatchId,
            WorkOrderId = "WO001",
            Status      = ProductionSessionStatus.Open,
            StartAt     = new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc),
            MachineCode = "MC01",
            PlantId     = PlantId,
        });
        await _context.SaveChangesAsync();
    }

    // ── ① 報工回寫：AccumulatedShots += (QtyOk+QtyNg)/CavityCount ───────────────
    [Fact]
    public async Task CreateReport_AccumulatesShots_ByCavityCount()
    {
        await SeedTooling("M001", cavity: 4);
        await SeedMachine();
        await SeedDispatch("D001", DispatchStatus.InProgress, toolingCode: "M001");
        await SeedSession("S1", "D001");

        await _ctrl.CreateReport("D001", "S1", 6m, 2m, null, ngReasonCode: "SCRATCH");

        var t = await _context.Toolings.FindAsync("M001");
        Assert.Equal(2m, t!.AccumulatedShots);   // (6+2)/4
    }

    // ── ② 無模具代號 / 無穴數（夾治具）→ 不累計、不出錯 ─────────────────────────
    [Fact]
    public async Task CreateReport_NoToolingOrNoCavity_NoAccumulation()
    {
        await SeedTooling("F001", cavity: null);            // 夾治具無穴數
        await SeedMachine();
        await SeedDispatch("D001", DispatchStatus.InProgress, toolingCode: null);
        await SeedDispatch("D002", DispatchStatus.InProgress, toolingCode: "F001");
        await SeedSession("S1", "D001");
        await SeedSession("S2", "D002");

        var r1 = Assert.IsType<JsonResult>(await _ctrl.CreateReport("D001", "S1", 5m, 0m, null));
        var r2 = Assert.IsType<JsonResult>(await _ctrl.CreateReport("D002", "S2", 5m, 0m, null));

        var f = await _context.Toolings.FindAsync("F001");
        Assert.Equal(0m, f!.AccumulatedShots);
        Assert.Empty(_context.MachineStatusOrders);
    }

    // ── ③ 未設保養間隔 → 只累計，不建保養單 ────────────────────────────────────
    [Fact]
    public async Task CreateReport_NoPmInterval_AccumulatesOnly()
    {
        await SeedTooling("M001", cavity: 2, pmInterval: null, accumulated: 999m);
        await SeedMachine();
        await SeedDispatch("D001", DispatchStatus.InProgress, toolingCode: "M001");
        await SeedSession("S1", "D001");

        await _ctrl.CreateReport("D001", "S1", 10m, 0m, null);

        var t = await _context.Toolings.FindAsync("M001");
        Assert.Equal(1004m, t!.AccumulatedShots);
        Assert.Empty(_context.MachineStatusOrders);
    }

    // ── ④ 跨越門檻 → 自動建 HI 保養單＋機台 Maintenance＋§13.4 副作用 ─────────────
    [Fact]
    public async Task CreateReport_CrossesThreshold_CreatesHiOrder_WithSideEffects()
    {
        await SeedTooling("M001", cavity: 2, pmInterval: 100, accumulated: 98m);
        await SeedMachine();
        // 觸發者本身（InProgress、報 6 未達量）→ 應轉 OnHold
        await SeedDispatch("D001", DispatchStatus.InProgress, qty: 100m, toolingCode: "M001");
        // 同機台其他派工：Scheduled → Pending
        await SeedDispatch("D002", DispatchStatus.Scheduled, workOrderId: "WO002");
        await SeedSession("S1", "D001");

        var result = Assert.IsType<JsonResult>(
            await _ctrl.CreateReport("D001", "S1", 6m, 0m, null));

        var t = await _context.Toolings.FindAsync("M001");
        Assert.Equal(101m, t!.AccumulatedShots);   // 98 + 6/2

        var order = Assert.Single(_context.MachineStatusOrders);
        Assert.Equal(MachineEventType.HI, order.EventType);
        var line = Assert.Single(_context.MachineStatusOrderLines);
        Assert.Equal("MC01", line.MachineCode);
        Assert.Equal(MachineStatus.Maintenance, line.TargetStatus);
        Assert.Equal("M001", line.SourceDocId);    // HO 關閉時據此歸零

        var machine = await _context.WorkCenterMachines
            .FirstAsync(m => m.MachineCode == "MC01");
        Assert.Equal(MachineStatus.Maintenance, machine.MachineStatus);

        var d1 = await _context.Dispatches.FindAsync("D001");
        Assert.Equal(DispatchStatus.OnHold, d1!.Status);
        var d2 = await _context.Dispatches.FindAsync("D002");
        Assert.Equal(DispatchStatus.Pending, d2!.Status);
    }

    // ── ⑤ 觸發報工同時達量 Closed → 不得被副作用改回 OnHold ─────────────────────
    [Fact]
    public async Task CreateReport_ClosedByThisReport_NotRevertedToOnHold()
    {
        await SeedTooling("M001", cavity: 1, pmInterval: 10, accumulated: 5m);
        await SeedMachine();
        await SeedDispatch("D001", DispatchStatus.InProgress, qty: 10m, qtyDone: 4m, toolingCode: "M001");
        await SeedSession("S1", "D001");

        // 報 6 → QtyDone=10 達量 Closed；shots 5+6=11 跨門檻 10
        await _ctrl.CreateReport("D001", "S1", 6m, 0m, null);

        var d = await _context.Dispatches.FindAsync("D001");
        Assert.Equal(DispatchStatus.Closed, d!.Status);
        Assert.Single(_context.MachineStatusOrders);
    }

    // ── ⑥ 已逾門檻未保養，再次報工 → 不重複開單（僅跨越當次觸發一次）──────────────
    [Fact]
    public async Task CreateReport_AlreadyPastThreshold_NoDuplicateOrder()
    {
        await SeedTooling("M001", cavity: 1, pmInterval: 10, accumulated: 15m);
        await SeedMachine();
        await SeedDispatch("D001", DispatchStatus.InProgress, toolingCode: "M001");
        await SeedSession("S1", "D001");

        await _ctrl.CreateReport("D001", "S1", 5m, 0m, null);

        var t = await _context.Toolings.FindAsync("M001");
        Assert.Equal(20m, t!.AccumulatedShots);    // 仍累計
        Assert.Empty(_context.MachineStatusOrders); // 但不重複開單
    }

    // ── ⑦ 委外/無機台派工 → 累計但不建停機單 ───────────────────────────────────
    [Fact]
    public async Task Accumulate_NoMachine_AccumulatesWithoutOrder()
    {
        await SeedTooling("M001", cavity: 1, pmInterval: 10, accumulated: 9m);
        var d = await SeedDispatch("D001", DispatchStatus.InProgress,
            toolingCode: "M001", machineCode: null);

        await _svc.AccumulateAsync(d, 5m, DateTime.UtcNow);
        await _context.SaveChangesAsync();

        var t = await _context.Toolings.FindAsync("M001");
        Assert.Equal(14m, t!.AccumulatedShots);
        Assert.Empty(_context.MachineStatusOrders);
    }

    // ── ⑧ HO＋SourceDocId=模具 → 歸零；非 HO 或無 SourceDocId → 不動 ──────────────
    [Fact]
    public async Task ResetOnMaintenanceClosed_HoWithToolingCode_ResetsShots()
    {
        await SeedTooling("M001", cavity: 2, pmInterval: 100, accumulated: 123m);

        await _svc.ResetOnMaintenanceClosedAsync(MachineEventType.HO, "M001", DateTime.UtcNow);
        await _context.SaveChangesAsync();

        var t = await _context.Toolings.FindAsync("M001");
        Assert.Equal(0m, t!.AccumulatedShots);
    }

    [Theory]
    [InlineData(MachineEventType.HI, "M001")]   // 非 HO
    [InlineData(MachineEventType.HO, null)]     // 無 SourceDocId
    [InlineData(MachineEventType.HO, "OTHER")]  // SourceDocId 非模具代號
    public async Task ResetOnMaintenanceClosed_NotApplicable_ShotsUnchanged(
        MachineEventType evt, string? sourceDocId)
    {
        await SeedTooling("M001", cavity: 2, pmInterval: 100, accumulated: 123m);

        await _svc.ResetOnMaintenanceClosedAsync(evt, sourceDocId, DateTime.UtcNow);
        await _context.SaveChangesAsync();

        var t = await _context.Toolings.FindAsync("M001");
        Assert.Equal(123m, t!.AccumulatedShots);
    }
}
