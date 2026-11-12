-- ═══════════════════════════════════════════════════════════════════════════
-- 廠商單價目錄測試資料（採購單價分析的「標準價」欄）
-- ═══════════════════════════════════════════════════════════════════════════
--
-- 用途
--   `PurchasePriceTool` 逐筆列出進貨明細的實際單價，右邊對上 `VendorPriceLists`
--   的議定價。兩個庫的 `VendorPriceLists` 都是 0 筆，所以那一欄目前**恆為空白**
--   ——報表跑得出來、筆數也對，只是它存在的理由（比價）一格都沒有。
--   本腳本從**既有進貨歷史**反推議定價，讓每一筆進貨都比得出高低。
--
-- 為什麼不寫成 Seeder
--   這是示範／驗收資料不是系統種子。議定價一旦進 `Program.cs` 的 seeder，
--   就會跟著出貨到客戶的乾淨部署，而那是一批憑空捏造的商業條件。
--
-- 執行
--     psql "$CONN" -v ON_ERROR_STOP=1 -f scripts/seed-vendor-pricelist.sql
--
-- 冪等
--   唯一索引是 (SupplierCode, ProductCode, CurrencyCode, ValidFrom)，
--   全部 INSERT 走 ON CONFLICT DO NOTHING。重跑不會增列也不會失敗。
--
-- 怎麼清掉
--   本腳本建立的列 Remarks 一律為 'SEED:vendor-pricelist'，移除用：
--     DELETE FROM "VendorPriceLists" WHERE "Remarks" = 'SEED:vendor-pricelist';
--   ⚠️ 不要用「全刪」——手動建過的議定價沒有這個標記，會一起被清掉。
--
-- ─────────────────────────────────────────────────────────────────────────
-- 三個刻意的設計決定
--
-- 1) **價差是決定性的，不是亂數**。同一個 (廠商, 品號) 每次重建都落在同一個
--    百分比上，否則「上次看到的差異這次不一樣」會讓人以為程式改了行為。
--    偏移量取自 md5(廠商||品號) 的分桶，範圍 −8% ~ +16%，兩個方向都有：
--    標準價高於實際＝議價有利，標準價低於實際＝**超買**，後者才是報表要抓的。
--
-- 2) **刻意留約 1/8 的組合沒有議定價**。全部補滿之後，`StandardUnitPrice`
--    為 null 的那條分支（畫面顯示「—」、差異欄不計算）就再也沒有資料走得到，
--    而那正是今天唯一有覆蓋的狀態。驗收要同時看得到「有標準價」與「沒有」。
--
-- 3) **幣別留空的進貨一律跳過，不給預設值**。`VendorPriceLists.CurrencyCode`
--    有 FK，硬塞 'TWD' 在沒有該幣別的庫上會直接違反外鍵；而更糟的是塞對了
--    也會讓「幣別留空」這個真實資料形狀被這批種子蓋掉——委外單價工具就是
--    在這個形狀上被實測抓到過缺陷（空字串被當成另一種幣別）。跳過的筆數
--    會在最後印出來。
-- ═══════════════════════════════════════════════════════════════════════════

\set ON_ERROR_STOP on

BEGIN;

WITH receipts AS (
    -- 口徑逐項照抄 PurchasePriceTool.ExecuteAsync：只取已過帳的進貨。
    -- 種子與報表讀同一批單據，否則會種出報表看不到的議定價。
    SELECT  gr."SupplierCode"                          AS supplier_code,
            grl."ProductCode"                          AS product_code,
            NULLIF(TRIM(COALESCE(grl."CurrencyCode",'')),'') AS currency_code,
            grl."UnitPrice"                            AS unit_price,
            gr."ReceiptDate"                           AS receipt_date
    FROM    "GoodsReceiptLines" grl
    JOIN    "GoodsReceipts"     gr ON gr."GoodsReceiptId" = grl."GoodsReceiptId"
    WHERE   gr."Status" = 3            -- DocStatus.Posted
      AND   grl."UnitPrice" > 0        -- 單價 0 的進貨反推不出議定價
),
combos AS (
    SELECT  supplier_code,
            product_code,
            currency_code,
            AVG(unit_price)   AS avg_price,
            MIN(receipt_date) AS first_receipt,
            COUNT(*)          AS receipt_lines
    FROM    receipts
    WHERE   currency_code IS NOT NULL
    GROUP BY supplier_code, product_code, currency_code
),
bucketed AS (
    SELECT  c.*,
            -- 0..7，對同一組 (廠商, 品號) 恆定
            MOD(('x' || SUBSTR(MD5(c.supplier_code || '|' || c.product_code), 1, 7))::BIT(28)::INT, 8) AS bucket
    FROM    combos c
),
priced AS (
    SELECT  b.supplier_code,
            b.product_code,
            b.currency_code,
            b.first_receipt,
            -- bucket 1..7 → −8, −4, 0, +4, +8, +12, +16 (%)
            ROUND(b.avg_price * (1 + ((b.bucket - 3) * 4)::NUMERIC / 100), 6) AS std_price
    FROM    bucketed b
    WHERE   b.bucket <> 0        -- bucket 0 刻意不給議定價（見設計決定 2）
      -- CurrencyCode 有 FK→Currencies。進貨明細的幣別欄不保證是 Currencies 裡的值，
      -- 不檢查的話整份腳本會在某一列違反外鍵而全數 rollback——而錯誤訊息講的是
      -- 約束名稱，看不出是哪一筆進貨的幣別沒建主檔。
      AND EXISTS (SELECT 1 FROM "Currencies" cu WHERE cu."CurrencyCode" = b.currency_code)
)
INSERT INTO "VendorPriceLists"
        ("SupplierCode", "ProductCode", "CurrencyCode", "UnitPrice",
         "MinQty", "ValidFrom", "ValidTo", "IsActive", "Remarks",
         "CreatedAt", "UpdatedAt")
SELECT   p.supplier_code,
         p.product_code,
         p.currency_code,
         GREATEST(p.std_price, 0.000001),   -- 折讓後仍須為正
         NULL,                               -- MinQty：不設限，免得帶價邏輯被最小量擋掉
         p.first_receipt,                    -- 起日＝該組合最早那筆進貨，確保「現行有效」
         NULL,                               -- 迄日留空＝長期有效
         TRUE,
         'SEED:vendor-pricelist',
         NOW(), NOW()
FROM     priced p
ON CONFLICT ("SupplierCode", "ProductCode", "CurrencyCode", "ValidFrom") DO NOTHING;

COMMIT;

-- ─────────────────────────────────────────────────────────────────────────
-- 驗收：跑完之後這三個數字要合得起來
-- ─────────────────────────────────────────────────────────────────────────

\echo ''
\echo '--- 本腳本建立的議定價 ---'
SELECT COUNT(*) AS seeded_rows FROM "VendorPriceLists" WHERE "Remarks" = 'SEED:vendor-pricelist';

\echo '--- 幣別留空而跳過的進貨明細（設計決定 3）---'
SELECT COUNT(*) AS skipped_blank_currency
FROM   "GoodsReceiptLines" grl
JOIN   "GoodsReceipts" gr ON gr."GoodsReceiptId" = grl."GoodsReceiptId"
WHERE  gr."Status" = 3 AND grl."UnitPrice" > 0
  AND  NULLIF(TRIM(COALESCE(grl."CurrencyCode",'')),'') IS NULL;

\echo '--- 報表會長成什麼樣（比價命中率，應約 7/8 有標準價）---'
SELECT  COUNT(*)                                        AS 進貨明細數,
        COUNT(v."Id")                                   AS 有標準價,
        COUNT(*) - COUNT(v."Id")                        AS 無標準價,
        COUNT(*) FILTER (WHERE grl."UnitPrice" > v."UnitPrice") AS 實際高於標準_超買,
        COUNT(*) FILTER (WHERE grl."UnitPrice" < v."UnitPrice") AS 實際低於標準_有利
FROM    "GoodsReceiptLines" grl
JOIN    "GoodsReceipts" gr ON gr."GoodsReceiptId" = grl."GoodsReceiptId"
LEFT JOIN "VendorPriceLists" v
       ON  v."SupplierCode" = gr."SupplierCode"
       AND v."ProductCode"  = grl."ProductCode"
       AND v."IsActive"
       AND v."ValidFrom" <= CURRENT_DATE
       AND (v."ValidTo" IS NULL OR v."ValidTo" >= CURRENT_DATE)
WHERE   gr."Status" = 3;
