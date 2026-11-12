# 測試補強實施規劃（非順向流程 / 邊界值 / 過帳資料異常）

> 建立日期：2026-07-29
> 起因：盤點資料流程測試的涵蓋範圍，發現三類結構性缺失。
> 狀態：Phase 0–8 全數完成（2026-07-31 覆核）。單元 2592 綠、整合 223 綠、慣例 35/35。
> 主線無待辦，但各 Phase 內文留下 4 項「刻意不做／歸他案」的殘留，已彙整於文末〈5. 殘留待辦〉。

---

## 1. 背景與已驗證事實

盤點對象為三層測試（單元 2063 Fact / 整合 82 Fact / UI 66 Fact）與 22 張過帳單據的
Delete/Save 守門路徑。以下是**逐檔讀過程式碼後確認**的事實，非推測：

### 1.1 守門本身完整，無可繞過路徑

三種一致的守門模式：

| 模式 | 單據 | 判定依據 |
|---|---|---|
| 倉庫作業量 | ScrapDoc / LoanDoc / SampleDoc / OutsourceMaterial / ReworkMaterial / StockAdjustment / StockTransfer / GoodsReceipt / GoodsReturn | `Lines.Any(l => l.QtyPutAway > 0)` |
| 單據狀態 | CrossPlantTransfer / MaterialStaging / LandedCostAllocation / OutsourceReceipt / OutsourceRework / GoodsShipment / MaterialSlip / OutsourcePurchaseOrder | `Status != Draft` |
| 下游拋轉量 | PurchaseOrder(ReceivedQty) / SalesOrder(ShippedQty + MRP 展開) / PurchaseRequest(OrderedQty) / InventoryCount(ItemStatus) | 下游已消耗即擋 |

三個關鍵確認：

1. **簡化模式不繞過守門**：`PostDocSimplifiedAsync` 內含
   `SetQtyPutAway = qty => line.QtyPutAway = qty`，故「確認即過帳、不經倉庫作業」的單據
   一樣會回寫 QtyPutAway，`QtyPutAway > 0` 這個代理指標在簡化模式下仍然有效。
   IQC 放行認列 AP 那條路徑（`IqcInspectionService.cs:233`）同理。
2. **修改與刪除共用守門**：`GoodsReceiptController.cs:370-373` 在 Save 就擋
   QtyPutAway 與 ReturnedQty，不是只擋 Delete。
3. **AP/AR 單據沒有 Delete action**：FIN 模組只有 BankAccountController 有刪除。
   已認列的應付/應收只能作廢回沖。

> 本規劃**不修改任何既有守門邏輯**，目的是把既有正確行為釘住，並補上偵測層。

### 1.2 結構性風險：過帳資料與單據之間無 DB 約束

`StockMovement` 對單據**只有索引、沒有外鍵**
（`ApplicationDbContextExtensions.cs:1018-1027`），靠 `DocNo + DocLineNo` 字串弱關聯。
`JournalEntry` 對來源單同樣是弱關聯（`SourceDocType` + `SourceDocId`）。

- 好處：58 處 Cascade delete 都不觸及 StockMovement，不會誤刪過帳紀錄
- 壞處：**資料庫層完全不阻止孤兒過帳**。守門 100% 依賴應用層，任一路徑漏寫
  （新增單據型別、直接 SQL、守門欄位選錯）都會靜默留下庫存異動與傳票，無 FK 報錯

### 1.3 「重複過帳」有真實前科，防護只有一支回歸測試

`ScrapDocController.cs:238-243` 註解記載：

> `RemoveRange` 僅標記舊行為 Deleted，不會從 `doc.Lines` 記憶體集合移除；若不 Clear，
> 下方重新 Add 新行後過帳會把殘留的舊行一併算入，造成沿用舊行號的行被重複過帳。

全庫 **19 支** controller 使用同樣的 RemoveRange + 重建寫法，回歸測試只有
`SmartFactory.MES.Tests/INV/StockAdjustmentDuplicateLineReproTests.cs` 一支。

### 1.4 測試缺口

| 缺口 | 現況 |
|---|---|
| 刪除/修改後的過帳殘留 | **零覆蓋**。所有 Delete 守門測試只斷言「拋例外」，無一支回頭查 StockMovement / JournalEntry / AP·ARInvoice 是否留下孤兒。守門若被改壞，測試會綠燈 |
| 流程層非順向 | 9 支流程測試中 7 支純順向；2 支逆向（PostAndVoid / PurchaseReturn）測的是合法業務逆流，非錯誤操作被擋 |
| 邊界值 / 等價劃分 | `[Theory]` 僅佔 3.6%（78/2063）。既有 BVA 集中在 AVCO、委外罰款、成本分攤三處，屬個別自覺而非制度 |
| 守門測試覆蓋不均 | MaterialStaging（有 Reservation 副作用）、MaterialSlip（直接動庫存）為零覆蓋 |
| 孤兒稽核 | DataAuditService 14 支稽核中無孤兒偵測。有 `AuditDuplicateJournalAsync`（重複傳票）、`AuditPoReceivedQtyDriftAsync` / `AuditSoShippedQtyDriftAsync`（拋轉量漂移） |

---

## 2. 階段規劃

### 相依關係

```
Phase 0 (盤查) ──┬──> Phase 1 (殘留斷言) ──> Phase 3 (中途反悔) ──> Phase 5 (收口)
                 └──> Phase 2 (R16 + 回歸)
Phase 4 (邊界/等價) ── 完全獨立，可任意時點平行插入
```

Phase 0 是唯一硬前置。

---

### Phase 0：孤兒過帳稽核（唯讀盤查）

**先做的理由**：唯一能回答「歷史上是否真的產生過爛帳」的動作，且純唯讀。
結果決定 Phase 1 的定位是防護性或修復驗證。

- **交付物**：`IDataAuditService.AuditOrphanPostingAsync`
- **檢查對象**：
  - `StockMovement.DocNo` 反查來源單據
  - `JournalEntry.SourceDocId` 反查來源單據（`Manual` 略過）
  - `APInvoice.SourceDocId` / `ARInvoice.SourceDocId` 反查來源單據
- **實作策略**：**反向集合比對**，不建 TypeCode → 資料表映射。
  理由：`TypeCode` 取自使用者可維護的 `InventoryTransactionType`，且跨單據重複
  （SalesReturn 與 GoodsReceipt 都用 `"GR"`），不是可靠的型別鍵。
  改為撈出全部單據表的單號集合，凡不屬於任一集合的 DocNo 即為孤兒。
- **DocNo 正規化（必要）**：後沖領料的
  `DocNo = workOrderId + "-BF"`（`ProductionCompletionService.cs:125`），
  比對前須剝除已知後綴，否則所有後沖異動會被誤報為孤兒。
- **關鍵約束**：**先不掛進 `RunAllAsync`**。
  `RunPreClosingAuditAsync` 直接呼叫 `RunAllAsync`（`DataAuditService.cs:66`），
  而 `ClosePeriod_AuditFails_ThrowsAndStaysClosing` 會把期間卡在 Closing 狀態。
  掛入時機在 Phase 5，前提是 Phase 0 報表全零。
- **驗收**：對 dev 庫實跑，產出各單據型別孤兒筆數報表
- **風險**：低（唯讀）
- **規模**：1 服務方法 + 1 測試檔，約 3–4 小時

---

### Phase 1：刪除/修改後的過帳殘留斷言

- **交付物**：`SmartFactory.MES.IntegrationTests/Postings/PostingResidueTestBase.cs`
  ＋ 22 張單據的派生測試
- **三條標準斷言**：
  1. 刪除成功後，該單號的 StockMovement / JournalEntry / AP·ARInvoice 零筆
  2. 修改成功後，過帳筆數與金額不變
  3. 守門擋下時，過帳資料完全不動
- **為何走整合測試**：需真 PostgreSQL。InMemory 無交易語意，`RemoveRange`
  的殘留行為與正式庫不同——這正是 StockAdjustmentDuplicateLineRepro 存在的原因。
- **關鍵約束**：必須進**既有** xUnit collection。新增第二個 collection 會與既有
  collection 平行共用同一測試庫，觸發 migration（pg_type / EFMigrationsHistory）
  與種子（PK_Plants / IX_MenuPermissions）的雙 23505 競態，失敗數跨輪跳動且
  擴散到無關類別。
- **驗收**：22 × 3 全綠；乾淨庫與重跑庫各一輪
- **風險**：中（測試庫種子涵蓋 22 張單據，為主要工作量）
- **規模**：1 基底 + 22 派生，約 2–3 天

#### 實作紀錄：INV 12 張完成（批次 A / B）

實際落地時足跡從三項擴充為四項，並分出兩層基底：

| 檔案 | 角色 |
|---|---|
| `PostingResidueTestBase` | 足跡擷取（庫存 / 總帳 / 子帳 / **預留**）＋ 三條標準斷言 |
| `InvDocResidueTestBase` | 「Save→Confirm→Delete」同構單據的 4 條共用斷言 |

- **加入預留維度的原因**：備料單確認鎖的是 `InventoryReservation` 而非庫存異動。
  預留殘留不會讓任何報表對不起來（在手量完全正確），只是可用量莫名少一塊，
  且指向一張已不存在的單——比孤兒異動更難查，故納入 `IsEmpty` 判定。
- **前置條件斷言改用 `!IsEmpty`**（原為 `MovementLines > 0`）：讓
  「確認不產生庫存異動」的單據（備料單）也能共用同一條前置條件。

四張不能套樣板的單據，各自的差異與應對：

| 單據 | 差異 | 應對 |
|---|---|---|
| CrossPlantTransfer | 過帳分兩段（XP 出 / XI 入），中途 CancelConfirm 走**沖銷**不是抹除 | 加測「筆數加倍、淨額歸零」；XI 段測不到（`TestAuthHandler` 廠別固定 P1，Receive 要求目的廠身分） |
| MaterialStaging | 確認鎖預留、揀料才動庫存 | 加測預留生命週期（鎖 → 作廢釋放 → 揀料後仍在） |
| LandedCostAllocation | 完全不動數量，只滾單價與傳票 | 把 `Inventory.UnitCost` 本身當一項足跡；重複確認不得二次墊高 |
| InventoryCount | 足跡掛在**另一張採番出來的調整單**上，只靠 `Remarks` 字串相連 | 先反查調整單再擷取足跡；重點測重複結帳 |

**InventoryCount 的結構性弱點**（測試中已釘住）：`PostDocSimplifiedAsync` 的冪等
檢查以「單號＋行號＋類別」為鍵，而第二次結帳會**另外採番出新單號**，冪等完全攔不到。
該路徑唯一的防線是 `SheetStatus != "Counting"`，破了就是同批差異扣兩次庫存，
且兩張調整單看起來都合法。→ 結構弱點本身未修，見〈5. 殘留待辦〉#4。

#### 實作紀錄：PUR/ORD 批次 C

到這裡浮現出**第三種殘留形態**，前兩批都沒有：

| 形態 | 代表 | 為什麼難查 |
|---|---|---|
| 孤兒過帳 | 庫存異動／傳票留著、單據沒了 | 對帳時查得到，但追不到來源 |
| 孤兒鎖定 | 預留留著（批次 B） | 帳面數量全對，只有可用量莫名少一塊 |
| **上下游欄位懸空** | PO.ReceivedQty / SO.ShippedQty / PR.OrderedQty / GS.ReturnedQty | 沒有異動也沒有傳票，**完全無聲** |

第三種最值得補測試，因為它連 `PostingFootprint` 都涵蓋不到——那些是單純的欄位回寫。

| 單據 | 額外釘住的方向 |
|---|---|
| GoodsReceipt | 刪草稿回補 `PurchaseOrderLine.ReceivedQty`；確認一次同時產生庫存＋傳票＋AP |
| GoodsShipment | 回補 `SalesOrderLine.ShippedQty`；確認一次同時產生庫存＋傳票＋AR |
| GoodsReturn | 應付變動在**來源進貨單**的 AP 發票上，不在退貨單身上 |
| SalesReturn | 應收變動在**來源出貨單**的 AR 發票上；另回補 `GoodsShipmentLine.ReturnedQty` |
| PurchaseOrder | 擋下時 `PurchaseRequestLine.OrderedQty` 不得先被減掉（守門與回補的**順序**） |
| SalesOrder / InternalOrder | MRP 需求列引用；兩者**共用 `MrpRunLine.SalesOrderId` 欄位**，靠 NO- 前綴區分 |

批次 C 觀察到的 ORD／FIN 不對稱，已依裁示於後續修正：回沖（負向）見〈批次 C 後續：兩處回沖修正〉，
正向累加上限見〈批次 C 後續之二：正向累加上限〉。

**OutsourceReceipt 移到批次 D**：它的 `OutsourcePurchaseOrderId` 是必填且確認流程
會回寫 OPO 狀態與在途量，種子必須先有完整的 工單 → OPO 鏈；與批次 D 的
OutsourcePurchaseOrder 共用同一組前置，分開做等於把同一份種子寫兩次。

#### 順帶揪出的正式缺陷（非測試問題）

`IDateTimeService.Now` 回的是 Kind=Unspecified 的當地時間，寫進 timestamptz 欄位
會被 Npgsql 直接拒收：

| 位置 | 後果 |
|---|---|
| `LandedCostService.ConfirmAsync` | 確認採購分攤費用單在 PostgreSQL 上 **100% 失敗** |
| `InspectionOrderController`（FPI／檢驗結果／派工暫停 3 處） | 檢驗結果存檔失敗；且與 `IqcInspectionService`（寫 UtcNow）、`SpcAnalysisService`（以 UTC 區間篩選）語意不一致 |

兩處都逃過單元測試：InMemory 不檢查 `Kind`，而測試又把 `IDateTimeService.Now`
模擬成 `Kind=Utc`——等於把唯一會露餡的地方也一併掩蓋。已修為 `DateTime.UtcNow`
並補**規則 R17** 守住整個缺陷類別（規則本身以「重新植入違規會紅」驗證過）。

#### 批次 C 後續：兩處回沖修正

批次 C 記錄的兩處 ORD／FIN 不對稱，經裁示後修正。兩者的共同性質是
**PUR 側早已寫對、ORD 側沒跟上**——不是新設計，是把既有慣例補齊。

| # | 位置 | 修正 | PUR 側對照 |
|---|---|---|---|
| 1 | `GoodsShipmentController.ReverseShippedQtyAsync`<br>`SalesReturnController.ReverseGsReturnedQtyAsync` | `-=` → `Math.Max(0m, … − …)` | `PurchaseService.RestorePrOrderedQtyAsync`、`ReceivedQty`／`GoodsReceiptLine.ReturnedQty` 回沖 |
| 2 | `IARInvoiceService.ReverseForReturnedQtyAsync` | 第 2 參數 `string productCode` → `int shipmentLineNo`，改比對 `ARInvoiceLine.SourceLineNo` | `IAPInvoiceService.ReverseForReturnedQtyAsync` 一開始就收行號 |

**下限 0 為什麼是不變量而非防禦性寫法**：已出貨／已退貨量是累計語意，負值無意義；
而訂單剩餘可出量＝`OrderQty − ShippedQty`，負的已出貨量會讓額度算得比訂購量還大，
超出的部分**沒有任何守門會擋**。刪除守門保證「只回沖一次」是行為，下限是資料不變量——
行為可能因日後改動而破，不變量不會。

**行號比對的真正理由**：同一張出貨的同品號可能有多行（不同倉／批號／單價）。
品號比對固定命中第一個尚有 `Qty` 的行 → 退第 2 行時扣到第 1 行、**用錯的單價算金額**，
且第 1 行被扣光後第 2 行永遠回沖不到。回沖資料一直都在
（`SourceLineNo` 與 AR 發票同一個 commit 引入，開票路徑始終有寫），
只是 API 沒收；手開發票 `SourceDocType="Manual"`，不在回沖查詢範圍，故無遺留資料風險。

呼叫端一併收斂為「只回沖有連結來源出貨明細的行」（`ShipmentLineNo.HasValue`），
與 `GoodsReturn` 的 AP 側同一守則；手工加入、未連結來源的行本就無對應發票明細可沖。

驗證方式同 R17——**先確認測試會紅**：暫時把兩處改回舊寫法，
`Delete_POST_ShippedQtyReversal_FloorsAtZero`、`Delete_POST_ReturnedQtyReversal_FloorsAtZero`、
`Reverse_SameProductOnTwoLines_HitsRequestedLineOnly` 如期失敗，再還原。

順帶揪出一個測試種子缺口：`WarehousePutAwayAccrualIntegrationTests` 手工建 AR 發票時
未填 `SourceLineNo`，改行號比對後回沖靜默 no-op。這正是「手工種子與真實開票路徑分歧」
的典型——真實路徑一直有寫，只有種子沒有。

#### 批次 C 後續之二：正向累加上限

修完回沖（負向）後，同一組欄位的**正向累加**被指出也不對稱。盤點全庫四處
`+=` 累計回寫，只有一處有上限守門：

| 位置 | 累計欄位 ← 上限 | 原狀 | 處置 |
|---|---|---|---|
| `PurchaseService.UpdatePoReceivedQtyAsync` | `ReceivedQty` ← `OrderQty` | ✅ 已 throw | 不動（本次的對照組） |
| `PurchaseService.UpdatePrOrderedQtyAsync` | `OrderedQty` ← `RequestQty` | ✅ 已 throw | 不動 |
| `PurchaseService.UpdateGrReturnedQtyAsync` | `ReturnedQty` ← `ReceiptQty` | ❌ 無上限 | **補守門** |
| `GoodsShipmentController.UpdateSalesOrderShippedQtyAsync` | `ShippedQty` ← `OrderQty` | ❌ 無上限 | **補守門** |
| `SalesReturnController.UpdateGsReturnedQtyAsync` | `ReturnedQty` ← `ShipQty` | ❌ 無上限 | **補守門** |
| `MaterialSlipController.BackfillWorkOrderMaterialQtyAsync` | `IssuedQty`／`ReturnedQty` ← `RequiredQty` | ❌ 無上限 | **不補**（見下） |

原本記錄的觀察只寫「ORD 側沒跟上」，實際盤點後發現**PUR 的退貨側同樣沒有**——
缺口不是模組邊界，而是「進貨／轉單有守門、退貨沒有」。只修 ORD 會留下同一類洞。

**超退的實際傷害不只是數字難看**：退貨會負向回沖來源 AP／AR 發票，
退超過進貨／出貨量等於把應付／應收沖到低於實際金額。帳上看起來是「已開立金額」，
對帳時只會顯示這張單的子帳與單據金額對不起來，追不到源頭。庫存側則憑空多進／多退一批貨。

**MFG 為什麼不補**：`WorkOrderMaterial` 的超領補料是正常製造場景（不良重投、耗損補發），
`IssuedQty > RequiredQty` 有業務意義，不是資料異常。這是真實的領域差異，不是漏補。

**修改路徑不會誤擋**：兩個 ORD Save 與 `UpdateGoodsReturnAsync` 都是
「先 Reverse 舊值 → 再 Update 新值」，守門比對的是扣掉本單之後的餘額，
本單自己出過的量不會被算成別人的額度（`Save_POST_EditDraft_DoesNotCountItsOwnQtyTwice` 釘住）。

守門一律用 `>` 而非 `>=`——剛好出滿／全退必須放行，否則最後一批貨永遠出不掉。
三組測試都成對寫（超量拒絕 ＋ 等量放行），避免只驗一半而把守門寫得過緊。

驗證方式同前：三道守門暫時停用，三支超量測試如期變紅，
五支邊界／等量改單測試維持綠（證明它們不是靠守門通過的），再還原。

**UOM 一致性**（既有缺口，本次未動）：累加的是業務單位量（`ShipQty` → `ShippedQty`），
若出貨行的 `UomCode` 與訂單行不同，累加本身就已經在加不同單位的數字——
守門沿用同一組數字比較，與累加保持一致，但沒有修正這個更底層的問題。
PUR 側有同樣情況。若要根治須以 `StockQty`（共同基準單位）為累計基礎，屬另一項變更。
→ 見〈5. 殘留待辦〉#1。

---

### Phase 1 批次 D：MFG／委外三張單（20 支，全數原本零覆蓋）

`MaterialSlip`（發退料單）、`OutsourcePurchaseOrder`（委外採購單）、
`OutsourceReceipt`（委外收貨單）。三者共用 `MfgDocResidueTestBase`
（生產批次 → 工單 → 工序 → 工單用料，委外再從工單接出）。

**沒有沿用 `InvDocResidueTestBase`**：那個樣板假設「Save 建單 → Confirm 過帳 →
Delete 判 QtyPutAway」，三張單沒有一張符合——發退料單走五段狀態機、過帳動作叫
`Issue`，委外採購單的 `Confirm` 根本不碰庫存。硬套只會逼出一堆覆寫。

| 單據 | 釘住的殘留方向 |
|---|---|
| MaterialSlip | 庫存異動 ＋ `WorkOrderMaterial.IssuedQty` 回寫；**沒有任何回沖路徑**，過帳後唯一出口是「不准動」 |
| OutsourcePurchaseOrder | 純第三種殘留——足跡永遠是空的，壞掉的是別張單上的數字 |
| OutsourceReceipt | 庫存 ＋ 總帳 ＋ 應付三方；唯一有作廢路徑的一張 |

#### 找到的正式缺陷：刪除委外採購單未回沖 `OutsourceDemand.TransferredQty`

`Convert` 轉單時 `demand.TransferredQty += SplitQty`，但 `DeleteConfirmed`
只清掉 `WorkOrder` 的三個委外欄位，**已轉量留在原地**。後果不是數字難看而是死結，
而且同時卡住兩處、都沒有畫面能解除：

| 卡住的地方 | 機制 |
|---|---|
| 轉委外清單 | `Worklist` 只列 `TransferredQty < DemandQty` → 這筆建議直接消失，再也轉不出新單，但料根本沒訂出去 |
| MRP 重跑 | `MrpEngine` 把 `TransferredQty > 0` 判為「已轉委外＝對外承諾」並 **Blocked** 擋下重跑，理由指向一張已不存在的採購單 |

修法＝刪除時 `TransferredQty = Math.Max(0m, … − SplitQty)`，與全庫其他回沖同一寫法。
已驗紅（停用回沖後 `DraftDelete_RestoresDemandTransferredQty` 失敗，其餘五支維持綠）。

#### 兩個刻意分開測的維度

**兩段式後門**：`Cancelled` 是可刪狀態。若已過帳的單能被取消，
「取消 → 刪除」兩步就繞過刪除守門，而每一步各自看起來都合法。
狀態機的每個非草稿格子都要各自擋住——故 MaterialSlip 測 Posted／Confirmed 兩格的
取消與刪除，OutsourcePurchaseOrder 測 Confirmed／Void 兩格，OutsourceReceipt 測作廢後仍不可刪。

**簡化 vs 完整模式**：委外收貨單的簡化模式 `Confirm` 直接過帳（Posted），
完整模式只轉 `Confirmed`——但**應付發票兩種模式都當下就開出來**。
作廢守門判的是 `Status == Posted`，所以完整模式下「確認 → 作廢」是合法路徑，
而那條路徑必須連同 AP 一起作廢，否則發票變成掛在作廢單上的孤兒。
程式碼註解點名了這個風險但沒有測試釘住，`Full_ConfirmThenVoid_AlsoVoidsApInvoice` 補上（已驗紅）。

#### 種子踩到的坑

`DateOnly.ToDateTime(...)` 產出 `Kind=Unspecified`，寫進工單／生產批次的 timestamptz
欄位被 Npgsql 直接拒收——**與 R17 守的是同一個缺陷類別**，只是這次踩在測試種子而不是產品程式碼。
改用 `DateTime.SpecifyKind(…, DateTimeKind.Utc)` 常數。

另：`OutsourceReceipt.OutsourcePurchaseOrderId` **沒有外鍵**，
「委外採購單 Confirmed 不可刪」是唯一擋住收貨單變成孤兒的東西，故種子一律把 OPO 種成
Confirmed——種在 Draft 等於測一個實際流程走不到的狀態。

---

### Phase 2：RemoveRange 重複過帳回歸

兩條路徑並行：

| 路徑 | 內容 | 取捨 |
|---|---|---|
| **靜態規則 R16**（主力） | `RemoveRange(x.Lines)` 之後必須有 `x.Lines.Clear()` | 一次守住 19 支含未來新增者；成本低、無測試庫依賴 |
| 動態回歸測試 | 抽出 StockAdjustment 那支的手法，補到過帳量最大的 5–6 支 | 靜態規則抓不到「Clear 了但邏輯仍錯」的變形 |

- **關鍵約束**：新增 R16 須同步登錄 `consistency-framework.md`〈規則索引〉表，
  否則 R0 meta 規則擋 build。編號取 R16——R15 已被「簽核回寫落點」佔用。
- **實作紀錄（已完成）**：規則初版只認 `x.Lines.Clear()`，對 PUR 的
  `x.Lines = 新集合` 寫法誤報 3 筆；且判定窗口用固定字元數時，
  `GoodsReturn` 那支（先跑完整個 foreach 才指派）也會誤報。
  修正為「兩種安全寫法都接受」＋「以大括號深度取到方法結束」後，**baseline 為空**。
  誤報若被凍進 baseline，反而會遮蔽該檔案日後的真違規，故不接受帶違規上線。
- **規模**：約 1 天

#### 動態回歸（已完成）：6 支，各 1 支

| 單據 | 模組 | 重建寫法 | 殘留的實際去向 | 斷言點 |
|---|---|---|---|---|
| StockTransfer | INV | `Lines.Clear()` | 過帳請求多一行舊量 | `PostSamePlantTransferRequest.Lines` |
| ScrapDoc | INV | `Lines.Clear()` | 同上 | `PostDocSimplifiedRequest.Lines` |
| OutsourceMaterial | INV | `Lines.Clear()` | 同上（另牽動在外原料 AVCO 餘額） | `PostDocSimplifiedRequest.Lines` |
| GoodsShipment | ORD | `Lines.Clear()` | **上游 `SalesOrderLine.ShippedQty` 被重複累加** | 訂單已出貨量 |
| SalesReturn | ORD | `Lines.Clear()` | **上游 `GoodsShipmentLine.ReturnedQty` 被重複累加** | 出貨已退貨量 |
| PurchaseOrder | PUR | `Lines = 新集合`（reseat） | **`TotalAmount` 被灌水** | 表頭金額 + 明細集合 |

三個設計決定：

**選單據的準據不是只有過帳量**。三支 INV 是「Save 當下就過帳」型，殘留直接變成重複扣庫存；
但出貨單與銷貨退回的 Save **不過帳**（過帳在 Confirm，屆時單據已重新載入，殘留咬不到），
它們的殘留改咬同一請求內的上游回寫——`ReverseShippedQtyAsync` 扣掉舊值後，
`UpdateSalesOrderShippedQtyAsync` 走 `doc.Lines` 再加回去，殘留舊行就被多加一次。
這種殘留**不留庫存異動也不留傳票**，屬第三種殘留（上下游欄位懸空），
任何以單號查過帳足跡的檢查都看不見。只挑過帳量最大的幾支會整批漏掉這個形態。

**PUR 那支挑的是寫法而非量**。全庫只有 PurchaseService 用 reseat（`po.Lines = 新集合`），
R16 對 Clear 與 reseat 一視同仁放行，但只有動態測試能證明換上去的那份真的沒帶到舊行。
它的觀察點也不同：緊接在重建之後的 `po.TotalAmount = po.Lines.Sum(l => l.AmountLocal)`
直接吃這個集合，殘留代價是金額而非數量，且該金額一路流進三方對帳與採購金額報表。

**GoodsReceipt 看過但沒選**。它的上游回寫 `UpdatePoReceivedQtyAsync(vm.Lines)` 吃的是 VM 而不是
`gr.Lines`，`TotalAmount` 也取自區域變數 `tempGr`——殘留在這支身上沒有可觀察的出口，
補一支只會得到恆綠的測試。改挑 PurchaseOrder（同檔同寫法、但確實消費重建後的集合）。

**驗紅**：六處分別停用（五支移除 `Lines.Clear()`、PUR 改成往舊集合 Add），六支如期全紅，還原後
單元 2430 全綠。順帶確認 R16 對這六種破法也全數攔下——**兩層在「字面 `RemoveRange(x.Lines)`」
這個形狀上是重疊的**，動態層的獨立價值在另外兩件事：一是它斷言的是*數值*
（新數量、新金額、上游累計量），R16 對數值一字未提；二是它不綁寫法，
改用輔助方法、`ExecuteDelete` 或帶條件的 `RemoveRange` 重建明細時 R16 的 regex 直接失效，
而動態測試照樣成立。

（附帶查清：R16 的判定窗口是「RemoveRange 所在區塊」而非整個方法——把 `Clear()`
搬到 `else` 區塊之外仍會被判違規。比原先寫的「取到方法結束」更嚴，這是好的。）

---

### Phase 3：流程層「中途反悔」測試

| 流程 | 情境 | 斷言 |
|---|---|---|
| P2P | 進貨單已上架 → 刪進貨單 / 改採購單數量 | 被擋，AP + GL + 庫存三方不動 |
| O2C | 出貨已過帳 → 刪銷貨訂單 | 被擋（ShippedQty + MRP 雙重守門），AR 不受影響 |
| MFG | 工單已完工繳庫 → 改工單 / 刪領料單 | 被擋，WIP 與成本結算不變 |
| 委外 | 已發料 → 刪委外採購單 | 被擋，在外原料餘額不變 |

- 整合層沿用 `IntegrationTests/Flows/FlowTestBase.cs`（每流程專屬廠別 + GL/庫存通用斷言）
- UI 層沿用 `UITests/FlowTestBase.cs` 的 `DialogMessages` 收後端錯誤訊息
- **規模**：4 支整合 + 2 支 UI，約 2 天。風險：中（UI 兩支對 seed 狀態敏感）

#### 整合層（已完成）：`Postings/MidFlowReversalTests.cs`，4 支

改掛 `PostingResidueTestBase`（HTTP + 過帳足跡）而非 `Flows/FlowTestBase`——
四個反悔動作的守門**全都寫在 Controller**，`FlowTestBase` 只有服務層 scope，
直接呼叫服務等於繞過要驗的東西。唯一例外是改採購單，`UpdatePurchaseOrderAsync`
的守門在服務層，那支就直接呼叫服務（走 HTTP 反而會先被表單模型驗證擋下，
得到一支「看起來綠、其實沒碰到守門」的測試）。

| 流程 | 反悔動作 | 與 Phase 1 的差別 |
|---|---|---|
| P2P | 刪採購單 ＋ **改採購單數量** | 改單那條 Phase 1 完全沒測 |
| O2C | 刪銷貨訂單 | Phase 1 有測，但當時**沒有出貨單也沒有應收發票**可以被動到 |
| MFG | **作廢工單** | 全新——工單沒有刪除動作，作廢是唯一出口，從未被測過 |
| 委外 | 刪委外採購單 | Phase 1 有測，但當時下游一張收貨單都沒有 |

差別的核心：Phase 1 是**合成狀態**（直接把 `ReceivedQty = 5` 寫進種子再試刪除），
本批是**真的把鏈跑出來**再回頭動上游。守門擋下與「擋下之前已經先改了東西」
在畫面上完全一樣，差別只在別張單據上的數字與帳裡的那幾筆——
「先回沖、後檢查」這種順序錯誤只有在下游真的有帳的時候才會露出來。

#### 打出來的缺口：工單作廢不擋已領未退的料

`WorkOrderController.Void` 原本只擋一件事：派工已回報產出（`QtyDone > 0`，§4.2 明文）。
**「還沒開始生產」與「料還在倉裡」不是同一件事**——領料單過帳的當下料就已經實際離開倉庫、
庫存扣掉、`IssuedQty` 回寫，而那全都發生在任何人回報產出之前。
所以一張料已經領光的工單可以照樣作廢，作廢後沒有任何東西會來承接那批料：
工單不會結案、成本結算不會跑。

而且**生產領料不入總帳**（形態轉換，見 `BuildJournalLines` 的帳務語意），
所以這個缺口不會讓試算表不平——它只在數量面留下無主的已領料，帳上完全安靜。
這正是這批測試要找的那一類：帳面正確，壞掉的是別的地方。

已補第二道守門（`IssuedQty − ReturnedQty > 0` 即拒絕作廢），
並同步更新 `mfg-domain.md` §4.2——原規格只寫了第一道。

#### 驗紅範圍（誠實界線）

MFG 那支是先紅後綠：補守門之前它如實失敗（作廢成功了）。
另外三支的「擋下」半邊沿用 Phase 1 已驗紅的守門，本批未重複破壞驗證；
「下游足跡不動」半邊目前沒有可觸發的失敗路徑，屬看門狗斷言——
它要等到有人把守門改成「先回沖、後檢查」才會叫。這是刻意留的，不是漏驗。

#### UI 層（已完成）：`UITests/Flows/MidFlowReversalUiTests.cs`，2 支

分工很窄但實在：整合層那 4 支打 HTTP 端點，證明的是**後端會擋**；
這 2 支證明的是使用者**看得到被擋**。中間隔著一整條前端接線——
列表按鈕是否渲染 → `data-action="deleteItem"` 的 handler 是否掛在 window 上 →
confirm 之後是否真的發出請求 → 後端訊息是否被吐回畫面。
壞掉的症狀是「按了沒反應」：dispatcher 找不到 handler 只 `console.warn` 就靜默結束，
後端測試全綠，因為請求根本沒送出去。

兩個設計決定：

**上游鏈用 SQL 種，不用畫面一步步建。** 要驗的是刪除那一下，
前面建採購單、匯入進貨、上架過帳那十幾步與待驗行為無關，走完只會讓失敗訊息
指向不相干的那一段（P2P 主幹另有專屬流程測試）。種子只設定唯一前提：
累計收貨量／出貨量 > 0——那正是兩道守門判斷的依據。

**斷言「有第二則對話框訊息」，不比對訊息字串。** 文案是在地化資源，改一個字就紅，
而要釘的不是文案。只有 confirm 那一則就代表按下確定之後什麼都沒發生，
正是 handler 沒掛上的靜默失敗樣態。

已驗紅：把前提改成「下游零進度」後刪除應當成功，2 支如期失敗（只剩 confirm 一則訊息）。

#### 本機跑 UI 測試的環境陷阱（實測記錄）

1. **`ConnectionStrings__DefaultConnection` 環境變數對 app 無效。**
   `appsettings.Development.json` 的 `DefaultEncrypted` 有值，而
   `ResolveConnectionStringAndContext` 先看它、非空就整條走加密路徑，
   `DefaultConnection` 連讀都不會讀到。症狀極具誤導性：app 正常啟動、
   登入成功、畫面也對，只是連的是 `ProjectZ` 開發庫而不是測試庫，
   於是所有測試都以「查不到資料」的形式失敗，看起來像測試寫錯。
   解法（不必改任何檔案）：兩個都用命令列參數蓋掉，命令列優先級最高——
   ```
   ASPNETCORE_ENVIRONMENT=Development ./bin/Debug/net10.0/SmartFactory.MES \
     --urls http://localhost:5110 \
     --ConnectionStrings:DefaultEncrypted= \
     --ConnectionStrings:DefaultConnection="…Database=SmartFactoryMES_Test…"
   ```
   （`ASPNETCORE_ENVIRONMENT` 必須是 Development：內部授權限開發環境，
   Production 會在啟動時直接拋 SecurityException。）

2. **測試庫的 admin 密碼與 UITests 的候選密碼不一致。**
   `SmartFactoryMES_Test` 由整合測試建立，那條路徑走 `TestAuthHandler` 不經登入，
   密碼從沒對齊過 UI 測試的 `Dev12345` / `E2eBootstrap!2026`。
   一次性把 admin 的 `PasswordHash` 對齊即可（整合測試不受影響，它不走密碼登入）。

3. 測試庫與整合測試共用同一個 `SmartFactoryMES_Test`。E2E 種子的刪除全部
   `E2E-%` 限定，整合測試用 `RESID` 前綴與 `ZZ*` 廠別，兩邊不相撞；
   nightly 也是同一個庫跑完整套。

---

### Phase 4：邊界值分析與等價劃分

#### 4-1 精度邊界表

| 維度 | 邊界值 |
|---|---|
| UOM dp | 0 / 1 / 4 / 未定義品號 |
| Currency dp | 0（日圓型）/ 2 / 4 |
| 中點進位 | `.5` / `.05` / `.0005`（對齊 `MidpointRounding.AwayFromZero`） |
| 超精度 | 第 5 位小數輸入 → 截斷或四捨五入 |
| 匯率 | 極小 / 極大 / 乘法方向（本幣 = 外幣 × Rate） |

##### 實作紀錄：`Tests/BAS/PrecisionBoundaryTests.cs`（已完成，51 格）

打點選在 `IUomService.RoundQtyAsync` 與 `ICurrencyService.RoundAsync`：所有單據存檔前
都經過這兩支，它們的邊界行為就是全系統的邊界行為，不必逐張單據重測。

實際落地時，「小數位數」這個維度本身比原表列的更需要切分——原表只列了值域，
但真正出事的是**主檔設定本身**的等價類：

| 小數位數等價類 | 原本行為 | 處置 |
|---|---|---|
| 0 / 1 / 4（合法） | 正確 | 釘住 |
| 未定義（空 / null / 查無代碼） | 退回 dp=0 | 釘住現狀，並修正相反的 XML 註解 |
| **已停用** | dp 退回 0，數量小數被無聲吃掉 | **修**：dp 對照表不再濾 `IsActive` |
| **超出儲存上限（5–10）** | 應用層算 6 位、DB 存 4 位，兩邊不同值 | **修**：`[Range(0,4)]` ＋ 服務層夾擠 |
| **負值** | `Math.Round(x,-1)` 拋例外 → 存檔一律 500 | **修**：同上 |

四個缺口的共同成因：`DecimalPlaces` 只有畫面上的 `min="0" max="10"`，模型上沒有任何
驗證，直接 POST 就繞過了；而 `max="10"` 這個上限本身也是錯的——全庫金額與數量欄位
以 `numeric(18,4)` 為主（241 欄），5 位以上存不進去。上限已收斂到
`DecimalPrecision.MaxStorable = 4`，讓 `[Range]` 與四捨五入共用同一個常數。

**停用主檔那條最值得記**：dp 對照表跟著下拉選單一起濾 `IsActive`，於是停用 KG（dp=4）
之後，既有單據重新存檔時 `1.2345 KG` 被當成整數單位捨成 `1`——庫存憑空少掉 0.2345，
畫面上單位還是 KG，看不出任何異狀。小數位數是「怎麼記數」的性質，不是「還能不能挑選」，
`IsActive` 只該擋在下拉選單那一層。幣別側同理（停用日圓 → dp 0 變 2）。

順帶釘住三條容易被誤判成 bug 的既有行為，只記錄不改：
換算往返不等冪（`3210 → 100.31 USD → 3209.92`，故對帳要用匯率快照重算而非往返）、
`RateType.Mid` 的 `Rate>0` 守門發生在平均之後（只填賣匯時會拿到「賣匯的一半」且不報錯）、
以及數量欄位的 scale 在全庫並不一致（103 欄 4 位、27 欄 6 位）——後者要收斂需大批 migration，
留待日後。

- **驗紅**：8 格如期紅（3 格 `[Range]` 缺、2 格負值拋例外、1 格超精度、2 格停用主檔），
  修完 51/51 綠，全單元 2481 綠。

#### 4-2 無效輸入等價類（UI 層）

擴 `CrudSmokeTestBase` 成標準斷言，一次套 25 個主檔畫面：
單一必填缺漏 / 日期區間倒置 / 非本廠代碼 / 已停用主檔代碼 / 超長字串 / 數量為 0 或負。
（目前唯一的無效輸入測試是 `NewPanel_EmptySubmit`，只涵蓋全空這一類。）

##### 實作紀錄：`CrudSmokeTestBase` ⑩–⑬（已完成）

原本設想的六類，實際只有四類能寫進共用樣板。分界是**能不能從 DOM 自行推導出要測什麼**：

| 等價類 | 落點 | 說明 |
|---|---|---|
| 單一必填缺漏 | ⑩ 樣板 | 逐欄清空（其餘填妥）再送出，全部須被拒 |
| 超長字串 | ⑪ 樣板 | 兩條：文字欄位皆須宣告長度上限；拔掉 `maxlength` 後超長仍須被拒 |
| 數量 0 或負 | ⑫ 樣板 | 只測畫面宣告 `min>0` 的欄位，送 -1 |
| 日期區間倒置 | ⑬ 樣板 | 以 `XxxFrom/XxxTo`、`XxxStart/XxxEnd` 命名自動配對 |
| 非本廠代碼 | **不進樣板** | 得知道哪個欄位是哪張主檔的外來參照，DOM 推導不出來 |
| 已停用主檔代碼 | **不進樣板** | 同上 |

後兩類留在各畫面自行補。硬塞進樣板只會變成一條永遠跳過的斷言。
→ 至今無任何派生類補上，見〈5. 殘留待辦〉#2。

**⑬ 原本是空跑的**：BAS 那 16 個主檔畫面沒有任何一個有 From/To 日期對，
所以這條斷言加進去之後探測不到任何畫面。補了 `VendorPriceListSmokeTests`
（全站第一個帶生效起訖的派生類）它才真正跑起來，並立刻打出缺口。

**打出來的缺口**

| 缺口 | 影響 |
|---|---|
| 生效區間可倒置（`VendorPriceList` / `OutsourceQuotation` / `Routing` 三張主檔全缺） | 三張都被 `From <= today && (To == null \|\| To >= today)` 這種區間查詢消費。倒置時存檔正常、列表也看得到，只有查價／查途程永遠落空——症狀是「主檔明明建好了卻不生效」 |
| 9 個畫面載入 `_CrudPanelScripts` 卻沒有 `#successToast` | 見下 |

**`#successToast` 那條值得單獨記**：`crud-panel.js` 的 `toastSuccess()` 是
`new bootstrap.Toast(getElementById('successToast')).show()`，元素不存在時建構子直接拋，
而它位於 `submitPanel` 成功分支的**第一行**（`toastSuccess(); refreshList();`），
於是例外連帶讓 `refreshList()` 永遠跑不到。後果是**資料已經存進資料庫，但面板不關、
列表不刷新、什麼提示都沒有**，例外只落在 `.catch` 變成一行 `console.error`。
使用者看到「按了儲存沒反應」，於是再按一次——撞唯一鍵才第一次看到錯誤訊息。
刪除與 `postAction` 走的是同一支 `toastSuccess`，故不限於存檔。
已補上 R18 靜態規則守住這個頁面契約（零違規）。

**兩處刻意不修，只記錄**

- `InspectionSpec` / `Instrument` 的 `Remarks` 欄位沒有長度上限——但它們的資料庫欄位是
  `text` 而非其他畫面慣用的 `varchar(n)`，不會撞 22001。屬既有設計差異，
  以 `AllowedUnboundedTextNames` 明確豁免；要對齊成 varchar 得一併出 migration。
- 數量欄位的 scale 在全庫並不一致（103 欄 4 位、27 欄 6 位），收斂需大批 migration。

**跨測試層共用測試庫的殘留互相干擾（實測）**

`MaterialSlipActionSmokeTests` 4 支在全套跑時失敗，訊息是「存檔後列表沒有任何可開啟的單據」。
追下去不是產品缺陷也不是測試寫錯：Phase 1／3 的整合測試為了避開已關帳期間，
把單據日期一律設在 **2097 年**，而領料單列表預設 `SlipDate DESC` 每頁 15 筆——
32 筆 2097 年的 `RESIDMS-*` 剛好佔滿第一頁，UI 測試當天新建的那張永遠被擠到第二頁去。
nightly 跑乾淨庫不會遇到；但本機依序跑「整合 → UI」就會踩到。
清掉殘留即恢復（51/51 綠）。根治屬 Phase 5：整合測試應自行清理，或種子重置一併purge。

- **驗紅**：⑩ 15 個畫面、⑫ 2 個畫面、⑬ 1 個畫面經反轉斷言確認真的有探測到並被正確擋下；
  ⑪ 的第二條在 15 個畫面如期紅（且揪出 locator 延遲求值的自身缺陷：拔掉 `maxlength`
  之後同一個 locator 會改指向下一個欄位，超長字串被填進上限更寬的欄位 → 永遠綠）。
- **總數**：UI 496 支，僅餘 2 支 `Flows` 紅（`OrderToCashFlowTests` /
  `ProcureToPayFlowTests`，屬他案未完成工作）。單元 2496 綠。
  → 此數字為當時快照，其後他案可能已修；狀態見〈5. 殘留待辦〉。

#### 4-3 四大單據狀態機矩陣

以 `[Theory]` 窮舉合法與非法轉移。範本為 `QCR/NcrStateMachineTests.cs`
（全庫唯一以 Theory 掃狀態轉移矩陣者），照抄至採購單、進貨單、工單、出貨單。

- **規模**：三子項各約 1 天，共約 3 天。風險：低

##### 4-3 實作紀錄（已完成）

**沒有照抄範本，因為範本本身沒有咬合力。** `NcrStateMachineTests.cs` 把 controller 的
守衛條件**在測試檔裡再寫一遍**（`private static bool CanEdit(status) => status == Open;`），
所以它釘住的是「當初的意圖」，不是產品行為——controller 那邊改掉條件，測試照樣全綠。
四張單照抄只會多出四份會漂移的文件。

改成先把守衛抽成生產程式的純函式（`Modules/_Shared/DocumentStateMachines.cs`），
controller 改為呼叫它，矩陣才有唯一事實來源：

| 留在 controller | 移進狀態機 |
|---|---|
| 權限、找不到單據、DB 查詢（已上架量／已退貨量／派工回報量／已領未退料量） | 狀態與旗標的判斷本身 |
| resx 取字（守衛回傳原因列舉，controller 映射到原有 key，訊息逐字不變） | 檢查順序＝訊息優先順序 |

守衛回傳的是**原因列舉**而非 bool：同一個動作有多條拒絕理由（進貨單確認有三條），
收斂成 bool 會讓使用者失去「卡在哪一關」的資訊。

**測試形式（每個動作兩種斷言）**

1. **合法格全集**：跑完「狀態 × 簽核狀態 × 旗標」的整個笛卡爾積，收集回傳 `Allowed`
   的格子，與寫死的期望集合逐字比對。這條是**列舉**而非條件式複寫，所以守衛改了、
   或 `DocStatus` 之後多一個成員而守衛沒處理，集合就會多／少一格而直接紅。
2. **拒絕原因表**：手寫代表格釘住訊息優先順序（同時違反兩條時使用者看到哪一句）。
   優先順序錯了不會有功能異常，只會讓人看到「僅草稿可刪除」而不是「倉庫已開始上架」，
   然後往錯的方向查。

`Tests/StateMachines/` 4 檔共 73 支。另補 `IntegrationTests/Postings/PendingApprovalGuardTests.cs`
4 支走真 HTTP——矩陣證明不了 controller 有去呼叫守衛，那一段只有整合測試碰得到。

**打出來的缺口：三張單各缺一格，剛好互補**

| 單據 | 原本缺 | 後果 |
|---|---|---|
| 進貨單 | 刪除沒擋審核中 | 單刪掉了 `ApprovalRequest` 還留著，簽核人從「待我簽核」點進去只看到 openPanel 404 |
| 採購單 | 刪除沒擋審核中 | 同上 |
| 出貨單 | 修改沒擋審核中 | 送審後照樣改得動品項與數量，簽核人核准的是另一張單 |

採購單／進貨單本來就有修改守衛、出貨單本來就有刪除守衛——每張單各自看都「有守衛」，
把三張攤平成同一張矩陣才看得出缺的是互補的那一格。出貨單那道新守衛刻意讀
**DB 的 `ApprovalStatus`** 而非 vm 帶回的值（採購單／進貨單沿用既有寫法讀 vm，
而 vm 的 hidden 欄位是使用者可改的，那道守衛其實繞得過去；一併改動風險較高，另記）。
→ 已於 Phase 6 全數收口，見〈Phase 6〉。

**刻意保留的不對稱（已寫進測試註記）**

- 出貨單確認允許 `Confirmed` 再按一次（供「先確認、後來才開簡化模式」的部署完成過帳），
  進貨單只允許 `Draft`。重複過帳由 `InventoryPostingService` 的逐行冪等性檢查擋住，
  不是靠狀態判斷。
- 出貨單確認的合法格含 `InTransit`——出貨單實際上永遠不會進那個狀態（跨廠調撥用的），
  守衛沒排除它。列進期望集合是為了讓它是「已知且無害」而不是「沒人看過」。

**踩到的坑**：整合測試斷言中文訊息必須先 `HtmlDecode`。Razor 預設 encoder 把 CJK
全部輸出成數值字元參照（`無法編輯` → `&#x7121;&#x6CD5;&#x7DE8;&#x8F2F;`），
`body.Contains("無法編輯")` 永遠不會中——訊息其實在，測試卻看不到，
於是「守門有沒有接上」會被誤判成沒接上。

**驗紅**：狀態機反轉 3 處 → 9 支紅；controller 三道新守衛各自移除 → 整合 3 支紅。
新測試檔自帶 `Dispose` 清掉自己的 `RESIDPAG-*`（2097 年單據佔住列表第一頁的坑見 4-2 紀錄）。

**總數**：單元 2569 綠（+73）、整合 213 綠（+4）。

---

### Phase 5：收口

| 項目 | 內容 |
|---|---|
| 孤兒稽核掛入 `RunAllAsync` | **前提：Phase 0 報表全零** |
| 補零覆蓋單據 | MaterialStaging、MaterialSlip 的 Delete/Edit 守門測試 |
| 跨模組並行衝突 | 沿用 xmin 並發測試的雙 scope 模式：同批庫存揀料 vs 盤點凍結、同張發票兩張付款單並行核銷 |
| CI 納管 | 新增測試進 nightly；確認整合測試仍走單一 collection |

- **風險：高** —— 掛入 `RunAllAsync` 若有殘留髒資料會直接擋住關帳作業
- **規模**：約 1.5 天

#### 實作紀錄（已完成）

**① 孤兒稽核掛入 `RunAllAsync`**

前提先驗：以唯讀 SQL 等價物對三個資料庫實跑。

| 資料庫 | 孤兒 | 說明 |
|---|---|---|
| `ProjectZ`（dev） | **0** | 掛入的前提成立 |
| `SmartFactoryMES` | — | 沒有任何表，未使用 |
| `SmartFactoryMES_Test` | **41** | 全部是測試自己的殘留，見③ |

風險評估比計畫預估低：`RunPreClosingAuditAsync` 的關帳測試是**單元測試**（InMemory 自建資料），
不吃共用測試庫，所以測試庫那 41 筆不會擋到 CI。因此**不加系統參數開關**——
關帳被擋下時正確的動作是去清資料，一個能把稽核關掉的開關只會被用來繞過它。

掛入後 `ClosePeriod_AuditPasses_Closes` 立刻紅：它種了一張 `SourceDocType=APPayment`
的傳票卻沒有建對應的 `APPayment`。那不是測試被稽核誤傷，是**種子本來就不自洽**，
補上那筆付款單即綠。另補兩支測試釘住「有掛進去」這件事本身
（`RunAllAsync_IncludesOrphanPostingAudit`）——稽核方法存在但沒被呼叫時，
Phase 0 的 18 支測試全部還是綠的。

**② 補零覆蓋單據**

`MaterialStaging` 的 Delete/Edit 早已由 `EditFormOrNull` 覆蓋；缺的是 `MaterialSlip` 的
**修改**路徑（原本只有刪除與取消）。修改比刪除更難察覺：刪掉一張已過帳的單至少會讓
列表少一筆，改掉數量卻是庫存異動與單據明細從此各說各話，兩邊都還在。

補測試時打出一個 UI 缺陷：`MaterialSlip/_Form.cshtml` **完全沒有 validation summary**，
於是 controller 六處 `AddModelError("", …)`（非草稿、找不到單據、無權限、來源工單未核准…）
的訊息**渲染不到任何地方**——使用者按存檔後面板原地重繪、什麼字都沒有，
與 4-2 的 `#successToast` 是同一種「回饋被靜默吞掉」。

全庫掃描後定性：70 支面板表單有 37 支沒有 ModelOnly 摘要，但其中只有 4 支的 controller
真的會加整表錯誤（`MaterialSlip`／`VendorPriceList`／`OutsourceReceipt`／`InternalOrder`），
其餘 33 支的欄位級錯誤走 `asp-validation-for` 顯示正常。修那 4 支，並加
**R19** 靜態規則守住這個配對（Core 端 0 筆，不需動 Core）。R19 上線第一次跑就抓到
我手動掃描漏掉的 `InternalOrder`。

**③ 跨模組並行衝突**

既有並發測試全是「同一張單被按兩次」；真正沒人測的是**兩個模組同時動同一列**——
各自的狀態守門都通過（看的是不同單據），撞在一起的是共同寫入的那一列。

| 場景 | 共用列 | 結果 |
|---|---|---|
| 揀料 vs 盤點差異調整 | `Inventory` | 已有 xmin，測試補上即綠 |
| 兩張付款單核銷同一張應付發票 | `APInvoice` | **原本無並發權杖** |
| 兩張收款單核銷同一張應收發票 | `ARInvoice` | **原本無並發權杖** |

後兩者是真缺口：核銷是「讀未結餘額 → 驗證 → 累加」的讀改寫，兩張付款單各自讀到
`PaidAmount = 0` 都通過「不超出未結餘額」的驗證，接著各自寫入絕對值——後到者覆蓋先到者，
**發票只認一次付款，而銀行帳戶被扣了兩次、兩張付款單都是 Paid**。
供應商收到兩筆錢而應付帳款只沖掉一筆，帳面完全自洽，只有對帳才看得出來。
補 `APInvoice`／`ARInvoice` 的 `UseXminAsConcurrencyToken`（零 DDL migration，
Npgsql 對系統欄位不產生實際 SQL）＋兩個 controller 的 `DbUpdateConcurrencyException`
catch（否則使用者看到 500）。刻意不動 `FinPaymentService`（他案正在改）。
→ 該服務至今仍無 catch，見〈5. 殘留待辦〉#3。

**踩到的坑**：並發測試裡「寫回與快照相同的值」時 EF 判定沒有變更、根本不發 UPDATE，
權杖也就不會作用——測試會以「沒有拋例外」失敗，看起來像 xmin 沒生效，
其實是測試自己什麼都沒改。盤點那一格原本寫回 100（＝快照值）就是這樣假紅的。

**④ CI 納管**

`ci.yml` 與 `nightly.yml` 的 `dotnet test` 都沒有任何 `--filter`，新測試自動納入，
無需改 workflow。「整合測試仍走單一 collection」改成守門而非人工確認：
`CollectionLayoutGuardTests` 以反射列舉本組件所有 `[Collection]` 名稱，
超出 `Integration`／`ScheduleIntegration` 即紅，並另一支釘住
`DisableTestParallelization` 沒被移除。口頭約定守不住，兩道都變成測試。

**⑤ 整合測試殘留根治（4-2 發現的項目）**

三個殘留來源各自修在來源處，而不是靠人工清庫：

| 來源 | 真因 | 修法 |
|---|---|---|
| `RESIDMS-*`／`RESIDXP-*`（40 組孤兒異動） | `ClearWorkOrderChainAsync` 刪領料單卻沒刪它過帳產生的 `StockMovement` | `PostingResidueTestBase` 加 `Dispose`，以前綴清過帳＋單據 |
| `FLOW-*-OPEN`（3 組） | 流程測試的期初入庫直接呼叫過帳服務給假單號；只在建構子 Reset（跑之前清），跑完的留在庫裡 | `Flows/FlowTestBase` 加 `Dispose` 再 Purge 一次 |
| `WAP-AR1` | AR 發票的 `SourceDocId` 指向刻意不建的出貨單；同樣只在跑之前清 | 該測試類別加 `Dispose` 呼叫既有 `Purge` |

前綴清理的第一版自己製造了 188 筆新孤兒：子帳是「單據 → 發票 → 傳票」兩層，
發票的 `SourceDocId` 指單據（`RESID%`）但**傳票的 `SourceDocId` 指的是發票號**，
只按前綴刪會把發票刪掉而留下指向它的傳票。改成先把發票號抓成陣列、連傳票與
配款分攤一起刪。修完跑整套後測試庫孤兒歸零。

**驗紅**：孤兒稽核從 `RunAllAsync` 移除 → 1 支紅；R19 加入前 → 4 支違規；
`APInvoice`／`ARInvoice` 的 xmin 移除 → 2 支紅；collection 允許清單縮減 → 1 支紅。

**總數**：單元 **2572** 綠（+3）、整合 **219** 綠（+6）。
dev 庫與測試庫孤兒過帳皆為 0。

---

### Phase 6：修改守衛不得採信前端帶回的簽核狀態（已完成）

Phase 4-3 留下的唯一未做項。原以為是採購單／進貨單兩處，攤平後是**系統性**的。

**缺陷**：各 `_Form.cshtml` 的 `<input type="hidden" asp-for="ApprovalStatus" />`
刻意放在 `<fieldset disabled>` **外面**（disabled 的欄位瀏覽器不送，放裡面存檔會少欄位），
所以那個值是使用者可改的。守衛若判 `vm.ApprovalStatus`，自己組一個把
`PendingApproval` 改成 `Draft` 的 POST 就能改掉一張審核中的單；
存檔後狀態又被 `ResolveSaveStatusAsync` 寫回 `PendingApproval`，
`ApprovalRequest` 那筆完全沒動——**簽核人核准的是他沒看過的內容，事後也看不出來改過**。

**範圍**：不是兩支，是 **13 支**（PUR 3／ORD 1／INV 9）＋ `MaterialStaging`。
複製貼上把同一個寫法散出去，而每支單看都「有守衛」，只有攤平成一張清單才看得出來。
第一次 grep 還漏掉 `MaterialStagingController`（resx 鍵是 `StagingErrPendingApproval`
而非 `PendingApprovalCannotEdit`）——這正是要有靜態規則的理由。

**修法**：新增 `Modules/_Shared/ApprovalDocumentQueries.cs`，
`_context.LoadApprovalStatusAsync<T>(id)` 從 DB 投影出簽核狀態，各守衛改讀它。

三個刻意的設計選擇，都寫在該檔的文件註解裡：

| 選擇 | 理由 |
|---|---|
| 做成 `ApplicationDbContext` 擴充方法，不掛 `ModuleControllerBase` | base 的 `Db` 走 `HttpContext.RequestServices` 延遲解析，而 controller 單元測試是直接 new 的、沒有 HttpContext——掛在 base 上會讓那些測試倒在 `GetRequiredService`（**已實測 7 支紅**，才改的設計） |
| 不用 `FindAsync`，投影＋`AsNoTracking` | `FindAsync` 會把實體放進 identity map，更新路徑「重建同鍵實體再 `Update()`」會撞「已被追蹤」 |
| 走一般查詢（吃廠別＋資料權限兩層過濾器），看不到的單回 `null` | 不是漏洞：守衛放行後更新路徑同樣查不到，會以「找不到單據」失敗，是 fail-safe |

**守門**：新增 **R20** 靜態規則——controller 不得讀 `(vm|model|form|dto).ApprovalStatus`
做判斷（寫入 `=` 不算）。`baseline-R20.txt` 零違規。

**測試**（三層，各測不同的事）：

| 層 | 檔案 | 測什麼 |
|---|---|---|
| 整合（真 HTTP，2 支） | `Postings/PendingApprovalGuardTests.cs` | DB 種 `PendingApproval`、POST 帶 `ApprovalStatus=Draft`，必須被擋且 `Remarks` 未變 |
| 單元（代表性 1 支） | `INV/LoanDocControllerTests.cs` | 同上但走 controller；原有測試兩邊都是 `PendingApproval`，**分不出守衛讀的是哪一邊** |
| 單元（helper 10 支） | `_Shared/ApprovalDocumentQueriesTests.cs` | 空 id／查無此單／四種狀態往返／不進 identity map |

**順帶修正**：`PurchaseRequestControllerTests.Save_POST_PendingApproval_ReturnsFormWithError`
原本只在 vm 上設狀態、DB 沒種那張單，改讀 DB 後守衛不再觸發而炸 NRE——
測試自己本來就沒測到它宣稱在測的東西。

**踩到的坑**：helper 測試的 `ExistingDoc_ReturnsDbStatus` 四格全回 `null`——
資料權限過濾器比對 `CreatedByRoleId == CurrentRoleId`，種子給了 `"1"` 卻沒設
`_context.CurrentRoleId`，那張單對查詢而言不存在。

**驗紅**：PO／GR 守衛各自還原成讀 vm → 整合 2 支紅；`StockTransferController`
還原成讀 vm → R20 1 支紅；`LoanDocController` 守衛改讀 vm → 單元 1 支紅。

**總數**：單元 **2583** 綠（+11）、整合 **221** 綠（+2）。

---

### Phase 7：收掉五個「刻意留下的界線」（已完成）

Phase 0–6 結案時列了五項刻意不做的取捨。逐項處理後，其中三項的成因與原本的描述不同——
記在這裡，因為判斷錯誤的地方比做對的地方更值得留。

**① `RateType.Mid` 的 `Rate > 0` 守門發生在平均之後**（原歸類為「只記錄不改」）

原描述說這是「容易被誤判成 bug 的既有行為」。不是——它就是 bug。
`(Buy + Sell) / 2` 在只維護單邊時得到「單邊的一半」，那個值大於 0，**通過守門**一路用下去。
規格指定成本與存貨評價用 `Mid`，所以影響是整批評價金額少一半，而且沒有任何地方報錯。
已改成兩邊都要有值才算平均，缺一邊回 `null`（與「查無匯率」同路徑，由呼叫端擋下單據）。
`currency.md` 補上這條。驗紅：還原成算完才守門 → 2 格紅。

**② QCR 三個備註欄是無上限的 PG `text`**

原以為是 InspectionSpec／Instrument 兩欄，實際三欄（漏了 `InstrumentSerial.Remarks`；
兩個類別寫在同一個檔案裡，第一次改錯了對象，看 migration 產出的表名才發現）。
已對齊全庫慣例 `varchar(500)`，兩支 smoke test 的 `AllowedUnboundedTextNames` 豁免隨之移除。
migration 刻意**不**加 `left(Remarks, 500)` 預先截斷：那是不可逆且安靜的資料損失，
寧可讓它撞 22001、App 起不來、逼出人工處置。

**③ 跨廠調撥的 XI（入庫）段測不到**

原因寫的是「TestAuthHandler 解出的廠別固定 P1」——那個說法錯了。
廠別不是驗證 handler 給的，是 `UserContext.PlantId` 讀 `ApplicationUser.ActivePlantId`，
**資料庫裡的一個欄位**。把 admin 的作用廠別暫時切到 P2 就走得完，不必動任何測試基建。
補兩支：收貨過帳（斷言 XI 的 `PlantId` 是目的廠——寫回來源廠就是料在兩廠之間憑空消失）、
以及已收貨的單不得再撤回發出。驗紅：把 XI 的 PlantId 改成沿用 XP 的 → 1 支紅。

**④ Phase 3 三支「下游足跡不動」沒有可觸發的失敗路徑**

查下去發現 P2P 那支的問題不是「沒驗紅」，是**沒東西可壞**：
刪／改採購單通過守門後第一件事是 `RestorePrOrderedQtyAsync`（回沖請購單已轉採購量），
但種子裡的採購單根本不是從請購單轉來的，那段回沖是 no-op。
看門狗守的是一個在本測試中不可能發生的事件，所以它永遠綠。
已補上請購單上游鏈與 `PrOrderedQty` 斷言。四支全部驗紅：
把各自的回沖／刪除／轉狀態搬到守門之前（＝「先回沖、後檢查」）→ 4 支如期紅。

**⑤ 數量欄位 scale 全庫不一致（103 欄 4 位／27 欄 6 位）**

原描述把它當成「要收斂需大批 migration」。實際盤過之後，那個 4 對 6 的分佈**是對的**：

| scale | 適用 | 例 |
|---|---|---|
| `decimal(18,4)` | 交易量與金額（使用者輸入或由其累加，寫入前經 `RoundQtyAsync`／`RoundAsync` 夾到 4 位） | 大多數單據欄位 |
| `decimal(18,6)` | 每單位比率與其推導值 | BOM 單位用量、單位工時、匯率、`WorkOrderMaterial.RequiredQty` |
| `decimal(8,4)` | 稅率 | `Tax.TaxRate` |

把 6 位收成 4 位反而會**實際改變算量**：BOM 單位用量 `0.000125` 會被四捨五入成 `0.0001`，
Backflush 寫回的 `IssuedQty` 同理。所以那批不動。

真正的缺陷藏在旁邊，是原描述完全沒提到的一類：**10 個欄位從未宣告 `[Column(TypeName)]`**，
EF 因此產出**無界的** `numeric`。無界不是更精確，是沒人決定過——
全庫 11 個 `ExchangeRate` 是 `decimal(18,6)`，只有出貨明細與銷退明細這兩個無界；
13 個 `TaxRate` 是 `decimal(8,4)`，只有 `Company.TaxRate` 無界。
同一個語意的數字在不同資料表存進不同精度，對帳差異落在小數末位，
看起來像四捨五入誤差而不像結構問題。已全部對齊並出 migration。
scale 的分類寫進 `DecimalPrecision`，並加 **R21** 守住（掃 EF snapshot 而非原始碼——
snapshot 只列真的 mapped 的欄位，ViewModel／報表 DTO 的 decimal 不會混進來）。
檢驗量測值 9 欄刻意維持無界（精度由量測儀器決定，不受 UOM 小數位數約束），列在規則白名單。
驗紅：把 `Company.TaxRate` 改回無界 → R21 紅。

**未做**：兩支 QCR smoke test 拿掉豁免後需實機跑 Playwright 驗證，
本輪沒有可用的執行環境（需 app 起在 :5110 ＋ 已套用 migration 的資料庫），留待下次跑 UI 套件時確認。
→ 已於 Phase 8 補跑，見下。

**踩到的坑**：跨廠收貨那支第一版斷言目的廠在手量 `== LineQty`，單獨跑綠、整套跑紅（實際 70）——
目的廠收進去的料沒有人清，庫存列跨測試輪次累加。改成比**增量**才與執行順序無關。
單獨跑綠不等於乾淨，這類斷言一律要先讀基準再比差。

**總數**：單元 **2592** 綠（+9）、整合 **223** 綠（+2）、慣例 **35/35**（+R21）。
整合套件連跑兩輪皆綠（確認新測試不留殘留）。

---

### Phase 8：把 Phase 7 唯一的待辦真的跑起來（已完成）

環境其實是齊的——Playwright 瀏覽器已裝、PG 在跑、`Program.cs` 預設 `Database:AutoMigrate`。
建置後以 `ASPNETCORE_URLS=http://localhost:5110` 起 app，兩個 migration 在真實 dev 庫（ProjectZ）
套用成功：`Companies.TaxRate → numeric(8,4)`、`GoodsShipmentLines.ExchangeRate → numeric(18,6)`、
`Products.MinLotSize → numeric(18,4)`。**兩支 QCR smoke test 26/26 綠**，待辦結清。

**收窄前先驗資料**：PG 對 `numeric` 收窄是無聲四捨五入，不會像 `varchar` 那樣報 22001。
套用前逐欄查「會被 round 掉的列數」，10 欄全部為 0，才讓 app 去跑 migration。
`Remarks` 那三欄的 text→varchar(500) 同理先查 >500 字的列，也是 0 筆。

**我在 Phase 7 對機制的判斷是錯的**：以為 `maxlength` 屬性得在 view 手寫
（因為隔壁 `DefectCode/_Form.cshtml` 確實寫了 `maxlength="500"`），差點多改兩支 view。
實際上 ASP.NET Core 的 `DefaultHtmlGenerator` 會從 `[MaxLength]`／`[StringLength]` 直接產生
`maxlength` 屬性——伺服端原始 HTML 就帶著它。Phase 7 只加模型註記是**充分**的。
順帶推翻一個假警報：全站 143 個表單欄位「view 裡沒有 maxlength」不等於缺守門，
它們的模型有註記。**驗證機制本身，不要從隔壁檔案的寫法反推。**

**驗紅**：拿掉 `InspectionSpec.Remarks` 的 `[MaxLength(500)]` → 重建、重啟 app →
該支紅且訊息精準指名 `Remarks`；還原後綠。

**這輪的真發現：`NewPanel_TextInputs_AllDeclareMaxLength` 沒有掃描器自檢。**
框架的 R0–R21 每條規則都帶 `probed > 0`，這條 UI 規則卻沒有——
面板沒開成、`PanelBodySel` 改名、選擇器失準，`missing` 都會是空集合，
斷言「因為什麼都沒檢查」而通過，整條規則安靜退化成永遠綠燈。已補上自檢。

自檢的條件試錯過一輪：第一版數「可見文字欄位」，`DepartmentActualCost` 立刻紅——
但那個畫面**真的**只有兩個下拉加兩個數值欄，一個文字欄位都沒有，是合法的零。
改成數「可見表單控件」才分得開「這畫面沒有文字欄位」與「面板根本沒開」。
22/22 綠，且這道自檢在調整過程中真的響過，不是死碼。

**踩到的坑**：用 Python 以 `encoding='utf-8-sig'` 回寫 `.cshtml` 做臨時反轉，
替兩個原本沒有 BOM 的檔案加了 BOM，還原時 `git diff` 才看出來——
臨時反轉一律用 `git checkout --` 還原，不要靠腳本反寫。
另外 `pkill -f "…SmartFactory.MES.dll"` 會連帶殺掉自己那層 shell（命令列本身含該字串），
改用 `pgrep | xargs kill`。

---

## 3. 風險總表

| 等級 | 風險 | 緩解 |
|---|---|---|
| 高 | 孤兒稽核掛入 `RunAllAsync` 後擋住關帳 | Phase 0 唯讀先行，零筆才掛；必要時加系統參數開關 |
| 高 | Phase 1 新增 xUnit collection → 測試庫 23505 競態 | 一律進既有 collection，維持 `DisableTestParallelization` |
| 中 | Phase 1 種子涵蓋 22 張單據，工作量可能低估 | 分批交付：INV → PUR/ORD → MFG |
| 中 | R16 編號撞號或未登錄索引表 → R0 擋 build | 動手前先讀〈規則索引〉表確認最大編號 |
| 低 | UI 測試對 seed 狀態敏感 | 沿用 `SeedFixture.ResetAsync()` |

## 4. 總量

約 9–11 個工作天。交付順序 `0 → 1 → 2 → 3 → 4 → 5`，
另加 Phase 4-3 遞延出來的 Phase 6（修改守衛改讀 DB）、
收掉五項刻意界線的 Phase 7，以及把 Phase 7 待辦實機跑完的 Phase 8。

**收尾狀態**（2026-08-03 更新）：單元 **2637** 綠、整合 **237** 綠、慣例 **35/35**、
UI **496** 綠（Playwright，實機 :5110，14m35s）；
Phase 0–8 全數完成，〈5. 殘留待辦〉四項亦已全部處理。

---

## 5. 殘留待辦（2026-08-03 收口）

Phase 0–8 每一段都有「這次不做」的取捨，理由都寫在當段內文裡。
問題是那些句子埋在各自的段落中，下次盤點會再漏一次——故在此攤平成一張表。

**2026-08-03 全數處理完畢**，逐項修法見下。收口過程中發現**原本 2026-07-31
那份「現況（實查）」有兩項是失準的**（第 3、4 項），先記在這裡：
下次再做這種盤點時，「現況」欄要連同守門所在的**呼叫端**一起查，
只看服務層或只看單一 controller action 會得到偏樂觀或偏悲觀的結論。

| # | 項目 | 出處 | 修法（2026-08-03） |
|---|---|---|---|
| 1 | **UOM 不一致的累計** | 〈批次 C 後續之二〉 | 已改以庫存基準單位累計。`SalesOrderLine.ShippedStockQty`／`PurchaseOrderLine.ReceivedStockQty` 兩個新欄位承接**精確**的基準量累計（直接加下游行的 `StockQty`，不經換算故無捨入漂移），超量守門改比這一組；原本的 `ShippedQty`／`ReceivedQty` 保留為顯示欄位，但改成**先換算回上游行自己的單位**再累加，`OrderQty − ShippedQty` 因此才是對的。migration `UpstreamAccumulationInStockUom` 一併回填既有資料（計入集合＝現存且 `Status <> Void` 的下游單據，與應用層「已累加未回沖」的集合完全一致）。**連帶修掉一個文件沒記到的前置缺陷**：`SalesOrderLine.StockQty` 原本是前端送什麼存什麼（不像出貨單會用品號主檔 `StockUom` 重算），把守門建在它上面等於建在可竄改的輸入上——已補 `SalesOrderController.ResolveLineStockQtyAsync` 伺服端權威覆寫 |
| 2 | **兩類無效輸入等價類零覆蓋** | 〈4-2 實作紀錄〉 | 當時的判斷「留給各畫面自補」方向就錯了：這兩類**不該進 Playwright**——畫面上這些欄位是 `<select>`，選項早被廠別與 `IsActive` 過濾，UI 根本選不到非法值，寫在 UI 測試裡只能證明下拉過濾正確。改以整合測試直接 POST 表單（`Validation/ForeignKeyEquivalenceTests`，8 個等價類 + **3 個正向對照**），判定看「DB 有沒有多出那一列」。結果 8/8 全紅——`ProductCategory`／`WorkCenter`／`Product` 三支 Save 完全沒有 FK 檢核，故一併補 `MasterReferenceGuard`（不存在／他廠／已停用三態）並接進三支 controller 的 Create＋Edit |
| 3 | **`FinPaymentService` 未接並發** | 〈Phase 5 ③〉 | ⚠️ **原描述失準**：「現仍無任何 catch」只查了服務層，但守門本來就寫在 controller——`Confirm` 兩條路徑**早就接住了**（`APPaymentController:174`／`ARReceiptController:148`）。真正裸奔的是 **`Void`**：`VoidAPPaymentAsync`／`VoidARReceiptAsync` 同樣改寫 `APInvoice`／`ARInvoice`／`BankAccount`（三者皆有 xmin），卻只 catch `InvalidOperationException`。缺口實際只有 2 個 catch 區塊，已補；並在 `CrossModuleConcurrencyTests` 加兩格證明 Void 真的會拋（catch 不是死碼） |
| 4 | **`InventoryCount` 重複結帳** | 〈Phase 1 批次 A/B〉 | ⚠️ **原描述失準**：「唯一防線是 `SheetStatus`」漏看清冊表頭已有 `xmin` 且 `Close` 末端已 catch `DbUpdateConcurrencyException`——**並發**雙結帳本來就擋得住。真正沒守的是**順序性重放**（狀態被改回 `Counting` 時，第二次結帳另採新單號，過帳層以單號為鍵的冪等失效）。已加 `InventoryCountSheet.AdjustmentDocId` + `(PlantId, AdjustmentDocId)` 部分唯一索引，`Close` 開頭改看這一欄（比狀態守門更早），冪等從「行為」變成「資料上的事實」 |

**套用前先跑**：`scripts/audit-mixed-uom-accumulation.sql`（唯讀）——列出真正混單位的行、
migration 會補值的行數，以及**補不了的行**（單位不同且 `StockQty = 0`，SQL 拿不到換算率）。
守門對這些行會自動退回「以上游行自身單位比對」而不是拿 0 當上限把所有出貨擋掉，
但那是降級模式，該修的還是資料。

**屬他案的 2 支紅——已實機覆核，數字作廢**：〈4-2〉記錄的 UI `Flows` 2 支紅
（`OrderToCashFlowTests`／`ProcureToPayFlowTests`）標為「他案未完成工作」。
2026-08-03 實機重跑（:5110，DB `ProjectZ_E2EUI`）：**`Flows` 5/5 綠、全 UI 套件 496/496 綠**。
E2E 流程測試地基一案確實把它們修好了，本文件當時沒回填，那 2 支紅是過期資訊。

**跑 UI 測試的兩個本機前置**（兩次都踩到，記下來免得下次再花時間）：

1. **授權指紋會過期**。環境指紋變動後（WSL 重建等）`license.json` 就對不上，
   app 直接在 `ResolveConnectionStringAndContext()` 拋 `SecurityException` 起不來。
   錯誤訊息會印出當下指紋，拿去重新核發、更新**專案根目錄那一份**即可
   （csproj 已改 `Always`，不必再同步 bin）。整合測試不受影響——
   它的 csproj AfterBuild 會把 Security DLL 從測試輸出刪掉，走 fallback 路徑。
2. **`ConnectionStrings__DefaultEncrypted=""` 環境變數蓋不掉 `appsettings.Development.json`**。
   實測設了環境變數，app 仍走加密路徑連到 `ProjectZ`（不是指定的種子庫），
   `pg_stat_activity` 一查就看得出來。必須直接把該檔的 `DefaultEncrypted` 清空
   （檔案在 `.gitignore` 內），跑完記得還原。

**有理由的取捨，不列為缺口**（避免下次又被當成漏做）：

- `MaterialSlip` 的 `IssuedQty` 超領不設上限——不良重投／耗損補發是正常製造場景，
  `IssuedQty > RequiredQty` 有業務意義，是真實的領域差異（見〈批次 C 後續之二〉）
- 匯率換算往返不等冪——已改為對帳一律以匯率快照重算，不走往返（見〈4-1 實作紀錄〉）

---

## 6. UI 測試涵蓋盤點（2026-09-09 新增工具）

補使用手冊截圖時順帶盤了一次「手冊有寫、UITests 一次都沒碰過」的畫面。

**這一節刻意不列清單，只指向工具**：

```bash
scripts/audit-ui-test-coverage.py            # 純文字
scripts/audit-ui-test-coverage.py --format md  # 貼進 PR／規格用
```

理由是這個 repo 已經因為手抄聚合吃過虧——同一組數字在 `manual-test-plan.md`
的計數表寫 39／7、§13.6 散文寫 34／12，兩邊都湊得出 47，而沒有任何東西會發現
它們對不起來。多寫一份手抄清單只是多一個會漂的地方。

**2026-09-09 當下的量**（會變，以工具輸出為準）：手冊涵蓋 128 個畫面，
UITests 出現過 61、未出現 67；未出現的裡面 **17 支有寫入動作**、50 支純唯讀。

### 兩個必須一起讀的極限

1. **「沒有 UI 測試」是下限，會誤報。** 判定方式是「路由字串有沒有出現在
   `SmartFactory.MES.UITests/**/*.cs`」，靠選單點擊導覽、不寫死路由的測試會被
   算成沒有。拿它當**待查清單**，不是當結論。反向不會漏：字串出現了就真的有檔案提到。

2. **寫入動作的正則改過一次。** 第一版寫成 `\[HttpPost\]|\[HttpPost\(`，漏掉本專案
   最常見的 `[HttpPost, ValidateAntiForgeryToken]`，於是 `MonthEnd`／`SystemParam`／
   `ReportSchedule` 全被誤判成唯讀畫面，清單少了 10 支。要動那條正則，請先拿這三支覆驗。

### 同批修掉的截圖工具缺陷

`~/.cache/manual-i18n/shooter` 的失敗判定原本只看 HTTP 狀態碼，而
**ASP.NET Core Identity 的拒絕存取是 302 導到 `/Identity/Account/AccessDenied`，
那一頁回 200**。實測後果：三支報表工具畫面（功能開關關閉 → 沒有選單 → 沒有
`MenuPermission` → default-deny）全部被判成 OK，產出三張**逐位元組相同**的
Access denied 圖，工具還回報「成功 6，失敗 0」。這比截到 500 黃頁更難發現——
那三張看起來就是一張正常的頁面。

已改成用**最終停在哪個網址**判斷。成對驗證：探針指向 `AccessDenied` → 判為
`DENY`、結束碼 1；拿掉探針跑全部 128 條路由 → 128 成功 0 失敗（同時證明既有的
122 張沒有任何一張是 Access denied）。

⚠️ 這支工具住在 `~/.cache/manual-i18n/shooter`，**不在版控內**。
