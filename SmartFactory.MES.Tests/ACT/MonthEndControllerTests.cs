using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Modules.ACT.Controllers;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// 月結步驟 4 的部門費用輸入框，要帶入「部門實際費用」畫面已存的本期資料。
/// 原本永遠是空的而且必填——畫面存過的費用得再打一次，送出後還會 upsert 覆蓋畫面的值。
/// </summary>
public class MonthEndControllerTests : TestBase
{
    private const string Plant = "P1";

    private readonly Mock<IMonthEndService> _monthEnd = new();

    private MonthEndController Build()
    {
        _context.CurrentPlantId = Plant;
        var perm = new Mock<IPermissionService>();
        perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        var user = new Mock<IUserContext>();
        user.Setup(u => u.PlantId).Returns(Plant);
        return new MonthEndController(_context,
            _monthEnd.Object, new Mock<INrvAssessmentService>().Object,
            new Mock<IFxRevaluationService>().Object, new Mock<IEclService>().Object,
            new Mock<IActualRateReestimationService>().Object,
            perm.Object, user.Object, GetMockLocalizer<ProjectZSharedResource>().Object,
            GetMockDateTimeService().Object, new Mock<IAccountingPeriodService>().Object);
    }

    [Fact]
    public async Task Index_帶入本期本廠已存的部門費用_依工作中心排序()
    {
        _context.CurrentPlantId = Plant;
        _context.DepartmentActualCosts.AddRange(
            new DepartmentActualCost { PeriodId = "209001", WorkCenterCode = "WC02", ActualLaborCost = 80000m, ActualOverheadCost = 35000.5m, PlantId = Plant },
            new DepartmentActualCost { PeriodId = "209001", WorkCenterCode = "WC01", ActualLaborCost = 1000m,  ActualOverheadCost = 600m,      PlantId = Plant },
            new DepartmentActualCost { PeriodId = "208912", WorkCenterCode = "WC01", ActualLaborCost = 9m,     ActualOverheadCost = 9m,        PlantId = Plant });
        _context.SaveChanges();

        var view = Assert.IsType<ViewResult>(await Build().Index("209001"));

        Assert.Equal("WC01:1000:600\nWC02:80000:35000.5", view.ViewData["DeptCostPrefill"]);
    }

    [Fact]
    public async Task Index_本期沒有部門費用時輸入框為空()
    {
        var view = Assert.IsType<ViewResult>(await Build().Index("209001"));
        Assert.Equal("", view.ViewData["DeptCostPrefill"]);
    }

    /// <summary>「執行人」存的是 UserId（GUID），畫面要顯示帳號名稱。</summary>
    [Fact]
    public async Task Index_執行人顯示帳號名稱而不是UserId()
    {
        _context.Users.Add(new ApplicationUser { Id = "u-guid-1", UserName = "finance01" });
        _context.SaveChanges();
        var cl = new SmartFactory.MES.Modules.ACT.Models.MonthEndChecklist { ChecklistId = "209001-P1", PeriodId = "209001", PlantId = Plant };
        cl.Items.Add(new SmartFactory.MES.Modules.ACT.Models.ChecklistItem { ChecklistId = cl.ChecklistId, StepNo = 1, CompletedBy = "u-guid-1" });
        _monthEnd.Setup(m => m.GetChecklistAsync("209001", Plant, It.IsAny<CancellationToken>())).ReturnsAsync(cl);

        var view = Assert.IsType<ViewResult>(await Build().Index("209001"));

        var names = Assert.IsType<Dictionary<string, string>>(view.ViewData["UserNames"]);
        Assert.Equal("finance01", names["u-guid-1"]);
    }
}
