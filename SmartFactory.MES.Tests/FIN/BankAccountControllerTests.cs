using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Controllers;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.FIN;

public class BankAccountControllerTests : TestBase
{
    private readonly Mock<IUserContext>       _mockUser    = new();
    private readonly Mock<IPermissionService> _mockPerm    = new();
    private readonly Mock<IDocNumberService>  _mockDocNo   = new();
    private readonly Mock<IDateTimeService>   _mockDt      = new();
    private readonly Mock<ICurrencyService>   _mockCurrency;
    private readonly Mock<IStringLocalizer<ProjectZSharedResource>> _mockLoc = new();
    private readonly BankAccountController _controller;

    private const string PlantId = "P001";

    public BankAccountControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(false);
        _mockLoc.Setup(x => x[It.IsAny<string>()])
                .Returns<string>(k => new LocalizedString(k, k));
        _mockDt.Setup(d => d.Now).Returns(DateTime.UtcNow);
        _mockCurrency = GetMockCurrencyService();

        _controller = new BankAccountController(
            _context, _mockUser.Object, _mockPerm.Object,
            _mockDocNo.Object, _mockDt.Object, _mockCurrency.Object, _mockLoc.Object);
        _controller.ControllerContext = GetControllerContext();
    }

    private void SetAjax() =>
        _controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

    private BankAccount SeedBankAccount(string id = "BANK001")
    {
        var ba = new BankAccount
        {
            AccountId    = id,
            BankName     = "測試銀行",
            AccountNo    = "1234-5678",
            CurrencyCode = "TWD",
            IsActive     = true,
            PlantId      = PlantId,
        };
        _context.BankAccounts.Add(ba);
        _context.SaveChanges();
        return ba;
    }

    // ── Index ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_NoSearch_ReturnsNullModel()
    {
        var result = await _controller.Index(null, null, searched: false);
        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task Index_WithSearch_ReturnsPaginatedList()
    {
        SeedBankAccount("BANK001");
        SeedBankAccount("BANK002");

        var result = await _controller.Index(null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<BankAccount>>(view.Model);
        Assert.Equal(2, list.Count());
    }

    [Fact]
    public async Task Index_SearchByBankName_ReturnsFilteredResults()
    {
        SeedBankAccount("BANK001");
        _context.BankAccounts.Add(new BankAccount
        {
            AccountId = "BANK002", BankName = "其他銀行",
            AccountNo = "9999-0000", CurrencyCode = "USD",
            IsActive = true, PlantId = PlantId,
        });
        _context.SaveChanges();

        var result = await _controller.Index("測試銀行", null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<BankAccount>>(view.Model);
        Assert.Single(list);
    }

    // ── Create GET ────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_GET_AjaxReturnsPartialView()
    {
        SetAjax();
        var result = await _controller.Create();
        var view = Assert.IsType<PartialViewResult>(result);
        Assert.IsType<BankAccount>(view.Model);
    }

    // ── Edit GET ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Edit_GET_ExistingId_AjaxReturnsPartialView()
    {
        SetAjax();
        SeedBankAccount("BANK001");

        var result = await _controller.Edit("BANK001");

        var view = Assert.IsType<PartialViewResult>(result);
        var model = Assert.IsType<BankAccount>(view.Model);
        Assert.Equal("BANK001", model.AccountId);
    }

    [Fact]
    public async Task Edit_GET_NotFound_Returns404()
    {
        var result = await _controller.Edit("NONEXISTENT");
        Assert.IsType<NotFoundResult>(result);
    }

    // ── Save POST (new) ───────────────────────────────────────────────────

    [Fact]
    public async Task Save_NewRecord_PersistsAndReturnsContent()
    {
        var vm = new BankAccount
        {
            AccountId    = string.Empty,
            BankName     = "新銀行",
            AccountNo    = "5555-1234",
            CurrencyCode = "TWD",
            IsActive     = true,
        };

        var result = await _controller.Save(vm);

        Assert.IsType<ContentResult>(result);
        Assert.Equal(1, _context.BankAccounts.Count());
    }

    [Fact]
    public async Task Save_NewRecord_UserEnteredAccountId_Persists()
    {
        // 回歸：AccountId 是「使用者輸入」的主鍵，新增時必填、必有值——上面那支用
        // AccountId = string.Empty 的案例在真實畫面根本不會發生，卻正是它讓
        // 「新增永遠被判為編輯 → FindAsync 找不到 → 回 404」的缺陷長期漏測。
        // 這裡以真實輸入（非空、且 DB 尚無此筆）驅動，才守得住新增這條路徑。
        var vm = new BankAccount
        {
            AccountId    = "BANK-NEW-001",
            BankName     = "使用者輸入主鍵的新銀行",
            AccountNo    = "9999-8888",
            CurrencyCode = "TWD",
            IsActive     = true,
        };

        var result = await _controller.Save(vm);

        Assert.IsType<ContentResult>(result);
        var saved = await _context.BankAccounts.FindAsync("BANK-NEW-001");
        Assert.NotNull(saved);
        Assert.Equal("使用者輸入主鍵的新銀行", saved!.BankName);
    }

    // ── Save POST (update) ────────────────────────────────────────────────

    [Fact]
    public async Task Save_ExistingRecord_UpdatesBankName()
    {
        SeedBankAccount("BANK001");

        var vm = new BankAccount
        {
            AccountId    = "BANK001",
            BankName     = "更新後銀行名稱",
            AccountNo    = "1234-5678",
            CurrencyCode = "TWD",
            IsActive     = true,
        };

        var result = await _controller.Save(vm);

        Assert.IsType<ContentResult>(result);
        var saved = await _context.BankAccounts.FindAsync("BANK001");
        Assert.Equal("更新後銀行名稱", saved!.BankName);
    }

    // ── Delete POST ───────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ExistingRecord_RemovesAndReturnsJsonSuccess()
    {
        SeedBankAccount("BANK001");

        var result = await _controller.Delete("BANK001");

        var json = Assert.IsType<JsonResult>(result);
        var success = (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
        Assert.True(success);
        Assert.Equal(0, _context.BankAccounts.Count());
    }

    [Fact]
    public async Task Delete_NotFound_Returns404()
    {
        var result = await _controller.Delete("NONEXISTENT");
        Assert.IsType<NotFoundResult>(result);
    }
}
