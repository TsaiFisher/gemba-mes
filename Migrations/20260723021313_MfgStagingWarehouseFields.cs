using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MfgStagingWarehouseFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StagingLocationCode",
                table: "WorkCenters",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StagingWhCode",
                table: "WorkCenters",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StagedAt",
                table: "MaterialStagingOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StagedBy",
                table: "MaterialStagingOrders",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FromLocationCode",
                table: "MaterialStagingLines",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FromWarehouseCode",
                table: "MaterialStagingLines",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceDispatchId",
                table: "MaterialStagingLines",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceWorkOrderId",
                table: "MaterialStagingLines",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StagingLocationCode",
                table: "WorkCenters");

            migrationBuilder.DropColumn(
                name: "StagingWhCode",
                table: "WorkCenters");

            migrationBuilder.DropColumn(
                name: "StagedAt",
                table: "MaterialStagingOrders");

            migrationBuilder.DropColumn(
                name: "StagedBy",
                table: "MaterialStagingOrders");

            migrationBuilder.DropColumn(
                name: "FromLocationCode",
                table: "MaterialStagingLines");

            migrationBuilder.DropColumn(
                name: "FromWarehouseCode",
                table: "MaterialStagingLines");

            migrationBuilder.DropColumn(
                name: "SourceDispatchId",
                table: "MaterialStagingLines");

            migrationBuilder.DropColumn(
                name: "SourceWorkOrderId",
                table: "MaterialStagingLines");
        }
    }
}
