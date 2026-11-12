using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <summary>
    /// 回填採購明細的庫存單位數量（StockQty／ReceivedStockQty 為 0 的舊列）。
    ///
    /// <para>在途量改以庫存單位計算（InventoryReservationService.GetAvailableInTransitAsync，2026-10-27）。
    /// 兩種舊列 StockQty 恆 0：投產自動建立的委外採購單（WorkOrderReleaseService 原本沒填）、
    /// 以及 StockQty 欄位出現前的採購單。不回填的話它們的在途會變成 0，投產料閘與缺貨分析看不到。</para>
    ///
    /// <para>換算比例逐字照 UomService.GetConversionRateAsync 的四層查找：同單位＝1 → 品號正向 → 品號反向 → 全域正向 → 全域反向。
    /// 單位不同又查不到換算的列<b>不動</b>（與 UomService 不把缺換算當成 1 的原則一致）。純資料修正，Down 不還原。</para>
    /// </summary>
    public partial class PUR_BackfillPoLineStockQty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
WITH r AS (
  SELECT l.""PurchaseOrderId"", l.""LineNo"",
         COALESCE(l.""StockUomCode"", p.""StockUom"") AS stock_uom,
         COALESCE(
           CASE WHEN l.""UomCode"" IS NULL OR COALESCE(l.""StockUomCode"", p.""StockUom"") IS NULL
                  OR upper(trim(l.""UomCode"")) = upper(trim(COALESCE(l.""StockUomCode"", p.""StockUom""))) THEN 1.0 END,
           (SELECT c.""Numerator"" / c.""Denominator"" FROM ""ProductUomConversions"" c
             WHERE c.""ProductCode"" = l.""ProductCode"" AND c.""FromUomCode"" = l.""UomCode""
               AND c.""ToUomCode"" = COALESCE(l.""StockUomCode"", p.""StockUom"") AND c.""Denominator"" <> 0 LIMIT 1),
           (SELECT c.""Denominator"" / c.""Numerator"" FROM ""ProductUomConversions"" c
             WHERE c.""ProductCode"" = l.""ProductCode"" AND c.""ToUomCode"" = l.""UomCode""
               AND c.""FromUomCode"" = COALESCE(l.""StockUomCode"", p.""StockUom"") AND c.""Numerator"" <> 0 LIMIT 1),
           (SELECT c.""Numerator"" / c.""Denominator"" FROM ""UomConversions"" c
             WHERE c.""FromUomCode"" = l.""UomCode"" AND c.""ToUomCode"" = COALESCE(l.""StockUomCode"", p.""StockUom"")
               AND c.""Denominator"" <> 0 LIMIT 1),
           (SELECT c.""Denominator"" / c.""Numerator"" FROM ""UomConversions"" c
             WHERE c.""ToUomCode"" = l.""UomCode"" AND c.""FromUomCode"" = COALESCE(l.""StockUomCode"", p.""StockUom"")
               AND c.""Numerator"" <> 0 LIMIT 1)
         ) AS rate
  FROM ""PurchaseOrderLines"" l
  LEFT JOIN ""Products"" p ON p.""ProductCode"" = l.""ProductCode""
  WHERE l.""StockQty"" = 0 AND l.""OrderQty"" <> 0
)
UPDATE ""PurchaseOrderLines"" l
   SET ""StockQty""         = round(l.""OrderQty"" * r.rate, 4),
       ""ReceivedStockQty"" = CASE WHEN l.""ReceivedStockQty"" = 0 THEN round(l.""ReceivedQty"" * r.rate, 4)
                                   ELSE l.""ReceivedStockQty"" END,
       ""StockUomCode""     = COALESCE(l.""StockUomCode"", r.stock_uom)
  FROM r
 WHERE r.""PurchaseOrderId"" = l.""PurchaseOrderId"" AND r.""LineNo"" = l.""LineNo"" AND r.rate IS NOT NULL;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
