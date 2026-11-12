using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.Services
{
    /// <summary>
    /// 驗證訊息在地化的「鏈式查找」：Core SharedResource 永遠優先，查無 key 才退到
    /// 部署專案登記的補充資源。此機制讓部署專案不必為了自有欄位而修改 Core 的 resx。
    /// </summary>
    public class LocalizedValidationChainTests
    {
        /// <summary>部署專案自有資源的替身。</summary>
        private sealed class FakeDeploymentResource { }

        /// <summary>依型別回傳固定字典的 localizer 工廠，模擬兩份 resx。</summary>
        private sealed class StubFactory : IStringLocalizerFactory
        {
            private readonly Dictionary<Type, Dictionary<string, string>> _byType;

            public StubFactory(Dictionary<Type, Dictionary<string, string>> byType) => _byType = byType;

            public IStringLocalizer Create(Type resourceSource) =>
                new StubLocalizer(_byType.TryGetValue(resourceSource, out var d)
                    ? d
                    : new Dictionary<string, string>());

            public IStringLocalizer Create(string baseName, string location) =>
                throw new NotSupportedException();
        }

        private sealed class StubLocalizer : IStringLocalizer
        {
            private readonly Dictionary<string, string> _values;
            public StubLocalizer(Dictionary<string, string> values) => _values = values;

            public LocalizedString this[string name] => _values.TryGetValue(name, out var v)
                ? new LocalizedString(name, v)
                : new LocalizedString(name, name, resourceNotFound: true);

            public LocalizedString this[string name, params object[] arguments] =>
                _values.TryGetValue(name, out var v)
                    ? new LocalizedString(name, string.Format(v, arguments))
                    : new LocalizedString(name, string.Format(name, arguments), resourceNotFound: true);

            public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
                _values.Select(kv => new LocalizedString(kv.Key, kv.Value));
        }

        private static IStringLocalizer BuildLocalizer(bool withFallback)
        {
            var factory = new StubFactory(new()
            {
                [typeof(SmartFactory.MES.SharedResource)] = new()
                {
                    ["SupplierCode"] = "供應商代碼",
                    ["Shared"]       = "來自 Core",
                    ["WithArg"]      = "核心 {0}"
                },
                [typeof(FakeDeploymentResource)] = new()
                {
                    ["SlipNo"]  = "單號",
                    ["Shared"]  = "來自部署專案",
                    ["WithArg"] = "部署 {0}"
                }
            });

            var services = new ServiceCollection();
            services.AddLocalizedValidation();
            if (withFallback)
                services.AddValidationResourceFallback<FakeDeploymentResource>();

            var provider = services.BuildServiceProvider()
                .GetRequiredService<IOptions<MvcDataAnnotationsLocalizationOptions>>()
                .Value.DataAnnotationLocalizerProvider;

            Assert.NotNull(provider);
            return provider!(typeof(object), factory);
        }

        [Fact]
        public void Chain_FallsBackToDeploymentResource_WhenKeyMissingInCore()
        {
            var loc = BuildLocalizer(withFallback: true);

            var s = loc["SlipNo"];
            Assert.False(s.ResourceNotFound);
            Assert.Equal("單號", s.Value);
        }

        [Fact]
        public void Chain_KeepsCoreValue_WhenKeyExistsInBoth()
        {
            var loc = BuildLocalizer(withFallback: true);

            Assert.Equal("來自 Core", loc["Shared"].Value);
        }

        [Fact]
        public void Chain_StillResolvesCoreOnlyKeys()
        {
            var loc = BuildLocalizer(withFallback: true);

            Assert.Equal("供應商代碼", loc["SupplierCode"].Value);
        }

        [Fact]
        public void Chain_ReturnsKeyWithResourceNotFound_WhenMissingEverywhere()
        {
            var loc = BuildLocalizer(withFallback: true);

            var s = loc["NoSuchKeyAnywhere"];
            Assert.True(s.ResourceNotFound);
            Assert.Equal("NoSuchKeyAnywhere", s.Value);
        }

        [Fact]
        public void Chain_FormatsUsingTheResourceThatOwnsTheKey()
        {
            var loc = BuildLocalizer(withFallback: true);

            // WithArg 兩邊都有 → 取 Core 那份做格式化
            Assert.Equal("核心 X", loc["WithArg", "X"].Value);
        }

        [Fact]
        public void Chain_GetAllStrings_UnionsBothResources_CorePreferredOnCollision()
        {
            var loc = BuildLocalizer(withFallback: true);

            var all = loc.GetAllStrings(false).ToDictionary(s => s.Name, s => s.Value);
            Assert.Equal("單號", all["SlipNo"]);
            Assert.Equal("供應商代碼", all["SupplierCode"]);
            Assert.Equal("來自 Core", all["Shared"]);   // 同名 key 不被後段覆蓋
        }

        [Fact]
        public void NoFallbackRegistered_BehavesExactlyAsBefore()
        {
            var loc = BuildLocalizer(withFallback: false);

            Assert.Equal("供應商代碼", loc["SupplierCode"].Value);
            Assert.True(loc["SlipNo"].ResourceNotFound);   // 未登記 fallback → 查不到
        }

        [Fact]
        public void AddValidationResourceFallback_IsIdempotent()
        {
            var services = new ServiceCollection();
            services.AddLocalizedValidation();
            services.AddValidationResourceFallback<FakeDeploymentResource>();
            services.AddValidationResourceFallback<FakeDeploymentResource>();

            var opts = services.BuildServiceProvider()
                .GetRequiredService<IOptions<LocalizedValidationOptions>>().Value;

            Assert.Single(opts.FallbackResourceTypes);
        }
    }
}
