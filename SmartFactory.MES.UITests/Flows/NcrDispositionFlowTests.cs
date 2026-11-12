using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Flows
{
    /// <summary>
    /// NCR 各處置方式從畫面走一遍（桌機＋真資料庫）：報廢、退貨、退件重工、特採（先送審才能執行）、結案、刪除。
    /// 重工另見 <see cref="ReworkFlowTests"/>。
    ///
    /// <para>NCR 明細面板的「執行處置／送審／結案／刪除」到 2026-10-15 前從沒出現過（顯示條件與渲染位置互斥），
    /// 這些處置因此從沒有人從畫面上跑過——修好按鈕後逐一補上。</para>
    ///
    /// <para>測試開的 NCR 備註一律填 <c>E2E-NCRFLOW</c>，種子 ㉗ 依此清掉它們與它們開出的單。
    /// 退貨的來源是種子 ㉗ 的進料檢驗不合格 E2E-IQC51（進貨單 E2E-PE01 第 1 行，E2E-FG01 × 100、單價 10）。</para>
    /// </summary>
    public class NcrDispositionFlowTests : FlowTestBase
    {
        private const string Mark = "E2E-NCRFLOW";
        private const string Fg = "E2E-FG01";

        public NcrDispositionFlowTests(UiTestFixture fixture) : base(fixture) { }

        [Fact]
        public async Task 報廢_執行後開出已過帳報廢單並扣庫存_結案_報廢單可作廢回沖()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();
            var before = await OnHandAsync(Fg, "E2E-LFG");

            var ncr = await CreateNcrAsync(page, "Manual", null, 2m, "Scrap", wh: "E2E-WFG", loc: "E2E-LFG");
            AssertSuccess(await ClickActionAsync(page, ncr, "executeNcr", "Execute"), "執行報廢");
            Assert.Equal("Executed", await StatusAsync(ncr));

            var scrap = await SeedFixture.ScalarAsync<string>($"""SELECT "ScrapDocId" FROM "NonConformanceReports" WHERE "NcrId"='{ncr}'""");
            Assert.False(string.IsNullOrEmpty(scrap), "NCR 執行報廢後沒有記下報廢單號");
            Assert.Equal(3, await SeedFixture.ScalarAsync<int>($"""SELECT "Status" FROM "ScrapDocs" WHERE "ScrapDocId"='{scrap}'"""));   // Posted
            Assert.Equal(before - 2m, await OnHandAsync(Fg, "E2E-LFG"));

            AssertSuccess(await ClickActionAsync(page, ncr, "closeNcr", "Close"), "結案");
            Assert.Equal("Closed", await StatusAsync(ncr));

            // 報廢單要能回沖：從報廢單畫面作廢，庫存回來。原本報廢單停在「已確認」，作廢鈕不出現、後端也只收 Posted。
            await GotoAsync(page, "/ScrapDoc?searched=true&pageSize=50");
            await OpenRowPanelAsync(page, scrap!, $"/ScrapDoc/Details/{scrap}", "#sidePanelBody [data-action='voidDoc']");
            var resp = await page.RunAndWaitForResponseAsync(
                () => page.ClickAsync("#sidePanelBody [data-action='voidDoc']"),
                r => r.Request.Method == "POST" && r.Url.Contains($"/ScrapDoc/Void/{scrap}"));
            AssertSuccess(await resp.TextAsync(), "作廢 NCR 開出的報廢單");
            Assert.Equal(4, await SeedFixture.ScalarAsync<int>($"""SELECT "Status" FROM "ScrapDocs" WHERE "ScrapDocId"='{scrap}'"""));   // Void
            Assert.Equal(before, await OnHandAsync(Fg, "E2E-LFG"));
            Assert.Empty(JsErrors);
        }

        [Fact]
        public async Task 退貨_來源為進料檢驗_執行後開出追回原進貨單的採購退貨草稿()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            var ncr = await CreateNcrAsync(page, "InspectionOrder", "E2E-IQC51", 4m, "Return");
            AssertSuccess(await ClickActionAsync(page, ncr, "executeNcr", "Execute"), "執行退貨");
            Assert.Equal("Executed", await StatusAsync(ncr));

            var grt = await SeedFixture.ScalarAsync<string>($"""SELECT "GoodsReturnId" FROM "NonConformanceReports" WHERE "NcrId"='{ncr}'""");
            Assert.False(string.IsNullOrEmpty(grt), "NCR 執行退貨後沒有記下退貨單號");
            // 草稿、供應商與單價追自進貨單、明細連回進貨單第 1 行：4 × 10 = 40
            Assert.Equal("0|E2E-CO01|E2E-PE01|1|4.00|40.00", await SeedFixture.ScalarAsync<string>(
                $"""
                SELECT g."Status"||'|'||g."SupplierCode"||'|'||l."GoodsReceiptId"||'|'||l."ReceiptLineNo"||'|'||l."ReturnQty"::numeric(18,2)||'|'||l."LineAmount"::numeric(18,2)
                FROM "GoodsReturns" g JOIN "GoodsReturnLines" l USING ("GoodsReturnId") WHERE g."GoodsReturnId"='{grt}'
                """));

            // 草稿要在進貨退出畫面看得到、打得開（採購接手確認過帳）
            await GotoAsync(page, "/GoodsReturn?searched=true&pageSize=50");
            Assert.True(await page.Locator($"[data-url*='/GoodsReturn/'][data-url$='/{grt}']").CountAsync() > 0,
                $"NCR 開出的退貨草稿 {grt} 在進貨退出列表上找不到");
            Assert.Empty(JsErrors);
        }

        [Fact]
        public async Task 退件重工_執行後開出委外重工單草稿_預設廠商全責()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            var ncr = await CreateNcrAsync(page, "Manual", null, 3m, "ReturnRework");
            AssertSuccess(await ClickActionAsync(page, ncr, "executeNcr", "Execute"), "執行退件重工");
            Assert.Equal("Executed", await StatusAsync(ncr));

            var osr = await SeedFixture.ScalarAsync<string>($"""SELECT "OutsourceReworkId" FROM "NonConformanceReports" WHERE "NcrId"='{ncr}'""");
            Assert.False(string.IsNullOrEmpty(osr), "NCR 執行退件重工後沒有記下委外重工單號");
            Assert.Equal($"0|{ncr}|3.00|100.00", await SeedFixture.ScalarAsync<string>(
                $"""SELECT "Status"||'|'||"SourceNcrId"||'|'||"ReworkQty"::numeric(18,2)||'|'||"VendorLiablePct"::numeric(18,2) FROM "OutsourceReworks" WHERE "OutsourceReworkId"='{osr}'"""));

            await GotoAsync(page, "/OutsourceRework?searched=true&pageSize=50");
            Assert.True((await page.InnerTextAsync("body")).Contains(osr!),
                $"NCR 開出的委外重工單草稿 {osr} 在委外重工單列表上找不到");
            Assert.Empty(JsErrors);
        }

        [Fact]
        public async Task 特採_未核准先執行被擋_送審核准後才能執行()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            var ncr = await CreateNcrAsync(page, "Manual", null, 1m, "UseAsIs");

            var body = await ClickActionAsync(page, ncr, "executeNcr", "Execute");
            Assert.Contains("\"success\":false", body);
            Assert.Equal("Open", await StatusAsync(ncr));

            // 未設簽核範本：送審即核准（寬鬆模式）
            AssertSuccess(await ClickActionAsync(page, ncr, "submitNcr", "Submit"), "特採送審");
            Assert.Equal("Approved", await StatusAsync(ncr));

            AssertSuccess(await ClickActionAsync(page, ncr, "executeNcr", "Execute"), "執行特採");
            Assert.Equal("Executed", await StatusAsync(ncr));
            Assert.Empty(JsErrors);
        }

        [Fact]
        public async Task 未處置的NCR可從明細面板刪除()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            var ncr = await CreateNcrAsync(page, "Manual", null, 1m, "Scrap", wh: "E2E-WFG", loc: "E2E-LFG");
            await OpenNcrAsync(page, ncr);
            var del = page.Locator("#sidePanelBody [data-action='deleteItem']");
            Assert.True(await del.CountAsync() == 1, $"NCR {ncr}（未處置）的明細面板上沒有「刪除」鈕");
            var resp = await page.RunAndWaitForResponseAsync(() => del.ClickAsync(),
                r => r.Request.Method == "POST" && r.Url.Contains("/Ncr/DeleteConfirmed"));
            AssertSuccess(await resp.TextAsync(), "刪除");
            Assert.Equal(0L, await SeedFixture.ScalarAsync<long>($"""SELECT count(*) FROM "NonConformanceReports" WHERE "NcrId"='{ncr}'"""));
            Assert.Empty(JsErrors);
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        /// <summary>從 NCR 畫面新增一張（備註填 <see cref="Mark"/>），回傳單號。</summary>
        private async Task<string> CreateNcrAsync(IPage page, string sourceType, string? sourceId, decimal qty,
            string disposition, string? wh = null, string? loc = null)
        {
            await GotoAsync(page, "/Ncr?searched=true");
            await page.ClickAsync("button[data-url$='/Ncr/Create']");
            const string F = "#sidePanelBody #sideForm";
            await page.WaitForSelectorAsync($"{F} input[name='ProductCode']");
            await page.SelectOptionAsync($"{F} select[name='SourceType']", sourceType);
            if (sourceId != null) await page.FillAsync($"{F} input[name='SourceId']", sourceId);
            await page.FillAsync($"{F} input[name='ProductCode']", Fg);
            await page.FillAsync($"{F} input[name='Qty']", qty.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await page.FillAsync($"{F} input[name='UomCode']", "E2E-EA");
            await page.SelectOptionAsync($"{F} #dispositionSel", disposition);
            if (wh != null)
            {
                await page.SelectOptionAsync($"{F} #whSel", wh);
                await page.FillAsync($"{F} input[name='LocationCode']", loc!);
            }
            await page.FillAsync($"{F} input[name='Remarks']", Mark);
            await SavePanelAsync(page, $"{F} button[value='save']", $"NCR（{disposition}）存檔");

            var id = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "NcrId" FROM "NonConformanceReports" WHERE "Remarks"='{Mark}' ORDER BY "CreatedAt" DESC LIMIT 1""");
            Assert.False(string.IsNullOrEmpty(id), $"NCR 存檔後資料庫沒有這張單。{DescribeDialogs()}");
            return id!;
        }

        private async Task OpenNcrAsync(IPage page, string ncr)
        {
            await GotoAsync(page, $"/Ncr?searched=true&searchNcrId={ncr}");
            await OpenRowPanelAsync(page, ncr, $"/Ncr/Details/{ncr}", "#sidePanelBody #sideForm");
        }

        /// <summary>開明細面板、按動作鈕（確認對話框自動接受），回傳後端回應內容。</summary>
        private async Task<string> ClickActionAsync(IPage page, string ncr, string dataAction, string endpoint)
        {
            await OpenNcrAsync(page, ncr);
            var btn = page.Locator($"#sidePanelBody [data-action='{dataAction}']");
            Assert.True(await btn.CountAsync() == 1,
                $"NCR {ncr}（{await StatusAsync(ncr)}）的明細面板上沒有 {dataAction}。面板上的按鈕：" +
                string.Join("、", await page.Locator("#sidePanelBody button").AllInnerTextsAsync()));
            var resp = await page.RunAndWaitForResponseAsync(() => btn.ClickAsync(),
                r => r.Request.Method == "POST" && r.Url.Contains($"/Ncr/{endpoint}"));
            var body = await resp.TextAsync();
            await page.WaitForTimeoutAsync(1200);   // 成功後面板 900ms 後重新載入
            return body;
        }

        private void AssertSuccess(string body, string what)
            => Assert.True(body.Contains("\"success\":true"), $"{what}被拒：{body}{DescribeDialogs()}");

        private static async Task<string?> StatusAsync(string ncr) => await SeedFixture.ScalarAsync<string>(
            $"""SELECT "NcrStatus" FROM "NonConformanceReports" WHERE "NcrId"='{ncr}'""");

        private static Task<decimal> OnHandAsync(string product, string loc) => SeedFixture.ScalarAsync<decimal>(
            $"""SELECT COALESCE(SUM("QtyOnHand"),0) FROM "Inventories" WHERE "ProductCode"='{product}' AND "LocationCode"='{loc}'""");
    }
}
