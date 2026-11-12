using System.Text.Json;
using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.FIN
{
    /// <summary>
    /// 應付折讓（APDebitNote）／應收折讓（ARCreditNote）面板守門。兩個畫面結構相同，差別只在資料表。
    ///
    /// <para><b>為什麼不用 <see cref="DocActionSmokeTestBase"/></b>：折讓單沒有「新增」，
    /// 只由退貨單確認時的服務產生。草稿由 seed-e2e-fixture.sql ⑲ 直接放好。</para>
    ///
    /// <para><b>為什麼要按到「確認」並驗資料庫</b>：這是會寫總帳的動作，而畫面上的成功提示
    /// 只代表 API 回了 success。之前這兩個畫面沒有任何 Playwright 覆蓋——
    /// 「按了沒反應」「按了有反應但帳沒過」都不會有人發現。</para>
    /// </summary>
    [Collection("UI Tests")]
    public abstract class CreditNoteUiTestBase
    {
        protected readonly UiTestFixture _fixture;
        protected CreditNoteUiTestBase(UiTestFixture fixture) => _fixture = fixture;

        private const string PanelBodySel = "#sidePanelBody";

        /// <summary>畫面路徑，也是控制器名與分錄的 SourceDocType，例如 "APDebitNote"。</summary>
        protected abstract string DocType { get; }
        protected abstract string NoteId { get; }
        protected abstract string NoteTable { get; }
        protected abstract string NoteIdColumn { get; }
        protected abstract string InvoiceId { get; }
        protected abstract string InvoiceTable { get; }
        protected abstract string InvoiceIdColumn { get; }
        /// <summary>發票上累計折讓額的欄位（AP：DebitedAmount；AR：CreditedAmount）。</summary>
        protected abstract string InvoiceCreditedColumn { get; }
        /// <summary>收付款畫面挑發票的端點（AR：/ARReceipt/GetOpenInvoices?customerCode=）。</summary>
        protected abstract string OpenInvoicesPath { get; }
        protected abstract string PartyColumn { get; }
        protected abstract string SettledColumn { get; }
        /// <summary>退款用的收付款畫面（AR：ARReceipt；AP：APPayment），也是分錄 SourceDocType。</summary>
        protected abstract string SettleDocType { get; }
        protected abstract string SettleTable { get; }
        protected abstract string SettleIdColumn { get; }
        protected abstract string AllocationTable { get; }
        protected abstract string PartySelectName { get; }
        protected abstract string PartySearchParam { get; }
        protected abstract string ConfirmSettleAction { get; }
        /// <summary>退款分錄逐科目「借−貸」（AR：借 應收 105／貸 銀行；AP：借 銀行／貸 應付）。</summary>
        protected abstract string RefundJournalNet { get; }

        private async Task<(IPage page, List<string> jsErrors)> OpenDraftAsync()
        {
            await SeedFixture.ResetAsync();

            var page = await _fixture.NewPageAsync();
            var jsErrors = new List<string>();
            page.PageError += (_, e) => jsErrors.Add(e);

            await page.GotoAsync($"{_fixture.BaseUrl}/{DocType}?searched=true");
            var open = page.Locator($"[data-url$='/{DocType}/Details/{NoteId}']");
            Assert.True(await open.CountAsync() > 0,
                $"/{DocType} 列表上找不到草稿 {NoteId}。種子 ⑲ 沒建起來，或列表過濾（廠別／資料授權）把它藏起來了。");

            await open.First.ClickAsync();
            await Assertions.Expect(page.Locator($"{PanelBodySel} [data-action='confirmNote']")).ToBeVisibleAsync();
            return (page, jsErrors);
        }

        [Fact]
        public async Task 草稿面板打得開且每個動作都接得上()
        {
            var (page, jsErrors) = await OpenDraftAsync();

            var missing = await page.EvaluateAsync<string[]>(@"(bodySel) => {
                const names = new Set();
                document.querySelectorAll(bodySel + ' [data-action]').forEach(el => names.add(el.dataset.action));
                return [...names].filter(n => typeof window[n] !== 'function');
            }", PanelBodySel);

            Assert.True(missing.Length == 0,
                $"/{DocType} 面板上的 data-action 找不到全域函式：{string.Join("、", missing)}（按了會沒反應）。");
            Assert.True(jsErrors.Count == 0, $"/{DocType} 打開草稿時有 JS 錯誤：\n{string.Join("\n", jsErrors)}");
        }

        [Fact]
        public async Task 在面板上確認之後單據過帳且發票折讓額與分錄都寫入()
        {
            var (page, _) = await OpenDraftAsync();

            string? dialogText = null;
            page.Dialog += async (_, d) =>
            {
                // 第一個是 confirm()；失敗時的 alert() 也會走到這裡，把訊息留下來當失敗說明。
                if (d.Type == "alert") dialogText = d.Message;
                await d.AcceptAsync();
            };

            var response = await page.RunAndWaitForResponseAsync(
                () => page.Locator($"{PanelBodySel} [data-action='confirmNote']").ClickAsync(),
                r => r.Url.Contains($"/{DocType}/Confirm/{NoteId}"));
            var body = await response.TextAsync();
            Assert.True(body.Contains("\"success\":true"),
                $"/{DocType} 確認被拒：{body}{(dialogText is null ? "" : $"（畫面提示：{dialogText}）")}");

            var status = await SeedFixture.ScalarAsync<int>(
                $@"SELECT ""Status"" FROM ""{NoteTable}"" WHERE ""{NoteIdColumn}"" = '{NoteId}'");
            Assert.Equal(1, status);   // CreditNoteStatus.Posted

            var credited = await SeedFixture.ScalarAsync<decimal>(
                $@"SELECT ""{InvoiceCreditedColumn}"" FROM ""{InvoiceTable}"" WHERE ""{InvoiceIdColumn}"" = '{InvoiceId}'");
            Assert.Equal(105m, credited);

            // 分錄：借貸各自合計都是含稅 105，而且相等。只數 SourceDocId＝這張單的分錄。
            var sql = $@"
                SELECT coalesce(sum(l.""Debit""),0)::text || '/' || coalesce(sum(l.""Credit""),0)::text
                FROM ""JournalEntries"" j JOIN ""JournalLines"" l ON l.""JournalId"" = j.""JournalId""
                WHERE j.""SourceDocType"" = '{DocType}' AND j.""SourceDocId"" = '{NoteId}'";
            var totals = await SeedFixture.ScalarAsync<string>(sql);
            Assert.Equal("105.0000/105.0000", totals);

            // 收付款畫面挑發票時的未結要扣掉折讓——否則照原額收付得下去（超收／溢付）。
            var party = await SeedFixture.ScalarAsync<string>(
                $@"SELECT ""{PartyColumn}"" FROM ""{InvoiceTable}"" WHERE ""{InvoiceIdColumn}"" = '{InvoiceId}'");
            var expected = await SeedFixture.ScalarAsync<decimal>(
                $@"SELECT ""TotalAmount"" - ""{SettledColumn}"" - ""{InvoiceCreditedColumn}"" FROM ""{InvoiceTable}"" WHERE ""{InvoiceIdColumn}"" = '{InvoiceId}'");
            var url  = $"{OpenInvoicesPath}{Uri.EscapeDataString(party!)}";
            var text = await page.EvaluateAsync<string>(
                "async u => { const r = await fetch(u, { headers: { 'X-Requested-With': 'XMLHttpRequest' } }); return r.status + '\\n' + await r.text(); }", url);
            var nl = text.IndexOf('\n');
            Assert.True(text.StartsWith("200") && text[(nl + 1)..].TrimStart().StartsWith("["),
                $"{url} 沒有回 JSON 陣列：{text[..Math.Min(300, text.Length)]}");
            var json = JsonDocument.Parse(text[(nl + 1)..]).RootElement;
            var row = json.EnumerateArray().Select(e => (JsonElement?)e).FirstOrDefault(e =>
                e!.Value.EnumerateObject().Any(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.String && p.Value.GetString() == InvoiceId));
            // 種子發票是全額收付的，折讓後是多收付（負餘額）——要列出來供退款／抵用；沖平（0）才不列。
            if (expected != 0)
            {
                Assert.True(row.HasValue, $"{OpenInvoicesPath} 清單上找不到 {InvoiceId}（未結 {expected}）");
                Assert.Equal(expected, row!.Value.GetProperty("outstanding").GetDecimal());
            }
            else
                Assert.False(row.HasValue, $"{InvoiceId} 已被折讓沖平，不該再出現在 {OpenInvoicesPath} 的可核銷清單");
        }

        /// <summary>
        /// 全額收付後才開折讓＝多收付 105。從收付款畫面對該發票填 −105 退款：
        /// 發票餘額歸零且仍是收付清、銀行與總帳同步。原本這筆錢只看得到、沒有任何畫面能沖掉。
        /// </summary>
        [Fact]
        public async Task 折讓後多收付_從收付款畫面以負數核銷退款()
        {
            var (page, _) = await OpenDraftAsync();
            var alerts = new List<string>();
            page.Dialog += async (_, d) => { if (d.Type == "alert") alerts.Add(d.Message); await d.AcceptAsync(); };
            await ClickNoteActionAsync(page, "confirmNote", "Confirm", alerts);

            var party = await SeedFixture.ScalarAsync<string>(
                $@"SELECT ""{PartyColumn}"" FROM ""{InvoiceTable}"" WHERE ""{InvoiceIdColumn}"" = '{InvoiceId}'");
            var bankBefore = await SeedFixture.ScalarAsync<decimal>(@"SELECT ""Balance"" FROM ""BankAccounts"" WHERE ""AccountId"" = 'E2E-BANK'");

            await page.GotoAsync($"{_fixture.BaseUrl}/{SettleDocType}?searched=true");
            await page.ClickAsync($"button[data-url$='/{SettleDocType}/Create']");
            await page.WaitForSelectorAsync("#sidePanelBody #panelForm");
            await page.Locator($"#sidePanelBody select[name='{PartySelectName}']").SelectOptionAsync(party!);
            var alloc = page.Locator("#invoiceContainer tr", new PageLocatorOptions { HasText = InvoiceId }).Locator(".alloc-amt").First;
            await Assertions.Expect(alloc).ToBeVisibleAsync();
            Assert.Equal("-105", (await alloc.GetAttributeAsync("data-outstanding"))?.Split('.')[0]);
            await alloc.FillAsync("-105");
            await alloc.DispatchEventAsync("input");
            await page.SelectOptionAsync("#sidePanelBody select[name='BankAccountId']", "E2E-BANK");
            Assert.Equal("-105", await page.InputValueAsync("#sidePanelBody input[name='Amount']"));   // 金額跟著核銷合計
            await page.ClickAsync("#sidePanelBody button[type='submit']");

            string? docId = null;
            for (var waited = 0; waited < 15_000 && docId is null; waited += 250)
            {
                docId = await SeedFixture.ScalarAsync<string>($@"
                    SELECT d.""{SettleIdColumn}"" FROM ""{SettleTable}"" d JOIN ""{AllocationTable}"" a ON a.""{SettleIdColumn}"" = d.""{SettleIdColumn}""
                    WHERE a.""{InvoiceIdColumn}"" = '{InvoiceId}' AND a.""AllocatedAmount"" = -105 AND d.""Status"" = 0
                    ORDER BY d.""CreatedAt"" DESC LIMIT 1");
                if (docId is null) await Task.Delay(250);
            }
            Assert.True(docId is not null, $"/{SettleDocType} 退款單沒有存進去。畫面錯誤：" +
                await page.Locator("#sidePanelBody .validation-summary-errors, #sidePanelBody .text-danger").AllInnerTextsAsync().ContinueWith(t => string.Join(" / ", t.Result)));

            await page.GotoAsync($"{_fixture.BaseUrl}/{SettleDocType}?searched=true&{PartySearchParam}={Uri.EscapeDataString(party!)}&searchStatus=Draft&pageSize=50");
            var settled = await page.RunAndWaitForResponseAsync(
                () => page.Locator($"[data-action='{ConfirmSettleAction}'][data-id='{docId}']").First.ClickAsync(),
                r => r.Url.Contains($"/{SettleDocType}/Confirm/{docId}"));
            var body = await settled.TextAsync();
            Assert.True(body.Contains("\"success\":true"), $"/{SettleDocType} 退款確認被拒：{body}");

            Assert.Equal(0m, await SeedFixture.ScalarAsync<decimal>(
                $@"SELECT ""TotalAmount"" - ""{SettledColumn}"" - ""{InvoiceCreditedColumn}"" FROM ""{InvoiceTable}"" WHERE ""{InvoiceIdColumn}"" = '{InvoiceId}'"));
            Assert.Equal(2, await SeedFixture.ScalarAsync<int>(
                $@"SELECT ""Status"" FROM ""{InvoiceTable}"" WHERE ""{InvoiceIdColumn}"" = '{InvoiceId}'"));   // 仍是收訖／付清
            var bankAfter = await SeedFixture.ScalarAsync<decimal>(@"SELECT ""Balance"" FROM ""BankAccounts"" WHERE ""AccountId"" = 'E2E-BANK'");
            Assert.Equal(SettleDocType == "ARReceipt" ? bankBefore - 105m : bankBefore + 105m, bankAfter);
            Assert.Equal(RefundJournalNet, await SeedFixture.ScalarAsync<string>($@"
                SELECT string_agg(code || ':' || net::text, '|' ORDER BY code) FROM (
                  SELECT l.""AccountCode"" code, sum(l.""Debit"" - l.""Credit"") net
                  FROM ""JournalLines"" l JOIN ""JournalEntries"" j ON j.""JournalId"" = l.""JournalId""
                  WHERE j.""SourceDocType"" = '{SettleDocType}' AND j.""SourceDocId"" = '{docId}'
                  GROUP BY l.""AccountCode"") x"));
        }

        private async Task ClickNoteActionAsync(IPage page, string action, string route, List<string> alerts)
        {
            var response = await page.RunAndWaitForResponseAsync(
                () => page.Locator($"{PanelBodySel} [data-action='{action}']").ClickAsync(),
                r => r.Url.Contains($"/{DocType}/{route}/{NoteId}"));
            var body = await response.TextAsync();
            Assert.True(body.Contains("\"success\":true"),
                $"/{DocType} {route} 被拒：{body}{(alerts.Count == 0 ? "" : $"（畫面提示：{string.Join(" / ", alerts)}）")}");
        }

        /// <summary>
        /// 作廢已過帳的折讓單：發票上的累計折讓額要扣回、分錄要整組反向——
        /// 兩張分錄（確認＋作廢）逐科目淨額為零。只把狀態改成作廢而沒回沖，
        /// 發票會永遠少掉那 105 元的可沖帳金額，總帳卻也還掛著折讓。
        /// </summary>
        [Fact]
        public async Task 確認後作廢_發票折讓額扣回且分錄整組反向()
        {
            var (page, _) = await OpenDraftAsync();
            var alerts = new List<string>();
            page.Dialog += async (_, d) =>
            {
                if (d.Type == "alert") alerts.Add(d.Message);
                await d.AcceptAsync();
            };

            await ClickNoteActionAsync(page, "confirmNote", "Confirm", alerts);

            // 確認後頁面會重新整理；重新打開這張單，作廢鈕在已過帳的面板上。
            await page.GotoAsync($"{_fixture.BaseUrl}/{DocType}?searched=true");
            await page.Locator($"[data-url$='/{DocType}/Details/{NoteId}']").First.ClickAsync();
            await Assertions.Expect(page.Locator($"{PanelBodySel} [data-action='voidNote']")).ToBeVisibleAsync();

            await ClickNoteActionAsync(page, "voidNote", "Void", alerts);

            Assert.Equal(2, await SeedFixture.ScalarAsync<int>(
                $@"SELECT ""Status"" FROM ""{NoteTable}"" WHERE ""{NoteIdColumn}"" = '{NoteId}'"));   // CreditNoteStatus.Void
            Assert.Equal(0m, await SeedFixture.ScalarAsync<decimal>(
                $@"SELECT ""{InvoiceCreditedColumn}"" FROM ""{InvoiceTable}"" WHERE ""{InvoiceIdColumn}"" = '{InvoiceId}'"));

            var from = $@"FROM ""JournalEntries"" j JOIN ""JournalLines"" l ON l.""JournalId"" = j.""JournalId""
                          WHERE j.""SourceDocType"" = '{DocType}' AND j.""SourceDocId"" = '{NoteId}'";
            Assert.Equal(2L, await SeedFixture.ScalarAsync<long>($@"SELECT count(DISTINCT j.""JournalId"") {from}"));
            var unbalancedAccounts = await SeedFixture.ScalarAsync<long>($@"
                SELECT count(*) FROM (SELECT l.""AccountCode"" {from}
                  GROUP BY l.""AccountCode"" HAVING abs(sum(l.""Debit"" - l.""Credit"")) > 0.0001) x");
            Assert.True(unbalancedAccounts == 0, $"/{DocType} 作廢後有 {unbalancedAccounts} 個科目沒有被沖回。");
        }
    }

    public class APDebitNoteUiTests : CreditNoteUiTestBase
    {
        public APDebitNoteUiTests(UiTestFixture fixture) : base(fixture) { }
        protected override string DocType               => "APDebitNote";
        protected override string NoteId                => "E2E-DN01";
        protected override string NoteTable             => "APDebitNotes";
        protected override string NoteIdColumn          => "APDebitNoteId";
        protected override string InvoiceId             => "E2E-API01";
        protected override string InvoiceTable          => "APInvoices";
        protected override string InvoiceIdColumn       => "APInvoiceId";
        protected override string InvoiceCreditedColumn => "DebitedAmount";
        protected override string OpenInvoicesPath      => "/APPayment/GetOpenInvoices?supplierCode=";
        protected override string PartyColumn           => "SupplierCode";
        protected override string SettledColumn         => "PaidAmount";
        protected override string SettleDocType         => "APPayment";
        protected override string SettleTable           => "APPayments";
        protected override string SettleIdColumn        => "APPaymentId";
        protected override string AllocationTable       => "APPaymentAllocations";
        protected override string PartySelectName       => "SupplierCode";
        protected override string PartySearchParam      => "searchSupplier";
        protected override string ConfirmSettleAction   => "confirmPayment";
        protected override string RefundJournalNet      => "1130:105.0000|2100:-105.0000";
    }

    public class ARCreditNoteUiTests : CreditNoteUiTestBase
    {
        public ARCreditNoteUiTests(UiTestFixture fixture) : base(fixture) { }
        protected override string DocType               => "ARCreditNote";
        protected override string NoteId                => "E2E-CN01";
        protected override string NoteTable             => "ARCreditNotes";
        protected override string NoteIdColumn          => "ARCreditNoteId";
        protected override string InvoiceId             => "E2E-ARI01";
        protected override string InvoiceTable          => "ARInvoices";
        protected override string InvoiceIdColumn       => "ARInvoiceId";
        protected override string InvoiceCreditedColumn => "CreditedAmount";
        protected override string OpenInvoicesPath      => "/ARReceipt/GetOpenInvoices?customerCode=";
        protected override string PartyColumn           => "CustomerCode";
        protected override string SettledColumn         => "ReceivedAmount";
        protected override string SettleDocType         => "ARReceipt";
        protected override string SettleTable           => "ARReceipts";
        protected override string SettleIdColumn        => "ARReceiptId";
        protected override string AllocationTable       => "ARReceiptAllocations";
        protected override string PartySelectName       => "CustomerCode";
        protected override string PartySearchParam      => "searchCustomer";
        protected override string ConfirmSettleAction   => "confirmReceipt";
        protected override string RefundJournalNet      => "1130:-105.0000|1170:105.0000";
    }
}
