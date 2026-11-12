using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Tests.StateMachines;

/// <summary>
/// 進貨單狀態機矩陣（Phase 4-3）。
///
/// 兩種斷言各有分工：
///   1. <b>合法格全集</b>——把「狀態 × 簽核狀態 × 旗標」的整個笛卡爾積跑完，
///      收集所有回傳 Allowed 的格子，與寫死的期望集合逐字比對。
///      這一條的價值在於它是**列舉**而非條件式的複寫：守衛的判斷改了、
///      或 <see cref="DocStatus"/> 之後多一個成員而守衛沒跟著處理，
///      合法格集合就會多／少一格而直接紅。
///   2. <b>拒絕原因表</b>——手寫代表格，釘住訊息優先順序（同一格同時違反兩條時
///      使用者看到哪一句）。優先順序錯了不會有任何功能異常，只會讓人看到
///      「僅草稿可刪除」而不是「倉庫已經開始上架」，然後往錯的方向查。
/// </summary>
public class GoodsReceiptStateMachineTests
{
    // ── 笛卡爾積工具 ─────────────────────────────────────────────────────────

    private static readonly DocStatus[] AllStatuses = Enum.GetValues<DocStatus>();
    private static readonly DocumentApprovalStatus[] AllApprovals = Enum.GetValues<DocumentApprovalStatus>();
    private static readonly bool[] Bools = { false, true };

    // ── 修改 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CanEdit_AllowedCells()
    {
        var allowed = AllApprovals
            .Where(a => GoodsReceiptStateMachine.CanEdit(a) == GoodsReceiptGuard.Allowed)
            .ToArray();

        // 退回（Rejected）可修改後重新送審，故也在合法集內
        Assert.Equal(
            new[] { DocumentApprovalStatus.Draft, DocumentApprovalStatus.Approved, DocumentApprovalStatus.Rejected },
            allowed);
        Assert.Equal(GoodsReceiptGuard.PendingApproval,
                     GoodsReceiptStateMachine.CanEdit(DocumentApprovalStatus.PendingApproval));
    }

    // ── 刪除 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CanDelete_AllowedCells()
    {
        var allowed = (from s in AllStatuses
                       from a in AllApprovals
                       from putAway in Bools
                       from returns in Bools
                       where GoodsReceiptStateMachine.CanDelete(s, a, putAway, returns) == GoodsReceiptGuard.Allowed
                       select $"{s}/{a}/putAway={putAway}/returns={returns}")
                      .ToArray();

        // 唯一可刪的是「草稿、未在審、倉庫沒動過、沒退貨」
        Assert.Equal(new[]
        {
            "Draft/Draft/putAway=False/returns=False",
            "Draft/Approved/putAway=False/returns=False",
            "Draft/Rejected/putAway=False/returns=False",
        }, allowed);
    }

    [Theory]
    // 倉庫已動 → 最優先（不論狀態與簽核）
    [InlineData(DocStatus.Draft,     DocumentApprovalStatus.Draft,           true,  false, GoodsReceiptGuard.WarehouseStarted)]
    [InlineData(DocStatus.Posted,    DocumentApprovalStatus.Approved,        true,  true,  GoodsReceiptGuard.WarehouseStarted)]
    // 已退貨 → 次優先
    [InlineData(DocStatus.Posted,    DocumentApprovalStatus.Approved,        false, true,  GoodsReceiptGuard.HasReturns)]
    // 狀態層底線：非草稿
    [InlineData(DocStatus.Confirmed, DocumentApprovalStatus.Approved,        false, false, GoodsReceiptGuard.NotDraft)]
    [InlineData(DocStatus.Posted,    DocumentApprovalStatus.Approved,        false, false, GoodsReceiptGuard.NotDraft)]
    [InlineData(DocStatus.Void,      DocumentApprovalStatus.Approved,        false, false, GoodsReceiptGuard.NotDraft)]
    // 草稿但審核中：刪掉會留下指向不存在單據的簽核申請
    [InlineData(DocStatus.Draft,     DocumentApprovalStatus.PendingApproval, false, false, GoodsReceiptGuard.PendingApproval)]
    public void CanDelete_RejectionReasons(
        DocStatus status, DocumentApprovalStatus approval,
        bool hasPutAway, bool hasReturns, GoodsReceiptGuard expected)
    {
        Assert.Equal(expected, GoodsReceiptStateMachine.CanDelete(status, approval, hasPutAway, hasReturns));
    }

    // ── 確認 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CanConfirm_AllowedCells()
    {
        var allowed = (from s in AllStatuses
                       from a in AllApprovals
                       from hasLines in Bools
                       where GoodsReceiptStateMachine.CanConfirm(s, a, hasLines) == GoodsReceiptGuard.Allowed
                       select $"{s}/{a}/lines={hasLines}")
                      .ToArray();

        Assert.Equal(new[] { "Draft/Approved/lines=True" }, allowed);
    }

    [Theory]
    [InlineData(DocStatus.Confirmed, DocumentApprovalStatus.Approved,        true,  GoodsReceiptGuard.NotDraft)]
    [InlineData(DocStatus.Posted,    DocumentApprovalStatus.Approved,        true,  GoodsReceiptGuard.NotDraft)]
    [InlineData(DocStatus.Void,      DocumentApprovalStatus.Approved,        true,  GoodsReceiptGuard.NotDraft)]
    [InlineData(DocStatus.Draft,     DocumentApprovalStatus.Draft,           true,  GoodsReceiptGuard.NeedsApproval)]
    [InlineData(DocStatus.Draft,     DocumentApprovalStatus.PendingApproval, true,  GoodsReceiptGuard.NeedsApproval)]
    [InlineData(DocStatus.Draft,     DocumentApprovalStatus.Rejected,        true,  GoodsReceiptGuard.NeedsApproval)]
    // 空單不得確認：過帳零行等於單據狀態走到 Posted 卻沒有任何庫存異動
    [InlineData(DocStatus.Draft,     DocumentApprovalStatus.Approved,        false, GoodsReceiptGuard.NoLines)]
    public void CanConfirm_RejectionReasons(
        DocStatus status, DocumentApprovalStatus approval, bool hasLines, GoodsReceiptGuard expected)
    {
        Assert.Equal(expected, GoodsReceiptStateMachine.CanConfirm(status, approval, hasLines));
    }

    // ── 取消確認 ─────────────────────────────────────────────────────────────

    [Fact]
    public void CanCancelConfirm_AllowedCells()
    {
        var allowed = (from s in AllStatuses
                       from putAway in Bools
                       where GoodsReceiptStateMachine.CanCancelConfirm(s, putAway) == GoodsReceiptGuard.Allowed
                       select $"{s}/putAway={putAway}")
                      .ToArray();

        Assert.Equal(new[] { "Confirmed/putAway=False" }, allowed);
    }

    [Theory]
    [InlineData(DocStatus.Draft,     false, GoodsReceiptGuard.NotConfirmed)]
    [InlineData(DocStatus.Posted,    false, GoodsReceiptGuard.NotConfirmed)]
    [InlineData(DocStatus.Void,      false, GoodsReceiptGuard.NotConfirmed)]
    // 已確認但倉庫開始上架：退回草稿會讓已上架的量失去單據依據
    [InlineData(DocStatus.Confirmed, true,  GoodsReceiptGuard.WarehouseStarted)]
    public void CanCancelConfirm_RejectionReasons(DocStatus status, bool hasPutAway, GoodsReceiptGuard expected)
    {
        Assert.Equal(expected, GoodsReceiptStateMachine.CanCancelConfirm(status, hasPutAway));
    }

    // ── 作廢 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CanVoid_AllowedCells()
    {
        var allowed = AllStatuses
            .Where(s => GoodsReceiptStateMachine.CanVoid(s) == GoodsReceiptGuard.Allowed)
            .ToArray();

        // 作廢＝以反向異動沖銷，沒過帳就沒有東西可沖
        Assert.Equal(new[] { DocStatus.Posted }, allowed);
    }

    // ── 正常流：每一步的守衛都要放行下一步 ───────────────────────────────────

    [Fact]
    public void NormalFlow_DraftToVoid_AllGuardsPass()
    {
        // 草稿＋已核准＋有明細 → 可確認
        Assert.Equal(GoodsReceiptGuard.Allowed,
            GoodsReceiptStateMachine.CanConfirm(DocStatus.Draft, DocumentApprovalStatus.Approved, true));

        // 已確認、倉庫還沒動 → 可反悔退回草稿
        Assert.Equal(GoodsReceiptGuard.Allowed,
            GoodsReceiptStateMachine.CanCancelConfirm(DocStatus.Confirmed, false));

        // 已過帳 → 不可刪、只能作廢
        Assert.NotEqual(GoodsReceiptGuard.Allowed,
            GoodsReceiptStateMachine.CanDelete(DocStatus.Posted, DocumentApprovalStatus.Approved, false, false));
        Assert.Equal(GoodsReceiptGuard.Allowed, GoodsReceiptStateMachine.CanVoid(DocStatus.Posted));

        // 已作廢 → 什麼都不能做
        Assert.NotEqual(GoodsReceiptGuard.Allowed,
            GoodsReceiptStateMachine.CanConfirm(DocStatus.Void, DocumentApprovalStatus.Approved, true));
        Assert.NotEqual(GoodsReceiptGuard.Allowed, GoodsReceiptStateMachine.CanVoid(DocStatus.Void));
        Assert.NotEqual(GoodsReceiptGuard.Allowed,
            GoodsReceiptStateMachine.CanDelete(DocStatus.Void, DocumentApprovalStatus.Approved, false, false));
    }
}
