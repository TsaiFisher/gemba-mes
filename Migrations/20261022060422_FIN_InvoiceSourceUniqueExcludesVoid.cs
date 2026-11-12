using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class FIN_InvoiceSourceUniqueExcludesVoid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ARInvoices_SourceDocType_SourceDocId",
                table: "ARInvoices");

            migrationBuilder.DropIndex(
                name: "IX_APInvoices_SourceDocType_SourceDocId",
                table: "APInvoices");

            migrationBuilder.CreateIndex(
                name: "IX_ARInvoices_SourceDocType_SourceDocId",
                table: "ARInvoices",
                columns: new[] { "SourceDocType", "SourceDocId" },
                unique: true,
                filter: "\"SourceDocType\" IS NOT NULL AND \"SourceDocId\" IS NOT NULL AND \"Status\" <> 3");

            migrationBuilder.CreateIndex(
                name: "IX_APInvoices_SourceDocType_SourceDocId",
                table: "APInvoices",
                columns: new[] { "SourceDocType", "SourceDocId" },
                unique: true,
                filter: "\"SourceDocType\" IS NOT NULL AND \"SourceDocId\" IS NOT NULL AND \"Status\" <> 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ARInvoices_SourceDocType_SourceDocId",
                table: "ARInvoices");

            migrationBuilder.DropIndex(
                name: "IX_APInvoices_SourceDocType_SourceDocId",
                table: "APInvoices");

            migrationBuilder.CreateIndex(
                name: "IX_ARInvoices_SourceDocType_SourceDocId",
                table: "ARInvoices",
                columns: new[] { "SourceDocType", "SourceDocId" },
                unique: true,
                filter: "\"SourceDocType\" IS NOT NULL AND \"SourceDocId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_APInvoices_SourceDocType_SourceDocId",
                table: "APInvoices",
                columns: new[] { "SourceDocType", "SourceDocId" },
                unique: true,
                filter: "\"SourceDocType\" IS NOT NULL AND \"SourceDocId\" IS NOT NULL");
        }
    }
}
