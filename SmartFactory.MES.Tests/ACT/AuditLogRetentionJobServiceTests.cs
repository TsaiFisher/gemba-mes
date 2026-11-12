using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using SmartFactory.MES.Modules.ACT.Services.Jobs;
using Xunit;

namespace SmartFactory.MES.Tests.ACT
{
    /// <summary>
    /// M5：AuditLog 保留期排程的保留天數解析邏輯（純設定，不觸 DB）。
    /// 刪除行為（ExecuteDeleteAsync 需關聯型 provider）由整合測試以真 PG 驗證。
    /// </summary>
    public class AuditLogRetentionJobServiceTests
    {
        private static AuditLogRetentionJobService Make(int? days)
        {
            var dict = new Dictionary<string, string?>();
            if (days.HasValue)
                dict["Security:AuditLogRetentionDays"] = days.Value.ToString();
            var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
            return new AuditLogRetentionJobService(scopeFactory: null!, config, logger: null!);
        }

        [Fact]
        public void Unset_UsesDefault730()
        {
            Assert.Equal(730, Make(null).ResolveRetentionDays());
        }

        [Theory]
        [InlineData(365)]
        [InlineData(1000)]
        public void PositiveValue_IsHonored(int days)
        {
            Assert.Equal(days, Make(days).ResolveRetentionDays());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void NonPositive_FallsBackToDefault(int days)
        {
            Assert.Equal(730, Make(days).ResolveRetentionDays());
        }
    }
}
