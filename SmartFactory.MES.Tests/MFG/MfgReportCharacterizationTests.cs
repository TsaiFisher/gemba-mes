using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Reporting;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Modules.MFG.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// §6.1 前置作業二：MFG 報表特徵測試（characterization test）。
///
/// 與 <c>OrdReportCharacterizationTests</c>／<c>PurReportCharacterizationTests</c> 同一個目的：
/// **不主張現況是對的，只鎖住現況**，好讓 <c>BuildQueryAsync</c> 抽成 <c>IReportTool</c>
/// 之後數字一字不差。報表算錯不會 crash，只會靜默吐出不同筆數。
///
/// <para>本批三張報表各有一個「不直覺但已在線上跑」的行為，都各有一條斷言鎖住：
/// 工單進度的迄日是 <c>&lt;= to.AddDays(1)</c>（多含一天）、不良率的時間區間是
/// <b>半開</b>（<c>&gt;= start &amp;&amp; &lt; end</c>，與其餘報表的全閉相反）、
/// MRP 達成率只看 <c>Executed</c> 的執行單。</para>
/// </summary>
public class MfgReportCharacterizationTests : TestBase
{
    private readonly Mock<IUserContext>       _user = new();
    private readonly Mock<IPermissionService> _perm = new();
    private readonly Mock<IDateTimeService>   _dt   = new();
    private readonly Mock<IUomService>        _uom  = new();

    private const string PlantId = "P001";

    public MfgReportCharacterizationTests()
    {
        _context.CurrentPlantId = PlantId;
        _user.Setup(u => u.PlantId).Returns(PlantId);
        _user.Setup(u => u.UserId).Returns("user1");

        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);

        _dt.Setup(d => d.Now).Returns(new DateTime(2026, 5, 14, 9, 0, 0));
        _dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(new DateOnly(2026, 5, 14));
        // 使用者時區＝UTC：日期界線換算成恆等，本檔守的是篩選語意，不是時區（時區見 RelativePeriodResolverTests）。
        _dt.Setup(d => d.ToUtcTime(It.IsAny<DateTime>()))
           .Returns<DateTime>(x => DateTime.SpecifyKind(x, DateTimeKind.Utc));

        _uom.Setup(u => u.GetAllDecimalPlacesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int> { ["PC"] = 0 });
    }

    private WorkOrderProgressController NewWorkOrderProgress()
        => new(_context, _user.Object, _perm.Object,
               GetMockExportService().Object, _dt.Object, _uom.Object,
               new WorkOrderProgressTool(_context, Resolver(), HeaderLocalizer()),
               GetMockLocalizer<ProjectZSharedResource>().Object,
               HeaderLocalizer())
           { ControllerContext = GetControllerContextWithServices() };

    private DefectRateController NewDefectRate()
        => new(_context, _user.Object, _perm.Object,
               GetMockExportService().Object, _dt.Object, _uom.Object,
               new DefectRateTool(_context, Resolver()),
               GetMockLocalizer<ProjectZSharedResource>().Object,
               HeaderLocalizer())
           { ControllerContext = GetControllerContextWithServices() };

    private MrpAchievementController NewMrpAchievement()
        => new(_context, _user.Object, _perm.Object,
               GetMockExportService().Object, _dt.Object, _uom.Object,
               new MrpAchievementTool(_context),
               GetMockLocalizer<ProjectZSharedResource>().Object,
               HeaderLocalizer())
           { ControllerContext = GetControllerContextWithServices() };

    /// <summary>
    /// 工具用真的 <see cref="RelativePeriodResolver"/> 而非 mock：Controller→Tool→Resolver
    /// 這條路徑本身就是重構要保住的東西，換成替身等於不測它。
    /// </summary>
    private RelativePeriodResolver Resolver() => new(_dt.Object, _context);

    /// <summary>真的 resx localizer：工單進度的「逾期」欄值本身是在地化的。</summary>
    private static IStringLocalizer<ProjectZReportHeaderResource> HeaderLocalizer()
        => new ServiceCollection()
            .AddLogging()
            .AddLocalization(o => o.ResourcesPath = "Resources")
            .BuildServiceProvider()
            .GetRequiredService<IStringLocalizer<ProjectZReportHeaderResource>>();

    private static T ModelOf<T>(IActionResult result) where T : class
        => Assert.IsType<T>(Assert.IsType<ViewResult>(result).Model);

    // ════════════════════════════════════════════════════════════════
    // WorkOrderProgress（工單進度）
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 多工序工單：完工量取**最後一道工序**的完工量，不是每道工序報工的加總。
    /// 2026-10-05 回報：3 道工序各做完 100 的工單顯示完工 300、進度 300%。
    /// 同一道工序拆成多張派工時仍然加總（上一支測試）。
    /// </summary>
    [Fact]
    public async Task WorkOrderProgress_多工序取最後一道工序的完工量()
    {
        AddWorkOrder("WO-MO", "PRD-A", WorkOrderStatus.InProgress,
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 20), 100m);
        foreach (var seq in new[] { 10, 20, 30 })
            _context.WorkOrderOperations.Add(new WorkOrderOperation { WorkOrderId = "WO-MO", OperationSeq = seq, WorkCenterId = "WC" });
        _context.SaveChanges();
        AddDispatch("D-10", "WO-MO", 100m, 10);
        AddDispatch("D-20", "WO-MO", 100m, 20);
        AddDispatch("D-30a", "WO-MO", 50m, 30);   // 最後一道拆兩張
        AddDispatch("D-30b", "WO-MO", 30m, 30);

        var rows = ModelOf<List<WorkOrderProgressRow>>(
            await NewWorkOrderProgress().Index(null, "2026-05-01", "2026-05-31", null, searched: true));

        var wo = rows.Single(r => r.WorkOrderId == "WO-MO");
        Assert.Equal(80m, wo.QtyDone);
        Assert.Equal(80m, wo.ProgressPct);
    }

    /// <summary>
    /// 完工量取派工 <c>QtyDone</c> 的**加總**（同一道工序拆成多張派工時）、
    /// 進度百分比為計算屬性且四捨五入到小數 1 位、無派工時完工量為 0（不是 null）。
    /// </summary>
    [Fact]
    public async Task WorkOrderProgress_完工量取派工加總()
    {
        AddWorkOrder("WO-01", "PRD-A", WorkOrderStatus.Released,
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 20), 300m);
        AddDispatch("D-1", "WO-01", 100m);
        AddDispatch("D-2", "WO-01", 50m);
        AddWorkOrder("WO-02", "PRD-B", WorkOrderStatus.Released,
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 21), 100m);

        var rows = ModelOf<List<WorkOrderProgressRow>>(
            await NewWorkOrderProgress().Index(null, "2026-05-01", "2026-05-31", null, searched: true));

        var wo1 = rows.Single(r => r.WorkOrderId == "WO-01");
        Assert.Equal(150m, wo1.QtyDone);
        Assert.Equal(50m,  wo1.ProgressPct);      // 150 / 300

        var wo2 = rows.Single(r => r.WorkOrderId == "WO-02");
        Assert.Equal(0m, wo2.QtyDone);
        Assert.Equal(0m, wo2.ProgressPct);
    }

    /// <summary>
    /// 迄日條件是 <c>PlannedEndDate &lt;= to.AddDays(1)</c>——**比使用者輸入的那一天多含一天**。
    /// 這不直覺，但它已經在線上跑：把 <c>AddDays(1)</c> 拿掉，迄日當天有時間部分的工單
    /// （<c>2026-05-31 08:00</c>）會整批消失，而畫面上沒有任何訊號。
    /// </summary>
    [Fact]
    public async Task WorkOrderProgress_迄日多含一天()
    {
        AddWorkOrder("WO-IN", "PRD-A", WorkOrderStatus.Released,
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31, 8, 0, 0), 10m);
        AddWorkOrder("WO-EDGE", "PRD-A", WorkOrderStatus.Released,
            new DateTime(2026, 5, 1), new DateTime(2026, 6, 1), 10m);        // ＝ to.AddDays(1)
        AddWorkOrder("WO-OUT", "PRD-A", WorkOrderStatus.Released,
            new DateTime(2026, 5, 1), new DateTime(2026, 6, 2), 10m);

        var rows = ModelOf<List<WorkOrderProgressRow>>(
            await NewWorkOrderProgress().Index(null, "2026-05-01", "2026-05-31", null, searched: true));

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.WorkOrderId == "WO-IN");
        Assert.Contains(rows, r => r.WorkOrderId == "WO-EDGE");
    }

    /// <summary>
    /// 作廢工單一律不列（不論篩選條件）；品號是**模糊**比對（與 ORD／PUR 的精確比對相反）。
    /// </summary>
    [Fact]
    public async Task WorkOrderProgress_作廢不列且品號為模糊比對()
    {
        AddWorkOrder("WO-OK", "PRD-ABC", WorkOrderStatus.Released,
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 20), 10m);
        AddWorkOrder("WO-VOID", "PRD-ABC", WorkOrderStatus.Void,
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 20), 10m);
        AddWorkOrder("WO-OTHER", "XYZ", WorkOrderStatus.Released,
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 20), 10m);

        var rows = ModelOf<List<WorkOrderProgressRow>>(
            await NewWorkOrderProgress().Index("ABC", "2026-05-01", "2026-05-31", null, searched: true));

        Assert.Equal("WO-OK", Assert.Single(rows).WorkOrderId);
    }

    /// <summary>
    /// 逾期工單排在最前面（<c>IsOverdue</c> 優先於計劃完工日）；已結案的工單即使逾期也不算逾期。
    /// 排序鍵拿掉的話，逾期的工單會散在清單中間——那正是這張報表要一眼看到的東西。
    /// </summary>
    [Fact]
    public async Task WorkOrderProgress_逾期優先排序且結案不算逾期()
    {
        // 基準時間是 DateTime.UtcNow，所以用相對於「現在」的日期構造。
        var past   = DateTime.UtcNow.AddDays(-10);
        var future = DateTime.UtcNow.AddDays(10);

        AddWorkOrder("WO-FUTURE", "PRD-A", WorkOrderStatus.Released, past.AddDays(-20), future, 10m);
        AddWorkOrder("WO-LATE",   "PRD-A", WorkOrderStatus.Released, past.AddDays(-20), past,   10m);
        AddWorkOrder("WO-CLOSED", "PRD-A", WorkOrderStatus.Closed,   past.AddDays(-20), past,   10m);

        var rows = ModelOf<List<WorkOrderProgressRow>>(
            await NewWorkOrderProgress().Index(null, null, null, null, searched: true));

        Assert.Equal("WO-LATE", rows[0].WorkOrderId);
        Assert.True(rows[0].IsOverdue);
        Assert.False(rows.Single(r => r.WorkOrderId == "WO-CLOSED").IsOverdue);
        Assert.False(rows.Single(r => r.WorkOrderId == "WO-FUTURE").IsOverdue);
    }

    /// <summary>狀態篩選為列舉精確比對；無法解析的字串**不篩**（不是回空清單）。</summary>
    [Fact]
    public async Task WorkOrderProgress_狀態篩選解析失敗時不篩()
    {
        AddWorkOrder("WO-R", "PRD-A", WorkOrderStatus.Released,
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 20), 10m);
        AddWorkOrder("WO-P", "PRD-A", WorkOrderStatus.Planned,
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 20), 10m);

        var filtered = ModelOf<List<WorkOrderProgressRow>>(
            await NewWorkOrderProgress().Index(null, null, null, "Released", searched: true));
        Assert.Equal("WO-R", Assert.Single(filtered).WorkOrderId);

        var garbage = ModelOf<List<WorkOrderProgressRow>>(
            await NewWorkOrderProgress().Index(null, null, null, "NotAStatus", searched: true));
        Assert.Equal(2, garbage.Count);
    }

    /// <summary>閘門動詞是 <c>CanRead</c>。抽出後工具要照抄這一個。</summary>
    [Fact]
    public async Task WorkOrderProgress_閘門動詞為CanRead()
    {
        _perm.Setup(p => p.CanRead("WorkOrderProgress")).ReturnsAsync(false);

        Assert.IsType<ForbidResult>(
            await NewWorkOrderProgress().Index(null, null, null, null, searched: true));
    }

    // ════════════════════════════════════════════════════════════════
    // DefectRate（不良率）
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 依品號彙總合格量與不良量、報工筆數取 count、不良率為計算屬性（四捨五入 2 位），
    /// 依不良率降冪。同一品號跨工單要合併。
    /// </summary>
    [Fact]
    public async Task DefectRate_依品號彙總與降冪()
    {
        AddWorkOrder("WO-A", "PRD-A", WorkOrderStatus.Released,
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 20), 100m);
        AddWorkOrder("WO-A2", "PRD-A", WorkOrderStatus.Released,
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 20), 100m);
        AddWorkOrder("WO-B", "PRD-B", WorkOrderStatus.Released,
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 20), 100m);

        AddProductionReport("R1", "WO-A",  new DateTime(2026, 5, 10), 90m, 10m);
        AddProductionReport("R2", "WO-A2", new DateTime(2026, 5, 11), 90m, 10m);
        AddProductionReport("R3", "WO-B",  new DateTime(2026, 5, 10), 99m,  1m);

        var rows = ModelOf<List<DefectRateRow>>(
            await NewDefectRate().Index("2026-05-01", "2026-05-31", null, searched: true));

        Assert.Equal(2, rows.Count);

        Assert.Equal("PRD-A", rows[0].ProductCode);
        Assert.Equal(180m,    rows[0].QtyOk);       // 跨工單合併
        Assert.Equal(20m,     rows[0].QtyNg);
        Assert.Equal(200m,    rows[0].TotalQty);
        Assert.Equal(10m,     rows[0].DefectRate);
        Assert.Equal(2,       rows[0].ReportCount);

        Assert.Equal("PRD-B", rows[1].ProductCode);
        Assert.Equal(1m,      rows[1].DefectRate);
    }

    /// <summary>
    /// 時間區間是**半開**（<c>&gt;= start &amp;&amp; &lt; toDate + 1 天</c>）——與 ORD／PUR
    /// 的全閉區間相反。迄日當天整天都算在內，隔天 00:00 的報工不算。
    /// 這是本報表最容易在重構時被「順手統一」成全閉的一行。
    /// </summary>
    [Fact]
    public async Task DefectRate_時間區間為半開()
    {
        AddWorkOrder("WO-A", "PRD-A", WorkOrderStatus.Released,
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 20), 100m);

        AddProductionReport("R-START", "WO-A", new DateTime(2026, 5, 1, 0, 0, 0), 1m, 0m);
        AddProductionReport("R-LAST",  "WO-A", new DateTime(2026, 5, 31, 23, 59, 0), 1m, 0m);
        AddProductionReport("R-NEXT",  "WO-A", new DateTime(2026, 6, 1, 0, 0, 0), 1m, 0m);

        var rows = ModelOf<List<DefectRateRow>>(
            await NewDefectRate().Index("2026-05-01", "2026-05-31", null, searched: true));

        Assert.Equal(2, Assert.Single(rows).ReportCount);   // R-START 與 R-LAST，不含 R-NEXT
    }

    /// <summary>不良率分母為零時回 0 而非除以零；品號為模糊比對。</summary>
    [Fact]
    public async Task DefectRate_零總量回零且品號為模糊比對()
    {
        AddWorkOrder("WO-A", "PRD-ABC", WorkOrderStatus.Released,
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 20), 100m);
        AddWorkOrder("WO-B", "XYZ", WorkOrderStatus.Released,
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 20), 100m);

        AddProductionReport("R1", "WO-A", new DateTime(2026, 5, 10), 0m, 0m);
        AddProductionReport("R2", "WO-B", new DateTime(2026, 5, 10), 1m, 1m);

        var rows = ModelOf<List<DefectRateRow>>(
            await NewDefectRate().Index("2026-05-01", "2026-05-31", "ABC", searched: true));

        var only = Assert.Single(rows);
        Assert.Equal("PRD-ABC", only.ProductCode);
        Assert.Equal(0m, only.TotalQty);
        Assert.Equal(0m, only.DefectRate);
    }

    /// <summary>閘門動詞是 <c>CanRead</c>。</summary>
    [Fact]
    public async Task DefectRate_閘門動詞為CanRead()
    {
        _perm.Setup(p => p.CanRead("DefectRate")).ReturnsAsync(false);

        Assert.IsType<ForbidResult>(
            await NewDefectRate().Index(null, null, null, searched: true));
    }

    // ════════════════════════════════════════════════════════════════
    // MrpAchievement（MRP 達成率）
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 實際供給量 ＝ 庫存 ＋ 採購 ＋ 委外 ＋ 自製**四項相加**；
    /// 達成率與短缺量為計算屬性。少加任何一項都會讓達成率偏低而看起來像缺料。
    /// </summary>
    [Fact]
    public async Task MrpAchievement_實際量為四項相加()
    {
        AddMrpRun("MRP-01", MrpRunStatus.Executed);
        AddMrpRunLine("MRP-01", 1, "PRD-A", new DateTime(2026, 5, 20), 100m,
            stock: 10m, purchase: 20m, outsource: 30m, make: 25m);

        var rows = ModelOf<List<MrpAchievementRowVm>>(
            await NewMrpAchievement().Index(null, null, searched: true));

        var row = Assert.Single(rows);
        Assert.Equal(85m, row.ActualQty);          // 10 + 20 + 30 + 25
        Assert.Equal(85m, row.AchievementRate);    // 85 / 100
        Assert.Equal(15m, row.ShortfallQty);
    }

    /// <summary>
    /// 只列 <c>Executed</c> 的執行單——草稿／其他狀態的 MRP 展開結果不得出現。
    /// 這一條失效時，報表會混進「還沒跑完」的展開結果而看不出差別。
    /// </summary>
    [Fact]
    public async Task MrpAchievement_只列已執行的展開單()
    {
        AddMrpRun("MRP-RUN", MrpRunStatus.Executed);
        AddMrpRunLine("MRP-RUN", 1, "PRD-A", new DateTime(2026, 5, 20), 100m, make: 100m);
        AddMrpRun("MRP-DRAFT", MrpRunStatus.Draft);
        AddMrpRunLine("MRP-DRAFT", 1, "PRD-B", new DateTime(2026, 5, 20), 100m, make: 100m);

        var rows = ModelOf<List<MrpAchievementRowVm>>(
            await NewMrpAchievement().Index(null, null, searched: true));

        Assert.Equal("MRP-RUN", Assert.Single(rows).MrpRunId);
    }

    /// <summary>需求日升冪、同日再依品號升冪；展開單與品號皆為精確比對。</summary>
    [Fact]
    public async Task MrpAchievement_排序與精確比對()
    {
        AddMrpRun("MRP-01", MrpRunStatus.Executed);
        AddMrpRunLine("MRP-01", 1, "PRD-B", new DateTime(2026, 5, 20), 10m, make: 10m);
        AddMrpRunLine("MRP-01", 2, "PRD-A", new DateTime(2026, 5, 20), 10m, make: 10m);
        AddMrpRunLine("MRP-01", 3, "PRD-C", new DateTime(2026, 5, 10), 10m, make: 10m);

        var rows = ModelOf<List<MrpAchievementRowVm>>(
            await NewMrpAchievement().Index(null, null, searched: true));

        Assert.Equal(new[] { "PRD-C", "PRD-A", "PRD-B" }, rows.Select(r => r.ProductCode));

        var byProduct = ModelOf<List<MrpAchievementRowVm>>(
            await NewMrpAchievement().Index(null, "PRD-A", searched: true));
        Assert.Equal("PRD-A", Assert.Single(byProduct).ProductCode);
    }

    /// <summary>
    /// 品號在 <c>Products</c> 查無時仍要列出（左外聯），只是單位為 null。
    /// 改成內聯會讓「主檔還沒建的品號」整批從缺料報表上消失——那正是最該被看到的一批。
    /// </summary>
    [Fact]
    public async Task MrpAchievement_品號主檔查無仍列出()
    {
        AddMrpRun("MRP-01", MrpRunStatus.Executed);
        AddMrpRunLine("MRP-01", 1, "PRD-GHOST", new DateTime(2026, 5, 20), 100m, make: 0m);

        var rows = ModelOf<List<MrpAchievementRowVm>>(
            await NewMrpAchievement().Index(null, null, searched: true));

        var row = Assert.Single(rows);
        Assert.Equal("PRD-GHOST", row.ProductCode);
        Assert.Null(row.UomCode);
    }

    /// <summary>閘門動詞是 <c>CanRead</c>。</summary>
    [Fact]
    public async Task MrpAchievement_閘門動詞為CanRead()
    {
        _perm.Setup(p => p.CanRead("MrpAchievement")).ReturnsAsync(false);

        Assert.IsType<ForbidResult>(
            await NewMrpAchievement().Index(null, null, searched: true));
    }

    // ── 共用建構 ──────────────────────────────────────────────────

    private void AddWorkOrder(
        string id, string product, WorkOrderStatus status,
        DateTime plannedStart, DateTime plannedEnd, decimal qty)
    {
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId       = id,
            ProductionBatchId = "B-" + id,
            ProductCode       = product,
            RoutingId         = "RT-1",
            Quantity          = qty,
            Status            = status,
            PlannedStartDate  = plannedStart,
            PlannedEndDate    = plannedEnd,
            PlantId           = PlantId,
        });
        _context.SaveChanges();
    }

    private void AddDispatch(string id, string workOrderId, decimal qtyDone, int operationSeq = 10)
    {
        _context.Dispatches.Add(new Dispatch
        {
            DispatchId   = id,
            WorkOrderId  = workOrderId,
            OperationSeq = operationSeq,
            Quantity     = qtyDone,
            QtyDone      = qtyDone,
            PlantId      = PlantId,
        });
        _context.SaveChanges();
    }

    private void AddProductionReport(
        string id, string workOrderId, DateTime reportedAt, decimal qtyOk, decimal qtyNg)
    {
        _context.ProductionReports.Add(new ProductionReport
        {
            ReportId    = id,
            SessionId   = "S-1",
            DispatchId  = "D-1",
            WorkOrderId = workOrderId,
            QtyOk       = qtyOk,
            QtyNg       = qtyNg,
            ReportedAt  = reportedAt,
            PlantId     = PlantId,
        });
        _context.SaveChanges();
    }

    private void AddMrpRun(string id, MrpRunStatus status)
    {
        _context.MrpRuns.Add(new MrpRun { MrpRunId = id, Status = status, PlantId = PlantId });
        _context.SaveChanges();
    }

    private void AddMrpRunLine(
        string runId, int seq, string product, DateTime requiredDate, decimal requiredQty,
        decimal stock = 0m, decimal purchase = 0m, decimal outsource = 0m, decimal make = 0m)
    {
        _context.MrpRunLines.Add(new MrpRunLine
        {
            MrpRunId     = runId,
            LineSeq      = seq,
            ProductCode  = product,
            RequiredDate = requiredDate,
            RequiredQty  = requiredQty,
            StockQty     = stock,
            PurchaseQty  = purchase,
            OutsourceQty = outsource,
            MakeQty      = make,
        });
        _context.SaveChanges();
    }
}
