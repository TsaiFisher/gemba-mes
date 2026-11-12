using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_DispatchReportXminConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "ProductionReports");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Dispatches");

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "ProductionReports",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Dispatches",
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
                table: "ProductionReports");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Dispatches");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "ProductionReports",
                type: "bytea",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Dispatches",
                type: "bytea",
                rowVersion: true,
                nullable: true);
        }
    }
}
