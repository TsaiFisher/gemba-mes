using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Flows
{
    /// <summary>
    /// 退貨 → 發票回沖／折讓單（桌機畫面＋真資料庫）。完整模式下退貨在倉庫下架（進貨退出）／上架（銷售退回）
    /// 那一刻過帳，同時回沖來源發票：
    /// <list type="bullet">
    /// <item>發票<b>未收付款</b>：直接把發票明細的量與金額改小，不開折讓單。</item>
    /// <item>發票<b>已收付款</b>：發票不動，自動開一張折讓單草稿（金額＝退貨量 × 單價 × (1＋稅率)），
    /// 從折讓單畫面確認後才寫進發票的累計折讓額與總帳。</item>
    /// </list>
    ///
    /// <para><see cref="FIN.CreditNoteUiTestBase"/> 用的是種子直接放好的折讓單草稿；這裡驗的是前半段——
    /// 退貨真的會開出那張草稿、開對金額、而且開出來的草稿從畫面確認得了。</para>
    ///
    /// <para>種子 ㉖：品號 E2E-RTN01；進貨單 E2E-RGR01／02（各 10 × 100、稅 5%），應付發票 E2E-API11 已付清、E2E-API12 未付；
    /// 出貨單 E2E-RGS01／02（各 10 × 200、稅 5%），應收發票 E2E-ARI11 已收清、E2E-ARI12 未收。每支開頭重跑種子。</para>
    /// </summary>
    public class ReturnAllowanceFlowTests : FlowTestBase
    {
        private const string Party = "E2E-CO01";
        private const decimal ReturnQty = 3m;

        public ReturnAllowanceFlowTests(UiTestFixture fixture) : base(fixture) { }

        // ── 進貨退出 ─────────────────────────────────────────────────────────

        [Fact]
        public async Task 進貨退出_來源發票已付款_下架後開出折讓單草稿_確認後寫入發票折讓額與分錄()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            var grt = await CreateGoodsReturnAsync(page, "E2E-RGR01");
            await PickAsync(page, grt, "E2E-LRAW", ReturnQty, "進貨退出下架");
            Assert.Equal(3, await ScalarIntAsync($"""SELECT "Status" FROM "GoodsReturns" WHERE "GoodsReturnId"='{grt}'"""));

            // 已付清的發票不動，缺口開成折讓單草稿：3 × 100 × 1.05 = 315
            Assert.Equal("10.00|1050.00", await InvoiceAsync("APInvoices", "APInvoiceId", "E2E-API11"));
            var note = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "APDebitNoteId" FROM "APDebitNotes" WHERE "SourceDocType"='GoodsReturn' AND "SourceDocId"='{grt}'""");
            Assert.False(string.IsNullOrEmpty(note),
                $"來源發票已付清，進貨退出 {grt} 下架過帳後卻沒有開出折讓單草稿——那 315 元只會留在倉庫畫面的一則提示裡。{DescribeDialogs()}");
            Assert.Equal("0|315.00|E2E-API11|3.00", await SeedFixture.ScalarAsync<string>(
                $"""
                SELECT n."Status"||'|'||n."TotalAmount"::numeric(18,2)||'|'||l."APInvoiceId"||'|'||l."Qty"::numeric(18,2)
                FROM "APDebitNotes" n JOIN "APDebitNoteLines" l USING ("APDebitNoteId") WHERE n."APDebitNoteId"='{note}'
                """));

            await ConfirmNoteAsync(page, "APDebitNote", note!);
            Assert.Equal(315m, await SeedFixture.ScalarAsync<decimal>(
                """SELECT "DebitedAmount" FROM "APInvoices" WHERE "APInvoiceId"='E2E-API11'"""));
            Assert.Equal("315.00/315.00", await JournalTotalsAsync("APDebitNote", note!));
            Assert.Empty(JsErrors);
        }

        [Fact]
        public async Task 進貨退出_來源發票未付款_下架後直接沖減發票_不開折讓單()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            var grt = await CreateGoodsReturnAsync(page, "E2E-RGR02");
            await PickAsync(page, grt, "E2E-LRAW", ReturnQty, "進貨退出下架");
            Assert.Equal(3, await ScalarIntAsync($"""SELECT "Status" FROM "GoodsReturns" WHERE "GoodsReturnId"='{grt}'"""));

            // 7 × 100 = 700，含稅 735
            Assert.Equal("7.00|735.00", await InvoiceAsync("APInvoices", "APInvoiceId", "E2E-API12"));
            Assert.Equal(0L, await SeedFixture.ScalarAsync<long>(
                $"""SELECT count(*) FROM "APDebitNotes" WHERE "SourceDocType"='GoodsReturn' AND "SourceDocId"='{grt}'"""));
            Assert.Empty(JsErrors);
        }

        // ── 銷售退回 ─────────────────────────────────────────────────────────

        [Fact]
        public async Task 銷售退回_來源發票已收款_上架後開出折讓單草稿_確認後寫入發票折讓額與分錄()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            var sr = await CreateSalesReturnAsync(page, "E2E-RGS01");
            await PutAwayAsync(page, sr, "E2E-WFG", "E2E-LFG", "銷售退回上架");
            Assert.Equal(3, await ScalarIntAsync($"""SELECT "Status" FROM "SalesReturns" WHERE "SalesReturnId"='{sr}'"""));

            // 已收清的發票不動，缺口開成折讓單草稿：3 × 200 × 1.05 = 630
            Assert.Equal("10.00|2100.00", await InvoiceAsync("ARInvoices", "ARInvoiceId", "E2E-ARI11"));
            var note = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "ARCreditNoteId" FROM "ARCreditNotes" WHERE "SourceDocType"='SalesReturn' AND "SourceDocId"='{sr}'""");
            Assert.False(string.IsNullOrEmpty(note),
                $"來源發票已收清，銷售退回 {sr} 上架過帳後卻沒有開出折讓單草稿。{DescribeDialogs()}");
            Assert.Equal("0|630.00|E2E-ARI11|3.00", await SeedFixture.ScalarAsync<string>(
                $"""
                SELECT n."Status"||'|'||n."TotalAmount"::numeric(18,2)||'|'||l."ARInvoiceId"||'|'||l."Qty"::numeric(18,2)
                FROM "ARCreditNotes" n JOIN "ARCreditNoteLines" l USING ("ARCreditNoteId") WHERE n."ARCreditNoteId"='{note}'
                """));

            await ConfirmNoteAsync(page, "ARCreditNote", note!);
            Assert.Equal(630m, await SeedFixture.ScalarAsync<decimal>(
                """SELECT "CreditedAmount" FROM "ARInvoices" WHERE "ARInvoiceId"='E2E-ARI11'"""));
            Assert.Equal("630.00/630.00", await JournalTotalsAsync("ARCreditNote", note!));
            Assert.Empty(JsErrors);
        }

        [Fact]
        public async Task 銷售退回_來源發票未收款_上架後直接沖減發票_不開折讓單()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            var sr = await CreateSalesReturnAsync(page, "E2E-RGS02");
            await PutAwayAsync(page, sr, "E2E-WFG", "E2E-LFG", "銷售退回上架");
            Assert.Equal(3, await ScalarIntAsync($"""SELECT "Status" FROM "SalesReturns" WHERE "SalesReturnId"='{sr}'"""));

            // 7 × 200 = 1400，含稅 1470
            Assert.Equal("7.00|1470.00", await InvoiceAsync("ARInvoices", "ARInvoiceId", "E2E-ARI12"));
            Assert.Equal(0L, await SeedFixture.ScalarAsync<long>(
                $"""SELECT count(*) FROM "ARCreditNotes" WHERE "SourceDocType"='SalesReturn' AND "SourceDocId"='{sr}'"""));
            Assert.Empty(JsErrors);
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        /// <summary>進貨退出：選供應商 → 自進貨單匯入退貨量 → 存檔（未設簽核即確認、待倉庫下架）。</summary>
        private async Task<string> CreateGoodsReturnAsync(IPage page, string goodsReceiptId)
        {
            await GotoAsync(page, "/GoodsReturn?searched=true");
            await page.ClickAsync("button[data-url$='/GoodsReturn/Create']");
            await page.WaitForSelectorAsync("#sidePanelBody #grtForm", new PageWaitForSelectorOptions { Timeout = 10_000 });
            await page.Locator("#sidePanelBody select[name='SupplierCode']").SelectOptionAsync(Party);
            await page.WaitForTimeoutAsync(400);

            await ClickActionAsync(page, "openGrModal", 800);
            await page.Locator($"#grImportTbody tr:has-text('{goodsReceiptId}')").First.WaitForAsync(
                new LocatorWaitForOptions { Timeout = 10_000 });
            await FillImportQtyAsync(page, "#grImportTbody", goodsReceiptId, "gr-line-qty", ReturnQty);
            await ClickActionAsync(page, "confirmGrImport", 800);

            await SavePanelAsync(page, "#sidePanelBody #grtForm button[value='save']", $"進貨退出（{goodsReceiptId}）存檔");
            return await ReturnIdAsync(
                $"""SELECT "GoodsReturnId" FROM "GoodsReturnLines" WHERE "GoodsReceiptId"='{goodsReceiptId}' LIMIT 1""",
                """SELECT "Status" FROM "GoodsReturns" WHERE "GoodsReturnId"='{0}'""", "進貨退出");
        }

        /// <summary>銷售退回：選客戶 → 自出貨單匯入退回量 → 存檔（未設簽核即確認、待倉庫上架）。</summary>
        private async Task<string> CreateSalesReturnAsync(IPage page, string goodsShipmentId)
        {
            await GotoAsync(page, "/SalesReturn?searched=true");
            await page.ClickAsync("button[data-url$='/SalesReturn/Create']");
            await page.WaitForSelectorAsync("#sidePanelBody #grForm", new PageWaitForSelectorOptions { Timeout = 10_000 });
            await page.Locator("#sidePanelBody select[name='CompanyCode']").SelectOptionAsync(Party);
            await page.WaitForTimeoutAsync(400);

            await ClickActionAsync(page, "importFromGS", 800);
            await page.Locator($"#gsLinesTbody tr:has-text('{goodsShipmentId}')").First.WaitForAsync(
                new LocatorWaitForOptions { Timeout = 10_000 });
            await FillImportQtyAsync(page, "#gsLinesTbody", goodsShipmentId, "gs-line-qty", ReturnQty);
            await ClickActionAsync(page, "applyGSLines", 800);

            await SavePanelAsync(page, "#sidePanelBody #grForm button[type='submit']", $"銷售退回（{goodsShipmentId}）存檔");

            // 與進貨退出不同：銷售退回存檔只到「已核准的草稿」，要在編輯面板按「確認（待倉庫上架）」才交給倉庫。
            var draft = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "SalesReturnId" FROM "SalesReturnLines" WHERE "GoodsShipmentId"='{goodsShipmentId}' LIMIT 1""");
            Assert.False(string.IsNullOrEmpty(draft), $"銷售退回存檔後資料庫沒有這張單。{DescribeDialogs()}");
            await GotoAsync(page, $"/SalesReturn?searched=true&pageSize=50");
            await OpenRowPanelAsync(page, draft!, $"/SalesReturn/Edit/{draft}", "#sidePanelBody #grForm");
            var confirm = page.Locator($"#sidePanelBody [data-action='docAction'][data-doc-action='Confirm'][data-id='{draft}']");
            Assert.True(await confirm.CountAsync() == 1, $"銷售退回 {draft} 的編輯面板上沒有「確認」鈕。{DescribeDialogs()}");
            var resp = await page.RunAndWaitForResponseAsync(() => confirm.ClickAsync(),
                r => r.Request.Method == "POST" && r.Url.Contains($"/SalesReturn/Confirm/{draft}"));
            Assert.True(resp.Ok, $"銷售退回 {draft} 確認被拒：HTTP {resp.Status} {await resp.TextAsync()}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            return await ReturnIdAsync(
                $"""SELECT "SalesReturnId" FROM "SalesReturnLines" WHERE "GoodsShipmentId"='{goodsShipmentId}' LIMIT 1""",
                """SELECT "Status" FROM "SalesReturns" WHERE "SalesReturnId"='{0}'""", "銷售退回");
        }

        /// <summary>取剛存的退貨單號，並確認它已是「已確認、待倉庫作業」（1）——否則倉庫畫面找不到它。</summary>
        private async Task<string> ReturnIdAsync(string idSql, string statusSql, string what)
        {
            var id = await SeedFixture.ScalarAsync<string>(idSql);
            Assert.False(string.IsNullOrEmpty(id), $"{what}存檔後資料庫沒有這張單。{DescribeDialogs()}");
            var status = await ScalarIntAsync(string.Format(statusSql, id));
            Assert.True(status == 1, $"{what} {id} 存檔後狀態應為 1（已確認、待倉庫作業），實際 {status}。{DescribeDialogs()}");
            return id!;
        }

        /// <summary>從折讓單畫面打開那張草稿、按確認，並斷言後端回成功、狀態變成已過帳。</summary>
        private async Task ConfirmNoteAsync(IPage page, string docType, string noteId)
        {
            await GotoAsync(page, $"/{docType}?searched=true");
            var open = page.Locator($"[data-url$='/{docType}/Details/{noteId}']");
            Assert.True(await open.CountAsync() > 0,
                $"/{docType} 列表上找不到退貨開出的折讓單 {noteId}（廠別／資料授權過濾把它藏起來了？）");
            await open.First.ClickAsync();
            var confirm = page.Locator("#sidePanelBody [data-action='confirmNote']");
            await confirm.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

            var response = await page.RunAndWaitForResponseAsync(
                () => confirm.ClickAsync(),
                r => r.Url.Contains($"/{docType}/Confirm/{noteId}"));
            var body = await response.TextAsync();
            Assert.True(body.Contains("\"success\":true"), $"/{docType} 確認 {noteId} 被拒：{body}{DescribeDialogs()}");
        }

        private static Task<int> ScalarIntAsync(string sql) => SeedFixture.ScalarAsync<int>(sql);

        /// <summary>發票明細量｜發票含稅總額。</summary>
        private static async Task<string?> InvoiceAsync(string table, string idColumn, string id) =>
            await SeedFixture.ScalarAsync<string>(
                $"""
                SELECT (SELECT sum(l."Qty") FROM "{table[..^1]}Lines" l WHERE l."{idColumn}"=i."{idColumn}")::numeric(18,2)
                       ||'|'||i."TotalAmount"::numeric(18,2)
                FROM "{table}" i WHERE i."{idColumn}"='{id}'
                """);

        /// <summary>某張單的分錄「借方合計/貸方合計」。</summary>
        private static async Task<string?> JournalTotalsAsync(string docType, string docId) =>
            await SeedFixture.ScalarAsync<string>(
                $"""
                SELECT coalesce(sum(l."Debit"),0)::numeric(18,2)::text||'/'||coalesce(sum(l."Credit"),0)::numeric(18,2)::text
                FROM "JournalEntries" j JOIN "JournalLines" l ON l."JournalId"=j."JournalId"
                WHERE j."SourceDocType"='{docType}' AND j."SourceDocId"='{docId}'
                """);
    }
}
