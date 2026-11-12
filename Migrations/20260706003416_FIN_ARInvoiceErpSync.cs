using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class FIN_ARInvoiceErpSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ErpErrorMessage",
                table: "ARInvoices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ErpRetryCount",
                table: "ARInvoices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ErpSyncStatus",
                table: "ARInvoices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ErpSyncTime",
                table: "ARInvoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErpVoucherNo",
                table: "ARInvoices",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ErpErrorMessage",
                table: "ARInvoices");

            migrationBuilder.DropColumn(
                name: "ErpRetryCount",
                table: "ARInvoices");

            migrationBuilder.DropColumn(
                name: "ErpSyncStatus",
                table: "ARInvoices");

            migrationBuilder.DropColumn(
                name: "ErpSyncTime",
                table: "ARInvoices");

            migrationBuilder.DropColumn(
                name: "ErpVoucherNo",
                table: "ARInvoices");
        }
    }
}
