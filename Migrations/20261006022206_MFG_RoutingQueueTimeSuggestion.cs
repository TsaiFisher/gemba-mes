using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_RoutingQueueTimeSuggestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RoutingQueueTimeSuggestions",
                columns: table => new
                {
                    RoutingId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    OperationSeq = table.Column<int>(type: "integer", nullable: false),
                    SuggestedQueueMinutes = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    MedianGapMinutes = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    PeriodDays = table.Column<int>(type: "integer", nullable: false),
                    CalculatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoutingQueueTimeSuggestions", x => new { x.RoutingId, x.OperationSeq });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoutingQueueTimeSuggestions");
        }
    }
}
