using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Services.Jobs;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// L4：PasswordHistory 保留清理排程刪除行為（真 PG，因 ExecuteDeleteAsync 需關聯型 provider）。
    /// 驗證「每人保留最近 K 筆、刪其餘」且保留的正是最新 K 筆（不削弱重用檢查）。
    /// </summary>
    [Collection("Integration")]
    public class PasswordHistoryRetentionJobTests
    {
        private readonly TestWebApplicationFactory _factory;

        public PasswordHistoryRetentionJobTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task RunOnce_KeepsMostRecentKPerUser_DeletesOlder()
        {
            var scopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
            const int keep = 3;
            var userId = "PWDHIST_RETENTION_" + Guid.NewGuid().ToString("N");
            var baseTime = DateTime.UtcNow.AddDays(-100);

            // 建一位測試使用者（PasswordHistory.UserId 對 AspNetUsers 有 FK），
            // 種 keep+3 = 6 筆歷史，CreatedAt 遞增（h0 最舊 … h5 最新）。
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.Users.Add(new ApplicationUser
                {
                    Id = userId,
                    UserName = userId,
                    NormalizedUserName = userId.ToUpperInvariant(),
                    Email = userId + "@test.local",
                    NormalizedEmail = (userId + "@test.local").ToUpperInvariant(),
                    SecurityStamp = Guid.NewGuid().ToString(),
                });
                for (int i = 0; i < keep + 3; i++)
                {
                    db.PasswordHistories.Add(new PasswordHistory
                    {
                        UserId = userId,
                        PasswordHash = "hash-" + i,
                        CreatedAt = baseTime.AddDays(i),
                    });
                }
                await db.SaveChangesAsync();
            }

            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["Security:PasswordHistoryRetentionKeep"] = keep.ToString(),
                    ["Security:PasswordHistoryCount"] = "5", // < keep 生效值？此處 N=5 > keep=3 → ResolveKeepCount 應取 5
                }).Build();

            // 注意：ResolveKeepCount = max(keep=3, N=5) = 5 → 應保留最新 5 筆、刪最舊 1 筆。
            var job = new PasswordHistoryRetentionJobService(
                scopeFactory, config, NullLogger<PasswordHistoryRetentionJobService>.Instance);

            var deleted = await job.RunOnceAsync(CancellationToken.None);

            // RunOnceAsync 回傳「全表」刪除數：共用測試庫上其他測試的使用者也可能
            // 超過保留上限而被一併修剪，總數不可斷言為定值。此處只驗下限，
            // 本使用者的精確行為（6 → 保留最新 5、刪的正是最舊一筆）由下方斷言把關。
            Assert.True(deleted >= 1, $"應至少刪除本使用者 1 筆，實際 deleted={deleted}");

            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var remaining = await db.PasswordHistories
                    .Where(h => h.UserId == userId)
                    .OrderBy(h => h.CreatedAt)
                    .Select(h => h.PasswordHash)
                    .ToListAsync();

                Assert.Equal(5, remaining.Count);
                // 最舊一筆(hash-0)被刪，保留 hash-1 … hash-5
                Assert.DoesNotContain("hash-0", remaining);
                Assert.Contains("hash-1", remaining);
                Assert.Contains("hash-5", remaining);
            }
        }
    }
}
