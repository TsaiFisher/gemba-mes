using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Data
{
    /// <summary>
    /// L4（security-assessment-and-plan.md）：於任何密碼變更時，將新密碼雜湊寫入
    /// PasswordHistory。掛在 SaveChanges 攔截器，涵蓋所有變更路徑（管理員重設、
    /// 使用者自助變更、強制變更、建立帳號的初始密碼），毋須逐一修改各 Controller，
    /// 也不需跨部署專案（ProjectZ 等）補程式碼。
    ///
    /// 記錄採 Add-only：每次密碼變更新增一列雜湊。列極小，成長可忽略；
    /// 驗證僅取最近 N 筆比對，功能正確性不依賴修剪。若日後需限制表大小，
    /// 可比照 AuditLogRetentionJob 另建 prune 排程，不影響本攔截器。
    /// </summary>
    public sealed class PasswordHistoryInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Record(eventData.Context);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(eventData.Context);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private static void Record(DbContext? context)
        {
            if (context == null) return;

            // 只看 ApplicationUser：新增（初始密碼）或修改（PasswordHash 有異動）
            var userEntries = context.ChangeTracker.Entries<ApplicationUser>()
                .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified)
                .ToList();

            if (userEntries.Count == 0) return;

            var now = DateTime.UtcNow;
            foreach (var entry in userEntries)
            {
                var hashProp = entry.Property(nameof(ApplicationUser.PasswordHash));

                // 修改：僅在 PasswordHash「值」實際變動時記錄；新增：只要有值即記錄。
                // 不能只看 IsModified —— Identity UserStore.UpdateAsync 走 Context.Update()，
                // 所有屬性一律標記已修改，任何個人資料編輯（語系、廠別…）都會誤判為改密，
                // 灌入重複雜湊列，把驗證器「最近 N 筆」視窗擠滿同一雜湊、削弱重用檢查。
                var changed = entry.State == EntityState.Added
                    ? !string.IsNullOrEmpty(entry.Entity.PasswordHash)
                    : hashProp.IsModified
                      && !string.IsNullOrEmpty(entry.Entity.PasswordHash)
                      && !string.Equals((string?)hashProp.OriginalValue, entry.Entity.PasswordHash, StringComparison.Ordinal);

                if (!changed) continue;

                context.Set<PasswordHistory>().Add(new PasswordHistory
                {
                    UserId = entry.Entity.Id,
                    PasswordHash = entry.Entity.PasswordHash!,
                    CreatedAt = now
                });
            }
        }
    }
}
