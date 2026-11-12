using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class CoreSnapshotSyncApprovalV13 : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// CoreSnapshotSync 空 migration：merge upstream（Core PR #12 簽核強化 v1.3）後，
        /// merge=ours 保留的舊 snapshot 缺 ApprovalRequest.CreatedAt 與 xmin。
        /// DDL 已由 Core 的 20260713001636_Approval_CreatedAtAndXminConcurrency 套用
        /// （xmin 為 PG 系統欄位，本就零 DDL），此處僅同步 snapshot。
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
