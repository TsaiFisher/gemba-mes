using SmartFactory.MES.Modules.QA.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QA
{
    /// <summary>
    /// <see cref="CjkTokenizer"/> 的守門（規格 §10.5）。
    ///
    /// 這一組測試的價值不在「函式有沒有照規格切字」，而在於**它保護的是一個沉默的失敗**：
    /// 切詞一旦壞掉（少切一個位置、大小寫不一致、標點沒當成分隔符），中文提問的詞彙通道
    /// 只會回 0 筆，不會拋例外、不會有日誌。整個 RAG 看起來仍然「能用」，只是答得比較差。
    ///
    /// 對應的 PostgreSQL 端行為在 <c>SmartFactory.MES.IntegrationTests</c> 另有實測守門——
    /// 本檔只驗純函式，不碰資料庫。
    /// </summary>
    public class CjkTokenizerTests
    {
        // ── 核心行為：重疊雙字 ────────────────────────────────────────────

        [Fact]
        public void 中文切成重疊雙字()
        {
            Assert.Equal("銷售 售排 排名", CjkTokenizer.Tokenize("銷售排名"));
        }

        [Fact]
        public void 重疊而非切段_中段詞才查得到()
        {
            var tokens = CjkTokenizer.Tokenize("銷售排名報表").Split(' ');

            // 若切成不重疊的「銷售 排名 報表」，查「售排」「名報」會漏；
            // 更關鍵的是使用者輸入的斷詞位置與索引不同時整句都會落空。
            Assert.Equal(new[] { "銷售", "售排", "排名", "名報", "報表" }, tokens);
        }

        [Theory]
        [InlineData("銷售")]     // 起首
        [InlineData("售排")]     // 跨詞邊界
        [InlineData("排名")]     // 中段
        [InlineData("報表")]     // 結尾
        public void 任一位置的雙字都在索引詞串內(string term)
        {
            var indexed = CjkTokenizer.Tokenize("銷售排名報表可依客戶或品號彙總");
            var query   = CjkTokenizer.Tokenize(term);

            Assert.Contains(query, indexed.Split(' '));
        }

        [Fact]
        public void 長查詢詞切出的每個雙字都必須在索引詞串內()
        {
            // plainto_tsquery 會把多個 token 以 AND 串起來，所以「每一個」都要在。
            var indexed = CjkTokenizer.Tokenize("庫存短缺報表列出低於安全存量的品號").Split(' ');
            var query   = CjkTokenizer.Tokenize("安全存量").Split(' ');

            Assert.All(query, q => Assert.Contains(q, indexed));
        }

        [Fact]
        public void 單一中文字保留原字()
        {
            // 孤立單字（如表格裡的「否」）仍要進得了索引，否則整格內容消失。
            Assert.Equal("否", CjkTokenizer.Tokenize("否"));
        }

        // ── 拉丁字母與數字 ───────────────────────────────────────────────

        [Fact]
        public void 英文轉小寫保留為完整詞()
        {
            Assert.Equal("delivery rate", CjkTokenizer.Tokenize("Delivery Rate"));
        }

        [Fact]
        public void 路由與代號的符號被當成分隔符()
        {
            // 「路由：/DeliveryRate」是每一頁都有的資訊，使用者確實會拿路由來問。
            Assert.Equal("路由 deliveryrate", CjkTokenizer.Tokenize("路由：/DeliveryRate"));
        }

        [Fact]
        public void 數字保留()
        {
            Assert.Equal("iqc 2152", CjkTokenizer.Tokenize("IQC 2152"));
        }

        [Fact]
        public void 中英混雜依邊界各自切()
        {
            Assert.Equal("查詢 詢銷 銷售 salesranking", CjkTokenizer.Tokenize("查詢銷售 SalesRanking"));
        }

        // ── 分隔符與邊界 ─────────────────────────────────────────────────

        [Theory]
        [InlineData("銷售、排名")]      // 全形頓號
        [InlineData("銷售，排名")]      // 全形逗號
        [InlineData("銷售 排名")]       // 半形空白
        [InlineData("銷售（排名）")]    // 全形括號
        public void 標點一律切斷而不併入雙字(string input)
        {
            // 若標點被當成 CJK 字元，會產生「售、」「、排」這種永遠查不到的垃圾 token。
            Assert.Equal("銷售 排名", CjkTokenizer.Tokenize(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("！？—")]
        public void 空輸入與純標點回空字串(string? input)
        {
            Assert.Equal(string.Empty, CjkTokenizer.Tokenize(input));
        }

        [Fact]
        public void 詞之間恰好一個空白且無前後綴空白()
        {
            // to_tsvector 對多餘空白其實寬容，但這裡要守的是「輸出可預測」——
            // 後續若拿詞串做 hash 比對或長度估算，多一個空白就會不一致。
            var s = CjkTokenizer.Tokenize("  銷售  排名  ");
            Assert.Equal("銷售 排名", s);
            Assert.DoesNotContain("  ", s);
        }

        // ── 索引期／查詢期一致性 ─────────────────────────────────────────

        [Fact]
        public void 同一段文字重複切詞結果相同()
        {
            const string text = "以實際出貨日對訂單交期統計準時達成率與平均延遲天數 Delivery Rate";
            Assert.Equal(CjkTokenizer.Tokenize(text), CjkTokenizer.Tokenize(text));
        }

        [Fact]
        public void 詞串再切一次結果不變()
        {
            // 詞串本身再餵一次應該只是把空白重新排列，不會出現新的雙字組合
            // （若 Append 少了空白，第二次切會把相鄰兩個 token 黏成新 token——那是真實的迴歸樣態）。
            var once  = CjkTokenizer.Tokenize("銷售排名");
            var twice = CjkTokenizer.Tokenize(once);
            Assert.Equal(once, twice);
        }

        // ── 語系判定與檢索組態 ───────────────────────────────────────────

        [Theory]
        [InlineData("銷售排名怎麼查", "zh")]
        [InlineData("SalesRanking 怎麼查", "zh")]   // 夾雜英文專有名詞仍是中文提問
        [InlineData("How do I run the sales ranking report?", "en")]
        public void 語系依提問文字判定(string q, string expected)
        {
            Assert.Equal(expected, CjkTokenizer.DetectLang(q));
        }

        [Theory]
        [InlineData("")]
        [InlineData("2152")]
        [InlineData("？？？")]
        public void 判不出語系時回null_由呼叫端以UI語系遞補(string q)
        {
            // 這裡若回一個猜的值（例如一律 "en"），中文使用者輸入「2152」會拿到英文答案，
            // 而且沒有任何地方看得出來那是猜的。
            Assert.Null(CjkTokenizer.DetectLang(q));
        }

        [Theory]
        [InlineData("zh", "simple")]
        [InlineData("en", "english")]
        [InlineData("EN", "english")]
        [InlineData(null, "simple")]
        public void 檢索組態依語系對應(string? lang, string expected)
        {
            // 中文若誤走 english 組態，stemmer 會再處理一次雙字並移除 stop word，
            // 索引期與查詢期只要有一邊走錯就全盤落空。
            Assert.Equal(expected, CjkTokenizer.ConfigFor(lang));
        }
    }
}
