# ERP 拋轉橋接 — 交接說明（給後續顧問）

> **狀態**：本系統**刻意不實作** ERP 拋轉。狀態機欄位與服務層已就位，
> 但沒有任何呼叫端；是否接、接哪一套 ERP、用什麼介面，由各專案依自身狀況決定。
> 本文說明「現況是什麼、為什麼停在這裡、要接的話該從哪裡下手、以及**不要**怎麼接」。
>
> 撰於 2026-09-07（人工驗收 Wave 4 / W4-F7 的後續）。

---

## 1. 現況：有狀態機，沒有搬運工

### 1.1 已經有的東西

| 位置 | 內容 |
|------|------|
| `Models/IErpSyncEntity.cs`（Core） | `ErpSyncStatus` / `ErpVoucherNo` / `ErpSyncTime` / `ErpRetryCount` / `ErpErrorMessage` 五個欄位 |
| `Modules/FIN/Models/IErpSyncDocument.cs` | 在上者之上補 `TotalAmount`（尾差比對）與 `UpdatedAt` |
| `Modules/FIN/Services/ErpSyncService.cs` | `MarkSyncedAsync<T>` / `MarkFailedAsync<T>` / `MarkExcludedAsync<T>`、逾時查詢兩支 |
| DB | `APInvoices` 與 `ARInvoices` 上的 `Erp*` 欄位（migration `PhaseC_ErpSyncFields`、`FIN_ARInvoiceErpSync`） |
| 稽核 | `DataAuditService.AuditErpSyncLagAsync` — 逾 24 小時未拋轉即產生 `ERP_SYNC_LAG` |

狀態機本身是完整的：`Pending → Synced`、`Pending → Failed`（重試計數）、
`* → Excluded`（人工排除），金額有 ±0.01 的尾差容忍（可由系統參數
`audit_amount_tolerance` 覆寫）。

### 1.2 缺的是什麼

**沒有任何程式呼叫過 `MarkSyncedAsync`。** 全庫搜尋 `IErpSyncService`，
除了 `FINModule.cs` 那一行 DI 註冊之外，只有測試在用。具體缺口：

- 沒有「把單據送出去」的那一段（HTTP／檔案／DB link／訊息佇列，一個都沒有）；
- 沒有排程去掃 `Pending` 的單；
- `MarkExcludedAsync`（人工排除）**沒有畫面入口**，所以連逐張排除都做不到；
- 沒有「重送」的入口。

於是每一張 AP/AR 發票的 `ErpSyncStatus` 生下來是 `Pending`，就永遠是 `Pending`。

---

## 2. 為什麼 `ERP_SYNC_LAG` 被改成「僅提示」（W4-F7）

原本 `ERP_SYNC_LAG` 是**擋關帳**的硬閘。把上一節合起來看，那條規則的實際效果是：

> 每開一張發票，24 小時後就多一筆關帳阻擋，只增不減；
> 而唯一的解除辦法沒有入口。**關帳在任何部署上都做不到。**

2026-09-06 在 dev 庫實測：ADM 廠 25 筆異常裡有 19 筆是 `ERP_SYNC_LAG`。

修正的分界線寫在 `Modules/ACT/Services/IDataAuditService.cs` 的
`AuditSeverityRules`：**錯的是本系統的帳，才擋關帳；錯的是對外介面的狀態，只提示。**
`ERP_SYNC_LAG` 屬後者——帳本身完整且平衡，拋不出去是外部系統的可用性問題。

> 若你接上了 ERP 橋接，**不要**把 `ERP_SYNC_LAG` 改回硬閘。理由見 §4。

---

## 3. 要接的話：從哪裡下手

四件事，順序無所謂，但缺一不可。

### 3.1 一支 `IErpGateway`（你自己的，不要動 `IErpSyncService`）

`IErpSyncService` 管的是**狀態**，不該知道傳輸方式。新增一支介面負責「送出去」：

```csharp
public interface IErpGateway
{
    /// <summary>回傳 ERP 端的傳票號與金額；拋例外代表這次失敗。</summary>
    Task<(string VoucherNo, decimal? Amount)> PushAsync(APInvoice invoice, CancellationToken ct);
    Task<(string VoucherNo, decimal? Amount)> PushAsync(ARInvoice invoice, CancellationToken ct);
}
```

實作放在自己的模組（`Modules/{YourErp}/`），於 `{YourErp}Module.cs` 的
`RegisterServices` 註冊——不要改 `Program.cs`（見 `CONSULTANT-ARCHITECTURE.md`）。

### 3.2 一支排程

參考 `Services/` 下既有的每日排程（**先算下一個固定時刻再 Delay**，不要寫
`Task.Delay(24h)` 迴圈——那種寫法只要不是開機當下就永遠跑不到，本專案踩過）。
迴圈本體：

```
取 Pending/Failed 且 RetryCount < 上限的單
  → gateway.PushAsync
      成功 → _erpSync.MarkSyncedAsync<T>(docId, voucherNo, amount)
      失敗 → _erpSync.MarkFailedAsync<T>(docId, ex.Message)
```

`MarkSyncedAsync` 內建金額比對：ERP 回來的金額與 MES 差超過尾差容忍就擲錯，
**不會**留下一個「同步成功但金額不同」的假象。這是刻意的，不要拿掉。

### 3.3 兩個畫面入口（這是目前最大的缺口）

沒有這兩個，橋接接上去也只是把「永遠 Pending」換成「永遠 Failed」：

1. **拋轉狀態清單**：可依狀態／廠別／日期篩選，顯示 `ErpErrorMessage` 與
   `ErpRetryCount`。資料都在，只差一支 Controller + View。
2. **重送與排除**：重送＝把 `ErpSyncStatus` 退回 `Pending` 並清 `RetryCount`；
   排除＝呼叫 `MarkExcludedAsync`。兩者都是 `[HttpPost]`，記得補權限檢查（R13）。

### 3.4 一個決定：拋什麼

本系統的總帳（`JournalEntries` / `JournalLines`）是完整的，
科目對照見 `Modules/ACT/StandardAccounts.cs` 與 `business_data_flow_reconciliation.md`。
兩種常見做法：

- **拋發票**（AP/AR 子帳）：ERP 端自己產生分錄。目前的 `Erp*` 欄位就是為這個設計的。
- **拋傳票**（`JournalEntry`）：ERP 端只收分錄。這條路目前**沒有**狀態機欄位，
  要自己在 `JournalEntries` 上補一組，或另建對照表。

選哪一條取決於「哪一邊是總帳的主人」。若 ERP 是主帳，拋傳票較乾淨；
若 MES 是主帳、ERP 只做應收付與稅務，拋發票較貼近現有結構。

---

## 3.5 `ERP_SYNC_LAG` 的 24 小時門檻：在等什麼、誰給、給了之後改哪裡（2026-09-24）

`manual-test-plan.md` §11 待決 #4 掛著「⏸ 等上線數據」快一個月，但沒有寫清楚
**等的到底是什麼**。這一節就是把它寫清楚，免得它變成一個沒有人知道怎麼結案的待辦。

### 在等什麼

**真實拋轉流量的延遲分佈。** 目前的 24 小時是**憑空訂的，不是量出來的**——
在沒有任何外部 ERP 可拋的情況下，那個數字沒有任何依據。

### 現況：這條規則的鑑別力是 0

2026-09-24 查開發庫：

| | 張數 | 最早一張 | `ErpSyncStatus` |
|---|---|---|---|
| APInvoice | 18 | 2026-07-24 | 全部 `Pending` |
| ARInvoice | 5 | 2026-08-16 | 全部 `Pending` |

**23 張全部逾期，命中率 100%。** 一條永遠亮著的規則等於沒有規則：它不會告訴你
任何事，只會把稽核清單塞滿，讓真正的異常混在裡面不被看見。
（W4-F7 已把它降為提示類所以不擋關帳——但**看不見**與**擋不住**是兩個不同的問題，
降級解決的是後者。）

⚠️ `manual-test-plan.md` §1 的表寫的是「3 張」，那是 2026-09-05 的數字。
單據會繼續長，**這個數字不必回頭維護**——重點是比例而不是張數，而比例一直是 100%。

### 怎麼決定那個數字（不是拍一個數）

這條規則要偵測的是**異常**，不是**慢**。所以門檻要訂在「正常延遲的上界之外」：

1. 橋接上線後累積**至少兩週**的 `ErpSyncStatus` 轉換時間（`Pending → Synced` 的間隔）。
   兩週是為了涵蓋到月結那幾天——那是拋轉量最大、延遲最長的時段，
   用平日資料訂出來的門檻會在月結當天整批誤報。
2. 取該分佈的 **p99**，再加一個緩衝（建議 ×1.5 或 +2 小時，取大者）。
3. 檢查結果是否落在**人會看到的節奏**上：門檻若短於一個班次（8 小時），
   夜間的正常積壓會在早上變成一排紅字；若長於 48 小時，真的斷線兩天才被發現，
   那時已經跨月。**合理區間大致是 8～48 小時**，24 只是這個區間的中點，不是量測結果。

### 誰給

接上橋接的那一方（見 §3.1／§3.2）產生資料，**專案負責人裁決數字**。
`manual-test-plan.md` §11 #4 的裁決人一欄寫的就是專案負責人。

### 給了之後改哪裡

`Modules/ACT/Services/DataAuditService.cs`：

```csharp
/// <summary>ERP 拋轉滯留時數（§2.2）。</summary>
internal const int ErpSyncLagHours = 24;
```

⚠️ **它是 `const`，也就是編譯期常數**——調一次要改程式碼、重新建置、重新部署。
而這是一個**天生需要現場調校**的參數：不同客戶的 ERP、不同的網路、不同的拋轉批次
大小，合理值都不一樣，而且會隨營運變化。

同一支服務裡的 `_qtyTolerance`／`_amountTolerance` 是**可設欄位**不是常數，
專案也已經有 `SystemParams`／`PlantParams` 兩層參數機制（`mfg_simplified_mode`
就是用它做到分廠覆寫的）。所以把它改成參數不是新增機制，只是套用既有的那一套。

**但先不要為了這件事動它**：在沒有任何實際數據之前，把一個猜出來的常數搬成一個
猜出來的參數，只是讓它更容易被繼續猜。**正確順序是：先有數據 → 訂出數字 →
連同「改成參數」一起做一次**，那時才有東西可以填進去，也才驗得出來改對了沒有。
同樣的話適用於旁邊的 `GrIrAgingDays = 30`（30 天同樣沒有量測依據）。

### 結案條件

這一項可以從「⏸ 等上線數據」改成「完成」的條件是**三件事同時成立**：

1. 有至少兩週的實際拋轉延遲分佈，且涵蓋一次月結；
2. 依上述方法訂出數字並由專案負責人確認；
3. 門檻改為可設定（`SystemParams`，允許 `PlantParams` 覆寫），並補一支測試釘住
   「設了就生效、沒設退回預設值並留警告」——與 `Jobs:TimeZone` 同一個模式
   （見 `manual-test-plan.md` §10.3 TZ-5：退回可以接受，**悄悄地**退回不行）。

在三件事齊備之前，**這一項不該被當成待辦排進任何一個波次**——
它不是沒人做，是條件不存在。

---

## 4. 不要這樣接

### 4.1 不要把關帳綁在外部系統的回應上

這是 W4-F7 的教訓。ERP 拋轉失敗的正確處理是「看得到、能重送」，
不是「本系統從此不能結帳」。外部系統維護、憑證過期、網路不通都會發生，
把它接成硬閘等於把別人的可用性變成自己的死結。

要有硬閘也該是**期末的檢查清單項**（月結作業的其中一步，可由人判斷後放行），
不是 `ClosePeriodAsync` 裡不可繞過的 `IsPassed`。

### 4.2 不要在拋轉時改動單據金額

`MarkSyncedAsync` 只寫 `Erp*` 欄位，不動 `TotalAmount` 或任何明細。
若 ERP 回來的金額不同，那是**對帳問題**，該讓它擲錯並留在清單上給人看，
不要「以 ERP 為準」自動覆寫——那會讓 MES 的總帳與子帳當場對不起來。

### 4.3 不要重用 `SourceDocType` / `SourceDocId` 當 ERP 對照

那兩個欄位是**本系統內部**的來源單據追溯（發票 → 進貨單／出貨單），
稽核 `GS_NO_AR_INVOICE`、`RETURN_SUBLEDGER_UNREVERSED` 都靠它。
ERP 端的識別請一律放 `ErpVoucherNo`。

---

## 5. 已知的小瑕疵（接的時候會遇到）

- **`ErpSyncService.MarkFailedAsync` 的三元運算式兩邊相同**：
  「永久失敗」與「暫時失敗」都寫成 `ErpSyncStatus.Failed`，兩者在狀態上無法區分。
  判斷是否鎖定要另外呼叫 `IsLocked(entity)`（比對 `ErpRetryCount >= MaxErpRetryCount`）
  ——但 **`IsLocked` 沒有列在 `IErpSyncService` 介面上**，透過 DI 拿到的型別呼叫不到。
  接橋接時請一併處理（把 `IsLocked` 提上介面，或讓狀態真的分成兩個值）。
- **`GetPendingOverdueAsync` 只回 `APInvoice`，AR 是另一支 `GetPendingOverdueArAsync`**。
  泛型化到一半，新增第三種可拋轉單據時要記得補第三支，或先把它泛型化。
- **`MaxErpRetryCount` 是常數 3**，不是系統參數。要可調就自己接
  `ISystemParamService`（尾差容忍 `audit_amount_tolerance` 已經是這樣做的，可照抄）。

---

## 6. 相關檔案索引

```
Models/IErpSyncEntity.cs                      狀態機欄位（Core）
Modules/FIN/Models/IErpSyncDocument.cs        FIN 擴充（TotalAmount / UpdatedAt）
Modules/FIN/Services/ErpSyncService.cs        狀態機實作
Modules/ACT/Services/IDataAuditService.cs     AuditSeverityRules（ERP_SYNC_LAG 為提示類）
Modules/ACT/Services/DataAuditService.cs      AuditErpSyncLagAsync
SmartFactory.MES.Tests/FIN/ErpSyncServiceTests.cs   狀態機的既有測試
.claude/specifications/reconciliation-decisions.md  D3：拋轉決策紀錄
.claude/specifications/roadmap-fin-cst-sub.md       §9 P-3：泛型化的緣由
```
