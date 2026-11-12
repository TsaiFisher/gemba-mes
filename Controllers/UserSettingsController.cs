using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Controllers
{
    [Authorize(Policy = "AuthenticatedUser")]
    public class UserSettingsController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<ApplicationRole> _roleManager;
        private readonly ApplicationDbContext _context;
        private readonly IPlantSwitchService _plantSwitch;
        private readonly IUserContext _userContext;
        private readonly IRoleSwitchService _roleSwitch;

        public UserSettingsController(
            UserManager<ApplicationUser> userManager,
            RoleManager<ApplicationRole> roleManager,
            ApplicationDbContext context,
            IPlantSwitchService plantSwitch,
            IUserContext userContext,
            IRoleSwitchService roleSwitch)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _context = context;
            _plantSwitch = plantSwitch;
            _userContext = userContext;
            _roleSwitch = roleSwitch;
        }

        [PermissionGateExempt("只讀呼叫者自己的帳號設定（WHERE UserName = 目前登入者）")]
        public async Task<IActionResult> Index()
        {
            var user = await _userManager.Users
                .Include(u => u.ActiveRole!)
                .ThenInclude(r => r.PlantAssignments)
                .ThenInclude(rpa => rpa.Plant)
                .Include(u => u.ActivePlant)   // 個人時區那張卡要顯示「現在生效的是哪一層」
                .FirstOrDefaultAsync(u => u.UserName == User.Identity!.Name);
            if (user == null) return NotFound();

            // 生效時區**一律取自 IUserContext**，不在這裡重算一次。
            // 「兩處各自實作同一條鏈、答案卻不同」正是 §11 #13 那個缺陷的形狀
            // （UserContext 用 ??、Resolver 用 IsNullOrWhiteSpace），不要再製造第三份。
            // 檢視只負責判斷這個值來自哪一層，判斷不了的情況一律顯示為預設值。
            ViewBag.EffectiveTimeZoneId = _userContext.TimeZoneId ?? DateTimeService.FallbackTimeZoneId;

            var roles = await _userManager.GetRolesAsync(user);
            ViewBag.UserRoles = await _roleManager.Roles.Where(r => roles.Contains(r.Name!)).ToListAsync();

            // 可選廠別與「能不能切」必須是同一份判定（見 IPlantSwitchService 的說明）：
            // 這裡列得出來、SwitchPlant 卻擋下來（或反過來）是最難查的那種缺陷。
            var switchable = await _plantSwitch.GetSwitchablePlantsAsync(user.Id);

            // 檢視是 `ViewBag.UserPlants as List<Plant>`，型別對不上會靜靜變成 null
            // （畫面上就是廠別下拉整個不見，沒有錯誤訊息），所以這裡明寫 ToList()。
            ViewBag.UserPlants = switchable.Plants.ToList();
            ViewBag.RoleIsRestricted = switchable.RoleIsRestricted;

            return View(user);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SwitchRole(string roleId)
        {
            var outcome = await _roleSwitch.TrySwitchAsync(User, roleId);
            return outcome switch
            {
                RoleSwitchOutcome.Switched => RedirectToAction("Index", "Home"),
                RoleSwitchOutcome.UserNotFound => NotFound(),
                RoleSwitchOutcome.NotAllowed => Forbid(),
                _ => StatusCode(500, CoreText.Get("Msg_SwitchRoleFailedRetry"))
            };
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SwitchPlant(string plantId)
        {
            // 判定與 Index() 列清單走同一個服務，規則只有一份。
            var outcome = await _plantSwitch.TrySwitchAsync(_userManager.GetUserId(User), plantId);

            if (outcome == PlantSwitchOutcome.SaveFailed)
                return StatusCode(500, CoreText.Get("Msg_SwitchPlantFailedRetry"));

            // 沒有個人語系的帳號跟著廠別走：換到哪個廠就用那個廠的預設語系（LanguagePreference.Resolve）。
            // 有個人語系的不動——個人優先。
            if (outcome == PlantSwitchOutcome.Switched)
            {
                var user = await _userManager.GetUserAsync(User);
                if (!LanguagePreference.HasPersonal(user))
                    LanguagePreference.AppendCookie(Response, await LanguagePreference.ResolveForUserAsync(_context, user, plantId));
            }

            // ⚠️ NotAllowed／UserNotFound 維持原本的「安靜地不切換、導回首頁」。
            // 這其實不是好行為（按了沒反應、也沒有訊息），但改成顯示錯誤會動到桌機的
            // 既有互動，不在本次範圍內；手機端是新做的，那一側回 403 讓使用者知道。
            // 兩邊的**規則**仍然是同一份，差別只在呈現。
            return RedirectToAction("Index", "Home");
        }

        /// <summary>
        /// 自助設定個人時區。空值＝跟隨廠別時區（不是「沒有時區」）。
        ///
        /// <para><b>為什麼需要這個入口</b>：時區鏈是「廠別 → 個人 → 預設」三層，
        /// 但中間那層長期沒有任何入口——`UserSettingsViewModel.TimeZoneId` 是零引用的死碼，
        /// 而使用者維護畫面沒有這個欄位，存一次檔反而把該帳號的個人時區清成 null
        /// （§11 #14；該處已由 _UserForm 補上欄位修掉）。管理員代設得了，本人設不了，
        /// 於是文件與程式都說三層、實際只有兩層。</para>
        ///
        /// <para><b>為什麼壞值要擋在這裡</b>：解不開的時區代碼不會拋到使用者面前——
        /// <see cref="DateTimeService"/> 接住之後退回預設時區，畫面一切正常，
        /// 只有日期會差一天，而沒有任何一則訊息提到時區。下拉本身擋得住手動輸入，
        /// 但擋不住改請求內容，所以判準要落在伺服器端，且與執行期用的是同一個
        /// （<see cref="TimeZoneCatalog.IsResolvable"/>，不另外維護白名單）。</para>
        /// </summary>
        [PermissionGateExempt("只寫呼叫者自己的帳號設定（WHERE Id = 目前登入者）")]
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetTimeZone(string? timeZoneId)
        {
            // 空字串與空白一律收斂成 null。留著空白字串會讓 UserContext 那條鏈
            // 誤判成「有值」——那正是 §11 #13 修掉的形狀，不要從這個入口再種一次。
            var value = string.IsNullOrWhiteSpace(timeZoneId) ? null : timeZoneId.Trim();

            if (value != null && !TimeZoneCatalog.IsResolvable(value))
            {
                TempData["TimeZoneError"] = true;
                return RedirectToAction(nameof(Index));
            }

            var user = await _context.Users.FindAsync(_userManager.GetUserId(User));
            if (user == null) return NotFound();

            // 存 IANA 形式：同一筆資料換到另一種 OS 的主機才解得開（見 TimeZoneCatalog）。
            user.TimeZoneId = value == null ? null : TimeZoneCatalog.Normalize(value);

            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException) { return StatusCode(500, CoreText.Get("Msg_SaveTimeZoneFailedRetry")); }

            TempData["TimeZoneSaved"] = true;
            return RedirectToAction(nameof(Index));
        }

        // 語言切換必須開放匿名：登入頁（/Identity/Account/Login）本身就在導覽列顯示語言下拉，
        // 若此動作受 [Authorize] 保護，未登入者點擊只會被導回登入頁、Cookie 永遠寫不進去
        // ——症狀就是「登入畫面沒有中英版」。GET 只寫 cookie，不碰資料庫（GET 不得改狀態）。
        [AllowAnonymous]
        [HttpGet]
        public IActionResult SetLanguage(string culture, string returnUrl)
        {
            if (!LanguagePreference.Supported.Contains(culture))
                return BadRequest(CoreText.Get("Msg_UnsupportedLanguage"));

            LanguagePreference.AppendCookie(Response, culture);
            return BackTo(returnUrl);
        }

        /// <summary>
        /// 已登入者從導覽列切換語系：除了 cookie，也寫回帳號偏好（見 <see cref="LanguagePreference"/>）。
        /// </summary>
        [PermissionGateExempt("只寫呼叫者自己的語系偏好（WHERE Id = 目前登入者）")]
        [HttpPost, ValidateAntiForgeryToken]
        [ActionName(nameof(SetLanguage))]
        public async Task<IActionResult> SetLanguagePost(string culture, string returnUrl)
        {
            if (!LanguagePreference.Supported.Contains(culture))
                return BadRequest(CoreText.Get("Msg_UnsupportedLanguage"));

            LanguagePreference.AppendCookie(Response, culture);
            await LanguagePreference.PersistAsync(_context, User.FindFirstValue(ClaimTypes.NameIdentifier), culture);
            return BackTo(returnUrl);
        }

        private IActionResult BackTo(string returnUrl)
        {
            // 匿名可達 → returnUrl 視為不可信輸入：非本站網址一律退回首頁，
            // 而不是讓 LocalRedirect 丟 InvalidOperationException 變成 500。
            return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : Redirect("/");
        }
    }
}
