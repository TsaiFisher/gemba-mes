using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// 存貨價值分析（InventoryValue）的顯示模式與分頁單位。
///
/// <para>這支畫面原本沒有分頁，原因不是漏做：版面是「每個分類一張表、表內列出全部品號」，
/// 沒有任何一個單一集合可以切。2026-09-21 裁決改成由 <c>showDetail</c> 決定列單位——
/// 關＝一列一個分類（分頁切分類），開＝一列一個品號（分頁切明細列）。</para>
///
/// <para>這裡驗的是**彙總的正確性**，不是分頁本身：分頁切片由 <c>ReportPaging</c> 負責
/// 且已有自己的測試，而「分類小計／佔比是不是拿當頁算的」才是這次改動真正的風險
/// ——那種錯會隨翻頁變動，畫面上看起來完全正常。</para>
/// </summary>
public class InventoryValueControllerTests : TestBase
{
    private readonly Mock<IUserContext>       _user = new();
    private readonly Mock<IPermissionService> _perm = new();
    private readonly Mock<IDateTimeService>   _dt   = new();

    private const string PlantId = "P001";

    public InventoryValueControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _user.Setup(u => u.PlantId).Returns(PlantId);
        _user.Setup(u => u.UserId).Returns("user1");
        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);
        _dt.Setup(d => d.Now).Returns(new DateTime(2026, 9, 21, 9, 0, 0));
    }

    private InventoryValueController New(Mock<IExportService>? export = null)
        => new(_context, _user.Object, _perm.Object,
               (export ?? GetMockExportService()).Object, _dt.Object,
               GetMockLocalizer<ProjectZSharedResource>().Object)
           { ControllerContext = GetControllerContextWithServices() };

    private void AddProduct(string code, string name, string? category)
    {
        _context.Products.Add(new Product
        {
            ProductCode = code, ProductName = name,
            CategoryCode = category, StockUom = "PC", IsActive = true,
        });
        _context.SaveChanges();
    }

    private void AddInventory(string product, decimal onHand, decimal unitCost, string loc = "L1")
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = product, LocationCode = loc,
            LotNo = "", QtyOnHand = onHand, UnitCost = unitCost,
        });
        _context.SaveChanges();
    }

    /// <summary>甲類 100 + 300 = 400、乙類 100，總計 500。</summary>
    private void SeedTwoCategories()
    {
        AddProduct("A1", "甲一", "CAT-A");
        AddProduct("A2", "甲二", "CAT-A");
        AddProduct("B1", "乙一", "CAT-B");
        AddInventory("A1", onHand: 10m, unitCost: 10m);   // 100
        AddInventory("A2", onHand: 30m, unitCost: 10m);   // 300
        AddInventory("B1", onHand:  5m, unitCost: 20m);   // 100
    }

    private static List<InventoryValueCategoryVm> CatRowsOf(IActionResult r)
        => (List<InventoryValueCategoryVm>)Assert.IsType<ViewResult>(r).ViewData["CategoryRows"]!;

    [Fact]
    public async Task 彙總模式_一列一分類_金額與佔比以完整結果集為分母()
    {
        SeedTwoCategories();

        var result = await New().Index(categoryCode: null, showDetail: false, searched: true);

        var cats = CatRowsOf(result);
        Assert.Equal(2, cats.Count);
        // 依金額由大到小：甲類在前
        Assert.Equal("CAT-A", cats[0].CategoryCode);
        Assert.Equal(400m, cats[0].TotalValue);
        Assert.Equal(2,    cats[0].ItemCount);
        Assert.Equal(80m,  cats[0].ValuePct);
        Assert.Equal("CAT-B", cats[1].CategoryCode);
        Assert.Equal(100m, cats[1].TotalValue);
        Assert.Equal(20m,  cats[1].ValuePct);
        // 佔比合計 100%：分類佔比與明細佔比同一個分母，這是兩種模式能互相對照的前提
        Assert.Equal(100m, cats.Sum(c => c.ValuePct));
    }

    [Fact]
    public async Task 明細模式_Model仍是明細列_分類彙總同時可用於小計()
    {
        SeedTwoCategories();

        var result = await New().Index(categoryCode: null, showDetail: true, searched: true);

        var rows = Assert.IsType<List<InventoryValueRowVm>>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(3, rows.Count);
        // 明細模式仍要有分類彙總——畫面用它印分類標題上的小計，
        // 且該小計取自完整結果集，不是當頁切片。
        var cats = CatRowsOf(result);
        Assert.Equal(400m, cats.Single(c => c.CategoryCode == "CAT-A").TotalValue);
    }

    [Fact]
    public async Task 未分類品號自成一組_不會被丟掉()
    {
        AddProduct("N1", "無類", null);
        AddInventory("N1", onHand: 2m, unitCost: 50m);   // 100

        var cats = CatRowsOf(await New().Index(null, showDetail: false, searched: true));

        var uncategorized = Assert.Single(cats);
        Assert.Null(uncategorized.CategoryCode);
        Assert.Equal(100m, uncategorized.TotalValue);
    }

    [Fact]
    public async Task 篩選分類後_佔比分母是該分類而非全部()
    {
        SeedTwoCategories();

        var cats = CatRowsOf(await New().Index("CAT-A", showDetail: false, searched: true));

        var only = Assert.Single(cats);
        Assert.Equal("CAT-A", only.CategoryCode);
        Assert.Equal(400m, only.TotalValue);
        Assert.Equal(100m, only.ValuePct);   // 篩選後自己就是全部
    }

    [Fact]
    public async Task 匯出跟著模式走_彙總四欄_明細八欄()
    {
        SeedTwoCategories();

        var export = GetMockExportService();
        var captured = new List<ExportRequest>();
        export.Setup(x => x.ToExcel(It.IsAny<ExportRequest>()))
              .Callback<ExportRequest>(captured.Add)
              .Returns(Array.Empty<byte>());

        await New(export).Export(categoryCode: null, showDetail: false);
        await New(export).Export(categoryCode: null, showDetail: true);

        Assert.Equal(2, captured.Count);
        // 彙總：品項類別／品號數／庫存金額／佔比%，一分類一列
        Assert.Equal(4, captured[0].Headers.Length);
        Assert.Equal(2, captured[0].Rows.Count());
        // 明細：八欄，一品號一列
        Assert.Equal(8, captured[1].Headers.Length);
        Assert.Equal(3, captured[1].Rows.Count());
    }

    [Fact]
    public async Task 未查詢時不查資料_但分類彙總不是null()
    {
        SeedTwoCategories();

        var result = await New().Index(null, showDetail: false, searched: false);

        Assert.Empty(Assert.IsType<List<InventoryValueRowVm>>(Assert.IsType<ViewResult>(result).Model));
        // 畫面無條件讀這個 ViewBag；留 null 的話首次進畫面就是 NullReferenceException
        Assert.Empty(CatRowsOf(result));
    }
}
