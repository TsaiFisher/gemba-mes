using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.QA.Data;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.QA
{
    /// <summary>
    /// 對話畫面 S-7 的真實 HTTP 守門（契約：<c>new-screen-projectz.md</c> §12）。
    ///
    /// <para><b>這裡守的是「不需要推論服務就能成立」的那一半。</b>真的問一題必須有 Ollama，
    /// 那一段由 <c>ManualGenerationBenchmarkTests</c>（<c>[LiveOllamaFact]</c>）負責；
    /// 本檔刻意只涵蓋與模型無關的部分，否則整批測試會在沒有 GPU／沒開 Ollama 的機器上變成紅燈，
    /// 而那些機器上真正該守的東西（權限、閘門、不落地）其實一條都沒壞。</para>
    ///
    /// <para>沿用 <see cref="AuthedScheduleFactory"/> 與既有 collection，理由見
    /// <c>CollectionLayoutGuardTests</c>：全專案共用同一個測試庫，不另開 collection。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class QaAssistantScreenTests : IAsyncLifetime
    {
        private const string RoleId = "1";
        private const string MenuKey = "QaAssistant";

        /// <summary>
        /// 一句**檢索不到任何東西**的提問。
        ///
        /// <para>⚠️ <b>探針裡不能有常用中文詞。</b>原本寫的是
        /// 「ITEST-QA-PROBE 這串字不會出現在任何手冊頁裡」——那句話本身含
        /// 「手冊」「出現」「不會」「現在」，而詞彙通道用的是重疊雙字，
        /// 語料長到 679 塊時這幾個雙字命中 <b>61 塊</b>，探針早就不是零命中了。
        /// 這不是排序程式碼的回歸，是待決 #48 說的語料稀釋（每加一批手冊頁就再稀釋一截）。</para>
        ///
        /// <para>⚠️ 另一個前提：本 fixture 的 <c>DocChunks</c> 沒有向量，所以只有詞彙通道在跑。
        /// 語意通道**沒有相關度門檻**（定案 #29：與其用一個會砍掉正解的數字，
        /// 不如讓模型說不知道），一旦這台機器補了嵌入，任何提問都會拿到 top-K，
        /// 「零命中」這個前提就不成立了——屆時要改的是測試的做法（比照
        /// <c>ManualIndexIntegrationTests</c> 釘死通道），不是再換一句探針。</para>
        /// </summary>
        private const string NoHitProbe = "ITESTQAPROBE ZZQXPROBE";

        private readonly AuthedScheduleFactory _factory;

        public QaAssistantScreenTests(AuthedScheduleFactory factory) => _factory = factory;

        public Task InitializeAsync() => SetAsync(read: true, update: true);

        // 權限還原是必須的，不是禮貌：測試庫的 MenuPermissions 是跨進程共用的，
        // 留一個 CanRead=false 給別的量測，症狀是那一輪的數字看起來很正常但少了一整頁。
        public Task DisposeAsync() => SetAsync(read: true, update: true);

        // ── 權限閘門 ────────────────────────────────────────────────────────

        [Fact]
        public async Task 沒有讀取權限時連畫面都打不開()
        {
            await SetAsync(read: false, update: false);

            using var client = Client();
            var res = await client.GetAsync("/QaAssistant");

            // Forbid() 在有登入的情況下是 403；轉導到登入頁（302）代表驗證掉了，
            // 那是另一回事，不該被當成「權限有擋」。
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }

        [Fact]
        public async Task 沒有讀取權限時也問不了()
        {
            // 選單隱藏不等於擋得住——專案沒有全域權限 filter，POST 端點必須自己守。
            using var client = Client();
            var token = await TokenAsync(client);
            await SetAsync(read: false, update: false);

            var res = await client.PostAsync("/QaAssistant/Ask",
                Form(("question", "測試"), ("__RequestVerificationToken", token)));

            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }

        [Fact]
        public async Task 重建索引需要修改權限_只有讀取擋下來()
        {
            // 這一條就是 QA_ASSISTANT 矩陣裡 Full 與 Read 的實際差別：
            // 問問題只要 Read，重建索引會打滿一顆 CPU 幾分鐘並與所有人的提問搶推論槽。
            using var client = Client();
            var token = await TokenAsync(client);
            await SetAsync(read: true, update: false);

            var res = await client.PostAsync("/QaAssistant/Rebuild",
                Form(("__RequestVerificationToken", token)));

            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }

        // ── 畫面本身 ────────────────────────────────────────────────────────

        [Fact]
        public async Task 畫面渲染得出來_而且重建鈕只給有修改權限的人()
        {
            var html = await GetAsync("/QaAssistant");

            // View 位置若沒在 QAModule 註冊，Razor 會去找 /Views/QaAssistant/Index.cshtml，
            // **建置照樣過**，只有打開畫面才炸——所以這一條非整合測試不可。
            Assert.Contains("id=\"qaQuestion\"", html, StringComparison.Ordinal);
            Assert.Contains("id=\"qaForm\"", html, StringComparison.Ordinal);

            // 延遲是本類畫面的 UX 主軸（§12.5）：一題約一分鐘，畫面必須先講。
            Assert.Contains("__RequestVerificationToken", html, StringComparison.Ordinal);
            Assert.Contains("id=\"qaRebuild\"", html, StringComparison.Ordinal);

            await SetAsync(read: true, update: false);
            var readOnly = await GetAsync("/QaAssistant");
            Assert.DoesNotContain("id=\"qaRebuild\"", readOnly, StringComparison.Ordinal);
        }

        [Fact]
        public async Task 畫面沒有內聯事件屬性()
        {
            // CSP3 一旦於 script-src 加入 nonce-source 即忽略 unsafe-inline（本專案已如此），
            // 屆時 inline on*= 全數失效——而失效的形狀是「按了沒反應」，不是錯誤訊息。
            var html = await GetAsync("/QaAssistant");

            Assert.DoesNotMatch(new Regex(@"\son(click|change|submit|input|keyup)="), html);

            // 反面：委派要真的掛上去。
            Assert.Contains("data-action-submit=\"qaAsk\"", html, StringComparison.Ordinal);
            Assert.Contains("data-action=\"qaCancel\"", html, StringComparison.Ordinal);
        }

        // ── 提問的前置檢查（不需要推論服務）────────────────────────────────

        [Fact]
        public async Task 空白提問給明確訊息而不是靜默失敗()
        {
            var json = await AskAsync("   ");

            Assert.Contains("\"success\":false", json, StringComparison.Ordinal);
            // 訊息走 IStringLocalizer（R7），這裡只確認不是空的通用錯誤。
            Assert.DoesNotContain("\"message\":null", json, StringComparison.Ordinal);
        }

        [Fact]
        public async Task 超長提問擋在檢索之前()
        {
            // CPU 推論的 prefill 時間與 prompt token 數成正比，而超長提問還會在檢索端
            // 把 ts_query 撐成上百個詞——兩邊都要付錢，所以擋在最前面。
            var json = await AskAsync(new string('料', 5000));

            Assert.Contains("\"success\":false", json, StringComparison.Ordinal);
        }

        // ── 資料查詢模式（Phase 2，§9.1）──────────────────────────────────

        [Fact]
        public async Task 沒有讀取權限時也查不了資料()
        {
            // 與 Ask 分開測，不是重複：AskData 是**另一支** action，
            // 漏掉閘門不會有任何共用程式碼幫它擋。而它讀的是營運資料。
            using var client = Client();
            var token = await TokenAsync(client);
            await SetAsync(read: false, update: false);

            var res = await client.PostAsync("/QaAssistant/AskData",
                Form(("question", "測試"), ("__RequestVerificationToken", token)));

            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }

        [Fact]
        public async Task 資料查詢的前置檢查擋在推論之前()
        {
            // 空白與超長都必須在**送進模型之前**被擋掉。這一支跑得很快正是它的證明——
            // 一旦哪天前置檢查被搬到推論之後，這裡會從幾毫秒變成幾分鐘（或撞整題預算），
            // 症狀不是紅燈而是整批測試變慢。
            using var client = Client();
            var token = await TokenAsync(client);

            foreach (var q in new[] { "   ", new string('料', 5000) })
            {
                var res = await client.PostAsync("/QaAssistant/AskData",
                    Form(("question", q), ("__RequestVerificationToken", token)));

                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
                Assert.Contains("\"success\":false",
                    await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task 畫面有兩種提問模式_而且預設是手冊問答()
        {
            // 模式由使用者明確選（§9.5），不由程式從字面猜——猜錯的方向是
            // 把營運資料當成手冊資料處理，而使用者不會知道自己問到的是哪一邊。
            var html = await GetAsync("/QaAssistant");

            Assert.Contains("id=\"qaModeManual\"", html, StringComparison.Ordinal);
            Assert.Contains("id=\"qaModeData\"", html, StringComparison.Ordinal);

            // 預設值要釘住：預設落在資料查詢那一側，等於每個人第一次提問都打到營運資料。
            var manual = Regex.Match(html, @"<input[^>]*id=""qaModeManual""[^>]*>").Value;
            var data = Regex.Match(html, @"<input[^>]*id=""qaModeData""[^>]*>").Value;
            Assert.Contains("checked", manual, StringComparison.Ordinal);
            Assert.DoesNotContain("checked", data, StringComparison.Ordinal);

            // 委派要真的掛上去（同「沒有內聯事件屬性」那一支的理由）。
            Assert.Contains("data-action-change=\"qaModeChanged\"", html, StringComparison.Ordinal);
        }

        // ── §12.6 第 1 條：提問與答案一律不得落地 ───────────────────────────

        [Fact]
        public async Task 提問不會寫進索引()
        {
            // 規格 §8.7：業務資料絕不可進入向量庫。提問裡很可能有單號、料號、客戶名，
            // 一旦落進 DocChunks，它就會出現在**別人**的檢索結果裡——那張表的權限
            // 是靠 MenuKey 對應手冊頁來的，沒有任何欄位擋得住一段憑空多出來的文字。
            var before = await CountChunksAsync();

            // 這一題可能零命中（直接拒答）、也可能命中後因為推論服務沒開而失敗，
            // 兩條路都要驗——**它們都不得寫入任何一列**。
            await AskAsync(NoHitProbe);

            Assert.Equal(before, await CountChunksAsync());
        }

        // ── 串流（§11.2 C4）────────────────────────────────────────────────

        [Fact]
        public async Task 沒有讀取權限時串流端點也擋得住()
        {
            // 新增端點最容易漏掉的一件事。專案沒有全域權限 filter，每一支 POST 都要自己守，
            // 而漏掉的症狀是「功能正常」——只有沒權限的人才看得到差別。
            using var client = Client();
            var token = await TokenAsync(client);
            await SetAsync(read: false, update: false);

            var res = await client.PostAsync("/QaAssistant/AskStream",
                Form(("question", "測試"), ("__RequestVerificationToken", token)));

            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }

        [Fact]
        public async Task 串流的前置檢查仍然回一般JSON而不是訊框()
        {
            // 提問還沒送進模型就被擋下時，回應尚未開始輸出，所以還能用一般的
            // { success:false } 形狀。若這裡也改成 error 訊框，畫面端就得為
            // 「連線前失敗」與「連線後失敗」各寫一條路，而其中一條幾乎不會被走到。
            var (status, bodyText) = await AskStreamAsync("   ");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("\"success\":false", bodyText, StringComparison.Ordinal);
        }

        [Fact]
        public async Task 零命中在串流上也是直接拒答_不送模型()
        {
            // 這一支**不需要推論服務**：零命中會在送進模型前就短路。
            // 它因此是唯一能在沒有 Ollama 的機器上驗證整個 NDJSON 框架的路徑——
            // 端點通不通、訊框格式對不對、done 有沒有帶完整負載，全在這裡。
            var (status, bodyText) = await AskStreamAsync(NoHitProbe);

            Assert.Equal(HttpStatusCode.OK, status);

            var lines = bodyText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var last = System.Text.Json.JsonDocument.Parse(lines[^1]).RootElement;

            Assert.True(last.TryGetProperty("done", out var done),
                $"最後一行應該是 done 訊框，實際是：{lines[^1]}");
            Assert.True(done.GetProperty("isNoAnswer").GetBoolean());
            Assert.Equal(0, done.GetProperty("citations").GetArrayLength());
        }

        [Fact]
        public async Task 串流的第一個訊框是檢索階段()
        {
            // 這一格要在**檢索開始之前**送出，不是之後。檢索本身要先把提問送去嵌入
            // （一次 Ollama 往返），送在後面的話畫面在最需要回饋的那幾秒完全沒東西可顯示。
            // 用「第一行」而不是「有沒有出現」來斷言，就是為了釘住這個順序。
            var (status, bodyText) = await AskStreamAsync(NoHitProbe);

            Assert.Equal(HttpStatusCode.OK, status);

            var lines = bodyText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var first = System.Text.Json.JsonDocument.Parse(lines[0]).RootElement;

            Assert.True(first.TryGetProperty("stage", out var stage),
                $"第一行應該是階段訊框，實際是：{lines[0]}");
            Assert.Equal("search", stage.GetString());
        }

        [Fact]
        public async Task 零命中不會送出推論階段()
        {
            // 零命中在送進模型前就短路，畫面不該閃過「模型讀取中」——那會讓使用者
            // 以為問題出在模型很慢，而實際上根本沒有任何一頁手冊被找到。
            var (_, bodyText) = await AskStreamAsync(NoHitProbe);

            var stages = bodyText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
                .Where(e => e.TryGetProperty("stage", out _))
                .Select(e => e.GetProperty("stage").GetString())
                .ToList();

            Assert.DoesNotContain("infer", stages);
        }

        [Fact]
        public async Task 串流的提問一樣不會寫進索引()
        {
            // 與 Ask 同一條紅線（§8.7／§12.6-1）。新開一條輸出路徑時最容易忘記重驗的
            // 就是這種「兩條路徑共用同一個禁令」的約束。
            var before = await CountChunksAsync();

            await AskStreamAsync(NoHitProbe + " STREAM");

            Assert.Equal(before, await CountChunksAsync());
        }

        private async Task<(HttpStatusCode Status, string Body)> AskStreamAsync(string question)
        {
            using var client = Client();
            var token = await TokenAsync(client);

            var res = await client.PostAsync("/QaAssistant/AskStream",
                Form(("question", question), ("__RequestVerificationToken", token)));

            return (res.StatusCode, await res.Content.ReadAsStringAsync());
        }

        // ── 輔助 ────────────────────────────────────────────────────────────

        private HttpClient Client() => _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        private async Task<string> GetAsync(string url)
        {
            using var client = Client();
            return await GetAsync(client, url);
        }

        private static async Task<string> GetAsync(HttpClient client, string url)
        {
            var res = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            // HTML 實體還原：Razor 的預設 HtmlEncoder 會把模型帶出來的非拉丁字元編成
            // &#x63DB; 這種數值實體，不還原的話中文斷言會永遠通過（見 QaExtractScreenTests）。
            return WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
        }

        private async Task<string> AskAsync(string question)
        {
            // **取 token 與送出必須是同一個 client。** AntiForgery 是 cookie＋欄位成對驗證，
            // 每個 CreateClient 有自己的 cookie 容器——換一個 client 送出，
            // 收到的會是 400，而那與「權限擋下來」在斷言上長得很像。
            using var client = Client();
            var token = await TokenAsync(client);

            var res = await client.PostAsync("/QaAssistant/Ask",
                Form(("question", question), ("__RequestVerificationToken", token)));

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            return await res.Content.ReadAsStringAsync();
        }

        private static async Task<string> TokenAsync(HttpClient client)
        {
            var html = await GetAsync(client, "/QaAssistant");
            var m = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
            Assert.True(m.Success, "對話畫面上找不到 AntiForgery token——POST 全部會變成 400");
            return m.Groups[1].Value;
        }

        private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
            new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

        private async Task<int> CountChunksAsync()
        {
            using var scope = _factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<QaDbContext>()
                .DocChunks.CountAsync();
        }

        private async Task SetAsync(bool read, bool update)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var perm = await db.MenuPermissions
                .FirstOrDefaultAsync(p => p.RoleId == RoleId && p.MenuKey == MenuKey);

            if (perm == null)
            {
                perm = new MenuPermission { RoleId = RoleId, MenuKey = MenuKey };
                db.MenuPermissions.Add(perm);
            }

            perm.CanRead = read;
            perm.CanUpdate = update;
            await db.SaveChangesAsync();
        }
    }
}
