using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_ScheduleRunState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScheduleRunStates",
                columns: table => new
                {
                    PlantId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRunBy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    LastScheduled = table.Column<int>(type: "integer", nullable: false),
                    LastSkipped = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleRunStates", x => x.PlantId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduleRunStates");
        }
    }
}
