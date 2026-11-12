using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using QRCoder;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Controllers
{
    /// <summary>
    /// 安全修正 M3：TOTP 兩階段驗證（2FA）自助啟用／復原碼。
    ///
    /// <para>基建沿用 Identity 標準：AuthenticatorTokenProvider（<c>AddDefaultTokenProviders</c>）、
    /// 金鑰/復原碼存 <c>AspNetUserTokens</c>；登入側 2FA 挑戰由 DefaultUI <c>LoginWith2fa</c> 處理。
    /// 本控制器僅補「啟用 UI」（DefaultUI 的 Manage 頁已於 M4 封鎖且屬 Core）。</para>
    ///
    /// <para>強制導向由 <see cref="SmartFactory.MES.Middleware.RequireTwoFactorForAdminMiddleware"/>
    /// 負責；管理員重置他人 2FA 見 <see cref="AdminReset"/>（不改 Core UserController）。</para>
    /// </summary>
    [Authorize(Policy = "AuthenticatedUser")]
    public class TwoFactorController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;

        /// <summary>驗證器 App 顯示的發行者名稱。</summary>
        private const string Issuer = "SmartFactory MES";

        /// <summary>復原碼組數。</summary>
        private const int RecoveryCodeCount = 10;

        public TwoFactorController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager)
        {
            _userManager = userManager;
            _signInManager = signInManager;
        }

        private static bool IsEn => CultureInfo.CurrentUICulture.Name.StartsWith("en");

        public class SetupViewModel
        {
            public string SharedKey { get; set; } = "";
            public string QrDataUri { get; set; } = "";
            public bool AlreadyEnabled { get; set; }
        }

        // ── 自助啟用 ────────────────────────────────────────────────────────────

        [HttpGet]
        [PermissionGateExempt("只讀寫呼叫者自己的 2FA 設定（_userManager.GetUserAsync(User)）")]
        public async Task<IActionResult> Setup()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();
            return View(await BuildSetupVmAsync(user));
        }

        // 補：驗證碼是 6 位數（僅 100 萬種組合），本端點驗證前未受任何速率限制保護，
        // 只靠 Identity 帳號鎖定（5 次/30 分鐘）防護不足以擋自動化暴力嘗試。
        // 沿用登入頁同一組「依 client IP 分桶」policy（Program.cs），生產環境 10 次/15 分鐘。
        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("loginRateLimit")]
        public async Task<IActionResult> Enable(string code)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();

            var verificationCode = (code ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty);
            var isValid = await _userManager.VerifyTwoFactorTokenAsync(
                user, _userManager.Options.Tokens.AuthenticatorTokenProvider, verificationCode);

            if (!isValid)
            {
                ModelState.AddModelError(string.Empty,
                    IsEn ? "Verification code is invalid. Please try again." : "驗證碼不正確，請重新輸入。");
                return View("Setup", await BuildSetupVmAsync(user));
            }

            await _userManager.SetTwoFactorEnabledAsync(user, true);
            var recoveryCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);
            await _signInManager.RefreshSignInAsync(user);
            return View("RecoveryCodes", recoveryCodes?.ToArray() ?? System.Array.Empty<string>());
        }

        // 原為 GET（`<a asp-action>` 直連）。重產復原碼會**作廢**既有復原碼，是不可逆的狀態變更，
        // 放在 GET 等於任何一張別人塞來的 `<img src="/TwoFactor/RegenerateRecoveryCodes">`
        // 就能在使用者不知情下把他手上抄好的復原碼全部作廢——2FA 裝置遺失時就此鎖死帳號。
        // 改 POST + AntiForgery 後，跨站誘導失效（權限面不變：本來就只動呼叫者自己的 token）。
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegenerateRecoveryCodes()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();
            if (!user.TwoFactorEnabled) return RedirectToAction(nameof(Setup));

            var recoveryCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);
            return View("RecoveryCodes", recoveryCodes?.ToArray() ?? System.Array.Empty<string>());
        }

        // ── helpers ─────────────────────────────────────────────────────────────

        private async Task<SetupViewModel> BuildSetupVmAsync(ApplicationUser user)
        {
            var key = await _userManager.GetAuthenticatorKeyAsync(user);
            if (string.IsNullOrEmpty(key))
            {
                await _userManager.ResetAuthenticatorKeyAsync(user);
                key = await _userManager.GetAuthenticatorKeyAsync(user);
            }

            return new SetupViewModel
            {
                SharedKey = FormatKey(key!),
                QrDataUri = BuildQrDataUri(user.Email ?? user.UserName ?? "user", key!),
                AlreadyEnabled = user.TwoFactorEnabled,
            };
        }

        private static string FormatKey(string unformattedKey)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < unformattedKey.Length; i += 4)
                sb.Append(unformattedKey.AsSpan(i, System.Math.Min(4, unformattedKey.Length - i))).Append(' ');
            return sb.ToString().Trim().ToUpperInvariant();
        }

        private static string BuildQrDataUri(string account, string unformattedKey)
        {
            var uri =
                $"otpauth://totp/{UrlEncoder.Default.Encode(Issuer)}:{UrlEncoder.Default.Encode(account)}" +
                $"?secret={unformattedKey}&issuer={UrlEncoder.Default.Encode(Issuer)}&digits=6";

            using var generator = new QRCodeGenerator();
            var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
            var png = new PngByteQRCode(data).GetGraphic(8);
            return "data:image/png;base64," + System.Convert.ToBase64String(png);
        }
    }
}
