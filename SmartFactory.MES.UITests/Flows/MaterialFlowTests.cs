using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Flows
{
    /// <summary>
    /// 工單用料的端到端流程（桌機畫面＋真資料庫），每一支都看「料最後落在哪個儲位」：
    /// <list type="number">
    /// <item>備料後發料：已備料的料要先從備料區扣，從原儲位發料在存檔當下就被擋（2026-10-13）。</item>
    /// <item>領料申請 → 送審 → 核准 → 揀料 → 過帳。</item>
    /// <item>發料後退料 → 倉庫收料 → 過帳：庫存回補、已退量累計。</item>
    /// <item>投產自動產生 Push 發料單 → 指定儲位 → 過帳。</item>
    /// <item>委外發料／委外退料過帳。</item>
    /// </list>
    ///
    /// <para>種子（scripts/seed-e2e-fixture.sql）：E2E-WO0001 用料 E2E-RM01 ×20、E2E-RM02 ×10（Push），
    /// 庫存在 E2E-WRAW/E2E-LRAW（RM01 1000、RM02 500），備料倉 E2E-WSTG/E2E-LSTG；委外採購單 E2E-OPO01。
    /// 每支開頭重跑種子，可重複執行。</para>
    /// </summary>
    /// <para>2026-10-13 這組測試揪出：投產重算（毛需求 − 可用量）在成品庫存已足時把工單量算成 0，按確認後照樣投產，
    /// Push 發料單每一行都是 0、工單永遠卡住。已改成「重算為 0 不得投產」（見最後一支）。④ 以 GrossDemandQty=0 不做重算。</para>
    public class MaterialFlowTests : MaterialSlipFlowTestBase
    {
        private const string Wo = "E2E-WO0001";
        private const string Dispatch = "E2E-DP0001";
        private const string RawWh = "E2E-WRAW", RawLoc = "E2E-LRAW";
        private const string StgWh = "E2E-WSTG", StgLoc = "E2E-LSTG";
        private const string Rm1 = "E2E-RM01", Rm2 = "E2E-RM02";

        public MaterialFlowTests(UiTestFixture fixture) : base(fixture) { }

        // ── ① 備料後發料 ───────────────────────────────────────────────────

        [Fact]
        public async Task 備料後發料_從原儲位發被擋_改從備料區發_備料區扣帳原儲位不動_鎖量釋放()
        {
            await ResetFixtureAsync();
            await ApproveWorkOrderAsync();
            var page = await NewFlowPageAsync();
            await StageViaUiAsync(page);
            Assert.Equal(20m, await OnHandAsync(Rm1, StgLoc));
            var rawAfterStaging = await OnHandAsync(Rm1, RawLoc);

            var errors = await SaveNewSlipAsync(page, "InternalIssue", Wo, (Rm1, 20m, RawWh, RawLoc));
            Assert.True(errors.Any(e => e.Contains(StgLoc)), "已備料卻從原儲位發料，存檔當下就要擋下並指出備料儲位：" + string.Join(" / ", errors));

            await SetLastLineLocationAsync(page, StgWh, StgLoc);
            Assert.Empty(await SubmitPanelAsync(page));
            var slip = await LatestSlipAsync(0);

            await RunSlipAsync(page, slip, "InternalIssue", "Approve", "MarkPicking", "Confirm", "Issue");
            Assert.Equal(5, await SlipStatusAsync(slip));
            Assert.Equal(0m, await OnHandAsync(Rm1, StgLoc));
            Assert.Equal(rawAfterStaging, await OnHandAsync(Rm1, RawLoc));
            Assert.Equal(20m, await IssuedAsync(Rm1));
            Assert.Equal(0m, await SeedFixture.ScalarAsync<decimal>(
                $"""SELECT COALESCE(SUM("ReservedQty"),0) FROM "InventoryReservations" WHERE "ProductCode"='{Rm1}' AND "Status"=0 AND ("SourceDocId"='{Wo}' OR "SourceDocId" IN (SELECT "StagingId" FROM "MaterialStagingOrders" WHERE "SourceId" LIKE 'E2E-%'))"""));   // 備料與工單的鎖量都已釋放
            Assert.Empty(JsErrors);
        }

        // ── ② 領料申請 ─────────────────────────────────────────────────────

        [Fact]
        public async Task 領料申請_送審核准揀料過帳_原料扣帳_工單已發量累計()
        {
            await ResetFixtureAsync();
            await ApproveWorkOrderAsync();
            // 領料（MR）只限領料（Pull）用料
            await SeedFixture.ExecAsync($"""UPDATE "WorkOrderMaterials" SET "IssueMethod"=1 WHERE "WorkOrderId"='{Wo}' AND "ProductCode"='{Rm1}'""");
            var page = await NewFlowPageAsync();
            var before = await OnHandAsync(Rm1, RawLoc);

            Assert.Empty(await SaveNewSlipAsync(page, "InternalRequisition", Wo, (Rm1, 5m, RawWh, RawLoc)));
            var slip = await LatestSlipAsync(1);

            await RunSlipAsync(page, slip, "InternalRequisition", "Submit", "Approve", "MarkPicking", "Confirm", "Issue");
            Assert.Equal(5, await SlipStatusAsync(slip));
            Assert.Equal(before - 5m, await OnHandAsync(Rm1, RawLoc));
            Assert.Equal(5m, await IssuedAsync(Rm1));
            Assert.Empty(JsErrors);
        }

        // ── ③ 發料後退料 ───────────────────────────────────────────────────

        [Fact]
        public async Task 發料後退料_倉庫收料過帳_庫存回補_工單已退量累計()
        {
            await ResetFixtureAsync();
            await ApproveWorkOrderAsync();
            var page = await NewFlowPageAsync();
            var before = await OnHandAsync(Rm1, RawLoc);

            Assert.Empty(await SaveNewSlipAsync(page, "InternalIssue", Wo, (Rm1, 10m, RawWh, RawLoc)));
            var issue = await LatestSlipAsync(0);
            await RunSlipAsync(page, issue, "InternalIssue", "Approve", "MarkPicking", "Confirm", "Issue");
            Assert.Equal(before - 10m, await OnHandAsync(Rm1, RawLoc));

            Assert.Empty(await SaveNewSlipAsync(page, "InternalReturn", Wo, (Rm1, 4m, RawWh, RawLoc)));
            var ret = await LatestSlipAsync(2);
            await RunSlipAsync(page, ret, "InternalReturn", "Approve", "MarkPicking", "Confirm", "Issue");

            Assert.Equal(5, await SlipStatusAsync(ret));
            Assert.Equal(before - 6m, await OnHandAsync(Rm1, RawLoc));
            Assert.Equal(4m, await SeedFixture.ScalarAsync<decimal>(
                $"""SELECT "ReturnedQty" FROM "WorkOrderMaterials" WHERE "WorkOrderId"='{Wo}' AND "ProductCode"='{Rm1}'"""));
            Assert.Empty(JsErrors);
        }

        // ── ④ 投產自動產生 Push 發料單 ────────────────────────────────────────

        [Fact]
        public async Task 投產自動產生Push發料單_指定儲位後過帳_兩個料都扣帳()
        {
            await ResetFixtureAsync();
            // GrossDemandQty=0：不做投產重算——種子的成品已有庫存，重算會把工單量算成 0（見本檔結尾註記）
            await SeedFixture.ExecAsync($"""UPDATE "WorkOrders" SET "Status"=0, "ApprovalStatus"=0, "GrossDemandQty"=0 WHERE "WorkOrderId"='{Wo}'""");
            await SeedFixture.ExecAsync($"""DELETE FROM "MaterialSlipLines" WHERE "SlipId" IN (SELECT "SlipId" FROM "MaterialSlips" WHERE "SourceDocId"='{Wo}'); DELETE FROM "MaterialSlips" WHERE "SourceDocId"='{Wo}';""");
            var page = await NewFlowPageAsync();
            var (r1, r2) = (await OnHandAsync(Rm1, RawLoc), await OnHandAsync(Rm2, RawLoc));

            await GotoAsync(page, $"/WorkOrder?searched=true&searchText={Wo}&pageSize=50");
            await OpenRowPanelAsync(page, Wo, $"/WorkOrder/Details/{Wo}", "[data-action='releaseWorkOrder']");
            await page.ClickAsync("[data-action='releaseWorkOrder']");
            await WaitForAsync(async () => await SeedFixture.ScalarAsync<long>(
                $"""SELECT count(*) FROM "MaterialSlips" WHERE "SourceDocId"='{Wo}' AND "Category"=0""") == 1, "投產後沒有自動產生 Push 發料單");
            var slip = await LatestSlipAsync(0);

            // 自動產生的單沒有來源儲位：倉庫在草稿上指定後才進流程
            await OpenSlipPanelAsync(page, slip, "InternalIssue");
            await SetAllLinesLocationAsync(page, RawWh, RawLoc);
            Assert.Empty(await SubmitPanelAsync(page));
            await RunSlipAsync(page, slip, "InternalIssue", "Approve", "MarkPicking", "Confirm", "Issue");

            Assert.Equal(5, await SlipStatusAsync(slip));
            Assert.Equal((r1 - 20m, r2 - 10m), (await OnHandAsync(Rm1, RawLoc), await OnHandAsync(Rm2, RawLoc)));
            Assert.Equal((20m, 10m), (await IssuedAsync(Rm1), await IssuedAsync(Rm2)));
            Assert.Empty(JsErrors);
        }

        // ── ⑤ 委外發料／退料 ──────────────────────────────────────────────

        [Fact]
        public async Task 委外發料與委外退料_過帳後庫存扣回()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();
            var before = await OnHandAsync(Rm2, RawLoc);

            Assert.Empty(await SaveNewSlipAsync(page, "OutsourceIssue", "E2E-OPO01", (Rm2, 6m, RawWh, RawLoc)));
            var oi = await LatestSlipAsync(3);
            await RunSlipAsync(page, oi, "OutsourceIssue", "Approve", "MarkPicking", "Confirm", "Issue");
            Assert.Equal(5, await SlipStatusAsync(oi));
            Assert.Equal(before - 6m, await OnHandAsync(Rm2, RawLoc));

            Assert.Empty(await SaveNewSlipAsync(page, "OutsourceReturn", "E2E-OPO01", (Rm2, 2m, RawWh, RawLoc)));
            var or = await LatestSlipAsync(4);
            await RunSlipAsync(page, or, "OutsourceReturn", "Approve", "MarkPicking", "Confirm", "Issue");
            Assert.Equal(5, await SlipStatusAsync(or));
            Assert.Equal(before - 4m, await OnHandAsync(Rm2, RawLoc));
            Assert.Empty(JsErrors);
        }

        // ── 重算為 0 不得投產 ───────────────────────────────────────────────

        [Fact]
        public async Task 成品庫存已足_投產被擋並說明不需生產_工單維持未投產_不產生發料單()
        {
            await ResetFixtureAsync();
            await SeedFixture.ExecAsync($"""UPDATE "WorkOrders" SET "Status"=0, "ApprovalStatus"=0, "GrossDemandQty"=10 WHERE "WorkOrderId"='{Wo}'""");
            await SeedFixture.ExecAsync($"""DELETE FROM "MaterialSlipLines" WHERE "SlipId" IN (SELECT "SlipId" FROM "MaterialSlips" WHERE "SourceDocId"='{Wo}'); DELETE FROM "MaterialSlips" WHERE "SourceDocId"='{Wo}';""");
            // 成品 E2E-FG01 在庫 200（≥ 毛需求 10）
            await SeedFixture.ExecAsync("""
                DELETE FROM "Inventories" WHERE "ProductCode"='E2E-FG01' AND "LocationCode"='E2E-LFG';
                INSERT INTO "Inventories" ("PlantId","ProductCode","LocationCode","LotNo","QtyOnHand","QtyReserved","UnitCost","LastUpdatedAt")
                VALUES ('P1','E2E-FG01','E2E-LFG','',200,0,10,now());
                """);
            var page = await NewFlowPageAsync();

            await GotoAsync(page, $"/WorkOrder?searched=true&searchText={Wo}&pageSize=50");
            await OpenRowPanelAsync(page, Wo, $"/WorkOrder/Details/{Wo}", "[data-action='releaseWorkOrder']");
            await page.ClickAsync("[data-action='releaseWorkOrder']");
            await WaitForAsync(() => Task.FromResult(DialogMessages.Any(m => m.Contains("不需生產") || m.Contains("not need to be produced"))),
                "投產沒有說明「不需生產」：" + DescribeDialogs());

            Assert.Equal(0, await SeedFixture.ScalarAsync<int>($"""SELECT "Status" FROM "WorkOrders" WHERE "WorkOrderId"='{Wo}'"""));
            Assert.Equal(10m, await SeedFixture.ScalarAsync<decimal>($"""SELECT "Quantity" FROM "WorkOrders" WHERE "WorkOrderId"='{Wo}'"""));
            Assert.Equal(0L, await SeedFixture.ScalarAsync<long>($"""SELECT count(*) FROM "MaterialSlips" WHERE "SourceDocId"='{Wo}'"""));
            Assert.Empty(JsErrors);
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        private static Task ApproveWorkOrderAsync() => SeedFixture.ExecAsync(
            $"""UPDATE "WorkOrders" SET "ApprovalStatus"=2 WHERE "WorkOrderId"='{Wo}'""");

        private static Task<decimal> OnHandAsync(string product, string loc) => SeedFixture.ScalarAsync<decimal>(
            $"""SELECT COALESCE(SUM("QtyOnHand"),0) FROM "Inventories" WHERE "ProductCode"='{product}' AND "LocationCode"='{loc}'""");

        private static Task<decimal> IssuedAsync(string product) => SeedFixture.ScalarAsync<decimal>(
            $"""SELECT "IssuedQty" FROM "WorkOrderMaterials" WHERE "WorkOrderId"='{Wo}' AND "ProductCode"='{product}'""");

        /// <summary>派工 → 建立備料單 → 填來源儲位 → 確認鎖料 → 揀料完成（同 StagingToCompletionFlowTests ①～④）。</summary>
        private async Task StageViaUiAsync(IPage page)
        {
            await GotoAsync(page, $"/Dispatch?searched=true&searchText={Dispatch}&pageSize=50");
            await OpenRowPanelAsync(page, Dispatch, "SetupForm", "#stagingForm");
            await page.Locator("#stagingForm button[type='submit']").First.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);

            await GotoAsync(page, $"/MaterialStaging?searched=true&searchTerm={Dispatch}&pageSize=50");
            await OpenRowPanelAsync(page, Dispatch, "/MaterialStaging/Edit", "#linesBody tr");
            var n = await page.Locator("#linesBody tr").CountAsync();
            for (var i = 0; i < n; i++)
            {
                var row = page.Locator("#linesBody tr").Nth(i);
                await row.Locator(".line-from-wh").SelectOptionAsync(RawWh);
                await page.WaitForTimeoutAsync(200);
                await row.Locator(".line-from-loc").SelectOptionAsync(RawLoc);
            }
            await page.Locator("#stagingForm button[type='submit']").First.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);

            await GotoAsync(page, $"/MaterialStaging?searched=true&searchTerm={Dispatch}&pageSize=50");
            await OpenRowPanelAsync(page, Dispatch, "/MaterialStaging/Edit", "#linesBody tr");
            if (await page.Locator("[data-url*='/MaterialStaging/Confirm']").CountAsync() == 0)
            {
                await ClickPanelActionAsync(page, "/MaterialStaging/Submit");
                await GotoAsync(page, $"/MaterialStaging?searched=true&searchTerm={Dispatch}&pageSize=50");
                await OpenRowPanelAsync(page, Dispatch, "/MaterialStaging/Edit", "#linesBody tr");
            }
            await ClickPanelActionAsync(page, "/MaterialStaging/Confirm");

            await GotoAsync(page, $"/MaterialStaging?searched=true&searchTerm={Dispatch}&pageSize=50");
            await OpenRowPanelAsync(page, Dispatch, "/MaterialStaging/Edit", "#linesBody tr");
            await ClickPanelActionAsync(page, "/MaterialStaging/Stage");
            await WaitForAsync(async () => await OnHandAsync(Rm1, StgLoc) > 0, "揀料完成後備料儲位沒有庫存");
        }
    }
}
