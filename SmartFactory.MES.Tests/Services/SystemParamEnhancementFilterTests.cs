using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Moq;
using SmartFactory.MES.Filters;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Services;

/// <summary>
/// 系統參數畫面（庫存設定）擴充 Filter 測試。
/// 這是「簡化模式」開關唯一的使用者入口：GET 讀出注入 ViewData、POST 存回 SystemParams。
/// </summary>
public class SystemParamEnhancementFilterTests : TestBase
{
    private readonly Mock<ISystemParamService> _mockSysParam = new();
    private readonly SystemParamEnhancementFilter _filter;

    public SystemParamEnhancementFilterTests()
    {
        _filter = new SystemParamEnhancementFilter(_context, _mockSysParam.Object);
    }

    // ══════════════════════════════════════════════════════════════
    // 測試骨架
    // ══════════════════════════════════════════════════════════════

    private static ActionExecutingContext BuildExecutingContext(
        string controller, string action, string method = "GET",
        Dictionary<string, string>? form = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        if (form != null)
        {
            httpContext.Request.Form = new FormCollection(
                form.ToDictionary(kv => kv.Key, kv => new Microsoft.Extensions.Primitives.StringValues(kv.Value)));
        }

        var routeData = new RouteData();
        routeData.Values["controller"] = controller;
        routeData.Values["action"] = action;

        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
        return new ActionExecutingContext(
            actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());
    }

    private static ViewResult NewViewResult() => new()
    {
        ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
    };

    /// <summary>執行 Filter，並回傳 pipeline 內層產生的結果。</summary>
    private async Task<IActionResult?> RunAsync(ActionExecutingContext ctx, IActionResult? innerResult)
    {
        ActionExecutionDelegate next = () =>
        {
            var executed = new ActionExecutedContext(ctx, ctx.Filters, ctx.Controller) { Result = innerResult };
            return Task.FromResult(executed);
        };
        await _filter.OnActionExecutionAsync(ctx, next);
        return innerResult;
    }

    /// <summary>
    /// 瀏覽器實際送出的樣子：hidden 與 checkbox 同名，依 DOM 順序兩個值都送。
    /// 既有測試每次只塞一個值，剛好避開了這條路徑——而 _ProjectZSystemParams.cshtml 把 hidden
    /// 寫在 checkbox 之前，Filter 若取第一個值就永遠拿到 "false"，開關勾了也存不進去。
    /// </summary>
    private static ActionExecutingContext BuildBrowserPost(string field, params string[] values)
    {
        var ctx = BuildExecutingContext("SystemParam", "Save", "POST");
        ctx.HttpContext.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            [field] = new Microsoft.Extensions.Primitives.StringValues(values),
        });
        return ctx;
    }

    [Theory]
    [InlineData("InvSimplifiedMode")]
    [InlineData("MfgSimplifiedMode")]
    public async Task Save_CheckedSwitch_IsStored_EvenWhenHiddenFalseComesFirst(string field)
    {
        var ctx = BuildBrowserPost(field, "false", "true");   // hidden 在前（實際的 DOM 順序）
        await RunAsync(ctx, new RedirectToActionResult("Index", "SystemParam", null));

        var key = field == "InvSimplifiedMode"
            ? ProjectZSystemParamKeys.InvSimplifiedMode
            : ProjectZSystemParamKeys.MfgSimplifiedMode;
        _mockSysParam.Verify(s => s.SetAsync(key, "true"), Times.Once);
    }

    [Theory]
    [InlineData("InvSimplifiedMode")]
    [InlineData("MfgSimplifiedMode")]
    public async Task Save_UncheckedSwitch_StoresFalse(string field)
    {
        var ctx = BuildBrowserPost(field, "false");           // 未勾選：只有 hidden 會送出
        await RunAsync(ctx, new RedirectToActionResult("Index", "SystemParam", null));

        var key = field == "InvSimplifiedMode"
            ? ProjectZSystemParamKeys.InvSimplifiedMode
            : ProjectZSystemParamKeys.MfgSimplifiedMode;
        _mockSysParam.Verify(s => s.SetAsync(key, "false"), Times.Once);
    }

    private void SeedCurrency(string code, bool isActive = true) =>
        _context.Currencies.Add(new Currency { CurrencyCode = code, CurrencyName = code, IsActive = isActive });

    // ══════════════════════════════════════════════════════════════
    // GET Index：簡化模式讀出
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]   // 大小寫不敏感
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]    // 從未設定過（乾淨部署）→ 完整模式
    public async Task Index_ShouldProjectInvSimplifiedModeAsBool(string? stored, bool expected)
    {
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode)).ReturnsAsync(stored);
        var vr = NewViewResult();

        await RunAsync(BuildExecutingContext("SystemParam", "Index"), vr);

        Assert.Equal(expected, vr.ViewData["InvSimplifiedMode"]);
    }

    [Fact]
    public async Task Index_ShouldAlsoProjectCurrencyAndCostMethodDefaults()
    {
        SeedCurrency("USD");
        SeedCurrency("TWD");
        SeedCurrency("JPY", isActive: false);   // 停用幣別不得出現
        await _context.SaveChangesAsync();

        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.BaseCurrency)).ReturnsAsync("TWD");
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode)).ReturnsAsync("true");
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvDefaultCostMethod)).ReturnsAsync((string?)null);
        var vr = NewViewResult();

        await RunAsync(BuildExecutingContext("SystemParam", "Index"), vr);

        var codes = Assert.IsType<List<string>>(vr.ViewData["CurrencyCodes"]);
        Assert.Equal(new[] { "TWD", "USD" }, codes);
        Assert.Equal("TWD", vr.ViewData["BaseCurrency"]);
        Assert.Equal(true, vr.ViewData["InvSimplifiedMode"]);
        Assert.Equal("MA", vr.ViewData["InvDefaultCostMethod"]);   // 未設定 → 預設 MA
    }

    /// <summary>
    /// §25 製造簡化模式：與 INV 簡化模式各自獨立的開關，未設定（乾淨部署）必須是 false＝完整流程。
    /// </summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public async Task Index_ShouldProjectMfgSimplifiedModeAsBool(string? stored, bool expected)
    {
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgSimplifiedMode)).ReturnsAsync(stored);
        var vr = NewViewResult();

        await RunAsync(BuildExecutingContext("SystemParam", "Index"), vr);

        Assert.Equal(expected, vr.ViewData["MfgSimplifiedMode"]);
    }

    /// <summary>兩個簡化模式互不連動——開 INV 不會順帶開 MFG。</summary>
    [Fact]
    public async Task Index_InvAndMfgSimplifiedMode_ShouldBeIndependent()
    {
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode)).ReturnsAsync("true");
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgSimplifiedMode)).ReturnsAsync((string?)null);
        var vr = NewViewResult();

        await RunAsync(BuildExecutingContext("SystemParam", "Index"), vr);

        Assert.Equal(true, vr.ViewData["InvSimplifiedMode"]);
        Assert.Equal(false, vr.ViewData["MfgSimplifiedMode"]);
    }

    [Fact]
    public async Task OtherController_ShouldNotTouchSystemParams()
    {
        var vr = NewViewResult();

        await RunAsync(BuildExecutingContext("Product", "Index"), vr);

        Assert.Null(vr.ViewData["InvSimplifiedMode"]);
        _mockSysParam.Verify(s => s.GetAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Index_NonViewResult_ShouldNotThrowNorRead()
    {
        await RunAsync(BuildExecutingContext("SystemParam", "Index"), new RedirectToActionResult("Login", "Account", null));

        _mockSysParam.Verify(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode), Times.Never);
    }

    // ══════════════════════════════════════════════════════════════
    // POST Save：自動重排排程（2026-10-06）
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("MfgAutoScheduleOnChange")]
    [InlineData("MfgAutoScheduleBeforeDispatch")]
    public async Task Save_AutoScheduleSwitch_IsStored(string field)
    {
        var ctx = BuildBrowserPost(field, "false", "true");
        await RunAsync(ctx, new RedirectToActionResult("Index", "SystemParam", null));

        var key = field == "MfgAutoScheduleOnChange"
            ? ProjectZSystemParamKeys.MfgAutoScheduleOnChange
            : ProjectZSystemParamKeys.MfgAutoScheduleBeforeDispatch;
        _mockSysParam.Verify(s => s.SetAsync(key, "true"), Times.Once);
    }

    [Theory]
    [InlineData("16:00", "16:00")]
    [InlineData("7:30", "07:30")]
    [InlineData("", null)]
    [InlineData("abc", null)]
    public async Task Save_AutoScheduleDailyTime_IsNormalizedOrCleared(string posted, string? expected)
    {
        var ctx = BuildExecutingContext("SystemParam", "Save", "POST", new Dictionary<string, string>
        {
            ["MfgAutoScheduleDailyTime"] = posted
        });

        await RunAsync(ctx, new RedirectToActionResult("Index", "SystemParam", null));

        _mockSysParam.Verify(s => s.SetAsync(ProjectZSystemParamKeys.MfgAutoScheduleDailyTime, expected), Times.Once);
    }

    // 2026-10-14：自動重排規則／凍結時界——不合法或空白一律清除（null＝原行為：Priority、不凍結）
    [Theory]
    [InlineData("EDD", "EDD")]
    [InlineData("spt", "SPT")]
    [InlineData("", null)]
    [InlineData("Bogus", null)]
    public async Task Save_AutoScheduleRule_IsNormalizedOrCleared(string posted, string? expected)
    {
        var ctx = BuildExecutingContext("SystemParam", "Save", "POST", new Dictionary<string, string>
        {
            ["MfgAutoScheduleRule"] = posted
        });

        await RunAsync(ctx, new RedirectToActionResult("Index", "SystemParam", null));

        _mockSysParam.Verify(s => s.SetAsync(ProjectZSystemParamKeys.MfgAutoScheduleRule, expected), Times.Once);
    }

    [Theory]
    [InlineData("4", "4")]
    [InlineData("2.5", "2.5")]
    [InlineData("999", "168")]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("", null)]
    public async Task Save_AutoScheduleFreezeHours_IsNormalizedOrCleared(string posted, string? expected)
    {
        var ctx = BuildExecutingContext("SystemParam", "Save", "POST", new Dictionary<string, string>
        {
            ["MfgAutoScheduleFreezeHours"] = posted
        });

        await RunAsync(ctx, new RedirectToActionResult("Index", "SystemParam", null));

        _mockSysParam.Verify(s => s.SetAsync(ProjectZSystemParamKeys.MfgAutoScheduleFreezeHours, expected), Times.Once);
    }

    [Fact]
    public async Task Index_AutoScheduleRuleAndFreeze_NeverSet_ProjectEmpty()
    {
        var vr = NewViewResult();
        await RunAsync(BuildExecutingContext("SystemParam", "Index"), vr);

        Assert.Equal("", vr.ViewData["MfgAutoScheduleRule"]);
        Assert.Equal("", vr.ViewData["MfgAutoScheduleFreezeHours"]);
    }

    // ══════════════════════════════════════════════════════════════
    // POST Save：簡化模式存回
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("true", "true")]
    [InlineData("True", "true")]
    [InlineData("false", "false")]
    [InlineData("", "false")]
    public async Task Save_ShouldNormalizeInvSimplifiedMode(string posted, string expected)
    {
        var ctx = BuildExecutingContext("SystemParam", "Save", "POST", new Dictionary<string, string>
        {
            ["InvSimplifiedMode"] = posted
        });

        await RunAsync(ctx, new RedirectToActionResult("Index", "SystemParam", null));

        _mockSysParam.Verify(s => s.SetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, expected), Times.Once);
    }

    /// <summary>
    /// checkbox 未勾選時瀏覽器完全不送該欄位 → 必須明確存 "false"，
    /// 否則舊值殘留會讓使用者「取消勾選」無效。
    /// </summary>
    [Fact]
    public async Task Save_UncheckedCheckboxAbsentFromForm_ShouldStoreFalse()
    {
        var ctx = BuildExecutingContext("SystemParam", "Save", "POST", new Dictionary<string, string>
        {
            ["BaseCurrency"] = "TWD"
        });

        await RunAsync(ctx, new RedirectToActionResult("Index", "SystemParam", null));

        _mockSysParam.Verify(s => s.SetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, "false"), Times.Once);
    }

    /// <summary>Core 的 Save 驗證失敗（回 View 而非重導向）時不得寫入任何參數。</summary>
    [Fact]
    public async Task Save_CoreReturnedViewResult_ShouldNotPersistAnything()
    {
        var ctx = BuildExecutingContext("SystemParam", "Save", "POST", new Dictionary<string, string>
        {
            ["InvSimplifiedMode"] = "true"
        });

        await RunAsync(ctx, NewViewResult());

        _mockSysParam.Verify(s => s.SetAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Save_ShouldAlsoPersistBaseCurrencyAndCostMethodAndSrmWeights()
    {
        var ctx = BuildExecutingContext("SystemParam", "Save", "POST", new Dictionary<string, string>
        {
            ["BaseCurrency"]         = " TWD ",
            ["InvSimplifiedMode"]    = "true",
            ["InvDefaultCostMethod"] = "FIFO",
            ["SrmWeightQuality"]     = "40",
            ["SrmWeightDelivery"]    = "-5",     // 負值不存
            ["SrmWeightPrice"]       = "",       // 空值 → 清回預設
            ["SrmWeightService"]     = "abc",    // 非數字不存
        });

        await RunAsync(ctx, new RedirectToActionResult("Index", "SystemParam", null));

        _mockSysParam.Verify(s => s.SetAsync(ProjectZSystemParamKeys.BaseCurrency, "TWD"), Times.Once);
        _mockSysParam.Verify(s => s.SetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, "true"), Times.Once);
        _mockSysParam.Verify(s => s.SetAsync(ProjectZSystemParamKeys.InvDefaultCostMethod, "FIFO"), Times.Once);
        _mockSysParam.Verify(s => s.SetAsync(ProjectZSystemParamKeys.SrmWeightQuality, "40"), Times.Once);
        _mockSysParam.Verify(s => s.SetAsync(ProjectZSystemParamKeys.SrmWeightDelivery, It.IsAny<string?>()), Times.Never);
        _mockSysParam.Verify(s => s.SetAsync(ProjectZSystemParamKeys.SrmWeightPrice, null), Times.Once);
        _mockSysParam.Verify(s => s.SetAsync(ProjectZSystemParamKeys.SrmWeightService, It.IsAny<string?>()), Times.Never);
    }

    [Theory]
    [InlineData("true", "true")]
    [InlineData("True", "true")]
    [InlineData("false", "false")]
    [InlineData("", "false")]
    public async Task Save_ShouldNormalizeMfgSimplifiedMode(string posted, string expected)
    {
        var ctx = BuildExecutingContext("SystemParam", "Save", "POST", new Dictionary<string, string>
        {
            ["MfgSimplifiedMode"] = posted
        });

        await RunAsync(ctx, new RedirectToActionResult("Index", "SystemParam", null));

        _mockSysParam.Verify(s => s.SetAsync(ProjectZSystemParamKeys.MfgSimplifiedMode, expected), Times.Once);
    }

    /// <summary>MFG 開關取消勾選（欄位不出現）同樣必須明確存 false，否則舊值殘留。</summary>
    [Fact]
    public async Task Save_MfgUncheckedCheckboxAbsentFromForm_ShouldStoreFalse()
    {
        var ctx = BuildExecutingContext("SystemParam", "Save", "POST", new Dictionary<string, string>
        {
            ["InvSimplifiedMode"] = "true"
        });

        await RunAsync(ctx, new RedirectToActionResult("Index", "SystemParam", null));

        _mockSysParam.Verify(s => s.SetAsync(ProjectZSystemParamKeys.MfgSimplifiedMode, "false"), Times.Once);
    }

    [Fact]
    public async Task Save_EmptyCostMethod_ShouldFallBackToMa()
    {
        var ctx = BuildExecutingContext("SystemParam", "Save", "POST", new Dictionary<string, string>
        {
            ["InvDefaultCostMethod"] = "  "
        });

        await RunAsync(ctx, new RedirectToActionResult("Index", "SystemParam", null));

        _mockSysParam.Verify(s => s.SetAsync(ProjectZSystemParamKeys.InvDefaultCostMethod, "MA"), Times.Once);
    }
}
