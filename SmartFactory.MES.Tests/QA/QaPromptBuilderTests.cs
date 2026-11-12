using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QA
{
    /// <summary>
    /// <see cref="QaPromptBuilder"/> 的守門測試（規格 §10.8）。
    ///
    /// <para><b>這裡守的是「提示詞與解析器講的是同一套格式」。</b>
    /// 兩邊各自看起來都對、但格式對不上，症狀是**答案品質指標無聲下降**：
    /// 引文解析不出來就記成「沒附來源」，而模型其實有附。
    /// 這種壞法不會有例外、不會有紅燈，只會讓待決 #5 的選型比到假數據。</para>
    /// </summary>
    public class QaPromptBuilderTests
    {
        private static DocSearchHit Hit(int rank, string code, string heading, string text) =>
            new(rank, rank, "zh", heading, text,
                QaPromptBuilder.DocRoot + code, "MENU", Array.Empty<string>(), 1.0 / rank);

        // ── 提示詞與解析器的格式必須是同一套 ──────────────────────────────

        [Fact]
        public void 系統提示詞必須內含拒答句與來源前綴這兩個常數()
        {
            // 常數改了、提示詞沒跟著改（或反過來）＝解析器與模型講不同的格式。
            Assert.Contains(QaPromptBuilder.NoAnswer, QaPromptBuilder.SystemPrompt);
            Assert.Contains(QaPromptBuilder.CitationPrefix, QaPromptBuilder.SystemPrompt);
        }

        [Fact]
        public void 提示詞裡的頁面代號要能被自己的解析器讀回來()
        {
            var prompt = QaPromptBuilder.BuildUserPrompt(
                "呆滯天數怎麼算", new[] { Hit(1, "inv/slow-moving-inventory.html", "庫存 › 呆滯料", "內容") });

            Assert.Contains("[inv/slow-moving-inventory.html]", prompt);

            // 模型照提示詞第 5 條「原樣抄寫中括號裡的字」回覆時，解析器要拿得到同一個字串。
            var parsed = QaPromptBuilder.Parse(
                $"呆滯天數＝最後異動日到今天。\n{QaPromptBuilder.CitationPrefix}inv/slow-moving-inventory.html");
            Assert.Equal(new[] { "inv/slow-moving-inventory.html" }, parsed.Citations);
        }

        // ── 組 prompt ────────────────────────────────────────────────────

        [Fact]
        public void 零命中不得組出假的參考資料()
        {
            var prompt = QaPromptBuilder.BuildUserPrompt("今天午餐吃什麼", Array.Empty<DocSearchHit>());

            Assert.Contains("（無）", prompt);
            Assert.Contains("今天午餐吃什麼", prompt);
            Assert.DoesNotContain("[", prompt);
        }

        [Fact]
        public void 超過字數預算時整塊丟掉而不是截斷內容()
        {
            var hits = new[]
            {
                Hit(1, "a.html", "A", new string('甲', 400)),
                Hit(2, "b.html", "B", new string('乙', 400)),
                Hit(3, "c.html", "C", new string('丙', 400)),
            };

            var prompt = QaPromptBuilder.BuildUserPrompt("問題", hits, contextBudget: 900);

            Assert.Contains("[a.html]", prompt);
            Assert.Contains("[b.html]", prompt);
            Assert.DoesNotContain("[c.html]", prompt);

            // 留下來的兩塊都必須是完整的：半塊內容會讓模型引用到一段被切掉結論的規則。
            Assert.Contains(new string('甲', 400), prompt);
            Assert.Contains(new string('乙', 400), prompt);
        }

        [Fact]
        public void 塞不下的那塊之後仍要繼續試更小的塊()
        {
            // 遇到第一個放不下就 break 的話，一塊 2000 字的長塊會把它後面
            // 所有短塊一起擋掉——被丟掉的是名次更後但**內容更精準**的塊。
            var hits = new[]
            {
                Hit(1, "a.html", "A", new string('甲', 400)),
                Hit(2, "big.html", "B", new string('乙', 5000)),
                Hit(3, "c.html", "C", new string('丙', 100)),
            };

            var prompt = QaPromptBuilder.BuildUserPrompt("問題", hits, contextBudget: 900);

            Assert.Contains("[a.html]", prompt);
            Assert.DoesNotContain("[big.html]", prompt);
            Assert.Contains("[c.html]", prompt);
        }

        [Fact]
        public void 第一塊即使超出預算也要留著()
        {
            // 否則預算設小一點就會組出「參考資料：」後面空無一物的 prompt，
            // 而那與零命中是完全不同的情況，模型會拿到一個自相矛盾的指令。
            var prompt = QaPromptBuilder.BuildUserPrompt(
                "問題", new[] { Hit(1, "a.html", "A", new string('甲', 9000)) }, contextBudget: 100);

            Assert.Contains("[a.html]", prompt);
        }

        [Fact]
        public void 挑進預算的那幾塊必須與提示詞裡真的出現的完全一致()
        {
            // 呼叫端拿 SelectWithinBudget 當「模型看到了哪幾頁」的依據——
            // 兩者若對不上，被預算丟掉的頁會被算成「有給模型看」，
            // 於是模型引不到就變成模型的失分，而它根本沒看到那一頁。
            var hits = new[]
            {
                Hit(1, "a.html", "A", new string('甲', 400)),
                Hit(2, "big.html", "B", new string('乙', 5000)),
                Hit(3, "c.html", "C", new string('丙', 100)),
            };

            var prompt = QaPromptBuilder.BuildUserPrompt("問題", hits, contextBudget: 900);
            var kept = QaPromptBuilder.SelectWithinBudget(hits, contextBudget: 900);

            foreach (var h in hits)
            {
                var code = QaPromptBuilder.PageCode(h.DocUrl);
                var inPrompt = prompt.Contains($"[{code}]");
                var inKept = kept.Any(k => QaPromptBuilder.PageCode(k.DocUrl) == code);
                Assert.Equal(inPrompt, inKept);
            }
        }

        [Theory]
        [InlineData("/docs/user/inv/stock-ledger.html", "inv/stock-ledger.html")]
        [InlineData("/docs/user/index.html", "index.html")]
        [InlineData("https://example.com/other.html", "https://example.com/other.html")]
        public void 頁面代號是去掉手冊根路徑後的相對路徑(string url, string expected) =>
            Assert.Equal(expected, QaPromptBuilder.PageCode(url));

        // ── 解析答案 ─────────────────────────────────────────────────────

        [Fact]
        public void 多頁來源以頓號分隔()
        {
            var parsed = QaPromptBuilder.Parse(
                "先詢價再開委外採購單。\n來源：osp/outsource-quotation.html、osp/outsource-purchase-order.html");

            Assert.Equal(
                new[] { "osp/outsource-quotation.html", "osp/outsource-purchase-order.html" },
                parsed.Citations);
            Assert.Equal("先詢價再開委外採購單。", parsed.Body);
        }

        [Theory]
        [InlineData("來源：a.html")]
        [InlineData("來源:a.html")]
        [InlineData("來源：[a.html]")]
        [InlineData("來源：`a.html`")]
        [InlineData("  來源：a.html")]
        [InlineData("來源：a.html。")]
        public void 來源行的常見變體都要讀得出來(string citationLine)
        {
            // 模型不會每次都輸出一模一樣的標點。這些變體全是真的會出現的，
            // 解析不出來就會被記成「沒附來源」——一個由解析器造成的假失分。
            var parsed = QaPromptBuilder.Parse($"答案。\n{citationLine}");
            Assert.Equal(new[] { "a.html" }, parsed.Citations);
        }

        [Fact]
        public void 同一份答案出現兩次來源行時取最後一個()
        {
            var parsed = QaPromptBuilder.Parse("來源：wrong.html\n答案在這裡。\n來源：right.html");
            Assert.Equal(new[] { "right.html" }, parsed.Citations);
        }

        [Fact]
        public void 重複的頁面代號要去重()
        {
            var parsed = QaPromptBuilder.Parse("答案。\n來源：a.html、a.html、b.html");
            Assert.Equal(new[] { "a.html", "b.html" }, parsed.Citations);
        }

        [Fact]
        public void 沒有來源行時不得自行補一個()
        {
            var parsed = QaPromptBuilder.Parse("這是一段沒有附來源的答案。");

            Assert.Empty(parsed.Citations);
            Assert.False(parsed.IsNoAnswer);
            Assert.Equal("這是一段沒有附來源的答案。", parsed.Body);
        }

        [Fact]
        public void 拒答句前面多幾個字仍算拒答()
        {
            // 「很抱歉，使用手冊中查無相關說明。」仍然是正確的拒答行為。
            // 要求完全相等的話，模型會因為多了兩個字被記成幻覺——那是量錯了東西。
            var parsed = QaPromptBuilder.Parse($"很抱歉，{QaPromptBuilder.NoAnswer}");
            Assert.True(parsed.IsNoAnswer);
        }

        [Fact]
        public void 拒答與沒附來源是兩件事()
        {
            var refused = QaPromptBuilder.Parse(QaPromptBuilder.NoAnswer);
            var answered = QaPromptBuilder.Parse("這裡有一段答案。");

            Assert.True(refused.IsNoAnswer);
            Assert.Empty(refused.Citations);

            Assert.False(answered.IsNoAnswer);
            Assert.Empty(answered.Citations);
        }

        [Fact]
        public void 空回覆不得炸掉解析器()
        {
            // ChatAsync 已經把空回覆轉成例外，但解析器是公開 API，
            // 不能假設呼叫端一定走過那條路。
            var parsed = QaPromptBuilder.Parse("   ");

            Assert.Equal(string.Empty, parsed.Body);
            Assert.Empty(parsed.Citations);
            Assert.False(parsed.IsNoAnswer);
        }
    }
}
