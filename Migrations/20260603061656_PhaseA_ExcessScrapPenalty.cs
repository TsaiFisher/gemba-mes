using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class PhaseA_ExcessScrapPenalty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AllowedScrapRate",
                table: "OutsourceReceiptLines",
                type: "numeric(8,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ExcessScrapPenalty",
                table: "OutsourceReceiptLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedScrapRate",
                table: "OutsourceReceiptLines");

            migrationBuilder.DropColumn(
                name: "ExcessScrapPenalty",
                table: "OutsourceReceiptLines");
        }
    }
}
