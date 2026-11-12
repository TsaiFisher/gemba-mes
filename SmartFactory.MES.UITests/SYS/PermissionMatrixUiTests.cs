using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.UITests.SYS
{
    /// <summary>
    /// 權限矩陣側面板（<c>/Permission</c> → 每個角色一個 <c>_PermissionPanel</c>）。
    ///
    /// <para><b>這一頁決定了誰看得到什麼</b>，而它的渲染邏輯有一個靜態測試看不到的形狀：
    /// 表單欄位名的索引 <c>permissions[i]</c> 來自<b>扁平清單</b>
    /// （<c>GetFlattenedAccessibleMenus()</c>），列卻是照<b>選單樹</b>
    /// （<c>GetFullMenu()</c>）分群渲染的，中間靠一張 <c>MenuKey → index</c> 的對照表接起來。
    /// 兩份清單一旦對不上，症狀不是錯誤訊息，而是<b>某個模組整組從矩陣上消失</b>
    /// （<c>if (!leafItems.Any()) continue;</c>）——那個模組的權限從此沒有人能授予，
    /// 而畫面看起來完全正常。</para>
    ///
    /// <para>🚨 全部唯讀：只開面板、只在瀏覽器裡切勾選框，<b>絕不按儲存</b>。
    /// 按下去會把該角色的權限整批 <c>RemoveRange</c> 再重建
    /// （<c>PermissionController.Edit</c>），:5110 與 ProjectZ 庫是多 session 共用的。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class PermissionMatrixUiTests
    {
        private readonly UiTestFixture _fixture;
        private readonly ITestOutputHelper _out;

        /// <summary>
        /// 矩陣至少要有這麼多個可授權項目，否則判定為「面板根本沒渲染出來」。
        ///
        /// <para>🚨 這道下限與選單巡覽那支是同一個理由：清單從 DOM 撈，
        /// selector 一旦失效就會撈到空清單，而<b>空清單驗起來永遠是綠的</b>。
        /// 實際值遠高於此（選單巡覽數到 145 個可走訪頁面）。</para>
        /// </summary>
        private const int MinimumRows = 60;

        /// <summary>逐列資料在 JS 與 C# 之間的分隔字元，選一個不會出現在選單標題裡的。</summary>
        private const string Sep = "|::|";

        public PermissionMatrixUiTests(UiTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _out = output;
        }

        private async Task<IPage> OpenFirstRolePanelAsync()
        {
            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);
            await page.GotoAsync($"{_fixture.BaseUrl}/Permission?searched=true",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            var button = page.Locator("button[data-action='openPermissionPanel']").First;
            await Assertions.Expect(button).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await button.ClickAsync();

            // 等的是面板**內容**（AJAX 回來的 partial），不是 offcanvas 容器——
            // 容器一開始就在 DOM 上，等它等於沒等。
            await Assertions.Expect(page.Locator("#permissionPanelForm"))
                .ToBeVisibleAsync(new() { Timeout = 20_000 });
            return page;
        }

        /// <summary>
        /// 面板真的展開，而且<b>每一個可授權項目都拿得到至少一個開關</b>。
        ///
        /// <para>一列五格全空代表 <c>SupportedActions</c> 是空的——那一列看得到、
        /// 卻一個動詞都勾不了，是「權限授不出去」最安靜的形狀。</para>
        /// </summary>
        [Fact]
        public async Task 權限矩陣的側面板展開_而且每一列都至少有一個開關()
        {
            var page = await OpenFirstRolePanelAsync();

            var rows = page.Locator("#permissionPanelForm tr.module-item-row");
            var rowCount = await rows.CountAsync();
            _out.WriteLine($"可授權項目列數：{rowCount}");
            Assert.True(rowCount >= MinimumRows,
                $"權限矩陣只渲染出 {rowCount} 列（下限 {MinimumRows}）——" +
                "面板沒展開、AJAX 沒回來，或 selector 失效了。");

            var emptyRows = new List<string>();
            for (int i = 0; i < rowCount; i++)
            {
                var row = rows.Nth(i);
                if (await row.Locator("input[type='checkbox']").CountAsync() == 0)
                    emptyRows.Add((await row.Locator("td").First.InnerTextAsync()).Trim());
            }
            Assert.True(emptyRows.Count == 0,
                $"有 {emptyRows.Count} 列一個開關都沒有，權限授不出去：{string.Join("、", emptyRows)}");
        }

        /// <summary>
        /// 🚨 <b>每一列各用自己的表單索引，不得有兩列共用同一個 <c>permissions[i]</c></b>。
        ///
        /// <para>索引是 <c>MenuKey → 扁平清單位置</c> 查出來的。兩個選單項若拿到同一個索引
        /// （扁平清單裡 MenuKey 重覆，例如同一個畫面被兩個 contributor 各登錄一次），
        /// POST 時後面那一列會<b>整個蓋掉</b>前面那一列的勾選——使用者勾了 A、
        /// 存完變成 B 的權限，畫面上看不出任何異狀。</para>
        ///
        /// <para>這件事只有在渲染完成的 DOM 上看得出來：索引是 Razor 算的，
        /// 而重覆與否要把整張表看過一遍才知道。</para>
        /// </summary>
        [Fact]
        public async Task 每一列的表單索引都是自己的_沒有兩列共用()
        {
            var page = await OpenFirstRolePanelAsync();

            // 逐列取出該列所有 checkbox 的索引，一列應該只對應一個索引。
            var perRow = await page.EvalOnSelectorAllAsync<string[]>(
                "#permissionPanelForm tr.module-item-row",
                @"(rows, sep) => rows.map(r => {
                    const ix = new Set();
                    r.querySelectorAll(""input[type='checkbox'][name]"").forEach(cb => {
                        const m = cb.name.match(/^permissions\[(\d+)\]/);
                        if (m) ix.add(m[1]);
                    });
                    const label = (r.querySelector('td')?.innerText || '').trim();
                    return label + sep + [...ix].join(',');
                })", Sep);

            var byIndex = new Dictionary<string, List<string>>();
            foreach (var entry in perRow)
            {
                var parts = entry.Split(Sep);
                var label = parts[0];
                var indices = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries);

                Assert.True(indices.Length <= 1,
                    $"「{label}」這一列的開關分屬 {indices.Length} 個不同索引（{parts[1]}）——" +
                    "同一列的五個動詞必須綁在同一個 permissions[i] 上。");

                foreach (var ix in indices)
                {
                    if (!byIndex.TryGetValue(ix, out var list))
                        byIndex[ix] = list = new List<string>();
                    list.Add(label);
                }
            }

            var shared = byIndex.Where(kv => kv.Value.Count > 1)
                                .Select(kv => $"permissions[{kv.Key}] ← {string.Join(" / ", kv.Value)}")
                                .ToList();
            _out.WriteLine($"用到的索引數：{byIndex.Count}");
            Assert.True(shared.Count == 0,
                "有選單項共用同一個表單索引，存檔時會互相覆蓋：\n" + string.Join("\n", shared));
        }

        /// <summary>
        /// 模組的「全選」只動自己模組那幾列，收合也只收自己那幾列。
        ///
        /// <para>兩個按鈕都靠 <c>data-mod</c> 這個 class 名做範圍界定
        /// （<c>querySelectorAll('tr.' + mod + ' input')</c>）。範圍寫錯的後果是
        /// <b>一鍵把全站權限開給某個角色</b>，而畫面上只是「多了一些勾」——
        /// 沒有人會逐列去數。</para>
        ///
        /// <para>只在瀏覽器裡切勾選框，<b>不送出</b>，所以資料庫一列都不會動。</para>
        /// </summary>
        [Fact]
        public async Task 模組的全選只動自己那幾列()
        {
            var page = await OpenFirstRolePanelAsync();

            var selectAll = page.Locator("#permissionPanelForm [data-bulk='select-all']").First;
            await Assertions.Expect(selectAll).ToBeVisibleAsync();
            var mod = await selectAll.GetAttributeAsync("data-mod");
            Assert.False(string.IsNullOrWhiteSpace(mod), "全選按鈕沒有 data-mod，範圍無從界定。");

            var inside = page.Locator($"#permissionPanelForm tr.{mod} input[type='checkbox']");
            var insideCount = await inside.CountAsync();
            Assert.True(insideCount > 0, $"模組 {mod} 底下一個開關都沒有。");

            var totalCount = await page.Locator(
                "#permissionPanelForm input[type='checkbox']").CountAsync();
            Assert.True(totalCount > insideCount,
                "整張表的開關數等於這一個模組的開關數——" +
                "只有一個模組的話，「範圍有沒有寫對」根本驗不出來。");

            async Task<int> CheckedOutsideAsync() => await page.EvaluateAsync<int>(
                @"mod => [...document.querySelectorAll(""#permissionPanelForm input[type='checkbox']"")]
                        .filter(cb => cb.checked && !cb.closest('tr').classList.contains(mod)).length", mod);

            var before = await CheckedOutsideAsync();

            await selectAll.ClickAsync();
            Assert.Equal(insideCount, await page.Locator(
                $"#permissionPanelForm tr.{mod} input[type='checkbox']:checked").CountAsync());
            Assert.Equal(before, await CheckedOutsideAsync());

            var deselectAll = page.Locator(
                $"#permissionPanelForm [data-bulk='deselect-all'][data-mod='{mod}']").First;
            await deselectAll.ClickAsync();
            Assert.Equal(0, await page.Locator(
                $"#permissionPanelForm tr.{mod} input[type='checkbox']:checked").CountAsync());
            Assert.Equal(before, await CheckedOutsideAsync());

            _out.WriteLine(
                $"模組 {mod}：自己 {insideCount} 個開關／全表 {totalCount} 個，模組外已勾 {before} 個未受影響");

            // 收合：只收自己那幾列，模組標題列要留著（不然收起來就再也展不開）。
            var toggle = page.Locator($"#permissionPanelForm .toggle-module[data-mod='{mod}']").First;
            await toggle.ClickAsync();
            await Assertions.Expect(page.Locator($"#permissionPanelForm tr.{mod}").First)
                .Not.ToBeVisibleAsync(new() { Timeout = 5_000 });
            await Assertions.Expect(toggle).ToBeVisibleAsync();

            await toggle.ClickAsync();
            await Assertions.Expect(page.Locator($"#permissionPanelForm tr.{mod}").First)
                .ToBeVisibleAsync(new() { Timeout = 5_000 });
        }
    }
}
