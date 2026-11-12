using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class Approval_CreatedAtAndXminConcurrency : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// xmin 為 PostgreSQL 系統欄位（UseXminAsConcurrencyToken 零 DDL），
        /// 已自 scaffold 產物移除 AddColumn/DropColumn——實際執行會撞系統欄位。
        /// CreatedAt 既有列以套用當下時間補值（無法回溯真實送審時間）。
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "ApprovalRequests",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "ApprovalRequests");
        }
    }
}
