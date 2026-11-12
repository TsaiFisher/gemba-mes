using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Reporting;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using Xunit;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// 試算表（2026-09-23）。
///
/// <para><b>這支工具存在的理由就是那條合計等式</b>：<c>DataAuditService.AuditJournalBalanceAsync</c>
/// 驗的是逐傳票借貸平衡，而「每張傳票都平衡」不等於「科目餘額是對的」。所以本檔的重點
/// 不是欄位有沒有填對，而是<b>期初＋本期＝期末</b>與<b>借方合計＝貸方合計</b>這兩條在
/// 各種期間切法下都成立，以及不平衡時真的會被標出來。</para>
/// </summary>
public class TrialBalanceToolTests : TestBase
{
    private const string Plant = "P001";

    public TrialBalanceToolTests()
    {
        _context.CurrentPlantId = Plant;

        // 科目主檔：資產（借方為正）與收入（貸方為正）各一，用來驗正負號慣例。
        _context.Set<ChartOfAccount>().AddRange(
            new ChartOfAccount { AccountCode = "1101", AccountName = "現金",
                                 AccountType = AccountType.Asset,   IsActive = true },
            new ChartOfAccount { AccountCode = "4101", AccountName = "銷貨收入",
                                 AccountType = AccountType.Revenue, IsActive = true });

        // 202608：現金 1000 / 銷貨收入 1000
        AddEntry("J1", "202608", ("1101", 1000m, 0m), ("4101", 0m, 1000m));
        // 202609：現金 500 / 銷貨收入 500
        AddEntry("J2", "202609", ("1101",  500m, 0m), ("4101", 0m,  500m));

        _context.SaveChanges();
    }

    private void AddEntry(string id, string period, params (string Acc, decimal Dr, decimal Cr)[] lines)
    {
        var e = new JournalEntry
        {
            JournalId = id, PeriodId = period, PlantId = Plant,
            EntryDate = new DateOnly(int.Parse(period[..4]), int.Parse(period[4..]), 1),
        };
        var no = 0;
        foreach (var (acc, dr, cr) in lines)
        {
            e.Lines.Add(new JournalLine
            {
                JournalId = id, LineNo = ++no, AccountCode = acc,
                AccountName = acc == "1101" ? "現金" : "銷貨收入",
                Debit = dr, Credit = cr,
            });
        }
        _context.JournalEntries.Add(e);
    }

    private TrialBalanceTool BuildTool() => new(_context, new AccountClassifier(_context));

    /// <summary>
    /// 借方合計必須等於貸方合計。這是試算表唯一不可妥協的斷言——
    /// 它不成立就代表這段期間的分錄有問題，而畫面會擋在關帳之前提醒。
    /// </summary>
    [Fact]
    public async Task 借貸合計相等且標示為平衡()
    {
        var vm = await BuildTool().ExecuteAsync(
            new TrialBalanceRequest { PlantId = Plant, ToPeriod = "202609" }, user: null);

        Assert.Equal(vm.TotalDebit, vm.TotalCredit);
        Assert.Equal(0m, vm.Difference);
        Assert.True(vm.IsBalanced);
    }

    /// <summary>
    /// <b>期初 ＋ 本期 ＝ 期末</b>，而且期初取的是**起期之前**的累計。
    ///
    /// <para>只查 202609 時，202608 那張要落在期初而不是本期——分不清這兩段的話，
    /// 期末數字照樣算得出來（因為總量不變），只是期初與本期對不起來，
    /// 而那正是試算表最容易寫錯、也最看不出來的地方。</para>
    /// </summary>
    [Fact]
    public async Task 期初取起期之前的累計而本期只含區間內()
    {
        var vm = await BuildTool().ExecuteAsync(
            new TrialBalanceRequest { PlantId = Plant, FromPeriod = "202609", ToPeriod = "202609" },
            user: null);

        var cash = Assert.Single(vm.Rows, r => r.AccountCode == "1101");
        Assert.Equal(1000m, cash.OpeningBalance);   // 202608 那張
        Assert.Equal( 500m, cash.PeriodDebit);      // 202609 那張
        Assert.Equal(   0m, cash.PeriodCredit);
        Assert.Equal(1500m, cash.ClosingBalance);
        Assert.Equal(cash.OpeningBalance + cash.PeriodDebit - cash.PeriodCredit, cash.ClosingBalance);
    }

    /// <summary>
    /// 起期留空＝從有史以來，期初因而為 0，本期涵蓋全部。
    /// 與上一支合起來才分得出「期初算對了」與「期初永遠是 0」。
    /// </summary>
    [Fact]
    public async Task 起期留空時期初為零且本期涵蓋全部()
    {
        var vm = await BuildTool().ExecuteAsync(
            new TrialBalanceRequest { PlantId = Plant, ToPeriod = "202609" }, user: null);

        var cash = Assert.Single(vm.Rows, r => r.AccountCode == "1101");
        Assert.Equal(   0m, cash.OpeningBalance);
        Assert.Equal(1500m, cash.PeriodDebit);
        Assert.Equal(1500m, cash.ClosingBalance);
    }

    /// <summary>
    /// 正負號由科目性質決定：資產（借方為正）借餘為正，收入（貸方為正）貸餘也要是正的。
    /// 兩邊都用「借－貸」的話，收入會整排變成負數——看起來像虧損，其實是符號寫反。
    /// </summary>
    [Fact]
    public async Task 貸方為正的科目餘額不得為負()
    {
        var vm = await BuildTool().ExecuteAsync(
            new TrialBalanceRequest { PlantId = Plant, ToPeriod = "202609" }, user: null);

        var revenue = Assert.Single(vm.Rows, r => r.AccountCode == "4101");
        Assert.Equal(1500m, revenue.ClosingBalance);
        Assert.Equal(1500m, revenue.PeriodCredit);
    }

    /// <summary>
    /// 借貸不平衡時必須被標出來。**這一支是整個守門的核心**：前面幾支測的是
    /// 平衡時算得對，但一張永遠回報「平衡」的試算表比沒有試算表更糟——
    /// 它會讓人放心關帳。
    /// </summary>
    [Fact]
    public async Task 分錄不平衡時標示為不平衡並給出差額()
    {
        // 單邊分錄：借 200、沒有對應貸方。正常入口產生不出來，但資料修補、
        // 匯入或過帳中斷都可能留下這種殘骸，而試算表就是用來看見它的。
        AddEntry("J-BAD", "202609", ("1101", 200m, 0m));
        _context.SaveChanges();

        var vm = await BuildTool().ExecuteAsync(
            new TrialBalanceRequest { PlantId = Plant, ToPeriod = "202609" }, user: null);

        Assert.False(vm.IsBalanced);
        Assert.Equal(200m, vm.Difference);
    }

    /// <summary>
    /// 廠別隔離：別廠的分錄不得混進來。<c>JournalLines</c> 的查詢用了
    /// <c>IgnoreQueryFilters</c>（理由見工具註解），所以廠別條件是**手寫**的——
    /// 手寫的條件就是會被漏掉的條件，故釘一支。
    /// </summary>
    [Fact]
    public async Task 別廠的分錄不進本廠試算表()
    {
        var other = new JournalEntry
        {
            JournalId = "J-OTHER", PeriodId = "202609", PlantId = "P999",
            EntryDate = new DateOnly(2026, 9, 1),
        };
        other.Lines.Add(new JournalLine
        {
            JournalId = "J-OTHER", LineNo = 1, AccountCode = "1101",
            AccountName = "現金", Debit = 77777m, Credit = 0m,
        });
        _context.JournalEntries.Add(other);
        _context.SaveChanges();

        var vm = await BuildTool().ExecuteAsync(
            new TrialBalanceRequest { PlantId = Plant, ToPeriod = "202609" }, user: null);

        var cash = Assert.Single(vm.Rows, r => r.AccountCode == "1101");
        Assert.Equal(1500m, cash.ClosingBalance);   // 不含別廠那 77777
    }
}
