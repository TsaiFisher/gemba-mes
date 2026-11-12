-- ═══════════════════════════════════════════════════════════════════════════
-- 進料費用分攤的「費用類別」代碼（關稅／運費／報關費…）
-- ═══════════════════════════════════════════════════════════════════════════
--
-- 起因
--   2026-09-24 業主說「目前沒有如關稅、運費、報關費等外貌的作業資訊，
--   故測試使用幣別來看是否可行」——查證後**機制其實是完整的**：
--     `Modules/INV/Models/LandedCostAllocation.cs`   分攤單（金額／數量／重量三種基礎）
--     `Services/ProjectZMenuContributor.cs:193`      選單已登錄，畫面進得去
--     `LandedCostService`                            資本化進 Inventory.UnitCost 與 FIFO 成本層，
--                                                    並過帳 借原料存貨＋進料費用差異／貸暫估應付運費
--     `DocNumberConfigs`                             已有 LandedCostAllocation 的單號設定
--
--   缺的只有一樣東西：**`landed_cost_type` 這個代碼類別一筆都沒有**，
--   所以開單時「費用類別」下拉是空的——而空下拉看起來就像「這個系統沒有這個概念」。
--
-- 為什麼這是缺陷而不是「還沒建主檔」
--   `LandedCostAllocation.CostType` 標的是「可空」，所以少了它單子仍然開得成、
--   費用照樣分攤進成本。**不會報錯、不會擋流程**——它只是讓所有分攤單都沒有類別，
--   事後想問「這批進口的關稅佔多少」就永遠分不出來。這與乾淨部署缺
--   `DocNumberConfig`／異動類別的那一批是同一個家族：功能做完了，
--   啟動不了它的那一格主檔沒人種。
--
-- 執行
--     psql "$CONN" -v ON_ERROR_STOP=1 -f scripts/seed-landed-cost-types.sql
--
-- 冪等
--   唯一索引是 (Category, Code)，走 ON CONFLICT DO NOTHING。重跑不增列。
--
-- 怎麼清掉
--     DELETE FROM "CodeEntries" WHERE "Category" = 'landed_cost_type';
--
-- ⚠️ ApprovalStatus 寫 2（已核准），與既有 7 個代碼類別的現況一致。
--    留 0（草稿）的話代碼建了卻可能在簽核流程裡半路卡住，而下拉依舊是空的
--    ——症狀與完全沒建一模一樣，卻更難查。
-- ═══════════════════════════════════════════════════════════════════════════

\set ON_ERROR_STOP on

INSERT INTO "CodeEntries" ("Id", "Category", "Code", "NameZh", "NameEn", "Remark", "IsActive", "SortOrder", "ApprovalStatus")
VALUES
  (gen_random_uuid()::text, 'landed_cost_type', 'FREIGHT',   '國際運費',   'International Freight', '海／空運主運費',                   TRUE, 1, 2),
  (gen_random_uuid()::text, 'landed_cost_type', 'DUTY',      '進口關稅',   'Import Duty',           '依完稅價格與稅則課徵',             TRUE, 2, 2),
  (gen_random_uuid()::text, 'landed_cost_type', 'CUSTOMS',   '報關費',     'Customs Clearance Fee', '報關行服務費、簽審規費',           TRUE, 3, 2),
  (gen_random_uuid()::text, 'landed_cost_type', 'INSURANCE', '運輸保險費', 'Freight Insurance',     '貨物運輸保險',                     TRUE, 4, 2),
  (gen_random_uuid()::text, 'landed_cost_type', 'HANDLING',  '港口作業費', 'Terminal Handling',     'THC／吊櫃／倉租等港區費用',        TRUE, 5, 2),
  (gen_random_uuid()::text, 'landed_cost_type', 'INLAND',    '內陸運費',   'Inland Freight',        '港口到廠的拖車費',                 TRUE, 6, 2)
ON CONFLICT ("Category", "Code") DO NOTHING;

\echo ''
\echo '--- landed_cost_type 代碼 ---'
SELECT "Code", "NameZh", "NameEn", "SortOrder"
FROM   "CodeEntries" WHERE "Category" = 'landed_cost_type' AND "IsActive"
ORDER BY "SortOrder";
