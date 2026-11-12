# 對帳框架決策文件 — D1 成本語意 / D2 傳票工單維度 / D3 ERP 拋轉範圍

> ✅ **2026-10-08 狀態：D1–D4 全部定案並已實作**（定案內容見文末〈決策狀態追蹤〉）。本檔保留為**決策紀錄**——
> 各節「現況」描述的是撰寫當日的程式，不是現行行為。唯一仍開著的是 D3 中／大層級綁定的 **P-4 ERP 雙向核對**（外部依賴），
> 追蹤在 `STATUS.md`〈真正未完成〉。本檔刻意不併入 `business_data_flow_reconciliation.md`：它是 QA 擷取排除檔（決策討論非現況），且被多處程式註解以檔名引用。
>
> 2026-07-06 依 `business_data_flow_reconciliation.md` §9 P-1/P-2/P-3 前置決策需求撰寫。
> 所有「現況」皆經程式碼逐點查證（檔案:行號為當日 commit 基準）；**本文件只提供分析與建議，未改動任何會計語意**——各項「待決策」由業務/財務方定案後再實作。

---

## D1. Push/Pull 發料材料成本語意（P-1 金額恆等式前置，兼成本正確性問題）

### 現況（已查證）

| 環節 | 行為 | 出處 |
|------|------|------|
| 標準成本 | Release 時 `ComputeStandard` 以**全部**工單用料（Push/Pull/Backflush 不分）計 `StandardMaterialCost` | `Modules/MFG/Services/WorkOrderReleaseService.cs:125-128` |
| 實際材料成本 | 完工時 `totalMaterialCost` **只加總 Backflush+AtCompletion 料**後 `wo.ActualMaterialCost += totalMaterialCost`；Push/Pull 發料（MaterialSlip IO 過帳）只回寫 `WorkOrderMaterial.IssuedQty`，**不累計** `wo.ActualMaterialCost` | `Modules/MFG/Services/ProductionCompletionService.cs:79-137`、`Modules/MFG/Controllers/MaterialSlipController.cs:404` |
| 完工品單位成本 | `unitCost = (ActualMaterial+Labor+Overhead)/qtyDone` → **不含 Push 料成本**，以此金額繳庫入存貨 | `ProductionCompletionService.cs:139-140` |
| GL 面（**2026-07-06 更正**） | 生產物料異動（後沖 IO、Push/Pull 發退料、完工 IT）走 `PostDocSimplifiedAsync`，**完全不落日記帳**（只寫 StockMovement），故材料**從未被費用化**，一直留在存貨；唯一的生產 GL 是完工吸收分錄（借原料存貨、貸人工/製費已分配）。〔早先「發料借 ScrapLoss」的記述有誤——那是 `PostLineDetailAsync`（倉儲調整/報廢）路徑，生產不走此路〕 | `Modules/INV/Services/InventoryPostingService.cs`（PostDocSimplifiedAsync 無 JE）、`ProductionCompletionService` 吸收分錄 |
| 規格依據 | mfg-domain §16.3「完工入庫時 ActualMaterialCost = INV 回傳實際發料成本總計」——**規格意圖含全部發料，實作只做了後沖** | `.claude/specifications/mfg-domain.md:1509` 起 |

**GL 於彙總層其實一致**：材料收料入存貨後不再移出（發料無 JE），完工吸收只加人工/製費至存貨——製成品在 GL 以材料＋加工成本掛在存貨。**真正的低估在「逐工單成本記錄」**：`wo.ActualMaterialCost` 只含後沖料、不含 Push 料 → 完工品單位成本偏低 → 該 FG 之後出貨時 COGS 以偏低 AVCO 沖銷，存貨殘留虛值。

### 影響

1. **存貨計價低估／下游 COGS 失真**：Push 料佔比高的產品，完工品 AVCO 單位成本偏低；FG 出貨時 COGS 低估、存貨帳面殘留虛值。
2. **材料差異假訊號**（D1-B 已先止血，D1-A 後移除）：Standard 含全部料、Actual 只含後沖料 → Push 料佔比高時恆為大額負差異。

### 選項與定案

- **A（原提「完整 WIP 化」）**：發料改資本化入 WIP 科目、完工轉 FG。**經 2026-07-06 查證後修正認知**：生產物料異動本就不落日記帳（材料未被費用化，已在存貨），故不需要「反費用化」；且 `PostDocSimplifiedAsync` 為 QCR/ORD/PUR/MFG/INV 多模組共用、皆不落 JE，若在此加 WIP 過帳將是**全系統 GL 變更**，遠逾本項且非修正存貨低估所需。→ **不採全 GL 重設**。
- **實際採行（2026-07-06 已實作）＝ 成本收斂**：完工時把 Push/Pull 實際發料成本（`StockMovement.UnitCost` 凍結 AVCO，經 `MaterialSlip.SourceDocId=工單` 二跳、依方向淨計）併入 `wo.ActualMaterialCost`；差異基準復原為全料 `StandardMaterialCost`（移除 D1-B 的 `ComputeComparableStandardMaterial` 可比口徑）。**存貨計價、單位成本、差異三者一次修正，零 GL/科目變更、零 migration。** §3.11 材料腿同步改為對「庫存異動子帳」核對（生產物料無日記帳可核）。

### 殘留（非本次）

- 若未來需要**逐工單的材料 GL 顆粒度**（WIP 科目、發料/完工分錄），屬全系統 GL 過帳設計（`PostDocSimplifiedAsync` 全模組加 JE），須另立專案並經財務定案科目表；本次不做，也非存貨計價正確性所需。

---

## D2. 傳票工單維度（P-2 逐工單成本回溯核對前置）

### 現況（已查證）

`JournalEntry` 僅表頭 `SourceDocType/SourceDocId`（`Modules/ACT/Models/JournalEntry.cs:26-29`）；`JournalLine` 有 `CostCenter`（50 字，未被任何寫入方使用）。工單成本鏈四條傳票路徑的工單可回溯性：

| 傳票 | SourceDocType / SourceDocId | 工單維度 |
|------|------------------------------|----------|
| 人工/製費吸收 | `WorkOrderCompletion` / 工單號 | ✅ 直接 |
| 後沖材料 | `INV` / `工單號-BF` | ✅ 字尾剝離 |
| 完工繳庫 | `INV` / 工單號 | ✅ 直接 |
| Push/Pull 發料 | `INV` / MaterialSlip 單號 | △ 二跳（`MaterialSlip.SourceDocId`=工單） |
| **期末費率重估** | `ActualRateReest` / **期間 ID** | ❌ **全期彙總單傳票，無工單維度**（`Modules/MFG/Services/ActualRateReestimationService.cs:226-228`） |

**真正卡點只有重估分錄**：金額是全期所有工單調整的淨額，逐工單回溯在現行 schema 下不可能。

### 選項

- **A — 補維度**：`JournalLine` 加 `SourceRef`（nullable, MaxLength 50）+ migration；`ActualRateReestimationService` 改逐工單拆行（行數=工單數×2~3 科目，量可控）；P-2 稽核即可全鏈實作。
- **B（建議先行）— 排除重估、先稽核可回溯部分**：P-2 稽核只核 `WorkOrderCompletion` + `INV`（含 -BF/繳庫/slip 二跳）三路徑 vs `WorkOrderActualCost` 的 Preliminary 要素金額；重估調整額列為已知排除項。零 schema 變更、可立即依 §8.5 流程實作。
- ~~C — 借用 `JournalLine.CostCenter` 放工單號~~：語意濫用，未來真成本中心上線即衝突，不採。

### 決策（撰寫時待決，2026-07-06 定案：選項 A，見〈決策狀態追蹤〉）

1. 是否接受 B 的「Preliminary 口徑」稽核先上線（覆蓋 4/5 路徑）？
2. A 的 `SourceRef` 是否與未來成本中心維度需求（GL 自動歸集 provider 的前置）**合併一次 migration**——建議合併，避免傳票表連續兩次 DDL。

---

## D3. IErpSyncEntity 拋轉範圍擴大（P-3 前置）

### 現況（已查證）

- `IErpSyncEntity`（`Models/BaseEntity.cs:29-36`：SyncStatus/VoucherNo/SyncTime/ErrorMessage/RetryCount）僅 **APInvoice** 實作（`Modules/FIN/Models/APInvoice.cs:20`）。
- `ErpSyncService` **寫死 `_db.APInvoices`**（`FindAsync`/`GetPendingOverdueAsync`，`Modules/FIN/Services/ErpSyncService.cs:101-120`）——擴大前必須泛型化（`MarkSyncedAsync<T> where T : class, IErpSyncEntity` + DbSet 解析）。
- `ARInvoice` 模型已存在（`Modules/FIN/Models/ARInvoice.cs`），尚無拋轉欄位；§3.5 滯留稽核（ERP_SYNC_LAG）目前僅 APInvoice。

### 範圍選項（可疊加）

| 層級 | 內容 | 成本 | 依賴 |
|------|------|------|------|
| 最小（建議） | **ARInvoice** 補 5 欄 + 狀態機（與 AP 完全對稱）；§3.5 稽核擴大 | 1 migration + 泛型化 ErpSyncService（一次性）| 無——銷項發票拋 ERP 是通例，不需等需求 |
| 中 | + **JournalEntry**（總帳傳票彙總拋轉） | 1 migration；拋轉粒度（逐張/日彙總）需 ERP 端定 | ERP 介接需求（P-4） |
| 大 | + **StockMovement**（異動明細級） | 量大（每日千筆級），逐筆拋轉不建議，應改彙總中介表 | ERP 介接需求（P-4） |

### 決策（撰寫時待決，2026-07-06 定案：最小範圍已實作，中／大層級綁 P-4）

1. 是否核准「最小範圍」先行（ARInvoice + 泛型化）？——技術上無外部依賴，可獨立完成。
2. JournalEntry/StockMovement 層級**建議與 P-4（ERP 雙向核對）綁定**，待 ERP 端介接方式（API/中介表）定案一起設計，避免欄位白做。

---

## D4. 跨期 WIP 人工/製費結轉政策（P-1 金額恆等式最後前置）

### 問題（2026-07-16 逐行查證）

`ActualRateReestimationService` 的人工/製費「本期口徑」只取本期 session/setup 工時 × 本期實際費率，
造成同一根因的兩個病灶：

1. **WIP 快照腿**：跨期在製工單的 `WipLabor/WipOverhead` 不含前期已投人工——而同一快照的
   `WipMaterial = wo.ActualMaterialCost` 卻是跨期累計，兩腿口徑不一致，期末 WIP 低估。
2. **完工回刷腿**：跨期完工工單 Preliminary→Final 時 `ActualLabor` 被「僅本期工時×本期費率」
   整段覆蓋（Preliminary 原為全生命週期標準費率累計），前期工時被沖掉——Final 成本低估
   → 單位成本/存貨計價/COGS 失真，且調整分錄錯誤反轉前期已吸收人工。

前期價值並未遺失：凍結在前期（已關帳）`WipSnapshot.WipLabor/WipOverhead`，只差沒有接回。

### 選項與定案

| 選項 | 內容 | 評估 |
|------|------|------|
| **B 期初快照結轉（分層成本法）** | 本期人工/製費 = 前期快照凍結層（前期實際費率）＋本期工時×本期實際費率；兩腿一致套用 | ✅ **定案（2026-07-16）**——直接對應恆等式「期初＋投入−產出」、前期層永凍結符合關帳不可變、零 migration、費率池不失真 |
| A 逐期費率重算全生命週期 | 持久化各期費率、每期全鏈重算 | ❌ 結果與 B 相同（前期層本就不許變），白付持久化與重算成本 |
| C 全生命週期工時×當期費率 | 所有工時用當期費率重評 | ❌ 本期費用分攤到跨期工時（吸收失真）＋追溯重述已關期間 |

**邊界定案**：無前期快照的工單期初＝0（本期才投產者天然正確；跨期而缺快照者由 §3.10
`WIP_SNAPSHOT_MISSING` 於前期關帳擋下）；本期無活動的跨期在製工單原值結轉；
同期重跑冪等（讀前期快照，不受本期刪重建影響）。

**報廢口徑定案**：全庫查證無任何流程扣減 `wo.Actual*Cost`——成本唯一出口是完工
（NG 成本經完工 `/qtyDone` 吸收進良品單位成本），故恆等式**無獨立報廢腿**。
前期在製工單本期作廢屬洩漏（發料應先退回再作廢），恆等式逐筆報 `WIP_VOID_LEAKAGE` 並自期初調節。

### 隨定案啟用：§3.10-5 金額恆等式稽核（P-1 收口）

`WIP_IDENTITY_DRIFT`：逐廠核 `Σ期末快照 = Σ期初快照 + 本期發料淨值 + 本期部門人工/製費 − 本期完工成本`。
各腿獨立來源（非快照互推）：發料＝§3.11 材料腿同源（BF＋MaterialSlip 二跳）加期間切片；
人工/製費＝`DepartmentActualCost`（月結輸入的重估分子）；完工＝`WorkOrderActualCost`（CostDate ∈ 期間）。
僅於結構性守門（§3.10-1~4）全數通過時執行；失衡常見原因：工作中心有費用但無工時（費用無處吸收）、
重估後又有發料/完工未重跑。

---

## 決策狀態追蹤

| 項目 | 狀態 | 定案日 | 定案內容 |
|------|------|--------|----------|
| D1 Push 料成本語意 | ✅ 成本收斂已實作（存貨計價低估已解） | 2026-07-06 | 完工彙總 Push/Pull 發料成本（StockMovement AVCO，經 MaterialSlip 二跳、依方向淨計）入 `wo.ActualMaterialCost` → 單位成本/存貨計價/下游 COGS 修正；差異復原全料基準，移除 D1-B 可比口徑（`ComputeComparableStandardMaterial` 及報表註記已刪）。查證更正：生產物料異動不落日記帳、材料未被費用化，故無需 WIP 反費用化；全 GL 顆粒度另屬全系統專案（見上「殘留」） |
| D2 傳票工單維度 | ✅ 選項 A 已實作 | 2026-07-06 | `JournalLine.SourceRef`（migration `ACT_JournalLineSourceRef`）＋重估調整分錄逐工單拆行；§3.11 人工/製費腿改吸收科目（5110/5120）淨額＝完工吸收＋重估調整（SourceRef=工單），Preliminary/Final 皆核 |
| D3 ERP 拋轉範圍 | ✅ 最小範圍已實作；中/大層級綁 P-4 | 2026-07-06 | ARInvoice 補 5 欄（migration `FIN_ARInvoiceErpSync`）＋`IErpSyncDocument`（FIN，Core 介面不動）＋`ErpSyncService` 泛型化（`MarkSyncedAsync<T>` 等，AP 簽章相容保留）＋FINModule DI 註冊＋§3.5 稽核 AP＋AR |
| D4 跨期 WIP 結轉 | ✅ 選項 B 已實作（P-1 收口） | 2026-07-16 | `ActualRateReestimationService` 兩腿（WIP 快照＋完工回刷）套期初快照結轉（零 migration）；§3.10-5 `WIP_IDENTITY_DRIFT` 金額恆等式＋`WIP_VOID_LEAKAGE` 啟用（結構守門全過才評估）；測試：單元 4 案＋整合 `WipCarryForwardTests` 兩期端到端（恆等式平衡驗算 930+50+600−900=680） |

> 定案後：更新本表 → 依 `business_data_flow_reconciliation.md` §8.5 流程實作 → 回寫 §9 對應列。
