using Moq;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.BAS.Controllers;
using SmartFactory.MES.Modules.BAS.Models;

namespace SmartFactory.MES.Tests.BAS;

public class InventoryTransactionTypeControllerTests : TestBase
{
    private readonly InventoryTransactionTypeController _controller;
    private readonly Mock<IPermissionService> _mockPerm;

    public InventoryTransactionTypeControllerTests()
    {
        _mockPerm = new Mock<IPermissionService>();
        _mockPerm.Setup(x => x.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(x => x.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(x => x.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(x => x.CanDelete(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(x => x.CanPrint(It.IsAny<string>())).ReturnsAsync(true);

        var mockApproval = new Mock<IApprovalService>();

        var mockUserCtx = new Mock<IUserContext>();
        mockUserCtx.Setup(x => x.PlantId).Returns("P001");
        mockUserCtx.Setup(x => x.UserId).Returns("User1");

        _controller = new InventoryTransactionTypeController(
            _context,
            mockApproval.Object,
            mockUserCtx.Object,
            GetMockExportService().Object,
            _mockPerm.Object,
            GetMockLocalizerFactory().Object);
        _controller.ControllerContext = GetControllerContext();
    }

    private void SetAjax() =>
        _controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

    /// <summary>
    /// 取得（或建立）系統內建的 IO 生產領料類別。
    /// 種子資料本來就有這一筆，直接 Add 會撞主鍵——這也是實機上它「改得掉、刪不掉」的那一筆。
    /// </summary>
    private void SeedSystemIoAsync(string typeName = "生產領料")
    {
        var rec = _context.InventoryTransactionTypes.Find("IO");
        if (rec == null)
        {
            _context.InventoryTransactionTypes.Add(new InventoryTransactionType
            {
                TypeCode = "IO", TypeName = typeName, TypeCategory = "Production",
                Direction = -1, IsSystemType = true, IsActive = true,
            });
        }
        else
        {
            rec.TypeName     = typeName;
            rec.TypeCategory = "Production";
            rec.Direction    = -1;
            rec.IsSystemType = true;
        }
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    // 1. Index_NotSearched_ReturnsNullModel
    [Fact]
    public async Task Index_NotSearched_ReturnsNullModel()
    {
        var result = await _controller.Index(null, null, null, null, searched: false);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    // 2. Index_Searched_ReturnsPaginatedList
    [Fact]
    public async Task Index_Searched_ReturnsPaginatedList()
    {
        _context.InventoryTransactionTypes.AddRange(
            new InventoryTransactionType { TypeCode = "IN01", TypeName = "Receipt", IsActive = true },
            new InventoryTransactionType { TypeCode = "OUT01", TypeName = "Issue", IsActive = true }
        );
        await _context.SaveChangesAsync();

        var result = await _controller.Index("01", 1, 10, null, searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<PaginatedList<InventoryTransactionType>>(view.Model);
        Assert.Equal(2, model.Count);
    }

    // 3. Index_SetsCanCreateInViewBag
    [Fact]
    public async Task Index_SetsCanCreateInViewBag()
    {
        var result = await _controller.Index(null, null, null, null, searched: false);

        var view = Assert.IsType<ViewResult>(result);
        Assert.True((bool)view.ViewData["CanCreate"]!);
    }

    // 4. Create_GET_ReturnsView
    [Fact]
    public async Task Create_GET_ReturnsView()
    {
        var result = await _controller.Create();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<InventoryTransactionType>(view.Model);
        Assert.True(model.IsActive);
    }

    // 5. Create_POST_ValidModel_AjaxReturnsSuccess
    [Fact]
    public async Task Create_POST_ValidModel_AjaxReturnsSuccess()
    {
        SetAjax();
        var model = new InventoryTransactionType { TypeCode = "IN01", TypeName = "Receipt", IsActive = true };

        var result = await _controller.Create(model);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
    }

    // 6. Create_POST_DuplicateCode_ReturnsError
    [Fact]
    public async Task Create_POST_DuplicateCode_ReturnsError()
    {
        _context.InventoryTransactionTypes.Add(new InventoryTransactionType { TypeCode = "IN01", TypeName = "Existing", IsActive = true });
        await _context.SaveChangesAsync();

        SetAjax();
        var model = new InventoryTransactionType { TypeCode = "IN01", TypeName = "Duplicate", IsActive = true };

        var result = await _controller.Create(model);

        var view = Assert.IsType<PartialViewResult>(result);
        Assert.False(_controller.ModelState.IsValid);
    }

    // 7. Edit_GET_ExistingRecord_ReturnsView
    [Fact]
    public async Task Edit_GET_ExistingRecord_ReturnsView()
    {
        _context.InventoryTransactionTypes.Add(new InventoryTransactionType { TypeCode = "IN01", TypeName = "Receipt", IsActive = true });
        await _context.SaveChangesAsync();

        var result = await _controller.Edit("IN01");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<InventoryTransactionType>(view.Model);
        Assert.Equal("IN01", model.TypeCode);
    }

    // 8. Edit_GET_NotFound_Returns404
    [Fact]
    public async Task Edit_GET_NotFound_Returns404()
    {
        var result = await _controller.Edit("NONEXISTENT");

        Assert.IsType<NotFoundResult>(result);
    }

    // 9. Edit_POST_ValidModel_AjaxReturnsSuccess
    [Fact]
    public async Task Edit_POST_ValidModel_AjaxReturnsSuccess()
    {
        _context.InventoryTransactionTypes.Add(new InventoryTransactionType { TypeCode = "IN01", TypeName = "Old Name", IsActive = true });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        SetAjax();
        var updated = new InventoryTransactionType { TypeCode = "IN01", TypeName = "New Name", IsActive = true };

        var result = await _controller.Edit("IN01", updated);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
    }

    /// <summary>
    /// 系統內建異動類別的**過帳語意**不可改。
    ///
    /// Direction 決定每一筆庫存異動的正負號、TypeCategory 決定要不要產生分錄。
    /// 實機驗收時 IO（生產領料）的 Direction 被改成 +1，於是「發料」變成入庫：
    /// 領一次料庫存不減反增，工單成本、可用量、後續完工單價全部連鎖失真，
    /// 而畫面沒有任何錯誤、傳票照樣平衡。刪除早就擋了，改語意其實更危險——
    /// 刪不掉的東西被改成另一個意思，看起來一切正常。
    /// </summary>
    [Fact]
    public async Task Edit_POST_SystemType_DirectionChange_IsRejected()
    {
        SeedSystemIoAsync();

        SetAjax();
        var updated = new InventoryTransactionType
        {
            TypeCode = "IO", TypeName = "生產領料", TypeCategory = "Production",
            Direction = 1, IsSystemType = true, IsActive = true,
        };

        var result = await _controller.Edit("IO", updated);

        var data = Assert.IsType<JsonResult>(result).Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);

        _context.ChangeTracker.Clear();
        var rec = await _context.InventoryTransactionTypes.FindAsync("IO");
        Assert.Equal(-1, rec!.Direction);
    }

    [Fact]
    public async Task Edit_POST_SystemType_CategoryChange_IsRejected()
    {
        SeedSystemIoAsync();

        SetAjax();
        // Production 類不入總帳、Purchase 類會掛 GR/IR——改掉等於整批分錄悄悄出現或消失
        var updated = new InventoryTransactionType
        {
            TypeCode = "IO", TypeName = "生產領料", TypeCategory = "Purchase",
            Direction = -1, IsSystemType = true, IsActive = true,
        };

        var result = await _controller.Edit("IO", updated);

        var data = Assert.IsType<JsonResult>(result).Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        _context.ChangeTracker.Clear();
        Assert.Equal("Production", (await _context.InventoryTransactionTypes.FindAsync("IO"))!.TypeCategory);
    }

    /// <summary>名稱與啟用狀態不影響過帳，系統類別仍應可改，否則連錯字都修不了。</summary>
    [Fact]
    public async Task Edit_POST_SystemType_RenameOnly_IsAllowed()
    {
        SeedSystemIoAsync(typeName: "舊名稱");

        SetAjax();
        var updated = new InventoryTransactionType
        {
            TypeCode = "IO", TypeName = "生產領料", TypeCategory = "Production",
            Direction = -1, IsSystemType = true, IsActive = true,
        };

        var result = await _controller.Edit("IO", updated);

        var data = Assert.IsType<JsonResult>(result).Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        _context.ChangeTracker.Clear();
        Assert.Equal("生產領料", (await _context.InventoryTransactionTypes.FindAsync("IO"))!.TypeName);
    }

    /// <summary>非系統類別（使用者自建）不受限制。</summary>
    [Fact]
    public async Task Edit_POST_UserDefinedType_DirectionChange_IsAllowed()
    {
        _context.InventoryTransactionTypes.Add(new InventoryTransactionType
        {
            TypeCode = "UD01", TypeName = "自建", TypeCategory = "Adjustment",
            Direction = -1, IsSystemType = false, IsActive = true,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        SetAjax();
        var updated = new InventoryTransactionType
        {
            TypeCode = "UD01", TypeName = "自建", TypeCategory = "Adjustment",
            Direction = 1, IsSystemType = false, IsActive = true,
        };

        var result = await _controller.Edit("UD01", updated);

        var data = Assert.IsType<JsonResult>(result).Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        _context.ChangeTracker.Clear();
        Assert.Equal(1, (await _context.InventoryTransactionTypes.FindAsync("UD01"))!.Direction);
    }

    // 10. Delete_AjaxReturnsSuccess
    [Fact]
    public async Task Delete_AjaxReturnsSuccess()
    {
        _context.InventoryTransactionTypes.Add(new InventoryTransactionType { TypeCode = "IN01", TypeName = "To Delete", IsActive = true });
        await _context.SaveChangesAsync();

        SetAjax();
        var result = await _controller.DeleteConfirmed("IN01");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.Null(await _context.InventoryTransactionTypes.FindAsync("IN01"));
    }

    // 11. Export_NoPrintPermission_ReturnsForbid
    [Fact]
    public async Task Export_NoPrintPermission_ReturnsForbid()
    {
        _mockPerm.Setup(x => x.CanPrint(It.IsAny<string>())).ReturnsAsync(false);

        var result = await _controller.Export("excel", null);

        Assert.IsType<ForbidResult>(result);
    }
}
