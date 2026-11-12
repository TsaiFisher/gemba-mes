# MFG 模組領域模型草稿（mfg-domain.md）

> 來源：逆向工程 /home/dev/ProjectZ-O/PZ/MES/ 下 PLN01/WO01/WW01/WW02/MSFB01/MSFC01 與輔助檢視 PLN11-14。
> 核心規格已由 Fisher 確認（見 §10），可作為實作依據。尚待確認的議題列於 §13。

---

## 1. 模組定位與範圍

**目標**：把訂單變成可執行的生產活動，並能即時掌握進度。

**範圍**（以業務節點切）：
1. **生產批展開（MRP）**：從訂單→生產批→工單→工序→用料 的需求展開
2. **產能排程**：把派工單分派到具體機台與時段（對應 WW02）
3. **派工管理**：派工單建立、優先序、合併、拆批
4. **生產執行與回報**：上工、報工、檢驗（含手機 API）
5. **即時 ETA 推算**：依生產進度，動態預估每張派工/機台的完成時間（公車到站邏輯）
6. **入庫與退工**：完工入庫、不良品退工或重工

**不在範圍**：
- 庫存帳本本身（已在 INV 模組）
- 採購、銷貨單據（已在 PUR/ORD）
- 但 MFG 會**呼叫**這些模組（如：需求觸發採購、完工觸發入庫）

---

## 2. 命名對照表（舊 → 新）

舊系統用代碼縮寫（如 PLN/WO/WW），新系統採用語意化英文，建議如下：

| 舊（中文）| 舊（代碼）| 新（建議命名）| 說明 |
|----------|----------|--------------|------|
| 產品 | PROD | Product *(已存在)* | BAS 模組 |
| 生產途程主檔 | BOR_U | `Routing` | 製造路徑主檔（含批量、緩衝天數、平均工時）|
| 工序明細 | BOR | `RoutingOperation` | 一張途程下的多道工序 |
| 物料清單 | BOM | `RoutingBom` 或 `Bom` | 工序的用料展開 |
| 工作中心 | WC | `WorkCenter` | 一群同性質機台 |
| 機台/設備 | APPA | `Machine` | 具體可分派的設備 |
| 模具/夾治具 | MOLD | `Tooling` *(BAS，擴充)* | 引用現有 BAS.Tooling；加 `ToolingType`（Mold/Fixture）與 `CavityCount` |
| 工作日曆 | WKC | `WorkCalendar` | 日期×設備 的可用工時 |
| 生產批展開單（MRP 執行）| PLN_U | `MrpRun` | 一次 MRP 執行的紀錄 |
| 生產批明細 | PLN | `MrpRunLine` | MRP 對每筆訂單列的展開結果 |
| 生產批號 | ID_PLN | `ProductionBatch` | 獨立 Entity：批次狀態、優先序、預計完工日（見 §13.3）|
| 工單 | WO | `WorkOrder` | 一個自製/委外單 |
| 工單工序 | WO_R | `WorkOrderOperation` | 工單下的工序快照 |
| 工單用料 | WO_M | `WorkOrderMaterial` | 工單下的物料需求 |
| 派工單 | WW | `Dispatch` | 一張可指派到機台的工作單 |
| 派工用料 | WW_M | `DispatchMaterial` | 派工下的物料需求 |
| 上工/開工紀錄 | MSFB | `ProductionSession` | 誰、機台、模具、起訖時間 |
| 報工明細 | MSFC | `ProductionReport` | 完成數量、合格/不良、容器 |
| 報工容器 | msmc | `ProductionReportContainer` | 容器→實際入庫量 |
| **開工準備記錄**（新增）| - | `ProductionSetup` | Setup 起訖時間、實際/標準工時比對 |
| 機台異動單（主）| GTR | `MachineStatusOrder` | 一張異動申請（可含多台機台）|
| 機台異動明細 | GTR_D | `MachineStatusOrderLine` | 一台機台的狀態切換紀錄 |
| 庫存上鎖（指定庫存）| IQ | `InventoryReservation` | 把現有庫存綁定到某個需求 |

> 中文 UI 顯示時仍用原中文（生產批、工單、派工單），只是 C# Entity 與 DB 表用英文。

---

## 3. 核心 Entity 與關聯

```
  SalesOrder（ORD）          Product（BAS）       Tooling（BAS，擴充）
       │                         │                      │
       │ 觸發 MRP                 │ 有 Routing           │ 用於 Setup
       ▼                         ▼                      │
  ┌─────────┐              ┌──────────┐                 │
  │ MrpRun  │─1:N─►MrpRunLine│         │ Routing  │     │
  └─────────┘    │          └────┬─────┘                │
                 │ 產生           │ 1:N                   │
                 ▼               ▼                      │
        ┌─────────────────┐ RoutingOperation             │
        │ ProductionBatch  │    ↓ 1:N                    │
        │ (Priority/Status)│ RoutingBom                  │
        └────────┬────────┘                             │
                 │ 1:N                                  │
                 ▼                                      │
        ┌──────────────────────┐                        │
        │ WorkOrder（上下階）   │─1:N─►WorkOrderOperation─1:N─►WorkOrderMaterial
        └──────────────────────┘           │
                                           │ 1:1
                                           ▼
                                     ┌──────────────┐
                                     │ Dispatch     │─1:N─►DispatchMaterial
                                     │（順序鏈結）   │
                                     └──────┬───────┘
                                            │ 0..1
                                            ▼
                                  ┌──────────────────┐
                                  │ ProductionSetup  │ ← Tooling（可選）
                                  └──────────────────┘
                                            │ Setup 完成後
                                            ▼
                                     Machine（屬 WC）
                                            │ 上工
                                            ▼
                                  ┌──────────────────┐
                                  │ ProductionSession │─1:N─►ProductionReport
                                  └──────────────────┘           ↓ 1:N
                                                         ProductionReportContainer
```

**Entity 欄位補充**：

RoutingBom（工序用料）：
- `UomCode` (FK → Uom)：發料計量單位（必填）
  > 說明：與 Product 的庫存單位（StockUom）可能不同。例如購買以「盒」計，發料以「個」計。
  > 計算發料量時：`需求數 = 工單量 × BOM用料量 / 批量 × (1 + 耗損率)`，單位轉換基於此 UomCode。
- 修改 RoutingBom Entity：
  ```csharp
  public class RoutingBom
  {
      public string RoutingId { get; set; }
      public int OperationSeq { get; set; }
      public int BomSeq { get; set; }
      public string ProductCode { get; set; }          // 物料代碼
      public decimal Quantity { get; set; }             // 用量
      public string UomCode { get; set; }               // 發料單位
      public decimal ScrapRate { get; set; } = 0M;      // 耗損率；發料量 = 需求 × (1 + ScrapRate)
      public MaterialIssueMethod? IssueMethod { get; set; }            // null = 走三層 Fallback
      public BackflushTiming BackflushTiming { get; set; }             // 後沖時機（僅 Backflush 模式生效）
          = BackflushTiming.AtCompletion;
      // ... 其他欄位
  }

  /// <summary>發料方式（三層 Fallback：RoutingBom → Product → PlantSetting）</summary>
  public enum MaterialIssueMethod
  {
      Push,       // 發料：投產後倉庫整批推出（建發料單）
      Pull,       // 領料：生產單位依需提出申請（建領料單）
      Backflush,  // 後沖：完工時系統自動倒算扣除
  }

  /// <summary>後沖時機（IssueMethod = Backflush 時生效）</summary>
  public enum BackflushTiming
  {
      AtCompletion,   // WO 全部完工時一次沖（預設，適合短週期/低價物料）
      PerReport,      // 每次報工時按完工比例分次沖（適合長週期/高價物料）
  }
  ```

  > **三層 Fallback 優先序**：`RoutingBom.IssueMethod`（工序料件層，明確覆寫）→ `Product.DefaultIssueMethod`（品號預設）→ `PlantSetting.DefaultIssueMethod`（廠別預設，必填，不可空）。`WorkOrderMaterial` 建立時從此 Fallback 鏈快照 `IssueMethod`，後續邏輯讀 WorkOrderMaterial 不再回查主檔。

**關鍵關聯說明**：
- **ProductionBatch**：一個生產批對應一筆 MrpRunLine；一個生產批下有多張 WorkOrder（成品 + 各階半成品）
- **RoutingBom.UomCode**：明確指定發料單位，與庫存單位換算確保準確扣帳
- **WorkOrder.ParentWorkOrderId**：自製階層（成品工單 → 半成品工單）
- **WorkOrder.ReworkFromWorkOrderId**：重工工單連結原始工單
- **Dispatch.PrevDispatchId**：派工順序鏈結（每張派工只有一張前置派工）
- **Dispatch.SplitGroupId**：拆分派工的群組關聯（子派工數量加總 = 原派工數量）
- **Dispatch → ProductionSetup**：Setup 完成後才可開工（TM_SETUP > 0 時）
- **ProductionSession.DispatchId**：一張派工可有多次上工（換班、中斷）
- **ProductionReport.SessionId**：一次上工可分多次報工（容器、批次）

### 3.1 途程設計定案（2026-05-26）

> 與 Fisher 討論後定案的途程主檔／工序／用料設計，逐項標註**目標行為**。
> **實作狀態（2026-07-09 複查）：途程主檔／工序／用料（`Routing`/`RoutingOperation`）已落地**（含投產前「先刪後加」覆寫、成本鏈見 §14 與 roadmap-fin-cst-sub.md）；本節保留作設計依據，個別欄位以程式為準。

**(1) 途程與品號關係 — 預設途程 + 一品多途程（手工開工單用）**
- `Routing.RoutingId` 維持獨立 PK；`Routing.ProductCode` **改為非必填（可空）**：有值＝該途程歸屬某品號（供候選篩選／驗證），空＝通用途程（任意品號可用）。
- `Product.RoutingId` = 該品號的**預設途程代號**（UI 標籤改為「預設途程代號」，以 select2 從途程代碼選），供 MRP/CRP 自動展開（`MrpEngine` 以 `product.RoutingId` 取唯一 active 途程）。
- 「一品多途程」用於**手工開立工單／重展**：可為同一 `ProductCode` 建多條途程，工單可改用預設以外的途程後重展；候選途程依 `Routing.ProductCode == 工單品號`（或通用途程）篩選。
- 驗證（放寬）：`Product.RoutingId` 指向的途程須存在；僅當該途程**有設 `ProductCode`** 時才要求與品號相符（通用途程＝空 `ProductCode` 放行）。
- 跨品號共用以「通用途程（空 `ProductCode`）」涵蓋；若日後需常態化多品共用，再導入 Product↔Routing 多對多適用表。

**(2) 計量單位 — 保留多單位，工序單位 = 移轉批量單位**
- 不把單位鎖成庫存單位；維持可編輯（保留「發料單位 ≠ 庫存單位」的換算能力，見上方 RoutingBom 說明）。
- `RoutingOperation.UomCode` 的語意是**移轉批量 `QT_BOR` 的單位**（非工時單位）。新增工序時**預設帶「途程品號的 `StockUom`」**，仍可改。
- `RoutingBom.UomCode` 為發料單位；用料品號（select2）選定後**預設帶該物料品號的 `StockUom`**，仍可改。
- ⚠️ 畫面修正：現行 `_Form.cshtml` 把 `UomCode` 顯示在「單位時間 TM_UNIT」欄旁，應移到**移轉批量 `QT_BOR`** 欄旁；單位時間欄改顯示時間單位（見 (3)）。

**(3) 工時定義與單位 — 以獨立欄位記錄時間單位**
- 定義釐清：
  - `TM_SETUP` = **設置/整備時間**：開始生產前換模、換線等準備時間（每張派工一次，與數量無關）。
  - `TM_UNIT` = **單位標準工時**：生產一個（一單位）所需標準時間。
- 時間單位以**獨立欄位 `RoutingOperation.TimeUnit`**（列舉 `OperationTimeUnit`：秒=1／分=2／時=3，預設分）持久化記錄，**不正規化**：`TM_SETUP`／`TM_UNIT` 以使用者輸入原值儲存，單位由 `TimeUnit` 解讀。一個工序的 setup 與 unit time 共用同一個 `TimeUnit`。
- 畫面：工序表單以下拉選 `TimeUnit`，存檔即存該單位；載入時以原單位顯示（不再做存檔換算）。
- 排程／前置期：`EstimateLeadDays` 需依各工序的 `TimeUnit` 將 `TM_SETUP + TM_UNIT×qty` 換算為共同基準（小時）後彙總。`WorkOrderOperation` 複製工序工時時**一併帶入 `TimeUnit`**（已實作；其 `SetupTime`/`UnitTime` 為原值、單位由 `TimeUnit` 解讀），Phase C 排程消費 WO 工時時據此換算。

**(4) 用料／品號輸入 — select2**
- 用料清單品號 (`RoutingBom.ProductCode`) 與途程主檔品號 (`Routing.ProductCode`) 改用 **select2**（品號清單大，建議遠端搜尋）；選定後依 (2) 自動帶 `StockUom`。

**(5) 工序容器 — 參照容器維護（取代 Yes/No）**
- `RoutingOperation` 的 `UseContainerIn`／`UseContainerOut`（布林）改為 `ContainerInCode`／`ContainerOutCode`（可空 FK → 容器維護 `Container`，空＝該工序不以容器轉移）。
- 緣由：CD_BOR 定量轉移以容器為單位，需容器容量；報工 `ProductionReportContainer` 需比對工序預期容器。畫面以 select 從容器維護選。

**(6) 用料防遞迴 — 兩層**
- 存檔把關：途程存檔時拒絕「用料品號＝該途程自身 `ProductCode`」的明細（僅當 `ProductCode` 有值時適用）。
- MRP 展開把關：以「根→當前」的品號祖先鏈偵測直接／間接循環，命中即丟明確「循環用料」錯誤；`MaxBomDepth` 保留為最後防線。

**(7) 工序產出良率 `YieldRate`（RoutingOperation）**
- 每道工序投入→良品的比率（%，預設 100），與 `RoutingBom.ScrapRate`（用料損耗）語意不同，兩者並存。
- 消費（MRP）✅ **已實作**（`MrpEngine.ComputeCumulativeYield`，見 §5.1）：投產量以累積良率回推——良品需求 N → 起始投產 = N ÷ Π(各工序良率)，各工序處理量 = N ÷ Π(該工序起算至末工序之良率)，材料量與產能負載按各工序處理量計；報工以工序良率為良/不良基準。**預設 100% 時行為與現況一致**（既有測試不受影響）。

**(8) 工序治具/移轉時間 與 途程版本**
- `RoutingOperation.ToolingCode`（可空 FK → `Tooling`）：Setup 規劃用，模穴數（`Tooling.CavityCount`）可帶定量產出率。
- `RoutingOperation.MoveTime`／`QueueTime`：工序間搬運/等待時間，單位沿用該工序 `TimeUnit`；較 `Routing.BufferDays`（整路由粗緩衝）精細，供前置期/排程細算。
- `Routing.Version` + `EffectiveFrom/EffectiveTo`：工程變更控管與「一品多途程」版本追溯；查詢有效途程時可加生效日篩選（引擎細化時套用）。
- 上述為**主檔欄位先到位**；其中**累積良率回推已實作**（§5.1）；移轉時間納入前置期、生效日篩選仍待 Phase C 排程/CRP 細化時實作。

**(9) 首件檢查閘口旗標（Phase G 加入）**
- `RoutingOperation.RequiresFpi`（bool，預設 `false`）：該工序 Setup 完成後是否強制進行首件檢查（FPI）再開工。
- `RoutingOperation.RequiresInspection`（bool，預設 `false`）：Dispatch 開工後是否自動建立 IPQC InspectionOrder（製程中巡檢）。
- 兩個旗標語意不同，可同時為 true（首件 + 製程巡檢皆需要）。
- Phase G 時一併加入途程工序維護畫面，可在建立或修改工序時勾選；欄位預留後 Phase H 才接入狀態機邏輯。

---

## 4. 狀態機

### 4.1 MrpRun（MRP 執行單）
```
Draft（建立中，可加入訂單列）
   │ Run（執行展開）
   ▼
Executed（已展開，產生 ProductionBatch 與 WorkOrder）
```
- 一次 MRP 執行是「批次」概念：選定一批待展開的訂單列 → 一次跑完
- 舊系統允許重跑（Re-run），但已 InProgress/Completed 的工單不可覆蓋（見 §10 規格 5）

> **MRP 邏輯確認**：採**無限產能、由需求日期倒推**模式，輸出日期為「最晚須完成日期」而非排程精確完工日。
> 倒推**使用工作日曆**（`WorkingTimeWalker` 走可用時段），但**不看任何機台負荷**（有限產能在 Phase C `SchedulingEngine`）。
> 庫存在 MRP 展開時依需求時間點建立 `InventoryReservation` 實體鎖定。

> **逐工序倒推與庫出日期（精度，2026-06-19 落地）**：倒推**逐道工序**進行，而非僅算工單頭兩個日期。
> 自工單需求日往回（後製程先），逐工序以 `OpMinutes`（整備＋單件×投產量＋MoveTime＋QueueTime）
> 透過工作日曆倒推出**工序級**計畫開工/完工日，寫入 `Dispatch.PlannedStartDate/PlannedEndDate`
> （與 `ScheduledStart/End`＝Phase C 有限產能精確時刻區別）。
> - **各料庫出日 = 消耗它那道工序的 `Dispatch.PlannedStartDate`**。
> - **終產品庫出固定**：頂層工單需求日 = 訂單交期（倒推錨點，不浮動）。
> - **中間庫出浮動**：下階自製/委外子工單的交期 = **消耗它的那道工序**之計畫開工日（非整張父工單投產日）；
>   被後段工序用到的半成品交期較晚，被前段用到的較早——故中間庫出可較工單頭**提早或延後**。
> - 倒推落到今日之前 → 改自今日**逐工序正推**估算實際完工日，超過需求日標 `IsPredictedLate`。
> - **退回**：未注入日曆服務／廠別需求日當週無日曆設定／日曆涵蓋不足 → 退回 24h/天日數版（此時各工序共用工單級日期，行為同舊版）。
> **提早投產處理**：當 Dispatch 轉為 InProgress 時，系統自動對同物料的後續需求重算可用庫存並重建
> `InventoryReservation`，確保提早投產不造成其他需求的庫存預留時間點錯位。

### 4.2 WorkOrder（工單）
```
Planned（已規劃，待釋出）
   │ Release（釋出，可開始排程派工）
   ▼
Released（已釋出，工序可排程）
   │ 任何 Dispatch → InProgress（開工）
   ▼
InProgress（生產中，至少有一張派工已開工）
   │ 所有派工完工
   ▼
Completed（完工，待入庫）
   │ 入庫完成
   ▼
Closed
   │
   └── Void（作廢，任何階段都可，但要檢查派工有無回報**與有無已領未退的料**）
```

> ⚠️ **P3（文件清理）**：WorkOrder Void 時，若已建立 `InventoryReservation`，必須呼叫 `ReleaseBySourceAsync(sourceType, workOrderId)` 釋放預留量，否則可用庫存將被永久佔用。`ReleaseBySourceAsync` 已實作於 `INV.InventoryReservationService`；MRP 重跑流程已呼叫（§10），工單作廢路徑需補上相同呼叫（詳見 §10 MRP 重跑備忘）。

> **作廢的兩道守門（2026-07-29 補第二道）**：
> 1. 派工已回報產出（`Dispatches.QtyDone > 0`）
> 2. **有已領未退的料（`WorkOrderMaterial.IssuedQty − ReturnedQty > 0`）**
>
> 第二道原本沒有，是 Phase 3「中途反悔」測試打出來的缺口。
> 「還沒開始生產」與「料還在倉裡」不是同一件事——領料單過帳的當下料就已經實際離開倉庫、
> 庫存扣掉、`IssuedQty` 回寫，而那全都發生在任何人回報產出之前。只看 `QtyDone`
> 會讓一張料已經領光的工單照樣作廢掉，作廢後沒有任何東西會來承接那批料：
> 工單不會結案、成本結算不會跑。生產領料不入總帳（形態轉換），所以試算表照樣平衡——
> 帳面完全安靜，只有倉庫少了東西而查無去向。正確順序是先開退料單退回，淨已領量歸零後才能作廢。

**InProgress 觸發條件**：
- 工單下任何一張 Dispatch 轉為 InProgress 時，WorkOrder 自動同步 → InProgress
- 用途：生管人員在工單列表可直覺區分「待開工」（Released）與「生產中」（InProgress）
- 逆向轉換：當所有 Dispatch 都完工或作廢時，自動 → Completed

### 4.3 Dispatch（派工單）
```
Pending（未排程，沒有指定時段）
   │ Schedule（排程引擎指派到機台+時段）
   ▼
Scheduled（已排定，等待開工）
   │ SetupStart（開始備料/換模等準備作業）
   ▼
SetupInProgress（準備中，建立 ProductionSetup）
   │ SetupDone（準備完成）
   ▼
ReadyToStart（準備完成，等待開工）
   │ （若 RoutingOperation.RequiresFpi = true）FpiRequired
   ▼
PendingFPI（等待首件檢查，「開工」按鈕強制置灰）
   │ 品管手機建立 FPI 型 InspectionOrder 並送出 Pass
   ▼
ReadyToStart → Start（首次上工，建立 ProductionSession）
   │ （若 RequiresFpi = false 或 FPI 已 Pass）
   ▼
InProgress（生產中）
   │ 累計 QT_OK >= QT_WW（派工數量）
   ▼
Closed（完工 = FG_CLOSE='Y'）
```
> Setup 步驟**可選**：若工序無 Setup 工時（`TM_SETUP = 0`），Dispatch 直接從 Scheduled → InProgress，跳過 Setup 狀態。
> FPI 閘口**可選**：僅當 `RoutingOperation.RequiresFpi = true` 時，`SetupDone` 後轉入 `PendingFPI` 而非 `ReadyToStart`；FPI Pass 後才釋出為 `ReadyToStart`，允許開工。

副狀態：
- `OnHold`：暫停（2026-10-06 改版）。一律經 `DispatchHold.Hold` 進入，記下 `HoldReason`（MachineDown／FpiFailed／IpqcFailed）、
  `HoldFromStatus`、`HoldAt`。來源：機台停用（SetupInProgress／ReadyToStart／PendingFPI／InProgress）、FPI 不合格、IPQC 不合格（僅 InProgress）。
  **不參與重排**（凍結，佔原機台）。出口三條（`IDispatchLifecycleService`）：
  - 恢復：回 `HoldFromStatus`；FpiFailed 回 `PendingFPI`；舊資料無紀錄者有產出→InProgress、否則 ReadyToStart 再過首件閘口。
    MachineDown 於機台轉回 Available/Standby 時自動恢復；機台仍停用不可手動恢復；品質原因須 `InspectionOrder` 更新權限。
  - 換機續做：無產出整張改掛；有產出原派工 `Quantity=QtyDone` 結案、剩餘量開 `-sN` 續做派工（同 SplitGroup）。續做為 Pending＋`IsMachineLocked`。
  - 剩餘量結案：有產出→Closed；無產出→`Cancelled`。
- `Cancelled`（8）：工單作廢（含 MRP 整批作廢）或暫停後無產出結案。不佔產能、清時段，派工清單預設不列。
- 工單自動完工：派工全部為 Closed／Cancelled 且至少一張 Closed（`DispatchHold.IsWorkOrderDone`）。**暫停不算完成**（舊規則算，會帶著剩餘量靜默完工）。
- 排程結果可隨時重算 → Scheduled 可退回 Pending（僅在 Pending/Scheduled 階段）

### 4.4 ProductionSetup（開工準備記錄）
```
Open（Setup 開始，SetupStartAt 有值，SetupEndAt 為空）
   │ Done（準備完成）
   ▼
Closed（SetupEndAt 有值，實際 Setup 時間確定）
```

### 4.5 ProductionSession（上工紀錄）
```
Open（已上工，DT_DATA_B 有值，DT_DATA 為空）
   │ Stop
   ▼
Closed（已下工，DT_DATA 有值）
```

### 4.6 ProductionReport（報工）
```
Reported（已報工 → 累計到 Dispatch.QtyDone）
   │ 檢驗
   ▼
Inspected（已檢驗，QT_OK / QT_NG 確定）
```

#### 4.6.1 超量報工與委外超收（2026-10-12）

原本：報工累計合格數超過派工量一律硬擋（桌機／手機 `OVER_REPORT`），多做的實物在系統裡沒有路可走；
委外收貨超收不擋、只在手機跳一次確認，沒有上限、照付加工費。改為廠別參數（`PlantParamCatalog`，可各廠覆寫）：

| 參數 | 值 | 未設定時 |
|---|---|---|
| `mfg_over_report_tolerance_pct` | 廠內超量容許率（%，非負） | 0 |
| `mfg_over_report_action` | 超過容許上限：`Block` 拒收／`Supervisor` 需主管 | Block（＝原本硬擋） |
| `mfg_outsource_over_receipt_tolerance_pct` | 委外超收容許率（%） | 0 |
| `mfg_outsource_over_receipt_action` | 超過容許上限：`Supervisor`／`Block` | Supervisor（最接近原本「只確認」） |
| `mfg_outsource_over_receipt_fee` | 超過委外採購量的部分：`Pay` 照付／`NoPay` 不付 | Pay |

- 判定單一實作 `IOverQuantityPolicy`：累計 ≤ 計畫量照收；≤ 計畫量 ×（1＋容許率）照收；再超過依 action。
- **主管**＝角色在「手機模組權限」被授予「超量核准」：投產報工 `OverReportApprove`、倉儲作業 `OverReceiveApprove`。
  桌機報工（`DispatchController`）與委外收貨確認也看同一格。**不吃新作業鍵自動回補**（否則人人變主管），由管理者明確授予。
- 廠內：桌機報工／完工、手機報工／完工並報工同一判定。派工 `QtyDone ≥ Quantity` 即結案，所以超量要在最後一次報工一起報；
  完工量（最後工序 QtyDone）、完工入庫、後沖用料（`CalcBackflushQty` 依完工量比例）自然跟著多。
  ⚠️ 領料額度（`RequestableQty`）仍以需求量為上限，未依容許率放寬——要放寬另案。
- 委外：在 `OutsourceReceiptService.ConfirmCoreAsync`（桌機確認、手機收料、簽核後確認的共同入口）判定，已確認／過帳收貨＋本張 vs 訂購量；
  不過即整張不確認。`NoPay` 時把本行加工單價改成「可計費量 × 合約單價 ÷ 實收量」，應付發票（實收量 × 單價）、成品入庫價值、上架一致；
  合約單價與計費量寫進明細備註。

---

## 5. 核心業務流程

### 5.1 從訂單到生產批（MRP 展開）

**輸入**：一批訂單列（NO_SO, ID_PROD, QT_SO, DT_DLV）
**輸出**：每個產品對應的 ProductionBatch（庫存可滿足/採購/委外/自製）

```
For each 訂單列 (sorted by 優先序 CN_P, 交期 DT_DLV):
  1. Fstock(ID_PROD, DT_DLV) → 取得可用庫存 QT_Q（含已預留扣除）
     若 QT_Q > 0：建立 InventoryReservation 鎖定庫存
  2. 剩餘需求 QT_R = QT_SO - QT_Q
  3. 依產品的 CD_MRP 決定處理：
     - P（採購）：QT_PO = QT_R，產生 PurchaseRequest（觸發 PUR 模組）
     - S（委外）：QT_OO = QT_R，呼叫 RunJ
     - M（自製）：QT_WO = QT_R，呼叫 RunMPS
  4. 寫回 MrpRunLine: QT_Q, QT_PO, QT_OO, QT_WO, ProductionBatchId
```

**RunMPS（自製展開遞迴）**：
```
Input: ID_PROD, QT_WO, ID_PLN, ID_BOR, DT_DLV
1. 讀 Product + Routing
2. **逐工序倒推**（無限產能、用工作日曆；見上方「逐工序倒推與庫出日期」）：
     自 DT_DLV 往回，後製程先排，逐道算出 opStart[i]/opEnd[i]
     （opEnd[末]=DT_DLV；opEnd[i]=opStart[i+1]）。
     DT_START（工單投產日）= opStart[首] − DY_BUFFER。

     ⚠️ **倒推防呆**：
     if (DT_START < TODAY):  // 過去日期風險
         // 切換正向排程，自今日起逐工序往後堆
         DT_START = TODAY；逐工序正推得 opStart[i]/opEnd[i]、DT_END = opEnd[末]
         if (DT_END > DT_DLV):
             標記 ProductionBatch.IsPredictedLate（預計延遲）
             警示日誌：此批可能無法準時交期

3. 建立 WorkOrder（NO_WO = ID_PLN + "_" + 流水號）
4. 讀 RoutingOperation（OperationSeq 升冪儲存；日期由 step 2 逐工序倒推取得）
   For each operation i:
     a. 建立 WorkOrderOperation（複製工序資料）
     b. 建立 Dispatch（QT_WW = QT_WO；NO_WW_U = 上一張派工；
        PlannedStartDate = opStart[i]、PlannedEndDate = opEnd[i]）
     c. 讀 RoutingBom（該工序的物料）
        For each material:
          - 計算需求量 QT = QT_WO × QT_M / QT_LOT × (1 + RI_L / 100)
          - 該料庫出日 = opStart[i]（消耗工序之計畫開工日）
          - 試扣庫存 Fstock；剩餘依物料的 CD_MRP 遞迴：
            * P → 觸發採購
            * S → 遞迴 RunJ（子工單交期 = opStart[i]）
            * M → 遞迴 RunMPS（產生下階 WorkOrder，ParentWorkOrderId = 本 WO，**交期 = opStart[i]**）
          - 建立 DispatchMaterial / WorkOrderMaterial
```

**遞迴邊界**：當某物料的 CD_MRP='P' 或庫存足夠時停止。

> ✅ **已實作**：RunMPS 已完整實作 `RoutingOperation.YieldRate` 累積良率回推（`MrpEngine.ComputeCumulativeYield`）。WorkOrder.Quantity = 投產量（需求量 ÷ 全線累積良率）；各工序 Dispatch.Quantity = 需求量 ÷ 該工序起算累積良率；BOM 用料以工序派工量計算。良率全 100% 時行為與舊版完全一致。

> ⚠️ **P3**：現無生產批批量上限（`ProductionBatch.MaxQty`），大量訂單可能產生超大批。建議在 MrpRunController import 邏輯加入批量拆分規則，待業務確認門檻值。

### 5.2 派工建立後的排程

詳見 [§7 排程演算法]。

### 5.3 上工與報工（手機 API）

> 手機 PWA 的完整架構規劃見 [mobile-pwa.md](./mobile-pwa.md)。本節僅描述業務流程。

```
作業員手機操作：

【Setup（若 TM_SETUP > 0）】
  0a. 掃描派工單 QR Code → 查到 Dispatch（Scheduled）
  0b. 開始備料/換模 → POST 建立 ProductionSetup
      副作用：Dispatch.Status = SetupInProgress
  0c. Setup 完成 → PATCH ProductionSetup（設 SetupEndAt）
      副作用：Dispatch.Status = ReadyToStart

【開工】
  1. 確認機台、模具（從 Dispatch.MachineId 預設）→ POST 建立 ProductionSession
     副作用：Dispatch.Status = InProgress（從 Scheduled 或 ReadyToStart）

【報工】
  2. 報合格/不良數量 → POST 建立 ProductionReport
     副作用：
       a. Dispatch.QtyDone += QT_OK
       b. 若 Dispatch.QtyDone >= Dispatch.QtyTotal：Dispatch.Status = Closed
       c. 觸發 ETA 重算（後續所有後置派工）

【下工】
  3. 結束上工 → PATCH ProductionSession（設 DT_DATA）
```

### 5.4 完工入庫

當 WorkOrder 的所有 Dispatch 都 Closed：
- WorkOrder.Status = Completed
- 觸發 INV 模組：建立 InventoryMovement（直接入庫到指定倉庫）
  > 入庫數量 = `Σ Dispatch.QtyDone`（`QtyDone` 只累計 `QtyOk`，廢品已在報工時排除，無需再扣）；詳見 §17.2.3 與 `MobileMfgController.ReportCreate`。
  > FQC 閘口已實作（Phase G-H）：`InspectionSpec.RequiresFQC = true` 時，`ProductionCompletionController` 會驗證是否存在已通過的 FQC InspectionOrder，否則拒絕入庫。
- 若 ParentWorkOrderId 有值，則此批料變成上階 WorkOrder 的 input
  > ⚠️ **P3**：子工單完工如何觸發上階工單計算可用量，待 Phase C 確認觸發機制（事件 vs 輪詢）。

---

## 6. CD_MRP 與 CD_BOR 編碼

### CD_MRP（產銷模式）
| 代碼 | 舊系統 | 新系統建議 |
|------|--------|-----------|
| Q | 庫存出貨 | `StockOnly` |
| P | 採購 | `Purchase` |
| S | 委外 | `Outsource` |
| M | 自製 | `Make` |

### CD_BOR（生產批量模式，每道工序的派工模式）
| 代碼 | 含義 | ETA 計算行為 |
|------|------|-------------|
| 1 | 連續 | 前序未完，後序可同步開始（pipeline）|
| 2 | 定量 | 前序累積到 QT_BOR 後，後序才開始 |
| 3 | 整批 | 前序全部完工，後序才開始 |

**ETA 影響**：CD_BOR 決定派工之間的依賴關係，是排程引擎的核心參數。

---

## 7. 排程演算法（WW02 邏輯抽煉）

舊系統 WW02 約 700 行，可拆解成 4 個獨立子問題：

### 7.1 資料準備
- 取**生產中**派工（已有 MSFB 但 QT_OK 未滿足）
- 取**未排程**派工
- 取機台清單：**僅取 `Machine.CurrentStatus = Available` 的機台**；非可用狀態（BreakDown、Maintenance 等）排程時跳過
- 取計劃停機（`PlannedDowntime`）時段：排程時排除此期間的機台工時
- 取機台日曆 WKC（過濾今日及之後、有工時的）
  > **批次預載（避免 N+1）**：排程啟動時以 `LoadCalendarContextAsync(plantId, dateFrom, dateTo)` **一次撈回**指定區間（建議上限 60 天）該廠三層 `WorkCalendar` + `WorkCalendarSegment`，於應用層轉成 `Dictionary<(machineCode, workCenterId, date), segments>` 唯讀查表。排程迴圈中**不得**逐格呼叫 `GetWorkHoursAsync`（現行三次循序 `FirstOrDefaultAsync`，在數百機台×30 天的迴圈會引發數萬次查詢）。
  > 查表以「日期為鍵」即足、**不需 interval tree**（區間樹是給「任意時間點落在哪個區間」的查詢，此處用不到）。`GetWorkHoursAsync` 維持供單格 CRUD／預覽使用。
- 把今天「已過去的時間」從首日扣除

### 7.2 派工順序決定
依以下優先順位排序：
1. 生產批優先序（依所選排序規則，見下表）— 整批為單位
2. 工單內部依工序順序（`OperationSeq` 升冪，前製程先）— 交期由 §5.1 逐工序倒推取得
3. 然後再依「前一張派工」鏈結

**生產批排序規則（可依工作中心或全廠參數設定）**：

| 規則代碼 | 說明 |
|---------|------|
| `Priority`（預設）| 依生產批 CN_P 值，數字小者優先 |
| `EDD`（Earliest Due Date）| 最早交期優先 |
| `SPT`（Shortest Processing Time）| 最短工時優先（有助於減少換模等待）|
| `FIFO` | 先進先出（依 MrpRun 建立時間）|
| `LIFO` | 後進先出 |

同規則下相同優先序的派工，再依工單內部工序順序排列（`OperationSeq` 升冪 → PrevDispatch 鏈）。

### 7.3 單張派工的時段配置
這是 WW02 最複雜的部分，邏輯如下：

```
Input: Dispatch + 前一張派工的 timeS/timeE/wh
Output: 本派工的 timeS, timeE, MachineId

1. 委外（無機台）：
   - timeS = max(前序 timeE, 今天)
   - 依工作日曆找連續可上班的日子
   - 用 QT_BOR 當「天數」直接累加

2. 一般機台：
   a. 取此機台「最早可用時間」appa.DT
   b. 依 CD_BOR 判斷往前或往後計算：
      - 連續（1）：前序開始後一個工時即可開始，可平行
      - 定量（2）：前序累積到 QT_BOR 後才開始
      - 整批（3）：前序全部完工後才開始
      → 往前計算 = 從交期倒推；往後計算 = 從前序完工往後堆
   c. 往前計算試排，若得到的開始時間早於機台最早可用，改往後
   d. 依 WKC 扣除每天的工時直到 TM 用完
   e. 更新 appa.DT = 本派工 timeE（下一張同機台派工從這裡開始）
```

> **工序重疊定案（2026-07-22 實作，對應舊系統 WW02.aspx.cs L363-401 / L404 / L544）**
>
> | 定案 | 內容 |
> |------|------|
> | **CD_BOR 的歸屬** | 移轉方式與移轉批量 QT_BOR 取自**前工序**（它決定怎麼交付），不是本工序。前工序為委外時無工時概念，一律當整批處理 |
> | **方向判準** | 有重疊（連續/定量）時，**比較前後兩序的總工時**再決定方向，不是一律往後堆：<br>· 前序較快（瓶頸在後）→ **往後推**：起點 = 前序開始 + 產出第一個移轉批 + Move/Queue 間隔<br>· 前序較慢（瓶頸在前）→ **往前推**：終點 = 前序完工 + 本序處理最後一批，再倒推起點 |
> | **每次移轉量** | 定量取 `QT_BOR`（≤0 時視為 1）；連續取 1 件；整批不重疊 |
> | **往前推的回退** | 倒推起點早於「機台最早可用」或早於今天 → 這段時間其實排不進去，落回往後計算（舊系統 `bForward="N"`）|
> | **倒推的停機處理** | `SubtractWorkingMinutes` 原生不扣計劃停機，倒推前先以 `ExcludeDowntimes` 把停機自可用時段扣掉，否則倒推起點偏晚、實際做不完 |
> | **錨點校正** | 倒推錨點落在非上班時段時，先用 `WorkingTimeWalker.SnapBackToAvailable` 退回最近一段產能的結束點，否則會憑空多倒推一段空白 |
>
> **產能不變式（2026-07-22 補測試釘住）**：一般機台/生產線派工的 `ScheduledStart` 與
> `ScheduledEnd` **一律落在可用時段內**——起點由 `SnapToAvailable` 校正（排程執行時刻若已過
> 當日收班或落在午休，自動推到下一段產能），終點由 `AddWorkingMinutes` 保證。
> **例外——委外（定案 2026-07-22，維持現狀不校正）**：委外派工的 `timeS` 直接取
> `max(前序完工, today)`（未經 `SnapToAvailable`）、`timeE` 為工作日加總後的 00:00，
> **刻意不套產能不變式**。理由：廠商產能無法掌握，本廠日曆對它沒有意義，硬套只會產生
> 一個假精確的時間點；委外列就以「計算出來的負荷」原樣顯示即可。
> 修改此段前請先確認是否真要引入廠商產能模型，否則不要「順手對齊」。
>
> ⚠️ **產能列（甘特灰帶）必須畫 `WorkCalendarSegment`，不得由 `WorkStart + TM_WKS` 反推**：
> `TM_WKS` 是**淨**工時（`ShiftGroupController.ComputeDayTotals`：各班別淨工時加總），
> 加回 `WorkStart` 會把午休吃掉、收班時間短一小時，畫面看起來像「排程排到沒產能的時段」，
> 實際是圖畫錯（`WorkCalendar` 另有 `WorkEnd` 欄位，反推法連它都沒用到）。
>
> 實作：`SchedulingEngine.AllocateDispatchSlot`（新增 `PrevOpContext` 參數）＋ `PlaceSlot`；
> `RunAsync` 逐工序建立 `PrevOpContext` 交棒（兄弟派工取最早起點、最晚完工、工時加總）。
> ⚠️ 這是**排程結果的實質變更**：同一生產批內採連續/定量移轉的工序，甘特圖上會出現重疊，
> 不再是「前序整條畫完才接後序」。改動此段務必同步 `SchedulingEngineTests` 的 7 個重疊案例。

### 7.4 重要副產品
排程跑完後可順便輸出：
- **延遲清單**：派工 timeE > 工單 DT_DLV 的派工
- **未排清單**：找不到可用時段的派工
- **領料率**：依 WW_M 計算每張派工的物料齊備率
- **產能圖**：每台機台每日的正班/加班總時數

### 7.5 ETA（即時完工預估）— 新增能力
舊系統 WW02 是「全量重排」。新系統的需求是「增量重算」：

```
觸發點：ProductionReport 寫入後
影響範圍：
  1. 本派工剩餘量 → 重算本派工 timeE
  2. 同機台後續派工 → 平移 timeS/timeE
  3. 同工單後續工序的派工（依 CD_BOR）→ 平移
  4. 上階工單的派工 → 平移
```

> **重算邊界（滾動視窗，與 §10.12 一致）**：機台是共享資源，本派工 timeE 延遲會推遲同機台上其他工單的後續派工，
> 故影響範圍含上述第 2 項。但為避免單機台長鏈造成即時報工的運算量爆炸，局部策略只平移
> **未來 48 小時內（可系統參數調整）、節點數 `K ≤ 50`** 的派工；超出此滾動視窗者不即時平移，
> 改由每日跨班的「全量」策略校準。目標：每次報工的 ETA 運算 ~100ms 內完成。

**設計建議**：把 7.3 的「單派工時段配置」抽成純函式，可單獨呼叫；ETA 重算就是部分呼叫。

> **2026-10-06 修正（兩條路徑原本都沒有生效過）**：報工後的增量重算（`ReportEtaNotifier` → `EtaRecalculator`）與
> 每晚 01:00 全量校準都在**沒有登入使用者的背景 scope** 執行，DbContext 全域過濾是 `PlantId == null`、
> `CreatedByRoleId == null`，查詢一律撈到 0 筆（測試都在建構式設了 CurrentPlantId，剛好遮住）。現在兩條路徑
> （含 `WorkCalendarService.GetSegmentsAsync`）一律 `IgnoreQueryFilters` 並以 plantId／派工鏈自行限定範圍。
> 全量校準同時重寫為 `EtaCalibrationService`：舊版即使撈得到資料也會再套一次 48h 視窗、跳過已開工工單、
> 同機台後續派工按工單數重複平移、且不重判 `IsPredictedLate`。新版直接呼叫排程引擎的
> `ScheduleRunMode.PreserveAssignment`——**與排程同一套時間規則**（重疊、倒推錨點、日曆、計劃停機、凍結重估；
> 延誤往後、提早往前），只是維持每張已排派工目前的機台與先後（批序依目前最早排定時刻）、不排入待排派工、
> 不更新 ScheduleRunState；換機台、改順序、排新工作屬重排排程（使用者 2026-10-06 裁決：規則須與排程相同）。
> 之後重判 `IsPredictedLate` 並回傳新轉延遲者。鎖由引擎取得。

---

## 8. 手機 API 規劃

依您所述「手機進行每個工作中心/機台的後續工單安排，並以手機進行投產」，建議 API：

> 完整 PWA 架構（前端框架、認證、離線、條碼掃描、INV 範圍）見 [mobile-pwa.md](./mobile-pwa.md)。
> 本節僅列 MFG 相關端點。所有端點前綴統一為 `/api/mobile/`（與 mobile-pwa.md §8.1 一致）。

### 8.1 我的待辦
```
GET /api/mobile/dispatches?machineId=M01&status=Scheduled
→ 回傳此機台已排定的派工，按 timeS 排序
```

### 8.2 開工準備（Setup）與備料

> 2026-07-23 定案：「備料」＝開**備料單**把該派工用料移入備料倉待領用（inventory.md §11），
> 現場**不再回報模具**——模具沿用排程指定的 `Dispatch.ToolingCode`（打次回寫與衝突偵測不受影響）。
> 「機台準備（Setup）」則單純是換模／換線的計時，工序 `SetupTime = 0` 時免準備、可直接開工。

```
GET /api/mobile/dispatches/{id}/staging
→ { orders: [已開備料單狀態], pending: [尚未備妥的用料] }

POST /api/mobile/dispatches/{id}/staging-order
Body: { clientId, clientTimestamp }
→ 由本派工未發料用料建立草稿備料單（預設備料倉取自工作中心設定），回傳 stagingId

POST /api/mobile/dispatches/{id}/setup/start
Body: { operatorId, clientId, clientTimestamp }
→ 建立 ProductionSetup（Setup 開始），Dispatch.Status = SetupInProgress
   （ToolingCode 取自 Dispatch，request 內同名欄位已不採用，僅保留舊版相容）

PATCH /api/mobile/dispatches/{id}/setup/done
Body: { clientId, clientTimestamp }
→ 關閉 ProductionSetup（SetupEndAt），Dispatch.Status = ReadyToStart（或 PendingFPI）
```

**Setup 成本歸屬**（`IProductionCostAccrualService.AccrueOnSetupDoneAsync`，桌機／手機共用）：
- 人工＝**逐人**各自備料時長 × `WorkCenter.StandardLaborCost` → `ActualLaborCost`
  （備料人員與生產人員可能不同人，各自入帳；此段人工原本完全未計）。
- 製造費用＝「最早開始備料 → 完成」時間窗 × `MachineHourlyRate` → `ActualOverheadCost`，
  **不隨備料人數疊加**（與下工 overhead 防灌水同原則）。

### 8.3 即時投產
```
POST /api/mobile/sessions
Body: { dispatchId, machineId, moldId, operatorId, clientId, clientTimestamp }
→ 建立 ProductionSession（上工），Dispatch.Status = InProgress
```

### 8.4 報工
```
POST /api/mobile/reports
Body: { sessionId, qtyOk, qtyNg, containerId?, ngReasonCode?, clientId, clientTimestamp }
→ 建立 ProductionReport，副作用累計、ETA 重算（session 維持 Open，可多次報工）

PATCH /api/mobile/sessions/{sessionId}/complete
Body: { clientId, clientTimestamp, qtyOk?, qtyNg?, containerId?, ngReasonCode? }
→ 完工：有填數量則一併建立 ProductionReport，再關閉 session 並結算工時成本
```

**報工／完工雙入口（2026-07-23 定案）**

| 動作 | 行為 | 可否多次 |
|------|------|---------|
| 報工（`/reports`、桌機 `CreateReport`） | 累計 `QtyDone`，**session 保持 Open、繼續生產**。批次轉移的現場每轉一批報一次 | 可 |
| 完工（`/sessions/{id}/complete`、桌機 `CompleteSession`） | 可選填最後一批數量（未填＝純下工）→ 關閉 session ＋ 結算人工／製造費用 | 每次開工一次 |

- 未生產完可再次開工，形成下一輪「開工 → 報工 → 完工」循環。
- 派工是否結案**與完工解耦**：仍由累計 `QtyDone ≥ Quantity` 自動 `Closed`。
- 生產線多人：各自開工／各自完工、各報各的量，最後一人完工且數量達標才結案。
- 兩支入口共用同一段報工核心（守門條件僅一份）：負數／全零拒絕、超量報工防護、
  模具打次回寫、達量自動結案與工單完工連動、ETA 重算。

### 8.5 排程瀏覽（甘特圖資料）

前端使用 **`timelines-chart.min.js`** 繪製（與舊 WW02 相同），桌機版與手機版共用同一 JSON 格式。

> 📌 **版本 2.14.3**（2026-08-14 由 2.12.1 升上來）。這支是**手工 vendored**
> （不在 `package.json`／libman 內），換版＝手動換檔。守門是
> `SmartFactory.MES.UITests/MFG/ScheduleGanttTests.cs`（升級時一併補的，在此之前甘特圖**零覆蓋**）。
>
> **`Modules/MFG/Views/Schedule/Index.cshtml` 耦合到這個函式庫的內部實作，共五處，另加 vendored 檔一處本地修補（第 6 點）**，
> 每一處都是讀原始碼才問出來的，換版時逐條複驗：
>
> | # | 耦合點 | 2.12.1 → 2.14.3 實測 |
> |---|---|---|
> | 1 | 畫 bar 顏色只吃 `zColorScale(val)`，不支援逐 segment 自訂色（後端的 `col` 欄位其實從未生效） | **未變** |
> | 2 | 攤平後額外欄位要從 `d.data` 取，**不可用 `d.labelVal`**（會退化成字串，tooltip 與點擊整段失效） | **未變**（形狀一字未改） |
> | 3 | tooltip 走 d3-tip、掛在 `document.body` 而非 container，故有兩段強制隱藏的補丁 | 未變 |
> | 4 | 執行期注入 `<style>`，在 `style-src 'self'` 下被 CSP 擋掉，故 `timelines-chart.css` 是**從注入樣式手抄的副本** | **注入樣式逐條相同**，副本不必重抽 |
> | 5 | `zColorScale` 傳的是**純函式**，但圖例元件會判成 ordinal 並呼叫 `scale.domain()` | 兩版皆會拋錯，見下 |
> | 6 | **vendored 檔本身有一處本地修補**：縮圖刷選（TimeOverview）的 `update` 開頭補 `t.scale&&` 守衛（2026-10-15，見下） | 換版會被蓋掉，`ScheduleGanttTests.Gantt_VendoredLibrary_KeepsOverviewGuard` 會紅 |
>
> **建構語法不必改**：2.14.0 的 release note 寫「switch to conventional class new」，
> 但底層 kapsule 的 `KapsuleComp` 仍是普通 function、以 `this instanceof` 判斷模式，
> **`TimelinesChart()(el)` 與 `new TimelinesChart(el)` 在 2.14.3 都可用**；
> 而 `new` 在 2.12.1 會**靜默不綁定**（不拋錯也不畫圖）。故一律維持工廠寫法。
>
> **升級順手修掉的既有缺陷（第 5 點）**：`ganttColorScale` 是純函式沒有 `.domain()`，
> 色階圖例每次繪製都會拋一次 `TypeError: t.scale.domain is not a function`。
> **2.12.1 與 2.14.3 實測皆會發生**——它不是升級帶來的，是一直都在。
> 沒被發現的原因有三層疊在一起：錯誤發生在 kapsule 的 digest 裡（非同步）故 **bar 照樣畫得出來**、
> `loadGantt` 的 `.catch(() => {})` 在另一條路徑上又把例外整個丟掉、以及沒有任何測試。
> 修法是給它補 `domain()` duck-type 成 d3 scale，並以 `.zQualitative(true)` 走離散圖例。
>
> **上游競態，已在 vendored 檔修補（第 6 點）**：底部 TimeOverview（縮圖刷選）的 update 被 debounce 1ms 觸發，
> 可能早於父層把 scale 傳進去，於是 `state.scale.domain(...)` 讀到 null（`timelines-chart.js:9427`），
> 拋 `Cannot read properties of null (reading 'domain')`。它發生在 `TimelinesChart()(el)` **綁定當下**，
> 早於任何鏈式設定，`.enableOverview(false)` 也擋不掉，呼叫端無解。
> 原先記的頻率「真畫面 0/5」不準：2026-10-15 在真畫面量到 **8/20** 次載入就拋一次（未攔截錯誤、選單巡訪時紅時綠）。
> 修法：在 min.js 的刷選 `update:function(t){if(` 後補 `t.scale&&`——scale 還沒傳進來就先不畫，父層設 scale 時會再觸發一次 update。
> 修後 0/40，且每次縮圖都有畫出刻度。Schedule/Index 的 `<script>` 同時加 `asp-append-version`，讓瀏覽器拿到修補後的檔。
> **換版時必須重補**；`ScheduleGanttTests` 的真畫面測試已不再放行這個錯誤。

> **升級拿到的東西**：`minSegmentWidth`（2.13.0，分鐘級工序不再被壓成次像素寬而看不見也點不到）、
> `#152` hover 時只在必要時搬動 DOM、隨附 `.d.ts`。資安面：`timelines-chart` 與其
> 12 個直接依賴在 GitHub Advisory DB 皆為 **0 筆**，升級不是資安驅動的。

**API 端點**
```
GET /api/mfg/schedule?dateTo=2026-06-30&plantId=P01   ← 桌機版（生產排程圖主頁）
GET /api/mobile/schedule?dateTo=2026-06-30             ← 手機版（廠別由 JWT 取得）
```

**Response：timelines-chart.js 格式**
```json
[
  {
    "group": "WC1",
    "data": [
      {
        "label": "A11",
        "data": [
          {
            "timeRange": ["2026-06-01T08:00:00+08:00", "2026-06-02T10:30:00+08:00"],
            "val": "PLN-001",
            "ww": "WW-20260001",
            "wo": "WO-20260001",
            "dtDlv": "2026-06-10",
            "wh": 150,
            "pir": 1
          }
        ]
      },
      {
        "label": "A11産能",
        "data": [
          {
            "timeRange": ["2026-06-01T08:00:00+08:00", "2026-06-01T17:00:00+08:00"],
            "val": "正班"
          },
          {
            "timeRange": ["2026-06-01T17:00:00+08:00", "2026-06-01T20:00:00+08:00"],
            "val": "加班"
          }
        ]
      }
    ]
  },
  {
    "group": "委外",
    "data": [
      { "label": "委外", "data": [ ] }
    ]
  }
]
```

**欄位對應（舊 WW02 DataTable → API 欄位）**

| WW02 欄位 | API 欄位 | 說明 |
|----------|---------|------|
| `ID_WC` | `group` | 工作中心（甘特圖左側標籤） |
| `ID_APPA` | `label` | 機台（派工列） |
| `timeS` | `timeRange[0]` | 派工開始時間（ISO 8601 含時區） |
| `timeE` | `timeRange[1]` | 派工結束時間 |
| `val`（ID_PLN）| `val` | 生產批號，決定甘特條顏色 |
| `ww`（NO_WW）| `ww` | 派工單號（tooltip） |
| `NO_WO` | `wo` | 工單號（tooltip） |
| `DT_DLV` | `dtDlv` | 應完成日（tooltip，逾期標紅） |
| `wh` | `wh` | 工時（分，tooltip） |
| `Pir`（CN_P）| `pir` | 優先序（tooltip） |

**産能列規則**：
- `label = 機台ID + "産能"`（固定命名，前端依此識別套用産能底色樣式）
- 來源：`WorkCalendar`（`TM_WKS` 正班 + `TM_WKO` 加班）依日展開為區段
- `val = "正班"` → 正班色（如淺綠）；`val = "加班"` → 加班色（如橘黃）；前端 `colorScale` 指定
- 委外行（`IsOutsource=true`）無産能列
- 産能列僅供產能使用率目視評估，不計入排程演算

### 8.6 調整派工優先序
```
PATCH /api/mobile/dispatches/{id}/priority
Body: { newPriority }
→ 重新排序此機台的待排派工
```

---

## 9. 切片實作建議（呼應 Fisher 提出的業務先後順序）

> ⚠️ **本節為 Phase 概覽**。**詳細交付物／驗收標準／估算以 §11 為準**（§11 已把 Phase A 細分並補上驗收標準）。
> 兩份估算的差異說明：§11「12-14 週」為核心 Phase A–F 依序推進（D–E 併行）的工期；
> 本節「15-22 週」為含緩衝與品質/成本擴充（§15/§16 的 Phase G–J）之保守上界。實作排程請採 §11。

依您指示「生產批 → 工單派工單 → 排程 → 回報」的順序：

### Phase A：BAS 擴充（前置主檔）
- 新增 `Routing`, `RoutingOperation`, `RoutingBom`
- 新增 `WorkCenter`, `Machine`（含 `CurrentStatus`，預設 `Available`）
  - **成本埋入**：`WorkCenter` 加 `MachineHourlyRate`（機台費率，decimal 18,4）、`LaborHourlyRate`（人工費率，decimal 18,4）
- 擴充現有 `BAS.Tooling`（加 `ToolingType`, `CavityCount`, `ProductsPerCavity`, `AccumulatedShots`）
- 擴充 `BAS.Product`：確認 `StandardCost`（decimal 18,6）欄位存在，作為工單建立時材料成本快照的來源
- 新增 `WorkCalendar`（日曆，三層覆蓋架構）
- 新增 `MachineStatusOrder` / `MachineStatusOrderLine`（機台狀態管理）
- 新增 `PlannedDowntime`（計劃停機，影響排程可用時段）
- 估算：3-4 週（含機台狀態模組，較原先增加 1 週）

> **WorkCalendar 三層覆蓋規則**：查詢工時時依「機台 > 工作中心 > 全廠」順序取最小範圍設定。
> 日常只需維護全廠日曆（班別 + 公假），工作中心或機台有特殊工時時才新增覆蓋紀錄，
> 避免每台機台都要設定完整日曆的重複作業。

#### 產能模型與班別鏈（已實作 / 已定案，2026-05-21）

> 此區把「班別→班制→工作日曆→產能查詢」的定案鏈與排程的時間模型釘住，避免日後漂移。

- **物化三階日曆（已定案，不改規則式）**：產能以批次展開寫成 `WorkCalendar`（每日 `TM_WKS`/`TM_WKO` + 機台>工作中心>全廠 覆蓋，per-plant）；排程預載這些列。`WorkCalendarService.GetWorkHoursAsync(machine, wc, plant, date)` 已實作三層 fallback、查無回 0。**不改為純規則式 on-demand**（理由：spec 排程綁定預載 WKC，且本就保留「批次展開」人工觸發動作）。
- **班別鏈（已實作）**：`Shift`（班別，含 StartTime/EndTime/WorkMinutes、**新增 BreakStart/BreakEnd**）→ `ShiftGroup`／`ShiftGroupDay`（每日排班，`Shift1~4Code` 為 Shift 主檔下拉）→ **正班工時/開班/收班由所選班別伺服端自動推算**（正班=各班別 WorkMinutes 加總、開班=最早 StartTime、收班=最晚 EndTime；加班手填）→「批次展開」依特殊日（假日歸零/補班套指定週幾）寫入 `WorkCalendar`（對齊舊系統 `WKC01_RunBtnAJAX`）。
- **預設班制（`ShiftGroup.IsDefault`，2026-05-26 定案）**：`Plant` 在 Core、班制在 ProjectZ，故**不在 Core `Plant` 加欄位**；改在 ProjectZ 的 `ShiftGroup`（`IPlantEntity`）加 `IsDefault`＝本廠預設班制（每廠至多一筆，設定時清除同廠其他預設）。**批次產生未指定班制時的 fallback**：`指定工作中心 → 該 WC.ShiftGroupCode → 本廠 IsDefault 班制`；皆無則報錯（無樣板無法展開）。
- **批次產生去重/覆蓋（已修正）**：去重鍵對齊 `WorkCalendar` 唯一索引 `(PlantId, WorkCenterId, MachineCode, Date)`，**不含 `ShiftGroupCode`**（`ShiftGroupCode` 僅為「以哪個班制展開」的來源註記）；`WorkCenterId/MachineCode` 一律正規化為 null。提供「**重產即覆蓋**」選項：勾選時先刪除範圍內既有列再重建，不勾則略過已存在日期。
- **甘特圖時間模型**：現沿用舊 WW02 慣例 `timeE = 開班 + 淨工時`（午休工時壓縮、不畫缺口），產能帳正確。**「實際到離時鐘」甘特延到 Phase C**：屆時以「working-time walker」沿「可用時段」步進、把淨工時換算為真實時刻、跳過休息/班距。休息時段欄位已先收齊，Phase C 零返工。
- **可用時段物化（`WorkCalendarSegment` 子表，已實作 2026-05-26）**：`WorkCalendar` 一列只有單一 `(WorkStart, WorkEnd)`＋總工時，**無法表達多班別日的班距空檔與午休缺口**。對策：批次展開時一併物化 `WorkCalendarSegment(PlantId, WorkCenterId?, MachineCode?, Date, Seq, StartMinute, EndMinute)`，**時刻以「自當日 00:00 起算分鐘」(0..1440)** 表示（避免 TimeOnly 無法表達 24:00／跨午夜）。建構邏輯為純函式 `WorkCalendarSegmentBuilder.BuildDay()`（合併重疊班別、扣午休、班距留空；跨午夜班的午夜後尾段以 carry 物化於隔日），已有單元測試。
  > **產生規則**：批次產生時就所選範圍/範疇**一律刪除並重建** Segment（純班別衍生），與 `WorkCalendar` 表頭列的 skip/overwrite 解耦；carry 逐日串接（假日/週末亦算以承接夜班尾段）。唯一索引 `(PlantId, WorkCenterId, MachineCode, Date, Seq)`。
  > **為何物化、不在走步時重解 Shift 鏈**：特殊日下展開當下的 `ShiftGroupDay→Shift` 映射可能與事後重解漂移；物化結果才與產能帳一致。`WorkCalendar` 的工時欄保留作彙總/產能帳，`WorkCalendarSegment` 專供走步器讀真實時刻。

  > 🆕 **加班（`TM_WKO`）自 2026-08-26 起也物化進 `WorkCalendarSegment`**（使用者裁決「須改程式納入加班工時」）。
  > 在此之前 `BuildDay()` 是**純班別衍生**，`TM_WKO` 完全沒有參與，於是**排程走步器永遠只看得到正班**——
  > 而同一份日曆的 `TM_WKO` 在別處是算的：`WorkCalendarService.GetWorkHoursAsync` 三層都回 `TM_WKS + TM_WKO`、
  > `OeeService` 的計畫時間（OEE 分母）含加班、甘特的可用分鐘也是 `(TM_WKS + TM_WKO) × 60`。
  > **結果是同一天的產能有兩個答案**：甘特畫得出加班灰帶（`ScheduleController` 原本註明「僅作為視覺參考」）、
  > OEE 分母含加班，但排程只排正班。兩套算法各自都自洽，不一致只在跨模組比對時才看得見——
  > **沒有任何測試抓得到，是使用者審擷取物時憑直覺抓出來的**（`code-mfg-schedulecontroller-3995516d`）。
  >
  > 作法：`BuildDay(shifts, carryIn, out carryOut, overtimeMinutes = 0)`，
  > 加班接在**當日班別**的最後一段之後（與甘特把加班灰帶接在正班後面一致，兩邊要一起改）。
  > 三個刻意的邊界：
  > ① **錨點不看 `carryIn`**——carryIn 是前一日跨午夜的尾段、位置在當日開頭，
  >    拿它當錨點會把今天的加班接到昨天的班尾後面。因此**當日沒有班別就沒有加班**（假日、週末），
  >    即使 `TM_WKO` 有值也不產生時段：沒有正班可接的加班沒有起點。
  > ② **先合併再取最後一段**——多班別或有午休時，未合併清單的末項不是時間最晚的那一段。
  > ③ **加班可以跨午夜**，尾段照常進 carry。
  >
  > ⚠️ **`Edit` 也必須重建時段**：加班時數是在這張畫面上逐日改的，改完不重建，排程就吃不到那個改動，
  > **而且不會有任何錯誤訊息**（存檔成功、排程結果一模一樣）。故 `BatchGenerate` 的本體抽成
  > `GenerateAsync`，`Edit` 在 `TM_WKO`／日期／範疇有變動時呼叫它重建該月時段；
  > 日期或範疇搬動時新舊兩邊都重建。權限檢查留在各自的動作上（展開是 Create、編輯是 Update）。
  >
  > ⚠️ **既有列的加班以現存的 `TM_WKO` 為準**，不用班制預設值蓋回去——否則使用者調過的加班
  > 會在下次批次展開時安靜地被還原。
- **計劃停機（`PlannedDowntime`，已實作 2026-05-26）**：「未來不可用時間區段」（保養/改裝/教育訓練），欄位 `(PlantId, WorkCenterId?, MachineCode?, StartTime, EndTime, DowntimeType?, Reason, IsActive)`；`StartTime/EndTime` 用 `timestamp without time zone`（廠別壁鐘）。`DowntimeType` 取自代碼維護 `downtime_type`。**範疇＝工作中心或機台擇一必填**：只填工作中心＝整個工作中心停機，選機台＝單機（工作中心自動回填）；**全廠停機走 `SpecialDay`／日曆歸零**，故不開放兩者皆空。與機台狀態 A–H（點狀）互補。
  > **消費（定案）**：**不**物化進 `WorkCalendarSegment`（停機可隨時編輯，物化會被迫重建漂移）；由 **Phase C 有限產能排程於可用時段中執行期相減**。機台 M 在某時段是否停機＝存在 row 滿足 `MachineCode == M` **或** `(MachineCode 為空 且 WorkCenterId == M 的工作中心)`（工作中心級套用到該中心所有機台，與機台級取聯集）。實體/索引/migration/CRUD 維護畫面已實作；排程相減於 Phase C。
- **⚠️ UTC 地雷（前端 JS 二次換算，2026-07-14 踩坑記錄）**：本系統房規＝DateTime **數字本身就是廠別當地時間**，`Kind=Utc` 只是貼給 Npgsql 用的標籤（並未真的做時區換算，見 `MrpEngine.ctx.Today` / `SchedulingEngine` 各處 `SpecifyKind(Utc)` 用法）。**這類 DateTime 序列化成 JSON 給前端 JS 用時，禁止用內建 `"O"` 格式**——`"O"` 對 `Kind=Utc` 值會補上 `Z` 尾碼，前端 `new Date("...Z")` 依 ECMAScript 規範會把它當成真正的 UTC 時刻，再用瀏覽器所在時區（台灣 +8）換算成本地時間顯示，等於把本來就是本地時間的數字**又加了一次 +8 小時（可能跨天）**。`ScheduleController`（甘特圖 `/api/mfg/schedule`、生產準備看板、機台負荷）已全面改用 `ToString("yyyy-MM-ddTHH:mm:ss")`（不帶 `Z`/時區資訊，JS 依規範會當本地時間解析，不再二次換算）。**日後任何把這類「房規本地時間」DateTime 吐給前端 JS 解析的新程式碼，一律比照此格式，不得用 `"O"`。**
  > **除錯提醒**：用 `psql` 直接查 `timestamptz` 欄位核對時間時，預設連線時區（通常非 UTC）會再對存值做一次顯示換算，容易跟「數字即當地時間」的房規搞混、誤判成資料算錯。查證時先下 `SET TIME ZONE 'UTC';` 拿到未經換算的原始存值，才能跟程式邏輯內部算出來的數字直接比對。
- **生產線排程支援（`WorkCenter.IsProductionLine`，已實作 2026-07-14）**：工作中心若為「生產線」（連續作業、可能含多人多機），**依業務規則本來就不掛 `WorkCenterMachine`**——`SchedulingEngine.AllocateDispatchSlot` 原本假設每個工作中心必有機台清單，遇到生產線一律回 null（永遠跳過），是排程引擎的支援缺口，不是資料設定問題。已修：新增 `isProductionLine` 分支，**把整條線視為單一排程資源**，可用時段/停機查詢改用 `ctx.GetSegments(machineCode:null, workCenterId, date)` / `ctx.GetDowntimeRanges(machineCode:null, workCenterId)`（沿用既有機台>工作中心>全廠 fallback，`machineCode=null` 天生對應工作中心層級，無需新 API）；產能追蹤 key 從機台代碼改用工作中心代碼；`Dispatch.MachineCode` 存工作中心代碼本身（讓甘特圖沿用既有「依 MachineCode 分列」邏輯正常顯示，否則 MachineCode=null 的派工完全不會出現在圖上，比照委外的「無 MachineCode」慣例但兩者用途不同勿混淆）。單元測試：`SchedulingEngineTests.AllocateSlot_ProductionLine_SchedulesWithoutMachine`。
- **生產線多人上/下工與報工（`WorkCenter.IsProductionLine`，已實作 2026-07-15）**：生產線可能同時由多人共同作業，`DispatchController.StartSession`/`MobileMfgController.StartSession` 原本寫死「同一張派工單只要有任何一筆 Open 的 `ProductionSession` 就擋」（不分是誰），一般機台合理但生產線會擋住第二個人上工。已修：守門條件改成「生產線 → 同一個操作員（`OperatorId`）不能重複上工，不同操作員可同時各自 Open」；一般機台維持原本「同時只能 1 人在線上」不變（避免同一台機台被兩人各自宣稱操作、產出歸屬混淆）。
  - **產出數量維持單一流**：同一條線的 `Dispatch.QtyDone` 不按人拆分（實體產出本來就混在一起，硬拆會失真），報工面板（`ReportForm`/`_ReportForm.cshtml`）改成優先抓「目前登入者自己」的 Open session（抓不到才退回抓任一筆，維持主管代為報工的相容行為）。
  - **人工成本 vs 機台/產線 overhead 分離結算（`ProductionCostAccrualService`，Web/Mobile 共用，取代原本兩邊各自一份的 `AccumulateSessionCostAsync`）**：人工成本（`ActualLaborCost += 工時 × StandardLaborCost`）不論一般機台或生產線都按「每人各自 session 工時」直接累加，這本來就對（5 人各做 2 小時＝10 人時人工成本）。但 overhead（`ActualOverheadCost += 工時 × MachineHourlyRate`）代表的是設備/產線本身的固定費用，**不會因為在線人數變多而跟著變大**，若沿用「每次下工都疊加」會被人數灌水（5 人同時上工 2 小時，overhead 會算成 10 小時份，錯）。改成：一般機台維持原行為（同時恆 1 人在線，session 工時＝機台占用時間，直接累加即可）；生產線改成**「這張派工單最後一人下工」時，才依 `Dispatch.OverheadChargedThroughUtc`（本輪已結算到的時間點）到現在的運轉時間窗結算一次 overhead**，並把該欄位推進到 now，避免下一輪（同一天分多次上下工，或隔天再開工）重複計費同一段時間或漏算閒置缺口。
  - 單元測試：`ProductionLineMultiOperatorTests`（`StartSession_ProductionLine_TwoOperators_BothSucceed`／`StartSession_ProductionLine_SameOperatorTwice_SecondBlocked`／`StartSession_RegularMachine_SecondOperatorBlocked`／`StopSession_ProductionLine_LaborAddsPerPerson_OverheadChargedOnceForWindow`／`StopSession_RegularMachine_OverheadChargedImmediatelyPerSession`）。
  - **一般機台換班（同一派工單先後由不同人上工）本來就已支援，非新增**：`StartSession` 的守門條件只鎖「同時」（`AnyAsync(Open)`），不鎖「誰」；前一人 `StopSession` 關閉後即無 Open session，任何人（含不同操作員）都能對同一張仍在生產中的派工單重新 `StartSession` 繼續。`CreateReport` 的 `QtyDone` 累加也是掛在 Dispatch 層級，橫跨多個先後的 session 正確加總。已補回歸測試鎖定此行為：`StartSession_RegularMachine_ShiftChange_DifferentOperatorContinuesAfterPreviousStops`／`CreateReport_RegularMachine_ShiftChange_QtyAccumulatesAcrossSessions`。
  - **備料（`ProductionSetup`）比照上工邏輯支援生產線多人（2026-07-15）**：`StartSetup` 守門同樣依 `IsProductionLine` 分流；生產線允許已是 `SetupInProgress` 時其他人加入（一般機台仍只能從 `Scheduled`/`ReadyToStart` 進入一次）。`DoneSetup`／Mobile `SetupDone` 改成**關閉這張派工單「所有」Open 的 `ProductionSetup`**（不只抓第一筆），overhead 用「這批設備全部關閉的 setups 中最早開始」到現在的單一時間窗算一次——這個算法對「一般機台恆只有 1 筆」與「生產線多筆」兩種情況都成立，不需要另外分支或加欄位（Setup 不像 Session 需要 `OverheadChargedThroughUtc` 水位線，因為 Setup 本來就是一次性動作，不會像上下工一樣分好幾輪）。單元測試：`StartSetup_ProductionLine_TwoOperators_BothSucceed`／`StartSetup_RegularMachine_SecondOperatorBlocked`／`DoneSetup_ProductionLine_ClosesAllOpenSetups_OverheadChargedOnceForWindow`。
  - **產線即時看板（`/MFG/LineStatus`，2026-07-15）**：「一鍵看整條線在忙什麼」——全部生產線（`IsProductionLine=true`）卡片總覽，即時顯示各線目前生產中/備料中/待機、在線操作員（含上工多久）、目前派工進度。純即時快照不快取（`LineStatusAnalyticsService`），前端每 30 秒輪詢＋手動重新整理按鈕；沿用 Gantt 那次學到的教訓，loading 提示與內容區用兩個常駐子元素分開，重繪只清內容區。

### Phase B：MRP 與生產批
- `MrpRun` / `MrpRunLine` / `ProductionBatch`（含 Priority, Status, ExpectedEndDate）
- MRP 引擎（Fstock、RunMPS、RunJ 的新實作）
- `WorkOrder` + `WorkOrderOperation` + `WorkOrderMaterial` 自動建立
- `Dispatch` + `DispatchMaterial` 自動建立
- **成本埋入**：
  - `WorkOrderMaterial` 加 `UnitCost`（decimal 18,6）：工單建立時快照自 `Product.StandardCost`
  - `WorkOrder` 加六個成本欄位（見 §16.2）：釋出時計算標準成本並寫入
- UI：MRP 執行頁、生產批查詢（依 ProductionBatch 彙總）、工單查詢
- 估算：3-4 週

### Phase C：排程引擎
- 抽出純函式版的「單派工時段配置」
- 全量排程 API（對應 WW02）
- 排程結果寫入 Dispatch.PlannedStartAt / PlannedEndAt
- UI：甘特圖（可沿用 WW02 前端，僅換 API）
- 估算：4-6 週（最難）

### Phase D：上工與報工
- `ProductionSession`, `ProductionReport`, `ProductionReportContainer`
- Web 版報工頁
- 完工累計、Dispatch.FG_CLOSE 自動更新
- **成本埋入**：`ProductionSession` 關閉時，依實際工時自動累計至 `WorkOrder.ActualLaborCost` 與 `ActualOverheadCost`（見 §16.3）
- 估算：2-3 週

### Phase E：手機 API + ETA 即時推算
- API as per §8
- 增量 ETA 重算引擎
- 估算：3-4 週

### Phase F：完工入庫銜接
- WorkOrder 完工 → 觸發 INV 模組入庫
- 不良品：`ProductionReport.QtyNg` 計入損耗，不需單據
- 重工：手工建立 `WorkOrder`（`IsRework = true`, `ReworkFromWorkOrderId`）
- 估算：1-2 週

**總估算**：15-22 週（單人開發，含緩衝與 §15/§16 品質/成本擴充 Phase G–J 的保守上界）。
> ⚠️ 核心 Phase A–F 的工期以 **§11「12-14 週」為準**（本節為含擴充的保守上界，兩者差異說明見本節開頭）。

---

## 10. 已確認規格與決策

> 原「待確認問題」已由 Fisher 審核，以下為定案規格，可直接作為實作依據。

### 資料模型

**1. 生產批號（ProductionBatchId）設計**
`ID_PLN` 對應**一筆訂單需求列**，一個 ProductionBatchId 下可有多張 WorkOrder（對應 BOM 各階半成品）。
不跨訂單需求合併。用途：與訂單需求對應、追蹤整批完成狀況。
> 評估方向：支援分次展開（同一 BatchId 保留，只新增/覆蓋未投產的工單）；合併展開需討論作業流程後再決定。

**2. WW 與 WO_R 的關係**
現行為 1:1（一工序對一派工）。**新系統目標**：支援 1:N 拆分，允許多機台同步生產同一工序。
- 拆分須保持完整紀錄：子派工以 `SplitGroupId` 關聯，數量加總等於原派工
- 限制：僅 `Pending` / `Scheduled` 狀態可拆分；`InProgress` 或 `Closed` 不可拆
> **[快照邊界 (Snapshot Boundary) 備註]**：
> 生產途程工序與 BOM 的更新，基於關聯完整性，在尚未投產前採用「先刪後加 (Delete-then-Insert)」的覆寫模式。這定義了明確的快照邊界：
> 1. 當工單尚未釋出或未開始生產時，編輯途程會直接以新設定全量覆蓋。
> 2. 一旦工單已釋出 (Released) 或進入生產中 (InProgress)，該工單的工序與用料已完成「快照物化 (Materialized Snapshot)」，此時主檔的刪除與修改將不會影響已生成的工單工序 (`WorkOrderOperation`) 與工單用料 (`WorkOrderMaterial`)，確保生產履歷的一致性與追溯性。
> 3. **實作**：主檔工序重建採**單一交易（刪 + 插原子性）**——`RoutingController.UpdateOperations` 先反序列化再刪、再插，且 Create/Edit 以 `BeginTransactionAsync` 包覆 header save 與工序重建，避免「舊工序已刪、新工序未入」的部分失敗。


**3. 工單 BOM 巢狀深度**
無上限，以遞迴方式設定。RunMPS 以遞迴實作，建議設防呆上限（20 層），超過時警告。

**4. 委外的 Dispatch 處理**
委外類似「需發/領料的採購」，但須依生產節奏回報進度。
決策：**保留在 Dispatch Entity**，加 `IsOutsource` 旗標，不另建 Entity。
- 發料：沿用 `DispatchMaterial`
- 進度回報：ProductionSession / ProductionReport 照常建立，MachineId = null
- 完工：觸發委外廠商採購訂單的收貨流程

**4.1 工序委外的委外建議產生（2026-06-09 定案）**

委外有兩條路徑，均產生 `OutsourceDemand` 建議（與一般採購 `PurchaseDemands` 分流）：

| 路徑 | 觸發 | 狀態 |
|------|------|------|
| **整品委外**（CD_MRP=S / `MrpMode.Outsource`，`RunJAsync`）| MRP 展開時 | ✅ 已實作（Phase G）|
| **工序委外**（`RoutingOperation.IsOutsource`，`RunMpsAsync`）| MRP 遇 IsOutsource 工序時 | ✅ 已實作（Phase G）|

> ⚠️ **實作架構（2026-06-09，與原始設計差異）**：MRP 引擎（`MrpEngine`）展開時**不持久化** `OutsourceDemand` 實體，
> 只在 `MrpRunResult.OutsourceDemands` 產生 **in-memory 訊號**（`MrpOutsourceDemand`，整品＝`RunJAsync`、工序＝`RunMpsAsync`）。
> 真正的 `OutsourceDemand` 實體改在「**轉委外**」步驟由 `OutsourcePurchaseOrderController` **lazy 建立**（Stage 2），
> 並保留**相容路徑**：對尚無 `OutsourceDemand` 的 `Dispatch.IsOutsource=true` 派工，於轉委外清單直接以派工衍生顯示、按下轉換時才建實體。
> 故下方「`RunMpsAsync`／`RunJAsync` 建立 `OutsourceDemand`」描述為**原設計意圖**，實際落地點為轉委外 Controller。

**OutsourceDemand Entity（委外建議）**：

```csharp
public class OutsourceDemand   // DocType: OD，MRP 展開後產生，拋轉前的中間狀態
{
    public string DemandId { get; set; }
    public string MrpRunId { get; set; }             // FK → MrpRun
    public string WorkOrderId { get; set; }           // FK → WorkOrder（委外工單）
    public string? DispatchId { get; set; }           // FK → Dispatch（工序委外時有值，整品委外為 null）
    public OutsourceDemandType DemandType { get; set; }
    public string ProductCode { get; set; }           // 收回品（整品委外=成品；工序委外=半成品）
    public decimal DemandQty { get; set; }            // 需委外數量
    public DateOnly RequiredDate { get; set; }        // 委外應完成日
    public string? RoutingOperationCode { get; set; } // 工序委外時：哪道工序
    public decimal TransferredQty { get; set; } = 0M; // 已拋轉至 OPO 的累計量
    public bool IsFullyTransferred => TransferredQty >= DemandQty;
    // 送外料清單由 DispatchMaterial（IsOutsource=true 的派工）衍生顯示，不另存欄位
}

public enum OutsourceDemandType { WholeProduct, Operation }
```

**`RunMpsAsync` 遇 `op.IsOutsource` 時的行為**（原設計；實際 demand 訊號於展開產生、實體於轉委外 lazy 建立）：
1. 設定 `Dispatch.IsOutsource = true`（已實作）
2. 產生 `MrpOutsourceDemand` 訊號（`DemandType = Operation`，`DispatchId = 本派工`）；`OutsourceDemand` 實體於轉委外時建立
3. 送外料由 `DispatchMaterial` 衍生顯示（不重複儲存）

**`RunJAsync`（整品委外）**：產生 `MrpOutsourceDemand` 訊號（`DemandType = WholeProduct`，`DispatchId = null`）；`OutsourceDemand` 實體於轉委外時建立。（一般採購需求仍走 `PurchaseDemands`。）

### 業務規則

**5. MRP 重跑規則**
- 已 `InProgress` 或 `Completed` 的工單**不可被重跑覆蓋**
- 投產（Dispatch 轉 InProgress）時，自動對**受影響的同物料後續需求**重算庫存可用量並重建 `InventoryReservation`，避免提早投產造成其他需求的時間點錯位
  > **實作策略（定案）**：採**同步重算**，在投產交易內以 `RowVersion` 樂觀並發保護、並收斂交易範圍即可；
  > 單廠掃碼併發程度尚不需非同步化。**已評估並暫緩**「in-memory `Channel` 事件驅動 + 防抖」方案——記憶體佇列在
  > 行程重啟即丟失、對庫存正確性風險過高，且「MRP 啟動 flush」涵蓋不到其他讀者。若日後實測出現鎖爭用，
  > 改走 **durable outbox（落地事件表）** 而非記憶體佇列。
- 已 `OnHold` 的派工**不重排**（使用者 2026-10-06 裁決：準備中、準備完成、暫停皆凍結）；要換機台走「換機續做」（見 §4.3 副狀態）
- 人指定的機台（`IsMachineLocked`：ReassignMachine、Split 指定機台、換機續做）重排只排在該機台；該機台不可用才退回同工作中心挑選並清旗標
- 凍結佔用只計未結案工單（Released／InProgress）的派工
- 同廠互斥：`RunAsync` 以 `pg_try_advisory_xact_lock(hashtext('mfg-schedule:'||plantId))` 鎖，拿不到回 `Busy`；完成寫 `ScheduleRunState`
- **自動重排規則**（2026-10-14）：自動重排（異動後／派工前／每日定時）依廠別參數 `mfg_auto_schedule_rule` 排序（未設定＝Priority，原本寫死 Priority）；排程頁規則下拉預設同值，手動仍可另選。01:00 ETA 校準（`PreserveAssignment`）依目前先後、不看規則
- **凍結時界**（2026-10-14，使用者採納 10/6 評估時留下的待決項）：廠別參數 `mfg_auto_schedule_freeze_hours`＝N 時，自動重排把「`Scheduled`、`ScheduledStart < now+N`、`ScheduledEnd > now`、機台仍可用」的派工視同凍結（不移動、佔機台、接力後續工序）。**手動排程不套用**——10/6 裁決「手動時只有 FrozenStatuses 凍結」不變。已知限制：機台佔用是單一最早可用指標、不插空檔，時界內被取消或提早完工留下的空檔要等時界過了才回填；時界內派工的前工序若延誤，該派工仍不移動（由 01:00 校準調整時間）
- **排程紀錄**（2026-10-14）：每次 `Full` 模式排程（手動＋自動）寫一筆 `ScheduleRunLog`＋`ScheduleRunChange` 明細（NewlyScheduled／Moved（同機台起點差 ≥1 分）／MachineChanged／Unscheduled），另記 `TomorrowChangedCount`（異動前或後落在明天）與 `FencedCount`。單次明細上限 1000 筆（計數仍全量）；每次寫入時刪本廠 60 天前的紀錄（PG 走 `ExecuteDelete`＋FK `ON DELETE CASCADE`）。與 `ScheduleRunState` 同理不實作 IPlantEntity，端點 `/Schedule/RunHistory`、`/Schedule/RunChanges/{id}` 自行檢查廠別權限。校準模式不寫

**6. 庫存預留**
採**實體鎖定**：MRP 展開後建立 `InventoryReservation`，其他需求查詢可用庫存時扣除已預留量。
與新 INV 模組的對接詳見 §17。

**7. 派工合併/拆分**
目標支援合併與拆分，須保持完整紀錄：
- **拆分**：一張派工 → 多張子派工（`SplitGroupId` 關聯），各分配機台，數量加總等於原派工
- **合併**：多張同機台同產品 Pending 派工 → 一張合併派工（記錄來源派工 ID）
- **限制**：僅 `Pending` / `Scheduled` 狀態可操作；`InProgress` / `Closed` 不可異動

**8. 緊急插單排程**
排程採**動態全量重算**模式（非傳統有限產能固定排程）：每次執行排程程式都重新決定下一張派工順序。
- 已 `InProgress` 的派工鎖定不動，其餘重排
  > **「鎖定」＝時段起點、機台、狀態不動；計畫結束（`ScheduledEnd`）仍要重算**：帳面結束是投產前
  > 以整批量算出來的，生產中的工單一有進度就不再成立。重排時一律依**未完成量**
  > （`Quantity − QtyDone`）自 now 沿工作日曆重估完成時刻，與 §7.5 報工即時 ETA 同口徑
  > （`EtaHelper.CalcRemainingMinutes` 依完工比例縮減標準工時），避免兩條路徑給出不同預估。
  > 進度落後/超時 → 往後延（機台佔用同步順延，新工作不會被排進「帳面已結束、實際仍在生產」的
  > 時段）；進度超前 → 往前收（機台提早釋出、後續工序接力到重估後的完成時刻）。
  > 例外：`OnHold` 的重估只是「立即恢復」的樂觀下界，只准往後延、不准把原計畫結束往前拉；
  > 委外派工無工時概念（以工作天步進），不重估。
  >
  > **起訖必須成對重算（2026-08-14 修正）**：舊實作只重算 `ScheduledEnd`，起點用
  > `ScheduledStart ??= today`（已有值就不動），於是甘特長條變成「左緣是前一次排程留下的化石、
  > 右緣是本次自 now 的重估」，長度既不等於工時、也不等於任何一次排程的結果，畫面上表現為
  > **排程開始時間排在過去**。判準取「有沒有實際投入產能」而非狀態字面：
  >   - 有產出（`QtyDone > 0`）或機台已實際投入（`InProgress` / `OnHold` / `SetupInProgress`）
  >     → 帳面起點是真實開工時刻，**保留**（起點在過去是正確的）；
  >   - 其餘（`ReadyToStart` / `PendingFPI` 且尚無產出）→ 一件都還沒做，整份工時都自 now 起算，
  >     起點**必須跟著移到 now**，並以 `WorkingTimeWalker.SnapToAvailable` 校正進可用時段
  >     （否則起點會停在沒有產能的時刻，與經日曆校正的完工時刻對不起來）。
  >
  > 重估邏輯已抽為純函式 `SchedulingEngine.EstimateFrozenDispatch`（不需 DbContext）；
  > 守門：`SchedulingEngineTests.FrozenEstimate_*`（7 支，含 OnHold／委外／已完量的回歸）
  > 與 `ScheduleRunFrozenDispatchIntegrationTests`（真 PG + 真 `/Schedule/Run`，守寫回路徑）。
  >
  > **凍結派工彼此之間也要有先後（2026-08-14 修正）**：凍結派工不進 `pendingWos`，因此
  > `OrderOperationsInBatch` 的 BOM 深度排序與 `prevEnd` 接力**對它們一行都不生效**。
  > 舊實作每張各自自 now 起算自己的剩餘工時，等於假設所有機台平行、彼此無關，於是
  >   ① 母件工時短時會**早於自己的元件完工**（實例 `PB-2607-00014`：母件 640 分算到 8/17、
  >      元件 2460 分算到 8/20，母件早三天）；
  >   ② 同一台機器上的兩張凍結派工會**同時開工**、時段整段重疊。
  >
  > 改由 `SchedulingEngine.EstimateFrozenDispatches`（純函式）統一排序與接力，規則與主迴圈一致：
  > **同批內 BOM 深階先**（元件做完才輪到母件），同深度依工序序號，最後以派工編號穩定 tie-break；
  > 跨批之間沒有物料相依、只共用機台，故僅透過 `machineAvail` 互相推擠。
  > BOM 深度由 `SchedulingEngine.BomDepth` 計算（父工單不在集合內時就地停止，並擋環狀父子鏈）。
  >
  > **接力必須套用 §7.3 b 工序重疊，不是單純「接在前面之後」**：前置為定量/連續移轉時，
  > 本工序邊收移轉批邊做，不必等前置整批完工。實例 `PB-2607-00014_02` 是「每 30 件移轉」，
  > 母件 `_01` 的正確畫法是**終點 = 元件完工 + 單件工時 × 移轉批量，再往前倒推起點**
  > （母件因此與元件的生產時段重疊）。重疊推算與主迴圈共用 `ResolveOverlapStart`——
  > 只在 `AllocateDispatchSlot` 實作的話，同一批會因為「一張凍結、一張沒凍結」而畫成兩種語意
  > （`PB-2607-00014` 凍結被畫成單純接力，`PB-2607-00015` 未凍結卻是正確的倒推重疊）。
  >
  > ⚠️ **兩個下限語意不同，合併就會把重疊抵消**：
  >   - 「同批接力點」（前置完工）只是**往後推**那條的候選起點；
  >   - 「機台可用時刻」才是兩條路徑共同的**硬地板**（機台被佔著，重疊也穿不過去）。
  >   倒推那條若拿前置完工當地板，倒推出來的起點永遠 &lt; 地板 → 一律落回往後推，重疊形同不存在。
  >
  > ⚠️ 接力**只推遲「尚未實際投入」的派工**（判準同上）。已在跑的機台不可能因為前置晚了就
  > 倒退重來——它的起點是既成事實，只有完工時刻需要重估。
  >
  > 守門：`SchedulingEngineTests.FrozenBatch_*`（母子先後／同機台不重複預約／已開工不被推遲／
  > 跨批不互相接力）與 `BomDepth_*`（缺父、環狀），加上
  > `ScheduleRunFrozenDispatchIntegrationTests.ScheduleRun_FrozenParent_DoesNotFinishBeforeItsComponent`。
  > ⚠️ 該整合測試的派工編號刻意讓**母件排在字母序前面**——否則 tie-break 會讓元件碰巧先排，
  > 就算 BOM 深度整個失效測試也會通過（第一版正是如此，突變驗證才抓出來）。
- 插單只需提高 `ProductionBatch.Priority`（CN_P），下次排程自動往前排

### 排程演算法

**9. CD_BOR=2（定量批量）語意**
前工序每產出 `QT_BOR` 數量後，後工序即可開始（例：容器裝滿後移轉）。
一次可移轉一個或多個容器，每次移轉量為固定批量（QT_BOR）。

**10. TM_WKS 欄位語意**
- `TM_WKS` = 正班工時（小時，非加班）
- `TM_WKO` = 加班工時（小時）
- 數值 24 是排程演算法中「每日最大工時上限」的計算常數，非表示常態全日生產

**11. 機台共用**
一台機台同時生產多個產品理論可行但非常態（例：注塑機開部分模穴生產不同產品）。
處理方式：BOM 中以不同工序或不同模具區分，各自建立 Dispatch。
排程引擎目前**不支援**同一機台同時段並行（單一時段只有一張 Dispatch），留作後期擴充。
> 化工、板材（玻璃等）生產模式**不在現階段範圍內**，未來可另行評估擴充方案。

### 即時 ETA

**12. ETA 重算範圍**
支援三層觸發策略（系統參數可設定）：

| 策略 | 觸發條件 | 重算範圍 |
|------|---------|---------|
| 局部（預設）| 報工寫入 | 本派工 + 同工單後續派工 + **同機台後續派工** + 上階工單派工 |
| 工作中心 | 同工作中心有新報工 | 該工作中心所有 Scheduled 派工 |
| 全量 | 人工觸發或跨班排程 | 全部未 Closed 派工 |

> **「同機台後續派工」的納入**：機台為共享資源，本派工延遲會推遲同機台上**其他工單**的後續派工，
> 純「同工單後續」覆蓋不到此連鎖。此處與 §7.5 影響範圍對齊（修正先前 §10.12 漏列「同機台後續」之不一致）。
>
> **滾動時間視窗（Rolling Horizon）**：局部重算僅平移**未來 48 小時內**（可系統參數調整）、節點數 `K ≤ 50`
> 的受影響派工，確保 ETA 運算 ~100ms 內完成；超出視窗者改由每日跨班「全量」策略校準（遠期排程會被現場隨機
> 干擾稀釋，即時平移投報率低）。

高頻報工時 Background Service 合併 1 分鐘內同一派工的多次觸發，避免重複計算。

**13. 工時校準（Phase J）**
依實際報工時間定期校準 `RoutingOperation` 設定工時
- 差異 ≥ 10% 時產生「工時調整建議」，由主管審核後更新 `RoutingOperation.TM_UNIT`
- **資料品質前置（必要）**：校準來源須先過濾被污染的報工——例如「忘記下工」導致 `ProductionSession` 時長異常灌水。報工模組需加超時異常標記（如時長 > 班別工時 N 倍），校準時剔除被標記列與統計極端值（如剔除上下 5% 或 IQR 外界），避免污染拉歪標準工時。

> **換線／整備時間（備忘，非現階段範圍）**：`TM_SETUP` 現為固定值（`RoutingOperation.cs`）。「序列相依整備」（Changeover Matrix，如白→黑 vs 黑→白 清機時間不同）屬**排程最佳化**範疇，會把現行貪婪優先序派工（§7）升級為含懲罰項的順序最佳化（類 TSP），且**需先建立「產品族群」概念**。維持 `TM_SETUP` 為基底值，未來如有足夠現場數據再以矩陣加權覆蓋——**勿在 Phase C 強推**。

### 手機 API

**14. 離線報工**
支援 PWA 離線佇列（詳見 [mobile-pwa.md](./mobile-pwa.md)）。
作業員按提交後寫入本地 IndexedDB → Service Worker 偵測網路恢復後自動依序送出。

**15. 多語**
手機 UI 支援中英文切換，語系設定可獨立選擇或沿用桌機端語系 cookie。

---

## 11. 下一步建議

§10 所有規格已確認，可直接進入實作。以下為分期實施計劃，包含技術準備、交付物與驗收標準。

### Sprint 0：技術準備（Phase A 前置）

| 項目 | 內容 |
|------|------|
| **EF Core 基礎** | `dotnet ef migrations add MFG_Init`；確保所有 MFG Entity 包含 `[Timestamp] byte[] RowVersion` 欄位（§ 6 並發控制） |
| **測試框架** | `xUnit` + `FluentAssertions` + `Testcontainers.PostgreSql` 建立；`dotnet test` 可獨立執行 |
| **API 文件** | § 8 API 端點寫成 Swagger/OpenAPI 定義，供 PWA 並行開發 |
| **CI 整備** | 確保 `dotnet test` 在 CI pipeline 中可執行 |

---

### Phase A：BAS 擴充（1-2 週）

**交付物**
- Entity：`Routing`、`RoutingOperation`（含 `UomCode` 欄位 — **本次新增，用於物料發料單位**）、`WorkCenter`、`WorkCalendar`、`Machine`
- Controller + View：新增/修改/刪除 CRUD 畫面
- 服務：`WorkCalendar.GetWorkHours(machineId, date)`（三層查詢邏輯 — § 4.1）

**驗收標準**
- Migration 執行成功；DB 新表出現
- `RoutingOperation.UomCode` 欄位存在，CRUD 畫面可新增/修改/刪除工序並設定發料單位
- `WorkCalendar.GetWorkHours` 單元測試通過（機台特殊工時 > 工作中心預設 > 全廠預設；查無設定時回傳 0）
- 整合測試通過（新增 3 層日曆後查詢結果正確）

---

### Phase B：MRP + 工單（2-3 週）

**交付物**
- Entity：`ProductionBatch`、`WorkOrder`（含 `Status` 包括新增 **InProgress** 狀態 — § 4.2）、`WorkOrderMaterial`（含 `UomCode`）、`Dispatch`（派工分解單）
- 服務：`RunMPS()`（遞迴展開 BOM、計算需求、生成工單，含 **倒推防呆邏輯** — § 5.1：DT_START < TODAY 時切換正推、標記 `PredictedLate`）
- Controller：`POST /api/mfg/run`（MRP 執行）、工單查詢

**驗收標準**
- 3 層 BOM 展開單元測試通過（工單層級與 ParentWorkOrderId 鏈正確）
- `RunMPS` 倒推防呆測試通過（過期日期自動轉正推、超期標記 PredictedLate）
- WorkOrder 狀態轉換測試通過（Planned → Released；投產時觸發 InProgress；全部派工完工後 Closed — § 4.2）
- 已 InProgress 工單拒絕重跑（HTTP 400 + 錯誤碼 `WO_IN_PROGRESS` — § 12.2）
- `WorkOrderMaterial` 內含 `UomCode`，發料計算單位正確

> **實作狀態（2026-05-27，Phase B 主體完成）**
> - 引擎：`MrpEngine`（RunMPS/RunJ/Fstock/倒推防呆/循環防呆/工序委外建議分流）+ `InventoryReservation`，單元測試綠。
> - 成本/委外欄位：`WorkOrder` 6 成本欄 + 4 委外欄、`WorkOrderMaterial.UnitCost`、`WorkCenter.MachineHourlyRate`、UnitCost 快照（見 §16.2 狀態註）。
> - **UI 三畫面已建**（皆遵循 new-screen 慣例、雙語、慣例測試綠）：
>   1. `MrpRunController` — MRP 執行單草稿/需求列編輯 + 「執行 MRP」展開 + 展開結果面板（採購/委外/缺料/預計延遲訊號）。
>   2. `WorkOrderController` — 工單查詢（狀態過濾）+ 唯讀明細（工序/用料/子工單/成本）+ **釋出（Release）**：Planned→Released、計算並快照 `Standard*` 成本（`WorkOrderCostCalculator`）。
>   3. `ProductionBatchController` — 生產批查詢 + 明細（子工單進度）+ 優先序調整（插單往前排，§10-8）。
>
> **Phase B 落地補強（2026-05-27，與 Fisher 對齊後）**
> - **①可行性預檢**：`MrpEngine.ValidateLineAsync` 展開前唯讀走訪途程/BOM，阻擋性錯誤（自製缺有效途程、途程無工序、用料品號不存在、用料缺單位換算、循環、超深）**逐列跳過、其餘照展**；錯誤回填 `MrpRunLine.ValidationError`、列入 `MrpRunResult.SkippedLines`，UI（_ExecuteResult/_Form）顯示。✅ 不阻擋：缺庫存→採購/委外、缺工作日曆（Phase C 才需）。
> - **②淨需求 + 訂單匯入**：需求**一律從訂單匯入**（MrpRun 表單**無手動新增需求列**，2026-05-28 回饋）。匯入來源＝已核准、未結案的**銷售訂單 + 內部訂單**明細（剩餘 > 0）；「已排程」＝已展開成批/工單，由 MFG **動態計算**剩餘 = 訂單量 − Σ(該訂單明細在已執行 MRP 單的需求量，排除作廢批)。勾選 + 輸入本次排程量加入需求列；訂單號寫入 `MrpRunLine.SalesOrderId`（沿用為「來源訂單號」，SO/NO 前綴自辨型別）、`SalesOrderLineSeq` 為明細序。⚠️ **匯入 modal 必須移至 `document.body` 再 show**（`data-moved-modal`），否則巢狀於 offcanvas 內會 backdrop 灰屏凍結。
>   - **內部訂單發料併入統一倉庫作業（2026-05-28 回饋＋inventory.md 對齊）**：內部訂單入口**移除倉庫/儲位/批號輸入**（純需求，缺料走 MRP）。內部訂單採**全模式**：核准（Save/Submit→Sync/Confirm）→ `DocStatus.Confirmed`，**不在訂單過帳**（移除 simplified `PostDocSimplifiedAsync`/`BuildSimplifiedRequest`）。發料統一於**庫存管理 → 倉庫作業**完成：`WarehouseApiController` 新增 `InternalOrderOutTypeCodes={"NO"}` + pending-outbound 區塊 + `PickInternalOrder`（鏡像 `PickSampleDoc`：選儲位/批號 → `PostLineDetailAsync` 扣庫存 → 回寫 QtyPutAway → 全領完轉 `Posted`）。新增 `InternalOrderLocationDetail`（mirror `SampleDocLocationDetail`）+ migration。`WarehouseOps` Index UI 為 data-driven，無需修改。⚠️ 寫 PG `timestamptz` 的 DateTime 一律 `SpecifyKind(Utc)`（MrpRunLine.RequiredDate 與 ctx.Today 已修）。
> - **③採購需求落地**：執行後（同交易內）依採購需求自動建 `PurchaseRequest`（同品號合併、Remarks=MRP單號），沿用既有「請購轉採購」批次作業（PR→PO 已支援 `OrderedQty/IsClosed/PurchaseOrderLine.PurchaseRequestId+PrLineNo` 部分轉/併單/拆單）。
> - **④委外需求落地（轉委外）**：新增 `OutsourcePurchaseOrder`/`OutsourcePurchaseOrderLine` 實體（§19）。`OutsourcePurchaseOrderController`：委外採購單查詢/明細 + **轉委外清單**（`Dispatch.IsOutsource=true 且尚無對應委外採購單行` 衍生，整品/工序統一以派工為單位）→ 勾選 + 指定廠商 **併單** 建委外採購單（收回品明細；**送外料於明細頁由來源派工 `DispatchMaterial` 衍生顯示**，不另存表）。整品委外回填 `WorkOrder.OutsourcePurchaseOrderId/SupplierCode/ExpectedDeliveryDate`。狀態 Draft→Confirmed→Void；採番 DocType `OutsourcePurchaseOrder`(OP)。**不接線上簽核**；委外領發料/收貨/IQC/完工入庫屬 Phase F。
> - **⑤工單釋出簽核（§18.2，已完成）**：`WorkOrder` 加 `ApprovalStatus`/`ApprovalRequestId`。`Release` 算 Standard* 後依「WorkOrder」`DocumentApprovalConfig` → 需簽核則 `SubmitForApproval`（PendingApproval）、否則 Approved；`Withdraw` 撤回退回 Planned；`WorkOrderApprovalSync : IApprovalSyncContributor`（核准/退回即時回寫）+ Details sync-on-read；Index/Details badge、`_DocDetailsUrls` 加 WorkOrder。✅ **2026-08-30 更正**：金額門檻已實作，落點是廠別參數 `mfg_wo_approval_threshold`（`plant-scoped-system-params.md` §7.2 於 2026-08-17 裁決＝方案 a，並在該處明確**否決**「`DocumentApprovalConfig` 增門檻欄位」的方案 b）。本行原本寫「屬未來擴充、需增欄位」是與該裁決不同步的舊敘述。語意＝§18.2 的**快速通道**：有組態才要簽，標準成本 ≤ 門檻者自動核准；無範本一律直接核准。§18.2 其餘三條觸發（工期延期、委外、重工）**2026-10-20 已實作**為廠別參數 `mfg_wo_approval_on_delay`／`_outsource`／`_rework`（有流程時符合者一律送審、不走快速通道）；有門檻或條件成立卻無流程時直接核准並回 `ReleasedNoApprovalFlow` 警示。
> - **⑥MRP 重跑（§10-5，已完成）**：`MrpEngine.ReRunAsync`＝清除前次展開（釋放預留 `ReleaseBySourceAsync` + 刪批/工單/工序/用料/派工）後重新展開。**任一工單已投產（InProgress/Completed/Closed）即拒絕**，拋 `MrpReRunBlockedException`（錯誤碼 `WO_IN_PROGRESS`，§12.2）。「保留鎖定批、僅重建未投產」之增量重跑（§10-1 評估方向，需 Phase D 投產 + 預留按列粒度釋放）列為未來精修。MrpRun Index「重跑」鈕（Executed）→ 回 `_ExecuteResult` 面板。
> - **尚缺**（2026-07-03 依程式實況校正——原列三項已完成兩項）：~~`Actual*` 成本累計（Phase D）~~ **已實作**（`DispatchController` session/setup 關閉時累加人工/製費＋`ProductionCompletionService` 後沖累加材料，見 §16.3）；~~委外領發料/收貨入庫（§19 Stage 4-6，Phase F）~~ **已實作**（INV `OutsourceMaterialController` 發退料過帳＋PUR `OutsourceReceiptController.Confirm` 收貨入庫、超耗扣款接 AP，另有 `OutsourceProgress` 進度看板）；**仍缺：機台自動拆批 v2/v3**。**Phase B 本體至此完成**。下一大塊＝**Phase C 排程引擎**（機台選最早可用、人工拆派、甘特畫實際時段）。

---

### Phase C：排程引擎（2-3 週）

**交付物**
- 服務：`SchedulingEngine`（含 EDD、SPT、FIFO、LIFO 四種排序規則）、`AllocateDispatchSlot()`（派工槽位分配 — § 7.3）
- 服務：`LoadCalendarContextAsync()` 批次預載 WKC + `WorkCalendarSegment` 至記憶體查表（§7.1，避免 N+1）；working-time walker 沿可用時段步進（§9 Phase A 產能模型）
- 甘特圖 UI（使用 `timelines-chart.min.js`；詳細 JSON 格式與産能列規則見 §8.5）
  - 每機台雙列：**派工列**（`label = 機台ID`，色鍵 = `val = ID_PLN` 生產批號）+ **産能列**（`label = 機台ID + "産能"`，`val = "正班"` / `"加班"` 不同色，供產能使用率目視評估）
  - 桌機版端點：`GET /api/mfg/schedule?dateTo={date}&plantId={id}`

**驗收標準**
- 排序規則單元測試通過（5 張派工按各規則排序結果符合預期）
- `AllocateDispatchSlot` 單元測試通過（CD_BOR=1/2/3 委外各一案例；委外無機台情境）
- working-time walker 單元測試通過（跨午休、多班別班距、週末的「淨工時→真實時刻」換算正確）
- `LoadCalendarContextAsync` 預載後排程不再逐格查 DB（以查詢計數驗證無 N+1）
- 甘特圖可顯示派工在機台/時段的分配位置；産能列與派工列並排，正班/加班色塊正確對應 `WorkCalendar`
- 如需先行驗證，可抽出此 Phase 的純函式先做**單元測試**（不接 DB、不寫 UI），確認邏輯後再整合

---

### Phase D：上工報工（Web 桌機版）（2-3 週）

**交付物**
- Entity：`ProductionSession`（開工紀錄）、`ProductionReport`（報工紀錄，含 `[Timestamp] RowVersion` — § 6）、`ProductionSetup`（Setup 完成回報）
- Controller：開工、報工、Setup 回報畫面及 API
- 並發衝突處理：DbUpdateConcurrencyException 捕捉、409 回應（§ 6）

**驗收標準**
- 開工→報工→完工整個流程可操作，派工轉換為 InProgress → Closed
- 超量報工拒絕（回 HTTP 400 + 明確錯誤訊息）
- **並發衝突測試通過**：同時編輯同一派工報工時，後編輯者收到 409 + 衝突提示，需重新載入後編輯（§ 12.2）
- Setup 回報記錄實際備模/調機時間供後續達成率分析

---

### Phase E：手機 API + ETA（與 Phase D 並行，3-4 週）

**前置條件**
- 後端 Phase D（桌機版報工）完成後，Phase E 前端啟動
- 後端 `POST /api/mobile/*` API 契約（Swagger/OpenAPI）確認後，Phase E 前端可立即並行開發

**交付物**
- 後端 API 路由：`GET /api/mobile/dispatches`、`POST /api/mobile/sessions`、`POST /api/mobile/reports`、`POST /api/mobile/machines/{id}/status-event`（機台狀態異動 — § 13）
- 服務：`EtaRecalculator`（報工後 ETA 動態重算 — § 7.4）
- 前端詳細規劃見 [mobile-pwa.md § 9](./mobile-pwa.md)

**驗收標準**
- 後端 `/api/mobile/*` 鑑權、限流、廠別隔離測試通過（§ 12.2）
- 機台狀態異動測試通過（DI 故障 → Scheduled 派工退回 Pending；CI 恢復 → 無副作用）
- ETA 重算測試通過（報工後同派工 + 後續派工時間正確平移）
- 客戶端去重測試通過（同一 clientId 第二次 POST 回傳第一次結果）

---

### Phase F：完工入庫（INV 模組並行）（1-2 週）

**交付物**
- Entity：INV 模組 `Warehouse`、`InventoryMovement` Entity（由 INV 模組負責）
- Controller：完工入庫、庫存異動畫面
- 整合：WorkOrder.Closed → 自動或手動建立 InventoryMovement

**驗收標準**
- 完工派工入庫流程可操作，庫存增加正確
- INV 模組測試通過

---

## 分工與並行度

| 時段 | 活動 |
|------|------|
| **Week 1-2** | Sprint 0 技術準備 + Phase A 開始 |
| **Week 3-4** | Phase A 完成，Phase B 開始 |
| **Week 5-7** | Phase B + Phase C 併行（排程可抽純函式先測） |
| **Week 8-10** | Phase D（桌機版報工）+ Phase E（手機 API） 併行 |
| **Week 11-12** | Phase F（完工入庫）+ Phase E 前端最後階段 |

**總估算**：12-14 週（Phase A-F 依序推進，D-E 併行可提升效率）

---

## 12. 自動測試規劃

### 12.1 單元測試（xUnit，純函式，不需 DB）

| 測試對象 | 典型測試案例 |
|---------|------------|
| `RunMPS` 遞迴展開 | 3 層 BOM 展開後工單數量、ParentWorkOrderId 鏈正確；BOM 超過 20 層時拋警告 |
| `Fstock` 可用庫存 | 有 Reservation 時正確扣減；跨日期計算；庫存恰好為零時的邊界 |
| `AllocateDispatchSlot`（§7.3 純函式）| CD_BOR=1/2/3 各一個案例的 timeS/timeE；委外無機台情境 |
| `WorkCalendar.GetWorkHours`（三層查詢）| 機台覆蓋 > 工作中心 > 全廠；查無任何設定時回傳 0 |
| `EtaRecalculator`（局部重算）| 報工後本派工 + 後續派工時間平移計算正確 |
| 排序規則 `EDD / SPT / FIFO / LIFO` | 5 張派工依各規則排序結果驗證 |

工具：`xUnit` + `FluentAssertions`

### 12.2 整合測試（TestServer + 測試 DB）

| 測試對象 | 驗證點 |
|---------|--------|
| MRP 全流程 | `POST /api/mfg/run` → WorkOrder / Dispatch 數量、狀態 Planned |
| Dispatch 狀態機 | Pending → Scheduled → SetupInProgress → InProgress → Closed 各步轉換 |
| `POST /api/mobile/sessions` | 開工後 Dispatch.Status = InProgress；重複 clientId 去重 |
| `POST /api/mobile/reports` | QtyDone 累計；達到 QtyTotal 後自動 Closed |
| `clientId` 去重 | 同一 clientId 第二次 POST 回傳第一次結果，DB 不重複寫入 |
| 已 InProgress 工單拒絕 MRP 重跑 | HTTP 400 + 明確錯誤碼 `WO_IN_PROGRESS` |
| InventoryReservation 提早投產重算 | 投產觸發後同物料後續需求的 Reservation 正確重建 |

工具：`Microsoft.AspNetCore.Mvc.Testing` + `Testcontainers.PostgreSql`

| 測試對象 | 驗證點 |
|---------|--------|
| 工單釋出後標準成本 | `WorkOrder.StandardMaterialCost` = Σ (WorkOrderMaterial.Qty × UnitCost)；`StandardLaborCost` = TM_UNIT × LaborHourlyRate |
| ProductionSession 關閉 | `WorkOrder.ActualLaborCost` += 實際工時 × LaborHourlyRate；`ActualOverheadCost` += 實際工時 × MachineHourlyRate |
| UnitCost 快照 | `WorkOrderMaterial.UnitCost` 等於建立時 `Product.StandardCost`；標準成本事後變動不影響已建工單 |

### 12.3 E2E 桌機 UI 測試（Playwright，套用 webapp-testing.md 規格）

| 畫面 | 測試情境 |
|------|---------|
| Routing 主檔 | 新增途程 + 工序（含 TM_SETUP / TM_UNIT）+ BOM；刪除有工單的途程應拒絕 |
| WorkCalendar | 設定全廠日曆；覆蓋單一機台特殊工時；查詢結果依三層優先序顯示 |
| MRP 執行 | 選訂單列 → 執行 → 查看生產批結果；重跑時已投產工單不受影響 |
| WorkOrder 查詢 | 搜尋、查看工序、釋出工單（Planned → Released）|
| Dispatch 甘特圖 | 執行排程 → 派工出現在正確機台/時段；切換排序規則後結果變化 |
| 報工桌機版 | 新增 ProductionReport → QtyDone 累計顯示；超量報工應拒絕 |

工具：`Playwright + xUnit`（`SmartFactory.MES.UITests`，同 webapp-testing.md 規格）

### 12.4 測試資料策略

- **共用 Fixture**：每個 xUnit Collection 建立最小必要主檔（1 WorkCenter + 1 Machine + 1 Routing + 1 Product）
- **BOM 深度**：各測試只建立所需深度，不共用全域 BOM Tree
- **隔離**：每個整合測試用獨立 `ProductionBatchId` 前綴（如 `TEST-{Guid}`），避免交叉污染
- **排程測試**：使用固定時間戳（`IDateTimeService` mock），確保時段計算結果可預期

---

## 13. 機台狀態管理（對應 GTR01）

> 來源：逆向工程 GTR01.aspx（機台異動）。此功能原未列入規格，補充如下。

### 13.1 機台狀態定義

`Machine.CurrentStatus` 紀錄當前狀態，影響排程可否分派：

| 狀態代碼 | 中文 | 英文 | 可排程 |
|---------|------|------|--------|
| A | 正常 | `Available` | ✅ |
| B | 待機 | `Standby` | ❌（預設，可系統參數設定為可排程）|
| C | 外修中 | `ExternalRepair` | ❌ |
| D | 故障停機 | `BreakDown` | ❌ |
| E | 改良/改裝 | `Modification` | ❌ |
| F | 外借 | `Loaned` | ❌ |
| G | 廢棄 | `Retired` | ❌（永久，不再出現在選單）|
| H | 保養中 | `Maintenance` | ❌ |

### 13.2 Entity 設計

**`MachineStatusOrder`**（機台異動單，Master）
```
MachineStatusOrder
├── OrderId (PK, 自動編號 NO_GTR)
├── EventType (CD_MTR): CI/CO/DI/DO/EO/FI/FO/GO/HI/HO/II
├── EventAt (DT_TRAN): 異動日期時間
├── RequestedBy (ID_APPLY): 申請者
└── Note (RM)
```

**`MachineStatusOrderLine`**（機台異動明細，Detail）
```
MachineStatusOrderLine
├── LineId (PK)
├── OrderId (FK → MachineStatusOrder)
├── MachineId (FK → Machine, 或 ToolingId → Tooling)
├── TargetStatus: 切換後的目標狀態
├── SourceDocId: 來源單號（保養工單、維修單等，非必填）
└── Note
```

> 機台（Machine）與模具/夾治具（Tooling）均可建立異動紀錄。

**`PlannedDowntime`**（計劃停機，影響排程）
```
PlannedDowntime
├── DowntimeId (PK)
├── MachineId (FK → Machine)
├── PlannedFrom: 計劃開始時間
├── PlannedTo: 計劃結束時間
├── Reason: 保養/換模/改裝等
└── MachineStatusOrderId: 連結對應的異動單（選填）
```

### 13.3 狀態轉換規則（對應 CD_MTR）

| 事件代碼 | 中文 | 來源狀態 | 目標狀態 |
|---------|------|---------|---------|
| CI | 恢復正常 | 任何 | Available |
| II | 設為待機 | Available | Standby |
| DI | 故障停機 | Available / Standby | BreakDown |
| DO | 故障排除 | BreakDown | Available / Standby |
| HI | 開始保養 | 非 Retired | Maintenance |
| HO | 保養完成 | Maintenance | Available / Standby |
| EO | 進入改良 | 非 Retired | Modification |
| FO | 外借出廠 | 非 Retired | Loaned |
| FI | 外借回廠 | Loaned | Available / Standby |
| GO | 除役廢棄 | 任何 | Retired |
| CO | 送出外修 | 非 Retired | ExternalRepair |

### 13.4 對排程的影響

**即時影響**：
- 當 `MachineStatusOrderLine` 寫入時（TargetStatus ≠ Available），系統自動：
  1. 將 `Machine.CurrentStatus` 更新為 TargetStatus
  2. 若該機台有 `Scheduled` 派工 → 自動退回 `Pending`，等待下次排程重排
  3. 若該機台有 `SetupInProgress`／`ReadyToStart`／`PendingFPI`／`InProgress` 派工 → 轉 `OnHold`（HoldReason=MachineDown）
  4. TargetStatus 為 Available／Standby → 該機台 MachineDown 暫停的派工自動恢復
  > 桌機 MachineStatusOrderController、手機 MachineStatusEvent、模具保養到期（ToolingShotService）共用
  > `DispatchLifecycleService`／`DispatchHold.ApplyMachineDownAsync`。工作中心主檔不得改既有機台狀態（唯讀），
  > 有未結束派工的機台不得刪除。

**計劃停機（`PlannedDowntime`）影響**：
- 排程引擎讀取 `PlannedDowntime` 時，扣除該機台在對應時段的工時（視同零可用時數）
- 可事先建立計劃停機（如定期保養），讓排程提前反映

### 13.5 手機端支援

機台狀態異動可由手機操作（補充至 mobile-pwa.md §7 或 §8.1）：
- 作業員發現故障 → 手機掃機台 QR → 選「故障回報」→ 建立 `MachineStatusOrder`（DI）
- 維修完成 → 選「恢復正常」→ 建立 `MachineStatusOrder`（DO 或 CI）
- 保養開始/完成 → HI / HO

> API 端點建議：`POST /api/mobile/machines/{id}/status-event`
> Body: `{ eventType, note, sourceDocId?, clientId, clientTimestamp }`

### 13.6 自動測試補充（對應 §12）

| 測試對象 | 驗證點 |
|---------|--------|
| MachineStatusOrder 寫入 | Machine.CurrentStatus 正確更新 |
| 機台 BreakDown 時 | Scheduled 派工退回 Pending；InProgress 派工轉 OnHold |
| 排程引擎 | 非 Available 機台不被分派派工 |
| PlannedDowntime 期間 | 排程演算法的可用工時扣除正確 |
| 手機故障回報 API | POST 成功後 Machine 狀態變更 |


---

## 14. 已確認補充規格

### 14.1 Mold 與 Tooling：統一為 BAS.Tooling

**決策**：不另建 `Mold` Entity，MFG 直接引用現有 `BAS.Tooling`。BAS.Tooling 尚未被使用，趁此機會擴充欄位。

**BAS.Tooling 擴充欄位**：
| 欄位 | 說明 |
|------|------|
| `ToolingType` | `Mold`（模具）/ `Fixture`（夾治具）/ `Other` |
| `CavityCount` | 模具穴數（Mold 適用；Fixture 為 null）|
| `ProductsPerCavity` | 每穴產品組合（多品項一模時使用）|
| `AccumulatedShots` | 累計打次（每次報工後回寫，= Σ (QtyOk+QtyNg) / CavityCount）|
| `PmIntervalShots` | 保養間隔打次（未來擴充用，目前可為 null）|

**MFG 使用規則**：
- Setup 回報（`ProductionSetup`）時記錄 `ToolingId`（使用哪個模具或夾治具）
- 生產報工（`ProductionReport`）時，`QtyOk` + `QtyNg` 為**實際產品數量**（不乘穴數，作業員如實回報看到的數量）
- 模數（打次）回寫：`ShotCount = (QtyOk + QtyNg) / CavityCount`，寫入 `Tooling.AccumulatedShots`，作為後續保養門檻判斷依據
- 第一階段：只做回報（記錄累計打次），不做管控
- 第二階段：啟用 `PmIntervalShots` 保養排程（見下方）—— ✅ 已實作（2026-07-16）：
  `ToolingShotService`（桌機/手機報工共用，與報工同交易）；欄位 `AccumulatedShots`/`PmIntervalShots`/`ProductsPerCavity`
  ＋ migration `BAS_Tooling_ShotMaintenance`；派工清單/報工面板/模具主檔「需保養」警示；
  單元測試 `ToolingShotMaintenanceTests`（10 案）

**模具保養排程（`PmIntervalShots` 啟用後）**：

| 步驟 | 動作 | 說明 |
|------|------|------|
| 觸發 | `AccumulatedShots >= PmIntervalShots` | 每次報工回寫時檢查；**只在跨越門檻那一次觸發**（已逾期未保養者不重複開單，警示持續顯示） |
| 警示 | 在派工/報工畫面標示「模具需保養」| 派工清單狀態欄 badge＋報工面板 alert＋模具主檔清單 badge |
| 排程 | 建立 `MachineStatusOrder`（CD_MTR = HI / Maintenance）| 自動建單（Line.SourceDocId = ToolingCode），機台轉 Maintenance 並套 §13.4 副作用；觸發報工若同時達量 Closed 不受影響；委外/無機台派工僅累計警示不建單 |
| 執行 | 完成保養後，工程師將 MachineStatusOrder 關閉（HO 事件）| 機台回到 Available；HO 的 SourceDocId 需填模具代號 |
| 重置 | 關閉後將 `Tooling.AccumulatedShots` 歸零 | HO＋SourceDocId=模具代號 → 歸零（桌機/手機兩路徑皆支援），開始下一個保養週期 |

> 夾治具（`Fixture`）通常以「點檢表」管控，不做打次累計，`CavityCount` 與 `PmIntervalShots` 均為 null 即可。

---

### 14.2 退工/重工流程

**不良品處理方式**：

| 情況 | 處理 | 單據 |
|------|------|------|
| 生產線當下不良 | 直接報廢，計入 `ProductionReport.QtyNg` | 無需單據（等同生產損耗）|
| 重工（需特定製程再加工）| 手工建立重工工單 | 新建 `WorkOrder`，`IsRework = true`，`ReworkFromWorkOrderId` 連結原工單 |

**重工工單規格**：
- `IsRework = true`：標識為重工工單，排程優先序、成本可分開統計
- `ReworkFromWorkOrderId`：連結原始工單，方便追蹤品質歷程
- 只需建立需要重做的製程（部分工序），不需複製整張途程
- 重工完工後入庫流程與一般工單相同

> **業界常見做法補充**：部分系統以「不合格品單（NCR）」串接原工單與重工工單，提供完整品質追蹤鏈。若未來有品質管理需求（如 ISO 9001 的不合格品控制程序），可在 NCR 上記錄原因分析（8D）、暫時/永久措施，並連結 `WorkOrder.ReworkFromWorkOrderId`。現階段以最簡單的連結欄位實作即可。

> ⚠️ **P2**：重工料件來源需明確定義：不良品應移至「隔離倉」（獨立倉別代碼），重工工單領料從隔離倉出，與正常在製品倉區隔。現行 §17 的 MaterialSlip 框架中，ReworkIssue/ReworkReturn 分類已預留；但隔離倉的倉別代碼及「良品 vs 報廢 vs 重工」分流判斷，需在 Phase G（NCR 實作前）確認業務規則，否則重工成本計算易混入正常製造成本。

---

### 14.3 ProductionBatch 建立為獨立 Entity

**問題背景**：目前 §2 的「生產批號」只是存在 `MrpRunLine` 裡的一個 ID 值（`ProductionBatchId`），並非獨立 Entity。

**建立獨立 Entity 的理由**：
一個生產批包含多張 WorkOrder（BOM 各階）與多張 Dispatch，完成需要一段時間。若只靠 ID 連結，以下需求難以處理：
- **批次層級的狀態查詢**：「這筆訂單的生產進行到哪裡？」需 JOIN 多張表才能算出
- **優先序管理**：插單調整時以生產批為單位調整 CN_P，需要一個表存這個值
- **預計完工日**：MRP 展開的最晚完成日存在 `MrpRunLine`，但隨排程執行後的動態 ETA 需要儲存

**設計決策**：建立輕量級 `ProductionBatch` Entity：

```
ProductionBatch
├── BatchId (PK, = 原 ID_PLN)
├── MrpRunLineId (FK → MrpRunLine)
├── ProductId (FK → Product)
├── Quantity
├── Priority (CN_P，可調整)
├── PlannedEndDate (MRP 倒推日期)
├── ExpectedEndDate (排程後動態 ETA，定期更新)
└── Status: Planned | InProgress | Completed | Void
    (從子 WorkOrder 狀態衍生，排程執行後更新)
```

`MrpRunLine` 改為純 MRP 計算結果紀錄（QT_Q / QT_PO / QT_WO 等），不再承擔批次追蹤責任。

---

## 15. 品質管理規劃（對應 QCR 模組）

> **實作狀態（2026-06-09 更新）**：
> - §15.2（製程不良回報）：✅ 已實作（Phase D）
> - §15.3（IPQC / QCR module）：✅ 已實作（Phase G-H，2026-06-05）—— InspectionSpec/Order/Item 實體、MilStd1916Service、SamplingPlanService、FQC 閘口。
> - §15.4（NCR）：✅ 已實作（2026-07-16 複查確認）—— `NcrController` 已完整實作 Disposition（Scrap→ScrapDoc、Rework→WorkOrder(Type=Rework)、Return→委外/採購退貨），詳見 §15.4 內文。

### 15.1 原系統 QCR 模組盤點

| 程式 | 資料表 | 功能 | 是否納入 MFG 範圍 |
|------|--------|------|------------------|
| QNI01 | QNI | 不良項目主檔（FG_M=Y 為製程不良） | ✅ 引用（NgReasonCode FK）|
| QNO01 | QNO | 檢驗規範主檔 | ✅ 已納入（`InspectionSpec`，Phase G）|
| QIT01 | QIT | 檢驗項目（含儀器對應） | ✅ 已納入（`InspectionSpecItem`，Phase G）|
| QIN01 | QIN | 檢驗儀器主檔 | ⬜ 儀器管理獨立維護 |
| QIV01 | QIV | 儀器校正紀錄 | ⬜ 儀器管理獨立維護 |
| QIP01 | QIP | 年度校正計劃 | ⬜ 儀器管理獨立維護 |
| QTR01 | QTR | 儀器異動單 | ⬜ 儀器管理獨立維護 |
| QCM01 | QCM | AQL 抽樣表（加嚴/正常/減量）| ⬜ 不在 MFG 範圍——**已實作於 QCR** |
| QRM01 | QRM | RQL 抽樣表 | ⬜ 不在 MFG 範圍——**已實作於 QCR** |
| QIQ01 | QIQ | 進料/成品檢驗記錄 | ⬜ 不在 MFG 範圍——**已實作於 QCR**（非 PUR）|

> ✅ 第一階段已納入 ｜ 🔵 第二階段規劃 ｜ ⬜ 不在 MFG 範圍

> ⚠️ **最右欄問的是「是否納入 **MFG** 範圍」，不是「系統有沒有做」**——⬜ 代表「不歸 MFG 管」，
> 不代表「沒有這個功能」。2026-08-21 更正三列（原本前兩列標 🔵 未來、第三列標「屬 PUR 模組」）：
> **抽樣分級是現行規則、已實作**——`Modules/QCR/Services/IqcInspectionService.cs` 有抽樣計畫的
> VL 切換、跳批與 Ac=0 判定，桌機（`QCR/InspectionOrder`）與手機（`api/mobile/inv/iqc`）共用同一套；
> 批量與建議抽樣數一律伺服端重算。IQC 檢驗紀錄的歸屬也是 **QCR 不是 PUR**（它只是連結進貨單）。

---

### 15.2 第一階段：製程不良回報（已納入 ProductionReport）

製程不良品的記錄已整合在報工流程中：

**`ProductionReport` 新增欄位**：

| 欄位 | 說明 |
|------|------|
| `QtyNg` | 不良品數量（作業員如實回報） |
| `NgReasonCode` | 不良原因代碼（FK → `QNI.ID_QNI`，`FG_M = 'Y'`）|

**業務規則**：
- 不良原因下拉選單只顯示 `QNI` 中 `FG_M = 'Y'` 的項目（製程不良類型）
- `QtyNg > 0` 時 `NgReasonCode` 為必填
- 報工後累計值反映在工單完成率：`WorkOrder.QtyComplete = Σ QtyOk`，`WorkOrder.QtyScrap = Σ QtyNg`

**不良品處置**（對應 §14.2）：
- 生產線當下報廢：計入 `QtyNg`，無需額外單據
- 需重工：建立重工工單（`IsRework = true`），詳見 §14.2

---

### 15.3 第二階段：製程品質檢驗（IPQC）

**觸發情境**：某些製程（或產品規格）要求在生產過程中進行抽樣檢驗，而非完工後才檢驗。

> **實作狀態（Phase G-H）**：已實作（2026-06-05）。QCR module（InspectionSpecController、InspectionOrderController）、MilStd1916Service、SamplingPlanService、FQC 閘口（ProductionCompletionController）均已上線。詳見 §15.5 切片表。

#### 15.3.1 核心業務規則
1. **抽樣標準**：採用 MIL-STD-1916（Ac=0 允收計劃）。應抽樣數由 `MilStd1916Service` 根據批量（`LotSize = Dispatch.QtyTotal`）及驗證等級（VL-I～VL-VII）動態查表。
2. **狀態轉移機制**：`SamplingPlanService` 追蹤連續合格批數，動態切換 `Normal`／`Tightened`／`Reduced`／`SkipLot`；切換邊界依 `InspectionSpec.SamplingScope`（SpecLevel／MachineLevel／ToolingLevel）決定計數鍵。
3. **異常中斷副作用**：IPQC 結果 `Fail`（`FailQty ≥ 1`）時，系統自動將對應 `Dispatch` 變更為 `OnHold`，手機端顯示警示防止不良擴大。

#### 15.3.2 實體資料模型（Phase G-H，已實作）

```
InspectionSpec（檢驗規格主檔，對應 QNO）
├── SpecId (PK = QNO.ID_QNO)
├── SpecCode / SpecName
├── PlantId (FK)
├── ProductCode (FK → Product, nullable — null 表示通用規格)
├── SupplierCode (FK, nullable)
├── VerificationLevel (現行 VL, 預設 4 = VL-IV)
├── SamplingType (Attribute / Variable)
├── TriggerMode (Always / Periodic / OnDemand)
├── PeriodHours (定期觸發間隔)
├── RequiresFQC (完工入庫前強制檢驗)
├── RequiresRQC (銷退回廠強制檢驗)
├── SamplingScope: SpecLevel | MachineLevel | ToolingLevel
│     SpecLevel    -- 跨機台共用 ConsecutivePasses 計數器
│     MachineLevel -- 以 (SpecId, MachineId) 為邊界各自計數
│     ToolingLevel -- 以 (SpecId, ToolingId) 為邊界（模具敏感型產品）
├── NormalVl / TightenedVl / ReducedVl
├── PassesToReduce / PassesToNormal
├── SkipLotEnabled / SkipLotFrequency / PassesToSkipLot
├── SamplingState (Normal / Tightened / Reduced / SkipLot)
├── ConsecutivePasses
└── LotCounter (跳批計數器)

InspectionSpecItem（檢驗規格項目，對應 QIT）
├── SpecItemId (PK)
├── SpecId (FK → InspectionSpec)
├── SeqNo
├── ItemName / UomCode
├── LowerSpec / TargetValue / UpperSpec (decimal?)
└── TargetStdDev decimal?  -- 製程 σ，Phase H 3-sigma 輸入防錯使用；null 時只比對上下限

InspectionOrder（檢驗單）
├── InspectionOrderId (PK)
├── PlantId (FK)
├── InspectionType (IQC / IPQC / FQC / RQC)
├── OrderType (FPI / IPQC / FQC — 首件 / 製程巡檢 / 完工)
├── SourceDocId (IQC → GoodsReceiptId；IPQC/FQC → DispatchId)
├── SourceLineNo
├── ProductCode / LotSize / SampleSize / VerificationLevel
├── ActualSampleQty / FailQty
├── Result (Pass / Fail / Pending / Conditional)
├── ActiveMaterialLots string?  -- 批號快照 JSON；Phase F 領料後改系統自動填入
├── InspectorId (FK → User) / InspectedAt / Remarks
└── Items (→ InspectionItem 一對多)

InspectionItem（量測明細）
├── ItemId (PK) / InspectionOrderId (FK)
├── SeqNo / ItemName / UomCode
├── LowerSpec / TargetValue / UpperSpec (規格快照)
├── MeasuredValue decimal?
└── IsConforming bool
```

**觸發規則**：
- `RoutingOperation.RequiresFpi = true`：Setup 完成後 Dispatch 轉 `PendingFPI`，FPI 型 InspectionOrder Pass 後才解鎖開工。
- `RoutingOperation.RequiresInspection = true`：Dispatch 轉 InProgress 後自動建立 IPQC 型 InspectionOrder。
- 手機端顯示「待檢驗」清單，品管人員掃碼報告結果。

> ⚠️ **P3**：IPQC LotSize 定義為 `Dispatch.QtyTotal`；同一派工單可能跨批次，若有需要可再細分為子批，待 Phase G 確認需求。

---

### 15.4 第三階段：不合格品單（NCR）

**觸發情境**：重大不良（如整批報廢、客訴、ISO 8D 要求），需要正式文件追蹤原因分析與改善行動。

**規劃方向**（需求確認後實作）：

```
NonConformanceReport（不合格品單）
├── NcrId (PK)
├── SourceType: ProductionReport | InspectionOrder | GoodsReceipt
├── SourceId                     -- 來源單號
├── ProductId (FK → Product)
├── Qty
├── DefectCode (FK → QNI.ID_QNI)
├── Disposition: Scrap | Rework | UseAsIs | Return
├── ReworkWorkOrderId (FK → WorkOrder, nullable)  -- 若開重工工單
├── RootCause                    -- 根本原因說明
├── CorrectiveAction             -- 矯正措施
└── ClosedAt
```

**與現有系統的關聯**：
- `QNI.ID_QNI`：不良代碼共用（已有 FG_M/P/S 分類）
- 重工工單：`WorkOrder.ReworkFromWorkOrderId` 連結（§14.2）
- 簽核流程：可接入 Core `IApprovalService`（NCR 需主管審核）

> ✅ 已實作（2026-07-16 複查）：`NcrController` 已提供 NCR 建單與 Disposition 處置（Scrap/Rework/Return），與 `ProductionReport.QtyNg + NgReasonCode` 並存。8D 流程分析欄位未見額外實作，若需要仍可視為後續擴充。

---

### 15.5 品質管理切片建議

| Phase | 內容 | 前置條件 |
|-------|------|---------|
| **已完成（Phase D）** | ProductionReport.QtyNg + NgReasonCode | QNI 主檔存在 |
| **已完成（Phase G）** | QNI 代碼維護、`InspectionSpec`/`InspectionSpecItem` 主檔（含 SamplingScope、TargetStdDev）、`RoutingOperation.RequiresFpi/RequiresInspection`、`DispatchStatus.PendingFPI`、`InspectionOrder` 基礎實體 | Phase D 完成 | 2026-06-05 |
| **已完成（Phase H）** | FPI 閘口狀態機、IPQC/FQC InspectionOrder 建立/執行/報告、手機端檢驗清單、3-sigma 輸入防錯、SamplingState 切換（MilStd1916Service + SamplingPlanService）、FQC 入庫閘口 | Phase G + Phase E（手機）| 2026-06-05 |
| **已完成（Phase I）**（NCR）| 不合格品單 + Disposition 處置（Scrap/Rework/Return）；利用 `ActiveMaterialLots` 進行交叉分析 | Phase H | 2026-07-16 複查確認 |

---

## 16. 生產成本規劃

> 生產成本資料具有時效性：標準成本異動、費率調整後，若未在交易當下快照，事後無法還原成本基礎。
> 本節規劃**嵌入主幹 Phase A-D 的最小必要欄位**，讓未來成本模組可直接使用，無需回溯重建。

### 16.1 成本結構

```
生產成本（WorkOrder 層級）
├── 直接材料成本 = Σ (WorkOrderMaterial.Qty × WorkOrderMaterial.UnitCost)
├── 直接人工成本 = Σ (ProductionSession.實際工時 × WorkCenter.LaborHourlyRate)
└── 製造費用     = Σ (ProductionSession.實際機台工時 × WorkCenter.MachineHourlyRate)
                  + Setup 攤提（ProductionSetup.實際 Setup 時間 × 費率）
```

**標準成本 vs 實際成本**：釋出工單時計算並快照標準成本，生產過程中累計實際成本，完工後可做差異分析。

### 16.2 欄位設計

#### WorkCenter（Phase A 加入）

| 欄位 | 型別 | 說明 |
|------|------|------|
| `MachineHourlyRate` | decimal(18,4) | 機台費率（元/小時）；設在工作中心層級，同一工作中心的機台共用。**（已實作 2026-05-27）** |
| `StandardLaborCost` | decimal(18,4) | 人工費率（元/小時）＝ spec 原稱 `LaborHourlyRate`；依工作中心性質設定（技術工 vs 操作員）。**沿用既有欄位**（UI 標籤「標準工時單價」），未改名以免動既有 CRUD/resx；下方成本公式中的 `LaborHourlyRate` 即此欄位。 |

> 費率設在 `WorkCenter` 而非 `Machine`，避免每台機台都需維護費率。若特定機台費率特殊，可日後擴充 `Machine` 層覆蓋。
> **實作備註（2026-05-27）**：`MachineHourlyRate` 已加入 `WorkCenter` 模型、CRUD 表單/Index/Excel 匯出與 migration；`StandardLaborCost` 早於 Phase A 既存。兩者皆 decimal(18,4)。

#### Product（Phase A 確認）

| 欄位 | 型別 | 說明 |
|------|------|------|
| `StandardCost` | decimal(18,6) | 產品標準成本；若 BAS 模組尚未建立此欄位，Phase A 時補充 |

#### WorkOrderMaterial（Phase B 加入）

| 欄位 | 型別 | 說明 |
|------|------|------|
| `UnitCost` | decimal(18,6) | 工單建立時快照自 `Product.StandardCost`（或最近進貨單價）；之後不隨標準成本異動 |

#### WorkOrder（Phase B 加入）

| 欄位 | 型別 | 說明 |
|------|------|------|
| `StandardMaterialCost` | decimal(18,4) | 釋出時 = Σ (WorkOrderMaterial.Qty × UnitCost) |
| `StandardLaborCost` | decimal(18,4) | 釋出時 = Σ (RoutingOperation.TM_UNIT × WorkCenter.LaborHourlyRate) / 3600 |
| `StandardOverheadCost` | decimal(18,4) | 釋出時 = Σ (RoutingOperation.TM_UNIT × WorkCenter.MachineHourlyRate) / 3600 |
| `ActualMaterialCost` | decimal(18,4) | 實際發料累計（Phase F 完工入庫時由 INV 模組回寫）|
| `ActualLaborCost` | decimal(18,4) | Phase D：每次 ProductionSession 關閉時累加 |
| `ActualOverheadCost` | decimal(18,4) | Phase D：每次 ProductionSession 關閉時累加 |
| `MaterialVarianceCost` | decimal(18,4) | Backflush 後沖時累計：`Σ ActualIssued − Σ(BOM標準用量 × QtyDone/WO.Quantity)`；Push/Pull 模式差異以退補料單反映，此欄保持 0 |

> `Standard*` 三欄在工單釋出（`WorkOrder.Status = Released`）時一次計算並寫入，之後不再異動。
> 釋出後費率或 BOM 更新，不影響已釋出工單的標準成本（需重新建工單才套用新費率）。
>
> **實作狀態（2026-05-27）**：`WorkOrderMaterial.UnitCost`（decimal 18,6）與 `WorkOrder` 六個成本欄（decimal 18,4）欄位到位；MRP 展開（`RunMpsAsync`/`RunJAsync`）建立用料時已快照 `UnitCost = Product.StandardCost`。
> **`Standard*` 釋出時計算已實作**（`WorkOrderController.Release` → `WorkOrderCostCalculator.ComputeStandard`，純函式、有單元測試）：`Material = Σ(RequiredQty × UnitCost)`；`Labor/Overhead = Σ 各工序 (SetupTime + UnitTime×qty) 依 TimeUnit 折算小時 × WorkCenter.StandardLaborCost / MachineHourlyRate`（較 §16.2 簡化式更精確：含整備時間、依工序 TimeUnit 換算、未設費率以 0 計）。公式中 `LaborHourlyRate` 讀 `WorkCenter.StandardLaborCost`、`MachineHourlyRate` 讀 `WorkCenter.MachineHourlyRate`。
> **實作狀態（2026-07-03 校正——原「尚未實作」敘述已全數解除）**：**`Actual*` 累計已實作**——人工/製費於報工 session 與 setup 關閉時累加（`DispatchController`：`ActualLaborCost += hours × WorkCenter.StandardLaborCost`、`ActualOverheadCost += hours × MachineHourlyRate`），材料於完工入庫後沖時累加（`ProductionCompletionService`，並回寫 `MaterialVarianceCost`）；期末再由 `ActualRateReestimationService` 以部門實際費率重估（見 roadmap-fin-cst-sub.md §3.3）。**簽核閥值整合（§18.2）已實作**——`WorkOrder` 已具 `ApprovalStatus`/`ApprovalRequestId` 欄位，工單釋出走 `WorkOrderReleaseService`（`GetApprovalConfig` 取簽核設定，有流程即送審、`WorkOrderReleaseOutcome.Submitted`）。

### 16.3 成本累計邏輯

#### ProductionSession 關閉時（Phase D）

```
實際工時（小時）= (Session.EndAt - Session.StartAt).TotalHours
                 - PauseMinutes / 60  （若有記錄暫停時間）

WorkOrder.ActualLaborCost    += 實際工時 × WorkCenter.LaborHourlyRate
WorkOrder.ActualOverheadCost += 實際工時 × WorkCenter.MachineHourlyRate
```

#### ProductionSetup 關閉時（Phase D，可選）

```
Setup 工時（小時）= (Setup.SetupEndAt - Setup.SetupStartAt).TotalHours

WorkOrder.ActualOverheadCost += Setup 工時 × WorkCenter.MachineHourlyRate
```

> Setup 成本是否計入 `ActualOverheadCost` 由系統參數控制；第一階段可先合併計入，後期如需獨立分析 Setup 成本再拆欄位。

#### 完工入庫時（Phase F）

```
ActualMaterialCost = INV 模組回傳的實際發料成本總計
（依 InventoryMovement 的出庫紀錄計算，由 INV 模組負責回寫）
```

### 16.4 成本查詢與差異分析（不需額外 Entity，查詢即可）

完工後可直接從 `WorkOrder` 計算：

```
標準總成本 = StandardMaterialCost + StandardLaborCost + StandardOverheadCost
實際總成本 = ActualMaterialCost   + ActualLaborCost   + ActualOverheadCost

材料差異 = ActualMaterialCost   - StandardMaterialCost（全料基準）
人工差異 = ActualLaborCost      - StandardLaborCost
費用差異 = ActualOverheadCost   - StandardOverheadCost
```

> **材料成本口徑（2026-07-06，D1-A，reconciliation-decisions.md）**：
> 完工時 `ActualMaterialCost` 同時彙總**後沖料（Backflush+AtCompletion）與 Push/Pull 發料實際成本**
> （後者取 `MaterialSlip.SourceDocId=工單` 對應 `StockMovement` 的凍結 AVCO，依方向淨計），
> 使完工品單位成本涵蓋全部耗用材料、存貨計價不低估。差異以全料 `StandardMaterialCost` 為基準。
> 生產物料異動不落日記帳（材料留在存貨、未費用化），故無需 WIP 反費用化；
> 逐工單材料 GL 顆粒度屬另立之全系統過帳專案（見 D1「殘留」）。

**單位成本**（完工後）：
```
實際單位成本 = 實際總成本 / WorkOrder.QtyComplete
```

### 16.4b 重工成本計算與異常分析

重工（`WorkOrder.IsRework = true`）產生的成本需獨立追蹤，作為**品質異常成本**納入報表：

```
重工成本（廠內）= ActualMaterialCost + ActualLaborCost + ActualOverheadCost
重工成本（委外）= ActualMaterialCost（不良品帳面值）+ OutsourcingCost（委外加工費，來自 OPO.LineAmount）
```

**成本分類**（用於報表異常分析維度）：

| 分類 | 識別條件 | 成本欄位 | 報表標籤 |
|------|---------|---------|---------|
| 正常生產成本 | `IsRework = false` | Standard* / Actual* | 生產成本 |
| 廠內重工損失 | `IsRework = true, IsOutsource = false` | Actual* | 重工損失（廠內）|
| 委外重工損失 | `IsRework = true, IsOutsource = true` | ActualMaterial + OutsourcingCost | 重工損失（委外）|

**成本分析報表需求（Phase J）**：

| 報表 | 維度 | 指標 |
|------|------|------|
| 重工率分析 | 期間 / 品號 / 工作中心 / 廠商 | 重工工單數、重工數量、重工率（重工量/正常產量）|
| 重工成本分析 | 期間 / 品號 / 不良原因代碼 | 廠內重工成本、委外重工加工費、重工佔生產成本比率 |
| 異常成本排行 | 期間 | 各品號/工序/廠商的異常成本排行（含 Scrap + Rework）|
| 委外效益分析 | 廠商 / 品號 | 委外加工費、不良率、重工次數、綜合評分 |

> **資料來源**：`WorkOrder`（IsRework + IsOutsource 維度）+ `OutsourcePurchaseOrderLine`（委外加工費）+ `ProductionReport.NgReasonCode`（不良原因）。無需新增 Entity，Phase J 以查詢層實作。

**重工成本入帳（2026-10-16 實作，使用者裁決「原值回庫、重工增量轉損失」，IAS 2 異常耗損不得計入存貨）**：

| 時點 | 子帳 | 總帳 |
|---|---|---|
| 工單的重工發料 RI／重工退料 RX（來源＝工單） | 料進出在製品；計入重工工單 `ActualMaterialCost`（`WorkOrderMaterialMovement.Categories` 含 `Rework`） | **不入帳**（`PostDocSimplifiedRequest.IsWorkOrderMaterial`） |
| 重工工單完工入庫 | 良品單價＝**發進來的不良品原值**（同品號發退料淨值）÷ 完工量，不超過實際總成本 | 借 5250 重工損失（＝實際總成本 − 回存值）、存貨淨腿（人工＋製費 − 重工損失）、貸 5110／5120 |
| 庫存模組重工發退料單 RI／RR（不屬於任何工單） | 一般出入庫 | RI 借 5250／貸存貨；RR 借存貨／貸 5250（原本落到通用規則貸 2152） |

- 原值只認真的發進來的不良品；生產中判 NG、從沒入庫的不良品不在帳上，原值＝0（料錢已攤進原工單良品單位成本，改用原工單單位成本或標準單價會讓同一份料在存貨裡算兩次）。
- `WorkOrderActualCost.ReworkLoss` 記完工當下的重工損失；之後期末費率重估差額照一般工單進 5130，不回頭改。
- 分工：有工單的重工一律走工單發退料單；`/ReworkMaterial` 的工單欄填既有工單號會被擋（`ReworkMaterialUseWorkOrderSlip`）。
- 報表：工單成本差異（/WorkOrderVariance）加工單類型欄與篩選、重工損失欄、重工小計列。Phase J 其餘報表（重工率、異常成本排行等）仍未做。
- 不良品本身列為用料（同日補）：重工工單複製原工單 BOM 用料，原本**不會把不良品本身列為用料**——不良品已入庫時，完工多出一份良品、不良品仍留在庫存，且沒有原值可回存貨。`IReworkWorkOrderService.AddAsync(includeDefective)` 另加一行（第一道重做工序、BomSeq 0、Push、`MrpMode.StockOnly`、數量＝重工數量）。工單列表「建立重工」由勾選決定（已入庫結案的工單預設勾、已完工未繳庫的預設不勾）；NCR 處置「重工」在 NCR 有倉庫＋儲位時自動帶入。生產中判 NG 的不良品不在帳上，勾了會讓倉庫把庫存良品當不良品發出——這是使用者判斷，系統不擋。

### 16.5 與財務模組的銜接預留

**MFG 模組**目前不自行產生財務傳票，但預留以下資料供未來對接：

> ⚠️ **「不產生傳票」只限 MFG，不是整個系統。** 系統確實有 GL 過帳
> （`Modules/INV/Services/InventoryPostingService.BuildJournalLines` 是全站帳務語意的單點，
> AP／AR 也各自入帳）。與 MFG 有關的是那支方法的第一道判斷：
> `typeCategory is "Production" or "Outsource"` 時**刻意回傳空分錄**——
> 生產領料與繳庫只是廠內存貨型態轉換、委外發料所有權未移轉，入帳會把領料誤記成報廢損失、
> 繳庫誤記成進貨暫估應付。**所以「MFG 不產生傳票」是設計，不是還沒做。**

| 財務需求 | 對應資料 | 來源 |
|---------|---------|------|
| 在製品（WIP）評價 | WorkOrder 中 Standard* 成本 × 完成比例 | WorkOrder |
| 製成品入庫成本 | ActualMaterialCost + ActualLaborCost + ActualOverheadCost | WorkOrder 完工時 |
| 生產損耗（不良品）| ProductionReport.QtyNg × 單位標準成本 | Report + WorkOrder |
| 廠內重工成本 | IsRework=true, IsOutsource=false 的 Actual* | WorkOrder |
| 委外重工成本 | IsRework=true, IsOutsource=true 的 Material + OutsourcingCost | WorkOrder + OPO |
| 委外加工費 | OPO.LineAmount（委外採購單金額）| OutsourcePurchaseOrderLine |
| 標準成本差異（月結）| Σ (Actual* - Standard*) 依工作中心彙總 | WorkOrder group by WorkCenter |

> **財務傳票**（借：在製品 / 貸：原料庫存 等）屬財務模組範疇，MFG 只負責提供上表資料，不產生傳票。

### 16.6 成本模組切片建議

| Phase | 動作 | 估算 |
|-------|------|------|
| **A**（BAS）| `WorkCenter` 加 `MachineHourlyRate` / `LaborHourlyRate`；確認 `Product.StandardCost` | 含在 Phase A 估算內 |
| **B**（MRP）| `WorkOrderMaterial.UnitCost` 快照；`WorkOrder` 加六個成本欄位；釋出時計算 Standard* | 含在 Phase B 估算內（+1-2 天）|
| **D**（報工）| Session 關閉時累計 `ActualLaborCost` / `ActualOverheadCost` | 含在 Phase D 估算內（+0.5 天）|
| **F**（入庫）| INV 模組回寫 `ActualMaterialCost`；委外 OPO.LineAmount 寫入 OutsourcingCost | 含在 Phase F 估算內 |
| **J**（成本分析，後期）| 成本查詢畫面、差異分析報表（含重工異常分析）、財務銜接 | 4-6 週，待財務需求確認後啟動 |

---

## 17. MFG ↔ INV 庫存異動類型對應表

MFG 模組產生的各項庫存異動，均透過 `InventoryMovement` Entity 與 INV 模組互動。本表定義 MFG 各流程觸發的異動類型。

### 17.1 庫存異動類型定義（INV 全域）

| `InventoryTransactionType` | 中文 | 流向 | 觸發來源 | 說明 |
|---------------------------|------|------|---------|------|
| `MATERL_ISSUE` | 材料領用 | 庫存- | MaterialSlip(InternalIssue/InternalRequisition) 過帳 | 廠內工單 Push/Pull 用料出庫 |
| `MATERL_RETURN` | 退料 | 庫存+ | MaterialSlip(InternalReturn) 過帳 | 廠內多發或取消時退還倉庫 |
| `OUTSOURCE_ISSUE` | 委外發料 | 庫存- | MaterialSlip(OutsourceIssue/ReworkIssue+IsOutsource) 過帳 | 送外料出庫至委外廠商 |
| `OUTSOURCE_RETURN` | 委外退料 | 庫存+ | MaterialSlip(OutsourceReturn) 過帳 | 委外廠商退還未用料件回倉 |
| `OUTSOURCE_RECEIPT` | 委外進貨 | 庫存+ | GoodsReceipt(OI) 過帳 | 委外完工品/重工品入庫（簡易/上架依系統參數） |
| `REWORK_MATERIAL` | 重工用料 | 庫存- | MaterialSlip(ReworkIssue+IsInternal) 過帳 | 廠內重工發料出庫（不入總帳，完工結算，見 §16.4b） |
| `REWORK_COMPLETE` | 重工完成 | 庫存+ | 重工 WorkOrder 完工（廠內） | 廠內重工成品入庫 |
| `PROD_COMPLETE` | 生產完成 | 庫存+ | WorkOrder 完工 | 廠內派工累計 QtyDone = QtyTotal 時，成品入庫 |
| `SCRAP_WRITE_OFF` | 生產報廢 | 庫存- | ProductionReport.QtyNg | 生產過程中不良品報廢（直接損耗，無需單據） |
| `TRANSFER_IN` | 轉入（廠間） | 庫存+ | （MFG 一般無使用）| 預留給跨廠調撥 |
| `TRANSFER_OUT` | 轉出（廠間） | 庫存- | （MFG 一般無使用）| 預留給跨廠調撥 |
| `RETURN_TO_SUPPLIER` | 退廠商 | 庫存- | GoodsReturn(OT) | 委外/採購進貨後全批退回廠商 |

### 17.2 MFG 各流程的異動映射

#### 投產前：用料出庫（三模式）

用料出庫依 `WorkOrderMaterial.IssueMethod` 走不同路徑（詳細設計見 §17.3）：

| IssueMethod | 觸發者 | MATERL_ISSUE 時機 | Hard reservation 消費時機 |
|---|---|---|---|
| `Push`（發料） | 倉庫 | 倉庫確認揀料→過帳 | 每次發料後逐筆 release |
| `Pull`（領料） | 生產單位 | 倉庫核准後過帳（可分批） | 每次撿貨後分批 release |
| `Backflush`（後沖） | 系統自動 | WO 完工時（或每次報工，依 BackflushTiming） | WO 完工時整批 release |

> 三種模式的 Hard(MFG) reservation 均在**投產（WO Release 核准後）**統一建立，差異僅在 MATERL_ISSUE 的觸發時機與主體。

#### 報廢處理：廢品直接損耗

```
觸發點：ProductionReport.QtyNg > 0 寫入時
動作：檢查 ProductionReport.NgReasonCode 並決定處理
  若判定為「報廢」（不重工）：
    建立反向 InventoryMovement
    ├── Type: SCRAP_WRITE_OFF
    ├── DocumentNo: ProductionReport.Id
    ├── Product: Dispatch.Product（或 WorkOrder.ProductCode）
    ├── Quantity: 負值（-QtyNg，報廢）
    ├── FromWarehouse: 生產現場（MFG 無具體倉位）
    └── Remarks: 包含不良原因代碼
```

> 報廢品**不需建立作廢單**，直接損耗記錄即可。

#### 重工：用料 + 成品異動

```
觸發 A：建立重工工單時
動作：同新工單的用料出庫
  ├── Type: REWORK_MATERIAL
  ├── DocumentNo: 重工 WorkOrder 編號
  └── 其餘同 MATERL_ISSUE

觸發 B：重工工單完工時
動作：成品入庫（見下文 17.2.3）
  ├── Type: REWORK_COMPLETE
  ├── DocumentNo: 重工 WorkOrder 編號
  └── Remarks: 指出來源原始工單 ID（ReworkFromWorkOrderId）
```

#### 完工入庫：成品回庫

```
觸發點：WorkOrder 所有 Dispatch 累計完工（QtyDone = QtyTotal）
動作：建立入庫 InventoryMovement
  InventoryMovement
  ├── Type: PROD_COMPLETE（一般成品）或 REWORK_COMPLETE（重工）
  ├── DocumentNo: WorkOrder.WorkOrderId
  ├── DocumentType: "WorkOrder"
  ├── Product: WorkOrder.Product
  ├── Quantity: 正值，= WorkOrder.QtyDone（入庫數）
  ├── ToWarehouse: 預設成品倉（Plant 層級設定）
  ├── FromWarehouse: null（生產現場無來源）
  ├── LotNo: ProductionBatch.BatchId（成批標誌，用於追蹤）
  ├── UnitCost: (WorkOrder.ActualMaterialCost + ActualLaborCost + ActualOverheadCost) / WorkOrder.QtyDone（實際單位全成本，含材料＋人工＋製費）
  └── Status: Posted
```

> **重要**：成品入庫時應記錄 `UnitCost`，供 INV 模組計算庫存評價。

#### 委外進貨：供應商交貨入庫

```
觸發點：GoodsReceipt（TypeCode="OI"，SourceDocType="OutsourcePurchaseOrder"）過帳時

PUR/INV 側：
  GoodsReceipt 過帳 → InventoryMovement(Type: OUTSOURCE_RECEIPT，庫存+)
  入庫目標依系統參數 INV_SIMPLE_MODE：
    開啟（簡易模式）→ 直接入庫至目標倉位（Plant 層級成品倉）
    關閉（完整模式）→ 先入暫存區，等待手機 aii（入庫上架）確認儲位後入正式倉位

MFG 側同步：
  GoodsReceipt 過帳後，MFG 以事件/Hook 方式自動建立 ProductionReport：
    ProductionReport.DispatchId = 委外 Dispatch
    ProductionReport.QtyOk = GoodsReceipt.QtyGood（合格進貨量）
    ProductionReport.QtyNg = GoodsReceipt.QtyRejected（不良量）
  → QtyOk 累計至 Dispatch.QtyDone，達到 QtyTotal 時 Dispatch → Closed
  → QtyNg > 0 時，可觸發委外重工流程（見 §19.3 Stage 5b）
```

> GoodsReceipt 加通用欄位 `SourceDocType`（`PurchaseOrder` | `OutsourcePurchaseOrder`）與 `SourceDocId`，確保帳務往上追蹤到委外採購單。

---

### 17.3 發退料單統一框架（MaterialSlip）（2026-06-09 定案）

> 原「發料單（MaterialIssueSlip）」與「領料單（MaterialRequisition）」合併為統一的 `MaterialSlip` Entity，以 `Category` 欄位區分廠內/委外/重工等七種情境，共用倉庫揀料作業（手機 `aio`/`aii`）與庫存異動機制。

#### 三模式概念邊界（廠內仍適用 Push/Pull/Backflush）

```
投產（WO Release 核准）
  → 所有 WorkOrderMaterial 建立 Hard(MFG) InventoryReservation
  → IssueMethod 已快照在 WorkOrderMaterial，後續各自走分支

Push 分支（Category: InternalIssue）
  -- ⚠️ P2：MaterialSlip(Draft) 建立時機 = WO.ApprovalStatus = Approved（核准後），
  --         而非 WO Release 前；未核准工單不得預先備料。
  系統自動建立 MaterialSlip(Draft)，彙整 Push 料件
  倉庫備料→手機 aio 掃描揀料→Confirmed→Post → MATERL_ISSUE
  → consume Hard reservation（逐筆）

Pull 分支（Category: InternalRequisition）
  生產單位建 MaterialSlip(Draft，由本人申請)
  → Status: Pending → 倉庫核准(Approved) → 手機 aio 揀料 → Post → MATERL_ISSUE
  可設系統參數跳過核准（Pending 直接進 Picking）

Backflush 分支（不建 MaterialSlip，WO 完工時系統直接建 InventoryMovement）
  WO 完工（或每次報工，依 BackflushTiming）：
    ActualIssued = WorkOrderMaterial.Qty × (QtyDone / WO.Quantity)
    直接建 InventoryMovement(MATERL_ISSUE，ActualIssued)
    差異 = ActualIssued − Qty → WorkOrder.MaterialVarianceCost
```

> ⚠️ Backflush 適用條件：BOM 精度高（ScrapRate 穩定）、物料單價低、生產週期短。若 BOM 有臨時調整，須手動補建退料單校正差異。

#### Entity：MaterialSlip（發退料單統一 Entity）

```csharp
public class MaterialSlip   // 採番前綴依 Category 不同（MI/MR/MX/OI/OR/RI/RX）
{
    public string SlipId { get; set; }               // PK
    public string SlipNo { get; set; }               // 單號（採番）
    public MaterialSlipCategory Category { get; set; }
    public string PlantId { get; set; }
    public string SourceDocType { get; set; }        // "WorkOrder" | "OutsourcePurchaseOrder"
    public string SourceDocId { get; set; }          // WorkOrderId 或 OutsourcePurchaseOrderId
    public string? TargetSupplierCode { get; set; }  // 僅 OutsourceIssue/OutsourceReturn/ReworkIssue(委外) 使用
    public MaterialSlipStatus Status { get; set; }
    public string? RequestedBy { get; set; }          // InternalRequisition/ReworkIssue 申請人
    public DateTime? RequestedAt { get; set; }
    public string? ApprovedBy { get; set; }           // InternalRequisition 核准人
    public DateTime? ApprovedAt { get; set; }
    public string? IssuedBy { get; set; }             // 倉庫人員（揀料確認）
    public DateTime? IssuedAt { get; set; }
    public string? Remarks { get; set; }
    public List<MaterialSlipLine> Lines { get; set; }
}

public enum MaterialSlipCategory
{
    InternalIssue,        // 廠內 Push 發料（DocType: MI）
    InternalRequisition,  // 廠內 Pull 領料（DocType: MR）— 含申請→核准工作流
    InternalReturn,       // 廠內退料（DocType: MX）— 多發或取消退還倉庫
    OutsourceIssue,       // 委外發料/送外料（DocType: OI）— 送至委外廠商
    OutsourceReturn,      // 委外退料（DocType: OR）— 廠商退還未用料件
    ReworkIssue,          // 重工發料（DocType: RI）— 廠內或委外重工的料件發出
    ReworkReturn,         // 重工退料（DocType: RX）— 重工多餘料件退還
}

public enum MaterialSlipStatus
{
    Draft,       // 草稿（系統自動建議 或 人工建立）
    Pending,     // 待核准（僅 InternalRequisition）
    Approved,    // 已核准（InternalRequisition 核准後可揀料）
    Picking,     // 揀料中（手機掃描進行中）
    Confirmed,   // 倉庫確認（揀料完成，待過帳）
    Posted,      // 已過帳（InventoryMovement 已建立）
    Closed,      // 已結案（含退料完成）
    Cancelled,   // 已取消
}

public class MaterialSlipLine
{
    public string SlipId { get; set; }               // FK
    public int LineNo { get; set; }
    public string? SourceLineId { get; set; }        // WorkOrderMaterialId 或 OutsourcePurchaseOrderLineId
    public string ProductCode { get; set; }
    public decimal RequestedQty { get; set; }        // 申請/建議量
    public decimal? ApprovedQty { get; set; }        // 核准量（僅 InternalRequisition）
    public decimal ConfirmedQty { get; set; }        // 倉庫實際確認出庫量
    public string UomCode { get; set; }
    public string? FromWarehouseCode { get; set; }   // 揀料倉庫
    public string? FromLocationCode { get; set; }    // 揀料儲位
    public string? ToWarehouseCode { get; set; }     // 目的倉庫（退料時為來源）
    public string? ToLocationCode { get; set; }
    public string? LotNo { get; set; }
    public string? SerialNo { get; set; }
    public decimal ReturnedQty { get; set; } = 0M;  // 退料量（InternalReturn/OutsourceReturn 時使用）
}
```

#### Category ↔ InventoryMovement 映射

| Category | 觸發條件 | InventoryMovement Type |
|----------|---------|----------------------|
| InternalIssue | WO Release 後 Push 料自動建 | `MATERL_ISSUE` |
| InternalRequisition | 生產單位申請 | `MATERL_ISSUE` |
| InternalReturn | 廠內多發退料 | `MATERL_RETURN` |
| OutsourceIssue | OPO Confirmed 後自動建 | `OUTSOURCE_ISSUE` |
| OutsourceReturn | 委外廠商退料 | `OUTSOURCE_RETURN` |
| ReworkIssue（廠內）| WorkOrder(IsRework=true, IsOutsource=false) | `REWORK_MATERIAL` |
| ReworkIssue（委外）| WorkOrder(IsRework=true, IsOutsource=true) | `OUTSOURCE_ISSUE` |
| ReworkReturn | 重工多餘料退還 | `MATERL_RETURN` |

#### Category 業務規則速查

| Category | 自動建立時機 | 需核准？ | 手機作業 |
|----------|------------|---------|---------|
| InternalIssue | WO Release（Push 料）| 否 | aio 出庫掃描 |
| InternalRequisition | 生產單位手動建立 | **是**（可參數跳過）| aio 出庫掃描 |
| InternalReturn | 手動建立 | 否 | aii 入庫掃描 |
| OutsourceIssue | OPO Confirmed | 否（OPO 已是授權）| aio 出庫掃描（標示送至廠商） |
| OutsourceReturn | 手動建立 | 否 | aii 入庫掃描（標示廠商退料）|
| ReworkIssue | 重工 WO 建立時 | 否 | aio 出庫掃描（含不良品揀料）|
| ReworkReturn | 手動建立 | 否 | aii 入庫掃描 |

#### WorkOrderMaterial 補充欄位

```csharp
public class WorkOrderMaterial
{
    // ... 現有欄位 ...
    public MaterialIssueMethod IssueMethod { get; set; }   // 快照自 Fallback 鏈
    public BackflushTiming BackflushTiming { get; set; }   // 快照自 RoutingBom
    public decimal IssuedQty { get; set; } = 0M;           // 累計已發出量
    public decimal ReturnedQty { get; set; } = 0M;         // 累計退料量
    // NetIssuedQty = IssuedQty - ReturnedQty（淨發料量，完工差異基準）
}
```

#### Phase 實作建議

| Phase | 優先 Category | 理由 |
|---|---|---|
| Phase D（上工報工）| **Backflush**（不建 MaterialSlip）| 最少額外 Entity，先跑通完整流程 |
| Phase D 補強 | **InternalIssue（Push）** | 主流工廠模式，倉庫主控 |
| Phase F（委外完整流程）| **OutsourceIssue / OutsourceReturn** | 委外送外料/退料 |
| Phase F（重工）| **ReworkIssue / ReworkReturn** | 重工發退料 |
| Phase E 之後 | **InternalRequisition（Pull）** | 申請→核准工作流，較複雜 |

---

## 18. 各單據簽核需求對照表

MFG 各主要單據涉及不同的簽核層級與規則，本表定義審核設定的基準。

### 18.1 簽核主體對照

| Entity | 簽核狀態欄 | 簽核觸發 | 簽核類型 | 簽核者角色 | 備註 |
|--------|----------|--------|--------|----------|------|
| **Routing（途程）** | — | ❌ 無簽核 | — | — | 主檔，不需簽核 |
| **WorkCenter** | — | ❌ | — | — | 主檔 |
| **Machine** | — | ❌ | — | — | 主檔 |
| **WorkCalendar** | — | ❌ | — | — | 主檔 |
| **ProductionBatch** | — | ❌ | — | — | MRP 展開產生，狀態由子 WorkOrder 決定 |
| **WorkOrder（工單）** | `ApprovalStatus` | ✅ Release 時 | Conditional | 生產主管 / 廠長 | 簽核金額：標準成本總額 |
| **Dispatch（派工）** | — | ❌ | — | — | 自動建立，排程系統管理，無簽核 |
| **MachineStatusOrder（機台異動）** | ✅ (可選) | 手動 / 自動 | Conditional | 設備主管 | 重大故障、計劃停機可需簽核（系統參數） |
| **ProductionSetup（開工準備）** | — | ❌ | — | — | 手機端自動記錄，無簽核 |
| **ProductionSession（上工）** | — | ❌ | — | — | 手機端報工自動建立，無簽核 |
| **ProductionReport（報工）** | — | ❌ | — | — | 手機端逐筆報工，累計統計 |

### 18.2 WorkOrder 簽核設定

#### 何時需簽核（系統參數控制）

| 條件 | 是否需簽核 | 說明 |
|------|----------|------|
| 標準成本 > 設定門檻（預設：TWD 500,000） | ✅ | 大額工單需稽核 |
| 工期延期 > 訂單交期 | ✅ | 可能影響交期的工單 |
| 委外工單 | ✅ 可選 | 委外涉及廠商選擇，可設為需簽核 |
| 重工工單 | ✅ 可選 | 品質問題的重工，可需技術主管簽核 |
| 一般自製工單（成本低、工期短）| ❌ | 快速通道，自動 Approved |

#### WorkOrder 簽核流程

```
狀態：Planned（新建，不可簽核）
  ↓ Release（釋出，確認工單可投產）
狀態：Released
  若此時 ApprovalStatus 需簽核：
    ✅ 跳轉 → PendingApproval（投產前審核）
    ❌ 直接 → Approved（無簽核門檻）
  ↓ 簽核核准 / Approved
狀態：Released + Approved
  ↓ 派工累計完工（所有 Dispatch.Closed）
狀態：Completed
  ↓ 入庫完成
狀態：Closed
```

> **實作狀態（2026-10-20）**：投產前審核已落實為三道閘——排程引擎不排、桌機與手機開工擋下送審中（PendingApproval）與已退回（Rejected）的工單（`WorkOrderApprovalGate`；Draft 為預設值，舊工單與無簽核設定者照常放行）。簽核駁回／退回修改／撤回時，Released 工單比照「撤回」退回 Planned，修正後可重新投產再送審。
> 工單作廢（§4.2）同日補兩條：草稿～揀料中的發料單隨作廢取消、已確認揀料的發料單與未作廢的委外採購單擋下作廢；發料過帳與委外收貨確認另擋來源工單已作廢者。

**簽核金額與明細**：
- `ApprovalRequest.Amount` = `WorkOrder.StandardMaterialCost + StandardLaborCost + StandardOverheadCost`
- Line 明細：按 `WorkOrderMaterial` 與 `WorkOrderOperation` 展示

### 18.3 MachineStatusOrder 簽核設定（可選）

取決於廠別政策，可關閉或啟用：

| 情況 | 簽核 | 原因 |
|------|------|------|
| 輕微狀態變更（Standby → Available） | ❌ | 作業員自行操作 |
| 計劃停機（Maintenance） | ✅ 可選 | 影響排程，可需設備主管核可 |
| 故障停機（BreakDown） | ❌ 紀錄即可 | 已發生事實，無法事前審批 |
| 大修改良（Modification） | ✅ 建議 | 成本大、時間長，需主管批准 |

### 18.4 簽核流程與回簽邏輯

#### WorkOrder Release 時的簽核決策

```csharp
// 準釋出工單時的簽核邏輯
public async Task<IActionResult> Release(string workOrderId)
{
    var wo = await _context.WorkOrders.Include(w => w.Materials).FindAsync(workOrderId);
    
    // 計算簽核金額
    var standardCost = wo.StandardMaterialCost + wo.StandardLaborCost + wo.StandardOverheadCost;
    
    // 判斷是否需簽核
    var approvalConfig = await _approvalService.GetApprovalConfig(
        docType: "WorkOrder",
        plantId: wo.PlantId
    );
    var requiresApproval = approvalConfig != null 
        && (standardCost > approvalConfig.ThresholdAmount);
    
    wo.Status = WorkOrderStatus.Released;
    
    if (requiresApproval)
    {
        // 發起簽核
        var approvalReq = await _approvalService.SubmitAsync(
            docType: "WorkOrder",
            docId: wo.WorkOrderId,
            amount: standardCost,
            plantId: wo.PlantId,
            userId: _userContext.UserId,
            remarks: $"工單釋出簽核：{wo.WorkOrderId}"
        );
        wo.ApprovalStatus = DocumentApprovalStatus.PendingApproval;
        wo.ApprovalRequestId = approvalReq.Id;
    }
    else
    {
        // 自動通過
        wo.ApprovalStatus = DocumentApprovalStatus.Approved;
    }
    
    await _context.SaveChangesAsync();
    return Ok(new { success = true, requiresApproval });
}
```

#### 簽核核准後的副作用

```csharp
// 簽核核准後，MFG 應監聽事件（或主動輪詢）
public async Task OnApprovalApproved(string workOrderId)
{
    var wo = await _context.WorkOrders.FindAsync(workOrderId);
    wo.ApprovalStatus = DocumentApprovalStatus.Approved;
    
    // 此時工單可正式投產
    // 自動排程：可在此觸發 ScheduleDispatches()
    
    await _context.SaveChangesAsync();
}
```

---

## 19. 委外完整流程

委外工單（MRP 展開時 CD_MRP='S' 或工序 `IsOutsource=true`）涉及 MFG、PUR、INV 三模組協作。本節定義完整業務流程與 Entity 互動。

### 19.1 委外的角色定位

| 模組 | 責任 |
|------|------|
| **MFG** | MRP 展開、委外建議管理、委外工單/派工、進度回報、重工觸發 |
| **PUR** | 廠商報價管理、委外採購單建立、進貨簽核、應付帳務 |
| **INV** | 送外料出庫、委外退料入庫、委外進貨入庫（含上架） |

### 19.2 委外相關 Entity 設計

#### WorkOrder 委外欄位（已實作）

| 欄位 | 型別 | 說明 |
|------|------|------|
| `IsOutsource` | bool | 標識此工單是委外 |
| `OutsourceSupplierCode` | string?(20) | FK → Company，委外廠商（拋轉時填入） |
| `OutsourcePurchaseOrderId` | string?(30) | FK → OutsourcePurchaseOrder（拋轉後回填） |
| `OutsourceExpectedDeliveryDate` | DateOnly? | 委外廠商應交日期 |

#### OutsourcePurchaseOrder（委外採購單，已規劃）

在現有設計基礎上，新增拆單追蹤欄位：

```csharp
public class OutsourcePurchaseOrderLine
{
    // ... 現有欄位 ...
    public string DemandId { get; set; }        // FK → OutsourceDemand（來源委外建議）
    public decimal SplitQty { get; set; }       // 本 OPO 行承接的拆單數量
    public decimal UnitPrice { get; set; }      // 加工單價（從 OutsourceQuotation 帶入，可覆寫）
    public string CurrencyCode { get; set; }
    // 送外料由來源 DispatchMaterial 衍生顯示，不另存
}
```

#### GoodsReceipt 擴充欄位

```csharp
// 現有 GoodsReceipt 新增通用來源連結（同時支援 PurchaseOrder 與 OutsourcePurchaseOrder）
public string? SourceDocType { get; set; }   // "PurchaseOrder" | "OutsourcePurchaseOrder"
public string? SourceDocId { get; set; }     // POId 或 OPOId
public int QtyGood { get; set; }             // 合格進貨量
public int QtyRejected { get; set; }         // 不良量（GoodsReceipt 過帳時自動觸發委外重工/退廠流程）
```

#### Dispatch 委外調適

| 欄位 | 值 | 說明 |
|------|-----|------|
| `MachineId` | null | 委外無具體機台 |
| `IsOutsource` | true | 複製自 WorkOrder.IsOutsource |
| `OutsourcePlannedStartAt` | DateTime? | 委外預計開始日 |
| `OutsourcePlannedEndAt` | DateTime? | 委外預計完成日 |

### 19.3 委外流程：從 MRP 展開到完工入庫

#### Stage 1：MRP 展開時建立委外建議與工單

```
觸發 A（整品委外）：RunJAsync，Product.CD_MRP = 'S'
觸發 B（工序委外）：RunMpsAsync，RoutingOperation.IsOutsource = true

流程：
  1. 建立 WorkOrder（IsOutsource=true）
  2. 建立 Dispatch（MachineId=null，IsOutsource=true）
  3. 建立 WorkOrderMaterial（送外料清單，來自 RoutingBom）
  4. 建立 OutsourceDemand（§4.1）：
       - 整品委外：DemandType=WholeProduct，DispatchId=null
       - 工序委外：DemandType=Operation，DispatchId=本工序派工
  5. 反推應交日期：
       OutsourceExpectedDeliveryDate = 訂單交期 - OutsourceQuotation.LeadDays
       （無報價時：Product.OutsourceLeadTime 或系統預設）

結果：WorkOrder Planned，OutsourceDemand.IsFullyTransferred=false（未拋轉）
```

#### Stage 2：批次拋轉 — 設廠商/價格/拆單 → 建委外採購單

```
觸發：採購人員在「委外採購單」→「轉委外清單」操作

畫面顯示：OutsourceDemand 清單（未拋轉量 = DemandQty - TransferredQty > 0 的項目）

操作流程：
  1. 勾選一或多筆委外建議
  2. 輸入拆單配置（每筆可拆多行）：
       SupplierCode  數量    預期交期    單價
       廠商 A         60     2026-07-10  120（從 OutsourceQuotation 自動帶入，可改）
       廠商 B         40     2026-07-25  115
  3. 確認 → 依廠商分別建立 OutsourcePurchaseOrder（Draft）
     每個 OPO.Line 記錄 DemandId + SplitQty
  4. 更新 OutsourceDemand.TransferredQty += 各 SplitQty
  5. 回填 WorkOrder.OutsourcePurchaseOrderId（主要 OPO）、SupplierCode

報價自動帶入規則：
  選定廠商後，系統依「SupplierCode + ProductCode（或 RoutingOperationCode）+ 有效日期」
  查詢 OutsourceQuotation（§22），取最具體符合的有效報價帶入單價；用戶可覆寫。

OPO 後續：Draft → Confirmed（採購確認）→ 印單/發廠商
  Confirmed 時：自動建立 MaterialSlip(OutsourceIssue，Draft）（見 Stage 3.5）
```

#### Stage 3：排程推算委外時段

```
觸發：排程引擎執行時

若 Dispatch.IsOutsource = true：
  1. 不查 Machine 可用時段（無機台）
  2. timeS = max(前工序 timeE, 今天)
  3. 依 CD_BOR 決定前置依賴（整批/定量/連續）
  4. timeE = timeS + OutsourcePurchaseOrderLine.LeadDays（或 RoutingOperation.QT_BOR 天）
  5. 更新 Dispatch.OutsourcePlannedStartAt / OutsourcePlannedEndAt
```

#### Stage 3.5：委外發料（送外料出庫）

```
觸發：OutsourcePurchaseOrder.Confirmed 時，自動建立 MaterialSlip(OutsourceIssue，Draft)

MaterialSlip(OutsourceIssue)：
  SourceDocType = "OutsourcePurchaseOrder"
  SourceDocId   = OutsourcePurchaseOrderId
  TargetSupplierCode = OPO.SupplierCode
  Lines = 來自 DispatchMaterial（送外料品項）

倉庫操作：
  倉庫人員依 MaterialSlip 揀料（手機 aio 掃描確認揀料位、批號）
  → MaterialSlip.Status: Draft → Picking → Confirmed → Post
  → InventoryMovement(OUTSOURCE_ISSUE，庫存-)

委外退料（廠商退還多餘/未用料件）：
  手工建立 MaterialSlip(OutsourceReturn)
  → InventoryMovement(OUTSOURCE_RETURN，庫存+)
```

#### Stage 4：進度回報

```
委外派工進度由來料驗收人員報工（GoodsReceipt 自動觸發，或手工）：
  ProductionReport.DispatchId = 委外派工
  ProductionReport.QtyOk = 合格收入量
  ProductionReport.QtyNg = 不良量

MFG 側：
  Dispatch.QtyDone += QtyOk
  QtyDone >= QtyTotal → Dispatch.Status = Closed → WorkOrder.Status = Completed
  ETA 重算（後續工序前移）
```

#### Stage 5：委外進貨（廠商交貨入庫）

```
PUR 側（GoodsReceipt）：
  1. 廠商交貨 → 建立 GoodsReceipt（TypeCode="OI"，SourceDocType="OutsourcePurchaseOrder"，SourceDocId=OPOId）
  2. IQC 檢驗（若需）
  3. Post → InventoryMovement(OUTSOURCE_RECEIPT，庫存+)
     入庫目標依系統參數 INV_SIMPLE_MODE：
       開啟 → 直接入庫至目標倉位
       關閉 → 先入暫存區，等待手機 aii 上架確認

MFG 側（GoodsReceipt.Post 事件）：
  自動建立 ProductionReport（QtyOk = QtyGood, QtyNg = QtyRejected）
```

#### Stage 5b：委外重工（進貨不良品送廠商重工）

```
觸發：GoodsReceipt.QtyRejected > 0 且決定「重工」（非報廢/特採/全批退）

流程：
  1. 建立重工 WorkOrder：
       IsRework = true
       IsOutsource = true（送回同廠商或另找廠商）
       ReworkFromWorkOrderId = 原委外工單
       Quantity = QtyRejected（不良數量）
  2. 建立 OutsourceDemand（DemandType=WholeProduct，觸發同 Stage 1）
  3. 重工材料 = 不良品本身（從暫存/隔離區揀出）
  4. 後續流程與正常委外相同（Stage 2 → 3.5 → 4 → 5 → 6）：
       建 MaterialSlip(ReworkIssue，TargetSupplierCode=廠商，OUTSOURCE_ISSUE)
       廠商重工完成 → GoodsReceipt(OI) → OUTSOURCE_RECEIPT
       完工入庫 → 付款

重工成本追蹤（含入成本分析）：
  WorkOrder(IsRework=true) 的成本欄位獨立計算：
    ActualMaterialCost：重工件本身的帳面成本（來自原工單）
    OutsourcingCost：本次重工的加工費（來自重工 OPO.LineAmount）
  這兩項作為「重工異常成本」，納入成本分析報表的「重工損失」分類（見 §16）
```

#### Stage 6：委外工單完工與入庫、付款帳務

```
完工條件：
  Dispatch.QtyDone >= QtyTotal
  OutsourcePurchaseOrder 已 Closed

MFG 側：
  WorkOrder.Status → Completed（此步驟已由 Stage 5 GoodsReceipt.Post 觸發）

付款文件鏈（PUR 側）：
  OutsourcePurchaseOrder（OP）
    ↓ 1:N
  GoodsReceipt（OI，SourceDocType="OutsourcePurchaseOrder"，SourceDocId=OPOId）
    ↓
  AP Invoice（應付發票，連結 GoodsReceipt 與 OutsourcePurchaseOrder）
    ↓
  Payment（付款，可與其他 AP 合併付款）

追蹤要求：
  從「付款」往上可追到「委外採購單 → 委外建議 → MRP 工單 → 銷售訂單」完整鏈路
  AP Invoice 須記錄 SourceOpoId，報表可依此過濾委外費用
```

#### Stage 7：委外工單完工入庫（MFG 側 + IQC 閘口 + 成本結算）

```
完成條件：
  1. Dispatch.QtyDone（合格收入量）>= QtyTotal
  2. 對應的 OutsourcePurchaseOrder 已 Closed（廠商交貨完成）
  3. IQC 檢驗結果確認

MFG 側：
  1. WorkOrder.Status → Completed
  2. 觸發成品入庫 InventoryMovement
     Type: PROD_COMPLETE
     Quantity: WorkOrder.QtyDone - QtyScrap
  3. 成本結算：
       - StandardCost：由 MFG 負責計算（= 採購成本 + 委外加工費）
       - ActualCost：由 INV 回寫實際進貨成本

PUR 側：
  1. OutsourcePurchaseOrder 自動或手工關閉
  2. 廠商評分（若有評等系統）
  3. 發票簽核（非 MES 範疇）
```

### 19.3a 委外收貨回寫委外派工（2026-10-12 查證；整品委外已修，工序委外待定）

> **已修（feat/outsource-iqc-completion）**：使用者流程＝收貨 → 品檢 → 入庫 → 領出，裁決「品檢合格入庫時才算」。
> - `IOutsourceProgressService.ApplyGoodQtyAsync`：收回品**進到一般倉**時（簡化模式確認收貨過帳；非簡化模式免驗品上架到一般倉、
>   或 IQC 合格自動放行）把庫存量累計到委外派工 `QtyDone`，滿即結案；工單派工都結束時，整品委外直接 `Closed`（入庫完成），其他照一般規則 `Completed`。
> - 委外收貨補品質隔離閘門（比照一般進貨）：非免驗品號未 IQC 合格前只能上架待驗倉；IQC 待檢清單（桌機／手機共用 `IIqcInspectionService`）納入委外收貨，
>   `IqcPendingLine.SourceKind`；合格放行沿用系統調撥（目的倉缺漏時退回品號類別預設倉儲）。委外的應付在收貨確認時已開，放行不再認列。
> - `ProductionCompletionService` 擋整品委外工單（成品已由收貨入庫，再做＝重複入庫）。
> - 簡化模式維持與一般進貨一致：確認即入一般倉、不經待驗倉。
>
> 以下為查證時的原始描述（工序委外部分仍成立）：

- `OutsourceReceiptService` 確認收貨只做：超耗結算、入庫（品號＝委外採購單行的品號）、在途轉在手、應付發票；**不碰工單與派工**。
  全系統寫 `Dispatch.QtyDone` 的只有桌機／手機報工，委外派工又被擋在報工之外（10/10 起手機整頁停用）→ 委外派工永遠不會累計、不會結案。
- 整品委外：成品已由收貨入庫，但工單永遠停在生產中（實例 PB-2609-00019_01：收回 8 個已入庫，派工 QtyDone 1／8 未結案，工單狀態 InProgress）。
  若之後硬把工單轉完工再做完工入庫，會**重複入庫**。
- 工序委外：委外採購單行的品號一律取 `wo.ProductCode`（成品），與 §3 `OutsourceDemand.ProductCode`「工序委外＝半成品」的設計不符——
  中間工序收回時會把「成品」記進庫存；且該派工 QtyDone 為 0，最後一道是委外工序時工單完工量＝0、無法完工入庫，
  簡化模式下一道工序也會被順序守門擋下。

### 19.4 委外工單的特殊狀態

#### 提前交貨

若委外廠商提前交貨（早於 OutsourceExpectedDeliveryDate）：
- 作業員正常報工 QtyOk
- ETA 重算：後續工序時間提前
- 排程自動前移
- 若涉及後工序，可能改變派工優先序

#### 逾期交貨

若交期超過 ExpectedDeliveryDate：
- 警示：派工頁面標記「超期」
- 聯繫廠商確認最新交期
- ETA 重算，可能延期下游交期 → 可能影響訂單交期（觸發預警）
- 可選：自動建立委外催單紀錄（未來擴充）

#### 廠商變更

若中途決定換廠商（如原廠商無法交貨）：
- 建立新 OutsourcePurchaseOrder，舊 OPO 作廢
- 更新 WorkOrder.OutsourceSupplierCode 與 OutsourcePurchaseOrderId
- 廢止原派工，新建派工（或更新派工的 OutsourcePlannedStartAt/EndAt）
- 需重新排程

### 19.5 委外成本處理

#### 成本構成

```
委外工單成本 = 委外廠商成本 + MFG 內部成本（發貨、驗收）

StandardCost 計算：
  ├── MaterialCost: Σ(WorkOrderMaterial.Qty × UnitCost)
  ├── OutsourcingCost: OutsourcePurchaseOrder.LineAmount（委外採購單金額）
  └── InboundCost: 進貨、驗收等費用（可選，通常併入 OutsourcingCost）

ActualCost 回寫：
  ├── 來自 OutsourcePurchaseOrder 的實際進貨成本（含發票調整）
  ├── 來自 ProductionReport.QtyNg 的報廢成本
  └── 由 INV 模組在完工入庫時回寫 WorkOrder.ActualMaterialCost
```

### 19.6 委外 MRP 邏輯（RunJ）

```csharp
// RunJ：委外展開邏輯（見 §5.1 RunMPS 對比）
public async Task RunJ(
    string productId, decimal quantity, string batchId, DateOnly deliveryDate, string routingId)
{
    var product = await _context.Products.FindAsync(productId);
    var outsourceSupplier = product.PreferredOutsourceSupplier;  // 預設委外廠商
    
    // 1. 建立委外工單
    var workOrder = new WorkOrder
    {
        WorkOrderId = GenerateWorkOrderId(batchId),
        ProductId = productId,
        Quantity = quantity,
        Status = WorkOrderStatus.Planned,
        IsOutsource = true,
        OutsourceSupplierCode = outsourceSupplier,
        OutsourceExpectedDeliveryDate = deliveryDate.AddDays(-product.OutsourceLeadTime ?? 0),
    };
    
    // 2. 建立 Dispatch（無機台）
    var dispatch = new Dispatch
    {
        DispatchId = GenerateDispatchId(batchId),
        WorkOrderId = workOrder.WorkOrderId,
        MachineId = null,  // 委外無機台
        IsOutsource = true,
        Status = DispatchStatus.Pending,
        QtyTotal = quantity,
    };
    
    // 3. 建立用料需求（與 RunMPS 相同）
    // 讀 RoutingBom，建立 DispatchMaterial
    
    // 4. 產生 MrpOutsourceDemand 訊號（DemandType=WholeProduct）
    // OutsourcePurchaseOrder 不在此自動建立；由採購人員在「轉委外」步驟（§19.3 Stage 2）
    // 指定廠商/拆單後，OutsourcePurchaseOrderController 建立 OPO 並回填 WorkOrder.OutsourcePurchaseOrderId
    
    await _context.SaveChangesAsync();
}
```

---

### 19.7 委外簡化（小型廠，2026-10-12 裁決；分支 feat/outsource-simplified）

> 背景：MRP 後「子委外工單＋委外建議 → 轉委外採購單 → 委外發料 → 收貨 → 待驗倉 → 品檢合格入庫 → 母工單領料」對小廠太重。
> 使用者裁決：B1／B2／B3 照建議做；C（工序委外）**要扣該工序的投料**；途程工序委外停用；手機首頁不另立委外區。
> 三個廠別參數皆 MFG、Bool、**預設關**（`PlantParamCatalog`），不開＝行為完全不變。

| 項目 | 參數 | 行為 | 狀態 |
|---|---|---|---|
| B1 送外料收貨時自動扣 | `mfg_outsource_issue_on_receipt` | 確認收貨時，應發＝`Round(WOM.RequiredQty × 累計收回量 ÷ 工單量)`，扣掉 `IssuedQty − ReturnedQty` 後由 `IMaterialSlipService.CreateAndPostOutsourceIssueAsync` 產生並過帳委外發料單（不自帶交易；出庫＝品號類別預設倉儲，取不到擋下）。整品委外扣全部用料、工序委外只扣該工序 `OperationSeq` 的用料；手動發過的不重扣。發料單照常進在外原料結算 | 已做 d6969bce |
| B2 收料當場驗收 | `mfg_outsource_inspect_at_receipt` | **只在手機收料**帶不良數（`MobileReceiveLine.FailQty`）。確認時逐行 `IIqcInspectionService.SubmitAsync`（抽樣 VL-IV、Ac=0），不論簡化與否都在確認當下過帳：合格／免驗→明細儲位或品號類別預設儲位、回寫委外派工；不合格→本廠第一個待驗倉儲位（沒有就擋）、不回寫，後續走 NCR。桌機收貨與簽核中的單照原流程 | 已做 |
| B3 投產自動建委外採購單 | `mfg_outsource_auto_po` | `WorkOrderReleaseService`（Release 與 AutoRelease 共用）建 **Confirmed** 的 `OutsourcePurchaseOrder`，取代舊 PUR 採購單。廠商＝工單指定者；未指定時取當日有效、`ProductCode` 明確等於此品號的報價，**僅一家廠商**才採用。單價等經 `IOutsourceQuotationResolver.Resolve`（工序＝委外派工 `OperationSeq`）；無適用報價不建（不帶 0 元）。委外建議沿用或新建並累加 `TransferredQty`；交期＝工單預計交期，否則今日＋`LeadDays`。挑不出就**不建也不退回舊路徑**，留人工轉單 | 已做 |
| C 工序委外（派工「轉為委外」） | — | 判定＝OPO 行來源工單存在且 `!IsOutsource`（不看參數）。確認收貨即 Posted：**不入庫**、不經上架／IQC；該工序投料一律走 B1 同一套 `AutoIssueSendMaterialsAsync`（以 IssuedQty−ReturnedQty 抵廠內已領，不重扣）；加工費 `Round(StockQty×UnitCost×ExchangeRate)` 借 1401／貸 2152（AP 照常借 2152 沖平）；加工費＋結算 `CarriedInCost` 累計 `WorkOrder.ActualOutsourceCost`，完工入庫併入單位成本並寫 `WorkOrderActualCost.ActualOutsource`，**不進**人工／製費吸收分錄；派工完工量＝收回量－手機不良數。同一張收貨混收工序／整品委外擲 `Osr_MixedOperationOutsource`；手機 `IsOperation` 時不要求儲位 | 已做 |
| 途程工序委外 | — | 停用：`RoutingOperation.IsOutsource` 畫面鎖住（保留顯示舊值、提示改用半成品或派工轉委外）；`MrpEngine` 一律當廠內工序展開，不產生委外派工／委外建議 | 已做 |

- 依賴都是 `OutsourceReceiptService` 的選填建構子參數（`slips`／`locations`／`iqc`／`progress`）；**參數開著卻缺依賴＝擲錯**，不靜默跳過。
  整合測試 `容器解析委外收貨服務_…` 守住容器確實都注入。

## 20. 自動測試補充（委外與簽核相關）

### 20.1 委外單元測試補充

| 測試對象 | 典型測試案例 |
|---------|------------|
| `RunJ` 委外展開 | 確認 IsOutsource=true，無 MachineId；OutsourcePurchaseOrderId 初為 null |
| 委外 Dispatch 排程 | CD_BOR=1/2/3 各場景下，委外派工時間推算正確（無機台日曆查詢）|
| 委外報工累計 | ProductionReport 寫入時 QtyDone 累加；達到 QtyTotal 後自動 Closed |
| 進貨退出反向異動 | GoodsReturn.Post 建立反向 InventoryMovement，庫存正確扣減 |

### 20.2 委外整合測試

| 測試對象 | 驗證點 |
|---------|--------|
| 委外工單流程 | MRP 展開 → WorkOrder(IsOutsource=true) → Dispatch(MachineId=null) 正確 |
| OutsourcePurchaseOrder 建立 | 轉委外（§19.3 Stage 2）指定廠商/拆單後建立 OPO 並回填 WorkOrder.OutsourcePurchaseOrderId |
| 進貨簽核觸發 | GoodsReceipt.Post → InventoryMovement 建立，成品入庫 |
| 委外不良處理 | QtyNg > 0 時可建 GoodsReturn 或 NCR，庫存正確調整 |

---

## 21. 訂單↔MRP↔生產聯動管控規則（2026-05-29 定案）

> 本節為跨模組管控規格的補充。涵蓋：SO/IO 結案對 MRP 預留的釋放、已匯入訂單修改警示、PO Scheduled Receipts 納入供給面、WO Release 缺料警示、物料備齊確認閘口設計提案。

### 21.1 SO/IO 結案或作廢 → 釋放 MRP 預留（P1，Phase B 補強）

**問題**：MRP 展開匯入銷售訂單/內部訂單後，在 `InventoryReservation` 建立對應預留（`ReservationSourceType.MrpRunLine`）。若原始訂單事後被結案或作廢，預留未跟著釋放，導致庫存被虛耗鎖定。

**規格**：
- `SalesOrderController.CloseDoc`（及 `CloseLine`）、`InternalOrderController.CloseDoc`（及 `CloseLine`）執行後，呼叫：
  ```csharp
  await _reservationService.ReleaseBySourceAsync(
      ReservationSourceType.MrpRunLine,
      docId);  // SalesOrderId 或 InternalOrderId
  ```
- 訂單作廢（若有 Void 流程）時相同處理。
- `ReleaseBySourceAsync` 已實作於 `INV.InventoryReservationService`，直接注入呼叫。

**實作位置**：`ORD.SalesOrderController`、`ORD.InternalOrderController` 的 CloseDoc / CloseLine action。

---

### 21.2 MRP 已匯入之訂單被修改 → 跨模組警示（P2，Phase C 前完成）

**問題**：訂單已匯入 MRP 且展開出工單後，若作業員修改訂單數量或交期，工單目標與訂單失真，且不會自動重算。

**規格**：
- `SalesOrderController.Save`（PUT 編輯路徑）儲存前查詢是否有對應 `MrpRunLine`：
  - 若有且任一關聯工單 `Status >= InProgress`：回傳 HTTP 400，要求先暫停工單再修改。
  - 若有但無投產工單：在 HTTP 200 回應中附帶 `warnings: ["訂單已匯入 MRP，建議重跑以同步變更"]`，前端彈確認 dialog 後允許儲存。
- 同樣規則適用 `InternalOrderController.Save`。

---

### 21.3 PO Scheduled Receipts 納入 Fstock 供給面（P2，Phase B 補強）

> **實作狀態（2026-07-09 複查）：✅ 已實作**。`MrpEngine.cs`（約 line 838）已加入 `scheduledReceipts`（自 `PurchaseOrderLines` join `PurchaseOrders`，計已確認未到貨待交量）納入供給面，下述問題已解除。以下規格保留作設計依據。

**問題（原始）**：現行 `MrpEngine.Fstock` 只計算 `QtyOnHand − Active InventoryReservation`，未考慮已確認但尚未到貨的採購單，造成 MRP 對在途物料重複觸發採購需求。

**定義**：Scheduled Receipt = `PurchaseOrderLine`（`PO.ApprovalStatus = Approved`、`!IsClosed`、`ReceivedStockQty < StockQty`）的待交量 = `StockQty − ReceivedStockQty`（**庫存單位**；2026-10-27 前取採購單位 `OrderQty − ReceivedQty`，採購／庫存單位不同時與在手量相加會錯）。

**Fstock 修正公式**（在需求日期 D 的展望可用量）：
```
Fstock(productCode, D) =
    QtyOnHand
    + Σ PurchaseOrderLines（Approved && !IsClosed
                            && ExpectedDeliveryDate ≤ D
                            && ReceivedStockQty < StockQty
                            → 計入 StockQty − ReceivedStockQty〔庫存單位〕）
    − Σ InventoryReservations（Active，RequiredDate ≤ D）
```

**實作**：`MrpEngine.ReserveAvailableAsync`（或 `Fstock` 輔助方法）加入 PO 查詢。

> ⚠️ PO Scheduled Receipts 以**已核准**為條件（Draft/PendingApproval 不計入），避免未確認採購膨脹供給面。

---

### 21.4 WO Release 缺料警示（P3，Phase B→C 銜接，非阻擋）

**問題**：工單釋出時，MRP 預留庫存可能因時間推移已被其他需求消耗，導致投產時缺料中斷。

**規格**：
- `WorkOrderController.Release` 在執行簽核流程前，對每個 `WorkOrderMaterial` 執行快速可用量試算。
- 若任一物料 `QtyAvailable < Qty`：不阻擋釋出，在回應中附帶 `warnings` 清單。
- 前端顯示「以下物料庫存不足，建議補貨後再投產」；使用者可選擇繼續或取消。
- `Release` 流程照常執行，`ApprovalStatus` 照常推進。

---

### 21.5 物料備齊確認閘口（定案規格，2026-06-10；Phase C/D 已完成可落地）

> 評估：**值得做**。Phase C/D（排程/開工）已完成，開工 gate 可落地；與已實作的 §21.4（Release 時非阻擋缺料警示）**互補**——§21.4 是釋出當下的提醒，§21.5 是「開工前」的正式閘口，持久化狀態並可由系統參數強制。

**目標**：工單釋出且核准後、實際開工前，確認物理物料可用（不僅靠帳本預留）。解決預留漂移、下階半成品未完工兩個核心場景。

#### 新欄位

`WorkOrder.MaterialReadinessStatus`（`enum MaterialReadinessStatus`，持久化為 int）

| 值 | 說明 |
|---|---|
| `NotChecked`（預設）| 未執行確認 |
| `Ready` | 全部物料確認可用 |
| `Shortage` | 有物料缺料 |

> **v1 精煉**：原案的 `PartialShortage`（非瓶頸料可容許）需要 BOM「關鍵料」標記，現無資料來源，**v1 不產生**（保留 enum 值供日後擴充，但服務僅輸出 Ready/Shortage）。

#### 新服務：`IWorkOrderMaterialReadinessService.CheckAsync(workOrderId, ct)`

逐一確認每筆 `WorkOrderMaterial`（v1 精煉，對齊現有服務能力）：
1. **可用量**：**本工單可用量＝廠級自由量＋本工單自身預留** ≥ `RequiredQty`。
   - 廠級自由量：`IInventoryReservationService.GetPlantAvailableQtyAsync(plantId, productCode)`（在手 − 全部 Active 在手預留）。
   - 本工單自身預留：`GetReservedForWorkOrderAsync(workOrderId, productCode)`＝ `WorkOrderMaterial` 預留（MRP 為工單鎖的）＋ 該工單備料單（`MaterialStagingOrder.SourceType="WorkOrder"` 且 `SourceId=workOrderId`）的預留。
   - **備料單預留轉移**：備料單確認時，可用量預檢會加回該工單的 MRP 預留，並在建立備料預留後以 `ReduceBySourceAsync` 等額扣掉工單預留（同一批料不重複鎖）。轉移後那筆量改由備料單持有，仍計入本工單自身預留，故備完料不會反而判缺料。備料單作廢僅釋放本單預留，**不自動還原**給工單。
   - **為何要加回**：MRP 展開時已用 `ReservationSourceType.WorkOrderMaterial` 為該工單鎖住在手庫存，這筆預留同時被廠級自由量扣掉；不加回會使「MRP 鎖料越成功、工單越被自己的預留判為缺料」，重跑 MRP 後全數工單顯示缺料。與 §24.2 net-at-release 的「可用＝本單預留＋自由量」同一原則（§24.3 硬料閘同步套用）。
   - **儲位級可用量延後**（現無儲位級查詢 API，與 §21.4 同源）。
2. **下階半成品**：若 `WorkOrderMaterial.ChildWorkOrderId` 有值（自製半成品）→ 確認該子工單 `Status == Completed`（用既有欄位，較原案「CD_MRP=M」精確）。
3. Backflush 料（`IssueMethod == Backflush`）：不在開工前硬性要求（完工才沖），列為 Ready 不擋。

回傳 per-material 狀態清單 + 整體 `MaterialReadinessStatus`（任一缺即 `Shortage`），並寫回 `WorkOrder.MaterialReadinessStatus`。

#### UI

- WO 明細頁（`Released + Approved` 狀態）顯示「確認物料備齊」按鈕 + `MaterialReadinessStatus` badge（雙語、Bootstrap badge：Ready=綠／Shortage=紅／NotChecked=灰）。
- 點擊後以面板/區塊顯示 per-material 狀態表（✅ Ready／⏳ 待子工單完工／❌ 缺料）。

#### 與 Dispatch 開工連動（系統參數 `MFG_MATERIAL_GATE`）

`DispatchController` 開工（轉 `InProgress`／`SetupInProgress`）前置檢查：

| 值 | 行為 |
|---|---|
| `None` | 不檢查 |
| `Warning`（**預設**）| `Shortage` 時回傳 warning，前端確認後仍可繼續 |
| `Block` | `Shortage` 時阻擋（HTTP 400），需主管填 `ByPassReason` 覆寫 |

> 預設 `Warning` 確保導入不破壞現有開工流程；要 `Block` 由各廠自行調參。

---

### 21.6 MRP dirty flag（定案規格，2026-06-10；**已部分落地**）

**問題**：訂單修改、採購取消後，MRP 展開結果過時但無機制提醒重跑。

**規格**：
- `MrpRun.IsDirty: bool`（預設 false）✅ **已實作**（欄位 + migration `MFG_Sec21_Corrections`）。
- 觸發設 `IsDirty = true`（對有匯入該訂單之 `MrpRunLine` 的 `MrpRun`）：
  - `SalesOrderLine` 數量/交期變動 → ✅ **已實作**（`SalesOrderController` 儲存時）。
  - `InternalOrderLine` 數量/交期變動 → ✅ **已實作**（`InternalOrderController` 儲存時呼叫 `MarkRelatedMrpRunsDirtyAsync`，line 226/303；2026-07-09 複查）。
  - `PurchaseOrderLine` 結案或取消 → ✅ **已實作**（`PurchaseOrderController` line 550 設 `IsDirty=true`；2026-07-09 複查）。
- `MrpEngine.ReRunAsync`／展開後清除 `IsDirty` → ✅ **已實作**（`MrpEngine` line 193）。
- `MrpRun.Index`：dirty 的 Executed 單以橘色 badge「有異動，建議重跑」標示 → ✅ **已實作**（`Views/MrpRun/Index.cshtml:89` `@if (item.IsDirty)`；2026-07-16 複查確認）。

> **架構選擇（採事件寫入，不採 lazy）**：SO 觸發已用「儲存時寫 IsDirty」事件式落地且運作中，沿用同模式補 IO/PO 即可，無需改 lazy on-read（lazy 需另存展開快照基準，反而增複雜度）。§21.6 全數項目（含 Index badge）已補齊。

**實作時機**：Phase C 完成後，作為 MRP 可靠性補強項目。

---

## 22. 委外廠商報價主檔（OutsourceQuotation）

委外廠商的加工費報價，作為建立 `OutsourcePurchaseOrder` 時自動帶入單價的依據。

### 22.1 Entity 設計

```csharp
public class OutsourceQuotation  // DocType: OQ；全域主檔（不實作 IPlantEntity）
{
    public string QuotationId { get; set; }          // PK（採番 OQ-YYYYMMDD-###）
    public string SupplierCode { get; set; }          // FK → Company（IsSupplier）
    public string? ProductCode { get; set; }          // 整品委外用品號（可空=適用任意品號）
    public string? RoutingId { get; set; }            // 特定途程（可空）
    public int? OperationSeq { get; set; }            // 工序委外用序號（可空=適用所有工序）
    public decimal UnitPrice { get; set; }            // 加工單價（decimal 18,4）
    public string CurrencyCode { get; set; }          // 幣別
    public string UomCode { get; set; }               // 計價單位（通常=收回品庫存單位）
    public decimal? MinQty { get; set; }              // 最低計價量（可空）
    public decimal? MaxQty { get; set; }              // 最高適用量（可空）
    public int LeadDays { get; set; }                 // 此廠商此料的委外前置天數（供 MRP 倒推）
    public DateOnly EffectiveFrom { get; set; }       // 報價生效日
    public DateOnly? EffectiveTo { get; set; }        // 報價失效日（可空=長期有效）
    public string? Remarks { get; set; }
    public bool IsActive { get; set; } = true;
}
```

**Unique Index**：`(SupplierCode, ProductCode, RoutingId, OperationSeq, EffectiveFrom)`

### 22.2 報價自動帶入規則

建立 `OutsourcePurchaseOrderLine` 時，依下列優先序查詢有效報價（`IsActive=true, EffectiveFrom ≤ Today ≤ EffectiveTo`）：

| 優先序 | 條件 | 說明 |
|--------|------|------|
| 1（最具體）| SupplierCode + ProductCode + RoutingId + OperationSeq 全符 | 工序委外精確報價 |
| 2 | SupplierCode + ProductCode + RoutingId | 特定途程整體報價 |
| 3 | SupplierCode + ProductCode | 整品委外報價 |
| 4（最通用）| SupplierCode only（其餘空）| 廠商通用加工費 |

取優先序最高的一筆，自動帶入 `OPO.Line.UnitPrice`；用戶可手動覆寫。若無任何符合，欄位留空等待手工填入。

### 22.3 與 MRP 的整合

`RunJAsync` / `RunMpsAsync` 展開委外工單時，查詢報價取 `LeadDays` 用於：
- 計算 `OutsourceExpectedDeliveryDate = RequiredDate - LeadDays`
- 若有多家廠商有效報價，取 `LeadDays` 最短者作為 MRP 倒推基準（可調整為系統參數控制策略）

### 22.4 CRUD 畫面規格

- **查詢**：依廠商 / 品號 / 有效日期篩選；顯示目前有效/已失效狀態
- **新增/修改**：廠商 select2、品號 select2（可空）、途程/工序可選（工序委外用）
- **版本管理**：同一廠商+品號可有多筆（不同有效期），系統自動取有效期內最新一筆
- **匯入**：支援 Excel 批次匯入報價（`.xlsx`）

### 22.5 Phase 實作時機

| Phase | 內容 |
|-------|------|
| **Phase F**（委外完整流程）| `OutsourceQuotation` Entity + migration + CRUD 畫面；`OutsourcePurchaseOrderController` 整合自動帶入邏輯 |
| **Phase F 補強** | MRP 引擎整合 `LeadDays` 倒推；Excel 批次匯入 |
---

## 23. 排程料況閘口、生產準備與檢驗閘口（2026-06-12 定案）

> 來源：8 點系統目標對照評估（`.claude/reviews/scheduling-spec-review.md`）之使用者決策。
> 本節為定案規格；與 §21.5（開工物料閘口）、§21.3（Scheduled Receipts）、§7.4（負荷視圖）銜接。

### 23.1 排程料況閘口（定案：廠級預留，不做儲位/批號級）

> ⚠️ **修訂中（見 §24 決策草案，2026-06-20）**：下方步驟 2「排程層軟料閘」將由 §24 取代——
> 料況確認上移至**投產（Release）為硬閘**，排程只挑已投產工單故不再重查料況；在途改為需鎖定
> （供給端預留）。步驟 1（MRP 軟預留驅動採購建議）與步驟 3（發料扣帳）**不變**。

**鎖庫粒度定案**：維持 `InventoryReservation` 廠級軟預留，**不做**儲位/批號級揀貨預留
（鎖太早喪失揀貨彈性、排程變動即需解鎖重鎖）。三段式：

1. **MRP 展開**：廠級軟預留（現行，§10-6）。
2. **排程進入條件**（新增）：`SchedulingEngine` 排入前對派工所屬工單呼叫
   `IWorkOrderMaterialReadinessService.CheckAsync`（§21.5 同一服務，閘口前移到計畫層）：
   - `Ready` → 排入。
   - `Shortage` 但**在途供給**（§21.3 Scheduled Receipts ＋ 委外在途）之預計到貨日
     ≤ 該派工計畫開工日 → 可排，派工標記**待料風險**（甘特黃色）。
   - 否則 → 不排，列入未排清單（§7.4）並提示催料。

   「實際生產比 MRP 預估提早」情境自動涵蓋：開工日提前 → 到貨日比對不過 → 擋下或示警。
3. **發料時**實際扣帳（現行 posting）並沖銷預留（`ReleaseLineAsync`，§21.1）。

**兩層閘口分工**：本節為計畫層（排程）、§21.5 `MFG_MATERIAL_GATE` 為執行層（開工），互不取代。

**預留生命週期（明文化）**：MRP 重跑→釋放重建（現行）；SO/IO 結案/作廢→釋放（§21.1）；
工單作廢/結案→釋放；**排程延後→不釋放**。

### 23.2 等候線理論落地（定案：簡化，不做排隊模型）

1. **機台佇列視圖**：§7.4 負荷視圖增加每機台等候佇列——已排未開工派工清單＋
   預估等候時間（= 該機台前方佔用分鐘）。
2. **QueueTime/MoveTime 統計回填**：以實際報工（Session/Report 的工序間隔）統計
   中位數，產出 `RoutingOperation.QueueTime/MoveTime` 建議值報表＋一鍵套用——
   **不自動改值**，由工藝人員確認。MRP 前置期（§5.1 倒推）隨之越用越準。
   > **2026-10-06 落地**（原 `QueueTimeBackfillService` 直接回寫主檔、且背景撈 0 筆從未生效）：
   > `QueueTimeSuggestionService` 每晚 02:00 統計近 90 天、寫入 `RoutingQueueTimeSuggestions`（整批覆蓋），
   > 途程編輯畫面在工序「等候時間」下顯示建議＋「套用」（只填欄位，存檔才生效）。樣本＝同工單
   > 前一道最後下工 → 下一道第一次上工，以**工作日曆上班時間**計（廠別時區換算），**歸給前一道工序**
   > （排程把工序的搬運＋等候放在該工序之後），建議＝中位數 − 前一道 MoveTime（下限 0）；首道工序不統計
   > （舊作法以工單建立時間當到達＝量到 MRP 前置期）；樣本 < 5 不給建議。MoveTime 無法與等候分離量測，不給建議。

### 23.3 生產準備看板＋模具（定案：準備清單＋衝突警示，不進排程引擎硬約束）

1. **Dispatch 增 `ToolingCode`**：MRP 產派工時自 `RoutingOperation.ToolingCode`（既有，§3.1(8)）帶入。
2. **T-1 生產準備看板**：依排程順序列出指定日（預設明日）各機台派工的——
   備料需求（連動 MaterialStaging）、模具/治具、Setup 預估時間。
   倉庫（備料）與現場（換模）兩種角色視圖；手機提供同內容頁。
3. **模具衝突警示**：同一 `ToolingCode` 被排於重疊時段的不同機台 → 準備看板＋甘特警示，
   **不阻擋排程**。模具是否升級為排程引擎硬約束資源，視導入後實際衝突頻率再議。

### 23.4 檢驗閘口（定案：FPI/IPQC 不自動暫停派工）

- **FPI/IPQC 不合格**：僅記錄＋通知（既有 Push），**不**自動變更派工狀態；
  是否停工由現場人工判斷（既有 StopSession/暫停操作）。
- **FQC 維持硬閘口**：完工入庫前必須通過（`ProductionCompletionController.Complete` 現行已強制）。
- **術語定案**：完工檢驗一律稱 **FQC**；IQC 專指進料檢驗（採購/委外收貨）。文件與 UI 用語統一。

### 23.5 批量政策（定案 2026-06-12：系統參數切換 A／B，預設 A）

差異分析見 `scheduling-spec-review.md`〈批量政策差異分析〉。定案：MRP 展開依系統參數切換。

- **新系統參數 `mfg_lot_policy`**：`LotForLot`（**預設**，行為與現行完全相同）／`MinMultiple`。
  SystemParam 本為廠別設定 → 各廠可採不同政策。
- **`Product` 新增欄位**：`MinLotSize`（decimal?，null/0 = 不限）、`LotMultiple`（decimal?，null/0 = 不限）。
  兩欄位未設定的品號，即使參數為 `MinMultiple` 也等同 lot-for-lot——可逐品號漸進導入。
- **展開規則（僅 `MinMultiple` 模式生效）**：對**單一需求列**的淨需求 round-up：
  `批量 = RoundUpToMultiple(max(淨需求, MinLotSize), LotMultiple)`。
  **不跨訂單／跨需求列合併**（此為與 C 期間合併的本質區別）→
  `MrpRunLine` ↔ 訂單 **1:1 pegging 維持**，§21.1/§21.2/§21.6 聯動規則全部不用改。
- **計算順序**：先 round-up 成品批量 → 再依 §5.1 既有公式除以累積良率得投產量；
  原料需求與預留以放大後批量計算（自動正確）。
- **餘量歸屬**：（批量 − 淨需求）完工入庫後為自由庫存，下次 MRP 展開經 Fstock（§21.3）自然抵扣。
- **切換語意**：參數變更只影響之後的展開；已 Executed 的 MrpRun 不受影響；ReRun 依當下參數。
- **UI**：Product 維護表單加兩欄位（數量欄位依 UOM 小數規範）；系統參數維護加 key（雙語 resx）。
- **實作時機**：與 §21 待補項（IO/PO dirty flag、Index badge）同批，列入 MRP 可靠性補強。

### 23.6 替代料（定案：本階段不做）

BOM 缺料一律依 23.1（排程層）與 §21.5（開工層）閘口處理；替代料機制列入長期 roadmap。

---

## 24. 投產（Release）為定案閘門：釋出時定量、硬料閘、在途鎖定與排程入口（2026-06-20 定案，已實作）

> 來源：使用者與 AI 設計討論，接續 §5.1（逐工序倒推／庫出日）與 §23.1（料況閘口）。
> **狀態：已實作**（2026-06-20；§24.8 清單全數落地、build/test 綠燈）。標註【現況】＝已驗證的現行程式行為。
> 取代並回填修訂 §23.1 步驟 2。

### 24.1 核心理念：MRP 暫定 → 投產定案
- MRP 展開產出的工單量為**暫定／建議量**（依展開「當時」庫存淨算）。
- **投產（＝ WorkOrder Release，Planned→Released）為定案（firming）動作**：在此重算生產量、硬性確認料況、鎖定庫出量；通過後才進入排程與甘特。
- 投產前的 pipeline 於生產批／MRP 清單檢視，**不上甘特**（甘特＝已 committed 的現場排程）。
- **投產粒度＝整批（ProductionBatch）all-or-nothing（定）**：一個生產批必須**整批一起投產**，不可只投批內部分工單。批內多階工單同時 Released，先後順序由排程引擎依 BOM 深度自動排（半成品先於成品）。

### 24.2 投產時重算生產量（net-at-release）
- 公式：**生產量 ＝ 毛需求 − 投產當下可用量**。
- 範例：毛需求 100、MRP 時可用 30 → 暫定生產 70；投產時可用掉到 20 → 重算 **80**；升到 50 → 重算 **50**。
- 「可用量」只能用「**本工單預留 ＋ 真正自由量 ＋ 已鎖在途**（見 24.4）」，**不得吃別單預留**（否則往下重算會餓死別的訂單）。
- **整批 top-down 重算（定）**：因整批一次投產，對批內工單樹**由頂層往下重算**（與 MRP 同邏輯，但用即時庫存）——成品先依成品庫存淨算 → 推導新用料量 → 半成品再依半成品庫存淨算。**投產前批內無任何產出，故批內不會有「半成品超產」殘量問題**；跨批的零頭仍由 Fstock 自然淨掉（lot-for-lot ＋ §21.3）。
- 重算改變工單量 → 連動工序派工量、BOM 用料量、庫出量，於投產時一併 firming。
- **重算需人工確認（定）**：投產時若**重算量 ≠ 暫定量**，於投產畫面**標示該工單**（顯示「暫定 → 重算」量），人工確認後才套用定量；無變動之工單可直接投產。
- **重算為 0 不得投產（2026-10-13 定）**：投產當下可用量已滿足毛需求＝這張工單不需生產。`ApplyAsync`／`PreviewAsync` 回 `NotNeeded`，
  確認也不放行（原本只當「數量有變動」，確認後工單量、用料、派工全縮成 0，派工報工上限 0、工單永遠卡在已投產）；畫面提示改按「作廢」。
  整批投產時這張**連同其下階子工單**略過（父工單不生產，子工單的半成品沒人用），其餘照常，結果列出略過的工單。
  另：Push 發料單不開 0 量的用料行。拿來抵的那批成品鎖給原需求，見 24.2a。

### 24.2a 拿來抵的庫存與完工產出留給原需求（2026-10-13 實作）
- **問題**：重算把可用量淨掉、工單少做（或為 0 不做），但那批拿來抵的庫存沒人鎖——隨時被別張出貨／工單用掉，原訂單最後落空。
- **原需求是誰**（`IWorkOrderDemandReservation`，`Modules/MFG/Services/WorkOrderDemandReservation.cs`）：
  | 工單 | 鎖在哪 | 何時消耗 |
  |---|---|---|
  | 成品工單（`SourceMrpRunId`＋`SourceMrpRunLineSeq`） | `MrpRunLine` 預留（需求列記銷貨／內部訂單單號＋列號） | 出貨過帳 |
  | 半成品子工單（`ParentWorkOrderId`） | 父工單消耗它的那筆用料（`WorkOrderMaterial`，`SourceDocId`＝父工單、序＝`ChildWorkOrderId` 該筆的 `OperationSeq`） | 父工單發料（既有 `ConsumeForWorkOrderAsync`） |
  | 手動工單、`GrossDemandQty = 0` 的舊單 | 不鎖（無可追的原需求） | — |
- **鎖多少**：
  - 投產（含 NotNeeded、整批投產略過的工單）：`min(自由量, 毛需求 − 原需求已鎖量)`。重算公式的「本工單預留」＝原需求已鎖量，故重按投產不會疊加（冪等）。
  - 完工入庫：`min(完工量, max(0, 毛需求 − 原需求已鎖量))`。完工 ⑥ 只釋放**本工單自己的**用料預留，不動鎖給父工單／需求列的量。
- **出貨消耗**（`IInventoryReservationService.ConsumeForSalesOrderLineAsync`）：以出貨行的訂單＋列號找 MRP 需求列，由舊到新扣 Active 在手預留，扣完轉 `Consumed`。
  呼叫點：簡化模式 `GoodsShipmentController.Confirm` 過帳後；完整模式 `WarehouseApiController.PickGoodsShipment` 每次揀貨（與過帳同交易）。
  原本出貨完全不消耗 `MrpRunLine` 預留，只等訂單（列）結案才釋放——期間同一批貨對廠級可用量被扣兩次。
- **作廢回鎖**（`RestoreForSalesOrderLineAsync`）：出貨作廢、已出量回沖後，把已出的量鎖回最新一次 MRP 需求列；
  上限＝`訂單列庫存量 − 已出庫存量 − 已鎖量`（出貨時沒消耗到預留的，作廢不得憑空多鎖），訂單列已結案不鎖。
- **未做**：銷退（退回入庫）不回鎖——退回的貨通常要先判定品質，不一定能再出給同一張訂單。

### 24.3 硬料閘：料齊才可投產（整批擋住，不分批）
- 投產前呼叫 `IWorkOrderMaterialReadinessService`，**全料 Ready 才放行**；任一缺 → 整批擋住、不可投產（**不做**部分投產／殘量子派工）。
- **閘口範圍＝整批的葉層採購／委外料**：對批內**所有**工單的採購/委外用料（依重算後數量）做齊料檢查；**批內自製半成品不納入硬閘**（`PendingSubassembly` 不擋——它由批內排序生產，§23.1 同原則），只查 `Shortage`（採購料）。
- 代價（已接受）：重算往上會增加用料 → 更易被擋；原本 70 料齊、重算 80 缺料則連 70 都不投（寧可全等、不做半套）。
- backflush 料完工才沖，不納入此閘口；委外工單不走此閘口。

### 24.4 在途可算，但需鎖定（新增：供給端預留／PO pegging）
- 「可用」得納入**在途供給**，條件三者皆備：① 已核准未結案 PO／委外在途；② 預計到貨日 ≤ 消耗工序計畫開工日（§5.1 庫出日）；③ **未被其他需求佔用**。
- 【現況缺口】現行排程料閘已會「算」在途（`SchedulingEngine.GetInTransitCoverageDateAsync`：PO `OrderQty−ReceivedQty`、到貨日比對）但**不鎖定** → 多張工單可能重複計同一批在途。
- 【待新增】**供給端預留**：對 PO／委外在途 open 量可 peg，可用在途 ＝ `OrderQty − ReceivedQty − 已被其他需求 peg 量`。投產確認在途時建立此 peg；到貨入庫時 peg 轉為在手預留／沖銷。
- 現行 `InventoryReservationService` 僅對「在手」運作，需擴充供給端 peg（中等新工）。
- **到貨日為估計值、採購不自動下單（定）**：在途條件②的「預計到貨日」採 PO／委外行的 `DeliveryDate`，本質為**估計**；MRP 的**採購建議不自動轉 PO**，下單與否、時點由採購人工依產業/企業判斷（現行短交期下多為立即下單）。系統僅以估計到貨日做投產可行性判定，不替採購決策。

### 24.5 投產後即進排程（排程入口＝Released）
- 【現況】`SchedulingEngine` 已只挑 `Status == Released || InProgress` 的工單（§10-8）→ 投產後才上甘特、未投產不上甘特，**排程端無需改**。
- 排程時**不再重查料況**（投產已硬閘把關）→ §23.1 步驟 2 的「排程層軟料閘」由本節取代（見 24.7）。

### 24.6 派工全自動（優先序＋最早空閒機台）
- 【現況】排程依生產批優先序（`ProductionBatch.Priority`，可切 EDD／SPT／FIFO／LIFO）排序，批內深階先、同工作中心內取**最早可用機台**（`AllocateDispatchSlot`）。日常派工**零人工**。
- 前提：同一工作中心機台**同質**（速度相同）；若日後有快慢不同機台，此規則需改為考慮速度。
- 人工僅限例外：延遲時甘特側板 換機台／拆分／轉委外（第三階段已實作）。
- 模具維持 §23.3「警示不擋」，自動挑機台不避撞模、只警告。

### 24.7 料閘職責重整（取代 §23.1 步驟 2）
- **保留**：(A) MRP 軟預留（對在手）→ 驅動採購建議（與料閘無關，不受影響）。
- **新增**：供給端預留（對 PO／委外 open 量 peg）→ 在途安全分配（24.4）。
- **上移為硬閘**：料況確認由「排程層（軟）」上移至「投產（硬）」；可用 ＝ 在手（本單預留）＋ 已鎖在途。
- **取消**：排程時再評估料況（`SchedulingEngine.EvaluateMaterialGatesAsync` 的重判）冗餘，移除。
- **保留並升級語意**：甘特「待料風險」黃標**保留**，意義由「排程引擎賭在途會到（未鎖）」升級為「投產時已鎖在途、貨未到但預計準時」。

### 24.8 資料模型異動清單（已實作 2026-06-20）
- ✅ `WorkOrder`：加 **GrossDemandQty（毛需求量，decimal(18,6)）** ＋ **來源需求連結快照** `SourceMrpRunId` / `SourceMrpRunLineSeq`（參照 MrpRunLine），供投產重算錨定。MRP 展開（`MrpEngine.RunMpsAsync`／`RunJAsync`）寫入：頂層成品取 `line.RequiredQty`、子工單取該 BOM 之 `reqQty`（netting 前）。
- ✅ 供給端預留：擴充 `InventoryReservation`——加 `Category`（`ReservationCategory.OnHand`／`Supply`）＋ `SupplyDocType`/`SupplyDocId`/`SupplyLineNo`/`ExpectedArrivalDate`，並 `UseXminAsConcurrencyToken` 並發保護。服務新增 `GetAvailableInTransitAsync`（查可用在途，扣已 peg）／`PegSupplyAsync`（peg 在途）／`ConvertSupplyOnArrivalAsync`（到貨轉 OnHand）；`GetPlantAvailableQtyAsync` 改為只計 OnHand 預留。
- ✅ 投產核心抽出 `IWorkOrderReleaseService`（`PreviewAsync` 試算／`ApplyAsync` 定案）：重算量（net-at-release，可用＝在手本單預留＋自由量；GrossDemandQty≤0 之舊單不重算）→ 硬料閘（缺料查在途、補足則 `PegSupplyAsync` 鎖、否則 MaterialBlocked）→ 連動派工量／用料量／庫出量重算 → 鎖庫出（現有 MaterialSlip）→ 標準成本快照 → 送審/核准。`WorkOrderController.Release(id, confirmed)` 委派之；重算量≠暫定量且未確認回 `needsConfirm`。
- ✅ 批層投產：orchestration 集中於 `IWorkOrderReleaseService.ReleaseBatchAsync(batchId, confirmed)` all-or-nothing——先逐張 `PreviewAsync`，任一缺料整批不投、任一重算待確認回清單，全過才半成品先逐張 `ApplyAsync`（整批一筆交易，任一張途中拋錯則整批回滾；單張投產亦自成交易，因送審會先 SaveChanges，2026-11-04）；`ProductionBatchController.ReleaseBatch` 與獨立投產畫面共用此服務。前端 `ProductionBatch/_Details` 加「整批投產」按鈕、`WorkOrder/_Details` 加「暫定→重算」人工確認對話。
- ✅ 獨立投產畫面：`BatchReleaseController`（MenuKey `BatchRelease`，選單「生產批投產」）——Index 查待投產（仍含 Planned 工單）生產批、`Details` 側欄 `_ReleasePanel` 列出各工單暫定→重算量／料況／缺料並執行整批投產。依 new-screen 慣例（查詢列+分頁+排序、Anti-Forgery、4 resx、ProjectZMenuContributor 登錄）。
- ✅ 移除 `SchedulingEngine.EvaluateMaterialGatesAsync`／`GetInTransitCoverageDateAsync` 與排程料閘分支（含 `_readiness` 依賴）；`Dispatch.MaterialRisk` 改由投產時設定（在途已鎖、貨未到），排程不再改寫。

### 24.9 已釐清（2026-06-20 定案）
1. **投產 UI 粒度**：✅ **整批投產（ProductionBatch all-or-nothing）**，不可一批部分投（見 24.1）。
2. **重算後人工確認**：✅ **要**——標示有重算（量變動）的工單供人工確認後才套用（見 24.2）。
3. **在途到貨日／採購下單**：✅ 到貨日為**估計值**；採購建議**不自動下單**，由採購人工判斷（見 24.4）。供給端 peg 於**到貨入庫時沖銷、轉在手預留**（並發以 `RowVersion` 保護）。

> 以上三點已定案並隨 §24.8 一併實作（2026-06-20）。供給端 peg 於到貨入庫沖銷／轉在手預留由 `ConvertSupplyOnArrivalAsync` 處理（以 xmin 並發保護），反向回沖由 `RevertSupplyOnUnreceiveAsync` 處理。
> **採購進貨接點（2026-06-21）**：`PurchaseService.UpdatePoReceivedQtyAsync`（收貨遞增 `PurchaseOrderLine.ReceivedQty` 的唯一入口）每行收貨後呼叫 `ConvertSupplyOnArrivalAsync("PurchaseOrder", poId, poLineNo)`；`RestorePoReceivedQtyAsync`（GR 作廢／取消確認，回沖 `ReceivedQty`）呼叫 `RevertSupplyOnUnreceiveAsync(...)`，使在途量（`OrderQty − ReceivedQty`）與 peg 轉換雙向同步。
> **委外進貨接點（2026-06-21）**：`OutsourceReceiptController` 於 Confirm/Post（在途→在手）與 Void（在手→在途回沖）以 `supplyDocType="OutsourcePurchaseOrder"`、`supplyDocId=OPO 單號`、`lineNo=OpoLineNo` 對應 peg 轉換／還原。
> **委外在途 peg 產生端（2026-06-21）**：投產硬料閘的在途覆蓋同時計入採購在途與委外在途——`InventoryReservationService.GetAvailableOutsourceInTransitAsync`（已確認未作廢 OPO 行 `Qty − Posted 委外進貨已收量`，委外應交日 ≤ 庫出日，扣已 peg 之委外 Supply）；`WorkOrderReleaseService` 缺料時**先以採購在途補、餘量再以委外在途補**（`FindCoveringOpoAsync` 依應交日累計），分別建立 `PurchaseOrder` / `OutsourcePurchaseOrder` 兩類 Supply peg。`GetAvailableInTransitAsync` 的已 peg 扣減已收斂為僅扣 `PurchaseOrder` 類，避免與委外重複扣。委外進貨接點至此完整生效。
> 已知限制：轉換／回沖以「供給來源行」為單位整批轉，未按部分收貨量 pro-rata。

---

## 25. 製造簡化模式（mfg_simplified_mode）：免投產、免排程直接生產（2026-07-28 實作完成）

> 來源：使用者需求「擴大簡化模式的範圍」——從既有的**庫存簡化模式**（`inv_simplified_mode`，
> 建單/核准即過帳）延伸到製造流程。對應決策討論 2026-07-27。
>
> **狀態（2026-07-28）：Phase 1／2／3 全數實作完成。**
> - ✅ **Phase 1｜參數 ＋ 免投產**——`mfg_simplified_mode` 參數、設定畫面、
>   `WorkOrderReleaseService.AutoReleaseAsync`、`MrpEngine.AutoReleaseIfSimplifiedAsync`（見 25.3／25.9）。
> - ✅ **Phase 2｜派工免排程直接開工**——`IMfgSimplifiedModeService` 放行 `Pending`、
>   手機待辦納入 `Pending`、`IOperationSequenceGuard` 工序順序守門（見 25.4）。
> - ✅ **Phase 3｜訂單自動展 MRP**——`IMrpRunExecutionService` 抽出、
>   `IMrpAutoExpandService` 於訂單核准後以**狀態判定**自動建單展開（見 25.5）。
>
> 本節所有【現況】標註均已對照程式碼驗證。

### 25.1 目標流程與適用對象

簡化模式的目標流程（相對 §5.1 → §24 → §7 的完整流程）：

```
訂單核准 ──自動──> MRP 展開 ──自動──> 工單 Released + 派工 Pending
                                          └─> 現場直接開工 ──> 報工 ──> 完工入庫
                                    （免投產閘門、免排程分派；開工/完工/報工照舊）
```

**適用對象**：訂單少量、單一產線或機台同質、當天接單當天生產、不做產能排程與投產定案的小型工廠。
**不適用**：需要跨訂單批量合併、需要投產前硬料閘把關、需要甘特產能排程的場景。

**與庫存簡化模式（`inv_simplified_mode`）的關係**：兩者是**獨立的兩個維度**（過帳時機 vs 流程關卡），
不合併為單一參數。既有 `MrpEngine` 已讀 `inv_simplified_mode` 調整在途計算（待上架 GR／待下架 GS），
該行為不受本節影響。

### 25.2 系統參數

【已實作 2026-07-28】

- `mfg_simplified_mode`（true/false，預設 **false**＝完整流程），
  常數 `ProjectZSystemParamKeys.MfgSimplifiedMode`（`Models/ProjectZSystemParam.cs`）。
- 設定畫面在 `Views/SystemParam/_ProjectZSystemParams.cshtml` 的**「製造設定」卡片**
  （與庫存設定分卡呈現，因兩者是不同維度），後端讀寫在
  `Filters/SystemParamEnhancementFilter.cs`（GET Index 投影、POST Save 正規化持久化）。
- 未設定（null）一律視同完整流程——**既有部署升級後不得因為新參數而改變行為**，
  由 `SystemParamEnhancementFilterTests` 的 `null` / `""` / `"false"` 三個 InlineData 釘住。
- 新增 ProjectZ 專屬系統參數的完整程序見 `.claude/LOCAL_RULES.md`〈系統參數 — ProjectZ 的擴充方式〉。

### 25.3 免投產：跳過與保留清單（本節最關鍵）

【現況】投產 `WorkOrderReleaseService.ApplyAsync` 在 Planned→Released 之間夾帶 8 件事。
**「免投產」不等於不呼叫 Release**——其中三件是下游必需品，跳過會讓成本、料帳與委外流程斷掉：

| # | 投產做的事 | 簡化模式處置 | 理由 |
|---|---|---|---|
| ① | 淨量重算（net-at-release，§24.2） | **跳過** | MRP 展開時已淨算過一次；簡化模式接單即生產，展開→生產之間庫存變化可忽略 |
| ② | 硬料閘（缺料整批擋單，§24.3） | **跳過** | 由開工時的 `mfg_material_gate` 軟閘接手（見 25.4） |
| ③ | 在途 peg（`PegSupplyAsync`，§24.4） | **保留**（2026-07-27 定案） | 「鎖在途但不擋單」比「完全不鎖」資料更正確：`AutoReleaseAsync` 既然已走 `WorkOrderReleaseService`，保留 peg 的邊際成本極低，可避免多張工單重複認領同一批在途。差別只在於 peg 的結果**不用來擋單**（②仍跳過） |
| ④ | **標準成本快照**（§16.2） | **必須保留** | 跳過則 `StandardMaterialCost/LaborCost/OverheadCost` 全 0 → 成本差異分析與 CST 成本鏈全空 |
| ⑤ | 簽核判定（`ApplyApprovalAsync` 前半，§18.2） | **預設跳過** | 這正是簡化的目的；直接視同已核准。**2026-10-20 例外（A4）**：有簽核流程且明確設了門檻（成本超過者）或強制送審條件成立時仍送審——沒設門檻、沒開條件時行為不變 |
| ⑥ | **Push 料領料單自動產生**（`CreatePushMaterialSlips`，§17.3） | **必須保留** | 【易漏】此呼叫**藏在 `ApplyApprovalAsync` 核准分支內**（非獨立步驟）。跳過簽核＝連帶跳過領料單 → `IssueMethod=Push` 的用料永遠沒有領料單、庫存不扣、`ActualMaterialCost` 短計 |
| ⑦ | `Dispatch.MaterialRisk` 設定（§24.7） | **保留**（隨 ③ 定案調整） | 初版規劃為「跳過」，理由是無 peg 即無語意；③ 定案保留 peg 後，「已鎖在途、貨未到」語意仍成立，故一併保留 |
| ⑧ | **委外工單自動建採購單**（§19.3 Stage 2） | **必須保留** | 跳過則委外工單沒有對應 OPO，整條委外流程斷掉 |

【已實作 2026-07-28】`WorkOrderReleaseService.AutoReleaseAsync`：保留 ③④⑥⑧、跳過 ①②⑤⑦，
直接設 `Status = Released`、`ApprovalStatus = Approved`。
由 `MrpEngine.AutoReleaseIfSimplifiedAsync` 在 `ExpandAsync` 結尾對本次展開的 Planned 工單逐張呼叫
（**半成品先於成品**，與 §24.1 `ReleaseBatchAsync` 同序）。

實作時一併做的兩個拆分（原本兩個副作用都藏在別的方法裡，不拆就會靜默漏掉）：

- **③ peg 迴圈抽成 `PegInTransitAsync`**，供 `ApplyAsync` 與 `AutoReleaseAsync` 共用。
  方法本身一律「能鎖多少鎖多少」，差別只在呼叫端是否拿結果去擋單：
  `ApplyAsync` 補不足回 `MaterialBlocked`，`AutoReleaseAsync` 鎖了但不擋。
- **⑥ `CreatePushMaterialSlips` 從 `ApplyApprovalAsync` 拆出為 `ApproveDirectly`**
  （設 `Approved` ＋ 產生 Push 領料單，兩件事綁在一起，兩條路徑共用）。
  這正是 25.14-① 指出的耦合，已在本次一併解掉。

料況據實回填：`MaterialReadinessStatus` 依 `ShortageProducts` 設 `Shortage`／`Ready`，
**不因為無硬閘就一律標 `Ready`**（否則畫面顯示與實際不符）。
缺料不擋單，但以 `MrpRunResult.Warnings` 逐張回報供畫面提示。

**專屬迴歸守門**（§25.10 要求的四項正向斷言，全部已補；三個「必須保留」項目的共同特性是
「不做也不會報錯、只會靜默少資料」，只能靠正向斷言釘住）：

| 斷言 | 測試 |
|---|---|
| ④ 成本快照非 0 | `WorkOrderReleaseServiceTests.AutoRelease_TakesStandardCostSnapshot`、`MfgSimplifiedModeTests.Expand_SimplifiedOn_TakesStandardCostSnapshot` |
| ⑥ Push 領料單存在且品號/數量正確 | `AutoRelease_PushMaterial_CreatesMaterialSlip`、`Expand_SimplifiedOn_CreatesPushMaterialSlip` |
| ⑧ 委外工單有 OPO | `AutoRelease_OutsourceWorkOrder_CreatesPurchaseOrder` |
| 完整模式不受影響（Planned／無領料單） | `Expand_SimplifiedOff_WorkOrderStaysPlanned`（`null`／`""`／`"false"` 三態） |

**狀態機不動**（定）：不新增狀態、不讓工單停在 `Planned`。理由是全庫有 18 處查詢以
`WorkOrderStatus.Released` 為前提（排程引擎、WIP 報表、儀表板、派工開工），
改狀態機的擴散面遠大於收益。簡化模式只是**自動且無條件地**通過這道閘門。

### 25.4 免排程：派工 Pending 可直接開工

【現況】派工單在 **MRP 展開時就已建好**（`MrpEngine`：自製於工序迴圈內建立、委外單一工序建立），
狀態 `Pending`。「派工直接生產回報」缺的只是狀態流轉，不是缺資料。現行三道卡點：

1. `SchedulingEngine` 只挑 `Released || InProgress` 的工單排程，把派工由 `Pending` 推進 `Scheduled`。
2. `DispatchController.StartSession` / `MobileMfgController.StartSession` 只接受
   `Scheduled / ReadyToStart / InProgress`，`Pending` 一律拒絕。
3. 手機派工清單過濾寫死 `Scheduled / ReadyToStart / InProgress`
   → **簡化模式的派工在手機待辦上根本不會出現**（不改此處，前兩點修了也沒用）。

【已實作 2026-07-28】判定收斂於 `Modules/MFG/Services/MfgSimplifiedModeService.cs`
（`IMfgSimplifiedModeService`），**桌機與手機共用同一份**——不讓各 controller 自己讀參數再各寫
一份狀態清單，正是為了避免重蹈 §26 FPI 閘口的雙軌漂移。

| 卡點 | 處置 |
|---|---|
| ① `SchedulingEngine` 只排 `Released/InProgress` | **不動**。簡化模式本來就不排程；工單已由 §25.3 自動投產為 `Released`，要排也排得動 |
| ② 兩條 `StartSession` 拒絕 `Pending` | 改呼叫 `CanStartSessionAsync(status)`：完整模式維持 `Scheduled/ReadyToStart/InProgress`，簡化模式**額外**放行 `Pending`（其餘狀態不因此放寬） |
| ③ 手機派工清單寫死三個狀態 | 改用 `ActionableDispatchStatusesAsync()`。Web `DispatchController.Index` 本來就沒有硬過濾（statusFilter 為選填），故不需改 |

- 料況閘 `mfg_material_gate`（None/Warning/Block，預設 Warning）**維持在開工時檢查，不搬家**
  ——因為簡化模式仍保留開工動作（見 25.7）。§25.3-② 跳掉的硬料閘正是由它接手。

**FPI 閘口（定：仍然尊重）**
【現況缺口】FPI 閘口目前是**手機端專屬**：`MobileMfgController.SetupDone` 依工序 `RequiresFpi`
把派工設為 `PendingFPI`、`StartSession` 擋 `PendingFPI`、FPI 量測 Pass 後放行為 `ReadyToStart`；
而 Web 版 `DispatchController.SetupDone` 直接設 `ReadyToStart`、**不看 `RequiresFpi`**，
Web `StartSession` 也**不擋 `PendingFPI`**。此為既有雙軌漂移。
【待實作】免排程後現場會從 `Pending` 直衝開工、繞過 Setup，因此 FPI 守門**必須做在 `StartSession` 本身**
（工序 `RequiresFpi = true` 且尚未通過 FPI → 拒絕開工並導向首件檢查），不可依賴 Setup 流程順帶；
同時補齊 Web 端的漂移，兩條入口共用同一份判定。

**工序順序守門（定：擋下）**
【現況】完整流程由排程保證前工序先做（`PrevDispatchId` 鏈結＋時段配置）。
【已實作 2026-07-28】`Modules/MFG/Services/OperationSequenceGuard.cs`（`IOperationSequenceGuard`）：
本工序累計產出（`QtyDone + qtyOk`）不得超過前工序 `QtyDone`，超過即拒絕。

- **只在簡化模式生效**。完整模式加上這道守門會擋掉工序重疊等既有排法，屬於改變既有作業流程。
- `PrevDispatchId` 由 `MrpEngine` 展開時就寫入（非排程階段），故免排程下仍然可用。
- 兩種放行例外：首道工序（`PrevDispatchId` 為空）、前置派工查無資料——
  後者是刻意的，守門本身不該因資料異常而變成停產原因。
- 接上三條報工入口：桌機 `DispatchController.AddReportCoreAsync`（報工／完工共用核心）、
  手機 `POST /reports` 與 `PATCH /sessions/{id}/complete`（手機的 `AddReportCoreAsync` 回傳
  實體而非錯誤字串，故守門置於兩個呼叫端，與既有 `OVER_REPORT` 守門同位置）。
- 訊息鍵 `ReportPrevOperationShortage`（雙語）；手機錯誤碼 `PREV_OPERATION_SHORTAGE`。

### 25.5 訂單自動展 MRP

【現況】MrpRun 是**人工建立的單據**：Draft → 挑選可匯入訂單列 → `Edit(doExecute=1)` 執行展開。
銷售訂單核准只做 `MarkRelatedMrpRunsDirtyAsync`（§21.6 標記 dirty 提示重跑），不觸發展開。
展開的交易包覆與「採購需求落地為請購單」（`CreatePurchaseRequestFromDemandsAsync`）
目前寫在 `MrpRunController.Edit` 內。

【已實作 2026-07-28】

- 執行邏輯抽為 `IMrpRunExecutionService`（`ExecuteAsync` / `ReRunAsync`），
  含交易包覆、請購單去重／對帳、預檢錯誤回填。畫面與自動展開共用同一份。
- `IMrpAutoExpandService.ExpandApprovedOrdersAsync()`：建立一張 MrpRun 納入
  「已核准訂單的未展開剩餘量」並立即展開；備註標為「訂單核准自動展開（製造簡化模式）」。
- **訂單「修改」不自動重跑**（定，維持不變）：只標 `IsDirty` 提示人工重跑。
  `ReRun` 有 §10-5 的 L1/L2/L3 足跡分級閘門，自動觸發會踩到已投產工單與已收貨採購單。
  （訂單**增量**則會被下一次自動展開視為新的未展開剩餘量而補展一張新單，這不是重跑。）

**以狀態判定、不以事件判定**（本節最關鍵的設計選擇）

自動展開的觸發條件是「已核准訂單還有多少沒展開」，不是「剛剛發生了核准事件」。
剩餘量口徑刻意與 MRP 執行畫面的「可匯入訂單列」完全一致：
剩餘 = 訂單量 − Σ(該訂單明細在已執行 MRP 單的需求量，排除已作廢生產批)。

這樣做的兩個理由：

1. **冪等**。重複呼叫安全——不冪等的話同一張訂單每次存檔就再展一次，數量翻倍累加，
   而且不會有任何錯誤訊息。這是本節最容易出錯、也最不會報錯的地方，測試逐項釘住
   （重複呼叫／訂單增量只展增量／作廢批視同未展開）。
2. **不需要在每一條核准路徑上都掛鉤**。系統有多條路徑會讓訂單變成已核准
   （儲存時自動核准、送審無流程直接核准、自動跳簽、簽核畫面核准），
   逐條掛鉤只要漏掉一條就是靜默少資料。狀態判定下漏掛一條只會**延後**到下一次呼叫，不會遺漏。

【已知取捨】目前的呼叫點是 `SalesOrderController` / `InternalOrderController` 的
儲存（`targetStatus == Approved`）與送審兩處。若部署**有設訂單簽核流程**、且核准動作發生在
簽核畫面，該張訂單要等到下一次有訂單被核准時才會一併展開。未加背景服務補這一段是刻意的：
背景執行沒有使用者情境，建立的 MrpRun 缺 `CreatedByRoleId` 會被資料權限過濾隱藏
（見 `feedback-seed-dataauth-createdbyroleid`）。且簡化模式的部署本來就以「訂單不走簽核流程」為前提。

**取捨（須讓導入者知情）**：一張訂單一次展開，等於**放棄跨訂單批量合併**——
MRP 的合併採購／合併生產效益消失，請購單會變得零碎，`mfg_lot_policy`（LotForLot/MinMultiple）
的批量擴大仍有效但作用範圍縮到單張訂單。

### 25.6 發料方式：維持 Push（預設）或改 Backflush

【現況】兩件事必須先釐清，否則會做出錯誤的料帳設計：
1. `MrpEngine` 建立 `WorkOrderMaterial` 時 `IssueMethod = bom.IssueMethod ?? MaterialIssueMethod.Push`
   ——**fallback 寫死 Push**。
2. Push 料**並非沒有發料流程**：投產核准時 `CreatePushMaterialSlips` 會彙整
   `IssueMethod=Push` 的用料自動產生一張 **MaterialSlip(Draft，Category=InternalIssue)**，
   由現場確認過帳後扣庫存。
3. 系統參數 `mfg_default_issue_method`（廠別預設發料方式）**已宣告但全庫沒有任何程式讀取**（死參數）。

【定案：預設維持 Push，不強制改 Backflush】理由是「不影響原本的作業流程」優先於「少一張單」：
只要 `AutoReleaseAsync` 保留 25.3-⑥ 的 `CreatePushMaterialSlips`，
簡化模式的料帳路徑與完整流程**完全相同**，領料單照樣自動產生、照樣人工確認過帳。
這是資料正確性最穩妥的選擇。

【可選加強，須另行決策】若導入者連領料單都不想做，才切到 Backflush：
- 把 `mfg_default_issue_method` 死參數接上（BOM 未指定 → 讀廠別預設 → 再 fallback Push），
  導入時設為 `Backflush`。
- 後沖時機沿用 `BackflushTiming.AtCompletion`，完工彙總時由 `ProductionCompletionService` 以
  `PostDocSimplifiedAsync`（TypeCode `IO` 生產領料）過帳。
- **代價**：材料成本認列時點由「發料時」延到「完工時」，在製品（WIP）期間帳上看不到材料已投入；
  跨月生產會讓材料成本落在完工月，影響月結。此為**會計政策變更**，不可預設開啟。

> 結論：25.6 不是 Phase 1 的必帶配套（原規劃有誤，已修正）。Phase 1 只要保留 ⑥ 即可；
> Backflush 是獨立的可選項，預設關閉。

### 25.7 明確不改的部分

- **開工／完工（ProductionSession）保留**（定，2026-07-27 使用者確認）：簡化模式仍走上工／下工，
  工時以真實 `StartAt`／`EndAt` 計算，`ProductionCostAccrualService` 完全不動
  → 實際人工／製造費用、OEE、產能利用率、期末實際費率重估均維持現狀，不失真。
- **完工入庫不改**：`ProductionCompletionService` 早已走 `PostDocSimplifiedAsync` 立即過帳，
  由 `WorkOrderCompleted` 事件驅動，本身即為簡化行為。
- **派工完工自動判定不改**：報工累計達量 → `Closed` → 全派工完工 → 工單 `Completed`（§4.2/§4.3）。
- **模具打次、ETA 重算、Push 通知不改**：均掛在既有報工核心上，自動沿用。

### 25.8 已定案決策（2026-07-27）

1. **開工/完工是否保留**：✅ **保留**。原提案曾考慮「派工直接報工、自動偽造 session」，
   使用者確認現場仍會做開工/完工，因此以真實起訖時間計算工時，與現行完全相同。
2. **工序順序**：✅ **擋下**（見 25.4）。
3. **FPI／IPQC**：✅ **仍然尊重 FPI**，不隨簡化模式跳過。品質關卡不在簡化範圍內。
4. **狀態機**：✅ **不動**，簡化模式只是自動通過投產閘門（見 25.3）。
5. **參數獨立性**：✅ 與 `inv_simplified_mode` **分開兩個參數**（見 25.1）。
6. **在途 peg**：✅ **保留**——鎖在途但不用來擋單（見 25.3-③）。原提案曾列為「跳過」，
   評估後認為資料正確性收益遠大於實作成本，改為保留。
7. **發料方式**：✅ **維持 Push**，`AutoReleaseAsync` 必須顯式產生 Push 領料單；
   Backflush 為預設關閉的可選項（見 25.6）。

### 25.9 實作分段建議

**Phase 1｜參數 + 免投產 — ✅ 已實作（2026-07-28）**

| 項目 | 落點 |
|---|---|
| 參數常數 | `Models/ProjectZSystemParam.cs` → `MfgSimplifiedMode` |
| 設定畫面 | `Views/SystemParam/_ProjectZSystemParams.cshtml`「製造設定」卡片 ＋ 同名 resx 雙語 |
| 讀寫 | `Filters/SystemParamEnhancementFilter.cs`（Index 投影／Save 正規化） |
| 自動投產 | `Modules/MFG/Services/WorkOrderReleaseService.cs` → `AutoReleaseAsync`（含拆出的 `PegInTransitAsync` / `ApproveDirectly`） |
| 觸發點 | `Modules/MFG/Services/MrpEngine.cs` → `AutoReleaseIfSimplifiedAsync`（`ExpandAsync` 結尾） |
| 測試 | `MfgSimplifiedModeTests`（9）、`WorkOrderReleaseServiceTests` AutoRelease 段（8）、`SystemParamEnhancementFilterTests` MFG 段（11） |

- `MrpEngine` 的 `IWorkOrderReleaseService` 刻意設為**必填**建構參數（非 optional）——
  可為 null 時簡化模式會靜默不投產，錯誤只在下游「工單永遠停在 Planned」才顯現。
- 25.6 的 Backflush 為**可選項，預設不做**（本次未做，維持 Push）。

**Phase 2｜派工免排程直接開工 — ✅ 已實作（2026-07-28）**

| 項目 | 落點 |
|---|---|
| 開工放行／待辦狀態判定 | `Modules/MFG/Services/MfgSimplifiedModeService.cs` |
| 工序順序守門 | `Modules/MFG/Services/OperationSequenceGuard.cs` |
| 桌機接點 | `DispatchController.StartSession`、`AddReportCoreAsync` |
| 手機接點 | `MobileMfgController.StartSession`、`GetDispatches`、`CreateReport`、`CompleteSession` |
| 測試 | `MfgSimplifiedStartAndSequenceTests`（24） |

- FPI 守門上移至 `StartSession` 並補齊 Web 端雙軌漂移，已於 §26 先行完成。

**Phase 3｜訂單自動展 MRP — ✅ 已實作（2026-07-28）**

| 項目 | 落點 |
|---|---|
| 展開編排 | `Modules/MFG/Services/MrpRunExecutionService.cs`（由 `MrpRunController` 抽出，291 行） |
| 自動展開 | `Modules/MFG/Services/MrpAutoExpandService.cs` |
| 呼叫點 | `SalesOrderController` / `InternalOrderController` 的 Save（核准）與 Submit |
| 測試 | `MrpAutoExpandServiceTests`（11，冪等為重點） |

- 排最後的理由（成立）：它會大量產生副產物（每次核准一個 MrpRun ＋ 請購單），
  等 Phase 1/2 穩定後再開，避免同時排查兩層問題。

### 25.10 測試規劃

比照 `inv_simplified_mode` 已完成的四層覆蓋（2026-07-27 補齊，可直接沿用範式）：
1. **設定畫面**：`SystemParamEnhancementFilterTests` 加 `mfg_simplified_mode` 的投影／正規化／
   未勾選存 false／參數從未設定視同 false。
2. **引擎兩模式對照**：`MrpEngineTests` 驗證簡化模式展開後工單為 `Released` 且成本快照非 0、
   委外工單有 OPO；完整模式仍停在 `Planned`。
3. **守門 controller 測試**：`Pending` 開工放行／拒絕、FPI 擋開工、工序順序不足擋報工，
   Web 與 Mobile 兩條入口各測。
4. **真 PG 端到端**：訂單核准 → 自動展開 → 開工 → 報工 → 完工入庫，
   斷言庫存真的異動、`StockMovement` 方向與 TypeCode 正確、工單成本非 0。
   須沿用既有規則：測試寫 `SystemParams` 後必須在 `Dispose` 以
   `ISystemParamService.SetAsync(key, null)` 清回（該表無廠別、全庫共用，且服務有 `IMemoryCache` 快取層）。

**專屬迴歸守門（不可省）**——本節的三個「必須保留」項目全部是「不做也不會報錯、只會靜默少資料」，
單靠既有測試抓不到，須逐項訂正向斷言：
- `AutoReleaseAsync` 後 `StandardMaterialCost + StandardLaborCost + StandardOverheadCost > 0`（④）。
- `AutoReleaseAsync` 後該工單**存在一張 Push MaterialSlip(Draft)**，明細品號/數量與 `IssueMethod=Push` 用料一致（⑥）。
- 委外工單 `AutoReleaseAsync` 後 `OutsourcePurchaseOrderId != null`（⑧）。
- **完整模式不受影響**：同一組種子在參數未設定時，工單仍為 `Planned`、無 MaterialSlip、無 OPO。

**文件同步（與程式同一 PR）**：
- 本節（§25）標註由「規劃中」改為「已實作」並補實際檔案/行號。
- `.claude/specifications/inventory.md`／`ord-module.md` 若涉及連動，補交叉引用。
- **使用者手冊**：`wwwroot/docs/user/` 的「系統參數」畫面頁必須補上 `mfg_simplified_mode` 的說明
  （用途、預設值、開啟後哪些步驟消失、與 `inv_simplified_mode` 的差異）。
  手冊頁與畫面的對應由 `HelpDocResolver` 測試守門，漏補會紅燈。
- 若 Phase 3 落地，`SalesOrder` 核准行為改變，需同步 ORD 相關手冊頁。

### 25.11 已知取捨與風險

- ~~在途重複認領~~：已由 25.3-③「保留 peg」消除（2026-07-27 定案）。
- **無投產硬料閘**：缺料只會在開工時以 `mfg_material_gate` 軟性提示（預設 Warning ＝不擋）。
  導入時若要保守，建議把 `mfg_material_gate` 設為 `Block`。
- **批量合併效益消失**：見 25.5 取捨。
- **模式切換**：參數為全域即時生效，切回完整模式時**已在簡化模式下 Released 的工單不會退回 Planned**，
  它們沒有經過硬料閘與在途 peg。切換時點應選在無在製工單時，或接受既有工單以簡化語意走完。

### 25.12 Core 邊界與擴充點（新增系統參數的正確做法）

`SystemParam` 畫面與 `SystemParamService` 皆為 **Core 主體**，本節所有變更**零 Core 改動**。
以 `upstream/main` merge-base 實測各接觸點歸屬如下：

| 檔案 | 歸屬 | 動作 | 邊界判定 |
|---|---|---|---|
| `Models/ProjectZSystemParam.cs` | **ProjectZ 新增** | 加一行 `MfgSimplifiedMode` 常數 | 位於保護目錄 `Models/` 內，但不存在於 upstream 歷史 → pre-commit「企業新增檔案」放行 |
| `Filters/SystemParamEnhancementFilter.cs` | **ProjectZ 新增** | Index 加投影、Save 加持久化 | `Filters/` 不在保護清單 |
| `Views/SystemParam/_SystemParamModuleExtensions.cshtml` | Core 既有檔（Core 版為空注解） | 加「製造設定」卡片 | **架構明定的擴充點**（CLAUDE.md「禁止覆蓋」表），且已在 `scripts/core-drift-baseline.txt` |
| `Resources/Views/SystemParam/_SystemParamModuleExtensions.{zh-TW,en-US}.resx` | ProjectZ 自有 | 加 4 個 key | 不在保護清單 |

**不需要的東西**（重要）：
- ❌ **不需 migration**：`SystemParams` 是既有 key-value 表，新增參數只是多一列資料，無 DDL。
- ❌ **不需 seeder**：未設定（null）即代表完整流程，與升級前行為一致。刻意**不**寫入預設列，
  讓「從未設定」與「明確設為 false」在資料上可區分。
- ❌ **不需新畫面／選單**：沿用既有系統參數畫面 → 無 `ProjectZMenuContributor`、
  無 `RolePermissionSeeder` 補鍵、無新 MenuKey。
- ❌ **不需改 `Program.cs`**：Filter 早已由 `Modules/BAS/BASModule.cs` 註冊
  （`AddScoped` + `options.Filters.AddService`），新參數只是同一個 Filter 內多幾行。

**寫入的正確姿勢**：一律經 `ISystemParamService.SetAsync/SetBatchAsync`，
**不可直接對 `DbContext` 增刪 `SystemParams` 列**——服務以 `IMemoryCache`（singleton，10 分鐘）
快取整份參數，只有這兩個方法會 `_cache.Remove`，直接動 DbContext 會留下髒快取。

### 25.13 相容性與資料正確性保證

**（A）不影響原本作業流程**——所有行為變更都必須包在同一個判斷內，且預設為關：

| 接觸點 | 參數關閉時的行為 | 保證方式 |
|---|---|---|
| `MrpEngine` 展開結尾 | 不呼叫 `AutoReleaseAsync`，工單維持 `Planned` | 單一 if 包住，無其他路徑 |
| `StartSession`（Web/Mobile） | `Pending` 仍拒絕開工 | 放行條件加在 `Pending` 這一支，不動既有三個狀態 |
| 派工清單過濾 | 仍只列 `Scheduled/ReadyToStart/InProgress` | 過濾條件以參數決定是否併入 `Pending` |
| 報工工序順序守門 | **兩模式都套用**（見下） | 完整流程由排程保證順序，守門恆為真、不改變行為 |
| FPI 守門 | 完整流程行為不變；Web 端補齊漂移後**變嚴格** | 見下方註記 |
| 投產畫面／整批投產 | 完全不動 | 簡化模式下只是沒人會用到 |

> ⚠️ **兩處例外必須明講**（非純加法）：
> 1. **工序順序守門**建議**兩模式都套用**——完整流程本來就由排程保證順序，守門不會擋到正常單；
>    但若既有資料/測試裡有「跳工序報工」的情境，會由容忍變成拒絕。實作前須先掃既有測試與正式資料。
> 2. **FPI 守門補齊 Web 端漂移**會讓 Web 端從「不檢查 `RequiresFpi`」變成「檢查」。
>    這是修既有缺陷（手機端本來就檢查），但對只用 Web 的既有部署是**行為收緊**，
>    須列入升級說明。若要更保守，可讓 Web 端補齊只在簡化模式生效，完整模式維持現狀——
>    但這等於保留雙軌漂移，不建議（見 25.14-②）。

**（B）資料正確性**——本節設計對每個帳務出口的影響與守門：

| 帳目 | 影響 | 守門 |
|---|---|---|
| 庫存帳（材料） | **無影響**：Push 領料單照常產生、照常過帳（25.3-⑥） | 端到端測試斷言 MaterialSlip 存在且明細相符 |
| 庫存帳（成品） | **無影響**：完工入庫路徑完全未動 | 端到端測試斷言 `StockMovement` |
| 標準成本 | **無影響**：快照保留（25.3-④） | 斷言三項標準成本 > 0 |
| 實際人工／製費 | **無影響**：開工/完工保留，工時用真實起訖時間（25.7） | 沿用既有 `ProductionCostAccrualService` 測試 |
| 實際材料成本 | **無影響**：Push 發料成本與後沖成本的彙總邏輯未動 | — |
| 在製品（WIP）數量 | **有風險→已擋**：免排程可能跳工序報工 | 工序順序守門（25.4） |
| 在途配貨 | **有風險→已知接受**：無 peg 會重複認領（25.11） | 文件化；可選保留 peg（25.14-③） |
| 會計期間鎖 | **無影響**：過帳仍走 `PostDocSimplifiedAsync`／MaterialSlip 既有路徑 | — |
| 簽核軌跡 | **有變更**：工單不再有釋出簽核記錄 | 這是簡化模式的定義，非缺陷；須列入導入說明 |

### 25.14 可優化／待討論事項（2026-07-27 提出）

以下為規劃過程中查證出的既有問題與優化提案，**與本節實作相關但可獨立決定**：

① **`CreatePushMaterialSlips` 與簽核耦合**（建議修，Phase 1 順手做）
   領料單產生藏在 `ApplyApprovalAsync` 的核准分支內，語意上兩件不同的事綁在一個方法。
   建議抽成獨立步驟由 `ApplyAsync` 直接呼叫，`AutoReleaseAsync` 共用。
   不改也能做，但會留下「改簽核邏輯時靜默影響料帳」的長期地雷。

② **FPI 閘口缺陷** → 已展開為獨立規劃 **§26**（2026-07-27）。
   查證後根因比「Web 漏實作」更深：閘口以 `PendingFPI` 狀態為準，而該狀態只能經備料進入，
   因此**不做備料直接開工即可繞過，Web／Mobile 皆然**。§26 提出改以「是否存在 Pass 的 FPI
   檢驗單」為守門事實，並補齊 Web 端整條路徑。**須先於 §25 Phase 2 實作。**

③ ~~簡化模式是否仍保留在途 peg~~ → **已定案：保留**（2026-07-27，見 25.3-③ 與 25.8-6）。

④ **`SystemParamEnhancementFilter.Save` 逐一 `SetAsync`**（小優化）
   目前 8 個參數各呼叫一次 `SetAsync` ＝ 8 次 DB 往返 ＋ 8 次快取失效。
   服務已提供 `SetBatchAsync(Dictionary)`，改批次可一次搞定。加入 `mfg_simplified_mode`
   會變成 9 次，正是順手改的時機。行為等價，既有測試可直接驗證。

⑤ **`_SystemParamModuleExtensions.cshtml` 的 Core 漂移持續擴大**（治本建議）
   此檔為 Core 既有檔（已在 drift baseline），每加一個模組設定區塊漂移就更大一點。
   可改為：此檔只保留一行 `@await Html.PartialAsync("_ProjectZSystemParams")`，
   實際內容移到 ProjectZ 自有 partial → 漂移永久收斂為一行。
   與 `_DocDetailsUrls.cshtml` 的既有模式一致。

⑥ **`mfg_default_issue_method` 為死參數**（建議清理或接上）
   已宣告但全庫無任何讀取點。要嘛接上（25.6 可選項），要嘛移除常數避免誤導。

⑦ **`MrpRunController.Edit` 內含業務 orchestration**（Phase 3 前置）
   交易包覆 + `RunAsync` + `CreatePurchaseRequestFromDemandsAsync` 寫在 controller，
   與 P1-2 結構收斂的目標相悖。Phase 3 需要從訂單端呼叫，正好一併抽成服務。

---

## 26. FPI 首件檢查閘口修復（2026-07-27 定案，已實作）

> 來源：§25 簡化模式規劃過程中查證發現的**既有缺陷**。與簡化模式無關、可獨立實作，
> 但 §25.4 的「簡化模式仍尊重 FPI」定案依賴本節先修好。
> **狀態：已實作**（2026-07-27；26.2 全數落地，單元 2266 綠、整合 92 綠）。
> 【現況】＝修復前的行為（保留供回溯），【已實作】＝落地內容。

### 26.1 根因：閘口以「狀態」為準，不以「事實」為準

【現況】現行 FPI 閘口的完整鏈路：

```
備料完成 SetupDone ──(op.RequiresFpi)──> Dispatch = PendingFPI
                                              └─> 手機 fpi.page.js 列出待首件
                                                    └─> FPI 量測 Pass ──> ReadyToStart（解鎖）
                                                        FPI 量測 Fail ──> OnHold
開工 StartSession ──(Status == PendingFPI)──> 拒絕
```

問題出在**閘口條件是 `Status == PendingFPI`，而 `PendingFPI` 只能經由 `SetupDone` 進入**。
因此存在兩個獨立缺口：

**缺口 A（守門條件錯，Web／Mobile 皆中）**
派工 `Scheduled` 時**不做備料直接開工**，`StartSession` 的狀態檢查為
`Scheduled or ReadyToStart or InProgress` → 通過，FPI 完全被繞過。
手機端亦然（`MobileMfgController.StartSession` 只擋 `PendingFPI` 這一個狀態）。
**即使工序 `RequiresFpi = true`，只要不點備料就能開工。**

**缺口 B（Web 端整條未實作）**
| 環節 | Mobile | Web |
|---|---|---|
| `SetupDone` 依 `RequiresFpi` 設 `PendingFPI` | ✅ `MobileMfgController.SetupDone` | ❌ `DispatchController.SetupDone` 一律設 `ReadyToStart` |
| `StartSession` 擋 `PendingFPI` | ✅ | ❌ 無此判斷 |
| FPI 待檢清單 | ✅ `wwwroot/mobile/mfg/fpi.page.js` | ❌ 無 |
| FPI 量測與 Pass/Fail 提交 | ✅ `MobileMfgController` 檢驗單端點 | ❌ `InspectionOrderController.Submit` **只處理 Fail→OnHold，無 FPI Pass→ReadyToStart 分支** |
| FPI 檢驗單建立 | ✅ 量測時自動建立 | ❌ 無任何建立路徑 |

⚠️ **因此不可只修缺口 B 的前兩列**：若 Web `SetupDone` 開始設 `PendingFPI` 而後三列沒補，
純 Web 部署會**死結**——派工進得去 `PendingFPI`、出不來。

### 26.2 修復方案（已實作 2026-07-27）

**① 守門改為以事實判定（核心修正，解缺口 A）**
`StartSession`（Web 與 Mobile 兩條入口）判定改為：

> 該派工所屬工序 `RequiresFpi == true`，且**不存在** `SourceDocId = DispatchId`、
> `Status = "Pass"` 的 FPI `InspectionOrder` → **拒絕開工**。

如此不論是否經過備料、不論 `Status` 為 `Scheduled` 或（簡化模式的）`Pending`，
閘口都成立。`PendingFPI` 狀態**保留**，但降級為「待辦清單與 UI 顯示用途」，不再是唯一守門依據。
- ✅ `Modules/MFG/Controllers/DispatchController.StartSession`、
  `Api/Mobile/MobileMfgController.StartSession` 皆改為呼叫 `IFpiGateService.EvaluateAsync`。
- ✅ **僅首次開工檢查**（`Status != InProgress`），續工不重複阻擋，與既有物料閘口一致。

**② Web 端補齊（解缺口 B）**
- ✅ `DispatchController.DoneSetup`：依閘口判定設 `PendingFPI` 或 `ReadyToStart`（對齊 Mobile）。
  已通過首件者重跑備料**不再退回** `PendingFPI`，避免二次卡住。
- ✅ `InspectionOrderController.Submit`：補 FPI 分支——Pass → 派工 `ReadyToStart`、Fail → `OnHold`。
  IPQC 既有分支（僅生產中派工因不良轉 `OnHold`）維持不變。
- ✅ **新增 Web FPI 待檢與量測**：沿用既有 IQC 三件套模式，於 `InspectionOrderController` 新增
  `PendingFpi` / `FpiForm` / `SubmitFpi` 三個 action，搭配 `PendingFpi.cshtml`、`_FpiForm.cshtml`
  與 4 個 resx；入口為檢驗單 Index 右上「FPI 待檢登錄」按鈕（與「IQC 待檢登錄」同一模式）。
  **沿用既有 MenuKey `InspectionOrder`**，故不需新增選單項、權限鍵或 `RolePermissionSeeder` 補鍵。
- ✅ 待檢清單同樣以事實判定列出（工序要求 FPI 且無 Pass 單），未經備料的派工也會出現——
  否則品管無從登錄，缺口 A 修好後反而變成新的死結。

**③ 兩端共用判定**
✅ 已抽 `IFpiGateService`（`Modules/MFG/Services/`，`EvaluateAsync` 回傳
`FpiGateResult(RequiresFpi, HasPassed)` 含 `IsBlocked`；`ApplyResult` 套用 Pass/Fail 的派工狀態），
由 `DispatchController`、`MobileMfgController`、`InspectionOrderController` 三處共用。
`OrderType`（Phase G 才加的欄位）與 `InspectionType` 字串**兩者皆比對**——早期資料只有後者，
漏比對會讓已通過首件的派工被誤擋。

### 26.3 相容性影響（必須列入升級說明）

本修復對既有部署是**行為收緊**，非純加法：

| 情境 | 修復前 | 修復後 |
|---|---|---|
| 工序 `RequiresFpi=true`，不做備料直接開工 | 允許（缺陷） | **拒絕** |
| Web 端備料完成 | 直接 `ReadyToStart` | 進 `PendingFPI`，需先過 FPI |
| Web 端 FPI Pass | 無此功能 | 派工解鎖 |
| 工序 `RequiresFpi=false` | 不變 | 不變 |

**升級前置檢查（上線前必做）**：正式庫若已有 `RequiresFpi = true` 的工序，且現場實際作業從不做 FPI，
修復後會立即擋住開工。上線前須先查 `RoutingOperation.RequiresFpi = true` 與
`WorkOrderOperation.RequiresFpi = true` 的分布，確認是「本來就該擋」還是「主檔設錯」。

過渡參數 `mfg_fpi_gate`（`Strict`/`Legacy`）**目前未實作**——刻意不做，因為它會讓缺陷以設定的形式
繼續存在。若某部署盤點後確實需要緩衝，再另行決策並設落日期限。
在此之前，資料未整理完的部署應以「把不需首件的工序取消勾選」處理，而非放寬閘口。

### 26.4 測試（已實作 2026-07-27）

✅ `SmartFactory.MES.Tests/MFG/FpiGateTests.cs`（16 支）
- 閘口判定：工序不要求 FPI／要求但無單／Pending 單／Fail 單／Pass 單／
  **早期資料 `OrderType = null` 只有 `InspectionType="FPI"`**／他張派工的 Pass 不解鎖本張。
- 開工守門：**`Scheduled` 跳過備料直接開工被擋**（缺口 A 的迴歸守門）、`PendingFPI` 被擋、
  通過後放行、不要求 FPI 的工序照常放行（既有行為迴歸）。
- 備料完成：要求 FPI → `PendingFPI`；不要求 → `ReadyToStart`；已通過者 → `ReadyToStart`（不二次卡住）。
- `ApplyResult`：Pass → `ReadyToStart`、Fail → `OnHold`。

✅ `SmartFactory.MES.Tests/QCR/InspectionOrderFpiTests.cs`（11 支）
- 待檢清單：列出跳過備料的 `Scheduled` 派工、排除已通過者、排除不要求 FPI 的工序。
- `SubmitFpi`：Pass 建單並解鎖、Fail 轉 `OnHold`、已通過則拒絕重複登錄、
  已有 Pending 單則就地補完不另開一張。
- `Submit`（既有待檢單路徑）：**FPI Pass → 解鎖**（缺口 B 的核心迴歸）、FPI Fail → `OnHold`、
  **IPQC Pass/Fail 既有行為不受影響**（防止 FPI 分支誤攔生產中的派工）。

✅ 雙軌一致性：既有 `SmartFactory.MES.IntegrationTests/Mobile/MobileFpiInspectionTests.cs`
（真 PG，Pass→`ReadyToStart`／Fail→`OnHold`／clientId 幂等）改用共用服務後**行為不變、全數通過**，
即為手機端與桌機端同語意的證據。

✅ 手冊：`wwwroot/docs/user/qcr/inspection-order.html` 補「FPI 首件檢查（桌機登錄）」段與閘口說明；
`wwwroot/docs/user/mfg/dispatch.html` 開工步驟補首件閘口說明與交叉連結。
沿用既有 MenuKey，`HelpDocResolver` 對應不需新增。

**驗證**：單元 2266 綠（新增 27 支）、整合 92 綠。

### 26.5 與 §25 的關係

§25.4「簡化模式仍尊重 FPI」的實作**直接依賴 26.2-①**：簡化模式下派工由 `Pending` 直衝開工、
不經過備料狀態，若守門仍以 `PendingFPI` 狀態為準則形同虛設。
**實作順序：§26 先於 §25 Phase 2。**

> ⚠️ **這段講的是狀態機，不是「可以跳過品質關卡」。** 簡化模式省掉的是**備料這個狀態轉換**，
> **首件檢查（FPI）閘口照樣擋**——§25.4 的整個要求就是「簡化模式仍尊重 FPI」，
> 26.2-① 之所以要先做，正是為了讓閘口在沒有備料狀態時仍然有效。
> 任何「簡化模式下品質關卡會被跳過」的說法都與本節相反。
