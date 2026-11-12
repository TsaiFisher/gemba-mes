using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services.Security;
using Xunit;

namespace SmartFactory.MES.Tests.Security
{
    /// <summary>
    /// L4：弱密碼黑名單驗證器（帳號名/Email/常見弱密碼）。
    /// </summary>
    public class BlocklistPasswordValidatorTests
    {
        // UserManager 於本驗證器未被使用（僅介面簽章需要），測試傳 null!。
        private static (BlocklistPasswordValidator validator, UserManager<ApplicationUser> mgr) Build(
            IEnumerable<KeyValuePair<string, string?>>? config = null)
        {
            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(config ?? new Dictionary<string, string?>())
                .Build();
            return (new BlocklistPasswordValidator(cfg), null!);
        }

        private static ApplicationUser User(string userName = "alice", string? email = "alice@corp.com")
            => new() { UserName = userName, Email = email };

        [Theory]
        [InlineData("password")]
        [InlineData("Password")]      // 大小寫不敏感
        [InlineData("12345678")]
        [InlineData("qwerty123")]
        [InlineData("changeme")]
        public async Task Rejects_CommonWeakPasswords(string pwd)
        {
            var (v, mgr) = Build();
            var result = await v.ValidateAsync(mgr, User(), pwd);
            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Code == "PasswordInBlocklist");
        }

        [Fact]
        public async Task Rejects_PasswordContainingUserName()
        {
            var (v, mgr) = Build();
            var result = await v.ValidateAsync(mgr, User("johnsmith"), "Xk9johnsmithQ");
            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Code == "PasswordContainsUserName");
        }

        [Fact]
        public async Task Rejects_PasswordContainingEmailLocalPart()
        {
            var (v, mgr) = Build();
            var result = await v.ValidateAsync(mgr, User("alice", "bobby.tables@corp.com"), "Zq7bobby.tables");
            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Code == "PasswordContainsEmail");
        }

        [Fact]
        public async Task Accepts_StrongUnrelatedPassword()
        {
            var (v, mgr) = Build();
            var result = await v.ValidateAsync(mgr, User(), "Tr0ub4dour-x9Kq");
            Assert.True(result.Succeeded);
        }

        [Fact]
        public async Task Accepts_DevSeedPassword_NotBroken()
        {
            // 開發種子 admin/Dev12345 不得被本驗證器擋下（否則乾淨 DB / CI 種子失敗）
            var (v, mgr) = Build();
            var result = await v.ValidateAsync(mgr, User("admin", "admin@localhost"), "Dev12345");
            Assert.True(result.Succeeded);
        }

        [Fact]
        public async Task Rejects_ConfiguredExtraBlocklistWord()
        {
            var (v, mgr) = Build(new Dictionary<string, string?>
            {
                ["Security:PasswordBlocklist:0"] = "AcmeCorp2026",
            });
            var result = await v.ValidateAsync(mgr, User(), "AcmeCorp2026");
            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Code == "PasswordInBlocklist");
        }

        [Fact]
        public async Task ShortUserName_NotFalsePositive()
        {
            // 帳號名 < 3 字元不參與比對，避免把含 "ab" 的正常密碼誤擋
            var (v, mgr) = Build();
            var result = await v.ValidateAsync(mgr, User("ab"), "Zq7about-house");
            Assert.True(result.Succeeded);
        }
    }
}
