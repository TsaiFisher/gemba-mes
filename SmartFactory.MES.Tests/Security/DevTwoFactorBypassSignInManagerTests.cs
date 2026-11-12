using System.Linq;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Security
{
    /// <summary>
    /// 開發環境跳過登入 2FA 挑戰的判定。
    ///
    /// <para>測的是 <see cref="DevTwoFactorBypassSignInManager.ShouldBypass"/> 這個純函式。
    /// 真正要釘住的不是「開發環境會跳過」，而是**另外三格都不跳過**——尤其
    /// 「正式環境 ＋ 有人把旗標設成 false」那一格：那是唯一會讓正式站台在無人察覺下
    /// 失去登入 2FA 的組合，也是這道判定之所以要有兩個條件的全部理由。</para>
    /// </summary>
    public class DevTwoFactorBypassSignInManagerTests
    {
        [Fact]
        public void ShouldBypass_DevelopmentAndEnforcementOff_IsTrue()
        {
            Assert.True(DevTwoFactorBypassSignInManager.ShouldBypass(
                isDevelopment: true, enforcementEnabled: false));
        }

        [Fact]
        public void ShouldBypass_NotDevelopment_StaysEnforcedEvenWhenFlagIsOff()
        {
            // 這一格是加上環境條件的全部意義：組態被誤帶到正式機也跳不過去。
            Assert.False(DevTwoFactorBypassSignInManager.ShouldBypass(
                isDevelopment: false, enforcementEnabled: false));
        }

        [Fact]
        public void ShouldBypass_DevelopmentButEnforcementOn_IsFalse()
        {
            // 開發環境的預設：沒有明講不強制，就照常挑戰。
            Assert.False(DevTwoFactorBypassSignInManager.ShouldBypass(
                isDevelopment: true, enforcementEnabled: true));
        }

        [Fact]
        public void ShouldBypass_ProductionDefaults_IsFalse()
        {
            Assert.False(DevTwoFactorBypassSignInManager.ShouldBypass(
                isDevelopment: false, enforcementEnabled: true));
        }

        /// <summary>
        /// 組態鍵必須與中介判讀的是同一個字串。兩邊各記一份字面值（見常數的說明），
        /// 所以這裡把值本身釘死——分歧的話，症狀會是「強制關掉了、登入還要輸」，
        /// 而那看起來像功能沒做好，不像打錯字。
        /// </summary>
        [Fact]
        public void EnforcementConfigKey_MatchesTheKeyTheMiddlewareReads()
        {
            Assert.Equal("Security:TwoFactorEnforcementEnabled",
                DevTwoFactorBypassSignInManager.EnforcementConfigKey);
        }

        /// <summary>
        /// 整套機制的**唯一支點**：模組的註冊要排在 Identity 之後，DI 才會解析到子類別。
        ///
        /// <para>Identity 用的是 <c>TryAddScoped</c>——它「已經有就不加」。所以若模組哪天被
        /// 搬到 <c>AddIdentity</c> 之前，這行 <c>AddScoped</c> 會先落地，接著 Identity 的
        /// <c>TryAdd</c> 看到已有註冊而跳過……**看起來還是我們的類別**。真正會出事的是相反的
        /// 順序假設被打破：本測試模擬 Identity 先註冊，斷言最後一筆（＝ DI 解析到的那筆）
        /// 是子類別。沒有這支，「覆寫其實沒生效」的症狀是「開發環境還是要輸驗證碼」——
        /// 那看起來像組態沒讀到，不像註冊順序。</para>
        /// </summary>
        [Fact]
        public void Module_RegistrationWinsOverIdentityDefault()
        {
            var services = new ServiceCollection();

            // AddIdentity 就是這樣註冊的：TryAddScoped<SignInManager<TUser>, SignInManager<TUser>>()
            services.TryAddScoped<SignInManager<ApplicationUser>, SignInManager<ApplicationUser>>();

            new DevTwoFactorBypassModule().RegisterServices(
                services, new ConfigurationBuilder().Build());

            var resolved = Assert.IsType<ServiceDescriptor>(
                services.Last(d => d.ServiceType == typeof(SignInManager<ApplicationUser>)));

            Assert.Equal(typeof(DevTwoFactorBypassSignInManager), resolved.ImplementationType);
            Assert.Equal(ServiceLifetime.Scoped, resolved.Lifetime);
        }
    }
}
