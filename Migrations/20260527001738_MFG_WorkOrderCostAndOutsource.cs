using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_WorkOrderCostAndOutsource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ActualLaborCost",
                table: "WorkOrders",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ActualMaterialCost",
                table: "WorkOrders",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ActualOverheadCost",
                table: "WorkOrders",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "IsOutsource",
                table: "WorkOrders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "OutsourceExpectedDeliveryDate",
                table: "WorkOrders",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutsourcePurchaseOrderId",
                table: "WorkOrders",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutsourceSupplierCode",
                table: "WorkOrders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StandardLaborCost",
                table: "WorkOrders",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "StandardMaterialCost",
                table: "WorkOrders",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "StandardOverheadCost",
                table: "WorkOrders",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitCost",
                table: "WorkOrderMaterials",
                type: "numeric(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MachineHourlyRate",
                table: "WorkCenters",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActualLaborCost",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "ActualMaterialCost",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "ActualOverheadCost",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "IsOutsource",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "OutsourceExpectedDeliveryDate",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "OutsourcePurchaseOrderId",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "OutsourceSupplierCode",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "StandardLaborCost",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "StandardMaterialCost",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "StandardOverheadCost",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "UnitCost",
                table: "WorkOrderMaterials");

            migrationBuilder.DropColumn(
                name: "MachineHourlyRate",
                table: "WorkCenters");
        }
    }
}
