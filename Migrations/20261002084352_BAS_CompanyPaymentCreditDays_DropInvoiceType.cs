using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class BAS_CompanyPaymentCreditDays_DropInvoiceType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 2026-10-02 使用者裁決移除「發票開立方式」：全系統沒有任何邏輯讀它（應收發票、到期日、ERP 同步皆不看），
            // 留著會讓使用者以為設「月結彙總／不用開立」有作用。⚠️ 刪欄位＝值不可回復；正式環境套用前若需保留，
            // 先匯出：SELECT "CompanyCode","InvoiceType" FROM "Companies" WHERE "InvoiceType" IS NOT NULL;
            // 同批新增收付款天數，與既有結帳日一起決定應收／應付到期日（PaymentTerms）。
            migrationBuilder.DropColumn(
                name: "InvoiceTypeCode",
                table: "GoodsShipments");

            migrationBuilder.DropColumn(
                name: "InvoiceType",
                table: "Companies");

            migrationBuilder.AddColumn<int>(
                name: "PaymentCreditDays",
                table: "Companies",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PaymentCreditDays",
                table: "Companies");

            migrationBuilder.AddColumn<string>(
                name: "InvoiceTypeCode",
                table: "GoodsShipments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceType",
                table: "Companies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }
    }
}
