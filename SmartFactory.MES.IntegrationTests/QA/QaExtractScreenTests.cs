using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Data;
using SmartFactory.MES.Modules.QA.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.QA
{
    /// <summary>
    /// 擷取物只讀畫面（§10.11.11）的真實 HTTP 守門。
    ///
    /// <para><b>為什麼非整合測試不可</b>：這一頁的兩個關鍵性質都只在真的送出請求時才成立——
    ///   ① View 位置若沒在 <c>QAModule</c> 註冊，Razor 會去找 <c>/Views/QaExtract/Index.cshtml</c>，
    ///      **建置照樣過**，只有打開畫面才炸；
    ///   ② 「未核可段落不顯示內文」是渲染層的性質，Controller 單元測試看不到最終 HTML。</para>
    ///
    /// <para>沿用 <see cref="AuthedScheduleFactory"/> 與既有 collection，理由見
    /// <c>CollectionLayoutGuardTests</c>：全專案共用同一個測試庫，不另開 collection。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class QaExtractScreenTests : IAsyncLifetime
    {
        private const string RoleId = "1";
        private const string MenuKey = "QaExtract";

        // 測試列的識別前綴。共用測試庫是重跑庫，每輪先自清同前綴的殘留，
        // 否則第二輪會撞 IX_DocChunks_FilePath_Lang_ChunkIndex。
        private const string FilePath = "extract/itest-qaextract";

        private const string ApprovedText = "ITEST-APPROVED-換算方向本幣等於外幣乘匯率";
        private const string PendingText = "ITEST-PENDING-這一段還沒有人審過不該出現在畫面上";
        private const string StaleText = "ITEST-STALE-來源已改這一段應該標成已過期";

        private readonly AuthedScheduleFactory _factory;

        public QaExtractScreenTests(AuthedScheduleFactory factory) => _factory = factory;

        public async Task InitializeAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            await qa.DocChunks.Where(c => c.FilePath == FilePath).ExecuteDeleteAsync();

            qa.DocChunks.AddRange(
                Row(0, "itest-approved", ApprovedText, approved: true, stale: false),
                Row(1, "itest-pending", PendingText, approved: false, stale: false),
                Row(2, "itest-stale", StaleText, approved: true, stale: true));
            await qa.SaveChangesAsync();

            await SetReadAsync(db, true);
        }

        public async Task DisposeAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            await qa.DocChunks.Where(c => c.FilePath == FilePath).ExecuteDeleteAsync();

            // 權限還原是必須的，不是禮貌：測試庫的 MenuPermissions 是跨進程共用的，
            // 留一個 CanRead=false 給別的量測，症狀是那一輪的數字看起來很正常但少了一整頁。
            await SetReadAsync(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), true);
        }

        [Fact]
        public async Task 生效與過期的段落都列得出來_未核可的內文不出現()
        {
            var html = await GetAsync("/QaExtract?searched=true&pageSize=100");

            // 生效中：標題、頁碼、內文都看得到。
            Assert.Contains("rules/itest-approved.html", html, StringComparison.Ordinal);
            Assert.Contains(ApprovedText, html, StringComparison.Ordinal);

            // 過期：**必須列得出來**。§10.11.3 是靜默排除，沒有這一頁的話
            // 症狀只會是「助理突然不知道這條規則了」，而且查不出原因。
            Assert.Contains("rules/itest-stale.html", html, StringComparison.Ordinal);
            Assert.Contains(StaleText, html, StringComparison.Ordinal);

            // 未核可：存在看得到（審核者要知道「產出來但我沒放行」），
            // **但內文一個字都不能出現**——§10.11.5 把安全邊界整個押在人工核可上，
            // 畫面印出未審內文等於在閘門旁邊另開一扇門。
            Assert.Contains("rules/itest-pending.html", html, StringComparison.Ordinal);
            Assert.DoesNotContain(PendingText, html, StringComparison.Ordinal);
        }

        [Fact]
        public async Task 未核可的內文不會被關鍵字查詢試探出來()
        {
            // 關鍵字只比對標題與頁碼。若改成也比對內文，未核可段落的文字就能靠
            // 「查得到／查不到」被一個字一個字試出來，而畫面本身從頭到尾沒印出過它。
            var html = await GetAsync("/QaExtract?searched=true&pageSize=100&searchTerm=" +
                                      Uri.EscapeDataString("這一段還沒有人審過"));

            Assert.DoesNotContain("rules/itest-pending.html", html, StringComparison.Ordinal);
            Assert.DoesNotContain(PendingText, html, StringComparison.Ordinal);
        }

        [Fact]
        public async Task 狀態篩選只回生效中的段落()
        {
            var html = await GetAsync("/QaExtract?searched=true&pageSize=100&state=effective");

            Assert.Contains("rules/itest-approved.html", html, StringComparison.Ordinal);
            Assert.DoesNotContain("rules/itest-stale.html", html, StringComparison.Ordinal);
            Assert.DoesNotContain("rules/itest-pending.html", html, StringComparison.Ordinal);
        }

        [Fact]
        public async Task 沒有讀取權限時擋下來()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await SetReadAsync(db, false);
            try
            {
                using var client = Client();
                var res = await client.GetAsync("/QaExtract?searched=true");

                // Forbid() 在有登入的情況下是 403；轉導到登入頁（302）代表驗證掉了，
                // 那是另一回事，不該被當成「權限有擋」。
                Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            }
            finally
            {
                await SetReadAsync(db, true);
            }
        }

        // ── 輔助 ────────────────────────────────────────────────────────────

        private HttpClient Client() => _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        /// <summary>
        /// 取回頁面，**並把 HTML 實體還原**。
        ///
        /// <para><b>這一步不是為了好讀，是這幾條斷言成不成立的關鍵。</b>Razor 的預設
        /// <c>HtmlEncoder</c> 會把模型帶出來的非拉丁字元編成 <c>&amp;#x63DB;</c> 這種數值實體
        /// （本頁實測：`整合測試 › itest-approved` 整串都是實體，而 <c>IViewLocalizer</c>
        /// 的輸出不會）。不還原的話，<c>DoesNotContain(PendingText)</c> 這類**安全斷言會永遠通過**——
        /// 原始 HTML 裡本來就不可能出現那串中文字面，**即使畫面真的把未核可內文印出來了**。
        /// 這是假綠，而且是最壞的一種：它守的正是 §10.11.5 押在人工核可上的那條邊界。</para>
        /// </summary>
        private async Task<string> GetAsync(string url)
        {
            using var client = Client();
            var res = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            return WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
        }

        private static async Task SetReadAsync(ApplicationDbContext db, bool canRead)
        {
            var perm = await db.MenuPermissions
                .FirstOrDefaultAsync(p => p.RoleId == RoleId && p.MenuKey == MenuKey);

            if (perm == null)
            {
                perm = new MenuPermission { RoleId = RoleId, MenuKey = MenuKey };
                db.MenuPermissions.Add(perm);
            }

            perm.CanRead = canRead;
            await db.SaveChangesAsync();
        }

        private static DocChunk Row(int index, string slug, string text, bool approved, bool stale) =>
            new()
            {
                Corpus = DocCorpus.Extract,
                FilePath = FilePath,
                Lang = "zh",
                ChunkIndex = index,
                ContentHash = new string('0', 64),
                HeadingPath = $"整合測試 › {slug}",
                MenuKey = string.Empty,
                Module = string.Empty,
                DocUrl = $"/docs/user/rules/{slug}.html",
                Text = text,
                Lexemes = string.Empty,
                SourcePath = ".claude/specifications/itest.md",
                SourceHash = new string('0', 64),
                Approved = approved,
                IsStale = stale,
                IndexedAt = DateTime.UtcNow,
            };
    }
}
