-- ============================================================================
-- 混單位累計稽核（唯讀，套用 UpstreamAccumulationInStockUom 前先跑）
--
-- 背景：SalesOrderLine.ShippedQty / PurchaseOrderLine.ReceivedQty 原本直接累加
-- 下游單據的「業務單位量」。下游行的 UomCode 允許與上游行不同（訂 PCS、出 BOX），
-- 因此這兩欄在混單位的情況下是「不同單位的數字相加」，而沿用同一組數字的超量守門
-- 也就跟著一致地錯——守門與累加都錯，帳面自洽，看不出來。
--
-- migration 會新增 ShippedStockQty / ReceivedStockQty 兩個庫存基準單位的累計欄，
-- 守門改看新欄。舊的 ShippedQty / ReceivedQty 保留作顯示用，值不動。
--
-- 這份腳本回答三個問題（全部唯讀，不改任何資料）：
--   A. 有哪些行真的是混單位的（＝舊值真的錯了，需要人工確認）
--   B. migration 的基準欄補值會動到哪些行（StockQty = 0 的舊資料）
--   C. 有哪些行補不了（單位不同且 StockQty = 0，SQL 拿不到換算率）
--
-- 用法：psql -f scripts/audit-mixed-uom-accumulation.sql
-- ============================================================================

\echo '=== A-1. ORD：出貨行單位 <> 訂單行單位（ShippedQty 是混單位相加的結果）==='
SELECT sol."SalesOrderId", sol."LineNo", sol."ProductCode",
       sol."UomCode"        AS so_uom,
       sol."OrderQty",
       sol."ShippedQty"     AS shipped_qty_mixed,
       string_agg(DISTINCT gsl."UomCode", ',') AS shipment_uoms,
       SUM(gsl."StockQty")  AS shipped_stock_qty_correct,
       sol."StockQty"       AS order_stock_qty,
       sol."StockUomCode"   AS stock_uom
FROM "SalesOrderLines" sol
JOIN "GoodsShipmentLines" gsl
     ON gsl."SalesOrderId" = sol."SalesOrderId" AND gsl."SoLineNo" = sol."LineNo"
JOIN "GoodsShipments" gs ON gs."GoodsShipmentId" = gsl."GoodsShipmentId"
WHERE gs."Status" <> 4
  AND COALESCE(gsl."UomCode", '') <> COALESCE(sol."UomCode", '')
GROUP BY sol."SalesOrderId", sol."LineNo", sol."ProductCode", sol."UomCode",
         sol."OrderQty", sol."ShippedQty", sol."StockQty", sol."StockUomCode"
ORDER BY 1, 2;

\echo ''
\echo '=== A-2. PUR：進貨行單位 <> 採購行單位 ==='
SELECT pol."PurchaseOrderId", pol."LineNo", pol."ProductCode",
       pol."UomCode"        AS po_uom,
       pol."OrderQty",
       pol."ReceivedQty"    AS received_qty_mixed,
       string_agg(DISTINCT grl."UomCode", ',') AS receipt_uoms,
       SUM(grl."StockQty")  AS received_stock_qty_correct,
       pol."StockQty"       AS order_stock_qty,
       pol."StockUomCode"   AS stock_uom
FROM "PurchaseOrderLines" pol
JOIN "GoodsReceiptLines" grl
     ON grl."PurchaseOrderId" = pol."PurchaseOrderId" AND grl."PoLineNo" = pol."LineNo"
JOIN "GoodsReceipts" gr ON gr."GoodsReceiptId" = grl."GoodsReceiptId"
WHERE gr."Status" <> 4
  AND COALESCE(grl."UomCode", '') <> COALESCE(pol."UomCode", '')
GROUP BY pol."PurchaseOrderId", pol."LineNo", pol."ProductCode", pol."UomCode",
         pol."OrderQty", pol."ReceivedQty", pol."StockQty", pol."StockUomCode"
ORDER BY 1, 2;

\echo ''
\echo '=== B. migration 會補值的行數（StockQty = 0 且單位相同 → 補成 OrderQty）==='
SELECT 'SalesOrderLines' AS tbl, COUNT(*) AS rows_to_backfill
FROM "SalesOrderLines"
WHERE "StockQty" = 0 AND "OrderQty" > 0
  AND COALESCE(NULLIF("StockUomCode", ''), "UomCode") IS NOT DISTINCT FROM "UomCode"
UNION ALL
SELECT 'PurchaseOrderLines', COUNT(*)
FROM "PurchaseOrderLines"
WHERE "StockQty" = 0 AND "OrderQty" > 0
  AND COALESCE(NULLIF("StockUomCode", ''), "UomCode") IS NOT DISTINCT FROM "UomCode";

\echo ''
\echo '=== C. 補不了的行（單位不同且 StockQty = 0）——這些行套用後會擋下所有新出貨/進貨 ==='
\echo '    處理方式：在畫面上重存一次該筆訂單（伺服端會重算 StockQty），或人工填值。'
SELECT 'SalesOrderLines' AS tbl, "SalesOrderId" AS doc_id, "LineNo", "ProductCode",
       "UomCode", "StockUomCode", "OrderQty", "StockQty"
FROM "SalesOrderLines"
WHERE "StockQty" = 0 AND "OrderQty" > 0
  AND COALESCE(NULLIF("StockUomCode", ''), "UomCode") IS DISTINCT FROM "UomCode"
UNION ALL
SELECT 'PurchaseOrderLines', "PurchaseOrderId", "LineNo", "ProductCode",
       "UomCode", "StockUomCode", "OrderQty", "StockQty"
FROM "PurchaseOrderLines"
WHERE "StockQty" = 0 AND "OrderQty" > 0
  AND COALESCE(NULLIF("StockUomCode", ''), "UomCode") IS DISTINCT FROM "UomCode"
ORDER BY 1, 2, 3;
