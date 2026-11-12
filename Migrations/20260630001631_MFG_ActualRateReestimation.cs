using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_ActualRateReestimation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DepartmentActualCosts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PeriodId = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    WorkCenterCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ActualLaborCost = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ActualOverheadCost = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepartmentActualCosts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WipSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PeriodId = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    WorkOrderId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    WipMaterial = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    WipLabor = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    WipOverhead = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    QtyCompleted = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    QtyInProcess = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    SnapshotAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WipSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DepartmentActualCosts_PlantId_PeriodId_WorkCenterCode",
                table: "DepartmentActualCosts",
                columns: new[] { "PlantId", "PeriodId", "WorkCenterCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WipSnapshots_PlantId_PeriodId_WorkOrderId",
                table: "WipSnapshots",
                columns: new[] { "PlantId", "PeriodId", "WorkOrderId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DepartmentActualCosts");

            migrationBuilder.DropTable(
                name: "WipSnapshots");
        }
    }
}
