using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <summary>
    /// merge upstream（簽核 v1.4：代理人＋自動跳簽）後的 snapshot 校正 migration。
    /// Up/Down 刻意留空：SkipSameApprover / ActiveDelegatorId / OnBehalfOfUserId 欄位與
    /// ApprovalDelegations 表的 DDL 已由 Core 的 20260713060501_Approval_DelegationAndSkip
    /// 套用（.gitattributes merge=ours 保留舊 snapshot 所致，僅需更新 snapshot 本身）。
    /// </summary>
    public partial class CoreSnapshotSyncApprovalV14 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
