# 採購與委外管理領域規格（pur-domain.md）

> **版本**：2026-06-10（校正：OutsourceReceipt 已實作雙狀態三層、NOW/Phase A 標已完成、VendorPriceList 索引與查詢 tie-break、OI 語意、SRM 前置欄位）  
> **範圍**：採購（PUR）、委外採購（OSP）、供應商管理相關功能的業務設計與 Phase 規劃。  
> （Entity/Controller/View 慣例與 API 形態見 **ord-module.md**；本文件聚焦業務領域設計，不重列 API 端點。）  
> 本文件與 `mfg-domain.md`（§19 委外完整流程）互補：mfg-domain 描述從 MRP 觸發委外的流程，本文件描述採購/委外管理的獨立維護作業。

---

## 1. 模組結構

```
採購管理（PUR）                     委外管理（OSP）
├── 請購單（PurchaseRequest）        ├── 委外採購單（OutsourcePurchaseOrder）  ← 已在 MFG，Phase F 遷移
├── 採購單（PurchaseOrder）          ├── 委外發退料（OutsourceMaterialDoc OO/OR） ← 已在 INV，Phase F 遷移+補 FK
├── 進貨單（GoodsReceipt）           ├── 委外進貨單（OutsourceReceipt）         ← Phase F 新建（成品+IQC）
├── 退貨單（GoodsReturn）            └── 委外重工單（OutsourceRework）          ← Phase F 新建
└── 廠商單價目錄（VendorPriceList）  ← NOW

共用（BAS 擴充）
├── 供應商資格狀態（Company.QualificationStatus）  ← NOW
└── 合格供應商評分卡（VendorEvaluation）           ← Phase G
```

> **委外發退料（OO/OR）整合說明（2026-06-01 定案）**：
> INV 模組現有 `OutsourceMaterialDoc`（TypeCode=OO 發料 / OR 退料）已涵蓋「外發退料單」功能，
> Phase F **不另建新 Entity**，直接補 `OutsourcePurchaseOrderId` + `DispatchId` FK 後遷移選單。
> `OutsourceReceipt`（委外進貨單）的語意是**收回加工成品 + IQC**，與 OR（退回原料）完全不同，需新建。

---

## 2. 廠商單價目錄（VendorPriceList）— NOW

### 2.1 業務說明

- 記錄特定廠商對特定品號的議定採購單價（含幣別、最小訂購量、有效期間）
- 建立後，**採購單建行時自動帶入最接近有效的議定單價**（可手動覆寫）
- 單價超過 `Product.StandardCost` 的現有邏輯已移至 `ResolveSaveStatusAsync`，
  未來可升級為比對廠商議定單價（有議定價時以議定價為基準）

### 2.2 Entity 設計

```
VendorPriceList（全域，非 IPlantEntity — 廠商單價全公司共用）
├── Id                int PK
├── SupplierCode      varchar(20) FK→Company (IsSupplier)  必填
├── ProductCode       varchar(30) FK→Product               必填
├── CurrencyCode      varchar(20) FK→Currency              必填
├── UnitPrice         decimal(18,6)                        必填 > 0
├── MinQty            decimal(18,4)?                       最小訂購量（null=無限制）
├── ValidFrom         DateOnly                             必填
├── ValidTo           DateOnly?                            null=長期有效
├── IsActive          bool default=true
├── Remarks           varchar(500)?
├── CreatedAt / UpdatedAt
└── [唯一索引] (SupplierCode, ProductCode, CurrencyCode, ValidFrom, MinQty)
```

> ⚠️ **唯一索引需含 `MinQty`**：§2.3 允許「同廠同品同幣同 ValidFrom 多筆（MinQty 階梯不同）」，
> 若索引不含 `MinQty` 則該情境會違反唯一約束。`MinQty` 可為 null，PostgreSQL 唯一索引視 null 為相異值，
> 若要對「無下限」階梯也唯一，應以 `MinQty` 預設 0（非 null）或加 `COALESCE` 表示式索引。

### 2.3 查詢邏輯（單價自動帶入）

採購單建行時，查詢邏輯：

```sql
-- 取「適用本次訂購量的階梯中、最高門檻」且「有效起日最新」者
SELECT UnitPrice, CurrencyCode, MinQty
FROM VendorPriceLists
WHERE SupplierCode = @supplier
  AND ProductCode  = @product
  AND IsActive     = true
  AND ValidFrom   <= @orderDate
  AND (ValidTo IS NULL OR ValidTo >= @orderDate)
  AND (MinQty IS NULL OR MinQty <= @orderQty)   -- 只取門檻 ≤ 訂購量的階梯
ORDER BY ValidFrom DESC,
         MinQty DESC NULLS LAST                  -- 同有效日取最高適用門檻（階梯價）
LIMIT 1
```

> tie-break 須由 SQL 落實：先 `ValidFrom DESC`（最新議定），再 `MinQty DESC`（符合訂購量的最高階梯門檻），
> 並以 `MinQty <= @orderQty` 過濾不適用階梯。先前版本只 `ORDER BY ValidFrom DESC` 無法選對階梯，已修正。
> （`TOP 1` 為 SQL Server 語法，PostgreSQL 用 `LIMIT 1`。）

### 2.4 UI 規格

- **選單**：採購管理 > 廠商單價目錄
- **Index**：可依廠商/品號/有效日期搜尋；搜尋後才顯示列表
- **Side Panel**：含廠商選擇、品號選擇、幣別、單價、最小訂購量、有效起訖日
- **慣例測試**：R1 / R4b / R5 / R6d / R9（金額欄位）

---

## 3. 供應商資格狀態（Company.QualificationStatus）— NOW

### 3.1 業務說明

在 `BAS.Company` 加入資格欄位，供採購/委外下單時參考。
非阻擋性（可繼續下單），僅顯示警示訊息；完整評核功能於 Phase G。

### 3.2 欄位設計

```
Company（擴充）
├── QualificationStatus  SupplierQualificationStatus  default=Unqualified
│     0 = Unqualified（未評核）
│     1 = Qualified（合格）
│     2 = Probation（觀察期）
│     3 = Suspended（暫停）
└── QualificationExpiry  DateOnly?  資格有效期（null=永久，Qualified 時常用）
```

### 3.3 採購/委外單的整合

- 選廠商時，若 `QualificationStatus == Suspended` → 前端顯示紅色警示 badge
- 若 `QualificationStatus == Probation` → 顯示黃色警示
- 若 `QualificationExpiry < today` → 提示「資格已過期，請重新評核」
- **不阻擋儲存**（由採購主管自行判斷）

---

## 4. Phase F：委外管理擴充（與 mfg-domain.md §19 Stage 4-6 對齊）

> **前置條件**：Phase D（報工/完工）完成後執行。

### 4.1 委外進貨單（OutsourceReceipt）— ✅ 已實作（Phase A）

> **狀態（2026-06-10 校正）**：本單據**已實作**，且結構為**雙狀態 + 三層**（比照 GoodsReceipt 慣例，見 ord-module.md §4.2），
> 非早期草案的「單狀態 + IQC 欄位」。以下依實際程式碼（`Modules/PUR/Models/OutsourceReceipt*.cs`）校正。

```
OutsourceReceipt（廠別 IPlantEntity, IDataAuthEntity）
├── OutsourceReceiptId        varchar(30) PK（DocNumberConfig DocType=OutsourceReceipt，單號前綴 OR，格式 OR{YYMM}-{Seq5}）
├── OutsourcePurchaseOrderId  varchar(30) 必填（→ MFG.OutsourcePurchaseOrder，僅字串，應用層驗證，不設 EF 導航）
├── ReceiptDate               DateOnly
├── SupplierCode              varchar(20) FK→Company（IsSupplier）
├── TypeCode                  varchar(20) = "OI"（INV 過帳類別：委外入庫）
├── TypeCategory              varchar(20) = "Outsource"
├── Status                    DocStatus（Draft/Confirmed/Posted/Void）        ← DocStatus
├── ApprovalStatus            DocumentApprovalStatus（Draft/PendingApproval/Approved/Rejected）  ← 簽核狀態
├── ApprovalRequestId         string?
├── CurrencyCode / ExchangeRate(18,6) / TotalAmount(18,4)                     ← 含成本金額
├── Remarks / CreatedBy / PlantId / CreatedAt / UpdatedAt / CreatedByRoleId / OrganizationUnitPath
└── Lines: OutsourceReceiptLine[]
     ├── (OutsourceReceiptId, LineNo) 複合 PK
     ├── OpoLineNo            int?（→ OutsourcePurchaseOrderLine）
     ├── ProductCode / UomCode / StockQty / StockUomCode
     ├── ReceiptQty           decimal(18,4)
     ├── UnitCost / CurrencyCode / ExchangeRate / TaxCode / TaxRate / LineAmount / TaxAmount / TotalAmount
     ├── WhCode / LocationCode / LotNo
     ├── QtyPutAway           decimal(18,4)（= Σ LocationDetails.Qty）
     ├── ExcessScrapPenalty / AllowedScrapRate（超耗罰款，Phase A 成本欄位）
     └── LocationDetails: OutsourceReceiptLocationDetail[]（第三層：Id PK、LocationCode、WhCode、LotNo、Qty、Cascade Delete）
```

> **合約允收損耗率的來源鏈**（2026-09-04 補齊）：
> `OutsourceQuotation.AllowedScrapRate` → `OutsourcePurchaseOrderLine.AllowedScrapRate`
> → `OutsourceReceiptLine.AllowedScrapRate`，與 `UnitPrice` 同一條快照鏈、同一組 4-level 特殊度。
> 放在報價主檔是因為它是**合約條款**：談價時一起談，換一份報價就換一組條件。
>
> 三者皆 `decimal?`，且 **null ≠ 0**：`null` ＝ 合約未約定損耗條款 → **不扣款**
> （沒談過的錢不能自己扣）；`0` ＝ 明確約定零容忍 → 短交即全額求償。兩者財務結果完全相反。
> 在此之前這個欄位只存在於收貨明細上、上游沒有任何來源（委外採購單與報價都沒有這個欄位、
> 全庫沒有一個寫入點），於是恆為 0，而扣款計算被 `allowedRate > 0` 擋著——
> **超耗扣款在正式環境結構性不可達**（人工驗收 W2-F14）。
> 流程測試看不到，因為它們直接把值填在自己 new 出來的收貨明細上。

> ⚠️ **與早期草案差異**：實際 entity **沒有** `IqcStatus`/`IqcRemark`/`NgQty`/`NgReasonCode`/`DispatchId` 欄位。
> IQC 來料檢驗由 **QCR 模組**獨立處理（NCR 流程）；委外派工連結走 `OpoLineNo`（委外採購單行），不直接掛 Dispatch。
> 若日後確需在收貨單上記錄不良數，須以 migration 補欄並回頭更新本節（先驗程式再改文件）。

**業務流程（雙狀態）**：
1. 委外廠商完工交件 → 建委外進貨單（Draft），選委外採購單帶入待收行
2. （視設定）Submit 送簽核 → Approved
3. Confirm → DocStatus: Confirmed（前提 Draft；下架後 QtyPutAway>0 不可 UnConfirm）
4. Post → 觸發 INV Posting（TypeCode=OI 委外入庫），回填 `OutsourcePurchaseOrderLine.ReceivedQty`、對應 Dispatch `QtyDone`
5. Void → 反向 InventoryMovement（前提 Posted）

### 4.2 委外重工單（OutsourceRework）

> **狀態（2026-07-10 實作回寫）**：畫面/服務**已實作**（第二增量，接續 entity+migration 基礎）。
> - `OutsourceReworkController`（Index 查詢/分頁/排序/Export、側欄 CRUD、Send/Receive/Void/Delete POST，MenuKey=OutsourceRework，PUR 選單）
> - 成本模型純函式 `OutsourceReworkCostCalculator.Compute(party, pct, reworkCost, processingFee, dp)`——儲存時後端計算，表單即時預覽僅供參考
> - **實作補欄**（比原欄位表多兩欄，migration `PUR_OutsourceRework_CostBaseAndDocSeed`）：`ReworkCost` / `ProcessingFee` decimal(18,4)＝扣款/應付的**計算基礎全額**；持久化基額才能在編輯時無損重算，避免從結果金額反推的捨入漂移
> - `Status` 實作為 Draft/Sent/Received/**Void**（作廢限 Draft/Sent；Received 為終態）；無簽核流
> - 採番 `DocNumberConfig` seed：DocType=OutsourceRework、Prefix=RW（HasData，隨 migration 落庫）
> - 單元測試 26 支（計算器 EP/BVA＋Controller 狀態守門）；使用手冊 `pur/outsource-rework.html`
> - **NCR 自動建單（2026-07-11 補實作）**：NCR 處置新增 `ReturnRework`（退件重工），Execute 自動建立
>   OutsourceRework 草稿（帶入 SourceNcrId/ReworkQty/ReworkReason，預設 LiabilityParty=Vendor、pct=100；
>   來源為 InspectionOrder 且其 SourceDocId 為委外派工時自動連結 OriginalDispatchId）；
>   NCR 補執行結果欄 `OutsourceReworkId`（雙向連結）。成本基額由採購於重工單補登後儲存重算。

```
OutsourceRework（廠別 IPlantEntity）
├── OutsourceReworkId     varchar(30) PK（採番 DocType=RW）
├── OriginalDispatchId    FK→Dispatch（原始委外派工）
├── OutsourcePurchaseOrderId  FK→OutsourcePurchaseOrder（新建委外單）
├── SourceNcrId           FK→Ncr（定責來源；IQC Failed 之 NCR）  ← 成本模型
├── ReworkQty             decimal(18,4)
├── ReworkReason          varchar(500)
├── LiabilityParty        enum（Vendor 廠商 / Us 我方 / Shared 共同）  ← 成本模型
├── VendorLiablePct       decimal(5,2)（Shared 時廠商分攤比例，0–100；Vendor=100、Us=0）
├── VendorChargebackAmt   decimal(18,4)（向廠商扣款/索賠金額，廠商責任占比 × 重工成本）
├── ReworkProcessingFee   decimal(18,4)（我方應付重工加工費，我方責任占比 × 加工費）
├── Status                ReworkStatus（Draft/Sent/Received）
├── PlantId / ...
```

**成本/索賠模型（2026-07-09 與 Fisher 定案：責任歸屬 + 雙路徑）**：
- `LiabilityParty` 決定成本流向，`SourceNcrId` 提供定責依據（承 NCR 處置）：
  - **Vendor（廠商責任）**：廠商免費重工，或我方重工 → 產生 `VendorChargebackAmt` **向廠商扣款/索賠**（沿用 `OutsourceReceiptLine.ExcessScrapPenalty` 的委外扣款精神：開借項通知或沖抵下期委外應付）。
  - **Us（我方責任）**：來料/規格問題 → 廠商重工產生 `ReworkProcessingFee` **應付重工加工費**（新委外 PO 或成本加項）。
  - **Shared（共同）**：依 `VendorLiablePct` 拆分——扣款與加工費同時按比例產生。
- **沿用既有機制**：扣款接 PUR 委外扣款/應付；退件走既有 NCR→`GoodsReturn`（`QcrReturnService`/`GoodsReturnId`）鏈；不另建全新財務通道。

**觸發時機**：
- 委外進貨 IQC Failed → NCR 處置選「退件重工」時自動建立（帶入 `SourceNcrId`、依 NCR 定責預設 `LiabilityParty`）
- 或手工建立（委外廠送件後發現問題）

### 4.3 委外發退料（OutsourceMaterialDoc）整合決策（2026-06-01 定案）

> **不新建 Entity**；直接擴充 INV 現有 `OutsourceMaterialDoc`（TypeCode OO/OR），補齊 MFG FK 欄位。
>
> **狀態（2026-07-11 實作回寫）**：兩欄已補（migration `PUR_PhaseF_MfgLinksAndNcrReturnRework`），
> 表單 select2 選填，Controller 檢核：OPO/派工存在性（廠別範圍）、OPO 供應商須與表頭一致、
> OO 指定派工時各品號累計發料量 ≤ `DispatchMaterial.RequiredQty` −「其他未作廢 OO 單已發量」
> （品號不在派工發料需求中即擋）；OR 不套發料量守門（沿用既有來源檢核 SourceTypeCode=OO）。
> 檢核置於建立實體/採番之前，失敗不耗用單號。派工帶入採「表頭選派工」——OPO Line 實際無
> DispatchId 欄（派工經 OutsourceDemand 關聯），故以表頭 FK＋DispatchMaterial 守門實現同等語意。

#### 現有 INV 模組的單據

| TypeCode | 中文名 | 說明 |
|----------|--------|------|
| `OO` | 委外發料 | 倉庫發料給委外廠商 → INV 庫存減 |
| `OR` | 委外退料（外發退料單）| 委外廠商退回未用材料 → INV 庫存增 |

「外發退料單」就是 TypeCode=OR 的現有功能，**不需另建新表**。

#### Phase F 擴充：補 MFG 連結欄位

```
OutsourceMaterialDoc（擴充，migration 加欄）
├── OutsourcePurchaseOrderId   varchar(30)?  FK→OutsourcePurchaseOrder（發退料對應的委外採購單）
└── DispatchId                 varchar(40)?  FK→Dispatch（OO 發料對應的來源派工）
```

**補欄後業務語意**：
- OO 建立時，從 `OutsourcePurchaseOrder.Lines[].DispatchId` 帶入派工，驗證發料量 ≤ `DispatchMaterial.RequiredQty`
- OR 建立時，關聯委外採購單，退料回庫存

#### 委外收回成品（TypeCode OI）— 由 OutsourceReceipt 負責，**非** OutsourceMaterialDoc

```
三個容易混淆的 TypeCode 釐清：
  OO = 委外發料（OutsourceMaterialDoc）   原料發給委外廠 → 庫存減
  OR = 委外退料（OutsourceMaterialDoc）   原料未用退回   → 庫存加
  OI = 委外入庫（OutsourceReceipt）       加工完成品回廠 → 成品入庫（§4.1 Post 觸發）
```

- `OI` **不是** `OutsourceMaterialDoc` 的新 TypeCode（該表只收 OO/OR）；`OI` 是 `OutsourceReceipt`（§4.1）過帳時寫入 INV 的庫存異動類別。
- 來料檢驗（IQC）不在 OutsourceReceipt 內，改由 **QCR 模組** NCR 流程處理（見上方 §4.1 差異說明）。
- `OutsourceReceipt` 才是 OO/OR 無法覆蓋的單據，語意完全不同（收回**成品**），**勿與 OR 退料混淆**。

### 4.4 委外管理選單重組（Phase F 時執行）

```
委外管理（OSP）
├── 委外採購單（OutsourcePurchaseOrder）   ← 從 MFG 選單遷移
├── 委外發退料（OutsourceMaterial）        ← 從 INV 選單遷移（保留 OO/OR 功能，補 MFG FK）
├── 委外進貨單（OutsourceReceipt）         ← 新建（收回成品 + IQC）
├── 委外重工單（OutsourceRework）          ← 新建
└── 委外進度追蹤（OutsourceProgress）      ← 從 PUR 選單遷移（2026-07-28 補遷，見下方狀態）
```

> **庫存管理選單**：Phase F 後可保留 `OutsourceMaterial` 唯讀查詢入口，
> 供倉庫作業人員查看發退料記錄（不影響主要操作入口在委外管理）。

> **狀態（2026-07-11 實作回寫）**：頂層群組 `OspManagement`（委外管理）已建，置於採購管理之後。
> - 實作比上表多納入**委外報價（OutsourceQuotation）**（自 MFG 遷入）——與委外採購單同一工作流，不宜拆散。
> - INV 選單保留 `OutsourceMaterial` 入口（同 MenuKey 雙入口；權限以 MenuKey 為準，不受群組影響）。
> - 權限種子：`GroupToModule["OspManagement"]="OSP"`＋矩陣列 OSP＝Row(HQ=F, SALES=R, WH=R, PROC=F, FIN=R, MGR=R, OP=N)，
>   另設 `WarehouseOspExecute={OutsourceMaterial, OutsourceReceipt}` 特判 WH=Full（倉庫實體作業）。
> - **既有部署不受影響**：MenuPermission 以 MenuKey 為鍵且全為既有鍵，seeder 只補全新鍵；矩陣僅影響乾淨部署首次種子。

> **補遷（2026-07-28）**：`OutsourceProgress`（委外進度追蹤）自 `PurManagement` 遷入 `OspManagement`，置於群組末。
> - **漏網原因**：本節（§4.4）的遷移清單只涵蓋**單據型**畫面；而 `OutsourceProgress` 屬 §5 Phase G 報表，
>   §5 明載這批報表「先前已有既存畫面、均在 PUR 模組」，Phase G 只補缺口不動選單——兩邊各自為政，這張就卡在中間。
> - **判準**：`OutsourceProgressController.BuildQueryAsync` 只查 `OutsourcePurchaseOrder(Line)` ＋ `OutsourceReceiptLine`，
>   完全不觸及 `PurchaseOrder`／`GoodsReceipt`，與 §5.1 採購稽催表不同資料鏈；留在採購管理是資料歸屬錯位。
> - **零影響**：MenuKey 不變 → 既有部署 MenuPermission 不動、不需 migration；
>   且權限矩陣 `PUR` 與 `OSP` 兩列完全相同 `Row(F,R,R,F,R,R,N)`，`WarehouseOspExecute` 亦不含本鍵 → 各角色權限一致。
> - 使用手冊頁維持 `wwwroot/docs/user/pur/outsource-progress.html`（手冊資料夾依 Controller 所屬程式模組，非選單群組；
>   `OutsourceReceipt`／`OutsourceRework` 同樣仍在 `pur/`），`HelpDocResolver` 不需改。

### 4.5 Phase F 驗收標準

- 委外發料（OO）建立時可選委外採購單 + 派工，發料量驗證正確
- 委外退料（OR）建立時關聯委外採購單
- 委外進貨→IQC→確認→INV 成品入庫完整流程可操作
- IQC Failed 退件後可建委外重工單重送
- Dispatch.QtyDone 隨 OutsourceReceipt.Confirm 自動累計
- OutsourcePurchaseOrderLine.ReceivedQty 隨進貨確認累計

---

## 5. Phase G：管理報表

> **前置條件**：採購進貨基本資料完整（GoodsReceipt + PO 有 DeliveryDate）。
> 報表框架使用 `/new-analytics`（Chart.js 儀表板頁面），或純資料表頁面。

> ✅ **狀態回寫（2026-07-11 實作盤點）**：本節五張報表**先前已有既存畫面**
> （`PurchaseOverdue`／`OutsourceProgress`／`SupplierDeliveryRate`／`PurchaseAmount`，
> 均在 PUR 模組、Menu「採購報表」群組；規格撰寫時漏盤點）。Phase G 改為「補齊缺口＋修正缺陷」
> 而非新建畫面，各節逐項回寫如下。
>
> ⚠️ **選單歸屬更正（2026-07-28）**：其中 `OutsourceProgress` 已改掛「委外管理（OSP）」群組（見 §4.4 補遷段）；
> 其餘四張仍在採購管理。Controller 檔案位置不變（`Modules/PUR/Controllers/`）。

### 5.1 採購稽催表（Expediting Report）

**定義**：預計交期已過、但尚未足額進貨的採購單明細。

```
查詢條件：PlantId、廠商、品號、逾期天數（≥N天）
欄位：PO號、廠商、品號、訂購量、已進量、未進量、預交日期、逾期天數（today - DeliveryDate）、採購員
操作：可直接從稽催表「催單」（觸發寄信或 Slack 通知，Phase H）
```

**資料來源**：`PurchaseOrderLine` 其中 `DeliveryDate < today && OrderQty > ReceivedQty && !IsClosed`

> ✅ 由 `PurchaseOverdueController` 承載。2026-07-11 補齊：逾期天數 ≥N 過濾（`minOverdueDays`）、
> 「採購員」欄（表頭 CreatedBy → UserName）、**排除已結案行 `!IsClosed`**（缺陷修正：
> 原查詢未排除，部分收貨後結案的行會永遠掛在稽催表）。「催單」通知維持 Phase H。

### 5.2 委外稽催表

同上但來源為 `OutsourcePurchaseOrderLine` + `OutsourcePurchaseOrder.ExpectedDeliveryDate`

> ✅ 由 `OutsourceProgressController`（委外進度追蹤）承載。2026-07-11 補齊/修正：
> - 逾期天數欄＋逾期 ≥N 天過濾（原僅有 IsOverdue 布林）
> - **拆單行委外量改 `SplitQty > 0 ? SplitQty : Qty`**（缺陷修正：原用 Qty＝原始需求全量，
>   拆單行會高估；口徑對齊 GoodsReceiptController 的訂購量計算）
> - **已驗收量含 Confirmed**（原僅 Posted；Confirmed 即廠商已交貨，不應再催）
> - 同（單號,品號）多行時收貨量依 LineNo 順序逐行分攤，避免每行重複計入全額

### 5.3 交期達成率統計

```
維度：廠商 × 月份（或季度）
指標：
  - 準時交貨筆數 / 總進貨筆數 = OTD Rate
  - 平均逾期天數
  - 最大逾期天數
資料來源：
  PO: GoodsReceiptLine.ReceiptDate vs PurchaseOrderLine.DeliveryDate
  委外: OutsourceReceiptLine.ReceiptDate vs OutsourcePurchaseOrderLine.DeliveryDate（Phase F）
```

> ✅ 由 `SupplierDeliveryRateController` 承載（原僅廠商×PO 單維度）。2026-07-11 補齊：
> - **月份維度**＋OTD Rate 月趨勢折線圖（Chart.js，單一序列免圖例）
> - 平均/最大逾期天數（僅計逾期筆；聚合邏輯抽出為純函式 `OtdAggregator`，可單元測試）
> - **委外來源**：查詢列可選「全部／採購進貨／委外收貨」。委外預交日採 **OPO 表頭
>   `ExpectedDeliveryDate`**（規格原文的 `OutsourcePurchaseOrderLine.DeliveryDate` 欄位不存在，
>   同 §4.3 的偏差說明）；委外收貨口徑 Confirmed 以上，對齊委外對帳報表（OutsourcePerformance）
> - PO 側口徑不變：過帳進貨明細行 vs 採購明細預交日（無預交日者不列入評估）

### 5.4 採購金額趨勢

```
維度：月份 × 廠商類別（或品號類別）
指標：採購含稅總金額（本幣）、筆數
資料來源：PurchaseOrderLine.TotalAmount × ExchangeRate（Confirmed 以上才計）
```

> ✅ 由 `PurchaseAmountController`（採購金額比較）承載，維度為年度 × 月份 × 廠商/品號
> （Company 無「廠商類別」欄位，以廠商/品號切換代替）。2026-07-11 修正：
> **金額換算本幣**——明細含稅總額 × 進貨時匯率（缺陷修正：原直接加總採購幣金額，
> 多幣別會混加）。資料來源維持「過帳進貨（實績）」而非規格原文的 PO 下單金額：
> 趨勢分析以實際進貨為準較有管理意義，且與本報表既有語意一致（偏差說明）。

### 5.5 廠商延誤統計

```
廠商排名（逾期次數、逾期天數）：用於供應商評核輸入
```

> ✅ 併入 §5.3 交期達成率頁面的「廠商延誤排名」表：逾期筆數多者在前、其次平均逾期天數，
> 含平均/最大逾期天數欄，可 Excel 匯出（供 Phase G+ 供應商評核 DeliveryScore 輸入）。

### 5.6 Phase G 驗收標準

- 稽催表可依逾期天數過濾，可 Excel 匯出 ✅（採購 §5.1／委外 §5.2 皆有 minOverdueDays＋匯出）
- OTD Report 顯示折線圖（月份趨勢）✅（§5.3 月趨勢折線圖）
- 所有報表支援雙語（zh-TW / en-US）✅（view resx 雙語對齊，R1/R5 慣例守門）

---

## 6. Phase G：合格供應商評分卡（SRM）

> 完整 SRM 工程量 3–4 週，與品質管理模組（Phase G/H）一起規劃。

> ✅ **已實作（2026-07-11，Phase G+）**：VendorEvaluation 全域主檔＋畫面（/VendorEvaluation，PurManagement 群組）落地。
> 實作與本節設計的差異／定案：
> - **不良量來源定案＝（b）QCR NCR 統計**（2026-07-11 決策）：不良量＝期間內 NCR（SourceType=GoodsReceipt、非作廢）Qty 加總，經 SourceId 追溯供應商；**免 GoodsReceiptLine.NgQty migration**。進貨量＝過帳進貨明細 ReceiptQty 加總。
> - Quality/Delivery 為**全公司跨廠彙總**（IgnoreQueryFilters，對齊全域主檔定位）；交期口徑與 §5.3 完全共用——事實建構抽為 `IOtdFactService`，報表（廠內視角）與評分卡（全公司）同源。
> - 期間無進貨/無到料＝該項滿分 100（公式分母為 0 無定義；已寫入使用手冊與 _Form 提示）。
> - 權重存系統參數 `srm_weight_quality/delivery/price/service`（預設 30/30/20/20）；加權平均/門檻/資格映射為 `VendorEvaluationCalculator` 純函式。
> - Entity 增補簽核欄位（ApprovalStatus/ApprovalRequestId）與稽核欄位；PK 為 int Id（非單號）。
> - 「Suspended 需主管核准」以標準線上簽核實現：設定 VendorEvaluation 簽核範本後所有評核送簽走流程（未設範本＝送簽即核准）；核准時（含 `VendorEvaluationApprovalSync` 流程回寫）才更新 Company.QualificationStatus。
> - Fail 必填 FollowUpAction（後端驗證）；同廠商×年度×季度唯一由應用層檢查（PG 唯一索引視 NULL 互異，擋不住年度評核重複）。
> - 自動分數存檔時一律後端重算為準；_Form「重新計算」供預覽與依據透明化（進貨量/不良量/到料筆數）。
> - 匯出：清單 Excel＋單筆評分卡列印頁（瀏覽器列印/另存 PDF，QR 連回 Details）。

### 6.1 VendorEvaluation Entity 設計

```
VendorEvaluation（全域，非 IPlantEntity）
├── Id
├── SupplierCode          FK→Company
├── EvaluationYear        int（年度，如 2026）
├── EvaluationQuarter     int?（1–4，null=年度評核）
├── QualityScore          decimal(8,2)   0–100（IQC 退件率衍生）
├── DeliveryScore         decimal(8,2)   0–100（OTD Rate 衍生）
├── PriceScore            decimal(8,2)   0–100（議定單價 vs 市場）
├── ServiceScore          decimal(8,2)   0–100（人工填寫）
├── TotalScore            decimal(8,2)   加權平均（權重可設定）
├── Evaluator             FK→ApplicationUser
├── EvaluationDate        DateOnly
├── ResultStatus          EvaluationResult（Pass/Conditional/Fail）
├── FollowUpAction        varchar(500)?（Fail 時的改善措施）
└── Remarks               varchar(500)?
```

**評核結果 → Company.QualificationStatus 自動更新**：
- `TotalScore ≥ 80` → Qualified
- `60 ≤ TotalScore < 80` → Probation
- `TotalScore < 60` → Suspended（需主管核准）

### 6.2 評核分數來源（系統自動計算）

```
QualityScore = (1 - IQC 退件率) × 100
  IQC 退件率 = 期間內 不良量 / 進貨量

DeliveryScore = OTD Rate × 100
  OTD Rate = 準時進貨筆數 / 總進貨筆數

PriceScore = 手工填寫（或比對市場基準，未來擴充）
ServiceScore = 手工填寫（回應速度、溝通品質等）
```

> ✅ **前置決策已定案（2026-07-11）＝（b）QCR NCR 退件記錄統計**：免 migration，沿用既有 IQC→NCR 品質流程，
> 資料單一來源（IQC 檢驗記錄 MobileIqcRecord 已綁 GoodsReceipt，NCR 有 SourceType/SourceId/Qty 可追溯供應商）。
> （a）GoodsReceiptLine.NgQty 會與 QCR 檢驗記錄雙重登載，不採。

### 6.3 Phase G 驗收標準

- 年度/季度評核可建立並帶入自動計算分數 ✅（_Form 重新計算＋存檔後端重算；`VendorEvaluationScoreService`）
- 評核結果自動更新 Company.QualificationStatus ✅（核准時：Submit 無流程路徑＋`VendorEvaluationApprovalSync`）
- 歷年評核趨勢折線圖 ✅（Index 選定單一廠商顯示核准評核總分折線，Chart.js＋CSP nonce）
- 評核清單可匯出 PDF/Excel ✅（Excel＝清單匯出；PDF＝評分卡列印頁瀏覽器另存）

---

## 7. Phase 時程摘要

> Phase 字母沿用 **mfg-domain.md** 的全域階段編號；**Phase A–E 屬 MFG 主幹（工單/MRP/報工/完工等），見 mfg-domain.md**。
> 本文件僅涵蓋採購/委外相關的 NOW 與 Phase F 之後。

| Phase | 內容 | 狀態 / 估算 |
|-------|------|------|
| **NOW** | VendorPriceList + Company.QualificationStatus | ✅ **已完成**（程式碼已落地） |
| **Phase A** | 委外進貨單（OutsourceReceipt，雙狀態三層） | ✅ **已完成**（見 §4.1） |
| **Phase F**（完工後）| 委外發退料補 MFG FK + 委外重工單（OutsourceRework）+ 選單重組 | ✅ **完成**（2026-07-11）：OutsourceRework（§4.2）＋NCR 退件重工自動建單＋§4.3 發退料 MFG FK/發料量守門＋§4.4 委外管理選單重組（OSP 群組＋權限矩陣） |
| **Phase G**（Phase F 後）| 採購稽催表 + 交期達成率 + OTD Report | ✅ **完成**（2026-07-11）：五張報表既存，本次補齊 §5 缺口（逾期天數過濾/採購員/月趨勢折線圖/委外來源/延誤排名）＋修正三缺陷（IsClosed 未排除/拆單 SplitQty/多幣別混加），見 §5 各節回寫 |
| **Phase G+**（SRM）| 合格供應商評分卡（VendorEvaluation）；前置：不良量欄位（§6.2）| ✅ **完成**（2026-07-11）：不良量定案 QCR NCR 統計（免 migration）；VendorEvaluation 主檔＋畫面＋自動分數（跨廠 NCR/OTD）＋權重系統參數＋簽核連動 QualificationStatus＋趨勢圖＋Excel/列印，見 §6 回寫 |

---

## 8. 關聯文件

- [`mfg-domain.md §19`](./mfg-domain.md) — 委外完整流程（MRP→工單→委外採購單→Phase F 收貨）
- [`inventory.md`](./inventory.md) — 委外收貨入庫的 INV 異動對應
- `new-screen.md` — 所有畫面產生規格
- `new-report.md` — 報表頁面規格（稽催表適用）
