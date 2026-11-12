using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class FIN_CreditAndDebitNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CreditedAmount",
                table: "ARInvoices",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DebitedAmount",
                table: "APInvoices",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "APDebitNotes",
                columns: table => new
                {
                    APDebitNoteId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SupplierCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    NoteDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AmountLocal = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SourceDocType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SourceDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedByRoleId = table.Column<string>(type: "text", nullable: true),
                    OrganizationUnitPath = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_APDebitNotes", x => x.APDebitNoteId);
                });

            migrationBuilder.CreateTable(
                name: "ARCreditNotes",
                columns: table => new
                {
                    ARCreditNoteId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CustomerCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    NoteDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AmountLocal = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SourceDocType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SourceDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedByRoleId = table.Column<string>(type: "text", nullable: true),
                    OrganizationUnitPath = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ARCreditNotes", x => x.ARCreditNoteId);
                });

            migrationBuilder.CreateTable(
                name: "APDebitNoteLines",
                columns: table => new
                {
                    APDebitNoteId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    APInvoiceId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    InvoiceLineNo = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    LineAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TaxRate = table.Column<decimal>(type: "numeric(8,4)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_APDebitNoteLines", x => new { x.APDebitNoteId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_APDebitNoteLines_APDebitNotes_APDebitNoteId",
                        column: x => x.APDebitNoteId,
                        principalTable: "APDebitNotes",
                        principalColumn: "APDebitNoteId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ARCreditNoteLines",
                columns: table => new
                {
                    ARCreditNoteId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ARInvoiceId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    InvoiceLineNo = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    LineAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TaxRate = table.Column<decimal>(type: "numeric(8,4)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ARCreditNoteLines", x => new { x.ARCreditNoteId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_ARCreditNoteLines_ARCreditNotes_ARCreditNoteId",
                        column: x => x.ARCreditNoteId,
                        principalTable: "ARCreditNotes",
                        principalColumn: "ARCreditNoteId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "DocNumberConfigs",
                columns: new[] { "DocType", "ScopeKey", "DateFormat", "DocTypeName", "IsActive", "IsSystemType", "PlantId", "Prefix", "ResetCycle", "Separator", "SeqLength" },
                values: new object[,]
                {
                    { "APDebitNote", "", 2, "進貨折讓單", true, true, "", "APD", 1, "-", 5 },
                    { "ARCreditNote", "", 2, "銷貨折讓單", true, true, "", "ARC", 1, "-", 5 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_APDebitNoteLines_APInvoiceId",
                table: "APDebitNoteLines",
                column: "APInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_APDebitNotes_PlantId_Status_NoteDate",
                table: "APDebitNotes",
                columns: new[] { "PlantId", "Status", "NoteDate" });

            migrationBuilder.CreateIndex(
                name: "IX_APDebitNotes_SourceDocType_SourceDocId",
                table: "APDebitNotes",
                columns: new[] { "SourceDocType", "SourceDocId" },
                unique: true,
                filter: "\"SourceDocType\" IS NOT NULL AND \"SourceDocId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ARCreditNoteLines_ARInvoiceId",
                table: "ARCreditNoteLines",
                column: "ARInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_ARCreditNotes_PlantId_Status_NoteDate",
                table: "ARCreditNotes",
                columns: new[] { "PlantId", "Status", "NoteDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ARCreditNotes_SourceDocType_SourceDocId",
                table: "ARCreditNotes",
                columns: new[] { "SourceDocType", "SourceDocId" },
                unique: true,
                filter: "\"SourceDocType\" IS NOT NULL AND \"SourceDocId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "APDebitNoteLines");

            migrationBuilder.DropTable(
                name: "ARCreditNoteLines");

            migrationBuilder.DropTable(
                name: "APDebitNotes");

            migrationBuilder.DropTable(
                name: "ARCreditNotes");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyValues: new object[] { "APDebitNote", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyValues: new object[] { "ARCreditNote", "" });

            migrationBuilder.DropColumn(
                name: "CreditedAmount",
                table: "ARInvoices");

            migrationBuilder.DropColumn(
                name: "DebitedAmount",
                table: "APInvoices");
        }
    }
}
