using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 「刪除／修改單據後，過帳資料是否留下爛帳」的共用測試基底（真 PostgreSQL）。
    ///
    /// 補的是全庫最大的一塊測試盲區：既有的 Delete 守門測試（DeleteGoodsReceipt_WithQtyPutAway_ShouldThrow
    /// 之類）**一律只斷言「有沒有被擋」，從來沒有任何一支回頭檢查過帳資料的狀態**。
    /// 這在本專案特別危險，因為 StockMovement 與 JournalEntry 對單據都是弱關聯
    /// （DocNo / SourceDocId 字串，無外鍵），資料庫層不會阻止孤兒過帳——守門若哪天被改壞，
    /// 現有測試會全綠通過，爛帳靜默產生，直到月結對不起來才發現。
    ///
    /// 三條標準斷言（見 <see cref="AssertNoResidueAsync"/> / <see cref="AssertUnchangedAsync"/>）：
    ///   ① 刪除成功後：該單號的庫存異動／傳票／應付應收發票必須零筆
    ///   ② 修改成功後：過帳筆數與金額不變
    ///   ③ 守門擋下時：過帳資料完全不動（不是「大致不動」，是逐項相等）
    ///
    /// 為什麼走 HTTP 而非直接呼叫服務：**守門寫在 Controller**（Delete/Save action 內的
    /// QtyPutAway &gt; 0、Status != Draft 檢查），服務層根本沒有這些判斷。直接呼叫服務
    /// 等於繞過要驗的東西。
    ///
    /// 為什麼掛在 "ScheduleIntegration" collection：那是既有唯一帶 <see cref="TestAuthHandler"/>
    /// 的已登入 collection（admin / 廠別 P1 / 角色 1）。新增第三個 collection 會與既有兩個
    /// 平行共用同一測試庫，觸發 migration 與種子的雙 23505 競態——失敗數跨輪跳動且擴散到
    /// 無關類別，是本專案踩過的坑。名稱不貼切勝過重蹈覆轍。
    ///
    /// 資料隔離採**單號前綴**而非專屬廠別：TestAuthHandler 解析出的廠別固定是 P1，
    /// 單據只能建在 P1；若照 FlowTestBase 的做法按廠別清空，會連帶刪掉同 collection
    /// 其他測試的種子。故一律以 <see cref="Prefix"/> 開頭命名並只清自己的資料。
    /// </summary>
    public abstract class PostingResidueTestBase : IDisposable
    {
        protected const string Plant  = "P1";
        protected const string RoleId = "1";

        /// <summary>本組測試專用的單號／主檔前綴；清理與斷言都以此為界。</summary>
        protected const string Prefix = "RESID";

        protected readonly AuthedScheduleFactory Factory;

        protected PostingResidueTestBase(AuthedScheduleFactory factory) => Factory = factory;

        // ════════════════════════════════════════════════════════════════════
        // 收尾清理（Phase 5）
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 測試類別跑完後清掉本組前綴的過帳與單據。
        ///
        /// 原本每支測試只在**種子階段**清自己那一個單號，跑完的東西全部留在共用測試庫，
        /// 造成兩個實際踩到的後果：
        ///
        ///   ① <b>孤兒過帳</b>：<c>ClearWorkOrderChainAsync</c> 會刪掉工單鏈上的領料單，
        ///      卻沒刪掉那些領料單過帳產生的 StockMovement——庫存異動留在原地、單據不見了，
        ///      正好是孤兒過帳稽核（Phase 5 已掛進 <c>RunAllAsync</c>）要抓的東西。
        ///      實測測試庫累積了 40 組這種孤兒，全部是測試自己的殘留。
        ///   ② <b>列表被佔位</b>：本組單據日期一律設在 2097 年（避開已關帳期間），
        ///      而各單據列表預設日期 DESC 每頁 15 筆，殘留長期佔住第一頁，
        ///      把 UI 測試當下建立的單擠到第二頁——UI 測試以「存檔後列表沒有任何可開啟的
        ///      單據」的形式失敗，而那個症狀離真因非常遠。
        ///
        /// 子類若要自己的 Dispose，一律 override 並呼叫 <c>base.Dispose()</c>。
        /// </summary>
        public virtual void Dispose()
        {
            using var scope = NewScope(out var db);
            db.Database.ExecuteSqlRaw(PurgePrefixedSql);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 以單號前綴清掉過帳與單據。
        ///
        /// 子表用 information_schema 動態列舉而非逐表寫死：單據型別有 17 種、
        /// 每種各有 Lines／LocationDetails，寫死等於 50 條 DELETE，
        /// 且下次新增單據型別時會靜默漏掉。表頭反過來必須寫死並排序——
        /// 退貨類表頭帶著指向進貨／出貨的外鍵，順序錯了會踩 FK。
        /// 前綴是編譯期常數、非外部輸入，故 <c>format()</c> 拼識別字沒有注入面。
        /// </summary>
        private const string PurgePrefixedSql = @"
DO $$
DECLARE
    keys text[] := ARRAY[
        'ScrapDocId','LoanDocId','SampleDocId','OutsourceMaterialDocId',
        'ReworkMaterialDocId','StockAdjustmentId','StockTransferId',
        'CrossPlantTransferId','StagingId','GoodsReceiptId','GoodsReturnId',
        'OutsourceReceiptId','GoodsShipmentId','SalesReturnId','InternalOrderId',
        'SlipId','LandedCostAllocationId'];
    headers text[] := ARRAY[
        'SalesReturns','GoodsReturns','LandedCostAllocations',
        'ScrapDocs','LoanDocs','SampleDocs','OutsourceMaterialDocs',
        'ReworkMaterialDocs','StockAdjustments','StockTransfers',
        'CrossPlantTransfers','MaterialStagingOrders','MaterialSlips',
        'InternalOrders','OutsourceReceipts','GoodsShipments','GoodsReceipts'];
    r record;
    t text;
    k text;
    ap_ids text[];
    ar_ids text[];
BEGIN
    -- 子帳發票是「單據 → 發票 → 傳票」兩層：發票的 SourceDocId 指單據（RESID%），
    -- 但傳票的 SourceDocId 指的是**發票號**（API-…／ARI-…）。
    -- 只按前綴刪，會把發票刪掉而留下指向它的傳票——反而自己製造出孤兒。
    -- 實測第一版就是這樣多出 188 筆，故先把發票號抓成陣列，連它的傳票一起刪。
    ap_ids := ARRAY(SELECT ""APInvoiceId"" FROM ""APInvoices"" WHERE ""SourceDocId"" LIKE 'RESID%');
    ar_ids := ARRAY(SELECT ""ARInvoiceId"" FROM ""ARInvoices"" WHERE ""SourceDocId"" LIKE 'RESID%');

    DELETE FROM ""StockMovements"" WHERE ""DocNo"" LIKE 'RESID%';

    DELETE FROM ""JournalLines"" WHERE ""JournalId"" IN (
        SELECT ""JournalId"" FROM ""JournalEntries""
        WHERE ""SourceDocId"" LIKE 'RESID%'
           OR (""SourceDocType"" = 'APInvoice' AND ""SourceDocId"" = ANY(ap_ids))
           OR (""SourceDocType"" = 'ARInvoice' AND ""SourceDocId"" = ANY(ar_ids)));
    DELETE FROM ""JournalEntries""
        WHERE ""SourceDocId"" LIKE 'RESID%'
           OR (""SourceDocType"" = 'APInvoice' AND ""SourceDocId"" = ANY(ap_ids))
           OR (""SourceDocType"" = 'ARInvoice' AND ""SourceDocId"" = ANY(ar_ids));

    DELETE FROM ""APPaymentAllocations"" WHERE ""APInvoiceId"" = ANY(ap_ids);
    DELETE FROM ""ARReceiptAllocations"" WHERE ""ARInvoiceId"" = ANY(ar_ids);
    DELETE FROM ""APInvoiceLines"" WHERE ""APInvoiceId"" = ANY(ap_ids);
    DELETE FROM ""APInvoices"" WHERE ""APInvoiceId"" = ANY(ap_ids);
    DELETE FROM ""ARInvoiceLines"" WHERE ""ARInvoiceId"" = ANY(ar_ids);
    DELETE FROM ""ARInvoices"" WHERE ""ARInvoiceId"" = ANY(ar_ids);

    DELETE FROM ""InventoryReservations"" WHERE ""SourceDocId"" LIKE 'RESID%';

    FOR r IN
        SELECT c.table_name AS tbl, c.column_name AS col
        FROM information_schema.columns c
        WHERE c.table_schema = 'public'
          AND c.column_name = ANY(keys)
          AND NOT (c.table_name = ANY(headers))
    LOOP
        EXECUTE format('DELETE FROM %I WHERE %I LIKE ''RESID%%''', r.tbl, r.col);
    END LOOP;

    FOREACH t IN ARRAY headers LOOP
        SELECT c.column_name INTO k
        FROM information_schema.columns c
        WHERE c.table_schema = 'public' AND c.table_name = t
          AND c.column_name = ANY(keys)
        ORDER BY c.ordinal_position
        LIMIT 1;
        IF k IS NOT NULL THEN
            EXECUTE format('DELETE FROM %I WHERE %I LIKE ''RESID%%''', t, k);
        END IF;
    END LOOP;
END $$;";

        // ════════════════════════════════════════════════════════════════════
        // 基礎設施
        // ════════════════════════════════════════════════════════════════════

        protected IServiceScope NewScope(out ApplicationDbContext db)
        {
            var scope = Factory.Services.CreateScope();
            db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CurrentPlantId = Plant;
            db.CurrentRoleId  = RoleId;
            return scope;
        }

        /// <summary>取一個已驗證為 admin 的 client，並附上 AntiForgery token。</summary>
        protected async Task<(HttpClient Client, string Token)> NewAuthedClientAsync()
        {
            var client = Factory.CreateClient();
            var html   = await client.GetStringAsync("/Schedule");
            var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(m.Success, "未能取得 AntiForgery token（/Schedule 未渲染或未登入）");
            return (client, m.Groups[1].Value);
        }

        /// <summary>
        /// POST 一個單據動作（Confirm／Delete／Void…），回傳解析後的 JSON 與 HTTP 狀態。
        ///
        /// 刻意不對狀態碼做斷言：守門擋下時各 controller 的慣例並不一致——有的回
        /// BadRequest + success=false，有的回 200 + success=false。測試要驗的是
        /// 「有沒有擋」與「帳有沒有動」，不是狀態碼風格。
        /// </summary>
        protected static async Task<(bool Success, string Message)> PostActionAsync(
            HttpClient client, string token, string url, IDictionary<string, string>? form = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("RequestVerificationToken", token);
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            req.Content = new FormUrlEncodedContent(form ?? new Dictionary<string, string>());
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            // 少數動作成功時直接導頁或回空 body，視為成功。
            if (string.IsNullOrWhiteSpace(body)) return (resp.IsSuccessStatusCode, "");

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root    = doc.RootElement;
                var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
                var message = root.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
                return (success, message);
            }
            catch (JsonException)
            {
                // HTML 回應（例如驗證失敗重新渲染表單）一律視為未成功。
                return (false, body.Length > 400 ? body[..400] : body);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // 過帳足跡
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 一張單在「庫存 × 總帳 × 子帳 × 預留」四處留下的足跡。
        /// 只比筆數會漏掉「筆數相同但金額被改掉」的情況，故連同淨量與金額一起釘。
        ///
        /// 預留（<see cref="ActiveReservations"/>）與其他三項性質不同但同樣是「刪單後可能殘留的鎖」：
        /// 備料單確認時鎖的是 <see cref="Modules.INV.Models.InventoryReservation"/> 而不是庫存異動，
        /// 若刪除守門失效，庫存會被一張已不存在的單永久鎖住——比孤兒異動更難查，
        /// 因為帳面數量完全正確，只是可用量莫名其妙少一塊。
        /// </summary>
        public sealed record PostingFootprint(
            int MovementLines, decimal MovementNetQty, decimal MovementTotalCost,
            int JournalEntries, decimal JournalDebitTotal,
            int ApInvoices, int ArInvoices,
            int ActiveReservations, decimal ReservedQty)
        {
            public bool IsEmpty => MovementLines == 0 && JournalEntries == 0
                                   && ApInvoices == 0 && ArInvoices == 0
                                   && ActiveReservations == 0;

            public override string ToString()
                => $"庫存異動 {MovementLines} 筆（淨量 {MovementNetQty:F4}、成本 {MovementTotalCost:F4}）、"
                   + $"傳票 {JournalEntries} 張（借方合計 {JournalDebitTotal:F4}）、"
                   + $"應付發票 {ApInvoices} 張、應收發票 {ArInvoices} 張、"
                   + $"有效預留 {ActiveReservations} 筆（{ReservedQty:F4}）";
        }

        /// <summary>
        /// 擷取指定單號的過帳足跡。
        ///
        /// 後沖領料的異動單號是「工單號 + -BF」，故庫存異動以 StartsWith 比對而非等於——
        /// 否則工單類單據的足跡會漏算後沖那一段，而那正是最容易產生殘留的一段。
        /// </summary>
        protected async Task<PostingFootprint> CaptureAsync(string docNo)
        {
            using var _ = NewScope(out var db);

            var movements = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.DocNo == docNo || m.DocNo.StartsWith(docNo + "-"))
                .Select(m => new { m.StockQty, m.Direction, m.TotalCost })
                .ToListAsync();

            var journals = await db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.SourceDocId == docNo)
                .Select(e => e.JournalId)
                .ToListAsync();

            var debitTotal = journals.Count == 0 ? 0m
                : await db.JournalLines.IgnoreQueryFilters().AsNoTracking()
                    .Where(l => journals.Contains(l.JournalId))
                    .SumAsync(l => l.Debit);

            var apCount = await db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(i => i.SourceDocId == docNo);
            var arCount = await db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(i => i.SourceDocId == docNo);

            // 只算 Active：Released／Consumed 是正常生命週期終點，留著不算殘留。
            var reservations = await db.InventoryReservations.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.SourceDocId == docNo && r.Status == ReservationStatus.Active)
                .Select(r => r.ReservedQty)
                .ToListAsync();

            return new PostingFootprint(
                movements.Count,
                movements.Sum(m => m.StockQty * m.Direction),
                movements.Sum(m => m.TotalCost ?? 0m),
                journals.Count,
                debitTotal,
                apCount,
                arCount,
                reservations.Count,
                reservations.Sum());
        }

        // ════════════════════════════════════════════════════════════════════
        // 三條標準斷言
        // ════════════════════════════════════════════════════════════════════

        /// <summary>① 刪除成功後：過帳足跡必須完全清空，不得留下孤兒。</summary>
        protected async Task AssertNoResidueAsync(string docNo, string step)
        {
            var after = await CaptureAsync(docNo);
            Assert.True(after.IsEmpty,
                $"{step}：單據 {docNo} 已被刪除，但過帳資料仍在——{after}。\n"
                + "StockMovement／JournalEntry／InventoryReservation 對單據都沒有外鍵，資料庫不會攔這種孤兒，"
                + "只能靠 Controller 的刪除守門或回沖邏輯；此處紅燈代表那道守門有缺口。");
        }

        /// <summary>②③ 修改成功或守門擋下後：過帳足跡逐項不變。</summary>
        protected async Task AssertUnchangedAsync(PostingFootprint before, string docNo, string step)
        {
            var after = await CaptureAsync(docNo);
            Assert.True(before == after,
                $"{step}：單據 {docNo} 的過帳足跡被動到了。\n"
                + $"  操作前：{before}\n  操作後：{after}\n"
                + "已過帳的單據被修改／被擋下時，庫存與帳務都不應該有任何變動。");
        }

        /// <summary>守門必須擋下，且要有可讀的錯誤訊息（不是靜默失敗）。</summary>
        protected static void AssertBlocked((bool Success, string Message) result, string step)
        {
            Assert.False(result.Success,
                $"{step}：預期被守門擋下，實際卻成功了。訊息：{result.Message}");
            Assert.False(string.IsNullOrWhiteSpace(result.Message),
                $"{step}：被擋下了但沒有回任何訊息——使用者會看到「按了沒反應」。");
        }

        protected static void AssertAllowed((bool Success, string Message) result, string step)
            => Assert.True(result.Success, $"{step}：預期可以執行，實際被擋下。訊息：{result.Message}");
    }
}
