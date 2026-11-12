using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Xml.Linq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Services
{
    /// <summary>
    /// 「規則寫好了，但沒接上去」的守門。
    /// <see cref="ValidTimeZoneAttribute"/> 本身有測試，但它若沒掛在欄位上、
    /// 或訊息沒有對應的 resx 鍵，畫面上都不會有任何異狀——前者是完全不驗，
    /// 後者是使用者看到未翻譯的英文模板字串。兩種都不會讓任何測試轉紅。
    /// </summary>
    public class TimeZoneWiringTests
    {
        [Fact]
        public void PlantDefaultTimeZone_CarriesTheTimeZoneValidation()
        {
            var property = typeof(Plant).GetProperty(nameof(Plant.DefaultTimeZone))!;

            Assert.NotNull(property.GetCustomAttribute<ValidTimeZoneAttribute>());
        }

        /// <summary>
        /// 驗證訊息的 resx 鍵就是屬性的預設訊息模板（見 LocalizedValidationExtensions 的回填機制），
        /// 因此這裡比對的是「屬性實際帶的字串」，而不是另外抄一份字面值——
        /// 抄一份的話，改了屬性訊息卻忘了改 resx，這支仍然會綠。
        /// </summary>
        [Theory]
        [InlineData("zh-TW")]
        [InlineData("en-US")]
        public void ValidationMessage_HasASharedResourceEntry(string culture)
        {
            // 屬性沒有顯式 ErrorMessage（那正是在地化機制的前提），生效中的模板放在
            // protected 的 ErrorMessageString——LocalizableDefaultMessageProvider 取的也是它。
            var key = (string?)typeof(ValidationAttribute)
                .GetProperty("ErrorMessageString", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(new ValidTimeZoneAttribute());
            Assert.False(string.IsNullOrWhiteSpace(key));

            var keys = ResourceKeys($"SharedResource.{culture}.resx");

            Assert.Contains(key!, keys);
        }

        /// <summary>
        /// 兩個欄位標籤走 SharedResource（表單以 asp-for 產生 label）。少了就顯示屬性名。
        /// </summary>
        [Theory]
        [InlineData("zh-TW", "DefaultTimeZone")]
        [InlineData("zh-TW", "TimeZoneId")]
        [InlineData("en-US", "DefaultTimeZone")]
        [InlineData("en-US", "TimeZoneId")]
        public void FieldLabels_HaveASharedResourceEntry(string culture, string key)
        {
            Assert.Contains(key, ResourceKeys($"SharedResource.{culture}.resx"));
        }

        private static HashSet<string> ResourceKeys(string fileName)
        {
            var path = Path.Combine(FindProjectRoot(), "Resources", fileName);
            Assert.True(File.Exists(path), $"找不到資源檔：{path}");

            return XDocument.Load(path).Root!
                .Elements("data")
                .Select(e => e.Attribute("name")!.Value)
                .ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>從測試輸出目錄往上找主專案目錄（resx 不是內嵌成可列舉的形式，只能讀原始檔）。</summary>
        private static string FindProjectRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SmartFactory.MES.csproj")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
