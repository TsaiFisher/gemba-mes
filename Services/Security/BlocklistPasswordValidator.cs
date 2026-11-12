using System.Globalization;
using Microsoft.AspNetCore.Identity;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services.Security
{
    /// <summary>
    /// L4（security-assessment-and-plan.md）：密碼政策強化 — 拒絕弱密碼。
    /// 依 NIST SP 800-63B，「黑名單／情境字典比對」比強制特殊字元組成更有效，故本驗證器：
    ///   (1) 拒絕包含帳號名或 Email 本地部分的密碼（情境相關字典）；
    ///   (2) 拒絕常見弱密碼（內建清單 + 可由 <c>Security:PasswordBlocklist</c> 擴充）。
    /// 與內建 <see cref="PasswordOptions"/>（長度/大小寫/數字）疊加生效。
    /// </summary>
    public sealed class BlocklistPasswordValidator : IPasswordValidator<ApplicationUser>
    {
        private readonly HashSet<string> _blocklist;

        // 常見弱密碼（小寫比對）。刻意精簡——目的是擋最常見的，而非取代完整字典服務。
        private static readonly string[] DefaultBlocklist =
        {
            "password", "passw0rd", "12345678", "123456789", "1234567890",
            "qwerty", "qwertyui", "qwerty123", "11111111", "00000000",
            "iloveyou", "admin123", "administrator", "letmein", "welcome",
            "abc12345", "1qaz2wsx", "changeme", "smartfactory", "factory123",
        };

        public BlocklistPasswordValidator(IConfiguration configuration)
        {
            _blocklist = new HashSet<string>(DefaultBlocklist, StringComparer.OrdinalIgnoreCase);
            var extra = configuration.GetSection("Security:PasswordBlocklist").Get<string[]>();
            if (extra != null)
            {
                foreach (var w in extra)
                    if (!string.IsNullOrWhiteSpace(w)) _blocklist.Add(w.Trim());
            }
        }

        public Task<IdentityResult> ValidateAsync(
            UserManager<ApplicationUser> manager, ApplicationUser user, string? password)
        {
            if (string.IsNullOrEmpty(password))
                return Task.FromResult(IdentityResult.Success); // 空值交由其他驗證器處理

            var isEn = (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en");
            var pwdLower = password.ToLowerInvariant();

            // (2) 常見弱密碼
            if (_blocklist.Contains(password) || _blocklist.Contains(pwdLower))
            {
                return Task.FromResult(Fail("PasswordInBlocklist", isEn
                    ? "This password is too common. Please choose a less predictable one."
                    : "此密碼過於常見，請改用較不易被猜測的密碼。"));
            }

            // (1) 情境字典：帳號名 / Email 本地部分（長度 >= 3 才比對，避免過短誤判）
            if (ContainsToken(pwdLower, user.UserName))
            {
                return Task.FromResult(Fail("PasswordContainsUserName", isEn
                    ? "Password must not contain your account name."
                    : "密碼不可包含您的帳號名稱。"));
            }

            var email = user.Email;
            var localPart = string.IsNullOrEmpty(email) ? null : email.Split('@')[0];
            if (ContainsToken(pwdLower, localPart))
            {
                return Task.FromResult(Fail("PasswordContainsEmail", isEn
                    ? "Password must not contain your email address."
                    : "密碼不可包含您的電子郵件位址。"));
            }

            return Task.FromResult(IdentityResult.Success);
        }

        private static bool ContainsToken(string pwdLower, string? token)
            => !string.IsNullOrEmpty(token)
               && token.Length >= 3
               && pwdLower.Contains(token.ToLowerInvariant(), StringComparison.Ordinal);

        private static IdentityResult Fail(string code, string description)
            => IdentityResult.Failed(new IdentityError { Code = code, Description = description });
    }
}
