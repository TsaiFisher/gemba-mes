using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Moq;
using SmartFactory.MES.Filters;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using SmartFactory.MES.Tests.Conventions;
using Xunit;

namespace SmartFactory.MES.Tests.BAS;

/// <summary>
/// 廠別維護面板的參數擴充 Filter（`plant-scoped-system-params.md` §3.5 方案丙）。
///
/// 這是廠別覆寫唯一的使用者入口，而它的失效方式全是「安靜的」：存不進去、存到別的廠、
/// 或是取消覆寫後那一列還留著。三種都不會有錯誤訊息，所以逐條釘住。
/// </summary>
public class PlantFormEnhancementFilterTests : TestBase
{
    private const string PlantA = "P001";
    private const string PlantB = "P002";

    private readonly Mock<IPlantParamService>  _plantParams  = new();
    private readonly Mock<ISystemParamService> _systemParams = new();
    private readonly Mock<IUserContext>        _userContext  = new();
    private readonly PlantFormEnhancementFilter _filter;

    public PlantFormEnhancementFilterTests()
    {
        _userContext.Setup(u => u.AllowedPlantIds).Returns(new List<string>());
        _filter = new PlantFormEnhancementFilter(
            _plantParams.Object, _systemParams.Object, _userContext.Object);
    }

    // ══════════════════════════════════════════════════════════════
    // 骨架
    // ══════════════════════════════════════════════════════════════

    private static ActionExecutingContext BuildContext(
        string controller, string action, string method = "GET",
        Dictionary<string, StringValues>? form = null,
        Dictionary<string, object?>? args = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        if (form != null) httpContext.Request.Form = new FormCollection(form);

        var routeData = new RouteData();
        routeData.Values["controller"] = controller;
        routeData.Values["action"] = action;

        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
        return new ActionExecutingContext(
            actionContext, new List<IFilterMetadata>(),
            args ?? new Dictionary<string, object?>(), controller: new object());
    }

    private static PartialViewResult NewPartial() => new()
    {
        ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
    };

    private async Task RunAsync(ActionExecutingContext ctx, IActionResult? inner)
    {
        ActionExecutionDelegate next = () => Task.FromResult(
            new ActionExecutedContext(ctx, ctx.Filters, ctx.Controller) { Result = inner });
        await _filter.OnActionExecutionAsync(ctx, next);
    }

    /// <summary>模擬瀏覽器送出：checkbox 勾選時同名欄位會有 hidden 的 "false" 與 checkbox 的 "true" 兩個值。</summary>
    private static StringValues Checked() => new(new[] { "true", "false" });
    private static StringValues Unchecked() => new("false");

    // ══════════════════════════════════════════════════════════════
    // GET：投影
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Edit_Get_ProjectsOverridesAndGlobalsForThatPlant()
    {
        _systemParams.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgFgWarehouse)).ReturnsAsync("FG01");
        _plantParams.Setup(s => s.GetOverridesAsync(PlantA, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new Dictionary<string, string?> { [ProjectZSystemParamKeys.MfgFgWarehouse] = "FG02" });

        var ctx = BuildContext("Plant", "Edit", args: new Dictionary<string, object?> { ["id"] = PlantA });
        var view = NewPartial();
        await RunAsync(ctx, view);

        Assert.Equal(PlantA, view.ViewData[PlantFormEnhancementFilter.PlantIdKey]);
        var globals = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(
            view.ViewData[PlantFormEnhancementFilter.GlobalsKey]);
        var overrides = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(
            view.ViewData[PlantFormEnhancementFilter.OverridesKey]);

        Assert.Equal("FG01", globals[ProjectZSystemParamKeys.MfgFgWarehouse]);
        Assert.Equal("FG02", overrides[ProjectZSystemParamKeys.MfgFgWarehouse]);
        // 目錄裡每一個參數都要有全域值欄位，否則畫面會少渲染一格而沒人發現
        Assert.All(PlantParamCatalog.All, d => Assert.True(globals.ContainsKey(d.Key)));
    }

    [Fact]
    public async Task Create_Get_HasNoOverrides_ButStillShowsGlobals()
    {
        var ctx = BuildContext("Plant", "Create");
        var view = NewPartial();
        await RunAsync(ctx, view);

        Assert.Equal("", view.ViewData[PlantFormEnhancementFilter.PlantIdKey]);
        var overrides = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(
            view.ViewData[PlantFormEnhancementFilter.OverridesKey]);
        Assert.Empty(overrides);
        _plantParams.Verify(s => s.GetOverridesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OtherControllers_AreUntouched()
    {
        var ctx = BuildContext("Product", "Edit", args: new Dictionary<string, object?> { ["id"] = PlantA });
        var view = NewPartial();
        await RunAsync(ctx, view);

        Assert.Null(view.ViewData[PlantFormEnhancementFilter.GlobalsKey]);
        _systemParams.Verify(s => s.GetAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Index_IsNotAFormAction_AndIsSkipped()
    {
        var ctx = BuildContext("Plant", "Index");
        var view = NewPartial();
        await RunAsync(ctx, view);

        Assert.Null(view.ViewData[PlantFormEnhancementFilter.GlobalsKey]);
    }

    // ══════════════════════════════════════════════════════════════
    // POST：持久化
    // ══════════════════════════════════════════════════════════════

    // 面板實際送出的樣子：Id + 存在標記。少了標記代表「這張表單沒有參數區塊」，Filter 會整段跳過。
    private Dictionary<string, StringValues> FormFor(string plantId) =>
        new()
        {
            ["Id"] = plantId,
            [PlantFormEnhancementFilter.PresentField] = new StringValues("1"),
        };

    private async Task PostAsync(Dictionary<string, StringValues> form, IActionResult? inner = null) =>
        await RunAsync(BuildContext("Plant", "Edit", "POST", form),
            inner ?? new JsonResult(new { success = true, message = "ok" }));

    /// <summary>
    /// 本檔最重要的一條。瀏覽器送出的是 ["true","false"] 兩個值，取第一個或最後一個都可能踩雷；
    /// Filter 必須與先後無關（`SystemParamEnhancementFilter` 曾因取第一個而讓開關永遠存成 false）。
    /// </summary>
    [Fact]
    public async Task Save_Checked_PersistsOverride_RegardlessOfHiddenFieldOrder()
    {
        foreach (var order in new[] { new[] { "true", "false" }, new[] { "false", "true" } })
        {
            _plantParams.Invocations.Clear();
            var form = FormFor(PlantA);
            form["pp_ovr_" + ProjectZSystemParamKeys.InvSimplifiedMode] = new StringValues(order);
            form["pp_val_" + ProjectZSystemParamKeys.InvSimplifiedMode] = new StringValues(order);

            await PostAsync(form);

            _plantParams.Verify(s => s.SetAsync(
                ProjectZSystemParamKeys.InvSimplifiedMode, PlantA, "true", It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task Save_Unchecked_ClearsOverride_SoItFallsBackToGlobal()
    {
        var form = FormFor(PlantA);
        form["pp_ovr_" + ProjectZSystemParamKeys.MfgFgWarehouse] = Unchecked();
        form["pp_val_" + ProjectZSystemParamKeys.MfgFgWarehouse] = new StringValues("FG09");

        await PostAsync(form);

        _plantParams.Verify(s => s.ClearAsync(
            ProjectZSystemParamKeys.MfgFgWarehouse, PlantA, It.IsAny<CancellationToken>()), Times.Once);
        _plantParams.Verify(s => s.SetAsync(
            ProjectZSystemParamKeys.MfgFgWarehouse, PlantA, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Save_MissingCheckbox_IsTreatedAsInherit()
    {
        // 表單根本沒送這個欄位（例如區塊被摺疊且瀏覽器不送 disabled 欄位）→ 一律當繼承，不可猜成覆寫
        await PostAsync(FormFor(PlantA));

        foreach (var def in PlantParamCatalog.All)
            _plantParams.Verify(s => s.ClearAsync(def.Key, PlantA, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("500000", "500000")]
    [InlineData("0", "0")]
    [InlineData("-1", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public async Task Save_Number_OnlyAcceptsNonNegative(string posted, string? expected)
    {
        var form = FormFor(PlantA);
        form["pp_ovr_" + ProjectZSystemParamKeys.MfgWoApprovalThreshold] = Checked();
        form["pp_val_" + ProjectZSystemParamKeys.MfgWoApprovalThreshold] = new StringValues(posted);

        await PostAsync(form);

        _plantParams.Verify(s => s.SetAsync(
            ProjectZSystemParamKeys.MfgWoApprovalThreshold, PlantA, expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("Block", "Block")]
    [InlineData("block", "Block")]   // 大小寫正規化回清單內的寫法，否則讀取端字串比對會落空
    [InlineData("Nonsense", null)]   // 竄改表單塞進來的值不收
    public async Task Save_Choice_OnlyAcceptsListedOptions(string posted, string? expected)
    {
        var form = FormFor(PlantA);
        form["pp_ovr_" + ProjectZSystemParamKeys.MfgMaterialGate] = Checked();
        form["pp_val_" + ProjectZSystemParamKeys.MfgMaterialGate] = new StringValues(posted);

        await PostAsync(form);

        _plantParams.Verify(s => s.SetAsync(
            ProjectZSystemParamKeys.MfgMaterialGate, PlantA, expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Save_Text_TrimsAndTreatsBlankAsExplicitEmpty()
    {
        var form = FormFor(PlantA);
        form["pp_ovr_" + ProjectZSystemParamKeys.MfgFgWarehouse] = Checked();
        form["pp_val_" + ProjectZSystemParamKeys.MfgFgWarehouse] = new StringValues("  FG02  ");
        form["pp_ovr_" + ProjectZSystemParamKeys.MfgFgLocation]  = Checked();
        form["pp_val_" + ProjectZSystemParamKeys.MfgFgLocation]  = new StringValues("   ");

        await PostAsync(form);

        _plantParams.Verify(s => s.SetAsync(
            ProjectZSystemParamKeys.MfgFgWarehouse, PlantA, "FG02", It.IsAny<CancellationToken>()), Times.Once);
        // 空字串＝「本廠明確不設」，與「繼承全域」不同——存 null 但那一列仍在
        _plantParams.Verify(s => s.SetAsync(
            ProjectZSystemParamKeys.MfgFgLocation, PlantA, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Save_WritesToThePlantOnTheForm_NotTheUsersActivePlant()
    {
        _userContext.Setup(u => u.PlantId).Returns(PlantB);

        var form = FormFor(PlantA);
        form["pp_ovr_" + ProjectZSystemParamKeys.MfgLotPolicy] = Checked();
        form["pp_val_" + ProjectZSystemParamKeys.MfgLotPolicy] = new StringValues("MinMultiple");

        await PostAsync(form);

        _plantParams.Verify(s => s.SetAsync(
            ProjectZSystemParamKeys.MfgLotPolicy, PlantA, "MinMultiple", It.IsAny<CancellationToken>()), Times.Once);
        _plantParams.Verify(s => s.SetAsync(
            It.IsAny<string>(), PlantB, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Save_RejectsPlantsTheUserHasNoRightTo()
    {
        _userContext.Setup(u => u.AllowedPlantIds).Returns(new List<string> { PlantB });

        var form = FormFor(PlantA);
        form["pp_ovr_" + ProjectZSystemParamKeys.MfgLotPolicy] = Checked();
        form["pp_val_" + ProjectZSystemParamKeys.MfgLotPolicy] = new StringValues("MinMultiple");

        await PostAsync(form);

        _plantParams.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Save_DoesNothingWhenCoreRejectedThePlantItself()
    {
        var form = FormFor(PlantA);
        form["pp_ovr_" + ProjectZSystemParamKeys.MfgLotPolicy] = Checked();
        form["pp_val_" + ProjectZSystemParamKeys.MfgLotPolicy] = new StringValues("MinMultiple");

        // 驗證失敗時 Core 回傳表單 View（不是 Json success），廠別根本沒存起來
        await PostAsync(form, NewPartial());

        _plantParams.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Save_AcceptsNonAjaxRedirectAsSuccess()
    {
        var form = FormFor(PlantA);
        form["pp_ovr_" + ProjectZSystemParamKeys.MfgLotPolicy] = Checked();
        form["pp_val_" + ProjectZSystemParamKeys.MfgLotPolicy] = new StringValues("LotForLot");

        await PostAsync(form, new RedirectToActionResult("Index", "Plant", null));

        _plantParams.Verify(s => s.SetAsync(
            ProjectZSystemParamKeys.MfgLotPolicy, PlantA, "LotForLot", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Save_WithoutPlantId_WritesNothing()
    {
        var form = new Dictionary<string, StringValues>
        {
            [PlantFormEnhancementFilter.PresentField] = new StringValues("1"),
            ["pp_ovr_" + ProjectZSystemParamKeys.MfgLotPolicy] = Checked(),
            ["pp_val_" + ProjectZSystemParamKeys.MfgLotPolicy] = new StringValues("LotForLot"),
        };

        await PostAsync(form);

        _plantParams.VerifyNoOtherCalls();
    }

    /// <summary>
    /// 沒有參數區塊的 POST 不得動到任何覆寫。
    ///
    /// <para>「沒有勾選」與「沒有這個區塊」在 <c>Request.Form</c> 裡長得一模一樣——
    /// 少了存在標記，任何只帶 Id/Name 的 POST（腳本、外部整合、未來新增的精簡表單）
    /// 都會把該廠所有覆寫**安靜地**清光。2026-08-18 用 curl 補手冊截圖資料時實際踩到：
    /// 兩個廠剛設好的覆寫，被下一個只改廠名的請求全部清掉，畫面上毫無徵兆。</para>
    /// </summary>
    [Fact]
    public async Task Save_WithoutParamSection_LeavesOverridesUntouched()
    {
        var form = new Dictionary<string, StringValues>
        {
            ["Id"]   = PlantA,
            ["Name"] = new StringValues("只改廠名"),
        };

        await PostAsync(form);

        _plantParams.VerifyNoOtherCalls();
    }

    // ══════════════════════════════════════════════════════════════
    // 目錄本身
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 目錄與規格 §2 的裁決必須一致。特別是 `inv_default_cost_method`（§7.1 裁決不納入）
    /// 與 §2 ④ 的 16 個——它們一旦被順手加進來，畫面就會提供一個資料上不自洽的開關。
    ///
    /// <para>`mfg_default_issue_method` 同樣不得在內（§7 #8 裁決＝先自目錄移除）：它**全庫沒有
    /// 任何讀取端**，放進目錄只會在面板上多一個「設了不會有任何效果、也不會有提示」的欄位。
    /// 等 `mfg-domain.md` §25.6 的三層 fallback 真的接上，再連同這條斷言一起改。</para>
    /// </summary>
    [Fact]
    public void Catalog_MatchesTheSpecDecision()
    {
        // 2026-09-29 +1：mfg_machine_status_gate（開工機台狀態閘口），見規格 §8 變更紀錄
        // 2026-10-06 +3：自動重排排程三種時機（異動後／派工前／每日時刻），各廠可能各自決定，故納入
        // 2026-10-12 +5：超量生產（容許率／超過時處理）與委外超收（容許率／超過時處理／超收部分加工費），各廠規則不同
        // 2026-10-12 +3：委外簡化（收貨自動扣送外料／收料當場驗收／投產自動建委外採購單），小廠與大廠做法不同
        // 2026-10-14 +2：自動重排規則、凍結時界（各廠排序慣例與備料提前量不同）
        // 2026-10-20 +3：工單強制送審條件（委外／重工／延期，mfg-domain §18.2 其餘三條觸發），各廠稽核政策不同
        Assert.Equal(28, PlantParamCatalog.All.Count);
        Assert.Single(PlantParamCatalog.ForModule(PlantParamCatalog.ModuleInv));
        Assert.Equal(27, PlantParamCatalog.ForModule(PlantParamCatalog.ModuleMfg).Count());

        foreach (var excluded in new[]
        {
            ProjectZSystemParamKeys.MfgDefaultIssueMethod,
            ProjectZSystemParamKeys.InvDefaultCostMethod,
            ProjectZSystemParamKeys.BaseCurrency,
            ProjectZSystemParamKeys.AuditQtyTolerance,
            ProjectZSystemParamKeys.AuditAmountTolerance,
            ProjectZSystemParamKeys.FinSettleTolerance,
            ProjectZSystemParamKeys.SrmWeightQuality,
            ProjectZSystemParamKeys.SrmWeightDelivery,
            ProjectZSystemParamKeys.SrmWeightPrice,
            ProjectZSystemParamKeys.SrmWeightService,
        })
            Assert.DoesNotContain(PlantParamCatalog.All, d => d.Key == excluded);
    }

    [Fact]
    public void Catalog_FormFieldNamesAndResourceKeysAreUnique()
    {
        Assert.Equal(PlantParamCatalog.All.Count, PlantParamCatalog.All.Select(d => d.OverrideField).Distinct().Count());
        Assert.Equal(PlantParamCatalog.All.Count, PlantParamCatalog.All.Select(d => d.ValueField).Distinct().Count());
        Assert.Equal(PlantParamCatalog.All.Count, PlantParamCatalog.All.Select(d => d.ResourceKey).Distinct().Count());

        // 值欄位不可與覆寫欄位撞名，否則兩個核取方塊會混在同一個 StringValues 裡
        Assert.Empty(PlantParamCatalog.All.Select(d => d.ValueField)
            .Intersect(PlantParamCatalog.All.Select(d => d.OverrideField)));
    }

    [Fact]
    public void Catalog_ChoiceParamsHaveOptions_AndOthersDoNot()
    {
        foreach (var d in PlantParamCatalog.All)
        {
            if (d.Kind == PlantParamKind.Choice)
                Assert.True(d.Choices is { Length: > 1 }, $"{d.Key} 是 Choice 卻沒有選項");
            else
                Assert.Null(d.Choices);
        }
    }

    [Fact]
    public void Catalog_ResourceKeysAreCoveredByBothResxFiles()
    {
        foreach (var lang in new[] { "zh-TW", "en-US" })
        {
            var path = Path.Combine(ConventionScan.RepoRoot(), "Resources", "Views", "Plant", $"_PlantParamField.{lang}.resx");
            var xml  = File.ReadAllText(path);

            foreach (var d in PlantParamCatalog.All)
                Assert.Contains($"name=\"{d.ResourceKey}\"", xml);

            foreach (var c in PlantParamCatalog.All.SelectMany(d => d.Choices ?? []))
                Assert.Contains($"name=\"Choice{c}\"", xml);
        }
    }
}
