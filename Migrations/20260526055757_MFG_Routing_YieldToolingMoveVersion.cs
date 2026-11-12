using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_Routing_YieldToolingMoveVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "EffectiveFrom",
                table: "Routings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EffectiveTo",
                table: "Routings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Version",
                table: "Routings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MoveTime",
                table: "RoutingOperations",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "QueueTime",
                table: "RoutingOperations",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ToolingCode",
                table: "RoutingOperations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            // 既有列預設良率 100%（避免 0 = 全報廢；對齊模型預設）
            migrationBuilder.AddColumn<decimal>(
                name: "YieldRate",
                table: "RoutingOperations",
                type: "numeric(8,4)",
                nullable: false,
                defaultValue: 100m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EffectiveFrom",
                table: "Routings");

            migrationBuilder.DropColumn(
                name: "EffectiveTo",
                table: "Routings");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Routings");

            migrationBuilder.DropColumn(
                name: "MoveTime",
                table: "RoutingOperations");

            migrationBuilder.DropColumn(
                name: "QueueTime",
                table: "RoutingOperations");

            migrationBuilder.DropColumn(
                name: "ToolingCode",
                table: "RoutingOperations");

            migrationBuilder.DropColumn(
                name: "YieldRate",
                table: "RoutingOperations");
        }
    }
}
