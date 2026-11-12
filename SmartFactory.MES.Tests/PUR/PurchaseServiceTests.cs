using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// PurchaseService 單元測試。
/// DocNumberService.NextAsync 依賴 PostgreSQL UPSERT，在 InMemory DB 下改用 Mock。
/// </summary>
public class PurchaseServiceTests : TestBase
{
    private readonly Mock<IDocNumberService> _mockDocNo;
    private readonly Mock<IDateTimeService>  _mockDt;
    private readonly Mock<IUomService>       _mockUom;
    private readonly Mock<ICurrencyService>  _mockCurrency;
    private readonly PurchaseService         _service;
    private const string PlantId  = "P001";
    private const string UserId   = "user1";
    private static readonly DateTime UtcNow = new(2025, 3, 27, 8, 0, 0, DateTimeKind.Utc);

    public PurchaseServiceTests()
    {
        _mockDocNo   = new Mock<IDocNumberService>();
        _mockDt      = new Mock<IDateTimeService>();
        _mockUom     = new Mock<IUomService>();
        _mockCurrency = GetMockCurrencyService();
        _mockDt.Setup(d => d.Now).Returns(UtcNow);

        _mockUom.Setup(u => u.ConvertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string p, string f, string t, decimal q, CancellationToken ct) => q);

        _service = new PurchaseService(_context, _mockDocNo.Object, _mockDt.Object, _mockUom.Object, _mockCurrency.Object, new SmartFactory.MES.Modules.INV.Services.InventoryReservationService(_context));

        // 全域查詢過濾器需設定 CurrentPlantId，否則 IPlantEntity 查詢全部被過濾
        _context.CurrentPlantId = PlantId;

        // 建立基礎資料
        _context.Products.Add(new Product
        {
            ProductCode = "P001", ProductName = "測試品項",
            PurchaseUom = "PC", StockUom = "PC",
        });
        _context.Uoms.Add(new Uom { UomCode = "PC", UomName = "個" });
        _context.Companies.Add(new Company
        {
            CompanyCode = "SUP001", CompanyName = "測試供應商",
            IsSupplier = true,
        });
        _context.SaveChanges();
    }

    // ── PurchaseRequest ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePurchaseRequest_WithLines_ShouldPersist()
    {
        _mockDocNo.Setup(d => d.NextAsync("PurchaseRequest", PlantId, It.IsAny<DateOnly?>(), default))
                  .ReturnsAsync("PR2025000001");

        var vm = new PurchaseRequestFormVm
        {
            RequestDate = new DateOnly(2025, 3, 27),
            RequestedBy = UserId,
            Lines = new List<PurchaseRequestLineVm>
            {
                new() { LineNo = 1, ProductCode = "P001", RequestQty = 10, UomCode = "PC", StockQty = 10, StockUomCode = "PC" },
                new() { LineNo = 2, ProductCode = "P001", RequestQty = 5,  UomCode = "PC", StockQty = 5,  StockUomCode = "PC" },
            }
        };

        var result = await _service.CreatePurchaseRequestAsync(vm, UserId, PlantId);

        Assert.Equal("PR2025000001", result.PurchaseRequestId);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(PlantId, result.PlantId);

        var inDb = await _context.PurchaseRequests
            .Include(p => p.Lines)
            .FirstAsync(p => p.PurchaseRequestId == "PR2025000001");
        Assert.Equal(2, inDb.Lines.Count);
    }

    [Fact]
    public async Task DeletePurchaseRequest_WithNoOrderedLines_ShouldDelete()
    {
        var pr = new PurchaseRequest
        {
            PurchaseRequestId = "PR2025000002",
            RequestDate = new DateOnly(2025, 3, 27),
            RequestedBy = UserId,
            PlantId = PlantId,
        };
        pr.Lines.Add(new PurchaseRequestLine
        {
            PurchaseRequestId = "PR2025000002", LineNo = 1,
            ProductCode = "P001", RequestQty = 5, OrderedQty = 0,
        });
        _context.PurchaseRequests.Add(pr);
        await _context.SaveChangesAsync();

        await _service.DeletePurchaseRequestAsync("PR2025000002");

        Assert.Null(await _context.PurchaseRequests.FindAsync("PR2025000002"));
    }

    [Fact]
    public async Task DeletePurchaseRequest_WithOrderedLines_ShouldThrow()
    {
        var pr = new PurchaseRequest
        {
            PurchaseRequestId = "PR2025000003",
            RequestDate = new DateOnly(2025, 3, 27),
            RequestedBy = UserId,
            PlantId = PlantId,
        };
        pr.Lines.Add(new PurchaseRequestLine
        {
            PurchaseRequestId = "PR2025000003", LineNo = 1,
            ProductCode = "P001", RequestQty = 10, OrderedQty = 5, // 已有轉採購
        });
        _context.PurchaseRequests.Add(pr);
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeletePurchaseRequestAsync("PR2025000003"));
    }

    // ── PurchaseOrder ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePurchaseOrder_FromPr_ShouldUpdatePrOrderedQty()
    {
        // 先建 PR
        var pr = new PurchaseRequest
        {
            PurchaseRequestId = "PR2025000010",
            RequestDate = new DateOnly(2025, 3, 27),
            RequestedBy = UserId,
            PlantId = PlantId,
        };
        pr.Lines.Add(new PurchaseRequestLine
        {
            PurchaseRequestId = "PR2025000010", LineNo = 1,
            ProductCode = "P001", RequestQty = 20, OrderedQty = 0, UomCode = "PC",
        });
        _context.PurchaseRequests.Add(pr);
        await _context.SaveChangesAsync();

        _mockDocNo.Setup(d => d.NextAsync("PurchaseOrder", PlantId, It.IsAny<DateOnly?>(), default))
                  .ReturnsAsync("PO2025000001");

        var vm = new PurchaseOrderFormVm
        {
            OrderDate    = new DateOnly(2025, 3, 27),
            CreatedBy    = UserId,
            SupplierCode = "SUP001",
            Lines = new List<PurchaseOrderLineVm>
            {
                new()
                {
                    LineNo = 1, ProductCode = "P001",
                    PurchaseRequestId = "PR2025000010", PrLineNo = 1,
                    OrderQty = 15, UomCode = "PC", StockQty = 15, StockUomCode = "PC",
                    UnitPrice = 100, CurrencyCode = "TWD", ExchangeRate = 1,
                    TaxRate = 0.05m, LineAmount = 1500, TaxAmount = 75, TotalAmount = 1575,
                }
            }
        };

        await _service.CreatePurchaseOrderAsync(vm, UserId, PlantId);

        var prLine = await _context.PurchaseRequestLines
            .FindAsync("PR2025000010", 1);
        Assert.Equal(15m, prLine!.OrderedQty);
    }

    // ── GoodsReceipt ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateGoodsReceipt_ShouldCreateLinesWithoutInventoryMovement()
    {
        _mockDocNo.Setup(d => d.NextAsync("GoodsReceipt", PlantId, It.IsAny<DateOnly?>(), default))
                  .ReturnsAsync("PE2025000001");

        var vm = new GoodsReceiptFormVm
        {
            ReceiptDate  = new DateOnly(2025, 3, 27),
            CreatedBy    = UserId,
            SupplierCode = "SUP001",
            Lines = new List<GoodsReceiptLineVm>
            {
                new()
                {
                    LineNo = 1, ProductCode = "P001",
                    ReceiptQty = 10, UomCode = "PC", StockQty = 10, StockUomCode = "PC",
                    UnitPrice = 100, CurrencyCode = "TWD", ExchangeRate = 1,
                    TaxRate = 0.05m, LineAmount = 1000, TaxAmount = 50, TotalAmount = 1050,
                }
            }
        };

        var result = await _service.CreateGoodsReceiptAsync(vm, UserId, PlantId);

        Assert.Equal("PE2025000001", result.GoodsReceiptId);
        Assert.Single(result.Lines);
        Assert.Equal(1050m, result.TotalAmount);
    }

    [Fact]
    public async Task CreateGoodsReceipt_AgainstPo_ConvertsSupplyPegToOnHand()
    {
        // mfg-domain.md §24.9(3)：投產時對 PO 在途建立 Category=Supply 預留；
        // 收貨（ReceivedQty 遞增）時應自動把該 peg 轉為 OnHand。
        _mockDocNo.Setup(d => d.NextAsync("GoodsReceipt", PlantId, It.IsAny<DateOnly?>(), default))
                  .ReturnsAsync("PE2025000002");

        _context.PurchaseOrders.Add(new PurchaseOrder
        {
            PurchaseOrderId = "PO-PEG", PlantId = PlantId,
            ApprovalStatus = SmartFactory.MES.Models.DocumentApprovalStatus.Approved,
        });
        _context.PurchaseOrderLines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO-PEG", LineNo = 1, ProductCode = "P001",
            OrderQty = 50m, ReceivedQty = 0m, IsClosed = false,
            DeliveryDate = new DateOnly(2025, 4, 1),
        });
        await _context.SaveChangesAsync();

        var reservations = new InventoryReservationService(_context);
        await reservations.PegSupplyAsync(PlantId, "P001", 30m,
            ReservationSourceType.WorkOrderMaterial, "WO-PEG", null,
            new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            "PurchaseOrder", "PO-PEG", 1, new DateOnly(2025, 4, 1));

        var vm = new GoodsReceiptFormVm
        {
            ReceiptDate  = new DateOnly(2025, 4, 1),
            CreatedBy    = UserId,
            SupplierCode = "SUP001",
            Lines = new List<GoodsReceiptLineVm>
            {
                new()
                {
                    LineNo = 1, ProductCode = "P001",
                    PurchaseOrderId = "PO-PEG", PoLineNo = 1,
                    ReceiptQty = 30, UomCode = "PC", StockQty = 30, StockUomCode = "PC",
                    UnitPrice = 100, CurrencyCode = "TWD", ExchangeRate = 1,
                }
            }
        };

        await _service.CreateGoodsReceiptAsync(vm, UserId, PlantId);

        var peg = _context.Set<InventoryReservation>()
            .Single(r => r.SupplyDocId == "PO-PEG" && r.ProductCode == "P001");
        Assert.Equal(ReservationCategory.OnHand, peg.Category);   // 已由在途轉為在手
    }

    [Fact]
    public async Task CreateGoodsReceipt_LotRequired_MissingLot_ShouldThrow()
    {
        // 準備 LotControlled 產品
        _context.Products.Add(new Product { ProductCode = "PROD-LOT", IsLotControlled = true, IsActive = true });
        await _context.SaveChangesAsync();

        var vm = new GoodsReceiptFormVm
        {
            ReceiptDate = new DateOnly(2025, 3, 27),
            SupplierCode = "SUP001",
            Lines = new List<GoodsReceiptLineVm>
            {
                new()
                {
                    LineNo = 1, ProductCode = "PROD-LOT",
                    ReceiptQty = 10, UomCode = "PC", StockQty = 10, StockUomCode = "PC",
                    LotNo = "" // Missing
                }
            }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateGoodsReceiptAsync(vm, UserId, PlantId));
        PzAssert.Message("Inv_LotRequiredFmt", ex.Message);
    }

    [Fact]
    public async Task DeleteGoodsReceipt_ShouldRemoveGoodsReceipt()
    {
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = "PE2025000099",
            ReceiptDate = new DateOnly(2025, 3, 27),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 1050, PlantId = PlantId,
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = "PE2025000099", LineNo = 1,
            ProductCode = "P001", ReceiptQty = 10, UomCode = "PC",
            StockQty = 10, StockUomCode = "PC",
            TotalAmount = 1050,
        });
        _context.GoodsReceipts.Add(gr);
        await _context.SaveChangesAsync();

        await _service.DeleteGoodsReceiptAsync("PE2025000099");

        Assert.Null(await _context.GoodsReceipts.FindAsync("PE2025000099"));
    }

    // ── GoodsReturn ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateGoodsReturn_ShouldCreateLinesWithoutInventoryMovement()
    {
        _mockDocNo.Setup(d => d.NextAsync("GoodsReturn", PlantId, It.IsAny<DateOnly?>(), default))
                  .ReturnsAsync("PF2025000001");

        var vm = new GoodsReturnFormVm
        {
            ReturnDate   = new DateOnly(2025, 3, 28),
            CreatedBy    = UserId,
            SupplierCode = "SUP001",
            Lines = new List<GoodsReturnLineVm>
            {
                new()
                {
                    LineNo = 1, ProductCode = "P001",
                    ReturnQty = 3, UomCode = "PC", StockQty = 3, StockUomCode = "PC",
                    UnitPrice = 100, CurrencyCode = "TWD", ExchangeRate = 1,
                    TaxRate = 0.05m, LineAmount = 300, TaxAmount = 15, TotalAmount = 315,
                }
            }
        };

        var result = await _service.CreateGoodsReturnAsync(vm, UserId, PlantId);

        Assert.Equal("PF2025000001", result.GoodsReturnId);
        Assert.Single(result.Lines);
        Assert.Equal(315m, result.TotalAmount);
    }

    // ── 正向累加上限（累計退貨不得超過進貨量）─────────────────────────────

    /// <summary>
    /// 累計退貨超過進貨量必須被拒絕。
    ///
    /// 同一支服務的 <see cref="PurchaseService.UpdatePoReceivedQtyAsync"/> 一開始就擋
    /// 「累計進貨 &gt; 訂購量」，退貨側卻一路沒有上限：可退量（ReceiptQty − ReturnedQty）
    /// 會變成負數，而負的可退量沒有任何地方會擋，一張進貨可以無限退。
    /// 退貨還會負向回沖來源 AP 發票，超退等於把應付沖到低於實際應付。
    /// </summary>
    [Fact]
    public async Task CreateGoodsReturn_ExceedingReceiptQty_Rejected()
    {
        _mockDocNo.Setup(d => d.NextAsync("GoodsReturn", PlantId, It.IsAny<DateOnly?>(), default))
                  .ReturnsAsync("PF2025000010");
        await SeedGoodsReceiptAsync("PE2025000100", receiptQty: 10m, returnedQty: 8m);

        // 8 + 3 = 11 > 10
        var vm = BuildReturnVmLinkedToReceipt("PE2025000100", returnQty: 3m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateGoodsReturnAsync(vm, UserId, PlantId));

        PzAssert.Message("Pur_ReturnExceedsReceiptFmt", ex.Message);
        var grLine = await _context.GoodsReceiptLines.FindAsync("PE2025000100", 1);
        Assert.Equal(8m, grLine!.ReturnedQty);
    }

    /// <summary>邊界：剛好全退必須放行（守門是 <c>&gt;</c> 不是 <c>&gt;=</c>）。</summary>
    [Fact]
    public async Task CreateGoodsReturn_ExactlyReceiptQty_Allowed()
    {
        _mockDocNo.Setup(d => d.NextAsync("GoodsReturn", PlantId, It.IsAny<DateOnly?>(), default))
                  .ReturnsAsync("PF2025000011");
        await SeedGoodsReceiptAsync("PE2025000101", receiptQty: 10m, returnedQty: 8m);

        var vm = BuildReturnVmLinkedToReceipt("PE2025000101", returnQty: 2m);

        await _service.CreateGoodsReturnAsync(vm, UserId, PlantId);

        var grLine = await _context.GoodsReceiptLines.FindAsync("PE2025000101", 1);
        Assert.Equal(10m, grLine!.ReturnedQty);
    }

    /// <summary>進貨單一行（品號 P001、單位 PC），進貨量與已退量由參數指定。</summary>
    private async Task SeedGoodsReceiptAsync(string id, decimal receiptQty, decimal returnedQty)
    {
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = id,
            ReceiptDate = new DateOnly(2025, 3, 27),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = receiptQty * 100m, PlantId = PlantId,
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = id, LineNo = 1,
            ProductCode = "P001", ReceiptQty = receiptQty, UomCode = "PC",
            StockQty = receiptQty, StockUomCode = "PC",
            UnitPrice = 100m, TotalAmount = receiptQty * 100m,
            ReturnedQty = returnedQty,
        });
        _context.GoodsReceipts.Add(gr);
        await _context.SaveChangesAsync();
    }

    /// <summary>連結到指定進貨單第 1 行的退貨單 VM。</summary>
    private static GoodsReturnFormVm BuildReturnVmLinkedToReceipt(string receiptId, decimal returnQty) => new()
    {
        ReturnDate   = new DateOnly(2025, 3, 28),
        CreatedBy    = UserId,
        SupplierCode = "SUP001",
        Lines = new List<GoodsReturnLineVm>
        {
            new()
            {
                LineNo = 1, ProductCode = "P001",
                GoodsReceiptId = receiptId, ReceiptLineNo = 1,
                ReturnQty = returnQty, UomCode = "PC", StockQty = returnQty, StockUomCode = "PC",
                UnitPrice = 100, CurrencyCode = "TWD", ExchangeRate = 1,
                TaxRate = 0.05m,
                LineAmount = returnQty * 100m, TaxAmount = returnQty * 5m, TotalAmount = returnQty * 105m,
            }
        }
    };
}
