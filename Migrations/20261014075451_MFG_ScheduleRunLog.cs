using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_ScheduleRunLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScheduleRunLogs",
                columns: table => new
                {
                    RunLogId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlantId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RunBy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Rule = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FreezeHours = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    ScheduledCount = table.Column<int>(type: "integer", nullable: false),
                    SkippedCount = table.Column<int>(type: "integer", nullable: false),
                    FencedCount = table.Column<int>(type: "integer", nullable: false),
                    NewlyScheduledCount = table.Column<int>(type: "integer", nullable: false),
                    MovedCount = table.Column<int>(type: "integer", nullable: false),
                    MachineChangedCount = table.Column<int>(type: "integer", nullable: false),
                    UnscheduledCount = table.Column<int>(type: "integer", nullable: false),
                    TomorrowChangedCount = table.Column<int>(type: "integer", nullable: false),
                    ChangesTruncated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleRunLogs", x => x.RunLogId);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleRunChanges",
                columns: table => new
                {
                    ChangeId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunLogId = table.Column<long>(type: "bigint", nullable: false),
                    DispatchId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    WorkOrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OperationSeq = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    OldMachineCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    OldStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NewMachineCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    NewStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleRunChanges", x => x.ChangeId);
                    table.ForeignKey(
                        name: "FK_ScheduleRunChanges_ScheduleRunLogs_RunLogId",
                        column: x => x.RunLogId,
                        principalTable: "ScheduleRunLogs",
                        principalColumn: "RunLogId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleRunChanges_RunLogId",
                table: "ScheduleRunChanges",
                column: "RunLogId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleRunLogs_PlantId_StartedAt",
                table: "ScheduleRunLogs",
                columns: new[] { "PlantId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduleRunChanges");

            migrationBuilder.DropTable(
                name: "ScheduleRunLogs");
        }
    }
}
