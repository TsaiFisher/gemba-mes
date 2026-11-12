using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Tests.StateMachines;

/// <summary>
/// 工單狀態機矩陣（Phase 4-3）。斷言形式同進貨單，見
/// <see cref="GoodsReceiptStateMachineTests"/> 的說明。
///
/// 工單走的是自己的 <see cref="WorkOrderStatus"/>（不是 DocStatus），
/// 且作廢的閘門有兩個 DB 事實：派工回報量與已領未退料量。
/// </summary>
public class WorkOrderStateMachineTests
{
    private static readonly WorkOrderStatus[] AllStatuses = Enum.GetValues<WorkOrderStatus>();
    private static readonly DocumentApprovalStatus[] AllApprovals = Enum.GetValues<DocumentApprovalStatus>();
    private static readonly bool[] Bools = { false, true };

    // ── 釋出 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CanRelease_AllowedCells()
    {
        var allowed = AllStatuses
            .Where(s => WorkOrderStateMachine.CanRelease(s) == WorkOrderGuard.Allowed)
            .ToArray();

        // 只有 Planned 可釋出：已釋出者再釋出會重排產能、重算需求量
        Assert.Equal(new[] { WorkOrderStatus.Planned }, allowed);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Released,   WorkOrderGuard.NotPlanned)]
    [InlineData(WorkOrderStatus.InProgress, WorkOrderGuard.NotPlanned)]
    [InlineData(WorkOrderStatus.Completed,  WorkOrderGuard.NotPlanned)]
    [InlineData(WorkOrderStatus.Closed,     WorkOrderGuard.NotPlanned)]
    [InlineData(WorkOrderStatus.Void,       WorkOrderGuard.NotPlanned)]
    public void CanRelease_RejectionReasons(WorkOrderStatus status, WorkOrderGuard expected)
    {
        Assert.Equal(expected, WorkOrderStateMachine.CanRelease(status));
    }

    // ── 撤回 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CanWithdraw_AllowedCells()
    {
        var allowed = AllApprovals
            .Where(a => WorkOrderStateMachine.CanWithdraw(a) == WorkOrderGuard.Allowed)
            .ToArray();

        // 撤回只對「審核中」有意義；其餘狀態沒有在跑的簽核可撤
        Assert.Equal(new[] { DocumentApprovalStatus.PendingApproval }, allowed);
    }

    // ── 作廢 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CanVoid_AllowedCells()
    {
        var allowed = (from s in AllStatuses
                       from production in Bools
                       from issued in Bools
                       where WorkOrderStateMachine.CanVoid(s, production, issued) == WorkOrderGuard.Allowed
                       select $"{s}/production={production}/issued={issued}")
                      .ToArray();

        // 尚未開始生產、且料還在倉裡（或已全數退回）才可作廢。
        // InProgress / Completed 也在合法集內並不矛盾：狀態是由派工「開工」帶動的，
        // 開工了但還沒有人回報產出（QtyDone = 0）的工單確實還可以作廢。
        Assert.Equal(new[]
        {
            "Planned/production=False/issued=False",
            "Released/production=False/issued=False",
            "InProgress/production=False/issued=False",
            "Completed/production=False/issued=False",
        }, allowed);
    }

    [Theory]
    // 已結案／已作廢：狀態層先擋，不管旗標
    [InlineData(WorkOrderStatus.Closed,     false, false, WorkOrderGuard.CannotVoid)]
    [InlineData(WorkOrderStatus.Void,       false, false, WorkOrderGuard.CannotVoid)]
    [InlineData(WorkOrderStatus.Closed,     true,  true,  WorkOrderGuard.CannotVoid)]
    // 已回報產出：優先於已領料（產出已經存在，退料不是解法）
    [InlineData(WorkOrderStatus.InProgress, true,  false, WorkOrderGuard.HasProduction)]
    [InlineData(WorkOrderStatus.InProgress, true,  true,  WorkOrderGuard.HasProduction)]
    [InlineData(WorkOrderStatus.Released,   true,  false, WorkOrderGuard.HasProduction)]
    // 尚無產出但料已領出未退：作廢後沒有任何單據會來承接那批料，
    // 而生產領料不入總帳，帳面完全安靜——只有倉庫少了東西而查無去向
    [InlineData(WorkOrderStatus.Planned,    false, true,  WorkOrderGuard.HasIssuedMaterial)]
    [InlineData(WorkOrderStatus.Released,   false, true,  WorkOrderGuard.HasIssuedMaterial)]
    public void CanVoid_RejectionReasons(
        WorkOrderStatus status, bool hasProduction, bool hasIssuedMaterial, WorkOrderGuard expected)
    {
        Assert.Equal(expected, WorkOrderStateMachine.CanVoid(status, hasProduction, hasIssuedMaterial));
    }

    // ── 正常流 ───────────────────────────────────────────────────────────────

    [Fact]
    public void NormalFlow_PlannedToClosed_AllGuardsPass()
    {
        // 已規劃：可釋出、可作廢
        Assert.Equal(WorkOrderGuard.Allowed, WorkOrderStateMachine.CanRelease(WorkOrderStatus.Planned));
        Assert.Equal(WorkOrderGuard.Allowed, WorkOrderStateMachine.CanVoid(WorkOrderStatus.Planned, false, false));

        // 審核中：可撤回（撤回後回到 Planned 可修正再送審）
        Assert.Equal(WorkOrderGuard.Allowed,
            WorkOrderStateMachine.CanWithdraw(DocumentApprovalStatus.PendingApproval));

        // 已釋出：不可再釋出
        Assert.Equal(WorkOrderGuard.NotPlanned, WorkOrderStateMachine.CanRelease(WorkOrderStatus.Released));

        // 有產出之後：作廢被擋（先前只看 QtyDone，領了料沒回報的工單照樣作廢得掉）
        Assert.Equal(WorkOrderGuard.HasProduction,
            WorkOrderStateMachine.CanVoid(WorkOrderStatus.InProgress, true, false));
        Assert.Equal(WorkOrderGuard.HasIssuedMaterial,
            WorkOrderStateMachine.CanVoid(WorkOrderStatus.InProgress, false, true));

        // 入庫完成：終態，不可作廢
        Assert.Equal(WorkOrderGuard.CannotVoid,
            WorkOrderStateMachine.CanVoid(WorkOrderStatus.Closed, false, false));
    }
}
