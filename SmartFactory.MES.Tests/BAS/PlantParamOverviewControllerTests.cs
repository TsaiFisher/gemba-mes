using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Controllers;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.ViewModels;
using SmartFactory.MES.Services;
using SmartFactory.MES.Tests.Conventions;
using Xunit;

namespace SmartFactory.MES.Tests.BAS;

/// <summary>
/// 各廠參數覆寫一覽（`plant-scoped-system-params.md` §7 待決 #7）。
///
/// <para>這份報表把「參數 × 廠別」轉置出來，兩件事最容易錯而且錯了不會有徵兆：
/// ① 覆寫成空值被畫成「繼承」——那會讓刻意設定看起來像忘了設；
/// ② 跨廠讀取沒有收斂——<see cref="PlantParam"/> 刻意不是 IPlantEntity，
///    沒有全域查詢過濾接手，收斂全靠 Controller 自己。</para>
/// </summary>
public class PlantParamOverviewControllerTests : TestBase
{
    private readonly Mock<IPermissionService> _permissions = new();
    private readonly Mock<IUserContext>       _userContext = new();
    private readonly Mock<ISystemParamService> _systemParams = new();

    public PlantParamOverviewControllerTests()
    {
        _permissions.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _permissions.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);
        _userContext.Setup(u => u.AllowedPlantIds).Returns(new List<string>());
        _systemParams.Setup(s => s.GetAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        _context.Plants.AddRange(
            new Plant { Id = "P1", Name = "一廠" },
            new Plant { Id = "P2", Name = "二廠" });
        _context.SaveChanges();
    }

    private PlantParamOverviewController Build() =>
        new(_context, _userContext.Object, _permissions.Object, _systemParams.Object,
            Mock.Of<IExportService>(), Mock.Of<IDateTimeService>(),
            Mock.Of<IStringLocalizer<ProjectZSharedResource>>());

    private async Task<PlantParamOverviewVm> IndexVmAsync()
    {
        var result = await Build().Index();
        var view = Assert.IsType<ViewResult>(result);
        return Assert.IsType<PlantParamOverviewVm>(view.Model);
    }

    private static PlantParamOverviewCell CellOf(PlantParamOverviewVm vm, string key, string plantId) =>
        vm.Rows.Single(r => r.Def.Key == key).Cells.Single(c => c.PlantId == plantId);

    [Fact]
    public async Task Index_ListsEveryCatalogParamOnceForEveryPlant()
    {
        var vm = await IndexVmAsync();

        Assert.Equal(PlantParamCatalog.All.Count, vm.Rows.Count);
        Assert.Equal(PlantParamCatalog.All.Select(d => d.Key), vm.Rows.Select(r => r.Def.Key));
        Assert.All(vm.Rows, r => Assert.Equal(vm.Plants.Count, r.Cells.Count));
        // 儲存格順序必須與欄位順序一致，否則整張矩陣會錯位而看起來完全正常
        Assert.All(vm.Rows, r => Assert.Equal(vm.Plants.Select(p => p.Id), r.Cells.Select(c => c.PlantId)));
    }

    [Fact]
    public async Task Index_OverriddenPlantShowsItsOwnValue_OthersInherit()
    {
        const string Key = ProjectZSystemParamKeys.MfgLotPolicy;
        _systemParams.Setup(s => s.GetAsync(Key)).ReturnsAsync("LotForLot");
        _context.PlantParams.Add(new PlantParam { Key = Key, PlantId = "P1", Value = "MinMultiple" });
        await _context.SaveChangesAsync();

        var vm = await IndexVmAsync();

        var p1 = CellOf(vm, Key, "P1");
        Assert.Equal(PlantParamOverviewCell.Overridden, p1.State);
        Assert.Equal("MinMultiple", p1.DisplayValue);

        var p2 = CellOf(vm, Key, "P2");
        Assert.Equal(PlantParamOverviewCell.Inherited, p2.State);
        Assert.Equal("LotForLot", p2.DisplayValue);   // 繼承欄顯示的是全域值，不是空白
    }

    /// <summary>
    /// 「覆寫成空值」與「繼承」不是同一件事。前者是有人刻意把它設成空的，
    /// 畫成「繼承」會讓那個決定消失，而且全域值改動時讀者會以為該廠也跟著變。
    /// </summary>
    [Fact]
    public async Task Index_OverriddenToEmpty_IsStillAnOverride_NotInherited()
    {
        const string Key = ProjectZSystemParamKeys.MfgFgWarehouse;
        _systemParams.Setup(s => s.GetAsync(Key)).ReturnsAsync("WH-FG");
        _context.PlantParams.Add(new PlantParam { Key = Key, PlantId = "P1", Value = null });
        await _context.SaveChangesAsync();

        var cell = CellOf(await IndexVmAsync(), Key, "P1");

        Assert.Equal(PlantParamOverviewCell.Overridden, cell.State);
        Assert.Null(cell.DisplayValue);
    }

    [Fact]
    public async Task Index_OnlyShowsPlantsTheRoleIsAssignedTo()
    {
        _userContext.Setup(u => u.AllowedPlantIds).Returns(new List<string> { "P2" });

        var vm = await IndexVmAsync();

        Assert.Equal(new[] { "P2" }, vm.Plants.Select(p => p.Id));
        Assert.All(vm.Rows, r => Assert.Single(r.Cells));
    }

    /// <summary>空清單＝角色沒有廠別指派限制（例如掛根 OU 的管理角色），此時看得到全部廠別。</summary>
    [Fact]
    public async Task Index_NoPlantAssignment_ShowsEveryPlant()
    {
        var vm = await IndexVmAsync();
        Assert.Equal(new[] { "P1", "P2" }, vm.Plants.Select(p => p.Id));
    }

    /// <summary>別廠的覆寫不得漏進來——PlantParam 沒有全域查詢過濾可以接手。</summary>
    [Fact]
    public async Task Index_DoesNotLeakOverridesOfPlantsOutsideScope()
    {
        const string Key = ProjectZSystemParamKeys.MfgMaterialGate;
        _context.PlantParams.Add(new PlantParam { Key = Key, PlantId = "P1", Value = "Block" });
        await _context.SaveChangesAsync();
        _userContext.Setup(u => u.AllowedPlantIds).Returns(new List<string> { "P2" });

        var vm = await IndexVmAsync();

        Assert.DoesNotContain(vm.Rows.SelectMany(r => r.Cells),
            c => c.State == PlantParamOverviewCell.Overridden);
    }

    [Fact]
    public async Task Index_WithoutReadPermission_IsForbidden()
    {
        _permissions.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(false);
        Assert.IsType<ForbidResult>(await Build().Index());
    }

    [Fact]
    public async Task Export_WithoutPrintPermission_IsForbidden()
    {
        _permissions.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(false);
        Assert.IsType<ForbidResult>(await Build().Export());
    }

    /// <summary>
    /// 報表的參數名稱是自己一份 resx（各 View 自帶標籤是本專案的慣例）。
    /// 目錄新增參數卻忘了補名稱時，畫面會顯示英文鍵名而不會有任何錯誤。
    /// </summary>
    [Theory]
    [InlineData("zh-TW")]
    [InlineData("en-US")]
    public void Resx_CoversEveryCatalogParamName(string culture)
    {
        var path = Path.Combine(ConventionScan.RepoRoot(),
            "Resources", "Modules", "BAS", "Views", "PlantParamOverview", $"Index.{culture}.resx");
        var text = File.ReadAllText(path);

        var missing = PlantParamCatalog.All
            .Select(d => d.ResourceKey)
            .Where(k => !text.Contains($"name=\"{k}\""))
            .ToList();

        Assert.True(missing.Count == 0, $"{culture} 缺少參數名稱：{string.Join(", ", missing)}");
    }
}
