using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.PUR.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 「審核中的單據不得刪除／修改」守門（Phase 4-3 順帶補的三個缺口）。
    ///
    /// 狀態機矩陣（Tests/StateMachines）驗的是純函式本身，證明不了 controller 有去呼叫它；
    /// 這一檔就是那半段——走真 HTTP 打 controller，確認守門真的接上了。
    /// 三個缺口都不是「擋不擋得住刪除」的問題，而是**簽核申請與單據脫節**：
    ///   - 進貨單／採購單：審核中照樣刪得掉，刪完 ApprovalRequest 還留著，
    ///     簽核人在「待我簽核」點進去只會看到一張不存在的單（openPanel 404）。
    ///   - 出貨單：送審後照樣改得動品項與數量，簽核人核准的是另一張單。
    ///
    /// 採購單／進貨單原本就有 Edit 守門、出貨單原本就有 Delete 守門——
    /// 三張單各自缺的那一格剛好互補，正是狀態機矩陣攤平後才看得出來的漂移。
    ///
    /// 種子刻意不建明細：三個守門都在讀明細之前就該擋下，
    /// 不建明細可以順便證明「擋下的原因是審核中」而不是別的前置條件不成立。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class PendingApprovalGuardTests : PostingResidueTestBase
    {
        private const string Supplier = Prefix + "-PAG-S";
        private const string Customer = Prefix + "-PAG-C";
        private static readonly DateOnly DocDate = new(2097, 6, 15);

        public PendingApprovalGuardTests(AuthedScheduleFactory factory) : base(factory) { }

        /// <summary>
        /// 測試結束清掉自己建的單。
        ///
        /// 這不是潔癖：本檔的單據日期是 2097，而各單據列表預設按日期 DESC 排序、
        /// 每頁 15 筆——殘留下來的單會長期佔住第一頁，把 UI 測試當下建的單擠到第二頁，
        /// 於是 UI 測試以「存檔後列表沒有任何可開啟的單據」的形式失敗，
        /// 而那個症狀離真因（另一個測試專案的殘留資料）非常遠。這個坑已經踩過一次。
        /// </summary>
        public override void Dispose()
        {
            // 逐表刪除已由 base 的前綴清理涵蓋（RESID% 過帳 + 單據），此處只需串接。
            base.Dispose();
        }

        // ── 進貨單 ──────────────────────────────────────────────────────────

        [Fact]
        public async Task GoodsReceipt_PendingApproval_Delete_Blocked()
        {
            var docNo = DocNoFor(nameof(GoodsReceipt_PendingApproval_Delete_Blocked), "PAG");
            using (var _ = NewScope(out var db))
            {
                await ClearGrAsync(db, docNo);
                await EnsureCompanyAsync(db, Supplier, isSupplier: true);
                db.GoodsReceipts.Add(new GoodsReceipt
                {
                    GoodsReceiptId = docNo, ReceiptDate = DocDate,
                    SupplierCode = Supplier, TypeCode = "PI", TypeCategory = "Purchase",
                    Status = DocStatus.Draft,
                    ApprovalStatus = DocumentApprovalStatus.PendingApproval,
                    CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId,
                });
                await db.SaveChangesAsync();
            }

            var (client, token) = await NewAuthedClientAsync();
            var result = await PostActionAsync(client, token, $"/GoodsReceipt/Delete/{docNo}");

            AssertBlocked(result, "刪除審核中的進貨單");
            AssertPendingDeleteMessage(result.Message, "進貨單");
            await AssertStillExistsAsync(docNo,
                db => db.GoodsReceipts.IgnoreQueryFilters().AnyAsync(d => d.GoodsReceiptId == docNo));
        }

        /// <summary>對照組：同一張單撤回簽核（改回草稿）之後就刪得掉——證明擋的是審核中而非別的。</summary>
        [Fact]
        public async Task GoodsReceipt_Draft_Delete_Succeeds()
        {
            var docNo = DocNoFor(nameof(GoodsReceipt_Draft_Delete_Succeeds), "PAG");
            using (var _ = NewScope(out var db))
            {
                await ClearGrAsync(db, docNo);
                await EnsureCompanyAsync(db, Supplier, isSupplier: true);
                db.GoodsReceipts.Add(new GoodsReceipt
                {
                    GoodsReceiptId = docNo, ReceiptDate = DocDate,
                    SupplierCode = Supplier, TypeCode = "PI", TypeCategory = "Purchase",
                    Status = DocStatus.Draft,
                    ApprovalStatus = DocumentApprovalStatus.Draft,
                    CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId,
                });
                await db.SaveChangesAsync();
            }

            var (client, token) = await NewAuthedClientAsync();
            AssertAllowed(await PostActionAsync(client, token, $"/GoodsReceipt/Delete/{docNo}"),
                          "刪除草稿進貨單");
            await AssertStillExistsAsync(docNo,
                db => db.GoodsReceipts.IgnoreQueryFilters().AnyAsync(d => d.GoodsReceiptId == docNo),
                expected: false);
        }

        // ── 採購單 ──────────────────────────────────────────────────────────

        [Fact]
        public async Task PurchaseOrder_PendingApproval_Delete_Blocked()
        {
            var poNo = DocNoFor(nameof(PurchaseOrder_PendingApproval_Delete_Blocked), "PAG");
            using (var _ = NewScope(out var db))
            {
                await ClearPoAsync(db, poNo);
                await EnsureCompanyAsync(db, Supplier, isSupplier: true);
                db.PurchaseOrders.Add(new PurchaseOrder
                {
                    PurchaseOrderId = poNo, OrderDate = DocDate,
                    SupplierCode = Supplier,
                    ApprovalStatus = DocumentApprovalStatus.PendingApproval,
                    CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId,
                });
                await db.SaveChangesAsync();
            }

            var (client, token) = await NewAuthedClientAsync();
            var result = await PostActionAsync(client, token, $"/PurchaseOrder/Delete/{poNo}");

            AssertBlocked(result, "刪除審核中的採購單");
            AssertPendingDeleteMessage(result.Message, "採購單");
            await AssertStillExistsAsync(poNo,
                db => db.PurchaseOrders.IgnoreQueryFilters().AnyAsync(d => d.PurchaseOrderId == poNo));
        }

        // ── 出貨單 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 出貨單缺的是修改守門。Save 走的是 PartialView 回傳（不是 JSON），
        /// 而 ModelState 不合法時同樣會回 PartialView——兩者在 <c>PostActionAsync</c>
        /// 眼裡長得一樣。若只斷言「沒成功」，表單欄位打錯字也會讓測試綠，
        /// 故這裡改讀整個 body 並比對訊息，確認擋下的原因就是審核中。
        /// </summary>
        [Fact]
        public async Task GoodsShipment_PendingApproval_Edit_Blocked()
        {
            var docNo = DocNoFor(nameof(GoodsShipment_PendingApproval_Edit_Blocked), "PAG");
            using (var _ = NewScope(out var db))
            {
                await ClearGsAsync(db, docNo);
                await EnsureCompanyAsync(db, Customer, isSupplier: false);
                db.GoodsShipments.Add(new GoodsShipment
                {
                    GoodsShipmentId = docNo, ShipDate = DocDate, TypeCode = "GS",
                    CompanyCode = Customer,
                    Status = DocStatus.Draft,
                    ApprovalStatus = DocumentApprovalStatus.PendingApproval,
                    CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId,
                });
                await db.SaveChangesAsync();
            }

            var (client, token) = await NewAuthedClientAsync();
            var raw = await PostFormRawAsync(client, token, "/GoodsShipment/Save", new Dictionary<string, string>
            {
                ["GoodsShipmentId"] = docNo,
                ["ShipDate"]        = DocDate.ToString("yyyy-MM-dd"),
                ["CompanyCode"]     = Customer,
                ["CreatedBy"]       = "admin",
                ["RowVersion"]      = "0",
                ["ShipAddress"]     = "改到別的地址",
            });

            // 必須先 HtmlDecode 再比中文：Razor 的預設 encoder 會把 CJK 全部輸出成
            // 數值字元參照（無法編輯 → &#x7121;&#x6CD5;&#x7DE8;&#x8F2F;），
            // 直接 Contains("無法編輯") 永遠不會中——訊息其實在，測試卻看不到它，
            // 於是「守門有沒有接上」這件事會被誤判成沒接上。
            var body = System.Net.WebUtility.HtmlDecode(raw);

            Assert.True(
                body.Contains("無法編輯") || body.Contains("cannot be edited", StringComparison.OrdinalIgnoreCase),
                "修改審核中的出貨單：預期被守門擋下並回「審核中無法編輯」，實際回應未含該訊息。\n"
                + "回應中的錯誤摘要：" + ExtractSummary(body));

            // 守門必須在寫入之前擋下：地址不得被改到
            using var __ = NewScope(out var check);
            var addr = await check.GoodsShipments.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.GoodsShipmentId == docNo).Select(d => d.ShipAddress).FirstAsync();
            Assert.Null(addr);
        }

        // ── 偽造 hidden 欄位（守衛存在但可繞過）────────────────────────────

        /// <summary>
        /// 採購單：把表單的 hidden <c>ApprovalStatus</c> 改成 Draft，不得繞過修改守衛。
        ///
        /// 這一格在守衛改讀資料庫之前是真的會過的。_Form 把 ApprovalStatus 放在
        /// <c>&lt;fieldset disabled&gt;</c> **外面**（否則 disabled 的欄位瀏覽器根本不送），
        /// 所以自行組一個 POST 把它從 PendingApproval 改成 Draft，守衛就放行，
        /// 接著品項、數量、單價照改。存檔後狀態又被寫回 PendingApproval，
        /// 而簽核申請那筆記錄完全沒動——簽核人核准的是他沒看過的內容，事後也看不出來改過。
        ///
        /// 明細必須送：PurchaseOrderFormVm 的 IValidatableObject 要求至少一筆，
        /// 少送會停在 ModelState 而根本走不到守衛，測試就變成驗了空氣。
        /// 單價給 0 以避開「有金額必須指定幣別／稅別／單位」的條件必填。
        /// </summary>
        [Fact]
        public async Task PurchaseOrder_PendingApproval_Edit_TamperedHiddenStatus_Blocked()
        {
            var poNo = DocNoFor(nameof(PurchaseOrder_PendingApproval_Edit_TamperedHiddenStatus_Blocked), "PAG");
            const string original = "原始備註（不得被改）";

            using (var _ = NewScope(out var db))
            {
                await ClearPoAsync(db, poNo);
                await EnsureCompanyAsync(db, Supplier, isSupplier: true);
                db.PurchaseOrders.Add(new PurchaseOrder
                {
                    PurchaseOrderId = poNo, OrderDate = DocDate,
                    SupplierCode = Supplier, Remarks = original,
                    ApprovalStatus = DocumentApprovalStatus.PendingApproval,
                    CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId,
                });
                await db.SaveChangesAsync();
            }

            var (client, token) = await NewAuthedClientAsync();
            var raw = await PostFormRawAsync(client, token, "/PurchaseOrder/Save", new Dictionary<string, string>
            {
                ["PurchaseOrderId"] = poNo,
                ["OrderDate"]       = DocDate.ToString("yyyy-MM-dd"),
                ["SupplierCode"]    = Supplier,
                ["CreatedBy"]       = "admin",
                ["RowVersion"]      = "0",
                ["Remarks"]         = "偽造改寫",
                // ↓ 偽造：資料庫是 PendingApproval，前端硬說是 Draft
                ["ApprovalStatus"]      = nameof(DocumentApprovalStatus.Draft),
                ["Lines[0].LineNo"]     = "1",
                ["Lines[0].ProductCode"] = Prefix + "-PAG-P",
                ["Lines[0].OrderQty"]   = "1",
                ["Lines[0].UnitPrice"]  = "0",
            });

            AssertEditBlocked(raw, "採購單");
            await AssertRemarksUnchangedAsync(poNo, original,
                db => db.PurchaseOrders.IgnoreQueryFilters().AsNoTracking()
                        .Where(d => d.PurchaseOrderId == poNo).Select(d => d.Remarks).FirstAsync());
        }

        /// <summary>
        /// 進貨單：同一種偽造。進貨單的另兩道修改守衛（已上架 QtyPutAway &gt; 0、
        /// 已退貨 ReturnedQty &gt; 0）本來就讀資料庫，擋得住；只有「審核中」這一格
        /// 原本信任前端。GoodsReceiptFormVm 沒有明細數量的條件必填，故連明細都不用送
        /// ——擋下的原因只可能是審核中。
        /// </summary>
        [Fact]
        public async Task GoodsReceipt_PendingApproval_Edit_TamperedHiddenStatus_Blocked()
        {
            var docNo = DocNoFor(nameof(GoodsReceipt_PendingApproval_Edit_TamperedHiddenStatus_Blocked), "PAG");
            const string original = "原始備註（不得被改）";

            using (var _ = NewScope(out var db))
            {
                await ClearGrAsync(db, docNo);
                await EnsureCompanyAsync(db, Supplier, isSupplier: true);
                db.GoodsReceipts.Add(new GoodsReceipt
                {
                    GoodsReceiptId = docNo, ReceiptDate = DocDate,
                    SupplierCode = Supplier, TypeCode = "PI", TypeCategory = "Purchase",
                    Status = DocStatus.Draft, Remarks = original,
                    ApprovalStatus = DocumentApprovalStatus.PendingApproval,
                    CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId,
                });
                await db.SaveChangesAsync();
            }

            var (client, token) = await NewAuthedClientAsync();
            var raw = await PostFormRawAsync(client, token, "/GoodsReceipt/Save", new Dictionary<string, string>
            {
                ["GoodsReceiptId"] = docNo,
                ["ReceiptDate"]    = DocDate.ToString("yyyy-MM-dd"),
                ["SupplierCode"]   = Supplier,
                ["CreatedBy"]      = "admin",
                ["RowVersion"]     = "0",
                ["Remarks"]        = "偽造改寫",
                ["ApprovalStatus"] = nameof(DocumentApprovalStatus.Draft),
            });

            AssertEditBlocked(raw, "進貨單");
            await AssertRemarksUnchangedAsync(docNo, original,
                db => db.GoodsReceipts.IgnoreQueryFilters().AsNoTracking()
                        .Where(d => d.GoodsReceiptId == docNo).Select(d => d.Remarks).FirstAsync());
        }

        // ── 輔助 ────────────────────────────────────────────────────────────

        /// <summary>
        /// Save 擋下時回的是 _Form PartialView（不是 JSON），欄位打錯字也會回同一個東西，
        /// 因此必須比對訊息本體。HtmlDecode 不可省——Razor 預設 encoder 會把 CJK
        /// 全部輸出成數值字元參照，直接 Contains 中文永遠不會中。
        /// </summary>
        private static void AssertEditBlocked(string raw, string doc)
        {
            var body = System.Net.WebUtility.HtmlDecode(raw);
            Assert.True(
                body.Contains("無法編輯") || body.Contains("cannot be edited", StringComparison.OrdinalIgnoreCase),
                $"偽造 hidden ApprovalStatus 修改審核中的{doc}：預期被守門擋下並回「審核中無法編輯」，"
                + "實際回應未含該訊息（代表守衛採信了前端帶回的狀態）。\n"
                + "回應中的錯誤摘要：" + ExtractSummary(body));
        }

        /// <summary>守門必須在寫入之前擋下——擋下了但內容已被改掉等於沒擋。</summary>
        private async Task AssertRemarksUnchangedAsync(
            string docNo, string expected, Func<ApplicationDbContext, Task<string?>> probe)
        {
            using var _ = NewScope(out var db);
            Assert.Equal(expected, await probe(db));
        }

        /// <summary>
        /// 訊息比對接受中英兩種：測試 client 不帶語系 cookie，
        /// 回哪一種語系取決於 RequestLocalization 的預設，不是本測試要釘的東西。
        /// </summary>
        private static void AssertPendingDeleteMessage(string message, string doc)
        {
            Assert.True(
                message.Contains("審核中") || message.Contains("approval is pending", StringComparison.OrdinalIgnoreCase),
                $"刪除審核中的{doc}：確實被擋下了，但訊息不是「審核中無法刪除」，"
                + $"代表擋下的原因是別的前置條件而非本測試要驗的守門。實際訊息：{message}");
        }

        /// <summary>
        /// 從回傳的 _Form HTML 裡挖出 validation summary 的內容。
        /// 直接印 body 前幾百字沒有用——那一段全是 hidden input，
        /// 真正的錯誤訊息在 fieldset 裡面，離開頭很遠。
        /// </summary>
        private static string ExtractSummary(string body)
        {
            var i = body.IndexOf("validation-summary", StringComparison.Ordinal);
            if (i < 0) return "（回應中沒有 validation summary 區塊）\n" + (body.Length > 400 ? body[..400] : body);
            var slice = body.Substring(i, Math.Min(600, body.Length - i));
            return slice;
        }

        /// <summary>回傳整個 response body（Save 這類回 PartialView 的動作需要）。</summary>
        private static async Task<string> PostFormRawAsync(
            HttpClient client, string token, string url, IDictionary<string, string> form)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("RequestVerificationToken", token);
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            req.Content = new FormUrlEncodedContent(form);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            var resp = await client.SendAsync(req);
            return await resp.Content.ReadAsStringAsync();
        }

        private async Task AssertStillExistsAsync(
            string docNo, Func<ApplicationDbContext, Task<bool>> probe, bool expected = true)
        {
            using var _ = NewScope(out var db);
            Assert.Equal(expected, await probe(db));
        }

        private static async Task EnsureCompanyAsync(ApplicationDbContext db, string code, bool isSupplier)
        {
            if (await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.CompanyCode == code)) return;
            db.Companies.Add(new Modules.BAS.Models.Company
            {
                CompanyCode = code, CompanyName = code,
                IsSupplier = isSupplier, IsCustomer = !isSupplier, IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>依測試方法名產生穩定且互不干擾的單號（同 InvDocResidueTestBase 的慣例）。</summary>
        private static string DocNoFor(string caseName, string tag)
            => $"{Prefix}{tag}-{Math.Abs(caseName.GetHashCode()) % 100000:D5}";

        private static async Task ClearGrAsync(ApplicationDbContext db, string docNo)
        {
            await db.GoodsReceiptLines.IgnoreQueryFilters()
                .Where(l => l.GoodsReceiptId == docNo).ExecuteDeleteAsync();
            await db.GoodsReceipts.IgnoreQueryFilters()
                .Where(d => d.GoodsReceiptId == docNo).ExecuteDeleteAsync();
        }

        private static async Task ClearPoAsync(ApplicationDbContext db, string poNo)
        {
            await db.PurchaseOrderLines.IgnoreQueryFilters()
                .Where(l => l.PurchaseOrderId == poNo).ExecuteDeleteAsync();
            await db.PurchaseOrders.IgnoreQueryFilters()
                .Where(d => d.PurchaseOrderId == poNo).ExecuteDeleteAsync();
        }

        private static async Task ClearGsAsync(ApplicationDbContext db, string docNo)
        {
            await db.GoodsShipmentLines.IgnoreQueryFilters()
                .Where(l => l.GoodsShipmentId == docNo).ExecuteDeleteAsync();
            await db.GoodsShipments.IgnoreQueryFilters()
                .Where(d => d.GoodsShipmentId == docNo).ExecuteDeleteAsync();
        }
    }
}
