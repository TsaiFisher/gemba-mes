-- ═══════════════════════════════════════════════════════════════════════════
-- E2E 流程測試專用種子（階段 1）
-- ═══════════════════════════════════════════════════════════════════════════
--
-- 用途
--   主檔 CRUD smoke test 自給自足（自己建、自己刪），但流程測試（備料→開工→
--   報工→完工、採購 P2P、發料→入庫…）需要工單、派工、庫存餘量這些上游資料。
--   全新部署的資料庫**沒有任何主檔**——Program.cs 只 seed 廠別、admin、角色、
--   選單權限、單號設定、庫存交易類型與一個待驗倉 QC-P1。因此整條上游鏈必須由
--   本腳本建立。
--
-- 執行時機
--   migration 之後、app 啟動之前（或啟動之後都可，本腳本不依賴 app）。
--     psql "$CONN" -v ON_ERROR_STOP=1 -f scripts/seed-e2e-fixture.sql
--
-- 兩條硬規則
--   1) **冪等**：整份可重複執行，不得因既有資料而失敗。所有 INSERT 都帶
--      ON CONFLICT，數量類欄位一律用 UPDATE 覆寫回基準值——流程測試會消耗庫存、
--      推進單據狀態，下一輪必須從同一個起點開始，否則「同一個庫重跑仍綠」做不到。
--   2) **隔離**：所有代碼一律 E2E- 前綴，單號自成號段（E2E-WO0001 / E2E-DP0001），
--      與 DocNumberService 產生的流水號不可能相撞。既有專案曾因兩廠同產
--      JE00001 撞 PK，隔離號段是既定做法。
--
-- 廠別
--   一律 P1（Program.cs 種的唯一廠別，也是 admin 的 ActivePlantId）。
--
-- 資料授權
--   IDataAuthEntity 的單據必須設 CreatedByRoleId，否則全域過濾會把資料藏起來，
--   畫面查不到、開單據直接 404。admin 的 ActiveRoleId = '1'（Admin）。
-- ═══════════════════════════════════════════════════════════════════════════

\set ON_ERROR_STOP on

BEGIN;

-- ═══ ⓪ 清除上一輪流程測試的產物 ═════════════════════════════════════════════
-- 流程測試（備料→開工→報工→完工）會建立備料單、預留、庫存異動、備料/開工/報工紀錄。
-- 這些是**過程產物**，不像主檔可以用 ON CONFLICT 覆寫回基準值，只能刪掉重來——
-- 留著的話下一輪會重複鎖料、備料單越積越多，症狀是「流程測試偶爾紅」，最難追。
--
-- 一律以 E2E- 字樣限定範圍，絕不碰其他資料。
DELETE FROM "MaterialStagingLines"
WHERE "StagingId" IN (SELECT "StagingId" FROM "MaterialStagingOrders" WHERE "SourceId" LIKE 'E2E-%');
DELETE FROM "MaterialStagingOrders" WHERE "SourceId" LIKE 'E2E-%';

DELETE FROM "InventoryReservations" WHERE "SourceDocId" LIKE 'E2E-%' OR "ProductCode" LIKE 'E2E-%';

DELETE FROM "ProductionReportContainers"
WHERE "ReportId" IN (SELECT "ReportId" FROM "ProductionReports" WHERE "DispatchId" LIKE 'E2E-%');
DELETE FROM "ProductionReports"  WHERE "DispatchId" LIKE 'E2E-%';
DELETE FROM "ProductionSessions" WHERE "DispatchId" LIKE 'E2E-%';
DELETE FROM "ProductionSetups"   WHERE "DispatchId" LIKE 'E2E-%';
-- ⑯ 派工例外操作產生的子派工（換機續做／拆分的 <原單號>-s<n>）：不在種子的 upsert 範圍內，
-- 留著的話下一輪拆分序號續編、換機續做多一張，斷言數量全錯。
DELETE FROM "Dispatches" WHERE "DispatchId" LIKE 'E2E-DP002_-%';

DELETE FROM "StockMovements" WHERE "ProductCode" LIKE 'E2E-%';

-- 工單完工的過程產物：工單本身下面會覆寫回基準值，但完工傳票與實際成本紀錄不會跟著消失。
-- 本機重跑幾輪後 E2E-WO0001 會掛著 N 張完工傳票（關帳前稽核 JOURNAL_DUPLICATE）、
-- 成本紀錄與吸收分錄對不起來（WO_COST_JOURNAL_DRIFT），月結第 10 步永遠關不了帳。
DELETE FROM "JournalLines" WHERE "JournalId" IN (
  SELECT "JournalId" FROM "JournalEntries"
  WHERE "SourceDocType" = 'WorkOrderCompletion' AND "SourceDocId" LIKE 'E2E-%');
DELETE FROM "JournalEntries"
  WHERE "SourceDocType" = 'WorkOrderCompletion' AND "SourceDocId" LIKE 'E2E-%';
DELETE FROM "WorkOrderActualCosts" WHERE "WorkOrderId" LIKE 'E2E-%';

-- JournalEntryActionSmokeTests 每跑一次開一張「E2E 手動傳票 …」。列表依分錄日期排序、同日先後不定，
-- 那支測試靠「新單在第一頁」找回剛存的那張；同日累積超過一頁（20 張）就找不到而紅。
DELETE FROM "JournalLines" WHERE "JournalId" IN (
  SELECT "JournalId" FROM "JournalEntries"
  WHERE "SourceDocType" = 'Manual' AND "Description" LIKE 'E2E 手動傳票 %');
DELETE FROM "JournalEntries"
  WHERE "SourceDocType" = 'Manual' AND "Description" LIKE 'E2E 手動傳票 %';

-- 折讓單（⑲）：測試會把草稿按「確認」過帳，下一輪必須回到草稿，而且那一次確認產生的
-- 分錄也要一起清——否則 E2E 發票的 DebitedAmount／CreditedAmount 被重設了，
-- 總帳上卻還掛著上一輪的折讓分錄，子帳與總帳對不起來（對帳類稽核會叫）。
DELETE FROM "JournalLines" WHERE "JournalId" IN (
  SELECT "JournalId" FROM "JournalEntries"
  WHERE "SourceDocType" IN ('APDebitNote','ARCreditNote') AND "SourceDocId" LIKE 'E2E-%');
DELETE FROM "JournalEntries"
  WHERE "SourceDocType" IN ('APDebitNote','ARCreditNote') AND "SourceDocId" LIKE 'E2E-%';
DELETE FROM "APDebitNoteLines"  WHERE "APDebitNoteId"  LIKE 'E2E-%';
DELETE FROM "APDebitNotes"      WHERE "APDebitNoteId"  LIKE 'E2E-%';
DELETE FROM "ARCreditNoteLines" WHERE "ARCreditNoteId" LIKE 'E2E-%';
DELETE FROM "ARCreditNotes"     WHERE "ARCreditNoteId" LIKE 'E2E-%';

-- 進料費用分攤單（⑭ 的下游）：單號由 DocNumberService 產生（LCA-yymm-nnnnn），沒有 E2E- 字樣，
-- 改以「明細參照 E2E 進貨單或 E2E 品號」限定範圍（流程測試建的進貨單是 RC- 採番，但品號仍是 E2E-）。
-- 之前完全沒清，測試每跑一輪就多幾張草稿（9/24 起累積）；
-- 動作測試會按「確認」，確認產生的分錄也要一起清，否則總帳掛著已不存在的分攤單。
CREATE TEMP TABLE _e2e_lca ON COMMIT DROP AS
  SELECT DISTINCT "LandedCostAllocationId" AS id FROM "LandedCostAllocationLines"
  WHERE "GoodsReceiptId" LIKE 'E2E-%' OR "ProductCode" LIKE 'E2E-%';
DELETE FROM "JournalLines" WHERE "JournalId" IN (
  SELECT "JournalId" FROM "JournalEntries"
  WHERE "SourceDocType" = 'LandedCostAllocation' AND "SourceDocId" IN (SELECT id FROM _e2e_lca));
DELETE FROM "JournalEntries"
  WHERE "SourceDocType" = 'LandedCostAllocation' AND "SourceDocId" IN (SELECT id FROM _e2e_lca);
DELETE FROM "LandedCostAllocationLines" WHERE "LandedCostAllocationId" IN (SELECT id FROM _e2e_lca);
DELETE FROM "LandedCostAllocations"     WHERE "LandedCostAllocationId" IN (SELECT id FROM _e2e_lca);

-- 委外收回 → IQC → 入庫流程（⑮，OutsourceIqcFlowTests）：收貨單號由 DocNumberService 採番（沒有 E2E- 字樣），
-- 以「來源委外採購單 = E2E-OPO02」限定範圍。非清不可：確認收貨時會依**累計已收量**判定超收，
-- 上一輪收滿的量留著，下一輪一確認就被超收規則擋下。收貨確認時開的應付發票、IQC 紀錄、
-- 合格放行的系統調撥（品號 E2E-OS01）都是同一輪的過程產物，一起清；庫存異動已由上方依品號清掉。
-- ⑰ 起範圍再加上 E2E-OPO03（工序委外）與投產時自動建給 E2E-WO0012 的委外採購單（採番、沒有 E2E- 字樣）。
CREATE TEMP TABLE _e2e_auto_opo ON COMMIT DROP AS
  SELECT DISTINCT "OutsourcePurchaseOrderId" AS id FROM "OutsourcePurchaseOrderLines" WHERE "SourceWorkOrderId" = 'E2E-WO0012';
CREATE TEMP TABLE _e2e_osr ON COMMIT DROP AS
  SELECT "OutsourceReceiptId" AS id FROM "OutsourceReceipts"
  WHERE "OutsourcePurchaseOrderId" IN ('E2E-OPO02','E2E-OPO03') OR "OutsourcePurchaseOrderId" IN (SELECT id FROM _e2e_auto_opo);
CREATE TEMP TABLE _e2e_osr_ap ON COMMIT DROP AS
  SELECT "APInvoiceId" AS id FROM "APInvoices"
  WHERE "SourceDocType" = 'OutsourceReceipt' AND "SourceDocId" IN (SELECT id FROM _e2e_osr);
DELETE FROM "JournalLines" WHERE "JournalId" IN (
  SELECT "JournalId" FROM "JournalEntries"
  WHERE "SourceDocId" IN (SELECT id FROM _e2e_osr UNION SELECT id FROM _e2e_osr_ap));
DELETE FROM "JournalEntries"
  WHERE "SourceDocId" IN (SELECT id FROM _e2e_osr UNION SELECT id FROM _e2e_osr_ap);
DELETE FROM "APInvoiceLines" WHERE "APInvoiceId" IN (SELECT id FROM _e2e_osr_ap);
DELETE FROM "APInvoices"     WHERE "APInvoiceId" IN (SELECT id FROM _e2e_osr_ap);
DELETE FROM "MobileIqcRecords" WHERE "GoodsReceiptId" IN (SELECT id FROM _e2e_osr);
DELETE FROM "InspectionOrders" WHERE "InspectionType" = 'IQC' AND "SourceDocId" IN (SELECT id FROM _e2e_osr);
CREATE TEMP TABLE _e2e_os_st ON COMMIT DROP AS
  SELECT DISTINCT "StockTransferId" AS id FROM "StockTransferLines" WHERE "ProductCode" = 'E2E-OS01';
DELETE FROM "StockTransferLines" WHERE "StockTransferId" IN (SELECT id FROM _e2e_os_st);
DELETE FROM "StockTransfers"     WHERE "StockTransferId" IN (SELECT id FROM _e2e_os_st);
DELETE FROM "OutsourceReceiptLocationDetails" WHERE "OutsourceReceiptId" IN (SELECT id FROM _e2e_osr);
DELETE FROM "OutsourceReceiptLines" WHERE "OutsourceReceiptId" IN (SELECT id FROM _e2e_osr);
DELETE FROM "OutsourceReceipts"     WHERE "OutsourceReceiptId" IN (SELECT id FROM _e2e_osr);
-- ⑰ 委外簡化：收貨確認時系統自動開的委外發料單（B1／工序委外 C），以及投產自動建的委外採購單與委外建議（B3）
DELETE FROM "MaterialSlipLines" WHERE "SlipId" IN (
  SELECT "SlipId" FROM "MaterialSlips" WHERE "SourceDocId" IN ('E2E-WO0011','E2E-WO0031'));
DELETE FROM "MaterialSlips" WHERE "SourceDocId" IN ('E2E-WO0011','E2E-WO0031');
DELETE FROM "OutsourcePurchaseOrderLines" WHERE "OutsourcePurchaseOrderId" IN (SELECT id FROM _e2e_auto_opo);
DELETE FROM "OutsourcePurchaseOrders"     WHERE "OutsourcePurchaseOrderId" IN (SELECT id FROM _e2e_auto_opo);
DELETE FROM "OutsourceDemands" WHERE "WorkOrderId" = 'E2E-WO0012';

-- 特殊日 smoke test 用 2090 年以後的遠期日期當唯一鍵，該區間不可能有真實營運資料。
-- 那支測試自己會刪，但清除是 best-effort（面板遮罩等時序問題會讓刪除靜默失敗），
-- 殘留累積到一定量就會撞日期而間歇性紅燈。這裡兜底，把累積上限壓住。
DELETE FROM "SpecialDays" WHERE "Year" >= 2090;

-- ── 常數 ────────────────────────────────────────────────────────────────────
-- PlantId / RoleId 以字面值寫死，psql 變數在 ON CONFLICT 子句中可讀性差。
--   PlantId = 'P1'    RoleId = '1'

-- ═══ ① 計量單位（全域主檔，無廠別）═════════════════════════════════════════
INSERT INTO "Uoms" ("UomCode","UomName","Remarks","DecimalPlaces","IsActive","CreatedAt","UpdatedAt")
VALUES ('E2E-EA','E2E 個','E2E 流程測試專用',0,true,now(),now())
ON CONFLICT ("UomCode") DO UPDATE
  SET "IsActive"=true, "DecimalPlaces"=0, "UpdatedAt"=now();

-- ═══ ①-b 幣別 / 班別 / 班別群組 ════════════════════════════════════════════
-- 這三項不是流程測試直接用的，而是「明細分頁」類 smoke test 的前提：那些測試要求
-- 列表**已有資料**才能開明細面板。在真正空的資料庫上，它們原本只是碰巧靠其他
-- smoke test 建出來的資料才會過——等於相依於測試執行順序。由種子保證存在。
INSERT INTO "Currencies" ("CurrencyCode","CurrencyName","ShortName","Symbol","DecimalPlaces","IsActive","CreatedAt","UpdatedAt")
VALUES ('E2E-CUR','E2E 測試幣別','E2E','$',2,true,now(),now())
ON CONFLICT ("CurrencyCode") DO UPDATE SET "IsActive"=true, "UpdatedAt"=now();

-- E2E-CUR 不是本位幣，凡要換算本幣的單據（到岸成本自 2026-10-23 起以單據日買匯換算、查無擋下）都需要匯率。
-- 1:1 讓本幣金額等於原幣，不影響任何金額斷言。日期取 400 天前：OutsourceSimplifiedE2ETests 會插再刪
-- 「今天」那筆，避開同一天；換算取「單據日以前最近一筆」，所以舊日期一樣適用。外幣重評的期末匯率由測試自行輸入，不讀此表。
INSERT INTO "CurrencyRates" ("CurrencyCode","RateDate","BuyRate","SellRate")
VALUES ('E2E-CUR', current_date - 400, 1, 1)
ON CONFLICT ("CurrencyCode","RateDate") DO NOTHING;

-- 稅別：採購／銷售單據的明細驗證是「有金額就必須指定幣別與稅別」
-- （PurchaseOrderController / SalesOrderController 的行驗證），乾淨資料庫一筆稅別
-- 都沒有 → 下拉是空的 → 只要單價不為 0 就存不了檔。ApprovalStatus=2（已核准）。
INSERT INTO "Taxes" ("TaxCode","TaxName","TaxRate","Remarks","IsActive","ApprovalStatus","CreatedAt","UpdatedAt")
VALUES ('E2E-TAX','E2E 測試稅別',0.05,'E2E 流程測試專用',true,2,now(),now())
ON CONFLICT ("TaxCode") DO UPDATE
  SET "IsActive"=true, "TaxRate"=0.05, "ApprovalStatus"=2, "UpdatedAt"=now();

INSERT INTO "Shifts" ("ShiftCode","ShiftName","StartTime","EndTime","WorkMinutes","IsActive","PlantId","CreatedAt","UpdatedAt")
VALUES ('E2E-SH1','E2E 早班','08:00','17:00',480,true,'P1',now(),now())
ON CONFLICT ("ShiftCode") DO UPDATE SET "IsActive"=true, "PlantId"='P1', "UpdatedAt"=now();

-- IsDefault=false：不可搶走既有部署的預設班別群組。
INSERT INTO "ShiftGroups" ("GroupCode","GroupName","IsActive","IsDefault","PlantId","CreatedAt","UpdatedAt")
VALUES ('E2E-SG1','E2E 班別群組',true,false,'P1',now(),now())
ON CONFLICT ("GroupCode") DO UPDATE SET "IsActive"=true, "PlantId"='P1', "UpdatedAt"=now();

-- 週一～週五上班（DayOfWeek 0=週日），週六日不排班；明細分頁需要有子列可顯示。
INSERT INTO "ShiftGroupDays" ("GroupCode","DayOfWeek","Shift1Code","WorkStart","WorkEnd","DailyRegularHours","DailyOvertimeHours")
SELECT 'E2E-SG1', d,
       CASE WHEN d BETWEEN 1 AND 5 THEN 'E2E-SH1' END,
       CASE WHEN d BETWEEN 1 AND 5 THEN time '08:00' END,
       CASE WHEN d BETWEEN 1 AND 5 THEN time '17:00' END,
       CASE WHEN d BETWEEN 1 AND 5 THEN 8 ELSE 0 END, 0
FROM generate_series(0,6) AS d
ON CONFLICT ("GroupCode","DayOfWeek") DO UPDATE
  SET "Shift1Code"=EXCLUDED."Shift1Code",
      "DailyRegularHours"=EXCLUDED."DailyRegularHours";

-- ═══ ①-c 往來對象（全域主檔）════════════════════════════════════════════════
-- 樣品單等單據的「往來對象」是必填，而乾淨資料庫一筆公司主檔都沒有。
-- 同時具備客戶與供應商身分，供不同單據共用一筆即可。
INSERT INTO "Companies"
  ("CompanyCode","CompanyName","ShortName","IsCustomer","IsSupplier","TaxRate",
   "IsActive","ApprovalStatus","QualificationStatus","Remarks","CreatedAt","UpdatedAt")
VALUES ('E2E-CO01','E2E 測試往來對象','E2E',true,true,0.05,true,0,0,'E2E 流程測試專用',now(),now())
ON CONFLICT ("CompanyCode") DO UPDATE
  SET "IsActive"=true, "IsCustomer"=true, "IsSupplier"=true,
      "ApprovalStatus"=0, "UpdatedAt"=now();

-- ═══ ② 產品分類（全域主檔）══════════════════════════════════════════════════
INSERT INTO "ProductCategories"
  ("CategoryCode","CategoryName","CostMethod","EnableLotTracking","IsActive",
   "DefaultStockUomCode","Remarks","CreatedAt","UpdatedAt")
VALUES ('E2E-CAT','E2E 測試分類',0,false,true,'E2E-EA','E2E 流程測試專用',now(),now())
ON CONFLICT ("CategoryCode") DO UPDATE
  SET "IsActive"=true, "UpdatedAt"=now();

-- ═══ ③ 品號（全域主檔）══════════════════════════════════════════════════════
-- E2E-FG01 成品（MrpMode=Make），E2E-RM01/RM02 原料（MrpMode=Purchase）。
-- IsLotControlled=false：流程測試不驗批號管制，少一層無關的失敗來源。
--
-- E2E-PP01 是 P2P 流程測試（採購→進貨→上架→應付→付款）專用的**免驗直放**料號：
-- 上架至正常倉會被品質隔離閘門擋下（WarehouseApiController.Fulfillment 的
-- GoodsReceiptMustPutawayToQuarantineFirst），除非該明細已 IQC 合格或品號設
-- IqcExempt=true。P2P 流程要驗的是「貨進正常倉 → 就地認列應付」，不是 IQC，
-- 故獨立一個免驗品號，而不去改 RM01/RM02 的性質（那會連帶影響 IQC 相關測試）。
INSERT INTO "Products"
  ("ProductCode","ProductName","Spec","RoutingId","SalesUom","PurchaseUom","StockUom",
   "StockMode","SafetyStock","StandardCost","MrpMode","LeadTimeDays","UrgentLeadTimeDays",
   "OutsourceQtyPerDay","IsLotControlled","IqcExempt","IsActive","ApprovalStatus","CategoryCode",
   "CreatedAt","UpdatedAt")
VALUES
  ('E2E-FG01','E2E 成品 FG01','E2E 流程測試成品','E2E-RT01','E2E-EA','E2E-EA','E2E-EA',
   'M',0,100,'M',1,1,0,false,false,true,0,'E2E-CAT',now(),now()),
  ('E2E-RM01','E2E 原料 RM01','E2E 流程測試原料 A',NULL,'E2E-EA','E2E-EA','E2E-EA',
   'P',0,10,'P',1,1,0,false,false,true,0,'E2E-CAT',now(),now()),
  ('E2E-RM02','E2E 原料 RM02','E2E 流程測試原料 B',NULL,'E2E-EA','E2E-EA','E2E-EA',
   'P',0,20,'P',1,1,0,false,false,true,0,'E2E-CAT',now(),now()),
  ('E2E-PP01','E2E 採購料 PP01','E2E P2P 流程測試專用（免驗直放）',NULL,'E2E-EA','E2E-EA','E2E-EA',
   'P',0,50,'P',1,1,0,false,true ,true,0,'E2E-CAT',now(),now()),
  -- E2E-LT01：批號流程測試（LotTrackingFlowTests）專用的**批號管制**料號，IsLotControlled=true。
  -- 其餘料號維持 false，免得批號規則牽連無關的流程測試。
  ('E2E-LT01','E2E 批號料 LT01','E2E 批號管制流程測試專用',NULL,'E2E-EA','E2E-EA','E2E-EA',
   'P',0,10,'P',1,1,0,true ,true ,true,0,'E2E-CAT',now(),now())
ON CONFLICT ("ProductCode") DO UPDATE
  SET "IsActive"=true, "ApprovalStatus"=0, "StockUom"='E2E-EA',
      "IqcExempt"=EXCLUDED."IqcExempt", "IsLotControlled"=EXCLUDED."IsLotControlled",
      "CategoryCode"='E2E-CAT', "UpdatedAt"=now();

-- ═══ ④ 倉庫與儲位（廠別主檔）════════════════════════════════════════════════
-- WarehouseType：0=Raw 原料倉、6=Staging 備料倉（見 Modules/BAS/Models/WarehouseType.cs）。
-- 備料倉是「備料單移入的待領用區」，庫存仍計入廠級可用量。
INSERT INTO "Warehouses" ("WhCode","WhName","IsMrpWarehouse","IsActive","Remarks","WarehouseType","PlantId","CreatedAt","UpdatedAt")
VALUES
  ('E2E-WRAW','E2E 原料倉',true ,true,'E2E 流程測試專用',0,'P1',now(),now()),
  ('E2E-WSTG','E2E 備料倉',true ,true,'E2E 流程測試專用',6,'P1',now(),now()),
  ('E2E-WFG' ,'E2E 成品倉',true ,true,'E2E 流程測試專用',2,'P1',now(),now())
ON CONFLICT ("WhCode") DO UPDATE
  SET "IsActive"=true, "WarehouseType"=EXCLUDED."WarehouseType",
      "PlantId"='P1', "UpdatedAt"=now();

INSERT INTO "StorageLocations" ("WhCode","LocationCode","LocationName","LocationType","Remarks","IsActive")
VALUES
  ('E2E-WRAW','E2E-LRAW','E2E 原料儲位',NULL,'E2E 流程測試專用',true),
  ('E2E-WSTG','E2E-LSTG','E2E 備料儲位',NULL,'E2E 流程測試專用',true),
  ('E2E-WFG' ,'E2E-LFG' ,'E2E 成品儲位',NULL,'E2E 流程測試專用',true)
ON CONFLICT ("WhCode","LocationCode") DO UPDATE
  SET "IsActive"=true;

-- ═══ ⑤ 工作中心（廠別主檔）══════════════════════════════════════════════════
-- 備料倉／儲位直接設在工作中心上：派工備料會優先取這裡，未設定才退到系統參數
-- mfg_default_staging_wh / mfg_default_staging_location。
INSERT INTO "WorkCenters"
  ("WorkCenterCode","WorkCenterName","DepartmentId","ShiftGroupCode","IsProductionLine",
   "AdditionalRate","StandardLaborCost","MachineHourlyRate","IsActive","Remarks",
   "StagingWhCode","StagingLocationCode","PlantId","CreatedAt","UpdatedAt")
VALUES ('E2E-WC01','E2E 工作中心 01',NULL,NULL,false,0,300,100,true,'E2E 流程測試專用',
        'E2E-WSTG','E2E-LSTG','P1',now(),now())
ON CONFLICT ("WorkCenterCode") DO UPDATE
  SET "IsActive"=true, "StagingWhCode"='E2E-WSTG', "StagingLocationCode"='E2E-LSTG',
      "StandardLaborCost"=300, "MachineHourlyRate"=100, "PlantId"='P1', "UpdatedAt"=now();

-- ═══ ⑥ 途程 + 工序 + BOM（廠別）═════════════════════════════════════════════
-- 單一工序（Seq 10）即可涵蓋備料→開工→報工→完工；多工序留給後續流程測試自行擴充。
INSERT INTO "Routings"
  ("RoutingId","ProductCode","RoutingName","BatchSize","BufferDays","AvgLeadTime",
   "IsActive","Version","PlantId","CreatedAt","UpdatedAt")
VALUES ('E2E-RT01','E2E-FG01','E2E 測試途程',1,0,1,true,'1','P1',now(),now())
ON CONFLICT ("RoutingId") DO UPDATE
  SET "IsActive"=true, "ProductCode"='E2E-FG01', "PlantId"='P1', "UpdatedAt"=now();

-- TimeUnit / SetupTimeUnit：2=分鐘、0=分鐘（見 RoutingOperation 預設值）。
-- RequiresInspection / RequiresFpi 一律 false：首件檢查會擋開工，流程測試不驗這段。
INSERT INTO "RoutingOperations"
  ("RoutingId","OperationSeq","OperationName","WorkCenterId","TM_SETUP","TM_UNIT",
   "CD_BOR","QT_BOR","UomCode","IsOutsource","RequiresInspection","RequiresFpi",
   "TimeUnit","SetupTimeUnit","MoveTime","QueueTime","YieldRate")
VALUES ('E2E-RT01',10,'E2E 加工','E2E-WC01',30,2,0,1,'E2E-EA',false,false,false,2,0,0,0,100)
ON CONFLICT ("RoutingId","OperationSeq") DO UPDATE
  SET "WorkCenterId"='E2E-WC01', "TM_SETUP"=30, "TM_UNIT"=2,
      "RequiresInspection"=false, "RequiresFpi"=false;

-- BackflushTiming=0（AtCompletion）：完工時一次沖料。
INSERT INTO "RoutingBoms"
  ("RoutingId","OperationSeq","BomSeq","ProductCode","Quantity","UomCode",
   "ScrapRate","OffcutRate","BackflushTiming","IssueMethod")
VALUES
  ('E2E-RT01',10,1,'E2E-RM01',2,'E2E-EA',0,0,0,0),
  ('E2E-RT01',10,2,'E2E-RM02',1,'E2E-EA',0,0,0,0)
ON CONFLICT ("RoutingId","OperationSeq","BomSeq") DO UPDATE
  SET "Quantity"=EXCLUDED."Quantity", "ScrapRate"=0, "OffcutRate"=0;

-- ═══ ⑦ 庫存餘量（原料倉）════════════════════════════════════════════════════
-- 數量一律 UPDATE 覆寫回基準值：流程測試會揀料移倉、發料扣帳，重跑必須回到起點。
-- QtyReserved 同樣歸零——上一輪殘留的預留會讓可用量不足而擋下備料。
-- ⚠️ LotNo 必須是**空字串**，不能自訂批號。未啟用批號管制（IsLotControlled=false）時，
-- 全系統一律以 '' 代表「無批號」——例如 PostSamePlantTransferAsync 查可用量是寫死
-- GetAvailableQtyAsync(..., lotNo: "")。若這裡塞 'E2E-LOT'，庫存明明有 1000，
-- 揀料移倉仍會以「可用量不足（需 20，可用 0）」被擋下。
-- 先整批刪再重建，而不是只清備料倉/成品倉：測試會把料移到、或建立在事先想不到的儲位
-- （調撥單、樣品單、報廢單都會產生新的庫存列），逐一列舉一定會漏。
-- 範圍限定在 E2E- 品號，不影響其他資料。
DELETE FROM "Inventories" WHERE "ProductCode" LIKE 'E2E-%';

-- E2E-FG01 的期初成品庫存供 O2C 流程測試出貨用（訂單→出貨→揀料→應收→收款）。
-- 單位成本 100 = 該品號的 StandardCost，出貨的銷貨成本因此可預期。
INSERT INTO "Inventories"
  ("PlantId","ProductCode","LocationCode","LotNo","QtyOnHand","QtyReserved","UnitCost","LastUpdatedAt")
VALUES
  ('P1','E2E-RM01','E2E-LRAW','',1000,0, 10,now()),
  ('P1','E2E-RM02','E2E-LRAW','', 500,0, 20,now()),
  ('P1','E2E-FG01','E2E-LFG' ,'', 200,0,100,now())
ON CONFLICT ("PlantId","ProductCode","LocationCode","LotNo") DO UPDATE
  SET "QtyOnHand"=EXCLUDED."QtyOnHand", "QtyReserved"=0,
      "UnitCost"=EXCLUDED."UnitCost", "LastUpdatedAt"=now();

-- 期初異動：帳面庫存必須等於異動淨額（Σ StockQty × Direction），這是關帳前稽核
-- INV_ROLLFORWARD 的規則。只寫 Inventories 不寫異動的話，種子自己就違反它——
-- 乾淨庫（CI）也一樣，E2E 環境裡月結第 10 步因此永遠關不了帳。
-- ⓪ 已刪掉 E2E 品號的全部異動，這裡以固定 UUID 重建期初，之後流程測試的異動接在後面。
-- 異動必須掛在一張真的單據上（DocNo 不在任何單據表＝稽核 ORPHAN_STOCK_MOVEMENT），
-- 期初量本來就是以庫存調整單入帳，故放一張已過帳的調整單 E2E-OPEN01 當來源。
INSERT INTO "StockAdjustments"
  ("StockAdjustmentId","AdjustDate","TypeCode","Status","ApprovalStatus","PlantId","CreatedBy","CreatedByRoleId","Remarks","CreatedAt","UpdatedAt")
VALUES ('E2E-OPEN01',current_date - 200,'E2E-OPEN',3,2,'P1','admin','1','E2E 期初庫存',now(),now())
ON CONFLICT ("StockAdjustmentId") DO UPDATE SET "Status"=3, "ApprovalStatus"=2, "UpdatedAt"=now();
INSERT INTO "StockMovements"
  ("MovementId","TypeCode","TypeCategory","DocNo","DocLineNo","MovementDate","PlantId","ProductCode",
   "LocationCode","LotNo","UomCode","Qty","StockUomCode","StockQty","Direction","UnitCost","TotalCost",
   "CreatedBy","CreatedAt","Remark")
VALUES
  ('e2e0e2e0-0000-4000-8000-000000000001','E2E-OPEN','Opening','E2E-OPEN01',1,current_date - 200,'P1','E2E-RM01','E2E-LRAW','','E2E-EA',1000,'E2E-EA',1000,1, 10, 10000,'admin',now(),'E2E 期初'),
  ('e2e0e2e0-0000-4000-8000-000000000002','E2E-OPEN','Opening','E2E-OPEN01',2,current_date - 200,'P1','E2E-RM02','E2E-LRAW','','E2E-EA', 500,'E2E-EA', 500,1, 20, 10000,'admin',now(),'E2E 期初'),
  ('e2e0e2e0-0000-4000-8000-000000000003','E2E-OPEN','Opening','E2E-OPEN01',3,current_date - 200,'P1','E2E-FG01','E2E-LFG' ,'','E2E-EA', 200,'E2E-EA', 200,1,100, 20000,'admin',now(),'E2E 期初')
ON CONFLICT ("MovementId") DO NOTHING;

-- ═══ ⑦-b 銀行帳戶（付款／收款的資金出入口）════════════════════════════════════
-- 乾淨資料庫一筆銀行帳戶都沒有，付款單／收款單的「銀行帳戶」下拉會是空的。
-- Balance 一律覆寫回同一個基準值：流程測試會確認付款（扣款）與收款（入帳），
-- 重跑必須從同一起點出發。餘額刻意給足——付款確認有「銀行帳戶餘額不足」的守門
-- （FinPaymentService.ConfirmAPPaymentAsync），餘額 0 會讓 P2P 流程卡在最後一步。
INSERT INTO "BankAccounts"
  ("AccountId","BankName","AccountNo","CurrencyCode","Balance","IsActive","Remarks",
   "PlantId","CreatedAt","UpdatedAt")
VALUES ('E2E-BANK','E2E 測試銀行','000-000-000',NULL,1000000,true,'E2E 流程測試專用',
        'P1',now(),now())
ON CONFLICT ("AccountId") DO UPDATE
  SET "IsActive"=true, "Balance"=1000000, "PlantId"='P1', "UpdatedAt"=now();

-- ═══ ⑧ 生產批次（WorkOrders.ProductionBatchId 的 FK 目標）═══════════════════
-- MrpRunId NOT NULL 但無 FK；本批次非 MRP 產出，以 E2E- 字樣標示來源。
INSERT INTO "ProductionBatches"
  ("BatchId","MrpRunId","MrpRunLineSeq","ProductCode","Quantity","Priority",
   "PlannedEndDate","Status","IsPredictedLate","PlantId","CreatedAt","UpdatedAt",
   "CreatedByRoleId","OrganizationUnitPath")
VALUES ('E2E-BATCH01','E2E-MRP00',1,'E2E-FG01',10,1,
        now()+interval '7 day',0,false,'P1',now(),now(),'1',NULL)
ON CONFLICT ("BatchId") DO UPDATE
  SET "Quantity"=10, "Status"=0, "PlantId"='P1',
      "CreatedByRoleId"='1', "UpdatedAt"=now();

-- ═══ ⑨ 工單 + 工序 + 用料 ═══════════════════════════════════════════════════
-- Status=1（Released 已釋出）：工序可排程、可開工。
-- MaterialReadinessStatus=0（NotChecked）：料況確認由流程測試自己跑。
-- 數量與狀態一律覆寫：上一輪跑到 Completed 的工單，下一輪要回到 Released。
INSERT INTO "WorkOrders"
  ("WorkOrderId","ProductionBatchId","ProductCode","RoutingId","Quantity","Status",
   "PlannedStartDate","PlannedEndDate","Type","ApprovalStatus","MaterialReadinessStatus",
   "GrossDemandQty","QtyScrap","IsOutsource",
   "ActualLaborCost","ActualMaterialCost","ActualOverheadCost","MaterialVarianceCost",
   "StandardLaborCost","StandardMaterialCost","StandardOverheadCost",
   "PlantId","CreatedAt","UpdatedAt","CreatedByRoleId","OrganizationUnitPath")
VALUES ('E2E-WO0001','E2E-BATCH01','E2E-FG01','E2E-RT01',10,1,
        now(),now()+interval '7 day',0,0,0,10,0,false,
        0,0,0,0,0,0,0,
        'P1',now(),now(),'1',NULL)
ON CONFLICT ("WorkOrderId") DO UPDATE
  SET "Quantity"=10, "Status"=1, "MaterialReadinessStatus"=0, "QtyScrap"=0,
      "ActualLaborCost"=0, "ActualMaterialCost"=0, "ActualOverheadCost"=0,
      "MaterialVarianceCost"=0,
      "PlannedStartDate"=now(), "PlannedEndDate"=now()+interval '7 day',
      "PlantId"='P1', "CreatedByRoleId"='1', "UpdatedAt"=now();

INSERT INTO "WorkOrderOperations"
  ("WorkOrderId","OperationSeq","OperationName","WorkCenterId","SetupTime","UnitTime",
   "BatchMode","BatchQty","IsOutsource","RequiresInspection","RequiresFpi",
   "TimeUnit","SetupTimeUnit","MoveTime","QueueTime")
VALUES ('E2E-WO0001',10,'E2E 加工','E2E-WC01',30,2,0,1,false,false,false,2,0,0,0)
ON CONFLICT ("WorkOrderId","OperationSeq") DO UPDATE
  SET "WorkCenterId"='E2E-WC01', "SetupTime"=30, "UnitTime"=2,
      "RequiresInspection"=false, "RequiresFpi"=false;

-- SourceMode=1（MrpMode.Purchase）：原料為採購件。
-- IssuedQty/ReturnedQty 歸零——上一輪的發料紀錄不能帶到下一輪。
-- IssueMethod／BackflushTiming 也要覆寫：MaterialFlowTests 的領料申請把 RM01 改成 Pull(1)，
-- 沒還原的話之後的「投產自動產生 Push 發料單」只會開 RM02——是否變紅取決於 xUnit 的執行順序。
INSERT INTO "WorkOrderMaterials"
  ("WorkOrderId","OperationSeq","BomSeq","ProductCode","RequiredQty","UomCode",
   "SourceMode","UnitCost","BackflushTiming","IssueMethod","IssuedQty","ReturnedQty")
VALUES
  ('E2E-WO0001',10,1,'E2E-RM01',20,'E2E-EA',1,10,0,0,0,0),
  ('E2E-WO0001',10,2,'E2E-RM02',10,'E2E-EA',1,20,0,0,0,0)
ON CONFLICT ("WorkOrderId","OperationSeq","BomSeq") DO UPDATE
  SET "RequiredQty"=EXCLUDED."RequiredQty", "IssuedQty"=0, "ReturnedQty"=0,
      "UnitCost"=EXCLUDED."UnitCost",
      "IssueMethod"=EXCLUDED."IssueMethod", "BackflushTiming"=EXCLUDED."BackflushTiming";

-- ═══ ⑩ 派工 + 派工用料 ══════════════════════════════════════════════════════
-- Status=1（Scheduled 已排定，等待開工）：備料與開工的起點。
-- QtyDone 歸零、ToolingCode 留空（模具衝突偵測不在流程測試範圍）。
INSERT INTO "Dispatches"
  ("DispatchId","WorkOrderId","OperationSeq","Quantity","QtyDone","Status",
   "IsOutsource","MaterialRisk","ScheduledStart","ScheduledEnd",
   "PlannedStartDate","PlannedEndDate",
   "PlantId","CreatedAt","UpdatedAt","CreatedByRoleId","OrganizationUnitPath")
VALUES ('E2E-DP0001','E2E-WO0001',10,10,0,1,
        false,false,now(),now()+interval '1 day',
        now(),now()+interval '1 day',
        'P1',now(),now(),'1',NULL)
ON CONFLICT ("DispatchId") DO UPDATE
  SET "Quantity"=10, "QtyDone"=0, "Status"=1, "MaterialRisk"=false,
      "ScheduledStart"=now(), "ScheduledEnd"=now()+interval '1 day',
      "PlannedStartDate"=now(), "PlannedEndDate"=now()+interval '1 day',
      "OverheadChargedThroughUtc"=NULL,
      "PlantId"='P1', "CreatedByRoleId"='1', "UpdatedAt"=now();

INSERT INTO "DispatchMaterials"
  ("DispatchId","BomSeq","ProductCode","RequiredQty","IssuedQty","UomCode")
VALUES
  ('E2E-DP0001',1,'E2E-RM01',20,0,'E2E-EA'),
  ('E2E-DP0001',2,'E2E-RM02',10,0,'E2E-EA')
ON CONFLICT ("DispatchId","BomSeq") DO UPDATE
  SET "RequiredQty"=EXCLUDED."RequiredQty", "IssuedQty"=0;

-- ═══ ⑪ 會計期間 ═════════════════════════════════════════════════════════════
-- 正式部署已由 AccountingPeriodSeeder 在期間表為空時建滿一年（Program.cs）。
-- 這裡仍補一次，涵蓋兩種該 seeder 不會處理的情況：
--   (a) 部署以 "Security:SeedAccountingPeriods": false 關閉了自動建立；
--   (b) 測試庫已有期間但不含上個月／未來月份（那個 seeder 只在完全空表時動作）。
-- 重疊的期間由 ON CONFLICT DO NOTHING 略過，不覆蓋既有期間的關帳狀態。
-- Status=0（Open 可過帳）。
-- ⚠️ 期間自 2026-08 起是**廠別主檔**：主鍵由 "PeriodId" 改為 ("PeriodId","PlantId")
--    （ACT_AccountingPeriodPerPlant，甲廠關帳不該把乙廠一起關掉）。
--    本段原本沒插 PlantId、且 ON CONFLICT 只帶 "PeriodId"，於是 PostgreSQL 丟
--    「there is no unique or exclusion constraint matching the ON CONFLICT specification」，
--    psql 以 exit 3 中止整支種子，Playwright 連一個測試都還沒跑就整個 job 紅。
--    ⚠️ 這個紅**只在 nightly 出現**：PR/push 的 CI 不跑 E2E，所以改主鍵那次全綠合併，
--    連紅四個晚上（08-29 起）才被信件通知揪出來。
INSERT INTO "AccountingPeriods"
  ("PeriodId","Year","Month","StartDate","EndDate","Status","CreatedAt","UpdatedAt","Remarks","PlantId")
SELECT to_char(d,'YYYYMM'),
       EXTRACT(YEAR FROM d)::int, EXTRACT(MONTH FROM d)::int,
       d::date, (d + interval '1 month' - interval '1 day')::date,
       0, now(), now(), 'E2E 種子建立', 'P1'
FROM generate_series(date_trunc('month', now() - interval '1 month'),
                     date_trunc('month', now() + interval '2 month'),
                     interval '1 month') AS d
ON CONFLICT ("PeriodId","PlantId") DO NOTHING;

-- ═══ ⑫ 第二廠別（跨廠調撥單的目的廠）════════════════════════════════════════════
-- CrossPlantTransfer 的 ToPlantId 下拉需要「非本廠(P1)」的可選項；乾淨庫僅 Program.cs 種的 P1。
-- 僅作為調撥目的廠代碼使用（草稿存檔不需該廠有倉庫/儲位），對其他流程測試無副作用。
INSERT INTO "Plants" ("Id","Name","NameEn","DefaultLanguage","DefaultTimeZone")
VALUES ('E2E-P2','E2E 第二廠','E2E Plant 2','zh-TW','Asia/Taipei')
ON CONFLICT ("Id") DO NOTHING;

-- ═══ ⑬ 委外採購單（委外收貨單的來源單）══════════════════════════════════════════
-- OutsourceReceipt 的 Create 下拉只列「本廠 + Status=Confirmed(1)」的委外採購單，且 Save 會驗其存在。
-- 附一筆明細（收回品 E2E-FG01、送外料由 BOM 衍生），讓收貨畫面可帶出明細。
INSERT INTO "OutsourcePurchaseOrders"
  ("OutsourcePurchaseOrderId","SupplierCode","OrderDate","ExpectedDeliveryDate","Status",
   "Remarks","PlantId","CreatedAt","UpdatedAt","CreatedByRoleId")
VALUES ('E2E-OPO01','E2E-CO01',current_date,current_date + 7,1,
        'E2E 流程測試專用','P1',now(),now(),'1')
ON CONFLICT ("OutsourcePurchaseOrderId") DO UPDATE
  SET "Status"=1, "SupplierCode"='E2E-CO01', "PlantId"='P1', "UpdatedAt"=now();

INSERT INTO "OutsourcePurchaseOrderLines"
  ("OutsourcePurchaseOrderId","LineNo","SourceWorkOrderId","OperationSeq","ProductCode",
   "Qty","SplitQty","UomCode","UnitPrice","CurrencyCode")
VALUES ('E2E-OPO01',1,'E2E-WO0001',10,'E2E-FG01',10,10,'E2E-EA',5,'E2E-CUR')
ON CONFLICT ("OutsourcePurchaseOrderId","LineNo") DO UPDATE
  SET "Qty"=10, "SplitQty"=10, "UnitPrice"=5;

-- ═══ ⑮ 整品委外工單＋委外採購單（委外收回 → IQC → 入庫流程）══════════════════════
-- OutsourceIqcFlowTests 專用（mfg-domain.md §19.3a）：委外收回品要先品檢，合格進一般倉的那一刻
-- 才回寫委外派工完工量；整品委外工單收滿直接「入庫完成」。
--   • E2E-OS01：**非免驗**（IqcExempt=false），否則上架一般倉直放、根本不經 IQC。
--     獨立品號而不用 E2E-FG01：FG01 是其他流程的成品，庫存與 IQC 紀錄會互相污染。
--   • E2E-OSCAT 帶預設倉儲 E2E-WFG/E2E-LFG：桌機委外收貨明細沒有目的倉欄位，
--     IQC 合格放行時退回品號類別預設倉儲；兩者都沒有就留在待驗倉、也不會回寫完工量。
--   • 派工 Status=0（Pending）、QtyDone=0；工單 Status=1（Released）、IsOutsource=true。
INSERT INTO "ProductCategories"
  ("CategoryCode","CategoryName","CostMethod","EnableLotTracking","IsActive",
   "DefaultStockUomCode","DefaultWarehouseCode","DefaultLocationCode","Remarks","CreatedAt","UpdatedAt")
VALUES ('E2E-OSCAT','E2E 委外成品分類',0,false,true,'E2E-EA','E2E-WFG','E2E-LFG','E2E 流程測試專用',now(),now())
ON CONFLICT ("CategoryCode") DO UPDATE
  SET "IsActive"=true, "DefaultWarehouseCode"='E2E-WFG', "DefaultLocationCode"='E2E-LFG', "UpdatedAt"=now();

INSERT INTO "Products"
  ("ProductCode","ProductName","Spec","RoutingId","SalesUom","PurchaseUom","StockUom",
   "StockMode","SafetyStock","StandardCost","MrpMode","LeadTimeDays","UrgentLeadTimeDays",
   "OutsourceQtyPerDay","IsLotControlled","IqcExempt","IsActive","ApprovalStatus","CategoryCode",
   "CreatedAt","UpdatedAt")
VALUES ('E2E-OS01','E2E 委外成品 OS01','E2E 委外 IQC 流程測試專用（須品檢）',NULL,'E2E-EA','E2E-EA','E2E-EA',
        'M',0,100,'M',1,1,0,false,false,true,0,'E2E-OSCAT',now(),now())
ON CONFLICT ("ProductCode") DO UPDATE
  SET "IsActive"=true, "ApprovalStatus"=0, "StockUom"='E2E-EA', "IqcExempt"=false,
      "CategoryCode"='E2E-OSCAT', "UpdatedAt"=now();

INSERT INTO "WorkOrders"
  ("WorkOrderId","ProductionBatchId","ProductCode","RoutingId","Quantity","Status",
   "PlannedStartDate","PlannedEndDate","Type","ApprovalStatus","MaterialReadinessStatus",
   "GrossDemandQty","QtyScrap","IsOutsource",
   "ActualLaborCost","ActualMaterialCost","ActualOverheadCost","MaterialVarianceCost",
   "StandardLaborCost","StandardMaterialCost","StandardOverheadCost",
   "PlantId","CreatedAt","UpdatedAt","CreatedByRoleId","OrganizationUnitPath")
VALUES ('E2E-WO0011','E2E-BATCH01','E2E-OS01','',10,1,
        now(),now()+interval '7 day',0,0,0,10,0,true,
        0,0,0,0,0,0,0,
        'P1',now(),now(),'1',NULL)
ON CONFLICT ("WorkOrderId") DO UPDATE
  SET "Quantity"=10, "Status"=1, "IsOutsource"=true, "QtyScrap"=0,
      "PlantId"='P1', "CreatedByRoleId"='1', "UpdatedAt"=now();

INSERT INTO "WorkOrderOperations"
  ("WorkOrderId","OperationSeq","OperationName","WorkCenterId","SetupTime","UnitTime",
   "BatchMode","BatchQty","IsOutsource","RequiresInspection","RequiresFpi",
   "TimeUnit","SetupTimeUnit","MoveTime","QueueTime")
VALUES ('E2E-WO0011',10,'E2E 整品委外','',0,0,0,1,true,false,false,2,0,0,0)
ON CONFLICT ("WorkOrderId","OperationSeq") DO UPDATE SET "IsOutsource"=true;

INSERT INTO "Dispatches"
  ("DispatchId","WorkOrderId","OperationSeq","Quantity","QtyDone","Status",
   "IsOutsource","MaterialRisk","ScheduledStart","ScheduledEnd",
   "PlannedStartDate","PlannedEndDate",
   "PlantId","CreatedAt","UpdatedAt","CreatedByRoleId","OrganizationUnitPath")
VALUES ('E2E-DP0011','E2E-WO0011',10,10,0,0,
        true,false,now(),now()+interval '7 day',
        now(),now()+interval '7 day',
        'P1',now(),now(),'1',NULL)
ON CONFLICT ("DispatchId") DO UPDATE
  SET "Quantity"=10, "QtyDone"=0, "Status"=0, "IsOutsource"=true,
      "PlantId"='P1', "CreatedByRoleId"='1', "UpdatedAt"=now();

INSERT INTO "OutsourcePurchaseOrders"
  ("OutsourcePurchaseOrderId","SupplierCode","OrderDate","ExpectedDeliveryDate","Status",
   "Remarks","PlantId","CreatedAt","UpdatedAt","CreatedByRoleId")
VALUES ('E2E-OPO02','E2E-CO01',current_date,current_date + 7,1,
        'E2E 委外 IQC 流程測試專用','P1',now(),now(),'1')
ON CONFLICT ("OutsourcePurchaseOrderId") DO UPDATE
  SET "Status"=1, "SupplierCode"='E2E-CO01', "PlantId"='P1', "UpdatedAt"=now();

INSERT INTO "OutsourcePurchaseOrderLines"
  ("OutsourcePurchaseOrderId","LineNo","SourceWorkOrderId","OperationSeq","ProductCode",
   "Qty","SplitQty","UomCode","UnitPrice","CurrencyCode")
VALUES ('E2E-OPO02',1,'E2E-WO0011',10,'E2E-OS01',10,10,'E2E-EA',5,'E2E-CUR')
ON CONFLICT ("OutsourcePurchaseOrderId","LineNo") DO UPDATE
  SET "SourceWorkOrderId"='E2E-WO0011', "OperationSeq"=10, "ProductCode"='E2E-OS01',
      "Qty"=10, "SplitQty"=10, "UnitPrice"=5;

-- ═══ ⑯ 派工例外操作與工單撤回（DispatchExceptionUiTests）══════════════════════════
-- 暫停處理（恢復／換機續做／剩餘量結案）、排程側板（拆分／轉委外）、工單撤回送審。
-- 獨立工作中心 E2E-WC02 掛三台機台：E2E-WC01 是備料→完工流程用的，掛了機台會讓開工面板多出
-- 機台選項、改變那條流程的行為。MachineStatus：0=正常、3=故障停機。
-- 派工狀態：0=待排、1=已排定、4=生產中、6=暫停；暫停原因：1=機台停用、3=巡檢不合格。
INSERT INTO "WorkCenters"
  ("WorkCenterCode","WorkCenterName","DepartmentId","ShiftGroupCode","IsProductionLine",
   "AdditionalRate","StandardLaborCost","MachineHourlyRate","IsActive","Remarks",
   "StagingWhCode","StagingLocationCode","PlantId","CreatedAt","UpdatedAt")
VALUES ('E2E-WC02','E2E 工作中心 02（派工例外）',NULL,NULL,false,0,300,100,true,'E2E 流程測試專用',
        'E2E-WSTG','E2E-LSTG','P1',now(),now())
ON CONFLICT ("WorkCenterCode") DO UPDATE SET "IsActive"=true, "PlantId"='P1', "UpdatedAt"=now();

INSERT INTO "WorkCenterMachines" ("WorkCenterCode","MachineCode","MachineName","MachineStatus","Remarks")
VALUES ('E2E-WC02','E2E-MC01','E2E 機台 01',0,'E2E 流程測試專用'),
       ('E2E-WC02','E2E-MC02','E2E 機台 02',0,'E2E 流程測試專用'),
       ('E2E-WC02','E2E-MC03','E2E 機台 03（故障）',3,'E2E 流程測試專用')
ON CONFLICT ("WorkCenterCode","MachineCode") DO UPDATE
  SET "MachineStatus"=EXCLUDED."MachineStatus";

INSERT INTO "WorkOrders"
  ("WorkOrderId","ProductionBatchId","ProductCode","RoutingId","Quantity","Status",
   "PlannedStartDate","PlannedEndDate","Type","ApprovalStatus","MaterialReadinessStatus",
   "GrossDemandQty","QtyScrap","IsOutsource",
   "ActualLaborCost","ActualMaterialCost","ActualOverheadCost","MaterialVarianceCost",
   "StandardLaborCost","StandardMaterialCost","StandardOverheadCost",
   "PlantId","CreatedAt","UpdatedAt","CreatedByRoleId","OrganizationUnitPath")
VALUES
  -- 派工例外的母工單：已釋出（1）。不可用生產中（2）——月結關帳前稽核要求每張生產中工單都有
  -- 本期 WIP 快照（WIP_SNAPSHOT_MISSING），PeriodCloseUiTests 會因這張工單關不了帳。
  ('E2E-WO0021','E2E-BATCH01','E2E-FG01','E2E-RT01',50,1,
   now(),now()+interval '7 day',0,2,0,50,0,false, 0,0,0,0,0,0,0, 'P1',now(),now(),'1',NULL),
  -- 撤回送審用：已規劃、待審核中
  ('E2E-WO0022','E2E-BATCH01','E2E-FG01','E2E-RT01',5,0,
   now(),now()+interval '7 day',0,1,0,5,0,false, 0,0,0,0,0,0,0, 'P1',now(),now(),'1',NULL)
ON CONFLICT ("WorkOrderId") DO UPDATE
  SET "Quantity"=EXCLUDED."Quantity", "Status"=EXCLUDED."Status", "ApprovalStatus"=EXCLUDED."ApprovalStatus",
      "ApprovalRequestId"=NULL, "PlantId"='P1', "CreatedByRoleId"='1', "UpdatedAt"=now();

INSERT INTO "WorkOrderOperations"
  ("WorkOrderId","OperationSeq","OperationName","WorkCenterId","SetupTime","UnitTime",
   "BatchMode","BatchQty","IsOutsource","RequiresInspection","RequiresFpi",
   "TimeUnit","SetupTimeUnit","MoveTime","QueueTime")
VALUES ('E2E-WO0021',10,'E2E 派工例外','E2E-WC02',0,1,0,1,false,false,false,2,0,0,0),
       ('E2E-WO0022',10,'E2E 撤回送審','E2E-WC02',0,1,0,1,false,false,false,2,0,0,0)
ON CONFLICT ("WorkOrderId","OperationSeq") DO UPDATE
  SET "WorkCenterId"='E2E-WC02', "IsOutsource"=false, "RequiresInspection"=false, "RequiresFpi"=false;

--   DP0021 巡檢不合格暫停（生產中做了 2）→ 恢復回生產中
--   DP0022 機台 MC03 故障暫停（做了 4）  → 不可恢復，換機到 MC02：原單以 4 結案、續做 6
--   DP0023 巡檢不合格暫停（沒產出）     → 剩餘量結案＝取消
--   DP0024 已排定 10（MC01）            → 排程側板拆 6／4
--   DP0025 已排定 10（MC02）            → 排程側板轉委外
--   （這兩張要有機台與時段：側板只能從甘特色塊點開，而甘特只畫排定時段落在視窗內的派工）
INSERT INTO "Dispatches"
  ("DispatchId","WorkOrderId","OperationSeq","Quantity","QtyDone","Status",
   "IsOutsource","MaterialRisk","MachineCode","IsMachineLocked","SplitGroupId",
   "HoldReason","HoldFromStatus","HoldAt","ScheduledStart","ScheduledEnd",
   "PlantId","CreatedAt","UpdatedAt","CreatedByRoleId","OrganizationUnitPath")
VALUES
  ('E2E-DP0021','E2E-WO0021',10,10,2,6, false,false,'E2E-MC01',false,NULL, 3,4,now(),NULL,NULL, 'P1',now(),now(),'1',NULL),
  ('E2E-DP0022','E2E-WO0021',10,10,4,6, false,false,'E2E-MC03',false,NULL, 1,4,now(),NULL,NULL, 'P1',now(),now(),'1',NULL),
  ('E2E-DP0023','E2E-WO0021',10,10,0,6, false,false,'E2E-MC01',false,NULL, 3,3,now(),NULL,NULL, 'P1',now(),now(),'1',NULL),
  ('E2E-DP0024','E2E-WO0021',10,10,0,1, false,false,'E2E-MC01',false,NULL, NULL,NULL,NULL,now()+interval '1 hour',now()+interval '3 hour', 'P1',now(),now(),'1',NULL),
  ('E2E-DP0025','E2E-WO0021',10,10,0,1, false,false,'E2E-MC02',false,NULL, NULL,NULL,NULL,now()+interval '1 hour',now()+interval '3 hour', 'P1',now(),now(),'1',NULL)
ON CONFLICT ("DispatchId") DO UPDATE
  SET "Quantity"=EXCLUDED."Quantity", "QtyDone"=EXCLUDED."QtyDone", "Status"=EXCLUDED."Status",
      "IsOutsource"=false, "MachineCode"=EXCLUDED."MachineCode", "IsMachineLocked"=false, "SplitGroupId"=NULL,
      "HoldReason"=EXCLUDED."HoldReason", "HoldFromStatus"=EXCLUDED."HoldFromStatus", "HoldAt"=EXCLUDED."HoldAt",
      "ScheduledStart"=EXCLUDED."ScheduledStart", "ScheduledEnd"=EXCLUDED."ScheduledEnd",
      "PlantId"='P1', "CreatedByRoleId"='1', "UpdatedAt"=now();

-- ═══ ⑰ 委外簡化參數開啟時的桌機流程（OutsourceSimplifiedFlowTests）══════════════════
-- 參數本身由測試在「廠別」畫面開關（快取 10 分鐘，直接改資料庫 app 看不到），這裡只備資料：
--   • E2E-RM03：送外料，分類 E2E-RMCAT 帶預設倉儲 E2E-WRAW/E2E-LRAW——自動扣料從「品號類別預設倉儲」出庫，
--     取不到會擋下確認。期初 100，掛在期初調整單 E2E-OPEN01 第 5 行（INV_ROLLFORWARD 帳面＝異動淨額）。
--     ⚠️ 期初異動的 MovementId／行號不可重複：寫入是 ON CONFLICT DO NOTHING，撞號的那筆會被**靜默略過**，
--     變成有帳面沒異動，月結關帳前稽核 INV_ROLLFORWARD 擋關帳。已用：…0001～…0004（④ RM01/RM02/FG01、NRV01）。
--   • B1：整品委外工單 E2E-WO0011（⑮）補用料 RM03 每件 2（共 20）。參數關閉時不影響 ⑮ 的流程。
--   • B3：整品委外工單 E2E-WO0012 已規劃、GrossDemandQty=0（不做投產重算），委外派工 E2E-DP0012（5）；
--     E2E-OS01 只有一家有效報價 E2E-OQ01（E2E-CO01，單價 7）。
--   • C ：廠內工單 E2E-WO0031（成品 FG01、工序 10 在 E2E-WC01，用料 RM03 每件 1）＋委外採購單 E2E-OPO03 指向它
--     ＝工序委外：收回不入庫、確認即過帳、扣該工序投料、加工費計入工單委外成本。
INSERT INTO "ProductCategories"
  ("CategoryCode","CategoryName","CostMethod","EnableLotTracking","IsActive",
   "DefaultStockUomCode","DefaultWarehouseCode","DefaultLocationCode","Remarks","CreatedAt","UpdatedAt")
VALUES ('E2E-RMCAT','E2E 送外料分類',0,false,true,'E2E-EA','E2E-WRAW','E2E-LRAW','E2E 流程測試專用',now(),now())
ON CONFLICT ("CategoryCode") DO UPDATE
  SET "IsActive"=true, "DefaultWarehouseCode"='E2E-WRAW', "DefaultLocationCode"='E2E-LRAW', "UpdatedAt"=now();

INSERT INTO "Products"
  ("ProductCode","ProductName","Spec","RoutingId","SalesUom","PurchaseUom","StockUom",
   "StockMode","SafetyStock","StandardCost","MrpMode","LeadTimeDays","UrgentLeadTimeDays",
   "OutsourceQtyPerDay","IsLotControlled","IqcExempt","IsActive","ApprovalStatus","CategoryCode",
   "CreatedAt","UpdatedAt")
VALUES ('E2E-RM03','E2E 送外料 RM03','E2E 委外簡化流程測試專用',NULL,'E2E-EA','E2E-EA','E2E-EA',
        'P',0,10,'P',1,1,0,false,true,true,0,'E2E-RMCAT',now(),now())
ON CONFLICT ("ProductCode") DO UPDATE
  SET "IsActive"=true, "ApprovalStatus"=0, "StockUom"='E2E-EA', "CategoryCode"='E2E-RMCAT', "UpdatedAt"=now();

INSERT INTO "Inventories"
  ("PlantId","ProductCode","LocationCode","LotNo","QtyOnHand","QtyReserved","UnitCost","LastUpdatedAt")
VALUES ('P1','E2E-RM03','E2E-LRAW','',100,0,10,now())
ON CONFLICT ("PlantId","ProductCode","LocationCode","LotNo") DO UPDATE
  SET "QtyOnHand"=100, "QtyReserved"=0, "UnitCost"=10, "LastUpdatedAt"=now();
INSERT INTO "StockMovements"
  ("MovementId","TypeCode","TypeCategory","DocNo","DocLineNo","MovementDate","PlantId","ProductCode",
   "LocationCode","LotNo","UomCode","Qty","StockUomCode","StockQty","Direction","UnitCost","TotalCost",
   "CreatedBy","CreatedAt","Remark")
VALUES ('e2e0e2e0-0000-4000-8000-000000000005','E2E-OPEN','Opening','E2E-OPEN01',5,current_date - 200,'P1','E2E-RM03','E2E-LRAW','','E2E-EA',100,'E2E-EA',100,1,10,1000,'admin',now(),'E2E 期初')
ON CONFLICT ("MovementId") DO NOTHING;

INSERT INTO "WorkOrders"
  ("WorkOrderId","ProductionBatchId","ProductCode","RoutingId","Quantity","Status",
   "PlannedStartDate","PlannedEndDate","Type","ApprovalStatus","MaterialReadinessStatus",
   "GrossDemandQty","QtyScrap","IsOutsource",
   "ActualLaborCost","ActualMaterialCost","ActualOverheadCost","MaterialVarianceCost",
   "StandardLaborCost","StandardMaterialCost","StandardOverheadCost",
   "PlantId","CreatedAt","UpdatedAt","CreatedByRoleId","OrganizationUnitPath")
VALUES
  ('E2E-WO0012','E2E-BATCH01','E2E-OS01','',5,0,
   now(),now()+interval '7 day',0,0,0,0,0,true, 0,0,0,0,0,0,0, 'P1',now(),now(),'1',NULL),
  ('E2E-WO0031','E2E-BATCH01','E2E-FG01','E2E-RT01',10,1,
   now(),now()+interval '7 day',0,2,0,10,0,false, 0,0,0,0,0,0,0, 'P1',now(),now(),'1',NULL)
ON CONFLICT ("WorkOrderId") DO UPDATE
  SET "Quantity"=EXCLUDED."Quantity", "Status"=EXCLUDED."Status", "ApprovalStatus"=EXCLUDED."ApprovalStatus",
      "GrossDemandQty"=EXCLUDED."GrossDemandQty", "IsOutsource"=EXCLUDED."IsOutsource",
      "ApprovalRequestId"=NULL, "OutsourcePurchaseOrderId"=NULL, "OutsourceSupplierCode"=NULL,
      "MaterialReadinessStatus"=0, "PlantId"='P1', "CreatedByRoleId"='1', "UpdatedAt"=now();
-- 委外成本累計欄（工序委外收回會累加）一律歸零；⑮ 的 E2E-WO0011 一併
UPDATE "WorkOrders" SET "ActualOutsourceCost"=0 WHERE "WorkOrderId" IN ('E2E-WO0011','E2E-WO0012','E2E-WO0031');

INSERT INTO "WorkOrderOperations"
  ("WorkOrderId","OperationSeq","OperationName","WorkCenterId","SetupTime","UnitTime",
   "BatchMode","BatchQty","IsOutsource","RequiresInspection","RequiresFpi",
   "TimeUnit","SetupTimeUnit","MoveTime","QueueTime")
VALUES ('E2E-WO0012',10,'E2E 整品委外（自動建單）','',0,0,0,1,true,false,false,2,0,0,0),
       ('E2E-WO0031',10,'E2E 工序委外','E2E-WC01',0,1,0,1,false,false,false,2,0,0,0)
ON CONFLICT ("WorkOrderId","OperationSeq") DO UPDATE SET "IsOutsource"=EXCLUDED."IsOutsource";

INSERT INTO "WorkOrderMaterials"
  ("WorkOrderId","OperationSeq","BomSeq","ProductCode","RequiredQty","UomCode",
   "SourceMode","UnitCost","BackflushTiming","IssueMethod","IssuedQty","ReturnedQty")
VALUES ('E2E-WO0011',10,1,'E2E-RM03',20,'E2E-EA',1,10,0,0,0,0),
       ('E2E-WO0031',10,1,'E2E-RM03',10,'E2E-EA',1,10,0,0,0,0)
ON CONFLICT ("WorkOrderId","OperationSeq","BomSeq") DO UPDATE
  SET "RequiredQty"=EXCLUDED."RequiredQty", "IssuedQty"=0, "ReturnedQty"=0, "UnitCost"=EXCLUDED."UnitCost",
      "IssueMethod"=EXCLUDED."IssueMethod", "BackflushTiming"=EXCLUDED."BackflushTiming";

INSERT INTO "Dispatches"
  ("DispatchId","WorkOrderId","OperationSeq","Quantity","QtyDone","Status",
   "IsOutsource","MaterialRisk","PlantId","CreatedAt","UpdatedAt","CreatedByRoleId","OrganizationUnitPath")
VALUES ('E2E-DP0012','E2E-WO0012',10,5,0,0, true,false,'P1',now(),now(),'1',NULL)
ON CONFLICT ("DispatchId") DO UPDATE
  SET "Quantity"=5, "QtyDone"=0, "Status"=0, "IsOutsource"=true, "PlantId"='P1', "UpdatedAt"=now();

INSERT INTO "OutsourceQuotations"
  ("QuotationId","SupplierCode","ProductCode","RoutingId","OperationSeq","UnitPrice","CurrencyCode","UomCode",
   "LeadDays","EffectiveFrom","EffectiveTo","Remarks","IsActive","CreatedAt","UpdatedAt")
VALUES ('E2E-OQ01','E2E-CO01','E2E-OS01',NULL,NULL,7,'E2E-CUR','E2E-EA',3,current_date - 30,NULL,'E2E 流程測試專用',true,now(),now())
ON CONFLICT ("QuotationId") DO UPDATE
  SET "IsActive"=true, "UnitPrice"=7, "EffectiveFrom"=current_date - 30, "EffectiveTo"=NULL, "UpdatedAt"=now();

INSERT INTO "OutsourcePurchaseOrders"
  ("OutsourcePurchaseOrderId","SupplierCode","OrderDate","ExpectedDeliveryDate","Status",
   "Remarks","PlantId","CreatedAt","UpdatedAt","CreatedByRoleId")
VALUES ('E2E-OPO03','E2E-CO01',current_date,current_date + 7,1,'E2E 工序委外流程測試專用','P1',now(),now(),'1')
ON CONFLICT ("OutsourcePurchaseOrderId") DO UPDATE
  SET "Status"=1, "SupplierCode"='E2E-CO01', "PlantId"='P1', "UpdatedAt"=now();
INSERT INTO "OutsourcePurchaseOrderLines"
  ("OutsourcePurchaseOrderId","LineNo","SourceWorkOrderId","OperationSeq","ProductCode",
   "Qty","SplitQty","UomCode","UnitPrice","CurrencyCode")
VALUES ('E2E-OPO03',1,'E2E-WO0031',10,'E2E-FG01',10,10,'E2E-EA',5,'E2E-CUR')
ON CONFLICT ("OutsourcePurchaseOrderId","LineNo") DO UPDATE
  SET "SourceWorkOrderId"='E2E-WO0031', "OperationSeq"=10, "ProductCode"='E2E-FG01', "Qty"=10, "SplitQty"=10, "UnitPrice"=5;

-- ═══ ⑱ 生產批次整批投產／MRP 差異決策（MfgBatchAndVarianceUiTests）══════════════════
-- E2E-BATCH02 已規劃，底下兩張已規劃工單 WO0041／WO0042（GrossDemandQty=0 不做投產重算、沒有用料＝不會缺料），
-- 整批投產後兩張都要變已釋出。不用 E2E-BATCH01：其他流程的工單都掛在那裡，整批投產會把它們一起推走。
-- 請購單 E2E-PRV01 三行＋MRP 差異紀錄（MrpRunId=E2E-MRPV）：
--   VAR01 Removed(4)   → 可「結案」：差異結清、請購第 1 行 IsClosed
--   VAR02 Increased(1) → 不可結案（數量變動不能用結案處理）：結案時略過、維持未結
--   VAR03 Decreased(2) → 「已處理」只標記差異結清，不動請購單
INSERT INTO "ProductionBatches"
  ("BatchId","MrpRunId","MrpRunLineSeq","ProductCode","Quantity","Priority",
   "PlannedEndDate","Status","IsPredictedLate","PlantId","CreatedAt","UpdatedAt",
   "CreatedByRoleId","OrganizationUnitPath")
VALUES ('E2E-BATCH02','E2E-MRP00',2,'E2E-FG01',4,1,
        now()+interval '7 day',0,false,'P1',now(),now(),'1',NULL)
ON CONFLICT ("BatchId") DO UPDATE
  SET "Quantity"=4, "Priority"=1, "Status"=0, "PlantId"='P1', "CreatedByRoleId"='1', "UpdatedAt"=now();

INSERT INTO "WorkOrders"
  ("WorkOrderId","ProductionBatchId","ProductCode","RoutingId","Quantity","Status",
   "PlannedStartDate","PlannedEndDate","Type","ApprovalStatus","MaterialReadinessStatus",
   "GrossDemandQty","QtyScrap","IsOutsource",
   "ActualLaborCost","ActualMaterialCost","ActualOverheadCost","MaterialVarianceCost",
   "StandardLaborCost","StandardMaterialCost","StandardOverheadCost",
   "PlantId","CreatedAt","UpdatedAt","CreatedByRoleId","OrganizationUnitPath")
VALUES
  ('E2E-WO0041','E2E-BATCH02','E2E-FG01','E2E-RT01',2,0, now(),now()+interval '7 day',0,0,0,0,0,false, 0,0,0,0,0,0,0, 'P1',now(),now(),'1',NULL),
  ('E2E-WO0042','E2E-BATCH02','E2E-FG01','E2E-RT01',2,0, now(),now()+interval '7 day',0,0,0,0,0,false, 0,0,0,0,0,0,0, 'P1',now(),now(),'1',NULL)
ON CONFLICT ("WorkOrderId") DO UPDATE
  SET "Quantity"=2, "Status"=0, "ApprovalStatus"=0, "ApprovalRequestId"=NULL, "GrossDemandQty"=0,
      "MaterialReadinessStatus"=0, "PlantId"='P1', "CreatedByRoleId"='1', "UpdatedAt"=now();
INSERT INTO "WorkOrderOperations"
  ("WorkOrderId","OperationSeq","OperationName","WorkCenterId","SetupTime","UnitTime",
   "BatchMode","BatchQty","IsOutsource","RequiresInspection","RequiresFpi",
   "TimeUnit","SetupTimeUnit","MoveTime","QueueTime")
VALUES ('E2E-WO0041',10,'E2E 整批投產','E2E-WC01',0,1,0,1,false,false,false,2,0,0,0),
       ('E2E-WO0042',10,'E2E 整批投產','E2E-WC01',0,1,0,1,false,false,false,2,0,0,0)
ON CONFLICT ("WorkOrderId","OperationSeq") DO NOTHING;

INSERT INTO "PurchaseRequests"
  ("PurchaseRequestId","RequestDate","RequestedBy","Remarks","CreatedAt","UpdatedAt","PlantId",
   "ApprovalStatus","CreatedByRoleId","SourceMrpRunId")
VALUES ('E2E-PRV01',current_date,'admin','E2E MRP 差異決策專用',now(),now(),'P1',2,'1','E2E-MRPV')
ON CONFLICT ("PurchaseRequestId") DO UPDATE SET "ApprovalStatus"=2, "PlantId"='P1', "UpdatedAt"=now();
INSERT INTO "PurchaseRequestLines"
  ("PurchaseRequestId","LineNo","ProductCode","DeliveryDate","RequestQty","UomCode","StockQty","StockUomCode",
   "IsClosed","OrderedQty","SourceMrpRunId")
VALUES ('E2E-PRV01',1,'E2E-RM01',current_date + 7,10,'E2E-EA',10,'E2E-EA',false,0,'E2E-MRPV'),
       ('E2E-PRV01',2,'E2E-RM02',current_date + 7,10,'E2E-EA',10,'E2E-EA',false,0,'E2E-MRPV'),
       ('E2E-PRV01',3,'E2E-RM01',current_date + 14,5,'E2E-EA',5,'E2E-EA',false,0,'E2E-MRPV')
ON CONFLICT ("PurchaseRequestId","LineNo") DO UPDATE SET "IsClosed"=false, "OrderedQty"=0;

INSERT INTO "MrpPurchaseVarianceRecords"
  ("VarianceId","MrpRunId","ProductCode","PurchaseRequestId","LineNo","Kind","Disposition","Status",
   "PreviousQty","CurrentQty","Description","DetectedAt","PlantId","CreatedByRoleId")
VALUES ('E2E-VAR01','E2E-MRPV','E2E-RM01','E2E-PRV01',1,4,2,0,10,0,'E2E 已無需求',now(),'P1','1'),
       ('E2E-VAR02','E2E-MRPV','E2E-RM02','E2E-PRV01',2,1,3,0,10,15,'E2E 需求增加',now(),'P1','1'),
       ('E2E-VAR03','E2E-MRPV','E2E-RM01','E2E-PRV01',3,2,3,0,5,3,'E2E 需求減少',now(),'P1','1')
ON CONFLICT ("VarianceId") DO UPDATE
  SET "Status"=0, "ResolvedBy"=NULL, "ResolvedAt"=NULL, "ResolutionNote"=NULL, "PlantId"='P1';

-- ═══ ⑭ 已確認進貨單（採購分攤費用單 LandedCostAllocation 的 GR 選入器來源）════════
-- LandedCostAllocation 的 GoodsReceiptLines 選入器只列「Status=Confirmed(1) 或 Posted」
-- 且有明細的進貨單（並經全域廠別/資料授權過濾），故 PlantId=P1、CreatedByRoleId='1'。
-- Save 只建草稿分攤單、不做過帳，任一有明細的已確認 GR 即可驅動整個 modal→勾選→加列流程。
INSERT INTO "GoodsReceipts"
  ("GoodsReceiptId","ReceiptDate","CreatedBy","SupplierCode","TypeCode","TypeCategory",
   "Status","TotalAmount","ApprovalStatus","CreatedAt","UpdatedAt","PlantId","CreatedByRoleId")
VALUES ('E2E-PE01',current_date,'admin','E2E-CO01','PI','Purchase',
        1,1050,1,now(),now(),'P1','1')
ON CONFLICT ("GoodsReceiptId") DO UPDATE
  SET "Status"=1, "ApprovalStatus"=1, "TotalAmount"=1050,
      "SupplierCode"='E2E-CO01', "PlantId"='P1', "CreatedByRoleId"='1', "UpdatedAt"=now();

INSERT INTO "GoodsReceiptLines"
  ("GoodsReceiptId","LineNo","ProductCode","ReceiptQty","UomCode","StockQty","StockUomCode",
   "UnitPrice","CurrencyCode","ExchangeRate","TaxRate","LineAmount","TaxAmount","TotalAmount",
   "AmountLocal","WhCode","LocationCode")
VALUES ('E2E-PE01',1,'E2E-FG01',100,'E2E-EA',100,'E2E-EA',
        10,'E2E-CUR',1,0.05,1000,50,1050,1050,'E2E-WFG','E2E-LFG')
ON CONFLICT ("GoodsReceiptId","LineNo") DO UPDATE
  SET "ProductCode"='E2E-FG01', "ReceiptQty"=100, "StockQty"=100,
      "LineAmount"=1000, "TaxAmount"=50, "TotalAmount"=1050, "AmountLocal"=1050;

-- ═══ ⑮ 月結清單（MonthEndUiTests 的唯讀前提）════════════════════════════════
-- MonthEnd 的畫面測試要驗「選了期間之後十個步驟逐列渲染」與「初始化按鈕只在
-- 這一期還沒有清單時才出現」。後者需要**兩種期間都存在**：一個有清單、一個沒有。
-- ⑪ 已建了上個月～未來兩個月共四期，這裡只對「上個月」那一期建清單，
-- 其餘三期維持沒有清單 —— 兩半都驗得到。
--
-- 🚨 **測試刻意不自己去按「初始化」**：那會在資料庫留下新資料，而 UITests 跑在
--    多 session 共用的環境上。前提由種子建立、由種子還原，才重跑得出同一個結果。
--
-- 步驟名稱與 MonthEndService.Steps 一致；名稱對不上不會壞（畫面照樣渲染），
-- 但人工看畫面時會以為是程式錯了，所以照抄。
INSERT INTO "MonthEndChecklists"
  ("ChecklistId","PeriodId","IsCompleted","CreatedAt","UpdatedAt","PlantId")
SELECT to_char(date_trunc('month', now() - interval '1 month'),'YYYYMM') || '-P1',
       to_char(date_trunc('month', now() - interval '1 month'),'YYYYMM'),
       false, now(), now(), 'P1'
ON CONFLICT ("ChecklistId") DO UPDATE
  SET "IsCompleted" = false, "CompletedAt" = NULL, "CompletedBy" = NULL, "UpdatedAt" = now();

-- Id 用 9500xx 的保留號段：ChecklistItems.Id 是 GENERATED BY DEFAULT，寫死的 Id
-- 不會推進序列，所以 app 自己新增時仍從 1 開始，不可能與這裡相撞。
-- Status=0（Pending）並在重跑時覆寫回 0 —— 上一輪若有測試把某一步完成掉，
-- 不歸零的話下一輪的「初始化按鈕」那一半會從不同的起點開始。
INSERT INTO "ChecklistItems" ("Id","ChecklistId","StepNo","StepName","Status")
SELECT 950000 + v.step,
       to_char(date_trunc('month', now() - interval '1 month'),'YYYYMM') || '-P1',
       v.step, v.name, 0
FROM (VALUES
    (1,'關閉庫存異動'), (2,'完成盤點差異調整'), (3,'結轉在製品'),
    (4,'計算工單實際成本'), (5,'結轉製造費用'), (6,'外幣評價'),
    (7,'存貨跌價評估'), (8,'鎖定期間'), (9,'期末調整分錄'), (10,'產出財務報表')
) AS v(step, name)
ON CONFLICT ("Id") DO UPDATE
  SET "Status" = 0, "Notes" = NULL, "CompletedAt" = NULL, "CompletedBy" = NULL;

-- ═══ ⑯ 報表工具三頁（ReportToolsUiTests 的唯讀前提）══════════════════════════
-- 執行紀錄要驗「依狀態篩選後每一列的徽章都是那個狀態」，所以 Success 與 Failed
-- **兩種都要有**；只種一種的話，篩選器失效（把全部列倒出來）仍然會通過。
-- 收件匣要驗「下載連結各自指向自己那一列」，所以要有多於一列帶 FilePath。
--
-- ToolKey 取 ReturnRate：它的 ContainsMonetaryValues 是 false，種一筆含金額的
-- 報表定義等於在測試庫裡放一份機密報表的殼，沒有必要。
INSERT INTO "ReportDefinitions"
  ("Id","Name","OwnerUserId","OwnerRoleId","PlantId","ToolKey","ParamsJson",
   "ScheduleKind","ScheduleHour","ScheduleMinute","IsActive","OutputFormat",
   "CreatedByRoleId","CreatedAt","ManualRunRequested")
SELECT 950001,'E2E-報表排程', u."Id", '1', 'P1', 'ReturnRate', '{"period":"Last30Days"}',
       0, 8, 0, true, 'Csv', '1', now(), false
FROM "AspNetUsers" u WHERE u."UserName" = 'admin'
ON CONFLICT ("Id") DO UPDATE
  SET "IsActive" = true, "ManualRunRequested" = false, "Name" = 'E2E-報表排程';

INSERT INTO "ReportRecipients" ("Id","DefinitionId","UserId","Language")
SELECT 950001, 950001, u."Id", 'zh-TW'
FROM "AspNetUsers" u WHERE u."UserName" = 'admin'
ON CONFLICT ("Id") DO UPDATE SET "Language" = 'zh-TW';

-- FilePath 指向一個**不存在**的檔：這兩列只用來驗畫面上的連結長相，測試不會按下去
-- （真的下載那條路由由 ReportPermissionIntegrationTests 驗，含權限重查）。
-- 種一個真的檔案反而要處理 Reports:StoragePath 在 CI 的落點，徒增一個會漂的前提。
--
-- ⚠️ 執行紀錄**掛在定義存在這個前提上**，而 "ReportRuns"."DefinitionId" 上沒有外鍵
--    （查過 information_schema：只有 ReportRecipients 那條有）。所以這裡從
--    "ReportDefinitions" 選出來插，不寫死。上面那段若因為找不到 admin 而插了 0 列，
--    這裡就會跟著是 0 列，驗收區的 ReportDefs(E2E) 與 ReportRuns(E2E) 一起顯示 0——
--    直接 VALUES 寫死的話會留下三列孤兒，畫面上看得到、卻查不到它屬於哪個排程。
INSERT INTO "ReportRuns"
  ("RunId","DefinitionId","RunAt","TriggerType","Status","RowCount","DurationMs",
   "FilePath","Language","ErrorMessage","CreatedBy")
SELECT v.run_id, d."Id", now() - v.ago, v.trigger, v.status, v.rows, v.ms,
       v.path, 'zh-TW', v.err, 'admin'
FROM "ReportDefinitions" d
CROSS JOIN (VALUES
    (950001, interval '2 hour',     'Manual',    'Success', 12, 510::bigint,
     'e2e/950001.csv'::varchar, NULL::varchar),
    (950002, interval '1 hour',     'Scheduled', 'Success',  7, 368,
     'e2e/950002.csv', NULL),
    (950003, interval '30 minute',  'Manual',    'Failed',   0,   0,
     NULL, 'E2E 種子建立的失敗紀錄：用來驗紅色徽章與失敗訊息欄。')
) AS v(run_id, ago, trigger, status, rows, ms, path, err)
WHERE d."Id" = 950001
ON CONFLICT ("RunId") DO UPDATE
  SET "Status" = EXCLUDED."Status", "FilePath" = EXCLUDED."FilePath",
      "RowCount" = EXCLUDED."RowCount", "ErrorMessage" = EXCLUDED."ErrorMessage";


-- ═══ ⑰ 簽核範本與三個步驟（ApprovalTemplateStepUiTests 的唯讀前提）════════════
--
-- 三個步驟刻意各用一種 ApproverType（**1**=Supervisor／2=Role／3=User，這個枚舉是
-- 1-based 的，不是 0-based），因為那支
-- UI 測試要驗的就是「換簽核者類型時，該露的那一欄才露出來」——三種都要有，
-- 只放一種的話，toggle 寫成常數也照樣全綠。
--
-- ⚠️ TargetRoleId 與 TargetUserId **不寫死**：角色表的 Id 是 Program.cs 種的
--    （Admin='1'），使用者 Id 是 Identity 產的 GUID，兩者在不同部署都不一樣。
--    所以從 "AspNetRoles" / "AspNetUsers" 選出來插；選不到就整段插 0 列，
--    驗收區會顯示 0，而不是留下指向不存在對象的孤兒步驟。
INSERT INTO "ApprovalTemplates"
  ("Id","Name","TargetDocumentType","PlantId","CreatedByRoleId","OrganizationUnitPath","SkipSameApprover")
VALUES ('e2e00000-0000-4000-8000-000000000001','E2E-簽核範本',NULL,'P1','1',NULL,false)
ON CONFLICT ("Id") DO UPDATE
  SET "Name" = EXCLUDED."Name", "PlantId" = EXCLUDED."PlantId",
      "CreatedByRoleId" = EXCLUDED."CreatedByRoleId";

-- 步驟 1：主管簽核——不需要對象，兩個目標欄都是 NULL。
INSERT INTO "ApprovalSteps" ("Id","TemplateId","Sequence","ApproverType","TargetRoleId","TargetUserId","Condition")
VALUES ('e2e00000-0000-4000-8000-000000000011','e2e00000-0000-4000-8000-000000000001',1,1,NULL,NULL,NULL)
ON CONFLICT ("Id") DO UPDATE
  SET "Sequence" = EXCLUDED."Sequence", "ApproverType" = EXCLUDED."ApproverType",
      "TargetRoleId" = EXCLUDED."TargetRoleId", "TargetUserId" = EXCLUDED."TargetUserId";

-- 步驟 2：指定角色。
INSERT INTO "ApprovalSteps" ("Id","TemplateId","Sequence","ApproverType","TargetRoleId","TargetUserId","Condition")
SELECT 'e2e00000-0000-4000-8000-000000000012','e2e00000-0000-4000-8000-000000000001',2,2,r."Id",NULL,NULL
FROM "AspNetRoles" r WHERE r."Id" = '1'
ON CONFLICT ("Id") DO UPDATE
  SET "Sequence" = EXCLUDED."Sequence", "ApproverType" = EXCLUDED."ApproverType",
      "TargetRoleId" = EXCLUDED."TargetRoleId", "TargetUserId" = EXCLUDED."TargetUserId";

-- 步驟 3：指定使用者。
INSERT INTO "ApprovalSteps" ("Id","TemplateId","Sequence","ApproverType","TargetRoleId","TargetUserId","Condition")
SELECT 'e2e00000-0000-4000-8000-000000000013','e2e00000-0000-4000-8000-000000000001',3,3,NULL,u."Id",NULL
FROM "AspNetUsers" u WHERE u."UserName" = 'admin'
ON CONFLICT ("Id") DO UPDATE
  SET "Sequence" = EXCLUDED."Sequence", "ApproverType" = EXCLUDED."ApproverType",
      "TargetRoleId" = EXCLUDED."TargetRoleId", "TargetUserId" = EXCLUDED."TargetUserId";

-- ═══ ⑱ 可手動選用的科目（JournalEntryActionSmokeTests 的前提）═══════════════════
-- 乾淨部署的科目表只有 StandardAccounts 種的**系統科目**，而系統科目的 AllowManualEntry
-- 一律為 FALSE（存貨、暫估應付這類餘額由自動分錄維護，見 ChartOfAccount.AllowManualEntry）。
-- 所以手動傳票的科目下拉在乾淨庫上是空的——這是設計，不是缺陷：正式環境要由會計自己建費用科目。
-- 測試因此自備一借一貸兩個非系統科目。ON CONFLICT 覆寫回可選狀態，防有人在畫面上把它停用。
INSERT INTO "ChartOfAccounts"
  ("AccountCode","AccountName","AccountType","IsSystem","AllowManualEntry","IsActive","CreatedAt","UpdatedAt","Remarks")
VALUES
  ('E2E-6101','E2E 雜項費用',6,false,true,true,now(),now(),'E2E 種子建立'),
  ('E2E-1101','E2E 零用金',  1,false,true,true,now(),now(),'E2E 種子建立')
ON CONFLICT ("AccountCode") DO UPDATE
  SET "AllowManualEntry"=true, "IsActive"=true, "UpdatedAt"=now();

-- ═══ ⑲ 折讓單草稿（APDebitNoteUiTests／ARCreditNoteUiTests 的前提）════════════
-- 折讓單沒有「新增」畫面，只由退貨單確認時的服務產生，而且只在來源發票**已付／已收款**時才開。
-- 要從畫面走到那一步得先跑完整條 P2P／O2C 再退貨，太長；這裡直接放一張草稿與它指向的已付款發票。
-- 金額：1 × 100，稅 5%。發票開立量 1，折讓量 1——確認時的「折讓量不得超過開立量」剛好守得住。
INSERT INTO "APInvoices"
  ("APInvoiceId","SupplierCode","InvoiceDate","DueDate","CurrencyCode","ExchangeRate","TaxRate",
   "TaxAmount","TotalAmount","AmountLocal","PaidAmount","DebitedAmount","Status","ApprovalStatus",
   "SourceDocType","PlantId","CreatedByRoleId","Remarks","CreatedAt","UpdatedAt")
VALUES ('E2E-API01','E2E-CO01',current_date,current_date + 30,NULL,1,0.05,
        5,105,105,105,0,2,2,'Manual','P1','1','E2E 折讓單來源發票',now(),now())
ON CONFLICT ("APInvoiceId") DO UPDATE
  SET "Status"=2, "PaidAmount"=105, "DebitedAmount"=0, "UpdatedAt"=now();
INSERT INTO "APInvoiceLines"
  ("APInvoiceId","LineNo","ProductCode","Qty","UomCode","UnitPrice","LineAmount","TaxRate","TaxAmount")
VALUES ('E2E-API01',1,'E2E-RM01',1,'E2E-EA',100,100,0.05,5)
ON CONFLICT ("APInvoiceId","LineNo") DO UPDATE SET "Qty"=1, "LineAmount"=100, "TaxAmount"=5;

INSERT INTO "APDebitNotes"
  ("APDebitNoteId","SupplierCode","NoteDate","CurrencyCode","ExchangeRate","TaxAmount","TotalAmount",
   "AmountLocal","Status","ApprovalStatus","SourceDocType","PlantId","CreatedByRoleId","Remarks","CreatedAt","UpdatedAt")
VALUES ('E2E-DN01','E2E-CO01',current_date,NULL,1,5,105,105,0,0,'GoodsReturn','P1','1','E2E 折讓單草稿',now(),now())
ON CONFLICT ("APDebitNoteId") DO NOTHING;
INSERT INTO "APDebitNoteLines"
  ("APDebitNoteId","LineNo","APInvoiceId","InvoiceLineNo","ProductCode","Qty","UomCode","UnitPrice","LineAmount","TaxRate","TaxAmount")
VALUES ('E2E-DN01',1,'E2E-API01',1,'E2E-RM01',1,'E2E-EA',100,100,0.05,5)
ON CONFLICT ("APDebitNoteId","LineNo") DO NOTHING;

INSERT INTO "ARInvoices"
  ("ARInvoiceId","CustomerCode","InvoiceDate","DueDate","CurrencyCode","ExchangeRate","TaxRate",
   "TaxAmount","TotalAmount","AmountLocal","ReceivedAmount","CreditedAmount","Status","ApprovalStatus",
   "SourceDocType","PlantId","CreatedByRoleId","Remarks","CreatedAt","UpdatedAt")
VALUES ('E2E-ARI01','E2E-CO01',current_date,current_date + 30,NULL,1,0.05,
        5,105,105,105,0,2,2,'Manual','P1','1','E2E 折讓單來源發票',now(),now())
ON CONFLICT ("ARInvoiceId") DO UPDATE
  SET "Status"=2, "ReceivedAmount"=105, "CreditedAmount"=0, "UpdatedAt"=now();
INSERT INTO "ARInvoiceLines"
  ("ARInvoiceId","LineNo","ProductCode","Qty","UomCode","UnitPrice","LineAmount","TaxRate","TaxAmount")
VALUES ('E2E-ARI01',1,'E2E-FG01',1,'E2E-EA',100,100,0.05,5)
ON CONFLICT ("ARInvoiceId","LineNo") DO UPDATE SET "Qty"=1, "LineAmount"=100, "TaxAmount"=5;

INSERT INTO "ARCreditNotes"
  ("ARCreditNoteId","CustomerCode","NoteDate","CurrencyCode","ExchangeRate","TaxAmount","TotalAmount",
   "AmountLocal","Status","ApprovalStatus","SourceDocType","PlantId","CreatedByRoleId","Remarks","CreatedAt","UpdatedAt")
VALUES ('E2E-CN01','E2E-CO01',current_date,NULL,1,5,105,105,0,0,'SalesReturn','P1','1','E2E 折讓單草稿',now(),now())
ON CONFLICT ("ARCreditNoteId") DO NOTHING;
INSERT INTO "ARCreditNoteLines"
  ("ARCreditNoteId","LineNo","ARInvoiceId","InvoiceLineNo","ProductCode","Qty","UomCode","UnitPrice","LineAmount","TaxRate","TaxAmount")
VALUES ('E2E-CN01',1,'E2E-ARI01',1,'E2E-FG01',1,'E2E-EA',100,100,0.05,5)
ON CONFLICT ("ARCreditNoteId","LineNo") DO NOTHING;

-- ⑲-b 未結發票（APPaymentActionSmokeTests／ARReceiptActionSmokeTests 的前提）。
-- #211 起收付款「金額必須等於核銷合計、至少一筆核銷」在**新增**就擋；乾淨庫沒有任何未結發票，
-- 新增付款／收款從畫面根本存不起來。⑲ 那兩張是全額收付的，不能拿來核銷，故另放一張未結的。
-- 草稿收付款不動 PaidAmount／ReceivedAmount（確認才動），每次重跑種子再歸零以防其他測試確認過。
INSERT INTO "APInvoices"
  ("APInvoiceId","SupplierCode","InvoiceDate","DueDate","CurrencyCode","ExchangeRate","TaxRate",
   "TaxAmount","TotalAmount","AmountLocal","PaidAmount","DebitedAmount","Status","ApprovalStatus",
   "SourceDocType","PlantId","CreatedByRoleId","Remarks","CreatedAt","UpdatedAt")
VALUES ('E2E-API02','E2E-CO01',current_date,current_date + 30,NULL,1,0.05,
        5,105,105,0,0,0,2,'Manual','P1','1','E2E 付款單 smoke 核銷用未結發票',now(),now())
ON CONFLICT ("APInvoiceId") DO UPDATE
  SET "Status"=0, "PaidAmount"=0, "DebitedAmount"=0, "UpdatedAt"=now();
INSERT INTO "APInvoiceLines"
  ("APInvoiceId","LineNo","ProductCode","Qty","UomCode","UnitPrice","LineAmount","TaxRate","TaxAmount")
VALUES ('E2E-API02',1,'E2E-RM01',1,'E2E-EA',100,100,0.05,5)
ON CONFLICT ("APInvoiceId","LineNo") DO NOTHING;

INSERT INTO "ARInvoices"
  ("ARInvoiceId","CustomerCode","InvoiceDate","DueDate","CurrencyCode","ExchangeRate","TaxRate",
   "TaxAmount","TotalAmount","AmountLocal","ReceivedAmount","CreditedAmount","Status","ApprovalStatus",
   "SourceDocType","PlantId","CreatedByRoleId","Remarks","CreatedAt","UpdatedAt")
VALUES ('E2E-ARI02','E2E-CO01',current_date,current_date + 30,NULL,1,0.05,
        5,105,105,0,0,0,2,'Manual','P1','1','E2E 收款單 smoke 核銷用未結發票',now(),now())
ON CONFLICT ("ARInvoiceId") DO UPDATE
  SET "Status"=0, "ReceivedAmount"=0, "CreditedAmount"=0, "UpdatedAt"=now();
INSERT INTO "ARInvoiceLines"
  ("ARInvoiceId","LineNo","ProductCode","Qty","UomCode","UnitPrice","LineAmount","TaxRate","TaxAmount")
VALUES ('E2E-ARI02',1,'E2E-FG01',1,'E2E-EA',100,100,0.05,5)
ON CONFLICT ("ARInvoiceId","LineNo") DO NOTHING;

-- ═══ ⑳ 唯讀角色與帳號（ReadOnlyRoleUiTests 的前提）════════════════════════════
-- 其他 UI 測試全部以 admin 登入，「權限收窄時畫面與後端是否都擋得住」從來沒有被走過。
-- 用**專屬角色** E2E-RO，不借用既有角色：收窄既有角色會把同庫其他測試／其他 session
-- 依賴的權限一起改掉（2026-08-10 就這樣毀掉過兩輪評測）。
-- 權限每次整批重建成固定的一組，防有人在「權限設定」畫面改過之後留下殘留。
--
-- 帳號 e2e.readonly／密碼 E2eReadOnly!2026（Identity v3 雜湊、固定鹽，僅供測試庫）。
-- 只有兩個畫面的 CanRead，其餘一概沒有——就算這支種子被誤跑到正式庫，這個帳號也寫不了任何東西。
-- 掛在根組織（ORG-ROOT，Program.cs 種的）：資料授權在角色**沒有**組織路徑時只放行
-- 「自己角色建的單」，那樣唯讀角色看不到任何別人建的單據，畫面層的權限就無從驗起。
-- 真實的唯讀查帳角色也是掛在某個組織底下，不是懸空的。
INSERT INTO "AspNetRoles" ("Id","Name","NormalizedName","NameZh","NameEn","ConcurrencyStamp","OrganizationUnitId")
VALUES ('E2E-RO','E2E 唯讀','E2E 唯讀','E2E 唯讀','E2E Read-only','e2e-ro','ORG-ROOT')
ON CONFLICT ("Id") DO UPDATE SET "OrganizationUnitId"='ORG-ROOT';

INSERT INTO "RolePlantAssignments" ("RoleId","PlantId","IsDefault")
VALUES ('E2E-RO','P1',true)
ON CONFLICT ("RoleId","PlantId") DO NOTHING;

DELETE FROM "MenuPermissions" WHERE "RoleId" = 'E2E-RO';
INSERT INTO "MenuPermissions" ("Id","RoleId","MenuKey","CanRead","CanCreate","CanUpdate","CanDelete","CanPrint")
VALUES ('e2e-ro-journalentry','E2E-RO','JournalEntry',true,false,false,false,false),
       ('e2e-ro-apdebitnote', 'E2E-RO','APDebitNote', true,false,false,false,false)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO "AspNetUsers"
  ("Id","UserName","NormalizedUserName","Email","NormalizedEmail","EmailConfirmed","PasswordHash",
   "SecurityStamp","ConcurrencyStamp","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled",
   "AccessFailedCount","MustChangePassword","ActivePlantId","ActiveRoleId","Language","TimeZoneId")
VALUES ('e2e00000-0000-4000-8000-0000000000a0','e2e.readonly','E2E.READONLY',
        'e2e.readonly@example.invalid','E2E.READONLY@EXAMPLE.INVALID',true,
        'AQAAAAIAAYagAAAAEAABAgMEBQYHCAkKCwwNDg9L5Dqic0V6FQ+q9JwwjmE50sylkRwobPDlqdJX0VNCyA==',
        'E2E-RO-STAMP','e2e-ro',false,false,true,0,false,'P1','E2E-RO','zh-TW','Asia/Taipei')
ON CONFLICT ("Id") DO UPDATE
  SET "PasswordHash"=EXCLUDED."PasswordHash", "LockoutEnd"=NULL, "AccessFailedCount"=0,
      "TwoFactorEnabled"=false, "MustChangePassword"=false,
      "ActivePlantId"='P1', "ActiveRoleId"='E2E-RO';

INSERT INTO "AspNetUserRoles" ("UserId","RoleId")
VALUES ('e2e00000-0000-4000-8000-0000000000a0','E2E-RO')
ON CONFLICT ("UserId","RoleId") DO NOTHING;
-- ═══ ㉓ Admin 角色的手機作業權限（MobilePwaOfflineTests 的前提）═══════════════
-- 乾淨部署的 MobileRolePermissions 是空的——連 Admin 登入手機首頁也是零個模組，
-- 「離線冷啟動時模組格跟線上一致」那支測試就只能比較 0 == 0，什麼都沒驗到。
--
-- ⚠️ 衝突時 DO NOTHING，不是 DO UPDATE：別的測試或人工驗收可能刻意把 Admin 的
--    某個手機作業設成不允許，這裡把它翻回 true 就是把別人的前提洗掉。
--    手機側測試只需要「有幾個」線上離線一致，不需要「全部」。
-- 編號用 ㉓：⑱～⑳ 是 UITests 強化、㉑㉒ 是財務／月結測試分支，避免合併時撞號。
INSERT INTO "MobileRolePermissions" ("Id","RoleId","ModuleActionId","IsAllowed")
SELECT 'e2e-mrp-' || a."Id", r."Id", a."Id", true
FROM "MobileModuleActions" a CROSS JOIN "AspNetRoles" r
WHERE r."Id" = '1'
ON CONFLICT ("RoleId","ModuleActionId") DO NOTHING;

-- ═══ ㉑ 財務報表與結帳（FinanceReportUiTests／PeriodCloseUiTests 的前提）═══════
-- 之前的種子只有「已付款」發票，帳齡／ECL 打開永遠是空表——空表不會讓任何斷言失敗，
-- 等於報表從沒被驗過。這裡放一組各帳齡區間都有值、金額互不相同的未結發票。
--
-- 用**專屬往來對象**：手動發票測試與 O2C／P2P 流程也會用 E2E-CO01 開發票，
-- 共用的話帳齡金額隨測試順序變動。報表測試一律以 searchCustomer／searchSupplier 限定。
-- 到期日以 current_date 為基準、離區間邊界至少 10 天，app 與 DB 的「今天」差一天也不會跨區。
--
-- AR（E2E-AGC）預期帳齡：未到期 1000／1-30 2000／31-60 2000（3000 已收 1000）／61-90 4000／90+ 5000
-- AP（E2E-AGS）預期帳齡：未到期 1100／1-30 2200／31-60 2200（3300 已付 1100）／61-90 4400／90+ 5500
-- 另各放一張「已收／付清」與一張「作廢」，兩者都必須被排除。
INSERT INTO "Companies"
  ("CompanyCode","CompanyName","ShortName","IsCustomer","IsSupplier","TaxRate",
   "IsActive","ApprovalStatus","QualificationStatus","Remarks","CreatedAt","UpdatedAt")
VALUES
  ('E2E-AGC','E2E 帳齡客戶','E2E帳齡客',true,false,0.05,true,0,0,'E2E 帳齡／ECL 報表專用',now(),now()),
  ('E2E-AGS','E2E 帳齡供應商','E2E帳齡供',false,true,0.05,true,0,0,'E2E 帳齡報表專用',now(),now())
ON CONFLICT ("CompanyCode") DO UPDATE
  SET "IsActive"=true, "IsCustomer"=EXCLUDED."IsCustomer", "IsSupplier"=EXCLUDED."IsSupplier",
      "ApprovalStatus"=0, "UpdatedAt"=now();

INSERT INTO "ARInvoices"
  ("ARInvoiceId","CustomerCode","InvoiceDate","DueDate","CurrencyCode","ExchangeRate","TaxRate",
   "TaxAmount","TotalAmount","AmountLocal","ReceivedAmount","CreditedAmount","Status","ApprovalStatus",
   "SourceDocType","PlantId","CreatedByRoleId","Remarks","CreatedAt","UpdatedAt")
VALUES
  ('E2E-AGR1','E2E-AGC',current_date - 20 ,current_date + 10 ,NULL,1,0,0,1000,1000,   0,0,0,2,'Manual','P1','1','E2E 帳齡 未到期',now(),now()),
  ('E2E-AGR2','E2E-AGC',current_date - 45 ,current_date - 15 ,NULL,1,0,0,2000,2000,   0,0,0,2,'Manual','P1','1','E2E 帳齡 1-30',now(),now()),
  ('E2E-AGR3','E2E-AGC',current_date - 75 ,current_date - 45 ,NULL,1,0,0,3000,3000,1000,0,1,2,'Manual','P1','1','E2E 帳齡 31-60 部分收款',now(),now()),
  ('E2E-AGR4','E2E-AGC',current_date - 105,current_date - 75 ,NULL,1,0,0,4000,4000,   0,0,0,2,'Manual','P1','1','E2E 帳齡 61-90',now(),now()),
  ('E2E-AGR5','E2E-AGC',current_date - 150,current_date - 120,NULL,1,0,0,5000,5000,   0,0,0,2,'Manual','P1','1','E2E 帳齡 90+',now(),now()),
  ('E2E-AGR6','E2E-AGC',current_date - 150,current_date - 120,NULL,1,0,0,9999,9999,9999,0,2,2,'Manual','P1','1','E2E 帳齡 已收清（應排除）',now(),now()),
  ('E2E-AGR7','E2E-AGC',current_date - 150,current_date - 120,NULL,1,0,0,8888,8888,   0,0,3,2,'Manual','P1','1','E2E 帳齡 作廢（應排除）',now(),now())
ON CONFLICT ("ARInvoiceId") DO UPDATE
  SET "CustomerCode"=EXCLUDED."CustomerCode", "InvoiceDate"=EXCLUDED."InvoiceDate", "DueDate"=EXCLUDED."DueDate",
      "TotalAmount"=EXCLUDED."TotalAmount", "AmountLocal"=EXCLUDED."AmountLocal",
      "ReceivedAmount"=EXCLUDED."ReceivedAmount", "CreditedAmount"=0, "Status"=EXCLUDED."Status",
      "PlantId"='P1', "UpdatedAt"=now();

INSERT INTO "APInvoices"
  ("APInvoiceId","SupplierCode","InvoiceDate","DueDate","CurrencyCode","ExchangeRate","TaxRate",
   "TaxAmount","TotalAmount","AmountLocal","PaidAmount","DebitedAmount","Status","ApprovalStatus",
   "SourceDocType","PlantId","CreatedByRoleId","Remarks","CreatedAt","UpdatedAt")
VALUES
  ('E2E-AGP1','E2E-AGS',current_date - 20 ,current_date + 10 ,NULL,1,0,0,1100,1100,   0,0,0,2,'Manual','P1','1','E2E 帳齡 未到期',now(),now()),
  ('E2E-AGP2','E2E-AGS',current_date - 45 ,current_date - 15 ,NULL,1,0,0,2200,2200,   0,0,0,2,'Manual','P1','1','E2E 帳齡 1-30',now(),now()),
  ('E2E-AGP3','E2E-AGS',current_date - 75 ,current_date - 45 ,NULL,1,0,0,3300,3300,1100,0,1,2,'Manual','P1','1','E2E 帳齡 31-60 部分付款',now(),now()),
  ('E2E-AGP4','E2E-AGS',current_date - 105,current_date - 75 ,NULL,1,0,0,4400,4400,   0,0,0,2,'Manual','P1','1','E2E 帳齡 61-90',now(),now()),
  ('E2E-AGP5','E2E-AGS',current_date - 150,current_date - 120,NULL,1,0,0,5500,5500,   0,0,0,2,'Manual','P1','1','E2E 帳齡 90+',now(),now()),
  ('E2E-AGP6','E2E-AGS',current_date - 150,current_date - 120,NULL,1,0,0,9999,9999,9999,0,2,2,'Manual','P1','1','E2E 帳齡 已付清（應排除）',now(),now()),
  ('E2E-AGP7','E2E-AGS',current_date - 150,current_date - 120,NULL,1,0,0,8888,8888,   0,0,3,2,'Manual','P1','1','E2E 帳齡 作廢（應排除）',now(),now())
ON CONFLICT ("APInvoiceId") DO UPDATE
  SET "SupplierCode"=EXCLUDED."SupplierCode", "InvoiceDate"=EXCLUDED."InvoiceDate", "DueDate"=EXCLUDED."DueDate",
      "TotalAmount"=EXCLUDED."TotalAmount", "AmountLocal"=EXCLUDED."AmountLocal",
      "PaidAmount"=EXCLUDED."PaidAmount", "DebitedAmount"=0, "Status"=EXCLUDED."Status",
      "PlantId"='P1', "UpdatedAt"=now();

-- NRV：專屬品號 10 個 @ 成本 100；期末前 90 天唯一一筆銷售單價 80（專屬發票 E2E-NRVI1）。
-- 2026-10-24 起 NRV 售價窗是「期末往前 90 天」，測試期間是 209001（期末 2090-01-31），
-- 所以這筆銷售必須落在 2089-11-02～2090-01-31。原本掛在帳齡發票 E2E-AGR1（今天 −20 天）上，期末口徑後永遠取不到售價。
-- 設為已全額收款：不進任何未結餘額（帳齡／ECL／外幣重評），NRV 只排除作廢發票，照樣採計。
-- 預期：銷售費用 80×2%=1.6、NRV 78.4、減損 (100−78.4)×10 = 216。
INSERT INTO "Products"
  ("ProductCode","ProductName","Spec","RoutingId","SalesUom","PurchaseUom","StockUom",
   "StockMode","SafetyStock","StandardCost","MrpMode","LeadTimeDays","UrgentLeadTimeDays",
   "OutsourceQtyPerDay","IsLotControlled","IqcExempt","IsActive","ApprovalStatus","CategoryCode",
   "CreatedAt","UpdatedAt")
VALUES ('E2E-NRV01','E2E NRV 減損品','E2E NRV 評估專用',NULL,'E2E-EA','E2E-EA','E2E-EA',
        'P',0,100,'P',1,1,0,false,false,true,0,'E2E-CAT',now(),now())
ON CONFLICT ("ProductCode") DO UPDATE SET "IsActive"=true, "ApprovalStatus"=0, "UpdatedAt"=now();

INSERT INTO "Inventories"
  ("PlantId","ProductCode","LocationCode","LotNo","QtyOnHand","QtyReserved","UnitCost","LastUpdatedAt")
VALUES ('P1','E2E-NRV01','E2E-LFG','',10,0,100,now())
ON CONFLICT ("PlantId","ProductCode","LocationCode","LotNo") DO UPDATE
  SET "QtyOnHand"=10, "QtyReserved"=0, "UnitCost"=100, "LastUpdatedAt"=now();
INSERT INTO "StockMovements"
  ("MovementId","TypeCode","TypeCategory","DocNo","DocLineNo","MovementDate","PlantId","ProductCode",
   "LocationCode","LotNo","UomCode","Qty","StockUomCode","StockQty","Direction","UnitCost","TotalCost",
   "CreatedBy","CreatedAt","Remark")
VALUES ('e2e0e2e0-0000-4000-8000-000000000004','E2E-OPEN','Opening','E2E-OPEN01',4,current_date - 200,'P1','E2E-NRV01','E2E-LFG','','E2E-EA',10,'E2E-EA',10,1,100,1000,'admin',now(),'E2E 期初')
ON CONFLICT ("MovementId") DO NOTHING;

DELETE FROM "ARInvoiceLines" WHERE "ARInvoiceId"='E2E-AGR1' AND "ProductCode"='E2E-NRV01';   -- 舊庫殘留
INSERT INTO "ARInvoices"
  ("ARInvoiceId","CustomerCode","InvoiceDate","DueDate","CurrencyCode","ExchangeRate","TaxRate",
   "TaxAmount","TotalAmount","AmountLocal","ReceivedAmount","CreditedAmount","Status","ApprovalStatus",
   "SourceDocType","PlantId","CreatedByRoleId","Remarks","CreatedAt","UpdatedAt")
VALUES ('E2E-NRVI1','E2E-CO01','2090-01-15','2090-02-14',NULL,1,0,0,80,80,80,0,2,2,'Manual','P1','1','E2E NRV 售價（已收清）',now(),now())
ON CONFLICT ("ARInvoiceId") DO UPDATE
  SET "InvoiceDate"='2090-01-15', "TotalAmount"=80, "AmountLocal"=80, "ReceivedAmount"=80, "Status"=2, "UpdatedAt"=now();
INSERT INTO "ARInvoiceLines"
  ("ARInvoiceId","LineNo","ProductCode","Qty","UomCode","UnitPrice","LineAmount","TaxRate","TaxAmount")
VALUES ('E2E-NRVI1',1,'E2E-NRV01',1,'E2E-EA',80,80,0,0)
ON CONFLICT ("ARInvoiceId","LineNo") DO UPDATE SET "ProductCode"='E2E-NRV01', "UnitPrice"=80, "LineAmount"=80;

-- 結帳專用期間 2090-01：遠在任何真實日期之後，關帳／重開不會影響其他測試的當期過帳。
-- 每輪回到 Open、清掉上一輪的月結清單與評估結果；再放一張已知金額的手動分錄給試算表驗。
DELETE FROM "ChecklistItems"      WHERE "ChecklistId" = '209001-P1';
DELETE FROM "MonthEndChecklists"  WHERE "ChecklistId" = '209001-P1';
DELETE FROM "EclAllowances"           WHERE "PeriodId" = '209001' AND "PlantId" = 'P1';
DELETE FROM "InventoryNrvAssessments" WHERE "PeriodId" = '209001' AND "PlantId" = 'P1';
DELETE FROM "JournalLines" WHERE "JournalId" IN (
  SELECT "JournalId" FROM "JournalEntries" WHERE "PeriodId" = '209001' AND "PlantId" = 'P1');
DELETE FROM "JournalEntries" WHERE "PeriodId" = '209001' AND "PlantId" = 'P1';

INSERT INTO "AccountingPeriods"
  ("PeriodId","PlantId","Year","Month","StartDate","EndDate","Status","ClosedAt","ClosedBy","CreatedAt","UpdatedAt","Remarks")
VALUES ('209001','P1',2090,1,'2090-01-01','2090-01-31',0,NULL,NULL,now(),now(),'E2E 結帳測試專用期間')
ON CONFLICT ("PeriodId","PlantId") DO UPDATE
  SET "Status"=0, "ClosedAt"=NULL, "ClosedBy"=NULL, "UpdatedAt"=now();

INSERT INTO "JournalEntries"
  ("JournalId","PeriodId","EntryDate","SourceDocType","SourceDocId","Type","Description","CreatedAt","PlantId")
VALUES ('E2E-TB01','209001','2090-01-15','Manual',NULL,1,'E2E 試算表驗算分錄',now(),'P1')
ON CONFLICT ("JournalId") DO NOTHING;
INSERT INTO "JournalLines"
  ("JournalId","LineNo","AccountCode","AccountName","Debit","Credit","CurrencyCode","Amount","ExchangeRate","AmountLocal","Description")
VALUES
  ('E2E-TB01',1,'E2E-6101','E2E 費用科目',1000,0,NULL,1000,1,1000,'E2E 試算表借方'),
  ('E2E-TB01',2,'E2E-1101','E2E 資產科目',0,1000,NULL,1000,1,1000,'E2E 試算表貸方')
ON CONFLICT ("Id") DO NOTHING;

-- ═══ ㉒ 月結批次（MonthEndBatchUiTests 的前提）════════════════════════════════
-- 清上一輪：部門費用、外幣重評、WIP 快照（2090-01 的分錄已在 ㉑ 一併清掉）。
DELETE FROM "DepartmentActualCosts" WHERE "PeriodId" = '209001' AND "PlantId" = 'P1';
DELETE FROM "FxRevaluationEntries"  WHERE "PeriodId" = '209001' AND "PlantId" = 'P1';
DELETE FROM "WipSnapshots"          WHERE "PeriodId" = '209001' AND "PlantId" = 'P1';
-- 步驟 9 的外幣重評迴轉分錄日期是期末次日（2090-02-01），落在下一期，㉑ 按期間清不到。
DELETE FROM "JournalLines" WHERE "JournalId" IN (
  SELECT "JournalId" FROM "JournalEntries"
  WHERE "SourceDocType" = 'FxRevaluationReversal' AND "SourceDocId" = '209001' AND "PlantId" = 'P1');
DELETE FROM "JournalEntries"
  WHERE "SourceDocType" = 'FxRevaluationReversal' AND "SourceDocId" = '209001' AND "PlantId" = 'P1';

-- 部門實際費用：與下面完工暫估的人工 300／製費 150 相等。關帳前稽核 WIP_IDENTITY_DRIFT 要求
-- 期末 WIP = 期初 + 發料 + 部門人工／製費 − 完工成本；少了這筆，完工 450 無從而來，結帳測試關不了帳。
-- MonthEndBatchUiTests 會從畫面把它改成 1000／600，重估後完工成本同為 1600，恆等式仍平衡。
INSERT INTO "DepartmentActualCosts" ("PeriodId","WorkCenterCode","ActualLaborCost","ActualOverheadCost","UpdatedAt","PlantId")
VALUES ('209001','E2E-WC01',300,150,now(),'P1')
ON CONFLICT ("PlantId","PeriodId","WorkCenterCode") DO UPDATE
  SET "ActualLaborCost"=300, "ActualOverheadCost"=150, "UpdatedAt"=now();

-- 步驟 4 實際費率重估：專屬已完工工單 E2E-WO9001，2090-01 在 E2E-WC01 報工 2 小時、換線 1 小時。
-- 完工時以標準費率暫估人工 300／製費 150（Preliminary），並配一張吸收分錄（貸 5110 300／5120 150）——
-- 少了那張分錄，關帳前稽核 WO_COST_JOURNAL_DRIFT 會擋下結帳測試。
-- 測試輸入部門費用人工 1000／製費 600 → 實際人工率 1000÷2=500、實際製費率 600÷(2+1)=200，
-- 工單重估為人工 1000／製費 600，差額 +700／+450，調整分錄 貸 5110 700、貸 5120 450、借 5130 1150。
INSERT INTO "WorkOrders"
  ("WorkOrderId","ProductionBatchId","ProductCode","RoutingId","Quantity","Status",
   "PlannedStartDate","PlannedEndDate","Type","ApprovalStatus","MaterialReadinessStatus",
   "GrossDemandQty","QtyScrap","IsOutsource",
   "ActualLaborCost","ActualMaterialCost","ActualOverheadCost","MaterialVarianceCost",
   "StandardLaborCost","StandardMaterialCost","StandardOverheadCost",
   "PlantId","CreatedAt","UpdatedAt","CreatedByRoleId","OrganizationUnitPath")
VALUES ('E2E-WO9001','E2E-BATCH01','E2E-FG01','E2E-RT01',1,3,
        '2090-01-10','2090-01-10',0,2,0,1,0,false,
        300,0,150,0,300,0,150,
        'P1',now(),now(),'1',NULL)
ON CONFLICT ("WorkOrderId") DO UPDATE
  SET "Status"=3, "ActualLaborCost"=300, "ActualOverheadCost"=150, "ActualMaterialCost"=0,
      "PlantId"='P1', "UpdatedAt"=now();

INSERT INTO "WorkOrderOperations"
  ("WorkOrderId","OperationSeq","OperationName","WorkCenterId","SetupTime","UnitTime",
   "BatchMode","BatchQty","IsOutsource","RequiresInspection","RequiresFpi",
   "TimeUnit","SetupTimeUnit","MoveTime","QueueTime")
VALUES ('E2E-WO9001',10,'E2E 月結重估','E2E-WC01',60,120,0,1,false,false,false,2,0,0,0)
ON CONFLICT ("WorkOrderId","OperationSeq") DO UPDATE SET "WorkCenterId"='E2E-WC01';

INSERT INTO "Dispatches"
  ("DispatchId","WorkOrderId","OperationSeq","Quantity","QtyDone","Status",
   "IsOutsource","MaterialRisk","ScheduledStart","ScheduledEnd",
   "PlannedStartDate","PlannedEndDate",
   "PlantId","CreatedAt","UpdatedAt","CreatedByRoleId","OrganizationUnitPath")
VALUES ('E2E-DP9001','E2E-WO9001',10,1,1,5,false,false,
        '2090-01-10 07:00+00','2090-01-10 10:00+00','2090-01-10','2090-01-10',
        'P1',now(),now(),'1',NULL)
ON CONFLICT ("DispatchId") DO UPDATE SET "Status"=5, "QtyDone"=1, "UpdatedAt"=now();

INSERT INTO "ProductionSetups"
  ("SetupId","DispatchId","WorkOrderId","Status","SetupStartAt","SetupEndAt","PlantId","CreatedAt","UpdatedAt")
VALUES ('E2E-SU9001','E2E-DP9001','E2E-WO9001',1,'2090-01-10 07:00+00','2090-01-10 08:00+00','P1',now(),now())
ON CONFLICT ("SetupId") DO UPDATE SET "SetupStartAt"=EXCLUDED."SetupStartAt", "SetupEndAt"=EXCLUDED."SetupEndAt";

INSERT INTO "ProductionSessions"
  ("SessionId","DispatchId","WorkOrderId","Status","StartAt","EndAt","PlantId","CreatedAt","UpdatedAt")
VALUES ('E2E-SS9001','E2E-DP9001','E2E-WO9001',1,'2090-01-10 08:00+00','2090-01-10 10:00+00','P1',now(),now())
ON CONFLICT ("SessionId") DO UPDATE SET "StartAt"=EXCLUDED."StartAt", "EndAt"=EXCLUDED."EndAt";

INSERT INTO "WorkOrderActualCosts"
  ("WorkOrderId","ActualMaterial","ActualLabor","ActualOverhead","StandardMaterial","StandardLabor","StandardOverhead",
   "VarMaterial","VarLaborEfficiency","VarOverheadRate","CostDate","Status","CreatedAt","PlantId")
VALUES ('E2E-WO9001',0,300,150,0,300,150,0,0,0,'2090-01-20',0,now(),'P1')
ON CONFLICT ("WorkOrderId") DO UPDATE
  SET "ActualLabor"=300, "ActualOverhead"=150, "ActualMaterial"=0,
      "VarLaborEfficiency"=0, "VarOverheadRate"=0, "Status"=0, "CostDate"='2090-01-20';

INSERT INTO "JournalEntries"
  ("JournalId","PeriodId","EntryDate","SourceDocType","SourceDocId","Type","Description","CreatedAt","PlantId")
VALUES ('E2E-WOC9001','209001','2090-01-20','WorkOrderCompletion','E2E-WO9001',0,'E2E 完工吸收（月結重估前提）',now(),'P1')
ON CONFLICT ("JournalId") DO NOTHING;
INSERT INTO "JournalLines"
  ("JournalId","LineNo","AccountCode","AccountName","Debit","Credit","CurrencyCode","Amount","ExchangeRate","AmountLocal","Description")
VALUES
  ('E2E-WOC9001',1,'1401','原料存貨',        450,  0,NULL,450,1,450,'E2E 完工入庫'),
  ('E2E-WOC9001',2,'5110','直接人工-已分配',   0,300,NULL,300,1,300,'E2E 人工吸收'),
  ('E2E-WOC9001',3,'5120','製造費用-已分配',   0,150,NULL,150,1,150,'E2E 製費吸收')
ON CONFLICT ("Id") DO NOTHING;

-- 步驟 6 外幣重評：專屬往來對象（不混進帳齡測試的 E2E-AGC／E2E-AGS），幣別 E2E-CUR、帳面匯率 30。
-- 測試輸入期末匯率 32：AR 外幣 100 已收 40 → 未結 60，本幣 1800 → 1920，差 +120；
--                      AP 外幣 200 已付 50 → 未結 150，本幣 4500 → 4800，應付變大＝損失 −300。
-- 步驟 9 入帳：借 1170 120／貸 4190 120；借 5199 300／貸 2100 300；2090-02-01 迴轉。
INSERT INTO "Companies"
  ("CompanyCode","CompanyName","ShortName","IsCustomer","IsSupplier","TaxRate",
   "IsActive","ApprovalStatus","QualificationStatus","Remarks","CreatedAt","UpdatedAt")
VALUES ('E2E-FXC','E2E 外幣往來對象','E2E外幣',true,true,0,true,0,0,'E2E 外幣重評專用',now(),now())
ON CONFLICT ("CompanyCode") DO UPDATE SET "IsActive"=true, "IsCustomer"=true, "IsSupplier"=true, "UpdatedAt"=now();

INSERT INTO "ARInvoices"
  ("ARInvoiceId","CustomerCode","InvoiceDate","DueDate","CurrencyCode","ExchangeRate","TaxRate",
   "TaxAmount","TotalAmount","AmountLocal","ReceivedAmount","CreditedAmount","Status","ApprovalStatus",
   "SourceDocType","PlantId","CreatedByRoleId","Remarks","CreatedAt","UpdatedAt")
VALUES ('E2E-FXR1','E2E-FXC',current_date - 5,current_date + 25,'E2E-CUR',30,0,0,100,3000,40,0,1,2,'Manual','P1','1','E2E 外幣重評 AR',now(),now())
ON CONFLICT ("ARInvoiceId") DO UPDATE
  SET "TotalAmount"=100, "AmountLocal"=3000, "ReceivedAmount"=40, "ExchangeRate"=30, "Status"=1, "UpdatedAt"=now();

INSERT INTO "APInvoices"
  ("APInvoiceId","SupplierCode","InvoiceDate","DueDate","CurrencyCode","ExchangeRate","TaxRate",
   "TaxAmount","TotalAmount","AmountLocal","PaidAmount","DebitedAmount","Status","ApprovalStatus",
   "SourceDocType","PlantId","CreatedByRoleId","Remarks","CreatedAt","UpdatedAt")
VALUES ('E2E-FXP1','E2E-FXC',current_date - 5,current_date + 25,'E2E-CUR',30,0,0,200,6000,50,0,1,2,'Manual','P1','1','E2E 外幣重評 AP',now(),now())
ON CONFLICT ("APInvoiceId") DO UPDATE
  SET "TotalAmount"=200, "AmountLocal"=6000, "PaidAmount"=50, "ExchangeRate"=30, "Status"=1, "UpdatedAt"=now();

-- ═══ ㉔ 多收付退款（CreditNoteUiTestBase 退款測試的前提）════════════════════════
-- ⑲ 的 E2E-ARI01／E2E-API01 是全額收付的發票，確認折讓後變成多收付 105，退款測試從收款／付款畫面
-- 以負數核銷退回。每輪清掉上一輪對這兩張發票開的收付款單與分錄（發票本身由 ⑲ 回到收付清狀態）。
DELETE FROM "JournalLines" WHERE "JournalId" IN (
  SELECT "JournalId" FROM "JournalEntries" WHERE "PlantId" = 'P1' AND (
       ("SourceDocType" IN ('ARReceipt','ARReceipt-Void') AND "SourceDocId" IN (
          SELECT "ARReceiptId" FROM "ARReceiptAllocations" WHERE "ARInvoiceId" = 'E2E-ARI01'))
    OR ("SourceDocType" IN ('APPayment','APPayment-Void') AND "SourceDocId" IN (
          SELECT "APPaymentId" FROM "APPaymentAllocations" WHERE "APInvoiceId" = 'E2E-API01'))));
DELETE FROM "JournalEntries" WHERE "PlantId" = 'P1' AND (
       ("SourceDocType" IN ('ARReceipt','ARReceipt-Void') AND "SourceDocId" IN (
          SELECT "ARReceiptId" FROM "ARReceiptAllocations" WHERE "ARInvoiceId" = 'E2E-ARI01'))
    OR ("SourceDocType" IN ('APPayment','APPayment-Void') AND "SourceDocId" IN (
          SELECT "APPaymentId" FROM "APPaymentAllocations" WHERE "APInvoiceId" = 'E2E-API01')));
CREATE TEMP TABLE _e2e_refund ON COMMIT DROP AS
  SELECT "ARReceiptId" AS id FROM "ARReceiptAllocations" WHERE "ARInvoiceId" = 'E2E-ARI01'
  UNION SELECT "APPaymentId" FROM "APPaymentAllocations" WHERE "APInvoiceId" = 'E2E-API01';
DELETE FROM "ARReceiptAllocations" WHERE "ARReceiptId" IN (SELECT id FROM _e2e_refund);
DELETE FROM "ARReceipts"           WHERE "ARReceiptId" IN (SELECT id FROM _e2e_refund);
DELETE FROM "APPaymentAllocations" WHERE "APPaymentId" IN (SELECT id FROM _e2e_refund);
DELETE FROM "APPayments"           WHERE "APPaymentId" IN (SELECT id FROM _e2e_refund);

-- ═══ ㉕ 重工流程（ReworkFlowTests 的前提）══════════════════════════════════════
-- 一張已完工的專用工單 E2E-WO0051（列表才有「重工」鈕）＋一筆報工 E2E-PR0051（NCR 以它為來源，
-- 執行重工處置時才找得到原工單）。不借用 E2E-WO0001：在它的派工上多一筆報工，完工流程測試的累計產出就錯了。
-- 重工工單的單號是系統給的（NCR 走單號設定、工單列表走「RW＋原單號後 8 碼＋時間」），沒有 E2E- 字樣，
-- 改以 ReworkFromWorkOrderId 限定範圍；NCR 也是採番，以來源報工單號限定。
CREATE TEMP TABLE _e2e_rw ON COMMIT DROP AS
  SELECT "WorkOrderId" AS id FROM "WorkOrders" WHERE "ReworkFromWorkOrderId" LIKE 'E2E-%';
CREATE TEMP TABLE _e2e_rw_slip ON COMMIT DROP AS
  SELECT "SlipId" AS id FROM "MaterialSlips" WHERE "SourceDocId" IN (SELECT id FROM _e2e_rw);
DELETE FROM "JournalLines" WHERE "JournalId" IN (
  SELECT "JournalId" FROM "JournalEntries" WHERE "SourceDocId" IN (SELECT id FROM _e2e_rw_slip));
DELETE FROM "JournalEntries" WHERE "SourceDocId" IN (SELECT id FROM _e2e_rw_slip);
DELETE FROM "MaterialSlipLines" WHERE "SlipId" IN (SELECT id FROM _e2e_rw_slip);
DELETE FROM "MaterialSlips"     WHERE "SlipId" IN (SELECT id FROM _e2e_rw_slip);
DELETE FROM "InventoryReservations" WHERE "SourceDocId" IN (SELECT id FROM _e2e_rw);
DELETE FROM "NonConformanceReports"
  WHERE "SourceId" = 'E2E-PR0051' OR "ReworkWorkOrderId" IN (SELECT id FROM _e2e_rw);
DELETE FROM "WorkOrderMaterials"  WHERE "WorkOrderId" IN (SELECT id FROM _e2e_rw);
DELETE FROM "WorkOrderOperations" WHERE "WorkOrderId" IN (SELECT id FROM _e2e_rw);
DELETE FROM "WorkOrders"          WHERE "WorkOrderId" IN (SELECT id FROM _e2e_rw);

INSERT INTO "WorkOrders"
  ("WorkOrderId","ProductionBatchId","ProductCode","RoutingId","Quantity","Status",
   "PlannedStartDate","PlannedEndDate","Type","ApprovalStatus","MaterialReadinessStatus",
   "GrossDemandQty","QtyScrap","IsOutsource",
   "ActualLaborCost","ActualMaterialCost","ActualOverheadCost","MaterialVarianceCost",
   "StandardLaborCost","StandardMaterialCost","StandardOverheadCost",
   "PlantId","CreatedAt","UpdatedAt","CreatedByRoleId","OrganizationUnitPath")
VALUES ('E2E-WO0051','E2E-BATCH01','E2E-FG01','E2E-RT01',10,3,
        now() - interval '3 day',now() - interval '1 day',0,2,1,0,0,false,
        0,0,0,0,0,0,0,
        'P1',now(),now(),'1',NULL)
ON CONFLICT ("WorkOrderId") DO UPDATE
  SET "Quantity"=10, "Status"=3, "ApprovalStatus"=2, "Type"=0, "QtyScrap"=0,
      "PlantId"='P1', "CreatedByRoleId"='1', "UpdatedAt"=now();

INSERT INTO "WorkOrderOperations"
  ("WorkOrderId","OperationSeq","OperationName","WorkCenterId","SetupTime","UnitTime",
   "BatchMode","BatchQty","IsOutsource","RequiresInspection","RequiresFpi",
   "TimeUnit","SetupTimeUnit","MoveTime","QueueTime")
VALUES ('E2E-WO0051',10,'E2E 加工','E2E-WC01',30,2,0,1,false,false,false,2,0,0,0),
       ('E2E-WO0051',20,'E2E 組裝','E2E-WC01',10,1,0,1,false,false,false,2,0,0,0)
ON CONFLICT ("WorkOrderId","OperationSeq") DO UPDATE SET "WorkCenterId"='E2E-WC01';

-- 兩道工序各掛一筆料（10 加工：RM01、20 組裝：RM02），「建立重工」只勾一道時看得出只帶那道的料。
-- 已發量歸零：本段只驗「重工工單複製了用料、而且是從 0 開始各自追蹤」，原工單的發料紀錄不在範圍內。
-- 先清掉：用料掛哪道工序改過（主鍵含工序），舊列留著就會多一筆。
DELETE FROM "WorkOrderMaterials" WHERE "WorkOrderId" = 'E2E-WO0051';
INSERT INTO "WorkOrderMaterials"
  ("WorkOrderId","OperationSeq","BomSeq","ProductCode","RequiredQty","UomCode",
   "SourceMode","UnitCost","BackflushTiming","IssueMethod","IssuedQty","ReturnedQty")
VALUES
  ('E2E-WO0051',10,1,'E2E-RM01',20,'E2E-EA',1,10,0,0,0,0),
  ('E2E-WO0051',20,2,'E2E-RM02',10,'E2E-EA',1,20,0,0,0,0)
ON CONFLICT ("WorkOrderId","OperationSeq","BomSeq") DO UPDATE
  SET "RequiredQty"=EXCLUDED."RequiredQty", "IssuedQty"=0, "ReturnedQty"=0, "IssueMethod"=0;

INSERT INTO "Dispatches"
  ("DispatchId","WorkOrderId","OperationSeq","Quantity","QtyDone","Status",
   "IsOutsource","MaterialRisk","ScheduledStart","ScheduledEnd",
   "PlannedStartDate","PlannedEndDate",
   "PlantId","CreatedAt","UpdatedAt","CreatedByRoleId","OrganizationUnitPath")
VALUES ('E2E-DP0051','E2E-WO0051',10,10,10,5,
        false,false,NULL,NULL,
        now() - interval '3 day',now() - interval '2 day',
        'P1',now(),now(),'1',NULL)
ON CONFLICT ("DispatchId") DO UPDATE
  SET "Quantity"=10, "QtyDone"=10, "Status"=5, "ScheduledStart"=NULL, "ScheduledEnd"=NULL,
      "PlantId"='P1', "CreatedByRoleId"='1', "UpdatedAt"=now();

-- 派工不給排程時段（ScheduledStart/End＝NULL）：它只是報工的掛載點，不必畫進排程甘特。
-- （當初是為了避開 /Schedule 縮圖刷選的上游競態 reading 'domain'；該競態已在 vendored 檔修補，見 mfg-domain.md §8.5 第 6 點。）
-- ⓪ 已依 DispatchId 刪掉這兩筆，這裡重建。報工 8 良 2 不良，NCR 的數量就是那 2 個不良。
INSERT INTO "ProductionSessions"
  ("SessionId","DispatchId","WorkOrderId","Status","StartAt","EndAt","PlantId","CreatedAt","UpdatedAt")
VALUES ('E2E-PS0051','E2E-DP0051','E2E-WO0051',1,now() - interval '3 day',now() - interval '2 day','P1',now(),now())
ON CONFLICT ("SessionId") DO NOTHING;
INSERT INTO "ProductionReports"
  ("ReportId","SessionId","DispatchId","WorkOrderId","Status","QtyOk","QtyNg","ReportedAt","PlantId","CreatedAt","UpdatedAt")
VALUES ('E2E-PR0051','E2E-PS0051','E2E-DP0051','E2E-WO0051',1,8,2,now() - interval '2 day','P1',now(),now())
ON CONFLICT ("ReportId") DO NOTHING;

-- ═══ ㉖ 退貨 → 折讓（ReturnAllowanceFlowTests 的前提）══════════════════════════
-- 退貨過帳（完整模式在倉庫下架／上架那一刻）會回沖來源發票：發票還沒收付款就直接改小，
-- 已收付款則不動發票、改開一張折讓單草稿。要從畫面走到「已收付款」得先跑完整條 P2P／O2C，
-- 這裡直接放好已過帳的進貨單／出貨單與發票，各兩組：01＝發票已收付清、02＝發票未收付。
-- 專用品號 E2E-RTN01，不與其他測試共用庫存；期初＋進貨＋出貨的異動與帳面一致（INV_ROLLFORWARD）。
-- 退貨單、折讓單都是採番，以「明細的來源進貨單／出貨單」限定範圍。
CREATE TEMP TABLE _e2e_rtn ON COMMIT DROP AS
  SELECT DISTINCT "GoodsReturnId" AS id FROM "GoodsReturnLines" WHERE "GoodsReceiptId" IN ('E2E-RGR01','E2E-RGR02')
  UNION SELECT DISTINCT "SalesReturnId" FROM "SalesReturnLines" WHERE "GoodsShipmentId" IN ('E2E-RGS01','E2E-RGS02');
CREATE TEMP TABLE _e2e_rtn_note ON COMMIT DROP AS
  SELECT "APDebitNoteId" AS id FROM "APDebitNotes" WHERE "SourceDocType" = 'GoodsReturn' AND "SourceDocId" IN (SELECT id FROM _e2e_rtn)
  UNION SELECT "ARCreditNoteId" FROM "ARCreditNotes" WHERE "SourceDocType" = 'SalesReturn' AND "SourceDocId" IN (SELECT id FROM _e2e_rtn);
DELETE FROM "JournalLines" WHERE "JournalId" IN (
  SELECT "JournalId" FROM "JournalEntries" WHERE "SourceDocId" IN (
    SELECT id FROM _e2e_rtn UNION SELECT id FROM _e2e_rtn_note
    UNION SELECT unnest(ARRAY['E2E-API11','E2E-API12','E2E-ARI11','E2E-ARI12'])));
DELETE FROM "JournalEntries" WHERE "SourceDocId" IN (
    SELECT id FROM _e2e_rtn UNION SELECT id FROM _e2e_rtn_note
    UNION SELECT unnest(ARRAY['E2E-API11','E2E-API12','E2E-ARI11','E2E-ARI12']));
DELETE FROM "APDebitNoteLines"  WHERE "APDebitNoteId"  IN (SELECT id FROM _e2e_rtn_note);
DELETE FROM "APDebitNotes"      WHERE "APDebitNoteId"  IN (SELECT id FROM _e2e_rtn_note);
DELETE FROM "ARCreditNoteLines" WHERE "ARCreditNoteId" IN (SELECT id FROM _e2e_rtn_note);
DELETE FROM "ARCreditNotes"     WHERE "ARCreditNoteId" IN (SELECT id FROM _e2e_rtn_note);
DELETE FROM "GoodsReturnLocationDetails" WHERE "GoodsReturnId" IN (SELECT id FROM _e2e_rtn);
DELETE FROM "GoodsReturnLines"           WHERE "GoodsReturnId" IN (SELECT id FROM _e2e_rtn);
DELETE FROM "GoodsReturns"               WHERE "GoodsReturnId" IN (SELECT id FROM _e2e_rtn);
DELETE FROM "SalesReturnLocationDetails" WHERE "SalesReturnId" IN (SELECT id FROM _e2e_rtn);
DELETE FROM "SalesReturnLines"           WHERE "SalesReturnId" IN (SELECT id FROM _e2e_rtn);
DELETE FROM "SalesReturns"               WHERE "SalesReturnId" IN (SELECT id FROM _e2e_rtn);

-- IqcExempt：退貨不經 IQC，免得上架被品質隔離閘門擋在無關的地方。
INSERT INTO "Products"
  ("ProductCode","ProductName","Spec","RoutingId","SalesUom","PurchaseUom","StockUom",
   "StockMode","SafetyStock","StandardCost","MrpMode","LeadTimeDays","UrgentLeadTimeDays",
   "OutsourceQtyPerDay","IsLotControlled","IqcExempt","IsActive","ApprovalStatus","CategoryCode",
   "CreatedAt","UpdatedAt")
VALUES ('E2E-RTN01','E2E 退貨料 RTN01','E2E 退貨折讓流程專用',NULL,'E2E-EA','E2E-EA','E2E-EA',
        'P',0,100,'P',1,1,0,false,true,true,0,'E2E-CAT',now(),now())
ON CONFLICT ("ProductCode") DO UPDATE
  SET "IsActive"=true, "IqcExempt"=true, "StockUom"='E2E-EA', "UpdatedAt"=now();

-- 進貨：10 × 100、稅 5% ＝ 1,050，進原料儲位並已上架過帳（Status=3）。
INSERT INTO "GoodsReceipts"
  ("GoodsReceiptId","ReceiptDate","CreatedBy","SupplierCode","TypeCode","TypeCategory",
   "Status","TotalAmount","ApprovalStatus","CreatedAt","UpdatedAt","PlantId","CreatedByRoleId")
VALUES ('E2E-RGR01',current_date - 10,'admin','E2E-CO01','PI','Purchase',3,1050,2,now(),now(),'P1','1'),
       ('E2E-RGR02',current_date - 10,'admin','E2E-CO01','PI','Purchase',3,1050,2,now(),now(),'P1','1')
ON CONFLICT ("GoodsReceiptId") DO UPDATE
  SET "Status"=3, "ApprovalStatus"=2, "TotalAmount"=1050, "SupplierCode"='E2E-CO01',
      "PlantId"='P1', "CreatedByRoleId"='1', "UpdatedAt"=now();
INSERT INTO "GoodsReceiptLines"
  ("GoodsReceiptId","LineNo","ProductCode","ReceiptQty","UomCode","StockQty","StockUomCode",
   "UnitPrice","CurrencyCode","ExchangeRate","TaxCode","TaxRate","LineAmount","TaxAmount","TotalAmount",
   "AmountLocal","WhCode","LocationCode","LotNo","QtyPutAway","ReturnedQty","QtySourceCleared","QtyGood","QtyRejected")
VALUES
  ('E2E-RGR01',1,'E2E-RTN01',10,'E2E-EA',10,'E2E-EA',100,NULL,1,'E2E-TAX',0.05,1000,50,1050,1050,'E2E-WRAW','E2E-LRAW','',10,0,0,10,0),
  ('E2E-RGR02',1,'E2E-RTN01',10,'E2E-EA',10,'E2E-EA',100,NULL,1,'E2E-TAX',0.05,1000,50,1050,1050,'E2E-WRAW','E2E-LRAW','',10,0,0,10,0)
ON CONFLICT ("GoodsReceiptId","LineNo") DO UPDATE
  SET "ProductCode"='E2E-RTN01', "ReceiptQty"=10, "StockQty"=10, "QtyPutAway"=10, "ReturnedQty"=0,
      "QtySourceCleared"=0, "QtyGood"=10, "UnitPrice"=100, "TaxRate"=0.05,
      "LineAmount"=1000, "TaxAmount"=50, "TotalAmount"=1050, "AmountLocal"=1050;

-- 出貨：10 × 200、稅 5% ＝ 2,100，自成品儲位出庫並已過帳。
INSERT INTO "GoodsShipments"
  ("GoodsShipmentId","ShipDate","TypeCode","CompanyCode","TotalAmount","Status","ApprovalStatus",
   "CreatedBy","CreatedAt","UpdatedAt","PlantId","CreatedByRoleId")
VALUES ('E2E-RGS01',current_date - 10,'GS','E2E-CO01',2100,3,2,'admin',now(),now(),'P1','1'),
       ('E2E-RGS02',current_date - 10,'GS','E2E-CO01',2100,3,2,'admin',now(),now(),'P1','1')
ON CONFLICT ("GoodsShipmentId") DO UPDATE
  SET "Status"=3, "ApprovalStatus"=2, "TotalAmount"=2100, "CompanyCode"='E2E-CO01',
      "PlantId"='P1', "CreatedByRoleId"='1', "UpdatedAt"=now();
INSERT INTO "GoodsShipmentLines"
  ("GoodsShipmentId","LineNo","ProductCode","ShipQty","UomCode","StockQty","StockUomCode",
   "UnitPrice","CurrencyCode","ExchangeRate","TaxCode","TaxRate","LineAmount","TaxAmount","TotalAmount",
   "WhCode","LocationCode","LotNo","QtySourceCleared","ReturnedQty","QtyPutAway","AmountLocal")
VALUES
  ('E2E-RGS01',1,'E2E-RTN01',10,'E2E-EA',10,'E2E-EA',200,NULL,1,'E2E-TAX',0.05,2000,100,2100,'E2E-WFG','E2E-LFG','',0,0,10,2100),
  ('E2E-RGS02',1,'E2E-RTN01',10,'E2E-EA',10,'E2E-EA',200,NULL,1,'E2E-TAX',0.05,2000,100,2100,'E2E-WFG','E2E-LFG','',0,0,10,2100)
ON CONFLICT ("GoodsShipmentId","LineNo") DO UPDATE
  SET "ProductCode"='E2E-RTN01', "ShipQty"=10, "StockQty"=10, "ReturnedQty"=0, "QtySourceCleared"=0,
      "QtyPutAway"=10, "UnitPrice"=200, "TaxRate"=0.05,
      "LineAmount"=2000, "TaxAmount"=100, "TotalAmount"=2100, "AmountLocal"=2100;

-- 發票：明細 SourceLineNo 指回進貨／出貨明細（回沖靠它找行）。11＝已收付清、12＝未收付。
INSERT INTO "APInvoices"
  ("APInvoiceId","SupplierCode","InvoiceDate","DueDate","CurrencyCode","ExchangeRate","TaxRate",
   "TaxAmount","TotalAmount","AmountLocal","PaidAmount","DebitedAmount","Status","ApprovalStatus",
   "SourceDocType","SourceDocId","PlantId","CreatedByRoleId","Remarks","CreatedAt","UpdatedAt")
VALUES ('E2E-API11','E2E-CO01',current_date - 10,current_date + 20,NULL,1,0.05,50,1050,1050,1050,0,2,2,
        'GoodsReceipt','E2E-RGR01','P1','1','E2E 退貨折讓：已付款',now(),now()),
       ('E2E-API12','E2E-CO01',current_date - 10,current_date + 20,NULL,1,0.05,50,1050,1050,0,0,0,2,
        'GoodsReceipt','E2E-RGR02','P1','1','E2E 退貨折讓：未付款',now(),now())
ON CONFLICT ("APInvoiceId") DO UPDATE
  SET "TaxAmount"=50, "TotalAmount"=1050, "AmountLocal"=1050, "PaidAmount"=EXCLUDED."PaidAmount",
      "DebitedAmount"=0, "Status"=EXCLUDED."Status", "UpdatedAt"=now();
INSERT INTO "APInvoiceLines"
  ("APInvoiceId","LineNo","ProductCode","Qty","UomCode","UnitPrice","LineAmount","TaxCode","TaxRate","TaxAmount","SourceLineNo")
VALUES ('E2E-API11',1,'E2E-RTN01',10,'E2E-EA',100,1000,'E2E-TAX',0.05,50,1),
       ('E2E-API12',1,'E2E-RTN01',10,'E2E-EA',100,1000,'E2E-TAX',0.05,50,1)
ON CONFLICT ("APInvoiceId","LineNo") DO UPDATE SET "Qty"=10, "LineAmount"=1000, "TaxAmount"=50, "SourceLineNo"=1;

INSERT INTO "ARInvoices"
  ("ARInvoiceId","CustomerCode","InvoiceDate","DueDate","CurrencyCode","ExchangeRate","TaxRate",
   "TaxAmount","TotalAmount","AmountLocal","ReceivedAmount","CreditedAmount","Status","ApprovalStatus",
   "SourceDocType","SourceDocId","PlantId","CreatedByRoleId","Remarks","CreatedAt","UpdatedAt")
VALUES ('E2E-ARI11','E2E-CO01',current_date - 10,current_date + 20,NULL,1,0.05,100,2100,2100,2100,0,2,2,
        'GoodsShipment','E2E-RGS01','P1','1','E2E 退貨折讓：已收款',now(),now()),
       ('E2E-ARI12','E2E-CO01',current_date - 10,current_date + 20,NULL,1,0.05,100,2100,2100,0,0,0,2,
        'GoodsShipment','E2E-RGS02','P1','1','E2E 退貨折讓：未收款',now(),now())
ON CONFLICT ("ARInvoiceId") DO UPDATE
  SET "TaxAmount"=100, "TotalAmount"=2100, "AmountLocal"=2100, "ReceivedAmount"=EXCLUDED."ReceivedAmount",
      "CreditedAmount"=0, "Status"=EXCLUDED."Status", "UpdatedAt"=now();
INSERT INTO "ARInvoiceLines"
  ("ARInvoiceId","LineNo","ProductCode","Qty","UomCode","UnitPrice","LineAmount","TaxCode","TaxRate","TaxAmount","SourceLineNo")
VALUES ('E2E-ARI11',1,'E2E-RTN01',10,'E2E-EA',200,2000,'E2E-TAX',0.05,100,1),
       ('E2E-ARI12',1,'E2E-RTN01',10,'E2E-EA',200,2000,'E2E-TAX',0.05,100,1)
ON CONFLICT ("ARInvoiceId","LineNo") DO UPDATE SET "Qty"=10, "LineAmount"=2000, "TaxAmount"=100, "SourceLineNo"=1;

-- 庫存與異動：期初 100 在成品儲位、兩張進貨各 +10 進原料儲位、兩張出貨各 −10 出成品儲位。
-- 出貨的出庫異動非放不可：銷售退回的退回成本取自原出貨那一筆出庫異動（SalesReturnCostBasis）。
INSERT INTO "Inventories"
  ("PlantId","ProductCode","LocationCode","LotNo","QtyOnHand","QtyReserved","UnitCost","LastUpdatedAt")
VALUES ('P1','E2E-RTN01','E2E-LFG' ,'',80,0,100,now()),
       ('P1','E2E-RTN01','E2E-LRAW','',20,0,100,now())
ON CONFLICT ("PlantId","ProductCode","LocationCode","LotNo") DO UPDATE
  SET "QtyOnHand"=EXCLUDED."QtyOnHand", "QtyReserved"=0, "UnitCost"=100, "LastUpdatedAt"=now();
INSERT INTO "StockMovements"
  ("MovementId","TypeCode","TypeCategory","DocNo","DocLineNo","MovementDate","PlantId","ProductCode",
   "LocationCode","LotNo","UomCode","Qty","StockUomCode","StockQty","Direction","UnitCost","TotalCost",
   "CreatedBy","CreatedAt","Remark")
VALUES
  ('e2e0e2e0-0000-4000-8000-000000000006','E2E-OPEN','Opening' ,'E2E-OPEN01',6,current_date - 200,'P1','E2E-RTN01','E2E-LFG' ,'','E2E-EA',100,'E2E-EA',100, 1,100,10000,'admin',now(),'E2E 期初'),
  ('e2e0e2e0-0000-4000-8000-000000000007','PI'      ,'Purchase','E2E-RGR01' ,1,current_date - 10 ,'P1','E2E-RTN01','E2E-LRAW','','E2E-EA', 10,'E2E-EA', 10, 1,100, 1000,'admin',now(),'E2E 退貨折讓進貨'),
  ('e2e0e2e0-0000-4000-8000-000000000008','PI'      ,'Purchase','E2E-RGR02' ,1,current_date - 10 ,'P1','E2E-RTN01','E2E-LRAW','','E2E-EA', 10,'E2E-EA', 10, 1,100, 1000,'admin',now(),'E2E 退貨折讓進貨'),
  ('e2e0e2e0-0000-4000-8000-000000000009','GS'      ,'Sales'   ,'E2E-RGS01' ,1,current_date - 10 ,'P1','E2E-RTN01','E2E-LFG' ,'','E2E-EA', 10,'E2E-EA', 10,-1,100, 1000,'admin',now(),'E2E 退貨折讓出貨'),
  ('e2e0e2e0-0000-4000-8000-000000000010','GS'      ,'Sales'   ,'E2E-RGS02' ,1,current_date - 10 ,'P1','E2E-RTN01','E2E-LFG' ,'','E2E-EA', 10,'E2E-EA', 10,-1,100, 1000,'admin',now(),'E2E 退貨折讓出貨')
ON CONFLICT ("MovementId") DO NOTHING;

-- ═══ ㉗ NCR 各處置方式（NcrDispositionUiTests 的前提）══════════════════════════════
-- NCR 的「執行處置／送審／結案／刪除」鈕到 2026-10-15 前從沒出現過（見 d037e9b8），這些處置從沒從畫面跑過。
-- 測試開的 NCR 備註一律填 E2E-NCRFLOW，以此限定清掉它們與它們開出來的單（報廢單已過帳、退貨與委外重工單是草稿）；
-- 報廢的庫存異動已由 ⓪ 依品號清掉、庫存由 ⑦ 回到基準。
CREATE TEMP TABLE _e2e_ncr ON COMMIT DROP AS
  SELECT "NcrId" AS id, "ScrapDocId" AS scrap, "GoodsReturnId" AS grt, "OutsourceReworkId" AS osr
  FROM "NonConformanceReports" WHERE "Remarks" = 'E2E-NCRFLOW';
DELETE FROM "JournalLines" WHERE "JournalId" IN (
  SELECT "JournalId" FROM "JournalEntries" WHERE "SourceDocId" IN (SELECT scrap FROM _e2e_ncr WHERE scrap IS NOT NULL));
DELETE FROM "JournalEntries" WHERE "SourceDocId" IN (SELECT scrap FROM _e2e_ncr WHERE scrap IS NOT NULL);
DELETE FROM "ScrapDocLines"    WHERE "ScrapDocId"    IN (SELECT scrap FROM _e2e_ncr WHERE scrap IS NOT NULL);
DELETE FROM "ScrapDocs"        WHERE "ScrapDocId"    IN (SELECT scrap FROM _e2e_ncr WHERE scrap IS NOT NULL);
DELETE FROM "GoodsReturnLines" WHERE "GoodsReturnId" IN (SELECT grt FROM _e2e_ncr WHERE grt IS NOT NULL);
DELETE FROM "GoodsReturns"     WHERE "GoodsReturnId" IN (SELECT grt FROM _e2e_ncr WHERE grt IS NOT NULL);
DELETE FROM "OutsourceReworks" WHERE "OutsourceReworkId" IN (SELECT osr FROM _e2e_ncr WHERE osr IS NOT NULL);
DELETE FROM "NonConformanceReports" WHERE "NcrId" IN (SELECT id FROM _e2e_ncr);

-- 進料檢驗不合格（IQC Fail）：來源是 ⑭ 的進貨單 E2E-PE01 第 1 行。NCR 處置「退貨」只認 IQC 來源，
-- 由此追回進貨單帶出供應商、單價與儲位。
INSERT INTO "InspectionOrders"
  ("InspectionOrderId","PlantId","InspectionType","SourceDocId","SourceLineNo","ProductCode",
   "LotSize","SampleSize","VerificationLevel","ActualSampleQty","FailQty","Status","Remarks","CreatedAt","UpdatedAt")
VALUES ('E2E-IQC51','P1','IQC','E2E-PE01',1,'E2E-FG01',100,5,4,5,1,'Fail','E2E NCR 退貨來源',now(),now())
ON CONFLICT ("InspectionOrderId") DO UPDATE
  SET "InspectionType"='IQC', "SourceDocId"='E2E-PE01', "SourceLineNo"=1, "Status"='Fail', "UpdatedAt"=now();

-- ═══ ㉘ 庫存重工發退料單（ReworkMaterialFlowTests 的前提）════════════════════════════
-- 測試開的單備註一律填 E2E-RWMFLOW，以此限定清掉它們與分錄（庫存異動已由 ⓪ 依品號清掉、庫存由 ⑦ 回到基準）。
-- 不清的話，上一輪留下的已過帳發料單會一直出現在退料的來源清單裡。
CREATE TEMP TABLE _e2e_rwm ON COMMIT DROP AS
  SELECT "ReworkMaterialDocId" AS id FROM "ReworkMaterialDocs" WHERE "Remarks" = 'E2E-RWMFLOW';
DELETE FROM "JournalLines" WHERE "JournalId" IN (
  SELECT "JournalId" FROM "JournalEntries" WHERE "SourceDocId" IN (SELECT id FROM _e2e_rwm));
DELETE FROM "JournalEntries" WHERE "SourceDocId" IN (SELECT id FROM _e2e_rwm);
DELETE FROM "ReworkMaterialDocLocationDetails" WHERE "ReworkMaterialDocId" IN (SELECT id FROM _e2e_rwm);
DELETE FROM "ReworkMaterialDocLines" WHERE "ReworkMaterialDocId" IN (SELECT id FROM _e2e_rwm);
DELETE FROM "ReworkMaterialDocs"     WHERE "ReworkMaterialDocId" IN (SELECT id FROM _e2e_rwm);

-- 同上，庫存委外發退料單（OutsourceMaterialFlowTests，備註 E2E-OSMFLOW）。委外類別不產生分錄，不必清傳票。
CREATE TEMP TABLE _e2e_osm ON COMMIT DROP AS
  SELECT "OutsourceMaterialDocId" AS id FROM "OutsourceMaterialDocs" WHERE "Remarks" = 'E2E-OSMFLOW';
DELETE FROM "OutsourceMaterialDocLocationDetails" WHERE "OutsourceMaterialDocId" IN (SELECT id FROM _e2e_osm);
DELETE FROM "OutsourceMaterialDocLines" WHERE "OutsourceMaterialDocId" IN (SELECT id FROM _e2e_osm);
DELETE FROM "OutsourceMaterialDocs"     WHERE "OutsourceMaterialDocId" IN (SELECT id FROM _e2e_osm);

-- 同上，借入借出單（LotTrackingFlowTests，備註 E2E-LOTFLOW）。借入（IB）會開傳票（存貨／其他應付款—借入物料），
-- 借出（IL）所有權未移轉不開。庫存已在上面依 E2E-% 清掉，傳票不清會讓總帳存貨多出這筆，月結稽核 INV_GL_SUBLEDGER 擋關帳。
CREATE TEMP TABLE _e2e_loan ON COMMIT DROP AS
  SELECT "LoanDocId" AS id FROM "LoanDocs" WHERE "Remarks" = 'E2E-LOTFLOW';
DELETE FROM "JournalLines" WHERE "JournalId" IN (
  SELECT "JournalId" FROM "JournalEntries" WHERE "SourceDocId" IN (SELECT id FROM _e2e_loan));
DELETE FROM "JournalEntries" WHERE "SourceDocId" IN (SELECT id FROM _e2e_loan);
DELETE FROM "LoanDocLocationDetails" WHERE "LoanDocId" IN (SELECT id FROM _e2e_loan);
DELETE FROM "LoanDocLines"           WHERE "LoanDocId" IN (SELECT id FROM _e2e_loan);
DELETE FROM "LoanDocs"               WHERE "LoanDocId" IN (SELECT id FROM _e2e_loan);

-- ═══ 孤兒庫存傳票（異動已被清掉、傳票還在）════════════════════════════════════
-- 種子開頭依 E2E-% 刪掉庫存異動，但各測試過帳時開的傳票留著；幾支測試的收尾也只刪單據與異動（例：
-- MobileQualityPickE2ETests、MobileLoanRecountE2ETests）。總帳存貨因此與明細帳對不上，
-- 全量執行時 PeriodCloseUiTests 被 INV_GL_SUBLEDGER 擋（殘差隨前面跑過哪些測試而變）。
-- 庫存過帳（SourceDocType='INV'）的傳票與異動一定同時產生，異動不在了傳票就該跟著清。
-- 只動 INV：進料費用分攤、完工吸收、期初開帳這類「只有傳票沒有異動」的來源不在此列。
DELETE FROM "JournalLines" WHERE "JournalId" IN (
  SELECT e."JournalId" FROM "JournalEntries" e
   WHERE e."PlantId"='P1' AND e."SourceDocType"='INV'
     AND NOT EXISTS (SELECT 1 FROM "StockMovements" m WHERE m."DocNo" = e."SourceDocId"));
DELETE FROM "JournalEntries" e
 WHERE e."PlantId"='P1' AND e."SourceDocType"='INV'
   AND NOT EXISTS (SELECT 1 FROM "StockMovements" m WHERE m."DocNo" = e."SourceDocId");

-- ═══ 期初開帳傳票（對應上面各段 E2E-OPEN01 期初異動）════════════════════════════════
-- 期初庫存直接寫進 Inventories／StockMovements，從沒有傳票——總帳存貨 1401 比明細帳少整筆期初價值，
-- 2026-10-22 起月結稽核 INV_GL_SUBLEDGER 擋關帳（PeriodCloseUiTests 紅）。金額取自期初異動本身，不寫死：
-- 哪段種子再加期初庫存，這張自動跟上。分錄日期與期初異動同日，也讓「當期試算表」不依賴其他測試先產生分錄。
INSERT INTO "ChartOfAccounts"
  ("AccountCode","AccountName","AccountType","IsSystem","AllowManualEntry","IsActive","CreatedAt","UpdatedAt","Remarks")
VALUES ('E2E-3101','E2E 期初開帳',3,false,false,true,now(),now(),'E2E 種子建立（期初庫存的對方科目）')
ON CONFLICT ("AccountCode") DO UPDATE SET "IsActive"=true, "UpdatedAt"=now();
DELETE FROM "JournalLines"   WHERE "JournalId" = 'E2E-OPENJ01';
DELETE FROM "JournalEntries" WHERE "JournalId" = 'E2E-OPENJ01';
INSERT INTO "JournalEntries"
  ("JournalId","PeriodId","EntryDate","SourceDocType","SourceDocId","Type","Description","CreatedAt","PlantId")
VALUES ('E2E-OPENJ01', to_char(current_date - 200, 'YYYYMM'), current_date - 200, 'Opening', 'E2E-OPEN01', 1,
        'E2E 期初庫存開帳', now(), 'P1');
INSERT INTO "JournalLines"
  ("JournalId","LineNo","AccountCode","AccountName","Debit","Credit","CurrencyCode","Amount","ExchangeRate","AmountLocal","Description")
SELECT 'E2E-OPENJ01', 1, '1401', '原料存貨', v, 0, NULL, v, 1, v, 'E2E 期初庫存'
  FROM (SELECT COALESCE(SUM("Direction" * "StockQty" * COALESCE("UnitCost",0)),0) AS v
          FROM "StockMovements" WHERE "PlantId"='P1' AND "DocNo"='E2E-OPEN01') t
UNION ALL
SELECT 'E2E-OPENJ01', 2, 'E2E-3101', 'E2E 期初開帳', 0, v, NULL, v, 1, v, 'E2E 期初庫存'
  FROM (SELECT COALESCE(SUM("Direction" * "StockQty" * COALESCE("UnitCost",0)),0) AS v
          FROM "StockMovements" WHERE "PlantId"='P1' AND "DocNo"='E2E-OPEN01') t;

COMMIT;

-- ── 驗收 ────────────────────────────────────────────────────────────────────
-- 每一列都必須 ≥ 1（品號 4、庫存 3）；為 0 代表該段沒建起來，流程測試會以難懂的
-- 「下拉沒有選項」或「查無資料」形式失敗。
\echo ''
\echo '── E2E fixture 驗收 ──'
SELECT 'Products'       AS entity, count(*) AS n FROM "Products"       WHERE "ProductCode" LIKE 'E2E-%'
UNION ALL SELECT 'Companies',        count(*) FROM "Companies"         WHERE "CompanyCode" LIKE 'E2E-%'
UNION ALL SELECT 'Currencies',       count(*) FROM "Currencies"        WHERE "CurrencyCode" LIKE 'E2E-%'
UNION ALL SELECT 'Taxes',            count(*) FROM "Taxes"             WHERE "TaxCode"     LIKE 'E2E-%'
UNION ALL SELECT 'ShiftGroups',      count(*) FROM "ShiftGroups"       WHERE "GroupCode"   LIKE 'E2E-%'
UNION ALL SELECT 'ShiftGroupDays',   count(*) FROM "ShiftGroupDays"    WHERE "GroupCode"   LIKE 'E2E-%'
UNION ALL SELECT 'Warehouses',       count(*) FROM "Warehouses"        WHERE "WhCode"      LIKE 'E2E-%'
UNION ALL SELECT 'WorkCenters',      count(*) FROM "WorkCenters"       WHERE "WorkCenterCode" LIKE 'E2E-%'
UNION ALL SELECT 'RoutingOperations',count(*) FROM "RoutingOperations" WHERE "RoutingId"   LIKE 'E2E-%'
UNION ALL SELECT 'RoutingBoms',      count(*) FROM "RoutingBoms"       WHERE "RoutingId"   LIKE 'E2E-%'
UNION ALL SELECT 'Inventories',      count(*) FROM "Inventories"       WHERE "ProductCode" LIKE 'E2E-%'
UNION ALL SELECT 'WorkOrders',       count(*) FROM "WorkOrders"        WHERE "WorkOrderId" LIKE 'E2E-%'
UNION ALL SELECT 'WorkOrderMaterials',count(*) FROM "WorkOrderMaterials" WHERE "WorkOrderId" LIKE 'E2E-%'
UNION ALL SELECT 'Dispatches',       count(*) FROM "Dispatches"        WHERE "DispatchId"  LIKE 'E2E-%'
UNION ALL SELECT 'DispatchMaterials',count(*) FROM "DispatchMaterials" WHERE "DispatchId"  LIKE 'E2E-%'
UNION ALL SELECT 'AccountingPeriods',count(*) FROM "AccountingPeriods"
UNION ALL SELECT 'Plants(E2E-P2)',   count(*) FROM "Plants"            WHERE "Id"          = 'E2E-P2'
UNION ALL SELECT 'OutsourcePOs',     count(*) FROM "OutsourcePurchaseOrders" WHERE "OutsourcePurchaseOrderId" LIKE 'E2E-%'
UNION ALL SELECT 'OutsourcePOLines', count(*) FROM "OutsourcePurchaseOrderLines" WHERE "OutsourcePurchaseOrderId" LIKE 'E2E-%'
UNION ALL SELECT 'GoodsReceipts',    count(*) FROM "GoodsReceipts"     WHERE "GoodsReceiptId" LIKE 'E2E-%'
UNION ALL SELECT 'GoodsReceiptLines',count(*) FROM "GoodsReceiptLines" WHERE "GoodsReceiptId" LIKE 'E2E-%'
UNION ALL SELECT 'BankAccounts',     count(*) FROM "BankAccounts"      WHERE "AccountId"   LIKE 'E2E-%'
UNION ALL SELECT 'MonthEndChecklists',count(*) FROM "MonthEndChecklists" WHERE "PlantId"   = 'P1'
UNION ALL SELECT 'ChecklistItems',   count(*) FROM "ChecklistItems"    WHERE "Id" BETWEEN 950001 AND 950010
UNION ALL SELECT 'ReportDefs(E2E)',  count(*) FROM "ReportDefinitions" WHERE "Id" = 950001
UNION ALL SELECT 'ReportRuns(E2E)',  count(*) FROM "ReportRuns"        WHERE "RunId" BETWEEN 950001 AND 950003
UNION ALL SELECT 'ApprovalTemplate(E2E)', count(*) FROM "ApprovalTemplates" WHERE "Id" LIKE 'e2e%'
UNION ALL SELECT 'ApprovalSteps(E2E)',    count(*) FROM "ApprovalSteps"     WHERE "Id" LIKE 'e2e%'
UNION ALL SELECT 'DebitNotes(E2E draft)',  count(*) FROM "APDebitNotes"  WHERE "APDebitNoteId"  = 'E2E-DN01' AND "Status" = 0
UNION ALL SELECT 'CreditNotes(E2E draft)', count(*) FROM "ARCreditNotes" WHERE "ARCreditNoteId" = 'E2E-CN01' AND "Status" = 0
UNION ALL SELECT 'ReadOnlyUser(E2E-RO)',  count(*) FROM "AspNetUserRoles"   WHERE "RoleId" = 'E2E-RO'
UNION ALL SELECT 'ManualAccounts(E2E)',   count(*) FROM "ChartOfAccounts"   WHERE "AccountCode" LIKE 'E2E-%' AND "AllowManualEntry"
UNION ALL SELECT 'MobilePerms(Admin)',    count(*) FROM "MobileRolePermissions" WHERE "RoleId" = '1' AND "IsAllowed"
ORDER BY 1;
