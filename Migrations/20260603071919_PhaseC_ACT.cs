using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class PhaseC_ACT : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountingPeriods",
                columns: table => new
                {
                    PeriodId = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Month = table.Column<int>(type: "integer", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedBy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingPeriods", x => x.PeriodId);
                });

            migrationBuilder.CreateTable(
                name: "EclAllowances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PeriodId = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    AgingBucket = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ArBalance = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    LossRate = table.Column<decimal>(type: "numeric(8,6)", nullable: false),
                    EclAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AssessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EclAllowances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EclLossRates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AgingBucket = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AgingDaysFrom = table.Column<int>(type: "integer", nullable: false),
                    AgingDaysTo = table.Column<int>(type: "integer", nullable: false),
                    LossRate = table.Column<decimal>(type: "numeric(8,6)", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EclLossRates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FxRevaluationEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PeriodId = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    DocType = table.Column<int>(type: "integer", nullable: false),
                    DocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    BookRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    PeriodEndRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    OutstandingAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    BookAmountLocal = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    RevaluedAmountLocal = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ExchangeGainLoss = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    RevaluedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FxRevaluationEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InventoryNrvAssessments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PeriodId = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CarryingCost = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    QtyOnHand = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    NrvPrice = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    SellingCost = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    NetRealizableValue = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ImpairmentLoss = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AssessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryNrvAssessments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JournalEntries",
                columns: table => new
                {
                    JournalId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PeriodId = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    EntryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SourceDocType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SourceDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntries", x => x.JournalId);
                });

            migrationBuilder.CreateTable(
                name: "MonthEndChecklists",
                columns: table => new
                {
                    ChecklistId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PeriodId = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    IsCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedBy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonthEndChecklists", x => x.ChecklistId);
                });

            migrationBuilder.CreateTable(
                name: "JournalLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JournalId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    AccountCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AccountName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Debit = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Credit = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    AmountLocal = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CostCenter = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JournalLines_JournalEntries_JournalId",
                        column: x => x.JournalId,
                        principalTable: "JournalEntries",
                        principalColumn: "JournalId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChecklistItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChecklistId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StepNo = table.Column<int>(type: "integer", nullable: false),
                    StepName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedBy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChecklistItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChecklistItems_MonthEndChecklists_ChecklistId",
                        column: x => x.ChecklistId,
                        principalTable: "MonthEndChecklists",
                        principalColumn: "ChecklistId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistItems_ChecklistId",
                table: "ChecklistItems",
                column: "ChecklistId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_JournalId",
                table: "JournalLines",
                column: "JournalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountingPeriods");

            migrationBuilder.DropTable(
                name: "ChecklistItems");

            migrationBuilder.DropTable(
                name: "EclAllowances");

            migrationBuilder.DropTable(
                name: "EclLossRates");

            migrationBuilder.DropTable(
                name: "FxRevaluationEntries");

            migrationBuilder.DropTable(
                name: "InventoryNrvAssessments");

            migrationBuilder.DropTable(
                name: "JournalLines");

            migrationBuilder.DropTable(
                name: "MonthEndChecklists");

            migrationBuilder.DropTable(
                name: "JournalEntries");
        }
    }
}
