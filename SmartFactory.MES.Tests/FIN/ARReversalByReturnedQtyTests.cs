using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.FIN;

/// <summary>
/// 銷貨退回折讓（方案：負向回沖原發票）：ReverseForReturnedQtyAsync 依退回量扣減
/// 來源出貨單應收發票「對應行號」的明細 Qty（下限 0），重算稅/總額。
///
/// 本檔只驗**子帳**（發票明細的量、稅、總額），因為 fixture 刻意不注入分錄服務。
/// ⚠️ 這不代表 AR 不入 GL——開票與退貨都會產生分錄（見
/// <see cref="ArInvoiceJournalAndRateTests"/>）。原本這裡寫著「AR 產生本身不入 GL」，
/// 那是 2026-08-31 之前的舊註解，在開票補上分錄之後就不成立了。
/// </summary>
public class ARReversalByReturnedQtyTests : TestBase
{
    private const string PlantId  = "P001";
    private const string CustCode = "CUST001";

    private readonly ARInvoiceService _svc;
    private readonly Mock<IDocNumberService> _mockDocNo = new();
    private readonly Mock<IDateTimeService>  _mockDt    = new();
    private readonly Mock<ICurrencyService>  _mockCurrency;
    private int _arSeq;

    public ARReversalByReturnedQtyTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockDocNo.Setup(d => d.NextAsync("ARInvoice", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult($"ARI{++_arSeq:D6}"));

        _mockCurrency = GetMockCurrencyService();
        _mockCurrency.Setup(c => c.GetRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()))
            .ReturnsAsync((decimal?)null);

        _svc = new ARInvoiceService(_context, _mockDocNo.Object, _mockDt.Object, _mockCurrency.Object,
            periodSvc: null);

        _context.Companies.Add(new Company { CompanyCode = CustCode, CompanyName = "測試客戶", IsCustomer = true });
        _context.SaveChanges();
    }

    private void SeedShipment(string shipId, string productCode, decimal shipQty, decimal unitPrice, decimal taxRate = 0m)
    {
        var ship = new GoodsShipment
        {
            GoodsShipmentId = shipId, ShipDate = new DateOnly(2025, 3, 1),
            CreatedBy = "user1", CompanyCode = CustCode,
            Status = DocStatus.Posted, ApprovalStatus = DocumentApprovalStatus.Approved,
            PlantId = PlantId,
        };
        ship.Lines.Add(new GoodsShipmentLine
        {
            GoodsShipmentId = shipId, LineNo = 1, ProductCode = productCode,
            ShipQty = shipQty, UomCode = "PC", StockQty = shipQty, StockUomCode = "PC",
            UnitPrice = unitPrice, TaxRate = taxRate,
        });
        _context.GoodsShipments.Add(ship);
        _context.SaveChanges();
    }

    /// <summary>建一張銷退單掛在 shipId 的第 1 行上（草稿當下就會加 ReturnedQty）。</summary>
    private void SeedSalesReturn(string srId, string shipId, decimal qty, DocStatus status)
    {
        var sr = new SalesReturn
        {
            SalesReturnId = srId, ReturnDate = new DateOnly(2025, 3, 5),
            CompanyCode = CustCode, CreatedBy = "user1",
            Status = status, ApprovalStatus = DocumentApprovalStatus.Approved,
            PlantId = PlantId,
        };
        sr.Lines.Add(new SalesReturnLine
        {
            SalesReturnId = srId, LineNo = 1, ProductCode = "FG001",
            ReturnQty = qty, UomCode = "PC", StockQty = qty, StockUomCode = "PC",
            GoodsShipmentId = shipId, ShipmentLineNo = 1,
        });
        _context.SalesReturns.Add(sr);
        var line = _context.GoodsShipmentLines.First(l => l.GoodsShipmentId == shipId && l.LineNo == 1);
        line.ReturnedQty += qty;
        _context.SaveChanges();
    }

    /// <summary>
    /// 開票前銷退**已過帳**：開票量必須扣掉它（與 AP 側同一個洞，見
    /// APAccrualByReceivedQtyTests.Generate_WhenPostedReturnPrecedesInvoicing_BillsNetOfIt）。
    /// </summary>
    [Fact]
    public async Task Generate_WhenPostedReturnPrecedesInvoicing_BillsNetOfIt()
    {
        SeedShipment("GS-RB1", "FG001", shipQty: 10m, unitPrice: 100m, taxRate: 0.05m);
        SeedSalesReturn("SR-RB1", "GS-RB1", 4m, DocStatus.Posted);

        var inv = await _svc.GenerateFromShipmentAsync("GS-RB1");

        Assert.Single(inv.Lines);
        Assert.Equal(6m,   inv.Lines.First().Qty);      // 10 − 4
        Assert.Equal(600m, inv.Lines.First().LineAmount);
        Assert.Equal(30m,  inv.Lines.First().TaxAmount);
        Assert.Equal(630m, inv.TotalAmount);            // 錯誤實作為 1050
    }

    /// <summary>
    /// 開票前銷退**還是草稿**：一個都不能扣，否則過帳回沖時會扣到第二次。
    /// `GoodsShipmentLine.ReturnedQty` 在存草稿當下就被加上去了，不能拿來當開票基準。
    /// </summary>
    [Fact]
    public async Task Generate_WhenDraftReturnPrecedesInvoicing_BillsFullQty()
    {
        SeedShipment("GS-RB2", "FG001", shipQty: 10m, unitPrice: 100m, taxRate: 0.05m);
        SeedSalesReturn("SR-RB2", "GS-RB2", 4m, DocStatus.Draft);
        Assert.Equal(4m, _context.GoodsShipmentLines
            .First(l => l.GoodsShipmentId == "GS-RB2" && l.LineNo == 1).ReturnedQty);

        var inv = await _svc.GenerateFromShipmentAsync("GS-RB2");

        Assert.Equal(10m,   inv.Lines.First().Qty);
        Assert.Equal(1050m, inv.TotalAmount);
    }

    [Fact]
    public async Task Reverse_ReducesInvoicedQtyAndTotal()
    {
        SeedShipment("GS-R1", "FG001", shipQty: 100m, unitPrice: 10m);
        await _svc.GenerateFromShipmentAsync("GS-R1");   // 開立 AR 100

        var rev = await _svc.ReverseForReturnedQtyAsync("GS-R1", 1, 40m);   // 退 40

        var inv = await LoadArAsync("GS-R1");

        Assert.NotNull(inv);
        Assert.Equal(60m, inv!.Lines.First().Qty);
        Assert.Equal(600m, inv.TotalAmount);
    }

    [Fact]
    public async Task Reverse_WithTax_RecomputesTaxOnRemainingQty()
    {
        SeedShipment("GS-R2", "FG001", shipQty: 100m, unitPrice: 300m, taxRate: 0.05m);
        await _svc.GenerateFromShipmentAsync("GS-R2");   // 30000 + 稅 1500

        var rev = await _svc.ReverseForReturnedQtyAsync("GS-R2", 1, 100m);   // 全退

        var inv = await LoadArAsync("GS-R2");

        Assert.Equal(0m, inv!.Lines.First().Qty);
        Assert.Equal(0m, inv.Lines.First().LineAmount);
        Assert.Equal(0m, inv.Lines.First().TaxAmount);
        Assert.Equal(0m, inv.TotalAmount);
    }

    /// <summary>
    /// 未注入分錄服務的部署（ACT 模組沒啟用）：回沖照樣更新子帳，且不得因為
    /// <c>_journalSvc == null</c> 就炸掉。
    ///
    /// 這支原名 <c>Reverse_PostsNoJournal_SubledgerOnly</c>，斷言「一張分錄都沒有」——
    /// 但它之所以會過，只是因為 fixture 沒給分錄服務，**不是因為程式不入帳**。
    /// 開票補上分錄之後，那個名字會讓讀的人得到相反的結論，故改成驗真正的契約：
    /// 沒有 ACT 模組時降級成純子帳。分錄本身由 ArInvoiceJournalAndRateTests 負責。
    /// </summary>
    [Fact]
    public async Task Reverse_WithoutJournalService_DegradesToSubledgerOnly()
    {
        SeedShipment("GS-R3", "FG001", shipQty: 100m, unitPrice: 300m, taxRate: 0.05m);
        var gen = await _svc.GenerateFromShipmentAsync("GS-R3");

        var rev = await _svc.ReverseForReturnedQtyAsync("GS-R3", 1, 50m);

        var inv = await LoadArAsync("GS-R3");

        Assert.Equal(50m,    inv!.Lines.First().Qty);
        Assert.Equal(15000m, inv.Lines.First().LineAmount);
        Assert.Equal(750m,   inv.Lines.First().TaxAmount);
        Assert.Empty(_context.JournalEntries.Where(e =>
            e.SourceDocId == gen.ARInvoiceId || e.SourceDocType == "ARInvoiceReturn"));
    }

    [Fact]
    public async Task Reverse_ExceedingInvoiced_ClampsAndFloorsAtZero()
    {
        SeedShipment("GS-R4", "FG001", shipQty: 100m, unitPrice: 10m);
        await _svc.GenerateFromShipmentAsync("GS-R4");

        var rev = await _svc.ReverseForReturnedQtyAsync("GS-R4", 1, 150m);   // 退 150 > 開立 100

        var inv = await LoadArAsync("GS-R4");

        Assert.Equal(0m, inv!.Lines.First().Qty);   // 下限 0
        Assert.Equal(0m, inv.TotalAmount);
    }

    [Fact]
    public async Task Reverse_MatchesLineBySourceLineNo()
    {
        // 同出貨單多行：只回沖被退的那一行，其他行不受影響。
        var ship = new GoodsShipment
        {
            GoodsShipmentId = "GS-R5", ShipDate = new DateOnly(2025, 3, 1),
            CreatedBy = "user1", CompanyCode = CustCode,
            Status = DocStatus.Posted, ApprovalStatus = DocumentApprovalStatus.Approved, PlantId = PlantId,
        };
        ship.Lines.Add(new GoodsShipmentLine { GoodsShipmentId = "GS-R5", LineNo = 1, ProductCode = "FG001", ShipQty = 100m, UomCode = "PC", StockQty = 100m, StockUomCode = "PC", UnitPrice = 10m });
        ship.Lines.Add(new GoodsShipmentLine { GoodsShipmentId = "GS-R5", LineNo = 2, ProductCode = "FG002", ShipQty = 50m, UomCode = "PC", StockQty = 50m, StockUomCode = "PC", UnitPrice = 20m });
        _context.GoodsShipments.Add(ship);
        _context.SaveChanges();
        await _svc.GenerateFromShipmentAsync("GS-R5");

        var rev = await _svc.ReverseForReturnedQtyAsync("GS-R5", 2, 20m);

        var inv = await LoadArAsync("GS-R5");

        Assert.Equal(100m, inv!.Lines.First(l => l.ProductCode == "FG001").Qty);   // 未退品號不變
        Assert.Equal(30m,  inv.Lines.First(l => l.ProductCode == "FG002").Qty);    // 50 - 20
    }

    /// <summary>
    /// 同一張出貨的同品號有兩行（不同單價）——舊的品號比對會固定命中第一行，
    /// 把第 2 行的退貨量扣到第 1 行上：總量看似對，但扣的是錯的單價，
    /// 發票金額因此少扣（或多扣）差價，而且第 1 行被扣光後第 2 行永遠回沖不到。
    /// 這是改用行號比對的唯一理由，也是唯一能證明改對了的斷言。
    /// </summary>
    [Fact]
    public async Task Reverse_SameProductOnTwoLines_HitsRequestedLineOnly()
    {
        var ship = new GoodsShipment
        {
            GoodsShipmentId = "GS-R10", ShipDate = new DateOnly(2025, 3, 1),
            CreatedBy = "user1", CompanyCode = CustCode,
            Status = DocStatus.Posted, ApprovalStatus = DocumentApprovalStatus.Approved, PlantId = PlantId,
        };
        // 同品號、不同單價（不同倉／批號出貨時的常態）。
        ship.Lines.Add(new GoodsShipmentLine { GoodsShipmentId = "GS-R10", LineNo = 1, ProductCode = "FG001", ShipQty = 100m, UomCode = "PC", StockQty = 100m, StockUomCode = "PC", UnitPrice = 10m });
        ship.Lines.Add(new GoodsShipmentLine { GoodsShipmentId = "GS-R10", LineNo = 2, ProductCode = "FG001", ShipQty = 40m,  UomCode = "PC", StockQty = 40m,  StockUomCode = "PC", UnitPrice = 25m });
        _context.GoodsShipments.Add(ship);
        _context.SaveChanges();
        await _svc.GenerateFromShipmentAsync("GS-R10");   // 1000 + 1000 = 2000

        var rev = await _svc.ReverseForReturnedQtyAsync("GS-R10", 2, 40m);   // 全退第 2 行

        var inv = await LoadArAsync("GS-R10");

        var l1 = inv!.Lines.First(l => l.SourceLineNo == 1);
        var l2 = inv.Lines.First(l => l.SourceLineNo == 2);
        Assert.Equal(100m, l1.Qty);        // 第 1 行完全不動（舊寫法會變成 60）
        Assert.Equal(1000m, l1.LineAmount);
        Assert.Equal(0m, l2.Qty);
        Assert.Equal(1000m, inv.TotalAmount);   // 舊寫法會扣成 1600（40 × 10 而非 40 × 25）
    }

    [Fact]
    public async Task Reverse_WhenNoSourceInvoice_ReportsNoInvoice()
    {
        SeedShipment("GS-R6", "FG001", shipQty: 100m, unitPrice: 10m);
        // 未開立 AR → 無可回沖，而且**沒有缺口**：沒開過的票談不上少沖
        var rev = await _svc.ReverseForReturnedQtyAsync("GS-R6", 1, 10m);
        Assert.Equal(ReturnReversalOutcome.NoInvoice, rev.Outcome);
        Assert.False(rev.HasGap);
        Assert.Null(rev.InvoiceId);
    }

    /// <summary>
    /// 已收款的發票不動它——這是刻意的（改已結清發票的金額會讓憑證與稅務申報前後不一致）。
    /// **但缺口必須被算出來並回報**：貨已經退回來了，那 400 元公司實際上欠客戶。
    /// 原本這裡只斷言「金額不變」，於是「一毛都沒回沖而且沒人知道」永遠是綠的。
    /// </summary>
    [Fact]
    public async Task Reverse_WhenInvoiceReceived_NoOp_ButReportsGap()
    {
        SeedShipment("GS-R7", "FG001", shipQty: 100m, unitPrice: 10m);
        var gen = await _svc.GenerateFromShipmentAsync("GS-R7");
        gen.Status = ARInvoiceStatus.Received;
        _context.SaveChanges();

        var rev = await _svc.ReverseForReturnedQtyAsync("GS-R7", 1, 40m);   // 已收款：本方案不回沖

        var inv = await LoadArAsync("GS-R7");

        Assert.Equal(100m, inv!.Lines.First().Qty);   // 金額不變
        Assert.Equal(ReturnReversalOutcome.SettledInvoice, rev.Outcome);
        Assert.Equal(0m, rev.ReversedGrossLocal);
        Assert.Equal(400m, rev.UnreversedGrossLocal);   // 40 × 10，無稅
        Assert.True(rev.HasGap);
    }

    /// <summary>部分收款也算已結清：同一條路徑、同一個缺口語意。</summary>
    [Fact]
    public async Task Reverse_WhenInvoicePartiallyReceived_ReportsGap()
    {
        SeedShipment("GS-R11", "FG001", shipQty: 100m, unitPrice: 10m, taxRate: 0.05m);
        var gen = await _svc.GenerateFromShipmentAsync("GS-R11");
        gen.Status = ARInvoiceStatus.PartialReceived;
        _context.SaveChanges();

        var rev = await _svc.ReverseForReturnedQtyAsync("GS-R11", 1, 40m);

        Assert.Equal(ReturnReversalOutcome.SettledInvoice, rev.Outcome);
        Assert.Equal(420m, rev.UnreversedGrossLocal);   // 40 × 10 × 1.05，缺口是**含稅**的
    }

    [Fact]
    public async Task Reverse_WhenLineNotOnInvoice_NoOp()
    {
        SeedShipment("GS-R8", "FG001", shipQty: 100m, unitPrice: 10m);
        var gen = await _svc.GenerateFromShipmentAsync("GS-R8");

        var rev = await _svc.ReverseForReturnedQtyAsync("GS-R8", 99, 10m);   // 行號不在發票上

        var inv = await LoadArAsync("GS-R8");

        Assert.Equal(100m, inv!.Lines.First().Qty);   // 不變
        Assert.Equal(ReturnReversalOutcome.LineNotFound, rev.Outcome);
        Assert.False(rev.HasGap);   // 對不到行不等於少沖錢，不可誤報成缺口
    }

    [Fact]
    public async Task Reverse_ZeroQty_ReportsNoInvoice()
    {
        SeedShipment("GS-R9", "FG001", shipQty: 100m, unitPrice: 10m);
        await _svc.GenerateFromShipmentAsync("GS-R9");

        var rev = await _svc.ReverseForReturnedQtyAsync("GS-R9", 1, 0m);

        // 退 0 個：連查都不必查，直接回「無可回沖」，且不得誤報缺口
        Assert.Equal(ReturnReversalOutcome.NoInvoice, rev.Outcome);
        Assert.False(rev.HasGap);

        var inv = await LoadArAsync("GS-R9");
        Assert.Equal(100m, inv.Lines.First().Qty);   // 發票原封不動
    }

    /// <summary>
    /// 回沖後把發票從 DbContext 重讀出來。
    ///
    /// <c>ReverseForReturnedQtyAsync</c> 改回傳 <c>ReturnReversalResult</c>（帶「沖了多少／
    /// 沒沖多少」）而不再回傳發票物件——因為呼叫端真正需要知道的是缺口，而原本回傳發票、
    /// 四個呼叫點全部丟掉，「已收款發票遇上退貨完全沒回沖」才會一路無聲。
    /// 這些測試要看的是**發票被改成什麼樣**，所以改成回資料庫拿。
    /// </summary>
    private async Task<ARInvoice> LoadArAsync(string shipmentId)
        => await _context.ARInvoices.IgnoreQueryFilters().Include(i => i.Lines)
                 .SingleAsync(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == shipmentId);
}
