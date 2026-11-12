using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// LandedCostService.ConfirmAsync 整合測試（策略 a：只調在庫餘量均價）。
/// 驗證在庫資本化 / 已出庫轉費用（PPV）/ 無在庫全費用化 / FIFO 成本層同步。
/// </summary>
public class LandedCostServiceTests : TestBase
{
    private readonly LandedCostService _svc;
    private readonly Mock<IJournalEntryService> _journal = new();
    private readonly List<IList<JournalLineRequest>> _postedJournals = new();
    private const string PlantId     = "P001";
    private const string ProductCode = "MAT001";
    private const string LocationCode = "A-01";

    public LandedCostServiceTests()
    {
        _context.CurrentPlantId = PlantId;
        _journal
            .Setup(j => j.CreateAutoEntryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<string>(),
                It.IsAny<IList<JournalLineRequest>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, DateOnly, string, IList<JournalLineRequest>, string?, CancellationToken>(
                (_, _, _, _, lines, _, _) => _postedJournals.Add(lines))
            .ReturnsAsync(new JournalEntry());
        _svc = new LandedCostService(_context, _journal.Object);
    }

    private async Task SeedInventoryAsync(decimal qtyOnHand, decimal unitCost)
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = ProductCode, LocationCode = LocationCode, LotNo = "",
            QtyOnHand = qtyOnHand, UnitCost = unitCost,
        });
        await _context.SaveChangesAsync();
    }

    private async Task<string> SeedAllocationAsync(decimal total, decimal receiptQty,
        decimal purchaseAmount, decimal originalUnitCost, LandedCostBasis basis = LandedCostBasis.Amount)
    {
        var alloc = new LandedCostAllocation
        {
            LandedCostAllocationId = "LCA-2606-00001",
            PlantId = PlantId, DocDate = new DateOnly(2026, 6, 11),
            Basis = basis, TotalLandedCost = total, Status = DocStatus.Draft,
            CreatedBy = "tester",
            Lines =
            {
                new LandedCostAllocationLine
                {
                    LineNo = 1, GoodsReceiptId = "GR-2606-00001", GoodsReceiptLineNo = 1,
                    ProductCode = ProductCode, LocationCode = LocationCode, LotNo = "",
                    ReceiptQty = receiptQty, PurchaseAmount = purchaseAmount,
                    Weight = receiptQty, OriginalUnitCost = originalUnitCost,
                },
            },
        };
        _context.LandedCostAllocations.Add(alloc);
        await _context.SaveChangesAsync();
        return alloc.LandedCostAllocationId;
    }

    [Fact]
    public async Task Confirm_FullyOnHand_CapitalizesAllAndRaisesAvco()
    {
        await SeedInventoryAsync(qtyOnHand: 100m, unitCost: 10m);
        var id = await SeedAllocationAsync(total: 1000m, receiptQty: 100m, purchaseAmount: 1000m, originalUnitCost: 10m);

        await _svc.ConfirmAsync(id);

        var line = _context.LandedCostAllocationLines.Single();
        Assert.Equal(1000m, line.AllocatedCost);
        Assert.Equal(1000m, line.CapitalizedCost);
        Assert.Equal(0m, line.ExpensedCost);
        Assert.Equal(20m, line.NewUnitCost);   // 10 + 1000/100

        var inv = _context.Inventories.Single();
        Assert.Equal(20m, inv.UnitCost);
    }

    [Fact]
    public async Task Confirm_HalfIssued_SplitsCapitalizedAndExpensed()
    {
        await SeedInventoryAsync(qtyOnHand: 50m, unitCost: 10m);   // 一半已出庫
        var id = await SeedAllocationAsync(total: 1000m, receiptQty: 100m, purchaseAmount: 1000m, originalUnitCost: 10m);

        await _svc.ConfirmAsync(id);

        var line = _context.LandedCostAllocationLines.Single();
        Assert.Equal(500m, line.CapitalizedCost);  // 1000 × 50/100
        Assert.Equal(500m, line.ExpensedCost);     // 已出庫對應 → PPV
        Assert.Equal(20m, line.NewUnitCost);       // 10 + 500/50

        Assert.Equal(20m, _context.Inventories.Single().UnitCost);
    }

    [Fact]
    public async Task Confirm_NoInventory_AllExpensed_NoCostChange()
    {
        // 不種庫存（全數已出庫）
        var id = await SeedAllocationAsync(total: 1000m, receiptQty: 100m, purchaseAmount: 1000m, originalUnitCost: 10m);

        await _svc.ConfirmAsync(id);

        var line = _context.LandedCostAllocationLines.Single();
        Assert.Equal(0m, line.CapitalizedCost);
        Assert.Equal(1000m, line.ExpensedCost);
        Assert.Equal(10m, line.NewUnitCost);       // 維持原值
        Assert.Empty(_context.Inventories);
    }

    [Fact]
    public async Task Confirm_WithFifoLayers_BumpsRemainingLayersPerUnit()
    {
        await SeedInventoryAsync(qtyOnHand: 100m, unitCost: 10m);
        _context.InventoryCostLayers.AddRange(
            new InventoryCostLayer { PlantId = PlantId, ProductCode = ProductCode, LocationCode = LocationCode, LotNo = "",
                ReceiptDate = new DateOnly(2026, 6, 1), SourceDocNo = "GR-1", QtyRemaining = 60m, UnitCost = 10m },
            new InventoryCostLayer { PlantId = PlantId, ProductCode = ProductCode, LocationCode = LocationCode, LotNo = "",
                ReceiptDate = new DateOnly(2026, 6, 2), SourceDocNo = "GR-2", QtyRemaining = 40m, UnitCost = 10m });
        await _context.SaveChangesAsync();

        var id = await SeedAllocationAsync(total: 1000m, receiptQty: 100m, purchaseAmount: 1000m, originalUnitCost: 10m);
        await _svc.ConfirmAsync(id);

        // 每單位資本化 = 1000 / 100 = 10；各層 10 → 20
        foreach (var layer in _context.InventoryCostLayers.ToList())
            Assert.Equal(20m, layer.UnitCost);
    }

    [Fact]
    public async Task Confirm_NonDraft_Throws()
    {
        await SeedInventoryAsync(100m, 10m);
        var id = await SeedAllocationAsync(1000m, 100m, 1000m, 10m);
        await _svc.ConfirmAsync(id);   // → Confirmed

        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.ConfirmAsync(id));
    }

    // ── JournalEntry 過帳（§3.4 / §4.4）──────────────────────────────────────

    [Fact]
    public async Task Confirm_FullyOnHand_PostsCapitalizedAndAccruedFreight()
    {
        await SeedInventoryAsync(qtyOnHand: 100m, unitCost: 10m);
        var id = await SeedAllocationAsync(total: 1000m, receiptQty: 100m, purchaseAmount: 1000m, originalUnitCost: 10m);

        await _svc.ConfirmAsync(id);

        var lines = Assert.Single(_postedJournals);
        // 全在庫：借 原料存貨 1000 / 貸 暫估應付運費 1000（無 PPV 行）
        Assert.Equal(2, lines.Count);
        var debitInv = lines.Single(l => l.AccountCode == "1401");
        Assert.Equal(1000m, debitInv.Debit);
        Assert.DoesNotContain(lines, l => l.AccountCode == "5151");
        var credit = lines.Single(l => l.AccountCode == "2153");
        Assert.Equal(1000m, credit.Credit);
        // 借貸平衡
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
    }

    [Fact]
    public async Task Confirm_HalfIssued_PostsCapitalizedAndPpvAndAccruedFreight()
    {
        await SeedInventoryAsync(qtyOnHand: 50m, unitCost: 10m);
        var id = await SeedAllocationAsync(total: 1000m, receiptQty: 100m, purchaseAmount: 1000m, originalUnitCost: 10m);

        await _svc.ConfirmAsync(id);

        var lines = Assert.Single(_postedJournals);
        Assert.Equal(3, lines.Count);
        Assert.Equal(500m, lines.Single(l => l.AccountCode == "1401").Debit);  // 資本化
        Assert.Equal(500m, lines.Single(l => l.AccountCode == "5151").Debit);  // PPV
        Assert.Equal(1000m, lines.Single(l => l.AccountCode == "2153").Credit); // GR/IR
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
    }

    [Fact]
    public async Task Confirm_NoInventory_PostsAllExpensedAsPpv()
    {
        var id = await SeedAllocationAsync(total: 1000m, receiptQty: 100m, purchaseAmount: 1000m, originalUnitCost: 10m);

        await _svc.ConfirmAsync(id);

        var lines = Assert.Single(_postedJournals);
        // 全數已出庫：借 進料費用差異 1000 / 貸 暫估應付運費 1000（無存貨行）
        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain(lines, l => l.AccountCode == "1401");
        Assert.Equal(1000m, lines.Single(l => l.AccountCode == "5151").Debit);
        Assert.Equal(1000m, lines.Single(l => l.AccountCode == "2153").Credit);
    }

    [Fact]
    public async Task Confirm_ZeroLandedCost_DoesNotPost()
    {
        await SeedInventoryAsync(qtyOnHand: 100m, unitCost: 10m);
        var id = await SeedAllocationAsync(total: 0m, receiptQty: 100m, purchaseAmount: 1000m, originalUnitCost: 10m);

        await _svc.ConfirmAsync(id);

        Assert.Empty(_postedJournals);
    }

    // ── 2026-10-23：外幣換算與同料多行合併比例 ─────────────────────────────

    private LandedCostService SvcWithRate(decimal? rate)
    {
        var currency = new Mock<ICurrencyService>();
        currency.Setup(c => c.GetRateAsync("USD", It.IsAny<DateOnly>(), RateType.Buy)).ReturnsAsync(rate);
        return new LandedCostService(_context, _journal.Object, currency.Object);
    }

    [Fact]
    public async Task Confirm_ForeignCurrency_CapitalizesAndPostsInLocalAtBuyRate()
    {
        await SeedInventoryAsync(qtyOnHand: 100m, unitCost: 10m);
        var id = await SeedAllocationAsync(total: 100m, receiptQty: 100m, purchaseAmount: 1000m, originalUnitCost: 10m);
        _context.LandedCostAllocations.Single().CurrencyCode = "USD";
        await _context.SaveChangesAsync();

        await SvcWithRate(32m).ConfirmAsync(id);

        Assert.Equal(42m, _context.Inventories.Single().UnitCost);        // 10 + 100×32/100
        var line = _context.LandedCostAllocationLines.Single();
        Assert.Equal(100m, line.CapitalizedCost);                          // 明細維持單據幣別
        var lines = _postedJournals.Single();
        Assert.Equal(3200m, lines.Single(l => l.AccountCode == "1401").Debit);
        var accrued = lines.Single(l => l.AccountCode == "2153");
        Assert.Equal(3200m, accrued.Credit);
        Assert.Equal(100m, accrued.Amount);
        Assert.Equal(32m, accrued.ExchangeRate);
    }

    [Fact]
    public async Task Confirm_ForeignCurrencyWithoutRate_Throws()
    {
        await SeedInventoryAsync(qtyOnHand: 100m, unitCost: 10m);
        var id = await SeedAllocationAsync(total: 100m, receiptQty: 100m, purchaseAmount: 1000m, originalUnitCost: 10m);
        _context.LandedCostAllocations.Single().CurrencyCode = "USD";
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SvcWithRate(null).ConfirmAsync(id));
        Assert.Equal(10m, _context.Inventories.Single().UnitCost);
        Assert.Empty(_postedJournals);
    }

    /// <summary>
    /// 兩張進貨各 100、在手只剩 100：逐行算比例會兩行都當全數在庫，兩份費用都資本化進那 100 個。
    /// 以兩行合計 200 為分母，各行比例 1/2。
    /// </summary>
    [Fact]
    public async Task Confirm_TwoReceiptsSameProduct_RatioUsesCombinedReceiptQty()
    {
        await SeedInventoryAsync(qtyOnHand: 100m, unitCost: 10m);
        var id = await SeedAllocationAsync(total: 1000m, receiptQty: 100m, purchaseAmount: 1000m, originalUnitCost: 10m);
        _context.LandedCostAllocationLines.Add(new LandedCostAllocationLine
        {
            LandedCostAllocationId = id, LineNo = 2, GoodsReceiptId = "GR-2606-00002", GoodsReceiptLineNo = 1,
            ProductCode = ProductCode, LocationCode = LocationCode, LotNo = "",
            ReceiptQty = 100m, PurchaseAmount = 1000m, Weight = 100m, OriginalUnitCost = 10m,
        });
        await _context.SaveChangesAsync();

        await _svc.ConfirmAsync(id);

        var lines = _context.LandedCostAllocationLines.OrderBy(l => l.LineNo).ToList();
        Assert.All(lines, l => Assert.Equal(250m, l.CapitalizedCost));   // 各分 500 × 1/2
        Assert.All(lines, l => Assert.Equal(250m, l.ExpensedCost));
        Assert.Equal(15m, _context.Inventories.Single().UnitCost);         // 10 + 500/100
    }

    // ── 2026-10-23：作廢回沖 ─────────────────────────────────────────────

    [Fact]
    public async Task Void_Confirmed_TakesCapitalizedCostBackOut()
    {
        await SeedInventoryAsync(qtyOnHand: 50m, unitCost: 10m);
        var id = await SeedAllocationAsync(total: 1000m, receiptQty: 100m, purchaseAmount: 1000m, originalUnitCost: 10m);
        await _svc.ConfirmAsync(id);
        Assert.Equal(20m, _context.Inventories.Single().UnitCost);    // 10 + 500/50

        await _svc.VoidAsync(id, new DateOnly(2026, 6, 20), "金額輸錯");

        Assert.Equal(10m, _context.Inventories.Single().UnitCost);
        var alloc = _context.LandedCostAllocations.Single();
        Assert.Equal(DocStatus.Void, alloc.Status);
        Assert.Contains("金額輸錯", alloc.Remarks);
    }

    [Fact]
    public async Task Void_AfterStockIssued_IsRejectedAndChangesNothing()
    {
        await SeedInventoryAsync(qtyOnHand: 100m, unitCost: 10m);
        var id = await SeedAllocationAsync(total: 1000m, receiptQty: 100m, purchaseAmount: 1000m, originalUnitCost: 10m);
        await _svc.ConfirmAsync(id);
        _context.StockMovements.Add(new StockMovement
        {
            TypeCode = "GS", TypeCategory = "Sales", DocNo = "GS-1", DocLineNo = 1, PlantId = PlantId,
            ProductCode = ProductCode, LocationCode = LocationCode, LotNo = "", Qty = 10m, StockQty = 10m,
            Direction = -1, UnitCost = 20m, TotalCost = 200m, MovementDate = new DateOnly(2026, 6, 15),
            CreatedAt = DateTime.UtcNow.AddMinutes(1),
        });
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.VoidAsync(id, new DateOnly(2026, 6, 20), null));
        Assert.Equal(20m, _context.Inventories.Single().UnitCost);
        Assert.Equal(DocStatus.Confirmed, _context.LandedCostAllocations.Single().Status);
    }

    [Fact]
    public async Task Void_Draft_Throws()
    {
        var id = await SeedAllocationAsync(total: 1000m, receiptQty: 100m, purchaseAmount: 1000m, originalUnitCost: 10m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.VoidAsync(id, new DateOnly(2026, 6, 20), null));
    }
}
