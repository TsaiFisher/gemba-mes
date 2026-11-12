# 系統參數廠別化（Plant-scoped SystemParam）— 設計與盤點

> 2026-08-17 撰寫。**尚未實作**。所有「現況」欄位皆逐項查證程式碼（含行號），非引用既有文件自報。
>
> 本文件是 [`manual-test-plan.md`](manual-test-plan.md) §11 **待決 #1 方案 A** 的設計文件，
> 同時收口該節 **#2**（`SystemParam` 主鍵變更）與 **#3**（`MfgStandbySchedulable` 註解與實作不符）。
>
> **範圍是「廠別」不是「公司別」**——系統中不存在法人／公司別維度：
> [`Modules/BAS/Models/Company.cs:19`](../../Modules/BAS/Models/Company.cs) 的 `Company` 是
> **來往公司（客戶／供應商）**主檔，[`Models/Plant.cs`](../../Models/Plant.cs) 也沒有上層公司欄位。
> 唯一的組織隔離維度是 `PlantId`。若日後真要法人層，本設計的 fallback 鏈可原樣加一層，見 §4.2 註。

---

## 0. 一句話結論

**可行、值得做；資料層完全落在 ProjectZ，畫面層需要 Core 開一個擴充點。實際範圍遠小於直覺。**

29 個系統參數（ProjectZ 21＋Core 5＋`ContributionMargin.*` 3）裡，真正該廠別化的是 **13 個**
（INV 2＋MFG 11），其中 **6 個是修既有缺陷而非新功能**（值本身是廠別相依的倉庫／儲位代號，
或註解已宣稱廠別隔離而實作沒有）。**其餘 16 個不該做**——理由是資料模型與帳務語意，不是成本。

**Core 的涉入只有一處**：儲存與讀寫完全落在 ProjectZ 自有的表與服務裡（§3.1–3.4）——
Core 的 `SystemParamController`／`Index.cshtml` 只處理 5 個公司識別 key（見 §1.1），
那 21 個 ProjectZ key 從頭到尾只被 ProjectZ 自有的 `SystemParamEnhancementFilter` 讀寫。
**唯一需要 Core 配合的是畫面**：2026-08-17 裁決設定入口掛在**廠別主檔**（§3.5 方案丙），
而 `Views/Plant/_PlantForm.cshtml` 沒有擴充點，需 Core 補一個一行委派的 partial hook。

技術上最大的風險不在 schema，而在**無 HTTP 上下文的背景服務會靜默退回全域值**（§4.3）。

---

## 1. 現況（已查證）

### 1.1 參數基建與檔案歸屬

| 檔案 | 歸屬 | 關鍵事實 |
|------|------|----------|
| [`Models/SystemParam.cs:8`](../../Models/SystemParam.cs) | **Core** | PK **只有 `Key`**；類別註解自陳「系統全域參數（KV 形式，**非廠別隔離**）」 |
| [`Services/SystemParamService.cs:29`](../../Services/SystemParamService.cs) | **Core** | `ISystemParamService.GetAsync(string key)` **無 `plantId` 參數**；以單一 cache key `"SystemParams"` 把整份參數表快取 10 分鐘 |
| `Controllers/SystemParamController.cs` | **Core** | **ViewModel 只有 5 個欄位**（`CompanyNameZh/En`、`CompanyShortZh/En`、`LogoPath`）——Core 完全不認識那 21 個 ProjectZ key |
| `Views/SystemParam/Index.cshtml` | **Core** | 同上，只有那 5 個輸入框，最後一行委派給 `_SystemParamModuleExtensions` |
| `Views/SystemParam/_SystemParamModuleExtensions.cshtml` | **Core**（在 `scripts/core-drift-baseline.txt`） | 依 LOCAL_RULES 只保留一行委派，不得往裡面加東西 |
| [`Views/SystemParam/_ProjectZSystemParams.cshtml`](../../Views/SystemParam/_ProjectZSystemParams.cshtml) | ProjectZ 自有 | 所有模組設定區塊 |
| [`Filters/SystemParamEnhancementFilter.cs`](../../Filters/SystemParamEnhancementFilter.cs) | ProjectZ 自有 | Index 投影到 ViewData、Save 讀 Form 持久化 |
| [`Models/ProjectZSystemParam.cs`](../../Models/ProjectZSystemParam.cs) | ProjectZ 自有 | 21 個 key 常數（另 5 個公司識別 key 在 Core 的 `SystemParamKeys`；`ContributionMargin.*` 3 個以字面值寫在 `Modules/RPT/Services/ContributionMarginService.cs:23-25`） |
| [`Data/ApplicationDbContextExtensions.cs`](../../Data/ApplicationDbContextExtensions.cs) | ProjectZ 自有 | 顧問模組的 DbSet 與 Fluent API——**新增實體與 migration 是 ProjectZ 原生工作** |

> ✅ **關鍵事實：Core 只擁有那 5 個公司識別 key。** 21 個 ProjectZ key 從頭到尾只被
> ProjectZ 自有的 `SystemParamEnhancementFilter` 投影與持久化，Core 的 ViewModel／View／Controller
> 都不認識它們——Core 的 KV 表只是被借去當儲存體。
>
> 因此**廠別覆寫的資料層不需要動 Core 任何一支檔案**：ProjectZ 自己擁有一張 `(Key, PlantId)`
> 的表即可，上面四支 Core 檔維持原樣。設計見 §3。
>
> ⚠️ **畫面層是另一回事**：§3.5 裁決把設定入口掛在廠別主檔，而
> `Views/Plant/_PlantForm.cshtml`（Core 檔，**不在** `core-drift-baseline.txt`）
> 全篇沒有任何 `PartialAsync` 掛載點——需要 Core 補一個，見 §3.5 與 §3.7。

### 1.2 廠別基建——全部現成，不需新增任何維度

| 元件 | 出處 |
|------|------|
| `IPlantEntity` + Global Query Filter（`PlantId == CurrentPlantId`） | [`Data/ApplicationDbContext.cs:169`](../../Data/ApplicationDbContext.cs) |
| `ApplyGlobalValues()` 自動填 `PlantId ??= CurrentPlantId` | [`Data/ApplicationDbContext.cs:239`](../../Data/ApplicationDbContext.cs) |
| 使用者目前廠別 `ActivePlantId`、角色可用廠別 `AllowedPlantIds` | [`Services/IUserContext.cs`](../../Services/IUserContext.cs) |
| 請求期注入 `dbContext.CurrentPlantId` | [`Middleware/UserPreferenceMiddleware.cs:31`](../../Middleware/UserPreferenceMiddleware.cs) |
| 角色↔廠別授權 | `Models/RolePlantAssignment.cs` |

**因此本案不需要**：新主檔、新的「目前範圍」切換 UI、新權限模型、使用者流程變更。

### 1.3 這件事文件早就宣告過

[`CLAUDE.md`](../../CLAUDE.md)〈新增 Entity 的判斷清單〉的「✅ 適合廠別的特徵」明列
**「廠別設定（單號格式、系統參數）」**。單號格式做到了，系統參數沒有。
**本案是補上文件既有宣告，不是新需求。**

### 1.4 現成的設計範本：`DocNumberConfig`

[`Modules/BAS/Models/DocNumberConfig.cs:12-30`](../../Modules/BAS/Models/DocNumberConfig.cs)
已經替「廠別設定」解過同一題，且解法比逐廠複製一份更好：

- `PlantId` 是**可編輯**欄位，可存逗號分隔多廠清單（`"P01,P02"`）→ 規則相同的多廠共用一筆，不必逐廠各建；
- 序號 scope（`SequenceScope`）與可變的 `PlantId` **解耦**，增減適用廠別不會換掉 scope。

系統參數若走「群組共用」形狀可直接沿用；本設計 §4.1 選擇較保守的單廠鍵，理由與取捨見該節。

---

## 2. 參數裁決（全部 29 個）

> 母體＝`ProjectZSystemParamKeys` 21 個＋Core `SystemParamKeys` 5 個＋
> `ContributionMarginService` 以字面值宣告的 3 個。合計 13 個該廠別化、16 個不做。

### ① 修缺陷 —— 現況在多廠環境即為錯誤，優先做

| 參數 | 缺陷 |
|------|------|
| `mfg_fg_warehouse` / `mfg_fg_location` | 值是倉庫／儲位代號，而 [`Warehouse`](../../Modules/BAS/Models/Warehouse.cs) 的主鍵是 **`WhCode` 單欄（全域唯一）** 且實作 `IPlantEntity`——一個代碼**只屬於一個廠**。參數卻是全域單值，故在其他廠必然指不到、或指到他廠倉。呼叫端 `ProductionCompletionController` |
| `mfg_default_staging_wh` / `mfg_default_staging_location` | 同上。是 `StagingSourceService` 三層 fallback 的最後一層 |
| `mfg_default_issue_method` | [`ProjectZSystemParam.cs:33`](../../Models/ProjectZSystemParam.cs) 註解原自寫「**廠別**預設發料方式」，實作全域（註解已於 2026-08-17 更正）。**全庫無任何呼叫端**（純宣告）。**2026-08-19 依待決 #8 自 `PlantParamCatalog` 移除**——沒有讀取端的參數放進目錄，只會變成畫面上設了不會有效果的欄位；常數與全域值保留，接上 `mfg-domain.md` §25.6 的三層 fallback 後再放回 |
| `mfg_standby_schedulable` | [`ProjectZSystemParam.cs:19`](../../Models/ProjectZSystemParam.cs) 註解宣稱「支援廠別隔離」，`SchedulingEngine` 讀的卻是全域值 → 即 `manual-test-plan.md` §11 #3 |

> 這 6 個不論本案整體是否推進，**註解與實作的落差都該先消除**（改實作或改註解二選一）。

### ② 值得做 —— 呼叫端已有廠別上下文

| 參數 | 呼叫端廠別可得性 |
|------|------------------|
| `inv_simplified_mode` | 11 個呼叫端：10 個 Controller（有 HTTP 上下文）＋ `MrpEngine`（已用明確 `ctx.PlantId`）。⚠️ 新增邊界見 §4.1 |
| `mfg_simplified_mode` | `MfgSimplifiedModeService`（請求期）＋ `MrpEngine`（`ctx.PlantId`） |
| `mfg_material_gate` | `DispatchController`（HTTP） |
| `mfg_machine_status_gate`（2026-09-29 新增） | 桌機 `DispatchController.StartSession`、手機 `MobileMfgController.StartSession`，皆以**派工的** `PlantId` 讀；判定走 `MachineAvailability`（與排程共用，含 `mfg_standby_schedulable`）。一開始就是廠別參數，不經過「全域→廠別化」這一步 |
| `mfg_lot_policy` | `MrpEngine`（`ctx.PlantId`） |
| `mfg_mrp_auto_close_obsolete_pr` | `MrpRunExecutionService`（run 帶 `PlantId`） |
| `mfg_wo_approval_threshold` | `WorkOrderReleaseService`（`:433` 取設定、`:446` 取閥值，**兩處都是廠別**）。見下方「已知不一致」——2026-08-30 已全部收口 |

> **已知不一致（本案的最佳佐證）** ——**2026-08-30 已收口，理由留在原地不要刪**：
> [`WorkOrderReleaseService.cs`](../../Modules/MFG/Services/WorkOrderReleaseService.cs) 原本先以
> `GetApprovalConfig(DocType, _userContext.PlantId)` 取**廠別**簽核設定，下一行卻退回**全域**金額閥值——
> 同一段判斷邏輯一半廠別、一半全域。此處另有設計選項，見〈待決 #4〉。
>
> 🐞 **廠別化修好之後，2026-08-30 才發現這個閥值從頭到尾就是死碼**：閥值分支只在「沒有簽核組態」
> 的分支內執行，而該分支的上一行守衛必定成立並 `return`，兩條路都走不到「金額超標所以送審」。
> 也就是說本節 2026-08-17 記為「收口」的那次修正，**修的是這個值怎麼讀，不是它讀了有沒有用**。
> 已改為 [`mfg-domain.md`](mfg-domain.md) §18.2 的快速通道語意（有組態就要簽，標準成本 ≤ 閥值者自動核准；
> 沒有範本一律直接核准），並補上全站唯一漏看的 `IsApprovalRequired`。
> **對本規格的教訓**：§8 那次取用端稽核逐一核對了 12 個 key 的讀取點有沒有帶 `plantId`，
> 卻讓一個「有讀取點、結果被丟掉」的 key 通過——對使用者而言那與沒有讀取點完全相同。
> 日後同類稽核要多問一句：**這個值改變時，行為會不會跟著變**。
>
> ⚠️ **2026-08-30 後記：這個診斷只看到了表面。** 廠別化把兩邊對齊之後，閥值仍然**完全無效**——
> 它的分支只在「沒有簽核組態」時執行，而沒有組態時下一行的守衛必定成立並直接核准，
> 兩條路都走不到送審。也就是說 #4 當初是**在一個從來沒被執行過的分支上**選落點，
> 收口的是「設定放哪裡」，不是「設定有沒有用」。已於 2026-08-30 依 `mfg-domain.md` §18.2
> 的快速通道語意修正並補測試（見 `manual-test-plan.md` §3.4 ⑧／§12.7）。

### ③ 可做，但必須配守門

| 參數 | 條件 |
|------|------|
| `inv_default_cost_method` | **廠別化在資料上自洽**：[`InventoryCostLayer.cs:12`](../../Modules/INV/Models/InventoryCostLayer.cs) 是 `IPlantEntity`，MA 均價鍵為 `(PlantId, ProductCode, LocationCode, LotNo)`，各廠成本層本就分離。但**同一廠內改法仍不可追溯**（既有成本層與新法不連續），故必須配「該廠已有異動即鎖定」守門。且 `ProductCategory.CostMethod` 已提供更細的覆寫（系統參數只是後援），邊際效益偏低 → 見〈待決 #2〉 |

### ④ 不做

| 參數 | 理由 |
|------|------|
| `base_currency` | **法人層屬性，不是廠別屬性**。同一法人下多廠共用一本總帳與 `JournalEntry.AmountLocal`（[`Modules/ACT/Models/JournalEntry.cs:15,73`](../../Modules/ACT/Models/JournalEntry.cs) 為 `IPlantEntity`、本位幣單一）。讓 A 廠 TWD、B 廠 USD 等於把總帳拆成兩套幣別卻沒有帳套邊界，並牽動匯率方向慣例與全部財務報表。**廠別 scope 下比公司別更明確地不該做** |
| `srm_weight_quality` / `_delivery` / `_price` / `_service` | [`Modules/PUR/Models/VendorEvaluation.cs:17`](../../Modules/PUR/Models/VendorEvaluation.cs) 註解明寫「全域，**非 `IPlantEntity`**（供應商評核全公司共用）」，且分數取**跨廠** NCR/OTD。權重分廠會產生同一供應商多個 `TotalScore`、卻只有一張評核卡。要分廠得**先讓 `VendorEvaluation` 變成廠別實體**——那是另一個題目，成本遠大於權重本身。**故「採購（SRM）設定廠別化」的答案是否定的** |
| `audit_qty_tolerance` / `audit_amount_tolerance` / `fin_settle_tolerance` | [`DataAuditService.cs:66`](../../Modules/ACT/Services/DataAuditService.cs) 的 `RunAllAsync(plantId: null)` 是**跨廠**稽核，多門檻會讓同一次稽核結果無法解釋；且這是技術精度常數（NUMERIC 最小單位、本位幣精度），非商業政策。`ErpSyncService` 亦共用同一容忍值 |
| `ContributionMargin.{Material,Labor,Overhead}Variable` | 報表用，查詢列已可單次覆寫並存為預設，全域預設足夠 |
| `company_name_zh/en`、`company_short_zh/en`、`company_logo`（Core 5 個） | **不需要廠別化**：查證 `Views/Shared/_PrintLayoutZ.cshtml` 與 `_PrintBase.cshtml` **完全未引用**這些參數（只有 `Pages/Index.cshtml`、`Pages/Shared/_Layout.cshtml` 的 app shell 在用）；而 [`Models/Plant.cs`](../../Models/Plant.cs) 已有 `Name`/`NameEn`/`AddressZh`/`AddressEn`。需要單據顯示各廠抬頭時**讀 Plant 主檔**即可 |

**淨範圍：13 個 ＝ INV 2 個（`inv_simplified_mode`＋條件性的 `inv_default_cost_method`）＋ MFG 11 個
（① 全部 6 個都是 MFG，② 另有 5 個 MFG）。
財務設定沒有可廠別化的標的；採購（SRM）設定需先改資料模型。**

---

## 3. 設計（ProjectZ 自有，**Core 零修改**）

> 2026-08-17 改版：原設計是「改 Core 的 `SystemParam` 換複合 PK」。查證後推翻——
> Core 的 Controller／View 只認得 5 個公司識別 key，那 21 個 ProjectZ key 純粹是
> ProjectZ 自有的 Filter 借 Core 的 KV 表當儲存體。廠別覆寫因此完全可以落在 ProjectZ 自己家裡。

### 3.1 儲存：ProjectZ 自有的廠別覆寫表

```csharp
// Modules/BAS/Models/PlantParam.cs（ProjectZ 自有）
public class PlantParam
{
    [MaxLength(100)] public string Key     { get; set; } = "";
    [MaxLength(20)]  public string PlantId { get; set; } = "";   // 複合 PK 的一半，故不可為 null
    public string?   Value     { get; set; }
    public DateTime  UpdatedAt { get; set; } = DateTime.UtcNow;
}
// PK: (Key, PlantId)；DbSet 與 Fluent API 加在 Data/ApplicationDbContextExtensions.cs（ProjectZ 自有）
```

**刻意不實作 `IPlantEntity`**：全域查詢過濾會讓「讀別廠的覆寫值」變成做不到，而設定畫面本來就
要能列出／編輯各廠。廠別比對一律由服務層明確帶入。（`ReportDefinition` 已有同樣的取捨先例。）

**儲存方案：✅ 2026-08-17 裁決＝方案甲**（使用者裁決）。

| 方案 | 內容 | 代價 |
|------|------|------|
| **甲 ✅ 採用** | 新表只放**廠別覆寫**；全域值繼續留在 Core 的 `SystemParams`。解析＝廠覆寫 → Core 全域 → 內建預設 | 既有資料一列都不用動，`_ProjectZSystemParams.cshtml` 的全域欄位一行不用改；代價是同一個參數的值分散在兩張表 |
| 乙（未採用） | 21 個 key 全搬進新表，Core 表只留公司 5 個 key | 語意最乾淨、Core 表回歸本份；代價是一次性資料搬遷 migration，且 Filter 的全域寫入端要一起改指向新表 |

### 3.2 解析順序（三層 fallback）

```
該廠覆寫（PlantParam）→ 全域值 → 程式內建預設
```

**第三層不得省略**：LOCAL_RULES 既有規則「**未設定（null）必須等同舊行為**，不寫預設列，
讓『從未設定』與『明確設為 false』可區分」在廠別化後仍然成立，且現在有兩個層級要各自維持這個
性質（「該廠從未設定」≠「該廠明確設為 false」）。

> 日後若真的需要法人層，這條鏈原樣插一層即可：`廠 → 公司 → 全域 → 內建`。本設計不預先實作。

### 3.3 服務介面（ProjectZ 自有）

```csharp
// Modules/BAS/Services/PlantParamService.cs（ProjectZ 自有）
public interface IPlantParamService
{
    Task<string?> GetAsync(string key, string? plantId, CancellationToken ct = default);
    Task SetAsync(string key, string? plantId, string? value, CancellationToken ct = default);
}
```

- **不擴充 Core 的 `ISystemParamService`**（那是 Core 檔）。ProjectZ 約 30 個讀取點改呼叫這支；
  Core 的服務只在方案甲的「全域層」被內部沿用。
- **刻意不提供無 plantId 的多載**。這正是 §4.3 那個「背景服務靜默退回全域值」的根治法：
  新介面一開始就不給「自動抓當前廠別」的糖，呼叫端必須明示，於是無 HTTP 上下文的
  `MrpEngine`／`SchedulingEngine`／`PreparationAlertBackgroundService` 不可能寫出會靜默失效的程式碼。
  **靠型別擋，不靠紀律擋。** 請求期的呼叫端從 `IUserContext.PlantId` 取值即可。

### 3.4 快取

比照 Core：一顆 `IMemoryCache` 存整份 `Dictionary<(string Key, string PlantId), string?>`
（一次查詢、一顆快取，不做 per-plant 快取以免 N 廠 N 份），`SetAsync` 整份失效。
方案甲的全域層仍走 Core 的 `ISystemParamService`（它自己有 10 分鐘快取），不重複快取一次。

LOCAL_RULES 既有規則「**不可直接對 `DbContext` 增刪 `SystemParams` 列**」原樣適用於新表——
直接動 DbContext 除了髒快取，還會繞過「全域列 vs 覆寫列」的判斷。

### 3.5 設定畫面 —— ✅ 2026-08-17 裁決＝方案丙（掛進廠別主檔）

**裁決：廠別參數與廠別管理一併設定**——編輯某廠時，在同一個側面板裡先看到「這個廠是誰」，
往下就是「這個廠怎麼運作」。使用者裁決；未採用的甲（系統參數頁加廠別下拉）與乙（獨立畫面）
的比較見草圖與下方代價欄。

| 方案 | 內容 | 結果 |
|------|------|------|
| **丙 ✅ 採用** | 在既有廠別維護畫面（`PlantController` + `Views/Plant/_PlantForm.cshtml`）內編輯該廠參數 | 入口直覺、與廠別建檔同一個動作；代價見下 |
| 甲（未採用） | `_ProjectZSystemParams.cshtml` 內加廠別下拉 | 全域與覆寫兩種語意混在同一張表單 |
| 乙（未採用） | 另開「廠別參數設定」畫面 | 職責最清楚且能做各廠一覽，但多一支畫面 |

**方案丙要付的三筆代價（都已知，不是意外）**：

1. **需要 Core 補擴充點**（唯一的 Core 變更）。比照 `SystemParam` 的既有模式：
   Core 新增 `Views/Plant/_PlantFormModuleExtensions.cshtml`（內容為空注解），
   並在 `_PlantForm.cshtml` 表單內加**一行** `@await Html.PartialAsync("_PlantFormModuleExtensions")`。
   ProjectZ 側則完全比照 `SystemParamEnhancementFilter` 的做法，不需要 Core 動 Controller：
   新增 `PlantFormEnhancementFilter` 攔截 `PlantController`，Edit/Create 的 GET 投影 ViewData、
   POST 之後從 `Request.Form` 讀值寫入 `PlantParam`（`Plant` 模型不含這些欄位，
   多餘的表單欄位本來就不會被模型繫結吃掉）。
2. **側面板空間**。面板寬度由 `Views/Plant/Index.cshtml`（Core 檔）的
   `u-width-min-520px-95vw` 決定。13 個三態欄位塞進 520px 會很擠——
   先以「模組分組、預設收合」處理（庫存 2 個、製造 11 個），
   **不**為此再要求 Core 加寬；若實測仍不堪用，再單獨評估 `u-width-min-680px-95vw`。
3. **失去「各廠覆寫一覽」**。方案乙的一覽表能一眼看出哪幾個廠跟預設不一樣，
   丙必須逐廠點進去。→ 見〈待決 #7〉。

**三態控制項**（三案共通，草圖已定稿）：覆寫欄位必須能表達「繼承全域／覆寫」，
並在下方以 `form-text` 對照全域值（覆寫時全域值加刪除線）。LOCAL_RULES 對 checkbox 的既有要求
（`<input type="hidden" value="false" />` 配對）只夠表達兩態，覆寫欄位需改用三選一控制項。

### 3.6 權限

沿用既有 `IUserContext.AllowedPlantIds` 限制「只能改自己有權的廠別」，否則 A 廠管理員可改 B 廠的
過帳模式與成本法。這一層方案丙自然滿足——廠別主檔畫面本來就受廠別授權管轄。

~~⚠️ **但方案丙帶來一個新問題**：參數編輯權因此綁在 `PlantManagement` 這個選單權限上。~~
**2026-08-18 查證後推翻**：`PlantController` 與 `SystemParamController` **都是 `[Authorize(Roles = "Admin")]`**，
兩支都不走選單權限。把參數從系統參數頁搬到廠別頁，**可編輯的人群沒有任何變化**（本來就只有 Admin），
方案丙不帶來權限擴散。→ 待決 #6 據此收口。

不過 `PlantFormEnhancementFilter` 的寫入端仍檢查 `IUserContext.AllowedPlantIds`（非空且不含目標廠即不寫入）。
這是縱深防禦，不是現在需要的功能：哪天 Core 把 Plant 的授權從 Admin-only 放寬成選單權限，
寫入端不會跟著失守。

### 3.7 邊界檢查表 —— Core 只動一個擴充點

| 檔案 | 動作 | 邊界 |
|------|------|------|
| `Models/SystemParam.cs`、`Services/SystemParamService.cs`、`Controllers/SystemParamController.cs`、`Views/SystemParam/Index.cshtml` | **不動** | Core 檔，維持零漂移 |
| `Views/SystemParam/_SystemParamModuleExtensions.cshtml` | **不動** | 已收斂為一行委派 |
| **Core**：新增 `Views/Plant/_PlantFormModuleExtensions.cshtml`（空注解）＋ `Views/Plant/_PlantForm.cshtml` 加一行 `PartialAsync` | **Core-first** | §3.5 方案丙的前提。是「補擴充點」不是「改語意」，其他部署專案同樣受益；提交順序依 CLAUDE.md〈邊界 hook 與 Core 修正的提交順序〉：先 Core push 上 main → ProjectZ `git fetch upstream main` → 再提交本側改動 |
| `Controllers/PlantController.cs`、`Views/Plant/Index.cshtml` | **不動** | 讀寫走 ProjectZ 的 `PlantFormEnhancementFilter`；面板寬度不加寬（§3.5 代價 2） |
| `Modules/BAS/Models/PlantParam.cs`、`Modules/BAS/Services/PlantParamService.cs` | 新增 | ProjectZ 自有 |
| `Data/ApplicationDbContextExtensions.cs` ＋ 新 migration | 新增 | ProjectZ 自有（CLAUDE.md 明列禁止被 Core 覆蓋） |
| `Filters/PlantFormEnhancementFilter.cs`、`Views/Plant/_ProjectZPlantParams.cshtml` ＋ 雙語 resx | 新增 | ProjectZ 自有 |
| `Models/ProjectZSystemParam.cs`、約 30 個讀取點（`Modules/**`） | 改 | ProjectZ 自有 |

⇒ Core 漂移**只增加那一行 `PartialAsync`**（且是 Core 端自己的新擴充點，不進 ProjectZ 的漂移 baseline）。
F4/F5「上送 Core 的模型變更必帶 migration」不適用——這次上送的是一支 View 擴充點，沒有模型變更。

## 4. 分廠後才出現的語意邊界

### 4.1 `CrossPlantTransfer` 兩端模式不一致 —— ✅ 2026-08-17 裁決

**規則：需要判定簡化模式時，一律取 `ToPlantId`（入庫端）的值，不取來源廠、也不取當前使用者廠別。**
（使用者裁決）

理由：這張單**本來就是兩段式**，且兩段分屬不同廠——
[`CrossPlantTransferController`](../../Modules/INV/Controllers/CrossPlantTransferController.cs)
的 `Confirm`（XP，`direction=-1`，來源廠過帳 → `InTransit`）只有來源廠能執行
（`SourcePlantOnlyCanConfirmOut`），`Receive`（XI，`direction=+1`，目的廠過帳 → `Posted`）只有目的廠能執行
（`DestPlantOnlyCanReceive`）。簡化模式決定的是「收進來之後還要不要再走一次上架」——
那是**入庫端的作業現實**，來源廠無從代決。

> ⚠️ **實作要點（P2 時必看）**：讀取端必須明確帶 `doc.ToPlantId`，**不可以用 `IUserContext.PlantId`**。
> `Receive` 由目的廠使用者執行時兩者相同，看起來會過；但 `Confirm`、作廢回沖、或任何由來源廠
> 使用者觸發的路徑，取當前使用者廠別就會拿到**錯的那一端**，而且不會報錯。
> 這正是 §3.3「服務介面刻意不提供無 plantId 多載」要防的同一類失效。

**現況**：`CrossPlantTransferController` **完全沒讀這個參數**，所以今天沒有這個問題；
本規則是 P2 的實作約定，屆時才會出現第一個讀取點。

### 4.2 成本法切換不可追溯

見 §2 ③。守門位置應在寫入端（設定畫面）而非讀取端：該廠一旦有 `InventoryCostLayer` 或庫存異動即鎖定。

### 4.3 背景服務靜默退回全域值（**最高風險**）

以下 5 處在**無 HTTP 上下文**執行，若採「服務內部自動取 `IUserContext.PlantId`」，會拿到 null：

| 服務 | 現況廠別可得性 |
|------|----------------|
| `Modules/MFG/Services/MrpEngine.cs` | ✅ 已有明確 `ctx.PlantId` |
| `Modules/MFG/Services/SchedulingEngine.cs` | ✅ 方法簽章即帶 `string plantId` |
| `Modules/MFG/Services/PreparationAlertBackgroundService.cs:83` | ✅ 自己 `db.Plants` 逐廠迴圈 |
| `Modules/ACT/Services/DataAuditService.cs:73` | ⚠️ `plantId` 可為 null（跨廠稽核），語意上**本來就不該**取單一廠別參數 |
| `Modules/FIN/Services/ErpSyncService.cs:57` | ⚠️ 讀容忍值，屬 §2 ④「不做」清單 |

好消息是前三支本來就拿得到廠別；壞消息是**這種失敗不報錯、行為悄悄變**，故 §3.3 才要求明確多載。
（同類坑已有前例：背景服務注入 `IUserContext` 會靜默撈 0 筆。）

---

## 5. 實作階段

| 階段 | 內容 | 風險 |
|------|------|------|
| **P0** ✅ | ProjectZ 自有基建（§3）：`PlantParam` 實體 + migration + `IPlantParamService`（三層 fallback、只收明確 plantId）+ `PlantFormEnhancementFilter` + 廠別主檔面板區塊。**外加一個 Core 擴充點**（`_PlantFormModuleExtensions.cshtml`，須先上 Core main） | 中（資料層純 ProjectZ；風險在 30 個讀取點的改寫，以及 Core 擴充點的提交順序） |
| **P1** | 分掉 §2 ① 的 6 個參數（倉庫／儲位／發料方式／Standby）。**2026-08-17 已以替代路徑交付缺陷面，見 §5.1** | 低（修缺陷，行為原本就錯） |
| **P2** ✅ | 分掉 §2 ② 的 6 個參數，含 §4.1 跨廠調撥規則定案。**2026-08-18 已交付，見 §5.2** | 中 |
| **P3** | `inv_default_cost_method` ＋ 鎖定守門（視〈待決 #2〉） | 高（帳務） |
| **P4** ✅ | 待決 #7 收口：各廠覆寫一覽（唯讀報表）＋ 面板改頁籤並加寬。**2026-08-19 已交付，見 §5.3** | 低（唯讀；唯一風險是廠別可見範圍） |
| **不變** | Core 的 `SystemParam` 表與畫面：續作那 5 個公司識別 key 的家（方案甲下同時是全域層的家） | — |
| **不做** | `base_currency`、3 個容忍值、4 個 SRM 權重、3 個 ContributionMargin、5 個公司識別 | — |

### 5.2 P0／P2 —— ✅ 2026-08-18 已交付

| 交付 | 檔案 |
|------|------|
| 覆寫表與服務 | `Modules/BAS/Models/PlantParam.cs`、`Modules/BAS/Services/PlantParamService.cs`、`Migrations/*_AddPlantParam.cs` |
| 讀取端 | §2 ①② 的參數呼叫端改走 `IPlantParamService`（`mfg_default_issue_method` 本來就沒有呼叫端，2026-08-19 已依 #8 自目錄移除，實際有讀取端的是 11 個）；`IMfgSimplifiedModeService` 三個方法改收明確 `plantId` |
| 參數目錄 | `Modules/BAS/Models/PlantParamCatalog.cs` —— **檢視與 Filter 的單一事實來源**，加參數只改這一支 |
| 面板 | `Views/Plant/_PlantFormModuleExtensions.cshtml`（一行委派）→ `_ProjectZPlantParams.cshtml`（分組）→ `_PlantParamField.cshtml`（三態欄位）＋ 兩組雙語 resx |
| 讀寫 | `Filters/PlantFormEnhancementFilter.cs`（GET 投影、POST 持久化），註冊於 `BASModule` |

**實作時撞到、值得記的四件事**：

1. **面板內不能有任何 JS，也不能有行內樣式。** `Views/Plant/Index.cshtml`（Core 檔）自己寫了一份面板
   載入器：`body.innerHTML = DOMPurify.sanitize(html)`，**不走 `crud-panel.js`**。`innerHTML` 不執行
   `<script>`，而 `crud-panel.js` 裡那段「重建 inline script 並補主文件 nonce」的機制在這條路徑上不存在；
   DOMPurify 也會把 `<script>` 整段移除。CSP 又是 `style-src 'self'`（無 `unsafe-inline`），連 `style=""`
   都不行。⇒ 三態控制項全部靠 Bootstrap 既有 class 與伺服器端渲染完成；未勾「覆寫」時值控制項**不會**
   被 disable（沒有 JS 可以做），語意一律以核取方塊為準，由 Filter 決定。

2. **「hidden false + checkbox true」不可以用 `FirstOrDefault` 讀。** 瀏覽器依 DOM 順序送出兩個同名欄位，
   而 `_ProjectZSystemParams.cshtml` 把 hidden 寫在 checkbox 前面 —— 取第一個永遠拿到 `"false"`。
   **這是既有缺陷**：`SystemParamEnhancementFilter` 的庫存／製造簡化模式開關因此勾了也存不進去，
   而既有單元測試每次只塞一個值，剛好避開這條路徑。已一併修正（改用「任一值為 true」判定，與先後無關），
   並補上以真實瀏覽器順序送出的回歸測試。

3. **成功判定要涵蓋兩種回傳。** Core 的 `PlantController` 在 AJAX 下回 `Json{success}`、非 AJAX 下回
   `RedirectToAction`，驗證失敗則回表單 View。Filter 只在前兩者才寫入——否則廠別本身沒存起來，
   參數卻先落地了。

### 5.3 P4 —— ✅ 2026-08-19 已交付

| 交付 | 檔案 |
|------|------|
| 各廠覆寫一覽（待決 #7 方案 b） | `Modules/BAS/Controllers/PlantParamOverviewController.cs`、`Modules/BAS/ViewModels/PlantParamOverviewVm.cs`、`Modules/BAS/Views/PlantParamOverview/Index.cshtml` ＋ 雙語 resx |
| 選單／權限／說明 | `Services/ProjectZMenuContributor.cs`（`BasManagement` 群組）、`Services/HelpDocResolver.cs`、`wwwroot/docs/user/bas/plant-param-overview.html`（中英同頁） |
| 面板改頁籤並加寬 | **全部在 ProjectZ 側**（Core 不動）：`Views/Plant/_PlantForm.cshtml`（擴充區塊非空才長第二個頁籤）、`Views/Plant/Index.cshtml`（面板 520px → 720px）——兩支為就地漂移，已登錄 `scripts/core-drift-baseline.txt`；`_ProjectZPlantParams.cshtml` 經 `HttpContext.Items` 提供兩個頁籤標題 |

**實作時撞到、值得記的三件事**：

1. **「沒有勾選」與「沒有這個區塊」在 `Request.Form` 裡長得一模一樣。** Filter 原本一律當成
   「全部取消覆寫」，於是任何只帶 `Id`／`Name` 的 POST（腳本、外部整合、日後的精簡表單）都會把該廠
   覆寫**安靜地**清光——2026-08-18 用 curl 補測試資料時實際踩到，整頁覆寫瞬間歸零且無任何訊息。
   已加存在標記 `pp_present`（hidden 欄位 + Filter 守門），並補單元與整合兩層回歸測試。

2. **報表的廠別可見範圍必須自己收斂。** `PlantParam` 刻意不實作 `IPlantEntity`（§3.1），
   所以**沒有**全域查詢過濾器可依靠——矩陣若直接讀 `_context.Plants` 就會把所有廠攤開給任何能開這頁的人。
   實作以 `IUserContext.AllowedPlantIds` 先收斂廠別清單，再用該清單去撈覆寫列（不是撈完再濾），
   並有專門的測試釘住「範圍外廠別的覆寫值不得出現在輸出裡」。

3. **頁籤這層排版留在 ProjectZ，不上送 Core。** 第二個頁籤是為了「廠別參數覆寫」才長出來的，
   而那是本部署專案獨有的概念——Core 的 `_PlantFormModuleExtensions.cshtml` 是空的，同樣一段程式
   放進 Core 只會是**永遠走不到的死碼**（實測：Core 端 `dotnet run` 完全看不出差別）。
   代價是 `Views/Plant/{Index,_PlantForm}.cshtml` 成為就地漂移，已連同理由登錄 baseline。

4. **`Html.PartialAsync` 給 partial 的是 ViewData 的「複本」，寫入回不到宿主。**
   原本讓擴充區塊把頁籤名稱寫進 `ViewData` 交給宿主——宿主永遠拿不到，於是靜靜退回單頁排版，
   **頁籤整組不見、卻沒有任何錯誤**。反向（宿主 → 擴充區塊，如 `PlantFormSupportsTabs`）是通的，
   因為複本會帶著原有項目。改走整個請求共用的 `HttpContext.Items`；宿主是先把擴充區塊渲染成字串、
   再決定要不要長頁籤，故寫入必定早於讀取。這條相依由
   `PlantParamsPartial_RendersInsideHostFormTab` 直接斷言 `nav-tabs` 與兩個 pane id 守住。

### 5.1 P1 替代路徑 —— ✅ 2026-08-17 已交付（不動 Core、不動 schema）

**做了什麼**：新增
[`Modules/BAS/Services/PlantDefaultLocationResolver.cs`](../../Modules/BAS/Services/PlantDefaultLocationResolver.cs)
（`IPlantDefaultLocationResolver`）作為「參數值 → 該廠可用值」的**單一語意點**：倉庫代碼必須存在於
目標廠別且啟用才採用，否則視同未設定；儲位單獨判定（倉可用而儲位不可用時只清儲位）。
查詢刻意 `IgnoreQueryFilters()` 並明確比對 `plantId`——廠別過濾是 `PlantId == CurrentPlantId`
而非放行，無 HTTP 上下文時靠環境過濾會一筆都撈不到，於是每個預設值都被誤判成不可用。

接上兩個呼叫端：

- [`ProductionCompletionController.ConfirmForm`](../../Modules/MFG/Controllers/ProductionCompletionController.cs)
  ——原本會把一個下拉選單裡根本不存在的代碼設為預選值（表單靜默失去預設）。
- [`StagingSourceService.ResolveStagingTargetAsync`](../../Modules/MFG/Services/StagingSourceService.cs)
  ——三層解析的**每一層**都經收斂（含工作中心層：其倉庫可能已停用或被改到別廠，
  現改為往下一層找），且工作中心查詢補上 `w.PlantId == plantId`（原本只靠環境過濾）。

同時更正 [`Models/ProjectZSystemParam.cs`](../../Models/ProjectZSystemParam.cs) 三處註解：
`MfgStandbySchedulable`（原稱「支援廠別隔離」）、`MfgDefaultIssueMethod`（原稱「廠別預設發料方式」，
且**目前無任何讀取端**）、備料倉兩參數（原稱「全廠層級」）。→ 收口 `manual-test-plan.md` §11 #3。

**測試**（共 12 案）：

- `SmartFactory.MES.Tests/BAS/PlantDefaultLocationResolverTests.cs`（7 案，含無環境廠別上下文那案）；
- `StagingSourceServiceTests` 補 3 案（參數指向他廠倉 → null／工作中心倉停用 → 往下層／只清儲位保留倉庫）；
- `SmartFactory.MES.Tests/MFG/ProductionCompletionDefaultWarehouseTests.cs` 補 2 案
  （擁有該代碼的廠取得預設／不擁有的廠得到空值），走 `GetControllerContextWithServices` 樣板。

廠別收斂一律用**真實作**而非 mock——這條規則要驗的就是「值在不在這個廠」，用 mock 蓋掉等於驗掉自己。

> 寫這批測試時踩到的坑，記在這裡免得下一個人再踩：測試自己讀 `IPlantEntity` 實體要記得
> `IgnoreQueryFilters()`。廠別過濾是 `PlantId == CurrentPlantId`，而 `CurrentPlantId` 在測試裡
> 預設是 null，於是 `Single(...)` 直接 `Sequence contains no elements`——看起來像種子沒進去，
> 其實是被自己的過濾器擋掉。

> ⚠️ **交付的是缺陷面，不是能力面**。這四個參數**仍然是全系統單一值**：
> 現在 A 廠設了 `FG01` 之後，B 廠是「乾淨地沒有預設」而不是「有自己的預設」。
> 要讓各廠各有預設值，仍然需要 P0。**§2 ① 在本表的狀態因此是「缺陷已消除、廠別化未做」。**

---

## 6. 測試計畫

| 對象 | 內容 |
|------|------|
| `SystemParamEnhancementFilterTests`（既有） | 擴為 scope 版：全域投影／廠別覆寫投影／三態（繼承 vs 明確 false）／未設定 |
| [`InvSimplifiedModeIntegrationTests`](../../SmartFactory.MES.IntegrationTests/INV/InvSimplifiedModeIntegrationTests.cs)（既有） | 目前是「真 Filter → 真 Service（含 `IMemoryCache`）→ 真 Controller」**唯一**的貫穿測試，必須擴成兩廠：A 廠簡化、B 廠完整，同一份程式碼走出兩種行為 |
| 新增 | 三層 fallback 解析純函式測試（廠有值／廠無值走全域／皆無走內建） |
| 新增 | 快取失效：設 A 廠覆寫後，B 廠與全域讀值不受影響 |
| 新增 | 權限：無 B 廠權限的角色不得寫入 B 廠覆寫 |
| 新增 | **背景服務**：無 HTTP 上下文呼叫無參數多載時，行為符合 §3.3 的裁決（丟例外，不得靜默） |
| 各廠覆寫一覽（P4） | `SmartFactory.MES.Tests/BAS/PlantParamOverviewControllerTests.cs`：目錄每個參數 × 每個廠都要出現一格且順序對齊欄位／覆寫成空值仍算覆寫（不是繼承）／只顯示角色可見的廠／**範圍外廠別的覆寫值不得外洩**／無 Read 與無 Print 權限各自擋掉／resx 覆蓋目錄全部參數名 |
| 存在標記（P4） | `PlantFormEnhancementFilterTests.Save_WithoutParamSection_LeavesOverridesUntouched`（單元）＋ `PlantParamPanelIntegrationTests.SavePanel_FormWithoutParamSection_DoesNotWipeOverrides`（整合，真的走 HTTP POST） |
| 頁籤結構（P4） | `PlantParamPanelIntegrationTests.PlantParamsPartial_RendersInsideHostFormTab`：直接斷言 `nav-tabs`、兩個 pane id 與兩個頁籤名稱。**刻意不只斷言「名字有出現」**——頁籤根本沒長出來時，退回單頁排版的名字照樣會出現，舊斷言在真缺陷下仍是綠的（實際發生過） |
| 樣板 | `MultiPlantTests`、`ApprovalMultiPlantTests` 為現成的多廠測試樣板 |

⚠️ 跨 worktree 共用測試庫的既有陷阱（庫名寫死、跨進程零保護）在多廠測試下更容易互踩，
權限類測試請用專屬角色。

---

## 7. 待決項

<!-- open-items ref="plant-scoped-system-params.md §7" -->

| # | 待決 | 決策者 |
|---|------|--------|
| ~~1~~ | `inv_simplified_mode` 廠別化後，`CrossPlantTransfer` 兩端一簡一繁時取哪一端。**2026-08-17 裁決＝依入庫端（`ToPlantId`）**，規則與實作要點見 §4.1 | 專案負責人 |
| ~~2~~ | `inv_default_cost_method` 是否納入範圍。**2026-08-17 裁決＝不納入**（依評估建議，見 §7.1），理由：這個參數只在 `ProductCategory.CostMethod = SystemDefault` 時才生效，而品類是比廠別更貼近成本語意的維度；風險（改法不可追溯）卻是全案最高。若日後真出現「兩廠成本法不同」的實需，再單獨做並配寫入端鎖定守門 | 專案負責人 |
| ~~3~~ | **廠別覆寫的儲存方案**（§3.1）。**2026-08-17 裁決＝方案甲**：新表只放廠別覆寫，全域值續留 Core 的 `SystemParams`，解析＝廠覆寫 → Core 全域 → 內建預設。**零資料遷移**，`_ProjectZSystemParams.cshtml` 的全域欄位一行不用改；接受「同一參數的值分散兩張表」這個代價 | 專案負責人 |
| ~~4~~ | `mfg_wo_approval_threshold` 的落點。**2026-08-17 裁決＝方案 a（廠別化參數）**（依評估建議，見 §7.2）：用剛建好的 `PlantParam` 即可，零 Core 變更，且**立刻消除**`WorkOrderReleaseService.cs:432` vs `:436` 的半廠別／半全域不一致；加欄位到 `DocumentApprovalConfig`（Core 模型＋migration）語意更整潔，但那修的是「設定分散兩處」而非本案的不一致，列為日後上送 Core 的候選 | 開發 |
| ~~5~~ | **設定畫面形狀**（§3.5）。**2026-08-17 裁決＝方案丙**：掛進廠別主檔側面板，與廠別管理一併設定。需 Core 補一個 `_PlantFormModuleExtensions.cshtml` 擴充點；已知代價（面板空間、失去各廠一覽、權限綁 `PlantManagement`）見 §3.5 與待決 #6／#7 | 專案負責人 |
| ~~6~~ | **參數編輯權的落點**（§3.6）。**2026-08-18 收口＝原命題不成立，沿用現況、不另設權限鍵**。查證後發現 `PlantController` 與 `SystemParamController` **都是 `[Authorize(Roles = "Admin")]`**，兩支都不走選單權限——把參數從系統參數頁搬到廠別頁，可編輯的人群**完全沒變**（本來就只有 Admin）。所謂「基礎資料管理員因此獲得政策編輯權」的顧慮建立在「Plant 走 `PlantManagement` 選單權限」這個錯誤前提上。§3.6 的敘述已一併更正。另在 `PlantFormEnhancementFilter` 加了 `AllowedPlantIds` 縱深防禦：哪天 Core 把 Plant 授權放寬成選單權限，寫入端不會跟著失守 | 專案負責人 |
| ~~7~~ | **各廠覆寫一覽怎麼補**（原選項 a／b／c 見變更紀錄）。**2026-08-19 裁決＝方案 (b) 唯讀報表**，已交付：`Modules/BAS/Controllers/PlantParamOverviewController.cs` + `Modules/BAS/Views/PlantParamOverview/Index.cshtml`，參數 × 廠別矩陣，逐格標示「覆寫值／繼承」，可匯出 Excel。ProjectZ 自有、Core 零變更。**廠別可見範圍收斂於 `IUserContext.AllowedPlantIds`**（空清單＝角色無廠別指派限制，顯示全部），故這支報表不會成為跨廠資料的旁路。詳見 §5.3 | 專案負責人 |

| ~~8~~ | **`mfg_default_issue_method` 是死參數，但現在長在畫面上**（2026-08-19 稽核發現）。全庫**沒有任何讀取端**（`mfg-domain.md` ⑥ 已記錄），卻因為在 `PlantParamCatalog` 裡而出現在「廠別參數設定」頁籤與「各廠參數覆寫一覽」上——使用者設了什麼事都不會發生，且沒有任何提示。P0／P4 把它從「死宣告」推成了「會誤導的設定」。**2026-08-19 裁決＝方案 (b) 先自目錄移除**（依建議）：目錄的語意是「可廠別化的參數清單」，不該收沒有語意的項目。常數 `ProjectZSystemParamKeys.MfgDefaultIssueMethod`、全域值與四份 resx 的欄位名稱都保留（resx 加 `<comment>` 註明暫停用），既有資料不受影響；等 `mfg-domain.md` §25.6 的三層 fallback（`RoutingBom.IssueMethod` → `Product.DefaultIssueMethod` → 廠別預設 → `Push`）真的接上，把目錄那一行加回來即可 | 專案負責人 |
<!-- /open-items -->

> §2 ① 的「註解與實作不符」（`mfg_standby_schedulable`、`mfg_default_issue_method`）**不列為待決**：
> 不論本案是否推進都該修，屬 P1 的實作項（2026-08-17 已修正註解）。
> ⚠️ `mfg_default_issue_method` 另有**性質不同**的問題——它現在是畫面上可設定但無人讀取的參數，
> 見待決 #8；那不是註解問題，是 P0／P4 把它從「死宣告」推到了「會誤導的設定」。

### 7.1 #2 `inv_default_cost_method` 的評估 —— 建議不納入

**先確認這個參數到底何時生效**：[`InventoryPostingService.ResolveCostMethodAsync`](../../Modules/INV/Services/InventoryPostingService.cs)
是 `ResolveCostMethodAsync(category.CostMethod)`——也就是**只有當該品類的 `CostMethod` 是 `SystemDefault` 時，
才會退到這個系統參數**。品類已明確指定 MA 或 FIFO 的，廠別化這個參數對它們毫無作用。

| 面向 | 判斷 |
|------|------|
| 技術可行性 | ✅ 自洽。`InventoryCostLayer` 是 `IPlantEntity`，MA 均價鍵含 `PlantId`，各廠成本層本就分離 |
| 覆蓋率 | ⚠️ 低。只影響「品類設為 `SystemDefault`」的那部分品號；真要讓兩廠不同，**品類維度已經能做得更準** |
| 風險 | ❌ 全案最高。同一廠內改法**不可追溯**——既有成本層依舊法計價、新異動依新法，庫存價值在切換點斷裂，且不會有任何錯誤訊息 |
| 額外工作 | 寫入端「該廠已有 `InventoryCostLayer` 或庫存異動即鎖定」的守門，以及鎖定後如何解鎖的作業規範 |

**建議**：不納入本輪。這是「低覆蓋率 × 最高風險 × 額外守門成本」的組合，而且需求端目前是假設性的
（沒有實際的「A 廠 FIFO、B 廠 MA」需求被提出）。若日後真出現，做法已經寫在上面，單獨做即可，
不會因為這次沒做而變貴。

### 7.2 #4 `mfg_wo_approval_threshold` 落點的評估 —— 建議先走方案 a

| 方案 | 內容 | 評估 |
|------|------|------|
| **a（建議）** | 廠別化這個參數，走本案新建的 `PlantParam` | 零 Core 變更；**立刻消除**既有不一致——`WorkOrderReleaseService.cs:432` 取廠別簽核設定、`:436` 退回全域閥值，兩邊都變成廠別之後就一致了。成本幾乎是零（本來就要做 P0） |
| b | 加 `Threshold` 欄位到 `DocumentApprovalConfig`（[`Models/ApprovalModels.cs:145`](../../Models/ApprovalModels.cs)，已是 `IPlantEntity`） | 語意更整潔：「這個單別在這個廠要不要簽核／用哪個範本／超過多少要簽」放在同一列。但它是 **Core 模型**，要 Core 變更＋migration＋所有部署共用；且金額閥值是 WorkOrder 專屬規則，塞進通用單別設定表等於加一個對其他單別無意義的 nullable 欄位，Core 未必願意收 |

**關鍵區分**：本案要修的不一致是「**一半廠別、一半全域**」——方案 a 就修掉了。
方案 b 修的是另一個層次的問題（「同一個判斷的設定分散在兩個地方」），那是整潔度而非正確性。

**建議**：現在走 a；把 b 記為日後若有其他理由要動 `DocumentApprovalConfig` 時「順便一起做」的候選，
不為它單獨開一次 Core 變更。

---

## 8. 變更紀錄

| 日期 | 內容 |
|------|------|
| 2026-10-12 | **新增超量五個 key**（mfg-domain.md §4.6.1）：`mfg_over_report_tolerance_pct`（Number）、`mfg_over_report_action`（Choice Block／Supervisor）、`mfg_outsource_over_receipt_tolerance_pct`（Number）、`mfg_outsource_over_receipt_action`（Choice Supervisor／Block）、`mfg_outsource_over_receipt_fee`（Choice Pay／NoPay）。目錄 15 → 20（MFG 14 → 19）。讀取點只有 `IOverQuantityPolicy`。 |
| 2026-10-14 | **新增自動重排兩個 key**：`mfg_auto_schedule_rule`（Choice：Priority／EDD／SPT／FIFO／LIFO，未設定＝Priority）、`mfg_auto_schedule_freeze_hours`（Number，正數小時、上限 168，未設定或 0＝不使用）：目錄由 15 個變 17 個（INV 1＋MFG 16）。起因是自動重排原本寫死 Priority——手動慣用 EDD 的廠一開自動重排就被改回 Priority；凍結時界則讓各廠依備料提前量決定多近的派工不再被自動搬動。讀取點同為 `AutoScheduleSettings.LoadAsync`；規則亦作排程頁規則下拉的預設。手動排程不套用凍結時界。 |
| 2026-10-20 | **新增工單強制送審三個 key**（mfg-domain.md §18.2 其餘三條觸發）：`mfg_wo_approval_outsource`、`mfg_wo_approval_rework`、`mfg_wo_approval_on_delay`（皆 Bool，未設定＝false＝舊行為只看門檻）：目錄由 25 個變 28 個（INV 1＋MFG 27）。語意：**有工單簽核流程時**，委外工單／重工工單／預計完工晚於生產批需求日（訂單交期）者一律送審、不走門檻快速通道；沒有流程時照舊直接核准但投產回應附警示。簡化模式（§25）亦適用，且簡化模式改為遵守明確設定的門檻。讀取點 `WorkOrderReleaseService.ApplyApprovalAsync`。 |
| 2026-10-06 | **新增自動重排排程三個 key**：`mfg_auto_schedule_on_change`（Bool）、`mfg_auto_schedule_before_dispatch`（Bool）、`mfg_auto_schedule_daily_time`（Text，HH:mm）直接列入 `PlantParamCatalog`：目錄由 12 個變 15 個（INV 1＋MFG 14）。全域預設在系統參數頁（`SystemParamEnhancementFilter`），各廠可覆寫——各廠的交班與備料節奏不同，是否自動重排、幾點重排須能分廠決定。讀取點：`AutoScheduleSettings.LoadAsync`（帶明確 plantId）。時刻格式不符一律視為未設定。 |
| 2026-09-29 | **新增 `mfg_machine_status_gate`**（開工機台狀態閘口，None／Warning／Block，預設 Warning）直接列入 `PlantParamCatalog`：目錄由 11 個變 12 個（INV 1＋MFG 11）。起因是手機版安全盤點發現上工完全不看機台狀態。§2 的母體與淨範圍數字是當時的裁決紀錄，不回改 |
| 2026-08-19 | **P4 交付**（§5.3）：**待決 #7 收口＝方案 (b) 唯讀報表**（各廠覆寫一覽，參數 × 廠別矩陣，可匯出）；面板改頁籤並由 520px 加寬至 720px（**兩個檔留在 ProjectZ 就地漂移，Core 不動**——擴充點在 Core 是空的，同樣程式放 Core 只會是死碼）。順帶修掉 `PlantFormEnhancementFilter` 的**靜默清空覆寫**缺陷——缺少參數區塊的 POST 原本會被當成「全部取消覆寫」，已加 `pp_present` 存在標記與兩層回歸測試 |
| 2026-08-19 | **取用端稽核**：逐一核對 `PlantParamCatalog` 的 12 個 key——11 個在正式碼共 26 個讀取點全走 `IPlantParamService.GetAsync(key, plantId)`，無人用無 `plantId` 的多載、也無字面字串繞道；唯一的直接全域讀取在 `SystemParamEnhancementFilter`（全域「系統參數」畫面本身，依設計正確）。第 12 個 `mfg_default_issue_method` 沒有讀取端，**新增並當日收口待決 #8＝方案 (b) 自目錄移除** |
| 2026-08-18 | **P0／P2 交付**（§5.2）：`PlantParam` 資料層、12 個讀取點改寫、廠別主檔面板的三態設定區塊。**待決 #6 收口＝原命題不成立**（Plant 與 SystemParam 兩支 Controller 都是 Admin-only，不走選單權限，§3.6 已更正）；#7 建議 (b) 唯讀報表，面板分組標頭先給「已覆寫 N 項」做部分緩解。順帶修掉 `SystemParamEnhancementFilter` 的 checkbox 讀取缺陷（見 §5.2 第 2 點）|
| 2026-08-17 | **待決 #5 裁決＝方案丙**（掛進廠別主檔，與廠別管理一併設定）；#2＝不納入、#4＝方案 a，兩者依評估建議收口。因方案丙需要 Core 補 `_PlantFormModuleExtensions.cshtml` 擴充點，**§0／§1.1／§3.7 的「Core 零修改」敘述一併校正為「Core 只動一個擴充點」**。新增待決 #6（參數編輯權是否綁 `PlantManagement`）與 #7（各廠覆寫一覽怎麼補）|
| 2026-08-17 | **待決 #1 裁決＝依入庫端（`ToPlantId`）**，含「不可用 `IUserContext.PlantId`」的實作要點（§4.1）；#2／#4 補上評估建議（§7.1／§7.2，建議分別為「不納入」與「先走廠別化參數」），兩項仍待裁決 |
| 2026-08-17 | **待決 #3 裁決＝方案甲**（零資料遷移）。#5 增列方案丙（掛進廠別主檔畫面）並查證其前提：`Views/Plant/_PlantForm.cshtml` 無擴充點且為 Core 檔，需 Core 先加掛載點 |
| 2026-08-17 | 建立。全 29 個參數逐項裁決；確認系統無公司別維度，scope 定為廠別 |
| 2026-08-17 | **推翻「必須改 Core」的前提**：查證 Core 的 `SystemParamController`／`Index.cshtml` 只處理 5 個公司識別 key，21 個 ProjectZ key 全在自有 Filter 手上 → §3 改為 ProjectZ 自有表、Core 零修改；原〈待決 #3：Core 上送時機〉撤銷，改為儲存方案選擇，另增〈#5 設定畫面形狀〉。同時更正母體計數：**29 個**（原寫 26，漏數 `ContributionMargin.*` 3 個）、該廠別化 **13 個**（原寫 11）、不做 **16 個**（原寫 11）；分類本身未變動 |
| 2026-08-17 | **P1 替代路徑落地**（§5.1）：`IPlantDefaultLocationResolver` + 兩個呼叫端 + 三處註解更正 + 10 個測試案例。`manual-test-plan.md` §11 **#3 已收口**；#1／#2 仍開放（需 P0） |
