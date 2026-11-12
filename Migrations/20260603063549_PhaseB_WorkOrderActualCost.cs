using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class PhaseB_WorkOrderActualCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkOrderActualCosts",
                columns: table => new
                {
                    WorkOrderId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ActualMaterial = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ActualLabor = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ActualOverhead = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    StandardMaterial = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    StandardLabor = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    StandardOverhead = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    VarMaterial = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    VarLaborEfficiency = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    VarOverheadRate = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CostDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderActualCosts", x => x.WorkOrderId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkOrderActualCosts");
        }
    }
}
