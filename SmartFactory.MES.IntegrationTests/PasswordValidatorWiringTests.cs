using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using SmartFactory.MES.Services.Security;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// 密碼驗證器有真的掛進 Identity 管線：單元測試只證明驗證器本身會擋，
    /// 漏了 Program.cs 那一行，建立帳號與改密碼照樣放行弱密碼，且沒有任何訊號。
    /// </summary>
    [Collection("Integration")]
    public class PasswordValidatorWiringTests
    {
        private readonly TestWebApplicationFactory _factory;
        public PasswordValidatorWiringTests(TestWebApplicationFactory factory) => _factory = factory;

        [Fact]
        public async Task UserManager_RejectsBlocklistedPassword_AndHasHistoryValidator()
        {
            using var scope = _factory.Services.CreateScope();
            var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            Assert.Contains(um.PasswordValidators, v => v is BlocklistPasswordValidator);
            Assert.Contains(um.PasswordValidators, v => v is PasswordHistoryValidator);

            var user = new ApplicationUser { UserName = "wiring-check", Email = "wiring@x.test" };
            var results = new List<IdentityResult>();
            foreach (var v in um.PasswordValidators)
                results.Add(await v.ValidateAsync(um, user, "Password"));   // 內建規則外，黑名單也要擋
            Assert.Contains(results, r => r.Errors.Any(e => e.Code == "PasswordInBlocklist"));
        }
    }
}
