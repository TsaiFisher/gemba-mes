using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Areas.Identity.Pages.Account
{
    public class LoginModel : PageModel
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly ILogger<LoginModel> _logger;
        private readonly ApplicationDbContext _db;

        public LoginModel(SignInManager<ApplicationUser> signInManager, ILogger<LoginModel> logger, ApplicationDbContext db)
        {
            _signInManager = signInManager;
            _logger = logger;
            _db = db;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        public IList<AuthenticationScheme> ExternalLogins { get; set; } = new List<AuthenticationScheme>();

        public string? ReturnUrl { get; set; }

        [TempData]
        public string? ErrorMessage { get; set; }

        private static bool IsEnUs =>
            System.Globalization.CultureInfo.CurrentUICulture.Name == "en-US";

        public class InputModel
        {
            [Required]
            // Removed [EmailAddress] attribute to allow generic usernames
            public string Email { get; set; } = string.Empty;

            [Required]
            [DataType(DataType.Password)]
            public string Password { get; set; } = string.Empty;

            [Display(Name = "Remember me?")]
            public bool RememberMe { get; set; }
        }

        public async Task OnGetAsync(string? returnUrl = null)
        {
            if (!string.IsNullOrEmpty(ErrorMessage))
            {
                ModelState.AddModelError(string.Empty, ErrorMessage);
            }

            returnUrl ??= Url.Content("~/");

            // Clear the existing external cookie to ensure a clean login process
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

            ExternalLogins = (await _signInManager.GetExternalAuthenticationSchemesAsync()).ToList();

            ReturnUrl = returnUrl;
        }

        public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
        {
            returnUrl ??= Url.Content("~/");

            ExternalLogins = (await _signInManager.GetExternalAuthenticationSchemesAsync()).ToList();

            if (ModelState.IsValid)
            {
                // Identity allows login by username or email. By default, it uses UserName if not configured otherwise.
                // Since we changed UserName to be generic, PasswordSignInAsync will use it.
                // 失敗要累計帳號鎖定（Program.cs：5 次鎖 30 分鐘）。登入限流的每 IP 額度刻意放寬給共用 NAT，
                // 能放寬的前提就是這裡會鎖——原本是 false，桌機登入等於只剩每 IP 1200 次／15 分鐘的上限。
                var result = await _signInManager.PasswordSignInAsync(Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: true);
                if (result.Succeeded)
                {
                    _logger.LogInformation("User logged in.");
                    var user = await _signInManager.UserManager.FindByNameAsync(Input.Email)
                               ?? await _signInManager.UserManager.FindByEmailAsync(Input.Email);
                    await WriteAuditAsync(AuditAction.Login, user?.Id, user?.UserName ?? Input.Email, plantId: user?.ActivePlantId);

                    // 語系 cookie 由 cookie 驗證事件統一寫入（SignInLanguage.OnSignedInAsync），
                    // 兩步驟驗證與復原碼登入走 Identity 內建頁面，只有那裡三條路徑都會經過。

                    return LocalRedirect(returnUrl);
                }
                if (result.RequiresTwoFactor)
                {
                    return RedirectToPage("./LoginWith2fa", new { ReturnUrl = returnUrl, RememberMe = Input.RememberMe });
                }
                if (result.IsLockedOut)
                {
                    _logger.LogWarning("User account locked out.");
                    await WriteAuditAsync(AuditAction.LoginFailed, null, Input.Email, "帳號已鎖定");
                    return RedirectToPage("./Lockout");
                }
                else
                {
                    await WriteAuditAsync(AuditAction.LoginFailed, null, Input.Email, "密碼錯誤或帳號不存在");
                    // 與 LocalizedIdentityErrorDescriber 同一在地化慣例；原本硬寫英文
                    // "Invalid login attempt."，zh-TW 使用者看到的是英文訊息。
                    // 不細分「帳號不存在／密碼錯誤」，避免帳號列舉。
                    ModelState.AddModelError(string.Empty, IsEnUs
                        ? "Invalid login attempt."
                        : "帳號或密碼錯誤，請重新輸入。");
                    return Page();
                }
            }

            // If we got this far, something failed, redisplay form
            return Page();
        }

        private async Task WriteAuditAsync(string action, string? userId, string? userName, string? reason = null, string? plantId = null)
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            var ua = HttpContext.Request.Headers["User-Agent"].ToString();
            _db.AuditLogs.Add(new AuditLog
            {
                Timestamp = DateTime.UtcNow,
                Action = action,
                EntityType = "Authentication",
                UserId = userId,
                UserName = userName,
                PlantId = plantId,
                IpAddress = ip,
                UserAgent = ua,
                Changes = reason
            });
            await _db.SaveChangesAsync();
        }
    }
}
