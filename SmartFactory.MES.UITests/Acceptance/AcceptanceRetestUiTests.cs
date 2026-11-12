using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;

namespace SmartFactory.MES.UITests.Acceptance
{
    /// <summary>
    /// 人工驗收重測項目的自動版：R-3（刪除排程與銀行帳戶）、R-5（試算表預設期間）。
    ///
    /// <para>R-3 的原缺陷：共用的刪除按鈕把 id 再接到網址後面，這兩頁的網址本來就帶 id，變成
    /// <c>/Delete/6/6</c> 而回 404——畫面只跳一個錯誤，資料還在。所以斷言一定要看資料庫，不能只看沒有錯誤。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class AcceptanceRetestUiTests
    {
        private readonly UiTestFixture _fixture;
        public AcceptanceRetestUiTests(UiTestFixture fixture) => _fixture = fixture;

        [Fact]
        public async Task R3_報表排程_從列表刪除_確認框顯示排程名稱_資料庫真的刪掉()
        {
            var name = "E2E-R3-" + Guid.NewGuid().ToString("N")[..8];
            var id = await SeedFixture.ScalarAsync<int>($"""
                INSERT INTO "ReportDefinitions"
                  ("Name","OwnerUserId","OwnerRoleId","PlantId","ToolKey","ParamsJson","ScheduleKind","ScheduleHour","ScheduleMinute",
                   "IsActive","ManualRunRequested","OutputFormat","CreatedByRoleId","CreatedAt")
                SELECT '{name}', u."Id", r."Id", 'P1', 'sales_ranking', '{"{"}{"}"}', 0, 7, 0, true, false, 'xlsx', r."Id", now()
                FROM "AspNetUsers" u, "AspNetRoles" r
                WHERE u."UserName" = 'admin' AND r."Name" = 'Admin'
                RETURNING "Id";
                """);

            var page = await SignedInAsync();
            var dialogs = CollectDialogs(page);
            await page.GotoAsync($"{_fixture.BaseUrl}/ReportSchedule?searched=true&plantFilter=P1&searchTerm={name}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            var del = page.Locator($"[data-action='deleteItem'][data-id='{id}']");
            Assert.True(await del.CountAsync() == 1, $"列表上找不到排程 {name} 的刪除鈕");
            await del.ClickAsync();

            await WaitGoneAsync($"""SELECT count(*) FROM "ReportDefinitions" WHERE "Id" = {id}""");
            Assert.Contains(dialogs, d => d.Type == DialogType.Confirm && d.Message.Contains(name));
            Assert.DoesNotContain(dialogs, d => d.Type == DialogType.Alert);
        }

        [Fact]
        public async Task R3_銀行帳戶_新增後從列表刪除_資料庫真的刪掉()
        {
            var accountId = "E2ER3" + Random.Shared.Next(100000, 999999);
            var page = await SignedInAsync();
            var crud = new DocFormPage(page, _fixture.BaseUrl);
            await crud.GotoIndexAsync("/BankAccount");
            await crud.OpenNewPanelAsync("button[data-url='/BankAccount/Create']", "#panelForm");
            await page.FillAsync("#sidePanelBody input[name='AccountId']:not([type=hidden])", accountId);
            await page.FillAsync("#sidePanelBody input[name='BankName']", "R3 測試銀行");
            await page.FillAsync("#sidePanelBody input[name='AccountNo']", "0000000000");
            await crud.ClickSaveAsync();
            Assert.Equal(1L, await SeedFixture.ScalarAsync<long>($"""SELECT count(*) FROM "BankAccounts" WHERE "AccountId" = '{accountId}'"""));

            var dialogs = CollectDialogs(page);
            await page.GotoAsync($"{_fixture.BaseUrl}/BankAccount?searched=true&search={accountId}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            var del = page.Locator($"[data-action='deleteItem'][data-id='{accountId}']");
            Assert.True(await del.CountAsync() == 1, $"列表上找不到銀行帳戶 {accountId} 的刪除鈕");
            await del.ClickAsync();

            await WaitGoneAsync($"""SELECT count(*) FROM "BankAccounts" WHERE "AccountId" = '{accountId}'""");
            Assert.DoesNotContain(dialogs, d => d.Type == DialogType.Alert);
        }

        [Fact]
        public async Task R5_試算表_不改條件就是本期到本期_未來期間有標示()
        {
            var page = await SignedInAsync();
            await page.GotoAsync($"{_fixture.BaseUrl}/TrialBalance");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            var from = await page.Locator("select[name='fromPeriod']").InputValueAsync();
            var to   = await page.Locator("select[name='toPeriod']").InputValueAsync();
            Assert.False(string.IsNullOrEmpty(from), "起始期間不得是空的（空的會從最遠的未來期間排起）");
            Assert.Equal(from, to);

            var texts = await page.Locator("select[name='fromPeriod'] option").AllTextContentsAsync();
            var values = await page.Locator("select[name='fromPeriod'] option").EvaluateAllAsync<string[]>("os => os.map(o => o.value)");
            var selectedText = texts[Array.IndexOf(values, from)];
            var later = values.Where(v => string.CompareOrdinal(v, from) > 0).ToList();
            Assert.Equal(selectedText.Trim(), from);   // 本期本身不標「未來」
            foreach (var v in later)
                Assert.NotEqual(v, texts[Array.IndexOf(values, v)].Trim());   // 之後的期間都有後綴
        }

        [Fact]
        public async Task R4_手動傳票_貼上逐筆說明略過原因_任一列可刪_存檔後沖銷產生借貸對調_不能沖第二次()
        {
            await SeedFixture.ResetAsync();   // ⑱ 兩個可手動選用的 E2E 科目
            var notManual = await SeedFixture.ScalarAsync<string>(
                """SELECT "AccountCode" FROM "ChartOfAccounts" WHERE NOT "AllowManualEntry" AND "IsActive" ORDER BY "AccountCode" LIMIT 1""");
            Assert.False(string.IsNullOrEmpty(notManual), "測試庫沒有不可手動選用的科目");

            var page = await SignedInAsync();
            var crud = new DocFormPage(page, _fixture.BaseUrl);
            await crud.GotoIndexAsync("/JournalEntry");
            await crud.OpenNewPanelAsync("button[data-url$='/JournalEntry/Create']", "#manualJournalForm");
            const string P = "#sidePanelBody";
            await page.FillAsync($"{P} input[name='Description']", "E2E R4 手動傳票");

            // 先清掉預設的空白列，只留貼上的內容
            while (await page.Locator($"{P} #jeLinesBody [data-action='jeRemoveLine']").CountAsync() > 0)
                await page.Locator($"{P} #jeLinesBody [data-action='jeRemoveLine']").First.ClickAsync();

            await page.ClickAsync($"{P} [data-action='jeTogglePaste']");
            var pasted = $"E2E-6101\t1000\t\tR4 借\nE2E-1101\t\t1000\tR4 貸\nE2E-NOPE\t5\t\n{notManual}\t1\t";
            await page.FillAsync($"{P} #jePasteText", pasted);
            await page.ClickAsync($"{P} [data-action='jeApplyPaste']");

            var msg = await page.Locator($"{P} #jePasteMsg").InnerTextAsync();
            Assert.Contains("E2E-NOPE", msg);              // 找不到的科目
            Assert.Contains(notManual!, msg);              // 未開放手動選用的科目，各自說明
            Assert.False(string.IsNullOrEmpty(await page.InputValueAsync($"{P} #jePasteText")), "有略過時原文要留著，方便改完重貼");
            Assert.Equal(2, await page.Locator($"{P} #jeLinesBody tr").CountAsync());

            // 任何一列都能刪：新增三列、刪最後一列
            for (var i = 0; i < 3; i++) await page.ClickAsync($"{P} [data-action='jeAddLine']");
            Assert.Equal(5, await page.Locator($"{P} #jeLinesBody tr").CountAsync());
            await page.Locator($"{P} #jeLinesBody tr:last-child [data-action='jeRemoveLine']").ClickAsync();
            await page.Locator($"{P} #jeLinesBody tr:last-child [data-action='jeRemoveLine']").ClickAsync();
            await page.Locator($"{P} #jeLinesBody tr:last-child [data-action='jeRemoveLine']").ClickAsync();
            Assert.Equal(2, await page.Locator($"{P} #jeLinesBody tr").CountAsync());

            // 成本中心是組織單位下拉（有選項就選第一個）
            var cc = page.Locator($"{P} select[name='Lines[0].CostCenter'] option:not([value=''])");
            if (await cc.CountAsync() > 0)
                await page.Locator($"{P} select[name='Lines[0].CostCenter']").SelectOptionAsync(await cc.First.GetAttributeAsync("value") ?? "");

            var saveResp = page.WaitForResponseAsync(r => r.Request.Method == "POST" && r.Url.Contains("/JournalEntry/Create"));
            await page.ClickAsync($"{P} button[type='submit']");
            var saveText = await (await saveResp).TextAsync();
            using var save = System.Text.Json.JsonDocument.Parse(saveText);
            Assert.True(save.RootElement.GetProperty("success").GetBoolean(), saveText);
            var id = save.RootElement.GetProperty("data").GetProperty("id").GetString()!;

            // 打開剛存的傳票，按「沖銷」並指定日期
            var dialogs = CollectDialogs(page);
            await page.GotoAsync($"{_fixture.BaseUrl}/JournalEntry?searched=true&searchDocType=Manual");
            await crud.OpenEditPanelAsync($"[data-url*='/JournalEntry/Details/{id}']", "#revDate");
            await page.FillAsync("#revDate", DateTime.Today.ToString("yyyy-MM-dd"));
            var revResp = page.WaitForResponseAsync(r => r.Request.Method == "POST" && r.Url.Contains($"/JournalEntry/Reverse/{id}"));
            await page.Locator("#sidePanelBody form[action*='/Reverse/'] button[type='submit']").ClickAsync();
            using var rev = System.Text.Json.JsonDocument.Parse(await (await revResp).TextAsync());
            Assert.True(rev.RootElement.GetProperty("success").GetBoolean());
            var revId = rev.RootElement.GetProperty("data").GetProperty("id").GetString()!;

            // 借貸對調：原借 6101 1000 → 沖銷貸 6101 1000
            Assert.Equal(1000m, await SeedFixture.ScalarAsync<decimal>(
                $"""SELECT "Credit" FROM "JournalLines" WHERE "JournalId"='{revId}' AND "AccountCode"='E2E-6101'"""));
            Assert.Equal(1000m, await SeedFixture.ScalarAsync<decimal>(
                $"""SELECT "Debit" FROM "JournalLines" WHERE "JournalId"='{revId}' AND "AccountCode"='E2E-1101'"""));
            Assert.Equal(id, await SeedFixture.ScalarAsync<string>(
                $"""SELECT "SourceDocId" FROM "JournalEntries" WHERE "JournalId"='{revId}' AND "SourceDocType"='ManualReversal'"""));
            Assert.Contains(dialogs, d => d.Type == DialogType.Confirm);

            // 原傳票顯示「已由 X 沖銷」，而且沒有沖銷鈕了
            await page.GotoAsync($"{_fixture.BaseUrl}/JournalEntry?searched=true&searchDocType=Manual");
            await crud.OpenEditPanelAsync($"[data-url*='/JournalEntry/Details/{id}']", "code");
            Assert.Contains(revId, await page.InnerTextAsync("#sidePanelBody"));
            Assert.Equal(0, await page.Locator("#sidePanelBody form[action*='/Reverse/']").CountAsync());
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        private async Task<IPage> SignedInAsync()
        {
            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);
            return page;
        }

        private sealed record Seen(string Type, string Message);

        /// <summary>confirm 一律按確定；alert 是失敗的訊息管道，收起來斷言。</summary>
        private static List<Seen> CollectDialogs(IPage page)
        {
            var seen = new List<Seen>();
            page.Dialog += async (_, d) =>
            {
                lock (seen) seen.Add(new Seen(d.Type, d.Message));
                try { await d.AcceptAsync(); } catch (PlaywrightException) { }
            };
            return seen;
        }

        private static async Task WaitGoneAsync(string countSql)
        {
            for (var i = 0; i < 50; i++)
            {
                if (await SeedFixture.ScalarAsync<long>(countSql) == 0) return;
                await Task.Delay(200);
            }
            Assert.Fail("按了刪除，資料庫裡還在：" + countSql);
        }
    }
}
