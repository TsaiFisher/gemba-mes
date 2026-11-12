using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class INV_SeedInventoryCountTransactionType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "InventoryTransactionTypes",
                columns: new[] { "TypeCode", "CreatedAt", "Direction", "IsActive", "IsSystemType", "RequireDoc", "RequireLot", "SourceTypeCode", "TypeCategory", "TypeName", "UpdatedAt" },
                values: new object[] { "IC", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), 1, true, true, true, false, null, "Adjustment", "盤點盈虧", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "IC");
        }
    }
}
