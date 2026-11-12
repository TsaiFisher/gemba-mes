using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 生產排程「機台負荷」的已排／可用／利用率（/api/mfg/machine-load）。
///
/// 2026-10-27 回報利用率 323%：已排取「結束 − 開始」的牆鐘時間（夜間、假日都算），
/// 可用取日曆工時；從 7 月一直生產中的派工整週 24×7 都被算成已排。
/// 修正後兩者用同一份日曆時段，只計本週內、上班時段內的分鐘。
/// </summary>
public class MachineLoadDataTests : TestBase
{
    private const string PlantId = "P001";
    private const string Machine = "M-01";
    private const string Wc      = "WC-01";

    // 2026-10-26 是週一；每天 08:00–20:00（720 分）
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly (int, int)[] DaySegs = { (480, 1200) };

    private readonly Mock<ISchedulingEngine> _engine = new();

    public MachineLoadDataTests()
    {
        _context.CurrentPlantId = PlantId;
        _context.WorkCenterMachines.Add(new WorkCenterMachine { WorkCenterCode = Wc, MachineCode = Machine });
        _context.SaveChanges();

        var map = new Dictionary<(string?, string?, DateOnly), IReadOnlyList<WorkCalendarSegmentBuilder.Segment>>();
        for (var d = Monday; d < Monday.AddDays(5); d = d.AddDays(1))   // 週一到週五上班
            map[(null, null, d)] = DaySegs.Select(s => new WorkCalendarSegmentBuilder.Segment(s.Item1, s.Item2)).ToList();
        _engine.Setup(e => e.LoadCalendarContextAsync(PlantId, Monday, Monday.AddDays(6), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CalendarContext(map, Array.Empty<PlannedDowntime>()));
    }

    private static DateTime At(DateOnly d, int h, int m = 0) =>
        DateTime.SpecifyKind(d.ToDateTime(new TimeOnly(h, m)), DateTimeKind.Utc);

    private void SeedDispatch(string id, DateTime start, DateTime end, DispatchStatus status = DispatchStatus.Scheduled)
    {
        // 查詢 Include 了 Operation → WorkOrder → ProductionBatch（必填導覽），缺了 InMemory 會整列濾掉
        _context.ProductionBatches.Add(new ProductionBatch
        {
            BatchId = "PB-" + id, MrpRunId = "MRP", MrpRunLineSeq = 1, ProductCode = "P", Quantity = 1,
            PlannedEndDate = end, PlantId = PlantId
        });
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = "WO-" + id, ProductionBatchId = "PB-" + id, ProductCode = "P", RoutingId = "R", Quantity = 1,
            Status = WorkOrderStatus.InProgress, PlantId = PlantId,
            PlannedStartDate = start, PlannedEndDate = end
        });
        _context.WorkOrderOperations.Add(new WorkOrderOperation
        {
            WorkOrderId = "WO-" + id, OperationSeq = 10, OperationName = "OP10", WorkCenterId = Wc
        });
        _context.Dispatches.Add(new Dispatch
        {
            DispatchId = id, WorkOrderId = "WO-" + id, OperationSeq = 10, PlantId = PlantId,
            MachineCode = Machine, Status = status, Quantity = 1, ScheduledStart = start, ScheduledEnd = end,
        });
        _context.SaveChanges();
    }

    private async Task<JsonElement> LoadRowAsync()
    {
        var user = new Mock<IUserContext>();
        user.Setup(u => u.PlantId).Returns(PlantId);
        var ctrl = new ScheduleController(_engine.Object, GetMockLocalizer<ProjectZSharedResource>().Object,
                                          new Mock<IDateTimeService>().Object)
        {
            ControllerContext = GetControllerContextWithServices(userContext: user.Object)
        };
        var json = Assert.IsType<JsonResult>(await ctrl.MachineLoadData(PlantId, Monday.ToString("yyyy-MM-dd")));
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(json.Value, (JsonSerializerOptions)json.SerializerSettings!));
        return Assert.Single(doc.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task 從上週以前一直生產到本週三_只算本週上班時段_不超過100()
    {
        SeedDispatch("D1", At(new DateOnly(2026, 7, 24), 20), At(Monday.AddDays(2), 21), DispatchStatus.InProgress);

        var row = await LoadRowAsync();

        Assert.Equal(5 * 720, row.GetProperty("availableMin").GetInt32());
        Assert.Equal(3 * 720, row.GetProperty("scheduledMin").GetInt32());   // 週一、二、三整天，夜間不算
        Assert.Equal(60.0, row.GetProperty("utilizationPct").GetDouble());
        Assert.False(row.GetProperty("overloaded").GetBoolean());
    }

    [Fact]
    public async Task 跨夜派工_夜間不計入已排()
    {
        // 週一 16:00 → 週二 10:00：當天 4 小時＋隔天 2 小時
        SeedDispatch("D1", At(Monday, 16), At(Monday.AddDays(1), 10));

        var row = await LoadRowAsync();

        Assert.Equal(360, row.GetProperty("scheduledMin").GetInt32());
    }

    [Fact]
    public async Task 計劃停機從可用與已排一起扣除()
    {
        // 週一 10:00–12:00 機台保養；派工 08:00–14:00 實際只做得到 4 小時
        var downtime = new PlannedDowntime
        {
            PlantId = PlantId, MachineCode = Machine, IsActive = true,
            StartTime = Monday.ToDateTime(new TimeOnly(10, 0)), EndTime = Monday.ToDateTime(new TimeOnly(12, 0))
        };
        var map = new Dictionary<(string?, string?, DateOnly), IReadOnlyList<WorkCalendarSegmentBuilder.Segment>>();
        for (var d = Monday; d < Monday.AddDays(5); d = d.AddDays(1))
            map[(null, null, d)] = new[] { new WorkCalendarSegmentBuilder.Segment(480, 1200) };
        _engine.Setup(e => e.LoadCalendarContextAsync(PlantId, Monday, Monday.AddDays(6), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CalendarContext(map, new[] { downtime }));
        SeedDispatch("D1", At(Monday, 8), At(Monday, 14));

        var row = await LoadRowAsync();

        Assert.Equal(5 * 720 - 120, row.GetProperty("availableMin").GetInt32());
        Assert.Equal(240, row.GetProperty("scheduledMin").GetInt32());
    }

    [Fact]
    public async Task 已取消派工不佔產能()
    {
        SeedDispatch("D1", At(Monday, 8), At(Monday, 12));
        SeedDispatch("D2", At(Monday, 12), At(Monday, 20), DispatchStatus.Cancelled);

        var row = await LoadRowAsync();

        Assert.Equal(240, row.GetProperty("scheduledMin").GetInt32());
    }
}
