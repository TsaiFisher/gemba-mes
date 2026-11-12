using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class BAS_Tooling_ShotMaintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AccumulatedShots",
                table: "Toolings",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "PmIntervalShots",
                table: "Toolings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductsPerCavity",
                table: "Toolings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccumulatedShots",
                table: "Toolings");

            migrationBuilder.DropColumn(
                name: "PmIntervalShots",
                table: "Toolings");

            migrationBuilder.DropColumn(
                name: "ProductsPerCavity",
                table: "Toolings");
        }
    }
}
