---
description: SmartFactory MES 進階庫存規範。實作預留機制(MrpRunLine/WorkOrderMaterial/Dispatch/MaterialStagingOrder)、跨廠調撥(ICO/ICC)、模組整合(PUR/ORD/OUT/QCR/成本結算)、手機上下架API時套用。核心庫存規範見 inventory.md。
---

# SmartFactory MES — 庫存運作規範（進階）

> **核心庫存規範**（資料模型、三層單據、過帳流程、UOM、時區、Service API、單號規則）詳見 `inventory.md`。
> 本文件補充以下進階場景：預留機制、跨廠調撥、模組整合、手機 API、作業畫面清單。

---

## 一、預留機制

### 1-1 預留類型

程式碼使用 `ReservationSourceType` enum + `ReservationStatus`，對應舊規格 Soft/Hard 說明如下：

| ReservationSourceType | 來源 | 類比強度 | 說明 |
|---|---|---|---|
| `MrpRunLine = 0` | MRP 自動計算 | Soft | 每次 MRP 重跑可覆寫（ReleaseBySource 後重建） |
| `WorkOrderMaterial = 1` | 生產工單確認 | Hard | 工單釋放/完工前不釋放 |
| `Dispatch = 2` | 銷售訂單出貨 | Hard | 出貨單確認後佔用至過帳 |
| `MaterialStagingOrder = 3` | 備料單確認（IM） | Hard | Confirm 時建立，Void/完工時釋放 |

`ReservationStatus` 生命週期：
- `Active` — 有效佔用（扣可用量）
- `Released` — 已釋放（不佔可用量）
- `Consumed` — 已實際出庫消耗（不佔可用量）

### 1-2 可用量計算

```csharp
// 廠級可用量（出庫驗證用 / MRP Fstock 基礎）
// 程式碼：IInventoryReservationService.GetPlantAvailableQtyAsync
decimal QtyAvailable = Math.Max(0,
    QtyOnHand - Reservations.Where(r => r.Status == ReservationStatus.Active).Sum(r => r.ReservedQty));

// MRP Fstock（需求日期 D 的展望可用量，已納入 Scheduled Receipts）
// Fstock(productCode, D) =
//   QtyOnHand
//   + Σ PurchaseOrderLines（Approved && !IsClosed && DeliveryDate ≤ D
//                           && ReceivedStockQty < StockQty → 計入 StockQty − ReceivedStockQty〔庫存單位〕）
//   − Σ InventoryReservations（Status=Active，RequiredDate ≤ D）
//   + inTransitAdjustment（全模式才加，見 §6-5）
//
// ⚠️ PO Scheduled Receipts 僅計入 ApprovalStatus = Approved 的採購單（Draft/PendingApproval 不列入）
```

### 1-3 ATP（Available-to-Promise）完整公式

全模式倉庫（Full Warehouse Mode）下，回應客戶承諾交期所用的可承諾庫存：

```
ATP(productCode, D) =
    QtyOnHand                                               // 帳面在庫
    + Σ PO ScheduledReceipts（Approved, ExpectedDate ≤ D）  // 已確認採購在途
    + Σ GoodsReceipts pending put-away（Approved, 尚未上架）  // 已驗收待上架
    − Σ GoodsShipment pending pick（Approved, 尚未下架）    // 已確認出貨待下架
    − Σ GoodsReturn pending down-shelf（已退回待下架，尚未減庫）
    − Σ InventoryReservations（Active，RequiredDate ≤ D）   // 所有來源預留
```

> **全模式 vs 簡化模式**（系統參數 `inv_simplified_mode`）：
> - 全模式（`inv_simplified_mode=false`）：GoodsReceipt 核准後庫存不立即增加，需完成上架才反映；GoodsShipment 核准後庫存不立即減少，需完成下架才反映。ATP 需將這兩段在途量加回/減去。
> - 簡化模式（`inv_simplified_mode=true`，預設）：入庫核准即過帳，上下架僅為倉位作業，`QtyOnHand` 已即時反映，在途修正項歸零。
>
> **MRP Fstock 與模式的關係**：`MrpEngine.ReserveAvailableAsync` 在每次執行時讀取 `inv_simplified_mode`。全模式下會額外計算 `pendingPutAway − pendingPick` 作為 `inTransitAdjustment` 補入 Fstock，避免低估可用供給。

---

## 二、跨廠庫存移動

跨廠調撥的現行設計（`CrossPlantTransfer`、狀態機、XP／XI 過帳、成本傳遞、跨廠存取）**以 `inventory.md` §四-C 為準**。

> 2026-10-08 移除本節原文：原文的 `InterPlantTransfer`／`TransferStatus{Draft,Shipped,Received,Void}`／ICO・ICC 代碼是實作前的草案，
> 與 `inventory.md` §四-C 的舊版也互相矛盾，程式兩者都沒採用。

---

## 三、模組整合

### 3-1 採購（PUR）

```
PUR 採購入庫    → GoodsReceipt（TypeCategory=II）→ Post → +Inventory / +CostLayer(FIFO)
PUR 採購退貨    → GoodsIssue（TypeCategory=IO）→ -Inventory / -CostLayer
```

### 3-2 銷售（ORD）

```
ORD 訂單確認    → InventoryReservation(Dispatch, Hard)
ORD 銷貨出庫    → GoodsIssue（TypeCategory=IO）→ -Inventory / ConsumeFifo → Release Reservation
ORD 銷貨退回    → GoodsReceipt（TypeCategory=II）→ +Inventory / +CostLayer
```

### 3-3 委外（OUT）

```
OUT 工單確認    → InventoryReservation(WorkOrderMaterial, Hard)
OUT 委外出料    → GoodsIssue（TypeCategory=OI）→ 入委外倉
OUT 委外回收    → GoodsReceipt（TypeCategory=OR）→ 入待驗倉 → QCR
```

### 3-4 品檢（QCR，MIL-STD-1916）

```
入廠            → 先入待驗倉（WarehouseType=Quarantine）
QCR 放行        → IW 移轉：待驗倉 → 良品倉（TypeCategory=QR）
QCR 拒收        → IO 出庫：待驗倉 → 報廢倉（TypeCategory=QJ）
```

### 3-5 成本結算

```
月結前：IX 期末結帳 → InventorySnapshot（ProductCode, LocationCode, LotNo, Qty, UnitCost, SnapshotDate）
FIFO：出庫成本 = ConsumeFifoCostAsync（依入庫日期先消耗）
MA：出庫成本 = Inventory.UnitCost × StockQty；入庫後更新 UnitCost
```

---

## 四、手機 API（上下架作業）

```csharp
// GET  /api/inv/location/{locationCode}
// GET  /api/inv/product/{productCode}/stock?lotNo=&locationCode=
// GET  /api/inv/grn/{docNo}/pending-putaway
// POST /api/inv/putaway  Body: { docNo, lineId, locationCode, qty }
// POST /api/inv/pick     Body: { docNo, lineId, locationCode, qty }（含可用量驗證）
// POST /api/inv/post/{docNo}（所有 LocationDetail 完成後過帳）
```

所有 API 均 `[Authorize]`，與 PC 端共用 `IInventoryService`。

手機 API 確認清單：
```
[ ] 所有 API [Authorize]
[ ] Pick 含可用量驗證（QtyAvailable < 0 攔截）
[ ] Post 前驗證所有 Line 的 LocationDetail 合計正確
[ ] 併發處理：掃碼提交後以 xmin 樂觀鎖偵測衝突，失敗時提示「庫存已變動，請重新掃描」
[ ] 離線策略：採「查詢連線、作業緩存」；若離線期間發生併發衝突，於上線同步時由人工排除
```

---

## 五、作業畫面清單

| 畫面 | 路由 | 說明 |
|------|------|------|
| 倉庫維護 | `/BAS/Warehouse` | 含 WarehouseType 設定 |
| 儲位維護 | `/BAS/StorageLocation` | 依倉庫篩選，全域唯一代號 |
| 產品類別維護 | `/BAS/ProductCategory` | 含批號/預設倉/成本法設定 |
| 庫存異動類別維護 | `/BAS/InventoryTransactionType` | 系統類型唯讀 |
| **單號設定** | `/BAS/DocNumberConfig` | ✅ 已實作 |
| 庫存查詢 | `/INV/Stock` | 依品項/倉庫/儲位/批號查詢 |
| 進貨單 | `/INV/GoodsReceipt` | 採購入庫（三層上架） |
| 出貨單 | `/INV/GoodsShipment` | 銷售出庫（三層下架） |
| 倉間移轉單 | `/INV/StockTransfer` | IW，出原位+入新位 |
| 庫存調整單 | `/INV/StockAdjustment` | IA，人工盤差調整（預設需簽核） |
| 盤點作業 | `/INV/InventoryCount` | IC 建立 → 實盤 → 結帳 |
| 跨廠調撥單 | `/INV/CrossPlantTransfer` | ICO → ICC 兩段式 |
| 報廢單 | `/INV/ScrapDoc` | IS，報廢出庫，須簽核 |
| 借料單 | `/INV/LoanDoc` | IL，借出/借入兩向，須簽核 |
| 樣品單 | `/INV/SampleDoc` | ISA，樣品出庫，須簽核 |
| 重工用料單 | `/INV/ReworkMaterial` | IRW，重工領用 |
| 委外用料單 | `/INV/OutsourceMaterial` | IOM，委外領用 |
| 備料單 | `/INV/MaterialStaging` | IM，鎖定預留（Confirm → 建立 Reservation） |
| 庫存帳簿 | `/INV/StockLedger` | 逐筆異動查詢（StockMovement） |
| 庫存異動查詢 | `/INV/StockMovement` | StockMovement log 彙總 |
| 庫存評價 | `/INV/InventoryValuation` | FIFO 成本層報表 |
| 呆滯庫存分析 | `/INV/SlowMovingInventory` | 依異動頻率排序 |
| 庫存不足分析 | `/INV/StockShortage` | 低於安全庫存的品項 |

---

## 六、MRP Fstock Scheduled Receipt 整合規格（2026-05-29）

### 6-1 背景

MRP 展開時的 Fstock 現行只計算在庫量與 InventoryReservation。納入 PO Scheduled Receipts 後，MRP 不會對已訂購在途的物料重複觸發採購需求。

### 6-2 Scheduled Receipt 資料來源

| 來源 | 條件 | 計入量 |
|------|------|--------|
| `PurchaseOrderLine` | `PO.ApprovalStatus = Approved` AND `!IsClosed` AND `ReceivedStockQty < StockQty` | `StockQty − ReceivedStockQty`（庫存單位，2026-10-27 前為採購單位） |

> Draft / PendingApproval 採購單**不計入**：未確認訂單不應影響 MRP 供給面。

### 6-3 Fstock 查詢實作要點

```csharp
// 在 MrpEngine.ReserveAvailableAsync 或 Fstock 輔助方法加入
var scheduledReceipts = await _context.PurchaseOrderLines
    .Include(l => l.PurchaseOrder)
    .Where(l => l.ProductCode == productCode
             && l.PurchaseOrder.PlantId == plantId
             && l.PurchaseOrder.ApprovalStatus == DocumentApprovalStatus.Approved
             && !l.IsClosed
             && l.ReceivedQty < l.OrderQty
             && l.DeliveryDate <= requiredDate)
    .SumAsync(l => l.OrderQty - l.ReceivedQty);

decimal fstock = qtyOnHand + scheduledReceipts - activeReservations;
```

### 6-4 與訂單↔MRP 聯動管控的關係

- 詳細的 SO/IO 結案釋放預留、PO dirty flag、WO Release 缺料警示規格，見 `mfg-domain.md §21`。
- 本節僅定義庫存面的可用量計算公式，業務觸發規則在 MFG 規格中描述。

### 6-5 全模式倉庫的在途差異（2026-06-09 已實作）

| 在途類型 | 描述 | 全模式影響 |
|---------|------|----------|
| GR pending put-away | 進貨單已核准但尚未上架 | QtyOnHand 未增加，Fstock 偏低 |
| GoodsShipment pending pick | 出貨單已核准但尚未下架 | QtyOnHand 未減少，Fstock 偏高 |

`MrpEngine.ReserveAvailableAsync` 在 `inv_simplified_mode=false` 時計算：

```csharp
var pendingPutAway = await _db.GoodsReceipts
    .Where(gr => gr.PlantId == plantId && gr.ApprovalStatus == Approved && gr.Status == Confirmed)
    .Join(_db.GoodsReceiptLines.Where(l => l.ProductCode == productCode), ...)
    .SumAsync(l => l.StockQty - l.QtyPutAway);

var pendingPick = await _db.GoodsShipments
    .Where(gs => gs.PlantId == plantId && gs.ApprovalStatus == Approved && gs.Status == Confirmed)
    .Join(_db.GoodsShipmentLines.Where(l => l.ProductCode == productCode), ...)
    .SumAsync(l => l.StockQty - l.QtyPutAway);

inTransitAdjustment = pendingPutAway - pendingPick;
var take = Math.Min(available + scheduledReceipts + inTransitAdjustment, demandQty);
```

> 簡化模式（`inv_simplified_mode=true`）下 `inTransitAdjustment = 0`，Fstock 計算與之前相同。
