using Microsoft.Playwright;
using SmartFactory.MES.UITests.Acceptance;
using Xunit;

namespace SmartFactory.MES.UITests.Mobile
{
    /// <summary>
    /// 手機庫存查詢與盤點走真後端：依品號／依儲位查庫存、依清冊初盤、自由盤點（掃儲位 → 加入清單 → 送出）。
    ///
    /// <para><b>為什麼要補</b>：2026-10-20 盤點時這兩頁只有離線快取測試（MobilePwaOfflineTests），
    /// 線上按下去之後沒有任何 Playwright 走過。這兩頁讀的 API 回應欄位名稱一改（或頁面讀錯大小寫），
    /// 畫面就靜默停在原地，不會有任何錯誤。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class MobileStockCountE2ETests
    {
        private const string Loc = "E2E-LRAW";

        private readonly UiTestFixture _fixture;
        public MobileStockCountE2ETests(UiTestFixture fixture) => _fixture = fixture;

        private static async Task<decimal?> DecAsync(string sql) =>
            decimal.TryParse(await SeedFixture.ScalarAsync<string>(sql), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

        // ── 庫存查詢：依品號看各儲位、依儲位看各品號 ─────────────────────────────────

        [Fact]
        public async Task Stock_ByProduct_And_ByLocation_ShowSeededStock()
        {
            var (ctx, page) = await MobileUi.SignInAsync(_fixture);
            try
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/stock.html");
                await page.FillAsync("#productInput", "E2E-RM01");
                await page.ClickAsync("[data-action='queryStock']");
                await Assertions.Expect(page.Locator("#resultSection")).ToBeVisibleAsync(new() { Timeout = 10_000 });
                await Assertions.Expect(page.Locator("#prodCode")).ToHaveTextAsync("E2E-RM01");
                await Assertions.Expect(page.Locator("#locationRows")).ToContainTextAsync(Loc);
                await Assertions.Expect(page.Locator("#totalQty")).ToContainTextAsync("1");

                await page.ClickAsync("label[for='modeLocation']");   // btn-check 單選鈕本身隱藏，點標籤
                await page.FillAsync("#productInput", Loc);
                await page.ClickAsync("[data-action='queryStock']");
                await Assertions.Expect(page.Locator("#prodCode")).ToHaveTextAsync(Loc, new() { Timeout = 10_000 });
                await Assertions.Expect(page.Locator("#locationRows")).ToContainTextAsync("E2E-RM02");
                Assert.Equal(0, await page.Locator("#toast-container .toast.text-bg-danger").CountAsync());
            }
            finally { await ctx.CloseAsync(); }
        }

        // ── 依清冊初盤：選清冊 → 改實盤數 → 送出；項目變「已初盤」並記下數量 ─────────────

        [Fact]
        public async Task CountSheet_FirstCount_RecordsQtyAndStatus()
        {
            const string sheet = "E2E-CNT1";
            async Task CleanAsync() => await SeedFixture.ExecAsync($"""
                DELETE FROM "InventoryCountItems" WHERE "SheetId"='{sheet}';
                DELETE FROM "InventoryCountSheets" WHERE "SheetId"='{sheet}';
                """);
            await CleanAsync();
            await SeedFixture.ExecAsync($"""
                INSERT INTO "InventoryCountSheets" ("SheetId","PlantId","CountMode","SheetStatus","SnapshotAt","CreatedBy","CreatedAt","UpdatedAt","CreatedByRoleId")
                VALUES ('{sheet}','P1','WithSheet','Counting',now(),'admin',now(),now(),'1');
                INSERT INTO "InventoryCountItems" ("SheetId","SeqNo","WhCode","LocationCode","LotNo","ProductCode","BookQty","ItemStatus")
                SELECT '{sheet}', 1, l."WhCode", l."LocationCode", '', 'E2E-RM03', 100, 'Pending'
                FROM "StorageLocations" l WHERE l."LocationCode"='{Loc}' LIMIT 1;
                """);
            var (ctx, page) = await MobileUi.SignInAsync(_fixture);
            try
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/stockcount.html");
                var select = page.Locator("#sheetSelect");
                await Assertions.Expect(select.Locator($"option[value='{sheet}']")).ToHaveCountAsync(1, new() { Timeout = 15_000 });
                await select.SelectOptionAsync(sheet);

                var qty = page.Locator("#sheet-qty-0");
                await Assertions.Expect(qty).ToHaveValueAsync("100", new() { Timeout = 10_000 });
                await qty.FillAsync("98");
                var (text, success) = await MobileUi.ToastAfterAsync(page, () => page.Locator("#btnSubmitSheet").ClickAsync());
                Assert.True(success, $"初盤送出失敗：{text}");

                Assert.Equal(98m, await DecAsync($"""SELECT "FirstCountQty"::text FROM "InventoryCountItems" WHERE "SheetId"='{sheet}'"""));
                Assert.Equal("Counted", await SeedFixture.ScalarAsync<string>($"""SELECT "ItemStatus" FROM "InventoryCountItems" WHERE "SheetId"='{sheet}'"""));
                // 全部初盤完，清冊不再出現在待盤清單
                await Assertions.Expect(select.Locator($"option[value='{sheet}']")).ToHaveCountAsync(0, new() { Timeout = 10_000 });
            }
            finally
            {
                await ctx.CloseAsync();
                await CleanAsync();
            }
        }

        // ── 自由盤點：輸入儲位 → 帶出該儲位庫存 → 加入清單 → 送出；建立無清冊盤點項目 ───────

        [Fact]
        public async Task FreeCount_LoadLocation_AddToCart_Submit_CreatesCountedItems()
        {
            var start = await SeedFixture.ScalarAsync<string>("SELECT to_char(now() - interval '1 second', 'YYYY-MM-DD HH24:MI:SS.US TZH')");
            var preSheets = await SeedFixture.ScalarAsync<string>(
                """SELECT coalesce(string_agg('''' || "SheetId" || '''', ','), '''''') FROM "InventoryCountSheets" WHERE "CountMode"='WithoutSheet' AND "SheetStatus"='Counting'""");
            var (ctx, page) = await MobileUi.SignInAsync(_fixture);
            try
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/stockcount.html");
                await page.ClickAsync("[data-action='switchMode'][data-mode='free']");
                await page.FillAsync("#locInput", Loc);
                await page.ClickAsync("[data-action='loadLocation']");

                await Assertions.Expect(page.Locator("#stepCount")).ToBeVisibleAsync(new() { Timeout = 10_000 });
                await Assertions.Expect(page.Locator("#locTitle")).ToContainTextAsync(Loc);
                await Assertions.Expect(page.Locator("#productRows")).ToContainTextAsync("E2E-RM01");

                await page.ClickAsync("[data-action='addToCart']");
                await Assertions.Expect(page.Locator("#btnSubmitCart")).ToBeVisibleAsync();
                var (text, success) = await MobileUi.ToastAfterAsync(page, () => page.Locator("#btnSubmitCart").ClickAsync());
                Assert.True(success, $"自由盤點送出失敗：{text}");

                var counted = await SeedFixture.ScalarAsync<string>($"""
                    SELECT count(*)::text FROM "InventoryCountItems" i JOIN "InventoryCountSheets" s ON s."SheetId"=i."SheetId"
                    WHERE s."CountMode"='WithoutSheet' AND i."LocationCode"='{Loc}' AND i."ProductCode"='E2E-RM01'
                      AND i."ItemStatus"='Counted' AND i."FirstCountedAt" >= '{start}'
                    """);
                Assert.Equal("1", counted);
            }
            finally
            {
                await ctx.CloseAsync();
                await SeedFixture.ExecAsync($"""
                    DELETE FROM "InventoryCountItems" i USING "InventoryCountSheets" s
                    WHERE s."SheetId"=i."SheetId" AND s."CountMode"='WithoutSheet' AND i."ProductCode" LIKE 'E2E-%' AND i."FirstCountedAt" >= '{start}';
                    DELETE FROM "InventoryCountSheets" s WHERE s."CountMode"='WithoutSheet' AND s."SheetId" NOT IN ({preSheets})
                      AND NOT EXISTS (SELECT 1 FROM "InventoryCountItems" i WHERE i."SheetId"=s."SheetId");
                    """);
            }
        }
    }
}
