using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// PurchaseService 更新與刪除操作單元測試。
/// </summary>
public class PurchaseServiceUpdateTests : TestBase
{
    private readonly Mock<IDocNumberService> _mockDocNo;
    private readonly Mock<IDateTimeService>  _mockDt;
    private readonly Mock<IUomService>       _mockUom;
    private readonly PurchaseService         _service;
    private const string PlantId = "P001";
    private const string UserId  = "user1";
    private static readonly DateTime UtcNow = new(2025, 4, 1, 8, 0, 0, DateTimeKind.Utc);

    public PurchaseServiceUpdateTests()
    {
        _mockDocNo = new Mock<IDocNumberService>();
        _mockDt    = new Mock<IDateTimeService>();
        _mockUom   = new Mock<IUomService>();
        var mockCurrency = GetMockCurrencyService();
        _mockDt.Setup(d => d.Now).Returns(UtcNow);

        _mockUom.Setup(u => u.ConvertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string p, string f, string t, decimal q, CancellationToken ct) => q);
        _mockUom.Setup(u => u.RoundQtyAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string uom, decimal qty, CancellationToken ct) => qty);

        _service = new PurchaseService(_context, _mockDocNo.Object, _mockDt.Object, _mockUom.Object, mockCurrency.Object, new SmartFactory.MES.Modules.INV.Services.InventoryReservationService(_context));

        // 全域查詢過濾器需設定 CurrentPlantId，否則 IPlantEntity 查詢全部被過濾
        _context.CurrentPlantId = PlantId;

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

    // ── 庫存單位以品號主檔為準 ────────────────────────────────────────────────

    [Fact]
    public async Task CreatePurchaseOrder_明細送來的庫存單位過時_以品號主檔為準換算()
    {
        // 2026-10-05 回報：品號庫存單位由 g 改成 KG 後，轉入舊請購明細（仍記 g）的採購單
        // 存檔一直報「找不到單位換算 KG → g」。換算基準只能取主檔，不能取前端或舊單據。
        _mockUom.Setup(u => u.ConvertAsync("P001", "PC", "g", It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UomConversionNotFoundException("P001", "PC", "g"));
        _mockDocNo.Setup(d => d.NextAsync("PurchaseOrder", PlantId, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("PO-STALE-UOM");

        var po = await _service.CreatePurchaseOrderAsync(new PurchaseOrderFormVm
        {
            OrderDate = new DateOnly(2025, 3, 1), SupplierCode = "SUP001",
            Lines = new()
            {
                new PurchaseOrderLineVm
                {
                    ProductCode = "P001", OrderQty = 5, UomCode = "PC", StockUomCode = "g",
                    UnitPrice = 1, CurrencyCode = "TWD", ExchangeRate = 1,
                },
            },
        }, UserId, PlantId);

        var line = Assert.Single(po.Lines);
        Assert.Equal("PC", line.StockUomCode);
        Assert.Equal(5, line.StockQty);
    }

    // ── PurchaseRequest Update ────────────────────────────────────────────────

    [Fact]
    public async Task UpdatePurchaseRequest_ShouldPersistChanges()
    {
        var pr = new PurchaseRequest
        {
            PurchaseRequestId = "PR2025000001",
            RequestDate = new DateOnly(2025, 3, 1),
            RequestedBy = UserId,
            Remarks = "original",
            PlantId = PlantId,
        };
        pr.Lines.Add(new PurchaseRequestLine
        {
            PurchaseRequestId = "PR2025000001", LineNo = 1,
            ProductCode = "P001", RequestQty = 10, UomCode = "PC",
            StockQty = 10, StockUomCode = "PC",
        });
        _context.PurchaseRequests.Add(pr);
        await _context.SaveChangesAsync();

        var vm = new PurchaseRequestFormVm
        {
            PurchaseRequestId = "PR2025000001",
            RequestDate = new DateOnly(2025, 4, 1),
            RequestedBy = UserId,
            Remarks = "updated",
            Lines = new List<PurchaseRequestLineVm>
            {
                new() { LineNo = 1, ProductCode = "P001", RequestQty = 20, UomCode = "PC", StockQty = 20, StockUomCode = "PC" },
                new() { LineNo = 2, ProductCode = "P001", RequestQty = 5,  UomCode = "PC", StockQty = 5,  StockUomCode = "PC" },
            }
        };

        var result = await _service.UpdatePurchaseRequestAsync(vm, UserId);

        Assert.Equal("updated", result.Remarks);
        Assert.Equal(new DateOnly(2025, 4, 1), result.RequestDate);
        Assert.Equal(2, result.Lines.Count);

        var line1 = result.Lines.First(l => l.LineNo == 1);
        Assert.Equal(20m, line1.RequestQty);
    }

    [Fact]
    public async Task UpdatePurchaseRequest_ShouldPreserveOrderedLines()
    {
        // 明細已有 OrderedQty，不應被刪除
        var pr = new PurchaseRequest
        {
            PurchaseRequestId = "PR2025000002",
            RequestDate = new DateOnly(2025, 3, 1),
            RequestedBy = UserId,
            PlantId = PlantId,
        };
        pr.Lines.Add(new PurchaseRequestLine
        {
            PurchaseRequestId = "PR2025000002", LineNo = 1,
            ProductCode = "P001", RequestQty = 10, OrderedQty = 5, UomCode = "PC",
            StockQty = 10, StockUomCode = "PC",
        });
        _context.PurchaseRequests.Add(pr);
        await _context.SaveChangesAsync();

        var vm = new PurchaseRequestFormVm
        {
            PurchaseRequestId = "PR2025000002",
            RequestDate = new DateOnly(2025, 4, 1),
            RequestedBy = UserId,
            Lines = new List<PurchaseRequestLineVm>
            {
                // Line 1 submitted — include it so it's updated, not removed
                new() { LineNo = 1, ProductCode = "P001", RequestQty = 10, UomCode = "PC", StockQty = 10, StockUomCode = "PC" },
            }
        };

        var result = await _service.UpdatePurchaseRequestAsync(vm, UserId);

        var inDb = await _context.PurchaseRequests.Include(p => p.Lines).FirstAsync(p => p.PurchaseRequestId == "PR2025000002");
        Assert.Single(inDb.Lines);
        Assert.Equal(5m, inDb.Lines.First().OrderedQty); // OrderedQty preserved
    }

    // ── PurchaseOrder Update ──────────────────────────────────────────────────

    [Fact]
    public async Task UpdatePurchaseOrder_ShouldRestoreAndUpdatePrOrderedQty()
    {
        // 建 PR
        var pr = new PurchaseRequest
        {
            PurchaseRequestId = "PR2025000010",
            RequestDate = new DateOnly(2025, 3, 1),
            RequestedBy = UserId, PlantId = PlantId,
        };
        pr.Lines.Add(new PurchaseRequestLine
        {
            PurchaseRequestId = "PR2025000010", LineNo = 1,
            ProductCode = "P001", RequestQty = 20, OrderedQty = 10, UomCode = "PC",
        });
        _context.PurchaseRequests.Add(pr);

        // 建 PO (已存在，明細 OrderedQty=10)
        var po = new PurchaseOrder
        {
            PurchaseOrderId = "PO2025000001",
            OrderDate = new DateOnly(2025, 3, 1),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 1000, PlantId = PlantId,
        };
        po.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO2025000001", LineNo = 1,
            ProductCode = "P001", OrderQty = 10, ReceivedQty = 0,
            UomCode = "PC", StockQty = 10, StockUomCode = "PC",
            UnitPrice = 100, CurrencyCode = "TWD", ExchangeRate = 1,
            TaxRate = 0, LineAmount = 1000, TaxAmount = 0, TotalAmount = 1000,
            PurchaseRequestId = "PR2025000010", PrLineNo = 1,
        });
        _context.PurchaseOrders.Add(po);
        await _context.SaveChangesAsync();

        // 更新 PO，改為 OrderQty = 15
        var vm = new PurchaseOrderFormVm
        {
            PurchaseOrderId = "PO2025000001",
            OrderDate = new DateOnly(2025, 4, 1),
            SupplierCode = "SUP001",
            Lines = new List<PurchaseOrderLineVm>
            {
                new()
                {
                    LineNo = 1, ProductCode = "P001",
                    PurchaseRequestId = "PR2025000010", PrLineNo = 1,
                    OrderQty = 15, UomCode = "PC", StockQty = 15, StockUomCode = "PC",
                    UnitPrice = 100, CurrencyCode = "TWD", ExchangeRate = 1,
                    TaxRate = 0, LineAmount = 1500, TaxAmount = 0, TotalAmount = 1500,
                }
            }
        };

        await _service.UpdatePurchaseOrderAsync(vm, UserId);

        var prLine = await _context.PurchaseRequestLines.FindAsync("PR2025000010", 1);
        Assert.Equal(15m, prLine!.OrderedQty);
    }

    /// <summary>
    /// 修改採購單後，表頭金額與明細集合只能反映這次送出的明細。
    ///
    /// 守的是 <c>RemoveRange</c> 殘留的另一種寫法：本檔用的不是 INV/ORD 的
    /// <c>Lines.Clear()</c>，而是整個換掉集合（<c>po.Lines = 新集合</c>）——
    /// 規則 R16 兩種都放行，但只有動態測試能證明換掉的那份真的沒帶到舊行。
    ///
    /// 這裡的代價是金額而不是庫存：緊接在重建之後的
    /// <c>po.TotalAmount = po.Lines.Sum(l =&gt; l.AmountLocal)</c> 直接吃這個集合，
    /// 殘留舊行會讓採購金額憑空灌水，而該金額一路流進三方對帳與採購金額報表。
    /// </summary>
    [Fact]
    public async Task UpdatePurchaseOrder_EditReplacesLines_TotalAmountExcludesStaleLines()
    {
        var po = new PurchaseOrder
        {
            PurchaseOrderId = "PO2025000010",
            OrderDate = new DateOnly(2025, 3, 1),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 1000, PlantId = PlantId,
        };
        po.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO2025000010", LineNo = 1,
            ProductCode = "P001", OrderQty = 10, ReceivedQty = 0,
            UomCode = "PC", StockQty = 10, StockUomCode = "PC",
            UnitPrice = 100, CurrencyCode = "TWD", ExchangeRate = 1,
            TaxRate = 0, LineAmount = 1000, TaxAmount = 0, TotalAmount = 1000,
            AmountLocal = 1000,
        });
        _context.PurchaseOrders.Add(po);
        await _context.SaveChangesAsync();

        // 第 1 行由 10 改成 4，另外加一行 7：新合計 1100。
        var vm = new PurchaseOrderFormVm
        {
            PurchaseOrderId = "PO2025000010",
            OrderDate = new DateOnly(2025, 4, 1),
            SupplierCode = "SUP001",
            Lines = new List<PurchaseOrderLineVm>
            {
                new()
                {
                    LineNo = 1, ProductCode = "P001",
                    OrderQty = 4, UomCode = "PC", StockQty = 4, StockUomCode = "PC",
                    UnitPrice = 100, CurrencyCode = "TWD", ExchangeRate = 1,
                    TaxRate = 0, LineAmount = 400, TaxAmount = 0, TotalAmount = 400,
                },
                new()
                {
                    LineNo = 2, ProductCode = "P001",
                    OrderQty = 7, UomCode = "PC", StockQty = 7, StockUomCode = "PC",
                    UnitPrice = 100, CurrencyCode = "TWD", ExchangeRate = 1,
                    TaxRate = 0, LineAmount = 700, TaxAmount = 0, TotalAmount = 700,
                },
            }
        };

        var result = await _service.UpdatePurchaseOrderAsync(vm, UserId);

        // 殘留舊行的話會多出一筆 AmountLocal=1000，合計變成 2100。
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(new[] { 1, 2 }, result.Lines.Select(l => l.LineNo).OrderBy(n => n).ToArray());
        Assert.Equal(1100m, result.TotalAmount);
        Assert.Equal(1100m, result.Lines.Sum(l => l.AmountLocal));
    }

    [Fact]
    public async Task UpdatePurchaseOrder_WithReceivedLines_ShouldThrow()
    {
        var po = new PurchaseOrder
        {
            PurchaseOrderId = "PO2025000002",
            OrderDate = new DateOnly(2025, 3, 1),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 0, PlantId = PlantId,
        };
        po.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO2025000002", LineNo = 1,
            ProductCode = "P001", OrderQty = 10, ReceivedQty = 5, // 已進貨
            UomCode = "PC", StockQty = 10, StockUomCode = "PC",
            UnitPrice = 0, CurrencyCode = "TWD", ExchangeRate = 1,
            TaxRate = 0, LineAmount = 0, TaxAmount = 0, TotalAmount = 0,
        });
        _context.PurchaseOrders.Add(po);
        await _context.SaveChangesAsync();

        var vm = new PurchaseOrderFormVm
        {
            PurchaseOrderId = "PO2025000002",
            OrderDate = new DateOnly(2025, 4, 1),
            SupplierCode = "SUP001",
            Lines = new List<PurchaseOrderLineVm>()
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.UpdatePurchaseOrderAsync(vm, UserId));
    }

    // ── PurchaseOrder Delete ──────────────────────────────────────────────────

    [Fact]
    public async Task DeletePurchaseOrder_WithNoReceivedLines_ShouldDelete()
    {
        var pr = new PurchaseRequest
        {
            PurchaseRequestId = "PR2025000020",
            RequestDate = new DateOnly(2025, 3, 1),
            RequestedBy = UserId, PlantId = PlantId,
        };
        pr.Lines.Add(new PurchaseRequestLine
        {
            PurchaseRequestId = "PR2025000020", LineNo = 1,
            ProductCode = "P001", RequestQty = 10, OrderedQty = 10, UomCode = "PC",
        });
        _context.PurchaseRequests.Add(pr);

        var po = new PurchaseOrder
        {
            PurchaseOrderId = "PO2025000010",
            OrderDate = new DateOnly(2025, 3, 1),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 0, PlantId = PlantId,
        };
        po.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO2025000010", LineNo = 1,
            ProductCode = "P001", OrderQty = 10, ReceivedQty = 0,
            UomCode = "PC", StockQty = 10, StockUomCode = "PC",
            UnitPrice = 0, CurrencyCode = "TWD", ExchangeRate = 1,
            TaxRate = 0, LineAmount = 0, TaxAmount = 0, TotalAmount = 0,
            PurchaseRequestId = "PR2025000020", PrLineNo = 1,
        });
        _context.PurchaseOrders.Add(po);
        await _context.SaveChangesAsync();

        await _service.DeletePurchaseOrderAsync("PO2025000010");

        Assert.Null(await _context.PurchaseOrders.FindAsync("PO2025000010"));

        // PR 已轉採購量應還原為 0
        var prLine = await _context.PurchaseRequestLines.FindAsync("PR2025000020", 1);
        Assert.Equal(0m, prLine!.OrderedQty);
    }

    [Fact]
    public async Task DeletePurchaseOrder_WithReceivedLines_ShouldThrow()
    {
        var po = new PurchaseOrder
        {
            PurchaseOrderId = "PO2025000011",
            OrderDate = new DateOnly(2025, 3, 1),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 0, PlantId = PlantId,
        };
        po.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO2025000011", LineNo = 1,
            ProductCode = "P001", OrderQty = 10, ReceivedQty = 5,
            UomCode = "PC", StockQty = 10, StockUomCode = "PC",
            UnitPrice = 0, CurrencyCode = "TWD", ExchangeRate = 1,
            TaxRate = 0, LineAmount = 0, TaxAmount = 0, TotalAmount = 0,
        });
        _context.PurchaseOrders.Add(po);
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeletePurchaseOrderAsync("PO2025000011"));
    }

    // ── GoodsReceipt Update ───────────────────────────────────────────────────

    [Fact]
    public async Task UpdateGoodsReceipt_ShouldReplaceLines()
    {
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = "PE2025000001",
            ReceiptDate = new DateOnly(2025, 3, 1),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 500, PlantId = PlantId,
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = "PE2025000001", LineNo = 1,
            ProductCode = "P001", ReceiptQty = 5, UomCode = "PC",
            StockQty = 5, StockUomCode = "PC",
            TotalAmount = 500,
        });
        _context.GoodsReceipts.Add(gr);
        await _context.SaveChangesAsync();

        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), default))
                  .ReturnsAsync("PE2025000099");

        var vm = new GoodsReceiptFormVm
        {
            GoodsReceiptId = "PE2025000001",
            ReceiptDate    = new DateOnly(2025, 4, 1),
            SupplierCode   = "SUP001",
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

        var result = await _service.UpdateGoodsReceiptAsync(vm, UserId);

        Assert.Equal("PE2025000001", result.GoodsReceiptId);
        Assert.Single(result.Lines);
        Assert.Equal(1050m, result.TotalAmount);
    }

    // ── GoodsReturn Delete ────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteGoodsReturn_ShouldRemoveGoodsReturn()
    {
        var ret = new GoodsReturn
        {
            GoodsReturnId = "PF2025000001",
            ReturnDate = new DateOnly(2025, 3, 27),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 315, PlantId = PlantId,
        };
        ret.Lines.Add(new GoodsReturnLine
        {
            GoodsReturnId = "PF2025000001", LineNo = 1,
            ProductCode = "P001", ReturnQty = 3, UomCode = "PC",
            StockQty = 3, StockUomCode = "PC",
            TotalAmount = 315,
        });
        _context.GoodsReturns.Add(ret);
        await _context.SaveChangesAsync();

        await _service.DeleteGoodsReturnAsync("PF2025000001");

        Assert.Null(await _context.GoodsReturns.FindAsync("PF2025000001"));
    }

    // ── DocStatus 門控測試 ───────────────────────────────────────────────────

    [Fact]
    public async Task UpdateGoodsReceipt_WhenConfirmed_ShouldThrow()
    {
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = "PE2025000100",
            ReceiptDate = new DateOnly(2025, 3, 1),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 0, PlantId = PlantId,
            Status = DocStatus.Confirmed, // 已確認
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = "PE2025000100", LineNo = 1,
            ProductCode = "P001", ReceiptQty = 10, UomCode = "PC",
            StockQty = 10, StockUomCode = "PC",
        });
        _context.GoodsReceipts.Add(gr);
        await _context.SaveChangesAsync();

        var vm = new GoodsReceiptFormVm
        {
            GoodsReceiptId = "PE2025000100",
            ReceiptDate = new DateOnly(2025, 4, 1),
            SupplierCode = "SUP001",
            Lines = new List<GoodsReceiptLineVm>()
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.UpdateGoodsReceiptAsync(vm, UserId));
    }

    [Fact]
    public async Task DeleteGoodsReceipt_WhenConfirmed_ShouldThrow()
    {
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = "PE2025000101",
            ReceiptDate = new DateOnly(2025, 3, 1),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 0, PlantId = PlantId,
            Status = DocStatus.Confirmed,
        };
        _context.GoodsReceipts.Add(gr);
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeleteGoodsReceiptAsync("PE2025000101"));
    }

    [Fact]
    public async Task DeleteGoodsReceipt_WithQtyPutAway_ShouldThrow()
    {
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = "PE2025000102",
            ReceiptDate = new DateOnly(2025, 3, 1),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 0, PlantId = PlantId,
            Status = DocStatus.Draft,
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = "PE2025000102", LineNo = 1,
            ProductCode = "P001", ReceiptQty = 10, UomCode = "PC",
            StockQty = 10, StockUomCode = "PC",
            QtyPutAway = 5, // 已有倉庫作業
        });
        _context.GoodsReceipts.Add(gr);
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeleteGoodsReceiptAsync("PE2025000102"));
    }

    [Fact]
    public async Task UpdateGoodsReceipt_DeleteLineWithQtyPutAway_ShouldThrow()
    {
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = "PE2025000103",
            ReceiptDate = new DateOnly(2025, 3, 1),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 0, PlantId = PlantId,
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = "PE2025000103", LineNo = 1,
            ProductCode = "P001", ReceiptQty = 10, UomCode = "PC",
            StockQty = 10, StockUomCode = "PC",
            QtyPutAway = 5,
        });
        _context.GoodsReceipts.Add(gr);
        await _context.SaveChangesAsync();

        // 嘗試提交不含 LineNo=1 的 VM（等於刪除該行）
        var vm = new GoodsReceiptFormVm
        {
            GoodsReceiptId = "PE2025000103",
            ReceiptDate = new DateOnly(2025, 4, 1),
            SupplierCode = "SUP001",
            Lines = new List<GoodsReceiptLineVm>()
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.UpdateGoodsReceiptAsync(vm, UserId));
    }

    [Fact]
    public async Task UpdateGoodsReturn_WhenConfirmed_ShouldThrow()
    {
        var ret = new GoodsReturn
        {
            GoodsReturnId = "PF2025000100",
            ReturnDate = new DateOnly(2025, 3, 27),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 0, PlantId = PlantId,
            Status = DocStatus.Confirmed,
        };
        _context.GoodsReturns.Add(ret);
        await _context.SaveChangesAsync();

        var vm = new GoodsReturnFormVm
        {
            GoodsReturnId = "PF2025000100",
            ReturnDate = new DateOnly(2025, 4, 1),
            SupplierCode = "SUP001",
            Lines = new List<GoodsReturnLineVm>()
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.UpdateGoodsReturnAsync(vm, UserId));
    }

    [Fact]
    public async Task DeleteGoodsReturn_WhenPosted_ShouldThrow()
    {
        var ret = new GoodsReturn
        {
            GoodsReturnId = "PF2025000101",
            ReturnDate = new DateOnly(2025, 3, 27),
            CreatedBy = UserId, SupplierCode = "SUP001",
            TotalAmount = 0, PlantId = PlantId,
            Status = DocStatus.Posted,
        };
        _context.GoodsReturns.Add(ret);
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeleteGoodsReturnAsync("PF2025000101"));
    }
}
