using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class PUR_OutsourceReceipt_FinalReceiptAndCarriedInCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFinalReceipt",
                table: "OutsourceReceipts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "CarriedInMaterialCost",
                table: "OutsourceReceiptLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsFinalReceipt",
                table: "OutsourceReceipts");

            migrationBuilder.DropColumn(
                name: "CarriedInMaterialCost",
                table: "OutsourceReceiptLines");
        }
    }
}
