# 系統開發規範文件 (System Development Guidelines)

## 1. 文件說明
本文件旨在規範 **SmartFactory.MES** 系統之開發流程、環境設定及程式撰寫標準。
- **主要語言**：所有專案規劃、註解及說明文件應以 **繁體中文 (Traditional Chinese)** 為主。

## 2. 開發環境與工具 (Environment & Tools)

### 系統需求
- **作業系統**：Linux (Ubuntu/Debian) 或跨平台支援 (Windows/macOS)
- **開發框架**：.NET 10.0 SDK
- **資料庫**：PostgreSQL

### 建議開發工具 (IDE)
- **Visual Studio Code** (推薦)
  - C# Dev Kit
  - .NET Install Tool
- **Visual Studio 2022**

### 專案結構
- `SmartFactory.MES`: 主要 Web 應用程式 (ASP.NET Core MVC)
- `SmartFactory.MES.Tests`: 單元測試專案 (xUnit)
- `SmartFactory.MES.IntegrationTests`: 整合測試專案 (xUnit + WebApplicationFactory)
- `SmartFactory.MES.UITests`: UI 自動化測試專案 (xUnit + Playwright)


## 3. 執行與測試指令 (Execution & Testing)

在專案根目錄下執行以下指令：

### 建置 (Build)
```bash
dotnet build
```

### 執行 (Run)

> **前置條件：Phase 2 安全模式（所有開發環境均需完成）**
> 應用程式使用 Security Anchor DLL 進行授權驗證與連線字串解密，**初次設定需執行以下步驟**。

### 首次環境設定

以下步驟在**本專案根目錄**執行，每台開發機器只需一次。

#### 步驟 1：取得授權

本專案**不持有簽發私鑰，無法自行核發授權**。所有部署（本倉庫開發、Gemba MES 使用者、顧問導入）
走同一套流程：申請碼 → 登記取得 `license.json` → 下載同版本的 Security DLL → 加密連線字串。
使用者端的完整步驟見 `public-overlay/INSTALL.md` §3–§5（發布後即 Gemba MES 的 `INSTALL.md`）。

先在本機取得申請碼，交給核發方（或開啟印出的登記網址）：

```bash
dotnet run --project SmartFactory.MES.MachineId -- --init   # 首次：產生機器錨定碼
dotnet run --project SmartFactory.MES.MachineId             # 顯示環境指紋、主機識別碼、申請碼與登記網址
```

> 核心開發機由持有私鑰的人在本機以 `issue --internal --this-host` 簽發（Core DEVELOPMENT.md 步驟 2），授權綁這台的主機板；
> 整顆 WSL 搬到別台時只會開授權頁，要在新機重簽並以新 LicenseId 重新加密下面的 `DefaultEncrypted`。
> **不可手動修改授權檔**（例如自己補 `hostIds`）——全部欄位在簽章內，改了就是「簽章無效」。

收到的 `license.json` 放專案根目錄；`SmartFactory.MES.Security.dll` 放在 `Security/`
（`csproj` 已設 `CopyToOutputDirectory`，之後每次 build 會自動複製）。使用者從 GitHub Releases 下載與系統同版本的 DLL；
本倉庫開發機用 Core 端 `SmartFactory.MES.Security` 編出的 DLL（Core DEVELOPMENT.md 步驟 1）。

> ⚠️ **錨定檔（`instance-id`）等同金鑰材料，且程式不會自動重建它。**
> 弄丟就是換一個身分——授權失效，已加密的連線字串也再也解不開。請一併備份。

#### 步驟 2：加密連線字串

**這一步在你自己的機器上執行。SmartFactory 不需要、也不應該知道你的資料庫密碼。**
一般授權不允許明文 `DefaultConnection`（`allowPlainConnection` 只在內部／測試授權開放），所以每個部署都要做這一步。

```bash
FINGERPRINT=$(bash tools/get-fingerprint.sh | grep -oP '[0-9A-F]{64}')
dotnet run --project tools/EncryptConnectionString -- \
  --fingerprint "$FINGERPRINT" \
  --license-id <license.json 裡的 licenseId> \
  --plaintext "Host=localhost;Database=ProjectZ;Username=dev;Password=..." \
  --this-host \
  --verify
```

> `--this-host` 讀本機主機板 UUID 產生 ENC:v3——授權有列主機識別碼（`hostIds`）時系統只接受 v3，
> 主機不符或讀不到時不連資料庫、只顯示授權頁（Core SECURITY-OPERATIONS.md §18.15.9）。授權沒有列主機（CI）改用 `--no-host-binding`。

> 每次加密結果不同（Salt 與 Nonce 均為隨機），這是正常現象。

#### 步驟 3：設定連線字串

在 `appsettings.Development.json`（不進 Git）中設定：

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "",
    "DefaultEncrypted": "ENC:v3:{salt_b64}:{nonce_b64}:{cipher_b64}:{tag_b64}"
  }
}
```

> `DefaultEncrypted` 非空時，應用程式自動進入 Phase 2 模式（Security DLL 解密）。
> 加密值與機器指紋、LicenseId 及主機板識別碼綁定，**不可跨機器複製**；換了授權編號也要重新加密。

#### 驗證

換授權或換 DLL 後要重新建置，且**指定 csproj**：`dotnet build SmartFactory.MES.csproj`。
在根目錄直接 `dotnet build` 會建 sln，本機曾出現「Build succeeded」但輸出目錄的 `license.json` 仍是舊檔
（Security DLL 讀的是輸出目錄那份），結果照樣報「簽章無效」。

確認方式：`/health` 回 Healthy，管理員「授權狀態」頁的授權編號正確、主機比對符合。
（「授權驗證通過」那行日誌只在無 DLL 的明文模式出現；DLL 模式只有內部授權會印橫幅，客戶授權不印。）設了 `DefaultEncrypted` 卻看到
「找不到 Security DLL」，代表 DLL 沒放進 `Security/` 或沒被複製到輸出目錄。
授權相關的錯誤訊息與處置見 [INSTALL.md](INSTALL.md) §9。

---

啟動應用程式（建議使用 `watch` 模式以支援熱重載，讀取程式碼變更並自動重啟）：
```bash
dotnet watch run --urls "http://localhost:5110;https://localhost:7054"
```
或(修改 DI 設定、Middleware、Startup 相關程式)
```bash
dotnet watch run --no-hot-reload --urls "http://localhost:5110;https://localhost:7054"
```

若需手動執行：
```bash
dotnet run --urls "http://localhost:5110;https://localhost:7054"
```

### ⏰ 開發環境刻意跑 UTC（`TZ=UTC`），不要改回去

`Properties/launchSettings.json` 的三個 profile 都設了 `"TZ": "UTC"`。**這是刻意的，不是設定錯誤。**

原因：正式部署的容器幾乎都跑 UTC，而廠別時區是 `Asia/Taipei`（`Program.cs` 的廠別種子
`DefaultTimeZone`）。如果開發機讓**伺服器時區與廠別時區剛好相同**，所有時區缺陷都會靜默
通過——`manual-test-plan.md` §15 的 Wave 1 就是這樣跑完的，8 個步驟全綠但時區部分一項都沒驗到。
讓兩者不同，是驗收環境前置 **0-9** 的內容。

**它只影響開發時的 `dotnet run`**（`launchSettings.json` 不進正式部署），所以改不到產品行為。

切過去之後**這幾件事看起來會變，但都不是新缺陷**：

| 現象 | 出處 | 說明 |
|---|---|---|
| ~~列印頁的時間戳早 8 小時~~ **已修** | `Modules/_Shared/ModuleControllerBase.cs` `ViewBag.PrintTime` | 2026-09-14 改為 `DateTimeSvc.Now`（`IDateTimeService`，廠別時區優先），列印頁尾 fallback 與 MFG 批次年度預設值一併修掉。原本它**與全站不一致**（其他 35 個 View 走 `ToLocalTime()`），只是以前兩個時區相同所以看不出來。已加 **R22(b)** 守門，新的顯示用時間戳再寫 `DateTime.Now` 會擋建置 |
| 報表／看板的查詢預設區間差一天 | R22 baseline【二】共 13 支 | 已列為「錯一天只是預設值不理想，不影響帳」 |
| ~~每日背景工作在當地 10:00–14:00 跑~~ **已修** | `Services/Jobs/DailyJobServiceBase.cs` | 2026-09-14 改讀 `appsettings.json` 的 `Jobs:TimeZone`（本專案 `Asia/Taipei`），**不再沿用伺服器時區**，所以 `TZ=UTC` 不影響排程時刻。未設定該鍵時退回伺服器時區並留警告。連帶讓「今天跑過沒」的日界也改用營運時區（TZ-5 已結案）|
| ~~系統管理類畫面的**匯出檔名**日期早 8 小時~~ **已修** | `Controllers/` 10 支（使用者／角色／權限／組織／廠別／稽核日誌…） | 走 Core-first：upstream PR #29 已合併，2026-09-14 同步回來。（原記述誤寫成「表頭」，實際是**檔名**：`User_20260914.csv`——匯出內容裡沒有時間戳可對照，檔案存下來就再也查不出真正的匯出時刻）|

**不會**受影響的是單據業務日：它走 `IDateTimeService.GetLocalDate()`（廠別時區優先），
`*FormVm.cs` 裡的 `DateTime.Today` 只是「表單沒帶該欄位」時的 fallback，Create 動作會覆寫
（例如 `SalesOrderController:230`）。全庫 502 處 `DateTime.UtcNow` 也完全不受影響。

要暫時跑回當地時區（例如重現某個只在 +08 出現的問題）：

```bash
TZ=Asia/Taipei dotnet run --urls "http://localhost:5110;https://localhost:7054"
```

### 本機開 SQL 日誌時的必要配套

**預設狀態不需要做任何事**：`appsettings.json` 的 `Microsoft.EntityFrameworkCore: Warning`
已經擋掉 SQL 記錄，`logs/` 成長量很小。以下只適用於「想在本機看 EF SQL」的情況。

在 `appsettings.Development.json`（不進 Git）調高日誌級別時，**必須連 File sink 一起覆寫**，
否則 `logs/` 會以每天約 100 MB 的速度成長（實測單日 119 MB、136,016 筆 SQL；30 天累積 530 MB）：

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug",
      "Override": {
        "Microsoft": "Information",
        "Microsoft.EntityFrameworkCore.Database.Command": "Information"
      }
    },
    "WriteTo": [
      { "Name": "Console" },
      {
        "Name": "File",
        "Args": { "restrictedToMinimumLevel": "Warning" }
      }
    ]
  }
}
```

**為什麼 `WriteTo` 必須把 File 也列出來**：Serilog 的 JSON 設定是**按陣列索引合併**，不是整段取代。
直覺上「只列 Console 就代表只寫 Console」，但實際上被覆蓋的只有 `appsettings.json` 的 Console，
base 的 `WriteTo[1] = File` 原封不動保留並照常生效——於是上面調高的級別與 EF SQL 全數落檔。
必須明確覆寫索引 1 才關得掉。

加上 `restrictedToMinimumLevel: "Warning"` 之後，Console 保留完整 Debug 與 SQL 供即時觀察，
檔案只留 Warning 以上。

> 不能改用 `Information` 來擋 SQL：EF 的 `Database.Command` 本身就是 Information 級，擋不掉。

日誌保留政策的完整說明（含資料庫層排程與 OS 層設定）見 [`deploy/log-retention.md`](deploy/log-retention.md)。

### 備份

資料庫備份見 [`scripts/backup-db.sh`](scripts/backup-db.sh) 檔頭（含加密、異地與還原 runbook）。

**檔案系統目前不在備份範圍**——開發區無此需求，但正式區在報表落地功能上線前必須先決定做法，
建議方案與還原程序見 [`deploy/file-backup.md`](deploy/file-backup.md)。

### 資料權限：歷史資料 `OrganizationUnitPath`

資料權限過濾器有一條 legacy 放行——`OrganizationUnitPath` 為空的列視為公開。
在單據畫面上影響有限，**在彙總報表上會被無聲加總進金額且數字自洽**。

開發區不處理（種子資料）。正式區在報表工具（`Features:ReportTools`）開燈前，
依 [`deploy/orgpath-backfill.md`](deploy/orgpath-backfill.md) 執行：

```bash
psql -f scripts/orgpath-inventory.sql              # 唯讀盤點，先跑這支
psql -f scripts/orgpath-backfill.sql               # 試跑（自動 ROLLBACK）
psql -v apply=1 -f scripts/orgpath-backfill.sql    # 套用（前面要先備份）
```

### 測試 (Test)

本專案包含三種測試類型：**單元測試 (Unit Tests)**、**整合測試 (Integration Tests)** 及 **UI 自動化測試 (UI Tests)**。

#### 3.1. 執行所有測試
```bash
dotnet test
```

#### 3.2. 單元測試 (Unit Tests)
測試個別控制器與服務的業務邏輯，使用記憶體資料庫 (In-Memory Database)。

**執行單元測試**：
```bash
dotnet test SmartFactory.MES.Tests/SmartFactory.MES.Tests.csproj
```

**涵蓋範圍**：
- `AccountControllerTests`: 角色切換功能
- `OrganizationControllerTests`: 組織架構 CRUD 與階層邏輯
- `PermissionControllerTests`: 權限管理
- `PlantControllerTests`: 廠區管理
- `RoleControllerTests`: 角色管理與級聯刪除
- `UserControllerTests`: 使用者管理
- `MenuServiceTests`: 選單結構與扁平化邏輯
- `PermissionServiceTests`: 權限服務邏輯
- `ApprovalServiceTests`: 審批服務邏輯

#### 3.3. 整合測試 (Integration Tests)
測試完整的 HTTP 請求/回應管線，包含中介軟體 (Middleware)、路由 (Routing) 與授權 (Authorization)。

**執行整合測試**：
```bash
dotnet test SmartFactory.MES.IntegrationTests/SmartFactory.MES.IntegrationTests.csproj
```

**涵蓋範圍**：
- `BasicTests`: 驗證基本端點 (`/`, `/Identity/Account/Login`) 可正常存取
- `AuthTests`: 驗證受保護路由 (`/Organization`, `/User`, `/Role`) 會重新導向至登入頁面

#### 3.4. UI 自動化測試 (UI Tests)
使用 Playwright 在真實瀏覽器環境中測試使用者操作流程。

**環境需求**：
```bash
# 安裝系統相依套件 (需要 sudo 權限)
cd ~/SmartFactory.MES/
sudo "$HOME/.dotnet/tools/playwright" install-deps

# 安裝瀏覽器
~/.dotnet/tools/playwright install
```

**執行 UI 測試**：
```bash
# 方法 1: 使用自動化腳本 (會自動啟動應用程式)
./run_ui_tests.sh

# 方法 2: 手動執行
dotnet test SmartFactory.MES.UITests/SmartFactory.MES.UITests.csproj
```

**涵蓋範圍**：
- `LoginTests`: 驗證登入流程 (有效/無效憑證)
- `CrudSmokeTestBase` 衍生的各畫面 CRUD smoke test（每個畫面 9 項標準斷言）
- `E2EFixtureGuardTests`: 守住下方 E2E 種子的完整性

**E2E 種子（流程測試的上游資料）**：

主檔 CRUD smoke test 自給自足，但流程測試需要工單、派工、庫存餘量等上游資料。
**全新資料庫沒有任何主檔** —— `Program.cs` 只 seed 廠別、admin、角色、選單權限、
單號設定與一個待驗倉。整條鏈由 `scripts/seed-e2e-fixture.sql` 建立：

```bash
psql "postgresql://dev:Dev12345@localhost/ProjectZ_E2E" \
  -v ON_ERROR_STOP=1 -f scripts/seed-e2e-fixture.sql
```

- **冪等**：可重複執行；數量與單據狀態會被覆寫回基準值，所以流程測試消耗掉庫存、
  把工單推到完工之後，重跑一次腳本就回到同一個起點。
- **隔離**：所有代碼 `E2E-` 前綴，單號自成號段（`E2E-WO0001` / `E2E-DP0001`），
  不會與 `DocNumberService` 產生的流水號相撞。
- schema 變動導致種子少建資料時，第一個紅的會是 `E2EFixtureGuardTests`，
  訊息直接指向種子腳本，不會以「下拉沒有選項」這種難追的形式出現。

CI 由 `.github/workflows/nightly.yml` 的 `Seed E2E fixture` 步驟在 migration 之後執行。

**完整的本機執行流程**：

```bash
# 1. 準備乾淨資料庫（app 啟動會自動套用 migration 與基礎 seed）
psql -h localhost -U dev -d postgres -c 'CREATE DATABASE "ProjectZ_E2E";'

# 2. 啟動 app
#    ⚠️ DefaultEncrypted 優先於 DefaultConnection，必須明確清空才能改指向測試庫
#    ⚠️ 2FA 強制在本機是靠 appsettings.Development.json 關的，該檔不進版控
ConnectionStrings__DefaultEncrypted="" \
ConnectionStrings__DefaultConnection="Host=localhost;Database=ProjectZ_E2E;Username=dev;Password=Dev12345" \
Security__TwoFactorEnforcementEnabled=false \
dotnet run --project SmartFactory.MES.csproj --urls http://localhost:5110

# 3. 灌 E2E 種子
psql "postgresql://dev:Dev12345@localhost/ProjectZ_E2E" -v ON_ERROR_STOP=1 -f scripts/seed-e2e-fixture.sql

# 4. 跑測試（約 3 分鐘）
#    TestDbConnection 供流程測試在開跑前重置單據狀態用（見下）
TestDbConnection="Host=localhost;Database=ProjectZ_E2E;Username=dev;Password=Dev12345" \
dotnet test SmartFactory.MES.UITests/SmartFactory.MES.UITests.csproj
```

**單據動作測試（`DocActionSmokeTestBase`）**：

補既有兩層測試的共同盲區：CRUD smoke test 與各 FormInteractionTests 都只驗**新增面板**，
從不驗「把一張已經存在的單據重新打開」。實際踩過的多個缺陷都落在這個縫裡。

派生類只要提供「怎麼建一張單」（`FillRequiredFieldsAsync`），就得到 4 個標準斷言：
既有單據面板可開啟、無 JS 錯誤、**面板內每個 `data-action` 的目標函式都存在**、
仍有可用的動作按鈕。

第三項是核心：CSP 強化後所有動作都走 `data-action` 委派，dispatcher 用 `window[name]`
找函式，找不到只會 `console.warn` 然後**靜默不動作**（見 `wwwroot/js/data-action.js`）。
使用者的體感是「按了沒反應」——沒有錯誤、沒有紀錄，是最難被發現的一種壞法。

⚠️ 開啟既有單據時不能只找「編輯」按鈕：多數單據列表的編輯鈕只在草稿狀態才渲染，
而沒有簽核流程設定時單據一存檔就直接是非草稿，此時列表上只剩「檢視」。基底因此
依序嘗試 `Edit` 與 `Details`。

**流程測試（`Flows/`）**：

跨畫面、跨單據的主幹流程測試，繼承 `FlowTestBase`。與 CRUD smoke test 的差別是它會
**推進單據狀態並消耗庫存** —— 跑完之後派工是 Closed、工單是 Completed，資料庫不再處於
起始狀態。因此每個流程測試開跑前都會重新執行 `scripts/seed-e2e-fixture.sql` 把整組
E2E 資料歸零（刻意重用同一份 SQL，不另寫一套重置邏輯，避免兩份漂移）。

這需要資料庫連線，取自 `TestDbConnection`，未設定時退回 `ConnectionStrings__DefaultConnection`。
兩者都沒有時測試會以明確訊息失敗，不會靜默略過。

`FlowTestBase` 會收集流程中所有對話框訊息並附在失敗訊息裡 —— 畫面的動作按鈕失敗時是用
`alert(result.message)` 吐出後端訊息的，而流程測試必須自動接受對話框才能往下走；不收的話
真因完全看不到（實際踩過：後端回「可用量不足」，測試卻在好幾步之後以「狀態不對」失敗）。

**注意事項**：
- UI 測試需要安裝瀏覽器相依套件 (如 `libnspr4`, `libnss3` 等)
- 若環境缺少相依套件，測試會失敗並顯示 `libnspr4.so: cannot open shared object file`
- 建議在 CI/CD 環境或具備完整相依套件的開發環境中執行
- 測試預設連 `:5110`；port 不符是歷來最常見的假性失敗來源
- **nightly 的 Playwright 步驟是硬閘門**，不得再加回 `continue-on-error`——
  它曾讓 211 個失敗的執行照樣報成功，整套測試因此空轉數月


#### 3.5. 手機實機測試 (Mobile Device Testing)

手機 PWA（`wwwroot/mobile/`）必須在實機驗證。與桌機測試最大的差異是瀏覽器的
**secure context 限制**：

| 功能 | 需要的條件 |
|------|-----------|
| 頁面載入、API 呼叫 | 無 |
| 登入（Identity cookie 帶 `Secure` 旗標） | secure context |
| 相機掃碼（`getUserMedia`） | secure context |
| Service Worker（離線快取、離線佇列） | secure context **且憑證鏈完全受信任** |
| 推播（`PushManager`）、PWA 安裝到主畫面 | 同上 |

> `http://<IP>` **不構成** secure context，登入會直接失敗（cookie 遭瀏覽器丟棄，
> 症狀是輸入帳密後跳回登入頁且無錯誤訊息）。只有 `http://localhost`、
> `http://127.0.0.1` 與 `https://` 算數。

**⚠️ 手動點掉憑證警告的限制**：以未受信任的憑證連線時選擇「繼續前往」，
`isSecureContext` 仍為 `true`，**登入、相機、API 都能正常運作**；但 Chrome 對
Service Worker 有 secure context 之外的額外限制，**憑證鏈有錯就一律拒絕註冊**。
因此沒有受信任憑證時，離線佇列、PWA 安裝、推播這三項無法測試。

**應用程式跑在 WSL 或 Windows／IIS 都可以**

> 📌 **2026-09-25 更正**：這裡原本寫「必須留在 WSL」，理由是在 Windows 上指紋會改取
> 「第一張運作中網卡的 MAC」，插拔網路線或開行動熱點就會讓應用程式起不來。
> **該理由已不存在**——v1 的三分支環境偵測（Docker／WSL2／實體機 MAC）已於 2026-09-14
> 隨錨定碼 v2 移除，指紋現在是 `SHA256(UTF8("v2:" + anchorId))`，只認 `MachineAnchor`
> 的錨定檔，與網卡、主機名稱、作業系統全部無關
> （見 `Services/EnvironmentFingerprintService.cs` 的類別註解）。
> 若還在別處看到「必須留在 WSL」的說法，那是同一批過期敘述。

留在 WSL 就照下面的方法 A／B 走。**把應用程式放到 Windows 的 IIS 反而讓手機測試簡單很多**——
不需要 mirrored 模式、不需要 `netsh portproxy`、不需要 Cloudflare Tunnel，手機直接連開發機 IP
即可（憑證那一關不變）。作法與**必須先改的集區設定**見 §3.6。

**輔助腳本**

`tools/` 下提供四支腳本，將下述流程自動化：

| 腳本 | 用途 |
|------|------|
| `tools/start-lan.sh` | **方法 A**。偵測目前 IP、必要時重簽憑證、以 http + https 啟動應用程式並自我驗證。`stop` 停止 |
| `tools/open-test.sh` | **方法 B**。以純 http 啟動應用程式並建立 Cloudflare Tunnel，印出手機網址。`url` 重印網址、`stop` 收回通道、`stop-all` 連應用程式一併停止 |
| `tools/make-dev-cert.sh` | 建立本機 CA 並簽發含指定 IP 的伺服器憑證。由 `start-lan.sh` 自動呼叫，亦可單獨使用 |
| `tools/dev-android.sh` | USB 路徑。以 Chrome 的埠轉發（`chrome://inspect`）讓手機以 `http://localhost:5110` 連線——`localhost` 天生是 secure context，**不需要任何憑證、不需要防火牆規則**，但僅限 Android 且不涵蓋 PWA 安裝後的獨立視窗 |

`start-lan.sh` 會把歷來用過的 IP **累積**簽入憑證 SAN，且**沿用同一個 CA**，
因此更換網段時手機不需重新安裝 CA；憑證效期剩餘不足 30 天時會自動換發。

> 腳本無法代勞的有兩項，首次設定仍須依方法 A 手動完成：
> **建立防火牆規則**（需系統管理員權限）與**手機安裝 CA**（需實機操作）。

> 憑證、日誌等產物一律輸出至 `~/.cache/`，不會寫入專案目錄。
> **本機產生的私鑰與 CA 絕不可提交版控。**

---

##### 方法 A：區域網路直連

適合驗證操作響應速度（實測延遲約 10ms）。

**A-1. WSL 網路模式**

`%USERPROFILE%\.wslconfig`（修改此檔不需系統管理員權限，但需 `wsl --shutdown` 生效）：

```ini
[wsl2]
networkingMode=mirrored
```

mirrored 模式下 WSL 直接共用 Windows 的網路介面與 IP，**不需要 `netsh portproxy`，
也不必處理 WSL IP 每次重開機變動的問題**。

> 不要加 `firewall=false`。該設定會停用整個 WSL 的 Hyper-V 防火牆，
> 使所有綁定 `0.0.0.0` 的服務暴露於區域網路。入站請改用下方的具名規則控管。

**A-2. 開放入站連接埠（需系統管理員）**

mirrored 模式的入站流量由 Hyper-V 防火牆管制，預設為 Block：

```powershell
# 以系統管理員身分執行 PowerShell
New-NetFirewallHyperVRule -Name 'MES-Mobile' -DisplayName 'MES mobile test' -Direction Inbound -VMCreatorId '{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}' -Protocol TCP -LocalPorts 5110,7054 -Action Allow
```

`{40E0AC32-...}` 是 WSL 固定的 VMCreatorId（可用 `Get-NetFirewallHyperVVMCreator` 確認）。
此規則綁定連接埠而非 IP，**更換網路環境後不需重建**。測試結束後以
`Remove-NetFirewallHyperVRule -Name 'MES-Mobile'` 收回。

> 驗證時注意：mirrored 模式下**主機以自己的 LAN IP 回連 WSL 服務本來就不通**
> （需額外的 `hostAddressLoopback` 設定），這**不代表外部裝置連不進來**，
> 兩者封包路徑不同，請勿據此誤判。

**A-3. 產生手機能信任的憑證**

`dotnet dev-certs` 產生的憑證**無法用於手機**：`CN=localhost`、SAN 只有 `DNS:localhost`
（以 IP 連線必定名稱不符），且為 `CA:FALSE`、無 `keyCertSign`，
Android 的 CA 安裝流程會直接拒收。必須自建 CA（以下用 openssl，亦可改用 mkcert）：

```bash
# 1) 建立本機 CA — 只需做一次，手機安裝的就是這一張
openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
    -keyout rootCA.key -out rootCA.pem -subj "/CN=MES Dev CA" \
    -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
    -addext "keyUsage=critical,keyCertSign,cRLSign"

# 2) 簽發伺服器憑證（<IP> 換成開發機的實際 IP）
openssl req -newkey rsa:2048 -nodes -keyout server.key -out server.csr -subj "/CN=<IP>"
openssl x509 -req -in server.csr -CA rootCA.pem -CAkey rootCA.key -CAcreateserial \
    -out server.crt -days 397 -sha256 \
    -extfile <(printf "subjectAltName=IP:<IP>,DNS:localhost,IP:127.0.0.1\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n")

# 3) 打包成 Kestrel 使用的 pfx
openssl pkcs12 -export -out mes-dev.pfx -inkey server.key -in server.crt \
    -certfile rootCA.pem -passout pass:changeit
```

**伺服器憑證的 `-days` 必須 ≤ 398**：Chrome 拒絕效期超過 398 天的伺服器憑證
（`ERR_CERT_VALIDITY_TOO_LONG`），自簽時習慣填的 `3650` 會直接被擋。CA 本身不受此限。

啟動應用程式：

```bash
ASPNETCORE_Kestrel__Certificates__Default__Path=/path/to/mes-dev.pfx \
ASPNETCORE_Kestrel__Certificates__Default__Password=changeit \
dotnet run --urls "https://0.0.0.0:7054"
```

**A-4. 手機安裝 CA**

將 `rootCA.pem` 傳送到手機（可改副檔名為 `.crt` 放在 `wwwroot/` 由開發伺服器提供下載，
其回應的 `application/x-x509-ca-cert` 會讓 Android 直接跳出憑證安裝對話框）：

- **Android**：設定 › 安全性 › 加密與憑證 › 安裝憑證 › **CA 憑證**
- **iOS**：安裝描述檔後，另須至 設定 › 一般 › 關於本機 › **憑證信任設定** 開啟完全信任

安裝完成後**必須完全關閉瀏覽器再重新開啟**。否則先前手動接受的憑證例外記錄會讓瀏覽器
繼續走例外路徑而不重新驗證憑證鏈，結果是 CA 裝好了 Service Worker 仍然註冊不了。

> **⚠️ 此方法可能完全不可用**：受 MDM、Samsung Knox 等裝置政策管理的手機會顯示
> 「無法安裝 CA 憑證，僅限安裝來自受信任組織」，**從開發端無法繞過**，請改用方法 B。

更換網段時：CA 不變，只需以新 IP 重新簽發伺服器憑證，**手機不必重新安裝 CA**。
建議將歷來用過的 IP 一併簽入 SAN，於固定網段間切換時就不必反覆換發。

---

##### 方法 B：Cloudflare Tunnel

適合驗證完整 PWA 功能。憑證由 Cloudflare 的公開 CA 簽發，**手機不需安裝任何憑證**，
Service Worker、離線佇列、PWA 安裝與推播皆可測試。全程為出站連線，
**不需要系統管理員權限，也不需要任何防火牆規則**。

```bash
# 安裝（單一執行檔，免安裝套件）
curl -fsSL https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-amd64 \
     -o ~/.local/bin/cloudflared && chmod +x ~/.local/bin/cloudflared

# 終端機 1：應用程式必須以「純 http」啟動
dotnet run --urls http://localhost:5110

# 終端機 2：建立通道
cloudflared tunnel --url http://localhost:5110
```

輸出的 `https://xxx.trycloudflare.com` 即為手機使用的網址。

**必要條件與限制**：

- **應用程式必須是純 http**。若同時啟用 https，`UseHttpsRedirection` 會將請求 307
  重新導向至內部 IP，通道外部無法解析。TLS 由 Cloudflare 終結並帶
  `X-Forwarded-Proto: https` 回源，`UseForwardedHeaders`（`Program.cs` 已設定）
  會正確改寫 scheme，Secure cookie 運作正常。
- **網址是公開的**，任何取得該網址者皆可存取登入頁，沒有額外保護。
  測試結束務必終止通道。
- **網址即 PWA 的 origin**。重啟通道會取得新網址，等同換到新的 origin，
  Service Worker 與離線快取全部重來。需連續多日測試時請保持通道不中斷。
- 封包繞經雲端，**延遲高於區域網路直連，不可用於評估操作響應速度**。

---

##### 疑難排解

| 症狀 | 原因與處理 |
|------|-----------|
| 手機等待很久才失敗（逾時） | 封包未送達 → 兩台裝置不在同一網段，或防火牆規則未建立 |
| 手機立即失敗（連線被拒） | 封包已送達但無服務監聽 → 應用程式未啟動，或連接埠不符 |
| 輸入帳密後跳回登入頁、無錯誤訊息 | 以 `http://<IP>` 連線，Secure cookie 遭丟棄 → 改用 https |
| 其他功能正常，僅 Service Worker 註冊失敗 | 憑證未受信任（見上方說明），或安裝 CA 後未重啟瀏覽器 |
| 畫面出現 `xxx.yyy` 這類原始 i18n 鍵 | `wwwroot/mobile/sw.js` 的 `CACHE_NAME` 未更新，裝置沿用舊快取 |
| 版面完全跑掉、連結變成藍字底線 | 使用了內聯 `<style>` 或 `style=`，被 CSP `style-src 'self'` 擋下 |

> **判斷 Service Worker 是否真的註冊成功**：觀察應用程式日誌，若 `manifest.json`
> 有被請求，且 `sw.js` 之 `STATIC_ASSETS` 清單內的檔案被整批抓取
> （使用者不可能同時瀏覽那些頁面），即表示 install 預快取已執行、註冊成功。

> **判斷 `UseForwardedHeaders` 是否生效**：不能看日誌的
> `Request starting HTTP/1.1 ...`——該行由 HostingApplication 在 middleware pipeline
> **之前**輸出，顯示的永遠是原始 scheme。應改以「`Failed to determine the https port`
> 警告次數遠少於請求數」反證 scheme 已被正確改寫。


#### 3.6. Windows／IIS 部署測試

把應用程式發布到 Windows 的 IIS（例如測試筆電）有三個好處：手機不必再繞通道、
`localhost` 同時連得到 WSL 裡的 PostgreSQL 與 Ollama、並且驗證到正式環境常見的 IIS 託管路徑。
**指紋不再是阻礙**（見 §3.5 的更正框）。

> ✅ **2026-09-25 已在測試筆電實作完成。**
> **逐步操作手冊：[`deploy/iis-deployment.md`](deploy/iis-deployment.md)**
> （含實測數值、現有站台盤點、憑證與授權的完整指令、疑難排解對照表）
> **建置腳本：[`deploy/iis-setup.ps1`](deploy/iis-setup.ps1)**
> （只做需要管理員權限的部分；冪等，可重複執行）
>
> 本節保留架構層面的判斷依據，操作細節不重複。

##### 前置：Windows 端

| 項目 | 說明 |
|------|------|
| **ASP.NET Core 10.0 Hosting Bundle** | 必裝，而且**必須是 Hosting Bundle，不是 Runtime／SDK** —— 只有它帶 ANCM v2。見下方警告 |
| 應用程式集區 → .NET CLR 版本 | 選「**沒有受控碼**」。ASP.NET Core 不是 CLR 模組，選錯會 500.30 |
| `web.config` | 原始碼裡沒有是正常的，`dotnet publish` 會自動產生，不要手寫 |
| `wwwroot/uploads` | 要給應用程式集區身分寫入權限 |

##### ⚠️ Hosting Bundle 與 Runtime 的混淆（實作時唯一真正卡住的一步）

三個訊號會一起讓人誤判「已經裝好了」：

1. `dotnet --list-runtimes` 看得到 `Microsoft.AspNetCore.App 10.0.x` —— 那是**共用框架，不含 ANCM**
2. 解除安裝清單裡有「Microsoft ASP.NET Core Module V2 **for IIS Express**」 —— 名字幾乎一樣，IIS 用不到
3. 下載頁上 Runtime／SDK／Hosting Bundle 在同一區塊，很容易點錯

判準：解除安裝清單要有 **`Windows Server Hosting`**（注意 DisplayName **不含**「Hosting Bundle」
字樣，拿那個字串去搜尋不管裝沒裝都是空的），且
`C:\Program Files\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll` 存在。
檔名 `dotnet-hosting-10.0.x-win.exe`，**約 112 MB；純 runtime 只有 ~11 MB，用大小就分得出來**（10.0.14 實測）。
裝完必須 `iisreset`。

##### ⚠️ 必改：集區回收會靜默停掉背景服務

`Program.cs` 註冊了 **13 支 `AddHostedService`**（授權看門狗、六支每日排程、稽核等）。
IIS 預設**閒置 20 分鐘無請求就關閉集區**、**每 29 小時固定回收**，集區一關背景服務就停。
排程的作法是「算出下一個固定時刻再 `Delay`」，重啟後重算，補跑只在每次啟動跑一次 ——
結果是**排程看起來有註冊、實際永遠不執行，而且完全無聲**（同一類缺陷在 WSL 上踩過一次，
見「六支每日排程從未執行」）。

```
應用程式集區 → 進階設定
  啟動模式 (startMode)          = AlwaysRunning
  閒置逾時 (idleTimeout)        = 0
網站 → 進階設定
  預先載入啟用 (preloadEnabled) = True
```

這三項不是效能調校，是**守門**：漏設的話任何排程相關的驗收都是假綠。

##### 資料庫：IIS（Windows）→ PostgreSQL（WSL）

可行，且已實測（2026-09-24 桌機，`SECURITY-OPERATIONS.md` §17.9 步驟 4 補驗）：
WSL 的 5432 由 `wslrelay` 接到 Windows 的 `127.0.0.1`，連線字串照常寫
`Host=localhost;Port=5432`。

- **PostgreSQL 只綁 `127.0.0.1` 也沒問題** —— WSL2 的 localhost 轉送會進到 distro 內的
  loopback。不要為此把 PG 改綁 `0.0.0.0`。
- 方向很重要：**Windows → WSL 是內建就通的那一邊**；會痛的是反方向（WSL → Windows），
  那個才需要 SSH 反向通道。
- **WSL 必須先起來**。IIS 隨開機自動啟動，WSL 不會；WSL 沒跑時應用程式啟動就連不到 5432。
  `wsl --shutdown` 或 WSL 閒置自動關機也會斷線。

##### 授權與連線字串

換一台機器就是換一組 `anchorId`：

1. 目標機上跑 `MachineId --init` 產生錨定碼，再跑一次（不加參數）取得申請碼
2. 回簽發主機以 `issue --request <申請碼>` 核發測試授權（指令見 `deploy/iis-deployment.md` §4）—— **不可用 `--internal`**，兩個獨立理由各自都會讓站台起不來：
   它會自動抓「執行簽發工具那台」的指紋（在 WSL 上簽就綁到 WSL 的錨定碼）；
   而且它設的 `clientCode=SMARTFACTORY-INTERNAL` 會撞上 `LicenseValidator` 的
   `isInternal && !_env.IsDevelopment()` 擋門，**IIS 跑的正是 Production**
3. `DefaultEncrypted` 綁的是**原主機的** `anchorId`，**新機器解不開**。測試階段最省事的是
   核發時帶 `--allow-plain`，改用明文 `DefaultConnection`

錨定檔位置：Windows 走 `%ProgramData%\SmartFactory\instance-id`（建立時需管理員權限；
無權限時 `ChooseWritablePath()` 會退到 `%LocalAppData%`，**此時以應用程式集區身分執行會讀不到**
—— 判斷方式是看 `MachineId --init` 輸出的路徑）。`ApplyRestrictivePermissions` 在 Windows
直接 return，檔案沿用 `%ProgramData%` 的繼承 ACL，因此集區身分讀得到，不需額外授權。

##### Ollama

`OllamaUrl` 預設 `http://localhost:11434`。應用程式在 Windows 時經同一條 relay 就連到 WSL 的
Ollama，**組態不用改**；`TrustedInferenceHosts` 留空（只信 loopback）也仍然成立。

##### 硬碟空間

**建議作法：在開發機 `dotnet publish`，整包複製過去，不要在目標機建置。**

| 位置 | 項目 | 空間 |
|------|------|------|
| Windows | ASP.NET Core 10 Hosting Bundle | ~200 MB |
| Windows | 發布產物（app DLL，`bin/Release` 實測 82 MB 扣 pdb） | ~75 MB |
| Windows | `wwwroot`（其中 `docs` 57 MB、`lib` 9.3 MB） | 67 MB |
| Windows | 日誌／上傳餘裕 | 1–2 GB |
| WSL | PostgreSQL 資料庫（`ProjectZ` 庫實測 50 MB，全新種子庫 50–100 MB） | ~100 MB |
| WSL | WAL／vacuum／成長餘裕 | ~1 GB |
| WSL | PostgreSQL 本體（若尚未安裝） | ~300 MB |
| | **合計** | **約 3 GB**（最小可跑約 700 MB） |

若堅持在目標機建置，另加約 **4 GB**：.NET 10 SDK（Windows 版 ~800 MB）、
`~/.nuget/packages`（實測 **2.8 GB**）、工作樹含 `bin` 168 MB ＋ `obj` 104 MB ＋
`node_modules` 66 MB。`node_modules` 只有 jest 在用，**執行期完全不需要，不要複製過去**。

> 相容性已查核（2026-09-25）：應用程式碼中沒有硬編的 POSIX 路徑或 shell 呼叫，
> 唯一的 `/etc/smartfactory/instance-id` 在 `MachineAnchor` 內已依作業系統分支。

---

### 資料庫遷移 (Database Migration)
若有修改 Entity Framework Model，需執行遷移：
```bash
# 建立遷移
dotnet ef migrations add [MigrationName]

# 更新資料庫（需指定 Development 環境，否則 EF 工具找不到正確連線字串）
ASPNETCORE_ENVIRONMENT=Development dotnet ef database update
```

> **注意**：應用程式啟動時會自動執行 `db.Database.Migrate()`，
> 因此一般開發流程只需 `dotnet run`，不必手動執行 `dotnet ef database update`。
> 僅在需要確認 migration 狀態或在 CI/CD 流程中才需要手動執行。

---

### 速率限制（Rate Limiting）

登入及 Razor Pages 套用 Fixed Window 速率限制，設定位於 `Program.cs`：

| 環境 | 允許次數 | 視窗時間 | 說明 |
|---|---|---|---|
| Development | 1000 次 | 1 分鐘 | 開發時不受限制干擾 |
| Production | 10 次 | 15 分鐘 | 防止暴力破解登入 |

**常見問題：HTTP 429 Too Many Requests**

開發時頻繁重新整理或測試觸發限制，等待視窗時間過後即恢復，
或重新啟動 `dotnet run`（記憶體中的計數器會重置）。

**若需調整生產環境限制**，修改 `Program.cs` 中以下數值後重新部署：
```csharp
limiterOptions.PermitLimit = 10;          // 允許次數
limiterOptions.Window = TimeSpan.FromMinutes(15);  // 視窗時間
```

> 速率限制於應用程式啟動時設定，修改後需**重啟應用程式**才會生效。

## 4. 角色與權限開發規範 (Roles & Permissions)

本系統採用 **RBAC (Role-Based Access Control)** 與 **選單鍵值 (MenuKey)** 結合的權限控管機制。開發新功能時，必須嚴格遵守以下規範：

### 4.1. 核心服務
- **IUserContext**: 用於取得當前登入使用者的資訊，特別是 `ActiveRoleId` (當前切換的角色 ID)。
- **IPermissionService**: 用於檢查具體權限 (Create/Update/Delete)。

### 4.2. 控制器層級 (Controller Level)
所有控制器必須加上 `[Authorize]` 屬性，確保僅有授權使用者可存取。
```csharp
[Authorize(Roles = "Admin,User,...")] // 若有特定角色限制
public class SomeController : Controller { ... }
```
若需更細緻的權限控制，應注入 `IPermissionService` 進行檢查。

### 4.3. 視圖層級 (View Level) - 按鈕控制
在 `.cshtml` 視圖中，**不可**直接顯示所有操作按鈕。必須依據 `PermissionService` 的檢查結果動態渲染。

**標準實作範例**：
```csharp
@inject IPermissionService PermissionService
@{
    // 使用 MenuKey 查詢權限
    var canCreate = await PermissionService.CanCreate("OrgStructure");
    var canUpdate = await PermissionService.CanUpdate("OrgStructure");
    var canDelete = await PermissionService.CanDelete("OrgStructure");
}

<!-- 新增按鈕 -->
@if (canCreate) {
    <a asp-action="Create" class="btn btn-primary">新增</a>
}

<!-- 列表中的操作按鈕 -->
@if (canUpdate) {
    <a asp-action="Edit" ...>編輯</a>
}
@if (canDelete) {
    <button ...>刪除</button>
}
```

### 4.4. 權限資料表 (MenuPermissions)
- 新增 ProjectZ 顧問模組功能時，必須在 `Services/ProjectZMenuContributor.cs` 定義對應的 `MenuKey`（❌ 禁止改 Core 的 `Services/MenuService.cs` 與 `Services/CoreMenuContributor.cs`）。
- MENU 多語字串加在 `Resources/ProjectZSharedResource.zh-TW.resx` / `en-US.resx`（❌ 禁止改 Core 的 `Resources/SharedResource.*.resx`）。
- 必須在資料庫表 `MenuPermissions` 中設定該 `MenuKey` 對應各角色的權限 (`CanRead`, `CanCreate`, `CanUpdate`, `CanDelete`, `CanPrint`)。
- 詳細禁/允許區界限見 `.claude/LOCAL_RULES.md` 的「核心禁止事項（ProjectZ 追加）」。

### 4.5. 多重角色與切換
- 使用者可能擁有多個角色，但系統運作時僅依據 `UserContext.ActiveRoleId` (當前切換的角色) 判斷權限。
- 開發時**嚴禁**使用 `User.IsInRole("Admin")` 來略過細部權限檢查，除非是全域系統設定。所有業務邏輯應優先檢查 `MenuPermission`。
- **權限排除邏輯**：系統預設選單（如 `Home`、`UserSettings`）應在權限設定矩陣中排除（在 `PermissionController.Edit` 中過濾），避免被意外關閉。

### 4.6. 權限按鈕與操作控制 (Action Button Control)
在列表頁或編輯頁中，應根據 `IPermissionService` 的檢查結果來決定按鈕的狀態（隱藏或禁用）。

**主要邏輯**：
1. **隱藏 (Hide)**：若使用者完全不應看到該功能（如：無權點擊進入的分頁連結）。
2. **禁用 (Disable)**：若使用者能看到該項功能，但因權限不足無法執行（如：稽核日誌中，能看到列表但「設定功能權限」按鈕因無修改權限而禁用）。

**視圖範例 (View Example)**：
```cshtml
@inject IPermissionService PermissionService
...
@if (await PermissionService.CanUpdate("PermissionSettings"))
{
    <a asp-action="Edit" asp-route-id="@role.Id" class="btn btn-sm btn-primary">
        <i class="bi bi-shield-check"></i> @Localizer["SetPermissions"]
    </a>
}
else
{
    <button class="btn btn-sm btn-outline-secondary" disabled title="@Localizer["NoUpdatePermission"]">
        <i class="bi bi-shield-lock"></i> @Localizer["SetPermissions"]
    </button>
}
```

## 5. UI 開發規範 (UI Development)

### 5.0. 標準畫面產生規範（new-screen skill）

AI 產生任何新畫面時，**必須同時**套用下列規格（缺一不可）：

1. [.claude/commands/new-screen.md](.claude/commands/new-screen.md) — Core 基礎規格（查詢／分頁／排序／多廠別／Side Panel／多語）
2. [.claude/commands/new-screen-projectz.md](.claude/commands/new-screen-projectz.md) — ProjectZ 補充規格（模組路徑、`_ViewImports`、`ProjectZMenuContributor`、UOM/Currency 套用）
3. [.claude/LOCAL_RULES.md](.claude/LOCAL_RULES.md) §「一致性框架」 — `ModuleControllerBase`、`_CrudPanelScripts`、`window.i18n`、`_PrintLayoutZ` 等共用元件

> 兩份 commands 衝突時，`new-screen-projectz.md` 優先。
> 建置期把關：`SmartFactory.MES.Tests/Conventions/` 的 R1–R6b 會在 `dotnet test` 時擋下不在 baseline 的新違規（多語 key 對齊、MenuKey 登錄、硬編碼 CJK、`armValidation` 注入、`submitPanel` 成功契約等）。

其他畫面／資料類工具：

| Skill | 用途 | 完整規格 |
|-------|------|---------|
| `/new-report` | 單據列印（QR Code / PDF） | [.claude/commands/new-report.md](.claude/commands/new-report.md) |
| `/xlsx` | Excel 匯出 | [.claude/commands/xlsx.md](.claude/commands/xlsx.md) |
| `/webapp-testing` | E2E UI 測試（Playwright） | [.claude/commands/webapp-testing.md](.claude/commands/webapp-testing.md) |
| `/new-job` | 背景作業／排程（IHostedService / Hangfire） | [.claude/commands/new-job.md](.claude/commands/new-job.md) |
| `/new-analytics` | 資料分析儀表板（非單據列印） | [.claude/commands/new-analytics.md](.claude/commands/new-analytics.md) |

---

### 5.0-A. 日期時間欄位處理規則（多時區）

系統所有日期時間統一以 **UTC 存入資料庫**，顯示時依廠別時區轉換。

#### 時區優先順序

```
廠別 Plant.DefaultTimeZone → 使用者 TimeZoneId → Asia/Taipei（fallback）
```

`IUserContext.TimeZoneId` 已自動實作上述優先順序。

#### 系統自動產生的時間欄位

```csharp
// Controller 或 Service 中，不要用 DateTime.UtcNow
// 改用注入的 IDateTimeService
entity.CreatedAt = _dateTimeService.UtcNow;  // 等同 DateTime.UtcNow，語意更清楚
```

#### 使用者輸入的日期欄位（如需求日期、到期日）

```csharp
// POST 儲存時：使用者輸入的是廠別當地時間，存入前需轉為 UTC
entity.RequiredDate = _dateTimeService.ToUtcTime(inputDate);

// GET 編輯時：從 DB 取出 UTC，轉為廠別當地時間顯示給使用者
ViewBag.RequiredDateLocal = _dateTimeService.ToLocalTime(entity.RequiredDate);
```

#### View 顯示時間（已全域注入 DateTimeSvc）

`_ViewImports.cshtml` 已全域注入 `@inject IDateTimeService DateTimeSvc`，所有 View 直接使用：

```html
<!-- ✅ 正確：依廠別時區顯示 -->
@DateTimeSvc.ToLocalTime(item.CreatedAt).ToString("yyyy-MM-dd HH:mm:ss")

<!-- ❌ 錯誤：使用伺服器本地時區，多廠別時顯示錯誤 -->
@item.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
```

---

### 5.1. 搜尋選單 (Select2)
系統已整合 **Select2** 以支援大數據量的下拉選單搜尋功能。

**如何使用**：
1. 在 `<select>` 元素加上 `select2-enable` 類別：
   ```html
   <select class="form-select select2-enable" data-placeholder="請選擇..." data-allow-clear="true">
       <option></option>
       <option value="1">選項 A</option>
   </select>
   ```
2. **屬性說明**：
   - `data-placeholder`: 設定提示文字（注意：若要使用 `allowClear`，第一個 `<option>` 必須為空）。
   - `data-allow-clear`: 是否顯示清除按鈕 (`true`/`false`)。
   - 樣式：自動套用 **Bootstrap 5** 主題。

### 5.2. 搜尋與分頁 (Search & Pagination)
為了確保系統操作的一致性與效能，**所有作業類型的列表頁面 (Operational Listing Pages)** 都必須實作搜尋與分頁功能。

**實作要求**：
1. **後端 (Controller)**：應接受 `searchTerm`, `pageNumber`, `pageSize` 參數，並傳回 `PaginatedList<T>`。
2. **前端 (View)**：
   - 應包含搜尋輸入框及每頁筆數選擇器 (`pageSize`)。
   - 應在列表底部顯示分頁導航 (`PageIndex`, `TotalPages`, `Previous/Next`)。
   - 應處理查無資料的情況。

**標準實作範例 (Controller)**：
```csharp
public async Task<IActionResult> Index(string? searchTerm, int? pageNumber, int? pageSize)
{
    ViewBag.SearchTerm = searchTerm;
    ViewBag.CurrentPageSize = pageSize ?? 10;

    var query = _context.Entities.AsQueryable();
    if (!string.IsNullOrEmpty(searchTerm)) {
        query = query.Where(x => x.Name.Contains(searchTerm));
    }

    return View(await PaginatedList<Entity>.CreateAsync(query.AsNoTracking(), pageNumber ?? 1, ViewBag.CurrentPageSize));
}
```

**標準實作範例 (View)**：
請參考 `Plant/Index.cshtml` 或 `Role/Index.cshtml` 的結構，使用統一的卡片佈局與樣式。配合 **Lazy Loading** 模式，初次載入時應呈現「請輸入搜尋條件」提示。

### 5.3. 查詢結果列印 (Printing Results)
為了確保報表輸出的一致性，系統採用 **CSS @media print** 技術搭配瀏覽器的 `window.print()`。

**實作做法 (Methodology)**：
1. **權限控管**：列印功能必須與選單權限掛鉤。在 View 中使用 `PermissionService.CanPrint(menuKey)` 判斷。
2. **UI 觸發**：在搜尋表單的按鈕區塊加入「列印」按鈕。
3. **樣式優化**：所有列表頁面應共用 `site.css` 中的全域列印樣式，避免重複撰寫。

**CSS 列印規範 (@media print)**：
開發新頁面時，若有特殊元素不應出現在報表中，請確保其符合以下全域規則：
- **隱藏元素**：導覽列 (`header`)、側邊欄 (`#sidebar-container`)、頁尾 (`footer`)、分頁導航 (`.pagination`)、所有按鈕 (`.btn`)、搜尋卡片 (`.card.mb-4`)。
- **表格優化**：
  - 使用 `thead { display: table-header-group; }` 確保跨頁列印時標頭自動重複。
  - 隱藏表格中的「操作」欄位（通常為最後一欄：`.table th:last-child, .table td:last-child`）。

**視圖範例 (View Example)**：
```cshtml
@inject IPermissionService PermissionService
...
<div class="col-md-auto d-flex align-items-end gap-2">
    <button type="submit" class="btn btn-primary">@Localizer["Search"]</button>
    
    @if (await PermissionService.CanPrint("UserManagement"))
    {
        <button type="button" class="btn btn-outline-info" onclick="window.print()">
            <i class="bi bi-printer"></i> @Localizer["Print"]
        </button>
    }
</div>
```

### 5.4. 動態排序 (Dynamic Sorting)
為了提升大數據量下的可用性，所有管理列表必須支援點擊標頭排序。

**實作要求**：
1. **後端 (Controller)**：
   - 接收 `sortOrder` 參數（設為選配，預設為 `null`）。
   - 使用 `ViewBag` 儲存排序狀態，以便在 View 中呈現切換連結。
   - 使用 `switch` 運算式處理排序邏輯。
   
2. **前端 (View)**：
   - 將表格標頭 (`<th>`) 改為 `<a>` 連結。
   - 連結必須包含當前所有搜尋參數（`searchTerm`, `pageSize` 等）以保存狀態。
   - 使用 `bi-sort-up` 或 `bi-sort-down` 圖示顯示當前排序方向。

**標準實作範例 (Controller)**：
```csharp
public async Task<IActionResult> Index(string? searchTerm, ..., string? sortOrder = null)
{
    ViewBag.CurrentSort = sortOrder;
    ViewBag.NameSort = string.IsNullOrEmpty(sortOrder) || sortOrder == "name_asc" ? "name_desc" : "name_asc";
    
    var query = _context.Entities.AsQueryable();
    // ... filtering ...
    
    query = sortOrder switch
    {
        "name_asc" => query.OrderBy(x => x.Name),
        "name_desc" => query.OrderByDescending(x => x.Name),
        _ => query.OrderBy(x => x.Id) // 預設排序
    };
    return View(await PaginatedList<T>.CreateAsync(query, ...));
}
```

---

### 5.5. 頁面標題與佈局
- 頁面標題應使用 `ViewData["Title"]` 設定。
- 列表頁面應包含功能圖示 (如 `<i class="bi bi-gear"></i>`)。
- 操作按鈕應統一位於列表右側或頁面頂部。

---

## 6. 新增功能模組 Checklist

> **AI 畫面產生器（new-screen skill）**
>
> 新增畫面時，AI 會自動套用 `.claude/commands/new-screen.md` 的完整規格。
> 對話開始時 AI 會詢問「是否自動產生？」：
> - **Y**：依預設值（查詢/分頁/排序/多廠別/時區/列印全開，簽核關閉）立即產生全部程式碼
> - **N**：逐項詢問需求（未回答項採預設值）
>
> 下列 Checklist 仍適用於驗收與手動補充的項目。

新增任何業務功能模組時，**必須依序完成以下所有步驟**，以確保系統一致性。

### 6.1. 後端 (Backend)

- [ ] **定義 EF Model**：顧問模組放 `Modules/{MODULE}/Models/`；Core 功能才放根目錄 `Models/`。實作 `IPlantEntity` 與否依「Entity 資料範圍規範」判斷
- [ ] **更新 DbContext**：顧問模組在 `Data/ApplicationDbContextExtensions.cs`（`partial class`）加入 `DbSet<T>` 與 `OnModelCreatingPartial` 內的 Fluent API（❌ 禁止改 Core 的 `Data/ApplicationDbContext.cs`）
- [ ] **執行 Migration**：`dotnet ef migrations add [模組名稱]` → 啟動應用程式自動 `Migrate()`
- [ ] **定義 MenuKey**：顧問模組在 `Services/ProjectZMenuContributor.cs`；Core 功能才在 `Services/CoreMenuContributor.cs`（❌ Core 的 `MenuService.cs` 僅為收集器，不含 menu 定義，禁改）
- [ ] **設定預設權限**：在資料庫 `MenuPermissions` 表中為各角色設定 `CanRead/CanCreate/CanUpdate/CanDelete/CanPrint`
- [ ] **建立 Controller**：在 `Controllers/` 建立 MVC Controller，實作標準 CRUD（Index/Create/Edit/Delete），參考 `PlantController.cs` 結構
- [ ] **（可選）建立 API Controller**：在 `Api/` 建立 REST API Controller，參考 `Api/WorkOrdersController.cs` 結構

### 6.2. 前端 (Frontend)

- [ ] **建立 Views 資料夾**：在 `Views/[ControllerName]/` 建立對應 `.cshtml`
- [ ] **Index 頁面**：須包含搜尋、分頁、排序、列印按鈕（參考 § 5.2~5.4）
- [ ] **Create/Edit 頁面**：須包含 Anti-Forgery Token、Select2 下拉選單（若需要）
- [ ] **權限按鈕控制**：注入 `IPermissionService`，依 `CanCreate/CanUpdate/CanDelete` 動態顯示按鈕

### 6.3. 測試 (Tests)

- [ ] **單元測試**：在 `SmartFactory.MES.Tests/` 建立 Controller 與 Service 的 xUnit 測試
- [ ] **整合測試（可選）**：在 `SmartFactory.MES.IntegrationTests/` 加入端點存取與授權驗證測試

### 6.4. 文件

- [ ] **更新 DEVELOPMENT.md**：若有特殊開發注意事項，補充說明

---

**標準 Controller 骨架範例**：
```csharp
[Authorize]
public class ExampleController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IPermissionService _permissionService;

    public ExampleController(ApplicationDbContext context, IPermissionService permissionService)
    {
        _context = context;
        _permissionService = permissionService;
    }

    // MenuKey 常數，顧問模組與 ProjectZMenuContributor.cs 對應；Core 功能才與 CoreMenuContributor.cs 對應
    private const string MenuKey = "Example";

    public async Task<IActionResult> Index(string? searchTerm, int? pageNumber, int? pageSize, string? sortOrder = null)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();
        // ... 搜尋、排序、分頁邏輯（參考 PlantController.cs）
    }
}
```


---

## 7. Docker 部署 (Docker Deployment)

### 7.1. 架構概觀

本系統採用 **單台 Server（企業內部機或雲端 VM）** 搭配 Docker Compose 執行，由兩個 Container 組成：

```
企業 Server（一台 VM 或實體機）
│
├── Container: app（MES 應用程式）
│   ├── ASP.NET Core MES（監聽 port 8080）
│   ├── 對外開放 port 80 → 內部 8080
│   └── 透過 Docker 內部網路連接 db container
│
└── Container: db（PostgreSQL）
    ├── PostgreSQL 16（port 5432）
    ├── 僅容器內部網路可存取（不對外開放）
    └── 資料持久化至 Docker Volume（pgdata）
```

**生產環境重要**：`db` container 的 port 5432 **不應對外暴露**，只有同一 docker-compose 網路內的 `app` container 能連接。現有 `docker-compose.yml` 為方便本機開發保留了 `ports: ["5432:5432"]`，**生產部署時必須將此設定移除或使用獨立的 `docker-compose.prod.yml`**。

### 7.2. 目錄結構（部署時）

```
/opt/smartfactory-mes/          ← 建議部署目錄
├── docker-compose.yml
├── .env                        ← 環境變數（不進 Git）
├── Security/
│   └── SmartFactory.MES.Security.dll  ← Security Anchor DLL（從 GitHub Releases 下載與系統同版本的檔案）
└── license.json                ← 環境授權檔（所有環境均需，由 SmartFactory 核發）
```

### 7.3. 環境變數設定

部署前，在 `.env` 檔設定資料庫密碼（此檔不得進版本控制）：

```bash
# .env（生產環境，不進 Git）
DB_PASSWORD=請設定強密碼
```

docker-compose.yml 使用 `${DB_PASSWORD:-Dev12345}` 讀取此變數。若未設定，預設值 `Dev12345` **僅限開發環境使用**，生產環境必須替換。

### 7.4. 常用操作指令

```bash
# 首次啟動（或更新版本後）
docker compose up -d

# 查看運行狀態
docker compose ps

# 查看應用程式日誌
docker compose logs -f app

# 查看資料庫日誌
docker compose logs -f db

# 停止服務
docker compose down

# 更新版本（顧問 CI/CD 推送新 Image 後）
docker compose pull
docker compose up -d

# 停止並清除所有資料（危險！會刪除資料庫）
docker compose down -v
```

### 7.5. 資料庫首次初始化

首次部署前需手動執行 EF Core Migration 建立資料庫結構：

```bash
# 方法 1：進入 app container 執行（Container 已啟動時）
docker compose exec app dotnet ef database update

# 方法 2：本機直接執行（需設定連線字串指向生產 DB）
dotnet ef database update --project SmartFactory.MES
```

> 系統目前已在 `Program.cs` 啟動時自動呼叫 `db.Database.Migrate()` 套用移轉，開發時只需執行 `dotnet run` 即可。

### 7.6. 資料持久化

| Volume 名稱 | 對應路徑 | 說明 |
|------------|---------|------|
| `pgdata` | `/var/lib/postgresql/data` | PostgreSQL 資料庫檔案 |
| `applogs` | `/app/logs` | 應用程式日誌 |

資料儲存在 Docker Named Volume，`docker compose down` **不會**刪除資料；唯有 `docker compose down -v` 才會清除。

#### Data Protection 金鑰持久化（上線前 checklist）

ASP.NET Core Data Protection 金鑰用於加解密**登入 Cookie 與 Anti-Forgery token**。
目前 `Program.cs` 未設定 `PersistKeysTo*`，金鑰落在預設位置（Linux：`~/.aspnet/DataProtection-Keys`）：

- **單機直接部署**：可運作（home 目錄持久），但金鑰**未加密落地**且綁定執行帳號，換帳號/換機即失效。
- **Docker / 多實例部署（風險）**：容器檔案系統重建即金鑰消失 → **重新部署後所有使用者被強制登出、進行中表單的 Anti-Forgery 驗證全數失敗**。多實例間金鑰不共用則 Cookie 互不相認。

**部署形態確定為 Docker 或多實例前，必須在 `Program.cs`（或以 IAppModule 擴充點）加入**：

```csharp
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo("/app/dpkeys"))   // 掛 Volume（如 dpkeys:/app/dpkeys）
    .SetApplicationName("SmartFactory.MES");                     // 多實例共用同一邏輯應用名
```

並在 `docker-compose.yml` 增加 `dpkeys` Named Volume。金鑰目錄權限應限執行帳號可讀寫（`chmod 700`）。

### 7.7. 本機開發環境

本機開發（`dotnet watch run`）時，直接使用 `docker compose up -d db` 啟動資料庫 container 即可，不需啟動 `app` container：

```bash
# 只啟動資料庫，應用程式在本機跑
docker compose up -d db

# 本機啟動應用程式（開發模式，熱重載）
dotnet watch run --urls "http://localhost:5110;https://localhost:7054"
```

此時 `appsettings.Development.json` 的連線字串指向 `localhost:5432`，docker-compose.yml 中的 `db` 服務需設定 `ports: ["5432:5432"]`（現有設定已包含）。

### 7.8. CI/CD 整合

Docker 與 CI/CD 搭配時，生產環境的 `docker-compose.yml` 應從 Registry 拉取預建 Image，而非在 Server 上本機 build：

```yaml
# 生產環境 docker-compose.yml（從 Registry 拉取）
services:
  app:
    image: ghcr.io/your-org/enterprise-acme:${APP_VERSION:-latest}
    # 移除 build: 區塊
```

**CI/CD 完整更新流程：**

```
顧問推送 code → GitHub Actions 觸發
    ↓
① build:  docker build → 產生新 Image（含版本 tag，如 v1.2.3）
② test:   dotnet test  → 執行自動化測試
③ push:   docker push  → 推送至 Container Registry
    ↓
客戶 IT 在生產 Server 執行（2 行完成更新）：
$ docker compose pull   ← 拉取新 Image
$ docker compose up -d  ← 舊 container 停止，新 container 啟動
```

版本回滾只需指定舊 tag 重啟：
```bash
APP_VERSION=v1.1.0 docker compose up -d
```

### 7.9. Docker vs 直接部署比較

本系統兩種部署方式均可運作，選擇依客戶 IT 熟悉度決定：

| | Docker Compose | 直接部署（裸機） |
|--|---------------|----------------|
| **更新方式** | `docker compose pull && up -d`（2 行） | 停服務 → 複製檔案 → 重啟（多步驟） |
| **版本回滾** | 指定舊 Image tag，秒速回滾 | 需保留舊版本檔案手動替換 |
| **環境一致性** | Image 包含所有相依，開發 = 生產 | 需確保 Server 上 .NET Runtime 版本相符 |
| **資料庫隔離** | PostgreSQL 不對外暴露 port | 需手動設定防火牆規則 |
| **CI/CD 整合** | 原生支援，推送 Image 即完成 | 需額外設定 SSH 部署腳本 |
| **IT 門檻** | 需了解 Docker 基本操作 | 熟悉傳統服務管理即可 |
| **Security Anchor DLL** | ✓ 均支援 | ✓ 均支援 |
| **環境指紋（License）** | ✓ 均支援 | ✓ 均支援（需確認為 Linux 環境） |

> **建議**：新部署優先採用 Docker，對於已有傳統維運習慣的客戶 IT 可採直接部署，兩者授權機制行為完全相同。

---

## 8. AI 輔助開發工具（ECC）

本專案採用 **Everything Claude Code（ECC）** 強化 Claude Code 的開發效率。ECC 是 Anthropic Hackathon 獲獎作品，提供 Agents、Slash Commands 和 Hooks 三層能力。

### 8.1. 安裝狀態

目前已選擇性安裝與本專案相關的元件至 `~/.claude/`：

```
~/.claude/
├── agents/
│   ├── architect.md         ← 架構設計輔助
│   ├── code-reviewer.md     ← 程式碼審查
│   ├── planner.md           ← 任務規劃
│   └── security-reviewer.md ← 安全性審查
└── commands/
    ├── plan.md              ← /plan
    ├── code-review.md       ← /code-review
    ├── tdd.md               ← /tdd
    ├── quality-gate.md      ← /quality-gate
    ├── save-session.md      ← /save-session
    ├── resume-session.md    ← /resume-session
    └── sessions.md          ← /sessions
```

### 8.2. Slash Commands 使用方式

在 Claude Code 對話中直接輸入：

| 指令 | 用途 | 建議使用時機 |
|------|------|-------------|
| `/plan` | 開發前架構規劃 | 新增模組或重構前 |
| `/code-review` | 程式碼審查 | PR 建立前、功能完成後 |
| `/tdd` | TDD 開發流程引導 | 建立新 Service 或複雜邏輯時 |
| `/quality-gate` | 品質門檻檢查 | 提交前快速掃描 |
| `/save-session` | 儲存目前對話進度 | 長時間開發 session 結束前 |
| `/resume-session` | 還原上次對話進度 | 新對話開始時接續前次工作 |
| `/sessions` | 查看所有已儲存的 session | 需要查閱歷史開發脈絡時 |

### 8.3. Agents 使用方式

透過 Claude Code 的 Agent 呼叫機制：

```
請用 architect agent 評估這個模組的設計...
請用 security-reviewer agent 審查以下程式碼...
```

| Agent | 功能 |
|-------|------|
| `architect` | 系統架構設計、模組邊界規劃、技術選型建議 |
| `code-reviewer` | 詳細程式碼審查，涵蓋可讀性、效能、安全性 |
| `planner` | 將需求拆解為具體任務清單，支援多步驟規劃 |
| `security-reviewer` | 安全漏洞掃描，對應 OWASP Top 10 |

### 8.4. Hooks 安裝（選擇性）

Hooks 需透過 Claude Code Plugin Marketplace 安裝，**安裝後自動觸發，無需手動呼叫**：

```bash
# 在終端機開啟 Claude Code 互動模式後執行
/plugin marketplace add affaan-m/everything-claude-code
/plugin install everything-claude-code@everything-claude-code
```

安裝後啟用的自動化行為：

| Hook | 觸發時機 | 效果 |
|------|---------|------|
| `quality-gate`（PostToolUse） | 每次編輯 `.cs` 等檔案後 | 自動執行品質檢查（非同步，不阻塞） |
| `pre-compact`（PreCompact） | Context 即將壓縮前 | 自動儲存開發進度 |
| `session-end`（Stop） | 每次回應結束後 | 持久化 session 狀態，供 `/resume-session` 還原 |
| `cost-tracker`（Stop） | 每次回應結束後 | 記錄 token 用量與費用 |
| `git-push-reminder`（PreToolUse） | 執行 `git push` 前 | 提示確認推送內容 |

> **注意**：TypeScript 相關 hooks（`post-edit-format`、`post-edit-typecheck`、`check-console-log`）對 .NET 專案無效，ECC 會自動略過。

### 8.5. 跨對話工作流程

結合 `save-session` / `resume-session`，解決長時間跨對話開發的 context 遺失問題：

```
開發 Session A：
  → 完成部分功能
  → /save-session          ← 儲存進度

開發 Session B（新對話）：
  → /resume-session        ← 還原 Session A 的進度與決策
  → 繼續開發
```

### 8.6. 完整安裝（重新設定環境時）

若需要在新機器重新安裝 ECC agents 和 commands：

```bash
git clone --depth=1 https://github.com/affaan-m/everything-claude-code /tmp/ecc

mkdir -p ~/.claude/agents ~/.claude/commands

# Agents
cp /tmp/ecc/agents/architect.md ~/.claude/agents/
cp /tmp/ecc/agents/code-reviewer.md ~/.claude/agents/
cp /tmp/ecc/agents/planner.md ~/.claude/agents/
cp /tmp/ecc/agents/security-reviewer.md ~/.claude/agents/

# Commands
cp /tmp/ecc/commands/plan.md ~/.claude/commands/
cp /tmp/ecc/commands/code-review.md ~/.claude/commands/
cp /tmp/ecc/commands/tdd.md ~/.claude/commands/
cp /tmp/ecc/commands/quality-gate.md ~/.claude/commands/
cp /tmp/ecc/commands/save-session.md ~/.claude/commands/
cp /tmp/ecc/commands/resume-session.md ~/.claude/commands/
cp /tmp/ecc/commands/sessions.md ~/.claude/commands/

rm -rf /tmp/ecc
```

> **不建議全量安裝** ECC 的 140+ skills，會稀釋 context window。本專案僅選用與 .NET 後端開發相關的元件。
