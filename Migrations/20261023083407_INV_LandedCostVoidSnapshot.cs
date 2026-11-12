using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class INV_LandedCostVoidSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MovementCountAtConfirm",
                table: "LandedCostAllocationLines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OutflowQtyAtConfirm",
                table: "LandedCostAllocationLines",
                type: "numeric(18,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MovementCountAtConfirm",
                table: "LandedCostAllocationLines");

            migrationBuilder.DropColumn(
                name: "OutflowQtyAtConfirm",
                table: "LandedCostAllocationLines");
        }
    }
}
