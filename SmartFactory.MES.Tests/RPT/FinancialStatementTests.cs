using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.RPT.Services;
using SmartFactory.MES.Tests.Conventions;
using Xunit;

namespace SmartFactory.MES.Tests.RPT;

/// <summary>
/// 資產負債表與損益表的科目分類、借貸正負號、期間截止與平衡恆等式。
///
/// 這兩支服務先前完全沒有測試，而它們的核心就是一段以科目代碼首碼決定
/// 「借正或貸正」的 switch——正負號寫反不會有任何徵兆，只會讓整張財報靜靜地錯。
/// </summary>
public class FinancialStatementTests : TestBase
{
    private const string PlantId = "P001";

    private readonly BalanceSheetService _bs;
    private readonly ProfitLossService   _pl;
    private int _seq;

    public FinancialStatementTests()
    {
        _context.CurrentPlantId = PlantId;
        // 科目分類服務傳真的那一支：主檔沒有的科目會退回代碼首碼，
        // 而本測試餵的正是「任意科目組合」——用假的分類器等於把受測行為換掉。
        var classifier = new Modules.BAS.Services.AccountClassifier(_context);
        _bs = new BalanceSheetService(_context, classifier);
        _pl = new ProfitLossService(_context, classifier);
    }

    /// <summary>直接落分錄（不經 JournalEntryService）：本測試要餵的是任意科目組合。</summary>
    private void Seed(string periodId, params (string Code, string Name, decimal Debit, decimal Credit)[] lines)
        => Seed(periodId, PlantId, lines);

    private void Seed(string periodId, string plantId,
        params (string Code, string Name, decimal Debit, decimal Credit)[] lines)
    {
        var entry = new JournalEntry
        {
            JournalId     = $"JE{++_seq:D5}",
            PeriodId      = periodId,
            EntryDate     = new DateOnly(int.Parse(periodId[..4]), int.Parse(periodId[4..]), 1),
            SourceDocType = "Test",
            PlantId       = plantId,
        };
        var no = 1;
        foreach (var l in lines)
            entry.Lines.Add(new JournalLine
            {
                LineNo = no++, AccountCode = l.Code, AccountName = l.Name,
                Debit = l.Debit, Credit = l.Credit, AmountLocal = l.Debit + l.Credit,
            });
        _context.JournalEntries.Add(entry);
        _context.SaveChanges();
    }

    // ════════════════════ 資產負債表 ════════════════════

    /// <summary>資產借正、負債與權益貸正——正負號寫反是這段 switch 最可能的錯法。</summary>
    [Fact]
    public async Task BalanceSheet_AppliesNormalBalanceSideByAccountPrefix()
    {
        Seed("202601",
            ("1100", "現金",     100000m, 0m),
            ("2100", "應付帳款",       0m, 30000m),
            ("3100", "股本",           0m, 70000m));

        var r = await _bs.GetReportAsync("202601", PlantId);

        Assert.Equal(100000m, r.TotalAssets);        // 借 − 貸
        Assert.Equal(30000m,  r.TotalLiabilities);   // 貸 − 借
        Assert.Equal(70000m,  r.TotalEquity);        // 貸 − 借
    }

    /// <summary>
    /// 會計恆等式：帳是平的時候，資產 = 負債 + 權益 + 本期損益。
    /// 本服務不做期末結轉（損益科目 4~8 不併入權益），所以恆等式必須帶上損益表的
    /// 本期淨利才會成立——這也正是「BS 與 PL 兩支服務算的是同一本帳」的交叉驗證。
    /// </summary>
    [Fact]
    public async Task BalanceSheetAndProfitLoss_SatisfyAccountingEquation()
    {
        Seed("202601",
            ("1100", "現金",     500000m, 0m),
            ("3100", "股本",          0m, 500000m));
        Seed("202601",                                   // 賒銷
            ("1200", "應收帳款", 120000m, 0m),
            ("4100", "銷貨收入",      0m, 120000m));
        Seed("202601",                                   // 銷貨成本
            ("5100", "銷貨成本",  70000m, 0m),
            ("1300", "存貨",          0m, 70000m));
        Seed("202601",                                   // 賒購費用
            ("6100", "營業費用",  20000m, 0m),
            ("2100", "應付帳款",      0m, 20000m));

        var bs = await _bs.GetReportAsync("202601", PlantId);
        var pl = await _pl.GetReportAsync("202601", "202601", PlantId);

        Assert.Equal(30000m, pl.NetProfit);              // 120000 − 70000 − 20000
        Assert.Equal(bs.TotalAssets,
                     bs.TotalLiabilities + bs.TotalEquity + pl.NetProfit);
    }

    /// <summary>asOfPeriod 是累計截止：之後期間的分錄不得計入。</summary>
    [Fact]
    public async Task BalanceSheet_IsCumulativeUpToAsOfPeriod()
    {
        Seed("202601", ("1100", "現金", 1000m, 0m), ("3100", "股本", 0m, 1000m));
        Seed("202602", ("1100", "現金", 2000m, 0m), ("3100", "股本", 0m, 2000m));
        Seed("202603", ("1100", "現金", 4000m, 0m), ("3100", "股本", 0m, 4000m));

        var r = await _bs.GetReportAsync("202602", PlantId);

        Assert.Equal(3000m, r.TotalAssets);   // 1 月 + 2 月，不含 3 月
    }

    /// <summary>借貸互抵為零的科目不列示（避免整排 0 佔版面），但也不得影響合計。</summary>
    [Fact]
    public async Task BalanceSheet_OmitsZeroBalanceAccounts()
    {
        Seed("202601", ("1100", "現金", 5000m, 0m), ("2100", "應付帳款", 0m, 5000m));
        Seed("202601", ("2100", "應付帳款", 5000m, 0m), ("1100", "現金", 0m, 5000m));   // 全額付清

        var r = await _bs.GetReportAsync("202601", PlantId);

        Assert.All(r.Sections, s => Assert.Empty(s.Items));
        Assert.Equal(0m, r.TotalAssets);
        Assert.Equal(0m, r.TotalLiabilities);
    }

    /// <summary>他廠分錄不得混入本廠財報。</summary>
    [Fact]
    public async Task BalanceSheet_ExcludesOtherPlants()
    {
        Seed("202601", ("1100", "現金", 1000m, 0m), ("3100", "股本", 0m, 1000m));
        Seed("202601", "P002", ("1100", "現金", 9999m, 0m), ("3100", "股本", 0m, 9999m));

        var r = await _bs.GetReportAsync("202601", PlantId);

        Assert.Equal(1000m, r.TotalAssets);
    }

    /// <summary>損益科目（4~8）不屬於資產負債表任何一段，須被略過而非誤歸。</summary>
    [Fact]
    public async Task BalanceSheet_IgnoresProfitAndLossAccounts()
    {
        Seed("202601", ("1200", "應收帳款", 8000m, 0m), ("4100", "銷貨收入", 0m, 8000m));

        var r = await _bs.GetReportAsync("202601", PlantId);

        Assert.Equal(8000m, r.TotalAssets);
        Assert.Equal(0m, r.TotalLiabilities);
        Assert.Equal(0m, r.TotalEquity);
        Assert.DoesNotContain(r.Sections.SelectMany(s => s.Items), i => i.AccountCode == "4100");
    }

    // ════════════════════ 損益表 ════════════════════

    /// <summary>收入類貸正、成本費用類借正，並落到正確的分類欄位。</summary>
    [Fact]
    public async Task ProfitLoss_ClassifiesAndSignsEachAccountGroup()
    {
        Seed("202601",
            ("4100", "銷貨收入",     0m, 100000m),
            ("5100", "銷貨成本", 60000m,      0m),
            ("6100", "營業費用", 15000m,      0m),
            ("7100", "利息收入",     0m,   3000m),
            ("8100", "利息費用",  2000m,      0m),
            ("1100", "現金",     26000m,      0m));   // 資產科目不入損益表

        var r = await _pl.GetReportAsync("202601", "202601", PlantId);

        Assert.Equal(100000m, r.Revenue);
        Assert.Equal(60000m,  r.Cogs);
        Assert.Equal(15000m,  r.OperatingExpenses);
        Assert.Equal(3000m,   r.OtherIncome);
        Assert.Equal(2000m,   r.OtherExpenses);
        Assert.Equal(40000m,  r.GrossProfit);        // 100000 − 60000
        Assert.Equal(25000m,  r.OperatingProfit);    // 40000 − 15000
        Assert.Equal(26000m,  r.NetProfit);          // 25000 + 3000 − 2000
        Assert.DoesNotContain(r.Lines, l => l.AccountCode == "1100");
    }

    /// <summary>銷貨退回（借記收入科目）必須沖減收入，而不是被當成費用。</summary>
    [Fact]
    public async Task ProfitLoss_RevenueContraEntryReducesRevenue()
    {
        Seed("202601", ("4100", "銷貨收入", 0m, 100000m), ("1200", "應收帳款", 100000m, 0m));
        Seed("202601", ("4100", "銷貨收入", 20000m, 0m), ("1200", "應收帳款", 0m, 20000m));

        var r = await _pl.GetReportAsync("202601", "202601", PlantId);

        Assert.Equal(80000m, r.Revenue);
    }

    /// <summary>期間區間為閉區間，區間外的分錄不得計入。</summary>
    [Fact]
    public async Task ProfitLoss_FiltersByInclusivePeriodRange()
    {
        Seed("202601", ("4100", "銷貨收入", 0m, 10m), ("1200", "應收帳款", 10m, 0m));
        Seed("202602", ("4100", "銷貨收入", 0m, 20m), ("1200", "應收帳款", 20m, 0m));
        Seed("202603", ("4100", "銷貨收入", 0m, 40m), ("1200", "應收帳款", 40m, 0m));

        var r = await _pl.GetReportAsync("202601", "202602", PlantId);

        Assert.Equal(30m, r.Revenue);
    }

    /// <summary>零收入時毛利率回 0，不得除以零。</summary>
    [Fact]
    public async Task ProfitLoss_ZeroRevenue_GrossMarginRateIsZeroNotDivideByZero()
    {
        Seed("202601", ("6100", "營業費用", 5000m, 0m), ("1100", "現金", 0m, 5000m));

        var r = await _pl.GetReportAsync("202601", "202601", PlantId);

        Assert.Equal(0m, r.Revenue);
        Assert.Equal(0m, r.GrossMarginRate);
        Assert.Equal(-5000m, r.NetProfit);
    }

    // ════════════════════ 合併（跨廠） ════════════════════

    /// <summary>
    /// 合併＝把指定廠別的分錄當成同一本帳加總。單廠版是合併版只給一個廠別的特例，
    /// 這裡同時釘住兩件事：加總正確，且單廠結果不因改走合併路徑而改變。
    /// </summary>
    [Fact]
    public async Task ConsolidatedBalanceSheet_SumsSelectedPlantsOnly()
    {
        Seed("202601", "P001", ("1100", "現金", 100m, 0m), ("3100", "股本", 0m, 100m));
        Seed("202601", "P002", ("1100", "現金", 250m, 0m), ("3100", "股本", 0m, 250m));
        Seed("202601", "P003", ("1100", "現金", 999m, 0m), ("3100", "股本", 0m, 999m));

        var two = await _bs.GetConsolidatedReportAsync("202601", new[] { "P001", "P002" });
        var one = await _bs.GetConsolidatedReportAsync("202601", new[] { "P001" });

        Assert.Equal(350m, two.TotalAssets);          // 沒選的 P003 不得混進來
        Assert.Equal(100m, one.TotalAssets);
        Assert.Equal(100m, (await _bs.GetReportAsync("202601", "P001")).TotalAssets);
    }

    /// <summary>
    /// 跨廠調撥的兩腿加總後歸零——這就是「合併不需要沖銷分錄」的依據。
    /// 若哪天有人把調撥改成加價搬（兩腿金額不同），這支會紅。
    /// </summary>
    [Fact]
    public async Task ConsolidatedBalanceSheet_InterPlantLegsNetToZeroWhenReceived()
    {
        const string Clearing = Modules.ACT.StandardAccounts.InterPlantClearingCode;
        Seed("202601", "P001",                          // 撥出廠：借 1450／貸 存貨
            (Clearing, "廠際調撥往來", 800m, 0m), ("1401", "原料存貨", 0m, 800m));
        Seed("202601", "P002",                          // 撥入廠：借 存貨／貸 1450
            ("1401", "原料存貨", 800m, 0m), (Clearing, "廠際調撥往來", 0m, 800m));

        var r = await _bs.GetConsolidatedReportAsync("202601", new[] { "P001", "P002" });

        var clearing = r.Sections.SelectMany(x => x.Items)
            .Where(i => i.AccountCode == Clearing).Sum(i => i.Balance);
        Assert.Equal(0m, clearing);
    }

    /// <summary>
    /// 已撥出但對方尚未入庫時，1450 的合併餘額＝在途存貨。它**不是**該被藏起來的殘值，
    /// 而是一個要標示出來的數字；畫面就是拿這個值顯示「在途存貨」。
    /// </summary>
    [Fact]
    public async Task ConsolidatedBalanceSheet_UnreceivedTransferRemainsAsInTransit()
    {
        const string Clearing = Modules.ACT.StandardAccounts.InterPlantClearingCode;
        Seed("202601", "P001",
            (Clearing, "廠際調撥往來", 800m, 0m), ("1401", "原料存貨", 0m, 800m));

        var r = await _bs.GetConsolidatedReportAsync("202601", new[] { "P001", "P002" });

        var clearing = r.Sections.SelectMany(x => x.Items)
            .Where(i => i.AccountCode == Clearing).Sum(i => i.Balance);
        Assert.Equal(800m, clearing);
    }

    /// <summary>廠別集合為空時回空報表，不得退化成「全公司」。</summary>
    [Fact]
    public async Task ConsolidatedBalanceSheet_NoPlants_ReturnsEmptyNotEverything()
    {
        Seed("202601", "P001", ("1100", "現金", 100m, 0m), ("3100", "股本", 0m, 100m));

        var r = await _bs.GetConsolidatedReportAsync("202601", Array.Empty<string>());

        Assert.Equal(0m, r.TotalAssets);
    }

    /// <summary>合併損益同理：只加總指定廠別，且單廠版與合併單廠一致。</summary>
    [Fact]
    public async Task ConsolidatedProfitLoss_SumsSelectedPlantsOnly()
    {
        Seed("202601", "P001", ("4100", "銷貨收入", 0m, 100m), ("1200", "應收帳款", 100m, 0m));
        Seed("202601", "P002", ("4100", "銷貨收入", 0m, 200m), ("1200", "應收帳款", 200m, 0m));

        var both = await _pl.GetConsolidatedReportAsync("202601", "202601", new[] { "P001", "P002" });
        var only = await _pl.GetConsolidatedReportAsync("202601", "202601", new[] { "P002" });

        Assert.Equal(300m, both.Revenue);
        Assert.Equal(200m, only.Revenue);
        Assert.Equal(200m, (await _pl.GetReportAsync("202601", "202601", "P002")).Revenue);
    }

    /// <summary>
    /// 合併畫面的分段標題與期間狀態是動態組鍵（"Section_" + 分段、"PeriodStatus_" + 狀態）查的，
    /// R1 只掃字面字串掃不到這批；漏補翻譯時畫面直接印出鍵名而不報錯。
    /// </summary>
    [Fact]
    public void 合併財報的動態組鍵在兩份resx都齊全()
    {
        var dir = Path.Combine(ConventionScan.RepoRoot(),
            "Resources", "Modules", "RPT", "Views", "ConsolidatedStatement");
        var zh = ConventionScan.ResxKeys(Path.Combine(dir, "Index.zh-TW.resx"));
        var en = ConventionScan.ResxKeys(Path.Combine(dir, "Index.en-US.resx"));

        var keys = new[] { "Assets", "Liabilities", "Equity" }.Select(c => "Section_" + c)
            .Concat(Enum.GetValues<PeriodStatus>().Select(s => "PeriodStatus_" + s));

        var missing = keys.Where(k => !zh.Contains(k) || !en.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "缺合併財報顯示名稱：" + string.Join("、", missing));
    }
}
