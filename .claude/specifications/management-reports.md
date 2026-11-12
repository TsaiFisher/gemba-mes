# 管理分析報表規格 (Management Reports)

> 本文件記錄基於各模組現有資料可實作的管理與分析報表清單，
> 並追蹤每張報表的資料來源、關鍵計算方式、實作狀態與已知缺口。
>
> 最後更新：2026-07-16（Phase 1、Phase 2、Phase 3 全數實作完成，逐張對照 Controller/View/選單登錄查證）

---

## 一、現有報表（已實作，無需重建）

| 識別 | 報表名稱 | 模組 | Controller | 說明 |
|------|---------|------|------------|------|
| — | 應收帳齡分析 | FIN | `ARAgingController` | 帳齡 0/30/60/90/90+ 分層 |
| — | 應付帳齡分析 | FIN | `APAgingController` | 同上，對象改供應商 |
| — | 庫存餘額查詢 | INV | `InventoryBalanceController` | 含倉庫/儲位/批號篩選 |
| — | 庫存異動帳本 | INV | `StockLedgerController` | 單一品號明細帳 |
| — | 工單成本差異 | MFG | `WorkOrderVarianceController` | 標準 vs 實際成本差異 |
| — | 毛利分析 | RPT | `GrossMarginController` | 出貨收入、COGS、毛利率 |
| — | 損益表 | RPT | `ProfitLossController` | 月度損益彙總 |
| — | 資產負債表 | RPT | `BalanceSheetController` | 期末資產負債彙總 |
| — | 管理儀表板 | RPT | `DashboardController` | KPI 摘要卡片 |

---

## 二、Phase 1（★★★ 必要）— 新增 6 張報表

### O1 — 訂單逾期跟催表

**目的**：列出所有預交日已過但尚未完全出貨的銷售訂單明細，供業務跟進追催。

| 項目 | 說明 |
|------|------|
| **模組** | ORD |
| **Controller** | `OrderOverdueController` |
| **MenuKey** | `OrderOverdue` |
| **資料來源** | `SalesOrderLine JOIN SalesOrder` |
| **過濾條件** | `DeliveryDate < today AND ShippedQty < OrderQty AND ApprovalStatus != Draft` |
| **顯示欄位** | 訂單號、客戶代號、訂單日期、品號、行號、預交日、訂購量、已出貨量、未出量、逾期天數 |
| **輸入參數** | 起始日期（DeliveryDate from）、截止日期（to）、客戶代號（選填）、品號（選填） |
| **排序** | 逾期天數降冪 |
| **匯出** | Excel（OrderOverdue\_YYYYMMDD.xlsx） |
| **實作位置** | `Modules/ORD/Controllers/OrderOverdueController.cs` |
| **狀態** | ✅ Phase 1 實作 |

**關鍵計算**：
```
PendingQty = OrderQty - ShippedQty
OverdueDays = today - DeliveryDate (天數)
```

---

### P1 — 採購逾期跟催表

**目的**：列出預交日已過但尚未完全到料的採購單明細，供採購人員追催廠商。

| 項目 | 說明 |
|------|------|
| **模組** | PUR |
| **Controller** | `PurchaseOverdueController` |
| **MenuKey** | `PurchaseOverdue` |
| **資料來源** | `PurchaseOrderLine JOIN PurchaseOrder` |
| **過濾條件** | `DeliveryDate < today AND ReceivedQty < OrderQty AND ApprovalStatus != Draft` |
| **顯示欄位** | 採購單號、供應商代號、採購日期、品號、行號、預交日、採購量、已到料量、未到量、逾期天數 |
| **輸入參數** | 起始日（DeliveryDate from）、截止日、供應商代號（選填）、品號（選填） |
| **排序** | 逾期天數降冪 |
| **匯出** | Excel |
| **狀態** | ✅ Phase 1 實作 |

---

### I3 — 缺貨分析

**目的**：列出可用量低於安全存量的品號，幫助採購/生管提前補料。

| 項目 | 說明 |
|------|------|
| **模組** | INV |
| **Controller** | `StockShortageController` |
| **MenuKey** | `StockShortage` |
| **資料來源** | `Inventory JOIN Product` |
| **過濾條件** | `QtyAvailable < Product.SafetyStock AND SafetyStock > 0` |
| **顯示欄位** | 品號、品名、庫存單位、安全存量、在手量、預留量、可用量、缺口量 |
| **輸入參數** | 品號（選填）、品項類別（選填）、按倉庫過濾（選填） |
| **排序** | 缺口量降冪 |
| **匯出** | Excel |
| **關鍵計算** | `ShortageQty = SafetyStock - QtyAvailable`（正值代表缺貨） |
| **狀態** | ✅ Phase 1 實作 |

---

### I4 — 呆滯庫存分析

**目的**：列出超過設定天數未有任何出入庫異動的庫存，供倉儲管理決策（處分/清倉/調撥）。

| 項目 | 說明 |
|------|------|
| **模組** | INV |
| **Controller** | `SlowMovingInventoryController` |
| **MenuKey** | `SlowMovingInventory` |
| **資料來源** | `Inventory` + `StockMovement`（Max MovementDate） |
| **過濾條件** | `QtyOnHand > 0 AND (today - LastMovementDate) >= IdleDaysThreshold` |
| **顯示欄位** | 品號、品名、儲位、批號、庫存量、單位成本、庫存金額、最後異動日、呆滯天數 |
| **輸入參數** | 呆滯天數門檻（預設 90 天）、品號（選填）、倉庫（選填） |
| **排序** | 庫存金額降冪 |
| **匯出** | Excel |
| **關鍵計算** | `IdleDays = today - MAX(StockMovement.MovementDate)` per (ProductCode, LocationCode, LotNo) |
| **缺口** | 若品號從未有任何出入庫記錄，`IdleDays` 設為 9999（無限呆滯） |
| **狀態** | ✅ Phase 1 實作 |

---

### M1 — 工單進度報表

**目的**：總覽所有進行中工單的計劃 vs 實際進度，標示逾期工單。

| 項目 | 說明 |
|------|------|
| **模組** | MFG |
| **Controller** | `WorkOrderProgressController` |
| **MenuKey** | `WorkOrderProgress` |
| **資料來源** | `WorkOrder` + `Dispatch`（QtyDone 加總） |
| **過濾條件** | `Status NOT IN (Void)`（顯示 Planned/Released/InProgress/Completed/Closed） |
| **顯示欄位** | 工單號、品號、工單類型、狀態、計劃開工、計劃完工、計劃量、完工量、進度%、逾期旗標 |
| **輸入參數** | 狀態篩選（多選）、品號、計劃完工起訖日（選填） |
| **排序** | 計劃完工日升冪（逾期的排最前） |
| **匯出** | Excel |
| **關鍵計算** | `ProgressPct = SUM(Dispatch.QtyDone) / WorkOrder.Quantity × 100`；`IsOverdue = PlannedEndDate < today AND Status NOT IN (Closed, Void)` |
| **狀態** | ✅ Phase 1 實作 |

---

### M2 — 不良率報表

**目的**：分析指定期間內各品項（或工作中心）的生產合格率與不良率趨勢。

| 項目 | 說明 |
|------|------|
| **模組** | MFG |
| **Controller** | `DefectRateController` |
| **MenuKey** | `DefectRate` |
| **資料來源** | `ProductionReport JOIN WorkOrder` |
| **過濾條件** | `ReportedAt` 在指定期間內；`wo.PlantId == plantId` |
| **顯示欄位** | 品號、合格量、不良量、總量、不良率%、報工筆數 |
| **輸入參數** | 起始日、截止日（必填）；品號（選填） |
| **排序** | 不良率降冪 |
| **匯出** | Excel |
| **關鍵計算** | `DefectRate = QtyNg / (QtyOk + QtyNg) × 100`；零除保護（QtyOk + QtyNg = 0 → 0%） |
| **狀態** | ✅ Phase 1 實作 |

---

## 三、Phase 2（★★）— 中優先管理分析（✅ 全數實作完成 2026-07-16）

> 規格 ID 與實際 Controller 命名不同（規格用縮寫 ID，程式用語意化名稱），對照如下。
> 全部具備 Index 查詢＋Excel 匯出＋權限檢查＋選單登錄（`ProjectZMenuContributor`）。

| # | 報表名稱 | 模組 | Controller | 狀態 |
|---|---------|------|------------|------|
| O2 | 客戶/品項銷售排行 | ORD | `SalesRankingController` | ✅ |
| O3 | 銷售同期比較表 | ORD | `SalesTrendController` | ✅ |
| O4 | 訂單交期達成率 | ORD | `DeliveryRateController` | ✅ |
| O5 | 退貨率分析 | ORD | `ReturnRateController` | ✅ |
| P2 | 供應商交期達成率 | PUR | `SupplierDeliveryRateController` | ✅ |
| P3 | 採購金額比較 | PUR | `PurchaseAmountController` | ✅ |
| P4 | 採購價格比較 | PUR | `PurchasePriceController` | ✅ |
| P5 | 委外進度追蹤 | PUR | `OutsourceProgressController` | ✅ |
| I2 | 存貨價值分析 | INV | `InventoryValueController` | ✅ |
| I5 | 庫存周轉率 | INV | `InventoryTurnoverController` | ✅ |
| I7 | 盤差分析 | INV | `StockVarianceController` | ✅ |
| M4 | WIP 在製品報表 | MFG | `WipReportController` | ✅ |
| M5 | 稼動率/利用率 | MFG | `UtilizationController` | ✅ |
| M6 | 生產效率分析 | MFG | `ProductionEfficiencyController` | ✅ |
| M7 | 異常停機分析 | MFG | `MachineDowntimeController` | ✅ |
| M9 | 備料進度追蹤 | MFG | `MaterialSlipProgressController` | ✅ |
| F3 | 毛利結構分析 | RPT | `MarginAnalysisController` | ✅ |
| F4 | NRV 存貨評估 | ACT | `NrvAssessmentController` | ✅ |

---

## 四、Phase 3（★）— 進階預測/分析（✅ 全數實作完成 2026-07-16）

| # | 報表名稱 | 模組 | Controller | 說明 | 狀態 |
|---|---------|------|------------|------|------|
| O6 | 銷售預測 | ORD | `SalesForecastController` | 近 12 月移動平均 + 季節係數 | ✅ |
| M8 | MRP 達成率 | MFG | `MrpAchievementController` | 計劃需求量 vs 實際備料量 | ✅ |
| F5 | 壞帳準備 ECL | ACT | `EclController` | 帳齡 × 損失率計提 | ✅ |

> 註：Phase 3「壞帳準備」另有 `BadDebtProvisionController`（會計計提分錄），與 `EclController`（ECL 分析報表）分工。

---

## 五、現有資料缺口（需補欄位方可實作）

| 缺口 | 影響報表 | 解法 |
|------|---------|------|
| `Inventory` / `StockMovement` 無 `LotExpiryDate` | I8 過期庫存 | 加 `LotExpiryDate DateOnly?` + migration |
| `SalesOrder` 無 `SalesRepCode` | O2 業務員績效 | 加 `SalesRepCode` + migration |
| QCR 無 IQC 交易表 | Q1 進料品質 | 需規劃 QCR Phase B |

---

## 六、實作規範

所有報表畫面遵循 `new-screen-projectz.md` 規格：

- Controller 繼承 `ModuleControllerBase`
- 報表不用 `_CrudPanelScripts`（唯讀無 CRUD 操作）
- Menu 登錄寫 `ProjectZMenuContributor`（各模組子群組），不改 CoreMenuContributor
- i18n 寫 `ProjectZSharedResource`（MenuKey）+ 各 View 自己的 per-view resx
- 匯出格式：Excel（`.xlsx`）
- 查詢前端 submit：`<input type="hidden" name="searched" value="true" />`

---
