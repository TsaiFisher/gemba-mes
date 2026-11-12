using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.Services
{
    /// <summary>
    /// LocalizedValidation 替非可空實值型別補的隱含 [Required] 只能落在<b>屬性</b>上。
    ///
    /// <para>原本連 action 參數也補：<c>[FromQuery] decimal lotSize = 1</c> 沒帶時不取預設值，
    /// [ApiController] 回 400、一般 Controller 的 ModelState 變 invalid。部署專案的手機首件頁
    /// 規格查詢因此從上線起一直 400（2026-10-05）；控制器單元測試直接呼叫方法、繞過繫結，看不到。</para>
    /// </summary>
    public class ImplicitRequiredScopeTests
    {
        private static IModelMetadataProvider Metadata()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLocalization();
            services.AddControllers();
            services.AddLocalizedValidation();
            return services.BuildServiceProvider().GetRequiredService<IModelMetadataProvider>();
        }

        private sealed class Form { public decimal Qty { get; set; } public bool Flag { get; set; } }

        private sealed class Probe
        {
            public void Act(decimal lotSize = 1, int page = 1) { }
        }

        [Fact]
        public void 表單屬性的非可空實值型別_仍補上必填()
        {
            var md = Metadata().GetMetadataForProperty(typeof(Form), nameof(Form.Qty));
            Assert.Contains(md.ValidatorMetadata, v => v is RequiredAttribute);
        }

        [Theory]
        [InlineData("lotSize")]
        [InlineData("page")]
        public void Action參數_不補必填(string name)
        {
            var param = typeof(Probe).GetMethod(nameof(Probe.Act))!.GetParameters().Single(p => p.Name == name);
            var md = ((ModelMetadataProvider)Metadata()).GetMetadataForParameter(param);
            Assert.DoesNotContain(md.ValidatorMetadata, v => v is RequiredAttribute);
        }
    }
}
