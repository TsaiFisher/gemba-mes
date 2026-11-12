using System.Reflection;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// 驗證訊息在地化的查找鏈設定。部署專案以
    /// <see cref="LocalizedValidationExtensions.AddValidationResourceFallback{TResource}"/>
    /// 登記自己的資源型別，Core 不需認識任何部署專案的型別名稱。
    /// </summary>
    public sealed class LocalizedValidationOptions
    {
        /// <summary>Core SharedResource 之後的 fallback 資源型別（依登記順序查找）。</summary>
        public IList<Type> FallbackResourceTypes { get; } = new List<Type>();
    }

    /// <summary>
    /// 驗證訊息在地化的「根」設定（Core 內建，所有部署專案共用）。
    ///
    /// 解決的問題：ASP.NET Core 的 DataAnnotations 只有在屬性「顯式」設定
    /// ErrorMessage 時才會走 IStringLocalizer；使用「預設訊息」的
    /// [Required]/[MaxLength] 與非可空實值型別的「隱含必填」，adapter 直接
    /// 回傳框架英文預設、完全不查 resx → zh-TW 模式仍顯示英文驗證訊息，
    /// 且訊息中的 {0} 退回英文屬性名。
    ///
    /// 本設定一次補齊：
    ///  1. DataAnnotationLocalizerProvider → 以 SharedResource 查找訊息
    ///  2. LocalizableDefaultMessageProvider → 把無 ErrorMessage 的驗證屬性
    ///     （含隱含必填）回填預設訊息字串作為 resx key
    ///  3. LocalizableDisplayNameProvider → 屬性名經 SharedResource 解析為
    ///     在地化顯示名（{0} 隨語系切換）
    ///  4. ModelBindingMessageProvider → 型別轉換 / 繫結層的中文訊息
    ///  5. 移除重複（無在地化）的 DataAnnotationsModelValidatorProvider
    ///
    /// 顧問模組「不需」再各自註冊這些 provider；Core 已於 Program.cs 套用。
    ///
    /// 查找來源為「鏈式」：先 Core 的 <see cref="SharedResource"/>，查無 key 再依序
    /// 退到部署專案登記的補充資源（見 <see cref="AddValidationResourceFallback{TResource}"/>）。
    /// 部署專案因此不必為了讓自有欄位有中文驗證訊息而去修改 Core 的 SharedResource resx。
    /// </summary>
    public static class LocalizedValidationExtensions
    {
        public static IServiceCollection AddLocalizedValidation(this IServiceCollection services)
        {
            services.AddOptions<LocalizedValidationOptions>();

            // 1. DataAnnotations 訊息查找來源 = SharedResource → 部署專案 fallback 鏈
            services.AddOptions<MvcDataAnnotationsLocalizationOptions>()
                .Configure<IOptions<LocalizedValidationOptions>>((options, lvo) =>
                    options.DataAnnotationLocalizerProvider = (_, factory) =>
                        CreateChain(factory, lvo.Value));

            // 2. 無 ErrorMessage 的驗證屬性（含隱含必填）回填預設訊息 = resx key
            services.Configure<MvcOptions>(o =>
                o.ModelMetadataDetailsProviders.Add(new LocalizableDefaultMessageProvider()));

            // 3. 屬性名 → 在地化顯示名（驗證訊息 {0} 隨語系）
            services.AddOptions<MvcOptions>()
                .Configure<IStringLocalizerFactory, IOptions<LocalizedValidationOptions>>((o, f, lvo) =>
                    o.ModelMetadataDetailsProviders.Add(
                        new LocalizableDisplayNameProvider(CreateChain(f, lvo.Value))));

            // 4. ModelBinding 層（型別轉換 / NRT 必填）訊息：每次產生訊息時才依目前語系取字
            services.Configure<MvcOptions>(options =>
            {
                var mp = options.ModelBindingMessageProvider;
                mp.SetValueMustNotBeNullAccessor(f       => CoreText.Get("ModelBinding_RequiredFmt", f));
                mp.SetValueMustBeANumberAccessor(f       => CoreText.Get("ModelBinding_MustBeNumberFmt", f));
                mp.SetAttemptedValueIsInvalidAccessor((_, f) => CoreText.Get("ModelBinding_InvalidFmt", f));
                mp.SetUnknownValueIsInvalidAccessor(f    => CoreText.Get("ModelBinding_InvalidFmt", f));
                mp.SetMissingBindRequiredValueAccessor(f => CoreText.Get("ModelBinding_RequiredFmt", f));
                mp.SetMissingKeyOrValueAccessor(()       => CoreText.Get("ModelBinding_MissingKeyOrValue"));
                mp.SetNonPropertyValueMustBeANumberAccessor(()    => CoreText.Get("ModelBinding_MustBeNumber"));
                mp.SetNonPropertyAttemptedValueIsInvalidAccessor(_ => CoreText.Get("ModelBinding_Invalid"));
                mp.SetNonPropertyUnknownValueIsInvalidAccessor(()  => CoreText.Get("ModelBinding_Invalid"));
            });

            // 5. AddMvcCore 與 AddDataAnnotationsLocalization 各加一個
            // DataAnnotationsModelValidatorProvider；前者（無在地化）因去重機制
            // 總是優先 → zh-TW 仍英文。PostConfigure 在所有 Configure 後移除無在地化者。
            services.PostConfigure<MvcOptions>(options =>
            {
                const string typeName = "DataAnnotationsModelValidatorProvider";
                var providers = options.ModelValidatorProviders
                    .Where(p => p.GetType().Name == typeName)
                    .ToList();
                foreach (var p in providers.SkipLast(1))
                    options.ModelValidatorProviders.Remove(p);
            });

            return services;
        }

        /// <summary>
        /// 部署專案登記自己的補充資源型別（如 ProjectZ 的 <c>ProjectZSharedResource</c>）。
        /// Core 的 <c>SharedResource</c> 永遠優先，查無 key 才依登記順序往後退。
        /// 於部署專案的 <c>IAppModule.RegisterServices</c> 呼叫即可，不需修改 Core。
        /// </summary>
        public static IServiceCollection AddValidationResourceFallback<TResource>(
            this IServiceCollection services)
        {
            services.AddOptions<LocalizedValidationOptions>();
            return services.Configure<LocalizedValidationOptions>(o =>
            {
                // 多個模組重複登記同一份資源時只保留一次（順序即優先序）
                if (!o.FallbackResourceTypes.Contains(typeof(TResource)))
                    o.FallbackResourceTypes.Add(typeof(TResource));
            });
        }

        /// <summary>
        /// 建出「SharedResource → 部署專案 fallback」的查找鏈。
        /// 沒有任何 fallback 登記時直接回傳單一 localizer（行為與改版前完全相同）。
        /// </summary>
        private static IStringLocalizer CreateChain(
            IStringLocalizerFactory factory, LocalizedValidationOptions options)
        {
            var head = factory.Create(typeof(SharedResource));
            if (options.FallbackResourceTypes.Count == 0) return head;

            var chain = new List<IStringLocalizer>(options.FallbackResourceTypes.Count + 1) { head };
            foreach (var t in options.FallbackResourceTypes)
                chain.Add(factory.Create(t));
            return new ChainedStringLocalizer(chain);
        }

        /// <summary>
        /// 依序查找多份資源，第一個「找得到 key」的結果勝出；全部查無則回傳
        /// <c>ResourceNotFound=true</c> 的 key 本身（與單一 localizer 的退化行為一致）。
        /// </summary>
        private sealed class ChainedStringLocalizer : IStringLocalizer
        {
            private readonly IReadOnlyList<IStringLocalizer> _chain;

            public ChainedStringLocalizer(IReadOnlyList<IStringLocalizer> chain) => _chain = chain;

            public LocalizedString this[string name]
            {
                get
                {
                    foreach (var l in _chain)
                    {
                        var s = l[name];
                        if (!s.ResourceNotFound) return s;
                    }
                    return new LocalizedString(name, name, resourceNotFound: true);
                }
            }

            public LocalizedString this[string name, params object[] arguments]
            {
                get
                {
                    foreach (var l in _chain)
                    {
                        // 先以無參數版判斷 key 是否存在，命中後才用同一份資源做格式化
                        if (!l[name].ResourceNotFound) return l[name, arguments];
                    }
                    return new LocalizedString(
                        name, string.Format(name, arguments), resourceNotFound: true);
                }
            }

            public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var l in _chain)
                    foreach (var s in l.GetAllStrings(includeParentCultures))
                        if (seen.Add(s.Name))
                            yield return s;   // 前段資源的同名 key 優先，不被後段覆蓋
            }
        }

        /// <summary>
        /// 把沒有顯式 ErrorMessage 的 ValidationAttribute（含非可空實值型別的
        /// 隱含 [Required]），其「預設訊息模板字串」回填到 ErrorMessage。
        /// 回填後該字串成為 resx 的查找 key（SharedResource 已含這些預設字串），
        /// client（data-val-*）與 server（ModelState）兩邊訊息即隨語系翻譯。
        /// </summary>
        private sealed class LocalizableDefaultMessageProvider : IValidationMetadataProvider
        {
            // ValidationAttribute.ErrorMessageString 為 protected，回傳目前生效的
            // 訊息模板（未設 ErrorMessage 時即框架英文預設）
            private static readonly PropertyInfo? ErrMsgStr =
                typeof(ValidationAttribute).GetProperty("ErrorMessageString",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            public void CreateValidationMetadata(ValidationMetadataProviderContext context)
            {
                foreach (var vm in context.ValidationMetadata.ValidatorMetadata)
                {
                    if (vm is not ValidationAttribute attr) continue;
                    if (!string.IsNullOrEmpty(attr.ErrorMessage) ||
                        attr.ErrorMessageResourceName != null ||
                        attr.ErrorMessageResourceType != null)
                        continue;   // 已有顯式訊息/資源 → 不動（冪等）
                    try
                    {
                        if (ErrMsgStr?.GetValue(attr) is string def && def.Length > 0)
                            attr.ErrorMessage = def;   // 成為 resx 查找 key
                    }
                    catch { /* 個別屬性取預設失敗則略過，不影響啟動 */ }
                }

                // 非可空實值型別（bool/int/enum/DateTime…）的「隱含必填」：框架
                // 不會把 RequiredAttribute 放進 ValidatorMetadata，而是於 client/
                // server 驗證各自合成「無 ErrorMessage」的 RequiredAttribute、繞過
                // localizer。對策：在 metadata 階段主動補一個帶「預設訊息(=resx
                // key)」的 RequiredAttribute。idempotent：已有 Required 則不加。
                //
                // ⚠️ 只補「屬性」（表單欄位），不補 action 參數。補到參數上時，帶預設值的選填參數
                //    （`[FromQuery] decimal lotSize = 1`、`int page = 1`）沒帶就不再取預設值：
                //    [ApiController] 直接回 400 ProblemDetails（沒有 message，前端只顯示「操作失敗(400)」），
                //    一般 Controller 則 ModelState.IsValid=false——有檢查 IsValid 的存檔會靜默不存。
                //    框架本身對參數也不做隱含必填；這裡的本意只是替表單欄位的必填訊息在地化。
                //    （2026-10-05 部署專案手機首件頁實測：規格查詢從上線起一直 400。）
                var modelType = context.Key.ModelType;
                if (context.Key.MetadataKind == ModelMetadataKind.Property
                    && modelType.IsValueType && Nullable.GetUnderlyingType(modelType) == null)
                {
                    var hasRequired = false;
                    foreach (var m in context.ValidationMetadata.ValidatorMetadata)
                        if (m is RequiredAttribute) { hasRequired = true; break; }

                    if (!hasRequired)
                    {
                        var req = new RequiredAttribute();
                        try
                        {
                            if (ErrMsgStr?.GetValue(req) is string def && def.Length > 0)
                                req.ErrorMessage = def;
                        }
                        catch { /* 取預設失敗則略過 */ }
                        context.ValidationMetadata.ValidatorMetadata.Add(req);
                    }
                }
            }
        }

        /// <summary>
        /// 屬性未標 [Display]/[DisplayName] 時，把 ModelMetadata.DisplayName 設為
        /// 「以屬性名為 key 經 SharedResource localizer 解析」的延遲委派，使驗證
        /// 訊息的 {0} 隨語系顯示在地化欄位名（如 SupplierCode → 供應商 / Supplier）。
        /// 查無對應 resx key 時回退屬性名（行為不退化）。
        /// </summary>
        private sealed class LocalizableDisplayNameProvider : IDisplayMetadataProvider
        {
            private readonly IStringLocalizer _loc;

            public LocalizableDisplayNameProvider(IStringLocalizer localizer)
                => _loc = localizer;

            public void CreateDisplayMetadata(DisplayMetadataProviderContext context)
            {
                var propName = context.Key.Name;
                if (string.IsNullOrEmpty(propName)) return;          // 僅屬性層級

                foreach (var a in context.Attributes)
                    if (a is DisplayAttribute || a is DisplayNameAttribute)
                        return;                                       // 尊重顯式設定

                if (context.DisplayMetadata.DisplayName != null)
                    return;                                           // 已被其他 provider 設

                var key = propName;
                context.DisplayMetadata.DisplayName = () =>
                {
                    var s = _loc[key];
                    return s.ResourceNotFound ? key : s.Value;
                };
            }
        }
    }
}
