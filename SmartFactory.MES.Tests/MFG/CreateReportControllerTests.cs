using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 報工（CreateReport）Controller + DB 整合單元測試（mfg-domain.md §4.3 / §5.3 / §6）。
///
/// 補足缺口：原 DispatchBusinessRulesTests 僅測純函式（IsOverReport / 完工條件布林），
/// 未實際呼叫 action 驗證「資料寫入正確性」——而報工正是最核心的資料路徑
/// （寫入 ProductionReport、累計 Dispatch.QtyDone、達量自動 Closed、連動工單 Completed、
/// 觸發 ETA 重算）。本檔以 InMemory DB 走完整 action，逐欄斷言落地結果。
/// </summary>
public class CreateReportControllerTests : TestBase
{
    private readonly DispatchController _ctrl;
    private readonly Mock<IPermissionService> _mockPerm;
    private readonly Mock<IReportEtaNotifier> _mockNotifier;
    private const string PlantId = "P001";
    private readonly Mock<IStringLocalizer<ProjectZSharedResource>> _mockLoc = new();

    public CreateReportControllerTests()
    {
        _context.CurrentPlantId = PlantId;

        _mockPerm = new Mock<IPermissionService>();
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);

        _mockNotifier = new Mock<IReportEtaNotifier>();

        _mockLoc.Setup(x => x[It.IsAny<string>()])
               .Returns<string>(k => new LocalizedString(k, k));
        _mockLoc.Setup(x => x[It.IsAny<string>(), It.IsAny<object[]>()])
               .Returns<string, object[]>((k, _) => new LocalizedString(k, k));

        _ctrl = BuildController(PassThroughUom.Create());
    }

    private DispatchController BuildController(IUomService uom)
    {
        return new DispatchController(
            _mockLoc.Object,
            new Mock<IInventoryReservationService>().Object,
            new Mock<IPlantParamService>().Object,
            new Mock<IWorkOrderMaterialReadinessService>().Object,
            new ProductionCostAccrualService(_context),
            new Mock<IStagingSourceService>().Object,
            uom,
            new FpiGateService(_context),
            MfgSimplified(),
            new OperationSequenceGuard(_context, MfgSimplified()))
        {
            // CreateReport 報工成功後以 GetRequiredService<IReportEtaNotifier> 觸發 ETA 重算、
            // 以 IToolingShotService 回寫模具打次，皆須註冊於 RequestServices，否則解析丟例外。
            ControllerContext = GetControllerContextWithServices(
                permService: _mockPerm.Object,
                configureServices: s =>
                {
                    s.AddSingleton(_mockNotifier.Object);
                    s.AddSingleton<IToolingShotService>(BuildToolingShotService());
                })
        };
    }

    private ToolingShotService BuildToolingShotService()
    {
        var userCtx = new Mock<IUserContext>();
        userCtx.Setup(u => u.UserId).Returns("User1");

        var loc = new Mock<IStringLocalizer>();
        loc.Setup(x => x[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, k));
        loc.Setup(x => x[It.IsAny<string>(), It.IsAny<object[]>()])
           .Returns<string, object[]>((k, _) => new LocalizedString(k, k));
        var locFactory = new Mock<IStringLocalizerFactory>();
        locFactory.Setup(f => f.Create(It.IsAny<Type>())).Returns(loc.Object);

        return new ToolingShotService(_context, userCtx.Object, locFactory.Object);
    }

    // ── 報工數量依產品庫存單位捨入（CLAUDE.md〈數量欄位 UOM 小數點〉：POST 儲存前四捨五入）──
    [Theory]
    [InlineData("KG", 2, 3.456, 3.46)]   // 小數單位：捨到單位位數，不是整數
    [InlineData("PCS", 0, 4.6, 5)]       // 整數單位：四捨五入成整數
    public async Task CreateReport_RoundsQty_ByProductStockUomDecimals(string uom, int dp, double input, double expected)
    {
        _context.Uoms.Add(new SmartFactory.MES.Modules.BAS.Models.Uom { UomCode = uom, UomName = uom, DecimalPlaces = dp });
        _context.Products.Add(new SmartFactory.MES.Modules.BAS.Models.Product
            { ProductCode = "P-001", ProductName = "P-001", StockUom = uom });
        await SeedWorkOrder("WO001", WorkOrderStatus.InProgress);
        await SeedDispatch("D001", DispatchStatus.InProgress, qty: 100m);
        await SeedSession("S1", "D001");
        var ctrl = BuildController(new UomService(_context,
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())));

        Assert.True(IsSuccess(Assert.IsType<JsonResult>(
            await ctrl.CreateReport("D001", "S1", (decimal)input, (decimal)input, null))));

        var report = Assert.Single(_context.ProductionReports);
        Assert.Equal((decimal)expected, report.QtyOk);
        Assert.Equal((decimal)expected, report.QtyNg);
        Assert.Equal((decimal)expected, (await _context.Dispatches.FindAsync("D001"))!.QtyDone);
    }

    // ── seeding ───────────────────────────────────────────────────────────────

    private async Task<Dispatch> SeedDispatch(
        string id, DispatchStatus status, decimal qty = 10m, decimal qtyDone = 0m,
        string workOrderId = "WO001")
    {
        var d = new Dispatch
        {
            DispatchId     = id,
            WorkOrderId    = workOrderId,
            OperationSeq   = 1,
            PlantId        = PlantId,
            Quantity       = qty,
            QtyDone        = qtyDone,
            Status         = status,
            MachineCode    = "MC01",
        };
        _context.Dispatches.Add(d);
        await _context.SaveChangesAsync();
        return d;
    }

    private async Task<ProductionSession> SeedSession(string sessionId, string dispatchId,
        string workOrderId = "WO001")
    {
        var s = new ProductionSession
        {
            SessionId   = sessionId,
            DispatchId  = dispatchId,
            WorkOrderId = workOrderId,
            Status      = ProductionSessionStatus.Open,
            StartAt     = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            MachineCode = "MC01",
            PlantId     = PlantId,
        };
        _context.ProductionSessions.Add(s);
        await _context.SaveChangesAsync();
        return s;
    }

    private async Task<WorkOrder> SeedWorkOrder(string workOrderId, WorkOrderStatus status)
    {
        var wo = new WorkOrder
        {
            WorkOrderId       = workOrderId,
            ProductionBatchId = "B001",
            ProductCode       = "P-001",
            RoutingId         = "R001",
            Quantity          = 10m,
            Status            = status,
            PlantId           = PlantId,
        };
        _context.WorkOrders.Add(wo);
        await _context.SaveChangesAsync();
        return wo;
    }

    private static bool IsSuccess(JsonResult json)
    {
        dynamic data = json.Value!;
        return (bool)data.GetType().GetProperty("success")!.GetValue(data)!;
    }

    private static string Message(JsonResult json)
    {
        dynamic data = json.Value!;
        return (string)data.GetType().GetProperty("message")!.GetValue(data)!;
    }

    // ── ① 守門：合格＋不良皆 0 → 拒絕 ───────────────────────────────────────────
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    public async Task CreateReport_NoQty_ReturnsFail_NoReportWritten(decimal qtyOk, decimal qtyNg)
    {
        await SeedDispatch("D001", DispatchStatus.InProgress);
        await SeedSession("S1", "D001");

        var result = Assert.IsType<JsonResult>(
            await _ctrl.CreateReport("D001", "S1", qtyOk, qtyNg, null));

        Assert.False(IsSuccess(result));
        Assert.Equal("ReportQtyRequired", Message(result));
        Assert.Empty(_context.ProductionReports);
    }

    // ── ①b 守門：任一數量為負（即使另一為正）→ 拒絕，不得倒扣 QtyDone ──────────────
    //   舊版守門僅擋「兩者皆非正」(qtyOk<=0 && qtyNg<=0)，混合正負（如 -5 合格 / 10 不良）
    //   會穿透 → dispatch.QtyDone += 負數 倒扣已完工量，汙染資料。
    [Theory]
    [InlineData(-5, 10)]   // 合格負、不良正
    [InlineData(5, -3)]    // 合格正、不良負
    public async Task CreateReport_NegativeQty_ReturnsFail_QtyDoneUnchanged(decimal qtyOk, decimal qtyNg)
    {
        await SeedDispatch("D001", DispatchStatus.InProgress, qty: 10m, qtyDone: 4m);
        await SeedSession("S1", "D001");

        var result = Assert.IsType<JsonResult>(
            await _ctrl.CreateReport("D001", "S1", qtyOk, qtyNg, null));

        Assert.False(IsSuccess(result));
        Assert.Equal("ReportQtyRequired", Message(result));
        Assert.Empty(_context.ProductionReports);

        var d = await _context.Dispatches.FindAsync("D001");
        Assert.Equal(4m, d!.QtyDone);                  // 未被倒扣
        Assert.Equal(DispatchStatus.InProgress, d.Status);
    }

    // ── ② 派工不存在 → 拒絕 ─────────────────────────────────────────────────────
    [Fact]
    public async Task CreateReport_DispatchNotFound_ReturnsFail()
    {
        var result = Assert.IsType<JsonResult>(
            await _ctrl.CreateReport("MISSING", "S1", 5m, 0m, null));

        Assert.False(IsSuccess(result));
        Assert.Equal("RecordNotFound", Message(result));
    }

    // ── ③ 派工非生產中 → 拒絕（只有 InProgress 可報工）───────────────────────────
    [Theory]
    [InlineData(DispatchStatus.Pending)]
    [InlineData(DispatchStatus.Scheduled)]
    [InlineData(DispatchStatus.Closed)]
    [InlineData(DispatchStatus.OnHold)]
    public async Task CreateReport_NotInProgress_ReturnsFail(DispatchStatus status)
    {
        await SeedDispatch("D001", status);
        await SeedSession("S1", "D001");

        var result = Assert.IsType<JsonResult>(
            await _ctrl.CreateReport("D001", "S1", 5m, 0m, null));

        Assert.False(IsSuccess(result));
        Assert.Equal("DispatchNotInProgress", Message(result));
        Assert.Empty(_context.ProductionReports);
    }

    // ── ④ 超量報工：QtyDone + qtyOk > Quantity → 拒絕，不寫報工、QtyDone 不變 ──────
    [Fact]
    public async Task CreateReport_OverReport_ReturnsFail_QtyDoneUnchanged()
    {
        await SeedDispatch("D001", DispatchStatus.InProgress, qty: 10m, qtyDone: 8m);
        await SeedSession("S1", "D001");

        // 8 + 3 = 11 > 10
        var result = Assert.IsType<JsonResult>(
            await _ctrl.CreateReport("D001", "S1", 3m, 0m, null));

        Assert.False(IsSuccess(result));
        Assert.Equal("OverReport", Message(result));

        var d = await _context.Dispatches.FindAsync("D001");
        Assert.Equal(8m, d!.QtyDone);                  // 未累計
        Assert.Equal(DispatchStatus.InProgress, d.Status);
        Assert.Empty(_context.ProductionReports);
    }

    // ── ⑤ Session 不存在 → 拒絕 ─────────────────────────────────────────────────
    [Fact]
    public async Task CreateReport_SessionNotFound_ReturnsFail()
    {
        await SeedDispatch("D001", DispatchStatus.InProgress);
        // 不建立 session

        var result = Assert.IsType<JsonResult>(
            await _ctrl.CreateReport("D001", "BAD", 5m, 0m, null));

        Assert.False(IsSuccess(result));
        Assert.Equal("SessionNotFound", Message(result));
        Assert.Empty(_context.ProductionReports);
    }

    // ── ⑥ 部分報工：寫入 ProductionReport（逐欄正確）、QtyDone 累計、狀態維持 InProgress ──
    [Fact]
    public async Task CreateReport_Partial_WritesReport_AccumulatesQty_StaysInProgress()
    {
        await SeedDispatch("D001", DispatchStatus.InProgress, qty: 10m, qtyDone: 2m);
        await SeedSession("S1", "D001");
        SeedContainer("BOX-01");

        var result = Assert.IsType<JsonResult>(
            await _ctrl.CreateReport("D001", "S1", 5m, 0m, "BOX-01"));

        Assert.True(IsSuccess(result));

        var report = Assert.Single(_context.ProductionReports);
        Assert.Equal("S1", report.SessionId);
        Assert.Equal("D001", report.DispatchId);
        Assert.Equal("WO001", report.WorkOrderId);
        Assert.Equal(5m, report.QtyOk);
        Assert.Equal(0m, report.QtyNg);
        Assert.Equal("BOX-01", report.ContainerCode);
        Assert.Equal(ProductionReportStatus.Reported, report.Status);
        Assert.Equal(PlantId, report.PlantId);

        var d = await _context.Dispatches.FindAsync("D001");
        Assert.Equal(7m, d!.QtyDone);                  // 2 + 5
        Assert.Equal(DispatchStatus.InProgress, d.Status);

        // 報工容器（container-tracking.md §8 L1）：有效容器種類記一筆「容器→實際數量」
        var c = Assert.Single(_context.ProductionReportContainers);
        Assert.Equal(report.ReportId, c.ReportId);
        Assert.Equal("BOX-01", c.ContainerCode);
        Assert.Equal(5m, c.ActualQty);
    }

    // ── ⑥' 桌機報工的容器是下拉選單：不在容器維護或已停用 → 拒絕，且不寫任何報工、不累計 ──
    [Theory]
    [InlineData("NOT-EXIST", false)]
    [InlineData("BOX-OFF", true)]      // 存在但已停用（畫面開著時被停用）
    public async Task CreateReport_InvalidContainer_ReturnsFail_NoReportWritten(string code, bool seedInactive)
    {
        await SeedDispatch("D001", DispatchStatus.InProgress, qty: 10m);
        await SeedSession("S1", "D001");
        if (seedInactive) SeedContainer(code, isActive: false);

        var result = Assert.IsType<JsonResult>(await _ctrl.CreateReport("D001", "S1", 5m, 0m, code));

        Assert.False(IsSuccess(result));
        Assert.Empty(_context.ProductionReports);
        Assert.Empty(_context.ProductionReportContainers);
        Assert.Equal(0m, (await _context.Dispatches.FindAsync("D001"))!.QtyDone);
    }

    private void SeedContainer(string code, bool isActive = true)
    {
        _context.Containers.Add(new SmartFactory.MES.Modules.BAS.Models.Container
            { ContainerCode = code, ContainerName = code, MaxCapacity = 50m, IsActive = isActive });
        _context.SaveChanges();
    }

    // ── ⑦ 不良數不累計到 QtyDone、不受超量守門影響；qtyNg>0 才存 NgReasonCode ──────
    [Fact]
    public async Task CreateReport_WithNg_DoesNotCountTowardQtyDone_StoresNgReason()
    {
        await SeedDispatch("D001", DispatchStatus.InProgress, qty: 10m, qtyDone: 0m);
        await SeedSession("S1", "D001");

        // 合格 3、不良 4：QtyDone 只加 3
        var result = Assert.IsType<JsonResult>(
            await _ctrl.CreateReport("D001", "S1", 3m, 4m, null, ngReasonCode: "SCRATCH"));

        Assert.True(IsSuccess(result));

        var report = Assert.Single(_context.ProductionReports);
        Assert.Equal(3m, report.QtyOk);
        Assert.Equal(4m, report.QtyNg);
        Assert.Equal("SCRATCH", report.NgReasonCode);

        var d = await _context.Dispatches.FindAsync("D001");
        Assert.Equal(3m, d!.QtyDone);                  // 不良不計入
    }

    // ── ⑧ 不良為 0 時，傳入的 NgReasonCode 應被忽略（存 null）────────────────────
    [Fact]
    public async Task CreateReport_NoNg_IgnoresNgReasonCode()
    {
        await SeedDispatch("D001", DispatchStatus.InProgress);
        await SeedSession("S1", "D001");

        var result = Assert.IsType<JsonResult>(
            await _ctrl.CreateReport("D001", "S1", 5m, 0m, null, ngReasonCode: "SHOULD-IGNORE"));

        Assert.True(IsSuccess(result));
        var report = Assert.Single(_context.ProductionReports);
        Assert.Null(report.NgReasonCode);
    }

    // ── ⑨ 達量報工：派工自動 Closed，且工單全完工 → 工單自動 Completed（§4.3）──────
    [Fact]
    public async Task CreateReport_ReachesQty_ClosesDispatch_AndCompletesWorkOrder()
    {
        await SeedWorkOrder("WO001", WorkOrderStatus.InProgress);
        await SeedDispatch("D001", DispatchStatus.InProgress, qty: 10m, qtyDone: 6m);
        await SeedSession("S1", "D001");

        // 6 + 4 = 10 → 達量
        var result = Assert.IsType<JsonResult>(
            await _ctrl.CreateReport("D001", "S1", 4m, 0m, null));

        Assert.True(IsSuccess(result));

        var d = await _context.Dispatches.FindAsync("D001");
        Assert.Equal(10m, d!.QtyDone);
        Assert.Equal(DispatchStatus.Closed, d.Status);

        var wo = await _context.WorkOrders.FindAsync("WO001");
        Assert.Equal(WorkOrderStatus.Completed, wo!.Status);
    }

    // ── ⑩ 同工單尚有未完工派工時，達量不得連帶把工單轉 Completed ──────────────────
    [Fact]
    public async Task CreateReport_ReachesQty_KeepsWorkOrderInProgress_WhenSiblingDispatchOpen()
    {
        await SeedWorkOrder("WO001", WorkOrderStatus.InProgress);
        await SeedDispatch("D001", DispatchStatus.InProgress, qty: 10m, qtyDone: 6m);
        await SeedDispatch("D002", DispatchStatus.InProgress, qty: 5m, qtyDone: 0m); // 兄弟派工未完工
        await SeedSession("S1", "D001");

        var result = Assert.IsType<JsonResult>(
            await _ctrl.CreateReport("D001", "S1", 4m, 0m, null));

        Assert.True(IsSuccess(result));

        var d = await _context.Dispatches.FindAsync("D001");
        Assert.Equal(DispatchStatus.Closed, d!.Status);

        var wo = await _context.WorkOrders.FindAsync("WO001");
        Assert.Equal(WorkOrderStatus.InProgress, wo!.Status);  // 未全完工，維持生產中
    }

    // ── ⑪ 報工成功後觸發 ETA 重算（與手機端同一 IReportEtaNotifier 管線）───────────
    [Fact]
    public async Task CreateReport_Success_TriggersEtaRecalculate()
    {
        await SeedDispatch("D001", DispatchStatus.InProgress, qty: 10m);
        await SeedSession("S1", "D001");

        await _ctrl.CreateReport("D001", "S1", 5m, 0m, null);

        _mockNotifier.Verify(n => n.QueueRecalculate(
            "D001", "MC01", PlantId, It.IsAny<DateTime>()), Times.Once);
    }

    // ── ⑫ 無更新權限 → 拒絕，不寫報工 ──────────────────────────────────────────
    [Fact]
    public async Task CreateReport_NoPermission_ReturnsFail()
    {
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(false);
        await SeedDispatch("D001", DispatchStatus.InProgress);
        await SeedSession("S1", "D001");

        var result = Assert.IsType<JsonResult>(
            await _ctrl.CreateReport("D001", "S1", 5m, 0m, null));

        Assert.False(IsSuccess(result));
        Assert.Equal("NoUpdatePermission", Message(result));
        Assert.Empty(_context.ProductionReports);
    }
}
