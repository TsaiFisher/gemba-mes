---
description: 產生 SmartFactory MES 標準背景作業／排程任務（IHostedService 或 Hangfire）。觸發時機：要求定時執行、背景作業、排程任務、輪詢、批次處理。
---

# /new-job — SmartFactory MES 背景作業／排程產生器

> **此 skill 為定時／背景任務（非互動式畫面）量身打造的標準骨架。**
> 觸發時機：使用者描述「每天凌晨 02:00 跑」「每 5 分鐘輪詢」「啟動時批次處理」「待 N 分鐘後重試」等需求。

---

## Step 0：技術選型矩陣

| 情境 | 建議技術 | 理由 |
|------|---------|------|
| 應用啟動時跑一次或在記憶體輪詢 | **`IHostedService` / `BackgroundService`** | .NET 內建、無相依、零安裝 |
| 跨重啟仍要保留排程／失敗重試／儀表板 | **Hangfire** | PostgreSQL 持久化、Dashboard、Recurring Jobs |
| 應用內手動觸發、無重試保證 | `Task.Run` + `IServiceScopeFactory` | 簡單，但 IIS 回收即失效 |
| 跨多副本叢集排程 | Hangfire（含 Redis lock）／K8s CronJob | 應用層或基礎設施擇一，**不要兩邊都跑** |

**預設選 `BackgroundService`**（內建、無相依）。若使用者明確說「跨重啟仍要重試」「需要管理介面」才改用 Hangfire。

---

## Step 1：詢問需求

```
背景作業需求確認：

1. 作業名稱（英文，如 DailyInventoryAggregator、OrderTimeoutChecker）：
2. 中文說明（如 每日庫存彙總、訂單逾時通知）：
3. 觸發方式：
   a. 啟動時跑一次（初始化）
   b. 固定間隔（每 N 分鐘／秒）
   c. Cron 排程（每天 02:00 / 每週一 09:00）
   d. 事件觸發（使用者操作後排入佇列）
4. 是否需要跨重啟保留（重啟後從中斷處繼續）？[Y/N，預設 N]
5. 失敗重試策略：[無 / 立即 1 次 / 指數退避 N 次，預設 立即 1 次]
6. 多廠別資料處理：[依 PlantId 一個個跑 / 一次跑全廠別，預設 依廠別]
7. 多副本叢集時是否需要避免重複執行？[Y/N，預設 N]
```

依答案選技術：
- (4) Y 或 (7) Y → **Hangfire**
- 其餘 → **BackgroundService**

---

## Step 2-A：`BackgroundService` 標準骨架

### 檔案路徑

```
Modules/{MODULE}/Services/Jobs/{JobName}Service.cs   ← 顧問模組
Services/Jobs/{JobName}Service.cs                    ← Core 功能（少用）
```

### 範本

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Services;
using Microsoft.EntityFrameworkCore;

namespace SmartFactory.MES.Modules.{MODULE}.Services.Jobs;

/// <summary>
/// {中文說明}。每 {N} 分鐘執行一次。
/// </summary>
public sealed class {JobName}Service : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<{JobName}Service> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public {JobName}Service(IServiceScopeFactory scopeFactory, ILogger<{JobName}Service> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { /* 關機，忽略 */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{JobName} 執行失敗");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        // BackgroundService 是 Singleton，DbContext 是 Scoped → 每次跑開一個 scope
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dateTimeSvc = scope.ServiceProvider.GetRequiredService<IDateTimeService>();

        // 廠別逐一處理（避免一次掃全部資料造成大 transaction）
        var plantIds = await db.Plants
            .AsNoTracking()
            .Where(p => p.IsActive)
            .Select(p => p.Id)
            .ToListAsync(ct);

        foreach (var plantId in plantIds)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessPlantAsync(db, dateTimeSvc, plantId, ct);
        }
    }

    private async Task ProcessPlantAsync(ApplicationDbContext db,
                                         IDateTimeService dateTimeSvc,
                                         string plantId,
                                         CancellationToken ct)
    {
        // ⚠️ Global Query Filter 是針對 _userContext.PlantId，背景作業無使用者上下文
        //    必須在每個查詢手動加 .Where(x => x.PlantId == plantId)，或用 .IgnoreQueryFilters()
        var pending = await db.Set<YourEntity>()
            .IgnoreQueryFilters()
            .Where(x => x.PlantId == plantId && x.SomeFlag)
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        // ... 業務邏輯 ...

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("{JobName} 廠別 {PlantId} 完成 {Count} 筆", plantId, pending.Count);
    }
}
```

### 註冊（顧問模組）

在 `Modules/{MODULE}/{MODULE}Module.cs` 的 `RegisterServices` 加：

```csharp
public void RegisterServices(IServiceCollection services, IConfiguration configuration)
{
    // ... 其他註冊 ...
    services.AddHostedService<Services.Jobs.{JobName}Service>();
}
```

> ❌ **禁止改 Core 的 `Program.cs`**——一律經 `IAppModule.RegisterServices` 註冊。

---

## Step 2-B：Cron 排程（`BackgroundService` 內手算）

固定間隔用 `Task.Delay(Interval)` 即可；要每天某時刻跑，改算下一個觸發時間：

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        // 每天 02:00（伺服器當地時區，要跨廠別 timezone 請用 IDateTimeService）
        var now = DateTimeOffset.Now;
        var next = now.Date.AddDays(now.Hour >= 2 ? 1 : 0).AddHours(2);
        var delay = next - now;

        try { await Task.Delay(delay, stoppingToken); }
        catch (OperationCanceledException) { break; }

        try { await RunOnceAsync(stoppingToken); }
        catch (Exception ex) { _logger.LogError(ex, "{JobName} 執行失敗"); }
    }
}
```

> 多廠別且各廠時區不同時，迴圈內按 `Plant.DefaultTimeZone` 個別判斷「該廠是否到 02:00」。

---

## Step 2-C：Hangfire（需跨重啟／儀表板）

### 安裝（第一次引入時）

```bash
# 在 SmartFactory.MES.csproj 加：
dotnet add package Hangfire.AspNetCore
dotnet add package Hangfire.PostgreSql
```

> ⚠️ 引入 Hangfire 會建立 `hangfire` schema 的 11 張表，**生產環境上線前需評估**。
> 若只是輪詢用，請優先用 `BackgroundService`。

### 註冊（顧問模組）

```csharp
// Modules/{MODULE}/{MODULE}Module.cs
public void RegisterServices(IServiceCollection services, IConfiguration configuration)
{
    services.AddHangfire(cfg => cfg
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(opt =>
            opt.UseNpgsqlConnection(configuration.GetConnectionString("DefaultConnection"))));

    services.AddHangfireServer();
}

public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    // Hangfire Dashboard 必須加授權，否則匿名可看任務內容
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = new[] { new RoleBasedDashboardAuthFilter() }  // 自寫 filter
    });

    // 啟動時註冊 Recurring Job
    using var scope = app.ApplicationServices.CreateScope();
    var recurring = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    recurring.AddOrUpdate<{JobName}Service>(
        "{JobName}",
        svc => svc.RunOnceAsync(CancellationToken.None),
        "0 2 * * *",  // 每天 02:00 (Cron)
        new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei") });
}
```

### Hangfire Dashboard 授權範本

```csharp
// Modules/{MODULE}/Services/Jobs/RoleBasedDashboardAuthFilter.cs
public class RoleBasedDashboardAuthFilter : Hangfire.Dashboard.IDashboardAuthorizationFilter
{
    public bool Authorize(Hangfire.Dashboard.DashboardContext context)
    {
        var http = context.GetHttpContext();
        return http.User.Identity?.IsAuthenticated == true
            && http.User.IsInRole("Admin");  // 限管理員
    }
}
```

---

## Step 3：與系統服務整合的注意事項

### 3.1 沒有 IUserContext

背景作業**無 HTTP 請求**，注入 `IUserContext` 會拿到空值或例外。處理方式：

```csharp
// ❌ 錯誤：背景作業不能依賴 IUserContext
public {JobName}Service(IUserContext userContext) { }

// ✅ 正確：手動指定廠別、或自建 ISystemContext
var entity = new YourEntity
{
    PlantId = plantId,           // 手動指定
    CreatedBy = "SYSTEM",        // 系統作業以 "SYSTEM" 標記
    CreatedAt = dateTimeSvc.UtcNow
};
```

### 3.2 繞過 Global Query Filter

```csharp
// Global Query Filter 依 _userContext.PlantId，背景作業中 PlantId 為空 → 永遠查不到資料
// 必須用 IgnoreQueryFilters() 並手動加 PlantId 條件
var data = await db.Set<YourEntity>()
    .IgnoreQueryFilters()
    .Where(x => x.PlantId == plantId)
    .ToListAsync(ct);
```

### 3.3 並發控制

同一作業 N 個副本同時跑會踩到對方時：

- **單副本**：`BackgroundService` 預設就是單實例
- **多副本（IIS WebGarden / K8s replicas）**：
  - 用 Hangfire 的 `DisableConcurrentExecution` filter
  - 或自建 `JobLock` 表加 `pg_advisory_xact_lock`

---

## Step 4：Checklist（不得省略）

```
[ ] BackgroundService 繼承自 BackgroundService 或 IHostedService
[ ] 用 IServiceScopeFactory 開 scope 取得 DbContext（不可直接注入 DbContext）
[ ] 注入 ILogger<T> 並在 try/catch 內 LogError，避免一次例外導致整個服務退出
[ ] Task.Delay 永遠傳 stoppingToken，OperationCanceledException 視為正常關機
[ ] 多廠別資料：手動 .IgnoreQueryFilters() + .Where(x => x.PlantId == plantId)
[ ] CreatedBy = "SYSTEM"、CreatedAt = IDateTimeService.UtcNow（不用 DateTime.UtcNow）
[ ] 在 IAppModule.RegisterServices 用 services.AddHostedService<T>() 註冊（❌ 禁改 Program.cs）
[ ] Hangfire Dashboard 必須加授權 filter（否則任何訪客可看 Job 內容）
[ ] 撰寫單元測試：Mock IServiceScopeFactory → 注入 InMemory DbContext，驗證 RunOnceAsync 行為
```

---

## Step 5：測試

### 5.1 單元測試（推薦）

```csharp
// SmartFactory.MES.Tests/Modules/{MODULE}/Jobs/{JobName}ServiceTests.cs
public class {JobName}ServiceTests : TestBase
{
    [Fact]
    public async Task RunOnceAsync_PlantHasPending_ProcessesAll()
    {
        using var db = CreateDbContext();
        db.Plants.Add(new Plant { Id = "P1", IsActive = true });
        db.Set<YourEntity>().Add(new YourEntity { PlantId = "P1", SomeFlag = true });
        await db.SaveChangesAsync();

        var sp = BuildServiceProvider(db);  // TestBase 提供
        var svc = new {JobName}Service(sp.GetRequiredService<IServiceScopeFactory>(),
                                       NullLogger<{JobName}Service>.Instance);

        await InvokePrivateAsync(svc, "RunOnceAsync", CancellationToken.None);

        Assert.False(db.Set<YourEntity>().Single().SomeFlag);
    }
}
```

### 5.2 整合測試（資料庫互動較複雜時）

放 `SmartFactory.MES.IntegrationTests/Modules/{MODULE}/Jobs/`，用 `WebApplicationFactory` 取得 DI 容器內已註冊的 `{JobName}Service`。

---

## 與其他規格的關係

- **多語**：背景作業若需寫稽核日誌或寄信，訊息走 `IStringLocalizer<SharedResource>`，**禁止硬編碼 CJK**
- **時區**：一律用 `IDateTimeService.UtcNow`，要顯示給使用者時用 `.ToLocalTime(plantId)`
- **權限**：背景作業不檢查使用者權限（無使用者上下文）；操作對象的「業務狀態」仍需檢查（如「已撤回的單據不再寄通知」）
- **零衝突模組架構**：服務註冊**只能**在 `IAppModule.RegisterServices`，與 `LOCAL_RULES.md` 的「核心禁止事項」一致

---

> 完整範例：未來實作後可指向 `Modules/{MODULE}/Services/Jobs/` 下的實檔。
