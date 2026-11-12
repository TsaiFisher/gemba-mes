using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_OutsourceAllowedScrapRate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "AllowedScrapRate",
                table: "OutsourceReceiptLines",
                type: "numeric(8,6)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(8,6)");

            migrationBuilder.AddColumn<decimal>(
                name: "AllowedScrapRate",
                table: "OutsourceQuotations",
                type: "numeric(8,6)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AllowedScrapRate",
                table: "OutsourcePurchaseOrderLines",
                type: "numeric(8,6)",
                nullable: true);

            // 既有資料的 0 不是「約定零容忍」，是**從來沒有來源**：這個欄位過去只存在於
            // 收貨明細上，上游（委外採購單／報價）根本沒有對應欄位，全庫沒有任何寫入點，
            // 於是每一列都停在 decimal 的預設值 0。
            //
            // 改成可為 null 之後 0 的意思變成「明確約定零容忍 → 短交即全額求償」，
            // 與原本的行為（被 `allowedRate > 0` 擋掉 → 不扣款）正好相反。
            // 不轉成 NULL 的話，這些從未談過損耗條款的舊單會突然變成最嚴苛的條款。
            migrationBuilder.Sql(
                @"UPDATE ""OutsourceReceiptLines"" SET ""AllowedScrapRate"" = NULL WHERE ""AllowedScrapRate"" = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedScrapRate",
                table: "OutsourceQuotations");

            migrationBuilder.DropColumn(
                name: "AllowedScrapRate",
                table: "OutsourcePurchaseOrderLines");

            migrationBuilder.AlterColumn<decimal>(
                name: "AllowedScrapRate",
                table: "OutsourceReceiptLines",
                type: "numeric(8,6)",
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric(8,6)",
                oldNullable: true);
        }
    }
}
