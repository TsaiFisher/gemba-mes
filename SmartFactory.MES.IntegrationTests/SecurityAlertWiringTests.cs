using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartFactory.MES.Services.Security;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// 安全告警有真的接上：服務以單例註冊（去重表才跨請求有效），且登入速率限制拒絕時會呼叫它。
    /// 漏了 OnRejected，暴力破解被擋下時不會有任何告警，而畫面與日誌都看不出少了什麼。
    /// </summary>
    [Collection("Integration")]
    public class SecurityAlertWiringTests
    {
        private readonly TestWebApplicationFactory _factory;
        public SecurityAlertWiringTests(TestWebApplicationFactory factory) => _factory = factory;

        [Fact]
        public void AlertService_IsSingleton()
        {
            using var a = _factory.Services.CreateScope();
            using var b = _factory.Services.CreateScope();
            var x = a.ServiceProvider.GetRequiredService<ISecurityAlertService>();
            Assert.IsType<SecurityAlertService>(x);
            Assert.Same(x, b.ServiceProvider.GetRequiredService<ISecurityAlertService>());
        }

        [Fact]
        public void RateLimiter_HasOnRejectedHook()
        {
            var opts = _factory.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
            Assert.NotNull(opts.OnRejected);
        }
    }
}
