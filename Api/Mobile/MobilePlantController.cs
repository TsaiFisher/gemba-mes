using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Api.Mobile
{
    /// <summary>
    /// 手機版的「我在哪個廠」。
    ///
    /// <para><b>為什麼需要這支端點</b>：手機端在此之前<b>完全看不到自己在哪個廠</b>——
    /// <c>home.html</c> 與 <c>home.page.js</c> 對 plant 的比對是 0 筆。更麻煩的是
    /// <c>auth.js</c> 明明把 <c>plantId</c> 存進 <c>sessionStorage</c>，卻沒有任何一處讀它，
    /// 於是它長期是**死資料**：看起來手機知道廠別，其實畫面上一個字都沒有。
    /// 而 <c>ActivePlantId</c> 是使用者資料列上的欄位、桌機手機共用同一個值，
    /// 所以現場人員在手機上是哪個廠，取決於他上次在**電腦上**切到哪——
    /// 他可能根本沒有電腦，也沒有任何線索可以判斷。</para>
    ///
    /// <para><b>為什麼不是加在 <c>/api/mobile/auth/me</c> 上</b>：那支在 Core，
    /// 而且不在 <c>core-drift-baseline.txt</c> 裡，為了一個欄位開 Core PR 不划算。
    /// 這支是新檔，沒有漂移；後續的廠別「切換」也會落在同一個路由前綴下。</para>
    ///
    /// <para>⚠️ <b>刻意不用 <see cref="IUserContext.AllowedPlantIds"/>。</b>
    /// 那支介面的空清單有兩個意思：角色不限廠（＝<b>全部</b>），以及使用者根本沒載入。
    /// 兩者都回空集合，呼叫端分不出來——直覺實作會讓權限最大的人看到空的廠別清單。
    /// 這裡只問「我現在在哪個廠」，用 <see cref="IUserContext.PlantId"/> 就夠了。</para>
    /// </summary>
    [Route("api/mobile/plants")]
    [ApiController]
    [Authorize(Policy = "AuthenticatedUser")]
    public class MobilePlantController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly IUserContext _userContext;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IPlantSwitchService _plantSwitch;

        public MobilePlantController(
            ApplicationDbContext db,
            IUserContext userContext,
            UserManager<ApplicationUser> userManager,
            IPlantSwitchService plantSwitch)
        {
            _db = db;
            _userContext = userContext;
            _userManager = userManager;
            _plantSwitch = plantSwitch;
        }

        /// <summary>目前作用中的廠別（手機頁首顯示用）。</summary>
        [HttpGet("current")]
        [PermissionGateExempt(
            "只讀呼叫者自己的帳號狀態（ActivePlantId 與該廠的名稱），與桌機 UserSettings/Index 同一類。"
            + "刻意不掛手機模組閘門：廠別是每一頁的前提，而 RequireMobileModuleAsync 的 403 "
            + "在畫面上長得跟『沒有廠別』一樣，會讓沒有手機模組權限的人連自己在哪個廠都看不到——"
            + "而那些人（例如只來簽核的主管）正是最需要知道自己在哪個廠的一群。")]
        public async Task<IActionResult> GetCurrent()
        {
            var plantId = _userContext.PlantId;
            if (string.IsNullOrEmpty(plantId))
            {
                // 分清「從沒選過」與「選過但已失效」（管理員把廠別從角色移除、或角色被移除）：
                // 後者手機要明講「廠別已失效、請重新選擇」並直接打開切換面板，
                // 原本兩者都顯示「未設定」，使用者在功能頁只看到「權限不足」（2026-11-11 筆電驗收 A4）。
                var stale = _userContext is UserContext uc && uc.HasStaleAuthorization;
                return Ok(new { plantId = (string?)null, name = (string?)null, nameEn = (string?)null, invalid = stale });
            }

            // 中英兩個名稱都給，由前端依 getLang() 自行取用（CodeEntry 慣例，
            // 見 MobileMfgController.GetCodeEntries）。伺服端不挑語言的理由是：
            // 手機的語言是 localStorage.mobileLang，與使用者資料列上的 Language 可以不同。
            var plant = await _db.Plants.AsNoTracking()
                .Where(p => p.Id == plantId)
                .Select(p => new { p.Id, p.Name, p.NameEn })
                .FirstOrDefaultAsync();

            // 廠別代碼查不到對應主檔（資料不一致）時仍要回代碼本身，
            // 不能整個回 null——那會讓畫面顯示成「沒有廠別」，與真的沒設定分不出來。
            return Ok(new
            {
                plantId,
                name = plant?.Name,
                nameEn = plant?.NameEn,
                invalid = false,
            });
        }

        /// <summary>這個使用者可以切到哪些廠。</summary>
        [HttpGet]
        [PermissionGateExempt(
            "回傳的內容完全由呼叫者自己的角色指派（RolePlantAssignment）決定，不含任何別人的資料。"
            + "刻意不掛 MenuKey 閘門，因為在這個題目上它做不到事："
            + "UserSettings 被 PermissionController 明確排除在權限矩陣之外（管理員改不了），"
            + "而種子給非管理員的是 CanRead=true／CanUpdate=false——"
            + "掛 CanRead 是恆真的裝飾品，掛 CanUpdate 則會把現場人員全部擋掉且沒有人打得開。"
            + "真正的授權是 RolePlantAssignment，由 IPlantSwitchService 強制執行。")]
        public async Task<IActionResult> GetSwitchable()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            var switchable = await _plantSwitch.GetSwitchablePlantsAsync(user.Id);

            return Ok(new
            {
                currentPlantId = user.ActivePlantId,

                // 畫面用它決定要不要說明「你的角色被限制在這幾個廠」。
                // ⚠️ 不可用清單長度反推：不限廠的角色在只有一個廠的系統裡，清單也是一筆。
                switchable.RoleIsRestricted,

                plants = switchable.Plants.Select(p => new { p.Id, p.Name, p.NameEn }),
            });
        }

        /// <summary>
        /// 切換作用中廠別。
        ///
        /// <para><b>沒有 <c>[ValidateAntiForgeryToken]</c></b>，與其他手機 API 一致：
        /// 認證 Cookie 是 <c>SameSite=Lax</c>（<c>Program.cs</c>），跨站的 POST 根本帶不上它。
        /// 手機頁是靜態 HTML，拿不到 Razor 產生的防偽權杖；為了這一支去做一套發權杖的機制，
        /// 換來的是與既有 6 支手機控制器不一致的作法，而防護並沒有增加。</para>
        /// </summary>
        [HttpPost("switch")]
        public async Task<IActionResult> Switch([FromBody] PlantSwitchRequest req)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            var outcome = await _plantSwitch.TrySwitchAsync(user.Id, req?.PlantId);

            // 這裡就是本端點的權限閘門本身：允不允許切到這個廠，由 RolePlantAssignment 決定
            // （見 IPlantSwitchService）。Forbid() 在 /api 路徑下是真的 403 而不是導向登入頁
            // ——Program.cs 的 OnRedirectToAccessDenied 已經對 /api 做過這件事。
            if (outcome is PlantSwitchOutcome.NotAllowed or PlantSwitchOutcome.UserNotFound)
                return Forbid();

            if (outcome == PlantSwitchOutcome.SaveFailed)
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { error = "PLANT_SWITCH_FAILED" });

            // 沒有個人語系的帳號跟著廠別走（LanguagePreference.Resolve，與桌機切換同一份規則）：
            // 回傳新廠的語系讓手機端換掉 localStorage.mobileLang，並同步桌機 culture cookie。
            var followsPlant = !LanguagePreference.HasPersonal(user);
            var language = await LanguagePreference.ResolveForUserAsync(_db, user, req!.PlantId);
            if (followsPlant) LanguagePreference.AppendCookie(Response, language);
            return Ok(new { plantId = req.PlantId, language, followsPlant });
        }
    }

    /// <summary>切換廠別的請求內容。</summary>
    public record PlantSwitchRequest(string? PlantId);
}
