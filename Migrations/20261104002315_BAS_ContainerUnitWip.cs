using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class BAS_ContainerUnitWip : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContainerUnitId",
                table: "ProductionReportContainers",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OperationSeq",
                table: "ContainerUnits",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkOrderId",
                table: "ContainerUnits",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OperationSeq",
                table: "ContainerUnitHistories",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkOrderId",
                table: "ContainerUnitHistories",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionReportContainers_ContainerUnitId",
                table: "ProductionReportContainers",
                column: "ContainerUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ContainerUnits_WorkOrderId",
                table: "ContainerUnits",
                column: "WorkOrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProductionReportContainers_ContainerUnitId",
                table: "ProductionReportContainers");

            migrationBuilder.DropIndex(
                name: "IX_ContainerUnits_WorkOrderId",
                table: "ContainerUnits");

            migrationBuilder.DropColumn(
                name: "ContainerUnitId",
                table: "ProductionReportContainers");

            migrationBuilder.DropColumn(
                name: "OperationSeq",
                table: "ContainerUnits");

            migrationBuilder.DropColumn(
                name: "WorkOrderId",
                table: "ContainerUnits");

            migrationBuilder.DropColumn(
                name: "OperationSeq",
                table: "ContainerUnitHistories");

            migrationBuilder.DropColumn(
                name: "WorkOrderId",
                table: "ContainerUnitHistories");
        }
    }
}
