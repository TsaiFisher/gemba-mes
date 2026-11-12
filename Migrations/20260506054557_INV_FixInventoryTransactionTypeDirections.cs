using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class INV_FixInventoryTransactionTypeDirections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "BR",
                columns: new[] { "Direction", "TypeName" },
                values: new object[] { -1, "借入退回" });

            migrationBuilder.UpdateData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "CI",
                columns: new[] { "Direction", "TypeName" },
                values: new object[] { 1, "客供進貨" });

            migrationBuilder.UpdateData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "CR",
                columns: new[] { "Direction", "TypeName" },
                values: new object[] { -1, "客供退回" });

            migrationBuilder.UpdateData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "IB",
                columns: new[] { "Direction", "TypeName" },
                values: new object[] { 1, "借入" });

            migrationBuilder.UpdateData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "IL",
                columns: new[] { "Direction", "TypeName" },
                values: new object[] { -1, "借出" });

            migrationBuilder.UpdateData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "LR",
                columns: new[] { "Direction", "TypeName" },
                values: new object[] { 1, "借出退回" });

            migrationBuilder.UpdateData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "SO",
                column: "TypeName",
                value: "樣品出貨");

            migrationBuilder.UpdateData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "SR",
                column: "TypeName",
                value: "樣品退回");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "BR",
                columns: new[] { "Direction", "TypeName" },
                values: new object[] { 1, "借出退回" });

            migrationBuilder.UpdateData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "CI",
                columns: new[] { "Direction", "TypeName" },
                values: new object[] { -1, "樣品出" });

            migrationBuilder.UpdateData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "CR",
                columns: new[] { "Direction", "TypeName" },
                values: new object[] { 1, "樣品退回" });

            migrationBuilder.UpdateData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "IB",
                columns: new[] { "Direction", "TypeName" },
                values: new object[] { -1, "借出" });

            migrationBuilder.UpdateData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "IL",
                columns: new[] { "Direction", "TypeName" },
                values: new object[] { 1, "借入" });

            migrationBuilder.UpdateData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "LR",
                columns: new[] { "Direction", "TypeName" },
                values: new object[] { -1, "借入退還" });

            migrationBuilder.UpdateData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "SO",
                column: "TypeName",
                value: "客供出");

            migrationBuilder.UpdateData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "SR",
                column: "TypeName",
                value: "客供退回");
        }
    }
}
