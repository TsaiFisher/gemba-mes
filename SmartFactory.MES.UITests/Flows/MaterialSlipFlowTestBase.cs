using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Flows
{
    /// <summary>
    /// 工單發退料單（/MaterialSlip）的畫面操作：新增一張單、指定儲位、存檔、依序按面板上的動作表單。
    /// <see cref="MaterialFlowTests"/>（一般／委外發退料）與 <see cref="ReworkFlowTests"/>（重工發退料）共用——
    /// 七種類別走同一張表單、同一條狀態流程，差別只在類別與來源單。
    /// </summary>
    public abstract class MaterialSlipFlowTestBase : FlowTestBase
    {
        protected MaterialSlipFlowTestBase(UiTestFixture fixture) : base(fixture) { }

        protected static Task<int> SlipStatusAsync(string slip) => SeedFixture.ScalarAsync<int>(
            $"""SELECT "Status" FROM "MaterialSlips" WHERE "SlipId"='{slip}'""");

        protected static async Task<string> LatestSlipAsync(int category)
        {
            var id = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "SlipId" FROM "MaterialSlips" WHERE "Category"={category} AND "PlantId"='P1' ORDER BY "CreatedAt" DESC LIMIT 1""");
            Assert.False(string.IsNullOrEmpty(id), $"找不到剛建的發退料單（類別 {category}）");
            return id!;
        }

        protected static async Task WaitForAsync(Func<Task<bool>> cond, string what)
        {
            for (var i = 0; i < 60; i++) { if (await cond()) return; await Task.Delay(250); }
            Assert.Fail(what);
        }

        /// <summary>設 select 的值並同時觸發 jQuery 與原生 change（select2 聽前者、data-action-change 聽後者）。</summary>
        protected static Task SetSelectAsync(IPage page, string selector, string value) => page.EvaluateAsync(@"([sel, v]) => {
            const els = document.querySelectorAll(sel); const el = els[els.length - 1];
            if (!el) throw new Error('找不到 ' + sel);
            if (![...el.options].some(o => o.value === v)) throw new Error(sel + ' 沒有選項 ' + v);
            el.value = v;
            if (window.jQuery) window.jQuery(el).trigger('change');
            el.dispatchEvent(new Event('change', { bubbles: true }));
        }", new object[] { selector, value });

        /// <summary>新增一張發退料單並存檔；回傳面板上的驗證訊息（空＝存成功）。</summary>
        protected async Task<string[]> SaveNewSlipAsync(IPage page, string category, string sourceId,
            (string Product, decimal Qty, string Wh, string Loc) line)
        {
            await GotoAsync(page, $"/MaterialSlip?searched=true&categoryFilter={category}");
            await page.Locator("button[data-url*='/MaterialSlip/Form?category']").First.ClickAsync();
            await page.WaitForSelectorAsync("#materialSlipForm");
            const string F = "#materialSlipForm";
            var outsource = category.StartsWith("Outsource");
            if (outsource)
            {
                await page.Locator($"{F} select[name='TargetSupplierCode']").SelectOptionAsync(new SelectOptionValue { Index = 1 });
                await SetSelectAsync(page, "#msSourceType", "OutsourcePurchaseOrder");
            }
            await page.Locator($"#msSourceDoc option[value='{sourceId}']").WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 10_000 });
            await SetSelectAsync(page, "#msSourceDoc", sourceId);

            await page.ClickAsync($"{F} [data-action='addMsLine']");
            await SetSelectAsync(page, $"{F} select[name$='.ProductCode']", line.Product);
            await page.Locator($"{F} input[name$='.RequestedQty']").Last.FillAsync(line.Qty.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await SetLastLineLocationAsync(page, line.Wh, line.Loc, category.EndsWith("Return"));
            return await SubmitPanelAsync(page);
        }

        protected static async Task SetLastLineLocationAsync(IPage page, string wh, string loc, bool isReturn = false)
        {
            var side = isReturn ? "To" : "From";
            await SetSelectAsync(page, $"#materialSlipForm select[name$='.{side}WarehouseCode']", wh);
            await page.WaitForFunctionAsync(@"([sel, v]) => { const els = document.querySelectorAll(sel); const el = els[els.length - 1];
                return el && [...el.options].some(o => o.value === v); }",
                new object[] { $"#materialSlipForm select[name$='.{side}LocationCode']", loc });
            await SetSelectAsync(page, $"#materialSlipForm select[name$='.{side}LocationCode']", loc);
        }

        protected static async Task SetAllLinesLocationAsync(IPage page, string wh, string loc)
        {
            var n = await page.Locator("#materialSlipForm select[name$='.FromWarehouseCode']").CountAsync();
            for (var i = 0; i < n; i++)
            {
                await page.EvaluateAsync(@"([i, wh]) => { const el = document.querySelectorAll(""#materialSlipForm select[name$='.FromWarehouseCode']"")[i];
                    el.value = wh; el.dispatchEvent(new Event('change', { bubbles: true })); }", new object[] { i, wh });
                await page.WaitForFunctionAsync(@"([i, v]) => { const el = document.querySelectorAll(""#materialSlipForm select[name$='.FromLocationCode']"")[i];
                    return el && [...el.options].some(o => o.value === v); }", new object[] { i, loc });
                await page.EvaluateAsync(@"([i, v]) => { const el = document.querySelectorAll(""#materialSlipForm select[name$='.FromLocationCode']"")[i];
                    el.value = v; el.dispatchEvent(new Event('change', { bubbles: true })); }", new object[] { i, loc });
            }
        }

        /// <summary>
        /// 按面板的存檔鈕，回傳驗證訊息（空＝存成功）。成功時伺服器回 refreshList 腳本、整頁重新載入，
        /// 所以成敗以回應內容判斷，不能事後再讀面板；沒有送出（前端驗證擋下）時回報面板上的欄位訊息。
        /// </summary>
        protected static async Task<string[]> SubmitPanelAsync(IPage page)
        {
            var respTask = page.WaitForResponseAsync(r => r.Request.Method == "POST" && r.Url.Contains("/MaterialSlip/Save"),
                new PageWaitForResponseOptions { Timeout = 10_000 });
            await page.Locator("#materialSlipForm button[type='submit']").First.ClickAsync();
            IResponse resp;
            try { resp = await respTask; }
            catch (TimeoutException)
            {
                var local = await page.EvaluateAsync<string[]>(@"() => [...document.querySelectorAll('#sidePanelBody .field-validation-error, #sidePanelBody .input-validation-error')]
                    .map(e => (e.getAttribute('name') || '') + ' ' + (e.textContent || '').trim())");
                return ["表單沒有送出（前端驗證）：" + string.Join(" / ", local)];
            }
            var body = await resp.TextAsync();
            if (body.Contains("refreshList"))
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.WaitForTimeoutAsync(1200);
                return [];
            }
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            return await page.EvaluateAsync<string[]>(@"() => {
                const p = document.querySelector('#sidePanelBody'); if (!p) return [];
                return [...p.querySelectorAll('.validation-summary-errors li, .field-validation-error, [data-valmsg-for]')]
                    .map(e => (e.textContent || '').trim()).filter(t => t); }") is { Length: > 0 } msgs
                ? msgs : ["存檔失敗（面板沒有訊息）：" + body[..Math.Min(300, body.Length)]];
        }

        protected async Task OpenSlipPanelAsync(IPage page, string slip, string category)
        {
            await GotoAsync(page, $"/MaterialSlip?searched=true&categoryFilter={category}&pageSize=50");
            await page.Locator($"[data-url$='/MaterialSlip/Form/{slip}']").First.ClickAsync();
            await page.WaitForSelectorAsync("#materialSlipForm, #sidePanelBody form[action*='/MaterialSlip/']");
        }

        /// <summary>依序執行面板上的動作表單（送審／核准／開始揀料／確認揀料／過帳），每步確認狀態有前進。</summary>
        protected async Task RunSlipAsync(IPage page, string slip, string category, params string[] actions)
        {
            foreach (var act in actions)
            {
                var before = await SlipStatusAsync(slip);
                await OpenSlipPanelAsync(page, slip, category);
                var btn = page.Locator($"#sidePanelBody form[action*='/MaterialSlip/{act}/'] button[type='submit']");
                Assert.True(await btn.CountAsync() == 1, $"{slip}（狀態 {before}）面板上找不到「{act}」{DescribeDialogs()}");
                await btn.ClickAsync();
                await WaitForAsync(async () => await SlipStatusAsync(slip) != before,
                    $"{slip} 按了「{act}」狀態沒有前進（仍是 {before}）{DescribeDialogs()}");
            }
        }
    }
}
