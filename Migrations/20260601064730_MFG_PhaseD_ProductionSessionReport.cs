using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_PhaseD_ProductionSessionReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProductionSessions",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DispatchId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    WorkOrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OperatorId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    MachineCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ToolingCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionSessions", x => x.SessionId);
                    table.ForeignKey(
                        name: "FK_ProductionSessions_Dispatches_DispatchId",
                        column: x => x.DispatchId,
                        principalTable: "Dispatches",
                        principalColumn: "DispatchId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionSetups",
                columns: table => new
                {
                    SetupId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DispatchId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    WorkOrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SetupStartAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SetupEndAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OperatorId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ToolingCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionSetups", x => x.SetupId);
                    table.ForeignKey(
                        name: "FK_ProductionSetups_Dispatches_DispatchId",
                        column: x => x.DispatchId,
                        principalTable: "Dispatches",
                        principalColumn: "DispatchId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionReports",
                columns: table => new
                {
                    ReportId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SessionId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DispatchId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    WorkOrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    QtyOk = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    QtyNg = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    ContainerCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    ReportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionReports", x => x.ReportId);
                    table.ForeignKey(
                        name: "FK_ProductionReports_ProductionSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "ProductionSessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionReportContainers",
                columns: table => new
                {
                    ReportId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ContainerSeq = table.Column<int>(type: "integer", nullable: false),
                    ContainerCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ActualQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionReportContainers", x => new { x.ReportId, x.ContainerSeq });
                    table.ForeignKey(
                        name: "FK_ProductionReportContainers_ProductionReports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "ProductionReports",
                        principalColumn: "ReportId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductionReports_PlantId_ReportId",
                table: "ProductionReports",
                columns: new[] { "PlantId", "ReportId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionReports_SessionId",
                table: "ProductionReports",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionSessions_DispatchId",
                table: "ProductionSessions",
                column: "DispatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionSessions_PlantId_SessionId",
                table: "ProductionSessions",
                columns: new[] { "PlantId", "SessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionSetups_DispatchId",
                table: "ProductionSetups",
                column: "DispatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionSetups_PlantId_SetupId",
                table: "ProductionSetups",
                columns: new[] { "PlantId", "SetupId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductionReportContainers");

            migrationBuilder.DropTable(
                name: "ProductionSetups");

            migrationBuilder.DropTable(
                name: "ProductionReports");

            migrationBuilder.DropTable(
                name: "ProductionSessions");
        }
    }
}
