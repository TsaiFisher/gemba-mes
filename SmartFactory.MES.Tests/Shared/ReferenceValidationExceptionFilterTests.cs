using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFactory.MES.Api.Mobile;
using SmartFactory.MES.Modules._Shared.ReferenceValidation;
using Xunit;

namespace SmartFactory.MES.Tests.Shared;

/// <summary>存檔被參照檢查擋下時，各種呼叫端收到的回應形狀。</summary>
public class ReferenceValidationExceptionFilterTests
{
    private static readonly ReferenceValidationException Ex = new(
        [new ReferenceViolation("WorkOrder", "ProductCode", ReferenceMaster.Product, "NO-SUCH", ReferenceProblem.NotFound)]);

    private readonly TempDataDictionary _tempData = new(new DefaultHttpContext(), Mock.Of<ITempDataProvider>());

    private ExceptionContext Run(string path, Exception ex, bool ajax = false, string? referer = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Request.Host = new HostString("mes.local");
        if (ajax) http.Request.Headers.XRequestedWith = "XMLHttpRequest";
        if (referer != null) http.Request.Headers.Referer = referer;
        var factory = new Mock<ITempDataDictionaryFactory>();
        factory.Setup(f => f.GetTempData(http)).Returns(_tempData);

        var ctx = new ExceptionContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), [])
        {
            Exception = ex,
        };
        new ReferenceValidationExceptionFilter(factory.Object, NullLogger<ReferenceValidationExceptionFilter>.Instance)
            .OnException(ctx);
        return ctx;
    }

    [Fact]
    public void 手機API_回400與結構化錯誤碼_不用409免得離線佇列一直重送()
    {
        var ctx = Run("/api/mobile/mfg/session/start", Ex);

        var result = Assert.IsType<BadRequestObjectResult>(ctx.Result);
        var body = Assert.IsType<MobileErrorResult>(result.Value);
        Assert.Equal(ReferenceValidationExceptionFilter.MobileErrorCode, body.Error!.Code);
        Assert.Contains("NO-SUCH", body.Error.Message);
        Assert.True(ctx.ExceptionHandled);
    }

    [Fact]
    public void 側欄面板AJAX_回success_false與訊息()
    {
        var ctx = Run("/WorkOrder/Create", Ex, ajax: true);

        var json = Assert.IsType<JsonResult>(ctx.Result);
        Assert.Contains("success = False", json.Value!.ToString());
        Assert.Contains("NO-SUCH", json.Value.ToString());
    }

    [Fact]
    public void 其他API_回400與message()
    {
        var ctx = Run("/api/warehouse/transfer", Ex);

        Assert.Contains("NO-SUCH", Assert.IsType<BadRequestObjectResult>(ctx.Result).Value!.ToString());
    }

    [Fact]
    public void 一般表單送出_訊息放TempData並導回本站來源頁()
    {
        var ctx = Run("/WorkOrder/Create", Ex, referer: "https://mes.local/WorkOrder?searched=true");

        Assert.Equal("/WorkOrder?searched=true", Assert.IsType<RedirectResult>(ctx.Result).Url);
        Assert.Contains("NO-SUCH", (string)_tempData["ErrorMessage"]!);
    }

    [Fact]
    public void 一般表單送出_來源頁是外站_不跟著導出去()
    {
        var ctx = Run("/WorkOrder/Create", Ex, referer: "https://evil.example/x");

        Assert.Equal("/", Assert.IsType<RedirectResult>(ctx.Result).Url);
    }

    [Fact]
    public void 其他例外_不處理()
    {
        var ctx = Run("/WorkOrder/Create", new InvalidOperationException("x"), ajax: true);

        Assert.Null(ctx.Result);
        Assert.False(ctx.ExceptionHandled);
    }
}
