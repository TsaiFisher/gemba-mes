using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Acceptance
{
    /// <summary>
    /// 場次 W 的手機料件作業端到端版：W-6（N-3 備料作業）、W-7（N-5 領料／退料）、W-13（倉儲選單待辦數）。
    /// 用 E2E 種子的工單 E2E-WO0001／派工 E2E-DP0001（用料 E2E-RM01 ×20、E2E-RM02 ×10，原料倉 E2E-LRAW）。
    /// 手機頁走真伺服器、手機尺寸；桌機只做手機做不了的步驟（從派工建備料單、核准領料單）。
    /// </summary>
    public class MobileMaterialAcceptanceE2ETests : FlowTestBase
    {
        public MobileMaterialAcceptanceE2ETests(UiTestFixture fixture) : base(fixture) { }

        private const string Wo = "E2E-WO0001";
        private const string Dispatch = "E2E-DP0001";
        private const string RawLoc = "E2E-LRAW", StgLoc = "E2E-LSTG";
        private const string Rm1 = "E2E-RM01", Rm2 = "E2E-RM02";

        // ── W-6／N-3 ─────────────────────────────────────────────────────────

        [Fact]
        public async Task W6_備料單不帶已發的料_手機確認鎖庫存_逐行選來源儲位移入備料倉()
        {
            await ResetFixtureAsync();
            // 工單已核准；E2E-RM02 已經全數發過（10/9 的缺陷：已發的料仍被帶進備料單、重複鎖庫存）
            await SeedFixture.ExecAsync($"""
                UPDATE "WorkOrders" SET "ApprovalStatus"=2 WHERE "WorkOrderId"='{Wo}';
                UPDATE "WorkOrderMaterials" SET "IssuedQty"=10 WHERE "WorkOrderId"='{Wo}' AND "ProductCode"='{Rm2}';
                """);
            var rawBefore = await OnHandAsync(Rm1, RawLoc);

            // 桌機：從派工建備料單，送審（無簽核設定時直接核准，手機清單才看得到）
            var desk = await NewFlowPageAsync();
            await GotoAsync(desk, $"/Dispatch?searched=true&searchText={Dispatch}&pageSize=50");
            await OpenRowPanelAsync(desk, Dispatch, "SetupForm", "#stagingForm");
            await desk.Locator("#stagingForm button[type='submit']").First.ClickAsync();
            await desk.WaitForLoadStateAsync(LoadState.NetworkIdle);
            var stagingId = await WaitScalarAsync(
                $"""SELECT "StagingId" FROM "MaterialStagingOrders" WHERE "SourceId"='{Dispatch}' ORDER BY "CreatedAt" DESC LIMIT 1""",
                "從派工建備料單沒有成功");
            Assert.Equal(0L, await SeedFixture.ScalarAsync<long>(
                $"""SELECT count(*) FROM "MaterialStagingLines" WHERE "StagingId"='{stagingId}' AND "ProductCode"='{Rm2}'"""));
            Assert.Equal(20m, await SeedFixture.ScalarAsync<decimal>(
                $"""SELECT COALESCE(SUM("RequiredQty"),0) FROM "MaterialStagingLines" WHERE "StagingId"='{stagingId}' AND "ProductCode"='{Rm1}'"""));
            if (await SeedFixture.ScalarAsync<int>($"""SELECT "ApprovalStatus" FROM "MaterialStagingOrders" WHERE "StagingId"='{stagingId}'""") != 2)
            {
                await GotoAsync(desk, $"/MaterialStaging?searched=true&searchTerm={Dispatch}&pageSize=50");
                await OpenRowPanelAsync(desk, Dispatch, "/MaterialStaging/Edit", "#linesBody tr");
                await ClickPanelActionAsync(desk, "/MaterialStaging/Submit");
            }
            Assert.Equal(2, await SeedFixture.ScalarAsync<int>($"""SELECT "ApprovalStatus" FROM "MaterialStagingOrders" WHERE "StagingId"='{stagingId}'"""));

            var (ctx, page) = await MobileUi.SignInAsync(_fixture);
            await using (ctx)
            {
                // 手機：選備料單 → 確認鎖庫存
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/staging.html");
                await page.Locator($"#orderList [data-action='openOrder'][data-id='{stagingId}']").ClickAsync();
                await page.WaitForSelectorAsync("#detailCard:not(.u-display-none) #btnConfirm");
                var (confirmText, confirmOk) = await MobileUi.ToastAfterAsync(page, () => page.ClickAsync("#btnConfirm"));
                Assert.True(confirmOk, "確認鎖庫存失敗：" + confirmText);
                Assert.Equal("Confirmed", await SeedFixture.ScalarAsync<string>($"""SELECT "Status" FROM "MaterialStagingOrders" WHERE "StagingId"='{stagingId}'"""));
                Assert.Equal(20m, await SeedFixture.ScalarAsync<decimal>(
                    $"""SELECT COALESCE(SUM("ReservedQty"),0) FROM "InventoryReservations" WHERE "SourceDocId"='{stagingId}' AND "Status"=0"""));

                // 逐行選來源儲位 → 移入備料倉
                await page.WaitForSelectorAsync("#btnStage:not(.u-display-none)");
                await page.Locator($"#lineRows [data-action='pickStockChip'][data-loc='{RawLoc}']").First.ClickAsync();
                var (stageText, stageOk) = await MobileUi.ToastAfterAsync(page, () => page.ClickAsync("#btnStage"));
                Assert.True(stageOk, "移入備料倉失敗：" + stageText);
            }

            Assert.Equal(20m, await OnHandAsync(Rm1, StgLoc));
            Assert.Equal(rawBefore - 20m, await OnHandAsync(Rm1, RawLoc));
            Assert.Equal(0m, await OnHandAsync(Rm2, StgLoc));
        }

        // ── W-7／N-5 ─────────────────────────────────────────────────────────

        [Fact]
        public async Task W7_領料Pull料_手機申請待核准_桌機核准_手機揀料過帳_退料超量擋下說明可退量_合理量建草稿()
        {
            await ResetFixtureAsync();
            await SeedFixture.ExecAsync($"""
                UPDATE "WorkOrders" SET "ApprovalStatus"=2 WHERE "WorkOrderId"='{Wo}';
                UPDATE "WorkOrderMaterials" SET "IssueMethod"=1 WHERE "WorkOrderId"='{Wo}' AND "ProductCode"='{Rm2}';
                DELETE FROM "MaterialSlipLines" WHERE "SlipId" IN (SELECT "SlipId" FROM "MaterialSlips" WHERE "SourceDocId"='{Wo}');
                DELETE FROM "MaterialSlips" WHERE "SourceDocId"='{Wo}';
                """);
            var rawBefore = await OnHandAsync(Rm2, RawLoc);

            var (ctx, page) = await MobileUi.SignInAsync(_fixture);
            await using (ctx)
            {
                // ① 手機領料申請：Pull 料 E2E-RM02 領 5 → 領料單待核准
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/mfg/requisition.html?dispatchId={Dispatch}");
                var reqInput = page.Locator($"#matRows input[data-product='{Rm2}']");
                await reqInput.WaitForAsync();
                Assert.Equal(0, await page.Locator($"#matRows input[data-product='{Rm1}']").CountAsync());   // Push 料不可申請
                await MobileUi.FillChangeAsync(reqInput, "5");
                var (reqText, reqOk) = await MobileUi.ToastAfterAsync(page, () => page.ClickAsync("#btnReq"));
                Assert.True(reqOk, "領料申請失敗：" + reqText);
                var mr = await WaitScalarAsync(
                    $"""SELECT "SlipId" FROM "MaterialSlips" WHERE "SourceDocId"='{Wo}' AND "Category"=1 ORDER BY "CreatedAt" DESC LIMIT 1""",
                    "手機領料申請沒有建出領料單");
                Assert.Equal(1, await SlipStatusAsync(mr!));   // 待核准

                // ② 桌機核准
                var desk = await NewFlowPageAsync();
                await GotoAsync(desk, "/MaterialSlip?searched=true&categoryFilter=InternalRequisition&pageSize=50");
                await desk.Locator($"[data-url$='/MaterialSlip/Form/{mr}']").First.ClickAsync();
                var approve = desk.Locator($"#sidePanelBody form[action*='/MaterialSlip/Approve/'] button[type='submit']");
                await approve.WaitForAsync();
                await approve.ClickAsync();
                await WaitUntilAsync(async () => await SlipStatusAsync(mr!) == 2, $"{mr} 核准沒有成功");

                // ③ 手機工單發退料：揀料並確認過帳
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/issue.html");
                await page.Locator($"#slipList [data-action='openSlip'][data-id='{mr}']").ClickAsync();
                await page.WaitForSelectorAsync("#detailCard:not(.u-display-none) #btnPost");
                var (postText, postOk) = await MobileUi.ToastAfterAsync(page, () => page.ClickAsync("#btnPost"));
                Assert.True(postOk, "揀料過帳失敗：" + postText);
                Assert.Equal(5, await SlipStatusAsync(mr!));
                Assert.Equal(5m, await SeedFixture.ScalarAsync<decimal>(
                    $"""SELECT "IssuedQty" FROM "WorkOrderMaterials" WHERE "WorkOrderId"='{Wo}' AND "ProductCode"='{Rm2}'"""));
                Assert.Equal(rawBefore - 5m, await OnHandAsync(Rm2, RawLoc));

                // ④ 手機退料：先填超過已發的 6 → 擋下並寫出可退量；改 5 → 建退料單（草稿，等倉庫收料）
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/mfg/requisition.html?dispatchId={Dispatch}");
                await page.Locator("label[for='kindRet']").ClickAsync();
                var retInput = page.Locator($"#matRows input[data-product='{Rm2}']");
                await retInput.WaitForAsync();
                Assert.Contains("5", await page.Locator("#matRows .mat-line").First.InnerTextAsync());   // 可退 5
                await MobileUi.FillChangeAsync(retInput, "6");
                var (overText, overOk) = await MobileUi.ToastAfterAsync(page, () => page.ClickAsync("#btnReq"));
                Assert.False(overOk, "退料超過已發未退仍送出：" + overText);
                Assert.Contains(Rm2, overText);
                Assert.Equal(0L, await SeedFixture.ScalarAsync<long>(
                    $"""SELECT count(*) FROM "MaterialSlips" WHERE "SourceDocId"='{Wo}' AND "Category"=2"""));

                await MobileUi.FillChangeAsync(retInput, "5");
                var (retText, retOk) = await MobileUi.ToastAfterAsync(page, () => page.ClickAsync("#btnReq"));
                Assert.True(retOk, "退料申請失敗：" + retText);
                var mx = await WaitScalarAsync(
                    $"""SELECT "SlipId" FROM "MaterialSlips" WHERE "SourceDocId"='{Wo}' AND "Category"=2 ORDER BY "CreatedAt" DESC LIMIT 1""",
                    "手機退料沒有建出退料單");
                Assert.Equal(0, await SlipStatusAsync(mx!));   // 草稿，等倉庫收料
            }
        }

        // ── W-13 ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task W13_倉儲選單三張料件卡的待辦數等於點進去的筆數()
        {
            // 每張卡至少一筆，比對才有意義（全是 0 時「相等」什麼都沒證明）
            await SeedFixture.ExecAsync("""
                DELETE FROM "MaterialSlips" WHERE "SlipId" LIKE 'E2E-W13-%';
                INSERT INTO "MaterialSlips" ("SlipId","SlipNo","Category","Status","SlipDate","SourceDocType","SourceDocId","PlantId","Remarks","CreatedAt","UpdatedAt")
                VALUES ('E2E-W13-MI','E2E-W13-MI',0,0,current_date,'WorkOrder','E2E-W13','P1','W13',now(),now()),
                       ('E2E-W13-MX','E2E-W13-MX',2,0,current_date,'WorkOrder','E2E-W13','P1','W13',now(),now());
                DELETE FROM "MaterialStagingOrders" WHERE "StagingId"='E2E-W13-IM';
                INSERT INTO "MaterialStagingOrders" ("StagingId","PlantId","SourceType","SourceId","StagingWhCode","StagingLocationCode","Status","ApprovalStatus","CreatedBy","CreatedAt","UpdatedAt")
                VALUES ('E2E-W13-IM','P1','Dispatch','E2E-W13','E2E-WSTG','E2E-LSTG','Draft',2,'admin',now(),now());
                """);
            try
            {
            var (ctx, page) = await MobileUi.SignInAsync(_fixture);
            await using (ctx)
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/list.html");
                await page.WaitForSelectorAsync("#slipBadge:not(.d-none)");
                await page.WaitForSelectorAsync("#stagingBadge:not(.d-none)");
                await page.WaitForSelectorAsync("#osrBadge:not(.d-none)");
                var nums = Regex.Matches(await page.InnerTextAsync("#slipBadge"), @"\d+").Select(m => int.Parse(m.Value)).ToArray();
                Assert.Equal(2, nums.Length);
                var staging = int.Parse(await page.InnerTextAsync("#stagingBadge"));
                var osr = int.Parse(await page.InnerTextAsync("#osrBadge"));
                Assert.True(nums[0] >= 1 && nums[1] >= 1 && staging >= 1, $"種子沒有反映在數字上：發 {nums[0]}・退 {nums[1]}、備料 {staging}");

                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/issue.html");
                Assert.Equal(nums[0], await CountAfterLoadAsync(page, "#slipList", "#slipList .slip-item"));
                await page.Locator("label[for='kindReturn']").ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                Assert.Equal(nums[1], await CountAfterLoadAsync(page, "#slipList", "#slipList .slip-item"));

                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/staging.html");
                Assert.Equal(staging, await CountAfterLoadAsync(page, "#orderList", "#orderList .slip-item"));

                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/outsource-receive.html");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                var opos = await CountAfterLoadAsync(page, "#opoList", "#opoList .slip-item");
                var approved = await page.Locator("#approvedList [data-action='confirmApproved']").CountAsync();
                Assert.Equal(osr, opos + approved);
            }
            }
            finally
            {
                await SeedFixture.ExecAsync("""
                    DELETE FROM "MaterialSlips" WHERE "SlipId" LIKE 'E2E-W13-%';
                    DELETE FROM "MaterialStagingOrders" WHERE "StagingId"='E2E-W13-IM';
                    """);
            }
        }

        [Fact]
        public async Task W13_沒有備料作業權限_那張卡不顯示數字_也不跳無權限()
        {
            await SeedFixture.ExecAsync("""
                INSERT INTO "AspNetRoles" ("Id","Name","NormalizedName","NameZh","NameEn","ConcurrencyStamp","OrganizationUnitId")
                VALUES ('E2E-INVNS','E2E 倉儲無備料','E2E 倉儲無備料','E2E 倉儲無備料','E2E Warehouse no staging','e2e-invns','ORG-ROOT')
                ON CONFLICT ("Id") DO UPDATE SET "OrganizationUnitId"='ORG-ROOT';
                INSERT INTO "RolePlantAssignments" ("RoleId","PlantId","IsDefault") VALUES ('E2E-INVNS','P1',true)
                ON CONFLICT ("RoleId","PlantId") DO NOTHING;
                DELETE FROM "MenuPermissions" WHERE "RoleId"='E2E-INVNS';
                INSERT INTO "MenuPermissions" ("Id","RoleId","MenuKey","CanRead","CanCreate","CanUpdate","CanDelete","CanPrint")
                VALUES ('e2e-invns-wh','E2E-INVNS','WarehouseOps',true,true,true,false,false);
                DELETE FROM "MobileRolePermissions" WHERE "RoleId"='E2E-INVNS';
                INSERT INTO "MobileRolePermissions" ("Id","RoleId","ModuleActionId","IsAllowed")
                SELECT 'e2e-invns-' || a."Id", 'E2E-INVNS', a."Id", true
                FROM "MobileModuleActions" a JOIN "MobileModules" m ON m."Id"=a."ModuleId"
                WHERE m."ModuleKey"='Inv' AND a."ActionKey" <> 'Staging';
                INSERT INTO "AspNetUsers"
                  ("Id","UserName","NormalizedUserName","Email","NormalizedEmail","EmailConfirmed","PasswordHash",
                   "SecurityStamp","ConcurrencyStamp","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled",
                   "AccessFailedCount","MustChangePassword","ActivePlantId","ActiveRoleId","Language","TimeZoneId")
                VALUES ('e2e00000-0000-4000-8000-0000000000a2','e2e.invns','E2E.INVNS',
                        'e2e.invns@example.invalid','E2E.INVNS@EXAMPLE.INVALID',true,
                        'AQAAAAIAAYagAAAAEAABAgMEBQYHCAkKCwwNDg9L5Dqic0V6FQ+q9JwwjmE50sylkRwobPDlqdJX0VNCyA==',
                        'E2E-INVNS-STAMP','e2e-invns',false,false,true,0,false,'P1','E2E-INVNS','zh-TW','Asia/Taipei')
                ON CONFLICT ("Id") DO UPDATE
                  SET "PasswordHash"=EXCLUDED."PasswordHash", "LockoutEnd"=NULL, "AccessFailedCount"=0,
                      "TwoFactorEnabled"=false, "MustChangePassword"=false, "ActivePlantId"='P1', "ActiveRoleId"='E2E-INVNS';
                INSERT INTO "AspNetUserRoles" ("UserId","RoleId") VALUES ('e2e00000-0000-4000-8000-0000000000a2','E2E-INVNS')
                ON CONFLICT ("UserId","RoleId") DO NOTHING;
                """);

            var (ctx, page) = await MobileUi.SignInAsync(_fixture, "e2e.invns", "E2eReadOnly!2026");
            await using (ctx)
            {
                var counts = page.WaitForResponseAsync(r => r.Url.Contains("/api/mobile/material/counts"));
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/list.html");
                Assert.Equal(200, (await counts).Status);
                await page.WaitForSelectorAsync("#slipBadge:not(.d-none)");      // 有權限的卡照常顯示
                await page.WaitForSelectorAsync("#osrBadge:not(.d-none)");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                Assert.Contains("d-none", await page.Locator("#stagingBadge").GetAttributeAsync("class") ?? "");
                Assert.Equal(0, await page.Locator("#toast-container .toast").CountAsync());   // 沒有「無權限」
            }
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        private static Task<decimal> OnHandAsync(string product, string loc) => SeedFixture.ScalarAsync<decimal>(
            $"""SELECT COALESCE(SUM("QtyOnHand"),0) FROM "Inventories" WHERE "ProductCode"='{product}' AND "LocationCode"='{loc}'""");

        private static Task<int> SlipStatusAsync(string slip) =>
            SeedFixture.ScalarAsync<int>($"""SELECT "Status" FROM "MaterialSlips" WHERE "SlipId"='{slip}'""");

        private static async Task<string?> WaitScalarAsync(string sql, string fail)
        {
            for (var i = 0; i < 50; i++)
            {
                var v = await SeedFixture.ScalarAsync<string>(sql);
                if (v != null) return v;
                await Task.Delay(200);
            }
            Assert.Fail(fail);
            return null;
        }

        private static async Task WaitUntilAsync(Func<Task<bool>> cond, string fail)
        {
            for (var i = 0; i < 50; i++)
            {
                if (await cond()) return;
                await Task.Delay(200);
            }
            Assert.Fail(fail);
        }

        /// <summary>等清單載入完（不再是轉圈），回傳項目數；清單空的時候是一行「沒有資料」不算。</summary>
        private static async Task<int> CountAfterLoadAsync(IPage page, string container, string itemSelector)
        {
            await page.WaitForFunctionAsync(
                "sel => { const b = document.querySelector(sel); return b && b.children.length > 0 && !b.querySelector('.spinner-border'); }",
                container, new PageWaitForFunctionOptions { Timeout = 15_000 });
            return await page.Locator(itemSelector).CountAsync();
        }
    }
}
