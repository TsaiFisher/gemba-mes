using Microsoft.AspNetCore.Mvc.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using SmartFactory.MES.Modules.BAS;
using SmartFactory.MES.Services;
using System.Globalization;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 顧問模組欄位名已從 Core 主導的 SharedResource 搬到 ProjectZ 自有的
    /// ProjectZValidationResource，靠 Core 的鏈式查找（BASModule 登記 fallback）解析。
    /// 這組測試用「真的 embedded resx」端到端驗證，避免登記漏掉或 resx 沒進組件時
    /// 驗證訊息的 {0} 靜默退回英文屬性名。
    /// </summary>
    public class ValidationResourceFallbackTests
    {
        private static IStringLocalizer BuildRealChain()
        {
            var services = new ServiceCollection();
            services.AddLogging();   // ResourceManagerStringLocalizerFactory 需要 ILoggerFactory
            services.AddLocalization(o => o.ResourcesPath = "Resources");
            services.AddLocalizedValidation();

            // 走真正的模組註冊路徑，而不是在測試裡自己補一行 fallback
            new BASModule().RegisterServices(services, new Microsoft.Extensions.Configuration
                .ConfigurationBuilder().Build());

            var sp = services.BuildServiceProvider();
            var provider = sp.GetRequiredService<IOptions<MvcDataAnnotationsLocalizationOptions>>()
                .Value.DataAnnotationLocalizerProvider;
            Assert.NotNull(provider);
            return provider!(typeof(object), sp.GetRequiredService<IStringLocalizerFactory>());
        }

        private static T WithCulture<T>(string culture, Func<T> f)
        {
            var original = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo(culture);
                return f();
            }
            finally { CultureInfo.CurrentUICulture = original; }
        }

        [Theory]
        [InlineData("SlipNo",             "zh-TW", "單號")]
        [InlineData("SlipNo",             "en-US", "Slip No")]
        [InlineData("ActualOverheadCost", "zh-TW", "實際製造費用")]
        public void MovedConsultantField_ResolvesViaFallback(string key, string culture, string expected)
        {
            var loc = BuildRealChain();
            var s = WithCulture(culture, () => loc[key]);

            Assert.False(s.ResourceNotFound, $"{key} 應由 ProjectZValidationResource 解析");
            Assert.Equal(expected, s.Value);
        }

        [Theory]
        [InlineData("zh-TW")]
        [InlineData("en-US")]
        public void CoreOwnedField_StillResolvesFromSharedResource(string culture)
        {
            var loc = BuildRealChain();
            var s = WithCulture(culture, () => loc["Home"]);

            Assert.False(s.ResourceNotFound);
        }

        /// <summary>
        /// 撞名保護：DefectRate 在 ProjectZSharedResource 是選單標題「不良率報表」，
        /// 在 ProjectZValidationResource 是欄位名「不良率」。鏈上不得取到選單標題。
        /// </summary>
        [Fact]
        public void CollidingKey_ResolvesToFieldName_NotMenuTitle()
        {
            var loc = BuildRealChain();
            var s = WithCulture("zh-TW", () => loc["DefectRate"]);

            Assert.Equal("不良率", s.Value);
        }
    }
}
