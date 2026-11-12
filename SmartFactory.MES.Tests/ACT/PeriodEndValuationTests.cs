using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// 期末評價三支服務（外幣重評價 / 預期信用損失 / 存貨淨變現價值）。
///
/// 三支都是純計算 + 覆寫式重跑，先前完全沒有測試：算錯不會拋例外，
/// 只會讓期末調整分錄的金額默默失真；重跑若沒刪乾淨舊資料則直接變成雙倍認列。
/// </summary>
public class PeriodEndValuationTests : TestBase
{
    private const string PlantId = "P001";
    private const string Period  = "202606";

    private readonly FxRevaluationService  _fx;
    private readonly EclService            _ecl;
    private readonly NrvAssessmentService  _nrv;

    public PeriodEndValuationTests()
    {
        _context.CurrentPlantId = PlantId;
        _fx  = new FxRevaluationService(_context);
        _ecl = new EclService(_context);
        _nrv = new NrvAssessmentService(_context, new Moq.Mock<SmartFactory.MES.Modules.BAS.Services.IUomService>().Object, TodayClock());
    }

    // ════════════════════ 外幣重評價 ════════════════════

    private void SeedAp(string id, string? currency, decimal total, decimal paid,
        decimal bookRate, APInvoiceStatus status = APInvoiceStatus.Open, string plant = PlantId)
        => _context.APInvoices.Add(new APInvoice
        {
            APInvoiceId = id, SupplierCode = "SUP001", PlantId = plant,
            InvoiceDate = new DateOnly(2026, 6, 1), DueDate = new DateOnly(2026, 7, 1),
            CurrencyCode = currency, ExchangeRate = bookRate,
            TotalAmount = total, PaidAmount = paid, Status = status,
        });

    private void SeedAr(string id, string? currency, decimal total, decimal received,
        decimal bookRate, ARInvoiceStatus status = ARInvoiceStatus.Open,
        DateOnly? dueDate = null, string plant = PlantId)
        => _context.ARInvoices.Add(new ARInvoice
        {
            ARInvoiceId = id, CustomerCode = "CUS001", PlantId = plant,
            InvoiceDate = new DateOnly(2026, 6, 1),
            DueDate = dueDate ?? new DateOnly(2026, 7, 1),
            CurrencyCode = currency, ExchangeRate = bookRate,
            // 本幣 = 外幣 × 匯率。真實發票一定有這欄；ECL 改以本幣計之後，漏設會被當成 0。
            TotalAmount = total, AmountLocal = total * bookRate, ReceivedAmount = received, Status = status,
        });

    /// <summary>未結＝總額 − 已付；應付是負債，期末匯率上升＝要多付＝損失（負數）。</summary>
    [Fact]
    public async Task FxRevaluation_ComputesGainLossOnOutstandingOnly()
    {
        SeedAp("AP-1", "USD", total: 1000m, paid: 400m, bookRate: 30m);
        await _context.SaveChangesAsync();

        var rows = await _fx.RunRevaluationAsync(Period, PlantId,
            new Dictionary<string, decimal> { ["USD"] = 32m });

        var r = Assert.Single(rows);
        Assert.Equal(600m,   r.OutstandingAmount);        // 1000 − 400
        Assert.Equal(18000m, r.BookAmountLocal);          // 600 × 30
        Assert.Equal(19200m, r.RevaluedAmountLocal);      // 600 × 32
        Assert.Equal(-1200m, r.ExchangeGainLoss);         // 應付變大＝損失
        Assert.Equal(RevaluationDocType.APInvoice, r.DocType);
    }

    // ── 2026-10-24：以期末當時評估（反關帳重做上一期月結） ──────────────────

    private void SeedPeriodJune()
        => _context.AccountingPeriods.Add(new AccountingPeriod
        {
            PeriodId = Period, PlantId = PlantId, Year = 2026, Month = 6,
            StartDate = new DateOnly(2026, 6, 1), EndDate = new DateOnly(2026, 6, 30), Status = PeriodStatus.Reopened,
        });

    private void SeedReceiptAfterEnd(string invoiceId, decimal amt)
    {
        _context.ARReceipts.Add(new ARReceipt
        {
            ARReceiptId = "RC-0705", CustomerCode = "CUS001", PlantId = PlantId, ReceiptDate = new DateOnly(2026, 7, 5),
            CurrencyCode = "USD", ExchangeRate = 31m, Amount = amt, Status = ARReceiptStatus.Received,
        });
        _context.ARReceiptAllocations.Add(new ARReceiptAllocation { ARReceiptId = "RC-0705", ARInvoiceId = invoiceId, AllocatedAmount = amt });
    }

    /// <summary>
    /// 期末之後才收清的發票，期末當時仍未結，要重評；期末之後才開的發票不屬於這一期，不重評。
    /// 原本取現在的累計且不濾開立日：反關帳重做上一期時，下一期的收款與新發票都算進來（2026-10-24 月結序列）。
    /// </summary>
    [Fact]
    public async Task FxRevaluation_UsesOutstandingAsOfPeriodEnd()
    {
        SeedPeriodJune();
        SeedAr("AR-PAIDLATER", "USD", total: 500m, received: 500m, bookRate: 30m, status: ARInvoiceStatus.Received);
        SeedReceiptAfterEnd("AR-PAIDLATER", 500m);
        SeedAr("AR-JULY", "USD", total: 800m, received: 0m, bookRate: 30m);
        _context.ARInvoices.Local.Single(i => i.ARInvoiceId == "AR-JULY").InvoiceDate = new DateOnly(2026, 7, 2);
        await _context.SaveChangesAsync();

        var rows = await _fx.RunRevaluationAsync(Period, PlantId, new Dictionary<string, decimal> { ["USD"] = 32m });

        var r = Assert.Single(rows);
        Assert.Equal("AR-PAIDLATER", r.DocId);
        Assert.Equal(500m, r.OutstandingAmount);
        Assert.Equal(1000m, r.ExchangeGainLoss);           // 500 × (32 − 30)
    }

    /// <summary>ECL 同一口徑：期末之後才收的款要加回，期末當時的曝險才對。</summary>
    [Fact]
    public async Task Ecl_UsesExposureAsOfDate()
    {
        SeedAr("AR-PAIDLATER", "USD", total: 500m, received: 500m, bookRate: 30m, status: ARInvoiceStatus.Received);
        SeedReceiptAfterEnd("AR-PAIDLATER", 500m);
        await _context.SaveChangesAsync();

        var rows = await _ecl.ComputeEclAsync(Period, PlantId, new DateOnly(2026, 6, 30));

        Assert.Equal(15000m, rows.Sum(r => r.ArBalance));  // 500 × 30
    }

    /// <summary>期末匯率低於入帳匯率時損益為負，不得取絕對值。</summary>
    [Fact]
    public async Task FxRevaluation_RateDrop_ProducesNegativeGainLoss()
    {
        SeedAr("AR-1", "USD", total: 500m, received: 0m, bookRate: 32m);
        await _context.SaveChangesAsync();

        var rows = await _fx.RunRevaluationAsync(Period, PlantId,
            new Dictionary<string, decimal> { ["USD"] = 30m });

        Assert.Equal(-1000m, Assert.Single(rows).ExchangeGainLoss);   // 500 ×(30−32)
    }

    /// <summary>已付清／已收訖／作廢與本位幣（CurrencyCode 為 null）皆不重評。</summary>
    [Fact]
    public async Task FxRevaluation_ExcludesSettledVoidAndLocalCurrency()
    {
        SeedAp("AP-PAID", "USD", 100m, 100m, 30m, APInvoiceStatus.Paid);
        SeedAp("AP-VOID", "USD", 100m, 0m,   30m, APInvoiceStatus.Void);
        SeedAp("AP-LOCAL", null, 100m, 0m,   1m);
        SeedAr("AR-RECV", "USD", 100m, 100m, 30m, ARInvoiceStatus.Received);
        await _context.SaveChangesAsync();

        var rows = await _fx.RunRevaluationAsync(Period, PlantId,
            new Dictionary<string, decimal> { ["USD"] = 32m });

        Assert.Empty(rows);
    }

    /// <summary>折讓過的部分不再是應收／應付，不重評；折讓沖平的發票整張不出現。</summary>
    [Fact]
    public async Task FxRevaluation_ExcludesCreditedAndDebitedAmounts()
    {
        SeedAr("AR-CN", "USD", total: 500m, received: 100m, bookRate: 30m);
        SeedAp("AP-DN", "USD", total: 300m, paid: 0m, bookRate: 30m);
        SeedAp("AP-DN0", "USD", total: 300m, paid: 0m, bookRate: 30m);
        await _context.SaveChangesAsync();
        _context.ARInvoices.Find("AR-CN")!.CreditedAmount = 150m;
        _context.APInvoices.Find("AP-DN")!.DebitedAmount = 100m;
        _context.APInvoices.Find("AP-DN0")!.DebitedAmount = 300m;
        await _context.SaveChangesAsync();

        var rows = await _fx.RunRevaluationAsync(Period, PlantId, new Dictionary<string, decimal> { ["USD"] = 32m });

        Assert.Equal("AP-DN:200|AR-CN:250", string.Join("|", rows.OrderBy(r => r.DocId)
            .Select(r => $"{r.DocId}:{r.OutstandingAmount:0.####}")));
    }

    /// <summary>
    /// 收訖後才開折讓的外幣發票：負餘額是欠客戶的外幣，同樣要重評——匯率上升＝要退更多本幣＝損失。
    /// </summary>
    [Fact]
    public async Task FxRevaluation_CreditBalanceAfterFullReceipt_IsRevalued()
    {
        SeedAr("AR-PAIDCN", "USD", total: 500m, received: 500m, bookRate: 30m, status: ARInvoiceStatus.Received);
        await _context.SaveChangesAsync();
        _context.ARInvoices.Find("AR-PAIDCN")!.CreditedAmount = 50m;
        await _context.SaveChangesAsync();

        var r = Assert.Single(await _fx.RunRevaluationAsync(Period, PlantId, new Dictionary<string, decimal> { ["USD"] = 32m }));

        Assert.Equal(-50m,  r.OutstandingAmount);
        Assert.Equal(-100m, r.ExchangeGainLoss);   // −50 ×(32−30)
    }

    /// <summary>期末匯率表沒給的幣別跳過，不得以 1 或帳上匯率硬算。</summary>
    [Fact]
    public async Task FxRevaluation_CurrencyWithoutPeriodEndRate_IsSkipped()
    {
        SeedAp("AP-JPY", "JPY", 10000m, 0m, 0.22m);
        await _context.SaveChangesAsync();

        var rows = await _fx.RunRevaluationAsync(Period, PlantId,
            new Dictionary<string, decimal> { ["USD"] = 32m });

        Assert.Empty(rows);
    }

    /// <summary>同期間重跑必須覆蓋而非累加（否則期末調整直接雙倍）。</summary>
    [Fact]
    public async Task FxRevaluation_RerunSamePeriod_ReplacesInsteadOfAccumulating()
    {
        SeedAp("AP-2", "USD", 1000m, 0m, 30m);
        await _context.SaveChangesAsync();

        await _fx.RunRevaluationAsync(Period, PlantId, new Dictionary<string, decimal> { ["USD"] = 31m });
        await _fx.RunRevaluationAsync(Period, PlantId, new Dictionary<string, decimal> { ["USD"] = 32m });

        var stored = _context.FxRevaluationEntries
            .Where(e => e.PeriodId == Period && e.PlantId == PlantId).ToList();
        Assert.Single(stored);
        Assert.Equal(-2000m, stored[0].ExchangeGainLoss);  // 只留第二次的結果（應付 1000 ×(30−32)）
    }

    // ════════════════════ 預期信用損失（ECL）════════════════════

    /// <summary>未逾期落在 Current 桶、預設損失率 0.5%。</summary>
    [Fact]
    public async Task Ecl_NotYetDue_FallsInCurrentBucket()
    {
        SeedAr("AR-C1", null, total: 100000m, received: 0m, bookRate: 1m,
            dueDate: new DateOnly(2026, 7, 31));
        await _context.SaveChangesAsync();

        var rows = await _ecl.ComputeEclAsync(Period, PlantId, new DateOnly(2026, 6, 30));

        var current = rows.Single(r => r.AgingBucket == "Current");
        Assert.Equal(100000m, current.ArBalance);
        Assert.Equal(500m,    current.EclAmount);      // 100000 × 0.5%
        Assert.All(rows.Where(r => r.AgingBucket != "Current"), r => Assert.Equal(0m, r.ArBalance));
    }

    /// <summary>折讓過的部分不是應收，不提備抵（外幣：折讓也按入帳匯率換本幣扣）。</summary>
    [Fact]
    public async Task Ecl_CreditNoteReducesTheBalance()
    {
        SeedAr("AR-CN1", "USD", total: 1000m, received: 200m, bookRate: 30m, dueDate: new DateOnly(2026, 7, 31));
        await _context.SaveChangesAsync();
        _context.ARInvoices.Find("AR-CN1")!.CreditedAmount = 300m;
        await _context.SaveChangesAsync();

        var rows = await _ecl.ComputeEclAsync(Period, PlantId, new DateOnly(2026, 6, 30));

        Assert.Equal(15000m, rows.Single(r => r.AgingBucket == "Current").ArBalance);   // (1000−200−300)×30
    }

    /// <summary>折讓超過剩餘未收（客戶多付）沒有信用風險，也不得抵減同一桶別張發票的備抵。</summary>
    [Fact]
    public async Task Ecl_OverCreditedInvoice_DoesNotOffsetOtherBalances()
    {
        SeedAr("AR-E1", null, total: 1000m, received: 0m,   bookRate: 1m, dueDate: new DateOnly(2026, 7, 31));
        SeedAr("AR-E2", null, total: 500m,  received: 450m, bookRate: 1m, dueDate: new DateOnly(2026, 7, 31),
            status: ARInvoiceStatus.PartialReceived);
        await _context.SaveChangesAsync();
        _context.ARInvoices.Find("AR-E2")!.CreditedAmount = 100m;   // 未結 −50
        await _context.SaveChangesAsync();

        var rows = await _ecl.ComputeEclAsync(Period, PlantId, new DateOnly(2026, 6, 30));

        Assert.Equal(1000m, rows.Single(r => r.AgingBucket == "Current").ArBalance);
    }

    /// <summary>逾期天數決定分桶，且 ECL = 桶內餘額 × 該桶損失率。</summary>
    [Theory]
    [InlineData(15,  "1-30",  0.02)]
    [InlineData(45,  "31-60", 0.05)]
    [InlineData(75,  "61-90", 0.10)]
    [InlineData(200, "90+",   0.30)]
    public async Task Ecl_BucketsByDaysPastDueAndAppliesRate(int daysPastDue, string bucket, double rate)
    {
        var asOf = new DateOnly(2026, 6, 30);
        SeedAr("AR-B1", null, total: 10000m, received: 0m, bookRate: 1m,
            dueDate: asOf.AddDays(-daysPastDue));
        await _context.SaveChangesAsync();

        var rows = await _ecl.ComputeEclAsync(Period, PlantId, asOf);

        var row = rows.Single(r => r.AgingBucket == bucket);
        Assert.Equal(10000m, row.ArBalance);
        Assert.Equal(Math.Round(10000m * (decimal)rate, 4), row.EclAmount);
    }

    /// <summary>ECL 基礎是未收餘額（總額 − 已收），不是發票總額。</summary>
    [Fact]
    public async Task Ecl_UsesOutstandingNotTotal()
    {
        SeedAr("AR-P1", null, total: 10000m, received: 6000m, bookRate: 1m,
            dueDate: new DateOnly(2026, 7, 31));
        await _context.SaveChangesAsync();

        var rows = await _ecl.ComputeEclAsync(Period, PlantId, new DateOnly(2026, 6, 30));

        Assert.Equal(4000m, rows.Single(r => r.AgingBucket == "Current").ArBalance);
    }

    /// <summary>同期間重跑覆蓋，桶數不得倍增。</summary>
    [Fact]
    public async Task Ecl_RerunSamePeriod_ReplacesInsteadOfAccumulating()
    {
        SeedAr("AR-R1", null, 1000m, 0m, 1m, dueDate: new DateOnly(2026, 7, 31));
        await _context.SaveChangesAsync();

        var first  = await _ecl.ComputeEclAsync(Period, PlantId, new DateOnly(2026, 6, 30));
        await _ecl.ComputeEclAsync(Period, PlantId, new DateOnly(2026, 6, 30));

        Assert.Equal(first.Count,
            _context.EclAllowances.Count(e => e.PeriodId == Period && e.PlantId == PlantId));
    }

    // ════════════════════ 存貨淨變現價值（NRV）════════════════════

    private void SeedInventory(string product, decimal qty, decimal unitCost, string plant = PlantId)
        => _context.Inventories.Add(new Inventory
        {
            PlantId = plant, ProductCode = product, LocationCode = "LOC1", LotNo = "",
            QtyOnHand = qty, UnitCost = unitCost,
        });

    private void SeedArWithLine(string id, string product, decimal unitPrice, int daysAgo = 10)
    {
        var inv = new ARInvoice
        {
            ARInvoiceId = id, CustomerCode = "CUS001", PlantId = PlantId,
            InvoiceDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-daysAgo)),
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            ExchangeRate = 1m, TotalAmount = unitPrice,
        };
        inv.Lines.Add(new ARInvoiceLine
        {
            ARInvoiceId = id, LineNo = 1, ProductCode = product,
            Qty = 1m, UnitPrice = unitPrice, LineAmount = unitPrice,
        });
        _context.ARInvoices.Add(inv);
    }

    /// <summary>成本高於淨變現價值時認列減損＝(成本 − NRV) × 在庫量；NRV = 售價 − 售價×售費率。</summary>
    [Fact]
    public async Task Nrv_CostAboveNrv_RecognizesImpairment()
    {
        SeedInventory("P-A", qty: 100m, unitCost: 100m);
        SeedArWithLine("ARI-1", "P-A", unitPrice: 90m);
        await _context.SaveChangesAsync();

        var rows = await _nrv.RunAssessmentAsync(Period, PlantId, sellingCostRatio: 0.02m);

        var r = Assert.Single(rows);
        Assert.Equal(1.8m,  r.SellingCost);            // 90 × 2%
        Assert.Equal(88.2m, r.NetRealizableValue);     // 90 − 1.8
        Assert.Equal(1180m, r.ImpairmentLoss);         // (100 − 88.2) × 100
    }

    /// <summary>成本低於 NRV 時不得認列「負減損」（存貨不因此增值）。</summary>
    [Fact]
    public async Task Nrv_CostBelowNrv_ImpairmentFlooredAtZero()
    {
        SeedInventory("P-B", qty: 50m, unitCost: 40m);
        SeedArWithLine("ARI-2", "P-B", unitPrice: 100m);
        await _context.SaveChangesAsync();

        var rows = await _nrv.RunAssessmentAsync(Period, PlantId);

        Assert.Equal(0m, Assert.Single(rows).ImpairmentLoss);
    }

    /// <summary>近 90 天無銷售記錄的品號沒有市價依據，跳過而非以 0 售價認列全額減損。</summary>
    [Fact]
    public async Task Nrv_NoRecentSales_SkipsProduct()
    {
        SeedInventory("P-C", qty: 10m, unitCost: 100m);
        SeedArWithLine("ARI-3", "P-C", unitPrice: 80m, daysAgo: 200);   // 超過 90 天
        await _context.SaveChangesAsync();

        var rows = await _nrv.RunAssessmentAsync(Period, PlantId);

        Assert.Empty(rows);
    }

    /// <summary>同期間重跑覆蓋，不得累加成雙筆減損。</summary>
    [Fact]
    public async Task Nrv_RerunSamePeriod_ReplacesInsteadOfAccumulating()
    {
        SeedInventory("P-D", qty: 10m, unitCost: 100m);
        SeedArWithLine("ARI-4", "P-D", unitPrice: 50m);
        await _context.SaveChangesAsync();

        await _nrv.RunAssessmentAsync(Period, PlantId);
        await _nrv.RunAssessmentAsync(Period, PlantId);

        Assert.Single(_context.InventoryNrvAssessments
            .Where(a => a.PeriodId == Period && a.PlantId == PlantId));
    }

    /// <summary>NRV 以當地營業日為基準（找不到期間時）；測試的銷售日期以今天往回推，這裡給今天。</summary>
    private static SmartFactory.MES.Services.IDateTimeService TodayClock()
    {
        var dt = new Moq.Mock<SmartFactory.MES.Services.IDateTimeService>();
        dt.Setup(d => d.GetLocalDate(Moq.It.IsAny<string?>())).Returns(DateOnly.FromDateTime(DateTime.UtcNow));
        return dt.Object;
    }
}
