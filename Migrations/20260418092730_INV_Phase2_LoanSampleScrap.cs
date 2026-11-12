using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class INV_Phase2_LoanSampleScrap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LoanDocs",
                columns: table => new
                {
                    LoanDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LoanDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TypeCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TypeCategory = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    EntityType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EntityCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanDocs", x => x.LoanDocId);
                });

            migrationBuilder.CreateTable(
                name: "SampleDocs",
                columns: table => new
                {
                    SampleDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SampleDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TypeCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TypeCategory = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    EntityType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EntityCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SampleDocs", x => x.SampleDocId);
                });

            migrationBuilder.CreateTable(
                name: "ScrapDocs",
                columns: table => new
                {
                    ScrapDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ScrapDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TypeCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TypeCategory = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DepartmentCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    ScrapReasonCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrapDocs", x => x.ScrapDocId);
                });

            migrationBuilder.CreateTable(
                name: "LoanDocLines",
                columns: table => new
                {
                    LoanDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    StockQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    StockUomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    QtyPutAway = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanDocLines", x => new { x.LoanDocId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_LoanDocLines_LoanDocs_LoanDocId",
                        column: x => x.LoanDocId,
                        principalTable: "LoanDocs",
                        principalColumn: "LoanDocId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SampleDocLines",
                columns: table => new
                {
                    SampleDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    StockQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    StockUomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    QtyPutAway = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SampleDocLines", x => new { x.SampleDocId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_SampleDocLines_SampleDocs_SampleDocId",
                        column: x => x.SampleDocId,
                        principalTable: "SampleDocs",
                        principalColumn: "SampleDocId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScrapDocLines",
                columns: table => new
                {
                    ScrapDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    StockQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    StockUomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    QtyPutAway = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrapDocLines", x => new { x.ScrapDocId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_ScrapDocLines_ScrapDocs_ScrapDocId",
                        column: x => x.ScrapDocId,
                        principalTable: "ScrapDocs",
                        principalColumn: "ScrapDocId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LoanDocLocationDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LoanDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanDocLocationDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanDocLocationDetails_LoanDocLines_LoanDocId_LineNo",
                        columns: x => new { x.LoanDocId, x.LineNo },
                        principalTable: "LoanDocLines",
                        principalColumns: new[] { "LoanDocId", "LineNo" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SampleDocLocationDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SampleDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SampleDocLocationDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SampleDocLocationDetails_SampleDocLines_SampleDocId_LineNo",
                        columns: x => new { x.SampleDocId, x.LineNo },
                        principalTable: "SampleDocLines",
                        principalColumns: new[] { "SampleDocId", "LineNo" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScrapDocLocationDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ScrapDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrapDocLocationDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScrapDocLocationDetails_ScrapDocLines_ScrapDocId_LineNo",
                        columns: x => new { x.ScrapDocId, x.LineNo },
                        principalTable: "ScrapDocLines",
                        principalColumns: new[] { "ScrapDocId", "LineNo" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoanDocLocationDetails_LoanDocId_LineNo",
                table: "LoanDocLocationDetails",
                columns: new[] { "LoanDocId", "LineNo" });

            migrationBuilder.CreateIndex(
                name: "IX_LoanDocs_PlantId_TypeCode_LoanDate",
                table: "LoanDocs",
                columns: new[] { "PlantId", "TypeCode", "LoanDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SampleDocLocationDetails_SampleDocId_LineNo",
                table: "SampleDocLocationDetails",
                columns: new[] { "SampleDocId", "LineNo" });

            migrationBuilder.CreateIndex(
                name: "IX_SampleDocs_PlantId_TypeCode_SampleDate",
                table: "SampleDocs",
                columns: new[] { "PlantId", "TypeCode", "SampleDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ScrapDocLocationDetails_ScrapDocId_LineNo",
                table: "ScrapDocLocationDetails",
                columns: new[] { "ScrapDocId", "LineNo" });

            migrationBuilder.CreateIndex(
                name: "IX_ScrapDocs_PlantId_TypeCode_ScrapDate",
                table: "ScrapDocs",
                columns: new[] { "PlantId", "TypeCode", "ScrapDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoanDocLocationDetails");

            migrationBuilder.DropTable(
                name: "SampleDocLocationDetails");

            migrationBuilder.DropTable(
                name: "ScrapDocLocationDetails");

            migrationBuilder.DropTable(
                name: "LoanDocLines");

            migrationBuilder.DropTable(
                name: "SampleDocLines");

            migrationBuilder.DropTable(
                name: "ScrapDocLines");

            migrationBuilder.DropTable(
                name: "LoanDocs");

            migrationBuilder.DropTable(
                name: "SampleDocs");

            migrationBuilder.DropTable(
                name: "ScrapDocs");
        }
    }
}
