using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// 庫存單據存檔的庫存數量由伺服器換算（2026-10-12）。
///
/// <para><b>原本的缺陷</b>：<c>stockUom = lv.StockUomCode ?? lv.UomCode</c>、<c>stockQty = lv.StockQty &gt; 0 ? lv.StockQty : lv.Qty</c>
/// ——畫面從來不送 StockQty／StockUomCode，於是選了「箱」調整 2，就把 2 記成庫存數量、單位記成「箱」，
/// 同一品號的庫存變成好幾種單位混著加。庫存八張單據（調整、移轉、報廢、重工領料、委外發料、借出入、樣品、
/// 跨廠調撥）是同一段程式複製出來的，這裡以調整單為代表，用真的 <see cref="UomService"/>。</para>
/// </summary>
public class StockAdjustmentUomConversionTests : TestBase
{
    private const string PlantId = "P001";
    private readonly Mock<IUserContext> _user = new();
    private readonly Mock<IPermissionService> _perm = new();
    private readonly Mock<IApprovalService> _approval = new();
    private readonly Mock<IDocNumberService> _docNo = new();
    private readonly Mock<IInventoryPostingService> _posting = new();

    public StockAdjustmentUomConversionTests()
    {
        _context.CurrentPlantId = PlantId;
        _user.Setup(u => u.PlantId).Returns(PlantId);
        _user.Setup(u => u.UserId).Returns("user1");
        _perm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        // 有簽核設定＝存成草稿、不過帳：本測試只看存下來的庫存數量
        _approval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new DocumentApprovalConfig { DocumentType = "IA" });
        _docNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("IA-UOM-0001");

        _context.Products.Add(new Product { ProductCode = "M1", ProductName = "M1", StockUom = "PC", CategoryCode = "C1" });
        _context.Uoms.AddRange(new Uom { UomCode = "PC", UomName = "PC" }, new Uom { UomCode = "BOX", UomName = "BOX" },
                               new Uom { UomCode = "KG", UomName = "KG", DecimalPlaces = 3 });
        _context.ProductUomConversions.Add(new ProductUomConversion { ProductCode = "M1", FromUomCode = "BOX", ToUomCode = "PC", Numerator = 12 });
        _context.Warehouses.Add(new Warehouse { WhCode = "W1", WhName = "W1", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "W1", LocationCode = "W1-1", LocationName = "W1-1" });
        _context.ProductCategories.Add(new ProductCategory { CategoryCode = "C1", CategoryName = "C1", DefaultWarehouseCode = "W1", DefaultLocationCode = "W1-1" });
        _context.SaveChanges();
    }

    private StockAdjustmentController Ctrl()
    {
        var c = new StockAdjustmentController(_context, _user.Object, _perm.Object, new Mock<IDateTimeService>().Object,
            _approval.Object, _docNo.Object, _posting.Object,
            new UomService(_context, new MemoryCache(new MemoryCacheOptions())), GetMockCurrencyService().Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        c.ControllerContext = GetControllerContext(userId: "user1");
        return c;
    }

    private static StockAdjustmentFormVm Vm(string uom, decimal qty) => new()
    {
        AdjustDate = new DateOnly(2026, 10, 12), TypeCode = "IA",
        Lines = new() { new() { LineNo = 1, ProductCode = "M1", Qty = qty, UomCode = uom, WhCode = "W1", LocationCode = "W1-1", Direction = 1 } },
    };

    [Fact]
    public async Task 選了非庫存單位_庫存數量依換算存成庫存單位()
    {
        await Ctrl().Save(Vm("BOX", 2));

        var line = await _context.StockAdjustmentLines.AsNoTracking().SingleAsync();
        Assert.Equal("BOX", line.UomCode);
        Assert.Equal(2m, line.Qty);
        Assert.Equal("PC", line.StockUomCode);
        Assert.Equal(24m, line.StockQty);
    }

    [Fact]
    public async Task 畫面送來的庫存數量與單位不採信()
    {
        var vm = Vm("BOX", 2);
        vm.Lines[0].StockQty = 2; vm.Lines[0].StockUomCode = "BOX";   // 舊程式就是這樣把箱數入帳

        await Ctrl().Save(vm);

        var line = await _context.StockAdjustmentLines.AsNoTracking().SingleAsync();
        Assert.Equal(("PC", 24m), (line.StockUomCode, line.StockQty));
    }

    [Fact]
    public async Task 查不到換算的單位_回到表單顯示原因_不存檔()
    {
        var ctrl = Ctrl();
        var result = await ctrl.Save(Vm("KG", 1));

        Assert.IsType<PartialViewResult>(result);
        Assert.Contains(ctrl.ModelState[string.Empty]!.Errors, e => e.ErrorMessage.Length > 0);
        Assert.False(await _context.StockAdjustmentLines.AnyAsync());
    }
}
