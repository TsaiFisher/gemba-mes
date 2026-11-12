using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Mobile
{
    /// <summary>
    /// 手機版「待我簽核」（唯讀）的真實 HTTP 守門。
    ///
    /// <para><b>為什麼走真 HTTP 而不是直接 new Controller</b>：這支控制器的重點有一半在
    /// <c>[Authorize]</c> 與 <c>_userManager.GetUserAsync(User)</c> 上——身分解析不出來時
    /// 回的是空清單，而空清單與「真的沒有待簽核」在斷言上長得一模一樣。
    /// 直接呼叫方法要自己組 <c>ClaimsPrincipal</c>，等於把最容易錯的那一段換成手工假資料。</para>
    ///
    /// <para>沿用 <see cref="AuthedScheduleFactory"/> 與既有 collection（見
    /// <c>CollectionLayoutGuardTests</c>：全專案共用同一個測試庫，不另開 collection）。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class MobileApprovalInboxTests : IAsyncLifetime
    {
        // 專屬前綴，收尾一律照這個前綴清——共用測試庫，留下 Pending 的簽核單
        // 會讓別支測試（以及桌機的待簽核清單量測）多出莫名其妙的資料。
        private const string Prefix = "ZZMOBAPV";

        private readonly AuthedScheduleFactory _factory;

        public MobileApprovalInboxTests(AuthedScheduleFactory factory) => _factory = factory;

        public Task InitializeAsync() => PurgeAsync();
        public Task DisposeAsync() => PurgeAsync();

        [Fact]
        public async Task 指到我的待簽核單會出現在手機清單上()
        {
            await SeedPendingAsync("PurchaseOrder", Prefix + "-PO-1");

            var items = await PendingItemsAsync();
            var mine = items.Single(e => e.GetProperty("documentId").GetString() == Prefix + "-PO-1");

            Assert.Equal("PurchaseOrder", mine.GetProperty("documentType").GetString());
            // 單別名稱與桌機共用 IApprovalDocumentTypeLabels（測試主機為 zh-TW 或 en-US 其一，兩者都不是代碼本身）
            Assert.Contains(mine.GetProperty("documentTypeLabel").GetString(), new[] { "採購單", "Purchase Order" });

            // 連結必須是**桌機**路由：手機端一張單據 Details 頁都沒有，
            // 這條連結存在的意義就是「到電腦上去看內容再簽」。
            Assert.Equal("/PurchaseOrder/Details/" + Prefix + "-PO-1",
                mine.GetProperty("detailsUrl").GetString());
        }

        [Fact]
        public async Task 沒有登錄連結的單別回null而不是省略欄位()
        {
            // 畫面靠 detailsUrl 是不是 null 決定要顯示按鈕還是「沒有連結」的說明。
            // 欄位整個不見的話，前端讀到 undefined 也會走同一條路——但那是巧合不是契約，
            // 而序列化設定一改（例如哪天加上 IgnoreNullValues）就會靜靜換成另一種行為。
            await SeedPendingAsync("ZZNoSuchDocType", Prefix + "-X-1");

            var items = await PendingItemsAsync();
            var mine = items.Single(e => e.GetProperty("documentId").GetString() == Prefix + "-X-1");

            Assert.True(mine.TryGetProperty("detailsUrl", out var url), "detailsUrl 欄位不見了。");
            Assert.Equal(JsonValueKind.Null, url.ValueKind);
        }

        [Fact]
        public async Task 筆數與清單長度一致()
        {
            // 兩支端點各自查一次 DB。條件寫歪的那一支會讓首頁 badge 與清單對不上，
            // 而使用者的回報會是「紅點說有 3 筆，點進去只有 1 筆」這種查不出來的抱怨。
            await SeedPendingAsync("PurchaseOrder", Prefix + "-PO-2");
            await SeedPendingAsync("WorkOrder", Prefix + "-WO-2");

            var items = await PendingItemsAsync();

            using var client = Client();
            var json = await client.GetStringAsync("/api/mobile/approval/count");
            var count = JsonDocument.Parse(json).RootElement.GetProperty("count").GetInt32();

            Assert.Equal(items.Count, count);
            Assert.True(count >= 2, $"種了 2 筆卻只數到 {count} 筆。");
        }

        [Fact]
        public async Task 已核准的單不會留在待簽核清單()
        {
            // 只驗「有的看得到」會讓一個永遠回全部的實作也通過。
            var id = await SeedPendingAsync("PurchaseOrder", Prefix + "-PO-3");

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var req = await db.ApprovalRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == id);
                req.Status = ApprovalStatus.Approved;
                await db.SaveChangesAsync();
            }

            var items = await PendingItemsAsync();
            Assert.DoesNotContain(items, e => e.GetProperty("documentId").GetString() == Prefix + "-PO-3");
        }

        [Fact]
        public async Task 沒有摘要的單別在手機上簽不了()
        {
            // 🚨 **這一支取代了原本的「手機簽核端點只有讀取」。**
            //
            // 原本那支守的是「刻意的缺席」：手機端沒有任何單據 Details 頁，
            // 在這裡放核准鈕等於做出一個看不到內容卻按得下去的按鈕，
            // 所以它斷言 MobileApprovalController 一個 POST 都沒有。
            // 那個理由**沒有變**，變的是缺的東西補上了——IApprovalSummaryProvider
            // 一個 DocumentType 一份摘要。於是不變式從「不准有寫入」變成
            // **「只有看得到內容的單別才簽得了」**，而這一支守的就是後者。
            //
            // 這裡用一個絕對不會有 provider 的假單別，驗第三層閘門（decide 那一層）。
            // 前兩層是 pending 的 canDecideOnMobile 與 summary 的 supported=false，
            // 各自另有測試——三層都要有，因為前端一有 bug 就會出現「看不到內容卻簽下去」。
            var id = await SeedPendingAsync("ZZNoSuchDocType", Prefix + "-NOSUM-1");

            using var client = Client();
            var res = await client.PostAsync($"/api/mobile/approval/{id}/decide",
                JsonContent("{\"action\":\"approve\"}"));

            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Contains("NOT_SUPPORTED_ON_MOBILE", await res.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            // 而且**真的沒有簽下去**。只驗狀態碼的話，一個「回 400 但照樣寫入」的實作
            // 也會讓上面兩行綠——那是最壞的一種：使用者看到錯誤，單子卻簽掉了。
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var req = await db.ApprovalRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == id);
            Assert.Equal(ApprovalStatus.Pending, req.Status);
            Assert.False(await db.ApprovalLogs.AnyAsync(l => l.RequestId == id),
                "沒有簽成，卻寫了一筆簽核紀錄。");
        }

        [Fact]
        public async Task 清單會標出哪些單別在手機上簽得了()
        {
            // 畫面靠 canDecideOnMobile 決定要給「查看並簽核」還是「請用電腦簽核」。
            // 兩種單別要在**同一份回應**裡分得出來——只驗其中一種的話，
            // 一個「永遠 true」或「永遠 false」的實作都會通過。
            await SeedPendingAsync("PurchaseOrder", Prefix + "-CAN-1");
            await SeedPendingAsync("ZZNoSuchDocType", Prefix + "-CAN-2");

            var items = await PendingItemsAsync();

            var can = items.Single(e => e.GetProperty("documentId").GetString() == Prefix + "-CAN-1");
            var cannot = items.Single(e => e.GetProperty("documentId").GetString() == Prefix + "-CAN-2");

            Assert.True(can.GetProperty("canDecideOnMobile").GetBoolean(),
                "PurchaseOrder 有 IApprovalSummaryProvider，應該簽得了。");
            Assert.False(cannot.GetProperty("canDecideOnMobile").GetBoolean(),
                "沒有 provider 的單別不該標成簽得了。");
        }

        [Fact]
        public async Task 摘要不得讓人讀到不是指給自己簽的單()
        {
            // 🚨 桌機的 Process GET 正是踩過這個洞——它原本只有 [Authorize]，
            //    任何登入者拿到 requestId 就讀得到別人簽核單的內容
            //    （見 ApprovalController.CanViewRequestAsync 的註解）。
            //    手機摘要回傳的是**金額與品項明細**，破口的代價比桌機那次更大。
            //
            //    這裡把單改成已核准——它就不在任何人的「待我簽核」清單裡了，
            //    而摘要的授權判定用的正是那份清單。
            var id = await SeedPendingAsync("PurchaseOrder", Prefix + "-SEC-1");

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var req = await db.ApprovalRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == id);
                req.Status = ApprovalStatus.Approved;
                await db.SaveChangesAsync();
            }

            using var client = Client();
            var res = await client.GetAsync($"/api/mobile/approval/{id}/summary");

            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            var body = await res.Content.ReadAsStringAsync();
            Assert.Contains("NOT_PENDING_FOR_YOU", body, StringComparison.Ordinal);

            // 連單號都不該漏。回 404 但把 documentId 放進 body 一樣是洩漏——
            // 那足以讓人枚舉出「有哪些單存在」。
            Assert.DoesNotContain(Prefix + "-SEC-1", body, StringComparison.Ordinal);
        }

        [Fact]
        public async Task 指給自己簽的單讀得到摘要()
        {
            // 反面對照。少了這一支，一個「一律回 404」的實作也會讓上面那支變綠，
            // 而那種實作的症狀是手機簽核整個不能用。
            var id = await SeedPendingAsync("PurchaseRequest", Prefix + "-OK-1");

            using var client = Client();
            var res = await client.GetAsync($"/api/mobile/approval/{id}/summary");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

            // 單據本身不存在（這裡只種了簽核申請、沒有真的請購單），
            // 所以 provider 回 null → supported=false + unavailable=true。
            // 這正是「單據已刪除或不在你的廠別」那條路徑，順便釘住它與
            // 「這個單別還沒做」是**不同的回應**——畫面的訊息完全不同。
            Assert.False(root.GetProperty("supported").GetBoolean());
            Assert.True(root.GetProperty("unavailable").GetBoolean(),
                "單據查不到時要標成 unavailable，否則畫面會顯示成「此單別請用電腦簽核」，"
                + "而那句話對「單子不見了」是錯的訊息。");
        }

        [Fact]
        public async Task 桌機清單的連結對照表與手機用的是同一份()
        {
            // ApprovalDocumentLinks 是唯一來源，_DocDetailsUrls.cshtml 由它產生 JS。
            // 這支測試釘的是「產生真的有跑」——Razor 那一段一旦寫壞（漏 Html.Raw、
            // 迴圈沒輸出），桌機的單號會**全部**變成點了沒反應，而頁面照樣 200、
            // 沒有任何錯誤訊息。這是本次改動最容易靜默失敗的地方。
            using var client = Client();
            var res = await client.GetAsync("/Approval/PendingIndex");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            // ⚠️ **刻意不做 HtmlDecode。** 這段輸出必須是**原始 JS**：Razor 不知道自己在
            // <script> 裡面，預設會把引號與反引號編成 &#x27; / &#x60;，瀏覽器讀到的就不是
            // 程式碼而是一堆實體。先解碼再斷言，正好把要偵測的缺陷抵銷掉——
            // 這支測試第一版就是這樣寫的，拿掉 Html.Raw 之後照樣綠。
            var html = await res.Content.ReadAsStringAsync();

            foreach (var key in new[] { "PurchaseOrder", "WorkOrder", "NonConformanceReport" })
            {
                var expected = $"'{key}': id => `" +
                    SmartFactory.MES.Modules._Shared.ApprovalDocumentLinks.Templates[key]
                        .Replace("{id}", "${id}") + "`";
                Assert.Contains(expected, html, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// 桌機「待我簽核」的單別欄要顯示名稱（依語系），不是代碼（2026-10-27 回報：中英都印 PurchaseOrder）。
        /// 走真 HTTP 渲染 Razor：名稱服務是 RequestServices 注入，漏註冊只有渲染時才炸。
        /// </summary>
        [Theory]
        [InlineData("zh-TW", "採購單")]
        [InlineData("en-US", "Purchase Order")]
        public async Task 桌機待我簽核_單別顯示名稱而不是代碼(string culture, string label)
        {
            await SeedPendingAsync("PurchaseOrder", Prefix + "-PO-D");

            using var client = Client();
            client.DefaultRequestHeaders.Add("Cookie", $".AspNetCore.Culture=c%3D{culture}%7Cuic%3D{culture}");
            var res = await client.GetAsync("/Approval/PendingIndex");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            // Razor 預設把非 ASCII 編成 &#x…; 實體，解碼後再比
            var html = System.Net.WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());

            Assert.Contains($"<span class=\"badge bg-secondary\">{label}</span>", html);
            Assert.DoesNotContain("<span class=\"badge bg-secondary\">PurchaseOrder</span>", html);
        }

        // ── 輔助 ────────────────────────────────────────────────────────────

        private static StringContent JsonContent(string json) =>
            new(json, System.Text.Encoding.UTF8, "application/json");

        private HttpClient Client() => _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        private async Task<List<JsonElement>> PendingItemsAsync()
        {
            using var client = Client();
            var res = await client.GetAsync("/api/mobile/approval/pending");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        }

        /// <summary>
        /// 種一張「當前步驟直接指名我」的待簽核單，回傳申請單 Id。
        ///
        /// <para>用 <see cref="ApproverType.User"/> 而不是 Role：Role 型要比對
        /// <c>ActiveRoleId</c>，而測試身分的角色由種子決定、不在本測試掌控之內——
        /// 那會讓這支測試在別人改了種子角色時以看不懂的方式變紅。</para>
        ///
        /// <para>⚠️ <c>PlantId</c>／<c>CreatedByRoleId</c> 必須明寫：
        /// 種資料的 scope 沒有登入使用者，<c>ApplyGlobalValues</c> 填不進去，
        /// 而全域查詢過濾會讓查詢端一筆都看不到——症狀是「清單永遠空的」。</para>
        /// </summary>
        private async Task<string> SeedPendingAsync(string docType, string docId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var admin = await db.Users.SingleAsync(u => u.UserName == "admin");

            var step = new ApprovalStep
            {
                Id = Guid.NewGuid().ToString(),
                Sequence = 1,
                ApproverType = ApproverType.User,
                TargetUserId = admin.Id,
            };
            db.ApprovalSteps.Add(step);

            var req = new ApprovalRequest
            {
                Id = Guid.NewGuid().ToString(),
                DocumentId = docId,
                DocumentType = docType,
                CurrentStepId = step.Id,
                Status = ApprovalStatus.Pending,
                RequesterId = admin.Id,
                PlantId = admin.ActivePlantId,
                CreatedByRoleId = admin.ActiveRoleId,
            };
            db.ApprovalRequests.Add(req);

            await db.SaveChangesAsync();
            return req.Id;
        }

        private async Task PurgeAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var reqs = await db.ApprovalRequests.IgnoreQueryFilters()
                .Where(r => r.DocumentId.StartsWith(Prefix)).ToListAsync();
            if (reqs.Count == 0) return;

            var stepIds = reqs.Select(r => r.CurrentStepId).ToList();
            db.ApprovalRequests.RemoveRange(reqs);
            db.ApprovalSteps.RemoveRange(
                await db.ApprovalSteps.Where(s => stepIds.Contains(s.Id)).ToListAsync());
            await db.SaveChangesAsync();
        }
    }
}
