using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_ShiftBreak_ShiftGroupDayHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Shift 休息（午休）時段 — 供未來甘特圖以實際時鐘呈現時跳過此區間
            migrationBuilder.AddColumn<TimeOnly>(
                name: "BreakStart", table: "Shifts",
                type: "time without time zone", nullable: true);
            migrationBuilder.AddColumn<TimeOnly>(
                name: "BreakEnd", table: "Shifts",
                type: "time without time zone", nullable: true);

            // ShiftGroupDay 每日工時 — 補回先前 entity 已宣告但漏建的欄位（snapshot 漂移）
            migrationBuilder.AddColumn<decimal>(
                name: "DailyRegularHours", table: "ShiftGroupDays",
                type: "numeric(5,2)", nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<decimal>(
                name: "DailyOvertimeHours", table: "ShiftGroupDays",
                type: "numeric(5,2)", nullable: false, defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "BreakStart", table: "Shifts");
            migrationBuilder.DropColumn(name: "BreakEnd", table: "Shifts");
            migrationBuilder.DropColumn(name: "DailyRegularHours", table: "ShiftGroupDays");
            migrationBuilder.DropColumn(name: "DailyOvertimeHours", table: "ShiftGroupDays");
        }
    }
}
