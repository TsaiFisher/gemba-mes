using System;
using System.Collections.Generic;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;
using Seg = SmartFactory.MES.Modules.MFG.Services.WorkCalendarSegmentBuilder.Segment;

namespace SmartFactory.MES.Tests.MFG
{
    public class WorkCalendarSegmentBuilderTests
    {
        private static Shift S(int sh, int sm, int eh, int em, int? bsh = null, int? bsm = null, int? beh = null, int? bem = null)
            => new()
            {
                ShiftCode = $"{sh:00}{eh:00}",
                ShiftName = "t",
                StartTime = new TimeOnly(sh, sm),
                EndTime = new TimeOnly(eh, em),
                BreakStart = bsh is { } h ? new TimeOnly(h, bsm ?? 0) : null,
                BreakEnd = beh is { } h2 ? new TimeOnly(h2, bem ?? 0) : null,
            };

        private static readonly IReadOnlyList<Seg> NoCarry = new List<Seg>();

        [Fact]
        public void DayShiftWithLunchBreak_SplitsIntoTwoSegments()
        {
            var segs = WorkCalendarSegmentBuilder.BuildDay(
                new[] { S(8, 0, 17, 0, 12, 0, 13, 0) }, NoCarry, out var carry);

            Assert.Equal(new[] { new Seg(480, 720), new Seg(780, 1020) }, segs);
            Assert.Empty(carry);
        }

        [Fact]
        public void TwoShiftsWithGap_KeepGapAsSeparateSegments()
        {
            // 早班 08-12、午後班 13-17，中間 12-13 空檔
            var segs = WorkCalendarSegmentBuilder.BuildDay(
                new[] { S(8, 0, 12, 0), S(13, 0, 17, 0) }, NoCarry, out _);

            Assert.Equal(new[] { new Seg(480, 720), new Seg(780, 1020) }, segs);
        }

        [Fact]
        public void OverlappingShifts_AreMerged()
        {
            var segs = WorkCalendarSegmentBuilder.BuildDay(
                new[] { S(8, 0, 14, 0), S(12, 0, 18, 0) }, NoCarry, out _);

            Assert.Equal(new[] { new Seg(480, 1080) }, segs);
        }

        [Fact]
        public void NightShift_CrossesMidnight_TailCarriesToNextDay()
        {
            // 夜班 22:00-06:00 → 當日 [1320,1440]，隔日 carry [0,360]
            var segs = WorkCalendarSegmentBuilder.BuildDay(
                new[] { S(22, 0, 6, 0) }, NoCarry, out var carry);

            Assert.Equal(new[] { new Seg(1320, 1440) }, segs);
            Assert.Equal(new[] { new Seg(0, 360) }, carry);
        }

        [Fact]
        public void CarryIn_MergedIntoNextDay()
        {
            // 前一日夜班帶入 [0,360]，當日再有日班 08-17
            var segs = WorkCalendarSegmentBuilder.BuildDay(
                new[] { S(8, 0, 17, 0) }, new List<Seg> { new(0, 360) }, out var carry);

            Assert.Equal(new[] { new Seg(0, 360), new Seg(480, 1020) }, segs);
            Assert.Empty(carry);
        }

        [Fact]
        public void Holiday_NoShifts_StillEmitsCarryIn()
        {
            // 假日無班別，但前一日夜班尾段仍是可用時間
            var segs = WorkCalendarSegmentBuilder.BuildDay(
                Array.Empty<Shift>(), new List<Seg> { new(0, 360) }, out var carry);

            Assert.Equal(new[] { new Seg(0, 360) }, segs);
            Assert.Empty(carry);
        }

        [Fact]
        public void TwentyFourHourShift_OneFullSegment()
        {
            // 00:00-00:00 視為整日 24h
            var segs = WorkCalendarSegmentBuilder.BuildDay(
                new[] { S(0, 0, 0, 0) }, NoCarry, out var carry);

            Assert.Equal(new[] { new Seg(0, 1440) }, segs);
            Assert.Empty(carry);
        }

        // ── 加班（TM_WKO，2026-08-26 納入）──────────────────────────────────────
        // 在此之前可用時段是「純班別衍生」，排程走步器因此永遠看不到加班，
        // 而甘特與 OEE 都是含加班算的——同一天的產能有兩個答案。

        [Fact]
        public void Overtime_AppendsAfterLastShiftSegment()
        {
            // 日班 08-17（含午休），加班 2 小時 → 17:00-19:00
            var segs = WorkCalendarSegmentBuilder.BuildDay(
                new[] { S(8, 0, 17, 0, 12, 0, 13, 0) }, NoCarry, out var carry, overtimeMinutes: 120);

            Assert.Equal(new[] { new Seg(480, 720), new Seg(780, 1140) }, segs);
            Assert.Empty(carry);
        }

        [Fact]
        public void Overtime_AnchorsToLatestSegment_NotListOrder()
        {
            // 班別順序刻意顛倒：先給午後班再給早班。加班要接在 17:00 之後，不是 12:00 之後。
            // 沒有先合併就取末項的話，這一題會把加班接到早班尾巴、疊進午後班裡。
            var segs = WorkCalendarSegmentBuilder.BuildDay(
                new[] { S(13, 0, 17, 0), S(8, 0, 12, 0) }, NoCarry, out _, overtimeMinutes: 60);

            Assert.Equal(new[] { new Seg(480, 720), new Seg(780, 1080) }, segs);
        }

        [Fact]
        public void Overtime_CrossingMidnight_CarriesToNextDay()
        {
            // 日班 08-23，加班 2 小時 → 23:00-01:00，跨午夜那段進 carry
            var segs = WorkCalendarSegmentBuilder.BuildDay(
                new[] { S(8, 0, 23, 0) }, NoCarry, out var carry, overtimeMinutes: 120);

            Assert.Equal(new[] { new Seg(480, 1440) }, segs);
            Assert.Equal(new[] { new Seg(0, 60) }, carry);
        }

        [Fact]
        public void Overtime_WithoutAnyShift_ProducesNothing()
        {
            // 假日：有 carry-in、也設了加班時數，但當日沒有班別 → 不產生加班時段。
            // **錨點刻意不看 carry-in**：那是昨天的班尾，接在它後面等於把今天的加班排在凌晨。
            var segs = WorkCalendarSegmentBuilder.BuildDay(
                Array.Empty<Shift>(), new List<Seg> { new(0, 360) }, out var carry, overtimeMinutes: 120);

            Assert.Equal(new[] { new Seg(0, 360) }, segs);
            Assert.Empty(carry);
        }

        [Fact]
        public void Overtime_Zero_LeavesSegmentsUnchanged()
        {
            // 預設參數＝0：既有呼叫端不傳它時行為必須與改動前一模一樣。
            var withArg = WorkCalendarSegmentBuilder.BuildDay(
                new[] { S(8, 0, 17, 0) }, NoCarry, out _, overtimeMinutes: 0);
            var without = WorkCalendarSegmentBuilder.BuildDay(
                new[] { S(8, 0, 17, 0) }, NoCarry, out _);

            Assert.Equal(without, withArg);
            Assert.Equal(new[] { new Seg(480, 1020) }, withArg);
        }
    }
}
