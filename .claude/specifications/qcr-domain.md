# 品質管理領域規格（qcr-domain.md）

> 對應舊系統 `PZ/QCR/` 模組（QNO01 檢驗單／QNI01 不良項目／QIN01 儀器／QIT01 規格）。
> 本檔描述「已實作」現況，作為編碼與同步的依據；抽樣狀態機與製程閘控的跨模組決策另見
> `mfg-domain.md` §14（FQC 完工閘控）、§15.3（抽樣計畫 Phase G/H）。
>
> ⚠️ 重要：編碼涉及 QCR 模組時請主動查閱本檔（不會自動納入上下文）。

---

## 1. 模組結構

```
Modules/QCR/
├── QCRModule.cs                 # IAppModule：僅註冊 View 路徑（無自有 DI 服務）
├── Models/Instrument.cs         # Instrument + InstrumentSerial（其餘 entity 在 /Models 根）
├── Controllers/
│   ├── InspectionSpecController   # 檢驗規格（IQC/IPQC 共用）
│   ├── InspectionOrderController  # 檢驗單（抽樣執行＋判定）
│   ├── DefectCodeController       # 不良項目代碼
│   ├── InstrumentController       # 檢驗儀器 + 序號 + 校正
│   └── NcrController              # 不合格品單（NCR）
└── Views/{Controller}/…

# QCR 領域 entity 散落位置（歷史因素）
Models/InspectionSpec.cs   # InspectionSpec / InspectionSpecItem / InspectionOrder /
                           # InspectionItem / InspectionSpecSamplingCounter（5 個 class 同檔）
Models/DefectCode.cs
Models/NonConformanceReport.cs
Modules/QCR/Models/Instrument.cs

# 抽樣決策服務（Core /Services，跨 QCR/MFG/ORD 共用）
Services/MilStd1916Service.cs   # 靜態查表：批量 + VL → 樣本數（Ac=0）
Services/ISamplingPlanService.cs / SamplingPlanService.cs  # VL 切換 + 跳批狀態機
```

> QCRModule 不註冊任何服務；抽樣服務由 Core `Services/` 提供並在 MFG 模組註冊，QCR 控制器透過 DI 取用。

---

## 2. 檢驗規格（InspectionSpec）— 已實作

### 2.1 業務說明

定義產品（或產品族）的 MIL-STD-1916 驗證等級與抽樣型態，供 **IQC（進料）** 與 **IPQC/FQC（製程／完工）** 共用。一張規格可掛多個量測特性項目（`InspectionSpecItem`），IPQC 計量值檢驗時量測值快照至檢驗單。

### 2.2 Entity 設計（`InspectionSpec`，全域主檔）

| 欄位 | 型別 | 說明 |
|------|------|------|
| `SpecId` | string PK | 規格 ID |
| `SpecCode` / `SpecName` | string | 規格代碼 / 名稱 |
| `ProductCode` | string? | 適用品號（null = 全品號通用） |
| `SupplierCode` | string? | 適用供應商（null = 全供應商；IQC 用） |
| `VerificationLevel` | int=4 | MIL-STD-1916 驗證等級 1–7（預設 VL-IV） |
| `SamplingType` | string | `Attribute`（計數）/ `Variable`（計量） |
| `TriggerMode` | string | IPQC 觸發：`Always` / `Periodic` / `OnDemand` |
| `PeriodHours` | int? | Periodic 時的抽樣間隔 |
| `RequiresFQC` | bool | 完工入庫前必須通過 FQC（見 mfg-domain.md §14.1） |
| `RequiresRQC` | bool | 銷退回廠後自動建立 RQC 待檢單 |

**MIL-STD-1916 VL 切換規則（規格層級設定）：**
`NormalVl`(4) / `TightenedVl`(6) / `ReducedVl`(2)；`PassesToReduce`(5：正常→減量) / `PassesToNormal`(3：加嚴→正常)。

**跳批（Skip-Lot，MIL-STD-1235 選配）：** `SkipLotEnabled` / `SkipLotFrequency`(每 N 批抽 1) / `PassesToSkipLot`(10：減量→跳批)。

**執行狀態（由 `SamplingPlanService` 動態維護，勿手改）：** `SamplingState`（Normal/Tightened/Reduced/SkipLot）/ `ConsecutivePasses` / `LotCounter`。

**抽樣計數邊界** `SamplingScope`（enum）：`SpecLevel`(1，整規格合算)／`MachineLevel`(2，依機台)／`ToolingLevel`(3，依治具)。多分區計數由 `InspectionSpecSamplingCounter`（`SpecId` + `ScopeKey`）承載，ScopeKey 規則：SpecLevel=`""`、MachineLevel=machineCode、ToolingLevel=toolingCode。

### 2.3 規格項目（`InspectionSpecItem`）

`SeqNo` / `ItemName` / `UomCode` / `LowerSpec` / `TargetValue` / `UpperSpec`；量測儀器 `InstrumentCode1~3`（最多 3 支，對應舊系統 `QNO_D.ID_QIN1/2/3`）；`TargetStdDev`（製程標準差 σ，null = 跳過 3-sigma 警告）。

---

## 3. 檢驗單（InspectionOrder）— 已實作

### 3.1 業務說明

一次抽樣的執行記錄。`InspectionType` = `IQC`（來源 GoodsReceipt）/ `IPQC`（來源 Dispatch）/ `FQC`（完工）；`OrderType`（enum FPI/IPQC/FQC，IQC 可為 null）。依 `InspectionSpec` + 批量算出 `SampleSize`，執行後記錄結果。

### 3.2 關鍵欄位

| 欄位 | 說明 |
|------|------|
| `SourceDocId` / `SourceLineNo` | 來源單（IQC=GoodsReceiptId；IPQC=DispatchId） |
| `LotSize` | 批量（進貨或派工數量） |
| `SampleSize` | 應抽樣數（MIL-STD-1916 依批量+VL 查表） |
| `ActualSampleQty` / `FailQty` | 實際抽取數 / 不良數 |
| `Status` | `Pending` / `Pass` / `Fail` |
| `ActiveMaterialLots` | 投產原料批號快照（JSON），供 NCR 交叉分析 |
| `Items` | `InspectionItem`：計量值量測紀錄（含 `MeasuredValue` / `IsConforming` / `HasSigmaWarning`） |

### 3.3 判定與閘控（`InspectionOrderController.Submit`）

- **Ac=0 允收計畫**：`Status = FailQty == 0 ? "Pass" : "Fail"`，任何一個不良即拒批。
- **失敗連動暫停派工**（mfg-domain.md §15.3.1）：IPQC 失敗且來源 Dispatch 為 `InProgress` 時，自動轉 `OnHold`。
- 僅 `Pending` 可提交；提交後寫入 `InspectorId` / `InspectedAt`。
- 提交 Pass/Fail 後應由呼叫端呼叫 `ISamplingPlanService.RecordResultAsync` 回寫規格的連續合格批數與狀態（VL 切換）。

---

## 4. 抽樣計畫服務（MIL-STD-1916）— 已實作

### 4.1 `MilStd1916Service`（靜態查表）

- `GetSampleSize(lotSize, VL)` → 應抽樣數 n（Table II，計數值）；`lotSize < n` 時全數檢驗（100%）。
- `IsAccepted(failQty)` → `failQty == 0`（允收數 Ac 恆為 0）。
- `GetVlOptions()` → VL I~VII 下拉說明（一般 IQC 建議 VL-IV，關鍵/安全件 VL-VI/VII）。

### 4.2 `ISamplingPlanService`（VL 切換 + 跳批狀態機）

- `GetDecisionAsync(productCode, supplierCode, inspectionType, lotSize, plantId, machineCode?, toolingCode?)` → `SamplingDecision(ShouldInspect, VerificationLevel, SampleSize, SamplingState, SkipReason?)`。`ShouldInspect = false` 表跳批，呼叫端可不建 InspectionOrder。
- `RecordResultAsync(productCode, supplierCode, plantId, passed, machineCode?, toolingCode?)`：依 MIL-STD-1916 切換規則更新規格狀態；`machineCode/toolingCode` 供 MachineLevel/ToolingLevel 分區計數。應在檢驗單提交（Pass/Fail）後呼叫。

---

## 5. 不合格品單（NCR）— 已實作

### 5.1 業務說明（`NonConformanceReport`，廠別主檔 + IDataAuthEntity）

單號格式 `NCR{YY}{MM}-{nnnnn}`（DocNumberService）。來源 `SourceType` = `ProductionReport` / `InspectionOrder` / `GoodsReceipt` / `Manual`。處置 `Disposition` = **Scrap 報廢 / Rework 重工 / UseAsIs 特採 / Return 退回**。不良原因 FK → `DefectCode.Code`，另記 `RootCause` / `CorrectiveAction`。

### 5.2 狀態機（`NcrStatus`）

```
Open ──Submit──▶ PendingApproval ──核准──▶ Approved
  │  （UseAsIs 必經簽核）                      │
  └──────────── Execute ◀──────────────────────┘
                  │
                  ▼
              Executed ──Close──▶ Closed
  （任一前置狀態可 Void）
```

- **Execute 可執行狀態** = `Open` 或 `Approved`；`UseAsIs` 處置強制要求 `Approved`（須上級簽核，`ApprovalStatus`）。
- **Execute 為單一原子交易**（`BeginTransactionAsync`，因 Scrap/Rework 內含庫存過帳，過帳路徑又含日記帳 SaveChanges）：
  - `Scrap` → `ExecuteScrapAsync`：建報廢單（`ScrapDocId`）並扣 `WhCode`/`LocationCode`/`LotNo` 庫存。
  - `Rework` → `ExecuteReworkAsync`：建重工工單（`ReworkWorkOrderId`）。
  - `UseAsIs` / `Return`：僅標記執行完成（Return 另需在 PUR 建退貨單，回傳 advisory）。
- 完成後寫 `ExecutedBy` / `ExecutedAt`，狀態 → `Executed`。

---

## 6. 不良項目代碼（DefectCode）— 已實作

全域主檔（對應舊系統 QNI）。`Code` / `Name` + 四個適用旗標 `FgIQC` / `FgIPQC` / `FgFQC` / `FgRQC`（控制各檢驗類型可選用的不良原因）。`ProductionReport.NgReasonCode` 以此為 lookup（目前保留字串，後續可加 FK）。

---

## 7. 檢驗儀器（Instrument / InstrumentSerial）— 已實作

### 7.1 `Instrument`（全域主檔，對應舊系統 QIN）

`InstrumentCode` / `InstrumentName` / `CalibrationFrequencyDays`（校正週期天，0=不需校正）/ `CalibrationMethod`。

### 7.2 `InstrumentSerial`（廠別主檔，對應舊系統 QIN_D）

每個物理儀器一筆。`SerialNo` / `DepartmentCode` / `CustodianId`；`Status` = `Active` / `Retired` / `Loaned` / `UnderCalibration`；規格欄 `Brand` / `Model` / `Spec` / `Tolerance` / `Accuracy`；`CalibrationFrequencyDays`（null = 沿用主檔）/ `LastCalibrationDate` / `NextCalibrationDate`。

`InstrumentController.Calibrate(serialId, remarks)`：記錄校正並依週期推算 `NextCalibrationDate`。

---

## 8. 控制器 Action 摘要

| 控制器 | 主要 Action |
|--------|-------------|
| `InspectionSpecController` | Index / Create / Edit（含 Items）/ DeleteConfirmed |
| `InspectionOrderController` | Index / Details / **Submit**（判定＋暫停派工）/ Export |
| `DefectCodeController` | Index / Create / Edit / DeleteConfirmed |
| `InstrumentController` | Index / Details / Create / Edit / CreateSerial / EditSerial / DeleteSerial / **Calibrate** / DeleteConfirmed |
| `NcrController` | Index / Create / Edit / Details / **Submit** / **Execute** / **Close** / DeleteConfirmed |

---

## 9. 關聯文件

- `mfg-domain.md` §14（FQC 完工閘控）、§15.3（抽樣計畫 Phase G/H、機台/治具分區計數）
- `inventory.md`（Scrap/Rework 處置的庫存過帳）
- `pur-domain.md`（IQC 來源 = GoodsReceipt；Return 處置 → PUR 退貨單）
- `currency.md`（不適用；QCR 無金額欄位）
