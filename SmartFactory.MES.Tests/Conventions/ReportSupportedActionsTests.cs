using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Conventions;

/// <summary>
/// 選單 <see cref="MenuItem.SupportedActions"/> 的守門測試（2026-08-14 裁決，
/// 覆寫 <c>report-permission-inventory.md</c> §8.1-終）。
///
/// <para>
/// 這兩條不變式失效時都**沒有任何錯誤訊息**：
/// 多宣告一個動詞只是在權限維護畫面長出一個永遠沒有作用的勾選框；
/// 少宣告一個動詞則是讓某個真的會被檢查的權限**再也無法授予或撤銷**——
/// 而且下一次有人存檔該角色時，那個沒被畫出來的勾選框會綁定成 false，靜默收走權限。
/// </para>
///
/// <para>
/// 兩條不變式：
/// ① 宣告了任何動詞就必須包含 <c>Read</c>。所有畫面的 Index 閘門都是 <c>CanRead</c>，
///    沒有它連畫面都進不去，其餘動詞的按鈕也就永遠不會出現——「有 Print 沒 Read」
///    在本系統不可能成立。
/// ② 宣告的動詞集合必須等於該 Controller 實際檢查過的動詞集合（含只餵 ViewBag 決定
///    畫不畫按鈕的那些）。這一條同時擋住「漏宣告」與「宣告了不存在的動詞」。
/// </para>
/// </summary>
public class ReportSupportedActionsTests
{
    /// <summary>預設值＝五動詞全開。等於預設者視為「未宣告」，不進 ② 的比對。</summary>
    private static readonly string[] Default = ["Read", "Create", "Update", "Delete", "Print"];

    /// <summary>
    /// 任何 <c>CanRead</c>／<c>CanCreate</c>… 呼叫，不論是守門（<c>return Forbid()</c>）
    /// 還是只餵 <c>ViewBag</c>。兩者都算「這個畫面真的會用到這個動詞」——
    /// 只餵 ViewBag 的動詞若不宣告，管理者就沒有辦法把那顆按鈕關掉。
    /// 涵蓋兩種呼叫式樣：<c>_permissionService.CanX(</c> 與 <c>Permissions.CanX(</c>
    /// （<c>ModuleControllerBase</c>）。
    /// </summary>
    private static readonly Regex VerbRx = new(
        @"(?:_permissionService|Permissions|_permission)\.Can(Read|Create|Update|Delete|Print)\s*\(",
        RegexOptions.Compiled);

    private static List<MenuItem> Menu()
    {
        var localizer = new Mock<IStringLocalizer<ProjectZSharedResource>>();
        localizer.Setup(l => l[It.IsAny<string>()])
                 .Returns((string k) => new LocalizedString(k, k));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:ReportTools:Enabled"] = "true",
            }).Build();
        return new ProjectZMenuContributor(localizer.Object, config).GetMenuItems();
    }

    private static IEnumerable<MenuItem> Flatten(IEnumerable<MenuItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var sub in Flatten(item.SubItems)) yield return sub;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // ① Read 是其餘動詞的前提
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void 宣告了動詞的畫面一定要包含Read()
    {
        var offenders = Flatten(Menu())
            .Where(i => !string.IsNullOrEmpty(i.MenuKey))
            .Where(i => i.SupportedActions.Count > 0)
            .Where(i => !i.SupportedActions.Contains("Read"))
            .Select(i => $"{i.MenuKey}: [{string.Join(", ", i.SupportedActions)}]")
            .ToList();

        Assert.True(offenders.Count == 0,
            "以下畫面宣告了動詞卻沒有 Read。畫面閘門是 CanRead，沒有它連 Index 都進不去，\n" +
            "其餘動詞的按鈕永遠不會出現——這種宣告只會在權限維護畫面誤導維護者：\n  "
            + string.Join("\n  ", offenders));
    }

    // ────────────────────────────────────────────────────────────────
    // ② 宣告的動詞集合 == Controller 實際用到的動詞集合
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void 宣告的動詞集合等於Controller實際檢查的動詞集合()
    {
        var root = ConventionScan.RepoRoot();
        var declared = Flatten(Menu())
            .Where(i => !string.IsNullOrEmpty(i.MenuKey) && !string.IsNullOrEmpty(i.Controller))
            .Where(i => !i.SupportedActions.SequenceEqual(Default))
            .ToList();

        Assert.True(declared.Count >= 52,
            $"宣告動詞的畫面只有 {declared.Count} 支，少於預期的 52 支報表 ＋ 3 支報表工具畫面。" +
            "多半是 ProjectZMenuContributor.ReportActions 的鍵被改壞或沒有套用。");

        var mismatches = new List<string>();

        foreach (var item in declared)
        {
            // 允許 partial controller（XxxController.Export.cs 之類）
            var files = Directory
                .EnumerateFiles(root, item.Controller + "Controller*.cs", SearchOption.AllDirectories)
                .Where(f => f.Contains(Path.DirectorySeparatorChar + "Controllers"
                                       + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .ToList();

            if (files.Count == 0)
            {
                mismatches.Add($"{item.MenuKey}: 找不到 {item.Controller}Controller.cs");
                continue;
            }

            var actual = files
                .SelectMany(f => VerbRx.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
                .ToHashSet(StringComparer.Ordinal);

            var want = item.SupportedActions.ToHashSet(StringComparer.Ordinal);
            if (actual.SetEquals(want)) continue;

            mismatches.Add(
                $"{item.MenuKey}: 宣告 [{string.Join(",", want.OrderBy(x => x, StringComparer.Ordinal))}]"
                + $" ≠ 實際 [{string.Join(",", actual.OrderBy(x => x, StringComparer.Ordinal))}]");
        }

        Assert.True(mismatches.Count == 0,
            "SupportedActions 與 Controller 實際檢查的動詞不一致。\n" +
            "多宣告 → 權限維護畫面長出沒有作用的勾選框；\n" +
            "少宣告 → 該權限無法授予或撤銷，且下次存檔角色時會被靜默設成 false：\n  "
            + string.Join("\n  ", mismatches));
    }

    // ────────────────────────────────────────────────────────────────
    // ③ 回歸鎖：報表不得再用 CanCreate 當 Index 閘門
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 2026-08-14 裁決：報表沒有「新增」語意，不得拿 <c>CanCreate</c> 當
    /// 「收窄可見範圍」的代理（可見範圍改由 <c>MenuPermission</c> 逐角色表達）。
    ///
    /// <para>
    /// 這支測試釘的是**方向**：把某支報表的 Index 閘門改回 <c>CanCreate</c> 是收緊，
    /// 不會有任何錯誤訊息，只會讓原本看得到的人開始看不到。
    /// 名單刻意寫死而不是動態推導——動態推導會隨著 ReportActions 一起被改壞。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("INV", "StockShortage")]
    [InlineData("MFG", "DefectRate")]
    [InlineData("MFG", "WorkOrderProgress")]
    [InlineData("ORD", "DeliveryRate")]
    [InlineData("ORD", "OrderOverdue")]
    [InlineData("ORD", "ReturnRate")]
    [InlineData("PUR", "PurchaseOverdue")]
    public void 七支報表的Index閘門已改為CanRead(string module, string key)
    {
        var file = Path.Combine(ConventionScan.RepoRoot(),
                                "Modules", module, "Controllers", key + "Controller.cs");
        var text = File.ReadAllText(file);

        var gate = new Regex(@"_permissionService\.(Can\w+)\(MenuKey\)\)\s*return Forbid\(\)")
            .Match(text);

        Assert.True(gate.Success, $"{key}Controller 找不到 Index 閘門");
        Assert.Equal("CanRead", gate.Groups[1].Value);
    }
}
