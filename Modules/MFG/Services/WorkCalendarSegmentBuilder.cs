using SmartFactory.MES.Modules.BAS.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 由班別清單建構單日「可用時段」（分鐘 0..1440），已扣午休與班距空檔、合併重疊班別，
    /// 並把跨午夜班別的午夜後尾段以 carry 傳給隔日（mfg-domain.md §產能模型 / WorkCalendarSegment）。
    /// 純函式，無 DB 相依，便於單元測試。
    ///
    /// <para><b>2026-08-26 起也納入加班時段</b>（<c>WorkCalendar.TM_WKO</c>）。
    /// 在此之前這裡是「純班別衍生」，於是排程走步器永遠看不到加班——
    /// 而同一份日曆的 <c>TM_WKO</c> 在別處是算的（<c>WorkCalendarService</c> 的三層產能查詢、
    /// <c>OeeService</c> 的計畫時間、甘特的可用分鐘都是 <c>TM_WKS + TM_WKO</c>）。
    /// <b>結果是同一天的產能有兩個答案</b>：甘特畫得出加班灰帶、OEE 分母含加班，
    /// 但排程只排正班。兩套算法各自都自洽，不一致只在跨模組比對時才看得見，
    /// 所以沒有任何測試抓得到——這是使用者審擷取物時憑直覺抓出來的。</para>
    /// </summary>
    public static class WorkCalendarSegmentBuilder
    {
        /// <summary>一段可用時間（自當日 00:00 起算分鐘）。</summary>
        public readonly record struct Segment(int Start, int End);

        private static int ToMin(TimeOnly t) => t.Hour * 60 + t.Minute;

        /// <summary>
        /// 建構某日可用時段。
        /// </summary>
        /// <param name="shifts">當日排定的班別（StartTime/EndTime 必填才納入；含選填午休 BreakStart/BreakEnd）。</param>
        /// <param name="carryIn">前一日跨午夜帶入之尾段（已以本日 00:00 起算，通常為 [0, x]）。</param>
        /// <param name="carryOut">本日跨午夜延伸到隔日之尾段（以隔日 00:00 起算）。</param>
        /// <param name="overtimeMinutes">
        /// 當日加班分鐘數（<c>WorkCalendar.TM_WKO</c> × 60）。0 或負數＝沒有加班。
        ///
        /// <para><b>加班接在「當日班別」的最後一段之後</b>，與甘特的畫法一致
        /// （<c>ScheduleController</c> 把加班灰帶接在正班最後一段後面）。
        /// 兩邊要一起改，否則畫出來的加班與排得進去的加班會錯開。</para>
        ///
        /// <para><b>錨點刻意不看 <paramref name="carryIn"/></b>：carryIn 是前一日跨午夜的尾段、
        /// 位置在當日開頭，拿它當錨點會把今天的加班接到昨天的班尾後面。
        /// 因此**當日沒有任何班別時就沒有加班**（假日、週末），
        /// 即使 <c>TM_WKO</c> 有值也不產生時段——沒有正班可接的加班沒有起點。</para>
        /// </param>
        /// <returns>本日可用時段（已排序、合併、限於 [0,1440]）。</returns>
        public static List<Segment> BuildDay(
            IReadOnlyList<Shift> shifts,
            IReadOnlyList<Segment> carryIn,
            out List<Segment> carryOut,
            int overtimeMinutes = 0)
        {
            var shiftIntervals = new List<Segment>();

            foreach (var sh in shifts ?? new List<Shift>())
            {
                if (sh.StartTime is not { } st || sh.EndTime is not { } en) continue;

                int s = ToMin(st), e = ToMin(en);
                if (e <= s) e += 1440;   // 跨午夜班

                // 扣午休（夾到班別範圍內）
                if (sh.BreakStart is { } bsT && sh.BreakEnd is { } beT)
                {
                    int bs = ToMin(bsT), be = ToMin(beT);
                    if (be <= bs) be += 1440;
                    bs = Math.Max(bs, s);
                    be = Math.Min(be, e);
                    if (be > bs)
                    {
                        if (bs > s) shiftIntervals.Add(new Segment(s, bs));
                        if (e > be) shiftIntervals.Add(new Segment(be, e));
                        continue;
                    }
                }
                shiftIntervals.Add(new Segment(s, e));
            }

            // 加班接在**當日班別**的最後一段之後（理由見 overtimeMinutes 的說明）。
            // 先合併一次才知道「最後一段」在哪：多班別或有午休時，未合併的清單末項不是時間最晚的那一段。
            var shiftMerged = Merge(shiftIntervals);
            if (overtimeMinutes > 0 && shiftMerged.Count > 0)
            {
                var otStart = shiftMerged[^1].End;
                shiftIntervals.Add(new Segment(otStart, otStart + overtimeMinutes));
            }

            var intervals = new List<Segment>();
            if (carryIn != null) intervals.AddRange(carryIn);
            intervals.AddRange(shiftIntervals);

            // 合併（含跨午夜 end>1440 的部分）
            var merged = Merge(intervals);

            // 以 1440 切分：本日 [s, min(e,1440)]；超過部分 → 隔日 [0, e-1440]
            var today = new List<Segment>();
            var carry = new List<Segment>();
            foreach (var seg in merged)
            {
                if (seg.Start < 1440)
                    today.Add(new Segment(seg.Start, Math.Min(seg.End, 1440)));
                if (seg.End > 1440)
                    carry.Add(new Segment(0, seg.End - 1440));
            }

            carryOut = Merge(carry);
            return Merge(today);
        }

        /// <summary>排序後合併重疊或相鄰（end &gt;= 下一段 start）的區段。</summary>
        private static List<Segment> Merge(List<Segment> input)
        {
            var result = new List<Segment>();
            foreach (var seg in input.Where(x => x.End > x.Start).OrderBy(x => x.Start).ThenBy(x => x.End))
            {
                if (result.Count > 0 && seg.Start <= result[^1].End)
                {
                    if (seg.End > result[^1].End)
                        result[^1] = new Segment(result[^1].Start, seg.End);
                }
                else
                {
                    result.Add(seg);
                }
            }
            return result;
        }
    }
}
