# SmartFactory MES 手機版架構規劃

> **文件性質**：架構設計規劃文件
> **閱讀對象**：核心開發人員、顧問
> **狀態**：本文為早期架構規劃；實作已超前本文 Phase 劃分。**以 `.claude/specifications/mobile-pwa.md`〈實作現況對照〉為準**（手機端已收斂至 `wwwroot/mobile/`，工單作業／報工／Setup／甘特等皆已落地）。本文僅保留作背景設計參考。
> **最後更新**：2026-06-28（狀態註記校正；內文 Phase 劃分未逐段更新）

---

## 1. 需求摘要

| 需求 | 說明 |
|------|------|
| 手機版登入畫面 | 獨立的手機友善登入頁，共用現有帳號體系 |
| 手機作業畫面 | 各業務作業的手機版 UI（工單執行、品質檢查等） |
| 模組存取控制 | 設定帳號（角色）可使用哪些手機功能模組 |
| 彈性作業權限 | 按鈕操作不限於 CRUD，支援掃描、開始、完工、記錄等自訂作業 |
| 條碼掃描 | 使用手機相機掃描工單條碼、料號、設備 QR Code |

---

## 2. 技術架構選型

### 採用：前後端分離（Bootstrap + 現有 REST API）

```
手機瀏覽器
└── wwwroot/mobile/（靜態 HTML + Bootstrap + JS）
    ├── 登入  → POST /api/mobile/auth/login
    ├── 首頁  → GET  /api/mobile/modules（取得允許的功能選單）
    ├── 作業  → GET/POST /api/mobile/{module}/...
    └── 掃描  → html5-qrcode → POST /api/mobile/scan
```

### 選型理由

| 考量 | 說明 |
|------|------|
| 複用現有 API | `Api/` 目錄下的 REST 端點可直接沿用，減少重複開發 |
| 共用 Identity | 同一套 ASP.NET Core Identity + Cookie Auth，不需獨立 JWT |
| 零額外 runtime | 純靜態檔案放 `wwwroot/mobile/`，不需 Node.js 或額外服務 |
| 未來升級彈性 | 可升級為 PWA（加 Service Worker）或包裝成 App，後端不需改動 |
| 條碼掃描 | html5-qrcode CDN 引入，10 行完成，支援 Android/iOS 15.1+ |

---

## 3. 資料庫設計

### 3.1 新增三張表

#### `MobileModules`（手機功能模組）
```
Id          string PK
ModuleKey   string UNIQUE   ← e.g., "WorkOrderExecution"
DisplayName string          ← e.g., "工單執行"
IconClass   string          ← Bootstrap Icon class, e.g., "bi-clipboard-check"
SortOrder   int
IsActive    bool
```

#### `MobileModuleActions`（模組支援的作業項目）
```
Id          string PK
ModuleId    string FK → MobileModules
ActionKey   string          ← e.g., "Start", "Scan", "Complete", "RecordMeasurement"
DisplayName string          ← e.g., "開始作業"
SortOrder   int
```

#### `MobileRolePermissions`（角色 × 作業 = 是否允許）
```
Id            string PK
RoleId        string FK → AspNetRoles
ModuleActionId string FK → MobileModuleActions
IsAllowed     bool
```

### 3.2 ER 關係

```
AspNetRoles ──< MobileRolePermissions >── MobileModuleActions ──< MobileModules
```

### 3.3 初始資料範例

| ModuleKey | ActionKey | 說明 |
|-----------|-----------|------|
| `WorkOrderExecution` | `View` | 查看工單 |
| `WorkOrderExecution` | `Start` | 開始作業 |
| `WorkOrderExecution` | `Scan` | 掃描條碼 |
| `WorkOrderExecution` | `Complete` | 完工回報 |
| `WorkOrderExecution` | `RecordQty` | 記錄數量 |
| `QualityInspection` | `View` | 查看檢驗單 |
| `QualityInspection` | `Scan` | 掃描樣品 |
| `QualityInspection` | `RecordResult` | 記錄量測值 |
| `QualityInspection` | `Pass` | 判定合格 |
| `QualityInspection` | `Fail` | 判定不合格 |

---

## 4. 後端 API 設計

### 4.1 目錄結構（新增）

```
Api/
├── Mobile/
│   ├── MobileAuthController.cs       ← 登入/登出/目前使用者資訊
│   ├── MobileModuleController.cs     ← 取得允許的功能模組清單
│   ├── MobileScanController.cs       ← 條碼掃描解析
│   ├── MobileWorkOrderController.cs  ← 工單手機作業
│   └── MobileQualityController.cs    ← 品質檢查手機作業
Services/
└── IMobilePermissionService.cs       ← 手機權限服務介面與實作
```

### 4.2 核心 Service 介面

```csharp
public interface IMobilePermissionService
{
    // 取得此角色可存取的手機模組清單（首頁顯示用）
    Task<IEnumerable<MobileModule>> GetAllowedModulesAsync(string roleId);

    // 判斷特定作業是否允許（按鈕顯示用）
    Task<bool> CanExecuteAsync(string roleId, string moduleKey, string actionKey);
}
```

### 4.3 主要 API 端點

| Method | Route | 說明 |
|--------|-------|------|
| `POST` | `/api/mobile/auth/login` | 手機版登入（同 Cookie） |
| `POST` | `/api/mobile/auth/logout` | 登出 |
| `GET`  | `/api/mobile/auth/me` | 目前登入使用者資訊 |
| `GET`  | `/api/mobile/modules` | 取得允許的功能模組清單 |
| `GET`  | `/api/mobile/modules/{key}/actions` | 取得模組允許的作業清單 |
| `POST` | `/api/mobile/scan` | 條碼掃描解析（回傳對應資料） |
| `GET`  | `/api/mobile/workorders` | 工單清單（手機版） |
| `POST` | `/api/mobile/workorders/{id}/start` | 開始作業 |
| `POST` | `/api/mobile/workorders/{id}/complete` | 完工回報 |
| `POST` | `/api/mobile/workorders/{id}/qty` | 記錄數量 |

### 4.4 條碼掃描解析邏輯

```csharp
// POST /api/mobile/scan
// 根據條碼內容自動判斷類型並回傳對應資料
public record ScanRequest(string Barcode);
public record ScanResult(string Type, string Id, object Data);

// Type 可能值：
// "WorkOrder"  → 工單號，回傳工單資訊
// "Material"   → 料號條碼，回傳物料資訊
// "Equipment"  → 設備 QR Code，回傳設備資訊
// "Unknown"    → 無法識別
```

---

## 5. 前端結構

### 5.1 目錄結構

```
wwwroot/mobile/
├── index.html          ← 登入頁
├── home.html           ← 首頁（功能模組格狀選單）
├── workorder/
│   ├── list.html       ← 工單清單
│   ├── detail.html     ← 工單詳情 + 作業按鈕
│   └── scan.html       ← 條碼掃描頁
├── quality/
│   ├── list.html       ← 檢驗單清單
│   └── inspect.html    ← 檢驗作業頁
├── css/
│   └── mobile.css      ← 手機版專用樣式
└── js/
    ├── api.js          ← API 呼叫共用模組（fetch wrapper）
    ├── auth.js         ← 登入狀態管理
    └── scanner.js      ← 條碼掃描共用模組（html5-qrcode 封裝）
```

### 5.2 登入頁（`index.html`）

```html
<!DOCTYPE html>
<html lang="zh-TW">
<head>
    <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1">
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap@5.3/dist/css/bootstrap.min.css">
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11/font/bootstrap-icons.css">
</head>
<body class="bg-light d-flex align-items-center min-vh-100">
    <div class="container" style="max-width:400px">
        <div class="card shadow">
            <div class="card-body p-4">
                <h4 class="text-center mb-4">
                    <i class="bi bi-factory"></i> SmartFactory MES
                </h4>
                <div class="mb-3">
                    <input type="text" id="username" class="form-control form-control-lg"
                           placeholder="帳號">
                </div>
                <div class="mb-4">
                    <input type="password" id="password" class="form-control form-control-lg"
                           placeholder="密碼">
                </div>
                <button class="btn btn-primary btn-lg w-100" onclick="login()">
                    登入
                </button>
                <div id="error" class="text-danger mt-2 text-center d-none"></div>
            </div>
        </div>
    </div>
    <script src="/mobile/js/auth.js"></script>
</body>
</html>
```

### 5.3 首頁選單（`home.html`）

```html
<!-- 功能模組以格狀大按鈕顯示，每格 icon + 名稱 -->
<div class="row g-3" id="moduleGrid">
    <!-- 動態由 /api/mobile/modules 填入 -->
    <!-- 範例：
    <div class="col-6">
        <a href="/mobile/workorder/list.html" class="card text-center p-3 text-decoration-none">
            <i class="bi bi-clipboard-check fs-1 text-primary"></i>
            <div class="mt-2">工單執行</div>
        </a>
    </div>
    -->
</div>
```

### 5.4 條碼掃描（`scanner.js`）

```javascript
// 共用掃描模組，各頁面引入後呼叫 startScan(callback)
// 依賴：html5-qrcode CDN

function startScan(onSuccess) {
    const scanner = new Html5QrcodeScanner("reader", {
        fps: 10,
        qrbox: { width: 250, height: 250 },
        supportedScanTypes: [
            Html5QrcodeScanType.SCAN_TYPE_CAMERA
        ]
    });

    scanner.render(
        async (barcode) => {
            scanner.clear();
            const res = await fetch('/api/mobile/scan', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ barcode })
            });
            const data = await res.json();
            onSuccess(data);
        },
        (err) => { /* 掃描中，忽略 */ }
    );
}
```

### 5.5 手機版 UI 設計原則

| 項目 | 規格 |
|------|------|
| 按鈕最小尺寸 | 44×44px（iOS HIG 標準） |
| 字體大小 | 最小 16px（避免 iOS 自動縮放） |
| Viewport | `width=device-width, initial-scale=1, maximum-scale=1` |
| 顏色編碼 | 成功=綠、警告=黃、危險=紅（Bootstrap 語義色） |
| 狀態回饋 | 所有作業按鈕點擊後顯示 loading spinner |
| 錯誤處理 | Toast 通知（Bootstrap Toast）而非 alert() |

---

## 6. 權限管理後台（整合進現有系統選單）

### 6.1 整合至 AdminManagement 選單群組

**不另開獨立路由**，直接在現有「系統管理」側欄加入「手機模組權限」項目：

**`Services/MenuService.cs` 修改位置**（AdminManagement SubItems 中，緊接 PermissionSettings 之後）：

```csharp
new MenuItem {
    Title = _localizer["MobilePermissionSettings"],
    Controller = "MobilePermission",
    Action = "Index",
    Icon = "bi-phone",
    MenuKey = "MobilePermissionSettings",
    SupportedActions = new List<string> { "Read", "Update" }
},
```

結果側欄「系統管理」群組將顯示：
```
▼ 系統管理  (bi-shield-lock)
    使用者管理
    角色管理
    組織架構
    廠區管理
    權限設定          ← 現有（電腦版 MenuKey 權限）
    手機模組權限      ← 新增（手機版 ActionKey 權限）  ★
    稽核日誌
```

此設計讓管理員在同一個「系統管理」功能群組內完成所有授權設定，不需在兩套介面之間切換。

### 6.2 `MobilePermissionController`（桌面版管理頁）

- 路由：`/MobilePermission`（需要 `MobilePermissionSettings` MenuKey 的 Read 權限）
- 功能：
  - 查看所有手機模組與作業定義（來自 `MobileModules` + `MobileModuleActions`）
  - 以角色為維度，設定每個模組作業的允許/拒絕
  - 批量設定（選取角色後一次設定所有作業）

### 6.3 設定畫面概念

```
角色：[操作員 ▼]

工單執行模組：
  ☑ 查看工單    ☑ 開始作業    ☑ 掃描條碼
  ☑ 完工回報    ☑ 記錄數量    ☐ 異常回報

品質檢查模組：
  ☑ 查看檢驗單  ☑ 掃描樣品    ☑ 記錄量測值
  ☑ 判定合格    ☑ 判定不合格  ☐ 豁免審核
```

---

## 7. Auth 整合

### 7.1 登入流程

```
手機 POST /api/mobile/auth/login { username, password }
    ↓
MobileAuthController 呼叫 SignInManager.PasswordSignInAsync()
    ↓
成功 → 設定 Cookie（與桌面版相同 Cookie，無需 JWT）
    ↓
回傳 { success: true, user: { name, roles, activePlant } }
    ↓
前端儲存至 sessionStorage，導向 home.html
```

### 7.2 API 請求帶 Cookie

瀏覽器自動帶 Cookie，`fetch` 需加 `credentials: 'include'`：

```javascript
// api.js 共用 fetch wrapper
async function apiCall(method, url, body = null) {
    const opts = {
        method,
        credentials: 'include',  // ← 帶 Cookie
        headers: { 'Content-Type': 'application/json' }
    };
    if (body) opts.body = JSON.stringify(body);

    const res = await fetch(url, opts);
    if (res.status === 401) {
        // 未登入或 session 過期，導回登入頁
        window.location.href = '/mobile/index.html';
        return;
    }
    return res.json();
}
```

### 7.3 CSRF 防護

手機 API 需在 Controller 加上：
```csharp
[ValidateAntiForgeryToken]  // 或對 API 使用 SameSite Cookie 保護
```

對純 API（JSON 請求），建議使用 `[IgnoreAntiforgeryToken]` + `SameSite=Strict` Cookie 設定。

---

## 8. 條碼掃描整合

### 8.1 支援格式

| 格式 | 應用場景 |
|------|---------|
| QR Code | 設備 QR Code、工單 QR Code |
| Code 128 | 料號條碼（工廠常用） |
| EAN-13 | 成品條碼 |
| Code 39 | 舊式設備 ID |
| Data Matrix | 零件追溯碼 |

### 8.2 CDN 引入

```html
<!-- html5-qrcode，不需 npm/Node.js -->
<script src="https://unpkg.com/html5-qrcode@2.3.8/html5-qrcode.min.js"></script>
```

### 8.3 手機瀏覽器相容性

| 平台 | 瀏覽器 | 相機掃描 |
|------|--------|---------|
| Android | Chrome / Edge / Firefox | ✅ |
| iOS 15.1+ | Safari | ✅ |
| iOS < 15.1 | Safari | ⚠️ 僅支援圖片上傳模式 |

> 製造業環境手機通常較新，iOS 15.1+ 覆蓋率高，可接受。

---

## 9. 實作 Phase 計畫

### Phase 1：基礎架構 ✅ 已完成（commit 2659bf3，2026-03-13）

**後端**
- [x] 新增 `MobileModules`、`MobileModuleActions`、`MobileRolePermissions` 資料表與 EF Migration
  - `Models/MobileModels.cs`
  - `Migrations/20260313035234_AddMobilePermissions.cs`
- [x] 實作 `IMobilePermissionService`
  - `Services/MobilePermissionService.cs`
  - `GetAllowedModulesAsync` / `CanExecuteAsync` / `GetAllowedActionKeysAsync`
- [x] `MobileAuthController`（登入/登出/me）+ AuditLog（Login/LoginFailed/Logout）
  - `Api/Mobile/MobileAuthController.cs`
  - 路由：`POST /api/mobile/auth/login`、`POST /api/mobile/auth/logout`、`GET /api/mobile/auth/me`
- [x] `MobileModuleController`（模組清單，依角色過濾）
  - `Api/Mobile/MobileModuleController.cs`
  - 路由：`GET /api/mobile/modules`、`GET /api/mobile/modules/{key}/actions`
- [x] 桌面版 `MobilePermissionController`（管理角色權限）+ AuditLog（Permission/Update）
  - `Controllers/MobilePermissionController.cs`
  - `Views/MobilePermission/Index.cshtml`（角色清單）
  - `Views/MobilePermission/Edit.cshtml`（模組作業勾選設定）
- [x] `MenuService` 新增 `MobilePermissionSettings` MenuItem（整合至 AdminManagement）
  - `Services/MenuService.cs`
  - i18n：`SmartFactory.MES.Services.MenuService.zh-TW.resx`（手機模組權限）/ `en-US.resx`（Mobile Module Permissions）

**前端**
- [x] `wwwroot/mobile/index.html`（手機版登入頁）
  - Bootstrap 5.3 + 已登入自動跳轉首頁
- [x] `wwwroot/mobile/home.html`（首頁格狀模組選單）
  - 動態載入 `/api/mobile/modules`、無權限顯示提示
- [x] `wwwroot/mobile/js/api.js`（fetch wrapper，統一 401/403 處理、Toast 通知）
- [x] `wwwroot/mobile/js/auth.js`（登入/登出/requireAuth）
- [x] `wwwroot/mobile/css/mobile.css`（手機版專用樣式，44px 最小點擊區域）

**測試**
- [x] `Tests/Mobile/MobilePermissionServiceTests.cs`（單元，8 cases）
  - 有/無權限角色、CanExecuteAsync 允許/拒絕、停用模組邊界
- [x] `IntegrationTests/Mobile/MobileAuthApiTests.cs`（整合）
- [x] `IntegrationTests/Mobile/MobileModuleApiTests.cs`（整合）
- [x] `IntegrationTests/Mobile/MobileAuditLogTests.cs`（整合）

> **測試結果**：單元測試 108/108 通過。整合測試在本地端因 Security DLL 進入點限制（testhost）無法執行，CI 環境（無 DLL）通過，與既有整合測試一致。

### Phase 2：工單作業（第一個完整模組）

**後端**
- [ ] `MobileScanController`（條碼解析）
- [ ] `MobileWorkOrderController` API + AuditLog（Start/Complete/RecordQty/Scan）

**前端**
- [ ] `wwwroot/mobile/workorder/list.html`（工單清單）
- [ ] `wwwroot/mobile/workorder/detail.html`（詳情 + 作業按鈕）
- [ ] `wwwroot/mobile/workorder/scan.html`（條碼掃描頁）
- [ ] `wwwroot/mobile/js/scanner.js`（掃描共用模組）

**測試**
- [ ] `Tests/Mobile/MobileScanServiceTests.cs`（單元）
- [ ] `IntegrationTests/Mobile/MobileWorkOrderApiTests.cs`（整合）
- [ ] `UITests/Mobile/MobileLoginTest.cs`（UI，Playwright 375×812 viewport）

### Phase 3：品質檢查 + PWA 升級

**後端**
- [ ] `MobileQualityController` API + AuditLog（RecordResult/Pass/Fail）

**前端**
- [ ] 品質檢驗手機作業頁
- [ ] 量測值輸入介面（數字鍵盤優化）
- [ ] `wwwroot/mobile/manifest.json`（PWA App 定義）
- [ ] `wwwroot/mobile/service-worker.js`（靜態資源 Cache First + API Network First）
- [ ] `wwwroot/mobile/icons/`（192×192、512×512 App 圖示）

**測試**
- [ ] `IntegrationTests/Mobile/MobileQualityApiTests.cs`（整合）
- [ ] `UITests/Mobile/MobilePWATest.cs`（驗證 manifest 可安裝、Service Worker 註冊成功）

### Phase 4：完整離線支援（依廠區 WiFi 狀況決定是否實作）

**前端**
- [ ] `wwwroot/mobile/js/db.js`（IndexedDB 封裝：work_orders、action_queue）
- [ ] `wwwroot/mobile/js/sync.js`（Background Sync 排隊與重試邏輯）
- [ ] Service Worker 升級：寫入 API 失敗 → 加入 action_queue → Background Sync

**後端**
- [ ] `/api/mobile/workorders/batch`（批次同步端點，接受離線期間累積的作業陣列）
- [ ] 衝突偵測邏輯（工單狀態已變更時回傳警告而非靜默覆蓋）

**測試**
- [ ] 離線模擬整合測試（關閉網路後操作，恢復後驗證同步結果）

---

## 10. 稽核日誌（AuditLog）整合

手機端所有作業**必須**寫入現有 `AuditLog` 表，確保操作軌跡可追溯。

### 10.1 記錄原則

| 原則 | 說明 |
|------|------|
| 所有寫入作業 | 登入、登出、掃描確認、開始/完工/記錄數量、權限異動 |
| 自動帶出操作人 | 由 `HttpContext.User` 取得，不依賴前端傳入 |
| EntityType 命名 | `Mobile.Auth`、`Mobile.WorkOrder`、`Mobile.Quality`、`Mobile.Permission` |
| 紀錄 PlantId | 多廠別環境下，日誌須攜帶廠別 ID，方便過濾查詢 |

### 10.2 手機作業日誌事件清單

| EntityType | ActionKey | 觸發時機 |
|------------|-----------|---------|
| `Mobile.Auth` | `Login` | 手機版登入成功 |
| `Mobile.Auth` | `LoginFailed` | 登入失敗（錯誤帳密） |
| `Mobile.Auth` | `Logout` | 登出 |
| `Mobile.WorkOrder` | `Start` | 工單開始作業 |
| `Mobile.WorkOrder` | `Complete` | 工單完工回報 |
| `Mobile.WorkOrder` | `RecordQty` | 記錄生產數量 |
| `Mobile.WorkOrder` | `Scan` | 掃描工單條碼（記錄掃描值） |
| `Mobile.Quality` | `RecordResult` | 記錄量測值 |
| `Mobile.Quality` | `Pass` | 判定合格 |
| `Mobile.Quality` | `Fail` | 判定不合格 |
| `Mobile.Permission` | `Update` | 手機模組權限變更（桌面管理頁） |

### 10.3 實作方式

共用現有 `AuditInterceptor` 機制（EF Core 攔截器自動記錄），
對無資料庫寫入的作業（如登入、掃描查詢），由 Controller 明確呼叫：

```csharp
// 範例：Controller 手動寫入 AuditLog
await _auditLogService.LogAsync(new AuditLogEntry
{
    EntityType = "Mobile.Auth",
    ActionKey  = "Login",
    EntityId   = user.Id,
    UserId     = user.Id,
    PlantId    = user.ActivePlantId,
    Remark     = $"手機版登入，IP: {Request.HttpContext.Connection.RemoteIpAddress}"
});
```

---

## 11. 自動測試規劃

### 11.1 測試分層策略

```
測試金字塔（手機版）
┌─────────────┐
│  UI Tests   │  Playwright：登入流程、首頁渲染（手機 viewport）
├─────────────┤
│  整合測試   │  WebApplicationFactory：Mobile API 端對端
├─────────────┤
│  單元測試   │  xUnit + Moq：Service 邏輯、權限計算
└─────────────┘
```

### 11.2 單元測試（`SmartFactory.MES.Tests/Mobile/`）

| 測試類別 | 測試項目 |
|---------|---------|
| `MobilePermissionServiceTests` | 有權限角色回傳正確模組清單 |
| | 無權限角色回傳空清單 |
| | `CanExecuteAsync` 允許/拒絕邏輯 |
| | 多角色使用者取聯集模組 |
| `MobileScanServiceTests` | 工單條碼正確解析 → Type="WorkOrder" |
| | 未知條碼回傳 Type="Unknown" |
| | 料號/設備條碼格式辨識 |

### 11.3 整合測試（`SmartFactory.MES.IntegrationTests/Mobile/`）

共用現有 `TestWebApplicationFactory`（`[Collection("Integration")]`）：

| 測試類別 | 測試項目 |
|---------|---------|
| `MobileAuthApiTests` | POST `/api/mobile/auth/login` 有效帳密 → 200 + Cookie |
| | POST `/api/mobile/auth/login` 無效帳密 → 401 |
| | GET `/api/mobile/auth/me` 未登入 → 401 |
| | GET `/api/mobile/auth/me` 已登入 → 200 + 使用者資訊 |
| `MobileModuleApiTests` | GET `/api/mobile/modules` 未授權角色 → 空陣列 |
| | GET `/api/mobile/modules` 已授權角色 → 正確模組清單 |
| `MobileAuditLogTests` | 登入後 AuditLog 正確產生 `Mobile.Auth/Login` 紀錄 |
| | 完工作業後 AuditLog 產生 `Mobile.WorkOrder/Complete` 紀錄 |

### 11.4 UI 自動化測試（`SmartFactory.MES.UITests/Mobile/`）

使用 Playwright，模擬手機 viewport（375×812，iPhone 14）：

| 測試案例 | 步驟 |
|---------|------|
| `MobileLoginTest` | 開啟 `/mobile/index.html` → 輸入帳密 → 點登入 → 驗證導向 `home.html` |
| `MobileHomeMenuTest` | 登入後 → 驗證首頁顯示正確模組格子數量 |
| `MobileUnauthorizedTest` | 直接訪問 `home.html`（未登入）→ 驗證重導回登入頁 |

```csharp
// Playwright viewport 設定範例
await page.SetViewportSizeAsync(375, 812);
await page.GotoAsync("/mobile/index.html");
await page.FillAsync("#username", "testuser");
await page.FillAsync("#password", "Test@1234");
await page.ClickAsync("button:text('登入')");
await page.WaitForURLAsync("**/mobile/home.html");
```

### 11.5 Phase 對應測試實作計畫

| Phase | 測試類型 | 新增檔案 |
|-------|---------|---------|
| Phase 1 | 單元 | `Tests/Mobile/MobilePermissionServiceTests.cs` |
| Phase 1 | 整合 | `IntegrationTests/Mobile/MobileAuthApiTests.cs` |
| Phase 1 | 整合 | `IntegrationTests/Mobile/MobileModuleApiTests.cs` |
| Phase 1 | 整合 | `IntegrationTests/Mobile/MobileAuditLogTests.cs` |
| Phase 2 | 單元 | `Tests/Mobile/MobileScanServiceTests.cs` |
| Phase 2 | 整合 | `IntegrationTests/Mobile/MobileWorkOrderApiTests.cs` |
| Phase 2 | UI | `UITests/Mobile/MobileLoginTest.cs` |
| Phase 3 | 整合 | `IntegrationTests/Mobile/MobileQualityApiTests.cs` |
| Phase 3 | UI | `UITests/Mobile/MobilePWATest.cs` |

---

## 12. 授權安全強制執行規範

> **⚠️ 實作強制要求**：前端隱藏按鈕只是 UX 設計，**不能作為安全邊界**。
> 所有寫入作業的 API Action 必須在 Server 端再次驗證權限。

### 12.1 必須執行的雙層驗證

```
Layer 1（前端 UX）：home.html 不顯示無權限的模組入口，detail.html 不顯示無權限的按鈕
Layer 2（後端 API）：每個寫入 Action 呼叫 CanExecuteAsync() → 無權限回 403 Forbid
```

### 12.2 標準 API Action 寫法

```csharp
[HttpPost("{id}/complete")]
[Authorize]
public async Task<IActionResult> Complete(string id)
{
    // ✅ Server 端二次驗證，不依賴前端是否隱藏按鈕
    var roleId = _userContext.ActiveRoleId;
    if (!await _mobilePermissionService.CanExecuteAsync(roleId, "WorkOrderExecution", "Complete"))
        return Forbid();  // 403，前端顯示「權限不足」Toast

    // 業務邏輯...
    await _auditLogService.LogAsync(...);  // ✅ 寫 AuditLog
    return Ok();
}
```

### 12.3 唯讀端點處理

查詢類端點（GET）只需 `[Authorize]`，不需 `CanExecuteAsync()`，
但回傳資料必須以角色過濾（僅回傳該角色有 `View` 權限的資料）：

```csharp
[HttpGet]
[Authorize]
public async Task<IActionResult> GetModules()
{
    // ✅ 依角色過濾，不回傳無權限的模組
    var modules = await _mobilePermissionService.GetAllowedModulesAsync(_userContext.ActiveRoleId);
    return Ok(modules);
}
```

---

## 13. PWA 架構詳細設計

### 13.1 PWA 升級成本評估

在 Phase 1 靜態 HTML 基礎上，Phase 3 升級 PWA 只需新增：

| 新增項目 | 說明 | 工時估計 |
|---------|------|---------|
| `manifest.json` | App 名稱、圖示、主題色、顯示模式 | 0.5h |
| `service-worker.js` | 快取策略（靜態 Cache First、API Network First） | 4h |
| App 圖示（5種尺寸） | 可用 favicon 工具批次產生 | 0.5h |
| Service Worker 註冊 | 在 `auth.js` 加 2 行 | 0.5h |
| **合計** | | **約 0.5 個工作天** |

### 13.2 manifest.json

```json
{
  "name": "SmartFactory MES",
  "short_name": "MES",
  "start_url": "/mobile/home.html",
  "display": "standalone",
  "background_color": "#ffffff",
  "theme_color": "#0d6efd",
  "icons": [
    { "src": "/mobile/icons/icon-192.png", "sizes": "192x192", "type": "image/png" },
    { "src": "/mobile/icons/icon-512.png", "sizes": "512x512", "type": "image/png" }
  ]
}
```

### 13.3 service-worker.js 快取策略

```javascript
const CACHE_NAME = 'mes-mobile-v1';
const STATIC_ASSETS = [
  '/mobile/index.html', '/mobile/home.html',
  '/mobile/css/mobile.css',
  '/mobile/js/api.js', '/mobile/js/auth.js', '/mobile/js/scanner.js'
];

// 安裝：預快取靜態資源
self.addEventListener('install', e => e.waitUntil(
    caches.open(CACHE_NAME).then(c => c.addAll(STATIC_ASSETS))
));

// 請求攔截策略
self.addEventListener('fetch', event => {
    const url = new URL(event.request.url);

    if (url.pathname.startsWith('/api/')) {
        // API → Network First（優先取最新，失敗則回快取）
        event.respondWith(networkFirst(event.request));
    } else {
        // 靜態資源 → Cache First
        event.respondWith(cacheFirst(event.request));
    }
});
```

### 13.4 PWA 與 iOS Safari 限制對照

| 功能 | Android Chrome | iOS Safari 16.4+ | iOS Safari < 16.4 |
|------|---------------|-----------------|-------------------|
| 加入主畫面 | ✅ 自動提示 | ✅ 手動加入 | ✅ 手動加入 |
| 全螢幕啟動 | ✅ | ✅ | ✅ |
| Service Worker | ✅ | ✅ | ✅ |
| Background Sync | ✅ | ❌ | ❌ |
| Push 通知 | ✅ | ✅ 16.4+ | ❌ |
| IndexedDB | ✅ | ✅ | ✅ |

> iOS 不支援 Background Sync，Phase 4 離線同步改用「App 重新開啟時自動觸發同步」策略。

---

## 14. License 控管對手機版的有效性

### 14.1 現行 License 機制（已實作）

| 機制 | 實作位置 | 說明 |
|------|---------|------|
| 啟動驗證 | `Program.cs` ~L235 | RSA 簽章 → 有效期 → 環境指紋，失敗直接 Exit(1) |
| 24h Watchdog | `LicenseWatchdogService` | 每日重新驗證，失敗標記 `/health` Unhealthy |
| 健康檢查 | `/health` endpoint | 可整合 Container orchestrator 自動重啟 |

### 14.2 對手機版的控管有效性評估

**有效的部分 ✅**

| 保護面向 | 說明 |
|---------|------|
| 整體應用程式啟動 | License 無效 → 整個 Server 拒絕啟動，手機 API 全部失效 |
| 到期後重啟防護 | License 到期後，服務重啟會失敗，手機端無法連線新 API |
| 環境指紋綁定 | License 綁定特定主機，換機部署無效，手機端無法繞過 |
| Security Anchor DLL | Phase 2 的 DLL 保護涵蓋整個後端，包含手機 API |

**需注意的邊界 ⚠️**

| 情境 | 說明 | 對策 |
|------|------|------|
| Watchdog 只標記 Unhealthy，不強制停止服務 | License 到期後服務仍繼續運行，直到下次重啟 | 在 Container 部署中設定 `/health` Unhealthy 時自動重啟 |
| PWA 靜態快取 | Service Worker 快取的 HTML/JS 在 Server 停止後仍可開啟頁面（但 API 呼叫會失敗） | 手機操作依賴 API，快取頁面僅顯示 UI 殼層，無法執行實際作業 |
| 無手機版專屬 License 條款 | 目前 License 不區分功能模組，所有模組同一授權 | 若未來需按模組收費，需在 `LicenseContext` 新增 `AllowedModules` 欄位 |

### 14.3 結論

> **現行 License 機制對手機版的保護等同桌面版**，因為兩者共用同一個 ASP.NET Core 應用程式與 Security Anchor DLL。
> 手機前端的靜態 HTML 本身不含業務邏輯，所有作業依賴後端 API，API 受 License 保護。
>
> 唯一需要額外確認的是：**Container 部署中 `/health` Unhealthy 時必須配置自動重啟策略**，確保 Watchdog 的 Unhealthy 狀態能在合理時間內中斷服務。

---

## 15. 與現有系統整合點

| 整合項目 | 做法 |
|---------|------|
| 使用者認證 | 共用 ASP.NET Core Identity，不需重建 |
| 多角色切換 | 手機版使用 `IUserContext.ActiveRoleId` |
| 多廠別 | 共用 `IUserContext.ActivePlantId` 過濾資料 |
| 多語系 | API 回傳語系由 `Accept-Language` Header 決定 |
| 稽核日誌 | 手機作業寫入同一 `AuditLog` 表（見第 10 節） |
| 審批流程 | 手機版可作為 `IApprovalService` 的輕量前端（掃描後觸發簽核） |
| 系統選單 | `MobilePermissionSettings` 整合進 AdminManagement 群組（見第 6 節） |
| License 控管 | 共用 Security Anchor DLL，保護力度與桌面版相同（見第 14 節） |
| Security Anchor | 不影響手機前端，由後端 DLL 處理，手機無感 |
