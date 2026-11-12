using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.Services
{
    /// <summary>
    /// ModelBinding 層（型別轉換、必填）訊息原本不分語系一律中文：英文介面輸入「abc」到數量欄，
    /// 看到的是「「Quantity」必須輸入數值。」。訊息要在產生當下依語系取字。
    /// </summary>
    public class ModelBindingMessageLocalizationTests
    {
        private static Microsoft.AspNetCore.Mvc.ModelBinding.Metadata.DefaultModelBindingMessageProvider Provider()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLocalization();
            services.AddLocalizedValidation();
            return services.BuildServiceProvider().GetRequiredService<IOptions<MvcOptions>>().Value.ModelBindingMessageProvider;
        }

        [Theory]
        [InlineData("en-US", "The field Qty must be a number.")]
        [InlineData("zh-TW", "「Qty」必須輸入數值。")]
        public void ValueMustBeANumber_FollowsUiCulture(string culture, string expected)
        {
            var mp = Provider();
            var previous = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                Assert.Equal(expected, mp.ValueMustBeANumberAccessor("Qty"));
                Assert.Equal(CoreText.Get("ModelBinding_InvalidFmt", "Qty"), mp.AttemptedValueIsInvalidAccessor("abc", "Qty"));
                Assert.Equal(CoreText.Get("ModelBinding_RequiredFmt", "Qty"), mp.ValueMustNotBeNullAccessor("Qty"));
            }
            finally
            {
                CultureInfo.CurrentUICulture = previous;
            }
        }
    }
}
