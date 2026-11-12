using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class INV_SourceDocFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "QtySourceCleared",
                table: "StockTransferLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "SourceDocId",
                table: "StockTransferLines",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceLineNo",
                table: "StockTransferLines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QtySourceCleared",
                table: "StockAdjustmentLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "SourceDocId",
                table: "StockAdjustmentLines",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceLineNo",
                table: "StockAdjustmentLines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QtySourceCleared",
                table: "ScrapDocLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "SourceDocId",
                table: "ScrapDocLines",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceLineNo",
                table: "ScrapDocLines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QtySourceCleared",
                table: "SampleDocLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "SourceDocId",
                table: "SampleDocLines",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceLineNo",
                table: "SampleDocLines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QtySourceCleared",
                table: "ReworkMaterialDocLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "SourceDocId",
                table: "ReworkMaterialDocLines",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceLineNo",
                table: "ReworkMaterialDocLines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QtySourceCleared",
                table: "OutsourceMaterialDocLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "SourceDocId",
                table: "OutsourceMaterialDocLines",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceLineNo",
                table: "OutsourceMaterialDocLines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QtySourceCleared",
                table: "LoanDocLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "SourceDocId",
                table: "LoanDocLines",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceLineNo",
                table: "LoanDocLines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QtySourceCleared",
                table: "GoodsReceiptLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QtySourceCleared",
                table: "StockTransferLines");

            migrationBuilder.DropColumn(
                name: "SourceDocId",
                table: "StockTransferLines");

            migrationBuilder.DropColumn(
                name: "SourceLineNo",
                table: "StockTransferLines");

            migrationBuilder.DropColumn(
                name: "QtySourceCleared",
                table: "StockAdjustmentLines");

            migrationBuilder.DropColumn(
                name: "SourceDocId",
                table: "StockAdjustmentLines");

            migrationBuilder.DropColumn(
                name: "SourceLineNo",
                table: "StockAdjustmentLines");

            migrationBuilder.DropColumn(
                name: "QtySourceCleared",
                table: "ScrapDocLines");

            migrationBuilder.DropColumn(
                name: "SourceDocId",
                table: "ScrapDocLines");

            migrationBuilder.DropColumn(
                name: "SourceLineNo",
                table: "ScrapDocLines");

            migrationBuilder.DropColumn(
                name: "QtySourceCleared",
                table: "SampleDocLines");

            migrationBuilder.DropColumn(
                name: "SourceDocId",
                table: "SampleDocLines");

            migrationBuilder.DropColumn(
                name: "SourceLineNo",
                table: "SampleDocLines");

            migrationBuilder.DropColumn(
                name: "QtySourceCleared",
                table: "ReworkMaterialDocLines");

            migrationBuilder.DropColumn(
                name: "SourceDocId",
                table: "ReworkMaterialDocLines");

            migrationBuilder.DropColumn(
                name: "SourceLineNo",
                table: "ReworkMaterialDocLines");

            migrationBuilder.DropColumn(
                name: "QtySourceCleared",
                table: "OutsourceMaterialDocLines");

            migrationBuilder.DropColumn(
                name: "SourceDocId",
                table: "OutsourceMaterialDocLines");

            migrationBuilder.DropColumn(
                name: "SourceLineNo",
                table: "OutsourceMaterialDocLines");

            migrationBuilder.DropColumn(
                name: "QtySourceCleared",
                table: "LoanDocLines");

            migrationBuilder.DropColumn(
                name: "SourceDocId",
                table: "LoanDocLines");

            migrationBuilder.DropColumn(
                name: "SourceLineNo",
                table: "LoanDocLines");

            migrationBuilder.DropColumn(
                name: "QtySourceCleared",
                table: "GoodsReceiptLines");
        }
    }
}
