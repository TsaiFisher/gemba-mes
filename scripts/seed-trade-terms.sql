-- ═══════════════════════════════════════════════════════════════════════════
-- 貿易條件（Incoterms® 2020）代碼 — 採購單與銷貨訂單表頭共用
-- ═══════════════════════════════════════════════════════════════════════════
--
-- 為什麼這一格重要
--   Incoterms 決定**誰負擔運費、保險、關稅與報關費**，也就是決定那些費用
--   該不該經 `LandedCostAllocation` 資本化進我們的存貨：
--     EXW / FCA / FOB …  這些是**我們的**  → 要進分攤單
--     CIF / CIP          運費保險是對方的  → 只有關稅與報關費是我們的
--     DDP                全部是對方的      → **一毛都不該**出現在分攤單裡
--   沒有這一格的時候，開分攤單的人只能靠記憶判斷，而掛錯**不會有任何訊號**：
--   單子照過、成本照算，只是那批料的單價永久多了或少了一截。
--
--   出口側（銷貨訂單）語意不同：運費保險**不資本化**（貨在離開），
--   它決定的是營業費用歸屬與**風險移轉時點＝收入認列時點**。
--
-- 執行
--     psql "$CONN" -v ON_ERROR_STOP=1 -f scripts/seed-trade-terms.sql
--   ⚠️ 需先套用 migration `PUR_ORD_TradeTermCode`（新增兩張表的欄位）。
--   本腳本只種代碼，不動單據。
--
-- 冪等
--   唯一索引 (Category, Code)，走 ON CONFLICT DO NOTHING。
--
-- 怎麼清掉
--     DELETE FROM "CodeEntries" WHERE "Category" = 'trade_term';
--
-- ⚠️ ApprovalStatus 寫 2（已核准），與既有代碼類別一致。留 0（草稿）的話
--    代碼建了卻可能卡在簽核流程，下拉依舊是空的——症狀與完全沒建一樣卻更難查。
--
-- 排序刻意依「賣方責任由小到大」而非字母序：談條件時是沿著這條軸移動的
-- （EXW 賣方責任最小 → DDP 最大），字母序會把 CIF 排在 FOB 前面而破壞這個直覺。
-- ═══════════════════════════════════════════════════════════════════════════

\set ON_ERROR_STOP on

INSERT INTO "CodeEntries" ("Id", "Category", "Code", "NameZh", "NameEn", "Remark", "IsActive", "SortOrder", "ApprovalStatus")
VALUES
  -- 任何運送方式皆適用
  (gen_random_uuid()::text, 'trade_term', 'EXW', '工廠交貨',       'Ex Works',                      '賣方責任最小：買方自賣方廠區提貨，出口報關亦由買方負責',       TRUE,  1, 2),
  (gen_random_uuid()::text, 'trade_term', 'FCA', '貨交運送人',     'Free Carrier',                  '賣方於指定地點交予買方指定運送人，出口報關由賣方負責',         TRUE,  2, 2),
  (gen_random_uuid()::text, 'trade_term', 'CPT', '運費付訖',       'Carriage Paid To',              '賣方付運費至指定目的地，風險於交付第一運送人時移轉',           TRUE,  3, 2),
  (gen_random_uuid()::text, 'trade_term', 'CIP', '運保費付訖',     'Carriage and Insurance Paid To','同 CPT 另加保險（2020 版要求投保 ICC(A) 較高等級）',            TRUE,  4, 2),
  (gen_random_uuid()::text, 'trade_term', 'DAP', '目的地交貨',     'Delivered at Place',            '賣方負責運至指定地點，**進口報關與關稅由買方負責**',           TRUE,  5, 2),
  (gen_random_uuid()::text, 'trade_term', 'DPU', '卸貨地交貨',     'Delivered at Place Unloaded',   '同 DAP 且由賣方負責卸貨（2020 版取代舊 DAT）',                  TRUE,  6, 2),
  (gen_random_uuid()::text, 'trade_term', 'DDP', '稅訖交貨',       'Delivered Duty Paid',           '賣方責任最大：含進口報關與關稅。**此條件下進項費用不進分攤單**', TRUE,  7, 2),
  -- 僅適用海運及內陸水路運送
  (gen_random_uuid()::text, 'trade_term', 'FAS', '船邊交貨',       'Free Alongside Ship',           '僅限海運：賣方於裝運港船邊交貨',                               TRUE,  8, 2),
  (gen_random_uuid()::text, 'trade_term', 'FOB', '船上交貨',       'Free on Board',                 '僅限海運：風險於貨物裝載上船時移轉，運費保險由買方負擔',       TRUE,  9, 2),
  (gen_random_uuid()::text, 'trade_term', 'CFR', '運費在內',       'Cost and Freight',              '僅限海運：賣方付運費，保險由買方自理',                         TRUE, 10, 2),
  (gen_random_uuid()::text, 'trade_term', 'CIF', '運保費在內',     'Cost, Insurance and Freight',   '僅限海運：賣方付運費與保險，**關稅與報關費仍為買方負擔**',     TRUE, 11, 2)
ON CONFLICT ("Category", "Code") DO NOTHING;

\echo ''
\echo '--- 貿易條件代碼（依賣方責任由小到大）---'
SELECT "SortOrder" AS 序, "Code" AS 代碼, "NameZh" AS 中文, "NameEn" AS 英文
FROM   "CodeEntries" WHERE "Category" = 'trade_term' AND "IsActive"
ORDER BY "SortOrder";
