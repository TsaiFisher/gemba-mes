using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using SmartFactory.MES.Modules.ACT.Services.Jobs;
using Xunit;

namespace SmartFactory.MES.Tests.ACT
{
    /// <summary>
    /// L4：PasswordHistory 保留排程的「每人保留筆數」解析邏輯（純設定，不觸 DB）。
    /// 刪除行為（ExecuteDeleteAsync 需關聯型 provider）由整合測試以真 PG 驗證。
    /// </summary>
    public class PasswordHistoryRetentionJobServiceTests
    {
        private static PasswordHistoryRetentionJobService Make(int? keep, int? historyCount)
        {
            var dict = new Dictionary<string, string?>();
            if (keep.HasValue)
                dict["Security:PasswordHistoryRetentionKeep"] = keep.Value.ToString();
            if (historyCount.HasValue)
                dict["Security:PasswordHistoryCount"] = historyCount.Value.ToString();
            var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
            return new PasswordHistoryRetentionJobService(scopeFactory: null!, config, logger: null!);
        }

        [Fact]
        public void Unset_UsesDefaultKeep24()
        {
            // 保留下限未設 → 24；歷史比對 N 未設 → 視為 5；max(24,5)=24
            Assert.Equal(24, Make(keep: null, historyCount: null).ResolveKeepCount());
        }

        [Theory]
        [InlineData(30)]
        [InlineData(50)]
        public void PositiveKeep_IsHonored_WhenAboveHistoryCount(int keep)
        {
            Assert.Equal(keep, Make(keep, historyCount: 5).ResolveKeepCount());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        public void NonPositiveKeep_FallsBackToDefault24(int keep)
        {
            Assert.Equal(24, Make(keep, historyCount: 5).ResolveKeepCount());
        }

        [Fact]
        public void KeepNeverBelowActiveHistoryCount()
        {
            // 驗證器 N=40 高於保留下限 10 → 保留 40，確保清理不削弱「禁用最近 N 次」
            Assert.Equal(40, Make(keep: 10, historyCount: 40).ResolveKeepCount());
        }
    }
}
