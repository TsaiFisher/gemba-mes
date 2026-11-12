using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class AddVendorEvaluation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VendorEvaluations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SupplierCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EvaluationYear = table.Column<int>(type: "integer", nullable: false),
                    EvaluationQuarter = table.Column<int>(type: "integer", nullable: true),
                    QualityScore = table.Column<decimal>(type: "numeric(8,2)", nullable: false),
                    DeliveryScore = table.Column<decimal>(type: "numeric(8,2)", nullable: false),
                    PriceScore = table.Column<decimal>(type: "numeric(8,2)", nullable: false),
                    ServiceScore = table.Column<decimal>(type: "numeric(8,2)", nullable: false),
                    TotalScore = table.Column<decimal>(type: "numeric(8,2)", nullable: false),
                    Evaluator = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    EvaluationDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ResultStatus = table.Column<int>(type: "integer", nullable: false),
                    FollowUpAction = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "text", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorEvaluations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorEvaluations_Companies_SupplierCode",
                        column: x => x.SupplierCode,
                        principalTable: "Companies",
                        principalColumn: "CompanyCode",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VendorEvaluations_SupplierCode_EvaluationYear_EvaluationQua~",
                table: "VendorEvaluations",
                columns: new[] { "SupplierCode", "EvaluationYear", "EvaluationQuarter" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VendorEvaluations");
        }
    }
}
