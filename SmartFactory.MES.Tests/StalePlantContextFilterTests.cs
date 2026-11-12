using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Controllers;
using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Moq;
using SmartFactory.MES.Filters;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 「畫面在 A 廠渲染、送出時人已經在 B 廠」的守門。
    ///
    /// <para><b>要防的是哪一種壞法</b>：<c>ApplyGlobalValues</c> 在 <c>SaveChanges</c> 當下
    /// 才填 <c>PlantId ??= CurrentPlantId</c>，取的是<b>送出當下</b>的廠別，不是
    /// <b>渲染當下</b>的。所以「電腦上開著 A 廠的新增表單 → 在手機把廠別切到 B →
    /// 回電腦按存檔」會把單據建到 B 廠，而畫面上<b>沒有任何提示</b>。
    /// 修改／刪除反而安全（全域過濾讓它查不到而回 404），真正會靜默壞掉的是新增。</para>
    ///
    /// <para>手機端可以切廠之後，這條路徑從「要開兩個桌機分頁」變成「同一個人兩隻手」。</para>
    /// </summary>
    public class StalePlantContextFilterTests
    {
        private const string PlantA = "P001";
        private const string PlantB = "P002";

        private readonly Mock<IUserContext> _userContext = new();
        private readonly StalePlantContextFilter _filter;

        public StalePlantContextFilterTests()
        {
            _userContext.Setup(u => u.PlantId).Returns(PlantB);   // 送出當下人在 B 廠
            _filter = new StalePlantContextFilter(
                _userContext.Object,
                new PassThroughLocalizer(),
                Mock.Of<ILogger<StalePlantContextFilter>>());
        }

        [Fact]
        public void 畫面渲染的廠別與送出當下不同時擋下寫入()
        {
            var ctx = Build("POST", renderedPlant: PlantA);

            _filter.OnActionExecuting(ctx);

            var json = Assert.IsType<JsonResult>(ctx.Result);

            // 契約與 DbUniqueViolationExceptionFilter 一致：submitPanel 收到
            // { success:false, message } 會顯示訊息且**不關閉面板**，使用者填的資料還在。
            var success = json.Value!.GetType().GetProperty("success")!.GetValue(json.Value);
            Assert.Equal(false, success);

            var message = json.Value.GetType().GetProperty("message")!.GetValue(json.Value) as string;
            Assert.NotNull(message);
            Assert.Contains(PlantA, message);   // 訊息要說得出是哪兩個廠，
            Assert.Contains(PlantB, message);   // 否則使用者不知道發生了什麼事

            // 桌機面板據此提供「切回原廠別並照原內容存檔」（2026-11-11 驗收 C3）：
            // 回的是**畫面開啟時**的廠別，不是目前的廠別——切回去資料才會落在使用者填表時看到的廠
            Assert.Equal(StalePlantContextFilter.PlantContextChangedCode,
                json.Value.GetType().GetProperty("code")!.GetValue(json.Value));
            Assert.Equal(PlantA, json.Value.GetType().GetProperty("renderedPlant")!.GetValue(json.Value));
        }

        [Fact]
        public void 廠別一致時放行()
        {
            // 反面對照：少了這一支，一個「永遠擋下」的實作也會讓上面那支變綠——
            // 而那種實作的症狀是所有側欄面板都存不了檔。
            var ctx = Build("POST", renderedPlant: PlantB);

            _filter.OnActionExecuting(ctx);

            Assert.Null(ctx.Result);
        }

        [Fact]
        public void 廠區作業缺少標頭時拒絕()
        {
            // ⚠️ 刻意的放行。標頭只有 crud-panel.js 的 submitPanel 會送；
            // 其他送出點（一般 form post）目前沒有接上，全部擋掉會把大量正常作業
            // 變成錯誤訊息。覆蓋範圍要擴大，就是讓更多送出點帶上這個標頭。
            var ctx = Build("POST", renderedPlant: null);

            _filter.OnActionExecuting(ctx);

            Assert.Equal(409, Assert.IsType<JsonResult>(ctx.Result).StatusCode);
        }

        [Fact]
        public void 廠區作業空白標頭時拒絕()
        {
            // 渲染時就沒有廠別（例如帳號尚未設定），無從比較。
            var ctx = Build("POST", renderedPlant: "");

            _filter.OnActionExecuting(ctx);

            Assert.Equal(409, Assert.IsType<JsonResult>(ctx.Result).StatusCode);
        }

        [Theory]
        [InlineData("GET")]
        [InlineData("HEAD")]
        [InlineData("OPTIONS")]
        public void 唯讀方法不擋(string method)
        {
            // 讀到別廠的資料已經由全域查詢過濾擋掉了；這道守門只管**寫**進錯的廠。
            // 把 GET 也擋掉會讓使用者切完廠之後連頁面都開不起來。
            var ctx = Build(method, renderedPlant: PlantA);

            _filter.OnActionExecuting(ctx);

            Assert.Null(ctx.Result);
        }

        [Fact]
        public void 其他寫入方法一樣要擋()
        {
            // 只擋 POST 會漏掉 DELETE／PUT 這類動作。
            foreach (var method in new[] { "PUT", "PATCH", "DELETE" })
            {
                var ctx = Build(method, renderedPlant: PlantA);
                _filter.OnActionExecuting(ctx);
                Assert.True(ctx.Result is JsonResult, $"{method} 沒有被擋下。");
            }
        }

        [Fact]
        public void GlobalMaster_IsNotBlockedByAnotherPlantSnapshot()
        {
            var ctx = Build("POST", PlantA);
            var descriptor = (ControllerActionDescriptor)ctx.ActionDescriptor;
            descriptor.ControllerName = "Currency";
            descriptor.ControllerTypeInfo = typeof(SmartFactory.MES.Modules.BAS.Controllers.CurrencyController).GetTypeInfo();
            _filter.OnActionExecuting(ctx);
            Assert.Null(ctx.Result);
        }

        [Fact]
        public void NativeForm_UsesRenderedHiddenField()
        {
            var ctx = Build("POST", null);
            ctx.HttpContext.Request.ContentType = "application/x-www-form-urlencoded";
            ctx.HttpContext.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
                { ["__PlantContext"] = PlantB });
            _filter.OnActionExecuting(ctx);
            Assert.Null(ctx.Result);
        }

        [Fact]
        public async Task AsyncFilter_RejectsBeforeExecutingBusinessAction()
        {
            var ctx = Build("POST", PlantA);
            var executed = false;
            await _filter.OnActionExecutionAsync(ctx, () =>
            {
                executed = true;
                return Task.FromResult(new ActionExecutedContext(ctx, ctx.Filters, ctx.Controller));
            });
            Assert.False(executed);
            Assert.Equal(409, Assert.IsType<JsonResult>(ctx.Result).StatusCode);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("<img src=x onerror=alert(1)>")]
        public void 缺少或格式不符的快照_用缺少訊息且不回顯內容(string? rendered)
        {
            var ctx = Build("POST", rendered);
            _filter.OnActionExecuting(ctx);
            var json = Assert.IsType<JsonResult>(ctx.Result);
            Assert.Equal(409, json.StatusCode);
            var message = (string)json.Value!.GetType().GetProperty("message")!.GetValue(json.Value)!;
            Assert.Equal("PlantContextMissing", message);
        }

        [Fact]
        public void 全域主檔控制器不檢查快照()
        {
            var ctx = Build("POST", null);
            var descriptor = (ControllerActionDescriptor)ctx.ActionDescriptor;
            descriptor.ControllerName = "Product";
            descriptor.ControllerTypeInfo = typeof(SmartFactory.MES.Modules.BAS.Controllers.ProductController).GetTypeInfo();
            _filter.OnActionExecuting(ctx);
            Assert.Null(ctx.Result);
        }

        [Fact]
        public void 箱號單位是廠別資料_需要快照()
        {
            var ctx = Build("POST", PlantA);
            var descriptor = (ControllerActionDescriptor)ctx.ActionDescriptor;
            descriptor.ControllerName = "ContainerUnit";
            descriptor.ControllerTypeInfo = typeof(SmartFactory.MES.Modules.BAS.Controllers.ContainerUnitController).GetTypeInfo();
            _filter.OnActionExecuting(ctx);
            Assert.Equal(409, Assert.IsType<JsonResult>(ctx.Result).StatusCode);
        }

        /// <summary>
        /// BAS 與手機 API 混有廠別與全域控制器，靠名單分類。新增控制器沒歸類＝它的寫入
        /// 靜默落在防護之外（ContainerUnit 就是這樣漏掉的），所以列舉實際控制器、逐支要求歸類。
        /// </summary>
        [Theory]
        [InlineData("SmartFactory.MES.Modules.BAS.Controllers")]
        [InlineData("SmartFactory.MES.Api.Mobile")]
        public void 混合命名空間的每支控制器都必須明確歸類(string ns)
        {
            var names = typeof(StalePlantContextFilter).Assembly.GetTypes()
                .Where(t => t.Namespace == ns && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
                .Select(t => t.Name.EndsWith("Controller") ? t.Name[..^"Controller".Length] : t.Name)
                .ToList();
            Assert.NotEmpty(names);
            var (plant, other) = ns.EndsWith("Mobile")
                ? (StalePlantContextFilter.MobilePlantControllers, StalePlantContextFilter.MobileOtherControllers)
                : (StalePlantContextFilter.BasPlantControllers, StalePlantContextFilter.BasGlobalControllers);
            var unclassified = names.Where(n => !plant.Contains(n) && !other.Contains(n)).ToList();
            Assert.True(unclassified.Count == 0,
                $"未歸類的控制器（請加進 StalePlantContextFilter 的廠別或全域名單）：{string.Join(", ", unclassified)}");
            Assert.Empty(plant.Intersect(other));
        }

        // ── 骨架 ────────────────────────────────────────────────────────────

        private static ActionExecutingContext Build(string method, string? renderedPlant)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = method;
            if (renderedPlant != null)
                httpContext.Request.Headers[StalePlantContextFilter.HeaderName] = renderedPlant;

            var actionContext = new ActionContext(httpContext, new RouteData(), new ControllerActionDescriptor
            {
                ControllerName = "StockAdjustment",
                ControllerTypeInfo = typeof(SmartFactory.MES.Modules.INV.Controllers.StockAdjustmentController).GetTypeInfo()
            });
            return new ActionExecutingContext(
                actionContext, new List<IFilterMetadata>(),
                new Dictionary<string, object?>(), controller: new object());
        }

        /// <summary>把鍵與參數原樣組回字串，讓測試看得到廠別代碼有沒有代進訊息。</summary>
        private sealed class PassThroughLocalizer : IStringLocalizer<ProjectZSharedResource>
        {
            public LocalizedString this[string name] => new(name, name, false);

            public LocalizedString this[string name, params object[] arguments]
                => new(name, name + ": " + string.Join(" / ", arguments), false);

            public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
                => Array.Empty<LocalizedString>();
        }
    }
}
