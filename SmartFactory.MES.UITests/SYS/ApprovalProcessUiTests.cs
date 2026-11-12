using Microsoft.Playwright;
using SmartFactory.MES.UITests.Acceptance;
using Xunit;

namespace SmartFactory.MES.UITests.SYS
{
    /// <summary>
    /// 「待我簽核」畫面的核准、退回修改、駁回——走真的瀏覽器點擊。
    ///
    /// <para><b>為什麼要補</b>：2026-10-20 盤點時發現，所有 UI 測試的「核准」都是直接改資料庫
    /// （<c>UPDATE ... SET "ApprovalStatus"=2</c>），簽核面板的三個按鈕、意見欄、送出後清單更新
    /// 從來沒有被點過。整合測試驗的是服務層，面板 JS 壞掉（例如 submitter 沒帶進 FormData，
    /// 三個按鈕送出去都變同一個動作）完全測不到。</para>
    ///
    /// <para>做法：測試期間替請購單掛一份「角色 1（Admin）簽核一關」的設定，用畫面新增請購單並送審，
    /// 再到「待我簽核」按按鈕，從資料庫確認單據與簽核單的狀態。設定在 finally 還原，
    /// 不影響其他假設請購單免簽核的測試。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class ApprovalProcessUiTests
    {
        private const string Plant = "P1";
        private const string TemplateId = "E2E-APR-TPL";
        private const string StepId = "E2E-APR-S1";
        private const string ConfigId = "E2E-APR-CFG-PR";

        private readonly UiTestFixture _fixture;
        public ApprovalProcessUiTests(UiTestFixture fixture) => _fixture = fixture;

        // 簽核單 ApprovalRequests.Status：Pending=0 Approved=1 Rejected=2 ReturnedForRevision=4
        // 單據 ApprovalStatus：Draft=0 PendingApproval=1 Approved=2 Rejected=3
        [Theory]
        [InlineData("1", 1, 2)]   // 核准 → 單據已核准
        [InlineData("4", 4, 0)]   // 退回修改 → 單據回草稿，可修改後重送
        [InlineData("2", 2, 3)]   // 駁回 → 單據已退回
        public async Task PendingPanel_Buttons_DriveRequestAndDocumentStatus(string action, int requestStatus, int docStatus)
        {
            string? prevConfig = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "Id" FROM "DocumentApprovalConfigs" WHERE "DocumentType"='PurchaseRequest' AND "PlantId"='{Plant}'""");
            await ArrangeApprovalConfigAsync(prevConfig);
            try
            {
                var page = await _fixture.NewPageAsync();
                var alerts = new List<string>();
                page.Dialog += async (_, d) =>
                {
                    if (d.Type == DialogType.Alert) lock (alerts) alerts.Add(d.Message);
                    try { await d.AcceptAsync(); } catch (PlaywrightException) { }
                };

                var prId = await CreatePurchaseRequestAsync(page);
                await SubmitAsync(page, prId);
                Assert.Equal(1, await DocStatusAsync(prId));

                // ── 待我簽核：找到這張單，開簽核面板 ─────────────────────────────
                await page.GotoAsync($"{_fixture.BaseUrl}/Approval/PendingIndex");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                var reqId = await SeedFixture.ScalarAsync<string>(
                    $"""SELECT "Id" FROM "ApprovalRequests" WHERE "DocumentType"='PurchaseRequest' AND "DocumentId"='{prId}' ORDER BY "CreatedAt" DESC LIMIT 1""");
                Assert.False(string.IsNullOrEmpty(reqId), $"請購單 {prId} 送審後沒有簽核單");

                var processBtn = page.Locator($"button[data-action='openPanel'][data-url*='/Approval/Process/{reqId}']");
                await Assertions.Expect(processBtn).ToBeVisibleAsync(new() { Timeout = 10_000 });
                await processBtn.ClickAsync();
                var form = page.Locator("#crudPanelBody form[data-ajax='true']");
                await Assertions.Expect(form).ToBeVisibleAsync(new() { Timeout = 10_000 });

                await form.Locator("textarea[name='comment']").FillAsync($"E2E {action}");
                alerts.Clear();
                await form.Locator($"button[name='approvalAction'][value='{action}']").ClickAsync();

                // ── 資料庫確認：按哪一顆就是哪一個結果（三顆按鈕共用一個 form，submitter 沒帶上就會錯）
                int? reqStatus = null, doc = null;
                for (var i = 0; i < 30; i++)
                {
                    reqStatus = await IntAsync($"""SELECT "Status"::text FROM "ApprovalRequests" WHERE "Id"='{reqId}'""");
                    doc = await DocStatusAsync(prId);
                    if (reqStatus == requestStatus && doc == docStatus) break;
                    await page.WaitForTimeoutAsync(300);
                }
                Assert.True(alerts.Count == 0, "簽核送出後跳出錯誤：" + string.Join(" / ", alerts));
                Assert.Equal(requestStatus, reqStatus);
                Assert.Equal(docStatus, doc);

                // 依動作類型取這次按下的那一筆（ApprovalLogs.Action 與申請單 Status 同一列舉），不靠 ActionTime 排序：
                // 送審與決策相隔一兩秒，WSL 時鐘回撥時「最新一筆」會變成送審那筆而偶發失敗（2026-10-27）。
                var comment = await SeedFixture.ScalarAsync<string>(
                    $"""SELECT "Comment" FROM "ApprovalLogs" WHERE "RequestId"='{reqId}' AND "Action"={requestStatus}""");
                Assert.Equal($"E2E {action}", comment);

                // 處理完就不該再出現在待我簽核
                await page.GotoAsync($"{_fixture.BaseUrl}/Approval/PendingIndex");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                Assert.Equal(0, await page.Locator($"[data-url*='/Approval/Process/{reqId}']").CountAsync());
            }
            finally
            {
                await RestoreApprovalConfigAsync(prevConfig);
            }
        }

        // ── 撤回：送審中的單據面板有「撤回」，按下去簽核單變已撤回、單據回草稿、可再送審 ─────

        [Fact]
        public async Task Withdraw_PendingDocument_ReturnsToDraft_AndLeavesPendingList()
        {
            string? prevConfig = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "Id" FROM "DocumentApprovalConfigs" WHERE "DocumentType"='PurchaseRequest' AND "PlantId"='{Plant}'""");
            await ArrangeApprovalConfigAsync(prevConfig);
            try
            {
                var page = await _fixture.NewPageAsync();
                var dialogs = new List<string>();
                page.Dialog += async (_, d) =>
                {
                    lock (dialogs) dialogs.Add(d.Message);
                    try { await d.AcceptAsync(); } catch (PlaywrightException) { }
                };

                var prId = await CreatePurchaseRequestAsync(page);
                await SubmitAsync(page, prId);
                Assert.Equal(1, await DocStatusAsync(prId));
                var reqId = await SeedFixture.ScalarAsync<string>(
                    $"""SELECT "Id" FROM "ApprovalRequests" WHERE "DocumentType"='PurchaseRequest' AND "DocumentId"='{prId}' ORDER BY "CreatedAt" DESC LIMIT 1""");

                await page.GotoAsync($"{_fixture.BaseUrl}/PurchaseRequest?searched=true&searchText={prId}");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.Locator($"[data-action='openPanel'][data-url*='{prId}']").First.ClickAsync();
                var withdraw = page.Locator($"#sidePanelBody [data-action='withdrawApproval'][data-id='{prId}']");
                await Assertions.Expect(withdraw).ToBeVisibleAsync(new() { Timeout = 10_000 });
                dialogs.Clear();
                await withdraw.ClickAsync();

                int? reqStatus = null, doc = null;
                for (var i = 0; i < 30; i++)
                {
                    reqStatus = await IntAsync($"""SELECT "Status"::text FROM "ApprovalRequests" WHERE "Id"='{reqId}'""");
                    doc = await DocStatusAsync(prId);
                    if (reqStatus == 3 && doc == 0) break;
                    await page.WaitForTimeoutAsync(300);
                }
                Assert.True(dialogs.Count > 0, "撤回前應先跳確認框");
                Assert.Equal(3, reqStatus);   // ApprovalStatus.Withdrawn
                Assert.Equal(0, doc);         // DocumentApprovalStatus.Draft

                // 撤回後不在待我簽核；重開面板沒有撤回鈕、改回送審鈕
                await page.GotoAsync($"{_fixture.BaseUrl}/Approval/PendingIndex");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                Assert.Equal(0, await page.Locator($"[data-url*='/Approval/Process/{reqId}']").CountAsync());

                await page.GotoAsync($"{_fixture.BaseUrl}/PurchaseRequest?searched=true&searchText={prId}");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.Locator($"[data-action='openPanel'][data-url*='{prId}']").First.ClickAsync();
                await Assertions.Expect(page.Locator($"#sidePanelBody [data-action='submitApproval'][data-id='{prId}']"))
                    .ToBeVisibleAsync(new() { Timeout = 10_000 });
                Assert.Equal(0, await page.Locator("#sidePanelBody [data-action='withdrawApproval']").CountAsync());
            }
            finally
            {
                await RestoreApprovalConfigAsync(prevConfig);
            }
        }

        // ── 手機「待我簽核」：卡片 → 查看並簽核 → 核准／駁回，意見一起送出 ───────────────

        [Theory]
        [InlineData("approveApproval", 1, 2)]
        [InlineData("rejectApproval", 2, 3)]
        public async Task MobileList_Decide_DrivesRequestAndDocumentStatus(string button, int requestStatus, int docStatus)
        {
            string? prevConfig = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "Id" FROM "DocumentApprovalConfigs" WHERE "DocumentType"='PurchaseRequest' AND "PlantId"='{Plant}'""");
            await ArrangeApprovalConfigAsync(prevConfig);
            try
            {
                var desk = await _fixture.NewPageAsync();
                desk.Dialog += async (_, d) => { try { await d.AcceptAsync(); } catch (PlaywrightException) { } };
                var prId = await CreatePurchaseRequestAsync(desk);
                await SubmitAsync(desk, prId);
                var reqId = await SeedFixture.ScalarAsync<string>(
                    $"""SELECT "Id" FROM "ApprovalRequests" WHERE "DocumentType"='PurchaseRequest' AND "DocumentId"='{prId}' ORDER BY "CreatedAt" DESC LIMIT 1""");
                Assert.False(string.IsNullOrEmpty(reqId), $"請購單 {prId} 送審後沒有簽核單");

                var (ctx, page) = await MobileUi.SignInAsync(_fixture);
                try
                {
                    await page.GotoAsync($"{_fixture.BaseUrl}/mobile/approval/list.html");
                    var card = page.Locator($"[data-action='reviewApproval'][data-request-id='{reqId}']");
                    await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = 15_000 });
                    await card.ClickAsync();
                    await Assertions.Expect(page.Locator("#reviewActions")).ToBeVisibleAsync(new() { Timeout = 10_000 });
                    await page.FillAsync("#reviewComment", $"E2E mobile {button}");

                    var (text, success) = await MobileUi.ToastAfterAsync(page,
                        () => page.Locator($"#reviewActions [data-action='{button}']").ClickAsync());
                    Assert.True(success, $"手機簽核送出失敗：{text}");

                    Assert.Equal(requestStatus, await IntAsync($"""SELECT "Status"::text FROM "ApprovalRequests" WHERE "Id"='{reqId}'"""));
                    Assert.Equal(docStatus, await DocStatusAsync(prId));
                    Assert.Equal($"E2E mobile {button}", await SeedFixture.ScalarAsync<string>(
                        $"""SELECT "Comment" FROM "ApprovalLogs" WHERE "RequestId"='{reqId}' AND "Action"={requestStatus}"""));   // 同上：依動作類型取
                    // 送出後清單重載，這張單不該還在
                    await Assertions.Expect(card).ToHaveCountAsync(0, new() { Timeout = 10_000 });
                }
                finally { await ctx.CloseAsync(); }
            }
            finally
            {
                await RestoreApprovalConfigAsync(prevConfig);
            }
        }

        // ── 準備與還原 ───────────────────────────────────────────────────────

        private static async Task ArrangeApprovalConfigAsync(string? prevConfig)
        {
            await SeedFixture.ExecAsync($"""
                INSERT INTO "ApprovalTemplates" ("Id","Name","TargetDocumentType","SkipSameApprover","PlantId","CreatedByRoleId")
                VALUES ('{TemplateId}','E2E 請購簽核','PurchaseRequest',false,'{Plant}','1')
                ON CONFLICT ("Id") DO UPDATE SET "SkipSameApprover"=false, "PlantId"='{Plant}';
                INSERT INTO "ApprovalSteps" ("Id","TemplateId","Sequence","ApproverType","TargetRoleId")
                VALUES ('{StepId}','{TemplateId}',1,2,'1')
                ON CONFLICT ("Id") DO UPDATE SET "TemplateId"='{TemplateId}', "Sequence"=1, "ApproverType"=2, "TargetRoleId"='1';
                """);
            if (prevConfig != null)
                await SeedFixture.ExecAsync($"""
                    CREATE TABLE IF NOT EXISTS "E2E_ApprovalCfgBackup" AS SELECT * FROM "DocumentApprovalConfigs" WHERE false;
                    DELETE FROM "E2E_ApprovalCfgBackup" WHERE "Id"='{prevConfig}';
                    INSERT INTO "E2E_ApprovalCfgBackup" SELECT * FROM "DocumentApprovalConfigs" WHERE "Id"='{prevConfig}';
                    UPDATE "DocumentApprovalConfigs" SET "IsApprovalRequired"=true, "ApprovalTemplateId"='{TemplateId}' WHERE "Id"='{prevConfig}';
                    """);
            else
                await SeedFixture.ExecAsync($"""
                    INSERT INTO "DocumentApprovalConfigs" ("Id","DocumentType","DocumentTypeName","IsApprovalRequired","ApprovalTemplateId","PlantId")
                    VALUES ('{ConfigId}','PurchaseRequest','請購單',true,'{TemplateId}','{Plant}');
                    """);
        }

        private static async Task RestoreApprovalConfigAsync(string? prevConfig)
        {
            if (prevConfig != null)
                await SeedFixture.ExecAsync($"""
                    UPDATE "DocumentApprovalConfigs" c SET "IsApprovalRequired"=b."IsApprovalRequired", "ApprovalTemplateId"=b."ApprovalTemplateId"
                    FROM "E2E_ApprovalCfgBackup" b WHERE b."Id"=c."Id" AND c."Id"='{prevConfig}';
                    """);
            else
                await SeedFixture.ExecAsync($"""DELETE FROM "DocumentApprovalConfigs" WHERE "Id"='{ConfigId}';""");
        }

        private static Task<int?> DocStatusAsync(string prId) => IntAsync(
            $"""SELECT "ApprovalStatus"::text FROM "PurchaseRequests" WHERE "PurchaseRequestId"='{prId}'""");

        /// <summary>SeedFixture.ScalarAsync 走 Convert.ChangeType，不收可空型別；一律以文字取回再轉。</summary>
        private static async Task<int?> IntAsync(string sql) =>
            int.TryParse(await SeedFixture.ScalarAsync<string>(sql), out var v) ? v : null;

        /// <summary>用畫面新增一張請購單（同 PurchaseRequestActionSmokeTests 的欄位填法），回傳單號。</summary>
        private async Task<string> CreatePurchaseRequestAsync(IPage page)
        {
            var before = await SeedFixture.ScalarAsync<string>(
                """SELECT to_char(coalesce(max("CreatedAt"), '2000-01-01'), 'YYYY-MM-DD HH24:MI:SS.US') FROM "PurchaseRequests" """);

            await page.GotoAsync($"{_fixture.BaseUrl}/PurchaseRequest?searched=true");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.ClickAsync("button[data-url$='/PurchaseRequest/Create']");
            await page.WaitForSelectorAsync("#sidePanelBody #prForm", new() { Timeout = 10_000 });

            await page.ClickAsync("#sidePanelBody [data-action='addLine']");
            await page.WaitForTimeoutAsync(400);
            var row = page.Locator("#sidePanelBody #linesTbody tr").Last;
            await row.Locator("select[name$='.ProductCode']").SelectOptionAsync("E2E-RM01");
            await page.WaitForTimeoutAsync(600);
            await row.Locator("input[name$='.RequestQty']").First.FillAsync("1");
            var uom = row.Locator("select[name$='.UomCode']");
            if (await uom.CountAsync() > 0 && string.IsNullOrEmpty(await uom.First.InputValueAsync()))
                await uom.First.SelectOptionAsync("E2E-EA");

            // 等存檔回應回來才讀單號／狀態：第一顆是「存檔並送審」，伺服端分三次 commit
            // （草稿 → 簽核單 → 送審中），只等「列出現」會在草稿那一刻就讀到狀態 0，
            // SubmitAsync 以為要手動送審，打開面板時卻已是審核中、沒有送審鈕（2026-11-07／11-09 nightly）。
            await page.RunAndWaitForResponseAsync(
                () => page.ClickAsync("#sidePanelBody button[type='submit']"),
                r => r.Url.Contains("/PurchaseRequest/Save", StringComparison.OrdinalIgnoreCase)
                     && r.Request.Method == "POST",
                new PageRunAndWaitForResponseOptions { Timeout = 15_000 });
            string? id = null;
            for (var i = 0; i < 30 && id == null; i++)
            {
                await page.WaitForTimeoutAsync(300);
                id = await SeedFixture.ScalarAsync<string>(
                    $"""SELECT "PurchaseRequestId" FROM "PurchaseRequests" WHERE "CreatedAt" > '{before}' ORDER BY "CreatedAt" DESC LIMIT 1""");
            }
            Assert.False(string.IsNullOrEmpty(id), "請購單存檔沒有成立");
            return id!;
        }

        /// <summary>
        /// 送審。有簽核設定時請購單「存檔即送審」（ApprovalService.ResolveSaveStatusAsync），
        /// 那就已經是送審中、面板上不會有送審鈕；否則從列表重開，按「送審」（會跳確認框）。
        /// </summary>
        private async Task SubmitAsync(IPage page, string prId)
        {
            if (await DocStatusAsync(prId) == 1) return;
            await page.GotoAsync($"{_fixture.BaseUrl}/PurchaseRequest?searched=true&searchText={prId}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.Locator($"[data-action='openPanel'][data-url*='{prId}']").First.ClickAsync();
            var submit = page.Locator($"#sidePanelBody [data-action='submitApproval'][data-id='{prId}']");
            await Assertions.Expect(submit).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await submit.ClickAsync();
            for (var i = 0; i < 30 && await DocStatusAsync(prId) != 1; i++)
                await page.WaitForTimeoutAsync(300);
        }
    }
}
