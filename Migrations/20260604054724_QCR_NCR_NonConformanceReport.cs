using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class QCR_NCR_NonConformanceReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NonConformanceReports",
                columns: table => new
                {
                    NcrId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    SourceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DefectCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    RootCause = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CorrectiveAction = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Disposition = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ScrapDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    ReworkWorkOrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    NcrStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "text", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExecutedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ExecutedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByRoleId = table.Column<string>(type: "text", nullable: true),
                    OrganizationUnitPath = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NonConformanceReports", x => x.NcrId);
                });

            migrationBuilder.InsertData(
                table: "DocNumberConfigs",
                columns: new[] { "DocType", "DateFormat", "DocTypeName", "IsActive", "IsSystemType", "Prefix", "ResetCycle", "Separator", "SeqLength" },
                values: new object[] { "NonConformanceReport", 2, "不合格品單", true, true, "NCR", 1, "-", 5 });

            migrationBuilder.CreateIndex(
                name: "IX_NonConformanceReports_PlantId_CreatedAt",
                table: "NonConformanceReports",
                columns: new[] { "PlantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NonConformanceReports_PlantId_NcrStatus",
                table: "NonConformanceReports",
                columns: new[] { "PlantId", "NcrStatus" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NonConformanceReports");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "NonConformanceReport");
        }
    }
}
