using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.BAS.Controllers;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using SmartFactory.MES.Tests.Conventions;
using Xunit;

namespace SmartFactory.MES.Tests.BAS;

/// <summary>
/// 會計科目主檔（原 <c>InvoiceAccount</c> 升級）。
///
/// <para>這裡驗的是**升級之後多出來的兩道閘門**：系統科目不可改性質／不可刪除，
/// 以及科目性質的分類與舊行為等價。CRUD 本身由 UITests 的 smoke 走過。</para>
/// </summary>
public class ChartOfAccountTests : TestBase
{
    private readonly Mock<IPermissionService> _perm = new();

    public ChartOfAccountTests()
    {
        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);
    }

    /// <summary>
    /// 一律走 AJAX 路徑（側面板實際使用的那一條）。非 AJAX 分支會 RedirectToAction，
    /// 而那需要 IUrlHelperFactory——在測試容器裡沒有，於是測試會紅在一個
    /// 與受測行為無關的例外上。
    /// </summary>
    private ChartOfAccountController New()
    {
        var ctx = GetControllerContextWithServices();
        ctx.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        return new(_context, GetMockExportService().Object, _perm.Object,
                   GetMockLocalizerFactory().Object)
               { ControllerContext = ctx };
    }

    private Mock<IStringLocalizerFactory> GetMockLocalizerFactory()
    {
        var factory = new Mock<IStringLocalizerFactory>();
        var loc = new Mock<IStringLocalizer>();
        loc.Setup(l => l[It.IsAny<string>()])
           .Returns<string>(k => new LocalizedString(k, k));
        factory.Setup(f => f.Create(It.IsAny<Type>())).Returns(loc.Object);
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<string>())).Returns(loc.Object);
        return factory;
    }

    /// <summary>取出 JsonResult 匿名物件的 success 旗標。</summary>
    private static bool SuccessOf(IActionResult result)
    {
        var value = Assert.IsType<JsonResult>(result).Value!;
        return (bool)value.GetType().GetProperty("success")!.GetValue(value)!;
    }

    private ChartOfAccount Add(string code, AccountType type, bool isSystem = false)
    {
        var a = new ChartOfAccount
        {
            AccountCode = code, AccountName = "測試" + code,
            AccountType = type, IsSystem = isSystem,
        };
        _context.Set<ChartOfAccount>().Add(a);
        _context.SaveChanges();
        return a;
    }

    // ── 系統科目的兩道閘門 ────────────────────────────────────────

    [Fact]
    public async Task 系統科目不可刪除()
    {
        Add("1401", AccountType.Asset, isSystem: true);

        var r = await New().DeleteConfirmed("1401");

        Assert.False(SuccessOf(r));
        Assert.NotNull(_context.Set<ChartOfAccount>().Find("1401"));   // 重點是資料還在
    }

    [Fact]
    public async Task 系統科目的性質_就算直接POST也改不動()
    {
        Add("1401", AccountType.Asset, isSystem: true);

        // 畫面已把性質做成唯讀，但唯讀是提示不是閘門——直接送一個改過的表單到得了這裡。
        await New().Edit("1401", new ChartOfAccount
        {
            AccountCode = "1401", AccountName = "被改過的名稱",
            AccountType = AccountType.OperatingExpense,
        });

        var rec = _context.Set<ChartOfAccount>().Find("1401")!;
        Assert.Equal(AccountType.Asset, rec.AccountType);   // 性質不動
        Assert.Equal("被改過的名稱", rec.AccountName);        // 名稱照改
    }

    [Fact]
    public async Task 自建科目的性質改得動()
    {
        Add("6201", AccountType.OperatingExpense);

        await New().Edit("6201", new ChartOfAccount
        {
            AccountCode = "6201", AccountName = "測試6201",
            AccountType = AccountType.OtherExpense,
        });

        Assert.Equal(AccountType.OtherExpense,
                     _context.Set<ChartOfAccount>().Find("6201")!.AccountType);
    }

    [Fact]
    public async Task 新建的科目不會是系統科目()
    {
        // IsSystem 不在 Bind 清單裡；就算硬塞也不該生效——
        // 否則使用者自建的科目會取得「不可刪」的保護，卻沒有任何自動分錄在用它。
        await New().Create(new ChartOfAccount
        {
            AccountCode = "9001", AccountName = "自建", AccountType = AccountType.OperatingExpense,
            IsSystem = true,
        });

        Assert.False(_context.Set<ChartOfAccount>().Find("9001")!.IsSystem);
    }

    [Fact]
    public async Task 被傳票引用過的科目不可刪除()
    {
        Add("6201", AccountType.OperatingExpense);
        _context.JournalEntries.Add(new Modules.ACT.Models.JournalEntry
        {
            JournalId = "JE001", PeriodId = "202609", PlantId = "P001",
            EntryDate = new DateOnly(2026, 9, 1),
            SourceDocType = "Manual",
            Lines =
            {
                new Modules.ACT.Models.JournalLine
                {
                    JournalId = "JE001", LineNo = 1,
                    AccountCode = "6201", AccountName = "測試6201",
                    Debit = 100m, AmountLocal = 100m,
                },
            },
        });
        _context.SaveChanges();

        var r = await New().DeleteConfirmed("6201");

        Assert.False(SuccessOf(r));
        Assert.NotNull(_context.Set<ChartOfAccount>().Find("6201"));
    }

    // ── 分類：升級後與升級前等價 ──────────────────────────────────

    [Theory]
    [InlineData("1401", AccountType.Asset)]
    [InlineData("2152", AccountType.Liability)]
    [InlineData("3100", AccountType.Equity)]
    [InlineData("4100", AccountType.Revenue)]
    [InlineData("5001", AccountType.Cogs)]
    [InlineData("6201", AccountType.OperatingExpense)]
    [InlineData("7100", AccountType.OtherIncome)]
    [InlineData("8100", AccountType.OtherExpense)]
    public void 主檔查無時退回代碼首碼(string code, AccountType expected)
        => Assert.Equal(expected, AccountTypeMap.FromLeadingDigit(code));

    [Theory]
    [InlineData("9001")]   // 9 不在 1–8
    [InlineData("A100")]
    [InlineData("")]
    public void 首碼不在範圍內時無法分類(string code)
        => Assert.Null(AccountTypeMap.FromLeadingDigit(code));

    [Fact]
    public async Task 主檔的設定勝過代碼首碼()
    {
        // 代碼是 6 開頭但主檔登記為營業外費用——這正是主檔存在的意義，
        // 否則「科目性質」欄位只是個顯示不出效果的裝飾。
        Add("6900", AccountType.OtherExpense);

        var map = await new AccountClassifier(_context).LoadAsync();

        Assert.Equal(AccountType.OtherExpense, map.Resolve("6900"));
    }

    [Fact]
    public async Task 停用的科目照樣分類得出來()
    {
        // 停用只影響「還能不能選它」，不影響「以前用它記過的帳現在算在哪一段」。
        var a = Add("6300", AccountType.OperatingExpense);
        a.IsActive = false;
        _context.SaveChanges();

        var map = await new AccountClassifier(_context).LoadAsync();

        Assert.Equal(AccountType.OperatingExpense, map.Resolve("6300"));
    }

    [Fact]
    public void 科目性質的顯示名稱在兩份resx都齊全()
    {
        // 畫面是以 "AccountType_" + 列舉值 動態組鍵查的，而 R1 只掃字面字串，
        // 掃不到這批——漏補翻譯時畫面會直接印出鍵名（AccountType_Cogs），不會報錯。
        var dir = Path.Combine(ConventionScan.RepoRoot(),
            "Resources", "Modules", "BAS", "Views", "ChartOfAccount");

        var missing = new List<string>();
        foreach (var view in new[] { "Index", "_ChartOfAccountForm" })
        {
            var zh = ConventionScan.ResxKeys(Path.Combine(dir, $"{view}.zh-TW.resx"));
            var en = ConventionScan.ResxKeys(Path.Combine(dir, $"{view}.en-US.resx"));
            foreach (var t in Enum.GetValues<AccountType>())
            {
                var key = "AccountType_" + t;
                if (!zh.Contains(key) || !en.Contains(key)) missing.Add($"{view}:{key}");
            }
        }

        Assert.True(missing.Count == 0, "缺科目性質顯示名稱：" + string.Join("、", missing));
    }

    // ── 種子完整性 ────────────────────────────────────────────────

    [Fact]
    public void StandardAccounts的每一個科目都在migration種子裡()
    {
        // StandardAccounts 加一個常數卻沒補種子時，畫面上那個科目就不存在，
        // 而自動分錄照樣會用它過帳——報表分類會退回代碼首碼，看起來完全正常。
        var migrations = Directory
            .EnumerateFiles(Path.Combine(ConventionScan.RepoRoot(), "Migrations"), "*.cs")
            .Select(File.ReadAllText)
            .Aggregate(new System.Text.StringBuilder(), (sb, t) => sb.Append(t))
            .ToString();

        var missing = StandardAccounts.All()
            .Where(a => !migrations.Contains($"'{a.Code}'"))
            .Select(a => $"{a.Code} {a.Name}")
            .ToList();

        Assert.True(missing.Count == 0,
            "下列標準科目沒有任何 migration 種子：" + string.Join("、", missing));
    }

    [Fact]
    public void StandardAccounts_All抓得到全部成對常數()
    {
        var all = StandardAccounts.All();
        Assert.True(all.Count >= 20, $"只抓到 {all.Count} 個，反射條件可能壞了");
        // 代碼唯一——兩個常數指到同一個代碼，種子會撞主鍵
        Assert.Equal(all.Count, all.Select(a => a.Code).Distinct().Count());
    }
}
