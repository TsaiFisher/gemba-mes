# SmartFactory MES — 未完成模組規劃路線圖

> 文件日期：2026-06-03  
> 最後修訂：2026-06-11（章節重排、補齊 Entity 欄位、補設計缺口與 Phase A 驗收標準）  
> 範圍：委外（SUB）、收付款（FIN）、成本（CST）、結帳與 IFRS（ACT）、管理報表（RPT）

---

## 0. 現況盤點

> **⚠️ 2026-06-11 重新校準**：本文件初版（06-03）的盤點已嚴重落後於程式碼。經實際掃描原始碼後，
> 原列為「✗ 完全缺失 / 待開發」的 FIN、ACT、RPT 與 CST 多數已實作完成。下表為**真實現況**；
> Phase A–D 任務表（第 6 節）大部分已落地，僅保留少數真實缺口，詳見表後「剩餘真實工作」。

| 模組 | 已完成（實際程式碼） | 尚缺 |
|------|----------------------|------|
| BAS | 公司/幣別/UOM/稅別/倉庫/品號/工作中心/班別/排程 | — |
| INV | 庫存異動/調撥/報廢/樣品/借出/委外發退料/重工發退料/跨廠調撥；**AVCO**（`Inventory.UnitCost` 移動均價，InventoryService/PostingService）；**FIFO 成本層**（`InventoryCostLayerService`） | 期末盤點鎖倉與 `AccountingPeriod` 整合驗證 |
| PUR | 請購→採購→進貨→退貨；**委外收貨入庫 `OutsourceReceipt`**（INV `OI` Posting + AP 發票自動產生 + xmin 樂觀鎖）；**超耗扣款自動計算**（Confirm 呼叫 `OutsourceScrapCalculator` → `ExcessScrapPenalty`，AP 發票負項扣減，commit 9eb6ddf；成本源已改「發料時點 AVCO 逐料計算」符合 §1.5，見剩餘工作 §1） | — |
| ORD | 銷售→出貨→銷退 | AR Invoice／收款核銷已移至 **FIN** |
| MFG | 工單→派工→報工→MRP→委外採購單→領料；**重工工單**（`WorkOrderType.Rework` + `ReworkFromWorkOrderId`）；**工單實際成本**（`WorkOrderActualCost` + `WorkOrderCostCalculator`）；**WIP**（`WipBalanceCalculator`）；**實際費率期末重估回刷**（`ActualRateReestimationService`＋`DepartmentActualCost`＋`WipSnapshot`，2026-06-30，見 §3.3） | — |
| QCR | 不良代碼/檢驗規格/量具/檢驗單(`InspectionOrder`)/NCR；**IQC 進料檢驗**（`MobileInvController`，抽樣/VL/跳批）＋**IPQC 製程檢驗**（`MobileMfgController`，工序 RequiresInspection 觸發）＋**NCR 處置全連動**（Scrap→ScrapDoc、Rework→WO、**Return→採購退貨草稿單** `QcrReturnService` 2026-07-01，NCR↔退貨雙向連結 `GoodsReturnId` 2026-07-02）＋**SPC 製程能力分析**（`SpcAnalysis`，Cp/Cpk/超規＋管制圖趨勢，2026-07-02） | — |
| **FIN** | ✅ AP/AR Invoice、付款(`APPayment`)/收款(`ARReceipt`) 含 Allocation 核銷、`BankAccount`、AP/AR 帳齡、`ErpSyncService` | — |
| **CST** | ✅ 無獨立模組——成本邏輯落在 INV（cost layer/AVCO）+ MFG（`WorkOrderCostCalculator`/`WipBalanceCalculator`）；成本法選擇已存在於 **`ProductCategory.CostMethod`**（enum `SystemDefault/MovingAverage/Fifo` + 系統參數 `InvDefaultCostMethod` 後援，見 `InventoryPostingService.ResolveCostMethodAsync`） | ✅ **Landed Cost 分攤**（後端+畫面+JournalEntry 串接皆完成 2026-06-11） |
| **ACT** | ✅ `AccountingPeriod`、`JournalEntry`、月結 Checklist(`MonthEnd`)、NRV 評估、IAS 21 匯率重評(`FxRevaluation`)、IFRS 9 ECL(`EclAllowance`)；**期間鎖已接入各 posting service**（2026-06-12，見剩餘工作 §3） | — |
| **RPT** | ✅ 損益(P&L)、毛利(GrossMargin)、資產負債表(BalanceSheet)、Dashboard KPI；帳齡在 FIN；**委外加工對帳與損耗分析表**（`OutsourcePerformance`，2026-07-01）；**多維度銷售毛利分析**（`MarginAnalysis`，客戶/品號/月份，2026-07-02）；庫存週轉/滯銷/庫齡分桶/NRV 在 INV/ACT 模組（`InventoryTurnover`/`SlowMovingInventory`/`InventoryAging`/`NrvAssessment`）；**OEE 設備綜合效率**在 MFG 模組（`Oee`，A×P×Q，2026-07-02）；**產品邊際貢獻分析**（`ContributionMargin`，2026-07-02，變動占比取自工單成本要素、要素分類存系統參數可調） | — |

### 剩餘真實工作（取代原 Phase A–D 大部分項目）

1. ~~**委外超耗自動計算**~~ —— **✅ 全數完成**。（a）接入 Confirm（commit 9eb6ddf）：`OutsourceReceiptController.Confirm` → `ApplyScrapPenaltiesAsync` 寫入 `ExcessScrapPenalty`，`APInvoiceService.GenerateFromOutsourceReceiptAsync` 以負項扣減應付加工費。（b）**AVCO 精度收口（2026-07-01）**：成本來源已從「標準成本」改為 spec §1.5 的「**發料時點 AVCO 逐料計算**」——發料原料量與加權 AVCO 取自委外發料單（OO）的 `StockMovement` 出庫快照（`InventoryPostingService` 對出庫寫入 `inv.UnitCost`，即發料當下均價，與在途外料結轉同時點），BOM 標準用量取自 `RoutingBom.Quantity`，逐料以 `OutsourceScrapCalculator.CalculateMultiMaterial` 計算後加總（4 新增單元測試，共 13 綠）。**無需 `MaterialCarryingCost` 欄位/migration**：StockMovement 快照本身即凍結的發料成本。邊界：粒度為「單筆收貨 vs 該工單全部發料」，分批收貨跨批彙總對帳屬另案。
2. ✅ **Landed Cost 分攤**（§3.4）：計算器/Entity/Service/Migration/**分攤單畫面**/**JournalEntry 串接**皆已完成（2026-06-11）。
3. ~~**期間鎖倉前置檢查**（§8 B5）~~ —— **✅ 已完成（2026-06-12，見 overall-assessment.md P0-5；2026-07-03 覆核）**：以既有 `IAccountingPeriodService.AssertCanPostAsync` 為 guard，已接入 `InventoryPostingService`（INV 過帳/作廢全路徑）、`APInvoiceService`/`ARInvoiceService`（發票產生/手開/作廢）、`FinPaymentService`（付款收款確認/沖銷）、`JournalEntryService`（Closed 全擋＋Closing 僅允許 Adjustment）。委外收貨（OI posting＋AP 發票）與 CST 成本分錄經上述服務**傳遞性受保護**。
4. **管理會計報表**（§5.3）：**已大致補齊** —— 委外加工對帳與損耗分析表 `OutsourcePerformance`（2026-07-01）；庫存週轉率 `InventoryTurnover`（既有）；呆滯庫存清單 `SlowMovingInventory`（既有，單一門檻）；**存貨庫齡分桶分析 `InventoryAging`**（2026-07-01）；IAS 2 NRV 減損 `NrvAssessment`（ACT，既有）；**多維度銷售毛利分析 `MarginAnalysis`**（2026-07-02，客戶/品號/月份維度切換，營收＝已確認出貨、成本＝GS StockMovement 經 SourceDocNo 回溯）；**OEE 設備綜合效率 `Oee`**（2026-07-02，MFG 模組，A×P×Q：稼動時間取自 `ProductionSession`、計畫時間取自 `WorkCalendar`(TM_WKS+TM_WKO)、理想工時＝產出×`WorkOrderOperation.UnitTime`、良率取自 `ProductionReport`）；**產品邊際貢獻分析 `ContributionMargin`**（2026-07-02，路線 A：變動占比＝Σ(標記為變動的工單成本要素)/Σ(全要素)，取自 `WorkOrderActualCost`⨝`WorkOrder`；分類存**系統參數** `ContributionMargin.{Material,Labor,Overhead}Variable`，預設材料+人工＝變動、製費＝固定，查詢列可單次覆寫或儲存為預設；產品期間內無工單要素則退回整體平均占比並標記）。**§5.3 管理會計報表已全數具備。**
5. ~~QCR IQC/IPQC 與成本勾稽完整化~~ —— **已完成**。IQC（`MobileInvController`）與 IPQC（`MobileMfgController`，工序 `RequiresInspection` 觸發）檢驗流程早已具備（MIL-STD-1916 抽樣/VL 切換/跳批）；NCR 處置成本連動亦補齊：Scrap→ScrapDoc（過帳）、Rework→重工 WO、**Return→採購退貨草稿單**（`QcrReturnService`，2026-07-01，追溯 IQC→GoodsReceipt 帶入供應商/估價，Draft 由採購確認過帳，並於 NCR 回填 `GoodsReturnId` 雙向連結 2026-07-02）。**SPC 量測值趨勢分析**亦已完成（`SpcAnalysis`，2026-07-02，`SpcCalculator` 純函式算 Cp/Cpk/σ/UCL-LCL/超規，單一品號附 Chart.js 管制圖）。

> 註：§3.1 原列的 `Product.CostMethod` 並非真缺口——成本法選擇已以**品號類別層級**（`ProductCategory.CostMethod`）實作；
> 若未來需「單一品號覆寫類別」的更細粒度，才需新增 `Product.CostMethod`，屬選配增強而非缺口。

---

## 1. 委外模組（SUB）架構決策

### 1.1 現況問題

目前委外資料橫跨兩個模組：
- `MFG.OutsourcePurchaseOrder`：與生產派工連結的委外採購單
- `INV.OutsourceMaterialDoc`：委外發退料單（OO/OR 類型）

**缺少**：委外完工收貨（OutsourceReceipt）→ 庫存入帳的完整流程。

### 1.2 架構決策：維持分散 + 補完 PUR

| 方案 | 優點 | 缺點 | 決策 |
|------|------|------|------|
| A：獨立 SUB 模組 | 清晰、自完整 | 大量重構，需移動現有 Entity | ✗ |
| B：擴充現有模組 | 最小改動 | 略顯分散 | **✅ 採用** |

**方案 B 配置**：

```
MFG.OutsourcePurchaseOrder  ← 委外採購單（由派工產生）
INV.OutsourceMaterialDoc    ← 發料/退料（OO/OR）
PUR.OutsourceReceipt        ← 委外完工收貨（新增，類似 GoodsReceipt）
INV posting OI              ← 收貨時觸發庫存增加（成品入庫）
INV posting OO 反沖         ← 退回外料的庫存沖回
```

### 1.3 委外完整資料流

```
委外需求（MFG MRP / 派工）
  │
  ▼
OutsourcePurchaseOrder（MFG）
  │  確認後
  ├─► OutsourceMaterialDoc TypeCode=OO（INV）— 發外料
  │     └─► 庫存 OUT：原料從成品倉 → 在途委外
  │
  ▼  委外廠完工交貨
OutsourceReceipt（新增於 PUR）
  │  確認後
  ├─► INV posting TypeCode=OI — 成品入庫
  ├─► QC IQC 觸發（可選）
  └─► 委外 AP Invoice（FIN）— 產生應付帳款

委外退貨
  OutsourceMaterialDoc TypeCode=OR — 退回外料
  └─► 庫存 IN：原料從在途委外 → 成品倉歸回
```

### 1.4 需新增的 Entity

```csharp
// PUR 模組新增
class OutsourceReceipt : IPlantEntity, IDataAuthEntity
{
    string OutsourceReceiptId;       // PK（單號格式依廠別 SystemParam）
    string OutsourcePurchaseOrderId; // FK → MFG.OutsourcePurchaseOrder
    DateOnly ReceiptDate;
    string SupplierCode;             // 委外廠商（全域主檔；查詢不得用 PlantId 過濾）
    string CurrencyCode;             // 加工費幣別（全域主檔）
    decimal ExchangeRate;            // 交易日匯率（換算本位幣，方向見 currency.md）
    DocStatus Status;                // Draft / Confirmed / Void
    DocumentApprovalStatus ApprovalStatus; // 線上簽核狀態（IApprovalService）
    string? PlantId { get; set; }    // IPlantEntity（作業單據，嚴格廠別隔離）
    IList<OutsourceReceiptLine> Lines;
    IList<OutsourceReceiptLocationDetail> LocationDetails; // 入庫倉/儲位明細
}

class OutsourceReceiptLine
{
    int LineNo;
    string ProductCode;
    decimal QtyReceived;
    decimal ProcessingFeeRate;     // 加工單價（來自委外採購單）
    decimal ProcessingFeeAmount;   // 應付加工費 = 實收數量 * 加工單價
    decimal MaterialCarryingCost;  // 在途外料結轉成本（系統按發料 AVCO 滾算）
    decimal UnitCost;              // 成品入庫單價 = (在途外料結轉成本 + 應付加工費) / 實收數量
    decimal ExcessScrapPenalty;    // 委外超耗扣款（轉入 AP 負項）
}

class OutsourceReceiptLocationDetail   // 入庫倉/儲位明細（與 GoodsReceiptLocationDetail 同結構）
{
    int LineNo;                    // 對應 OutsourceReceiptLine
    string WarehouseCode;          // 成品入庫倉（套 wh-loc 規則：倉別過濾儲位）
    string LocationCode;           // 儲位
    decimal Qty;                   // 該儲位入庫數量（合計 = Line.QtyReceived）
}
```

**委外收貨服務介面**：

```csharp
interface IOutsourceReceiptService
{
    // 由委外採購單帶出可收貨明細（加工單價、在途外料），預設 Draft
    Task<OutsourceReceipt> CreateFromPurchaseOrderAsync(string outsourcePurchaseOrderId, CancellationToken ct);

    // 確認過帳：滾算成品入庫單價 → INV posting OI → 計算超耗 → 回傳收貨單
    // 須在 ConfirmAsync 內完成「成品入庫單價 = (在途外料結轉成本 + 應付加工費) / 實收數量」
    Task<OutsourceReceipt> ConfirmAsync(string receiptId, CancellationToken ct);

    // 作廢：反沖 INV posting，回寫委外採購單已收量
    Task VoidAsync(string receiptId, CancellationToken ct);
}
```

> AP Invoice 由 `IAPInvoiceService.GenerateFromOutsourceReceiptAsync`（見 2.4）於收貨確認後產生，
> 服務邊界刻意分離：收貨服務只負責庫存與成本，帳款交由 FIN。

### 1.5 委外超耗與扣款規則（中小型製造業實務）

- **超耗定義**：委外發料後，實際完工入庫的成品折算原料量若低於領用量，且損耗率高於「合約允收損耗率」，即判定為超耗。
- **計算公式**：
  * `實際損耗量 = 發料原料量 - (實收成品量 × BOM 標準用量)`
  * `實際損耗率 = 實際損耗量 / 發料原料量`
  * `超耗扣款金額 = max(0, 實際損耗量 - (發料原料量 × 合約允收損耗率)) × 原料當下 AVCO`
- **財務處理**：超耗扣款金額（`ExcessScrapPenalty`）將於產生 `APInvoice` 時，作為該發票明細的負項直接扣減應付加工費，不單獨開發行折讓單，以符合中小型企業簡化對帳之實務。
- **AVCO 時點對齊（重要）**：本節公式中的「原料當下 AVCO」與 `OutsourceReceiptLine.MaterialCarryingCost`（在途外料結轉成本）**必須採用同一時點**——即「發料單（`OutsourceMaterialDoc` TypeCode=OO）過帳當下」的 AVCO 均價，而非收貨日均價。原因：外料於發料時即移轉至「在途委外」，其結轉成本已凍結；若超耗以收貨日均價計算，會與已結轉的在途成本不一致，導致對帳差額。實作時超耗計算應讀取發料 StockMovement 的 `UnitCost` 快照。

### 1.6 重工（Rework）資料流

目前 `INV.ReworkMaterialDoc` 僅處理重工發退料，缺少：
- 重工工單（連結原製令的返修工單）
- 重工完工後的重新入庫（分品質等級：良品/降等品/報廢）

```
品質異常通知（QCR）
  │
  ▼
重工工單（MFG.WorkOrder，TypeCode = Rework，連結原工單）
  │
  ├─► ReworkMaterialDoc TypeCode=RM（發重工料）
  │     └─► INV OUT
  │
  ├─► 生產報工（同一般派工）
  │
  └─► 重工完工入庫
        ├─► 良品：INV IN（成品倉，標準成本）
        ├─► 降等品：INV IN（次品倉，調降成本）
        └─► 報廢：ScrapDoc 沖銷
```

**WorkOrder 需新增欄位**：
```csharp
WorkOrderType Type;  // Normal / Rework / Outsource
string? SourceWorkOrderId;   // 重工/委外時連結原工單
```

---

## 2. 收付款模組（FIN）

### 2.1 模組範圍

| 子功能 | 說明 |
|--------|------|
| AP Invoice | 由進貨/委外收貨自動產生或手動建立 |
| AR Invoice | 由出貨自動產生或手動建立 |
| AP Payment | 付款作業：現金/匯款/票據 |
| AR Receipt | 收款作業：現金/匯款/票據 |
| BankAccount | 銀行帳戶餘額管理 |
| AgingReport | 應收/應付帳齡報表 |

### 2.2 資料流

```
採購進貨（GoodsReceipt 確認）
  └─► APInvoice 自動產生（金額 = 進貨數量 × 採購單價）
        └─► APPayment（付款後核銷）
              └─► 帳款結清

銷售出貨（GoodsShipment 確認）
  └─► ARInvoice 自動產生（金額 = 出貨數量 × 銷售單價）
        └─► ARReceipt（收款後核銷）
              └─► 應收結清
```

### 2.3 核心 Entity

```csharp
// FIN 模組
class APInvoice : IPlantEntity
{
    string APInvoiceId;
    string SupplierCode;
    DateOnly InvoiceDate;
    DateOnly DueDate;
    string CurrencyCode;
    decimal TaxRate;
    decimal TaxAmount;
    decimal TotalAmount;
    APInvoiceStatus Status;    // Open / PartialPaid / Paid / Void
    string? SourceDocType;     // GoodsReceipt / OutsourceReceipt
    string? SourceDocId;
    IList<APInvoiceLine> Lines;
    IList<APPayment> Payments;
    IList<APInvoiceClearing> Clearings; // 預付款核銷記錄
}

class APInvoiceLine
{
    int LineNo;
    string? ProductCode;
    decimal Qty;
    decimal UnitPrice;
    decimal LineAmount;
    bool IsPenalty;            // 是否為扣款項（例如委外超耗扣款，金額為負）
}

class APPayment : IPlantEntity
{
    string APPaymentId;
    string APInvoiceId;
    DateOnly PaymentDate;
    PaymentMethod Method;      // Cash / Wire / Cheque / Bill
    string CurrencyCode;
    decimal Amount;
    decimal ExchangeRate;      // 換算本位幣匯率
    decimal AmountLocal;       // 本位幣金額
    string? BankAccountId;
    decimal ExchangeGainLoss;  // IFRS 9 匯兌損益
}

class APPrepayment : IPlantEntity
{
    string APPrepaymentId;
    string SupplierCode;
    DateOnly PrepaymentDate;
    decimal Amount;
    string CurrencyCode;
    decimal ExchangeRate;
    decimal Balance;           // 剩餘未核銷金額
    PrepaymentStatus Status;   // Open / Cleared / Void
}

class APCreditNote : IPlantEntity // 進貨折讓單
{
    string APCreditNoteId;
    string SupplierCode;
    DateOnly DocDate;
    string CurrencyCode;
    decimal TotalAmount;
    CreditNoteStatus Status;   // Open / Cleared / Void
    string? SourceDocType;     // PurchaseReturn / OutsourceReceipt
    string? SourceDocId;
}

class ARInvoice : IPlantEntity  // 結構對稱 APInvoice
{
    string ARInvoiceId;
    string CustomerCode;       // 客戶（全域主檔）
    DateOnly InvoiceDate;
    DateOnly DueDate;
    string CurrencyCode;
    decimal TaxRate;
    decimal TaxAmount;
    decimal TotalAmount;
    ARInvoiceStatus Status;    // Open / PartialReceived / Received / Void
    string? SourceDocType;     // GoodsShipment
    string? SourceDocId;
    IList<ARInvoiceLine> Lines;
    IList<ARReceipt> Receipts;
    IList<ARInvoiceClearing> Clearings; // 預收款核銷記錄
}

class ARReceipt : IPlantEntity  // 結構對稱 APPayment
{
    string ARReceiptId;
    string ARInvoiceId;
    DateOnly ReceiptDate;
    PaymentMethod Method;      // Cash / Wire / Cheque / Bill
    string CurrencyCode;
    decimal Amount;
    decimal ExchangeRate;
    decimal AmountLocal;
    string? BankAccountId;
    decimal ExchangeGainLoss;  // IFRS 9 匯兌損益
}

class ARPrepayment : IPlantEntity // 結構對稱 APPrepayment
{
    string ARPrepaymentId;
    string CustomerCode;
    DateOnly PrepaymentDate;
    decimal Amount;
    string CurrencyCode;
    decimal ExchangeRate;
    decimal Balance;           // 剩餘未核銷金額
    PrepaymentStatus Status;   // Open / Cleared / Void
}

class ARCreditNote : IPlantEntity // 銷貨折讓單，結構對稱 APCreditNote
{
    string ARCreditNoteId;
    string CustomerCode;
    DateOnly DocDate;
    string CurrencyCode;
    decimal TotalAmount;
    CreditNoteStatus Status;   // Open / Cleared / Void
    string? SourceDocType;     // SalesReturn
    string? SourceDocId;
}

class BankAccount : IPlantEntity
{
    string AccountId;
    string BankName;
    string AccountNo;
    string CurrencyCode;
    decimal Balance;           // 即時餘額（每筆 Payment/Receipt 更新）
    byte[] RowVersion;         // 樂觀鎖（PostgreSQL xmin / concurrency token）
}
```

### 2.6 預收付款與折讓核銷規則

- **預收款/付款流程**：當交易發生前支付/收取訂金時，建立 `Prepayment` 傳票。後續產生發票時，透過發票核銷畫面進行 `Prepayment` 與發票的沖銷，系統更新兩者的未清餘額。
- **折讓對沖**：進退貨或銷退產生折讓單後，會計人員可手動或由系統批次將折讓單與對應的未結清發票（Open Invoice）進行對沖（Clearing），沖減應收/應付帳款餘額。

### 2.4 FIN 服務介面

```csharp
interface IAPInvoiceService
{
    Task<APInvoice> GenerateFromGoodsReceiptAsync(string goodsReceiptId, CancellationToken ct);
    Task<APInvoice> GenerateFromOutsourceReceiptAsync(string receiptId, CancellationToken ct);
    Task PayAsync(string invoiceId, PaymentRequest req, CancellationToken ct);
    Task<decimal> GetOutstandingBalanceAsync(string supplierCode, CancellationToken ct);
}

interface IARInvoiceService
{
    Task<ARInvoice> GenerateFromShipmentAsync(string shipmentId, CancellationToken ct);
    Task ReceiveAsync(string invoiceId, ReceiptRequest req, CancellationToken ct);
    Task<AgingReport> GetAgingAsync(DateOnly asOf, CancellationToken ct);
}
```

### 2.5 多幣別處理規則（IFRS 9 / IAS 21）

- 入帳：以**交易日匯率**（`CurrencyRate.Rate`）換算本位幣
- 期末重評：應收/應付以**期末匯率**重評，差額記「匯兌損益」
- 付款/收款：**結算匯率** vs 帳面匯率差異 = 已實現匯兌損益

> **併發控制**：`BankAccount.Balance` 採即時更新，`APPayment`/`ARReceipt` 過帳時須以 `RowVersion` 樂觀鎖更新餘額；
> 遇 `DbUpdateConcurrencyException` 應重讀餘額後重試（參考 INV `ReserveAsync` 可用量守門的併發處理經驗）。

---

## 3. 成本模組（CST）

### 3.1 成本方法

| 品號類型 | 成本方法 | 現況 |
|----------|----------|------|
| 原料/購入品 | AVCO（移動加權平均） | 部分實作（缺期末重算） |
| 購入品（冷凍成本） | FIFO | ✅ 已實作（2026-07-09 複查）：`InventoryCostLayer` 建層 `InventoryCostLayerService.AddLayerAsync`、出庫 `ConsumeLayersAsync` 依 `ReceiptDate` 先進先出扣減，已接入 `InventoryPostingService` |
| 製成品 | 標準成本 + 工單實際成本 | 標準成本已有 Calculator |
| 委外成品 | 委外單價 + 外料成本 | 缺 |

**成本法選擇鍵（需補欄位）**：上表「品號類型 → 成本法」的對應，需在**品號主檔**新增 `Product.CostMethod` 列舉欄位明確標記，不可靠品號類別硬編碼判斷。

```csharp
enum CostMethod { Avco, Fifo, Standard }   // 預設 Avco
// Product 新增：CostMethod CostMethod { get; set; } = CostMethod.Avco;
```

- 原料/購入品 → `Avco`；指定冷凍/批號管控品 → `Fifo`；製成品 → `Standard`（+ 工單實際成本回刷）。
- 委外成品沿用其製成品設定（`Standard`），入庫單價由 1.4 公式覆寫。
- INV/CST posting 依 `Product.CostMethod` 決定走 AVCO 滾算或 FIFO 層扣減；**同一品號的成本法一經有異動紀錄即不可變更**（避免成本層斷裂）。

### 3.2 AVCO 完整流程 — ✅ 已實作（2026-07-03 覆核更新標題）

> 實作落點：`Inventory.UnitCost` 即移動均價欄位（`InventoryService`/`InventoryPostingService` 滾算），
> **未新增 `AverageCost` 欄位**——原規劃欄位由既有 `UnitCost` 承載，語意相同。以下保留原設計說明。

```
每次庫存異動（IN）
  ├─► 更新 Inventory.AverageCost
  │     公式：(舊餘量 × 舊均價 + 入庫量 × 入庫單價) / 新總量
  └─► 記錄 StockMovement.UnitCost = 當下均價

每次庫存異動（OUT）
  └─► StockMovement.UnitCost = 異動當時的 AverageCost
```

**需新增欄位**：`Inventory.AverageCost decimal(18,6)`

### 3.3 工單實際成本計算與費率重估

```
工單完工時觸發：
  實際料工費
    ├─► 材料：MaterialIssueSlip 明細 × 當時 AVCO 均價
    ├─► 人工（期中）：ProductionSession 工時 × WorkCenter.LaborRate（標準費率）
    ├─► 人工（期末重估）：ProductionSession 工時 × 實際費率（當月部門實際薪資 / 實際總報工工時）
    ├─► 製費（期中）：機台工時 × WorkCenter.MachineRate（標準費率）
    └─► 製費（期末重估）：報工工時 × 實際製造費用率（當月部門實際折舊及水電等 / 實際總報工工時）

  差異分析（Variance）
    ├─► 料差 = 實際材料成本 - 標準材料成本
    ├─► 效率差 = (實際工時 - 標準工時) × 標準費率
    └─► 費率差 = (實際費率 - 標準費率) × 實際工時
```

**實際費率重估邏輯**：
中小型製造業於期中採用「標準費率」以進行即時報價與毛利評估。期末進行月結時，系統收集各工作中心（製造部門）當月的實際總費用（含直接薪資、折舊、水電費等，來自會計系統），除以該部門當月的實際總報工工時，算出「實際人工/製造費率」，並回刷更新當月完工工單與在製品的成本。

**需新增 Entity**：
```csharp
class WorkOrderActualCost : IPlantEntity
{
    string WorkOrderId;        // FK
    decimal ActualMaterial;
    decimal ActualLabor;
    decimal ActualOverhead;
    decimal VarMaterial;       // 料差
    decimal VarLaborEfficiency; // 效率差
    decimal VarOverheadRate;   // 費率差
    DateOnly CostDate;
    CostStatus Status;         // Preliminary / Final
}
```

**實作進度（2026-06-30）✅ 已完成**：

- ✅ **分子來源（可插拔）**：`DepartmentActualCost`（MFG.Models，期間×廠別×工作中心 唯一）持久化月結手動輸入的部門實際人工/製費；`IDepartmentActualCostProvider`/`PersistedDepartmentActualCostProvider`（MFG.Services）抽象之，未來成本中心標記到位後可換 GL 自動歸集 provider，重估引擎不動。
- ✅ **重估引擎**：`ActualRateReestimationService`（MFG.Services）。實際費率 = 部門實際費用 ÷ 當月工時池；工時未持久化，由 `ProductionSession`/`ProductionSetup` 的 StartAt/EndAt 還原（人工池=Σ session 工時；製費池=Σ session+setup 工時，與 DispatchController §16.3 累計口徑一致），經 `WorkOrderOperation.WorkCenterId` 對應工作中心。
- ✅ **回刷**：完工工單（`WorkOrderActualCost` Preliminary）以實際費率重算 ActualLabor/ActualOverhead 與差異後翻 Final；在製工單（InProgress）以實際費率重估產 `WipSnapshot`（§3.5）。材料無費率可重估，WipMaterial 取已實現 `wo.ActualMaterialCost`。
- ✅ **調整分錄**：彙總完工工單人工/製費調整淨額，經 `IJournalEntryService.CreateAdjustmentEntryAsync`（新增；Adjustment 型為 Closing 期間唯一可入帳型別）產平衡分錄（5110 直接人工-已分配 / 5120 製造費用-已分配 / 5130 製造成本差異，新增 `StandardAccounts.MfgCostVariance`）。受期間鎖守護（Closed 擋）。
- ✅ **串接**：`MonthEndController.RunActualRateReestimation`（比照 RunFxRevaluation，先 upsert 部門費用再回刷）；DI 註冊於 MFGModule；migration `MFG_ActualRateReestimation`（2 新表，snapshot 一致無漂移）。
- ✅ **驗證**：整合測試 `ActualRateReestimationTests`（真 PG，端到端：完工回刷+WIP 快照+調整分錄借貸平衡；已紅先綠後驗鑑別力）。單元 1530 / 整合 58 全綠。
- ✅ **部門實際費用維護畫面（2026-06-30 補）**：`DepartmentActualCostController`（MFG 模組，依 `new-screen.md` + `new-screen-projectz.md` 規範；繼承 `ModuleControllerBase`，JsonOk/JsonFail/ValidationPartial + `_CrudPanelScripts` + `data-action` 委派，單一本位幣 `BaseCurrencyDp` 顯示/輸入精度）。選單置於 ActManagement 群組（MonthEnd 之後）。唯一鍵（廠別+期間+工作中心）以 app 層 `ValidateAsync` 友善防重 + PG 唯一索引雙閘；未新增 migration、無 snapshot 漂移。測試：單元 9 + 整合 2（未登入轉址、真 PG 唯一索引防重）全綠。與 MonthEnd `RunActualRateReestimation` POST body 為同一張表的兩條輸入路徑。
- ⏳ **後續**：GL 自動歸集 provider（待成本中心標記基建；屆時替換 `IDepartmentActualCostProvider` 實作即可，引擎／JE／WIP／維護畫面皆不動）。

### 3.4 採購分攤費用（Landed Cost）分攤

- **業務需求**：採購原料時發生的國際運費、進口關稅、保險費等，須依 IAS 2 滾入存貨價值。
- **計算方法**：系統提供分攤單（LandedCostAllocation），可選取多筆進貨單品號，依「採購金額比例」、「採購數量比例」或「重量比例」將外包費用分攤至各進貨明細，重新計算其入庫 UnitCost 並更新 AVCO。

**實作進度（2026-06-11）**：採 **AVCO 回溯策略 (a)**（只調在庫餘量均價，已出庫對應轉費用 PPV；不破壞已關帳期間）。

- ✅ **分攤核心計算器**：`LandedCostAllocator`（`Modules/INV/Services/LandedCostAllocator.cs`，純函式 + 10 EP/BVA 測試）。
  三種基準、尾差補進最大權重行（Σ ≡ 總額）、回算新單價、數量 0 不除零。
- ✅ **Entity + DbContext + Migration**：`LandedCostAllocation` / `LandedCostAllocationLine`（INV.Models），
  DbSet + Fluent（xmin 樂觀鎖、唯一索引、Cascade）+ `DocNumberConfig` seed（Prefix `LCA`），
  Migration `20260611004209_AddLandedCostAllocation`（僅 2 表 + seed，無 snapshot 漂移）。
- ✅ **`ILandedCostService.ConfirmAsync`**（`Modules/INV/Services/LandedCostService.cs`）：依基準分攤，
  在庫餘量比例資本化（更新 `Inventory.UnitCost` AVCO；FIFO 未耗用成本層同步加單位成本），
  已出庫對應部分記 `ExpensedCost`（PPV）。DI 已註冊；5 個整合測試（在庫/半出庫/無在庫/FIFO/狀態守門）全綠。
- ✅ **分攤單畫面**（`LandedCostAllocationController` + `Index`/`_Form` + `LandedCostAllocationFormVm`）：
  依 new-screen / new-screen-projectz 規範——查詢列/分頁/排序/匯出（xlsx·csv·json）、Side Panel offcanvas + Toast +
  Anti-Forgery、`ModuleControllerBase`（`PrepareIndexPermissionsAsync`）、xmin 樂觀鎖、簽核串接（Submit/Withdraw/Confirm）、
  幣別小數（`CurrencyStepHelper`，step="any"）、**進貨明細選入器**（`GoodsReceiptLines` JSON 端點 + modal；開啟前搬 `document.body` 避免灰屏）。
  選單登錄於 `ProjectZMenuContributor`（InvManagement）+ `ProjectZSharedResource`；4 resx 齊備；R5b 驗證欄位名補入 Core `SharedResource`。
  14 個 Controller 單元測試全綠；R0–R12/Cx 慣例測試全綠（R7 依既有慣例 baseline）。
- ✅ **JournalEntry 串接**（`LandedCostService.PostJournalAsync`，2026-06-11）：確認分攤單後彙總產生自動分錄
  （借 `1401` 原料存貨＝Σ資本化、借 `5151` 進料費用差異＝Σ PPV／已出庫對應、貸 `2153` 暫估應付運費 GR/IR＝分攤總額），
  金額為零不產生分錄；科目常數集中於 `Modules/ACT/StandardAccounts.cs`（首個 `IJournalEntryService` 消費者，供後續過帳重用）。
  4 個過帳整合測試（全在庫/半出庫/無在庫/零金額）全綠。

### 3.5 在製品（WIP）評估

```
月末 WIP 評估：
  對所有「進行中」工單（Status = InProgress）：
    WIP金額 = 已領料成本 + 已報工人工 + 已報工製費（期末皆以實際費率重估）
    記錄至 WIPSnapshot（新 Entity）

WIPSnapshot
  ├─► WorkOrderId
  ├─► PeriodId（FK → AccountingPeriod）
  ├─► WIPMaterial / WIPLabor / WIPOverhead
  └─► QtyCompleted / QtyInProcess
```

### 3.6 期末成本分攤（Cost Allocation）

```
成本滾算順序：
  1. 進貨分攤費用分攤（Landed Cost Allocation）
  2. 計算並更新原料期末 AVCO 均價
  3. 月結時收集實際製造費用，計算各工作中心之實際人工/製造費率
  4. 計算製成品實際成本（回刷工單實際成本，重算完工入庫單價）
  5. 評估在製品 WIP 價值（以實際費率更新 WIPSnapshot）
  6. 計算標準成本 vs 實際成本之各項差異（VarMaterial / VarLabor / VarOverhead）
```

---

## 4. 結帳與 IFRS（ACT）

### 4.1 會計期間管理

```csharp
class AccountingPeriod
{
    string PeriodId;       // 格式：YYYYMM，如 "202601"
    int Year;
    int Month;
    DateOnly StartDate;
    DateOnly EndDate;
    PeriodStatus Status;   // Open / Closing / Closed / Reopened
    DateTime? ClosedAt;
    string? ClosedBy;
}
```

**期間控制規則**：
- 已關帳（Closed）期間：不允許任何庫存異動、AP/AR 新增
- Closing 狀態：僅允許結帳調整分錄
- 重開帳（Reopen）：需雙重核准，留審計軌跡

### 4.2 結帳流程

```
月末結帳 Checklist（ChecklistItem Entity）：
  □ 1. 確認所有進貨/出貨已過帳（DocStatus = Confirmed）
  □ 2. 盤點差異調整完成（StockAdjustment）
  □ 3. 完成 WIP 評估快照
  □ 4. 計算工單實際成本
  □ 5. 更新 AVCO 均價
  □ 6. 應收/應付期末匯率重評（IFRS 9 / IAS 21）
  □ 7. 計算折舊（若有 FA 模組）
  □ 8. 封鎖庫存異動（PeriodStatus = Closing）
  □ 9. 產生期末分錄（外幣重評／NRV／ECL 入帳，`PeriodEndAdjustmentService`；可重跑、只補差額）
  □ 10. 關帳（PeriodStatus = Closed）
```

### 4.3 日記帳/總帳（簡化版）

> 本 MES 不作完整 ERP 會計，採「**管理日記帳**」設計：
> 每筆業務交易產生對應的借貸記錄，可匯出至外部會計系統（如鼎新/SAP）。

```csharp
class JournalEntry
{
    string JournalId;
    string PeriodId;          // FK → AccountingPeriod
    DateOnly EntryDate;
    string SourceDocType;     // GoodsReceipt / Shipment / APPayment ...
    string SourceDocId;
    JournalEntryType Type;    // Automatic / Manual / Adjustment
    IList<JournalLine> Lines;
}

class JournalLine
{
    string AccountCode;       // 會計科目
    string AccountName;
    decimal Debit;
    decimal Credit;
    string CurrencyCode;
    decimal Amount;           // 外幣金額
    decimal ExchangeRate;
    decimal AmountLocal;      // 本位幣
    string? CostCenter;
}
```

### 4.4 標準會計科目對應與 GR/IR 暫估機制

為了符合會計內控制度並避免提前認列應付帳款，系統導入 **GR/IR（Goods Received/Invoice Received）暫估機制**：

| 業務交易 | 借方 | 貸方 | 說明 |
|----------|------|------|------|
| 採購進貨收料 | 原料存貨 | 暫估應付帳款（GR/IR） | 依收料單單價暫估 |
| 收到發票（核銷） | 暫估應付帳款（GR/IR） | 應付帳款（AP） | 依發票實際金額核銷 |
| 發票與進貨單價差異 | 進料差異（PPV，當期費用） | 暫估應付帳款（GR/IR） | 價差直接列費用，不回刷存貨 |
| 銷售出貨（成本） | 銷售成本 | 製成品存貨 | |
| 銷售出貨（收入） | 應收帳款 | 銷售收入 | |
| 付款 | 應付帳款 | 銀行存款 | |
| 收款 | 銀行存款 | 應收帳款 | |
| 委外完工收料 | 製成品存貨（暫估） | 暫估加工費（GR/IR） + 在途委外（原料成本結轉） | 暫估應付加工費 |
| 收到委外加工發票 | 暫估加工費（GR/IR） | 應付帳款（AP） | 依實際加工費核銷 |
| 委外超耗扣款 | 暫估加工費（GR/IR） | 委外超耗損失（費用類） | 作為發票負項扣減之沖銷分錄 |
| 材料領用（MFG） | 在製品（WIP） | 原料存貨 | |
| 工單完工入庫 | 製成品存貨 | 在製品（WIP） | |
| 工單實際成本重評差異 | 差異科目（料/工/費差異） | 在製品（WIP） | 結轉工單差異 |
| 期末匯率重評（利益） | 1170 應收／2100 應付 | 4190 匯兌利益 | 月結步驟 9 入帳、逐張拆行；**次期初（期末次日）全額迴轉**，收付款時的已實現損益仍以入帳匯率計 |
| 期末匯率重評（損失） | 5199 匯兌損失 | 1170 應收／2100 應付 | 同上。`ExchangeGainLoss` 一律公司立場（正＝利益），AP＝帳面本幣 − 重評本幣 |
| NRV 存貨跌價（增提／迴轉） | 5240 存貨跌價損失 ／ 1419 | 1419 備抵存貨跌價損失 ／ 5240 | 月結步驟 9：備抵調到等於本期評估額（差額＝評估 − 科目餘額）；不貸 1401 以免破壞 INV_ROLLFORWARD |
| ECL 備抵（增提／迴轉） | 6250 預期信用減損損失 ／ 1179 | 1179 備抵損失-應收帳款 ／ 6250 | 同上；本期沒跑評估則略過，不視為 0 |

**委外加工費價差（PPV）處理**：與採購進貨一致——委外發票實際加工費 vs 收貨單暫估加工費的差異，**直接列當期「加工費差異（PPV）」費用，不回刷製成品存貨成本**。對應分錄：

| 業務交易 | 借方 | 貸方 | 說明 |
|----------|------|------|------|
| 委外發票加工費 > 暫估 | 加工費差異（PPV，當期費用） | 暫估加工費（GR/IR） | 補差額 |
| 委外發票加工費 < 暫估 | 暫估加工費（GR/IR） | 加工費差異（PPV，當期費用） | 沖回差額 |

> 理由與採購 PPV 相同：避免發票延遲到貨時回刷已出庫成品成本，造成成本層追溯困難。

### 4.5 IFRS 合規重點

| 準則 | 業務影響 | 實作需求 |
|------|----------|----------|
| **IAS 2 存貨** | 存貨以成本與淨變現價值孰低入帳；採購附帶費用滾入成本。 | 新增 NRV 評估功能（品號售價 vs 帳面成本比對）、新增 Landed Cost 分攤。 |
| **IAS 21 外幣** | 期末匯率重評應收/應付 | APInvoice / ARInvoice 期末重評批次 |
| **IFRS 9 金融工具** | 應收帳款預期信用損失（ECL） | 帳齡分析 + 呆帳準備計算 |
| **IFRS 15 收入認列** | 「履約義務滿足」才認列收入 | 出貨確認時才產生 ARInvoice（已符合） |
| **IFRS 16 租賃** | 使用權資產 + 租賃負債 | 需新增 FAModule（固定資產，Phase N+1） |
| **IAS 36 資產減損** | 每年測試資產是否減損 | 存貨 NRV 低於帳面 → 減損損失 |
| **IAS 12 所得稅** | 遞延所得稅 | 與外部會計系統配合，MES 僅提供基礎數字 |

#### IAS 2 NRV 評估（Inventory Impairment）

```csharp
class InventoryNrvAssessment : IPlantEntity
{
    string PeriodId;
    string ProductCode;
    decimal CarryingCost;     // AVCO 帳面成本
    decimal NrVPrice;         // 預期銷售單價（取近 90 天平均 AR Invoice 單價）
    decimal SellingCost;      // 估計銷售費用
    decimal NetRealizableValue; // NRV = NrvPrice - SellingCost
    decimal ImpairmentLoss;   // max(0, CarryingCost - NRV)
    NrvPriceSource PriceSource; // 取價來源（稽核用，見下方 fallback 順序）
}
```

**`NrvPrice` 取價 fallback 順序**（新品或無近期銷售時須有替代來源，否則 NRV=0 會誤判全額減損）：

1. 近 90 天該品號 **AR Invoice 平均單價**（主要來源）→ `PriceSource = RecentSales`
2. 無近 90 天銷售 → 取該品號**有效報價/價目表單價**（若有 Price List）→ `PriceSource = PriceList`
3. 仍無 → 取**最近一筆銷售單價**（不限 90 天）→ `PriceSource = LastSales`
4. 全無銷售紀錄（純自製半成品/原料）→ **不評估 NRV**，跳過該品號並於報表標記「無市價，免評估」→ `PriceSource = NotAssessed`

> 嚴禁在取不到售價時以 0 代入 NRV——會把正常庫存全額提列減損。第 4 種情況應排除於減損計算之外。

---

## 5. 管理及分析報表（RPT）

### 5.1 財務類報表

| 報表 | 資料來源 | 優先 |
|------|----------|------|
| 損益表（P&L） | JournalEntry（收入/成本科目） | Phase A |
| 毛利分析 | ARInvoice 收入 - StockMovement 成本 | Phase A |
| 應收帳齡表 | ARInvoice + ARReceipt | Phase A |
| 應付帳齡表 | APInvoice + APPayment | Phase A |
| 現金流量表（簡化） | BankAccount 異動彙總 | Phase B |
| 資產負債表（BS） | JournalEntry 餘額彙總 | Phase B |

### 5.2 生產/庫存類報表

| 報表 | 資料來源 | 優先 |
|------|----------|------|
| 庫存評估表 | Inventory × AverageCost | Phase A |
| 工單成本差異報表 | WorkOrderActualCost vs WorkOrder.Standard* | Phase A |
| 庫存週轉率 | StockMovement / 平均庫存 | Phase A |
| 委外績效 | OutsourceReceipt：準時率/良率/成本 | Phase B |
| WIP 在製品報表 | WIPSnapshot | Phase B |
| 生產效率報表 | ProductionSession 工時 vs 標準工時 | Phase B |

### 5.3 管理會計分析報表（RPT）

管理會計報表著重於「決策支援、成本控制、利潤分析、績效評估」：

| 報表名稱 | 資料來源 | 管理價值與分析維度 | 優先級 |
|------|----------|------------------|------|
| **多維度銷售與毛利分析表** ✅ | `GoodsShipmentLine` + `StockMovement` | 按「客戶／品號／月份」維度展開銷售收入、成本與毛利率。**已實作**（`MarginAnalysis`，維度可切換；成本經 GS StockMovement 之 SourceDocNo 回溯出貨單客戶）。 | Phase A |
| **產品邊際貢獻分析表** ✅ | `GoodsShipmentLine` + `StockMovement` + `WorkOrderActualCost` | 計算邊際貢獻（營收 − 變動成本）。**已實作**（`ContributionMargin`，路線 A：以工單成本要素推每產品變動占比再套用到 COGS；變動/固定分類存系統參數可調；無工單要素之產品退回整體平均並標記）。 | Phase B |
| **工單料工費差異詳細表** | `WorkOrderActualCost` + `WorkOrder` | 拆解「材料用量差異（超領）」、「人工效率差異」與「製費效率/費率差異」，協助現場主管排查損耗瓶頸。 | Phase A |
| **委外加工對帳與損耗分析表** ✅ | `OutsourcePurchaseOrder` + `OutsourceReceipt` | 核對委外商之發料原料量與入庫成品折算量，計算「實際損耗率」，作為委外結帳與超耗扣款之依據。**已實作**（`OutsourcePerformance`，依供應商彙總加工費/超耗扣款/準時率；超耗扣款源自 §1.5 發料時點 AVCO）。 | Phase A |
| **整體設備效率與產能稼動率表** ✅ | `ProductionSession` + `WorkCalendar` + `ProductionReport` | 各機台 OEE＝可用率×表現性×良率。**已實作**（`Oee`，MFG 模組；稼動＝上工紀錄起訖、計畫＝工作日曆 TM_WKS+TM_WKO、理想工時＝產出×工序 UnitTime、良率＝良品/產出；純函式 `OeeCalculator` 可單元測試）。 | Phase B |
| **存貨庫齡與滯銷分析報表** ✅ | `Inventory` + `StockMovement` | 分析呆滯庫存天數（30/60/90/180/360 天以上無異動者），評估庫存積壓資金成本，並計算 IAS 2 NRV 減損準備。**已實作**：庫齡分桶 `InventoryAging`（2026-07-01）＋呆滯清單 `SlowMovingInventory`（既有）＋ NRV 減損 `NrvAssessment`（ACT，既有）。 | Phase A |

### 5.4 儀表板 KPI（Chart.js）

```
首頁 Dashboard：
  ├─► 本月採購金額 vs 上月（折線）
  ├─► 本月銷售收入 vs 目標（橫條）
  ├─► 庫存週轉天數（單值卡）
  ├─► 應收帳款 60 天以上佔比（圓餅）
  ├─► 工單達交率（單值卡）
  └─► 逾期 AP 金額警示（紅色卡）

生產 Dashboard：
  ├─► 今日報工產出 vs 計劃（比率）
  ├─► 機台稼動率（熱圖）
  ├─► 不良率趨勢（折線）
  └─► 委外在途數量
```

---

## 6. 開發 Phase 規劃

> **⚠️ 2026-06-11 狀態**：以下 Phase A–D 任務表為初版規劃，**多數已實作完成**。
> 真實剩餘工作請以第 0 節「剩餘真實工作」清單為準；本表保留作為歷史脈絡與工作量估算參考。

### Phase A — 委外補完 + FIN 核心（建議最先）

| 任務 | 模組 | 預估工作量 |
|------|------|-----------|
| `OutsourceReceipt` Entity + Controller | PUR | 3天 |
| 委外收貨 → INV Posting 串接 | INV | 2天 |
| `APInvoice` 自動產生（GoodsReceipt/OutsourceReceipt） | FIN | 3天 |
| `ARInvoice` 自動產生（GoodsShipment） | FIN | 2天 |
| 付款/收款作業 | FIN | 3天 |
| 應收/應付帳齡報表 | RPT | 2天 |
| `WorkOrder.Type` 重工欄位 + 重工入庫 | MFG | 2天 |

### Phase B — 成本完整化

| 任務 | 模組 | 預估工作量 |
|------|------|-----------|
| AVCO 均價欄位 + 異動同步 | CST/INV | 3天 |
| FIFO 成本層扣減邏輯 | CST/INV | 2天 |
| 工單實際成本計算 + 差異 | CST/MFG | 3天 |
| WIP 快照批次 | CST | 2天 |
| 庫存評估表 / 工單差異報表 | RPT | 2天 |

### Phase C — 結帳 + IFRS 基礎

| 任務 | 模組 | 預估工作量 |
|------|------|-----------|
| `AccountingPeriod` + 期間鎖倉 | ACT | 3天 |
| 月結 Checklist 工作流 | ACT | 3天 |
| 會計科目 + `JournalEntry` 自動產生 | ACT | 4天 |
| IAS 2 NRV 評估批次 | ACT/CST | 2天 |
| IAS 21 期末匯率重評 | ACT/FIN | 2天 |
| IFRS 9 ECL 呆帳準備 | ACT/FIN | 2天 |

### Phase D — 財務報表 + Dashboard

| 任務 | 模組 | 預估工作量 |
|------|------|-----------|
| 損益表 / 毛利分析 | RPT | 3天 |
| 資產負債表（科目餘額彙總） | RPT | 2天 |
| 管理儀表板（Chart.js） | RPT | 3天 |
| Excel 匯出（所有報表） | RPT | 2天 |

---

## 7. 模組依賴關係

```
BAS（基礎資料）
  │
  ├─► INV（庫存）──► CST（成本）──► ACT（結帳/IFRS）
  │      │                              │
  ├─► PUR（採購）──► FIN（收付款）──────┘
  │      │
  ├─► ORD（銷售）──► FIN
  │
  ├─► MFG（製造）──► CST
  │
  └─────────────────────► RPT（報表，讀所有模組）
```

**開發約束**：
- FIN 依賴 PUR（APInvoice 從 GoodsReceipt 產生）
- FIN 依賴 ORD（ARInvoice 從 GoodsShipment 產生）
- CST 依賴 INV（AVCO 與庫存異動同步）
- CST 依賴 MFG（工單成本計算）
- ACT 依賴 FIN + CST（結帳需要完整帳務）
- RPT 依賴所有模組（純讀取）

---

## 8. 資料庫移轉注意事項

- `Inventory` 新增 `AverageCost` 欄位：需提供 Migration + 初始化 Script（依現有 StockMovement 反推）
- `AccountingPeriod` 鎖倉：以 Service 層前置檢查，**所有過帳服務皆須**在過帳/作廢前呼叫 `IAccountingPeriodGuard.EnsureOpenAsync(date)`（不修改 Core `ApplicationDbContext`）。需納入檢查的服務清單：
  * `InventoryPostingService`（INV：所有 IN/OUT 異動、調撥、報廢、委外/重工發退料）
  * `OutsourceReceiptService.ConfirmAsync / VoidAsync`（SUB：成品入庫）
  * `APInvoiceService` / `ARInvoiceService`（FIN：發票產生、付款、收款、折讓核銷）
  * `WorkOrderCostService`（CST：工單實際成本回刷、WIP 快照、AVCO 期末重算）
  * `JournalEntryService`（ACT：自動/手動/調整分錄；Closing 期間僅允許 Adjustment）
  > 建議抽出共用 `IAccountingPeriodGuard`，避免各服務各自實作期間判斷而漏網。
- 所有 FIN/CST/ACT 的 DbSet 透過 `ApplicationDbContextExtensions.cs`（partial class）加入
- 結帳鎖倉的全域 Query Filter 不能用 EF Core Global Filter（跨期間），改用 Service 層前置檢查

---

## 9. Phase A 驗收標準（Acceptance Criteria）— ✅ 全數通過（2026-07-03 覆核回填）

> 覆核依據：§0 現況盤點＋程式碼實測（`AssertCanPostAsync` 接入 INV/AP/AR/Payment、`BankAccount.xmin` 樂觀鎖、
> `FinPaymentService` 已實現匯兌損益、`WorkOrderType.Rework`＋`ReworkFromWorkOrderId`）。
> 註：9.2 併發防護實作為 **xmin**（非 byte[] RowVersion——後者在 Npgsql 已證實無效，見專案通則）。

> 補：原文件無驗收條件，與專案 TDD／一致性框架慣例不符。以下為 Phase A 各任務的完成定義（DoD）。
> 測試慣例：UITests 用 `:5110` port；契約測試 baseline 以**檔案路徑**為鍵。

### 9.1 委外收貨 → INV/成本（SUB）

- [x] 由委外採購單建立收貨單，帶出加工單價與在途外料；存檔為 Draft。
- [x] 確認過帳後：產生 INV posting `OI`，成品依 1.4 公式計算入庫單價並更新 AVCO。
- [x] 超耗計算採「發料時點 AVCO」（見 1.5），低於合約允收損耗率時 `ExcessScrapPenalty = 0`。
- [x] 作廢時反沖 INV posting，並回寫委外採購單已收量。
- [x] 過帳/作廢命中已關帳期間時被 `IAccountingPeriodGuard` 擋下並回傳明確訊息。

### 9.2 AP/AR Invoice + 付款收款（FIN）

- [x] `GoodsReceipt` 確認可自動產生 `APInvoice`（金額＝進貨數量×採購單價，含稅）。
- [x] `OutsourceReceipt` 確認可自動產生委外 `APInvoice`，超耗作為負項明細扣減。
- [x] `GoodsShipment` 確認可自動產生 `ARInvoice`。
- [x] 付款/收款後發票狀態正確轉 PartialPaid/Paid（或 AR 對應狀態），`BankAccount.Balance` 同步更新。
- [x] 併發兩筆付款同一帳戶時，以 `RowVersion` 偵測衝突並重試，餘額不出現遺失更新。
- [x] 多幣別：入帳用交易日匯率、結算產生已實現匯兌損益（IAS 21）。

### 9.3 帳齡報表（RPT）

- [x] 應收/應付帳齡表可依 `asOf` 日期分桶（0–30/31–60/61–90/90+），金額合計對得上未結清發票餘額。

### 9.4 重工（MFG）

- [x] `WorkOrder.Type` 可選 Normal/Rework/Outsource；Rework 須帶 `SourceWorkOrderId`。
- [x] 重工完工入庫可分良品（成品倉/標準成本）、降等品（次品倉/調降成本）、報廢（ScrapDoc 沖銷）三路。

### 9.5 共用驗收

- [x] 所有新畫面符合 `new-screen` / `new-screen-projectz` 規範（查詢列、分頁、排序、廠別過濾、4 resx 多語、Side Panel + Toast、簽核整合）。
- [x] 金額欄位套用 Currency 小數點規範、數量欄位套用 UOM 小數點規範（見 CLAUDE.md）。
- [x] 新 Entity 正確區分全域主檔 vs `IPlantEntity`（供應商/客戶/幣別為全域，發票/收貨為廠別單據）。
- [x] DbSet 經 `ApplicationDbContextExtensions.cs` 加入，未修改 Core 主體。
- [x] 對應契約/UI 測試綠燈，無新增漂移。
