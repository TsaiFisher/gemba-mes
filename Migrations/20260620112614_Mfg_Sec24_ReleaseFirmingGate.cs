using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class Mfg_Sec24_ReleaseFirmingGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "GrossDemandQty",
                table: "WorkOrders",
                type: "numeric(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "SourceMrpRunId",
                table: "WorkOrders",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceMrpRunLineSeq",
                table: "WorkOrders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Category",
                table: "InventoryReservations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpectedArrivalDate",
                table: "InventoryReservations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupplyDocId",
                table: "InventoryReservations",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupplyDocType",
                table: "InventoryReservations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SupplyLineNo",
                table: "InventoryReservations",
                type: "integer",
                nullable: true);

            // xmin 為 PostgreSQL 系統欄位（UseXminAsConcurrencyToken），無需 DDL；
            // 與其他 xmin 並發實體一致，不在 migration 產生 AddColumn（系統欄位已存在）。

            migrationBuilder.CreateIndex(
                name: "IX_InventoryReservations_SupplyDocType_SupplyDocId",
                table: "InventoryReservations",
                columns: new[] { "SupplyDocType", "SupplyDocId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InventoryReservations_SupplyDocType_SupplyDocId",
                table: "InventoryReservations");

            migrationBuilder.DropColumn(
                name: "GrossDemandQty",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "SourceMrpRunId",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "SourceMrpRunLineSeq",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "InventoryReservations");

            migrationBuilder.DropColumn(
                name: "ExpectedArrivalDate",
                table: "InventoryReservations");

            migrationBuilder.DropColumn(
                name: "SupplyDocId",
                table: "InventoryReservations");

            migrationBuilder.DropColumn(
                name: "SupplyDocType",
                table: "InventoryReservations");

            migrationBuilder.DropColumn(
                name: "SupplyLineNo",
                table: "InventoryReservations");
        }
    }
}
