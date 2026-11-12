using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Data;
using SmartFactory.MES.Modules.QA.Services;
using SmartFactory.MES.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.IntegrationTests.QA
{
    /// <summary>
    /// 手冊檢索的**評測題庫**（規格 §10.9 S1 的產出）。
    ///
    /// <para>這不是一般的功能測試，而是一份可量化的基準：</para>
    /// <list type="bullet">
    ///   <item><b>現在</b>：守住 S1 的詞彙檢索品質不因切詞／切塊／排序的改動而倒退。</item>
    ///   <item><b>S2</b>：接上語意通道與 RRF 之後，同一份題庫直接量得出「融合有沒有變好」。
    ///     沒有基準的話，加了 embedding 只能靠感覺說「好像比較準」。</item>
    ///   <item><b>S3</b>：待決 #5（Breeze vs TAIDE）的選型評測就用這份題庫，**不是額外的工**。</item>
    /// </list>
    ///
    /// <para><b>題目來自真實手冊頁的實際用語，不是照著索引反推的</b>——
    /// 用「銷售排名」去查標題叫「銷售排名」的頁面只能證明字串比對會動。
    /// 這裡刻意大量使用同義說法（「呆料」對「呆滯庫存」、「補料」對「缺貨分析」），
    /// 因為那才是使用者真正會打的字。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ManualRetrievalBenchmarkTests : IAsyncLifetime
    {
        private const string RoleId = "1";

        /// <summary>
        /// 題庫：(提問, 應命中的手冊頁)。目前 **52 題：29 題中文 ＋ 23 題英文（英文佔 44%）**。
        ///
        /// <para>⚠️ **2026-09-24 訂正**：原文寫「29 題中文 ＋ 6 題英文」，那是題庫只有 35 題時的組成。
        /// 英文從 6/35（17%）長到 23/52（44%）而這行註解沒跟上——而**題庫組成本身就是一個變數**：
        /// 三個 Recall 都是聚合值，英文子群一向比中文弱（本次實測 @3 中文 23/29＝79%、英文 16/23＝70%），
        /// 所以英文占比翻三倍等於把門檻的意義換掉了一次，而聚合數字上看不出來。
        /// 下次動題庫時，**組成與門檻必須在同一個改動裡一起調**。</para>
        /// 新增畫面時請一併補題——題庫涵蓋不到的模組，檢索品質就是沒人在看的。
        /// </summary>
        public static readonly (string Question, string ExpectedFile)[] Bank =
        {
            // 直述型：使用者知道功能叫什麼
            ("銷售排名怎麼看", "ord/sales-ranking.html"),
            ("缺貨分析在哪裡", "inv/stock-shortage.html"),
            ("呆滯庫存分析怎麼用", "inv/slow-moving-inventory.html"),
            ("訂單逾期跟催的畫面在哪", "ord/order-overdue.html"),
            ("MRP 執行要怎麼跑", "mfg/mrp-run.html"),
            ("派工作業怎麼報工", "mfg/dispatch.html"),
            ("盤點清冊怎麼建立", "inv/inventory-count.html"),
            ("請購單要怎麼送簽核", "pur/purchase-request.html"),
            ("會計期間怎麼關帳", "act/accounting-period.html"),
            ("委外收貨的超耗扣款怎麼算", "osp/outsource-receipt.html"),

            // 同義／口語型：使用者不知道功能的正式名稱
            ("哪些料低於安全庫存", "inv/stock-shortage.html"),
            ("久沒有動的存貨要去哪裡查", "inv/slow-moving-inventory.html"),
            ("哪些客戶買最多", "ord/sales-ranking.html"),
            ("有哪些訂單已經過了交期還沒出貨", "ord/order-overdue.html"),
            ("倉庫之間要移轉庫存怎麼做", "inv/stock-transfer.html"),
            ("不良品要報廢或重工怎麼處理", "qcr/ncr.html"),
            ("設備稼動率跟良率的綜合指標", "mfg/oee.html"),
            ("應付發票是自動產生還是手開", "fin/ap-invoice.html"),

            // 名詞／代號型：專有名詞檢索，正是混合檢索存在的理由
            ("NCR 處置方式有哪些", "qcr/ncr.html"),
            ("OEE 的公式", "mfg/oee.html"),
            ("StockShortage 這個畫面在做什麼", "inv/stock-shortage.html"),
            ("模具主檔要維護哪些規格", "bas/tooling.html"),
            ("產品主檔有哪些欄位", "bas/product.html"),
            ("系統參數在哪個選單", "sys/system-param.html"),

            // 英文（en 索引；驗證 english 組態與 stemming）
            ("How do I check the sales ranking?", "ord/sales-ranking.html"),
            ("Which items are below safety stock?", "inv/stock-shortage.html"),
            ("How to run MRP", "mfg/mrp-run.html"),
            ("overdue sales orders", "ord/order-overdue.html"),
            ("outsource receipt scrap deduction", "osp/outsource-receipt.html"),
            ("management dashboard KPI", "rpt/dashboard.html"),

            // 本分支新增的 5 個畫面（規則：新增畫面一併補題）。
            // 一律用口語／同義說法，不照標題反推——照標題問只能證明字串比對會動。
            ("排程跑出來的報表要去哪裡拿", "rpt/report-inbox.html"),
            ("報表排程上次跑成功還是失敗", "rpt/report-run-history.html"),
            ("報表要怎麼設定每個月自動寄出", "rpt/report-schedule.html"),
            ("可以直接問系統操作問題嗎", "sys/qa-assistant.html"),
            ("哪幾個廠的參數設定跟其他廠不一樣", "bas/plant-param-overview.html"),

            // 英文擴充 17 題（2026-08-31，缺口 G4 的成本尺原本已飽和：舊的 6 題全部 6/6、
            // MRR 1.000，所以「開了 ExtractCrossLang 之後英文沒退步」這句話證據力有限）。
            //
            // **配對設計**：這 17 題的正解頁與上面 17 題中文題**完全相同**，只是換語言問。
            // 中英同頁並列，量到的差就只能歸因於語言，不能歸因於「英文那幾題剛好比較好答」
            // ——隨機挑頁做不到這件事，而挑得順手一點就會挑出一份天生飽和的題庫。
            //
            // 題目一律從**英文頁的實際內文**寫出來（`DocChunks` 的 en 塊逐頁讀過），
            // 不是把中文題丟去翻譯：翻譯會把中文標題的用字帶進來，變成在量翻譯一致性。
            // 也一律用口語／同義說法，不照標題反推（與上面中文題同一條規矩）。
            ("how do I stop people posting into a month that is already finished", "act/accounting-period.html"),
            ("which factories have settings different from the default", "bas/plant-param-overview.html"),
            ("where do I create a new item with its unit and cost", "bas/product.html"),
            ("where are the molds and their cavity counts kept", "bas/tooling.html"),
            ("are supplier invoices raised automatically or keyed in by hand", "fin/ap-invoice.html"),
            ("how do I set up a stocktake and post the differences", "inv/inventory-count.html"),
            ("which stock has not moved for a long time", "inv/slow-moving-inventory.html"),
            ("how do I move stock from one warehouse to another", "inv/stock-transfer.html"),
            ("how does the shop floor report progress on a job", "mfg/dispatch.html"),
            ("the combined measure of machine uptime and yield", "mfg/oee.html"),
            ("how do I send a requisition for approval", "pur/purchase-request.html"),
            ("what can I do with material that failed inspection", "qcr/ncr.html"),
            ("where do I pick up the reports a schedule produced", "rpt/report-inbox.html"),
            ("did last night's scheduled report succeed or fail", "rpt/report-run-history.html"),
            ("how do I have a report produced automatically every month", "rpt/report-schedule.html"),
            ("can I just ask the system how to do something", "sys/qa-assistant.html"),
            ("which menu holds the global switches for the whole system", "sys/system-param.html"),
        };

        /// <summary>不該有答案的提問。用來量「不相關」那一群的分數分佈（§10.8 門檻的另一半）。</summary>
        private static readonly string[] OutOfScope =
        {
            "鸚鵡螺保養週期",
            "今天午餐吃什麼",
            "公司尾牙什麼時候辦",
            "怎麼申請留職停薪",
        };

        private readonly WebApplicationFactory<Program> _app;
        private readonly ITestOutputHelper _out;

        public ManualRetrievalBenchmarkTests(AuthedScheduleFactory factory, ITestOutputHelper output)
        {
            _app = factory;
            _out = output;
        }

        public async Task InitializeAsync()
        {
            using var scope = _app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IDocIndexBuilder>().RebuildAsync();
            await GrantAllAsync(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        }

        public Task DisposeAsync() => Task.CompletedTask;

        /// <summary>
        /// 提問是不是英文。**刻意呼叫產品程式的 <c>CjkTokenizer.DetectLang</c>**，
        /// 不在測試裡自己數 ASCII：判準一旦有兩份，題庫分類與實際走哪套 tsvector 組態
        /// 就可能不一致，而那種偏差不會有任何錯誤訊息。
        /// </summary>
        internal static bool IsEnglish(string question) =>
            string.Equals(CjkTokenizer.DetectLang(question), "en", StringComparison.Ordinal);

        [Fact]
        public async Task 題庫的檢索品質不得倒退()
        {
            var ranks = new List<(string Question, int Rank)>();

            // 走純詞彙：守門值（下方）量自 S1 的詞彙通道。
            // 若這裡跟著環境有無 Ollama 而改變檢索方式，同一個門檻就會有兩種意義。
            foreach (var (question, expected) in Bank)
            {
                var hits = await SearchAsync(question, topK: 10, lexicalOnly: true);
                var idx = hits.ToList().FindIndex(h => h.DocUrl == "/docs/user/" + expected);
                ranks.Add((question, idx < 0 ? 0 : idx + 1));
            }

            var top1 = ranks.Count(r => r.Rank == 1);
            var top3 = ranks.Count(r => r.Rank is >= 1 and <= 3);
            var top5 = ranks.Count(r => r.Rank is >= 1 and <= 5);
            var miss = ranks.Where(r => r.Rank == 0).ToList();
            var mrr = ranks.Where(r => r.Rank > 0).Sum(r => 1.0 / r.Rank) / ranks.Count;

            _out.WriteLine($"題數 {ranks.Count}");
            _out.WriteLine($"Recall@1={top1}/{ranks.Count} ({top1 * 100.0 / ranks.Count:F0}%)");
            _out.WriteLine($"Recall@3={top3}/{ranks.Count} ({top3 * 100.0 / ranks.Count:F0}%)");
            _out.WriteLine($"Recall@5={top5}/{ranks.Count} ({top5 * 100.0 / ranks.Count:F0}%)");
            _out.WriteLine($"MRR     ={mrr:F3}");

            // 分語言印出來。合併成一個絕對計數會**掩蓋單邊退步**：
            // 中文掉 4 題、英文升 4 題，總數一模一樣而守門全綠。
            // 門檻仍設在總數上（一份題庫一個門檻），但讀的人要看得到是哪一邊在動。
            foreach (var (label, subset) in new[]
                     {
                         ("中文", ranks.Where(r => !IsEnglish(r.Question)).ToList()),
                         ("英文", ranks.Where(r => IsEnglish(r.Question)).ToList()),
                     })
            {
                if (subset.Count == 0) continue;
                _out.WriteLine($"  {label} {subset.Count} 題：" +
                               $"@1={subset.Count(r => r.Rank == 1)}" +
                               $"　@3={subset.Count(r => r.Rank is >= 1 and <= 3)}" +
                               $"　@5={subset.Count(r => r.Rank is >= 1 and <= 5)}" +
                               $"　未進前 10={subset.Count(r => r.Rank == 0)}");
            }
            if (miss.Count > 0)
            {
                _out.WriteLine("前 10 名內完全沒命中：");
                foreach (var m in miss) _out.WriteLine("  · " + m.Question);
            }

            // 逐題名次：紅燈時沒有這一段就只知道「掉了幾格」，不知道是哪幾題、掉到第幾名。
            // 語料一長就可能有題目被新頁面擠掉，要修得先知道從第 1 名掉到第幾名。
            _out.WriteLine("逐題名次（0＝前 10 名外）：");
            foreach (var (question, rank) in ranks)
                _out.WriteLine($"  {rank,2}  {question}");

            // ── 守門值 ────────────────────────────────────────────────────
            // 2026-08-05 首次實測（S1，純詞彙通道，30 題）：
            //   Recall@1=18/30 (60%)  Recall@3=26/30 (87%)  Recall@5=29/30 (97%)
            //   Recall@10=30/30       MRR=0.748
            //
            // 2026-08-19 重新實測（語料 154 頁，題庫依規則補到 35 題）：
            //   Recall@1=18/35 (51%)  Recall@3=31/35 (89%)  Recall@5=33/35 (94%)
            //   MRR=0.693
            //
            // 2026-08-26 重新實測（語料 686 塊；詞彙通道已拆成標題／內文兩欄，預設**等重**）：
            //   Recall@1=19/35 (54%)  Recall@3=31/35 (89%)  Recall@5=33/35 (94%)  MRR=0.708
            // 2026-08-31（52 題，＋17 題英文；同一份 QaBench 語料）：
            //   Recall@1=25/52 (48%)  Recall@3=41/52 (79%)  Recall@5=46/52 (88%)  MRR=0.645
            //   百分比下降**不是退步**，是題庫換了組成：英文題在詞彙通道上本來就比中文難，
            //   混進來會把整體百分比拉低。要看退步請看分語言那兩列。
            //   與 08-19 的 18／31／33／0.693 幾乎相同，而這是**預期中的**：
            //   拆兩欄在等重之下與拆之前逐列同分（psql 比對 686 列、兩種 regconfig，最大差 0），
            //   差的 1 題來自這段期間長出來的手冊頁，不是排序改動。
            //   ⚠️ 這支測試量的仍然**只有手冊**——測試庫裡沒有擷取物（見 §10.9）。
            //   手冊與擷取物在同一次排序裡競爭的那個數字要看 ManualLexicalWeightSweepTests。
            //
            // ⚠️ **絕對題數持平（18），命中率卻從 60% 掉到 51%——那是語料稀釋，不是排序回歸。**
            //    同一份原始 30 題實測：本分支起點（149 頁）16／補完 5 個新畫面的手冊頁（154 頁）14。
            //    純詞彙通道沒有標題／標頭加權（`Lexemes` 是 HeadingPath＋Text 平鋪後直接 to_tsvector），
            //    新頁面只要含有同樣的常用詞（「畫面」「設定」「客戶」）就會把正解擠下去；
            //    語料每長一批，這個數字就會再掉一截。**已登錄待辦：詞彙通道加標題權重**
            //    （`llm-qa-and-report-tools.md` §10.9）。在那之前，這個門檻擋得住「排序程式碼壞掉」，
            //    擋不住「語料長大」——看到它紅，先確認是哪一種。
            //
            // 門檻一律低於實測值數格：這是**回歸守門**，不是目標值。
            // 貼著實測值設門檻的話，任何無害的排序微調都會變成紅燈，
            // 於是門檻很快被調鬆或測試被停用——那比沒有守門更糟。
            // 提高門檻要連同題庫一起擴充，不要為了讓數字好看而刪題。
            // Recall@1 維持 15（未隨實測值調高）：同一份語料重跑會因 `ORDER BY Score DESC, Id`
            // 的同分排序而有 ±1 的抖動，貼著 18 設會變成間歇紅燈。
            // 2026-08-31 重訂門檻：題庫 35 → 52（＋17 題英文，見上方配對設計）。
            // ⚠️ **這一步不能忘，忘了就是靜默放鬆守門**——門檻是絕對題數，
            // 分母變大而門檻不動的話，@5 的容忍度會從「可掉 2 題」變成「可掉 16 題」，
            // 而且測試照樣全綠、沒有任何訊號。加題庫與升門檻必須同一次做完。
            // 留的餘裕與原本一致（3–4 格），理由見上方註解：貼著實測值設會製造假紅。
            // 2026-09-01 併入 main 後重新量測（語料 686 → 692 塊，題庫不變仍 52 題）：
            //   Recall@1=26/52 (50%)  Recall@3=43/52 (83%)  Recall@5=47/52 (90%)  MRR=0.674
            //   三欄都比 08-31 好 1～2 題——**語料變大而檢索變好**，因為多出來的 6 塊來自
            //   四頁既有手冊補內容（跨過整頁併塊門檻才多切的），不是新主題稀釋。
            //   @3 的餘裕因此變成 6 格，已回收到 4 格（37 → 39）；@5 與 @1 原本就是 4 格，不動。
            // 🔄 2026-09-24 重訂門檻：詞彙通道的預設權重由**等重**改為**標題 1.5 倍**
            //   （`ManualSearchService.DefaultLexicalWeights`）。
            //
            //   改之前（等重）：@1=22/52  @3=39/52  @5=46/52   ← @3 與 @1 貼在門檻上，零餘裕
            //   改之後（1.5倍）：@1=31/52  @3=42/52  @5=49/52
            //     中文 29 題：@1 13→19  @3 23→25  @5 27→27（持平）
            //     英文 23 題：@1  9→12  @3 16→17  @5 19→22
            //
            //   **為什麼現在才改**：2026-08-31 評估過並裁決維持等重，擋下它的理由是
            //   「中文 @5 等重勝 26 vs 25」。語料從 686 塊長到 863 塊（＋26%）之後
            //   那一格變成平手，跨語言取捨消失——六格裡五格改善、一格持平，沒有一格變差。
            //   機制見 `ManualSearchService.LexicalWeightsKey` 的文件：語料越大，
            //   標題作為「這一頁在講什麼」的訊號就越值錢。
            //
            //   門檻依本節既有政策「低於實測值數格」重訂：
            //     @1 22 → 27（實測 31，留 4 格）
            //     @3 39 → 39（實測 42，留 3 格；**不下調**——品質變好時調低門檻是反向操作）
            //     @5 43 → 45（實測 49，留 4 格）
            //   ⚠️ 這次是**在品質改善的同一個改動裡**調門檻，不是為了讓紅燈變綠而調鬆。
            Assert.True(top5 >= 45, $"Recall@5 掉到 {top5}/{ranks.Count}，低於守門值 45（2026-09-24 標題 1.5 倍權重下實測 49/52）");
            Assert.True(top3 >= 39, $"Recall@3 掉到 {top3}/{ranks.Count}，低於守門值 39（2026-09-24 標題 1.5 倍權重下實測 42/52）");
            Assert.True(top1 >= 27, $"Recall@1 掉到 {top1}/{ranks.Count}，低於守門值 27（2026-09-24 標題 1.5 倍權重下實測 31/52）");
        }

        [Fact]
        public async Task 量測_相關與不相關的分數分佈()
        {
            // §10.8 的相關度門檻要有依據才能訂。這裡把兩群的分數印出來——
            // 若兩群明顯分離，門檻就設在中間；若重疊嚴重，代表**單靠詞彙通道訂不出門檻**，
            // 必須等 S2 的語意通道到位（那也是一個結論，不是失敗）。
            // 一律走純詞彙：本測試量的是**詞彙分數**的分佈。
            // 接上語意通道後 Score 會變成 RRF 分數（量級只由名次決定），
            // 拿它來量門檻等於換了量尺卻沿用舊刻度。
            var relevant = new List<double>();
            foreach (var (question, expected) in Bank)
            {
                var hits = await SearchAsync(question, topK: 10, lexicalOnly: true);
                var hit = hits.FirstOrDefault(h => h.DocUrl == "/docs/user/" + expected);
                if (hit.LexicalScore > 0) relevant.Add(hit.LexicalScore);
            }

            var irrelevant = new List<double>();
            foreach (var q in OutOfScope)
            {
                var hits = await SearchAsync(q, topK: 10, lexicalOnly: true);
                if (hits.Count > 0) irrelevant.Add(hits.Max(h => h.LexicalScore));
            }

            _out.WriteLine($"應命中的正解分數  {Describe(relevant)}");
            _out.WriteLine($"不該有答案的最高分 {Describe(irrelevant)}");
            _out.WriteLine(
                relevant.Count > 0 && irrelevant.Count > 0
                    ? $"正解最低 {relevant.Min():F4} vs 雜訊最高 {irrelevant.Max():F4} → " +
                      (relevant.Min() > irrelevant.Max()
                          ? "兩群完全分離，門檻可訂在兩者之間"
                          : "兩群重疊，單憑詞彙分數訂不出安全門檻（留待 S2 語意通道）")
                    : "樣本不足");

            Assert.NotEmpty(relevant);
        }

        [LiveOllamaFact]
        public async Task 量測_語意通道與_RRF_融合是否勝過純詞彙()
        {
            // §10.9 S2-c。**這個測試的價值在於印出來的數字，不在於它綠不綠**——
            // 沒有這一份對照，「加了 embedding 好像比較準」就只是感覺。
            // 三個通道用的是同一份題庫、同一份索引、同一組權限，唯一的差別是檢索方式。
            using var scope = _app.Services.CreateScope();
            var embedding = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();
            Assert.False(embedding.EmbeddingModelName.Length == 0,
                "未設定 Features__QaAssistant__EmbeddingModel，這個測試無法量任何東西。");

            var builder = scope.ServiceProvider.GetRequiredService<IDocIndexBuilder>();
            var (embedded, pending) = await builder.EmbedPendingAsync();
            _out.WriteLine($"本次補嵌入 {embedded} 塊，仍待嵌入 {pending} 塊，模型 {embedding.EmbeddingModelName}");
            Assert.Equal(0, pending);

            var vectors = scope.ServiceProvider.GetRequiredService<IDocVectorStore>();
            var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var urlById = await qa.DocChunks.ToDictionaryAsync(c => c.Id, c => c.DocUrl);

            var lexicalRanks = new List<int>();
            var semanticRanks = new List<int>();
            var fusedRanks = new List<int>();
            var rrfRanks = new List<int>();
            var relevantSemantic = new List<double>();

            foreach (var (question, expected) in Bank)
            {
                var url = "/docs/user/" + expected;

                lexicalRanks.Add(RankOf(await SearchAsync(question, topK: 10, lexicalOnly: true), url));

                var fused = await SearchAsync(question, topK: 10);
                fusedRanks.Add(RankOf(fused, url));
                rrfRanks.Add(RankOf(await SearchAsync(question, topK: 10, useRrf: true), url));

                var hit = fused.FirstOrDefault(h => h.DocUrl == url);
                if (hit.SemanticScore is { } s) relevantSemantic.Add(s);

                var vector = await embedding.EmbedAsync(new[] { question }, EmbeddingKind.Query);
                var lang = CjkTokenizer.DetectLang(question) ?? "zh";
                var semantic = await vectors.SearchAsync(
                    vector[0], lang, AllKeys, AllModules, crossLangExtract: false, 10);
                semanticRanks.Add(semantic
                    .Select((v, i) => (Url: urlById.GetValueOrDefault(v.ChunkId, ""), Rank: i + 1))
                    .FirstOrDefault(x => x.Url == url).Rank);
            }

            // §10.8／S2-d：語意分數對「離題」的區辨力是否真的高於詞彙分數。
            var irrelevantSemantic = new List<double>();
            var outOfScopeDetail = new List<string>();
            foreach (var q in OutOfScope)
            {
                var hits = await SearchAsync(q, topK: 10);
                var best = hits.Where(h => h.SemanticScore is not null).ToList();
                if (best.Count == 0) continue;

                var top = best.MaxBy(h => h.SemanticScore!.Value);
                irrelevantSemantic.Add(top.SemanticScore!.Value);

                // 逐題印出「最像的是哪一段」。門檻訂不出來時，成因是
                // 「離題提問其實與手冊有真實交集」還是「模型亂比」——兩者的處理方式完全不同。
                outOfScopeDetail.Add($"  {q,-16} {top.SemanticScore:F4}  ← {top.HeadingPath}");
            }

            Report("純詞彙", lexicalRanks);
            Report("純語意", semanticRanks);
            Report("RRF 融合", rrfRanks);
            Report("預設模式", fusedRanks);

            // 逐題名次。彙總數字只說得出「誰比較好」，說不出「壞在哪一類提問」——
            // 而後者才決定要不要保留某個通道。
            _out.WriteLine("");
            _out.WriteLine("提問                                     詞彙  語意   RRF  預設");
            for (var i = 0; i < Bank.Length; i++)
                if (fusedRanks[i] != 1 || semanticRanks[i] != 1 || lexicalRanks[i] != 1 || rrfRanks[i] != 1)
                    _out.WriteLine(
                        $"{Bank[i].Question,-40} {lexicalRanks[i],4} {semanticRanks[i],5} " +
                        $"{rrfRanks[i],5} {fusedRanks[i],5}");
            _out.WriteLine($"正解的語意分數    {Describe(relevantSemantic)}");
            _out.WriteLine($"離題提問的最高語意分數 {Describe(irrelevantSemantic)}");
            foreach (var line in outOfScopeDetail) _out.WriteLine(line);
            _out.WriteLine(
                relevantSemantic.Count > 0 && irrelevantSemantic.Count > 0
                    ? $"正解最低 {relevantSemantic.Min():F4} vs 離題最高 {irrelevantSemantic.Max():F4} → " +
                      (relevantSemantic.Min() > irrelevantSemantic.Max()
                          ? "兩群分離，§10.8 的門檻可訂在兩者之間"
                          : "兩群仍重疊，語意分數也訂不出安全門檻")
                    : "樣本不足");

            // 唯一的硬性斷言：融合不得比純詞彙差。
            // **不對「融合要好多少」設門檻**——那個數字現在還沒有依據，
            // 硬設一個只會逼未來的人去調它。
            var fusedTop1 = fusedRanks.Count(r => r == 1);
            var semanticTop1 = semanticRanks.Count(r => r == 1);
            var lexicalTop1 = lexicalRanks.Count(r => r == 1);

            Assert.True(fusedTop1 >= lexicalTop1,
                $"預設模式的 Recall@1（{fusedTop1}）比純詞彙（{lexicalTop1}）還差，接語意通道沒有帶來好處。");

            // 預設模式就是「語意為主」，理應與純語意同分。真的掉下去代表補位邏輯
            // 把詞彙的名次帶進了前段——那正是 2026-08-06 改掉 RRF 預設的原因，不該重演。
            Assert.True(fusedTop1 >= semanticTop1,
                $"預設模式的 Recall@1（{fusedTop1}）低於純語意（{semanticTop1}），詞彙通道又擠進前段了。");
        }

        private static int RankOf(IReadOnlyList<DocSearchHit> hits, string url)
        {
            var idx = hits.ToList().FindIndex(h => h.DocUrl == url);
            return idx < 0 ? 0 : idx + 1;
        }

        private void Report(string label, List<int> ranks)
        {
            var n = ranks.Count;
            var top1 = ranks.Count(r => r == 1);
            var top3 = ranks.Count(r => r is >= 1 and <= 3);
            var top5 = ranks.Count(r => r is >= 1 and <= 5);
            var mrr = ranks.Where(r => r > 0).Sum(r => 1.0 / r) / n;
            _out.WriteLine(
                $"{label,-8} Recall@1={top1}/{n} @3={top3}/{n} @5={top5}/{n} MRR={mrr:F3}");
        }

        private static string Describe(List<double> xs)
        {
            if (xs.Count == 0) return "（無）";
            var s = xs.OrderBy(x => x).ToList();
            return $"n={s.Count} min={s[0]:F4} 中位={s[s.Count / 2]:F4} max={s[^1]:F4}";
        }

        private async Task<IReadOnlyList<DocSearchHit>> SearchAsync(
            string question, int topK, bool lexicalOnly = false, bool useRrf = false)
        {
            using var scope = _app.Services.CreateScope();

            // lexicalOnly 用一個「沒有設定模型」的 client 讓語意通道自行退場——
            // 這正是正式環境未設定嵌入模型時走的同一條路徑，不是測試專用的旁路。
            var embedding = lexicalOnly
                ? NoEmbeddingClient.Instance
                : scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();

            // 合併模式由組態決定，這裡直接換一份組態就能量另一種模式——
            // 不是走測試專用的旁路，量到的就是正式環境那條路。
            var configuration = useRrf
                ? new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    { [ManualSearchService.RetrievalModeKey] = ManualSearchService.ModeRrf }).Build()
                : scope.ServiceProvider.GetRequiredService<IConfiguration>();

            var service = new ManualSearchService(
                scope.ServiceProvider.GetRequiredService<QaDbContext>(),
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                new BenchmarkUserContext(),
                embedding,
                scope.ServiceProvider.GetRequiredService<IDocVectorStore>(),
                configuration,
                scope.ServiceProvider.GetRequiredService<ILogger<ManualSearchService>>());
            return await service.SearchAsync(question, topK);
        }

        /// <summary>可讀的選單鍵／模組——與 <see cref="GrantAllAsync"/> 授出的一致。</summary>
        private static string[] AllKeys => Bank
            .Select(b => HelpDocResolver.ResolveController(b.ExpectedFile))
            .Where(k => k != null).Select(k => k!).Distinct().ToArray();

        private static string[] AllModules => AllKeys
            .Select(HelpDocResolver.ModuleOf)
            .Where(m => !string.IsNullOrEmpty(m)).Select(m => m!).Distinct().ToArray();

        /// <summary>未設定嵌入模型的 client。<c>ManualSearchService</c> 見到空名稱就不走語意通道。</summary>
        private sealed class NoEmbeddingClient : IEmbeddingClient
        {
            public static readonly NoEmbeddingClient Instance = new();

            public string EmbeddingModelName => string.Empty;

            public Task<IReadOnlyList<float[]>> EmbedAsync(
                IReadOnlyList<string> inputs, EmbeddingKind kind, CancellationToken ct = default) =>
                throw new InvalidOperationException("純詞彙模式不應呼叫嵌入。");
        }

        /// <summary>評測要量的是檢索品質，故給予全部讀取權限，把權限變因排除在外。</summary>
        private static async Task GrantAllAsync(ApplicationDbContext db)
        {
            var keys = Bank
                .Select(b => HelpDocResolver.ResolveController(b.ExpectedFile))
                .Where(k => k != null)
                .Select(k => k!)
                .Distinct()
                .ToList();

            var existing = await db.MenuPermissions.Where(p => p.RoleId == RoleId).ToListAsync();
            foreach (var p in existing) p.CanRead = true;

            foreach (var key in keys)
            {
                if (existing.Any(p => p.MenuKey == key)) continue;
                db.MenuPermissions.Add(new MenuPermission { RoleId = RoleId, MenuKey = key, CanRead = true });
            }

            await db.SaveChangesAsync();
        }

        private sealed class BenchmarkUserContext : IUserContext
        {
            public string? ActiveRoleId => RoleId;
            public string? PlantId => "P1";
            public string? TimeZoneId => "Asia/Taipei";
            public string? Language => "zh-TW";
            public string? UserId => "admin";
            public string? ActiveRolePath => null;
            public IReadOnlyList<string> AllowedPlantIds => Array.Empty<string>();
        }
    }
}
