using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 生產批優先序調整（ProductionBatchController.UpdatePriority，mfg-domain.md §10-8）守門：
/// 伺服器端必須自行驗證 1..9999 範圍（前端 min/max 僅提示，可被繞過直接 POST），
/// 否則 priority=-999 之類值會強行插到所有生產批之前。
/// </summary>
public class ProductionBatchPriorityTests : TestBase
{
    private const string PlantId = "P001";

    private readonly ProductionBatchController _ctrl;

    public ProductionBatchPriorityTests()
    {
        _context.CurrentPlantId = PlantId;

        var mockLoc = new Mock<IStringLocalizer<SmartFactory.MES.ProjectZSharedResource>>();
        mockLoc.Setup(x => x[It.IsAny<string>()])
               .Returns<string>(k => new LocalizedString(k, k));

        _ctrl = new ProductionBatchController(mockLoc.Object, new Mock<IWorkOrderReleaseService>().Object,
            new Mock<IUomService>().Object)
        {
            ControllerContext = GetControllerContextWithServices()
        };
    }

    private async Task SeedBatch(string id, int priority = 50)
    {
        _context.ProductionBatches.Add(new ProductionBatch
        {
            BatchId = id, MrpRunId = "MRP1", MrpRunLineSeq = 1,
            ProductCode = "P-1", Quantity = 10m, Priority = priority,
            PlannedEndDate = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            PlantId = PlantId,
        });
        await _context.SaveChangesAsync();
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

    [Fact]
    public async Task UpdatePriority_WithinRange_Updates()
    {
        await SeedBatch("B001", priority: 50);

        var result = Assert.IsType<JsonResult>(await _ctrl.UpdatePriority("B001", 10));

        Assert.True(IsSuccess(result));
        var b = await _context.ProductionBatches.FirstAsync(x => x.BatchId == "B001");
        Assert.Equal(10, b.Priority);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-999)]
    [InlineData(10000)]
    public async Task UpdatePriority_OutOfRange_ReturnsFail_Unchanged(int priority)
    {
        await SeedBatch("B001", priority: 50);

        var result = Assert.IsType<JsonResult>(await _ctrl.UpdatePriority("B001", priority));

        Assert.False(IsSuccess(result));
        Assert.Equal("PriorityOutOfRange", Message(result));
        var b = await _context.ProductionBatches.FirstAsync(x => x.BatchId == "B001");
        Assert.Equal(50, b.Priority);   // 未被改成越界值
    }

    [Fact]
    public async Task UpdatePriority_BatchNotFound_ReturnsFail()
    {
        var result = Assert.IsType<JsonResult>(await _ctrl.UpdatePriority("MISSING", 10));

        Assert.False(IsSuccess(result));
        Assert.Equal("RecordNotFound", Message(result));
    }
}
