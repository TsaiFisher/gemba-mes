using SmartFactory.MES.Modules._Shared;
namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 工作時段走訪器（mfg-domain.md §產能模型）。
    /// 純靜態函式，無 DB 相依，便於單元測試。
    ///
    /// 沿「可用時段」步進，把所需淨工時（分鐘）換算為真實結束時刻，
    /// 跳過休息/班距/非工作日，並扣除計劃停機重疊區間。
    /// </summary>
    public static class WorkingTimeWalker
    {
        /// <summary>
        /// 自 <paramref name="start"/> 起消耗 <paramref name="requiredMinutes"/> 分鐘工時，
        /// 傳回真實完成時刻（廠別壁鐘，Kind=Utc——房規：DateTime 一律存 UTC，供 Npgsql timestamptz 相容）。
        /// </summary>
        /// <param name="start">起始時刻（廠別壁鐘，Kind=Utc）。</param>
        /// <param name="requiredMinutes">需消耗的淨工時（分鐘，可含小數）。</param>
        /// <param name="getSegments">
        ///   傳入日期，傳回該日可用時段清單（StartMinute/EndMinute 為自 00:00 起算分鐘，0..1440）。
        ///   無工時的日期應傳回空清單。
        /// </param>
        /// <param name="downtimes">
        ///   與此機台/工作中心相關的計劃停機絕對時間區段（廠別壁鐘）；可為 null。
        /// </param>
        /// <param name="maxDays">安全迴圈上限（預設 90 天）。</param>
        /// <returns>工作完成的真實時刻。</returns>
        public static DateTime AddWorkingMinutes(
            DateTime start,
            decimal requiredMinutes,
            Func<DateOnly, IReadOnlyList<(int Start, int End)>> getSegments,
            IReadOnlyList<(DateTime From, DateTime To)>? downtimes = null,
            int maxDays = 90)
        {
            if (requiredMinutes <= 0) return start;

            var remaining = requiredMinutes;
            var date = DateOnly.FromDateTime(start);
            int cursorMin = start.Hour * 60 + start.Minute;

            for (int day = 0; day < maxDays && remaining > 0; day++, date = date.AddDays(1), cursorMin = 0)
            {
                var segs = getSegments(date);
                if (segs.Count == 0) continue;

                foreach (var seg in segs)
                {
                    if (seg.End <= cursorMin) continue;   // 此段已過

                    int effectiveStart = Math.Max(seg.Start, cursorMin);
                    if (seg.End <= effectiveStart) continue;

                    // DateOnly.ToDateTime(TimeOnly) 不論來源 Kind 一律回傳 Unspecified，
                    // 須明確指定 Utc 才能維持呼叫端已標好的 Kind（否則寫回 timestamptz 欄位會被 Npgsql 拒絕）。
                    var slotFrom = date.ToDateTime(new TimeOnly(0, 0), DateTimeKind.Utc).AddMinutes(effectiveStart);
                    var slotTo   = date.ToDateTime(new TimeOnly(0, 0), DateTimeKind.Utc).AddMinutes(seg.End);

                    // 1. 收集並裁剪此時段內的停機區間
                    var activeDowntimes = new List<(DateTime From, DateTime To)>();
                    if (downtimes != null)
                    {
                        foreach (var dt in downtimes)
                        {
                            var oStart = dt.From > slotFrom ? dt.From : slotFrom;
                            var oEnd   = dt.To   < slotTo   ? dt.To   : slotTo;
                            if (oEnd > oStart)
                            {
                                activeDowntimes.Add((oStart, oEnd));
                            }
                        }
                    }

                    // 2. 將停機區間排序並合併（確保無重疊）
                    var mergedDowntimes = MergeIntervals(activeDowntimes);

                    // 3. 計算出此時段內的所有「可用工作小區間」
                    var workIntervals = new List<(DateTime From, DateTime To)>();
                    var currentStart = slotFrom;
                    foreach (var dt in mergedDowntimes)
                    {
                        if (dt.From > currentStart)
                        {
                            workIntervals.Add((currentStart, dt.From));
                        }
                        currentStart = dt.To > currentStart ? dt.To : currentStart;
                    }
                    if (slotTo > currentStart)
                    {
                        workIntervals.Add((currentStart, slotTo));
                    }

                    // 4. 依序消耗工時
                    foreach (var interval in workIntervals)
                    {
                        var intervalMinutes = (decimal)(interval.To - interval.From).TotalMinutes;
                        if (intervalMinutes <= 0) continue;

                        if (remaining <= intervalMinutes)
                        {
                            return interval.From.AddMinutes((double)remaining);
                        }
                        remaining -= intervalMinutes;
                    }

                    cursorMin = seg.End;
                }
            }

            throw new ScheduleCalendarException(
                PzText.Get("Mfg_ScheduleForwardFailFmt", maxDays, remaining));
        }

        /// <summary>
        /// 把 <paramref name="start"/> 往前校正（snap）到下一個真正落在可用時段內的時刻，
        /// 不消耗任何工時。供 <c>SchedulingEngine.AllocateDispatchSlot</c> 在呼叫
        /// <see cref="AddWorkingMinutes"/> 前先校正派工起點——否則若 <paramref name="start"/>
        /// 剛好落在非上班時段（如半夜、非工作日），排出來的 ScheduledStart 會停在那個
        /// 沒有產能的時刻，只有 ScheduledEnd 是經過工作日曆校正的，導致起訖對不起來。
        /// 找不到（<paramref name="maxDays"/> 天內都無可用時段）回傳 null。
        /// </summary>
        public static DateTime? SnapToAvailable(
            DateTime start,
            Func<DateOnly, IReadOnlyList<(int Start, int End)>> getSegments,
            int maxDays = 90)
        {
            var date = DateOnly.FromDateTime(start);
            int cursorMin = start.Hour * 60 + start.Minute;

            for (int day = 0; day < maxDays; day++, date = date.AddDays(1), cursorMin = 0)
            {
                var segs = getSegments(date);
                foreach (var seg in segs)
                {
                    if (seg.End <= cursorMin) continue;   // 此段已過
                    int effectiveStart = Math.Max(seg.Start, cursorMin);
                    if (seg.End <= effectiveStart) continue;
                    return date.ToDateTime(new TimeOnly(0, 0), DateTimeKind.Utc).AddMinutes(effectiveStart);
                }
            }
            return null;
        }

        /// <summary>
        /// 把 <paramref name="end"/> 往「回」校正到最近一個落在可用時段內的時刻（<see cref="SnapToAvailable"/>
        /// 的反向），不消耗工時。供工序重疊「往前計算」用：倒推的錨點結束時刻若落在非上班時段
        /// （下班後、假日），必須先退回當日最後一段產能的結束點，否則倒推起點會憑空多算一段空白。
        /// 找不到（<paramref name="maxDays"/> 天內都無可用時段）回傳 null。
        /// </summary>
        public static DateTime? SnapBackToAvailable(
            DateTime end,
            Func<DateOnly, IReadOnlyList<(int Start, int End)>> getSegments,
            int maxDays = 90)
        {
            var date = DateOnly.FromDateTime(end);
            int cursorMin = end.Hour * 60 + end.Minute;

            for (int day = 0; day < maxDays; day++, date = date.AddDays(-1), cursorMin = 1440)
            {
                var segs = getSegments(date);
                foreach (var seg in segs.OrderByDescending(s => s.End))
                {
                    if (seg.Start >= cursorMin) continue;   // 此段在 cursor 之後
                    int effectiveEnd = Math.Min(seg.End, cursorMin);
                    if (effectiveEnd <= seg.Start) continue;
                    return date.ToDateTime(new TimeOnly(0, 0), DateTimeKind.Utc).AddMinutes(effectiveEnd);
                }
            }
            return null;
        }

        /// <summary>
        /// 自 <paramref name="end"/> 起**往回**消耗 <paramref name="requiredMinutes"/> 分鐘工時，
        /// 傳回應開始的時刻（MRP 倒推用，mfg-domain.md §5.1）。
        /// 不處理計劃停機（MRP 為廠級估算，機台尚未指定；停機屬排程引擎層）。
        /// </summary>
        public static DateTime SubtractWorkingMinutes(
            DateTime end,
            decimal requiredMinutes,
            Func<DateOnly, IReadOnlyList<(int Start, int End)>> getSegments,
            int maxDays = 365)
        {
            if (requiredMinutes <= 0) return end;

            var remaining = requiredMinutes;
            var date = DateOnly.FromDateTime(end);
            int cursorMin = end.Hour * 60 + end.Minute;

            for (int day = 0; day < maxDays && remaining > 0; day++, date = date.AddDays(-1), cursorMin = 1440)
            {
                var segs = getSegments(date);
                if (segs.Count == 0) continue;

                // 由晚到早消耗
                foreach (var seg in segs.OrderByDescending(s => s.End))
                {
                    if (seg.Start >= cursorMin) continue;   // 此段在 cursor 之後，尚未輪到

                    int effectiveEnd = Math.Min(seg.End, cursorMin);
                    if (effectiveEnd <= seg.Start) continue;

                    var available = (decimal)(effectiveEnd - seg.Start);
                    if (remaining <= available)
                    {
                        return date.ToDateTime(new TimeOnly(0, 0), DateTimeKind.Utc)
                                   .AddMinutes(effectiveEnd - (double)remaining);
                    }
                    remaining -= available;
                    cursorMin = seg.Start;
                }
            }

            throw new InvalidOperationException(
                PzText.Get("Mfg_ScheduleBackwardFailFmt", maxDays, remaining));
        }

        private static List<(DateTime From, DateTime To)> MergeIntervals(List<(DateTime From, DateTime To)> intervals)
        {
            if (intervals.Count <= 1) return intervals;

            var sorted = intervals.OrderBy(i => i.From).ToList();
            var merged = new List<(DateTime From, DateTime To)> { sorted[0] };

            for (int i = 1; i < sorted.Count; i++)
            {
                var current = sorted[i];
                var last = merged[merged.Count - 1];

                if (current.From <= last.To)
                {
                    if (current.To > last.To)
                    {
                        merged[merged.Count - 1] = (last.From, current.To);
                    }
                }
                else
                {
                    merged.Add(current);
                }
            }

            return merged;
        }
    }

    /// <summary>
    /// 工作日曆排不出時段（工作中心沒設班別群組、日曆全是休息日…）。訊息已在地化，可直接給使用者看。
    ///
    /// <para>獨立成型別是為了讓呼叫端（排程畫面的「執行排程」）能接住它回成一般錯誤訊息：原本一律是裸 500，
    /// 畫面那段 fetch 只會 <c>r.json()</c>，回應不是 JSON 時使用者什麼都看不到（2026-10-20 驗收 Y-4 自動化發現）。
    /// 仍繼承 <see cref="InvalidOperationException"/>，既有的 catch 照樣接得到。</para>
    /// </summary>
    public sealed class ScheduleCalendarException : InvalidOperationException
    {
        public ScheduleCalendarException(string message) : base(message) { }
    }
}
