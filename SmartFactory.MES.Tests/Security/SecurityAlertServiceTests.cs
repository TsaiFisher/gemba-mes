using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFactory.MES.Services.Security;
using Xunit;

namespace SmartFactory.MES.Tests.Security
{
    /// <summary>
    /// M6：安全告警去重（冷卻窗）判定邏輯。
    /// </summary>
    public class SecurityAlertServiceTests
    {
        private static SecurityAlertService Build(int? cooldownMinutes = null)
        {
            var dict = new Dictionary<string, string?>();
            if (cooldownMinutes.HasValue)
                dict["Security:AlertCooldownMinutes"] = cooldownMinutes.Value.ToString();
            var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
            var httpFactory = new Mock<IHttpClientFactory>().Object;
            return new SecurityAlertService(
                NullLogger<SecurityAlertService>.Instance, config, httpFactory);
        }

        [Fact]
        public void ShouldRaise_FirstOccurrence_True()
        {
            var svc = Build();
            Assert.True(svc.ShouldRaise("LoginRateLimitExceeded", "1.2.3.4", DateTime.UtcNow));
        }

        [Fact]
        public void ShouldRaise_WithinCooldown_Deduped_False()
        {
            var svc = Build(cooldownMinutes: 5);
            var t0 = new DateTime(2026, 7, 7, 10, 0, 0, DateTimeKind.Utc);
            Assert.True(svc.ShouldRaise("LoginRateLimitExceeded", "1.2.3.4", t0));
            Assert.False(svc.ShouldRaise("LoginRateLimitExceeded", "1.2.3.4", t0.AddMinutes(2)));
        }

        [Fact]
        public void ShouldRaise_AfterCooldown_RaisesAgain_True()
        {
            var svc = Build(cooldownMinutes: 5);
            var t0 = new DateTime(2026, 7, 7, 10, 0, 0, DateTimeKind.Utc);
            Assert.True(svc.ShouldRaise("LoginRateLimitExceeded", "1.2.3.4", t0));
            Assert.True(svc.ShouldRaise("LoginRateLimitExceeded", "1.2.3.4", t0.AddMinutes(6)));
        }

        [Fact]
        public void ShouldRaise_DifferentIp_IndependentBucket_True()
        {
            var svc = Build(cooldownMinutes: 5);
            var t0 = new DateTime(2026, 7, 7, 10, 0, 0, DateTimeKind.Utc);
            Assert.True(svc.ShouldRaise("LoginRateLimitExceeded", "1.2.3.4", t0));
            // 另一 IP 於冷卻窗內仍應獨立告警
            Assert.True(svc.ShouldRaise("LoginRateLimitExceeded", "5.6.7.8", t0.AddMinutes(1)));
        }

        [Fact]
        public void ShouldRaise_NullIp_TreatedAsUnknown_Deduped()
        {
            var svc = Build(cooldownMinutes: 5);
            var t0 = new DateTime(2026, 7, 7, 10, 0, 0, DateTimeKind.Utc);
            Assert.True(svc.ShouldRaise("LoginRateLimitExceeded", null, t0));
            Assert.False(svc.ShouldRaise("LoginRateLimitExceeded", null, t0.AddMinutes(1)));
        }
    }
}
