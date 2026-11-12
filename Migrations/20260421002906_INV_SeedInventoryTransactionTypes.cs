using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class INV_SeedInventoryTransactionTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var ts = "TIMESTAMPTZ '2025-01-01T00:00:00Z'";
            migrationBuilder.Sql($@"
INSERT INTO ""InventoryTransactionTypes"" (""TypeCode"",""TypeName"",""TypeCategory"",""Direction"",""IsSystemType"",""RequireDoc"",""RequireLot"",""SourceTypeCode"",""IsActive"",""CreatedAt"",""UpdatedAt"") VALUES
('CI','樣品出','Sample',-1,TRUE,TRUE,FALSE,NULL,TRUE,{ts},{ts}),
('GS','訂單出貨','Sales',-1,TRUE,TRUE,FALSE,NULL,TRUE,{ts},{ts}),
('IA','庫存調整','Adjustment',1,TRUE,FALSE,FALSE,NULL,TRUE,{ts},{ts}),
('IB','借出','Loan',-1,TRUE,TRUE,FALSE,NULL,TRUE,{ts},{ts}),
('IJ','報廢','Scrap',-1,TRUE,FALSE,FALSE,NULL,TRUE,{ts},{ts}),
('IL','借入','Loan',1,TRUE,TRUE,FALSE,NULL,TRUE,{ts},{ts}),
('IO','生產領料','Production',-1,TRUE,TRUE,FALSE,NULL,TRUE,{ts},{ts}),
('IP','生產投入','Production',1,TRUE,TRUE,FALSE,NULL,TRUE,{ts},{ts}),
('IT','工單繳庫','Production',1,TRUE,TRUE,FALSE,NULL,TRUE,{ts},{ts}),
('NO','內部訂單出','InternalOrder',-1,TRUE,TRUE,FALSE,NULL,TRUE,{ts},{ts}),
('OI','委外進貨','Purchase',1,TRUE,TRUE,FALSE,NULL,TRUE,{ts},{ts}),
('OO','委外發料','Outsource',-1,TRUE,TRUE,FALSE,NULL,TRUE,{ts},{ts}),
('PI','進貨','Purchase',1,TRUE,TRUE,FALSE,NULL,TRUE,{ts},{ts}),
('RI','重工領料','Rework',-1,TRUE,TRUE,FALSE,NULL,TRUE,{ts},{ts}),
('SO','客供出','Sample',-1,TRUE,TRUE,FALSE,NULL,TRUE,{ts},{ts}),
('WP','調撥撥出','Transfer',-1,TRUE,TRUE,FALSE,NULL,TRUE,{ts},{ts}),
('BR','借出退回','Loan',1,TRUE,TRUE,FALSE,'IB',TRUE,{ts},{ts}),
('CR','樣品退回','Sample',1,TRUE,TRUE,FALSE,'CI',TRUE,{ts},{ts}),
('GR','銷貨退回','Sales',1,TRUE,TRUE,FALSE,'GS',TRUE,{ts},{ts}),
('IR','生產退料','Production',1,TRUE,TRUE,FALSE,'IO',TRUE,{ts},{ts}),
('LR','借入退還','Loan',-1,TRUE,TRUE,FALSE,'IL',TRUE,{ts},{ts}),
('NR','內部訂單退','InternalOrder',1,TRUE,TRUE,FALSE,'NO',TRUE,{ts},{ts}),
('OR','委外退料','Outsource',1,TRUE,TRUE,FALSE,'OO',TRUE,{ts},{ts}),
('OT','委外進貨退出','Purchase',-1,TRUE,TRUE,FALSE,'OI',TRUE,{ts},{ts}),
('PF','進貨退出','Purchase',-1,TRUE,TRUE,FALSE,'PI',TRUE,{ts},{ts}),
('RR','重工退料','Rework',1,TRUE,TRUE,FALSE,'RI',TRUE,{ts},{ts}),
('SR','客供退回','Sample',1,TRUE,TRUE,FALSE,'SO',TRUE,{ts},{ts}),
('WI','調撥撥入','Transfer',1,TRUE,TRUE,FALSE,'WP',TRUE,{ts},{ts})
ON CONFLICT (""TypeCode"") DO NOTHING;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "BR");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "CR");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "GR");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "IA");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "IJ");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "IP");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "IR");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "IT");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "LR");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "NR");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "OR");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "OT");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "PF");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "RR");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "SR");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "WI");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "CI");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "GS");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "IB");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "IL");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "IO");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "NO");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "OI");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "OO");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "PI");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "RI");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "SO");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "WP");
        }
    }
}
