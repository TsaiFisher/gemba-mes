using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Mobile
{
    /// <summary>
    /// 桌機兩步驟驗證登入後也要套用語系（個人 → 所在廠別預設 → zh-TW）。
    /// LoginWith2fa 是 Identity 內建頁面，原本只有密碼登入頁寫語系 cookie，開了 2FA 的帳號一律沒套用（2026-10-27）。
    /// 現在由 cookie 驗證事件 OnSignedIn（SignInLanguage）統一寫入。走真 HTTP：登入頁 → LoginWith2fa → 自算 TOTP。
    ///
    /// <para>專用測試廠 ZZLANG（預設 en-US）與專用帳號，不碰 admin；2FA 強制以組態蓋回 true（理由同 MobileAuthApiTests）。</para>
    /// </summary>
    [Collection("Integration")]
    public class TwoFactorLoginLanguageTests
    {
        private const string UserName = "lang2faUser";
        private const string Password = "Password123!";
        private const string PlantId  = "ZZLANG";

        private readonly TestWebApplicationFactory _factory;

        public TwoFactorLoginLanguageTests(TestWebApplicationFactory factory) => _factory = factory;

        [Fact]
        public async Task 兩步驟驗證登入後_個人語言留空_採用所在廠別的預設語言()
        {
            var key = await EnsureUserAsync();

            using var client = _factory.WithWebHostBuilder(b =>
                b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [DevTwoFactorBypassSignInManager.EnforcementConfigKey] = "true",
                }))).CreateClient(new WebApplicationFactoryClientOptions
                {
                    AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost"),
                });

            // ① 帳密 → 轉到 LoginWith2fa
            var token = await TokenAsync(client, "/Identity/Account/Login");
            var res = await client.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Email"] = UserName, ["Input.Password"] = Password, ["Input.RememberMe"] = "false",
                ["__RequestVerificationToken"] = token,
            }));
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
            var twoFaUrl = res.Headers.Location!.ToString();
            Assert.Contains("LoginWith2fa", twoFaUrl);
            Assert.False(HasCultureCookie(res), "密碼通過但還沒過 2FA，不該先寫語系。");

            // ② 驗證碼 → 登入完成，回應帶語系 cookie
            token = await TokenAsync(client, twoFaUrl);
            res = await client.PostAsync(twoFaUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.TwoFactorCode"] = Totp(key), ["Input.RememberMachine"] = "false",
                ["__RequestVerificationToken"] = token,
            }));
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
            Assert.DoesNotContain("LoginWith2fa", res.Headers.Location?.ToString() ?? "");
            Assert.True(HasCultureCookie(res), "兩步驟驗證登入後沒有寫語系 cookie。");
            Assert.Contains("c=en-US", Uri.UnescapeDataString(string.Join(";", res.Headers.GetValues("Set-Cookie"))));
        }

        private static bool HasCultureCookie(HttpResponseMessage res) =>
            res.Headers.TryGetValues("Set-Cookie", out var v) && v.Any(c => c.StartsWith(".AspNetCore.Culture="));

        private static async Task<string> TokenAsync(HttpClient client, string url)
        {
            var html = await client.GetStringAsync(url);
            var m = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
            if (!m.Success) m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(m.Success, $"{url} 找不到防偽 token。");
            return m.Groups[1].Value;
        }

        private async Task<string> EnsureUserAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (!await db.Plants.AnyAsync(p => p.Id == PlantId))
            {
                db.Plants.Add(new Plant { Id = PlantId, Name = "ZZ 語系測試廠", DefaultLanguage = "en-US" });
                await db.SaveChangesAsync();
            }
            await db.Plants.Where(p => p.Id == PlantId).ExecuteUpdateAsync(p => p.SetProperty(x => x.DefaultLanguage, "en-US"));

            var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await um.FindByNameAsync(UserName);
            if (user == null)
            {
                user = new ApplicationUser { UserName = UserName, Email = $"{UserName}@test.com" };
                Assert.True((await um.CreateAsync(user, Password)).Succeeded);
            }
            user.Language = null;
            user.ActivePlantId = PlantId;
            user.MustChangePassword = false;
            await um.UpdateAsync(user);
            await um.ResetAuthenticatorKeyAsync(user);
            await um.SetTwoFactorEnabledAsync(user, true);
            await um.SetLockoutEndDateAsync(user, null);
            await um.ResetAccessFailedCountAsync(user);
            return (await um.GetAuthenticatorKeyAsync(user))!;
        }

        /// <summary>RFC 6238 TOTP（SHA1、30 秒、6 碼），與 Identity 的 AuthenticatorTokenProvider 相同參數。</summary>
        private static string Totp(string base32Key)
        {
            var key = Base32Decode(base32Key);
            var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
            var msg = BitConverter.GetBytes(counter);
            if (BitConverter.IsLittleEndian) Array.Reverse(msg);
            var hash = new HMACSHA1(key).ComputeHash(msg);
            var offset = hash[^1] & 0x0F;
            var bin = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
            return (bin % 1_000_000).ToString("D6");
        }

        private static byte[] Base32Decode(string s)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            s = s.TrimEnd('=').ToUpperInvariant();
            var bytes = new List<byte>();
            int buffer = 0, bits = 0;
            foreach (var ch in s)
            {
                buffer = (buffer << 5) | alphabet.IndexOf(ch);
                bits += 5;
                if (bits >= 8) { bytes.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
            }
            return bytes.ToArray();
        }
    }
}
