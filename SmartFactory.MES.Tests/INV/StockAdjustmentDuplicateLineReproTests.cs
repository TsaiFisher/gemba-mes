using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>重現調查：VoidAndReopen 後新增第二行並 Save（免簽核流程立即過帳）是否對
/// LineNo=1 產生重複的 PostDocSimplifiedRequest.Lines 項目。</summary>
public class StockAdjustmentDuplicateLineReproTests : TestBase
{
    private readonly Mock<IUserContext>             _mockUser     = new();
    private readonly Mock<IPermissionService>       _mockPerm     = new();
    private readonly Mock<IDateTimeService>         _mockDt       = new();
    private readonly Mock<IApprovalService>         _mockApproval = new();
    private readonly Mock<IDocNumberService>        _mockDocNo    = new();
    private readonly Mock<IInventoryPostingService> _mockPosting  = new();
    private readonly Mock<IUomService>              _mockUom      = new();
    private readonly Mock<ICurrencyService>         _mockCurrency;

    private const string PlantId = "P001";
    private const string UserId  = "user1";

    public StockAdjustmentDuplicateLineReproTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns(UserId);
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((DocumentApprovalConfig?)null); // 免簽核流程 → Save 立即過帳
        _mockUom.Setup(u => u.RoundQtyAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string uom, decimal qty, CancellationToken ct) => qty);
        _mockCurrency = GetMockCurrencyService();

        _context.Products.Add(new Product { ProductCode = "PA2103", ProductName = "滾輪", StockUom = "PCS", CategoryCode = "CAT01" });
        _context.Products.Add(new Product { ProductCode = "MAK2103", ProductName = "主IC", StockUom = "PCS", CategoryCode = "CAT01" });
        _context.Uoms.Add(new Uom { UomCode = "PCS", UomName = "PCS" });
        _context.Warehouses.Add(new Warehouse { WhCode = "F001", WhName = "F001", PlantId = PlantId });
        _context.Warehouses.Add(new Warehouse { WhCode = "W001", WhName = "W001", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "F001", LocationCode = "F001-1", LocationName = "F001-1" });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "W001", LocationCode = "W001-1", LocationName = "W001-1" });
        _context.ProductCategories.Add(new ProductCategory { CategoryCode = "CAT01", CategoryName = "cat", DefaultWarehouseCode = "F001", DefaultLocationCode = "F001-1" });
        _context.SaveChanges();
    }

    private StockAdjustmentController BuildController()
    {
        var ctrl = new StockAdjustmentController(
            _context, _mockUser.Object, _mockPerm.Object, _mockDt.Object,
            _mockApproval.Object, _mockDocNo.Object, _mockPosting.Object,
            _mockUom.Object, _mockCurrency.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        ctrl.ControllerContext = GetControllerContext(userId: UserId);
        return ctrl;
    }

    [Fact]
    public async Task Save_EditExisting_AddSecondLine_ShouldNotDuplicateFirstLineInPostRequest()
    {
        // 既有單只有 1 行（模擬 VoidAndReopen 後的新單）
        var doc = new StockAdjustment
        {
            StockAdjustmentId = "IA-2607-00003",
            AdjustDate = new DateOnly(2026, 7, 19),
            TypeCode = "IA", CreatedBy = UserId, PlantId = PlantId,
            Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Draft,
        };
        doc.Lines.Add(new StockAdjustmentLine
        {
            StockAdjustmentId = doc.StockAdjustmentId, LineNo = 1,
            ProductCode = "PA2103", Qty = 200, UomCode = "PCS",
            StockQty = 200, StockUomCode = "PCS", WhCode = "F001", LocationCode = "F001-1", Direction = 1,
        });
        _context.StockAdjustments.Add(doc);
        _context.SaveChanges();
        _context.ChangeTracker.Clear();

        PostDocSimplifiedRequest? captured = null;
        _mockPosting.Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PostDocSimplifiedRequest, CancellationToken>((req, ct) => captured = req)
            .Returns(Task.CompletedTask);

        // 使用者在畫面上新增第二行後按 Save：vm.Lines 送出 2 行（既有 #1 + 新增 #2）
        var vm = new StockAdjustmentFormVm
        {
            StockAdjustmentId = "IA-2607-00003",
            AdjustDate = new DateOnly(2026, 7, 19),
            TypeCode = "IA",
            Lines = new List<StockAdjustmentLineVm>
            {
                new() { LineNo = 1, ProductCode = "PA2103", Qty = 200, UomCode = "PCS", StockQty = 200, StockUomCode = "PCS", WhCode = "F001", LocationCode = "F001-1", Direction = 1 },
                new() { LineNo = 2, ProductCode = "MAK2103", Qty = 200, UomCode = "PCS", StockQty = 200, StockUomCode = "PCS", WhCode = "W001", LocationCode = "W001-1", Direction = 1 },
            },
        };

        var ctrl = BuildController();
        var result = await ctrl.Save(vm);

        Assert.NotNull(captured);
        var line1Count = captured!.Lines.Count(l => l.LineNo == 1);
        var line2Count = captured!.Lines.Count(l => l.LineNo == 2);
        // 重現調查：如果 doc.Lines 因 RemoveRange 後殘留舊行物件，這裡 line1Count 會是 2 而非 1
        Assert.Equal(1, line1Count);
        Assert.Equal(1, line2Count);
        Assert.Equal(2, captured!.Lines.Count);
    }
}
