using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 完工入庫面板的預設成品倉解析（`plant-scoped-system-params.md` §2 ①／§5.1）。
///
/// `mfg_fg_warehouse` 是**全系統單一值**，存的卻是倉庫代碼；而 `Warehouse` 主鍵為 `WhCode`
/// 單欄（全域唯一）且屬 IPlantEntity——一個代碼只屬於一個廠。沒有廠別收斂時，在別的廠會把一個
/// 下拉選單裡根本不存在的代碼設成預選值，表單於是靜默失去預設（使用者看到的是「沒選」，
/// 而不是「選錯」，所以這種缺陷不會有人回報）。
/// </summary>
public class ProductionCompletionDefaultWarehouseTests : TestBase
{
    private const string PlantA = "P001";
    private const string PlantB = "P002";
    private const string WoId   = "WO2026001";

    private readonly Mock<IPlantParamService> _sysParam = new();
    private readonly Mock<IUserContext>        _user     = new();

    public ProductionCompletionDefaultWarehouseTests()
    {
        _sysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgFgWarehouse, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("FG01");
        _sysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgFgLocation, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("FG01-01");

        // A 廠有 FG01，B 廠沒有（代碼全域唯一，所以「B 廠也叫 FG01」是不可能存在的狀態）
        _context.Set<Warehouse>().Add(new Warehouse
            { WhCode = "FG01", WhName = "成品倉", PlantId = PlantA, IsActive = true });
        _context.Set<StorageLocation>().Add(new StorageLocation
            { WhCode = "FG01", LocationCode = "FG01-01", LocationName = "成品位", IsActive = true });
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = WoId, ProductCode = "P-001", Quantity = 10m,
            PlantId = PlantA, Status = WorkOrderStatus.Completed,
        });
        _context.SaveChanges();
    }

    private ProductionCompletionController BuildController(string plantId)
    {
        _context.CurrentPlantId = plantId;
        _user.Setup(u => u.PlantId).Returns(plantId);
        _user.Setup(u => u.UserId).Returns("usr1");

        return new ProductionCompletionController(
            GetMockLocalizer<ProjectZSharedResource>().Object,
            new Mock<IProductionCompletionService>().Object,
            _sysParam.Object,
            new Mock<ISamplingPlanService>().Object,
            new Mock<IUomService>().Object,
            new Mock<ICurrencyService>().Object,
            new PlantDefaultLocationResolver(_context))
        {
            ControllerContext = GetControllerContextWithServices(userContext: _user.Object),
        };
    }

    [Fact]
    public async Task ConfirmForm_UsesSystemParamDefault_InThePlantThatOwnsIt()
    {
        var result = Assert.IsType<PartialViewResult>(await BuildController(PlantA).ConfirmForm(WoId));

        Assert.Equal("FG01", result.ViewData["DefaultWh"]);
        Assert.Equal("FG01-01", result.ViewData["DefaultLoc"]);
    }

    /// <summary>
    /// 本案的核心：同一個全域參數值，在沒有這個代碼的廠必須是空的——
    /// 而不是預選一個該廠下拉選單裡不存在的倉庫。
    /// </summary>
    [Fact]
    public async Task ConfirmForm_DropsDefault_InAPlantThatDoesNotOwnTheCode()
    {
        // 取值本身要繞過廠別過濾：過濾是 `PlantId == CurrentPlantId`，
        // 測試裡 CurrentPlantId 尚未設定（null），直接查會一筆都撈不到。
        _context.WorkOrders.IgnoreQueryFilters().Single(w => w.WorkOrderId == WoId).PlantId = PlantB;
        await _context.SaveChangesAsync();

        var result = Assert.IsType<PartialViewResult>(await BuildController(PlantB).ConfirmForm(WoId));

        Assert.Equal("", result.ViewData["DefaultWh"]);
        Assert.Equal("", result.ViewData["DefaultLoc"]);
    }

    /// <summary>
    /// Controller 必須拿**目前登入廠別**去問參數。問錯廠不會有任何錯誤訊息，
    /// 只會安靜地拿到別廠的預設值——所以這條要用 Verify 釘住，不能只看畫面結果。
    /// </summary>
    [Fact]
    public async Task ConfirmForm_AsksParamsForTheCurrentPlant()
    {
        await BuildController(PlantA).ConfirmForm(WoId);

        _sysParam.Verify(s => s.GetAsync(
            ProjectZSystemParamKeys.MfgFgWarehouse, PlantA, It.IsAny<CancellationToken>()), Times.Once);
        _sysParam.Verify(s => s.GetAsync(
            ProjectZSystemParamKeys.MfgFgLocation, PlantA, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── 產品類別預設倉優先於系統參數（W2-F6）────────────────────────────────

    /// <summary>
    /// 工單產出是半成品時，預設倉必須是該產品類別的預設倉，不是全廠單一的成品倉。
    ///
    /// 照系統參數的預設收進成品倉**不會有任何錯誤**：入庫成功、單據結案。
    /// 錯誤要等到下一張母件工單後沖時才爆——後沖取的是**產品類別預設儲位**，
    /// 成品倉裡沒有這批半成品，於是判「可用量不足」；而後沖與完工入庫包在同一個交易裡，
    /// 整張母件工單從此再也入不了庫，訊息卻指向完全不相干的地方（人工驗收 W2-F6）。
    /// 兩邊同源才不會各指一處。
    /// </summary>
    [Fact]
    public async Task ConfirmForm_PrefersProductCategoryDefault_OverSystemParam()
    {
        SeedWipCategoryAndProduct();

        var result = Assert.IsType<PartialViewResult>(await BuildController(PlantA).ConfirmForm(WoId));

        Assert.Equal("WIP01", result.ViewData["DefaultWh"]);
        Assert.Equal("WIP01-01", result.ViewData["DefaultLoc"]);
    }

    /// <summary>產品類別沒設（或只設了倉庫沒設儲位）時退回系統參數——儲位是出庫驗證的實際單位。</summary>
    [Fact]
    public async Task ConfirmForm_FallsBackToSystemParam_WhenCategoryDefaultLacksLocation()
    {
        SeedWipCategoryAndProduct(locationCode: null);

        var result = Assert.IsType<PartialViewResult>(await BuildController(PlantA).ConfirmForm(WoId));

        Assert.Equal("FG01", result.ViewData["DefaultWh"]);
        Assert.Equal("FG01-01", result.ViewData["DefaultLoc"]);
    }

    /// <summary>產品類別的預設倉屬於別廠時同樣不可採用，退回系統參數。</summary>
    [Fact]
    public async Task ConfirmForm_IgnoresCategoryDefault_FromAnotherPlant()
    {
        SeedWipCategoryAndProduct(plantId: PlantB);

        var result = Assert.IsType<PartialViewResult>(await BuildController(PlantA).ConfirmForm(WoId));

        Assert.Equal("FG01", result.ViewData["DefaultWh"]);
        Assert.Equal("FG01-01", result.ViewData["DefaultLoc"]);
    }

    private void SeedWipCategoryAndProduct(string? locationCode = "WIP01-01", string plantId = PlantA)
    {
        _context.Set<Warehouse>().Add(new Warehouse
            { WhCode = "WIP01", WhName = "半成品倉", PlantId = plantId, IsActive = true });
        _context.Set<StorageLocation>().Add(new StorageLocation
            { WhCode = "WIP01", LocationCode = "WIP01-01", LocationName = "半成品位", IsActive = true });
        _context.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "WIP", CategoryName = "半成品", IsActive = true,
            DefaultWarehouseCode = "WIP01", DefaultLocationCode = locationCode,
        });
        _context.Products.Add(new Product
        {
            ProductCode = "P-001", ProductName = "半成品 A", CategoryCode = "WIP",
            StockUom = "PCS", PurchaseUom = "PCS", SalesUom = "PCS", IsActive = true,
        });
        _context.SaveChanges();
    }
}
