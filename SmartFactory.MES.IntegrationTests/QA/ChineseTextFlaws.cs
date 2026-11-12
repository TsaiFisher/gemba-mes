using System.Text;

namespace SmartFactory.MES.IntegrationTests.QA
{
    /// <summary>
    /// 答案的中文用語檢查（規格 §10.8.2 之三）。**評測工具，不是生產程式。**
    ///
    /// <para><b>這個檔案存在的理由是它出過錯。</b>2026-08-07 首輪六顆候選實測時，
    /// 「非中文作答」這一項用的是「漢字數 × 4 &lt; 總長度」——於是
    /// 「庫齡固定分成幾段」這一題**在五顆模型裡有四顆被判非中文作答**，
    /// 而該題的正確答案本來就是
    /// <c>0–30、31–60、61–90、91–180、181–360、360+ 六段</c>：整句幾乎都是數字。
    /// 同一題跨模型全中，原因在資料不在模型。</para>
    ///
    /// <para>規格 §10.8.2 自己寫著「**一個會誤報的品質指標比沒有指標更糟**」，
    /// 而那句話底下就掛著一個會誤報的指標。改法是把分母從「總長度」換成
    /// <b>拉丁字母數</b>——滿是數字的答案不是英文答案。</para>
    /// </summary>
    internal static class ChineseTextFlaws
    {
        /// <summary>
        /// 只列**簡體專用**的字。
        ///
        /// <para><b>刻意排除 后／干／只／里／系／制／于／采 這一類</b>——它們在繁體中同樣合法
        /// （皇后、干擾、只有、公里、關係、制度、于歸、采風），列進來會把正常的繁體回答判成簡體。</para>
        /// </summary>
        private const string SimplifiedOnly =
            "们为这个说时现实学会员单据报数项价计产质检验购库号变换击选择开关闭问题级类记录设备护费财务应账龄营业输图签编辑删执运销归结构规则码扫细视转载处发车与从众无电权认让请该讲论议显";

        private static readonly (string Term, string Better)[] MainlandTerms =
        {
            ("質量", "品質"), ("信息", "資訊"), ("默認", "預設"), ("視頻", "影片"),
            ("屏幕", "螢幕"), ("菜單", "選單"), ("界面", "介面"), ("登錄", "登入"),
            ("內存", "記憶體"), ("軟件", "軟體"), ("硬件", "硬體"), ("打印", "列印"),
            ("激活", "啟用"), ("缺省", "預設"), ("鼠標", "滑鼠"), ("保存", "儲存"),
        };

        /// <summary>
        /// 判定為「非中文作答」的最少拉丁字母數。
        /// <c>OEE</c>、<c>MRP</c>、<c>AVCO</c> 這類專有名詞本來就是英文，不該因此觸發。
        /// </summary>
        private const int MinLatinToJudge = 24;

        /// <summary>回傳空字串代表沒問題；否則是給人看的一句話。</summary>
        public static string Describe(string body)
        {
            if (string.IsNullOrEmpty(body)) return string.Empty;

            var sb = new StringBuilder();

            var simplified = body.Where(c => SimplifiedOnly.Contains(c)).Distinct().ToArray();
            if (simplified.Length > 0)
                sb.Append("簡體字 ").Append(new string(simplified));

            var terms = MainlandTerms.Where(t => body.Contains(t.Term, StringComparison.Ordinal)).ToArray();
            if (terms.Length > 0)
            {
                if (sb.Length > 0) sb.Append('／');
                sb.Append("用語 ").Append(string.Join("、", terms.Select(t => $"{t.Term}→{t.Better}")));
            }

            // 中文提問卻用英文作答。**分母是拉丁字母而不是總長度**——
            // 用總長度的話，「六段：0–30、31–60、61–90…」這種正確又精簡的答案會被判成英文。
            var han = body.Count(IsHan);
            var latin = body.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
            if (latin >= MinLatinToJudge && latin > han * 3)
            {
                if (sb.Length > 0) sb.Append('／');
                sb.Append($"非中文作答（漢字 {han}／拉丁字母 {latin}）");
            }

            return sb.ToString();
        }

        private static bool IsHan(char c) => c is >= '一' and <= '鿿';
    }
}
