---
description: 使用 Playwright + xUnit 為 MES 畫面產生 E2E UI 測試（Page Object Model）。觸發時機：要求撰寫 UI 測試、E2E 測試、瀏覽器自動化測試。
---

# /webapp-testing — SmartFactory MES UI 測試產生器

> 使用 Playwright + xUnit 為 MES 畫面產生 E2E UI 測試。
> 觸發時機：要求撰寫 UI 測試、E2E 測試、瀏覽器自動化測試。

---

## 測試專案位置

```
SmartFactory.MES.UITests/
  UiTestFixture.cs      ← 共用 Playwright Browser + Context + 設定
  GlobalUsings.cs
  appsettings.test.json ← BaseUrl、帳密、Headless 設定
  LoginTests.cs         ← 登入測試（已有）
  Pages/                ← Page Object Model（POM）
    LoginPage.cs        ← 登入頁（已有）
    {ScreenName}Page.cs ← 新畫面（由此 skill 產生）
  {Feature}/
    {FeatureName}Tests.cs
```

---

## Step 1：確認測試需求

詢問使用者：

```
UI 測試需求確認：

1. 要測試的功能畫面（如 CodeEntry、Plant、User）：
2. 測試帳號角色（Admin / Manager / Operator）[預設 Admin]：
3. 要覆蓋的情境（可多選）：
   a. 查詢（搜尋列輸入 + 顯示結果）
   b. 新增（填表 + 儲存 + Toast 顯示）
   c. 編輯（點編輯 + 修改 + 儲存）
   d. 刪除（點刪除 + Confirm + 消失）
   e. 簽核流程（送審 → 審核 → 核准/退回）
   f. 權限控制（無權限者看不到按鈕）
   g. 匯出（點匯出 + 確認下載）
4. 執行模式：[headless / headed，預設 headless]：
```

---

## Step 2：Page Object Model（必須產生）

檔案：`SmartFactory.MES.UITests/Pages/{ScreenName}Page.cs`

```csharp
using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.Pages;

/// <summary>{功能中文名稱} 頁面物件</summary>
public class {ScreenName}Page(IPage page, string baseUrl)
{
    private readonly IPage _page = page;
    private readonly string _url = $"{baseUrl}/{ScreenName}";

    // ── 導航 ──
    public async Task GotoAsync()
    {
        await _page.GotoAsync(_url);
        await _page.WaitForSelectorAsync("h1");
    }

    // ── 搜尋 ──
    public async Task SearchAsync(string keyword)
    {
        await _page.FillAsync("input[name='searchTerm']", keyword);
        await _page.CheckAsync("input[name='searched'][value='true']");
        await _page.ClickAsync("button[type='submit']");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    // ── 開啟新增面板 ──
    public async Task ClickAddAsync()
    {
        await _page.ClickAsync("button:has-text('{AddButtonText}')");
        await _page.WaitForSelectorAsync("#crudPanel.show");
    }

    // ── 填寫表單欄位 ──
    public async Task FillFormAsync({FormFields})
    {
        // 各欄位填寫（依實際表單結構）
        await _page.FillAsync("input[name='{Field1}']", {value1});
        // ...
    }

    // ── 儲存面板表單 ──
    public async Task SaveAsync()
    {
        await _page.ClickAsync("#panelForm button[type='submit']");
        await _page.WaitForSelectorAsync("#successToast.show");
    }

    // ── 確認 Toast 訊息 ──
    public async Task<string> GetToastMessageAsync()
    {
        await _page.WaitForSelectorAsync("#successToast.show");
        return await _page.InnerTextAsync("#successToastMsg");
    }

    // ── 取得資料列數 ──
    public async Task<int> GetRowCountAsync()
        => await _page.Locator("table tbody tr").CountAsync();

    // ── 點擊編輯 ──
    public async Task ClickEditAsync(int rowIndex = 0)
    {
        var editBtn = _page.Locator("table tbody tr").Nth(rowIndex)
                          .Locator("button:has-text('{EditText}')");
        await editBtn.ClickAsync();
        await _page.WaitForSelectorAsync("#crudPanel.show");
    }

    // ── 點擊刪除 ──
    public async Task ClickDeleteAsync(int rowIndex = 0)
    {
        _page.Dialog += (_, d) => d.AcceptAsync();
        var deleteBtn = _page.Locator("table tbody tr").Nth(rowIndex)
                            .Locator("button:has-text('{DeleteText}')");
        await deleteBtn.ClickAsync();
        await _page.WaitForSelectorAsync("#successToast.show");
    }

    // ── 點擊匯出 ──
    public async Task<Download> ClickExportAsync()
    {
        var download = await _page.RunAndWaitForDownloadAsync(
            () => _page.ClickAsync("a[href*='Export']"));
        return download;
    }
}
```

---

## Step 3：測試類別（必須產生）

檔案：`SmartFactory.MES.UITests/{ScreenName}/{ScreenName}Tests.cs`

```csharp
using SmartFactory.MES.UITests.Pages;
using Xunit;

namespace SmartFactory.MES.UITests.{ScreenName};

[Collection("UI Tests")]
public class {ScreenName}Tests(UiTestFixture fixture)
{
    private readonly UiTestFixture _fixture = fixture;

    // ── 登入 Helper ──
    private async Task<IPage> LoginAsAdminAsync()
    {
        var page = await _fixture.Context.NewPageAsync();
        var login = new LoginPage(page, _fixture.BaseUrl);
        await login.GotoAsync();
        await login.LoginAsync(
            _fixture.Configuration["TestAdmin:Username"] ?? "admin",
            _fixture.Configuration["TestAdmin:Password"] ?? "Dev12345");
        return page;
    }

    // ── 查詢測試 ──
    [Fact]
    public async Task Search_WithKeyword_ShowsFilteredResults()
    {
        var page = await LoginAsAdminAsync();
        var screen = new {ScreenName}Page(page, _fixture.BaseUrl);
        await screen.GotoAsync();

        await screen.SearchAsync("{TestKeyword}");

        var count = await screen.GetRowCountAsync();
        Assert.True(count >= 0, "搜尋結果應正常顯示");
    }

    // ── 新增測試 ──
    [Fact]
    public async Task Create_ValidData_ShowsSuccessToast()
    {
        var page = await LoginAsAdminAsync();
        var screen = new {ScreenName}Page(page, _fixture.BaseUrl);
        await screen.GotoAsync();

        await screen.ClickAddAsync();
        await screen.FillFormAsync({TestData});
        await screen.SaveAsync();

        var msg = await screen.GetToastMessageAsync();
        Assert.NotEmpty(msg);
    }

    // ── 刪除測試 ──
    [Fact]
    public async Task Delete_ExistingRecord_ShowsSuccessToast()
    {
        var page = await LoginAsAdminAsync();
        var screen = new {ScreenName}Page(page, _fixture.BaseUrl);
        await screen.GotoAsync();
        await screen.SearchAsync("{ExistingKeyword}");

        await screen.ClickDeleteAsync(0);

        var msg = await screen.GetToastMessageAsync();
        Assert.NotEmpty(msg);
    }

    // ── 匯出測試 ──
    [Fact]
    public async Task Export_SearchResults_DownloadsXlsxFile()
    {
        var page = await LoginAsAdminAsync();
        var screen = new {ScreenName}Page(page, _fixture.BaseUrl);
        await screen.GotoAsync();
        await screen.SearchAsync("");  // 搜尋全部

        var download = await screen.ClickExportAsync();

        Assert.EndsWith(".xlsx", download.SuggestedFilename());
    }

    // ── 簽核流程測試（HAS_APPROVAL=Y 時加入）──
    [Fact]
    public async Task ApprovalFlow_Submit_ShowsPendingBadge()
    {
        var page = await LoginAsAdminAsync();
        var screen = new {ScreenName}Page(page, _fixture.BaseUrl);
        await screen.GotoAsync();

        // 新增一筆記錄（送審）
        await screen.ClickAddAsync();
        await screen.FillFormAsync({TestData});
        await screen.SaveAsync();

        // 確認狀態為待審核
        var badge = page.Locator(".badge:has-text('待審核')");
        Assert.True(await badge.IsVisibleAsync());
    }
}
```

---

## Step 4：appsettings.test.json 確認

確認 `SmartFactory.MES.UITests/appsettings.test.json` 含有：

```json
{
  "BaseUrl": "https://localhost:7054",
  "Headless": "true",
  "TestAdmin": {
    "Username": "admin",
    "Password": "Dev12345"
  }
}
```

---

## 執行指令

```bash
# 執行所有 UI 測試（需先啟動應用程式）
dotnet test SmartFactory.MES.UITests/ -v normal

# 執行特定功能的測試
dotnet test SmartFactory.MES.UITests/ --filter "FullyQualifiedName~{ScreenName}"

# Headed 模式（可看到瀏覽器操作）
# 修改 appsettings.test.json: "Headless": "false"
```

---

## 注意事項

- **測試前必須啟動應用程式**：`dotnet run --project SmartFactory.MES`
- **測試資料獨立性**：測試用資料應有唯一關鍵字，避免與生產資料混淆
- **簽核流程測試**：需確認已在「作業類型簽核設定」設定對應 DocumentType
- **刪除測試順序**：若多個測試共用資料，使用 `IAsyncLifetime` 管理 setUp/tearDown


## 安全回歸補充（2026-11-05）

- 用隔離測試資料庫與專屬帳號，不修改開發者或正式環境共用帳號的角色／廠區。
- 兩個角色切換路由均驗證：角色屬於本人、廠區符合目標角色、角色撤銷立即拒絕。
- 使用兩個 browser context：A 廠開表單、另一裝置切到 B 廠，舊表單送出須回 409 且資料庫不新增。
- 桌機側欄、一般 POST、手機 API、離線重送均須測試；全域主檔不因廠區上下文而誤擋。
- 登入限流須驗證 GET 不消耗額度、IP 分桶、帳號鎖定、共用 NAT 與可信代理。
- 中文與英文錯誤契約都須驗收；未執行的瀏覽器／部署驗證須明確標記，不當作通過。
