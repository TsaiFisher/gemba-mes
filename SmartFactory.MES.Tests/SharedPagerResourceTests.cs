using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using System.Globalization;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 兩支共用分頁 partial（_Pagination / _PageSizeSelect）是全庫第一個從 View 注入
    /// <see cref="IStringLocalizer{SharedResource}"/> 的地方——這條解析路徑在此之前
    /// 從來沒有被走過。
    ///
    /// ⚠️ 這種錯不會拋例外、不會讓建置失敗、也不會讓任何既有測試變紅：
    /// ResourceManagerStringLocalizer 找不到鍵時**直接把鍵名當成翻譯回傳**，
    /// 於是中文介面上就出現「PagerSummary」這個英文字樣。只有真的把鍵取出來比對
    /// 才驗得到，所以這裡用真的 embedded resx 端到端跑一次。
    /// </summary>
    public class SharedPagerResourceTests
    {
        private static readonly string[] PagerKeys =
        {
            "PagerSummary", "PagerNav", "PagerFirst",
            "PagerPrevious", "PagerNext", "PagerLast", "PageSize",
        };

        private static IStringLocalizer BuildRealLocalizer()
        {
            var services = new ServiceCollection();
            services.AddLogging();   // ResourceManagerStringLocalizerFactory 需要 ILoggerFactory
            services.AddLocalization(o => o.ResourcesPath = "Resources");
            return services.BuildServiceProvider()
                .GetRequiredService<IStringLocalizer<SharedResource>>();
        }

        private static void WithCulture(string culture, Action body)
        {
            var original = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo(culture);
                body();
            }
            finally { CultureInfo.CurrentUICulture = original; }
        }

        [Theory]
        [InlineData("zh-TW")]
        [InlineData("en-US")]
        public void PagerKeys_ResolveToRealText_NotTheKeyName(string culture)
        {
            var localizer = BuildRealLocalizer();
            WithCulture(culture, () =>
            {
                foreach (var key in PagerKeys)
                {
                    var s = localizer[key];
                    Assert.False(s.ResourceNotFound,
                        $"{culture}：SharedResource 找不到 '{key}'——畫面上會直接印出鍵名");
                    Assert.NotEqual(key, s.Value);
                }
            });
        }

        [Fact]
        public void PagerSummary_TakesThreeArguments_PageIndexTotalPagesTotalItems()
        {
            var localizer = BuildRealLocalizer();
            WithCulture("zh-TW", () =>
            {
                // 三個佔位符缺一不可：這個鍵在收斂前曾經同時存在 2 參數與 3 參數兩種格式，
                // 少一個 {2} 不會報錯，只是「共 N 筆」從畫面上消失。
                var raw = localizer["PagerSummary"].Value;
                Assert.Contains("{0}", raw);
                Assert.Contains("{1}", raw);
                Assert.Contains("{2}", raw);
                Assert.Equal("第 2 / 7 頁，共 135 筆", string.Format(raw, 2, 7, 135));
            });
            WithCulture("en-US", () =>
                Assert.Equal("Page 2 of 7, 135 records",
                    string.Format(localizer["PagerSummary"].Value, 2, 7, 135)));
        }
    }
}
