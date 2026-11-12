using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QA
{
    /// <summary>
    /// <see cref="ManualDocParser"/> 對**合成 HTML** 的行為測試（規格 §10.2）。
    ///
    /// 與 <see cref="ManualCorpusFacts"/> 分工：那邊守的是「真實 134 頁語料現在是什麼樣子」，
    /// 這邊守的是「餵進特定形狀時解析器該怎麼做」——包括真實語料目前只有一個實例、
    /// 或根本還沒出現、但下次改版就會出現的形狀。
    /// </summary>
    public class ManualDocParserTests
    {
        private readonly ManualDocParser _parser = new();

        /// <summary>預設：所有靜態檔都存在。要測 fallback 的案例自行改傳。</summary>
        private static bool AllExist(string _) => true;

        private IReadOnlyList<ParsedChunk> Parse(string bodyHtml, Func<string, bool>? exists = null) =>
            _parser.Parse("/docs/user/ord", Wrap(bodyHtml), exists ?? AllExist);

        /// <summary>套上真實手冊的外殼：navbar ＋ sidebar ＋ main.content。</summary>
        private static string Wrap(string mainInner) => $$"""
            <!DOCTYPE html>
            <html lang="zh-TW" data-doc-lang="zh">
            <head><title>t</title><script>var x = 1;</script></head>
            <body>
              <nav class="navbar"><span lang="zh">使用手冊首頁</span><span lang="en">Manual home</span></nav>
              <div class="d-flex">
                <nav class="sidebar"><a href="x.html"><span lang="zh">銷售預測</span><span lang="en">Forecast</span></a></nav>
                <main class="content">{{mainInner}}</main>
              </div>
            </body></html>
            """;

        private static string Zh(IEnumerable<ParsedChunk> cs) =>
            string.Join("\n", cs.Where(c => c.Lang == "zh").Select(c => c.Text));

        private static string En(IEnumerable<ParsedChunk> cs) =>
            string.Join("\n", cs.Where(c => c.Lang == "en").Select(c => c.Text));

        private static string Filler(int chars) => new('壹', chars);

        // ── 雙語拆分（§10.2-0） ──────────────────────────────────────────

        [Fact]
        public void 中英同頁拆成兩組而非交錯成一串()
        {
            var chunks = Parse("""
                <h1><span lang="zh">交期達成率</span><span lang="en">Delivery Rate</span></h1>
                <p><span lang="zh">統計準時達成率。</span><span lang="en">Measures the on-time rate.</span></p>
                """);

            // 這是 §10.2-0 的整個重點：display:none 對 TextContent 沒有作用，
            // 不拆語系會得到「交期達成率Delivery Rate統計準時達成率。Measures…」。
            Assert.Equal("統計準時達成率。", Zh(chunks));
            Assert.Equal("Measures the on-time rate.", En(chunks));
        }

        [Fact]
        public void 麵包屑用各自語系的標題()
        {
            var chunks = Parse("""
                <h1><span lang="zh">交期達成率</span><span lang="en">Delivery Rate</span></h1>
                <p><span lang="zh">內容</span><span lang="en">Body</span></p>
                """);

            Assert.Equal("交期達成率", chunks.First(c => c.Lang == "zh").HeadingPath);
            Assert.Equal("Delivery Rate", chunks.First(c => c.Lang == "en").HeadingPath);
        }

        // ── 切塊邊界（§10.2-1） ──────────────────────────────────────────

        [Fact]
        public void 巢狀在div裡的h2一樣是切點()
        {
            // 實測 134 頁 492 個 h2 中，sys/index.html 有 1 個包在 <div> 裡。
            // 只看 main 的直屬子元素會漏掉它，那一段就併進前一塊——
            // 頁數不變、總字數不變，沒有任何指標會顯示異常。
            var chunks = Parse($$"""
                <h1><span lang="zh">系統管理</span><span lang="en">System</span></h1>
                <p><span lang="zh">{{Filler(1200)}}</span><span lang="en">{{Filler(1200)}}</span></p>
                <div class="card">
                  <h2><span lang="zh">系統參數</span><span lang="en">System Parameters</span></h2>
                  <p><span lang="zh">{{Filler(1200)}}</span><span lang="en">{{Filler(1200)}}</span></p>
                </div>
                """);

            var zh = chunks.Where(c => c.Lang == "zh").ToList();
            Assert.Equal(2, zh.Count);
            Assert.Equal("系統管理 › 系統參數", zh[1].HeadingPath);
        }

        [Fact]
        public void h2的文字歸標題不歸內文()
        {
            var chunks = Parse($$"""
                <h1><span lang="zh">交期達成率</span><span lang="en">Delivery Rate</span></h1>
                <p><span lang="zh">{{Filler(1200)}}</span><span lang="en">{{Filler(1200)}}</span></p>
                <h2><i class="bi bi-table"></i><span lang="zh">欄位說明</span><span lang="en">Field reference</span></h2>
                <p><span lang="zh">{{Filler(1200)}}</span><span lang="en">{{Filler(1200)}}</span></p>
                """);

            var second = chunks.Where(c => c.Lang == "zh").ElementAt(1);

            // h2 內含 <i class="bi…"> 圖示元素——用 regex 抓會得到空標題（§10.2-3）。
            Assert.Equal("交期達成率 › 欄位說明", second.HeadingPath);
            Assert.DoesNotContain("欄位說明", second.Text);
        }

        [Fact]
        public void 小頁面整頁一塊()
        {
            var chunks = Parse("""
                <h1><span lang="zh">交期達成率</span><span lang="en">Delivery Rate</span></h1>
                <p><span lang="zh">短短一段。</span><span lang="en">Short.</span></p>
                <h2><span lang="zh">查詢</span><span lang="en">Search</span></h2>
                <p><span lang="zh">點查詢按鈕。</span><span lang="en">Click search.</span></p>
                """);

            // §10.2-1：多數頁面本來就該是一塊。切開會讓「點查詢按鈕」失去所屬畫面的上下文。
            Assert.Single(chunks.Where(c => c.Lang == "zh"));

            // 併塊時各段標題仍要保留——專有名詞多半在標題裡。
            Assert.Contains("查詢", Zh(chunks));
            Assert.Contains("點查詢按鈕。", Zh(chunks));
        }

        [Fact]
        public void 大頁面依h2切開()
        {
            var chunks = Parse($$"""
                <h1><span lang="zh">交期達成率</span><span lang="en">Delivery Rate</span></h1>
                <p><span lang="zh">{{Filler(1500)}}</span><span lang="en">{{Filler(1500)}}</span></p>
                <h2><span lang="zh">查詢</span><span lang="en">Search</span></h2>
                <p><span lang="zh">{{Filler(1500)}}</span><span lang="en">{{Filler(1500)}}</span></p>
                """);

            Assert.Equal(2, chunks.Count(c => c.Lang == "zh"));
            Assert.Equal(2, chunks.Count(c => c.Lang == "en"));
        }

        [Fact]
        public void 每個語系的塊索引各自從零起算()
        {
            var chunks = Parse($$"""
                <h1><span lang="zh">標題</span><span lang="en">Title</span></h1>
                <p><span lang="zh">{{Filler(1500)}}</span><span lang="en">{{Filler(1500)}}</span></p>
                <h2><span lang="zh">區段</span><span lang="en">Section</span></h2>
                <p><span lang="zh">{{Filler(1500)}}</span><span lang="en">{{Filler(1500)}}</span></p>
                """);

            Assert.Equal(new[] { 0, 1 }, chunks.Where(c => c.Lang == "zh").Select(c => c.ChunkIndex));
            Assert.Equal(new[] { 0, 1 }, chunks.Where(c => c.Lang == "en").Select(c => c.ChunkIndex));
        }

        // ── 文字正規化（§10.2-4） ────────────────────────────────────────

        [Fact]
        public void 表格相鄰儲存格之間有分隔()
        {
            var chunks = Parse("""
                <h1><span lang="zh">標題</span><span lang="en">Title</span></h1>
                <table><tbody>
                  <tr><td><span lang="zh">客戶代號</span><span lang="en">Customer</span></td>
                      <td><span lang="zh">只看單一客戶</span><span lang="en">One customer only</span></td></tr>
                </tbody></table>
                """);

            // 不補分隔的話會黏成「客戶代號只看單一客戶」，雙字切詞就產生「號只」這種
            // 橫跨兩格、實際上不存在的詞。283 張欄位說明表全部受影響。
            Assert.Contains("客戶代號 | 只看單一客戶", Zh(chunks));
            Assert.DoesNotContain("客戶代號只看單一客戶", Zh(chunks));
        }

        [Fact]
        public void 相鄰段落不會黏成一句()
        {
            var chunks = Parse("""
                <h1><span lang="zh">標題</span><span lang="en">Title</span></h1>
                <p><span lang="zh">第一段。</span><span lang="en">First.</span></p>
                <p><span lang="zh">第二段。</span><span lang="en">Second.</span></p>
                """);

            Assert.DoesNotContain("第一段。第二段。", Zh(chunks));
        }

        [Fact]
        public void 不斷行空白與連續空白收成單一空白()
        {
            var chunks = Parse("""
                <h1><span lang="zh">標題</span><span lang="en">Title</span></h1>
                <p><span lang="zh">路由：/DeliveryRate &nbsp;|&nbsp; 準時率</span><span lang="en">Route</span></p>
                """);

            Assert.Contains("路由：/DeliveryRate | 準時率", Zh(chunks));
        }

        [Fact]
        public void script與style不進索引()
        {
            var chunks = Parse("""
                <h1><span lang="zh">標題</span><span lang="en">Title</span></h1>
                <style>.x{color:red}</style>
                <script>var secret = 1;</script>
                <p><span lang="zh">內容</span><span lang="en">Body</span></p>
                """);

            Assert.DoesNotContain("secret", Zh(chunks));
            Assert.DoesNotContain("color:red", Zh(chunks));
        }

        [Fact]
        public void 側欄與導覽列在main之外故不進索引()
        {
            var chunks = Parse("""
                <h1><span lang="zh">標題</span><span lang="en">Title</span></h1>
                <p><span lang="zh">內容</span><span lang="en">Body</span></p>
                """);

            // sidebar 是整個模組的畫面清單、每頁都一樣。納進來的話同模組每塊 chunk 都彼此包含，
            // 檢索時分不出高下——而且每一塊看起來都「有命中」，比漏掉更難察覺。
            Assert.DoesNotContain("銷售預測", Zh(chunks));
            Assert.DoesNotContain("使用手冊首頁", Zh(chunks));
        }

        // ── 截圖路徑（§10.2-5） ──────────────────────────────────────────

        [Fact]
        public void 中文塊用原圖_英文塊用data_src_en()
        {
            var chunks = Parse("""
                <h1><span lang="zh">標題</span><span lang="en">Title</span></h1>
                <img src="img/x.png" data-src-en="img/en/x.png" alt="a">
                """);

            Assert.Equal(new[] { "/docs/user/ord/img/x.png" },
                chunks.Single(c => c.Lang == "zh").ImagePaths);
            Assert.Equal(new[] { "/docs/user/ord/img/en/x.png" },
                chunks.Single(c => c.Lang == "en").ImagePaths);
        }

        [Fact]
        public void 英文截圖缺漏時退回中文截圖()
        {
            // 頁面本身靠 <img onerror> 退回中文圖；索引若存一個不存在的路徑，
            // 答覆會出現破圖，而破圖不會進任何日誌。
            var chunks = Parse("""
                <h1><span lang="zh">標題</span><span lang="en">Title</span></h1>
                <img src="img/x.png" data-src-en="img/en/x.png">
                """,
                exists: path => !path.Contains("/en/"));

            Assert.Equal(new[] { "/docs/user/ord/img/x.png" },
                chunks.Single(c => c.Lang == "en").ImagePaths);
        }

        [Fact]
        public void 截圖歸屬於它所在的區段()
        {
            var chunks = Parse($$"""
                <h1><span lang="zh">標題</span><span lang="en">Title</span></h1>
                <p><span lang="zh">{{Filler(1500)}}</span><span lang="en">{{Filler(1500)}}</span></p>
                <h2><span lang="zh">區段</span><span lang="en">Section</span></h2>
                <img src="img/second.png" data-src-en="img/en/second.png">
                <p><span lang="zh">{{Filler(1500)}}</span><span lang="en">{{Filler(1500)}}</span></p>
                """);

            var zh = chunks.Where(c => c.Lang == "zh").ToList();
            Assert.Empty(zh[0].ImagePaths);
            Assert.Equal(new[] { "/docs/user/ord/img/second.png" }, zh[1].ImagePaths);
        }

        // ── 錯誤處理 ─────────────────────────────────────────────────────

        [Fact]
        public void 缺少main_content時出聲而不是靜默略過()
        {
            // 靜默跳過一頁 = 該畫面在 Q&A 中永久消失，且沒有任何訊號。
            var ex = Assert.Throws<InvalidOperationException>(() =>
                _parser.Parse("/docs/user/ord", "<html><body><p>x</p></body></html>", AllExist));

            Assert.Contains("/docs/user/ord", ex.Message);
        }

        [Fact]
        public void 未提供靜態檔探測時直接擲例外()
        {
            // 預設成「一律存在」會讓英文截圖缺漏靜默寫進索引，所以這個參數沒有預設值。
            Assert.Throws<ArgumentNullException>(() =>
                _parser.Parse("/docs/user/ord", Wrap("<h1>x</h1>"), null!));
        }
    }
}
