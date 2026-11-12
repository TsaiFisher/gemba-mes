using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Api.Mobile;

namespace SmartFactory.MES.Modules._Shared.ReferenceValidation
{
    /// <summary>
    /// 全站安全網：存檔被 <see cref="ReferenceValidationInterceptor"/> 擋下時，把
    /// <see cref="ReferenceValidationException"/> 轉成使用者看得懂的訊息，取代 500。
    /// 訊息本身已在地化（<see cref="ReferenceValidator.Describe"/>），這裡只決定回應形狀：
    /// <list type="bullet">
    /// <item>手機 API（<c>/api/mobile/</c>）：400 <see cref="MobileErrorResult"/>（code <c>REFERENCE_INVALID</c>）。
    ///   刻意不用 409——離線佇列把 409 當並發衝突自動重送，這種錯誤重送幾次都一樣。</item>
    /// <item>其他 <c>/api/</c>：400 <c>{ success:false, message }</c>（api.js 讀 <c>err.message</c>）。</item>
    /// <item>AJAX（側欄面板）：<c>{ success:false, message }</c>，與 DbUniqueViolationExceptionFilter 一致，
    ///   submitPanel 顯示訊息且不關面板，使用者改選後可再送。</item>
    /// <item>一般表單送出：TempData["ErrorMessage"] 後導回來源頁。</item>
    /// </list>
    /// 動作自己 catch 了（例如 <c>catch (Exception ex)</c> 回 ex.Message）就不會走到這裡，訊息一樣可讀。
    /// </summary>
    public sealed class ReferenceValidationExceptionFilter(
        ITempDataDictionaryFactory tempDataFactory,
        ILogger<ReferenceValidationExceptionFilter> logger) : IExceptionFilter
    {
        public const string MobileErrorCode = "REFERENCE_INVALID";

        public void OnException(ExceptionContext context)
        {
            if (context.Exception is not ReferenceValidationException ex) return;

            var http = context.HttpContext;
            var path = http.Request.Path;
            logger.LogWarning("參照欄位檢查擋下存檔 path={Path} violations={Violations}",
                path, string.Join("; ", ex.Violations.Select(v => $"{v.Entity}.{v.Property}={v.Value}:{v.Problem}")));

            var message = ex.Message;
            if (path.StartsWithSegments("/api/mobile"))
                context.Result = new BadRequestObjectResult(new MobileErrorResult(Error: new(MobileErrorCode, message)));
            else if (path.StartsWithSegments("/api"))
                context.Result = new BadRequestObjectResult(new { success = false, message });
            else if (http.Request.Headers.XRequestedWith == "XMLHttpRequest")
                context.Result = new JsonResult(new { success = false, message });
            else
            {
                tempDataFactory.GetTempData(http)["ErrorMessage"] = message;
                context.Result = new RedirectResult(LocalReferer(http.Request) ?? "/");
            }
            context.ExceptionHandled = true;
        }

        /// <summary>來源頁（只接受本站路徑，避免開放重導）。</summary>
        private static string? LocalReferer(HttpRequest request)
        {
            if (!Uri.TryCreate(request.Headers.Referer.ToString(), UriKind.Absolute, out var uri)) return null;
            if (!string.Equals(uri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)) return null;
            return uri.PathAndQuery;
        }
    }
}
