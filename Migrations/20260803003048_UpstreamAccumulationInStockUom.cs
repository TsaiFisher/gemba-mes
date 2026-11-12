using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class UpstreamAccumulationInStockUom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ShippedStockQty",
                table: "SalesOrderLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ReceivedStockQty",
                table: "PurchaseOrderLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            // ── ① 基準欄位補值 ────────────────────────────────────────────────
            // 新守門以 StockQty 為「這張單到底訂了多少」的上限，該欄若是 0 就會把
            // 所有出貨／進貨都擋掉。SalesOrderLine.StockQty 在本次之前是前端送什麼存什麼
            // （表單有 hidden 欄位會同步，但 JS 失效、種子資料、程式建單都可能留下 0），
            // 故先補：同單位時基準量就等於單據量，這個等式與伺服端換算邏輯一致。
            // 單位真的不同而 StockQty 為 0 的行補不了（SQL 拿不到換算率），
            // 留 0 由應用層擋下並回報，不在 migration 裡猜。
            migrationBuilder.Sql("""
                UPDATE "SalesOrderLines"
                SET "StockUomCode" = "UomCode"
                WHERE COALESCE("StockUomCode", '') = '';

                UPDATE "SalesOrderLines"
                SET "StockQty" = "OrderQty"
                WHERE "StockQty" = 0 AND "OrderQty" > 0
                  AND COALESCE("StockUomCode", '') = COALESCE("UomCode", '');

                UPDATE "PurchaseOrderLines"
                SET "StockUomCode" = "UomCode"
                WHERE COALESCE("StockUomCode", '') = '';

                UPDATE "PurchaseOrderLines"
                SET "StockQty" = "OrderQty"
                WHERE "StockQty" = 0 AND "OrderQty" > 0
                  AND COALESCE("StockUomCode", '') = COALESCE("UomCode", '');
                """);

            // ── ② 累計回填 ────────────────────────────────────────────────────
            // 計入的單據集合必須與應用層「已累加但尚未回沖」的集合完全一致，否則
            // 新舊兩欄從第一天就對不上。ShippedQty 在 Save 累加、Void/Delete 回沖，
            // 被刪的單已不在庫裡，因此集合 = 現存且 Status <> Void(4) 的出貨單；
            // 進貨側同理（RestorePoReceivedQtyAsync 只在作廢時呼叫）。
            migrationBuilder.Sql("""
                UPDATE "SalesOrderLines" sol
                SET "ShippedStockQty" = sub.total
                FROM (
                    SELECT gsl."SalesOrderId" AS so_id, gsl."SoLineNo" AS so_line,
                           SUM(gsl."StockQty") AS total
                    FROM "GoodsShipmentLines" gsl
                    JOIN "GoodsShipments" gs ON gs."GoodsShipmentId" = gsl."GoodsShipmentId"
                    WHERE gs."Status" <> 4
                      AND gsl."SalesOrderId" IS NOT NULL
                      AND gsl."SoLineNo" IS NOT NULL
                    GROUP BY gsl."SalesOrderId", gsl."SoLineNo"
                ) sub
                WHERE sub.so_id = sol."SalesOrderId" AND sub.so_line = sol."LineNo";

                UPDATE "PurchaseOrderLines" pol
                SET "ReceivedStockQty" = sub.total
                FROM (
                    SELECT grl."PurchaseOrderId" AS po_id, grl."PoLineNo" AS po_line,
                           SUM(grl."StockQty") AS total
                    FROM "GoodsReceiptLines" grl
                    JOIN "GoodsReceipts" gr ON gr."GoodsReceiptId" = grl."GoodsReceiptId"
                    WHERE gr."Status" <> 4
                      AND grl."PurchaseOrderId" IS NOT NULL
                      AND grl."PoLineNo" IS NOT NULL
                    GROUP BY grl."PurchaseOrderId", grl."PoLineNo"
                ) sub
                WHERE sub.po_id = pol."PurchaseOrderId" AND sub.po_line = pol."LineNo";
                """);

            // 既有的 ShippedQty / ReceivedQty 不動：混單位的舊行本來就是「不同單位相加」
            // 的結果，這裡沒有資訊可以把它拆回去；能修的是從現在起的每一次累加。
            // 要在套用前先看清楚會動到哪些行，跑 scripts/audit-mixed-uom-accumulation.sql。
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShippedStockQty",
                table: "SalesOrderLines");

            migrationBuilder.DropColumn(
                name: "ReceivedStockQty",
                table: "PurchaseOrderLines");
        }
    }
}
