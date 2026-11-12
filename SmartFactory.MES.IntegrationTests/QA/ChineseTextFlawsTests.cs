using Xunit;

namespace SmartFactory.MES.IntegrationTests.QA
{
    /// <summary>
    /// <see cref="ChineseTextFlaws"/> 的守門測試。**不需要資料庫，也不需要推論服務**——
    /// 這是純字串判定，卻是 2026-08-07 唯一一個把六顆候選的分數算錯的東西。
    ///
    /// <para>第一組測試全部是**當天真的誤報過的字串**，不是想像的案例。</para>
    /// </summary>
    public class ChineseTextFlawsTests
    {
        // ── 真的誤報過的：正確答案本來就滿是數字 ────────────────────────

        [Theory]
        [InlineData("六段：0–30、31–60、61–90、91–180、181–360、360+。")]
        [InlineData("固定六段（天）：0-30 / 31-60 / 61-90 / 91-180 / 181-360 / 360+")]
        [InlineData("庫齡區間固定為 0–30、31–60、61–90、91–180、181–360、360+ 共六段。")]
        public void 幾乎都是數字的正確答案不得判成非中文作答(string body) =>
            Assert.DoesNotContain("非中文作答", ChineseTextFlaws.Describe(body));

        [Theory]
        [InlineData("OEE 的計畫時間來自工作日曆。")]
        [InlineData("成本方法支援 AVCO 與 FIFO，於 BAS 設定。")]
        [InlineData("MRP 有最小批量（MOQ），依品號主檔設定。")]
        public void 中文句子裡的英文專有名詞不得判成非中文作答(string body) =>
            Assert.DoesNotContain("非中文作答", ChineseTextFlaws.Describe(body));

        // ── 真的該抓的 ──────────────────────────────────────────────────

        [Fact]
        public void 整段英文作答要抓出來()
        {
            var flaws = ChineseTextFlaws.Describe(
                "The inventory aging report divides stock into six buckets based on the last " +
                "transaction date, and shows the amount and percentage for each bucket.");

            Assert.Contains("非中文作答", flaws);
        }

        [Fact]
        public void 簡體字要抓出來()
        {
            var flaws = ChineseTextFlaws.Describe("请点击查询按钮，报表会显示库存数据。");

            Assert.Contains("簡體字", flaws);
            Assert.DoesNotContain("非中文作答", flaws);   // 是簡體不是英文，別混為一談
        }

        [Fact]
        public void 大陸慣用語要抓出來並給出台灣說法()
        {
            var flaws = ChineseTextFlaws.Describe("請在畫面上填寫信息後點選保存。");

            Assert.Contains("信息→資訊", flaws);
            Assert.Contains("保存→儲存", flaws);
        }

        // ── 不得誤報 ────────────────────────────────────────────────────

        [Theory]
        [InlineData("使用「庫存評估表」依成本方法計算各品號存貨評價金額。")]
        [InlineData("請至「檢驗儀器」畫面設定校正週期。")]
        [InlineData("干擾、皇后、公里、關係、制度、采風、只有——全是合法繁體。")]
        public void 正常的繁體答案不得有任何旗標(string body) =>
            Assert.Equal(string.Empty, ChineseTextFlaws.Describe(body));

        [Fact]
        public void 空字串不得炸掉也不得報錯() =>
            Assert.Equal(string.Empty, ChineseTextFlaws.Describe(string.Empty));
    }
}
