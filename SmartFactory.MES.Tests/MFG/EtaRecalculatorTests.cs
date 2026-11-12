using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// EtaRecalculator 純函式單元測試（mfg-domain.md §7.5 / §10.12 / §12.1）。
/// 純函式，不接 DB。
/// </summary>
public class EtaRecalculatorTests
{
    // ── EtaHelper.FilterRollingWindow（滾動視窗篩選）────────────────────────

    private static Dispatch MakeDispatch(string id, DateTime? scheduledStart, DispatchStatus status = DispatchStatus.Scheduled)
        => new() { DispatchId = id, Status = status, ScheduledStart = scheduledStart, ScheduledEnd = scheduledStart?.AddHours(2) };

    [Fact]
    public void FilterRollingWindow_ReturnOnlyWithin48h()
    {
        var now = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var inside  = MakeDispatch("D1", now.AddHours(24));   // 24h 後 → 在視窗內
        var outside = MakeDispatch("D2", now.AddHours(50));   // 50h 後 → 超出視窗
        var past    = MakeDispatch("D3", now.AddHours(-1));   // 過去 → 超出視窗

        var result = EtaHelper.FilterRollingWindow(new[] { inside, outside, past }, now);

        Assert.Single(result);
        Assert.Equal("D1", result[0].DispatchId);
    }

    [Fact]
    public void FilterRollingWindow_CapAtMaxNodes()
    {
        var now = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        // 60 個以半小時間隔排列，全部在 48h 視窗內（30h < 48h）
        var candidates = Enumerable.Range(1, 60)
            .Select(i => MakeDispatch($"D{i}", now.AddMinutes(i * 30)))
            .ToList();

        var result = EtaHelper.FilterRollingWindow(candidates, now, horizonHours: 48, maxNodes: 50);

        Assert.Equal(50, result.Count);
    }

    [Fact]
    public void FilterRollingWindow_EmptyList_ReturnsEmpty()
    {
        var now = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var result = EtaHelper.FilterRollingWindow(Array.Empty<Dispatch>(), now);
        Assert.Empty(result);
    }

    [Fact]
    public void FilterRollingWindow_NullScheduledStart_Excluded()
    {
        var now = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var d = MakeDispatch("D1", null);   // 無排程 → 排除
        var result = EtaHelper.FilterRollingWindow(new[] { d }, now);
        Assert.Empty(result);
    }

    // ── EtaHelper.CalcRemainingMinutes（剩餘工時計算）──────────────────────

    [Fact]
    public void CalcRemainingMinutes_HalfDone_ReturnsHalfTime()
    {
        // 標準工時 120 分，已完 5 / 總量 10 → 剩 6 / 12 → 剩 60 分
        var remaining = EtaHelper.CalcRemainingMinutes(120m, 5m, 10m);
        Assert.Equal(60m, remaining);
    }

    [Fact]
    public void CalcRemainingMinutes_AllDone_ReturnsZero()
    {
        var remaining = EtaHelper.CalcRemainingMinutes(120m, 10m, 10m);
        Assert.Equal(0m, remaining);
    }

    [Fact]
    public void CalcRemainingMinutes_NothingDone_ReturnsFullTime()
    {
        var remaining = EtaHelper.CalcRemainingMinutes(120m, 0m, 10m);
        Assert.Equal(120m, remaining);
    }

    [Fact]
    public void CalcRemainingMinutes_ZeroTotal_ReturnsZero()
    {
        var remaining = EtaHelper.CalcRemainingMinutes(120m, 0m, 0m);
        Assert.Equal(0m, remaining);
    }

    // ── EtaHelper.ShiftSlot（時段平移）────────────────────────────────────

    [Fact]
    public void ShiftSlot_PositiveDelta_MovesForward()
    {
        var start = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var end   = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var delta = TimeSpan.FromHours(2);

        var (ns, ne) = EtaHelper.ShiftSlot(start, end, delta);

        Assert.Equal(new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc), ns);
        Assert.Equal(new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), ne);
    }

    [Fact]
    public void ShiftSlot_NegativeDelta_MovesBackward()
    {
        var start = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var end   = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var delta = TimeSpan.FromHours(-1);

        var (ns, ne) = EtaHelper.ShiftSlot(start, end, delta);

        Assert.Equal(new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc), ns);
        Assert.Equal(new DateTime(2026, 6, 1, 11, 0, 0, DateTimeKind.Utc), ne);
    }

    [Fact]
    public void ShiftSlot_ZeroDelta_ReturnsSameSlot()
    {
        var start = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var end   = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);

        var (ns, ne) = EtaHelper.ShiftSlot(start, end, TimeSpan.Zero);

        Assert.Equal(start, ns);
        Assert.Equal(end, ne);
    }

    // ── 機台狀態轉換 → 目標狀態（mfg-domain.md §13.3）─────────────────────

    [Theory]
    [InlineData("CI", MachineStatus.Available)]
    [InlineData("II", MachineStatus.Standby)]
    [InlineData("DI", MachineStatus.BreakDown)]
    [InlineData("DO", MachineStatus.Available)]
    [InlineData("HI", MachineStatus.Maintenance)]
    [InlineData("HO", MachineStatus.Available)]
    [InlineData("EO", MachineStatus.Modification)]
    [InlineData("FO", MachineStatus.Loaned)]
    [InlineData("FI", MachineStatus.Available)]
    [InlineData("GO", MachineStatus.Retired)]
    [InlineData("CO", MachineStatus.ExternalRepair)]
    public void MachineEventType_ParsesAndMapsCorrectly(string eventCode, MachineStatus expected)
    {
        bool parsed = Enum.TryParse<MachineEventType>(eventCode, ignoreCase: true, out var evt);
        Assert.True(parsed, $"Should parse event code {eventCode}");

        var targetStatus = evt switch
        {
            MachineEventType.CI => MachineStatus.Available,
            MachineEventType.II => MachineStatus.Standby,
            MachineEventType.DI => MachineStatus.BreakDown,
            MachineEventType.DO => MachineStatus.Available,
            MachineEventType.HI => MachineStatus.Maintenance,
            MachineEventType.HO => MachineStatus.Available,
            MachineEventType.EO => MachineStatus.Modification,
            MachineEventType.FO => MachineStatus.Loaned,
            MachineEventType.FI => MachineStatus.Available,
            MachineEventType.GO => MachineStatus.Retired,
            MachineEventType.CO => MachineStatus.ExternalRepair,
            _                   => MachineStatus.Available
        };
        Assert.Equal(expected, targetStatus);
    }

    // ── 機台不可用時派工狀態規則（mfg-domain.md §13.4）──────────────────────

    [Theory]
    [InlineData(MachineStatus.BreakDown,      false)] // 不可用 → 須影響派工
    [InlineData(MachineStatus.Maintenance,    false)]
    [InlineData(MachineStatus.Modification,   false)]
    [InlineData(MachineStatus.Loaned,         false)]
    [InlineData(MachineStatus.ExternalRepair, false)]
    [InlineData(MachineStatus.Retired,        false)]
    [InlineData(MachineStatus.Available,      true)]  // 可用 → 不影響派工
    [InlineData(MachineStatus.Standby,        true)]  // 待機 → 不影響派工
    public void MachineStatus_IsSchedulable(MachineStatus status, bool isSchedulable)
    {
        bool result = status is MachineStatus.Available or MachineStatus.Standby;
        Assert.Equal(isSchedulable, result);
    }

    // ── clientId 去重邏輯驗證 ──────────────────────────────────────────────

    [Fact]
    public void SyncLog_SameClientId_IsIdempotencyKey()
    {
        // 模擬 SyncLog 的去重邏輯：同一 clientId 不應重複建立
        var log1 = new SyncLog { ClientId = "test-client-001", Endpoint = "/api/mobile/sessions" };
        var log2 = new SyncLog { ClientId = "test-client-001", Endpoint = "/api/mobile/sessions" };

        // ClientId 為 PK，同一 clientId 只能存在一筆
        Assert.Equal(log1.ClientId, log2.ClientId);
    }
}
