using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class INV_DocHeaderXminRollout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "StockTransfers",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "StockAdjustments",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "SampleDocs",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "ReworkMaterialDocs",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "OutsourceMaterialDocs",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "LoanDocs",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "InventoryCountSheets",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "CrossPlantTransfers",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "SampleDocs");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "ReworkMaterialDocs");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "OutsourceMaterialDocs");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "LoanDocs");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "InventoryCountSheets");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "CrossPlantTransfers");
        }
    }
}
