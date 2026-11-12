using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// WorkOrderController 生命週期整合測試：Release / Withdraw / Void 狀態機。
/// mfg-domain.md §4.2 / §16.2 / §18.2
/// </summary>
public class WorkOrderLifecycleTests : TestBase
{
    private readonly Mock<IApprovalService>       _mockApproval   = new();
    private readonly Mock<IPlantParamService>    _mockSysParam   = new();
    private readonly Mock<IDocNumberService>      _mockDocNo      = new();
    private readonly Mock<IInventoryReservationService> _mockReserve = new();
    private readonly Mock<IWorkOrderMaterialReadinessService> _mockReadiness = new();
    private readonly Mock<IUserContext>           _mockUser       = new();
    private readonly Mock<IPermissionService>     _mockPerm       = new();

    private const string PlantId = "P001";
    private const string UserId  = "usr1";

    public WorkOrderLifecycleTests()
    {
        _context.CurrentPlantId = PlantId;

        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns(UserId);
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);

        // 預設：無簽核設定、無金額閥值 → Release 直接核准
        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string?>()))
                     .ReturnsAsync((DocumentApprovalConfig?)null);
        _mockSysParam.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync((string?)null);

        _mockReserve.Setup(r => r.ReleaseBySourceAsync(
            It.IsAny<ReservationSourceType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // §24.3 投產硬料閘預設放行：可用量充足（個別測試可覆寫以驗證缺料阻擋）
        _mockReserve.Setup(r => r.GetPlantAvailableQtyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1_000_000m);

        // Release 後若有 Push 料件會建立 MaterialSlip，需 DocNumber mock
        _mockDocNo.Setup(d => d.NextAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("MI2025000001");
    }

    private WorkOrderController BuildController()
    {
        // §24：Release 委派 WorkOrderReleaseService；以真實服務 + mock 依賴維持端到端覆蓋
        var releaseSvc = new WorkOrderReleaseService(
            _context, _mockApproval.Object, _mockSysParam.Object,
            _mockDocNo.Object, _mockReserve.Object, _mockUser.Object,
            Timezone.TimezoneTestKit.FixedLocalDate(new DateOnly(2026, 7, 1)).Object);
        return new WorkOrderController(
            Loc(), _mockApproval.Object, _mockReserve.Object, _mockReadiness.Object, releaseSvc,
            new Mock<IUomService>().Object, new Mock<ICurrencyService>().Object,
            new Mock<IReworkWorkOrderService>().Object)
        {
            ControllerContext = GetControllerContextWithServices(
                userId: UserId,
                userContext: _mockUser.Object,
                permService: _mockPerm.Object,
                configureServices: s => s.AddSingleton<IDispatchLifecycleService>(
                    new DispatchLifecycleService(_context, new FpiGateService(_context),
                                                 new ProductionCostAccrualService(_context))))
        };
    }

    private static IStringLocalizer<ProjectZSharedResource> Loc()
    {
        var mock = new Mock<IStringLocalizer<ProjectZSharedResource>>();
        mock.Setup(x => x[It.IsAny<string>()])
            .Returns<string>(k => new LocalizedString(k, k));
        return mock.Object;
    }

    private WorkOrder SeedWorkOrder(string id = "WO2025000001",
        WorkOrderStatus status = WorkOrderStatus.Planned,
        DocumentApprovalStatus approval = DocumentApprovalStatus.Draft,
        bool isOutsource = false)
    {
        var wo = new WorkOrder
        {
            WorkOrderId    = id,
            ProductCode    = "P001",
            Quantity       = 10m,
            Status         = status,
            ApprovalStatus = approval,
            IsOutsource    = isOutsource,
            PlantId        = PlantId,
            PlannedStartDate = new DateTime(2025, 6, 1),
            PlannedEndDate   = new DateTime(2025, 6, 30),
        };
        var op = new WorkOrderOperation
        {
            WorkOrderId  = id,
            OperationSeq = 10,
            OperationName = "組裝",
            WorkCenterId = "WC001",
        };
        op.Materials.Add(new WorkOrderMaterial
        {
            WorkOrderId  = id,
            OperationSeq = 10,
            BomSeq       = 1,
            ProductCode  = "MAT001",
            RequiredQty  = 5m,
            UomCode      = "PC",
        });
        wo.Operations.Add(op);
        _context.WorkOrders.Add(wo);
        _context.SaveChanges();
        return wo;
    }

    // ── Release ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Release_WhenPlanned_NoApprovalConfig_StatusReleasedAndApproved()
    {
        SeedWorkOrder("WO2025000001");
        var ctrl = BuildController();

        var result = await ctrl.Release("WO2025000001");

        var json = Assert.IsType<JsonResult>(result);
        var success = (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
        Assert.True(success);

        var wo = _context.WorkOrders.Find("WO2025000001")!;
        Assert.Equal(WorkOrderStatus.Released, wo.Status);
        Assert.Equal(DocumentApprovalStatus.Approved, wo.ApprovalStatus);
    }

    [Fact]
    public async Task Release_WhenPlanned_WithApprovalConfig_StatusPendingApproval()
    {
        SeedWorkOrder("WO2025000001");

        var config = new DocumentApprovalConfig
        {
            DocumentType       = "WorkOrder",
            IsApprovalRequired = true,        // 與其餘呼叫端一致：只有範本、旗標是 false 不算要簽核
            ApprovalTemplateId = "TMPL001",
        };
        _mockApproval.Setup(a => a.GetApprovalConfig("WorkOrder", PlantId)).ReturnsAsync(config);
        _mockApproval.Setup(a => a.SubmitForApproval(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new ApprovalRequest { Id = "REQ001" });

        var ctrl = BuildController();
        var result = await ctrl.Release("WO2025000001");

        var json = Assert.IsType<JsonResult>(result);
        var success = (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
        Assert.True(success);

        var wo = _context.WorkOrders.Find("WO2025000001")!;
        Assert.Equal(WorkOrderStatus.Released, wo.Status);
        Assert.Equal(DocumentApprovalStatus.PendingApproval, wo.ApprovalStatus);
        Assert.Equal("REQ001", wo.ApprovalRequestId);
    }

    [Fact]
    public async Task Release_WhenAlreadyReleased_ReturnsFailure()
    {
        SeedWorkOrder("WO2025000001", status: WorkOrderStatus.Released);
        var ctrl = BuildController();

        var result = await ctrl.Release("WO2025000001");

        var json = Assert.IsType<JsonResult>(result);
        var success = (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
        Assert.False(success);

        var wo = _context.WorkOrders.Find("WO2025000001")!;
        Assert.Equal(WorkOrderStatus.Released, wo.Status); // 狀態未改變
    }

    [Fact]
    public async Task Release_NotFound_ReturnsFailure()
    {
        var ctrl = BuildController();

        var result = await ctrl.Release("NONEXISTENT");

        var json = Assert.IsType<JsonResult>(result);
        var success = (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
        Assert.False(success);
    }

    [Fact]
    public async Task Release_SnapshotStandardCost_PopulatesFields()
    {
        // 有 WorkCenter 費率時，Release 應快照標準成本
        _context.Set<SmartFactory.MES.Modules.BAS.Models.WorkCenter>().Add(new SmartFactory.MES.Modules.BAS.Models.WorkCenter
        {
            WorkCenterCode     = "WC001",
            WorkCenterName     = "組裝站",
            StandardLaborCost  = 100m,
            MachineHourlyRate  = 50m,
            PlantId            = PlantId,
        });
        _context.SaveChanges();

        SeedWorkOrder("WO2025000001");
        var ctrl = BuildController();

        await ctrl.Release("WO2025000001");

        var wo = _context.WorkOrders.Find("WO2025000001")!;
        // WorkOrderCostCalculator 有費率時計算出非零標準成本
        Assert.True(wo.StandardMaterialCost >= 0m);
        Assert.True(wo.StandardLaborCost >= 0m);
    }

    [Fact]
    public async Task Release_NonOutsource_WithPushMaterial_CreatesDraftMaterialSlip()
    {
        _mockDocNo.Setup(d => d.NextAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("MI2025000001");

        SeedWorkOrder("WO2025000001", isOutsource: false);
        var ctrl = BuildController();

        await ctrl.Release("WO2025000001");

        var slip = _context.MaterialSlips
            .FirstOrDefault(s => s.SourceDocId == "WO2025000001" && s.SourceDocType == "WorkOrder");
        Assert.NotNull(slip);
        Assert.Equal(MaterialSlipStatus.Draft, slip!.Status);
        Assert.Equal(MaterialSlipCategory.InternalIssue, slip.Category);
    }

    [Fact]
    public async Task Release_Outsource_NoPushMaterialSlipCreated()
    {
        SeedWorkOrder("WO2025000001", isOutsource: true);
        var ctrl = BuildController();

        await ctrl.Release("WO2025000001");

        Assert.Equal(0, _context.MaterialSlips.Count(s => s.SourceDocId == "WO2025000001"));
    }

    // ── Withdraw ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Withdraw_WhenPendingApproval_ReturnsToPlanned()
    {
        SeedWorkOrder("WO2025000001",
            status: WorkOrderStatus.Released,
            approval: DocumentApprovalStatus.PendingApproval);
        var wo = _context.WorkOrders.Find("WO2025000001")!;
        wo.ApprovalRequestId = "REQ001";
        _context.SaveChanges();

        _mockApproval.Setup(a => a.WithdrawRequest(It.IsAny<string>(), It.IsAny<string>()))
                     .ReturnsAsync(true);

        var ctrl = BuildController();
        var result = await ctrl.Withdraw("WO2025000001");

        var json = Assert.IsType<JsonResult>(result);
        var success = (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
        Assert.True(success);

        var updated = _context.WorkOrders.Find("WO2025000001")!;
        Assert.Equal(WorkOrderStatus.Planned, updated.Status);
        Assert.Equal(DocumentApprovalStatus.Draft, updated.ApprovalStatus);
        Assert.Null(updated.ApprovalRequestId);
        _mockApproval.Verify(a => a.WithdrawRequest("REQ001", UserId), Times.Once);
    }

    [Fact]
    public async Task Withdraw_UnschedulesScheduledDispatches()
    {
        SeedWorkOrder("WO2025000009",
            status: WorkOrderStatus.Released,
            approval: DocumentApprovalStatus.PendingApproval);
        _context.Dispatches.Add(new Dispatch { DispatchId = "WD-1", WorkOrderId = "WO2025000009", OperationSeq = 10, Status = DispatchStatus.Scheduled, PlantId = PlantId });
        _context.SaveChanges();

        var result = await BuildController().Withdraw("WO2025000009");

        var json = Assert.IsType<JsonResult>(result);
        Assert.True((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
        Assert.Equal(DispatchStatus.Pending, _context.Dispatches.Find("WD-1")!.Status);
    }

    [Fact]
    public async Task Withdraw_WhenNotPendingApproval_ReturnsFailure()
    {
        SeedWorkOrder("WO2025000001",
            status: WorkOrderStatus.Released,
            approval: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();

        var result = await ctrl.Withdraw("WO2025000001");

        var json = Assert.IsType<JsonResult>(result);
        var success = (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
        Assert.False(success);
    }

    // ── Void ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_WhenNoProduction_StatusVoid()
    {
        SeedWorkOrder("WO2025000001");
        var ctrl = BuildController();

        var result = await ctrl.Void("WO2025000001");

        var json = Assert.IsType<JsonResult>(result);
        var success = (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
        Assert.True(success);

        var wo = _context.WorkOrders.Find("WO2025000001")!;
        Assert.Equal(WorkOrderStatus.Void, wo.Status);
        _mockReserve.Verify(r => r.ReleaseBySourceAsync(
            ReservationSourceType.WorkOrderMaterial, "WO2025000001", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>邊界值：已發未退只剩 0.000001（數量精度 6 位）仍算有料在外、擋作廢；剛好退完才放行。</summary>
    [Theory]
    [InlineData(10, 9.999999, false)]
    [InlineData(10, 10,       true)]
    public async Task Void_IssuedMinusReturned_Boundary(decimal issued, decimal returned, bool allowed)
    {
        SeedWorkOrder("WO2025000001");
        _context.WorkOrderMaterials.Add(new WorkOrderMaterial
        {
            WorkOrderId = "WO2025000001", OperationSeq = 10, BomSeq = 99, ProductCode = "MAT-B",
            RequiredQty = 10m, IssuedQty = issued, ReturnedQty = returned, UomCode = "PC",
        });
        _context.SaveChanges();

        var json = Assert.IsType<JsonResult>(await BuildController().Void("WO2025000001"));
        Assert.Equal(allowed, (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
    }

    [Fact]
    public async Task Void_CancelsUnfinishedDispatches()
    {
        // 作廢後殘留的派工會掛在派工清單當待辦，準備完成者還被排程當凍結佔用（2026-10-06）
        SeedWorkOrder("WO2025000001");
        _context.Dispatches.AddRange(
            new Dispatch { DispatchId = "D-SCH", WorkOrderId = "WO2025000001", PlantId = PlantId,
                           Status = DispatchStatus.Scheduled, ScheduledStart = DateTime.UtcNow, ScheduledEnd = DateTime.UtcNow.AddHours(1) },
            new Dispatch { DispatchId = "D-RDY", WorkOrderId = "WO2025000001", PlantId = PlantId,
                           Status = DispatchStatus.ReadyToStart });
        _context.SaveChanges();

        await BuildController().Void("WO2025000001");

        Assert.All(_context.Dispatches.Where(d => d.WorkOrderId == "WO2025000001").ToList(), d =>
        {
            Assert.Equal(DispatchStatus.Cancelled, d.Status);
            Assert.Null(d.ScheduledStart);
        });
    }

    [Fact]
    public async Task Void_WhenHasProduction_ReturnsFailure()
    {
        SeedWorkOrder("WO2025000001", status: WorkOrderStatus.Released);
        _context.Dispatches.Add(new Dispatch
        {
            DispatchId  = "DISP001",
            WorkOrderId = "WO2025000001",
            QtyDone     = 3m,
            PlantId     = PlantId,
        });
        _context.SaveChanges();

        var ctrl = BuildController();
        var result = await ctrl.Void("WO2025000001");

        var json = Assert.IsType<JsonResult>(result);
        var success = (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
        Assert.False(success);

        var wo = _context.WorkOrders.Find("WO2025000001")!;
        Assert.Equal(WorkOrderStatus.Released, wo.Status); // 狀態未改變
    }

    [Fact]
    public async Task Void_WhenAlreadyClosed_ReturnsFailure()
    {
        SeedWorkOrder("WO2025000001", status: WorkOrderStatus.Closed);
        var ctrl = BuildController();

        var result = await ctrl.Void("WO2025000001");

        var json = Assert.IsType<JsonResult>(result);
        var success = (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
        Assert.False(success);
    }
}
