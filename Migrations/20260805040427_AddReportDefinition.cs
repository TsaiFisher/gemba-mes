using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class AddReportDefinition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReportDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OwnerUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    OwnerRoleId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    PlantId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ToolKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ParamsJson = table.Column<string>(type: "text", nullable: false),
                    ScheduleKind = table.Column<int>(type: "integer", nullable: true),
                    ScheduleHour = table.Column<int>(type: "integer", nullable: true),
                    ScheduleMinute = table.Column<int>(type: "integer", nullable: true),
                    ScheduleDayOfWeek = table.Column<int>(type: "integer", nullable: true),
                    ScheduleDayOfMonth = table.Column<int>(type: "integer", nullable: true),
                    NextRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    OutputFormat = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedByRoleId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportDefinitions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportDefinitions_NextRunAt",
                table: "ReportDefinitions",
                column: "NextRunAt");

            migrationBuilder.CreateIndex(
                name: "IX_ReportDefinitions_OwnerUserId_Name",
                table: "ReportDefinitions",
                columns: new[] { "OwnerUserId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportDefinitions");
        }
    }
}
