using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Tests.StateMachines;

/// <summary>
/// 採購單狀態機矩陣（Phase 4-3）。斷言形式同進貨單，見
/// <see cref="GoodsReceiptStateMachineTests"/> 的說明。
///
/// 採購單是四張單裡唯一沒有 DocStatus 的：它的生命週期完全由簽核狀態
/// 加上明細層事實（已進貨量、IsClosed）決定，所以矩陣的維度比另外三張少一個。
/// </summary>
public class PurchaseOrderStateMachineTests
{
    private static readonly DocumentApprovalStatus[] AllApprovals = Enum.GetValues<DocumentApprovalStatus>();
    private static readonly bool[] Bools = { false, true };

    // ── 修改 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CanEdit_AllowedCells()
    {
        var allowed = AllApprovals
            .Where(a => PurchaseOrderStateMachine.CanEdit(a) == PurchaseOrderGuard.Allowed)
            .ToArray();

        Assert.Equal(
            new[] { DocumentApprovalStatus.Draft, DocumentApprovalStatus.Approved, DocumentApprovalStatus.Rejected },
            allowed);
        Assert.Equal(PurchaseOrderGuard.PendingApproval,
                     PurchaseOrderStateMachine.CanEdit(DocumentApprovalStatus.PendingApproval));
    }

    // ── 刪除 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CanDelete_AllowedCells()
    {
        var allowed = (from a in AllApprovals
                       from hasReceipts in Bools
                       where PurchaseOrderStateMachine.CanDelete(a, hasReceipts) == PurchaseOrderGuard.Allowed
                       select $"{a}/receipts={hasReceipts}")
                      .ToArray();

        Assert.Equal(new[]
        {
            "Draft/receipts=False",
            "Approved/receipts=False",
            "Rejected/receipts=False",
        }, allowed);
    }

    [Theory]
    // 已有進貨 → 最優先（進貨明細指回採購明細，採購單先消失會讓來源憑空不見）
    [InlineData(DocumentApprovalStatus.Draft,           true,  PurchaseOrderGuard.HasReceipts)]
    [InlineData(DocumentApprovalStatus.Approved,        true,  PurchaseOrderGuard.HasReceipts)]
    [InlineData(DocumentApprovalStatus.PendingApproval, true,  PurchaseOrderGuard.HasReceipts)]
    // 無進貨但審核中 → 刪掉會留下指向不存在單據的簽核申請
    [InlineData(DocumentApprovalStatus.PendingApproval, false, PurchaseOrderGuard.PendingApproval)]
    public void CanDelete_RejectionReasons(
        DocumentApprovalStatus approval, bool hasReceipts, PurchaseOrderGuard expected)
    {
        Assert.Equal(expected, PurchaseOrderStateMachine.CanDelete(approval, hasReceipts));
    }

    // ── 結案 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CanClose_AllowedCells()
    {
        var allowed = AllApprovals
            .Where(a => PurchaseOrderStateMachine.CanClose(a) == PurchaseOrderGuard.Allowed)
            .ToArray();

        // 未核准的採購單沒有在途供給可言，不該結案
        Assert.Equal(new[] { DocumentApprovalStatus.Approved }, allowed);
    }

    [Theory]
    [InlineData(DocumentApprovalStatus.Draft,           PurchaseOrderGuard.NeedsApproval)]
    [InlineData(DocumentApprovalStatus.PendingApproval, PurchaseOrderGuard.NeedsApproval)]
    [InlineData(DocumentApprovalStatus.Rejected,        PurchaseOrderGuard.NeedsApproval)]
    public void CanClose_RejectionReasons(DocumentApprovalStatus approval, PurchaseOrderGuard expected)
    {
        Assert.Equal(expected, PurchaseOrderStateMachine.CanClose(approval));
    }

    // ── 正常流 ───────────────────────────────────────────────────────────────

    [Fact]
    public void NormalFlow_DraftToClosed_AllGuardsPass()
    {
        // 草稿：可改、可刪
        Assert.Equal(PurchaseOrderGuard.Allowed,
            PurchaseOrderStateMachine.CanEdit(DocumentApprovalStatus.Draft));
        Assert.Equal(PurchaseOrderGuard.Allowed,
            PurchaseOrderStateMachine.CanDelete(DocumentApprovalStatus.Draft, false));

        // 審核中：不可改、不可刪、不可結案
        Assert.NotEqual(PurchaseOrderGuard.Allowed,
            PurchaseOrderStateMachine.CanEdit(DocumentApprovalStatus.PendingApproval));
        Assert.NotEqual(PurchaseOrderGuard.Allowed,
            PurchaseOrderStateMachine.CanDelete(DocumentApprovalStatus.PendingApproval, false));
        Assert.NotEqual(PurchaseOrderGuard.Allowed,
            PurchaseOrderStateMachine.CanClose(DocumentApprovalStatus.PendingApproval));

        // 已核准：可結案；一旦進過貨就不可刪
        Assert.Equal(PurchaseOrderGuard.Allowed,
            PurchaseOrderStateMachine.CanClose(DocumentApprovalStatus.Approved));
        Assert.Equal(PurchaseOrderGuard.HasReceipts,
            PurchaseOrderStateMachine.CanDelete(DocumentApprovalStatus.Approved, true));
    }
}
