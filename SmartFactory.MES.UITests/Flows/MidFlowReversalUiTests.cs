using Microsoft.Playwright;
using Npgsql;
using Xunit;

namespace SmartFactory.MES.UITests.Flows
{
    /// <summary>
    /// 「中途反悔」在畫面層的那一半：下游已經有進度之後，回頭刪上游訂單。
    ///
    /// 與 IntegrationTests/Postings/MidFlowReversalTests 的分工很窄但很實在。
    /// 那支直接打 HTTP 端點，證明的是**後端會擋**；本檔證明的是使用者**看得到被擋**——
    /// 兩者不是同一件事，中間隔著一整條前端接線：
    ///   列表按鈕是否渲染 → <c>data-action="deleteItem"</c> 的 handler 是否掛在 window 上
    ///   → confirm 之後是否真的發出請求 → 後端訊息是否被吐回畫面。
    /// 這條線壞掉的症狀是「按了沒反應」：dispatcher 找不到 handler 只會 console.warn
    /// 然後靜默結束（wwwroot/js/data-action.js），沒有錯誤、沒有紀錄，
    /// 而後端測試全綠——因為請求根本沒送出去。
    ///
    /// 為什麼上游鏈用 SQL 種而不是用畫面一步步建出來：本檔要驗的是**刪除那一下**，
    /// 前面建採購單、匯入進貨、上架過帳那十幾步與待驗行為無關，
    /// 走完只會讓失敗訊息指向不相干的那一段（P2P 主幹流程另有專屬測試涵蓋）。
    /// 種子只設定「下游已有進度」這個唯一前提：採購單的累計收貨量 &gt; 0、
    /// 銷貨訂單的累計出貨量 &gt; 0——那正是兩道守門判斷的依據。
    ///
    /// 資料一律 <c>E2EREV-</c> 前綴，與 seed-e2e-fixture.sql 的 <c>E2E-</c> 號段
    /// 及 DocNumberService 的流水號都不相撞；每支測試自己種、自己清。
    /// </summary>
    public class MidFlowReversalUiTests : FlowTestBase
    {
        private const string Prefix   = "E2EREV";
        private const string Product  = "E2E-PP01";   // 沿用 E2E 種子的料號與往來對象
        private const string Company  = "E2E-CO01";
        private const string Uom      = "E2E-EA";
        private const string Plant    = "P1";
        private const string RoleId   = "1";          // admin 的 ActiveRoleId；不設資料授權過濾會把單藏起來

        private const decimal OrderQty = 20m;
        private const decimal DoneQty  = 10m;        // 已收／已出量，> 0 即觸發守門
        private const decimal Price    = 50m;

        public MidFlowReversalUiTests(UiTestFixture fixture) : base(fixture) { }

        // ════════════════════════════════════════════════════════════════════
        // ① 採購單：已有進貨記錄 → 刪除
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task ReceivedPurchaseOrder_DeleteFromList_ShowsBlockedMessageAndKeepsRow()
        {
            var poId = $"{Prefix}-PO01";
            await SeedPurchaseOrderAsync(poId, receivedQty: DoneQty);

            var page = await NewFlowPageAsync();
            await GotoAsync(page, $"/PurchaseOrder?searched=true&searchTerm={poId}&sortOrder=id_desc");
            await AssertVisibleInListAsync(page, poId, "步驟①：採購單列表");

            await ClickDeleteAsync(page, poId, "步驟②：按下採購單的刪除鈕");

            AssertBlockedOnScreen(poId, "採購單");

            // 訊息看到了還不夠：真正要確認的是那張單沒被刪掉。
            await GotoAsync(page, $"/PurchaseOrder?searched=true&searchTerm={poId}&sortOrder=id_desc");
            await AssertVisibleInListAsync(page, poId, "步驟③：刪除被擋後採購單仍應在列表中");

            await CleanupPurchaseOrderAsync(poId);
            Assert.True(JsErrors.Count == 0, $"流程中有 JS 錯誤：\n  - {string.Join("\n  - ", JsErrors)}");
        }

        // ════════════════════════════════════════════════════════════════════
        // ② 銷貨訂單：已有出貨記錄 → 刪除
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task ShippedSalesOrder_DeleteFromList_ShowsBlockedMessageAndKeepsRow()
        {
            var soId = $"{Prefix}-SO01";
            await SeedSalesOrderAsync(soId, shippedQty: DoneQty);

            var page = await NewFlowPageAsync();
            await GotoAsync(page, $"/SalesOrder?searched=true&searchTerm={soId}&sortOrder=id_desc");
            await AssertVisibleInListAsync(page, soId, "步驟①：銷貨訂單列表");

            await ClickDeleteAsync(page, soId, "步驟②：按下銷貨訂單的刪除鈕");

            AssertBlockedOnScreen(soId, "銷貨訂單");

            await GotoAsync(page, $"/SalesOrder?searched=true&searchTerm={soId}&sortOrder=id_desc");
            await AssertVisibleInListAsync(page, soId, "步驟③：刪除被擋後銷貨訂單仍應在列表中");

            await CleanupSalesOrderAsync(soId);
            Assert.True(JsErrors.Count == 0, $"流程中有 JS 錯誤：\n  - {string.Join("\n  - ", JsErrors)}");
        }

        // ════════════════════════════════════════════════════════════════════
        // 共用操作與斷言
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 點該單號那一列的刪除鈕。
        ///
        /// 兩張列表的刪除都是 <c>data-action="deleteItem"</c> + <c>data-id</c>，
        /// 故以 data-id 精準鎖定，不靠列文字——列表上可能同時有別的測試留下的單。
        /// </summary>
        private static async Task ClickDeleteAsync(IPage page, string docId, string step)
        {
            var btn = page.Locator($"[data-action='deleteItem'][data-id='{docId}']").First;
            Assert.True(await btn.CountAsync() > 0,
                $"{step}：列表上找不到 {docId} 的刪除鈕（data-action='deleteItem'）。"
                + "按鈕沒渲染的話，使用者根本沒有反悔的入口，後端守門再嚴也驗不到。");

            await btn.ClickAsync();
            // confirm 由基底的 dialog handler 自動接受並記錄；後端回應的 alert 是第二則。
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);
        }

        /// <summary>
        /// 斷言「被擋」這件事有傳達到使用者眼前。
        ///
        /// 刻意不比對訊息字串：那是在地化資源，改一個字就紅，而測試要釘的不是文案。
        /// 釘的是**除了 confirm 之外還有第二則訊息**——只有 confirm 那一則，
        /// 代表按下確定之後什麼都沒發生（handler 沒掛上、或請求沒送出），
        /// 這正是 data-action dispatcher 找不到函式時的靜默失敗樣態。
        /// </summary>
        private void AssertBlockedOnScreen(string docId, string what)
        {
            Assert.True(DialogMessages.Count >= 2,
                $"{what} {docId}：按下刪除後只出現 {DialogMessages.Count} 則對話框訊息。"
                + "預期兩則——確認刪除的 confirm，以及後端擋下後回吐的訊息。"
                + "只有一則代表按了確定之後沒有任何事發生：多半是 data-action 的 handler "
                + "沒掛到 window 上，dispatcher 只 console.warn 就靜默結束，請求根本沒送出。"
                + DescribeDialogs());

            var backendMessage = DialogMessages[^1];
            Assert.False(string.IsNullOrWhiteSpace(backendMessage),
                $"{what} {docId}：後端回了一則空訊息，使用者看不出為什麼刪不掉。{DescribeDialogs()}");
        }

        // ════════════════════════════════════════════════════════════════════
        // 種子（直接寫 DB：本檔要驗的是刪除那一下，不是怎麼把鏈建出來）
        // ════════════════════════════════════════════════════════════════════

        private static async Task ExecAsync(string sql)
        {
            var conn = Environment.GetEnvironmentVariable("TestDbConnection")
                    ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
                    ?? throw new InvalidOperationException(
                        "需要資料庫連線才能種上游單據，但 TestDbConnection 與 "
                        + "ConnectionStrings__DefaultConnection 都沒有設定。"
                        + "請指向 app 正在使用的那個測試資料庫。");

            await using var db = new NpgsqlConnection(conn);
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, db);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>已核准的採購單一張，明細帶已收貨量（&gt; 0 即觸發刪除守門）。</summary>
        private static Task SeedPurchaseOrderAsync(string poId, decimal receivedQty) => ExecAsync($@"
DELETE FROM ""PurchaseOrderLines"" WHERE ""PurchaseOrderId"" = '{poId}';
DELETE FROM ""PurchaseOrders""     WHERE ""PurchaseOrderId"" = '{poId}';

INSERT INTO ""PurchaseOrders""
  (""PurchaseOrderId"",""OrderDate"",""CreatedBy"",""SupplierCode"",""TotalAmount"",
   ""ApprovalStatus"",""PlantId"",""CreatedByRoleId"",""CreatedAt"",""UpdatedAt"")
VALUES ('{poId}', CURRENT_DATE, 'admin', '{Company}', {OrderQty * Price},
        2, '{Plant}', '{RoleId}', now(), now());

INSERT INTO ""PurchaseOrderLines""
  (""PurchaseOrderId"",""LineNo"",""ProductCode"",""OrderQty"",""UomCode"",""StockQty"",""StockUomCode"",
   ""UnitPrice"",""ExchangeRate"",""TaxRate"",""LineAmount"",""TaxAmount"",""TotalAmount"",
   ""IsClosed"",""ReceivedQty"")
VALUES ('{poId}', 1, '{Product}', {OrderQty}, '{Uom}', {OrderQty}, '{Uom}',
        {Price}, 1, 0, {OrderQty * Price}, 0, {OrderQty * Price},
        false, {receivedQty});");

        /// <summary>已核准的銷貨訂單一張，明細帶已出貨量（&gt; 0 即觸發刪除守門）。</summary>
        private static Task SeedSalesOrderAsync(string soId, decimal shippedQty) => ExecAsync($@"
DELETE FROM ""SalesOrderLines"" WHERE ""SalesOrderId"" = '{soId}';
DELETE FROM ""SalesOrders""     WHERE ""SalesOrderId"" = '{soId}';

INSERT INTO ""SalesOrders""
  (""SalesOrderId"",""OrderDate"",""CompanyCode"",""TaxRate"",""TotalAmount"",""ApprovalStatus"",
   ""CreatedBy"",""PlantId"",""CreatedByRoleId"",""CreatedAt"",""UpdatedAt"")
VALUES ('{soId}', CURRENT_DATE, '{Company}', 0, {OrderQty * Price}, 2,
        'admin', '{Plant}', '{RoleId}', now(), now());

INSERT INTO ""SalesOrderLines""
  (""SalesOrderId"",""LineNo"",""ProductCode"",""OrderQty"",""UomCode"",""StockQty"",""StockUomCode"",
   ""UnitPrice"",""TaxRate"",""LineAmount"",""TaxAmount"",""TotalAmount"",
   ""ShippedQty"",""ReturnedQty"",""IsClosed"")
VALUES ('{soId}', 1, '{Product}', {OrderQty}, '{Uom}', {OrderQty}, '{Uom}',
        {Price}, 0, {OrderQty * Price}, 0, {OrderQty * Price},
        {shippedQty}, 0, false);");

        private static Task CleanupPurchaseOrderAsync(string poId) => ExecAsync($@"
DELETE FROM ""PurchaseOrderLines"" WHERE ""PurchaseOrderId"" = '{poId}';
DELETE FROM ""PurchaseOrders""     WHERE ""PurchaseOrderId"" = '{poId}';");

        private static Task CleanupSalesOrderAsync(string soId) => ExecAsync($@"
DELETE FROM ""SalesOrderLines"" WHERE ""SalesOrderId"" = '{soId}';
DELETE FROM ""SalesOrders""     WHERE ""SalesOrderId"" = '{soId}';");
    }
}
