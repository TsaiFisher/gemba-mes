using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class PhaseH_IPQC : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasSigmaWarning",
                table: "InspectionItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "TargetStdDev",
                table: "InspectionItems",
                type: "numeric",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InspectionSpecSamplingCounters",
                columns: table => new
                {
                    CounterId = table.Column<string>(type: "text", nullable: false),
                    SpecId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ScopeKey = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SamplingState = table.Column<string>(type: "text", nullable: false),
                    ConsecutivePasses = table.Column<int>(type: "integer", nullable: false),
                    LotCounter = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionSpecSamplingCounters", x => x.CounterId);
                    table.ForeignKey(
                        name: "FK_InspectionSpecSamplingCounters_InspectionSpecs_SpecId",
                        column: x => x.SpecId,
                        principalTable: "InspectionSpecs",
                        principalColumn: "SpecId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InspectionSpecSamplingCounters_SpecId_ScopeKey",
                table: "InspectionSpecSamplingCounters",
                columns: new[] { "SpecId", "ScopeKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InspectionSpecSamplingCounters");

            migrationBuilder.DropColumn(
                name: "HasSigmaWarning",
                table: "InspectionItems");

            migrationBuilder.DropColumn(
                name: "TargetStdDev",
                table: "InspectionItems");
        }
    }
}
