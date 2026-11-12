using SmartFactory.MES.Models;

namespace SmartFactory.MES.Tests.QCR;

/// <summary>
/// NCR 狀態機守衛邏輯單元測試（mfg-domain.md §15.4 / NcrController）。
/// 純邏輯，不接 DB。反映 NcrController 各動作的前置狀態守衛。
///
/// 狀態流向（UseAsIs 路徑）：
///   Open → PendingApproval → Approved → Executed → Closed
/// 狀態流向（Scrap/Rework/Return 路徑）：
///   Open → Executed → Closed
/// </summary>
public class NcrStateMachineTests
{
    // ── 守衛邏輯（對應 NcrController 各動作的判斷條件）──────────────────────

    private static bool CanEdit(string status) =>
        status == NcrStatus.Open;

    private static bool CanDelete(string status) =>
        status == NcrStatus.Open;

    private static bool CanSubmit(string status, string disposition) =>
        disposition == "UseAsIs" && status == NcrStatus.Open;

    private static bool CanExecute(string status, string disposition)
    {
        var executableStatuses = new[] { NcrStatus.Open, NcrStatus.Approved };
        if (!executableStatuses.Contains(status)) return false;
        if (disposition == "UseAsIs" && status != NcrStatus.Approved) return false;
        return true;
    }

    private static bool CanClose(string status) =>
        status == NcrStatus.Executed;

    // ── Edit / Delete：僅 Open 狀態可操作 ────────────────────────────────────

    [Theory]
    [InlineData(NcrStatus.Open,            true)]
    [InlineData(NcrStatus.PendingApproval, false)]
    [InlineData(NcrStatus.Approved,        false)]
    [InlineData(NcrStatus.Executed,        false)]
    [InlineData(NcrStatus.Closed,          false)]
    [InlineData(NcrStatus.Void,            false)]
    public void CanEdit_OnlyWhenOpen(string status, bool expected)
    {
        Assert.Equal(expected, CanEdit(status));
    }

    [Theory]
    [InlineData(NcrStatus.Open,            true)]
    [InlineData(NcrStatus.PendingApproval, false)]
    [InlineData(NcrStatus.Approved,        false)]
    [InlineData(NcrStatus.Executed,        false)]
    [InlineData(NcrStatus.Closed,          false)]
    public void CanDelete_OnlyWhenOpen(string status, bool expected)
    {
        Assert.Equal(expected, CanDelete(status));
    }

    // ── Submit：UseAsIs + Open 才能送審 ──────────────────────────────────────

    [Theory]
    // 允許：UseAsIs 且 Open
    [InlineData(NcrStatus.Open,            "UseAsIs", true)]
    // 拒絕：UseAsIs 但狀態非 Open
    [InlineData(NcrStatus.PendingApproval, "UseAsIs", false)]
    [InlineData(NcrStatus.Approved,        "UseAsIs", false)]
    [InlineData(NcrStatus.Executed,        "UseAsIs", false)]
    // 拒絕：非 UseAsIs 處置，即使 Open
    [InlineData(NcrStatus.Open,            "Scrap",   false)]
    [InlineData(NcrStatus.Open,            "Rework",  false)]
    [InlineData(NcrStatus.Open,            "Return",  false)]
    public void CanSubmit_RequiresUseAsIsAndOpen(string status, string disposition, bool expected)
    {
        Assert.Equal(expected, CanSubmit(status, disposition));
    }

    // ── Execute：Scrap / Rework / Return 路徑 ────────────────────────────────

    [Theory]
    // 允許：Open 或 Approved 均可執行（非 UseAsIs）
    [InlineData(NcrStatus.Open,            "Scrap",  true)]
    [InlineData(NcrStatus.Approved,        "Scrap",  true)]
    [InlineData(NcrStatus.Open,            "Rework", true)]
    [InlineData(NcrStatus.Approved,        "Rework", true)]
    [InlineData(NcrStatus.Open,            "Return", true)]
    [InlineData(NcrStatus.Approved,        "Return", true)]
    // 拒絕：其他狀態
    [InlineData(NcrStatus.PendingApproval, "Scrap",  false)]
    [InlineData(NcrStatus.Executed,        "Scrap",  false)]
    [InlineData(NcrStatus.Closed,          "Scrap",  false)]
    [InlineData(NcrStatus.Void,            "Scrap",  false)]
    public void CanExecute_ScrapReworkReturn_OpenOrApproved(
        string status, string disposition, bool expected)
    {
        Assert.Equal(expected, CanExecute(status, disposition));
    }

    // ── Execute：UseAsIs 路徑（必須先核准）───────────────────────────────────

    [Theory]
    [InlineData(NcrStatus.Approved,        "UseAsIs", true)]   // 已核准 → 可執行
    [InlineData(NcrStatus.Open,            "UseAsIs", false)]  // 未核准 → 拒絕
    [InlineData(NcrStatus.PendingApproval, "UseAsIs", false)]  // 審核中 → 拒絕（狀態守衛）
    [InlineData(NcrStatus.Executed,        "UseAsIs", false)]  // 已執行 → 拒絕
    public void CanExecute_UseAsIs_RequiresApproved(
        string status, string disposition, bool expected)
    {
        Assert.Equal(expected, CanExecute(status, disposition));
    }

    // ── Close：僅 Executed 可結案 ──────────────────────────────────────────

    [Theory]
    [InlineData(NcrStatus.Executed,        true)]
    [InlineData(NcrStatus.Open,            false)]
    [InlineData(NcrStatus.PendingApproval, false)]
    [InlineData(NcrStatus.Approved,        false)]
    [InlineData(NcrStatus.Closed,          false)]
    [InlineData(NcrStatus.Void,            false)]
    public void CanClose_OnlyWhenExecuted(string status, bool expected)
    {
        Assert.Equal(expected, CanClose(status));
    }

    // ── 完整路徑驗證：Scrap 正常流 ──────────────────────────────────────────

    [Fact]
    public void ScrapWorkflow_FullPath_AllGuardsPass()
    {
        // Open：可編輯、可刪除
        Assert.True(CanEdit(NcrStatus.Open));
        Assert.True(CanDelete(NcrStatus.Open));

        // Open + Scrap：不需送審，直接執行
        Assert.False(CanSubmit(NcrStatus.Open, "Scrap"));
        Assert.True(CanExecute(NcrStatus.Open, "Scrap"));

        // 執行後 → Executed
        Assert.False(CanEdit(NcrStatus.Executed));
        Assert.False(CanDelete(NcrStatus.Executed));
        Assert.True(CanClose(NcrStatus.Executed));
    }

    // ── 完整路徑驗證：UseAsIs 需核准流 ──────────────────────────────────────

    [Fact]
    public void UseAsIsWorkflow_FullPath_AllGuardsPass()
    {
        // Open：可送審
        Assert.True(CanSubmit(NcrStatus.Open, "UseAsIs"));
        // Open + UseAsIs：尚未核准，不可執行
        Assert.False(CanExecute(NcrStatus.Open, "UseAsIs"));

        // PendingApproval：不可編輯、不可刪除、不可執行
        Assert.False(CanEdit(NcrStatus.PendingApproval));
        Assert.False(CanExecute(NcrStatus.PendingApproval, "UseAsIs"));

        // Approved：UseAsIs 可執行
        Assert.True(CanExecute(NcrStatus.Approved, "UseAsIs"));

        // Executed → 可結案
        Assert.True(CanClose(NcrStatus.Executed));

        // Closed：所有動作均不可
        Assert.False(CanEdit(NcrStatus.Closed));
        Assert.False(CanClose(NcrStatus.Closed));
    }
}
