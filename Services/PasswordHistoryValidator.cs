using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// L4（security-assessment-and-plan.md）：禁止重用最近 N 次密碼。
    /// 於密碼變更前，以 PasswordHasher 比對候選明文與 PasswordHistory 中最近 N 筆雜湊，
    /// 命中即拒。N 由 <c>Security:PasswordHistoryCount</c> 設定（預設 5，&lt;=0 視為停用）。
    /// 歷史雜湊的寫入由 <see cref="Data.PasswordHistoryInterceptor"/> 於 SaveChanges 負責。
    /// </summary>
    public sealed class PasswordHistoryValidator : IPasswordValidator<ApplicationUser>
    {
        private readonly ApplicationDbContext _db;
        private readonly int _historyCount;

        public PasswordHistoryValidator(ApplicationDbContext db, IConfiguration configuration)
        {
            _db = db;
            _historyCount = configuration.GetValue<int?>("Security:PasswordHistoryCount") ?? 5;
        }

        public async Task<IdentityResult> ValidateAsync(
            UserManager<ApplicationUser> manager, ApplicationUser user, string? password)
        {
            // 停用、無密碼、或使用者尚無 Id（理論上不會）→ 放行
            if (_historyCount <= 0 || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(user.Id))
                return IdentityResult.Success;

            var recentHashes = await _db.Set<PasswordHistory>()
                .Where(h => h.UserId == user.Id)
                .OrderByDescending(h => h.CreatedAt)
                .ThenByDescending(h => h.Id)
                .Take(_historyCount)
                .Select(h => h.PasswordHash)
                .ToListAsync();

            foreach (var hash in recentHashes)
            {
                var verify = manager.PasswordHasher.VerifyHashedPassword(user, hash, password);
                if (verify != PasswordVerificationResult.Failed)
                {
                    return IdentityResult.Failed(new IdentityError
                    {
                        Code = "PasswordRecentlyUsed",
                        Description = IsEnglish()
                            ? $"This password was used recently. Please choose a password you have not used in your last {_historyCount} passwords."
                            : $"此密碼近期使用過，請勿重複使用最近 {_historyCount} 組密碼。"
                    });
                }
            }

            return IdentityResult.Success;
        }

        private static bool IsEnglish()
            => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase);
    }
}
