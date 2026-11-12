using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class PUR_PhaseF_MfgLinksAndNcrReturnRework : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DispatchId",
                table: "OutsourceMaterialDocs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutsourcePurchaseOrderId",
                table: "OutsourceMaterialDocs",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutsourceReworkId",
                table: "NonConformanceReports",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DispatchId",
                table: "OutsourceMaterialDocs");

            migrationBuilder.DropColumn(
                name: "OutsourcePurchaseOrderId",
                table: "OutsourceMaterialDocs");

            migrationBuilder.DropColumn(
                name: "OutsourceReworkId",
                table: "NonConformanceReports");
        }
    }
}
