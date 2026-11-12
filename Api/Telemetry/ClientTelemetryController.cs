using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Modules.RPT.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Api.Telemetry
{
    /// <summary>
    /// 現場裝置遙測上報（wwwroot/mobile/js/telemetry.js 定期送來）。儀表板在「報表管理 → 現場遙測」。
    ///
    /// <para><b>允許未登入</b>：登入頁本身出錯、或工作階段已過期時的錯誤也要收得到。
    /// 防護全在 <see cref="ClientTelemetryService"/>（未登入只收錯誤、筆數上限、每 IP 限流、欄位截斷）。
    /// 一律回 204：手機不需要知道收了幾筆，也不該因為遙測失敗而重試或跳訊息。</para>
    /// </summary>
    [Route("api/telemetry")]
    [ApiController]
    [AllowAnonymous]
    public class ClientTelemetryController : ControllerBase
    {
        private readonly IClientTelemetryService _telemetry;
        private readonly IUserContext _userContext;

        public ClientTelemetryController(IClientTelemetryService telemetry, IUserContext userContext)
        {
            _telemetry = telemetry;
            _userContext = userContext;
        }

        [HttpPost("events")]
        [RequestSizeLimit(256 * 1024)]
        [PermissionGateExempt("只收呼叫者自己裝置的遙測、不回傳任何資料；必須允許未登入（登入頁出錯也要收得到），防護在 ClientTelemetryService")]
        public async Task<IActionResult> Events([FromBody] ClientTelemetryBatch batch, CancellationToken ct)
        {
            var authed = User.Identity?.IsAuthenticated == true;
            var source = new ClientTelemetrySource(
                UserId: authed ? _userContext.UserId : null,
                UserName: authed ? User.Identity?.Name : null,
                PlantId: authed ? _userContext.PlantId : null,
                Ip: HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent: Request.Headers.UserAgent.ToString());

            var written = await _telemetry.IngestAsync(batch, source, ct);
            return written < 0 ? StatusCode(StatusCodes.Status429TooManyRequests) : NoContent();
        }
    }
}
