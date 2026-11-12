using Xunit;

namespace SmartFactory.MES.Tests.Conventions
{
    /// <summary>
    /// 掃描器基礎建設自身的測試。
    ///
    /// 為什麼需要：R22 是「去註解後再比對原始碼樣式」的規則，
    /// <see cref="ConventionScan.StripComments"/> 一旦壞掉，症狀不是紅燈而是
    /// **靜默漏報**——被吃掉的程式碼區段裡的違規再也掃不到，規則看起來永遠是綠的。
    /// 掃描器的正確性只能由它自己的測試守。
    /// </summary>
    public class ConventionScanTests
    {
        [Fact]
        public void StripComments_RemovesLineAndBlockComments()
        {
            var src = """
                var a = 1;          // DateTime.Today
                /* DateTime.Today */
                var b = 2;
                """;

            var stripped = ConventionScan.StripComments(src);

            Assert.DoesNotContain("DateTime.Today", stripped);
            Assert.Contains("var a = 1;", stripped);
            Assert.Contains("var b = 2;", stripped);
        }

        [Fact]
        public void StripComments_KeepsCodeAfterUrlInStringLiteral()
        {
            // 天真的 `//` 掃法會從 https 的雙斜線一路吃到行尾，
            // 把同一行後面的真違規靜默吞掉。
            var src = """var url = "https://example.com"; var d = DateTime.Today;""";

            var stripped = ConventionScan.StripComments(src);

            Assert.Contains("DateTime.Today", stripped);
        }

        [Fact]
        public void StripComments_KeepsCommentMarkersInsideStringLiterals()
        {
            var src = """var s = "/* not a comment */"; var d = DateTime.Today;""";

            var stripped = ConventionScan.StripComments(src);

            Assert.Contains("DateTime.Today", stripped);
        }

        [Fact]
        public void StripComments_HandlesVerbatimStringWithEscapedQuote()
        {
            // 逐字字串內的 "" 是跳脫引號而非字串結尾；判錯會讓解析從此錯位，
            // 後續整份檔案的比對都不可信。
            var src = "var s = @\"say \"\"hi\"\"\"; var d = DateTime.Today; // x";

            var stripped = ConventionScan.StripComments(src);

            Assert.Contains("DateTime.Today", stripped);
            Assert.DoesNotContain("// x", stripped);
        }

        [Fact]
        public void StripComments_PreservesLineCount()
        {
            var src = "line1 // c\nline2\n/* multi\nline */line3\n";

            var stripped = ConventionScan.StripComments(src);

            Assert.Equal(src.Count(c => c == '\n'), stripped.Count(c => c == '\n'));
        }
    }
}
