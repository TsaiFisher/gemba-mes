using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.ACT.Controllers;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.ACT.ViewModels;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// 手動傳票畫面。
///
/// <para><b>這個畫面到 2026-09-21 才存在，但服務早就寫好了</b>：
/// <c>CreateManualEntryAsync</c> 有實作、有測試，卻**沒有任何產品程式呼叫它**——
/// 只有測試在用。於是「會計自己打一筆調整分錄」這件事在系統裡做不到。</para>
///
/// <para>這裡驗的是畫面這一層才有的閘門（可手動選用、一行只能一邊、至少兩行），
/// 以及借貸不平衡與期間已關帳時**不會變成例外頁**而是回到表單。
/// 期間鎖與借貸平衡本身由 <c>JournalEntryServiceTests</c> 守。</para>
/// </summary>
public class ManualJournalControllerTests : TestBase
{
    private const string PlantId = "P001";
    private static readonly DateOnly Today = new(2026, 9, 21);

    private readonly Mock<IUserContext>       _user = new();
    private readonly Mock<IPermissionService> _perm = new();
    private readonly Mock<IDateTimeService>   _dt   = new();
    private int _seq;

    public ManualJournalControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _user.Setup(u => u.PlantId).Returns(PlantId);
        _user.Setup(u => u.UserId).Returns("user1");
        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);
        _dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(Today);
        _dt.Setup(d => d.Now).Returns(new DateTime(2026, 9, 21, 9, 0, 0));

        SeedAccount("6201", "水電瓦斯費", AccountType.OperatingExpense, manual: true);
        SeedAccount("1130", "銀行存款",   AccountType.Asset,            manual: true);
        SeedAccount("1401", "原料存貨",   AccountType.Asset,            manual: false);   // 系統維護，不開放手選
    }

    private void SeedAccount(string code, string name, AccountType type, bool manual)
    {
        _context.Set<ChartOfAccount>().Add(new ChartOfAccount
        {
            AccountCode = code, AccountName = name, AccountType = type,
            AllowManualEntry = manual, IsActive = true,
        });
        _context.SaveChanges();
    }

    private void SeedPeriod(PeriodStatus status)
    {
        _context.AccountingPeriods.Add(new AccountingPeriod
        {
            PeriodId = "202609", PlantId = PlantId, Year = 2026, Month = 9,
            StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2026, 9, 30),
            Status = status,
        });
        _context.SaveChanges();
    }

    private JournalEntryController New()
    {
        // 單號序列跨 controller 共用：同一測試裡建原傳票、再建沖銷傳票時不能撞號
        var docNo = new Mock<IDocNumberService>();
        docNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(),
                                     It.IsAny<CancellationToken>()))
             .ReturnsAsync(() => $"JE{++_seq:0000}");

        var periods = new AccountingPeriodService(_context, null);
        var journal = new JournalEntryService(_context, docNo.Object, periods);

        var ctx = GetControllerContextWithServices();
        ctx.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        return new JournalEntryController(
            _context, _perm.Object, _user.Object,
            GetMockExportService().Object, _dt.Object, journal,
            GetMockLocalizer<ProjectZSharedResource>().Object)
        { ControllerContext = ctx };
    }

    private static ManualJournalViewModel Vm(params (string Code, decimal Debit, decimal Credit)[] lines)
        => new()
        {
            EntryDate = Today,
            Description = "測試調整",
            Lines = lines.Select(l => new ManualJournalLineViewModel
            {
                AccountCode = l.Code, Debit = l.Debit, Credit = l.Credit,
            }).ToList(),
        };

    private static bool SuccessOf(IActionResult r)
    {
        var v = Assert.IsType<JsonResult>(r).Value!;
        return (bool)v.GetType().GetProperty("success")!.GetValue(v)!;
    }

    // ── 正常路徑 ────────────────────────────────────────────────

    [Fact]
    public async Task 平衡的兩行分錄存得起來()
    {
        SeedPeriod(PeriodStatus.Open);

        var r = await New().Create(Vm(("6201", 1000m, 0m), ("1130", 0m, 1000m)));

        Assert.True(SuccessOf(r));
        var entry = await _context.JournalEntries.Include(j => j.Lines).SingleAsync();
        Assert.Equal(JournalEntryType.Manual, entry.Type);
        Assert.Equal("Manual", entry.SourceDocType);
        Assert.Equal(2, entry.Lines.Count);
        // AmountLocal 要填：不填的話它是 0，而那是本位幣金額欄位
        Assert.Equal(1000m, entry.Lines.First(l => l.Debit > 0).AmountLocal);
        Assert.Equal("水電瓦斯費", entry.Lines.First(l => l.Debit > 0).AccountName);
    }

    [Fact]
    public async Task 空白行被丟掉_不算進行數()
    {
        SeedPeriod(PeriodStatus.Open);
        var vm = Vm(("6201", 1000m, 0m), ("1130", 0m, 1000m));
        vm.Lines.Add(new ManualJournalLineViewModel());   // 畫面預設多給的空白行

        var r = await New().Create(vm);

        Assert.True(SuccessOf(r));
        Assert.Equal(2, (await _context.JournalEntries.Include(j => j.Lines).SingleAsync()).Lines.Count);
    }

    // ── 成本中心＝本廠組織單位（2026-10-05 由手填改下拉）────────────────────

    [Fact]
    public async Task 成本中心必須是本廠組織單位_手填文字被擋下_選組織單位存的是Id()
    {
        SeedPeriod(PeriodStatus.Open);
        _context.OrganizationUnits.Add(new SmartFactory.MES.Models.OrganizationUnit { Id = "OU-ADM", Name = "管理部", PlantId = PlantId, Path = "/OU-ADM/" });
        _context.OrganizationUnits.Add(new SmartFactory.MES.Models.OrganizationUnit { Id = "OU-OTHER", Name = "他廠部門", PlantId = "P999", Path = "/OU-OTHER/" });
        _context.SaveChanges();

        var bad = Vm(("6201", 1000m, 0m), ("1130", 0m, 1000m));
        bad.Lines[0].CostCenter = "管理部";        // 舊畫面的手填文字
        Assert.IsType<PartialViewResult>(await New().Create(bad));

        var otherPlant = Vm(("6201", 1000m, 0m), ("1130", 0m, 1000m));
        otherPlant.Lines[0].CostCenter = "OU-OTHER";
        Assert.IsType<PartialViewResult>(await New().Create(otherPlant));
        Assert.Empty(_context.JournalEntries);

        var ok = Vm(("6201", 1000m, 0m), ("1130", 0m, 1000m));
        ok.Lines[0].CostCenter = "OU-ADM";
        Assert.True(SuccessOf(await New().Create(ok)));
        Assert.Equal("OU-ADM", (await _context.JournalEntries.Include(j => j.Lines).SingleAsync()).Lines.First(l => l.Debit > 0).CostCenter);
    }

    /// <summary>
    /// 部門沒填廠別、繼承上層的情形（ProjectZ 庫：業務部、總務部掛在「總經理」(ADM) 底下，自己的 PlantId 是空的）。
    /// 2026-10-07 前只比對自己的 PlantId → 下拉只剩總經理，選部門存檔也被擋。
    /// 全公司層（PlantId 空、往上也找不到）與他廠底下的部門仍不可選。
    /// </summary>
    [Fact]
    public async Task 成本中心_繼承上層廠別的部門可選_全公司層與他廠部門不可選()
    {
        SeedPeriod(PeriodStatus.Open);
        _context.OrganizationUnits.AddRange(
            new SmartFactory.MES.Models.OrganizationUnit { Id = "ROOT",  Name = "總公司", PlantId = null,  Path = "總公司" },
            new SmartFactory.MES.Models.OrganizationUnit { Id = "GM",    Name = "總經理", PlantId = PlantId, Path = "總經理", ParentId = "ROOT" },
            new SmartFactory.MES.Models.OrganizationUnit { Id = "SALES", Name = "業務部", PlantId = null,  Path = "總經理/業務部", ParentId = "GM" },
            new SmartFactory.MES.Models.OrganizationUnit { Id = "P9",    Name = "他廠",   PlantId = "P999", Path = "總經理/他廠", ParentId = "GM" },
            new SmartFactory.MES.Models.OrganizationUnit { Id = "P9-QC", Name = "他廠品保", PlantId = null, Path = "總經理/他廠/品保", ParentId = "P9" });
        _context.SaveChanges();
        // 新增時 DbContext 會把空的 PlantId 補成目前廠別（??= CurrentPlantId），要再清回空值才是庫裡的樣子
        foreach (var id in new[] { "ROOT", "SALES", "P9-QC" })
            _context.OrganizationUnits.Find(id)!.PlantId = null;
        _context.SaveChanges();

        foreach (var cc in new[] { "ROOT", "P9-QC" })
        {
            var vm = Vm(("6201", 1000m, 0m), ("1130", 0m, 1000m));
            vm.Lines[0].CostCenter = cc;
            var r = await New().Create(vm);
            Assert.True(r is PartialViewResult,
                $"{cc} 不該可選：{System.Text.Json.JsonSerializer.Serialize((r as JsonResult)?.Value)}");
        }

        var ok = Vm(("6201", 1000m, 0m), ("1130", 0m, 1000m));
        ok.Lines[0].CostCenter = "SALES";
        Assert.True(SuccessOf(await New().Create(ok)));
    }

    // ── 沖銷（傳票不改不刪，更正一律沖銷；2026-10-05 使用者裁決）──────────────

    private async Task<string> CreateManualAsync()
    {
        var r = await New().Create(Vm(("6201", 1000m, 0m), ("1130", 0m, 1000m)));
        Assert.True(SuccessOf(r));
        return (await _context.JournalEntries.OrderByDescending(j => j.CreatedAt).FirstAsync(j => j.SourceDocType == "Manual")).JournalId;
    }

    [Fact]
    public async Task 沖銷_借貸對調並連回原傳票_原傳票不動_不可沖第二次也不可沖沖銷傳票()
    {
        SeedPeriod(PeriodStatus.Open);
        var orig = await CreateManualAsync();

        Assert.True(SuccessOf(await New().Reverse(orig, Today)));

        var rev = await _context.JournalEntries.Include(j => j.Lines)
            .SingleAsync(j => j.SourceDocType == JournalEntryService.ManualReversalSourceType);
        Assert.Equal(orig, rev.SourceDocId);
        Assert.Equal(JournalEntryType.Manual, rev.Type);
        Assert.Equal(1000m, rev.Lines.Single(l => l.AccountCode == "6201").Credit);
        Assert.Equal(1000m, rev.Lines.Single(l => l.AccountCode == "1130").Debit);
        var original = await _context.JournalEntries.Include(j => j.Lines).SingleAsync(j => j.JournalId == orig);
        Assert.Equal(1000m, original.Lines.Single(l => l.AccountCode == "6201").Debit);

        Assert.False(SuccessOf(await New().Reverse(orig, Today)));            // 已沖銷過
        Assert.False(SuccessOf(await New().Reverse(rev.JournalId, Today)));   // 沖銷傳票本身
        Assert.Equal(1, await _context.JournalEntries.CountAsync(j => j.SourceDocType == JournalEntryService.ManualReversalSourceType));
    }

    [Fact]
    public async Task 沖銷_自動分錄不可從這裡沖_期間已關帳也擋下()
    {
        SeedPeriod(PeriodStatus.Open);
        _context.JournalEntries.Add(new JournalEntry
        {
            JournalId = "AUTO-1", PeriodId = "202609", EntryDate = Today, SourceDocType = "APInvoice", SourceDocId = "AP1",
            Type = JournalEntryType.Automatic, PlantId = PlantId,
            Lines = { new JournalLine { LineNo = 1, AccountCode = "6201", Debit = 5 }, new JournalLine { LineNo = 2, AccountCode = "1130", Credit = 5 } },
        });
        _context.SaveChanges();
        Assert.False(SuccessOf(await New().Reverse("AUTO-1", Today)));

        var orig = await CreateManualAsync();
        _context.AccountingPeriods.Single().Status = PeriodStatus.Closed;
        _context.SaveChanges();
        Assert.False(SuccessOf(await New().Reverse(orig, Today)));
        Assert.Equal(0, await _context.JournalEntries.CountAsync(j => j.SourceDocType == JournalEntryService.ManualReversalSourceType));
    }

    // ── 畫面層的閘門 ─────────────────────────────────────────────

    [Fact]
    public async Task 未開放手動選用的科目被擋下()
    {
        SeedPeriod(PeriodStatus.Open);

        // 1401 原料存貨的餘額由庫存過帳維護；手動打一筆進去帳面仍平衡，
        // 但會與庫存子帳對不起來，且要等稽核跑過才看得到。
        var r = await New().Create(Vm(("1401", 1000m, 0m), ("1130", 0m, 1000m)));

        Assert.IsType<PartialViewResult>(r);
        Assert.Empty(_context.JournalEntries);
    }

    [Fact]
    public async Task 同一行同時填借貸被擋下()
    {
        SeedPeriod(PeriodStatus.Open);

        // 借貸合計仍然平衡（1000/1000），服務層的平衡守門攔不到這種誤填。
        var r = await New().Create(Vm(("6201", 1000m, 1000m), ("1130", 0m, 0m)));

        Assert.IsType<PartialViewResult>(r);
        Assert.Empty(_context.JournalEntries);
    }

    [Fact]
    public async Task 只有一行被擋下()
    {
        SeedPeriod(PeriodStatus.Open);

        var r = await New().Create(Vm(("6201", 1000m, 0m)));

        Assert.IsType<PartialViewResult>(r);
        Assert.Empty(_context.JournalEntries);
    }

    [Fact]
    public async Task 借貸不平衡退回表單而不是擲例外()
    {
        SeedPeriod(PeriodStatus.Open);

        var r = await New().Create(Vm(("6201", 1000m, 0m), ("1130", 0m, 900m)));

        Assert.IsType<PartialViewResult>(r);
        Assert.Empty(_context.JournalEntries);
    }

    [Fact]
    public async Task 金額為負被擋下()
    {
        SeedPeriod(PeriodStatus.Open);

        // -100/-100 借貸合計相等，服務層照樣放行——負數分錄在報表上是反向的，
        // 而且沒有任何地方會問「為什麼這筆是負的」。
        var r = await New().Create(Vm(("6201", -100m, 0m), ("1130", 0m, -100m)));

        Assert.IsType<PartialViewResult>(r);
        Assert.Empty(_context.JournalEntries);
    }

    // ── 期間鎖 ──────────────────────────────────────────────────

    [Fact]
    public async Task 期間已關帳時回報錯誤而不是五百()
    {
        SeedPeriod(PeriodStatus.Closed);

        var r = await New().Create(Vm(("6201", 1000m, 0m), ("1130", 0m, 1000m)));

        Assert.False(SuccessOf(r));
        Assert.Empty(_context.JournalEntries);
    }

    [Fact]
    public async Task 月結中不接受一般手動分錄()
    {
        // Closing 期間只收 Adjustment；手動傳票是 Manual 型。
        SeedPeriod(PeriodStatus.Closing);

        var r = await New().Create(Vm(("6201", 1000m, 0m), ("1130", 0m, 1000m)));

        Assert.False(SuccessOf(r));
        Assert.Empty(_context.JournalEntries);
    }

    [Fact]
    public async Task 沒有期間列時仍可建立_與過帳閘門的寬鬆模式一致()
    {
        // AccountingPeriodService.AssertCanPostAsync 對「查無期間」是明確放行的；
        // 手動傳票若在這裡改成擋下，同一個日期會出現「庫存過得了帳、傳票打不了」的矛盾。
        var r = await New().Create(Vm(("6201", 1000m, 0m), ("1130", 0m, 1000m)));

        Assert.True(SuccessOf(r));
        Assert.Single(_context.JournalEntries);
    }

    [Fact]
    public async Task 空白表單預設兩行且日期取廠別當地日()
    {
        var r = await New().Create();

        var vm = Assert.IsType<ManualJournalViewModel>(Assert.IsType<PartialViewResult>(r).Model);
        Assert.Equal(2, vm.Lines.Count);
        Assert.Equal(Today, vm.EntryDate);
    }
}
