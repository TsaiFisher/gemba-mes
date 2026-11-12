using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_ScheduleRunStateFreshness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CalendarFingerprint",
                table: "ScheduleRunStates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "DataAsOf",
                table: "ScheduleRunStates",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateOnly>(
                name: "FingerprintFrom",
                table: "ScheduleRunStates",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "FingerprintTo",
                table: "ScheduleRunStates",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LastDailyRunDate",
                table: "ScheduleRunStates",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastTrigger",
                table: "ScheduleRunStates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            // 既有紀錄：以上次完成時刻當基準（近似值；下一次排程起就是精確的開始讀資料時刻）
            migrationBuilder.Sql(@"UPDATE ""ScheduleRunStates"" SET ""DataAsOf"" = ""LastRunAt"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalendarFingerprint",
                table: "ScheduleRunStates");

            migrationBuilder.DropColumn(
                name: "DataAsOf",
                table: "ScheduleRunStates");

            migrationBuilder.DropColumn(
                name: "FingerprintFrom",
                table: "ScheduleRunStates");

            migrationBuilder.DropColumn(
                name: "FingerprintTo",
                table: "ScheduleRunStates");

            migrationBuilder.DropColumn(
                name: "LastDailyRunDate",
                table: "ScheduleRunStates");

            migrationBuilder.DropColumn(
                name: "LastTrigger",
                table: "ScheduleRunStates");
        }
    }
}
