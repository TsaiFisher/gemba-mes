using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <summary>
    /// 單據明細補本位幣金額欄位 AmountLocal，並校正 TotalAmount 的語意。
    ///
    /// 背景：前端 calcLine 原本算的是 <c>TotalAmount = (未稅 + 稅額) × 匯率</c>，
    /// 導致 TotalAmount 實際存的是「本幣」，卻與同列外幣的 LineAmount / TaxAmount 併存
    /// （「未稅 + 稅額 ≠ 總額」），且下游 PurchaseAmountController 又乘一次匯率而重複換算。
    ///
    /// 校正後：LineAmount / TaxAmount / TotalAmount 一律為交易幣，AmountLocal 為本幣，
    /// 表頭 TotalAmount 改為各明細 AmountLocal 加總（明細可跨幣別，故表頭只能以本幣合計）。
    ///
    /// 舊資料回算：舊 TotalAmount 既是本幣 → 直接搬進 AmountLocal，
    /// 再以 <c>÷ ExchangeRate</c> 還原交易幣 TotalAmount（匯率 1 的本幣單據不受影響）。
    /// 表頭 TotalAmount 不需異動：舊值本就等於各行本幣加總。
    /// </summary>
    public partial class AddAmountLocalToDocumentLines : Migration
    {
        /// <summary>本次校正涉及的明細資料表（皆具 TotalAmount + ExchangeRate 欄位）。</summary>
        private static readonly string[] LineTables =
        {
            "PurchaseOrderLines",
            "GoodsReceiptLines",
            "GoodsReturnLines",
            "OutsourceReceiptLines",
            "SalesOrderLines",
            "GoodsShipmentLines",
            "SalesReturnLines",
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in LineTables)
            {
                migrationBuilder.AddColumn<decimal>(
                    name: "AmountLocal",
                    table: table,
                    type: "numeric(18,4)",
                    nullable: false,
                    defaultValue: 0m);

                // 舊 TotalAmount = 本幣 → 搬進 AmountLocal，並還原交易幣 TotalAmount。
                // 匯率為 NULL/0 時視為 1（本幣單據），只搬不除，避免除零。
                migrationBuilder.Sql($@"
                    UPDATE ""{table}""
                    SET ""AmountLocal"" = ""TotalAmount"",
                        ""TotalAmount"" = CASE
                            WHEN COALESCE(""ExchangeRate"", 0) = 0 THEN ""TotalAmount""
                            ELSE ROUND(""TotalAmount"" / ""ExchangeRate"", 4)
                        END;");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in LineTables)
            {
                // 還原：TotalAmount 變回本幣（即 AmountLocal），再移除欄位。
                migrationBuilder.Sql($@"
                    UPDATE ""{table}""
                    SET ""TotalAmount"" = ""AmountLocal"";");

                migrationBuilder.DropColumn(
                    name: "AmountLocal",
                    table: table);
            }
        }
    }
}
