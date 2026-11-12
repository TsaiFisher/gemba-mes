using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.MFG
{
    /// <summary>
    /// 2026-10-20 生產主線修正的畫面層：委外採購單「作廢」鈕、已確認發料單的取消提醒、
    /// 送審中工單在桌機開工被擋。服務層的規則由單元／整合測試驗；這裡驗的是按鈕有沒有出現在對的列、
    /// 確認框的文字、以及被擋時使用者看得到原因（不是靜默沒反應）。
    ///
    /// <para>每支測試自建專用資料（E2E-OPOV*／E2E-MSC*）並在 finally 刪除；
    /// 唯一動到既有種子的是第三支（暫時把 E2E-WO0021 改成送審中），同樣在 finally 還原。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class WorkOrderGateUiTests
    {
        private readonly UiTestFixture _fixture;
        public WorkOrderGateUiTests(UiTestFixture fixture) => _fixture = fixture;

        private static async Task<int?> IntAsync(string sql) =>
            int.TryParse(await SeedFixture.ScalarAsync<string>(sql), out var v) ? v : null;

        private static List<string> CollectDialogs(IPage page)
        {
            var messages = new List<string>();
            page.Dialog += async (_, d) =>
            {
                lock (messages) messages.Add(d.Message);
                try { await d.AcceptAsync(); } catch (PlaywrightException) { }
            };
            return messages;
        }

        // ── 委外採購單：已確認列有「作廢」、草稿列沒有；按下去真的作廢 ─────────────

        [Fact]
        public async Task OutsourcePo_ConfirmedRowHasVoid_DraftRowDoesNot_VoidWorks()
        {
            const string confirmed = "E2E-OPOV1", draft = "E2E-OPOV2";
            await SeedFixture.ExecAsync($"""
                DELETE FROM "OutsourcePurchaseOrderLines" WHERE "OutsourcePurchaseOrderId" IN ('{confirmed}','{draft}');
                DELETE FROM "OutsourcePurchaseOrders" WHERE "OutsourcePurchaseOrderId" IN ('{confirmed}','{draft}');
                INSERT INTO "OutsourcePurchaseOrders" ("OutsourcePurchaseOrderId","SupplierCode","OrderDate","Status","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
                VALUES ('{confirmed}','E2E-CO01',current_date,1,'P1','1',now(),now()),
                       ('{draft}','E2E-CO01',current_date,0,'P1','1',now(),now());
                INSERT INTO "OutsourcePurchaseOrderLines" ("OutsourcePurchaseOrderId","LineNo","SourceWorkOrderId","OperationSeq","ProductCode","Qty")
                SELECT x.id, 1, w."WorkOrderId", 10, w."ProductCode", 1
                FROM (VALUES ('{confirmed}'),('{draft}')) x(id), "WorkOrders" w WHERE w."WorkOrderId"='E2E-WO0011';
                """);
            try
            {
                var page = await _fixture.NewPageAsync();
                var dialogs = CollectDialogs(page);
                await page.GotoAsync($"{_fixture.BaseUrl}/OutsourcePurchaseOrder?searched=true&searchText=E2E-OPOV");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

                var voidConfirmed = page.Locator($"[data-action='confirmPostAction'][data-url*='/Void/{confirmed}']");
                await Assertions.Expect(voidConfirmed).ToBeVisibleAsync(new() { Timeout = 10_000 });
                Assert.Equal(0, await page.Locator($"[data-url*='/Void/{draft}']").CountAsync());

                await voidConfirmed.ClickAsync();
                int? status = null;
                for (var i = 0; i < 30 && status != 2; i++)
                {
                    await page.WaitForTimeoutAsync(300);
                    status = await IntAsync($"""SELECT "Status"::text FROM "OutsourcePurchaseOrders" WHERE "OutsourcePurchaseOrderId"='{confirmed}'""");
                }
                Assert.Equal(2, status);   // OutsourcePoStatus.Void
                Assert.Contains(dialogs, m => m.Contains("作廢"));   // 先問再做
                Assert.DoesNotContain(dialogs, m => m.Contains("無法") || m.Contains("失敗"));
            }
            finally
            {
                await SeedFixture.ExecAsync($"""
                    DELETE FROM "OutsourcePurchaseOrderLines" WHERE "OutsourcePurchaseOrderId" IN ('{confirmed}','{draft}');
                    DELETE FROM "OutsourcePurchaseOrders" WHERE "OutsourcePurchaseOrderId" IN ('{confirmed}','{draft}');
                    """);
            }
        }

        // ── 已確認揀料的發料單：面板有「取消」，確認框提醒放回原儲位，取消後狀態變更 ──

        [Fact]
        public async Task MaterialSlip_Confirmed_CancelPromptsPutBack_AndCancels()
        {
            const string slip = "E2E-MSC1";
            await SeedFixture.ExecAsync($"""
                DELETE FROM "MaterialSlipLines" WHERE "SlipId"='{slip}';
                DELETE FROM "MaterialSlips" WHERE "SlipId"='{slip}';
                INSERT INTO "MaterialSlips" ("SlipId","SlipNo","Category","Status","SlipDate","SourceDocType","SourceDocId","PlantId","RequestedBy","CreatedAt","UpdatedAt")
                VALUES ('{slip}','{slip}',0,4,current_date,'WorkOrder','E2E-WO0001','P1','admin',now(),now());
                INSERT INTO "MaterialSlipLines" ("SlipId","LineNo","ProductCode","RequestedQty","ConfirmedQty","UomCode","ReturnedQty")
                VALUES ('{slip}',1,'E2E-RM01',1,1,'E2E-EA',0);
                """);
            try
            {
                var page = await _fixture.NewPageAsync();
                var dialogs = CollectDialogs(page);
                await page.GotoAsync($"{_fixture.BaseUrl}/MaterialSlip?searched=true&searchText={slip}");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.Locator($"[data-action='openPanel'][data-url*='/MaterialSlip/Form/{slip}']").First.ClickAsync();

                var cancelForm = page.Locator($"#sidePanelBody form[action*='/Cancel/{slip}']");
                await Assertions.Expect(cancelForm).ToBeVisibleAsync(new() { Timeout = 10_000 });
                Assert.Contains("放回原儲位", await cancelForm.GetAttributeAsync("data-confirm") ?? "");

                await cancelForm.Locator("button[type='submit']").ClickAsync();
                int? status = null;
                for (var i = 0; i < 30 && status != 7; i++)
                {
                    await page.WaitForTimeoutAsync(300);
                    status = await IntAsync($"""SELECT "Status"::text FROM "MaterialSlips" WHERE "SlipId"='{slip}'""");
                }
                Assert.Equal(7, status);   // MaterialSlipStatus.Cancelled
                Assert.Contains(dialogs, m => m.Contains("放回原儲位"));
            }
            finally
            {
                await SeedFixture.ExecAsync($"""
                    DELETE FROM "MaterialSlipLines" WHERE "SlipId"='{slip}';
                    DELETE FROM "MaterialSlips" WHERE "SlipId"='{slip}';
                    """);
            }
        }

        // ── 送審中的工單：桌機開工被擋，使用者看得到原因，沒有建立上工紀錄 ────────────

        [Fact]
        public async Task Dispatch_StartSession_WorkOrderPendingApproval_BlockedWithReason()
        {
            const string wo = "E2E-WO0021", dispatch = "E2E-DP0024";
            var before = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "ApprovalStatus"::text FROM "WorkOrders" WHERE "WorkOrderId"='{wo}'""");
            await SeedFixture.ExecAsync($"""UPDATE "WorkOrders" SET "ApprovalStatus"=1 WHERE "WorkOrderId"='{wo}'""");
            try
            {
                var sessionsBefore = await IntAsync($"""SELECT count(*)::text FROM "ProductionSessions" WHERE "DispatchId"='{dispatch}'""");
                var page = await _fixture.NewPageAsync();
                var dialogs = CollectDialogs(page);
                await page.GotoAsync($"{_fixture.BaseUrl}/Dispatch?searched=true&searchText={dispatch}&pageSize=50");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

                var row = page.Locator("table tbody tr", new() { HasText = dispatch }).First;
                await row.Locator("[data-url*='SessionForm']").First.ClickAsync();
                await page.WaitForSelectorAsync("#panelForm", new() { Timeout = 10_000 });
                await page.Locator("#panelForm button[type='submit']").First.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.WaitForTimeoutAsync(1500);

                var shown = string.Join(" / ", dialogs) + " " + await page.Locator("body").InnerTextAsync();
                Assert.Contains("尚未核准", shown);
                Assert.Equal(sessionsBefore, await IntAsync($"""SELECT count(*)::text FROM "ProductionSessions" WHERE "DispatchId"='{dispatch}'"""));
            }
            finally
            {
                await SeedFixture.ExecAsync($"""UPDATE "WorkOrders" SET "ApprovalStatus"={before ?? "2"} WHERE "WorkOrderId"='{wo}'""");
            }
        }
    }
}
