using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class DataAuth_BusinessDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "WorkOrders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "WorkOrders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "StockTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "StockTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "StockAdjustments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "StockAdjustments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "ScrapDocs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "ScrapDocs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "SampleDocs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "SampleDocs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "SalesReturns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "SalesReturns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "SalesOrders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "SalesOrders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "ReworkMaterialDocs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "ReworkMaterialDocs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "PurchaseRequests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "PurchaseRequests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "PurchaseOrders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "PurchaseOrders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "ProductionBatches",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "ProductionBatches",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "OutsourceMaterialDocs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "OutsourceMaterialDocs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "MrpRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "MrpRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "LoanDocs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "LoanDocs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "InternalOrders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "InternalOrders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "GoodsShipments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "GoodsShipments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "GoodsReturns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "GoodsReturns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "GoodsReceipts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "GoodsReceipts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "Dispatches",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "Dispatches",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByRoleId",
                table: "CrossPlantTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationUnitPath",
                table: "CrossPlantTransfers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "ScrapDocs");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "ScrapDocs");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "SampleDocs");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "SampleDocs");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "SalesReturns");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "SalesReturns");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "ReworkMaterialDocs");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "ReworkMaterialDocs");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "ProductionBatches");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "ProductionBatches");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "OutsourceMaterialDocs");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "OutsourceMaterialDocs");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "MrpRuns");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "MrpRuns");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "LoanDocs");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "LoanDocs");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "InternalOrders");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "InternalOrders");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "GoodsShipments");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "GoodsShipments");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "GoodsReturns");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "GoodsReturns");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "GoodsReceipts");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "GoodsReceipts");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "Dispatches");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "Dispatches");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "CrossPlantTransfers");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "CrossPlantTransfers");
        }
    }
}
