using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Acceptance
{
    /// <summary>
    /// 手機上架／下架的另一種單別（借入 IB 上架、借出 BR 下架），以及盤點的複盤。
    ///
    /// <para><b>為什麼要補</b>：上架只有進貨與委外收料走過畫面、下架只有報廢單（MobileQualityPickE2ETests）。
    /// 各單別共用 PutAwayCoreAsync／PickCoreAsync，但「找單、對行、寫哪張明細表、全數完成後轉什麼狀態」
    /// 是逐單別各寫一份的；借入／借出用同一張表、方向相反，一次驗兩個方向。</para>
    ///
    /// <para><b>複盤不必第二個帳號</b>：規則是「複盤者不得與初盤者同一人」，把初盤者寫成另一個使用者 ID
    /// 就是「別人初盤的」；同時放一筆本人初盤的，驗它不出現在複盤清單。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class MobileLoanRecountE2ETests : AcceptanceE2EBase
    {
        private const string Loc = "E2E-LRAW";
        private const string Product = "E2E-RM02";

        public MobileLoanRecountE2ETests(UiTestFixture fixture) : base(fixture) { }

        private static async Task<decimal> StockAtAsync() => decimal.Parse(
            await SeedFixture.ScalarAsync<string>(
                $"""SELECT coalesce(sum("QtyOnHand"),0)::text FROM "Inventories" WHERE "ProductCode"='{Product}' AND "LocationCode"='{Loc}'""") ?? "0",
            System.Globalization.CultureInfo.InvariantCulture);

        private static Task SeedLoanAsync(string id, string type, decimal qty) => SeedFixture.ExecAsync($"""
            {CleanLoanSql(id)}
            INSERT INTO "LoanDocs" ("LoanDocId","LoanDate","TypeCode","TypeCategory","EntityType","EntityCode","Status","ApprovalStatus","CreatedBy","CreatedAt","UpdatedAt","PlantId","CreatedByRoleId")
            VALUES ('{id}',current_date,'{type}','Loan','Supplier','{Supplier}',1,2,'admin',now(),now(),'{Plant}','1');
            INSERT INTO "LoanDocLines" ("LoanDocId","LineNo","ProductCode","Qty","UomCode","StockQty","StockUomCode","UnitPrice","QtyPutAway","QtySourceCleared")
            VALUES ('{id}',1,'{Product}',{qty},'{Uom}',{qty},'{Uom}',0,0,0);
            """);

        private static string CleanLoanSql(string id) => $"""
            DELETE FROM "LoanDocLocationDetails" WHERE "LoanDocId"='{id}';
            DELETE FROM "LoanDocLines" WHERE "LoanDocId"='{id}';
            DELETE FROM "LoanDocs" WHERE "LoanDocId"='{id}';
            DELETE FROM "InventoryCostLayerConsumptions" WHERE "DocNo"='{id}';
            DELETE FROM "InventoryCostLayers" WHERE "SourceDocNo"='{id}';
            DELETE FROM "StockMovements" WHERE "DocNo"='{id}';
            """;

        // ── 借入（IB）上架：庫存增加、記上架明細、單據離開「已確認」 ─────────────────────

        [Fact]
        public async Task 借入單_手機上架_庫存增加並記明細_單據完成()
        {
            const string doc = "E2E-LNIB1";
            await SeedLoanAsync(doc, "IB", 3m);
            var before = await StockAtAsync();
            var (ctx, page) = await MobileAsync();
            try
            {
                var (text, success) = await MobilePutAwayAsync(page, doc, Loc);
                Assert.True(success, $"借入單上架失敗：{text}");

                Assert.Equal("3", (await SeedFixture.ScalarAsync<string>($"""SELECT "QtyPutAway"::numeric(18,0)::text FROM "LoanDocLines" WHERE "LoanDocId"='{doc}'""")));
                Assert.Equal(before + 3m, await StockAtAsync());
                Assert.Equal("1", await SeedFixture.ScalarAsync<string>($"""SELECT count(*)::text FROM "LoanDocLocationDetails" WHERE "LoanDocId"='{doc}' AND "LocationCode"='{Loc}'"""));
                Assert.NotEqual("1", await SeedFixture.ScalarAsync<string>($"""SELECT "Status"::text FROM "LoanDocs" WHERE "LoanDocId"='{doc}'"""));
            }
            finally
            {
                await ctx.CloseAsync();
                await SeedFixture.ExecAsync(CleanLoanSql(doc));
                await SeedFixture.ResetAsync();
            }
        }

        // ── 借出（BR）下架：庫存扣減、記下架明細、單據離開「已確認」 ─────────────────────

        [Fact]
        public async Task 借出單_手機下架_庫存扣減並記明細_單據完成()
        {
            const string doc = "E2E-LNBR1";
            await SeedLoanAsync(doc, "BR", 2m);
            var before = await StockAtAsync();
            var (ctx, page) = await MobileAsync();
            try
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/pick.html");
                var item = page.Locator("#pendingList [data-action='selectItem']", new() { HasText = doc });
                await Assertions.Expect(item).ToHaveCountAsync(1, new() { Timeout = 15_000 });
                await item.ClickAsync();
                await page.Locator($"#stockRows [data-action='selectLocation'][data-loc-code='{Loc}']").ClickAsync();
                var (text, success) = await ToastAfterAsync(page, () => page.ClickAsync("#btnPick"));
                Assert.True(success, $"借出單下架失敗：{text}");

                Assert.Equal(before - 2m, await StockAtAsync());
                Assert.Equal("1", await SeedFixture.ScalarAsync<string>($"""SELECT count(*)::text FROM "LoanDocLocationDetails" WHERE "LoanDocId"='{doc}' AND "LocationCode"='{Loc}'"""));
                Assert.NotEqual("1", await SeedFixture.ScalarAsync<string>($"""SELECT "Status"::text FROM "LoanDocs" WHERE "LoanDocId"='{doc}'"""));
            }
            finally
            {
                await ctx.CloseAsync();
                await SeedFixture.ExecAsync(CleanLoanSql(doc));
                await SeedFixture.ResetAsync();
            }
        }

        // ── 複盤：只列別人初盤的項目；送出後記下複盤量與複盤者 ────────────────────────

        [Fact]
        public async Task 複盤_只列別人初盤的項目_送出後記複盤量與複盤者()
        {
            const string sheet = "E2E-CNT2";
            const string other = "e2e-other-counter";
            var admin = await SeedFixture.ScalarAsync<string>("""SELECT "Id" FROM "AspNetUsers" WHERE "UserName"='admin'""");
            async Task CleanAsync() => await SeedFixture.ExecAsync($"""
                DELETE FROM "InventoryCountItems" WHERE "SheetId"='{sheet}';
                DELETE FROM "InventoryCountSheets" WHERE "SheetId"='{sheet}';
                """);
            await CleanAsync();
            await SeedFixture.ExecAsync($"""
                INSERT INTO "InventoryCountSheets" ("SheetId","PlantId","CountMode","SheetStatus","SnapshotAt","CreatedBy","CreatedAt","UpdatedAt","CreatedByRoleId")
                VALUES ('{sheet}','{Plant}','WithSheet','Counting',now(),'admin',now(),now(),'1');
                INSERT INTO "InventoryCountItems" ("SheetId","SeqNo","WhCode","LocationCode","LotNo","ProductCode","BookQty","FirstCountQty","FirstCountedBy","FirstCountedAt","ItemStatus")
                SELECT '{sheet}', s.seq, l."WhCode", l."LocationCode", '', s.product, 100, 95, s.counter, now(), 'Counted'
                FROM "StorageLocations" l,
                     (VALUES (1, 'E2E-RM03', '{other}'), (2, 'E2E-RM01', '{admin}')) s(seq, product, counter)
                WHERE l."LocationCode"='{Loc}';
                """);
            var (ctx, page) = await MobileAsync();
            try
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/stockcount.html");
                await page.ClickAsync("[data-action='switchMode'][data-mode='recount']");
                var rows = page.Locator("#recountRows .product-row");
                await Assertions.Expect(rows.Filter(new() { HasText = "E2E-RM03" })).ToHaveCountAsync(1, new() { Timeout = 15_000 });
                // 本人初盤的不可由本人複盤：不出現在清單（其他清冊的項目可能也在，只看這兩個品號）
                Assert.Equal(0, await rows.Filter(new() { HasText = "E2E-RM01" }).Filter(new() { HasText = Loc }).CountAsync());

                var idx = await rows.EvaluateAllAsync<int>(
                    "els => els.findIndex(e => e.textContent.includes('E2E-RM03'))");
                await page.FillAsync($"#recount-qty-{idx}", "96");
                var (text, success) = await ToastAfterAsync(page, () => page.ClickAsync("#btnSubmitRecount"));
                Assert.True(success, $"複盤送出失敗：{text}");

                Assert.Equal("96", await SeedFixture.ScalarAsync<string>($"""SELECT "RecountQty"::numeric(18,0)::text FROM "InventoryCountItems" WHERE "SheetId"='{sheet}' AND "ProductCode"='E2E-RM03'"""));
                Assert.Equal("Recounted", await SeedFixture.ScalarAsync<string>($"""SELECT "ItemStatus" FROM "InventoryCountItems" WHERE "SheetId"='{sheet}' AND "ProductCode"='E2E-RM03'"""));
                Assert.Equal(admin, await SeedFixture.ScalarAsync<string>($"""SELECT "RecountedBy" FROM "InventoryCountItems" WHERE "SheetId"='{sheet}' AND "ProductCode"='E2E-RM03'"""));
                Assert.Equal("Counted", await SeedFixture.ScalarAsync<string>($"""SELECT "ItemStatus" FROM "InventoryCountItems" WHERE "SheetId"='{sheet}' AND "ProductCode"='E2E-RM01'"""));
            }
            finally
            {
                await ctx.CloseAsync();
                await CleanAsync();
            }
        }
    }
}
