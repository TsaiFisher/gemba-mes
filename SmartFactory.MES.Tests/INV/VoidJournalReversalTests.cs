using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// 過帳單據作廢的總帳回沖。
///
/// 先前 VoidDocAsync 只補反向 StockMovement、完全不碰總帳，已過帳單據作廢後
/// GL 的存貨與 GR-IR 科目永久高估；而 §3.6 存貨滾動稽核以庫存異動為基礎計算，
/// 抓不到這個差額——沒有任何一層會發現，故補上「過帳→作廢→GL 淨額歸零」的守門。
///
/// 用真的 JournalEntryService（非 mock）：要驗的正是「寫進 DB 的分錄」本身，
/// mock 掉就只剩「有沒有呼叫」，測不出金額與借貸方向。
/// </summary>
public class VoidJournalReversalTests : TestBase
{
    private const string PlantId = "P001";
    private static readonly DateOnly PostDate = new(2026, 3, 1);
    private static readonly DateOnly VoidDate = new(2026, 3, 20);

    private readonly InventoryPostingService _postingSvc;
    private readonly Mock<IDateTimeService>  _mockDt = new();
    private int _journalSeq;

    public VoidJournalReversalTests()
    {
        _context.CurrentPlantId = PlantId;

        var mockDocNo = new Mock<IDocNumberService>();
        mockDocNo.Setup(d => d.NextAsync("JournalEntry", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult($"JRN{++_journalSeq:D6}"));

        // 作廢沖銷以「作廢當下日期」入帳，與過帳日不同，才驗得出日期取用是否正確
        _mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(VoidDate);

        var journalSvc = new JournalEntryService(
            _context, mockDocNo.Object, new AccountingPeriodService(_context));

        _postingSvc = new InventoryPostingService(
            _context, new InventoryService(_context),
            new Mock<IInventoryCostLayerService>().Object,
            new Mock<ISystemParamService>().Object,
            _mockDt.Object,
            journalSvc: journalSvc);

        SeedCommonData();
    }

    private void SeedCommonData()
    {
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "原料倉", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "LOC1", LocationName = "LOC1" });
        _context.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "RAW", CategoryName = "原料",
            CostMethod = CostMethod.MovingAverage, IsActive = true
        });
        _context.Products.Add(new Product { ProductCode = "MAT-A", CategoryCode = "RAW", IsActive = true });
        _context.SaveChanges();
    }

    private PostLineDetailRequest InboundReq(string docNo, decimal qty, decimal unitPrice)
        => new()
        {
            PlantId      = PlantId,
            DocNo        = docNo,
            LineNo       = 1,
            ProductCode  = "MAT-A",
            LocationCode = "LOC1",
            Qty          = qty,
            TypeCode     = "PI",
            TypeCategory = "Purchase",
            MovementDate = PostDate,
            UomCode      = "PC",
            StockUomCode = "PC",
            UnitPrice    = unitPrice,
            UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
            CheckAllLinesCompleteAsync = () => Task.FromResult(false),
            SetDocPosted               = () => { },
        };

    private async Task<bool> VoidAsync(string docNo, string? reason = "驗收退回")
    {
        var voided = false;
        await _postingSvc.VoidDocAsync(new VoidDocRequest
        {
            PlantId    = PlantId,
            DocNo      = docNo,
            Reason     = reason,
            CreatedBy  = "tester",
            SetDocVoid = () => voided = true,
        });
        await _context.SaveChangesAsync();
        return voided;
    }

    private async Task<List<JournalLine>> LinesOfAsync(string docNo)
        => await _context.JournalEntries.IgnoreQueryFilters()
            .Where(j => j.SourceDocId == docNo)
            .SelectMany(j => j.Lines)
            .ToListAsync();

    // ════════════════════════════════════════════════════════════════════════

    /// <summary>作廢已過帳的進貨：GL 每個科目的借貸淨額必須歸零。</summary>
    [Fact]
    public async Task VoidPostedDoc_NetsEveryAccountToZero()
    {
        await _postingSvc.PostLineDetailAsync(InboundReq("GR-V01", qty: 100m, unitPrice: 25m));
        await _context.SaveChangesAsync();

        // 前提：過帳確實入了帳（否則後面的「歸零」會是假綠燈）
        var posted = await LinesOfAsync("GR-V01");
        Assert.NotEmpty(posted);
        Assert.Equal(2500m, posted.Where(l => l.AccountCode == StandardAccounts.RawMaterialInventoryCode)
                                   .Sum(l => l.Debit));

        Assert.True(await VoidAsync("GR-V01"));

        var all = await LinesOfAsync("GR-V01");
        foreach (var g in all.GroupBy(l => l.AccountCode))
            Assert.Equal(0m, g.Sum(l => l.Debit) - g.Sum(l => l.Credit));
    }

    /// <summary>反向分錄本身也必須借貸平衡，且以「作廢當下日期」入帳、另立來源單別。</summary>
    [Fact]
    public async Task VoidPostedDoc_ReversalEntryIsBalancedAndDatedAtVoidTime()
    {
        await _postingSvc.PostLineDetailAsync(InboundReq("GR-V02", qty: 10m, unitPrice: 300m));
        await _context.SaveChangesAsync();
        await VoidAsync("GR-V02");

        var reversal = await _context.JournalEntries.IgnoreQueryFilters()
            .Include(j => j.Lines)
            .SingleAsync(j => j.SourceDocType == "INV-Void" && j.SourceDocId == "GR-V02");

        Assert.Equal(VoidDate, reversal.EntryDate);
        Assert.Equal(reversal.Lines.Sum(l => l.Debit), reversal.Lines.Sum(l => l.Credit));
        // 原分錄借存貨 → 反向必須貸存貨
        Assert.Equal(3000m, reversal.Lines
            .Where(l => l.AccountCode == StandardAccounts.RawMaterialInventoryCode)
            .Sum(l => l.Credit));
    }

    /// <summary>重複作廢不得再產生第二筆反向分錄（否則反而變成反向高估）。</summary>
    [Fact]
    public async Task VoidTwice_DoesNotDoubleReverse()
    {
        await _postingSvc.PostLineDetailAsync(InboundReq("GR-V03", qty: 5m, unitPrice: 100m));
        await _context.SaveChangesAsync();

        await VoidAsync("GR-V03");
        // 第二次作廢：庫存已被沖回，可用量不足會擋下出庫方向的沖銷，
        // 此處為入庫單（沖銷方向為出庫），故先補回庫存讓第二次呼叫得以進行。
        await new InventoryService(_context)
            .UpdateQtyAsync(PlantId, "MAT-A", "LOC1", "", 5m);
        await _context.SaveChangesAsync();
        await VoidAsync("GR-V03");

        var reversalCount = await _context.JournalEntries.IgnoreQueryFilters()
            .CountAsync(j => j.SourceDocType == "INV-Void" && j.SourceDocId == "GR-V03");
        Assert.Equal(1, reversalCount);
    }

    /// <summary>沒有原分錄（如簡化模式過帳不入總帳）時，作廢不得憑空生出反向分錄。</summary>
    [Fact]
    public async Task VoidDocWithoutOriginalJournal_CreatesNoReversal()
    {
        _context.StockMovements.Add(new StockMovement
        {
            TypeCode = "PI", TypeCategory = "Purchase", DocNo = "GR-V04", DocLineNo = 1,
            MovementDate = PostDate, PlantId = PlantId, ProductCode = "MAT-A",
            LocationCode = "LOC1", LotNo = "", UomCode = "PC", StockUomCode = "PC",
            Qty = 3m, StockQty = 3m, Direction = 1, UnitCost = 50m, TotalCost = 150m,
            CreatedBy = "tester",
        });
        await new InventoryService(_context).UpdateQtyAsync(PlantId, "MAT-A", "LOC1", "", 3m);
        await _context.SaveChangesAsync();
        // 庫存單價與異動一致（真實過帳路徑會這樣寫）；不一致的話作廢會被「收光時價值對不上」的保護擋下
        foreach (var inv in _context.Inventories.Where(i => i.ProductCode == "MAT-A")) inv.UnitCost = 50m;
        await _context.SaveChangesAsync();

        await VoidAsync("GR-V04");

        Assert.Empty(await LinesOfAsync("GR-V04"));
    }
}
