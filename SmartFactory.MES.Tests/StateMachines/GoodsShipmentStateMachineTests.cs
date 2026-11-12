using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Tests.StateMachines;

/// <summary>
/// 出貨單狀態機矩陣（Phase 4-3）。斷言形式同進貨單，見
/// <see cref="GoodsReceiptStateMachineTests"/> 的說明。
///
/// 出貨單與進貨單的狀態流向對稱，但守衛並不完全對稱——兩處刻意的差異在
/// 下面的測試裡各有註記（確認允許 Confirmed 再按一次、以及 InTransit 這一格）。
/// 這裡的目的不是要求兩張單長得一樣，而是讓差異變成「寫下來的決定」而非漂移。
/// </summary>
public class GoodsShipmentStateMachineTests
{
    private static readonly DocStatus[] AllStatuses = Enum.GetValues<DocStatus>();
    private static readonly DocumentApprovalStatus[] AllApprovals = Enum.GetValues<DocumentApprovalStatus>();
    private static readonly bool[] Bools = { false, true };

    // ── 修改 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CanEdit_AllowedCells()
    {
        var allowed = AllApprovals
            .Where(a => GoodsShipmentStateMachine.CanEdit(a) == GoodsShipmentGuard.Allowed)
            .ToArray();

        Assert.Equal(
            new[] { DocumentApprovalStatus.Draft, DocumentApprovalStatus.Approved, DocumentApprovalStatus.Rejected },
            allowed);
        Assert.Equal(GoodsShipmentGuard.PendingApproval,
                     GoodsShipmentStateMachine.CanEdit(DocumentApprovalStatus.PendingApproval));
    }

    // ── 確認 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CanConfirm_AllowedCells()
    {
        var allowed = (from s in AllStatuses
                       from a in AllApprovals
                       from hasLines in Bools
                       where GoodsShipmentStateMachine.CanConfirm(s, a, hasLines) == GoodsShipmentGuard.Allowed
                       select $"{s}/{a}/lines={hasLines}")
                      .ToArray();

        // 與進貨單（只允許 Draft）不同的兩格，都是刻意保留的現況：
        //   Confirmed —— 供「先確認、之後才打開簡化模式」的部署再按一次確認完成過帳；
        //                重複過帳由 InventoryPostingService 的逐行冪等性檢查擋住。
        //   InTransit —— 出貨單實際上永遠不會進入這個狀態（那是跨廠調撥用的），
        //                守衛沒有特別排除它，所以它出現在這張表上。
        //                列在這裡是為了讓它是「已知且無害」而不是「沒人看過」。
        Assert.Equal(new[]
        {
            "Draft/Approved/lines=True",
            "Confirmed/Approved/lines=True",
            "InTransit/Approved/lines=True",
        }, allowed);
    }

    [Theory]
    [InlineData(DocStatus.Posted,    DocumentApprovalStatus.Approved,        true,  GoodsShipmentGuard.AlreadyPosted)]
    [InlineData(DocStatus.Void,      DocumentApprovalStatus.Approved,        true,  GoodsShipmentGuard.AlreadyVoided)]
    [InlineData(DocStatus.Draft,     DocumentApprovalStatus.Draft,           true,  GoodsShipmentGuard.NeedsApproval)]
    [InlineData(DocStatus.Draft,     DocumentApprovalStatus.PendingApproval, true,  GoodsShipmentGuard.NeedsApproval)]
    [InlineData(DocStatus.Draft,     DocumentApprovalStatus.Rejected,        true,  GoodsShipmentGuard.NeedsApproval)]
    [InlineData(DocStatus.Draft,     DocumentApprovalStatus.Approved,        false, GoodsShipmentGuard.NoLines)]
    // 已過帳優先於未核准：兩者同時成立時先講「已過帳」
    [InlineData(DocStatus.Posted,    DocumentApprovalStatus.Draft,           true,  GoodsShipmentGuard.AlreadyPosted)]
    public void CanConfirm_RejectionReasons(
        DocStatus status, DocumentApprovalStatus approval, bool hasLines, GoodsShipmentGuard expected)
    {
        Assert.Equal(expected, GoodsShipmentStateMachine.CanConfirm(status, approval, hasLines));
    }

    // ── 取消確認 ─────────────────────────────────────────────────────────────

    [Fact]
    public void CanCancelConfirm_AllowedCells()
    {
        var allowed = (from s in AllStatuses
                       from picking in Bools
                       where GoodsShipmentStateMachine.CanCancelConfirm(s, picking) == GoodsShipmentGuard.Allowed
                       select $"{s}/picking={picking}")
                      .ToArray();

        Assert.Equal(new[] { "Confirmed/picking=False" }, allowed);
    }

    [Theory]
    [InlineData(DocStatus.Draft,     false, GoodsShipmentGuard.NotConfirmed)]
    [InlineData(DocStatus.Posted,    false, GoodsShipmentGuard.NotConfirmed)]
    [InlineData(DocStatus.Void,      false, GoodsShipmentGuard.NotConfirmed)]
    [InlineData(DocStatus.Confirmed, true,  GoodsShipmentGuard.HasPicking)]
    public void CanCancelConfirm_RejectionReasons(DocStatus status, bool hasPicking, GoodsShipmentGuard expected)
    {
        Assert.Equal(expected, GoodsShipmentStateMachine.CanCancelConfirm(status, hasPicking));
    }

    // ── 作廢 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CanVoid_AllowedCells()
    {
        var allowed = AllStatuses
            .Where(s => GoodsShipmentStateMachine.CanVoid(s) == GoodsShipmentGuard.Allowed)
            .ToArray();

        Assert.Equal(new[] { DocStatus.Posted }, allowed);
    }

    // ── 刪除 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CanDelete_AllowedCells()
    {
        var allowed = (from s in AllStatuses
                       from a in AllApprovals
                       where GoodsShipmentStateMachine.CanDelete(s, a) == GoodsShipmentGuard.Allowed
                       select $"{s}/{a}")
                      .ToArray();

        Assert.Equal(new[]
        {
            "Draft/Draft",
            "Draft/Approved",
            "Draft/Rejected",
        }, allowed);
    }

    [Theory]
    [InlineData(DocStatus.Confirmed, DocumentApprovalStatus.Approved,        GoodsShipmentGuard.NotDraft)]
    [InlineData(DocStatus.Posted,    DocumentApprovalStatus.Approved,        GoodsShipmentGuard.NotDraft)]
    [InlineData(DocStatus.Void,      DocumentApprovalStatus.Approved,        GoodsShipmentGuard.NotDraft)]
    [InlineData(DocStatus.Draft,     DocumentApprovalStatus.PendingApproval, GoodsShipmentGuard.PendingApproval)]
    public void CanDelete_RejectionReasons(
        DocStatus status, DocumentApprovalStatus approval, GoodsShipmentGuard expected)
    {
        Assert.Equal(expected, GoodsShipmentStateMachine.CanDelete(status, approval));
    }

    // ── 正常流 ───────────────────────────────────────────────────────────────

    [Fact]
    public void NormalFlow_DraftToVoid_AllGuardsPass()
    {
        Assert.Equal(GoodsShipmentGuard.Allowed,
            GoodsShipmentStateMachine.CanConfirm(DocStatus.Draft, DocumentApprovalStatus.Approved, true));
        Assert.Equal(GoodsShipmentGuard.Allowed,
            GoodsShipmentStateMachine.CanCancelConfirm(DocStatus.Confirmed, false));
        Assert.Equal(GoodsShipmentGuard.Allowed, GoodsShipmentStateMachine.CanVoid(DocStatus.Posted));

        // 已過帳不可刪（刪掉會留下無主的庫存異動與應收發票）
        Assert.Equal(GoodsShipmentGuard.NotDraft,
            GoodsShipmentStateMachine.CanDelete(DocStatus.Posted, DocumentApprovalStatus.Approved));
    }
}
