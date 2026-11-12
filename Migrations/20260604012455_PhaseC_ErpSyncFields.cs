using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class PhaseC_ErpSyncFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ErpErrorMessage",
                table: "APInvoices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ErpRetryCount",
                table: "APInvoices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ErpSyncStatus",
                table: "APInvoices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ErpSyncTime",
                table: "APInvoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErpVoucherNo",
                table: "APInvoices",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ErpErrorMessage",
                table: "APInvoices");

            migrationBuilder.DropColumn(
                name: "ErpRetryCount",
                table: "APInvoices");

            migrationBuilder.DropColumn(
                name: "ErpSyncStatus",
                table: "APInvoices");

            migrationBuilder.DropColumn(
                name: "ErpSyncTime",
                table: "APInvoices");

            migrationBuilder.DropColumn(
                name: "ErpVoucherNo",
                table: "APInvoices");
        }
    }
}
