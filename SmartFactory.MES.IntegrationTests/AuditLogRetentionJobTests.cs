using System;
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
    /// M5：AuditLog 保留期清理排程刪除行為（真 PG，因 ExecuteDeleteAsync 需關聯型 provider）。
    /// </summary>
    [Collection("Integration")]
    public class AuditLogRetentionJobTests
    {
        private readonly TestWebApplicationFactory _factory;

        public AuditLogRetentionJobTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task RunOnce_DeletesOlderThanRetention_KeepsRecent()
        {
            var scopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
            var marker = "RETENTION_TEST_" + Guid.NewGuid().ToString("N");

            // 保留 730 天：種一筆 800 天前（應刪）、一筆 10 天前（應留）。
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.AuditLogs.Add(new AuditLog
                {
                    Id = marker + "_OLD",
                    Timestamp = DateTime.UtcNow.AddDays(-800),
                    Action = "Create", EntityType = "RetentionTest", EntityId = marker,
                });
                db.AuditLogs.Add(new AuditLog
                {
                    Id = marker + "_NEW",
                    Timestamp = DateTime.UtcNow.AddDays(-10),
                    Action = "Create", EntityType = "RetentionTest", EntityId = marker,
                });
                await db.SaveChangesAsync();
            }

            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["Security:AuditLogRetentionDays"] = "730",
                }).Build();
            var job = new AuditLogRetentionJobService(
                scopeFactory, config, NullLogger<AuditLogRetentionJobService>.Instance);

            var deleted = await job.RunOnceAsync(CancellationToken.None);

            Assert.True(deleted >= 1); // 至少刪掉本測試的 OLD（可能含其他歷史舊資料）

            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var oldExists = await db.AuditLogs.IgnoreQueryFilters()
                    .AnyAsync(a => a.Id == marker + "_OLD");
                var newExists = await db.AuditLogs.IgnoreQueryFilters()
                    .AnyAsync(a => a.Id == marker + "_NEW");
                Assert.False(oldExists); // 逾保留期 → 已刪
                Assert.True(newExists);  // 保留期內 → 保留
            }
        }
    }
}
