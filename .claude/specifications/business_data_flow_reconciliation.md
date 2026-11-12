# SmartFactory MES 業務資料流與資料一致性檢核規格書

本規格書定義了 SmartFactory MES 系統中，採購付款（P2P）、銷售收款（O2C）、自製與委外生產等三大核心業務資料流的傳遞路徑、各節點資料一致性檢核點（Reconciliation Points），以及外部 ERP 憑證同步對應與狀態檢核機制，並提供用於日後資料稽核程式（Audit Diagnostic Scripts）的邏輯與 SQL 查詢，以防範系統運行中出現「帳實不符」或「資料斷層」等異常。

---

## 1. 核心業務資料流與檢核節點

### 1.1 採購付款流程（P2P, Procure-to-Pay）

#### A. 資料流向路徑
```
請購單 (PurchaseRequest) ──► 採購單 (PurchaseOrder) ──► 進貨收料單 (GoodsReceipt) ──► 庫存異動 (StockMovement) ──► 暫估應付帳款 (GR/IR) ──► AP 發票 (APInvoice) ──► 付款傳票 (APPayment) ──► 日記帳登帳 (JournalEntry)
```

#### B. 檢核點與稽核規則
1. **採購與收料數量一致性（PO vs GR）**：
   * **規則**：對每一筆採購單明細（`PurchaseOrderLine`），累計已收料數量不可大於採購單數量。
   * **公式**：`PurchaseOrderLine.OrderQty >= 累計已核准 GoodsReceiptLine.ReceiptQty`。
2. **暫估應付帳款平衡（GR/IR Reconciliation）**：
   * **規則**：收料後尚未收到發票前，系統產生的「暫估應付帳款（GR/IR）」餘額，必須等於進貨單未核銷金額。
   * **公式**：`暫估應付帳款餘額 = 累計進貨暫估金額 (GoodsReceiptLine.ReceiptQty × PurchaseOrderLine.UnitPrice) - 累計發票核銷金額 (APInvoiceLine.Qty × APInvoiceLine.UnitPrice) - 已認列進料差異 (PPV)`。
3. **發票與付款結清平衡（AP vs Payment）**：
   * **規則**：發票一旦標記為已結清（Paid），其累計沖銷金額必須等於發票總金額。
   * **公式**：`APInvoice.TotalAmount = 累計付款 (APPaymentAllocation.AllocatedAmount) + 累計預付沖銷 (Prepayment.Amount) + 累計折讓扣減 (CreditNote.Amount)`。

---

### 1.2 銷售收款流程（O2C, Order-to-Cash）

#### A. 資料流向路徑
```
銷售報價/訂單 (SalesOrder) ──► 銷售出貨單 (GoodsShipment) ──► 庫存異動 (StockMovement) ──► AR 發票 (ARInvoice) ──► 收款傳票 (ARReceipt) ──► 日記帳登帳 (JournalEntry)
```

#### B. 檢核點與稽核規則
1. **訂單與出貨數量一致性（SO vs GS）**：
   * **規則**：出貨數量不得超出訂單承諾數量。
   * **公式**：`SalesOrderLine.OrderQty >= 累計已核准 GoodsShipmentLine.ShipQty`。
2. **出貨與應收發票一致性（GS vs AR）**：
   * **規則**：出貨確認後始得認列應收帳款，發票開立數量應等於出貨確認數量。
   * **公式**：`GoodsShipmentLine.ShipQty = ARInvoiceLine.Qty + 未開票出貨數量`。
3. **存貨扣減與銷貨成本一致性（INV vs COGS）**：
   * **規則**：出貨庫存過帳時扣減的庫存金額，必須與日記帳中「銷貨成本（COGS）」的借方借記金額完全相符。
   * **公式**：`SUM(StockMovement.TotalCost) = JournalLine.Debit(COGS 科目)`。

---

### 1.3 生產與委外流程（Plan-to-Produce）

#### A. 自製生產資料流向路徑
```
MRP 需求 ──► 工單 (WO) ──► 備料單 (MaterialSlip/MI Draft) ──► [WH 揀料確認] ──► 過帳/Issue (Posted) ──► 庫存異動 (StockMovement) ──► 報工 (Session) ──► 完工入庫 (OI) ──► 實際成本計算 ──► 日記帳結轉 (Journal)
```

> **M2 架構說明（mfg-domain.md §17.3）**：工單核准時系統自動建立 `MaterialSlip(Category=InternalIssue, Status=Draft)`；經 Approved → Picking → Confirmed → Issue 後，`IssuedQty` 回寫至 `WorkOrderMaterial`，並觸發 `StockMovement` 過帳。

#### B. 委外生產資料流向路徑
```
委外工單 ──► 委外採購單 (OPO) ──► 委外發料 (OO) ──► 委外收成品 (OR) ──► 庫存入庫 (OI) ──► 暫估加工費 (GR/IR) ──► 加工費發票 (AP) ──► 日記帳 (Journal)
```

#### C. 檢核點與稽核規則
1. **自製工單投入產出平衡（WIP Reconciliation）**：
   * **規則**：工單期末在製品（WIP）金額必須等於投入與轉出之淨額。
   * **公式**：`期末 WIP = 期初 WIP + 本期發料淨值 (SUM(WorkOrderMaterial.IssuedQty - WorkOrderMaterial.ReturnedQty) × 成本) + 本期報工人工與製費 (ProductionSession) - 本期完工入庫成本 - 本期工單報廢`。
   * **資料來源**：`WorkOrderMaterial.IssuedQty` 由 `MaterialSlip.Issue()` 過帳後依 `SourceLineId = "{OperationSeq}_{BomSeq}"` 回寫（§17.3 M2 架構）。
2. **委外商在外原料平衡（Subcontractor Material Control）**：
   * **規則**：委外商在外原料為**推導量**（系統無委外虛擬倉 Inventory 列，2026-07-05 依實作修正）：淨發料扣除完工理論折算後不得為負；為負即超產或發退料資料錯誤。
   * **公式**：`在外餘量 = 累計發料 (OO Posted 出庫 StockMovement) − 累計退料 (OR Posted 入庫 StockMovement) − 理論消耗 (委外收貨 StockQty × RoutingBom 每單位用量)`；`在外餘量 ≥ 0`。超耗部分由 `OutsourceScrapPenaltyService` 依 §5.2 另行計罰，不重複列入平衡式。
   * **稽核落地**：§3.3 `AuditOutsourceBalanceAsync`。
3. **加工費核銷平衡（OPO vs AP）**：
   * **規則**：核銷之加工費總額需與實際完工入庫的加工費相符。
   * **公式**：`累計完工入庫加工費 (OutsourceReceiptLine.ReceiptQty × OutsourceReceiptLine.UnitCost) = 累計加工費發票金額 (APInvoiceLine.LineAmount) + 暫估加工費餘額 (GR/IR Outsource)`。

---

## 2. 外部憑證對應與拋轉狀態（ERP Voucher Mapping Status）檢核機制

中小型製造業通常將 MES 作為現場執行與物料成本計算系統，而總帳、稅務申報則在外部 ERP 系統（例如鼎新、SAP）中完成。兩套系統間的資料傳遞需透過整合轉拋機制。

### 2.1 資料庫設計擴充
所有需要拋轉至 ERP 的實體（如 `JournalEntry`、`APInvoice`、`ARInvoice`、`StockMovement`）必須實作 `IErpSyncEntity` 介面。

> **實作範圍現況（2026-07-06 更新，D3 最小範圍完成）**：`APInvoice` 與 `ARInvoice` 實作拋轉欄位（介面與 enum 定義於 `Models/BaseEntity.cs`；FIN 模組另有 `IErpSyncDocument : IErpSyncEntity` 補 `TotalAmount`/`UpdatedAt` 供泛型化，Core 介面不動）。
> `ErpSyncService` 已泛型化（`MarkSyncedAsync<T>` 等，`where T : IErpSyncDocument`；原 AP 簽章保留為相容包裝）並註冊 DI；§3.5 滯留稽核涵蓋 AP＋AR。
> `JournalEntry` / `StockMovement` 與 P-4（ERP 介接方式）綁定後再設計（見 `reconciliation-decisions.md` D3）。

```csharp
public interface IErpSyncEntity
{
    ErpSyncStatus ErpSyncStatus { get; set; } // Pending / Synced / Failed / Excluded
    string? ErpVoucherNo { get; set; }         // ERP 回寫的傳票/單據號碼
    DateTime? ErpSyncTime { get; set; }        // 拋轉時間
    string? ErpErrorMessage { get; set; }      // 拋轉失敗錯誤訊息
    int ErpRetryCount { get; set; }            // 重試次數
}
```

### 2.2 整合流程與檢核點（ERP Integration Checkpoints）
> ⚠️ **拋轉範圍的現況（2026-08-21 查證）**：下列兩條檢核寫的是「財務與庫存交易」，
> 但**實際具備拋轉狀態的只有 AP／AR 發票兩類**——`IErpSyncDocument` 全專案僅由
> `Modules/FIN/Models/APInvoice.cs` 與 `ARInvoice.cs` 實作，`ErpSyncService` 的逾時查詢
> 也只有 `GetPendingOverdueAsync`（AP）與 `GetPendingOverdueArAsync`（AR）兩支。
> **`StockMovement` 等庫存交易沒有 `ErpSyncStatus`／`ErpVoucherNo` 欄位，不在拋轉範圍內。**
> 下文照原樣保留，因為它是目標規格；要判斷「現在會不會拋」以本框為準。

1. **單據完整性檢核（No Missing Documents）**：
   * **規則**：MES 端所有已確認（Confirmed）且非排除（Excluded）的財務與庫存交易，必須在設定的時限內（如 24 小時內）成功拋轉至 ERP 並取得 `ErpVoucherNo`。
   * **公式**：`已確認單據總數 = 已成功拋轉數 (ErpSyncStatus = Synced) + 例外排除數 (Excluded)`。
2. **金額雙向核對（Bi-directional Amount Verification）**：
   * **規則**：拋轉成功後，MES 單據總金額必須與 ERP 端對應單據或傳票的金額完全一致，防範因兩系統匯率精度、稅額計算或小數點進位規則差異造成的財務落差。
   * **公式**：`MES.TotalAmount = ERP.VoucherAmount`。

---

## 3. 資料稽核診斷程式邏輯（DataAuditService）

> **2026-07-05 改版**：本章原為 T-SQL 稽核腳本，存在四類缺陷——(1) T-SQL 方言（`ISNULL`/`GETDATE`/`DATEADD`）無法在本系統 PostgreSQL 執行；(2) 未過濾草稿/作廢單據與作廢發票；(3) §3.3 漏計退料與超耗、假設不存在的 `SupplierCode + '_WH'` 虛擬倉慣例、統計基準誤用 Confirmed（實際庫存效果在 Posted）；(4) §3.4 寫死與實作不符的科目 `121301`。
> 現已**全部改以 EF Core LINQ 實作於 `Modules/ACT/Services/DataAuditService.cs`**（介面 `IDataAuditService`，ACTModule 註冊 DI），本章記錄各檢核的規則與修正後邏輯。單元測試：`SmartFactory.MES.Tests/ACT/DataAuditServiceTests.cs`。

**共通原則**（全部檢核一體適用）：
- 所有查詢 `IgnoreQueryFilters()` + 明確 `plantId` 參數（RPT 慣例）；`plantId = null` 稽核全廠。
- 容忍值（§9 P-7，2026-07-05 起系統參數化，未設定/不合法時退回預設、行為不變）：
  - 數量 `audit_qty_tolerance`（預設 `0.0001` = NUMERIC(18,4) 最小單位）
  - 金額 `audit_amount_tolerance`（預設 `0.01` = 本位幣最小精度；ERP 拋轉尾差 `ErpSyncService` 同用此參數，§5.4）
  - AP/AR 核銷尾差 `fin_settle_tolerance`（預設 `0.001`，§5.6/§5.15；**刻意緊於金額容忍值一個量級**——核銷結清判定過鬆會把真差額誤結清）
- 單據狀態：草稿（Draft）與作廢（Void）一律排除；作廢發票（`APInvoiceStatus.Void` / `ARInvoiceStatus.Void`）一律排除。
- 回傳 `AuditFailureDetail`（AuditType / DocId / ProductCode / PlantId / DifferenceAmount / ErrorDescription）。

### 3.1 採購超收稽核（`AuditPoOverReceiptAsync` → `PO_OVER_RECEIPT`）
* **規則**：對每一 PO 明細，累計**有效**收料（非草稿/作廢 GR 的 `ReceiptQty − ReturnedQty`）不得大於 `OrderQty`。
* **修正**：原腳本未 join GR 主檔過濾狀態，草稿/作廢收料會誤報；且未扣退貨。

### 3.2 GR/IR 稽核（`AuditGrIrAsync` → `GRIR_OVER_INVOICE` / `GRIR_AGING`）
* **溢開規則**：以**數量**判定——非作廢發票累計 `Qty` 大於 GR 收料數量即異常（金額差是合法 PPV，不以金額判溢開，見 §5.11）。
* **掛帳超期規則**：收料超過 30 天，暫估金額（Σ GR LineAmount）仍未被非作廢發票核銷完，列 `GRIR_AGING`。
* **修正**：原腳本未排除作廢發票（與 §5.9「作廢 → 未結餘額歸零」矛盾）、以金額判溢開會誤傷 PPV。

### 3.3 委外在外原料平衡稽核（`AuditOutsourceBalanceAsync` → `OUTSOURCE_NEG_BALANCE`）
* **規則**（對齊 `OutsourceScrapPenaltyService` 實作模型——委外商在外原料是**推導量**，非虛擬倉 Inventory 列）：
  ```
  在外餘量(工單,原料) = 發料(OO Posted 出庫 StockMovement)
                      − 退料(OR Posted 入庫 StockMovement)
                      − 理論消耗(Confirmed/Posted 委外收貨 StockQty × RoutingBom 每單位用量，經 OPO 明細 SourceWorkOrderId 歸屬工單)
  在外餘量 < −容忍值 → 異常（超產或發退料資料錯誤，對齊 §5.2 Invalid EP）
  ```
* **修正**：原腳本 (1) 漏計退料與超耗；(2) 假設 `SupplierCode + '_WH'` 儲位慣例（程式碼中不存在）；(3) 統計 Confirmed 但庫存效果在 Posted；(4) `RequiredQty / TargetQty` 除零風險；(5) `LEFT JOIN` 後 NULL 比較漏報。

### 3.4 傳票借貸平衡與重複立帳稽核（`AuditJournalBalanceAsync` / `AuditDuplicateJournalAsync`）
* **借貸平衡（`JOURNAL_UNBALANCED`）**：每張 `JournalEntry` 的 ΣDebit 與 ΣCredit 差額不得超過金額容忍值。
* **重複立帳（`JOURNAL_DUPLICATE`）**：一對一來源（`APPayment` / `ARReceipt` / `WorkOrderCompletion` / `LandedCostAllocation`）同一 `SourceDocId` 不得產生多張自動傳票（並發雙重過帳的事後偵測網）；`"INV"` 完整模式逐行過帳可多張，不列入。
* **修正**：原「工單實際成本 vs 總帳」腳本寫死假設科目 `121301`，且實作上工單成本鏈並無單一對應傳票（人工/製費吸收走 `WorkOrderCompletion`、材料走 `"INV"`、期末重估走 Adjustment），無法 1:1 對帳——改為上述兩個結構性檢核；逐工單成本回溯核對列 §9 規劃 P-2。

### 3.5 ERP 拋轉滯留稽核（`AuditErpSyncLagAsync` → `ERP_SYNC_LAG`）
* **規則**：非作廢 `APInvoice` / `ARInvoice` 建立超過 24 小時仍為 `Pending` / `Failed`（enum 比對，非字串）即異常；描述含重試次數與最後錯誤訊息。
* **範圍**：AP＋AR（AR 為 2026-07-06 D3 最小範圍擴大）；`JournalEntry`/`StockMovement` 綁 P-4；金額雙向核對需 ERP 端介接（§9 規劃 P-4）。

### 3.6 庫存滾動平衡稽核（`AuditInventoryRollForwardAsync` → `INV_ROLLFORWARD`）【盲區補強】
* **規則**：`Σ(StockMovement.StockQty × Direction)` 依（廠別、品號、儲位、批號）彙總，必須等於 `Inventory.QtyOnHand`；任一側缺列而另一側非零亦屬異常。
* **說明**：`StockQty` 恆正、`Direction` ±1 帶號；作廢單以反向 movement 沖銷，故直接淨額加總即可。直接 DB 灌庫存（無 movement）會被列異常——這正是本檢核目的。

### 3.7 銷售出貨稽核（`AuditSoOverShipmentAsync` / `AuditShipmentInvoiceGapAsync`）【盲區補強】
* **超出貨（`SO_OVER_SHIPMENT`）**：非草稿/作廢 GS 的累計 `ShipQty − ReturnedQty` 不得大於 SO 明細 `OrderQty`（§1.2 規則 1 的稽核落地）。
* **出貨未開票（`GS_NO_AR_INVOICE`）**：Confirmed/Posted 出貨單出貨日超過 1 天仍無非作廢 AR 發票（`SourceDocType = "GoodsShipment"`）即異常（§1.2 規則 2 的稽核落地）。

### 3.8 Backflush 後沖消耗核對（`AuditBackflushConsumptionAsync` → `BACKFLUSH_MISMATCH`）【P-5，2026-07-05】
* **規則**：對每張**已入庫（Closed）**工單的 Backflush + AtCompletion 用料（跨工序同料聚合），
  ```
  實際後沖 = Σ IO StockMovement 淨額（DocNo = 工單號 + "-BF"，消耗取正值）
  標準後沖 = Σ RequiredQty × (完工量 / 工單量)     ── 公式對齊 CalcBackflushQty
  完工量   = IT 工單繳庫 StockMovement 淨額（DocNo = 工單號）
  |實際 − 標準| > 數量容忍值 → 異常
  ```
* **範圍限制**：`BackflushTiming.PerReport`（按報工比例分次沖）目前無過帳落點（僅 AtCompletion 於 `ProductionCompletionService` 實作），未納入本檢核；PerReport 實作時須同步擴充。

### 3.9 反正規化欄位漂移稽核（`AuditPoReceivedQtyDriftAsync` / `AuditSoShippedQtyDriftAsync`）【P-6，2026-07-05】
* **回寫語意**（實作查核結論，決定期望值定義）：
  - `PurchaseOrderLine.ReceivedQty`：GR **建立/修改時累加（含草稿）**、刪除/作廢時回沖；**退貨不回沖**（退貨記於 `GoodsReceiptLine.ReturnedQty`）。
  - `SalesOrderLine.ShippedQty`：GS 儲存時累加（含草稿）、修改/刪除/作廢時回沖；**銷退不回沖**（銷退記於 `GoodsShipmentLine.ReturnedQty`）。
* **規則**：
  - `PO_RECEIVED_DRIFT`：`PurchaseOrderLine.ReceivedQty` ≠ 非作廢 GR 明細 `ReceiptQty` 彙總（含指向不存在 PO 明細的孤兒回寫）。
  - `SO_SHIPPED_DRIFT`：`SalesOrderLine.ShippedQty` ≠ 非作廢 GS 明細 `ShipQty` 彙總。
* **同步修正的三處作廢回沖破口**（原漂移根因；出貨單作廢原本就正確回沖 `ShippedQty`，其餘三處漏了）：
  1. `GoodsReceiptController.Void` 未回沖 `PO.ReceivedQty` → 作廢後 PO 額度永久占用，且與 §3.1「作廢不計」矛盾。
  2. `GoodsReturnController.Void` 未回沖 `GRL.ReturnedQty` → §3.1 有效收料被永久低估。
  3. `SalesReturnController.Void` 未回沖 `GSL.ReturnedQty` → §3.7 有效出貨被永久低估。
* **歷史資料配套**：migration `20260705083922_ACT_DataAuditRunPersistence` 一次性依單據明細重算
  `ReceivedQty` / `GoodsReceiptLine.ReturnedQty` / `ShippedQty` / `GoodsShipmentLine.ReturnedQty`（含草稿、排除作廢），
  否則本檢核上線即擋既有部署的關帳。

### 3.10 WIP 期末快照一致性稽核（`AuditWipSnapshotConsistencyAsync`）【P-1 結構性守門，2026-07-05】
* **僅於關帳前執行**（由 `RunPreClosingAuditAsync` 帶入關帳期間呼叫；快照由期末重估建立，平時不存在故不入 `RunAllAsync`）：
  - `WIP_COST_NOT_FINAL`：期間內完工成本（`WorkOrderActualCost`，`CostDate` 落在期間）仍為 Preliminary → 期末實際費率重估未跑，或重估後又有完工。
  - `WIP_SNAPSHOT_MISSING`：InProgress 工單無本期 `WipSnapshot` → 重估未跑或重估後才投產，需重跑。
  - `WIP_SNAPSHOT_STALE`：本期快照指向已非 InProgress 的工單（重估後完工/作廢）→ 快照過期，需重跑。
  - `WIP_SNAPSHOT_NEGATIVE`：快照材料/人工/製費/在製量任一為負。
* **材料成本語意（D1-A，2026-07-06 已收斂）**：`wo.ActualMaterialCost` 完工時同時彙總後沖料**與 Push/Pull 發料實際成本**（`StockMovement` AVCO，經 `MaterialSlip.SourceDocId=工單` 二跳、依方向淨計），完工品單位成本涵蓋全部材料，存貨計價低估已解；差異回全料 `StandardMaterialCost` 基準（已移除 D1-B 的 `ComputeComparableStandardMaterial`）。
* **跨期 WIP 人工/製費結轉（D4 期初快照結轉法，2026-07-16 已定案實作）**：`ActualRateReestimationService` 兩腿（WIP 快照＋完工回刷）之人工/製費 = 前期 `WipSnapshot` 凍結層（前期實際費率，已關帳不可重述）＋本期工時 × 本期實際費率；無前期快照者期初＝0。原缺口（只取本期工時，跨期完工工單 Final 低估、快照兩腿口徑不一致）已解，見 `reconciliation-decisions.md` D4。
* **金額恆等式（§3.10-5，隨 D4 啟用，2026-07-16）**：`WIP_IDENTITY_DRIFT`——逐廠核 `Σ期末快照 = Σ期初快照 + 本期發料淨值 + 本期部門人工/製費 − 本期完工成本`（§5.8 公式的期間彙總級應用，`WipBalanceCalculator` 同口徑）。各腿獨立來源：發料＝§3.11 材料腿同源加 `MovementDate` 期間切片；人工/製費＝`DepartmentActualCost`；完工＝`WorkOrderActualCost`（CostDate ∈ 期間）。**僅於 §3.10-1~4 結構性守門全數通過時執行**（結構缺陷未清恆等式必失衡）。無獨立報廢腿（成本唯一出口是完工，NG 經完工吸收進良品單位成本）；前期在製工單本期作廢 → 逐筆 `WIP_VOID_LEAKAGE` 並自期初調節。

### 3.11 逐工單成本回溯核對（`AuditWoCostJournalDriftAsync` → `WO_COST_JOURNAL_DRIFT`）【P-2 選項 A，2026-07-06】
* **口徑**：Preliminary/Final 皆核（`reconciliation-decisions.md` D2 選項 A；`JournalLine.SourceRef` 已上線，migration `ACT_JournalLineSourceRef`）。
* **規則**（對每筆 `WorkOrderActualCost`）：
  - **材料腿**（不分 Preliminary/Final，重估不動 `ActualMaterial`）：
    `ActualMaterial` vs **庫存異動子帳**淨成本——後沖（DocNo=工單號+"-BF"）＋ Push/Pull 發退料單
    （`MaterialSlip.SourceDocId=工單`）的 Production movement，依方向淨計（退料抵減）。
    生產物料異動走 `PostDocSimplifiedAsync`、不落日記帳，故核子帳而非 JE（D1-A）。
  - **人工/製費腿**（Preliminary/Final 皆核）：`ActualLabor + ActualOverhead` vs 吸收科目
    （5110 人工-已分配 / 5120 製費-已分配）淨額（貸−借），歸集兩條來源——完工吸收分錄
    `JE("WorkOrderCompletion", 工單號)` ＋期末重估調整逐工單拆行（`JournalLine.SourceRef=工單號`）。
    Final 記錄的 Actual 已含重估差額，故連同調整行才對得起來。
* **D2-A（逐工單維度）**：`ActualRateReestimationService` 的重估調整分錄改**逐工單拆行**、行帶 `SourceRef=工單號`（表頭 `SourceDocId` 仍為期間 ID）；`JournalLine.SourceRef`（MaxLength 50）供本稽核歸集，亦為未來成本中心維度的共用欄位。
* **納入 `RunAllAsync`**（日常排程即檢，非僅關帳）。

---

## 4. C# 資料一致性診斷服務（DataAuditService）

> **2026-07-05 起已實作**：`Modules/ACT/Services/DataAuditService.cs`（介面 `IDataAuditService`，`ACTModule` 註冊）。
> 本節原為設計稿，現記錄實際落地形態。

```csharp
public interface IDataAuditService
{
    // 月結前稽核：執行 §3 全部檢核，任何異常即不通過（由 ClosePeriodAsync 呼叫擋關帳）
    Task<DataAuditResult> RunPreClosingAuditAsync(string periodId, CancellationToken ct = default);
    // 日常診斷：可指定廠別（null = 全廠）
    Task<DataAuditResult> RunAllAsync(string? plantId = null, CancellationToken ct = default);
    // §3.1–§3.7 各檢核亦可單獨呼叫（AuditPoOverReceiptAsync、AuditGrIrAsync、…）
}

public class DataAuditResult
{
    public bool IsPassed => Failures.Count == 0;
    public IList<AuditFailureDetail> Failures { get; }
}

public class AuditFailureDetail
{
    public string AuditType { get; set; }          // 例如 "PO_OVER_RECEIPT", "GRIR_AGING", "INV_ROLLFORWARD"
    public string DocId { get; set; }              // 異常單據號或聚合鍵
    public string? ProductCode { get; set; }       // 異常品號（無品號維度為 null）
    public string? PlantId { get; set; }           // 異常所屬廠別
    public decimal DifferenceAmount { get; set; }  // 一致性差額（數量或金額）
    public string ErrorDescription { get; set; }   // 含期望值與實際值
}
```

### 4.1 期間鎖接入（關帳擋關）

`AccountingPeriodService.ClosePeriodAsync` 在狀態檢查（必須為 Closing）之後、寫入 Closed 之前呼叫
`RunPreClosingAuditAsync`；任何異常即拋 `InvalidOperationException`（含異常筆數與首筆明細），期間停留在 Closing，
待異常處理完畢方可關帳。

- **注入方式**：`IDataAuditService?` 為選配建構子參數——未註冊（或測試直接 `new AccountingPeriodService(db)`）時跳過稽核，維持原行為；DI 解析的正式路徑必然帶稽核。
- **與月結 Checklist 的關係**：`MonthEndService.CompleteStepAsync` 最終呼叫同一 `ClosePeriodAsync`，故 Checklist 流程同樣被擋。
- **範圍**：`RunPreClosingAuditAsync` = `RunAllAsync`（§3.1–§3.9＋§3.11 全部檢核）+ §3.10 WIP 期末快照一致性（帶關帳期間，僅此路徑執行）。

### 4.2 每日排程與稽核儀表板（§9 P-8，2026-07-05）

異常不再等月結才發現——稽核結果持久化 + 每日排程 + RPT 診斷畫面：

| 元件 | 位置 | 說明 |
|------|------|------|
| 持久化 | `DataAuditRun` / `DataAuditFinding`（`Modules/ACT/Models/DataAuditRun.cs`） | 每次執行一筆 Run（觸發方式/耗時/異常數）＋異常明細（`AuditFailureDetail` 一對一對應）；保留 90 天，逾期由下次執行清除 |
| 執行服務 | `IDataAuditRunService` / `DataAuditRunService`（ACT） | 包 `RunAllAsync` → 寫入 Run/Findings → 清舊；排程與畫面手動觸發共用 |
| 每日排程 | `DailyDataAuditJobService`（ACT，`BackgroundService`，/new-job 規格） | 每天 02:00（伺服器當地時區）全廠稽核（`plantId = null`），`CreatedBy = "SYSTEM"`；異常記 Warning log |
| 診斷畫面 | `RPT/DataAuditController`（MenuKey `DataAudit`，/new-analytics 規格） | KPI 卡＋近 30 次異常趨勢（Chart.js）＋異常明細表（稽核類型篩選）＋「立即執行稽核」（需異動權限）；明細僅呈現目前廠別＋無廠別維度的異常 |

測試：`ACT/DataAuditRunServiceTests.cs`（持久化/異常寫入/保留期清除）。

---

## 5. 資料流等價劃分與邊界值測試設計（QA Test Cases）

為了在開發階段主動防範資料邏輯錯誤，針對四大高風險資料流場景設計等價劃分（Equivalence Partitioning, EP）與邊界值分析（Boundary Value Analysis, BVA）測試案例：

### 5.1 採購進貨與 AP 發票核銷資料流

#### A. 等價劃分（EP）
* **有效等價類（Valid）**：
  * `GoodsReceiptLine.ReceiptQty = APInvoiceLine.Qty`：進貨數量與發票數量一致，暫估全額核銷。
  * `GoodsReceiptLine.ReceiptQty > APInvoiceLine.Qty`：發票分批送達，部分核銷，餘額繼續掛暫估（GR/IR）。
  * `GoodsReceiptLine.ReceiptQty < APInvoiceLine.Qty`：發票預開或溢開（系統可配置為允許「預開」，但須產生警示並記錄核銷異常）。
* **無效等價類（Invalid）**：
  * `APInvoiceLine.Qty > PurchaseOrderLine.OrderQty`：發票累計數量大於採購單數量，系統應拋出異常並拒絕過帳。
  * `APInvoiceLine.UnitPrice <= 0` 或 `APInvoiceLine.Qty <= 0`：發票單價或數量輸入負數或零值（除了扣款/折讓明細外，主明細應拒絕）。

#### B. 邊界值分析（BVA）
* **核銷臨界點 (0%)**：`APInvoiceLine.Qty = 0`，驗證系統不產生任何核銷紀錄與傳票。
* **核銷臨界點 (100%)**：最後一筆核銷數量剛好將採購單數量扣減至 0，檢查 `APInvoice` 狀態是否自動變更為 `Paid`，且 GR/IR 科目餘額精確歸零。
* **單價微差臨界值**：`APInvoiceLine.UnitPrice = PurchaseOrderLine.UnitPrice +/- 0.01`，測試「進料差異（PPV）」計算，並確認借貸分錄金額精確至小數點後 2 位，不產生任何浮點數精度丟失（Rounding Error）。

---

### 5.2 委外收貨與超耗扣款資料流

#### A. 等價劃分（EP）
* **有效等價類（Valid）**：
  * `實際損耗率 <= 合約允收損耗率`：未超出損耗上限，超耗扣款金額 `ExcessScrapPenalty = 0`。
  * `實際損耗率 > 合約允收損耗率`：超出損耗上限，系統自動按公式計算扣款金額 `ExcessScrapPenalty > 0`。
  * `實際損耗率 = 100%`：發出的原料全部損耗，成品實收為 0。
* **無效等價類（Invalid）**：
  * `實際損耗率 < 0%`：成品完工折算之原料量大於實際發出原料量（超產或數據錯誤），系統應判定過帳失敗並拋出異常。

#### B. 邊界值分析（BVA）
* **允收臨界點**：`實際損耗率 = 合約允收損耗率`，驗證扣款金額計算結果剛好為 0。
* **超耗臨界點**：`實際損耗率 = 合約允收損耗率 + 0.0001%`，驗證扣款計算精度與四捨五入邏輯。
* **扣款超額臨界點**：`ExcessScrapPenalty > 應付加工費總額`，即超耗金額大於本單加工費。系統需驗證此時 AP Invoice 是否能產生負項明細以扣減後續付款，或自動將超出部分拆分為「加工費 0 元 + 委外商賠償應付款（AR）」。

---

### 5.3 庫存平均成本（AVCO）計算與異動資料流

#### A. 等價劃分（EP）
* **有效等價類（Valid）**：
  * `期初庫存 > 0` 且 `入庫數量 > 0`：正常按比例滾算新 AVCO 均價。
  * `期初庫存 = 0` 且 `入庫數量 > 0`：新 AVCO 均價直接等於本次入庫單價。
* **無效等價類（Invalid）**：
  * `入庫單價 < 0` 或 `入庫數量 < 0`：入庫單價或數量為負（退料應使用 `Return` 類型交易，而非負入庫），系統應攔截。

#### B. 邊界值分析（BVA）
* **庫存歸零臨界點**：出庫剛好使庫存總量為 0。驗證 `Inventory.UnitCost` 應保留最後一次的均價，不可因除以 0 導致 `DivideByZeroException`，亦不可被清空為 0。
* **負庫存臨界點**：出庫量大於在庫量，導致庫存量變為负數。系統在後續入庫補回時，必須驗證是否能精確補回「暫估負存貨成本」，避免後續 AVCO 產生扭曲。
* **極端微小值**：入庫數量為 `0.0001`，入庫單價為 `0.000001`，驗證系統在極低單價與數量下的乘除運算是否會被截斷為 0，或因小數溢出導致成本異常。

---

### 5.4 ERP 憑證拋轉與金額校對資料流

#### A. 等價劃分（EP）
* **有效等價類（Valid）**：
  * `ErpSyncStatus = Pending -> Synced`：單次拋轉成功並取得 ERP 傳票號。
  * `ErpSyncStatus = Pending -> Failed -> Synced`：網路或 ERP 鎖定導致暫時失敗，經由重試後成功同步。
* **無效等價類（Invalid）**：
  * `ErpSyncStatus = Failed` 且 `ErpRetryCount > 最大重試次數`：系統判定為永久同步失敗，停止自動重試，將單據鎖定並發送系統警報郵件。

#### B. 邊界值分析（BVA）
* **進位尾差臨界值**：`MES 總金額 = ERP 傳票總金額 +/- 0.01`。驗證系統是否能自動將 0.01 元的精度進位尾差歸入「尾差調整（Rounding Difference）」科目，使傳票成功過帳，而非判定為金額不符而卡住同步。
* **重試次數臨界點**：`ErpRetryCount = 3`（假設上限為 3 次）。第 3 次失敗時，驗證狀態是否精確轉為 `Failed` 並觸發異常通知。

---

### 5.5 銷售出貨與 AR 發票生成一致性（GS → AR）

#### A. 等價劃分（EP）
* **有效等價類（Valid）**：
  * `GoodsShipmentLine.ShipQty > 0`（單行）：出貨數量、金額與稅額正確轉寫至 `ARInvoiceLine`，`TotalAmount = Σ(LineAmount + TaxAmount)`。
  * `GoodsShipmentLine` 多行：每行各自計算，`ARInvoice.TotalAmount = Σ 各行` 加總，不遺漏。
  * 冪等呼叫（Idempotent）：同一出貨單重複呼叫 `GenerateFromShipmentAsync` → 回傳已存在的同一張 AR 發票，不重複建立。
* **無效等價類（Invalid）**：
  * `ARInvoiceLine.Qty ≤ 0` 或 `ARInvoiceLine.UnitPrice < 0`（手動開立）：主明細輸入負數或零值應拒絕過帳，拋出 `InvalidOperationException`。

#### B. 邊界值分析（BVA）
* **稅額精度臨界點**：`TaxAmount = LineAmount × TaxRate / 100`，結果四捨五入至小數第 4 位（AwayFromZero），驗證不截斷（例：1 個 × 1 元 × 5% = 0.05，不歸零）。
* **小額稅額臨界值**：`LineAmount = 1`，`TaxRate = 5%` → `TaxAmount = 0.05`，驗證 `TotalAmount = 1.05` 精確無截斷。

---

### 5.6 AR 收款核銷平衡（AR vs Receipt）

#### A. 等價劃分（EP）
* **有效等價類（Valid）**：
  * `AllocatedAmount < ARInvoice.TotalAmount`：部分收款，`ARInvoice.Status = PartialReceived`，`ReceivedAmount` 精確累加。
  * `AllocatedAmount = ARInvoice.TotalAmount`（含 0.001 容忍）：全額收款，`ARInvoice.Status = Received`。
* **無效等價類（Invalid）**：
  * `AllocatedAmount > ARInvoice.TotalAmount − ReceivedAmount`：核銷金額超出未結餘額，`ConfirmARReceiptAsync` 應拋出 `InvalidOperationException`。

#### B. 邊界值分析（BVA）
* **全額臨界點（容忍）**：`AllocatedAmount = TotalAmount − 0.001`（在系統 0.001 容忍範圍內）→ `Status` 自動轉為 `Received`。
* **反沖邊界點**：`VoidARReceiptAsync` 已確認收款單 → `ReceivedAmount` 精確歸還為 `0`，`Status` 還原至 `Open`。

---

### 5.7 工單標準成本計算（WorkOrderCostCalculator EP/BVA 補充）

#### A. 等價劃分（EP）
* **有效等價類（Valid）**：
  * 多工作中心：各工作中心各自按費率計算後加總，費率互不干擾。
  * 材料成本與數量無關：`RequiredQty` 為整批需求，`Material = Σ(RequiredQty × UnitCost)` 不受 `quantity` 影響。

#### B. 邊界值分析（BVA）
* **零產量邊界點（quantity = 0）**：`SetupTime` 仍計費（固定換線成本），`UnitTime × 0 = 0`。材料成本保持整批 `RequiredQty × UnitCost`，不因零產量而歸零。
* **秒換算精度**：`TimeUnit = Second`，工時 = 原始秒數 ÷ 3600，驗證不截斷。例：360 秒 × 10 件 ÷ 3600 = 1 小時，費率精確計費。

---

### 5.8 WIP 在製品平衡（WipBalanceCalculator）

**實作位置**：`Modules/MFG/Services/WipBalanceCalculator.cs`（靜態計算器）

**計算公式**：
```
期末 WIP = 期初 WIP + 淨發料金額（發料 − 退料）+ 人工製費 − 完工入庫成本 − 報廢成本
```
**成本差異公式**：
```
總差異 = (實際材料 − 標準材料) + (實際人工 − 標準人工) + (實際製費 − 標準製費)
```

#### A. 等價劃分（EP）
* **有效等價類（Valid）**：
  * 簡單投入/產出（無報廢）：`期末 WIP = 期初 + 發料 − 完工`，驗證基本平衡。
  * 含人工製費與報廢：完整公式五個參數全部計入，期末 WIP 精確計算。
  * 完全完工：完工入庫成本 = 期初 + 發料 + 人工製費 → 期末 WIP = 0。
  * 在途（無完工）：完工入庫 = 0 → 期末 WIP = 期初 + 發料 + 人工製費（資源全在製）。
* **無效等價類（Invalid）**：
  * `淨發料金額 < 0`（退料超過發料）：資料異常，拋出 `ArgumentOutOfRangeException`。

#### B. 邊界值分析（BVA）
* **差異歸零臨界點**：`Actual = Standard`（三項均相等）→ `ComputeTotalVariance = 0`，驗證無浮點誤差。
* **超支差異臨界點**：部分項目超支、部分節省，總差異 = 各項加總（正負相消），精確至整數。

---

### 5.9 採購→進貨累計數量控管與作廢回補（Gap A）

**實作位置**：
- `Services/PurchaseService.cs`：`UpdatePoReceivedQtyAsync` 加入累計超收 guard
- `Modules/FIN/Services/APInvoiceService.cs`：`GenerateFromGoodsReceiptAsync` 改為跨 GR 累計查詢

#### A. 等價劃分（EP）

* **有效等價類（Valid）**：
  * 分批進貨，各批在剩餘可收範圍內（第 1 批 60 + 第 2 批 40 = PO 100）：均允許建立。
  * 刪除草稿 GR → `PO.ReceivedQty` 正確回補，稽核查詢不再偵測到超收異常。
  * 作廢 APInvoice → `GetOutstandingBalanceAsync` 不再納入已作廢發票，未結餘額歸零。
* **無效等價類（Invalid）**：
  * 第 2 批進貨使累計 `ReceivedQty + newQty > OrderQty` → `PurchaseService` 拋出 `InvalidOperationException`，拒絕建單。
  * GR 明細累計已超收時呼叫 `GenerateFromGoodsReceiptAsync` → `APInvoiceService` 亦攔截並拋出例外。

#### B. 邊界值分析（BVA）

* **累計剛好等於訂購量**：`ReceivedQty + newQty = OrderQty`（例：50 + 50 = 100）→ 允許，不拋例外。
* **超過一個最小單位**：`ReceivedQty + newQty = OrderQty + 0.0001`（50 + 50.0001）→ 拒絕，拋例外。

---

### 5.10 GR 確認→StockMovement 過帳數量與成本一致性（Gap B）

**實作位置**：`Modules/INV/Services/InventoryPostingService.cs`（`PostLineDetailAsync`）

**稽核原則**：每一筆 GR Line 過帳後，對應的 `StockMovement` 記錄須滿足：
```
StockMovement.StockQty  = GRLine.StockQty
StockMovement.TotalCost = StockMovement.UnitCost × StockMovement.StockQty
StockMovement.DocNo     = GR.GoodsReceiptId
StockMovement.SourceDocNo = GR.Lines[i].PurchaseOrderId（有 PO 來源時）
```

#### A. 等價劃分（EP）

* **有效等價類（Valid）**：
  * 單行 GR 過帳 → 產生 1 筆 `StockMovement`，`StockQty` 與 `TotalCost` 精確吻合。
  * 多行 GR 過帳 → 逐行產生對應 `StockMovement`，行數與各行數量獨立正確。
  * 含 PO 來源 → `StockMovement.SourceDocNo = PurchaseOrderId`，來源追蹤完整。

#### B. 邊界值分析（BVA）

* **微量進貨（小數精度）**：`StockQty = 0.0001`，`UnitCost = 100.00` → `TotalCost = 0.0100`，精確至 4 位小數不截斷。

---

### 5.11 PO→GR→AP 金額鏈正確性（Gap C）

**稽核原則**：採購金額鏈各節點計算須端到端一致：
```
PO 暫估金額   = PO.UnitPrice × GRLine.ReceiptQty
GR LineAmount  = ReceiptQty × UnitPrice（無稅）
AP TotalAmount = Σ(APInvoiceLine.LineAmount) + Σ(APInvoiceLine.TaxAmount)
PPV            = AP.TotalAmount − PO 暫估金額（正 = 不利差異；負 = 有利差異）
```

#### A. 等價劃分（EP）

* **有效等價類（Valid）**：
  * 無稅、同價：`AP.TotalAmount = GR.TotalAmount = ReceiptQty × UnitPrice`，三層金額完全一致，PPV = 0。
  * 含稅：`AP.TotalAmount = Σ(LineAmount + TaxAmount)`，加總精度不截斷。
  * 廠商報價與 PO 不同（PPV 存在）：`AP.TotalAmount ≠ PO 暫估`，差額 = PPV，精確至 2 位小數。
* **無效等價類（Invalid）**：
  * `APInvoiceLine.LineAmount ≠ Qty × UnitPrice`（外部匯入資料不一致）→ 稽核查詢偵測到金額鏈斷裂。

#### B. 邊界值分析（BVA）

* **精度邊界**：`UnitPrice = 100.001`，`Qty = 1000` → `LineAmount = 100001.0000`（4 位小數精確，不截斷為 100001.00）。
* **稅額精度**：`LineAmount = 1`，`TaxRate = 5%` → `TaxAmount = 0.05`，`TotalAmount = 1.05`（AwayFromZero，不截斷為 0）。

---

### 5.12 庫存模式資料流差異（inv_simplified_mode）

**實作位置**：`Modules/INV/Services/InventoryPostingService.cs`
**切換參數**：系統參數 `inv_simplified_mode`（`true` = 簡易；`false` = 完整）
**切換位置**：`Modules/PUR/Controllers/GoodsReceiptController.cs`（Confirm 動作依參數選擇路徑）

#### 模式行為對照

| 行為 | 簡易模式（`PostDocSimplifiedAsync`） | 完整模式（`PostLineDetailAsync`） |
|------|--------------------------------------|-----------------------------------|
| 觸發時機 | GR Confirm 時一次性過帳全部行 | 倉庫人員逐行呼叫（可分批） |
| StockMovement | 所有行同時產生 | 每次呼叫產生一筆 |
| QtyOnHand | 立即全量更新 | 每次呼叫後累積更新 |
| SetQtyPutAway | 以 `line.Qty` 直接 Set | 以本次過帳量累積 UpdateAsync |
| SetDocPosted | 恆於方法結束後觸發 | 僅 `CheckAllLinesCompleteAsync()` = true 時觸發 |

#### A. 等價劃分（EP）

* **有效等價類（Valid）— 簡易模式**：
  * 單行 GR Confirm → 產生 1 筆 `StockMovement`，`StockQty = line.Qty`，`QtyOnHand` 立即增加。
  * 多行 GR Confirm → 每行產生獨立 `StockMovement`，各品號/儲位相互不影響。
  * 所有行過帳後 → `SetDocPosted` 無條件在方法尾端呼叫。
* **有效等價類（Valid）— 完整模式**：
  * 部分行過帳（`CheckAllLinesCompleteAsync = false`）→ `StockMovement` 正確記錄，`SetDocPosted` 不觸發。
  * 最後一行過帳（`CheckAllLinesCompleteAsync = true`）→ `SetDocPosted` 觸發，單據狀態轉 Posted。
  * 分兩次過帳同品號同儲位 → `QtyOnHand` 累積（第一次 + 第二次）。

#### B. 邊界值分析（BVA）

* **最小數量精度**：簡易模式 `qty = 0.0001`，`UnitPrice = 100` → `TotalCost = 0.0100`，`QtyOnHand = 0.0001`（4 位小數不截斷）。

---

### 5.13 WorkOrder → MaterialSlip 自動建立與 Issue 發料一致性（M2 MFG Gap）

**觸發路徑**（mfg-domain.md §17.3）：
1. **直接核准路徑**：`WorkOrderController.Release()` 無簽核設定時，非委外工單有 Push 料件 → 立即建立 `MaterialSlip(MI, Draft)`。
2. **PendingApproval 路徑**：`WorkOrderApprovalSync.SyncAsync(Approved)` 非委外且有 Push 料件 → 同樣建立 `MaterialSlip(MI, Draft)`。

**狀態機**：`Draft → Approved → Picking → Confirmed → Posted（Issue）`

**回寫規則**：Issue 過帳後 `WorkOrderMaterial.IssuedQty` 以 `ConfirmedQty ?? RequestedQty` 填入；`SourceLineId = "{OperationSeq}_{BomSeq}"` 為反查鍵。

#### A. 等價劃分（EP）
* **有效等價類（Valid）**：
  * 非委外工單核准（直接或 ApprovalSync）且有 Push 料件 → 自動建立 Draft MI MaterialSlip；Lines 與 Push WorkOrderMaterial 一對一，`SourceLineId` 格式正確。
  * 委外工單 → 不建立任何 MaterialSlip，不呼叫 `IDocNumberService`。
  * 無 Push 料件（全為 Backflush）→ 不建立 MaterialSlip。
  * MaterialSlip Issue（Confirmed → Posted）且有對應工單 → `IssuedQty` 正確回寫，`PostDocSimplifiedAsync` 呼叫一次。
  * InternalIssue TypeCode = `IO`；InternalReturn TypeCode = `IR`。
  * InternalRequisition(MR) 類別需先 Submit(Draft→Pending) 才能 Approve；InternalIssue(MI) 可從 Draft 直接 Approve。
  * Cancel 允許 Draft / Picking 狀態；Posted / Confirmed 狀態不得取消。
* **無效等價類（Invalid）**：
  * 非 Confirmed 狀態呼叫 Issue → 拒絕（success=false），PostingService 不呼叫。
  * 非 InternalRequisition 類別呼叫 Submit → 拒絕（success=false）。
  * InternalRequisition 在 Draft 狀態直接 Approve → 拒絕（必須先 Submit）。

#### B. 邊界值分析（BVA）
* **ConfirmedQty 優先**：`ConfirmedQty = 4m, RequestedQty = 5m` → `IssuedQty = 4m`（以 ApprovedQty 回填 ConfirmedQty）。
* **ConfirmedQty 為 null**：降級使用 `RequestedQty`，不拋例外。
* **重複觸發防護**：同一工單若重複進入 Approved 路徑，設計上由簽核流程防止，當前版本不做去重守門（記錄為已知限制）。

---

### 5.14 請購單→採購單轉換數字一致性（PR→PO Gap）

**實作位置**：`Modules/PUR/Services/PurchaseRequestService.cs`（`ConvertToPOAsync`）

**轉換數字鏈**：
```
PR.RequestQty   → PO.OrderQty（逐行對應）
PR.ReleasedQty  累計回寫（每次轉換後遞增）
PR.Status       → FullyReleased（全轉）/ PartiallyReleased（部分轉）
```

#### A. 等價劃分（EP）

* **有效等價類（Valid）**：
  * 單張 PR 完整轉一張 PO → `PO.OrderQty = PR.RequestQty`；`PR.ReleasedQty = PR.RequestQty`；`PR.Status → FullyReleased`
  * 多張 PR 合併一張 PO（同廠商/品號）→ `PO.OrderQty = ΣPR.RequestQty`；各 PR 的 `ReleasedQty` 個別回寫；`POLine.SourcePrId` 每行對應正確
  * 部分轉換（只轉部分明細或部分數量）→ 未轉 PR 明細 `Status = PartiallyReleased`；已轉量精確回寫 `ReleasedQty`
  * PR 作廢後 PO 已建立 → PO 維持不變；`PR.ReleasedQty` 不影響 PO 數量
* **無效等價類（Invalid）**：
  * `ConvertQty > PR.RequestQty - PR.ReleasedQty`（超轉可用量）→ 拒絕，拋 `InvalidOperationException`
  * `PR.Status = Cancelled`（已取消請購轉 PO）→ 拒絕，拋 `InvalidOperationException`

#### B. 邊界值分析（BVA）

* **ReleasedQty 剛好全轉臨界**：`ReleasedQty + 本次轉量 = RequestQty`（如 60 + 40 = 100）→ 允許，`PR.Status → FullyReleased`，不拋例外
* **超出最小單位**：`ReleasedQty + 本次 = RequestQty + 0.0001` → 拒絕，拋例外
* **金額預算差異記錄**：`PO.UnitPrice ≠ PR.EstimatedUnitPrice`（議價後不同）→ 不阻擋建單；預算差額 = `(PO.UnitPrice - PR.EstimatedUnitPrice) × OrderQty`，精確至 2 位小數，正值為超預算、負值為節省

---

### 5.15 AP 付款→日記帳立帳數字一致性（AP Payment→Journal）

**實作位置**：`Modules/FIN/Services/APPaymentService.cs`（`ConfirmPaymentAsync`）

**立帳分錄**：
```
借：應付帳款（AP 科目）  = APPaymentAllocation.AllocatedAmount
貸：銀行/現金（Cash 科目）= APPayment.Amount
差額（若有）→ 匯兌損益科目（外幣付款時，見 §5.17）
```

#### A. 等價劃分（EP）

* **有效等價類（Valid）**：
  * 一次付清 → `APPayment.Amount = APInvoice.TotalAmount`；JournalEntry 借方 AP 科目 = 貸方銀行科目；`APInvoice.Status → Paid`
  * 部分付款 → `APPayment.Amount < APInvoice.TotalAmount`；JournalLine 記本次付款額；`APInvoice.Status = PartialPaid`；`PaidAmount` 累加精確
  * 多張發票合批付款 → `APPayment.Amount = ΣAllocatedAmount`；每張發票各自產生獨立 JournalLine，加總與付款額一致
  * 預付款沖銷 → `APInvoice.TotalAmount = Prepayment.Amount + 補付金額`；JournalEntry 借方含「AP 科目」+「預付款科目」兩行，加總一致
* **無效等價類（Invalid）**：
  * `APPaymentAllocation.AllocatedAmount > APInvoice.TotalAmount - AlreadyPaid` → 拒絕，拋 `InvalidOperationException`
  * `JournalEntry ΣDebit ≠ ΣCredit`（借貸不平衡）→ 過帳服務攔截，拋 `InvalidOperationException`

#### B. 邊界值分析（BVA）

* **最後一次付款臨界**：`ΣAlreadyPaid + 本次 = TotalAmount`（精確至本位幣最小精度，如 0.01）→ `APInvoice.Status → Paid`；JournalEntry 借貸差額 = 0
* **容忍尾差**：`|ΣAllocated - TotalAmount| ≤ 0.01`（進位尾差）→ 差額自動入「尾差調整」科目，發票標記 `Paid`，借貸仍平衡

---

### 5.16 AR 收款→日記帳立帳數字一致性（AR Receipt→Journal）

**實作位置**：`Modules/FIN/Services/ARReceiptService.cs`（`ConfirmReceiptAsync`）

**立帳分錄**：
```
借：銀行/現金（Cash 科目）= ARReceipt.Amount
貸：應收帳款（AR 科目）  = ARReceiptAllocation.AllocatedAmount
差額（若有）→ 匯兌損益科目（外幣收款時，見 §5.17）
```

#### A. 等價劃分（EP）

* **有效等價類（Valid）**：
  * 全額收款 → `ARReceipt.Amount = ARInvoice.TotalAmount`；JournalLine 借方銀行 = 貸方 AR；`ARInvoice.Status → Received`
  * 分次收款 → 各次 JournalEntry 記本次金額；`ARInvoice.Status = PartialReceived`；`ReceivedAmount` 累加精確
  * 沖銷貸項憑單（Credit Note）→ `ARInvoice.TotalAmount = ΣReceipt + ΣCreditNote`；JournalEntry 貸方 AR 科目含兩類來源，加總一致
* **無效等價類（Invalid）**：
  * `AllocatedAmount > ARInvoice.TotalAmount - ReceivedAmount` → 拒絕，拋 `InvalidOperationException`
  * `JournalEntry ΣDebit ≠ ΣCredit` → 攔截，拋 `InvalidOperationException`

#### B. 邊界值分析（BVA）

* **0.001 容忍全額臨界**（對齊 §5.6，延伸驗證 Journal 層）：`AllocatedAmount = TotalAmount - 0.001`（容忍範圍內）→ `Status → Received`；JournalLine 貸方 AR 科目 = `TotalAmount`（全額沖銷）
* **反沖 VoidARReceipt**：`VoidConfirmReceiptAsync` 觸發反轉分錄 → JournalLine 借貸方向完全相反、金額與原分錄一致；`ReceivedAmount` 精確歸還，`Status → Open`

---

### 5.17 多幣別金額換算一致性（Multi-Currency Amount Chain）

**換算方向**（全系統統一，見 `project-currency-direction-convention.md`）：
```
本幣金額 = 外幣金額 × ExchangeRate（ExchangeRate = 本幣/外幣，乘法方向）
```
**實作位置**：`Modules/BAS/Services/CurrencyService.cs`（`ConvertToBaseAsync`）、`Modules/FIN/Services/APInvoiceService.cs`、`Modules/FIN/Services/ARInvoiceService.cs`

#### A. 等價劃分（EP）

* **有效等價類（Valid）**：
  * 外幣 AP 發票入帳：`APInvoice.BaseAmount = Round(ForeignAmount × RateAtBooking, baseDp, AwayFromZero)`；精度依本位幣小數位
  * 外幣 AR 發票入帳：同上，`ARInvoice.BaseAmount` 精確
  * 付款日匯率 = 記帳日匯率（無匯差）→ 不產生匯兌損益 JournalLine；`APPayment.BaseAmount = APInvoice.BaseAmount`
  * 有利匯差（AP 外幣，付款日匯率低於記帳日）→ 額外產生「已實現匯兌利益」**貸方** JournalLine；整筆 JournalEntry 借貸仍平衡
  * 不利匯差（AP 外幣，付款日匯率高於記帳日）→ 額外產生「已實現匯兌損失」**借方** JournalLine；借貸仍平衡
* **無效等價類（Invalid）**：
  * `ExchangeRate ≤ 0` → `ConvertToBaseAsync` 拋 `ArgumentOutOfRangeException`
  * `APInvoice.BaseAmount ≠ Round(ForeignAmount × Rate, baseDp)`（精度錯誤）→ 稽核查詢偵測金額鏈斷裂

#### B. 邊界值分析（BVA）

* **精度邊界**：`ForeignAmount = 1.0001`，`Rate = 30.1234` → `BaseAmount = Round(30.1258..., 2, AwayFromZero) = 30.13`；不截斷為 30.12
* **匯差極小值**：`|BaseAmountAtPayment - BaseAmountAtInvoice| = 0.01`（最小本位幣單位）→ 仍產生獨立的匯兌損益 JournalLine，不被吸收為 0
* **匯差方向驗證**：有利匯差（AP）→ `匯兌利益 = BaseAmountAtInvoice - BaseAmountAtPayment > 0`，入**貸方**；不利匯差 → 入**借方**；方向不得反置

---

### 5.18 庫存過帳→日記帳雙向數字核對（StockMovement→Journal Amount）

**實作位置**：`Modules/INV/Services/InventoryPostingService.cs` + `Modules/FIN/Services/JournalPostingService.cs`

**分錄對應**：
```
進貨入庫：借：存貨科目 / 貸：GR/IR 暫估應付科目  （各 = StockMovement.TotalCost）
銷貨出庫：借：銷貨成本（COGS）/ 貸：存貨科目     （各 = StockMovement.TotalCost）
```

#### A. 等價劃分（EP）

* **有效等價類（Valid）**：
  * 進貨入庫單行過帳 → `JournalLine.Debit(存貨科目) = StockMovement.TotalCost`；`JournalLine.Credit(GR/IR) = StockMovement.TotalCost`；借貸平衡
  * 銷貨出庫過帳 → `JournalLine.Debit(COGS) = StockMovement.TotalCost`；`JournalLine.Credit(存貨科目) = StockMovement.TotalCost`
  * 多行 GR 過帳 → 每行對應獨立 JournalLine（不合併）；`ΣJournalLine.Debit = ΣStockMovement.TotalCost`
  * 庫存調整（盤盈/盤虧）→ 差額入「存貨差異」科目；JournalEntry 借貸平衡
* **無效等價類（Invalid）**：
  * `JournalEntry ΣDebit ≠ ΣCredit`（不平衡傳票）→ `JournalPostingService` 攔截，拋 `InvalidOperationException`
  * `JournalLine.Amount ≠ StockMovement.TotalCost`（金額不一致）→ §3.4 稽核查詢偵測異常

#### B. 邊界值分析（BVA）

* **微量金額**：`StockMovement.TotalCost = 0.0001`（4 位小數）→ `JournalLine.Debit = 0.0001`，不截斷為 0，借貸均 0.0001 平衡
* **AVCO 更新差異調整**：入庫後存貨均價改變，既有庫存帳面調整額 = `(新均價 - 舊均價) × 既有在庫量`；對應 JournalLine 金額與公式一致，正負依均價漲跌方向

---

## 6. 測試覆蓋分析（2026-06）

### 6.1 EP/BVA 案例覆蓋矩陣

| 場景 | 案例 | 類型 | 狀態 | 所在測試檔案 |
|------|------|------|------|------------|
| §5.1 | GR qty = AP qty（完全核銷）| EP Valid | ✅ 已覆蓋 | `P2PReconciliationTests` |
| §5.1 | GR qty > AP qty（部分核銷）| EP Valid | ✅ 已覆蓋 | `P2PReconciliationTests` |
| §5.1 | GR qty < AP qty（溢開/預開）| EP Valid | ✅ 已覆蓋 | `P2PReconciliationTests` |
| §5.1 | AP qty > PO qty → 拒絕 | EP Invalid | ✅ 已實作攔截 | `P2PReconciliationTests` |
| §5.1 | qty ≤ 0 或 price ≤ 0 → 拒絕 | EP Invalid | ✅ 已實作攔截 | `P2PReconciliationTests` |
| §5.1 | BVA 0%（qty=0）| BVA | ✅ 已覆蓋 | `P2PReconciliationTests` |
| §5.1 | BVA 100%（最後清零 → Paid）| BVA | ✅ 已覆蓋 | `P2PReconciliationTests` |
| §5.1 | BVA PPV ±0.01 精度 | BVA | ✅ 已覆蓋 | `P2PReconciliationTests` |
| §5.2 | 損耗率 ≤ 允收 → penalty = 0 | EP Valid | ✅ 已實作 | `OutsourceScrapCalculatorTests` |
| §5.2 | 損耗率 > 允收 → penalty > 0 | EP Valid | ✅ 已實作 | `OutsourceScrapCalculatorTests` |
| §5.2 | 損耗率 = 100%（全損）| EP Valid | ✅ 已實作 | `OutsourceScrapCalculatorTests` |
| §5.2 | 損耗率 < 0%（超產）→ 拋例外 | EP Invalid | ✅ 已實作 | `OutsourceScrapCalculatorTests` |
| §5.2 | BVA 允收臨界點（penalty = 0）| BVA | ✅ 已實作 | `OutsourceScrapCalculatorTests` |
| §5.2 | BVA 超耗臨界點 +0.0001% | BVA | ✅ 已實作 | `OutsourceScrapCalculatorTests` |
| §5.2 | BVA 扣款超額 > 加工費 | BVA | ✅ 已實作 | `OutsourceScrapCalculatorTests` |
| §5.3 | 期初 = 0 + 入庫 | EP Valid | ✅ 已覆蓋 | `InventoryServiceTests` |
| §5.3 | 期初 > 0 + 入庫 | EP Valid | ✅ 已覆蓋 | `InventoryServiceTests` |
| §5.3 | EP Invalid: 入庫 inUnitCost < 0（InventoryService）→ 拋例外 | EP Invalid | ✅ 已實作攔截 | `INV/InventoryServiceTests` |
| §5.3 | BVA 庫存歸零 → UnitCost 保留 | BVA | ✅ 已覆蓋 | `InventoryServiceTests` |
| §5.3 | BVA totalQty+addQty=0 guard | BVA | ✅ 已覆蓋 | `InventoryServiceTests` |
| §5.3 | BVA 極端微小值精度 | BVA | ✅ 已覆蓋 | `InventoryServiceTests` |
| §5.3 | EP Invalid: 入庫單價 < 0（PostLineDetail）→ 拋例外 | EP Invalid | ✅ 已實作攔截 | `InventoryPostingServiceTests` |
| §5.3 | EP Invalid: 入庫單價 < 0（PostDocSimplified）→ 拋例外 | EP Invalid | ✅ 已實作攔截 | `InventoryPostingServiceTests` |
| §5.4 | Pending → Synced | EP Valid | ✅ 已實作 | `ErpSyncServiceTests` |
| §5.4 | Failed → Synced（重試）| EP Valid | ✅ 已實作 | `ErpSyncServiceTests` |
| §5.4 | Failed + 超過最大重試 → 鎖定 | EP Invalid | ✅ 已實作 | `ErpSyncServiceTests` |
| §5.4 | BVA ±0.01 進位尾差 → Rounding Diff | BVA | ✅ 已實作 | `ErpSyncServiceTests` |
| §5.4 | BVA 重試臨界點 RetryCount=3 | BVA | ✅ 已實作 | `ErpSyncServiceTests` |
| §5.5 | GS → AR 單行數量/金額一致性 | EP Valid | ✅ 已覆蓋 | `O2CReconciliationTests` |
| §5.5 | GS → AR 多行 TotalAmount 加總 | EP Valid | ✅ 已覆蓋 | `O2CReconciliationTests` |
| §5.5 | GenerateFromShipment 冪等防重開 | EP Valid | ✅ 已覆蓋 | `O2CReconciliationTests` |
| §5.5 | qty ≤ 0 或 price < 0 → 拒絕（手動）| EP Invalid | ✅ 已實作攔截 | `O2CReconciliationTests` |
| §5.5 | BVA 稅額精度 4 位小數 AwayFromZero | BVA | ✅ 已覆蓋 | `O2CReconciliationTests` |
| §5.5 | BVA 小額稅額不截斷為 0 | BVA | ✅ 已覆蓋 | `O2CReconciliationTests` |
| §5.6 | 部分收款 → PartialReceived | EP Valid | ✅ 已覆蓋 | `O2CReconciliationTests` |
| §5.6 | 全額收款 → Received | EP Valid | ✅ 已覆蓋 | `O2CReconciliationTests` |
| §5.6 | 核銷超出餘額 → 拒絕 | EP Invalid | ✅ 已覆蓋 | `O2CReconciliationTests` |
| §5.6 | BVA TotalAmount−0.001 容忍 → Received | BVA | ✅ 已覆蓋 | `O2CReconciliationTests` |
| §5.6 | BVA VoidARReceipt 反沖還原 | BVA | ✅ 已覆蓋 | `O2CReconciliationTests` |
| §5.7 | 多工作中心費率各自加總 | EP Valid | ✅ 已覆蓋 | `WipReconciliationTests` |
| §5.7 | BVA qty=0 時 SetupCost 仍計費 | BVA | ✅ 已覆蓋 | `WipReconciliationTests` |
| §5.7 | BVA 秒換算精度（÷3600）| BVA | ✅ 已覆蓋 | `WipReconciliationTests` |
| §5.8 | WIP = 期初 + 發料 − 完工 | EP Valid | ✅ 已覆蓋 | `WipReconciliationTests` |
| §5.8 | WIP 五參數完整公式（含人工/報廢）| EP Valid | ✅ 已覆蓋 | `WipReconciliationTests` |
| §5.8 | WIP 完全完工歸零 | EP Valid | ✅ 已覆蓋 | `WipReconciliationTests` |
| §5.8 | WIP 在途全保留（完工=0）| EP Valid | ✅ 已覆蓋 | `WipReconciliationTests` |
| §5.8 | 淨發料 < 0 → ArgumentOutOfRange | EP Invalid | ✅ 已覆蓋 | `WipReconciliationTests` |
| §5.8 | BVA Actual=Standard → 總差異=0 | BVA | ✅ 已覆蓋 | `WipReconciliationTests` |
| §5.8 | BVA 超支+節省混合差異精確加總 | BVA | ✅ 已覆蓋 | `WipReconciliationTests` |

| §5.9 | 分批進貨各批在剩餘可收範圍 → 允許 | EP Valid | ✅ 已實作攔截 | `P2PDocFlowTests` |
| §5.9 | 第 2 批使累計超收 → 拒絕 | EP Invalid | ✅ 已實作攔截 | `P2PDocFlowTests` |
| §5.9 | 刪除 GR → PO ReceivedQty 回補 | EP Valid | ✅ 已覆蓋 | `P2PDocFlowTests` |
| §5.9 | 作廢 APInvoice → 未結餘額歸零 | EP Valid | ✅ 已覆蓋 | `P2PDocFlowTests` |
| §5.9 | BVA ReceivedQty + newQty = OrderQty → 允許 | BVA | ✅ 已覆蓋 | `P2PDocFlowTests` |
| §5.9 | BVA ReceivedQty + newQty = OrderQty + 0.0001 → 拒絕 | BVA | ✅ 已覆蓋 | `P2PDocFlowTests` |
| §5.10 | 單行 GR 過帳 → StockMovement qty 吻合 | EP Valid | ✅ 已覆蓋 | `GrStockPostingTests` |
| §5.10 | 單行 GR 過帳 → TotalCost 精確 | EP Valid | ✅ 已覆蓋 | `GrStockPostingTests` |
| §5.10 | 含 PO 來源 → SourceDocNo 追蹤正確 | EP Valid | ✅ 已覆蓋 | `GrStockPostingTests` |
| §5.10 | 多行 GR → 逐行獨立 StockMovement | EP Valid | ✅ 已覆蓋 | `GrStockPostingTests` |
| §5.10 | BVA StockQty=0.0001 微量精度 | BVA | ✅ 已覆蓋 | `GrStockPostingTests` |
| §5.11 | 無稅同價：AP = GR = PO 估值，PPV=0 | EP Valid | ✅ 已覆蓋 | `AmountChainTests` |
| §5.11 | 含稅：TotalAmount = ΣLineAmount + ΣTaxAmount | EP Valid | ✅ 已覆蓋 | `AmountChainTests` |
| §5.11 | PPV 存在：差額精確至 2 位小數 | EP Valid | ✅ 已覆蓋 | `AmountChainTests` |
| §5.11 | BVA UnitPrice=100.001 × 1000 → 4 位精度 | BVA | ✅ 已覆蓋 | `AmountChainTests` |
| §5.11 | BVA 稅額精度 1×5%=0.05，TotalAmount=1.05 | BVA | ✅ 已覆蓋 | `AmountChainTests` |
| §5.12 | 簡易模式：單行 → StockMovement.StockQty 吻合 | EP Valid | ✅ 已覆蓋 | `InvModeFlowTests` |
| §5.12 | 簡易模式：單行 → QtyOnHand 立即更新 | EP Valid | ✅ 已覆蓋 | `InvModeFlowTests` |
| §5.12 | 簡易模式：單行 → SetQtyPutAway 以全量呼叫 | EP Valid | ✅ 已覆蓋 | `InvModeFlowTests` |
| §5.12 | 簡易模式：呼叫後 SetDocPosted 恆觸發 | EP Valid | ✅ 已覆蓋 | `InvModeFlowTests` |
| §5.12 | 簡易模式：三行 → 各自獨立 StockMovement | EP Valid | ✅ 已覆蓋 | `InvModeFlowTests` |
| §5.12 | 簡易模式 BVA：qty=0.0001 精度不截斷 | BVA | ✅ 已覆蓋 | `InvModeFlowTests` |
| §5.12 | 完整模式：部分完成 → SetDocPosted 不觸發 | EP Valid | ✅ 已覆蓋 | `InvModeFlowTests` |
| §5.12 | 完整模式：全部完成 → SetDocPosted 觸發 | EP Valid | ✅ 已覆蓋 | `InvModeFlowTests` |
| §5.12 | 完整模式：分兩次過帳 → QtyOnHand 累積 | EP Valid | ✅ 已覆蓋 | `InvModeFlowTests` |
| §5.12 | 模式比較：簡易恆觸發；完整依條件觸發 | EP Valid | ✅ 已覆蓋 | `InvModeFlowTests` |
| §5.13 | 非委外 WO 核准（ApprovalSync）→ 建立 Draft MI Slip | EP Valid | ✅ 已覆蓋 | `MFG/WorkOrderApprovalSyncTests` |
| §5.13 | Slip Lines 與 Push 料件 SourceLineId 一對一 | EP Valid | ✅ 已覆蓋 | `MFG/WorkOrderApprovalSyncTests` |
| §5.13 | 委外工單 → 不建立 MaterialSlip，不呼叫 DocNumber | EP Valid | ✅ 已覆蓋 | `MFG/WorkOrderApprovalSyncTests` |
| §5.13 | 無 Push 料件（全 Backflush）→ 不建立 MaterialSlip | EP Valid | ✅ 已覆蓋 | `MFG/WorkOrderApprovalSyncTests` |
| §5.13 | Rejected → WO 狀態回寫、無 Slip | EP Valid | ✅ 已覆蓋 | `MFG/WorkOrderApprovalSyncTests` |
| §5.13 | Withdrawn → 清除 ApprovalRequestId、無 Slip | EP Valid | ✅ 已覆蓋 | `MFG/WorkOrderApprovalSyncTests` |
| §5.13 | UnknownDocument → 不拋例外 | EP Valid | ✅ 已覆蓋 | `MFG/WorkOrderApprovalSyncTests` |
| §5.13 | WO.ApprovalStatus 回寫為 Approved | EP Valid | ✅ 已覆蓋 | `MFG/WorkOrderApprovalSyncTests` |
| §5.13 | Release 直接核准路徑 → 建立 Draft MI Slip | EP Valid | ✅ 已覆蓋 | `MFG/WorkOrderLifecycleTests` |
| §5.13 | 委外 Release → 不建立 MaterialSlip | EP Valid | ✅ 已覆蓋 | `MFG/WorkOrderLifecycleTests` |
| §5.13 | Issue（Confirmed→Posted）呼叫 PostingService 一次 | EP Valid | ✅ 已覆蓋 | `MFG/MaterialSlipControllerTests` |
| §5.13 | Issue 後 IssuedQty 回寫 WorkOrderMaterial | EP Valid | ✅ 已覆蓋 | `MFG/MaterialSlipControllerTests` |
| §5.13 | 非 Confirmed 狀態 Issue → 拒絕 | EP Invalid | ✅ 已覆蓋 | `MFG/MaterialSlipControllerTests` |
| §5.13 | MI/MX TypeCode IO；MR/MX TypeCode IR | EP Valid | ✅ 已覆蓋 | `MFG/MaterialSlipControllerTests` |
| §5.13 | MR Draft→Submit→Pending；非 MR Submit → 拒絕 | EP Valid/Invalid | ✅ 已覆蓋 | `MFG/MaterialSlipControllerTests` |
| §5.13 | MR Draft 直接 Approve → 拒絕 | EP Invalid | ✅ 已覆蓋 | `MFG/MaterialSlipControllerTests` |
| §5.13 | Cancel Draft/Picking → 允許；Posted/Confirmed → 拒絕 | EP Mixed | ✅ 已覆蓋 | `MFG/MaterialSlipControllerTests` |
| §5.13 | BVA ConfirmedQty null → 降級用 RequestedQty | BVA | ✅ 已覆蓋 | `MFG/MaterialSlipControllerTests` |
| §5.13 | BVA ConfirmedQty(4m) 優先於 RequestedQty(5m) | BVA | ✅ 已覆蓋 | `MFG/MaterialSlipControllerTests` |

| §5.14 | 單張 PR 完整轉 PO → ReleasedQty 全量回寫，Status→FullyReleased | EP Valid | ✅ 已覆蓋 | `PUR/PrToPOConversionTests` |
| §5.14 | 多張 PR 合併 PO → 各 ReleasedQty 個別回寫，POLine.SourcePrId 正確 | EP Valid | ✅ 已覆蓋 | `PUR/PrToPOConversionTests` |
| §5.14 | 部分轉換 → PartiallyReleased，ReleasedQty 精確回寫 | EP Valid | ✅ 已覆蓋 | `PUR/PrToPOConversionTests` |
| §5.14 | PR 作廢後 PO 維持不變 | EP Valid | ✅ 已覆蓋 | `PUR/PrToPOConversionTests` |
| §5.14 | 超轉可用量 → 拒絕，拋 InvalidOperationException | EP Invalid | ✅ 已覆蓋 | `PUR/PrToPOConversionTests` |
| §5.14 | 已取消 PR 轉 PO → 拒絕，拋 InvalidOperationException | EP Invalid | ✅ 已覆蓋 | `PUR/PrToPOConversionTests` |
| §5.14 | BVA ReleasedQty + 本次 = RequestQty → 允許，Status→FullyReleased | BVA | ✅ 已覆蓋 | `PUR/PrToPOConversionTests` |
| §5.14 | BVA ReleasedQty + 本次 = RequestQty + 0.0001 → 拒絕 | BVA | ✅ 已覆蓋 | `PUR/PrToPOConversionTests` |
| §5.14 | BVA 金額預算差異精確至 2 位小數，不阻擋建單 | BVA | ✅ 已覆蓋 | `PUR/PrToPOConversionTests` |
| §5.15 | 一次付清 → APInvoice.Status=Paid，JournalEntry 借貸平衡 | EP Valid | ✅ 已覆蓋 | `FIN/APPaymentJournalTests` |
| §5.15 | 部分付款 → PartialPaid，PaidAmount 累加精確 | EP Valid | ✅ 已覆蓋 | `FIN/APPaymentJournalTests` |
| §5.15 | 多張發票合批付款 → ΣAllocated = APPayment.Amount，JournalLine 各自獨立 | EP Valid | ✅ 已覆蓋 | `FIN/APPaymentJournalTests` |
| §5.15 | 預付款沖銷 → JournalEntry 借方兩行加總一致 | EP Valid | ✅ 已覆蓋 | `FIN/APPaymentJournalTests` |
| §5.15 | AllocatedAmount 超出餘額 → 拒絕 | EP Invalid | ✅ 已覆蓋 | `FIN/APPaymentJournalTests` |
| §5.15 | JournalEntry 借貸不平衡 → 攔截，拋例外 | EP Invalid | ✅ 已覆蓋 | `FIN/APPaymentJournalTests` |
| §5.15 | BVA 最後一次付款臨界 → Paid，借貸差額 = 0 | BVA | ✅ 已覆蓋 | `FIN/APPaymentJournalTests` |
| §5.15 | BVA ±0.01 容忍尾差 → 入尾差調整科目，借貸仍平衡 | BVA | ✅ 已覆蓋 | `FIN/APPaymentJournalTests` |
| §5.16 | 全額收款 → Received，JournalEntry 借貸平衡 | EP Valid | ✅ 已覆蓋 | `FIN/ARReceiptJournalTests` |
| §5.16 | 分次收款 → PartialReceived，ReceivedAmount 累加精確 | EP Valid | ✅ 已覆蓋 | `FIN/ARReceiptJournalTests` |
| §5.16 | 沖銷 CreditNote → 貸方 AR 含兩類來源，加總一致 | EP Valid | ✅ 已覆蓋 | `FIN/ARReceiptJournalTests` |
| §5.16 | AllocatedAmount 超出餘額 → 拒絕 | EP Invalid | ✅ 已覆蓋 | `FIN/ARReceiptJournalTests` |
| §5.16 | JournalEntry 借貸不平衡 → 攔截 | EP Invalid | ✅ 已覆蓋 | `FIN/ARReceiptJournalTests` |
| §5.16 | BVA TotalAmount−0.001 容忍 → Received，貸方 AR 全額沖銷 | BVA | ✅ 已覆蓋 | `FIN/ARReceiptJournalTests` |
| §5.16 | BVA VoidARReceipt 反轉分錄金額一致，Status→Open | BVA | ✅ 已覆蓋 | `FIN/ARReceiptJournalTests` |
| §5.17 | 外幣 AP 發票：BaseAmount = Round(Foreign × Rate, dp, AwayFromZero) | EP Valid | ✅ 已覆蓋 | `FIN/MultiCurrencyAmountTests` |
| §5.17 | 外幣 AR 發票：BaseAmount 精確 | EP Valid | ✅ 已覆蓋 | `FIN/MultiCurrencyAmountTests` |
| §5.17 | 付款日匯率相同 → 不產生匯兌損益 JournalLine | EP Valid | ✅ 已覆蓋 | `FIN/MultiCurrencyAmountTests` |
| §5.17 | 有利匯差 → 匯兌利益貸方 JournalLine，借貸平衡 | EP Valid | ✅ 已覆蓋 | `FIN/MultiCurrencyAmountTests` |
| §5.17 | 不利匯差 → 匯兌損失借方 JournalLine，借貸平衡 | EP Valid | ✅ 已覆蓋 | `FIN/MultiCurrencyAmountTests` |
| §5.17 | ExchangeRate ≤ 0 → 拋 ArgumentOutOfRangeException | EP Invalid | ✅ 已覆蓋 | `FIN/MultiCurrencyAmountTests` |
| §5.17 | BVA 精度：Round(1.0001×30.1234, 2)=30.13，不截斷為 30.12 | BVA | ✅ 已覆蓋 | `FIN/MultiCurrencyAmountTests` |
| §5.17 | BVA 匯差極小值 0.01 → 獨立 JournalLine 不被吸收為 0 | BVA | ✅ 已覆蓋 | `FIN/MultiCurrencyAmountTests` |
| §5.17 | BVA 匯差方向：有利→貸方；不利→借方，不得反置 | BVA | ✅ 已覆蓋 | `FIN/MultiCurrencyAmountTests` |
| §5.18 | 進貨入庫：JournalDebit(存貨) = TotalCost，借貸平衡 | EP Valid | ✅ 已覆蓋 | `INV/StockMovementJournalTests` |
| §5.18 | 銷貨出庫：JournalDebit(COGS) = TotalCost，借貸平衡 | EP Valid | ✅ 已覆蓋 | `INV/StockMovementJournalTests` |
| §5.18 | 多行 GR：逐行獨立 JournalLine，ΣDebit = ΣTotalCost | EP Valid | ✅ 已覆蓋 | `INV/StockMovementJournalTests` |
| §5.18 | 庫存調整：差額入存貨差異科目，借貸平衡 | EP Valid | ✅ 已覆蓋 | `INV/StockMovementJournalTests` |
| §5.18 | JournalEntry 借貸不平衡 → JournalPostingService 攔截 | EP Invalid | ✅ 已覆蓋 | `INV/StockMovementJournalTests` |
| §5.18 | BVA TotalCost=0.0001 → JournalLine 0.0001，不截斷為 0 | BVA | ✅ 已覆蓋 | `INV/StockMovementJournalTests` |
| §5.18 | BVA AVCO 差異調整：金額與公式一致，正負依均價漲跌方向 | BVA | ✅ 已覆蓋 | `INV/StockMovementJournalTests` |

| §3.1 | 收料 ≤ 訂購量／超出最小單位拒絕／草稿作廢 GR 不計／退貨回扣 | EP+BVA | ✅ 已覆蓋 | `ACT/DataAuditServiceTests` |
| §3.2 | 發票數量溢開偵測／作廢發票排除／30 天掛帳超期／PPV 金額差不誤報 | EP Mixed | ✅ 已覆蓋 | `ACT/DataAuditServiceTests` |
| §3.3 | 委外在外餘量正常不報／理論消耗超發料揪出／退料計入淨發料 | EP Mixed | ✅ 已覆蓋 | `ACT/DataAuditServiceTests` |
| §3.4 | 傳票借貸平衡／不平衡揪出／1:1 來源重複立帳／INV 多張豁免 | EP Mixed | ✅ 已覆蓋 | `ACT/DataAuditServiceTests` |
| §3.5 | ERP 滯留 24h 揪出／Synced・新單・作廢不報 | EP Mixed | ✅ 已覆蓋 | `ACT/DataAuditServiceTests` |
| §3.6 | 滾動平衡吻合不報／帳差揪出／有異動無庫存列揪出 | EP Mixed | ✅ 已覆蓋 | `ACT/DataAuditServiceTests` |
| §3.7 | 超出貨揪出／草稿 GS 不計／出貨未開票／有效發票不報／作廢發票仍報 | EP Mixed | ✅ 已覆蓋 | `ACT/DataAuditServiceTests` |
| §3.8 | 後沖吻合不報／偏差揪出／完全漏沖揪出／未入庫工單不核對 | EP Mixed | ✅ 已覆蓋 | `ACT/DataAuditServiceTests` |
| §3.9 | 累計欄位吻合（含草稿、排除作廢）不報／漂移揪出／無單據但欄位非零揪出（PO+SO 雙側） | EP Mixed | ✅ 已覆蓋 | `ACT/DataAuditServiceTests` |
| §3.9 | 三處作廢回沖：GR 作廢回沖 ReceivedQty／退貨作廢回沖 ReturnedQty／銷退作廢回沖 GSL.ReturnedQty | EP Valid | ✅ 已覆蓋 | `PUR/GoodsReceiptFlowControllerTests`、`PUR/GoodsReturnFlowControllerTests`、`ORD/SalesReturnControllerTests` |
| §3.10 | 覆蓋吻合不報／在製無快照揪出／快照過期揪出／負分量揪出／期間內 Preliminary 揪出／期外不報／關帳擋關 | EP Mixed | ✅ 已覆蓋 | `ACT/DataAuditServiceTests` |
| §3.11 | 雙腿吻合不報／材料腿漂移揪出／漏後沖傳票揪出／Final 跳過人工/製費腿／Preliminary 人工/製費漂移揪出 | EP Mixed | ✅ 已覆蓋 | `ACT/DataAuditServiceTests` |
| §3.5 | AR 滯留揪出／AR Synced/新單/作廢不報（AP 案例既有） | EP Mixed | ✅ 已覆蓋 | `ACT/DataAuditServiceTests` |
| D1-B | 可比口徑純函式（混合料/PerReport 排除/無後沖料）／完工混合料差異用可比標準（假差異消除） | EP Mixed | ✅ 已覆蓋 | `MFG/WorkOrderCostCalculatorTests`、`MFG/ProductionCompletionJournalTests` |
| D3 | 泛型 MarkSynced（AR Pending→Synced／尾差超限拋例外）／AR 逾期查詢 | EP Mixed | ✅ 已覆蓋 | `FIN/ErpSyncServiceTests` |
| §4.1 | 稽核異常擋關帳（停留 Closing）／通過即關帳／未注入稽核維持原行為／廠別範圍過濾 | 整合 | ✅ 已覆蓋 | `ACT/DataAuditServiceTests` |
| §4.2 | 執行結果持久化／異常明細寫入／逾保留期清除 | EP Valid | ✅ 已覆蓋 | `ACT/DataAuditRunServiceTests` |
| P-7 | 容忍值參數覆寫生效（數量/金額/核銷/ERP 尾差）／不合法值退回預設／未設參數行為不變 | EP Mixed | ✅ 已覆蓋 | `ACT/DataAuditServiceTests`、`FIN/ErpSyncServiceTests`、`FIN/FinPaymentServiceAPTests` |

### 6.2 已知服務層缺陷（需後續 Sprint 補強）

| 缺陷 ID | 描述 | 影響範圍 | 狀態 |
|---------|------|---------|------|
| DEF-INV-01 | `InventoryPostingService` 未攔截負數入庫單價（`UnitPrice < 0`） | 庫存成本可能被錯誤拉低 | ✅ 已修補：`PostLineDetailAsync` + `PostDocSimplifiedAsync` 各加 guard；測試 `PostLineDetail_Inbound_NegativeUnitPrice_ShouldThrow`、`PostDocSimplified_Inbound_NegativeUnitPrice_ShouldThrow` |
| DEF-INV-02 | 負庫存情境下 AVCO 補回扭曲（PostingService 有攔截，但允許負庫存系統參數啟用後缺少測試） | 庫存成本均價異常 | ⚠️ 待補強：實作 `AllowNegativeStock` 系統參數時一併補測試 |
| DEF-PUR-01 | `PurchaseRequestService.ConvertToPOAsync` 缺少超轉防護（超過剩餘可轉量時未拋例外）及已取消 PR 轉換攔截 | PUR 請購轉採購 | ✅ 已修補：`PurchaseService.UpdatePrOrderedQtyAsync` 加入 `IsClosed` 及 `OrderedQty + OrderQty > RequestQty` 兩道 guard；測試 `PrToPOConversionTests`（9 cases） |
| DEF-FIN-01 | `APPaymentService.ConfirmPaymentAsync` 缺少付款超額攔截及 JournalEntry 借貸平衡驗證 | AP 付款立帳 | ✅ 已修補：AllocatedAmount 超額 guard 已存在；`CreateAPPaymentAsync` 加入負匯率 guard；`ConfirmAPPaymentAsync` 整合 `IJournalEntryService?`，確認後自動過帳 借 應付帳款 (2100) / 貸 銀行存款 (1130) / FX 差額；測試 `APPaymentJournalTests` C9 驗證分錄借貸方金額 |
| DEF-FIN-02 | `ARReceiptService.ConfirmReceiptAsync` 缺少 JournalEntry 借貸平衡驗證 | AR 收款立帳 | ✅ 已修補：AllocatedAmount 超額 guard 已存在；`CreateARReceiptAsync` 加入負匯率 guard；`ConfirmARReceiptAsync` 整合 `IJournalEntryService?`，確認後自動過帳 借 銀行存款 (1130) / 貸 應收帳款 (1170) / FX 差額；測試 `ARReceiptJournalTests` D8 驗證分錄借貸方金額 |
| DEF-FIN-03 | `CurrencyService.ConvertToBaseAsync` 未攔截 `ExchangeRate ≤ 0` | 多幣別金額換算 | ✅ 已修補：`CreateAPPaymentAsync` + `CreateARReceiptAsync` 各加 `ExchangeRate < 0` guard 拋 `ArgumentOutOfRangeException`；測試 `MultiCurrencyAmountTests`（E6/E7 cases） |
| DEF-INV-03 | `JournalPostingService.PostAsync` 未驗證傳票借貸平衡（`ΣDebit ≠ ΣCredit`） | 庫存/AP/AR 所有立帳路徑 | ✅ 已整合：`InventoryPostingService.PostLineDetailAsync` 整合 `IJournalEntryService?`，過帳後自動建分錄：入庫 借 原料存貨 (1401) / 貸 進貨暫估應付 (2152)；出庫 借 報廢損失 (5210) 或銷貨成本 (5001) / 貸 原料存貨 (1401)；`StockMovementJournalTests` F7 驗證借貸方金額與 TotalCost 一致 |

---

## 7. 實作補強計畫

### 7.1 §5.1 APInvoice 輸入驗證

**實作位置**：`Modules/FIN/Services/APInvoiceService.cs`

**新增邏輯**：
- `CreateManualAsync`：逐行驗證 `Qty > 0`（主明細）、`UnitPrice >= 0`
- `GenerateFromGoodsReceiptAsync`：加入 PO 總量上限檢查（查 `PurchaseOrderLine.OrderQty` vs `ReceivedQty + 本次 GR qty`）

```csharp
// CreateManualAsync 新增驗證段落
foreach (var line in invoice.Lines.Where(l => l.Qty <= 0 || l.UnitPrice < 0))
    throw new InvalidOperationException(
        $"發票明細 {line.LineNo}：數量必須 > 0，單價必須 ≥ 0");
```

### 7.2 §5.2 委外超耗扣款計算服務

**實作位置**：`Modules/PUR/Services/OutsourceScrapCalculator.cs`（靜態計算器）

**計算公式**：
```
實際損耗率 = 1 − (receivedQty / issuedQty)
超耗比率 = max(0, 實際損耗率 − allowedScrapRate)
超耗數量 = 超耗比率 × issuedQty
ExcessScrapPenalty = Round(超耗數量 × materialUnitCost, 4, AwayFromZero)
```

**邊界條件**：
- `issuedQty ≤ 0` → `ArgumentOutOfRangeException`
- `receivedQty < 0` → `ArgumentOutOfRangeException`
- `實際損耗率 < 0`（超產）→ `InvalidOperationException`
- `ExcessScrapPenalty > processingFee`（扣款超額）→ 回傳罰款金額，由呼叫端決定是否拆分為 AR 或截斷至加工費上限

### 7.3 §5.4 ERP 憑證拋轉基礎設施

**實作位置**：
- `Models/BaseEntity.cs`：新增 `IErpSyncEntity` 介面、`ErpSyncStatus` enum
- `Modules/FIN/Models/APInvoice.cs`：實作 `IErpSyncEntity`
- `Modules/FIN/Services/ErpSyncService.cs`：拋轉狀態機邏輯

**狀態轉移規則**：
```
Pending → Synced（拋轉成功，寫入 ErpVoucherNo + ErpSyncTime）
Pending → Failed（拋轉失敗，遞增 ErpRetryCount）
Failed  → Synced（重試成功）
Failed  → Failed（重試失敗，超過 MaxRetry 則鎖定）
Pending/Failed → Excluded（手動排除，不再拋轉）
```

**最大重試次數**：`MaxErpRetryCount = 3`（硬式常數，後續可移至系統參數）

---

## 8. 資料一致性檢核測試作業方式

### 8.1 整體架構

本規格書（`business_data_flow_reconciliation.md`）是唯一真相來源，從設計到程式碼形成單向驅動關係：

```
業務流程規格書（本檔 §1–§3）
         ↓ 定義檢核點
EP/BVA 測試案例設計（§5）
         ↓ 驅動
服務層防護實作（Service guards）  ＋  xUnit 測試程式
         ↓
dotnet test（CI 自動執行）
```

業務資料流測試群組全數實作完成（含 §5.14–5.18 與 §3 稽核服務），**2026-07-05 查核各檔案例數如下、全數通過**：

| 場景 | 測試檔 | 測試數 |
|------|--------|--------|
| §5.1 P2P 採購付款核銷 | `PUR/P2PReconciliationTests.cs` | 19 |
| §5.2 委外超耗計算 | `PUR/OutsourceScrapCalculatorTests.cs` | 9 |
| §5.3 AVCO 庫存成本 | `INV/InventoryServiceTests.cs` | 17 |
| §5.4 ERP 憑證拋轉 | `FIN/ErpSyncServiceTests.cs` | 7 |
| §5.5/5.6 O2C 銷售收款 | `ORD/O2CReconciliationTests.cs` | 12 |
| §5.7/5.8 生產/委外 WIP | `MFG/WipReconciliationTests.cs` | 10 |
| §5.9 PO 超收與作廢回補 | `PUR/P2PDocFlowTests.cs` | 9 |
| §5.10 GR 過帳數量成本 | `PUR/GrStockPostingTests.cs` | 6 |
| §5.11 PO→GR→AP 金額鏈 | `PUR/AmountChainTests.cs` | 8 |
| §5.12 庫存模式資料流 | `INV/InvModeFlowTests.cs` | 10 |
| §5.13 WO→MaterialSlip 自動建立與 Issue 發料 | `MFG/WorkOrderApprovalSyncTests.cs`（8）、`MFG/WorkOrderLifecycleTests.cs`（+2）、`MFG/MaterialSlipControllerTests.cs`（18） | 28 |
| §5.14 PR→PO 轉換數字 | `PUR/PrToPOConversionTests.cs` | 9 |
| §5.15 AP 付款立帳 | `FIN/APPaymentJournalTests.cs` | 9 |
| §5.16 AR 收款立帳 | `FIN/ARReceiptJournalTests.cs` | 8 |
| §5.17 多幣別換算 | `FIN/MultiCurrencyAmountTests.cs` | 8 |
| §5.18 庫存過帳立帳 | `INV/StockMovementJournalTests.cs` | 7 |
| §3 稽核服務（DataAuditService）＋ §4.1 關帳擋關 | `ACT/DataAuditServiceTests.cs` | 56 |
| §4.2 稽核結果持久化（DataAuditRunService） | `ACT/DataAuditRunServiceTests.cs` | 3 |

---

### 8.2 測試設計方法

每個高風險資料流節點使用兩種技術設計案例：

**等價劃分（EP, Equivalence Partitioning）**：
找出「具有相同系統行為」的輸入集合，各取一個代表值覆蓋整個等價類，避免測試冗餘。
```
Valid EP   → 系統應正常處理並產生正確結果（主路徑驗證）
Invalid EP → 系統應攔截並拋出明確例外（防護機制驗證）
```

**邊界值分析（BVA, Boundary Value Analysis）**：
在等價類的邊界點測試精度與臨界行為，因錯誤最常發生在邊界：
```
精確等於邊界（= 上限） → 驗證不漏判（應放行）
超出邊界一個最小單位  → 驗證不誤判（應攔截）
容忍範圍臨界值        → 驗證 ±0.001、±0.01 等設計精度
```

---

### 8.3 測試技術基礎設施

#### 測試隔離
每個測試類別繼承 `TestBase`，constructor 建立以 `Guid.NewGuid()` 命名的獨立 InMemory 資料庫，測試間完全隔離，Dispose 時自動清除：
```csharp
// TestBase.cs
var options = new DbContextOptionsBuilder<ApplicationDbContext>()
    .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
    .Options;
```

#### 服務層直接測試（非 Controller 層）
直接實例化 Service，繞過 HTTP 管線，確保業務邏輯純粹可測：
```csharp
// 範例：O2CReconciliationTests
_arInvoiceSvc = new ARInvoiceService(_context, _mockDocNo.Object, _mockDt.Object, _mockCurrency.Object);
var arInv = await _arInvoiceSvc.GenerateFromShipmentAsync("GS001");
Assert.Equal(5250m, arInv.TotalAmount);
```

#### Mock 策略（最小化）
只 Mock 外部基礎設施依賴，DB 使用真實 EF InMemory 執行：
- `IDocNumberService`：Mock 回傳序號（`ARI000001`, `ARR000001` …）
- `IDateTimeService`：Mock 直接回傳輸入值
- `ICurrencyService.GetRateAsync`：Mock 回傳 `null`（Service 自動以 `1m` 處理本位幣）
- EF Core InMemory：**不** Mock，驗證真實的 LINQ 查詢與狀態轉換

#### 純函式計算器（無 DB 依賴）
`OutsourceScrapCalculator`、`WipBalanceCalculator`、`WorkOrderCostCalculator` 設計為靜態類別，不注入 DB，測試最快且完全確定性：
```csharp
var penalty = OutsourceScrapCalculator.Calculate(1000m, 900m, 0.05m, 50m);
Assert.Equal(2500m, penalty);  // (0.10-0.05) × 1000 × 50
```

---

### 8.4 服務層防護機制（Guards）

測試不只「觀察結果」，更驗證服務層有主動攔截非法輸入：

| 服務方法 | 攔截條件 | 例外型別 |
|---------|---------|---------|
| `APInvoiceService.CreateManualAsync` | qty ≤ 0 或 price < 0 | `InvalidOperationException` |
| `APInvoiceService.GenerateFromGoodsReceiptAsync` | GR qty > PO OrderQty | `InvalidOperationException` |
| `ARInvoiceService.CreateManualAsync` | qty ≤ 0 或 price < 0 | `InvalidOperationException` |
| `FinPaymentService.ConfirmARReceiptAsync` | allocated > outstanding | `InvalidOperationException` |
| `ErpSyncService.MarkSyncedAsync` | \|MES − ERP\| > 0.01 | `InvalidOperationException` |
| `OutsourceScrapCalculator.Calculate` | issuedQty ≤ 0 或 receivedQty < 0 | `ArgumentOutOfRangeException` |
| `WipBalanceCalculator.ComputeEndingWip` | netMaterialIssued < 0 | `ArgumentOutOfRangeException` |
| `InventoryPostingService.PostLineDetailAsync` | direction=1 且 UnitPrice < 0 | `InvalidOperationException` |
| `InventoryPostingService.PostDocSimplifiedAsync` | effectiveDirection=1 且 line.UnitPrice < 0 | `InvalidOperationException` |
| `PurchaseRequestService.ConvertToPOAsync` | `ConvertQty > PR.RequestQty - PR.ReleasedQty` 或 `PR.Status = Cancelled` | `InvalidOperationException` |
| `APPaymentService.ConfirmPaymentAsync` | `AllocatedAmount > APInvoice.TotalAmount - AlreadyPaid` | `InvalidOperationException` |
| `JournalPostingService.PostAsync` | `ΣDebit ≠ ΣCredit`（借貸不平衡） | `InvalidOperationException` |
| `CurrencyService.ConvertToBaseAsync` | `ExchangeRate ≤ 0` | `ArgumentOutOfRangeException` |
| `AccountingPeriodService.ClosePeriodAsync` | `RunPreClosingAuditAsync` 回報任何 §3 稽核異常 | `InvalidOperationException`（期間停留 Closing） |

---

### 8.5 規格書與測試的雙向追蹤

§6 覆蓋矩陣維護「案例設計（§5）→ 實作狀態」的完整追蹤：

```
§5.x EP/BVA 案例設計
      ↓
§6 矩陣（每列一個案例）
      ✅ 已覆蓋   → 有對應 [Fact]/[Theory] 測試方法，且通過
      ⚠️ 已知缺陷 → 登記 DEF-xxx，待後續 Sprint 補強
```

新增測試案例的標準流程：
1. 在 §5 補充 EP/BVA 設計說明
2. 在服務層加入對應防護邏輯（若為 Invalid 案例）
3. 撰寫 `[Fact]` 或 `[Theory]` 測試方法
4. 在 §6 矩陣新增對應列，標示 ✅

---

### 8.6 執行方式

```bash
# 跑所有資料流相關測試群組（日常開發最常用）
dotnet test SmartFactory.MES.Tests \
  --filter "FullyQualifiedName~Reconciliation|FullyQualifiedName~OutsourceScrap|FullyQualifiedName~ErpSync|FullyQualifiedName~P2PDocFlow|FullyQualifiedName~GrStockPosting|FullyQualifiedName~AmountChain|FullyQualifiedName~InvModeFlow"

# 跑單一流程（如只改 O2C）
dotnet test SmartFactory.MES.Tests \
  --filter "FullyQualifiedName~O2CReconciliation"

# 只跑庫存模式流程
dotnet test SmartFactory.MES.Tests \
  --filter "FullyQualifiedName~InvModeFlow"

# 只跑 §3 資料稽核服務（含關帳擋關整合）
dotnet test SmartFactory.MES.Tests \
  --filter "FullyQualifiedName~DataAuditServiceTests"

# 跑全部（PR 前確認無回歸）
dotnet test SmartFactory.MES.Tests
```

CI 在每次 Pull Request 自動執行。任何服務層修改只要破壞資料流邏輯，對應的 EP/BVA 測試即紅燈，阻止合併。

---

## 9. 未涵蓋項規劃（Backlog，2026-07-05 評估）

以下為本次評估確認**尚未涵蓋**、依價值/成本排序納入規劃的檢核項目：

| 編號 | 項目 | 內容與依據 | 前置條件 |
|------|------|-----------|---------|
| P-1 | WIP 期間平衡稽核 | ✅ **完成（2026-07-16，D4 選項 B）**：結構性守門（§3.10-1~4）＋D1-A 成本收斂＋**跨期 WIP 人工/製費結轉（期初快照結轉法）＋金額恆等式 `WIP_IDENTITY_DRIFT`/`WIP_VOID_LEAKAGE`（§3.10-5）**全數落地；`ActualRateReestimationService` 兩腿套期初凍結層，跨期完工 Final 低估已解。測試：單元 4 案＋整合 `WipCarryForwardTests` 兩期端到端 | — |
| P-2 | ~~逐工單成本回溯核對~~ | ✅ **完成（2026-07-06，D2 選項 A）**：§3.11 `WO_COST_JOURNAL_DRIFT` 材料腿＋人工/製費腿（Preliminary/Final 皆核）入 RunAllAsync；`JournalLine.SourceRef` 上線（migration `ACT_JournalLineSourceRef`）、重估調整分錄逐工單拆行。`SourceRef` 亦供未來成本中心維度共用 | — |
| P-3 | ~~IErpSyncEntity 擴大覆蓋（最小範圍）~~ | ✅ **2026-07-06 最小範圍完成（D3）**：`ARInvoice` 補拋轉 5 欄（migration `FIN_ARInvoiceErpSync`）＋`IErpSyncDocument` 介面（FIN 模組，Core 不動）＋`ErpSyncService` 泛型化（AP 簽章保留相容）＋DI 註冊＋§3.5 稽核擴大 AP＋AR。**`JournalEntry`/`StockMovement` 層級移列 P-4 前置**，待 ERP 介接方式定案一起設計 | 剩餘層級綁 P-4 |
| P-4 | ERP 金額雙向核對 | §2.2 規則 2：MES ↔ ERP 傳票金額比對；±0.01 尾差自動入尾差調整科目（§5.4 BVA） | 需 ERP 端介接（API 或中介表），無法純 MES 端實作 |
| P-5 | ~~Backflush 消耗核對~~ | ✅ **2026-07-05 已完成**：`AuditBackflushConsumptionAsync`（§3.8） | — |
| P-6 | ~~PO/SO 反正規化欄位漂移稽核~~ | ✅ **2026-07-05 已完成**：`AuditPoReceivedQtyDriftAsync` / `AuditSoShippedQtyDriftAsync`（§3.9）；退貨語意已釐清（不回沖累計欄位），並同步修正三處作廢回沖破口＋migration 重算歷史欄位 | — |
| P-7 | ~~容忍值統一為系統參數~~ | ✅ **2026-07-05 已完成**：`audit_qty_tolerance`（0.0001）/ `audit_amount_tolerance`（0.01，含 ERP 尾差）/ `fin_settle_tolerance`（0.001，AP/AR 核銷）三參數，未設定時退回預設、行為不變（取值依據見 §3 共通原則）；全套單元/整合回歸綠燈 | — |
| P-8 | ~~稽核結果 UI 與排程~~ | ✅ **2026-07-05 已完成**：`DataAuditRun/Finding` 持久化＋`DailyDataAuditJobService` 每日 02:00 排程＋RPT `DataAudit` 診斷畫面（§4.2） | — |

> 新增檢核的標準流程沿用 §8.5：先在 §3 補規則說明 → `DataAuditService` 加 `AuditXxxAsync` → `DataAuditServiceTests` 補案例 → §6 矩陣登記。
