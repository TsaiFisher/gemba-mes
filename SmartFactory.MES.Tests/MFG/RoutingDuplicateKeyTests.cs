using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SmartFactory.MES.Filters;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 途程主鍵重複的防護測試：
/// - Create 預檢（同廠 DuplicateCode／跨廠 DuplicateCodeOtherPlant，PK 不含 PlantId）
/// - 工序/用料序號重複預檢（identity map 例外非 DbUpdateException，全域 Filter 接不到）
/// - 全站 23505 轉譯 Filter（AJAX 回 JSON alert 契約／非 AJAX 回 409 友善頁）
/// </summary>
public class RoutingDuplicateKeyTests : TestBase
{
    private const string PlantId = "P001";

    public RoutingDuplicateKeyTests()
    {
        _context.CurrentPlantId = PlantId;
    }

    private RoutingController BuildController(bool ajax = false)
    {
        var ctrl = new RoutingController(GetMockLocalizer<ProjectZSharedResource>().Object)
        {
            ControllerContext = GetControllerContextWithServices()
        };
        if (ajax)
            ctrl.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        ctrl.Url = new Moq.Mock<IUrlHelper>().Object;   // RedirectToAction 需要
        return ctrl;
    }

    [Fact]
    public async Task Create_同廠代碼重複_回表單並掛DuplicateCode欄位錯誤()
    {
        _context.Routings.Add(new Routing { RoutingId = "R-DUP", PlantId = PlantId });
        await _context.SaveChangesAsync();

        var ctrl = BuildController();
        var result = await ctrl.Create(new Routing { RoutingId = "R-DUP" });

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("_Form", view.ViewName);
        var errors = ctrl.ModelState[nameof(Routing.RoutingId)]!.Errors;
        Assert.Contains(errors, e => e.ErrorMessage == "DuplicateCode");
    }

    [Fact]
    public async Task Create_跨廠代碼重複_雖本廠查不到仍擋下並提示他廠使用()
    {
        // PK 只有 RoutingId：他廠已用的代碼被廠別 Query Filter 隱藏，但插入必撞 PK
        _context.Routings.Add(new Routing { RoutingId = "R-OTHER", PlantId = "P002" });
        await _context.SaveChangesAsync();

        var ctrl = BuildController();
        var result = await ctrl.Create(new Routing { RoutingId = "R-OTHER" });

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("_Form", view.ViewName);
        var errors = ctrl.ModelState[nameof(Routing.RoutingId)]!.Errors;
        Assert.Contains(errors, e => e.ErrorMessage == "DuplicateCodeOtherPlant");
    }

    [Fact]
    public async Task Create_工序序號重複_回表單並掛DuplicateOperationSeq錯誤()
    {
        var ctrl = BuildController();
        const string dupSeqJson =
            """[{"operationSeq":10,"workCenterId":"WC1","uomCode":"PC"},{"operationSeq":10,"workCenterId":"WC2","uomCode":"PC"}]""";

        var result = await ctrl.Create(new Routing { RoutingId = "R-SEQ" }, dupSeqJson);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("_Form", view.ViewName);
        Assert.Contains(ctrl.ModelState[""]!.Errors, e => e.ErrorMessage == "DuplicateOperationSeq");
    }

    [Fact]
    public async Task Create_用料序號重複_回表單並掛DuplicateOperationSeq錯誤()
    {
        var ctrl = BuildController();
        const string dupBomJson =
            """[{"operationSeq":10,"workCenterId":"WC1","uomCode":"PC","boms":[{"bomSeq":1,"productCode":"A","quantity":1,"uomCode":"PC"},{"bomSeq":1,"productCode":"B","quantity":1,"uomCode":"PC"}]}]""";

        var result = await ctrl.Create(new Routing { RoutingId = "R-BOM" }, dupBomJson);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Contains(ctrl.ModelState[""]!.Errors, e => e.ErrorMessage == "DuplicateOperationSeq");
    }

    [Fact]
    public async Task Create_無重複_正常入庫()
    {
        var ctrl = BuildController();
        var result = await ctrl.Create(new Routing { RoutingId = "R-OK", RoutingName = "新途程" });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.NotNull(await _context.Routings.FindAsync("R-OK"));
    }

    // ── 全站 23505 轉譯 Filter ──────────────────────────────────────────

    private static ExceptionContext BuildExceptionContext(Exception ex, bool ajax)
    {
        var httpContext = new DefaultHttpContext();
        if (ajax)
            httpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ExceptionContext(actionContext, new List<IFilterMetadata>()) { Exception = ex };
    }

    private static DbUpdateException NewUniqueViolation(string constraint = "PK_Routings") =>
        new("boom", new PostgresException("duplicate key", "ERROR", "ERROR",
            PostgresErrorCodes.UniqueViolation, constraintName: constraint));

    private DbUniqueViolationExceptionFilter BuildFilter() =>
        new(GetMockLocalizer<ProjectZSharedResource>().Object,
            NullLogger<DbUniqueViolationExceptionFilter>.Instance);

    [Fact]
    public async Task Filter_Ajax的23505_回JsonFail契約供面板alert()
    {
        var ctx = BuildExceptionContext(NewUniqueViolation(), ajax: true);
        await BuildFilter().OnExceptionAsync(ctx);

        Assert.True(ctx.ExceptionHandled);
        var json = Assert.IsType<JsonResult>(ctx.Result);
        var value = json.Value!;
        Assert.False((bool)value.GetType().GetProperty("success")!.GetValue(value)!);
        Assert.Equal("DbUniqueViolation",
            (string?)value.GetType().GetProperty("message")!.GetValue(value));
    }

    [Fact]
    public async Task Filter_非Ajax的23505_回409由友善錯誤頁接手()
    {
        var ctx = BuildExceptionContext(NewUniqueViolation(), ajax: false);
        await BuildFilter().OnExceptionAsync(ctx);

        Assert.True(ctx.ExceptionHandled);
        var status = Assert.IsType<StatusCodeResult>(ctx.Result);
        Assert.Equal(StatusCodes.Status409Conflict, status.StatusCode);
    }

    [Fact]
    public async Task Filter_非23505的DbUpdateException_不處理維持原例外流程()
    {
        var notNull = new DbUpdateException("boom",
            new PostgresException("null value", "ERROR", "ERROR", PostgresErrorCodes.NotNullViolation));
        var ctx = BuildExceptionContext(notNull, ajax: true);
        await BuildFilter().OnExceptionAsync(ctx);

        Assert.False(ctx.ExceptionHandled);
        Assert.Null(ctx.Result);
    }

    [Fact]
    public async Task Filter_一般例外_不處理()
    {
        var ctx = BuildExceptionContext(new InvalidOperationException("x"), ajax: true);
        await BuildFilter().OnExceptionAsync(ctx);

        Assert.False(ctx.ExceptionHandled);
    }
}
