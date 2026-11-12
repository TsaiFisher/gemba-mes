using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 驗證所有 MFG Index 畫面在 searched=false 時不查 DB、回傳 null model。
/// 對應 new-screen 慣例：if (!searched) return View(null)。
/// 避免日後新 controller 漏加 guard 而首次開啟就查全表。
/// </summary>
public class MfgIndexSearchGuardTests : TestBase
{
    private static IStringLocalizer<SmartFactory.MES.ProjectZSharedResource> Loc()
    {
        var mock = new Mock<IStringLocalizer<SmartFactory.MES.ProjectZSharedResource>>();
        mock.Setup(x => x[It.IsAny<string>()])
            .Returns<string>(k => new LocalizedString(k, k));
        return mock.Object;
    }

    /// <summary>Index 的讀取閘門走 CanRead；未注入 ControllerContext 服務的 controller 需自備。</summary>
    private static IPermissionService Perm()
    {
        var mock = new Mock<IPermissionService>();
        mock.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        return mock.Object;
    }

    [Fact]
    public async Task RoutingIndex_ReturnsNullModel_WhenNotSearched()
    {
        var ctrl = new RoutingController(Loc()) { ControllerContext = GetControllerContextWithServices() };
        var view = Assert.IsType<ViewResult>(await ctrl.Index(searched: false));
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task WorkOrderIndex_ReturnsNullModel_WhenNotSearched()
    {
        var ctrl = new WorkOrderController(
                Loc(), new Mock<IApprovalService>().Object,
                new Mock<IInventoryReservationService>().Object,
                new Mock<IWorkOrderMaterialReadinessService>().Object,
                new Mock<IWorkOrderReleaseService>().Object,
                new Mock<IUomService>().Object, new Mock<ICurrencyService>().Object,
                new Mock<IReworkWorkOrderService>().Object)
            { ControllerContext = GetControllerContextWithServices() };
        var view = Assert.IsType<ViewResult>(await ctrl.Index(searched: false));
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task MrpRunIndex_ReturnsNullModel_WhenNotSearched()
    {
        var ctrl = new MrpRunController(
            Loc(),
            new Mock<IMrpEngine>().Object,
            new Mock<IDocNumberService>().Object,
            new Mock<IMrpRunExecutionService>().Object)
            { ControllerContext = GetControllerContextWithServices() };
        var view = Assert.IsType<ViewResult>(await ctrl.Index(searched: false));
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task OutsourcePurchaseOrderIndex_ReturnsNullModel_WhenNotSearched()
    {
        var ctrl = new OutsourcePurchaseOrderController(Loc(), new Mock<IDocNumberService>().Object,
            new Mock<IUomService>().Object, new OutsourceQuotationResolver(_context))
            { ControllerContext = GetControllerContextWithServices() };
        var view = Assert.IsType<ViewResult>(await ctrl.Index(searched: false));
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task ProductionBatchIndex_ReturnsNullModel_WhenNotSearched()
    {
        var ctrl = new ProductionBatchController(Loc(), new Mock<IWorkOrderReleaseService>().Object,
            new Mock<IUomService>().Object) { ControllerContext = GetControllerContextWithServices() };
        var view = Assert.IsType<ViewResult>(await ctrl.Index(searched: false));
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task BatchReleaseIndex_ReturnsNullModel_WhenNotSearched()
    {
        var ctrl = new BatchReleaseController(Loc(), new Mock<IWorkOrderReleaseService>().Object,
            new Mock<IUomService>().Object)
            { ControllerContext = GetControllerContextWithServices() };
        var view = Assert.IsType<ViewResult>(await ctrl.Index(searched: false));
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task PlannedDowntimeIndex_ReturnsNullModel_WhenNotSearched()
    {
        var ctrl = new PlannedDowntimeController(
            _context,
            new Mock<IUserContext>().Object,
            Perm(),
            Loc())
            { ControllerContext = GetControllerContext() };
        var view = Assert.IsType<ViewResult>(await ctrl.Index(searched: false));
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task WorkCalendarIndex_ReturnsNullModel_WhenNotSearched()
    {
        var ctrl = new WorkCalendarController(
            _context,
            new Mock<IUserContext>().Object,
            Perm(),
            Loc())
            { ControllerContext = GetControllerContext() };
        var view = Assert.IsType<ViewResult>(await ctrl.Index(searched: false));
        Assert.Null(view.Model);
    }
}
