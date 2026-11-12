using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 開工機台狀態閘口（廠別參數 <c>MfgMachineStatusGate</c>，None／Warning／Block，預設 Warning）。
///
/// <para>原本只有排程引擎看機台狀態，上工完全不看——機台報修後，「可開工」的派工照樣能在故障停機的
/// 機台上開工。判定收斂到 <see cref="MachineAvailability"/>，排程、桌機上工、手機上工三處共用。</para>
/// </summary>
public class MachineStatusGateTests : TestBase
{
    private const string PlantId = "P001";
    private const string Machine = "MC01";

    private readonly Mock<IPlantParamService> _params = new();

    public MachineStatusGateTests()
    {
        _context.CurrentPlantId = PlantId;
        _params.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgMaterialGate, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync("None");
    }

    private void Gate(string? mode) =>
        _params.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgMachineStatusGate, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(mode);

    private void StandbySchedulable(bool on) =>
        _params.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgStandbySchedulable, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(on ? "true" : null);

    private void SeedMachine(MachineStatus status, string plantId = PlantId, string machine = Machine, string wc = "WC1")
    {
        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = wc, WorkCenterName = wc, PlantId = plantId });
        _context.WorkCenterMachines.Add(new WorkCenterMachine
        {
            WorkCenterCode = wc, MachineCode = machine, MachineName = machine, MachineStatus = status,
        });
        _context.SaveChanges();
    }

    private Task<MachineStartCheck> Check(string? machine = Machine) =>
        MachineAvailability.CheckStartAsync(_context, _params.Object, PlantId, machine);

    // ── 判定規則 ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null,      MachineStatus.BreakDown,   false, true)]    // 未設定＝預設 Warning
    [InlineData("Warning", MachineStatus.Maintenance, false, true)]
    [InlineData("Block",   MachineStatus.BreakDown,   true,  false)]
    [InlineData("block",   MachineStatus.ExternalRepair, true, false)] // 大小寫不拘
    [InlineData("None",    MachineStatus.BreakDown,   false, false)]
    [InlineData("Block",   MachineStatus.Available,   false, false)]
    public async Task 依閘口模式與機台狀態判定(string? mode, MachineStatus status, bool blocked, bool warned)
    {
        Gate(mode);
        SeedMachine(status);

        var r = await Check();

        Assert.Equal((blocked, warned), (r.Blocked, r.Warned));
        if (blocked || warned) Assert.Equal(status, r.Status);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true,  false)]
    public async Task 待機機台依可排程參數決定_與排程引擎同一條規則(bool standbySchedulable, bool blocked)
    {
        Gate("Block");
        StandbySchedulable(standbySchedulable);
        SeedMachine(MachineStatus.Standby);

        Assert.Equal(blocked, (await Check()).Blocked);
        Assert.Equal(standbySchedulable,
            (await MachineAvailability.UsableStatusesAsync(_params.Object, PlantId)).Contains(MachineStatus.Standby));
    }

    [Fact]
    public async Task 查不到機台或沒有機台_一律放行()
    {
        // 生產線未登錄線別、派工沒掛機台：沒有機台可看，不是機台不能用
        Gate("Block");
        SeedMachine(MachineStatus.BreakDown, plantId: "P999", wc: "WC-OTHER");   // 別廠同代號

        Assert.False((await Check()).Blocked);
        Assert.False((await Check(machine: null)).Blocked);
    }

    // ── 桌機上工 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task 桌機上工_Block且機台故障_不開工()
    {
        Gate("Block");
        SeedMachine(MachineStatus.BreakDown);
        await SeedDispatchAsync();

        var result = await MakeController().StartSession("D1", null, null);

        Assert.False(Prop<bool>(result, "success"));
        Assert.Equal("DispatchMachineUnavailableBlocked", Prop<string>(result, "message"));
        Assert.Empty(_context.ProductionSessions);
        Assert.Equal(DispatchStatus.ReadyToStart, (await _context.Dispatches.SingleAsync()).Status);
    }

    [Fact]
    public async Task 桌機上工_Warning且機台故障_照常開工並回警示()
    {
        Gate("Warning");
        SeedMachine(MachineStatus.BreakDown);
        await SeedDispatchAsync();

        var result = await MakeController().StartSession("D1", null, null);

        Assert.True(Prop<bool>(result, "success"));
        Assert.Equal("DispatchMachineUnavailableWarning", Prop<string>(result, "warning"));
        Assert.Single(_context.ProductionSessions);
    }

    [Fact]
    public async Task 桌機上工_改選的機台故障也要擋()
    {
        Gate("Block");
        SeedMachine(MachineStatus.Available);
        SeedMachine(MachineStatus.BreakDown, machine: "MC02", wc: "WC2");
        await SeedDispatchAsync();

        var result = await MakeController().StartSession("D1", machineCode: "MC02", toolingCode: null);

        Assert.False(Prop<bool>(result, "success"));
    }

    // ── 上工改選機台 → 派工改掛實際機台 ──────────────────────────────────────
    // 原本只記在上工紀錄，派工仍掛排定機台；進生產中後凍結、重跑排程不改機台，
    // 甘特與凍結佔用就一直算在原機台，實際那台被當成空閒。

    [Fact]
    public async Task 桌機上工改選機台_派工改掛實際使用的機台()
    {
        SeedMachine(MachineStatus.Available);
        SeedMachine(MachineStatus.Available, machine: "MC02", wc: "WC2");
        await SeedDispatchAsync();

        var result = await MakeController().StartSession("D1", machineCode: "MC02", toolingCode: null);

        Assert.True(Prop<bool>(result, "success"));
        Assert.Equal("MC02", (await _context.Dispatches.SingleAsync()).MachineCode);
        Assert.Equal("MC02", (await _context.ProductionSessions.SingleAsync()).MachineCode);
    }

    [Fact]
    public async Task 桌機上工改選的機台不存在或已廢棄_拒絕且派工不變()
    {
        SeedMachine(MachineStatus.Available);
        SeedMachine(MachineStatus.Retired, machine: "MC09", wc: "WC2");
        await SeedDispatchAsync();

        foreach (var bad in new[] { "NO-SUCH", "MC09" })
        {
            var result = await MakeController().StartSession("D1", machineCode: bad, toolingCode: null);
            Assert.False(Prop<bool>(result, "success"));
            Assert.Equal("MachineNotFound", Prop<string>(result, "message"));
        }

        Assert.Empty(_context.ProductionSessions);
        var dispatch = await _context.Dispatches.SingleAsync();
        Assert.Equal(Machine, dispatch.MachineCode);
        Assert.Equal(DispatchStatus.ReadyToStart, dispatch.Status);
    }

    [Fact]
    public async Task 未登錄線別的生產線派工_上工選了機台也不改掛()
    {
        // 排程把整條線視為單一資源，MachineCode 存工作中心代碼本身
        SeedMachine(MachineStatus.Available, machine: "MC02", wc: "WC2");
        await SeedDispatchAsync(machineCode: "WC1");

        var result = await MakeController().StartSession("D1", machineCode: "MC02", toolingCode: null);

        Assert.True(Prop<bool>(result, "success"));
        Assert.Equal("WC1", (await _context.Dispatches.SingleAsync()).MachineCode);
    }

    // ── 輔助 ─────────────────────────────────────────────────────────────────

    private async Task SeedDispatchAsync(string machineCode = Machine)
    {
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = "WO1", ProductionBatchId = "B1", ProductCode = "P-1",
            RoutingId = "R1", Quantity = 10m, PlantId = PlantId, Status = WorkOrderStatus.Released,
        });
        _context.WorkOrderOperations.Add(new WorkOrderOperation
        {
            WorkOrderId = "WO1", OperationSeq = 10, OperationName = "OP", WorkCenterId = "WC1",
        });
        _context.Dispatches.Add(new Dispatch
        {
            DispatchId = "D1", WorkOrderId = "WO1", OperationSeq = 10, PlantId = PlantId,
            Quantity = 10m, Status = DispatchStatus.ReadyToStart, MachineCode = machineCode,
        });
        await _context.SaveChangesAsync();
    }

    private DispatchController MakeController()
    {
        var mockPerm = new Mock<IPermissionService>();
        mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);

        var mockLoc = new Mock<IStringLocalizer<ProjectZSharedResource>>();
        mockLoc.Setup(x => x[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, k));
        mockLoc.Setup(x => x[It.IsAny<string>(), It.IsAny<object[]>()])
               .Returns<string, object[]>((k, _) => new LocalizedString(k, k));

        var mode = MfgSimplified(false);
        return new DispatchController(
            mockLoc.Object,
            new Mock<IInventoryReservationService>().Object,
            _params.Object,
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

    private static T Prop<T>(IActionResult result, string name)
    {
        var value = Assert.IsType<JsonResult>(result).Value!;
        return (T)value.GetType().GetProperty(name)!.GetValue(value)!;
    }
}
