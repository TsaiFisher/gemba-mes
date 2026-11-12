---
description: SmartFactory MES 庫存運作規範（核心）。凡涉及庫存查詢、異動、上下架、三層單據、過帳、跨廠調撥，必須依此規範實作。預留、模組整合 PUR/ORD/OUT/QCR 詳見 inventory-advanced.md。
---

# SmartFactory MES — 庫存運作規範

> **此規範在任何涉及庫存的畫面時自動套用（無需手動呼叫）。**
> 進階功能（預留機制、模組整合 PUR/ORD/OUT/QCR）詳見 `inventory-advanced.md`。

> **2026-10-08 已原地改寫**：2026-07-16 稽核列出的三處不符（並發控制寫法、§2-7 預留列舉、§四-C 跨廠調撥命名）已依現行程式碼修正；
> 跨廠調撥的權威描述在本檔 §四-C，預留機制在 `inventory-advanced.md` §一。

---

## 一、核心設計原則

| 原則 | 說明 |
|------|------|
| **雙軌架構** | `Inventory`（餘額快照）+ `StockMovement`（不可刪改的事件 log）同時維護 |
| **原子更新** | 每筆異動在同一 DB Transaction 內寫 `StockMovement` + UPDATE `Inventory`，以 PostgreSQL `xmin` 樂觀鎖防並發（`UseXminAsConcurrencyToken`） |
| **庫存單位** | 庫存餘額一律以 `StockUomCode`（庫存單位）記錄，採購/銷售單位於過帳時換算 |
| **批號追蹤** | 依 `ProductCategory.EnableLotTracking` 決定是否強制批號；空批號 = 不追蹤 |
| **三層單據** | Header（單據）→ Line（料品明細）→ LocationDetail（儲位明細），過帳前必須完成第3層 |
| **預留機制** | 軟預留（MRP/計畫）可覆寫；硬預留（SO/MFG/OUT 確認後）須先解除才能使用 |
| **成本方法** | 預設 **MovingAverage**（移動加權平均）；以 **Plant/Warehouse** 為單位計算均價（非儲位），避免成本片段化 |
| **方向控控** | 所有異動必須依 `InventoryTransactionType.Direction` 執行，嚴禁前端手動指定正負號 |
| **負庫存控管** | 系統預設不允許 `QtyAvailable < 0`。所有出庫動作必須通過可用量驗證 |
| **單號產生** | 所有單據使用 `IDocNumberService.NextAsync(docType, plantId, businessDate)` |

---

## 二、資料模型

### 2-1 倉庫（Warehouse）

```csharp
public class Warehouse
{
    [MaxLength(20)] public string WarehouseCode { get; set; } = "";
    [MaxLength(100)] public string WarehouseName { get; set; } = "";
    [MaxLength(20)] public string PlantId { get; set; } = "";
    public WarehouseType WarehouseType { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Remark { get; set; }
}

public enum WarehouseType
{
    Raw,         // 原料倉
    Wip,         // 在製倉
    Finished,    // 成品倉
    Quarantine,  // 待驗倉（QCR 放行前強制）
    Outsource,   // 委外倉（OUT 出料目的地）
    Scrap        // 報廢倉（QJ / 損耗）
}
```

### 2-2 儲位（StorageLocation）

```csharp
public class StorageLocation
{
    [MaxLength(20)] public string LocationCode { get; set; } = "";  // 全域唯一 PK
    [MaxLength(20)] public string WhCode { get; set; } = "";         // FK → Warehouse
    [MaxLength(100)] public string LocationName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public string? Remark { get; set; }
}
```

> **儲位代號全域唯一**，單據只填 `LocationCode`，`WhCode` 自動回寫（不同倉庫的儲位代號不可相同）。

### 2-3 產品類別（ProductCategory）

```csharp
public class ProductCategory
{
    [MaxLength(20)] public string CategoryCode { get; set; } = "";
    [MaxLength(100)] public string CategoryName { get; set; } = "";
    public bool EnableLotTracking { get; set; } = false;
    [MaxLength(20)] public string? DefaultWarehouseCode { get; set; }
    [MaxLength(20)] public string? DefaultLocationCode { get; set; }
    [MaxLength(20)] public string? DefaultStockUomCode { get; set; }
    public CostMethod CostMethod { get; set; } = CostMethod.MovingAverage;  // 預設 MA；FIFO 已實作（InventoryPostingService 依此分支）
    public bool IsActive { get; set; } = true;
}

public enum CostMethod { Fifo, MovingAverage }
```

### 2-4 庫存異動類別（InventoryTransactionType）

```csharp
public class InventoryTransactionType
{
    [MaxLength(20)] public string TypeCode { get; set; } = "";
    [MaxLength(100)] public string TypeName { get; set; } = "";
    [MaxLength(20)] public string? TypeCategory { get; set; }   // II/IO/IW/IA/IC/ICC/IX/OI/OR/QR/QJ/ICO
    public int Direction { get; set; }                          // +1 或 -1
    public bool IsSystemType { get; set; } = false;             // true = 不可刪除
    public bool RequireLot { get; set; } = false;
    public bool RequireDoc { get; set; } = true;
    public bool IsActive { get; set; } = true;
    [MaxLength(20)] public string? SourceTypeCode { get; set; } // 來源異動類別（如 CR→CI, BR→IB）
    [MaxLength(50)] public string? RequiredEntityType { get; set; }  // "Customer"|"Supplier"|"Department"|"WorkCenter"，null=不需關聯對象
    public bool RequiredEntityOptional { get; set; } = true;         // false=必填
}
```

**TypeCategory 對照：**

| Category | 說明 | Direction |
|----------|------|-----------|
| II | 採購入庫 | +1 |
| IO | 一般出庫 | -1 |
| IW | 倉間移轉 | ±1 |
| IA | 人工調整 | ±1 |
| IC | 盤點建立 | ±1 |
| ICC | 跨廠調撥入 | +1 |
| **ICO** | **跨廠調撥出** | **-1** |
| IX | 品檢放行入 | +1 |
| OI | 委外出料 | -1 |
| OR | 委外回料 | +1 |
| QR | 品檢放行 | +1 |
| QJ | 品檢拒收 | -1 |

**完整異動類別清單（Seed Data）：**

| TypeCode | TypeName | Dir | SourceTypeCode | RequiredEntityType | 使用單據 | TypeCategory |
|----------|----------|-----|----------------|--------------------|---------|-------------|
| PI | 採購進貨 | +1 | | | 進貨單(PE) | II |
| PF | 進貨退出 | -1 | PI | Supplier | 進貨退出(PF) | II |
| OI | 委外入庫 | +1 | | Supplier | 進貨單(PE) | II |
| OT | 委外退出 | -1 | OI | Supplier | 進貨退出(PF) | II |
| CI | 客供品入庫 | +1 | | Customer | 樣品/客供單 | II |
| CR | 客供品退回 | -1 | CI | Customer | 樣品/客供單 | IO |
| SO | 樣品領用 | -1 | | Customer/Supplier/Department | 樣品/客供單 | IO |
| SR | 樣品領用退回 | +1 | SO | | 樣品/客供單 | II |
| IB | 借入 | +1 | | Customer/Supplier/Department | 借入借出單 | II |
| BR | 借入還回 | -1 | IB | | 借入借出單 | IO |
| IL | 借出 | -1 | | Customer/Supplier/Department | 借入借出單 | IO |
| LR | 借出還回 | +1 | IL | | 借入借出單 | II |
| IJ | 報廢 | -1 | | Department | 報廢單 | IO |
| IO | 生產發料 | -1 | | WorkCenter | 生產發退料單 | IO |
| IP | 生產補領料 | -1 | | | 生產發退料單 | IO |
| IR | 生產退料 | +1 | IO | | 生產發退料單 | II |
| IT | 生產繳庫 | +1 | | WorkCenter/Department | 工單（需 MFG） | II |
| OO | 委外領料 | -1 | | | 委外發退料單 | OI |
| OR | 委外退料 | +1 | OO | | 委外發退料單 | OR |
| RI | 重工領料 | -1 | | WorkCenter | 重工發退料單／工單發退料單（重工發料） | IO |
| RR | 重工退料 | +1 | RI | | 重工發退料單／工單發退料單（重工退料 RX） | II |
| GS | 銷貨 | -1 | | Customer | 訂單出貨單（需 ORD） | IO |
| GR | 銷貨退回 | +1 | GS | | 銷貨退回（需 ORD） | II |
| NO | 內部訂單領用 | -1 | | Department | 內部訂單（需 SN） | IO |
| NR | 內部訂單退回 | +1 | NO | | 內部訂單（需 SN） | II |
| IA | 調整 | ±1 | | | 調整單(IA01) | IA |
| WP | 庫存撥出 | -1 | | | 調撥單(IW01) | IW |
| WI | 庫存撥入 | +1 | WP | | 調撥單(IW01) | IW |
| XO | 跨廠調撥出 | -1 | | | 跨廠調撥單(XP01) | ICO |
| XI | 跨廠調撥入 | +1 | XO | | 跨廠調撥單(XP01) | ICC |

**異動類別 → 單據對應（方案 B：依業務性質拆分畫面）：**

| 畫面 | 涵蓋 TypeCode | 共同特徵 |
|------|--------------|---------|
| 進貨單(PE) | PI, OI | 現有 GoodsReceipt |
| 進貨退出(PF) | PF, OT | 現有 GoodsReturn |
| 借入借出單 | IB, BR, IL, LR | 關聯對象=客戶/廠商/部門 |
| 生產發退料單 | IO, IP, IR | 關聯對象=工作中心 |
| 樣品/客供單 | CI, CR, SO, SR | 關聯對象=客戶/廠商 |
| 報廢單 | IJ | 關聯對象=部門 |
| 委外發退料單 | OO, OR | 關聯對象=委外工單 |
| 重工發退料單 | RI, RR | 關聯對象=工作中心；限**未開工單**的重工（有工單走工單發退料單，見 mfg-domain.md §16.4b） |
| 調撥單(IW01) | WP, WI | 同廠成對異動（撥出+撥入） |
| 跨廠調撥單(XP01) | XO, XI | 跨廠成對異動（來源廠撥出+目的廠撥入） |
| 調整單(IA01) | IA | 每行指定方向 |

### 2-5 庫存餘額（Inventory）

```csharp
public class Inventory
{
    public int Id { get; set; }
    [MaxLength(20)] public string PlantId { get; set; } = "";
    [MaxLength(50)] public string ProductCode { get; set; } = "";
    [MaxLength(20)] public string LocationCode { get; set; } = "";
    [MaxLength(50)] public string LotNo { get; set; } = "";           // 空白 = 不追蹤
    [Column(TypeName = "decimal(18,4)")] public decimal QtyOnHand { get; set; }
    [Column(TypeName = "decimal(18,4)")] public decimal QtyReserved { get; set; }
    // QtyAvailable = QtyOnHand - QtyReserved（計算欄，不存 DB）
    [Column(TypeName = "decimal(18,6)")] public decimal UnitCost { get; set; }   // 植物/倉庫級移動平均成本（MA）
    public uint xmin { get; set; }   // 樂觀鎖：UseXminAsConcurrencyToken（Npgsql 下 byte[] RowVersion 不受保護，勿用）
    public DateTime LastUpdatedAt { get; set; }
}
```

### 2-6 庫存異動 Log（StockMovement）

```csharp
public class StockMovement
{
    public Guid MovementId { get; set; } = Guid.NewGuid();
    [MaxLength(20)] public string TypeCode { get; set; } = "";
    [MaxLength(20)] public string? TypeCategory { get; set; }
    [MaxLength(30)] public string DocNo { get; set; } = "";
    [MaxLength(30)] public string? SourceDocNo { get; set; }
    public int? SourceDocSeq { get; set; }
    public DateOnly MovementDate { get; set; }               // 廠別本地業務日期（見§六時區規則）
    [MaxLength(20)] public string PlantId { get; set; } = "";
    [MaxLength(50)] public string ProductCode { get; set; } = "";
    [MaxLength(20)] public string LocationCode { get; set; } = "";
    [MaxLength(50)] public string LotNo { get; set; } = "";
    [MaxLength(20)] public string UomCode { get; set; } = "";
    [Column(TypeName = "decimal(18,4)")] public decimal Qty { get; set; }
    [MaxLength(20)] public string StockUomCode { get; set; } = "";
    [Column(TypeName = "decimal(18,4)")] public decimal StockQty { get; set; }
    public int Direction { get; set; }
    [Column(TypeName = "decimal(18,6)")] public decimal? UnitCost { get; set; }   // 異動當下單位成本
    [Column(TypeName = "decimal(18,4)")] public decimal? TotalCost { get; set; }  // = UnitCost × StockQty
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Remark { get; set; }
}
```

> `StockMovement` 只寫入，**絕不修改或刪除**。作廢以反向沖銷方式處理。

### 2-7 預留紀錄（InventoryReservation）

模型在 `Modules/INV/Models/InventoryReservation.cs`；來源類型 `ReservationSourceType`（`MrpRunLine`／`WorkOrderMaterial`／`Dispatch`／`MaterialStagingOrder`）、
狀態 `ReservationStatus`（`Active`／`Released`／`Consumed`）、類別 `ReservationCategory`（在手／在途供給）與可用量計算，
**以 `inventory-advanced.md` §一 為準**，本節不重複列欄位。

> 2026-10-08 移除舊版設計（`ReservationType{SO,MRP,MFG,OUT}`／`ReservationStatus{Soft,Hard,Released}`），那是實作前的草案，程式從未採用。

---

## 二-B、成本計價規劃 (Costing Planning)

### 0. 核心設計：依產品類別選擇 (Per Category Choice)

系統採「**設定多樣化、執行自動化**」原則：
*   由 `ProductCategory.CostMethod` (Enum: `MA`, `FIFO`) 決定該類產品的計價方式。
*   **限制**：當該品項已有庫存或異動紀錄時，不允許變更計價方式。
*   **性能考量**：不論採用何種方式，`Inventory.UnitCost` 永遠存放「當前參考成本」供快速查詢，但準確成本以 `StockMovement` 與 `CostLayer` 為準。

### 1. 移動加權平均 (Moving Average, MA) — 建議預設

*   **計算等級**：一律以 **Plant + ProductCode** 為單位計算（不依儲位區分成本）。
*   **觸發時機**：僅在「成本增加」之入庫異動時重算（如 PI 採購、OR 委外回料、IA 調整增值）。
*   **計算公式**：`新均價 = (現有總庫存 × 現有均價 + 入庫數量 × 入庫單價) / (現有總庫存 + 入庫數量)`。
*   **內部移轉 (IW)**：倉間移轉不影響均價，僅移動數量與總金額。
*   **負更新防護**：若 `現有總庫存 + 入庫數量 = 0`，保留原均價。

### 2. 先進先出 (FIFO) — 選配

*   **核心實體**：`InventoryCostLayer`（記錄每一批入庫的剩餘量與單價）。
*   **入庫邏輯**：每筆入庫 (II) 產生一條 `CostLayer` 記錄，並記錄 `ReceiptDate` 與 `SourceDocNo`。
*   **出庫邏輯**：
    1.  依 `ReceiptDate` 由早至晚 (ASC) 檢索有餘量的 `CostLayer`。
    2.  逐層扣減，直到滿足出庫數量。
    3.  若跨層，該筆 `StockMovement` 仍為單一紀錄，但 `TotalCost` 為各層加總之實際成本。
*   **退貨處理**：
    *   **採購退貨 (PF)**：手動指定或依 FIFO 沖銷對應的 `CostLayer`（優先扣除原單據層）。
    *   **銷貨退回 (GR)**：視為「原價入庫」的新層，日期採用原發貨日以維持排序。

### 3. 公共規則

*   **鎖定成本**：一旦 `StockMovement` 寫入，其 `UnitCost` 與 `TotalCost` 永不變動（即便事後重算均價，也僅影響後續異動）。
*   **單位換算**：成本一律以 **Inventory UOM** 為基準計算。
*   **庫存評價表**：期末報表 = `Σ (Inventory.QtyOnHand × Inventory.UnitCost)`。

---

## 三、三層單據結構與兩階段作業

### 3-0 兩階段作業模式

```
┌──────────────────────────────────┐   ┌──────────────────────────────────┐
│  Stage 1：業務單位                │   │  Stage 2：倉庫單位                │
│                                  │   │                                  │
│  建立/修改（Header + Line）      │   │  對已確認的 Line 輸入儲位+數量   │
│  產品、數量、單價等              │   │  （= LocationDetail）            │
│  儲存(不需簽核) 或 核准(需簽核)  │   │  儲存即產生 StockMovement        │
│  → 自動轉 Confirmed / Posted    │   │  不需線上簽核                    │
│                                  │   │                                  │
│  操作端：PC 業務畫面             │   │  操作端：PC 倉庫畫面 或 手機 PWA │
└──────────────────────────────────┘   └──────────────────────────────────┘
```

- Stage 1（業務）只操作 Header + Line（什麼品號、多少量、單價）
- Stage 2（倉庫）對已確認的 Line 輸入儲位+數量（= LocationDetail），儲存即產生 StockMovement + 更新 Inventory
- 一個 Line 可被多次倉庫操作（多儲位分配），透過 `QtyPutAway` 追蹤已處理量

### 簡化模式（系統參數 `INV.SimplifiedMode`）

| | 簡化模式 ON | 完整模式 OFF |
|---|---|---|
| 簽核 | 全部跳過（自動核准） | 依 ApprovalConfig 設定 |
| 倉庫作業 | 建單時自動用 ProductCategory.DefaultLocationCode 建立 LocationDetail | 業務建單後，倉庫另行入/出架 |
| DocStatus | Draft → Posted（一步到位） | Draft → Confirmed → Posted |
| 倉庫 PC/手機 | 不需要 | 需要 |

> 簡化模式下系統自動建立 LocationDetail，確保資料結構與完整模式一致。日後切換模式時歷史資料無結構問題。

### 三層結構

```
Header（單據主檔）── Stage 1 業務建立
  └── Line（料品明細，第2層）── Stage 1 業務建立
       └── LocationDetail（儲位明細，第3層）── Stage 2 倉庫建立（或簡化模式自動建立）

完成條件（自動轉 Posted）：
  所有 Line 的 QtyPutAway == QtyStockUnit
  （即 Σ LocationDetail.Qty == Line.QtyStockUnit）
```

### 3-1 Header Model 必備欄位

```csharp
public string DocNo { get; set; }              // IDocNumberService.NextAsync() 產生
public DateOnly DocDate { get; set; }          // 業務日期（廠別本地日期，前端確認）
public string TypeCode { get; set; }           // FK → InventoryTransactionType
public string? TypeCategory { get; set; }      // 冗餘，提升查詢效率
public DocStatus Status { get; set; }          // Draft / Confirmed / Posted / Void

// 線上簽核欄位（DocumentApprovalConfig 有設定且 IsApprovalRequired=true 時使用）
public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
public string? ApprovalRequestId { get; set; }  // FK → ApprovalRequest.Id

public enum DocStatus { Draft, Confirmed, InTransit, Posted, Void }
// InTransit 僅用於跨廠調撥（來源廠已出庫、目的廠尚未入庫），其他單據不使用
```

### 3-2 簽核流程整合

#### 狀態機（DocStatus × ApprovalStatus）

```
建立時：
  不需簽核：儲存後自動轉為 Confirmed（業務確認成立），允許倉庫作業（簡化模式點下儲存即 Posted）。
  需要簽核：儲存後為 Draft (PendingApproval)，核准後自動轉為 Confirmed。

簽核回寫 (Approval Callback)：
  核准：ApprovalStatus = Approved → Status = Confirmed（自動轉入，不需二次點擊，簡化模式則轉 Posted）。
  拒絕：ApprovalStatus = Rejected → Status = Draft（退回修改）。

倉庫作業（Stage 2）：
  每次儲存 LocationDetail → 產生 StockMovement + 更新 Inventory + 更新 Line.QtyPutAway
  所有 Line 的 QtyPutAway == QtyStockUnit → DocStatus 自動轉 Posted

作廢：
  VoidAsync：僅 Posted 可作廢。
  1. 產生反向 StockMovement（Direction * -1）。
  2. 業務日期 (MovementDate) 為當下作業日期，CreatedBy 記錄操作者。
  3. 原單據狀態轉為 Void。

#### 業務增修規則
為了維持資料一致性，單據的修改受以下限制：

| 狀態 | 倉庫進度 | 業務操作 |
|------|---------|---------|
| Draft / Confirmed | 尚未作業 (QtyPutAway = 0) | ✅ 允許修改、刪除、或送審撤回 |
| Confirmed | 已部分/全部作業 (QtyPutAway > 0) | ❌ 嚴禁修改單據內容、撤回或刪除 |
| Posted | 已完成 | ❌ 嚴禁修改，僅能作廢 |

#### 行級保護規則

只要該行已執行倉庫作業（`QtyPutAway > 0`），受以下保護：

- ❌ 不可刪除該行
- ❌ 行數量不可改為低於已過帳量（`QtyPutAway`）
- Line 的 `QtyPutAway` = Σ LocationDetail.Qty，每次倉庫儲存時更新

#### 建單送審（Controller POST）

```csharp
var config = await _approvalService.GetApprovalConfig(documentType, _userContext.CurrentPlantId);

if (config == null || !config.IsApprovalRequired)
{
    // 未設定或不需簽核 → 直接核准，可立即過帳
    doc.ApprovalStatus = DocumentApprovalStatus.Approved;
    doc.Status = DocStatus.Confirmed;
}
else
{
    // 需要簽核 → 送審，等待核准才可過帳
    var req = await _approvalService.SubmitForApproval(
        doc.DocNo, documentType, config.ApprovalTemplateId!, _userContext.UserId);
    doc.ApprovalStatus = DocumentApprovalStatus.PendingApproval;
    doc.ApprovalRequestId = req.Id;
    doc.Status = DocStatus.Draft;   // 保持 Draft，核准後才升 Confirmed
}
```

#### 倉庫作業門控（PostLineDetailAsync 起始處）

```csharp
// ⚠️ 倉庫作業前必須同時檢查兩個狀態
if (doc.Status != DocStatus.Confirmed)
    throw new BusinessException("單據狀態不符，無法進行倉庫作業（必須為 Confirmed）。");
if (doc.ApprovalStatus != DocumentApprovalStatus.Approved)
    throw new BusinessException("單據尚未核准，無法進行倉庫作業（請等待簽核流程完成）。");
```

#### 簽核回寫 callback（ApprovalService.SyncSourceDocument）

每種庫存單據需在 `SyncSourceDocument` 中加入對應的 case：

```csharp
case "GoodsReceipt":   // 依 documentType 分流
    var grDoc = await _context.GoodsReceipts.FindAsync(documentNo);
    if (grDoc != null)
    {
        grDoc.ApprovalStatus = approved
            ? DocumentApprovalStatus.Approved
            : DocumentApprovalStatus.Rejected;
        grDoc.Status = approved ? DocStatus.Confirmed : DocStatus.Draft;
        await _context.SaveChangesAsync();
    }
    break;
```

### 3-3 Line Model 必備欄位

```csharp
public string ProductCode { get; set; }
public string? LotNo { get; set; }
public decimal Qty { get; set; }               // 採購/銷售單位數量
public string UomCode { get; set; }
public decimal QtyStockUnit { get; set; }      // 換算後庫存單位
public string StockUomCode { get; set; }
public decimal QtyPutAway { get; set; }        // = Σ LocationDetails.Qty
```

### 3-4 LocationDetail Model 必備欄位

```csharp
public string LocationCode { get; set; }       // 全域唯一
public string WhCode { get; set; }             // 自動回寫（by LocationCode）
public decimal Qty { get; set; }               // 庫存單位
```

### 3-5 過帳流程

#### 完整模式：逐筆過帳（PostLineDetailAsync）

倉庫每次儲存 LocationDetail 即觸發過帳，不需等所有行完成：

```csharp
// 倉庫儲存單一 LocationDetail 時呼叫
async Task PostLineDetailAsync(string docNo, int lineNo, LocationDetail detail)
{
    // ① 門控：單據狀態
    if (doc.Status != DocStatus.Confirmed)
        throw new BusinessException("單據狀態不符，無法過帳。");
    if (doc.ApprovalStatus != DocumentApprovalStatus.Approved)
        throw new BusinessException("單據尚未核准，無法過帳。");

    await using var tx = await _context.Database.BeginTransactionAsync();

    // ② UOM 換算（若尚未換算）
    var factor = await _uomSvc.GetConversionFactorAsync(line.UomCode, line.StockUomCode);
    if (factor == null) throw new BusinessException($"找不到 {line.UomCode}→{line.StockUomCode} 換算比例");

    // ③ 出庫：驗證儲位可用量
    if (direction == -1)
    {
        var avail = await _invSvc.GetAvailableQtyAsync(line.ProductCode, detail.LocationCode, line.LotNo);
        if (avail < detail.Qty) throw new BusinessException($"{detail.LocationCode} 可用量不足");
    }

    // ④ 成本計算（依 ProductCategory.CostMethod 分支 MA / FIFO，已實作於 InventoryPostingService）
    decimal unitCost = 0;
    if (direction == 1) // 入庫
    {
        unitCost = line.UnitCostInput ?? 0;
        await _invSvc.UpdateMovingAverageCostAsync(..., unitCost);
        // FIFO（CostMethod.Fifo）：await _costSvc.AddLayerAsync(plantId, productCode, locationCode, lotNo, qty, unitCost, docNo);
    }
    else // 出庫
    {
        unitCost = inv.UnitCost; // MA 取現有均價
        // FIFO（CostMethod.Fifo）：unitCost = await _costSvc.ConsumeLayersAsync(plantId, productCode, locationCode, lotNo, qty);
    }

    // ⑤ 寫 StockMovement（不可變 log）
    _context.StockMovements.Add(new StockMovement {
        UnitCost = unitCost,
        TotalCost = unitCost * detail.Qty,
        ...
    });

    // ⑥ 更新 Inventory（xmin 樂觀鎖）
    await _invSvc.UpdateQtyAsync(line.ProductCode, detail.LocationCode, line.LotNo, detail.Qty * direction);

    // ⑦ 更新 Line.QtyPutAway
    line.QtyPutAway += detail.Qty;

    // ⑧ 檢查是否所有行完成 → 自動轉 Posted
    if (doc.Lines.All(l => l.QtyPutAway >= l.QtyStockUnit))
        doc.Status = DocStatus.Posted;

    await _context.SaveChangesAsync();
    await tx.CommitAsync();
}
```

#### 簡化模式：一步到位

業務儲存時自動建立 LocationDetail + 過帳：

```csharp
// 簡化模式：業務儲存時自動執行
foreach (var line in doc.Lines)
{
    // 自動用 ProductCategory.DefaultLocationCode 建立 LocationDetail
    var defaultLoc = category.DefaultLocationCode;
    line.LocationDetails.Add(new LocationDetail {
        LocationCode = defaultLoc, WhCode = loc.WhCode, Qty = line.QtyStockUnit
    });

    // 走上述 ⑤⑥⑦ 步驟
    // ...
}
doc.Status = DocStatus.Posted;  // 直接 Posted
```

> 簡化模式下資料結構與完整模式一致（皆有三層），日後切換模式時歷史資料無結構問題。

---

## 四、倉儲選擇 UX 規則

```
① ProductCategory.DefaultLocationCode 有值 → 自動填入，同步帶出 WhCode
② 直接輸入或掃描 LocationCode → 自動查詢並回寫 WhCode（唯讀）
③ 先選 WhCode → Location 下拉僅顯示該倉的啟用儲位
```

> ⚠️ **DOMPurify 限制**：Side Panel 透過 `innerHTML` 載入 Partial View 時，DOMPurify 會移除 `<script>` 標籤與 `onchange`/`onclick` 等 inline event。
> 解法：改用 `data-*` 屬性，並在 Index.cshtml 的 `bindPanelForms()` 以事件委派綁定。

```razor
@* Partial View：用 data-* 替代 inline onchange *@
<select asp-for="WhCode" id="warehouseSelect" class="form-select"
        data-filter-locations="true">...</select>
<select asp-for="LocationCode" id="locationSelect" class="form-select"
        data-location-input="true"></select>
<input type="hidden" id="locationData" value="@Html.Raw(ViewBag.LocationsJson)" />
```

```javascript
// Index.cshtml — bindPanelForms() 內加入
document.querySelectorAll('#crudPanelBody [data-filter-locations]').forEach(sel => {
    sel.addEventListener('change', function() { filterLocations(this.value); });
});
document.querySelectorAll('#crudPanelBody [data-location-input]').forEach(sel => {
    sel.addEventListener('change', function() { onLocationCodeInput(this.value); });
});

// 定義在 Index.cshtml @section Scripts
function filterLocations(whCode) {
    const all = JSON.parse(document.getElementById('locationData').value);
    const filtered = whCode ? all.filter(l => l.WhCode === whCode) : all;
    const sel = document.getElementById('locationSelect');
    sel.innerHTML = '<option value="">— —</option>' +
        filtered.map(l => `<option value="${l.LocationCode}">${l.LocationCode} — ${l.LocationName}</option>`).join('');
}
function onLocationCodeInput(locationCode) {
    const all = JSON.parse(document.getElementById('locationData').value);
    const loc = all.find(l => l.LocationCode === locationCode);
    if (loc) document.getElementById('warehouseSelect').value = loc.WhCode;
}
```

---

## 四-B、倉庫手機作業（PWA）

完整模式下倉庫作業可透過 PC 畫面或手機 PWA 操作。

### 技術架構

```
wwwroot/mobile/inv/   ← 手機倉儲頁（list／putaway／pick／stock／stockcount／iqc，*.html + *.page.js）
                        共用殼層、Service Worker、離線佇列見 wwwroot/mobile/（mobile-pwa.md）
                        （早期原型 wwwroot/pwa/ 與 /Pwa 殼層已於 2026-10-01 移除）

Modules/INV/Controllers/
  WarehouseApiController.cs   ← JSON API（供 PWA AJAX 呼叫）
```

- **認證**：HTTPS + Cookie（與 PC 端共用登入機制，不需另建 JWT）
- **掃碼**：html5-qrcode.min.js（品號、儲位、單號、批號皆可掃碼）
- **離線**：靜態資源可離線快取；查詢/寫入需在線（庫存涉及餘額驗證與 xmin 併發控制）
- **HTTPS 必要**：iOS Safari 相機（掃碼）需 HTTPS

### API 端點（WarehouseApiController）

```csharp
[Authorize]
[Route("api/warehouse")]
public class WarehouseApiController : ControllerBase
{
    [HttpGet("pending-inbound")]   // 查詢待入庫明細（Confirmed，QtyPutAway < QtyStockUnit）
    [HttpGet("pending-outbound")]  // 查詢待出庫明細
    [HttpGet("stock/{productCode}")]  // 指定品號各儲位庫存量（出庫時顯示「哪裡有」）
    [HttpPost("put-away")]         // 入庫上架（單一儲位）→ LocationDetail + StockMovement
    [HttpPost("pick")]             // 出庫下架（可多儲位）→ LocationDetail + StockMovement
    [HttpGet("location/{locationCode}")]  // 驗證儲位代號（掃碼後呼叫）
    [HttpGet("product/{productCode}")]    // 驗證品號（掃碼後呼叫）
}
```

### 併發控制

多人同時出庫同一品號/儲位時，xmin 樂觀鎖讓後者失敗，API 回傳明確錯誤（「庫存已被更新，請重新查詢」），前端自動刷新庫存清單。PC 和手機可操作同一張單，透過 QtyPutAway 即時更新 + RowVersion 防併發。

---

## 四-C、跨廠調撥（CrossPlantTransfer）

> 2026-10-08 依現行程式碼改寫（`Modules/INV/Models/CrossPlantTransfer*.cs`、`CrossPlantTransferController`、
> `InventoryPostingService.PostCrossPlantShipAsync`／`PostCrossPlantReceiveAsync`）。原文的 `InterPlantTransfer`、
> 第三層儲位明細、`TransferCost` 欄位、部分收貨與強制結案都是實作前的草案，程式沒有採用。

### 核心概念

跨廠調撥涉及兩個不同 PlantId 的庫存，須在**來源廠扣庫存**、**目的廠加庫存**，且兩廠的 Global Query Filter 互相隔離。

### 單據模型（兩層）

| 層 | 類別 | 重點欄位 |
|---|---|---|
| Header | `CrossPlantTransfer`（`IPlantEntity`、`IDataAuthEntity`、`IApprovalDocument`） | `CrossPlantTransferId`、`TransferDate`、`PlantId`＝**來源廠**、`ToPlantId`、`FromWhCode`／`ToWhCode`（預設值）、`Status`、`ApprovalStatus`、`xmin` |
| Line | `CrossPlantTransferLine` | `ProductCode`、`Qty`＋`UomCode`、`StockQty`＋`StockUomCode`、`FromWhCode`／`FromLocationCode`、`ToWhCode`／`ToLocationCode`、`QtyShipped`、`QtyReceived` |

儲位直接寫在明細行上（一行一個出庫儲位、一個入庫儲位），**沒有第三層 LocationDetail**；不帶批號（過帳時 `LotNo = ""`）。

### 狀態機

```
Draft ──(送簽核 → Approved)──→ Confirm：來源廠 XP 出庫過帳 ──→ InTransit
InTransit ──→ Receive：目的廠 XI 入庫過帳 ──→ Posted
InTransit ──→ CancelConfirm：沖銷 XP ──→ Draft（貨未到目的廠前可撤回）
Posted ──→ Void：沖銷 XP＋XI 兩廠異動 ──→ Void
```

- **Confirm** 限來源廠人員、Draft、簽核已核准、至少一行；**Receive** 限目的廠人員、InTransit。
- **整張一次收**：Receive 依各行 `StockQty` 全數入庫，沒有部分收貨與強制結案。
- **作廢**只有 Posted 可以；InTransit 要先 CancelConfirm 退回 Draft。Draft 才可刪除。

### 過帳與成本

| 項目 | 說明 |
|------|------|
| **異動類型** | 出庫 `XP`（Direction −1，來源廠）、入庫 `XI`（Direction +1，目的廠），`TypeCategory = "Transfer"` |
| **出庫檢查** | 來源儲位可用量不足即擋（`Inv_InsufficientProductAtLocationFmt`）；期間鎖同時檢查兩廠 |
| **成本傳遞** | 出庫取來源廠成本凍結在 XP 異動的 `UnitCost`；入庫以該單價寫入目的廠（移動平均→重算均價，FIFO→建成本層）。⚠️ 必須在加數量**之前**重算均價 |
| **帳務** | 出庫與入庫各做一筆跨廠往來分錄（`StandardAccounts.InterPlantClearingCode` 1450），兩廠一正一負、全公司合計為 0 |
| **在途** | InTransit 期間來源廠已扣、目的廠未加 |

### 跨廠存取

- 單據 `PlantId = 來源廠`，所以來源廠的 Global Filter 查得到；目的廠與清單頁一律 `IgnoreQueryFilters()`，
  **之後必須手動檢查「使用者所在廠是來源廠或目的廠」**，否則就是跨廠 IDOR（`CrossPlantTransferController` 的存取檢查）。
- 目的廠的倉庫／儲位下拉同樣 `IgnoreQueryFilters()` 後以 `PlantId == toPlantId` 過濾。
- 過帳服務一律接收明確的 `plantId`，不依賴 `CurrentPlantId`。

> ⚠️ `IgnoreQueryFilters` 會繞過所有 Global Filter（含 IsActive、資料權限），查詢時務必手動加入必要條件。

---

## 五、UOM 換算規則

```
StockQty = Qty × ConversionFactor
找不到換算比例 → 擋單，ModelState.AddModelError（"請至計量單位維護新增換算設定"）
```

---

## 六、時區規則

| 欄位 | 使用規則 |
|------|---------|
| `MovementDate` | **廠別本地業務日期**（由前端確認，允許使用者調整）；不可用 `DateTime.UtcNow.Date` 自動決定 |
| `CreatedAt` | `DateTime.UtcNow`（UTC，稽核用） |
| 月結鎖帳 | Per-PlantId + YearMonth，各廠獨立結帳 |
| `IDateTimeService.Now` | 依 `_userContext.TimeZoneId`（使用者時區），用於顯示用途 |
| `IDateTimeService.GetLocalDate(tz)` | 依廠別時區取今日日期，用於新建單據的 `MovementDate` 預設值 |

**廠別時區預設日期（Controller GET）：**

```csharp
// 取廠別，並以廠別時區決定預設業務日期
var plant = await _context.Plants.FindAsync(_userContext.PlantId);
var defaultDate = _dateTimeSvc.GetLocalDate(plant?.DefaultTimeZone);
ViewBag.DefaultMovementDate = defaultDate.ToString("yyyy-MM-dd");
```

> **跨午夜邊界操作**：使用者在 23:50 建立單據，`MovementDate` 前端顯示廠別今日日期並允許手動調整，後端不自動推算。

---

## 七、IInventoryService API

```csharp
// 查詢
decimal qtyOnHand  = await _invSvc.GetQtyOnHandAsync(productCode, locationCode, lotNo);
decimal qtyAvail   = await _invSvc.GetAvailableQtyAsync(productCode, locationCode, lotNo);

// 過帳（逐筆，倉庫每次儲存 LocationDetail 時呼叫）
await _invSvc.PostLineDetailAsync(docNo, lineNo, locationDetail);

// 作廢（反向沖銷所有 StockMovement）
await _invSvc.VoidDocAsync(docId, reason);

// 預留：在 IInventoryReservationService（不在 IInventoryService），詳見 inventory-advanced.md §一
await _reservations.ReserveAsync(plantId, productCode, qty, ReservationSourceType.MaterialStagingOrder, sourceDocId, lineSeq, requiredDate);
await _reservations.ReleaseBySourceAsync(ReservationSourceType.MaterialStagingOrder, sourceDocId);

// 成本（預設 MA）
await _invSvc.UpdateMovingAverageCostAsync(productCode, locationCode, lotNo, addQty, inUnitCost);
// 日後 FIFO：decimal unitCost = await _invSvc.ConsumeFifoCostAsync(...);

// 倉儲
StorageLocation? loc = await _invSvc.GetLocationAsync(locationCode);
```

---

## 八、單號產生規則

使用 `IDocNumberService`，設定在 **BAS → 單號設定** 畫面維護。

```
格式：{Prefix}{Separator}{DatePart}{Separator}{SeqNo}
例：  GR-25-00001 / IO-2503-0042 / PR-20250327-001
```

```csharp
// 在 Controller 或 Service 中產生單號
var docNo = await _docNumberSvc.NextAsync("GoodsReceipt", _userContext.CurrentPlantId,
    businessDate: DateOnly.Parse(vm.DocDate));
```

**Seed Data（Migration 中建入，`IsSystemType = true` 不可刪除）：**

```csharp
// Migration / DbContext.OnModelCreating HasData：
modelBuilder.Entity<DocNumberConfig>().HasData(
    new DocNumberConfig { DocType="GoodsReceipt", DocTypeName="進貨單", Prefix="GR",
        DateFormat=DocDateFormat.YY, SeqLength=5, ResetCycle=DocSeqReset.Yearly, IsSystemType=true },
    // ... 其他預設類型
);
```

**預設系統單據類型（IsSystemType = true）：**

| DocType | 說明 | 預設前置碼 |
|---------|------|-----------|
| GoodsReceipt | 進貨單 | GR |
| GoodsIssue | 出貨單 | GI |
| StockTransfer | 倉間移轉單 | TF |
| StockAdjustment | 庫存調整單 | IA |
| InventoryCount | 盤點單 | IC |
| CrossPlantTransfer | 跨廠調撥單（異動類型 XP 出／XI 入） | 依 DocNumberConfig |
| PurchaseRequisition | 請購單 | PR |
| SalesOrder | 銷售訂單 | SO |

---

## 九、自我確認清單

```
Model：
[ ] 三層結構：Header / Line / LocationDetail
[ ] Header 含 DocStatus + ApprovalStatus + ApprovalRequestId
[ ] Line 含 UomCode + QtyStockUnit + StockUomCode + QtyPutAway
[ ] LocationDetail 含 LocationCode（自動回寫 WhCode）
[ ] StockMovement 含 UnitCost + TotalCost
[ ] 成本預設 MA（Inventory.UnitCost）；FIFO 已實作（InventoryCostLayer + InventoryCostLayerService，依 ProductCategory.CostMethod 分支）

Stage 1 — 業務建單/送審：
[x] 儲存 (Save)：由邏輯偵測是否需簽核 → 自動設為 Confirmed 或 PendingApproval
[x] 簽核回寫 (SyncApprovalStatus)：核准後自動轉為 Confirmed（不需手動確認）
[x] 建立/修改時實施鎖定保護：若 `QtyPutAway > 0` 則阻擋所有異動
[ ] DocNo 使用 IDocNumberService.NextAsync()
[x] SyncSourceDocument callback 已實作：核准→Confirmed+Approved；拒絕→Draft+Rejected

Stage 2 — 倉庫作業/過帳：
[ ] ① 門控：Status == Confirmed AND ApprovalStatus == Approved
[ ] ② UOM 換算，找不到比例時擋單
[ ] ③ 出庫：每個儲位 QtyAvailable >= 出庫量
[ ] ④ 每次儲存 LocationDetail 即寫 StockMovement + 更新 Inventory（同一 Transaction）
[ ] ⑤ xmin 樂觀鎖（DbUpdateConcurrencyException → retry 或提示重新查詢）
[ ] ⑥ 更新 Line.QtyPutAway（= Σ LocationDetail.Qty）
[ ] ⑦ 所有行 QtyPutAway == QtyStockUnit → DocStatus 自動轉 Posted

業務撤回 & 行級保護：
[ ] Confirmed 且無 StockMovement → 允許撤回至 Draft
[ ] Confirmed 且有 StockMovement → 拒絕撤回
[ ] 有 StockMovement 的行不可刪除
[ ] 行數量不可改為低於 QtyPutAway

簡化模式（INV.SimplifiedMode）：
[ ] 業務儲存時自動用 DefaultLocationCode 建立 LocationDetail
[ ] 自動過帳，DocStatus 直接 → Posted
[ ] 資料結構與完整模式一致（三層皆有）

View：
[ ] 儲位選擇依倉篩選，直接輸入 LocationCode 時回寫倉別
[ ] 出庫顯示各儲位可用量（QtyAvailable）
[ ] MovementDate 讓使用者確認（不自動推算）

跨廠調撥（CrossPlantTransfer，詳見 §四-C）：
[ ] 狀態機：Draft →（簽核核准）Confirm → InTransit → Receive → Posted；InTransit 可 CancelConfirm 退回 Draft；Posted 才可 Void
[ ] DocStatus enum 含 InTransit（僅跨廠調撥使用）
[ ] Line 含出入庫儲位（From/ToLocationCode）＋ QtyShipped ＋ QtyReceived；無第三層、無批號
[ ] Confirm 限來源廠、Receive 限目的廠；IgnoreQueryFilters 後手動檢查使用者所在廠（防跨廠 IDOR）
[ ] 過帳服務明確傳入 plantId／toPlantId（不依賴 CurrentPlantId）
[ ] 成本傳遞：出庫單價凍結在 XP 異動，入庫以此單價按目的廠成本方法寫入（先重算均價再加數量）
[ ] 兩廠各做一筆跨廠往來分錄（1450），全公司合計為 0
[ ] IgnoreQueryFilters 查詢時手動加入 IsActive 等必要條件

常見錯誤：
[ ] ❌ 過帳未檢查 ApprovalStatus → 待審單據被直接過帳
[ ] ❌ SyncSourceDocument 未實作庫存單據 case → 核准後狀態不更新
[ ] ❌ 未用 Transaction 直接更新 Inventory → 並發時餘額錯誤
[ ] ❌ StockMovement 未填 UnitCost/TotalCost → 無法結帳
[ ] ❌ MovementDate 用 DateTime.UtcNow.Date → 跨時區業務日期錯誤
[ ] ❌ Partial View 使用 onchange/onclick inline → DOMPurify 剝除，事件失效
[ ] ❌ 未呼叫 ReleaseReservationAsync 就刪單 → 可用量永久偏低
[ ] ❌ 業務撤回未檢查 StockMovement 存在 → 倉庫已作業的單據被撤回
[ ] ❌ 刪除行時未檢查 QtyPutAway > 0 → 已過帳的行被刪除
[ ] ❌ 跨廠調撥只建 XO 未等待 XI 確認 → 目的廠庫存未更新
[ ] ❌ 跨廠入庫未用 IgnoreQueryFilters → 目的廠 Inventory 找不到/建不了
[ ] ❌ 跨廠強制結案未產生 IA 調整 → 差異量憑空消失
```

---

## 十、自動測試規範

**所有 Service 與功能都必須有對應的自動測試。** 測試在 `SmartFactory.MES.Tests/` 下，使用 xUnit + Moq + InMemory DB（`TestBase`）。

### 測試分層

| 層級 | 目的 | 測試目標 | 命名規則 |
|------|------|---------|---------|
| **Service 單元測試** | 核心商業邏輯 | Service 方法（CRUD、狀態轉換、過帳、成本計算） | `{Service}Tests.cs`、`{Service}UpdateTests.cs` |
| **Controller 測試** | HTTP 行為、權限、ModelState | Controller action（POST/GET/DELETE） | `{Controller}Tests.cs` |
| **整合測試** | 跨 Service 互動 | 跨模組流程（如 PO → GR → 庫存） | `{Feature}IntegrationTests.cs` |

### 目錄結構

```
SmartFactory.MES.Tests/
  TestBase.cs                          ← 共用基底（InMemory DB、Mock helpers）
  PUR/
    PurchaseServiceTests.cs            ← PurchaseService CRUD
    PurchaseServiceUpdateTests.cs      ← PurchaseService 更新/刪除
    PurchaseApprovalTests.cs           ← 簽核流程
  INV/
    InventoryServiceTests.cs           ← IInventoryService（餘額查詢、更新、MA 成本）
    InventoryPostingServiceTests.cs    ← IInventoryPostingService（逐筆過帳、門控、併發）
    CrossPlantTransferControllerTests.cs ← 跨廠調撥（狀態機、跨廠存取檢查；端到端見 IntegrationTests/Flows/CrossPlantTransferFlowTests.cs）
    StockTransferServiceTests.cs       ← 同廠調撥
    StockAdjustmentServiceTests.cs     ← 調整單
    WarehouseApiControllerTests.cs     ← 倉庫 API（入庫/出庫/查詢）
```

### Service 測試必備案例

每個 Service **至少**涵蓋以下測試案例：

**CRUD 基本操作：**
```
[ ] Create — 正常建立，驗證所有欄位正確持久化
[ ] Create — Lines 正確關聯至 Header
[ ] Update — 修改 Header 欄位 + 新增/修改/刪除 Lines
[ ] Delete — 正常刪除（含關聯 Lines）
```

**狀態機 & 門控：**
```
[ ] 不需簽核 → 直接 Confirmed + Approved
[ ] 需簽核 → PendingApproval + Draft
[ ] 過帳門控 — Status != Confirmed → 擋單
[ ] 過帳門控 — ApprovalStatus != Approved → 擋單
[ ] 業務撤回 — 無 StockMovement → 允許
[ ] 業務撤回 — 有 StockMovement → 拒絕（InvalidOperationException）
```

**行級保護：**
```
[ ] 刪除行 — QtyPutAway > 0 → 擋單
[ ] 修改行數量 — 新數量 < QtyPutAway → 擋單
[ ] 修改行數量 — 新數量 >= QtyPutAway → 允許
```

**過帳（PostLineDetailAsync）：**
```
[ ] 入庫 — StockMovement 正確寫入 + Inventory.QtyOnHand 增加
[ ] 出庫 — 可用量充足 → StockMovement + QtyOnHand 減少
[ ] 出庫 — 可用量不足 → 擋單（BusinessException）
[ ] QtyPutAway 正確累加
[ ] 所有行完成 → DocStatus 自動轉 Posted
[ ] MA 成本更新 — 入庫後 Inventory.UnitCost 正確（加權平均公式）
[ ] xmin 併發 — 兩筆同時更新同一 Inventory → DbUpdateConcurrencyException
```

**作廢（VoidAsync）：**
```
[ ] Posted 單據 → 產生反向 StockMovement + Inventory 回沖 + Status = Void
[ ] 非 Posted 單據 → 擋單
```

**簡化模式：**
```
[ ] 儲存時自動建立 LocationDetail（使用 DefaultLocationCode）
[ ] 自動過帳 → DocStatus 直接 Posted
[ ] 資料結構完整（三層皆存在）
```

### 跨廠調撥專屬測試案例

```
[ ] 來源廠出庫 → QtyShipped 累加 + XO StockMovement + 來源 Inventory 減少
[ ] 全部出庫 → Status 自動轉 InTransit
[ ] 目的廠入庫 → QtyReceived 累加 + XI StockMovement + 目的 Inventory 增加
[ ] 全部入庫 → Status 自動轉 Posted
[ ] 部分收貨 — QtyReceived < QtyShipped → Status 仍為 InTransit
[ ] 強制結案 — 差異量自動產生來源廠 IA 調整
[ ] 成本傳遞 — TransferCost 出庫時鎖定，入庫時正確寫入目的廠
[ ] 目的廠查詢 — IgnoreQueryFilters + ToPlantId 篩選正確
[ ] InTransit 不可作廢 → 擋單
[ ] 目的廠入庫量 > QtyShipped → 擋單
```

### 測試寫法範例

```csharp
public class InventoryPostingServiceTests : TestBase
{
    private readonly Mock<IDocNumberService> _mockDocNo;
    private readonly Mock<IDateTimeService>  _mockDt;
    private readonly InventoryPostingService _service;
    private const string PlantId = "P001";

    public InventoryPostingServiceTests()
    {
        _mockDocNo = new Mock<IDocNumberService>();
        _mockDt    = new Mock<IDateTimeService>();
        _mockDt.Setup(d => d.Now).Returns(new DateTime(2025, 4, 1, 8, 0, 0, DateTimeKind.Utc));
        _context.CurrentPlantId = PlantId;

        // Service 注入 Mock 的外部依賴
        _service = new InventoryPostingService(_context, _mockDocNo.Object, _mockDt.Object);

        // Seed 基礎資料
        SeedBaseData();
    }

    [Fact]
    public async Task PostLineDetail_Inbound_ShouldUpdateInventoryAndCreateStockMovement()
    {
        // Arrange: 建立 Confirmed + Approved 的單據
        var doc = CreateConfirmedDoc(typeCode: "PI", direction: +1);
        await _context.SaveChangesAsync();

        // Act: 倉庫入庫
        var detail = new LocationDetail { LocationCode = "A-01", WhCode = "WH01", Qty = 10 };
        await _service.PostLineDetailAsync(doc.DocNo, lineNo: 1, detail);

        // Assert
        var inv = await _context.Inventories.FirstAsync(i => i.ProductCode == "P001" && i.LocationCode == "A-01");
        Assert.Equal(10m, inv.QtyOnHand);

        var sm = await _context.StockMovements.FirstAsync();
        Assert.Equal("PI", sm.TypeCode);
        Assert.Equal(10m, sm.StockQty);
        Assert.Equal(+1, sm.Direction);
    }

    [Fact]
    public async Task PostLineDetail_Outbound_InsufficientStock_ShouldThrow()
    {
        // Arrange: 庫存只有 5，嘗試出庫 10
        await SeedInventory("P001", "A-01", qtyOnHand: 5);
        var doc = CreateConfirmedDoc(typeCode: "IO", direction: -1);
        await _context.SaveChangesAsync();

        // Act & Assert
        var detail = new LocationDetail { LocationCode = "A-01", WhCode = "WH01", Qty = 10 };
        await Assert.ThrowsAsync<BusinessException>(
            () => _service.PostLineDetailAsync(doc.DocNo, lineNo: 1, detail));
    }
}
```

### 測試覆蓋要求

| 類型 | 最低要求 |
|------|---------|
| Service 公開方法 | **每個方法至少 1 個正向 + 1 個反向測試** |
| 狀態轉換 | 每個合法轉換 + 每個非法轉換 |
| 門控/驗證 | 每個驗證條件至少 1 個觸發測試 |
| 金額/成本計算 | 驗證計算公式正確性 |
| 併發控制 | 至少 1 個 xmin 衝突測試（需真 PostgreSQL，InMemory 不檢查 xmin） |

> **新增功能時，測試須與 Service/Controller 一起提交。** 不接受「先實作，之後補測試」的做法。

---

## 十一、備料作業（Material Staging / Kitting）

> 對應舊系統 `IM01.aspx`（PC）、`aio.aspx`（手機）。  
> 有出庫需求時先建備料單，確認後鎖定庫存（`InventoryReservation`），  
> 物料搬至備料區後由倉管或作業員直接在備料儲位執行領料/發料掃碼出庫。

### 11-1 業務流程

```
出庫需求（WorkOrder / SalesOrder）
    ↓ 建立備料單（PC）── 指定備料目標倉庫 + 儲位
MaterialStagingOrder + MaterialStagingLine
    ↓ 確認 → InventoryReservation 鎖定庫存
    ↓ 移料至備料儲位（StockTransfer）
    ↓ 執行備料出庫
       PC：WarehouseOps 逐行確認
       手機：掃描品號 → 輸入取出量 → 確認
StockMovement（TypeCode=IO 工單領料 / GS 銷貨出貨）
```

**已備料先從備料區扣（2026-10-13 使用者裁決）**：工單的發料／領料（MaterialSlip 非退料類、來源為工單），
某品號有已完成（Completed）的備料單時，本單從該備料儲位扣的量必須 ≥ `min(備料移入量 − 先前已從備料儲位發出量, 備料儲位在庫, 本單數量)`；
不足的部分才可以從其他儲位領。判定在 `MaterialSlipService.ValidateIssueFromStagingFirstAsync`：
過帳（`PostCoreAsync`，桌機／手機／自動 Push 單共用）一定檢查；桌機存檔時先檢查（`draft: true`，未選儲位的品號略過），
因為離開草稿後明細儲位不能改。手機揀料一行可回報多個儲位（同 LineNo 多筆 → 伺服器拆行，沿用 SourceLineId），
畫面在備料儲位不夠時自動拆「備料區＋下一個有庫存的儲位」。退料不受影響。

### 11-2 Entity

```csharp
/// <summary>備料單主檔（舊 IM）</summary>
public class MaterialStagingOrder : IPlantEntity, IDataAuthEntity
{
    [Key, MaxLength(30)] public string StagingId { get; set; } = string.Empty;  // IM{YY}{MM}-nnnnn
    public string? PlantId { get; set; }

    /// <summary>WorkOrder | SalesOrder | Manual</summary>
    [MaxLength(30)] public string SourceType { get; set; } = "Manual";
    [MaxLength(40)] public string? SourceId  { get; set; }

    [MaxLength(20)] public string? StagingWhCode       { get; set; }
    [MaxLength(20)] public string? StagingLocationCode { get; set; }

    /// <summary>Draft | Confirmed | Picking | Completed | Void</summary>
    [MaxLength(20)] public string Status { get; set; } = "Draft";

    public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
    public string? ApprovalRequestId { get; set; }
    public string? ConfirmedBy { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    [MaxLength(500)] public string? Remarks { get; set; }
    [Required, MaxLength(450)] public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedByRoleId { get; set; }
    public string? OrganizationUnitPath { get; set; }

    public ICollection<MaterialStagingLine> Lines { get; set; } = new List<MaterialStagingLine>();
}

/// <summary>備料明細（舊 IM_D）</summary>
public class MaterialStagingLine
{
    [Required, MaxLength(30)] public string StagingId   { get; set; } = string.Empty;
    public int    LineNo      { get; set; }
    [Required, MaxLength(30)] public string ProductCode { get; set; } = string.Empty;
    [MaxLength(20)] public string? UomCode      { get; set; }
    [MaxLength(20)] public string? StockUomCode { get; set; }

    [Column(TypeName="decimal(18,4)")] public decimal RequiredQty { get; set; }  // 需求量
    [Column(TypeName="decimal(18,4)")] public decimal PickedQty   { get; set; }  // 已備量

    public int? ReservationId { get; set; }  // → InventoryReservation
    [MaxLength(500)] public string? Remarks { get; set; }

    [NotMapped] public decimal RemainingQty => RequiredQty - PickedQty;
}
```

### 11-3 DocNumber

```
DocType = "MaterialStagingOrder"，Prefix="IM"，DateFormat=YYMM，SeqLength=5
```

### 11-4 狀態機

```
Draft ─[確認 + 鎖庫存]→ Confirmed ─[揀料完成 StageAsync：實際移入備料倉]→ Completed
  └─[作廢]→ Void
```

確認時對每一明細建立 `InventoryReservation`，可用量不足則拋例外。

#### 揀料移入備料倉（`StageAsync`，2026-07-23 定案）

- `Confirmed → Completed`：逐行呼叫 `IInventoryPostingService.PostSamePlantTransferAsync`
  （來源儲位 → `StagingLocationCode`，WP 撥出＋WI 撥入一對異動、含期間鎖），回寫 `PickedQty`、
  填 `StagedBy`/`StagedAt`/`CompletedAt`。
- 任一行未填 `FromLocationCode` 整張擋下——半套移庫會讓料件散在兩處，領料無從對應扣帳。
- **預留不在此釋放**：料只是換儲位、仍未投入生產，鎖量撐到領料過帳才等額退場，
  否則備妥到領用之間出現空窗，已備妥的料可能被別張工單搶走。
- 備料倉倉別 `WarehouseType.Staging` 僅供辨識與 UI 篩選，
  **不得**比照 `Quarantine` 排除於 `GetPlantAvailableQtyAsync`——該批料已被本單預留鎖住，
  再排除一次等於同一批料扣兩次，全廠可用量會憑空短少。

#### 來源與帶料（`IStagingSourceService`）

- `SourceType`：`Manual` | `WorkOrder` | `Dispatch` | `ProductionBatch` | `SalesOrder`。
- 明細新增 `SourceWorkOrderId` / `SourceDispatchId`：生產批來源一張單橫跨多張工單，
  預留轉移、領料消耗、料況判定都必須**逐行認工單**，只看表頭會把整批鎖到同一張工單上。
- 帶料量＝需求 − 已發料 − 既有未作廢備料單已鎖量（重複開單不會把同一批料鎖兩次）；
  Backflush 料不帶入（完工才沖銷）。
- 備料倉三層解析：開單當下指定 > `WorkCenter.StagingWhCode/StagingLocationCode` >
  系統參數 `mfg_default_staging_wh` / `mfg_default_staging_location`。

#### 預留轉移與消耗

**工單來源備料單的預留轉移**（mfg-domain.md §21.5）：
- 可用量預檢＝廠級自由量 ＋ 各行所屬工單自身的 `WorkOrderMaterial` 預留（MRP 展開時已為它鎖住的量），否則 MRP 鎖料成功的工單永遠備不了料。
- 建立備料預留後，以 `ReduceBySourceAsync(WorkOrderMaterial, 該行工單, productCode, qty)` 等額扣掉工單預留（歸零轉 `Released`），同一批料不重複鎖。
- 轉移後該量由備料單持有，但 `GetReservedForWorkOrderAsync` 仍將其計入「為該工單保留的量」，工單料況不會因備料而翻成缺料。
- 作廢（`VoidAsync`）僅釋放本單預留回自由量，**不自動還原**給工單。

**領料消耗與結案釋放**（2026-07-23 補，原為缺口）：
- 發退料單過帳（`MaterialSlipController.Issue`，Issue 類別）→
  `ConsumeForWorkOrderAsync(workOrderId, productCode, qty)`：先扣該工單備料單預留（實際領走的就是那批已備妥的料），
  不足再扣工單自身 `WorkOrderMaterial` 預留；預留不足時只消耗得到的部分（超領／未預留補領屬正常業務）。
- 工單完工（`ProductionCompletionService`）與作廢（`WorkOrderController`）→
  除既有的 `ReleaseBySourceAsync(WorkOrderMaterial, …)` 外，另呼叫 `ReleaseWorkOrderStagingAsync(workOrderId)`
  釋放備料單接手的殘留鎖量，否則工單結案後仍永久鎖著庫存。
- 生產批備料單只釋放／扣減掛本工單的明細行，其他工單的鎖量不動。

### 11-5 畫面

| 畫面 | 介面 |
|------|------|
| `MaterialStagingController` CRUD | PC |
| 確認（Confirm）、完成（Complete）按鈕 | PC |
| `GET /api/mobile/staging/{id}` | 手機 |
| `POST /api/mobile/staging/{id}/pick` | 手機 |

### 11-6 手機 Pick API

```
POST /api/mobile/staging/{stagingId}/pick
Body: { lineNo, productCode, qty, locationCode, lotNo }
→ 驗證可用量 → 過帳 StockMovement → PickedQty += qty
→ 全行 RemainingQty = 0 → Status = Completed
```

---

## 十二、盤點作業（Physical Inventory Count）

> 對應舊系統 `IC01.aspx`（清冊建立 PC）、`ICC01.aspx`（清冊實盤 PC）、  
> `ail.aspx`（使用清冊手機初盤）、`ain.aspx`（不使用清冊手機初盤）、  
> `air.aspx`（手機覆盤）。

### 12-1 兩種模式

| 模式 | 流程 | 適用 |
|------|------|------|
| **WithSheet（使用清冊）** | PC 建清冊 → 快照存量 → 發清冊 → 初盤 → 覆盤 → 結帳 | 定期全盤、部分區盤 |
| **WithoutSheet（不使用清冊）** | 手機自由掃描 → 輸入實點 → 系統彙整 → 覆盤 → 結帳 | 臨時抽盤 |

### 12-2 完整業務流程

```
【PC】建立盤點清冊
  → 指定範圍（倉庫/儲位/品號區間）+ 凍結時間（SnapshotAt）
  → 系統依範圍快照 QtyOnHand → 建立 InventoryCountItem（BookQty = 快照值）

【PC 或手機】初盤（First Count）── 任意人員
  → 輸入實點數（FirstCountQty）
  → 記錄 FirstCountedBy + FirstCountedAt
  → ItemStatus: Counted

【手機（必要）/ PC（選配）】覆盤（Recount）── 另一位管理者
  ⚠️ RecountedBy ≠ FirstCountedBy（系統強制驗證職責分離）
  → 篩選需要抽驗項目 → 輸入覆盤數（RecountQty）
  → ItemStatus: Recounted

【PC】差異審核 + 結帳
  FinalQty = RecountQty ?? FirstCountQty
  Variance = FinalQty - BookQty
  → 差異 ≠ 0 → 建立 StockAdjustment（TypeCode=IC）→ 過帳
  → SheetStatus: Closed
```

### 12-3 Entity

```csharp
/// <summary>盤點清冊主檔（舊 IC）</summary>
public class InventoryCountSheet : IPlantEntity, IDataAuthEntity
{
    [Key, MaxLength(30)] public string SheetId  { get; set; } = string.Empty;  // IC{YY}{MM}-nnnnn
    public string? PlantId { get; set; }

    /// <summary>WithSheet | WithoutSheet</summary>
    [MaxLength(20)] public string CountMode { get; set; } = "WithSheet";

    // 盤點範圍（WithSheet 使用）
    [MaxLength(20)] public string? WhCodeFrom   { get; set; }
    [MaxLength(20)] public string? WhCodeTo     { get; set; }
    [MaxLength(20)] public string? LocCodeFrom  { get; set; }
    [MaxLength(20)] public string? LocCodeTo    { get; set; }
    [MaxLength(30)] public string? ProdCodeFrom { get; set; }
    [MaxLength(30)] public string? ProdCodeTo   { get; set; }

    /// <summary>庫存凍結時間（建立清冊時記錄）</summary>
    public DateTime? SnapshotAt { get; set; }

    /// <summary>Counting | Closing | Closed | Void</summary>
    [MaxLength(20)] public string SheetStatus { get; set; } = "Counting";

    public string? ClosedBy  { get; set; }
    public DateTime? ClosedAt { get; set; }
    [MaxLength(500)] public string? Remarks { get; set; }
    [Required, MaxLength(450)] public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedByRoleId { get; set; }
    public string? OrganizationUnitPath { get; set; }

    public ICollection<InventoryCountItem> Items { get; set; } = new List<InventoryCountItem>();
}

/// <summary>盤點明細（舊 ICC_D）</summary>
public class InventoryCountItem
{
    [Key] public int ItemId { get; set; }
    [Required, MaxLength(30)] public string SheetId { get; set; } = string.Empty;
    public int SeqNo { get; set; }  // 清冊序號（手機掃碼對應）

    // 位置與品號（快照時記錄）
    [MaxLength(20)] public string WhCode       { get; set; } = "";
    [MaxLength(20)] public string LocationCode { get; set; } = "";
    [MaxLength(50)] public string LotNo        { get; set; } = "";
    [MaxLength(30)] public string ProductCode  { get; set; } = "";

    /// <summary>帳面量（SnapshotAt 時點 QtyOnHand）</summary>
    [Column(TypeName="decimal(18,4)")] public decimal BookQty { get; set; }

    // 初盤
    [Column(TypeName="decimal(18,4)")] public decimal? FirstCountQty  { get; set; }  // QT_Q
    [MaxLength(450)] public string? FirstCountedBy  { get; set; }
    public DateTime? FirstCountedAt { get; set; }

    // 覆盤（RecountedBy ≠ FirstCountedBy）
    [Column(TypeName="decimal(18,4)")] public decimal? RecountQty  { get; set; }   // QT_D
    [MaxLength(450)] public string? RecountedBy  { get; set; }
    public DateTime? RecountedAt { get; set; }

    // 計算屬性（不存 DB）
    [NotMapped] public decimal? FinalQty => RecountQty ?? FirstCountQty;
    [NotMapped] public decimal? Variance => FinalQty.HasValue ? FinalQty.Value - BookQty : null;

    /// <summary>Pending | Counted | Recounted | Finalized</summary>
    [MaxLength(20)] public string ItemStatus { get; set; } = "Pending";

    [MaxLength(500)] public string? AuditNote { get; set; }  // 差異說明
}
```

### 12-4 職責分離驗證

```csharp
// 覆盤前強制驗證（service 層）
if (item.FirstCountedBy == userId)
    throw new InvalidOperationException("覆盤人員不得與初盤人員相同。");
```

### 12-5 結帳邏輯

```
foreach item where FinalQty != null:
    Variance = FinalQty - BookQty
    if Variance != 0:
        建立 StockAdjustmentLine（TypeCode=IC, Direction 視正負）
        → InventoryPostingService 過帳
    ItemStatus = Finalized
SheetStatus = Closed, ClosedBy, ClosedAt
```

### 12-6 DocNumber

```
DocType = "InventoryCountSheet"，Prefix="IC"，DateFormat=YYMM，SeqLength=5
```

### 12-7 畫面規劃

| 畫面 | 介面 | 功能 |
|------|------|------|
| `InventoryCountController.Index` | PC | 清冊列表 |
| `InventoryCountController.Create` | PC | 建立清冊（選範圍 + 模式）|
| `InventoryCountController.GenerateItems` | PC | 依範圍快照 → 產生明細 |
| `InventoryCountController.CountItems` | PC | 電腦端輸入初盤量 |
| `InventoryCountController.RecountItems` | PC | 覆盤明細（主管操作）|
| `InventoryCountController.Close` | PC | 結帳 → StockAdjustment |
| 手機 `GET /api/mobile/count/{sheetId}/items` | 手機 | 待盤明細 |
| 手機 `POST /api/mobile/count/first-count` | 手機 | 提交初盤量（兩種模式）|
| 手機 `POST /api/mobile/count/recount` | 手機 | 提交覆盤量 |

### 12-8 不使用清冊（WithoutSheet）手機 API

```
POST /api/mobile/count/first-count
Body: { sheetId, productCode, locationCode, lotNo, whCode, qty }

找不到對應 Item → 自動建立（BookQty = 目前 QtyOnHand 快照）
找到 → 更新 FirstCountQty
```

### 12-9 覆盤查詢過濾

| 過濾 | 條件 |
|------|------|
| 待覆盤 | `RecountedBy IS NULL AND ItemStatus='Counted'` |
| 已覆盤 | `ItemStatus='Recounted'` |
| 帳實不符 | `ABS(Variance) > 0` |

---

## 十三、功能實作優先序

| Priority | 功能 | 說明 |
|----------|------|------|
| **P1** | 盤點清冊建立（PC）| 手機盤點前置條件 |
| **P2** | 手機初盤 API（兩種模式）| 主要作業在手機 |
| **P3** | 手機覆盤 API | 品質管控 |
| **P4** | PC 差異審核 + 結帳 | 閉環 |
| **P5** | 備料單建立（PC）| 高頻倉庫作業 |
| **P6** | 手機備料出庫 API | 備料完整閉環 |
