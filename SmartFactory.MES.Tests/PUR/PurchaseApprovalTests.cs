using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.PUR.Controllers;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using Xunit;
using SmartFactory.MES;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// PUR 模組審核流程測試：送審/撤回控制器邏輯、價格超標判斷、AvailableLines 篩選。
/// </summary>
public class PurchaseApprovalTests : TestBase
{
    private readonly Mock<IDocNumberService>  _mockDocNo;
    private readonly Mock<IDateTimeService>   _mockDt;
    private readonly Mock<IUserContext>       _mockUser;
    private readonly Mock<IPermissionService> _mockPerm;
    private readonly Mock<IApprovalService>   _mockApproval;
    private readonly Mock<IUomService>        _mockUom;
    private readonly Mock<ICurrencyService>   _mockCurrency;
    private readonly PurchaseService          _service;
    private const string PlantId = "P001";
    private const string UserId  = "user1";
    private static readonly DateTime UtcNow = new(2025, 4, 1, 8, 0, 0, DateTimeKind.Utc);

    public PurchaseApprovalTests()
    {
        _mockDocNo   = new Mock<IDocNumberService>();
        _mockDt      = new Mock<IDateTimeService>();
        _mockDt.Setup(d => d.Now).Returns(UtcNow);
        _mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(new DateOnly(2025, 4, 1));

        // 全域查詢過濾器需設定 CurrentPlantId，否則 IPlantEntity 查詢全部被過濾
        _context.CurrentPlantId = PlantId;

        _mockUser     = new Mock<IUserContext>();
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns(UserId);

        _mockPerm     = new Mock<IPermissionService>();
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);

        _mockApproval = new Mock<IApprovalService>();
        _mockUom      = new Mock<IUomService>();
        _mockCurrency = GetMockCurrencyService();
        _mockUom.Setup(u => u.ConvertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string p, string f, string t, decimal q, CancellationToken ct) => q);

        _service = new PurchaseService(_context, _mockDocNo.Object, _mockDt.Object, _mockUom.Object, _mockCurrency.Object, new SmartFactory.MES.Modules.INV.Services.InventoryReservationService(_context));

        _context.Products.Add(new Product
        {
            ProductCode = "P001", ProductName = "測試品項",
            PurchaseUom = "PC", StockUom = "PC",
            StandardCost = 100m,              // StandardCost 設定，用於超標判斷
        });
        _context.Products.Add(new Product
        {
            ProductCode = "P002", ProductName = "無標準成本品項",
            PurchaseUom = "PC", StockUom = "PC",
            StandardCost = 0m,                // StandardCost = 0 → 不觸發審核
        });
        _context.Uoms.Add(new Uom { UomCode = "PC", UomName = "個" });
        _context.Companies.Add(new Company
        {
            CompanyCode = "SUP001", CompanyName = "測試供應商",
            IsSupplier = true, IsActive = true,
        });
        _context.Currencies.Add(new Currency { CurrencyCode = "TWD", CurrencyName = "台幣", IsActive = true });
        _context.SaveChanges();
    }

    private PurchaseOrderController BuildPoController()
    {
        var ctrl = new PurchaseOrderController(
            _context, _service, _mockUser.Object,
            _mockPerm.Object, _mockDt.Object, _mockApproval.Object,
            _mockUom.Object, _mockCurrency.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        ctrl.ControllerContext = GetControllerContextWithServices(
            userId: UserId, permService: _mockPerm.Object, userContext: _mockUser.Object,
            approvalService: _mockApproval.Object,
            sharedLocalizer: GetMockLocalizer<ProjectZSharedResource>().Object);
        return ctrl;
    }

    private PurchaseRequestController BuildPrController()
    {
        var ctrl = new PurchaseRequestController(
            _context, _service, _mockUser.Object,
            _mockPerm.Object, _mockDt.Object, _mockApproval.Object,
            _mockUom.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        ctrl.ControllerContext = GetControllerContextWithServices(
            userId: UserId, permService: _mockPerm.Object, userContext: _mockUser.Object,
            approvalService: _mockApproval.Object,
            sharedLocalizer: GetMockLocalizer<ProjectZSharedResource>().Object);
        return ctrl;
    }

    // ── PO Save — 價格超標觸發 Draft ─────────────────────────────────────────

    [Fact]
    public async Task SavePurchaseOrder_PriceExceedsStandard_ShouldSetDraftStatus()
    {
        _mockDocNo.Setup(d => d.NextAsync("PurchaseOrder", PlantId, It.IsAny<DateOnly?>(), default))
                  .ReturnsAsync("PO2025000001");

        var ctrl = BuildPoController();
        var vm = new PurchaseOrderFormVm
        {
            OrderDate    = new DateOnly(2025, 4, 1),
            SupplierCode = "SUP001",
            Lines = new List<PurchaseOrderLineVm>
            {
                new()
                {
                    LineNo = 1, ProductCode = "P001",
                    OrderQty = 5, UomCode = "PC", StockQty = 5, StockUomCode = "PC",
                    UnitPrice = 150m,  // > StandardCost(100) → 超標
                    CurrencyCode = "TWD", ExchangeRate = 1,
                    TaxRate = 0, LineAmount = 750, TaxAmount = 0, TotalAmount = 750,
                }
            }
        };

        await ctrl.Save(vm);

        var po = await _context.PurchaseOrders.FindAsync("PO2025000001");
        Assert.NotNull(po);
        Assert.Equal(DocumentApprovalStatus.Draft, po!.ApprovalStatus);
    }

    [Fact]
    public async Task SavePurchaseOrder_PriceWithinStandard_ShouldSetApprovedStatus()
    {
        _mockDocNo.Setup(d => d.NextAsync("PurchaseOrder", PlantId, It.IsAny<DateOnly?>(), default))
                  .ReturnsAsync("PO2025000002");
        _mockApproval.Setup(a => a.ResolveSaveStatusAsync("PurchaseOrder", PlantId))
                     .ReturnsAsync(DocumentApprovalStatus.Approved);

        var ctrl = BuildPoController();
        var vm = new PurchaseOrderFormVm
        {
            OrderDate    = new DateOnly(2025, 4, 1),
            SupplierCode = "SUP001",
            Lines = new List<PurchaseOrderLineVm>
            {
                new()
                {
                    LineNo = 1, ProductCode = "P001",
                    OrderQty = 5, UomCode = "PC", StockQty = 5, StockUomCode = "PC",
                    UnitPrice = 80m,   // < StandardCost(100) → 不超標
                    CurrencyCode = "TWD", ExchangeRate = 1,
                    TaxRate = 0, LineAmount = 400, TaxAmount = 0, TotalAmount = 400,
                }
            }
        };

        await ctrl.Save(vm);

        var po = await _context.PurchaseOrders.FindAsync("PO2025000002");
        Assert.NotNull(po);
        Assert.Equal(DocumentApprovalStatus.Approved, po!.ApprovalStatus);
    }

    [Fact]
    public async Task SavePurchaseOrder_ZeroStandardCost_ShouldSetApproved()
    {
        // StandardCost=0 不應觸發審核，即使 UnitPrice > 0
        _mockDocNo.Setup(d => d.NextAsync("PurchaseOrder", PlantId, It.IsAny<DateOnly?>(), default))
                  .ReturnsAsync("PO2025000003");
        _mockApproval.Setup(a => a.ResolveSaveStatusAsync("PurchaseOrder", PlantId))
                     .ReturnsAsync(DocumentApprovalStatus.Approved);

        var ctrl = BuildPoController();
        var vm = new PurchaseOrderFormVm
        {
            OrderDate    = new DateOnly(2025, 4, 1),
            SupplierCode = "SUP001",
            Lines = new List<PurchaseOrderLineVm>
            {
                new()
                {
                    LineNo = 1, ProductCode = "P002",   // StandardCost=0
                    OrderQty = 5, UomCode = "PC", StockQty = 5, StockUomCode = "PC",
                    UnitPrice = 999m,
                    CurrencyCode = "TWD", ExchangeRate = 1,
                    TaxRate = 0, LineAmount = 4995, TaxAmount = 0, TotalAmount = 4995,
                }
            }
        };

        await ctrl.Save(vm);

        var po = await _context.PurchaseOrders.FindAsync("PO2025000003");
        Assert.NotNull(po);
        Assert.Equal(DocumentApprovalStatus.Approved, po!.ApprovalStatus);
    }

    // ── PR Save — 依 ApprovalConfig 決定 Draft/Approved ──────────────────────

    [Fact]
    public async Task SavePurchaseRequest_WithApprovalRequired_ShouldSetDraft()
    {
        _mockDocNo.Setup(d => d.NextAsync("PurchaseRequest", PlantId, It.IsAny<DateOnly?>(), default))
                  .ReturnsAsync("PR2025000001");

        _mockApproval.Setup(a => a.GetApprovalConfig("PurchaseRequest", PlantId))
            .ReturnsAsync(new DocumentApprovalConfig
            {
                DocumentType = "PurchaseRequest",
                PlantId = PlantId,
                IsApprovalRequired = true,
                ApprovalTemplateId = "TPL001",
            });

        var ctrl = BuildPrController();
        var vm = new PurchaseRequestFormVm
        {
            RequestDate = new DateOnly(2025, 4, 1),
            Lines = new List<PurchaseRequestLineVm>
            {
                new() { LineNo = 1, ProductCode = "P001", RequestQty = 5, UomCode = "PC", StockQty = 5, StockUomCode = "PC" }
            }
        };

        await ctrl.Save(vm);

        var pr = await _context.PurchaseRequests.FindAsync("PR2025000001");
        Assert.NotNull(pr);
        Assert.Equal(DocumentApprovalStatus.Draft, pr!.ApprovalStatus);
    }

    [Fact]
    public async Task SavePurchaseRequest_NoApprovalRequired_ShouldSetApproved()
    {
        _mockDocNo.Setup(d => d.NextAsync("PurchaseRequest", PlantId, It.IsAny<DateOnly?>(), default))
                  .ReturnsAsync("PR2025000002");
        _mockApproval.Setup(a => a.ResolveSaveStatusAsync("PurchaseRequest", PlantId))
                     .ReturnsAsync(DocumentApprovalStatus.Approved);

        var ctrl = BuildPrController();
        var vm = new PurchaseRequestFormVm
        {
            RequestDate = new DateOnly(2025, 4, 1),
            Lines = new List<PurchaseRequestLineVm>
            {
                new() { LineNo = 1, ProductCode = "P001", RequestQty = 5, UomCode = "PC", StockQty = 5, StockUomCode = "PC" }
            }
        };

        await ctrl.Save(vm);

        var pr = await _context.PurchaseRequests.FindAsync("PR2025000002");
        Assert.NotNull(pr);
        Assert.Equal(DocumentApprovalStatus.Approved, pr!.ApprovalStatus);
    }

    // ── PO Submit ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubmitPurchaseOrder_DraftStatus_ShouldSetPendingApproval()
    {
        var po = new PurchaseOrder
        {
            PurchaseOrderId = "PO2025000090",
            OrderDate = new DateOnly(2025, 4, 1),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 0, PlantId = PlantId,
            ApprovalStatus = DocumentApprovalStatus.Draft,
        };
        _context.PurchaseOrders.Add(po);
        await _context.SaveChangesAsync();

        _mockApproval.Setup(a => a.GetApprovalConfig("PurchaseOrder", PlantId))
            .ReturnsAsync(new DocumentApprovalConfig
            {
                DocumentType = "PurchaseOrder", PlantId = PlantId,
                IsApprovalRequired = true, ApprovalTemplateId = "TPL002",
            });
        _mockApproval.Setup(a => a.SubmitForApproval("PO2025000090", "PurchaseOrder", "TPL002", UserId))
            .ReturnsAsync(new ApprovalRequest { Id = "REQ001", Status = ApprovalStatus.Pending });

        var ctrl = BuildPoController();
        var result = await ctrl.Submit("PO2025000090");

        Assert.IsType<OkObjectResult>(result);
        var updated = await _context.PurchaseOrders.FindAsync("PO2025000090");
        Assert.Equal(DocumentApprovalStatus.PendingApproval, updated!.ApprovalStatus);
        Assert.Equal("REQ001", updated.ApprovalRequestId);
    }

    [Fact]
    public async Task SubmitPurchaseOrder_AlreadyPending_ShouldReturnBadRequest()
    {
        var po = new PurchaseOrder
        {
            PurchaseOrderId = "PO2025000091",
            OrderDate = new DateOnly(2025, 4, 1),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 0, PlantId = PlantId,
            ApprovalStatus = DocumentApprovalStatus.PendingApproval,
        };
        _context.PurchaseOrders.Add(po);
        await _context.SaveChangesAsync();

        var ctrl = BuildPoController();
        var result = await ctrl.Submit("PO2025000091");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── PO Withdraw ───────────────────────────────────────────────────────────

    [Fact]
    public async Task WithdrawPurchaseOrder_PendingStatus_ShouldSetDraft()
    {
        var po = new PurchaseOrder
        {
            PurchaseOrderId = "PO2025000092",
            OrderDate = new DateOnly(2025, 4, 1),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 0, PlantId = PlantId,
            ApprovalStatus = DocumentApprovalStatus.PendingApproval,
            ApprovalRequestId = "REQ002",
        };
        _context.PurchaseOrders.Add(po);
        await _context.SaveChangesAsync();

        _mockApproval.Setup(a => a.WithdrawRequest("REQ002", UserId)).ReturnsAsync(true);

        var ctrl = BuildPoController();
        var result = await ctrl.Withdraw("PO2025000092");

        Assert.IsType<OkObjectResult>(result);
        var updated = await _context.PurchaseOrders.FindAsync("PO2025000092");
        Assert.Equal(DocumentApprovalStatus.Draft, updated!.ApprovalStatus);
        Assert.Null(updated.ApprovalRequestId);
    }

    [Fact]
    public async Task WithdrawPurchaseOrder_NotPending_ShouldReturnBadRequest()
    {
        var po = new PurchaseOrder
        {
            PurchaseOrderId = "PO2025000093",
            OrderDate = new DateOnly(2025, 4, 1),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 0, PlantId = PlantId,
            ApprovalStatus = DocumentApprovalStatus.Draft,
        };
        _context.PurchaseOrders.Add(po);
        await _context.SaveChangesAsync();

        var ctrl = BuildPoController();
        var result = await ctrl.Withdraw("PO2025000093");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── AvailableLines — 僅顯示已核准文件 ────────────────────────────────────

    [Fact]
    public async Task PurchaseRequest_AvailableLines_ShouldOnlyReturnApprovedDocs()
    {
        // 準備兩張請購單：一張已核准、一張草稿
        var prApproved = new PurchaseRequest
        {
            PurchaseRequestId = "PR2025000050",
            RequestDate = new DateOnly(2025, 4, 1),
            RequestedBy = UserId, PlantId = PlantId,
            ApprovalStatus = DocumentApprovalStatus.Approved,
        };
        prApproved.Lines.Add(new PurchaseRequestLine
        {
            PurchaseRequestId = "PR2025000050", LineNo = 1,
            ProductCode = "P001", RequestQty = 10, OrderedQty = 0, UomCode = "PC",
        });

        var prDraft = new PurchaseRequest
        {
            PurchaseRequestId = "PR2025000051",
            RequestDate = new DateOnly(2025, 4, 1),
            RequestedBy = UserId, PlantId = PlantId,
            ApprovalStatus = DocumentApprovalStatus.Draft,
        };
        prDraft.Lines.Add(new PurchaseRequestLine
        {
            PurchaseRequestId = "PR2025000051", LineNo = 1,
            ProductCode = "P001", RequestQty = 5, OrderedQty = 0, UomCode = "PC",
        });

        _context.PurchaseRequests.AddRange(prApproved, prDraft);
        await _context.SaveChangesAsync();

        var ctrl = BuildPrController();
        var result = await ctrl.AvailableLines(searchTerm: null);

        var json = Assert.IsType<JsonResult>(result);
        var lines = json.Value as IEnumerable<object>;
        Assert.NotNull(lines);

        // 反射取 PurchaseRequestId 值進行驗證
        var ids = lines!
            .Select(l => l.GetType().GetProperty("PurchaseRequestId")?.GetValue(l)?.ToString())
            .ToList();

        Assert.Contains("PR2025000050", ids);
        Assert.DoesNotContain("PR2025000051", ids);
    }

    [Fact]
    public async Task PurchaseOrder_AvailableLines_ShouldOnlyReturnApprovedAndFilterBySupplier()
    {
        // PO 已核准，供應商 SUP001
        var po = new PurchaseOrder
        {
            PurchaseOrderId = "PO2025000060",
            OrderDate = new DateOnly(2025, 4, 1),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 0, PlantId = PlantId,
            ApprovalStatus = DocumentApprovalStatus.Approved,
        };
        po.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO2025000060", LineNo = 1,
            ProductCode = "P001", OrderQty = 10, ReceivedQty = 0,
            UomCode = "PC", StockQty = 10, StockUomCode = "PC",
            UnitPrice = 100, CurrencyCode = "TWD", ExchangeRate = 1,
            TaxRate = 0, LineAmount = 1000, TaxAmount = 0, TotalAmount = 1000,
        });

        // PO 草稿，供應商 SUP001 — 不應出現
        var poDraft = new PurchaseOrder
        {
            PurchaseOrderId = "PO2025000061",
            OrderDate = new DateOnly(2025, 4, 1),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 0, PlantId = PlantId,
            ApprovalStatus = DocumentApprovalStatus.Draft,
        };
        poDraft.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO2025000061", LineNo = 1,
            ProductCode = "P001", OrderQty = 5, ReceivedQty = 0,
            UomCode = "PC", StockQty = 5, StockUomCode = "PC",
            UnitPrice = 100, CurrencyCode = "TWD", ExchangeRate = 1,
            TaxRate = 0, LineAmount = 500, TaxAmount = 0, TotalAmount = 500,
        });

        _context.PurchaseOrders.AddRange(po, poDraft);
        await _context.SaveChangesAsync();

        var ctrl = new PurchaseOrderController(
            _context, _service, _mockUser.Object,
            _mockPerm.Object, _mockDt.Object, _mockApproval.Object,
            _mockUom.Object, _mockCurrency.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        ctrl.ControllerContext = GetControllerContextWithServices(
            userId: UserId, permService: _mockPerm.Object, userContext: _mockUser.Object,
            approvalService: _mockApproval.Object,
            sharedLocalizer: GetMockLocalizer<ProjectZSharedResource>().Object);

        var result = await ctrl.AvailableLines(searchTerm: null, supplierCode: "SUP001");

        var json = Assert.IsType<JsonResult>(result);
        var lines = json.Value as IEnumerable<object>;
        Assert.NotNull(lines);

        var poIds = lines!
            .Select(l => l.GetType().GetProperty("PurchaseOrderId")?.GetValue(l)?.ToString())
            .ToList();

        Assert.Contains("PO2025000060", poIds);
        Assert.DoesNotContain("PO2025000061", poIds);
    }
}
