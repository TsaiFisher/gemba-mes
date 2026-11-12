using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MrpPurchaseVarianceRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MrpPurchaseVarianceRecords",
                columns: table => new
                {
                    VarianceId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    MrpRunId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PurchaseRequestId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Disposition = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PreviousQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CurrentQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolutionNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedByRoleId = table.Column<string>(type: "text", nullable: true),
                    OrganizationUnitPath = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MrpPurchaseVarianceRecords", x => x.VarianceId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MrpPurchaseVarianceRecords_PlantId_MrpRunId_Status",
                table: "MrpPurchaseVarianceRecords",
                columns: new[] { "PlantId", "MrpRunId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MrpPurchaseVarianceRecords");
        }
    }
}
