using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 工單成本差異報表的重工分開統計（mfg-domain.md §16.4b）：工單類型欄與篩選、重工損失、重工小計。
/// 原本報表不分類型，重工工單的成本混在一般生產裡，品質異常成本看不出來。
/// </summary>
public class WorkOrderVarianceReworkTests : TestBase
{
    private const string PlantId = "P001";
    private readonly Mock<IUserContext> _user = new();
    private readonly Mock<IPermissionService> _perm = new();

    public WorkOrderVarianceReworkTests()
    {
        _context.CurrentPlantId = PlantId;
        _user.Setup(u => u.PlantId).Returns(PlantId);
        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);

        Seed("WO-N1", WorkOrderType.Normal, material: 1_000m, labor: 200m, overhead: 300m, reworkLoss: 0m);
        Seed("WO-R1", WorkOrderType.Rework, material: 3_100m, labor: 200m, overhead: 300m, reworkLoss: 800m);
        Seed("WO-R2", WorkOrderType.Rework, material: 300m,   labor: 100m, overhead: 100m, reworkLoss: 500m);
        _context.SaveChanges();
    }

    private void Seed(string id, WorkOrderType type, decimal material, decimal labor, decimal overhead, decimal reworkLoss)
    {
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = id, ProductionBatchId = "PB", ProductCode = "FG01", RoutingId = "RT",
            Quantity = 4m, Type = type, Status = WorkOrderStatus.Closed, PlantId = PlantId,
        });
        _context.WorkOrderActualCosts.Add(new WorkOrderActualCost
        {
            WorkOrderId = id, ActualMaterial = material, ActualLabor = labor, ActualOverhead = overhead,
            ReworkLoss = reworkLoss, CostDate = new DateOnly(2026, 10, 1), PlantId = PlantId,
        });
    }

    private WorkOrderVarianceController Build()
    {
        var ctrl = new WorkOrderVarianceController(
            _context, _user.Object, _perm.Object, new ExportService(),
            new Mock<IDateTimeService>().Object, new Mock<IWorkOrderCostFinalizationService>().Object,
            new Mock<IUomService>().Object, GetMockCurrencyService().Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        ctrl.ControllerContext = GetControllerContext();
        return ctrl;
    }

    [Fact]
    public async Task Index_AllTypes_ShowsTypeAndReworkSubtotal()
    {
        var ctrl = Build();
        var view = Assert.IsType<ViewResult>(await ctrl.Index(null, null, null, null, searched: true));
        var rows = Assert.IsAssignableFrom<IEnumerable<WorkOrderVarianceRow>>(view.Model).ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal(WorkOrderType.Rework, rows.Single(r => r.WorkOrderId == "WO-R1").WorkOrderType);
        Assert.Equal(800m, rows.Single(r => r.WorkOrderId == "WO-R1").ReworkLoss);

        Assert.Equal(2, (int)ctrl.ViewBag.ReworkCount);
        Assert.Equal(3_100m + 500m + 300m + 200m, (decimal)ctrl.ViewBag.ReworkActualTotal);
        Assert.Equal(1_300m, (decimal)ctrl.ViewBag.ReworkLossTotal);
    }

    [Fact]
    public async Task Index_FilterNormal_ExcludesReworkAndSubtotalIsZero()
    {
        var ctrl = Build();
        var view = Assert.IsType<ViewResult>(
            await ctrl.Index(null, null, null, null, workOrderType: WorkOrderType.Normal, searched: true));
        var rows = Assert.IsAssignableFrom<IEnumerable<WorkOrderVarianceRow>>(view.Model).ToList();

        Assert.Equal(new[] { "WO-N1" }, rows.Select(r => r.WorkOrderId));
        Assert.Equal(0, (int)ctrl.ViewBag.ReworkCount);
        Assert.Equal(0m, (decimal)ctrl.ViewBag.ReworkLossTotal);
    }

    [Fact]
    public async Task Export_FilterRework_OnlyReworkRowsWithLossColumn()
    {
        var ctrl = Build();
        var file = Assert.IsType<FileContentResult>(
            await ctrl.Export(null, null, null, null, workOrderType: WorkOrderType.Rework, format: "csv"));
        var csv = System.Text.Encoding.UTF8.GetString(file.FileContents);

        Assert.Contains("WO-R1", csv);
        Assert.Contains("WO-R2", csv);
        Assert.DoesNotContain("WO-N1", csv);
        Assert.Contains("800", csv);
    }
}
