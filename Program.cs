using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using SmartFactory.MES.Middleware;
using Microsoft.AspNetCore.DataProtection;
using Serilog;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// 結構化日誌：使用 Serilog 取代預設 Logger
builder.Host.UseSerilog((ctx, cfg) =>
    cfg.ReadFrom.Configuration(ctx.Configuration));

// ── 連線字串 + 授權上下文解析 ─────────────────────────────────────────────
// Phase 2：優先嘗試從 Security Anchor DLL 解密；否則 fallback 至明文（開發用）
(string, ISecurityContext, object?) resolved;
try
{
    resolved = ResolveConnectionStringAndContext(builder.Configuration, builder.Environment);
}
catch (Exception ex) when (LicenseHostGate.ReasonOf(ex) is not null)
{
    // 主機識別碼閘門（§18.15.9）：授權不屬於這台電腦或讀不到主機識別碼——
    // 不解密連線字串、不連資料庫，只開授權頁（顯示申請碼）
    await LicenseBlockedHost.RunAsync(args, ex);
    return;
}
catch (Exception ex) when (ex is InvalidOperationException or System.Security.SecurityException)
{
    // 設定／授權類啟動錯誤（缺授權檔、指紋不符、未設定加密連線字串…）：訊息本身就是處置說明，
    // 只印訊息、以錯誤碼 1 結束。原本是未處理例外——exit 134 加一整串 stack trace，
    // 真正要看的那幾行被淹在中間（2026-11-09 gemba-mes 裝機測試）。
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
    return;
}
var (connectionString, securityContext, dllSecurityContext) = resolved;

// ── DI 服務注冊 ───────────────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddSingleton<PasswordHistoryInterceptor>(); // L4：無狀態，可 Singleton
// 授權唯讀（§18.8）：資料庫層保證。部署專案的其他 DbContext 也要掛這一個
builder.Services.AddSingleton<LicenseReadOnlyOptions>();
builder.Services.AddSingleton<LicenseReadOnlyInterceptor>();

builder.Services.AddDbContext<ApplicationDbContext>((serviceProvider, options) =>
{
    var auditInterceptor = serviceProvider.GetRequiredService<AuditInterceptor>();
    var passwordHistoryInterceptor = serviceProvider.GetRequiredService<PasswordHistoryInterceptor>();
    options.UseNpgsql(connectionString)
           // 授權唯讀排第一：被擋下的存檔不該先跑驗證或留稽核快照
           .AddInterceptors(serviceProvider.GetRequiredService<LicenseReadOnlyInterceptor>())
           // 模組攔截器（IModuleSaveChangesInterceptor）排最前：驗證類擋下存檔時不先留稽核快照
           .AddInterceptors(serviceProvider.GetServices<IModuleSaveChangesInterceptor>())
           // 順序：先 audit 快照，再由密碼歷史攔截器補寫歷史列（避免歷史列被稽核）
           .AddInterceptors(auditInterceptor, passwordHistoryInterceptor);
});

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentity<ApplicationUser, ApplicationRole>(options => {
    options.SignIn.RequireConfirmedAccount = false;
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 8;
    // L4：特殊字元要求改為可選（Security:RequireSpecialChar，預設 false）。
    // 依 NIST SP 800-63B，強制組成規則效益有限；主要弱密碼防護改由
    // BlocklistPasswordValidator（帳號名/Email/常見弱密碼字典）承擔。
    options.Password.RequireNonAlphanumeric =
        builder.Configuration.GetValue<bool?>("Security:RequireSpecialChar") ?? false;
    options.Password.RequireUppercase = true;
    options.Password.RequireLowercase = true;

    // H3：強化帳號鎖定 — 5 次失敗鎖 30 分鐘（含新建立的使用者）
    options.Lockout.AllowedForNewUsers      = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan  = TimeSpan.FromMinutes(30);
})
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders()
    .AddDefaultUI()
    .AddErrorDescriber<SmartFactory.MES.Services.LocalizedIdentityErrorDescriber>()
    // L4：弱密碼黑名單驗證器（帳號名/Email/常見弱密碼）— 與內建密碼規則疊加
    .AddPasswordValidator<SmartFactory.MES.Services.Security.BlocklistPasswordValidator>()
    // L4：禁止重用最近 N 次密碼（Security:PasswordHistoryCount，預設 5）
    .AddPasswordValidator<SmartFactory.MES.Services.PasswordHistoryValidator>();

builder.Services.Configure<PasswordHasherOptions>(options =>
{
    options.IterationCount = 600000; // 高於預設值，以滿足更嚴格的資安要求
});

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

builder.Services.AddScoped<IUserContext, UserContext>();
builder.Services.AddScoped<IDateTimeService, DateTimeService>();
// 時區清單本身無狀態、建一次就快取，Singleton 即可（見 TimeZoneCatalog 的說明）。
builder.Services.AddSingleton<ITimeZoneCatalog, TimeZoneCatalog>();
builder.Services.AddScoped<IPermissionService, PermissionService>();
// ⚠️ 這一行**必須寫在 ProjectZ 這一側**，即使 Core 的 Program.cs 也有同一行。
// Program.cs 在 .gitattributes 裡是 merge=ours（ProjectZ 有 Cookie 強化／推播註冊／
// Mobile 模組種子等客製），所以合併 Core 時 Core 的版本會被整個丟掉，**不留衝突、不警告**。
// 漏掉的症狀：建置成功、測試全綠，但「使用者設定」頁一開就 500——
// UserSettingsController 建構不出來，而沒有任何測試打得到那一頁。
builder.Services.AddScoped<IPlantSwitchService, PlantSwitchService>();
builder.Services.AddScoped<IRoleSwitchService, RoleSwitchService>();
builder.Services.AddScoped<SmartFactory.MES.Services.IMobilePermissionService, SmartFactory.MES.Services.MobilePermissionService>();
builder.Services.AddScoped<IApprovalService, ApprovalService>();
// 簽核畫面的單據類型名稱；部署專案以 IApprovalDocumentTypeLabelContributor 補自家單據類型
builder.Services.AddScoped<IApprovalDocumentTypeLabels, ApprovalDocumentTypeLabels>();
builder.Services.AddScoped<SmartFactory.MES.Services.IMenuContributor, SmartFactory.MES.Services.CoreMenuContributor>();
builder.Services.AddScoped<SmartFactory.MES.Services.IMenuContributor, SmartFactory.MES.Services.UserSettingsMenuContributor>();
builder.Services.AddScoped<SmartFactory.MES.Services.IMenuService, SmartFactory.MES.Services.MenuService>();
builder.Services.AddScoped<ISystemParamService, SystemParamService>();
builder.Services.AddScoped<IExportService, ExportService>();
builder.Services.AddScoped<ICodeEntryReferenceChecker, DefaultCodeEntryReferenceChecker>();
builder.Services.AddScoped<SmartFactory.MES.Services.IPushNotificationService, SmartFactory.MES.Services.PushNotificationService>();
builder.Services.AddMemoryCache();

// ── 安全修正 M6：In-repo 安全事件告警 ──────────────────────────────────────
// 產生 SECURITY_ALERT 結構化日誌（供集中式日誌/SIEM 擷取），並在設定
// Security:AlertWebhookUrl 時 fire-and-forget POST 通知。信號來源：速率限制器
// （H2）拒絕＝登入暴力破解。Log shipping／fail2ban 屬主機層，不在 in-repo 範圍。
builder.Services.AddHttpClient();
builder.Services.AddSingleton<SmartFactory.MES.Services.Security.ISecurityAlertService,
    SmartFactory.MES.Services.Security.SecurityAlertService>();

// ── 安全修正 M1：DataProtection 金鑰持久化 ─────────────────────────────────
// 預設金鑰存於容器內臨時路徑（~/.aspnet/DataProtection-Keys），容器重建即遺失
// → 既有登入 cookie / antiforgery token 全數失效、全員被登出。
// 設 DATAPROTECTION_KEYS_DIR（生產指向掛載 volume）使金鑰跨容器重建存活；
// SetApplicationName 固定 purpose 前綴，讓多副本水平擴充能共用同一組金鑰。
// 未設定時（本機開發）維持 ASP.NET Core 預設行為，不影響開發流程。
var dpKeysDir = builder.Configuration["DataProtection:KeysDirectory"]
    ?? Environment.GetEnvironmentVariable("DATAPROTECTION_KEYS_DIR");
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("SmartFactory.MES");
if (!string.IsNullOrWhiteSpace(dpKeysDir))
{
    Directory.CreateDirectory(dpKeysDir);
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir));

    // PersistKeysToFileSystem 單獨使用時，金鑰是**明文**落地：key-*.xml 的
    // <descriptor>/<value> 就是主金鑰本體，拿到檔案即可偽造登入 cookie 與
    // antiforgery token。啟動時 XmlKeyManager 會警告 "No XML encryptor configured"。
    //
    // 只在 Windows 上加密，是刻意的：本區塊原本的目標情境是 Linux 容器（金鑰放
    // 掛載 volume 以跨容器重建存活），而 DPAPI 在那裡不存在；即使存在，加密後
    // volume 也會綁死單一主機、失去可攜性。Windows 地端部署沒有這個顧慮。
    //
    // protectToLocalMachine: true（而非 CurrentUser）的理由與機器錨定檔相同
    // （見 Services/MachineAnchor.cs）：以服務帳號執行時不需要載入使用者設定檔，
    // 且管理員重設該帳號密碼不會讓既有金鑰變成無法解開。
    // 代價：金鑰綁該台機器，備份還原到別台會讓既有 session 失效——對地端部署
    // 這是正確的取捨，本來就不該讓憑材跟著備份走。
    if (OperatingSystem.IsWindows())
        dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
}

// License 授權服務：DLL 存在時使用 Phase 2 Adapter，否則 fallback 至 Phase 1 實作
if (dllSecurityContext != null)
{
    // Phase 2：ILicenseValidator 委派至 Security Anchor DLL
    builder.Services.AddSingleton<ILicenseValidator>(
        new LicenseValidatorAdapter(dllSecurityContext, !builder.Environment.IsDevelopment()));
}
else
{
    // Phase 1 fallback（DLL 未載入，開發環境或尚未配置加密連線字串）
    builder.Services.AddSingleton<IEnvironmentFingerprintService, EnvironmentFingerprintService>();
    builder.Services.AddSingleton<ILicenseValidator, LicenseValidator>();
}

// ISecurityContext：Phase 2 DLL 版（SecurityContextAdapter）或 Phase 1 fallback
builder.Services.AddSingleton<ISecurityContext>(securityContext);

// 授權撤銷清單（CRL）：預設關閉，離線廠區不必連網。見 SECURITY-OPERATIONS.md §15
var crlOptions = builder.Configuration.GetSection(LicenseCrlOptions.SectionName)
    .Get<LicenseCrlOptions>() ?? new LicenseCrlOptions();
builder.Services.AddSingleton(crlOptions);
builder.Services.AddSingleton<ILicenseCrlService, LicenseCrlService>();

// 授權狀態（正常／提醒／唯讀／停用／終止解除）。見 SECURITY-OPERATIONS.md §18.8
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ILicenseStateService, LicenseStateService>();
builder.Services.AddScoped<LicenseImportService>();
// 線上租約用戶端（§18.6）：每日續租，非 lease 授權時不動作
builder.Services.AddSingleton(builder.Configuration.GetSection("License").Get<LeaseOptions>() ?? new LeaseOptions());
builder.Services.AddHttpClient<LeaseRenewer>();
builder.Services.AddHostedService<LeaseRenewalService>();
// 終止聲明（§18.9）：每日讀一次公開的 sunset.json
builder.Services.AddHttpClient<LicenseSunsetChecker>();
builder.Services.AddHostedService<LicenseSunsetService>();

// LicenseWatchdog：每 24 小時重新驗證授權（含撤銷清單），失敗時 /health 回傳 Unhealthy
builder.Services.AddHostedService<LicenseWatchdogService>();

// Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "SmartFactory.MES API", Version = "v1" });
});

// 健康檢查（含 License Watchdog 狀態 + DB）
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "database", tags: ["db", "postgresql"])
    .AddCheck<LicenseHealthCheck>("license", tags: ["license"]);

// M3：HSTS — maxAge 1 年 + 涵蓋子網域 + 准許 Preload
builder.Services.AddHsts(options =>
{
    options.Preload           = true;
    options.IncludeSubDomains = true;
    options.MaxAge            = TimeSpan.FromDays(365);
});

// Trust only configured proxy IPs before restoring the request scheme and client address.
builder.Services.AddMesProxyHeaders(builder.Configuration);

// M6：CORS — 預設拒絕所有跨來源。PWA 部署到不同 origin 時，於模組中
// 新增具名 policy 並明確指定允許的 origin / methods / headers。
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.SetIsOriginAllowed(_ => false));
});

// API 端點回傳 401/403 而非重導向至登入頁
builder.Services.ConfigureApplicationCookie(options =>
{
    // M4：Cookie 安全旗標
    options.Cookie.HttpOnly     = true;
    options.Cookie.SameSite     = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

    // M8：Session 逾時。Identity 預設 14 天滑動，對工廠共用終端/平板過長。
    // 改 8 小時滑動（涵蓋一個班別）；逾時後回登入頁。可經設定
    // Security:SessionHours 覆寫（現場行為確認後調整）；PWA 若需長 session
    // 另立 remember-me 策略，不放寬本值。
    var sessionHours = builder.Configuration.GetValue<int?>("Security:SessionHours") ?? 8;
    options.ExpireTimeSpan    = TimeSpan.FromHours(sessionHours > 0 ? sessionHours : 8);
    options.SlidingExpiration = true;

    options.Events.OnRedirectToLogin = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
    // 登入成功（密碼／兩步驟驗證／復原碼三條路徑共同的終點）寫入語系 cookie：個人 → 廠別預設 → zh-TW
    options.Events.OnSignedIn = SmartFactory.MES.Services.SignInLanguage.OnSignedInAsync;
    // 權限不足一律直接回 403，不導向 AccessDeniedPath（同 Core）：
    // 預設的 /Identity/Account/AccessDenied 是 Identity DefaultUI 內建頁，只有英文，
    // 中文介面也照樣顯示「Access denied」（2026-11-11 筆電驗收 A5）。
    // 頁面請求的 403 由 UseStatusCodePagesWithReExecute 接到 /Error/403——中英雙語、手機路徑走手機版面。
    options.Events.OnRedirectToAccessDenied = ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});

// Credential POSTs share the Core policy; ordinary Razor page views consume no permits.
builder.Services.AddMesLoginRateLimiting(builder.Configuration, builder.Environment);

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AuthenticatedUser", policy => policy.RequireAuthenticatedUser());
});

// 跨廠別寫入守門：畫面在 A 廠渲染、送出時人已經在 B 廠 → 擋下並要求重新整理。
// 為什麼需要：ApplyGlobalValues 在 SaveChanges 當下才填 PlantId ??= CurrentPlantId，
// 取的是**送出當下**的廠別。手機端可以切廠之後，「電腦開著 A 廠表單 →
// 手機切到 B → 回電腦存檔」會把單據靜靜建到 B 廠。詳見 StalePlantContextFilter。
builder.Services.AddScoped<SmartFactory.MES.Filters.StalePlantContextFilter>();

builder.Services.AddControllersWithViews(options =>
    {
        options.Filters.AddService<SmartFactory.MES.Filters.StalePlantContextFilter>();
    })
    .AddViewLocalization()
    .AddDataAnnotationsLocalization();
builder.Services.AddRazorPages()
    .AddViewLocalization()
    .AddDataAnnotationsLocalization();

// 驗證訊息在地化（[Required]/[MaxLength]/隱含必填 + {0} 欄位顯示名隨語系）。
// 根設定置於 Core，所有部署專案共用；顧問模組不需各自註冊。
builder.Services.AddLocalizedValidation();

// ── 模組自動掃描（IAppModule）──────────────────────────────────────────────
// 自動掃描所有已載入組件中實作 IAppModule 的類別，無需修改此檔案即可擴充服務
var appModules = AppDomain.CurrentDomain.GetAssemblies()
    .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
    .Where(t => typeof(SmartFactory.MES.Services.IAppModule).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
    .Select(t => (SmartFactory.MES.Services.IAppModule)Activator.CreateInstance(t)!)
    .ToList();
foreach (var module in appModules)
    module.RegisterServices(builder.Services, builder.Configuration);

var app = builder.Build();

foreach (var module in appModules)
    module.Configure(app);

// H1：反向代理標頭必須在 HTTPS 重導/驗證之前套用，使後續管線看到正確的 scheme 與 client IP。
app.UseForwardedHeaders();

// L2（security-assessment-and-plan.md）：生產環境未處理例外導向友善錯誤頁（不洩漏堆疊）。
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error/500");
}

app.UseStatusCodePagesWithReExecute("/Error/{0}");

app.UseHttpsRedirection();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// H2：CSP nonce — 每請求產生隨機值，存入 HttpContext.Items["CspNonce"]。
//     View 以 @(Context.Items["CspNonce"] as string) 取值加在 <script nonce="..."> 上。
//     待所有 inline <script> 補完 nonce 且所有 inline 事件處理器改為 addEventListener 後，
//     將下方 CSP 的 script-src 改為：'self' 'nonce-{nonce}' 並刪除 'unsafe-inline'。
app.UseCspNonce();

// H1：安全標頭 — 在 StaticFiles 之前套用，所有回應都會帶
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"]        = "SAMEORIGIN";
    headers["Referrer-Policy"]        = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"]     = "camera=(self), microphone=(), geolocation=()";
    // CSP：'unsafe-eval' 與 'unsafe-inline' 皆已移除。
    //      script-src 改用 per-request nonce：所有 inline <script> 帶
    //      nonce="@(Context.Items[CspNonceMiddleware.ItemKey] as string)"（由 UseCspNonce 產生），
    //      880 個 inline 事件處理器已全數改為 addEventListener / data-action 委派，
    //      靜態 wwwroot/mobile/*.html 的 inline script 亦已外部化為 *.page.js，
    //      外部 CDN（chart.js / html5-qrcode）以 nonce 授權。
    //      style-src 亦已移除 'unsafe-inline'：所有 inline style="…" 屬性外部化為 utility class
    //      （wwwroot/css/csp-utilities.css），inline <style> 區塊移至 CSS 檔，動態寬度改 CSSOM
    //      .style.width= 套用（不受 style-src 管制）。CSP 的 nonce/hash 無法套用於 style 屬性，
    //      故唯一徹底之道為全面外部化。
    var nonce = context.Items[SmartFactory.MES.Middleware.CspNonceMiddleware.ItemKey] as string;
    headers["Content-Security-Policy"] =
        "default-src 'self'; "                                  +
        $"script-src  'self' 'nonce-{nonce}'; "                 +
        "style-src   'self'; "                                  +
        "img-src     'self' data: blob:; "                      +
        "font-src    'self' data:; "                            +
        "connect-src 'self'; "                                  +
        "frame-ancestors 'self'; "                              +
        "base-uri    'self'; "                                  +
        "object-src  'none'";
    await next();
});

app.UseStaticFiles();

// 健康檢查端點
// L3：收斂匿名外洩 — 探針（負載平衡/k8s）只需總體 status；
//     per-check 名稱/描述（DB 連線錯誤、license LastError 等內部訊息）僅對已登入者揭露。
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var detailed = context.User.Identity?.IsAuthenticated ?? false;
        string result = detailed
            ? System.Text.Json.JsonSerializer.Serialize(new
            {
                status = report.Status.ToString(),
                checks = report.Entries.Select(e => new
                {
                    name        = e.Key,
                    status      = e.Value.Status.ToString(),
                    description = e.Value.Description
                })
            })
            : System.Text.Json.JsonSerializer.Serialize(new
            {
                status = report.Status.ToString()
            });
        await context.Response.WriteAsync(result);
    }
});

var supportedCultures = new[] { "zh-TW", "en-US" };
var localizationOptions = new RequestLocalizationOptions()
    .SetDefaultCulture("zh-TW")
    .AddSupportedCultures(supportedCultures)
    .AddSupportedUICultures(supportedCultures);
localizationOptions.RequestCultureProviders.Insert(0,
    new Microsoft.AspNetCore.Localization.CookieRequestCultureProvider());
app.UseRequestLocalization(localizationOptions);

// ── 安全修正 M4：停用未使用的 Identity DefaultUI 自助頁 ─────────────────────
// 本系統帳號管理走自建 AccountController（/Account/ForceChangePassword、/switch-role）
// 與自建 Login/Logout；AddDefaultUI() 另掛出一整組自助頁（自助註冊、Email 找回密碼、
// 個資下載/自助刪除、2FA 等）——皆未接線、且無 Email 寄送設定，屬多餘攻擊面。
// 對這些殘留路徑一律回 404（不洩漏存在）。保留 Login/Logout/AccessDenied/Lockout。
string[] disabledIdentityPages =
{
    "/Identity/Account/Register",
    "/Identity/Account/RegisterConfirmation",
    "/Identity/Account/ForgotPassword",
    "/Identity/Account/ForgotPasswordConfirmation",
    "/Identity/Account/ResetPassword",
    "/Identity/Account/ResetPasswordConfirmation",
    "/Identity/Account/ResendEmailConfirmation",
    "/Identity/Account/ConfirmEmail",
    "/Identity/Account/ConfirmEmailChange",
    "/Identity/Account/ExternalLogin",
    "/Identity/Account/Manage",   // 整組帳號管理頁（含 PersonalData / DeletePersonalData / 2FA）
};
app.Use(async (context, next) =>
{
    foreach (var blocked in disabledIdentityPages)
    {
        if (context.Request.Path.StartsWithSegments(blocked, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
    }
    await next();
});

app.UseRouting();
// M6：啟用 CORS middleware（預設 policy 已設為拒絕所有跨來源）
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
// 授權唯讀／停用（§18.8）：排在強制改密之前，停用時直接導向授權頁
app.UseLicenseReadOnly();
app.UseForcePasswordChange();
// M3：先強制改密、再強制 2FA（被強制角色未啟用即擋，導向 /TwoFactor/Setup）。
app.UseRequireTwoFactorForAdmin();
app.UseUserPreference();

// L2：Swagger 移至 auth 後並要求登入 — Dev 環境意外暴露時仍須認證
if (app.Environment.IsDevelopment())
{
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/swagger")
            && !(ctx.User.Identity?.IsAuthenticated ?? false))
        {
            ctx.Response.Redirect("/Identity/Account/Login?ReturnUrl="
                + Uri.EscapeDataString(ctx.Request.Path + ctx.Request.QueryString));
            return;
        }
        await next();
    });
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "SmartFactory.MES API v1"));
}

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapGet("/mobile", context => {
    context.Response.Redirect("/mobile/index.html");
    return Task.CompletedTask;
}).AllowAnonymous();
// 手機模組目錄（/mobile/mfg、/mobile/inv）：目錄本身沒有 index 檔，未攔截時會落到
// MVC 的 default route 顯示「電腦版首頁」。一律導向該模組的手機列表頁。
foreach (var (segment, target) in new[]
         {
             ("mfg", "/mobile/mfg/list.html"),
             ("inv", "/mobile/inv/list.html"),
         })
{
    var landing = target;
    app.MapGet($"/mobile/{segment}", context => {
        context.Response.Redirect(landing);
        return Task.CompletedTask;
    }).AllowAnonymous();
}
app.MapGet("/login", context => {
    context.Response.Redirect("/Identity/Account/Login");
    return Task.CompletedTask;
}).AllowAnonymous();
app.MapGet("/Identity/Account/Register", context => {
    context.Response.Redirect("/Identity/Account/Login");
    return Task.CompletedTask;
}).AllowAnonymous();
app.MapRazorPages().RequireRateLimiting("loginRateLimit");

// ── 自動套用 Pending Migrations ───────────────────────────────────────────
// 生產環境建議設 "Database:AutoMigrate": false，改由部署流程手動執行：
//   1. scripts/backup-db.sh（先備份）
//   2. dotnet ef database update（或產出 SQL script 審閱後執行）
// 未設定時預設 true（維持既有行為，開發/測試環境零組態）。
if (builder.Configuration.GetValue("Database:AutoMigrate", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    // EF 9 起 Migrate() 即使無事可做也會先送 CREATE TABLE IF NOT EXISTS／LOCK（寫入指令）。
    // 正式環境的授權驗證在啟動流程中排在這之後（此時狀態是「尚未驗證、不擋」），所以目前不會被擋；但只要這裡執行時
    // 授權已是唯讀（測試替身、或日後調整啟動順序），LicenseReadOnlyInterceptor 就會擋下、app 起不來。
    // 先以唯讀查詢判斷，維持 .NET 8「沒有待套用的 migration 就不寫」的行為。
    if (db.Database.GetPendingMigrations().Any())
        db.Database.Migrate();
}

// C1 + L4：開發用 admin 帳號 — 僅在 Development 環境建立
//   正式部署請改用獨立的 admin seeding 腳本或環境變數，避免「已知帳密」風險。
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

    // 前置種子：admin 的 ActivePlantId / ActiveRoleId 皆有外鍵。乾淨 DB（CI、
    // 全新 dev 機）尚無 Plant "P1" / Role "1"，直接建 admin 會違反 FK 而啟動失敗。
    // 冪等補種（不存在才建）；已有資料的環境為 no-op，行為不變。
    var seedDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    if (!await seedDb.Plants.AnyAsync(p => p.Id == "P1"))
    {
        seedDb.Plants.Add(new Plant
        {
            Id = "P1",
            Name = "預設廠",
            DefaultLanguage = "zh-TW",
            DefaultTimeZone = "Asia/Taipei"
        });
        await seedDb.SaveChangesAsync();
    }
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
    if (await roleManager.FindByIdAsync("1") == null)
        await roleManager.CreateAsync(new ApplicationRole { Id = "1", Name = "Admin" });

    var devUserName = "admin";
    var devUser = await userManager.FindByNameAsync(devUserName);
    if (devUser == null)
    {
        devUser = new ApplicationUser
        {
            UserName           = devUserName,
            Email              = "admin@localhost",
            EmailConfirmed     = true,
            ActivePlantId      = "P1",
            Language           = "zh-TW",
            TimeZoneId         = "Asia/Taipei",
            ActiveRoleId       = "1",
            MustChangePassword = true
        };
        var result = await userManager.CreateAsync(devUser, "Dev12345");
        if (result.Succeeded)
            await userManager.AddToRoleAsync(devUser, "Admin");
    }
}

// ── 角色權限 seed（上線前補授權；冪等、全環境）─────────────────────────────
//   乾淨部署自動建立 8 個角色並依角色矩陣種選單權限；角色已有權限則略過（不覆蓋手動設定）。
//   生產若以外部流程管理角色授權，可設 "Security:SeedRolePermissions": false 關閉。
if (app.Configuration.GetValue("Security:SeedRolePermissions", true))
{
    using var scope = app.Services.CreateScope();
    await SmartFactory.MES.Services.RolePermissionSeeder.SeedAsync(scope.ServiceProvider);
}

// ── 待驗倉 seed（收貨品質隔離；冪等、全環境）───────────────────────────────
//   乾淨部署/既有部署升級時，自動為尚無待驗倉的廠別各建一座，收貨上架閘門才有地方可用。
if (app.Configuration.GetValue("Security:SeedQuarantineWarehouse", true))
{
    using var scope = app.Services.CreateScope();
    await SmartFactory.MES.Services.QuarantineWarehouseSeeder.SeedAsync(scope.ServiceProvider);
}

// ── 會計期間 seed（每次啟動補齊當月起算一年份的缺口；冪等、全環境）─────────
//   期間表原本沒有任何 seeder，空的期間表會讓 ACT/FIN/CST 在有人手動產生期間前不能用。
//   原本的條件是「僅在期間表完全為空時建立」，那等於讓它永遠不執行——只要有任何一筆
//   期間先落地（測試前置或手動建過一期），seeder 就從此短路，當期永遠不會出現。
//   改為只補缺的期別：既有期間一律不修改，也不回填當月之前的缺口（不與期間治理打架）。
if (app.Configuration.GetValue("Security:SeedAccountingPeriods", true))
{
    using var scope = app.Services.CreateScope();
    await SmartFactory.MES.Services.AccountingPeriodSeeder.SeedAsync(scope.ServiceProvider);
}

// ── 手機模組初始種子資料（若尚未建立） ─────────────────────────────────────
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    // 每次啟動完整 upsert 模組定義，確保 ModuleKey / DisplayName / IsActive 與代碼一致。
    // IsActive = false 的模組不顯示在首頁（home.html），待對應 HTML 頁面建立後再啟用。
    var seedModules = new[]
    {
        new SmartFactory.MES.Models.MobileModule { Id = "MOD-QI", ModuleKey = "QualityInspection", DisplayName = "品質檢驗",  IconClass = "bi-check2-circle",       SortOrder = 1, IsActive = false },
        new SmartFactory.MES.Models.MobileModule { Id = "MOD-MP", ModuleKey = "MaterialPicking",   DisplayName = "備料領料",  IconClass = "bi-box-seam",            SortOrder = 2, IsActive = false },
        new SmartFactory.MES.Models.MobileModule { Id = "MOD-MF", ModuleKey = "Mfg",               DisplayName = "投產報工",  IconClass = "bi-gear-wide-connected", SortOrder = 3, IsActive = true  },
        new SmartFactory.MES.Models.MobileModule { Id = "MOD-IV", ModuleKey = "Inv",               DisplayName = "倉儲作業",  IconClass = "bi-boxes",               SortOrder = 4, IsActive = true  },
    };
    var seedActions = new SmartFactory.MES.Models.MobileModuleAction[]
    {
        // 品質檢驗
        new() { Id = "ACT-QI-INSPECT", ModuleId = "MOD-QI", ActionKey = "Inspect", DisplayName = "執行檢驗",   SortOrder = 1 },
        new() { Id = "ACT-QI-PASS",    ModuleId = "MOD-QI", ActionKey = "Pass",    DisplayName = "判定合格",   SortOrder = 2 },
        new() { Id = "ACT-QI-FAIL",    ModuleId = "MOD-QI", ActionKey = "Fail",    DisplayName = "判定不合格", SortOrder = 3 },
        // 備料領料
        new() { Id = "ACT-MP-PICK",    ModuleId = "MOD-MP", ActionKey = "Pick",    DisplayName = "領料",       SortOrder = 1 },
        new() { Id = "ACT-MP-CONFIRM", ModuleId = "MOD-MP", ActionKey = "Confirm", DisplayName = "確認領料",   SortOrder = 2 },
        new() { Id = "ACT-MP-RETURN",  ModuleId = "MOD-MP", ActionKey = "Return",  DisplayName = "退料",       SortOrder = 3 },
        // 投產報工（MFG — mobile-pwa.md §9 Phase E1）
        new() { Id = "ACT-MF-SETUP",   ModuleId = "MOD-MF", ActionKey = "Setup",         DisplayName = "備料開工準備", SortOrder = 1 },
        new() { Id = "ACT-MF-START",   ModuleId = "MOD-MF", ActionKey = "StartWork",     DisplayName = "開工上工",     SortOrder = 2 },
        new() { Id = "ACT-MF-STOP",    ModuleId = "MOD-MF", ActionKey = "StopWork",      DisplayName = "下工",         SortOrder = 3 },
        new() { Id = "ACT-MF-REPORT",  ModuleId = "MOD-MF", ActionKey = "Report",        DisplayName = "報工",         SortOrder = 4 },
        new() { Id = "ACT-MF-MACHINE", ModuleId = "MOD-MF", ActionKey = "MachineStatus", DisplayName = "機台狀態異動", SortOrder = 5 },
        new() { Id = "ACT-MF-INSPECT", ModuleId = "MOD-MF", ActionKey = "Inspect",       DisplayName = "檢驗（FPI/IPQC）", SortOrder = 6 },
        new() { Id = "ACT-MF-SCHED",   ModuleId = "MOD-MF", ActionKey = "ScheduleView",  DisplayName = "生產排程查看（含排程查詢／準備看板）", SortOrder = 7 },
        new() { Id = "ACT-MF-REQ",     ModuleId = "MOD-MF", ActionKey = "Requisition",   DisplayName = "領料／退料申請", SortOrder = 8 },
        // 2026-10-12 超量核准＝「主管」（IOverQuantityPolicy）：超過廠內超量容許上限仍可報工。桌機報工也看這一格。
        new() { Id = "ACT-MF-OVERQTY", ModuleId = "MOD-MF", ActionKey = "OverReportApprove", DisplayName = "超量核准（超過容許上限仍可報工，桌機同）", SortOrder = 9 },
        // 倉儲作業（INV — mobile-pwa.md §7.2 Phase E4）
        new() { Id = "ACT-IV-PUTAWAY", ModuleId = "MOD-IV", ActionKey = "PutAway",    DisplayName = "入庫上架",   SortOrder = 1 },
        new() { Id = "ACT-IV-PICK",    ModuleId = "MOD-IV", ActionKey = "Pick",       DisplayName = "出庫下架",   SortOrder = 2 },
        new() { Id = "ACT-IV-STOCK",   ModuleId = "MOD-IV", ActionKey = "StockQuery", DisplayName = "庫存查詢",   SortOrder = 3 },
        new() { Id = "ACT-IV-IQC",     ModuleId = "MOD-IV", ActionKey = "Iqc",        DisplayName = "進料檢驗（IQC/FQC/RQC）", SortOrder = 4 },
        new() { Id = "ACT-IV-COUNT",   ModuleId = "MOD-IV", ActionKey = "Count",      DisplayName = "庫存盤點",   SortOrder = 5 },
        // 2026-10-05：工單發退料（倉庫揀料過帳）、備料單、委外收料（Api/Mobile/MobileMaterialController）
        new() { Id = "ACT-IV-MATSLIP", ModuleId = "MOD-IV", ActionKey = "MaterialIssue",    DisplayName = "工單發退料", SortOrder = 6 },
        new() { Id = "ACT-IV-STAGING", ModuleId = "MOD-IV", ActionKey = "Staging",          DisplayName = "備料作業",   SortOrder = 7 },
        new() { Id = "ACT-IV-OSRECV",  ModuleId = "MOD-IV", ActionKey = "OutsourceReceive", DisplayName = "委外收料",   SortOrder = 8 },
        new() { Id = "ACT-IV-OVERQTY", ModuleId = "MOD-IV", ActionKey = "OverReceiveApprove", DisplayName = "超量核准（委外超過容許上限仍可收貨，桌機同）", SortOrder = 9 },
    };

    // Upsert modules（更新 ModuleKey / DisplayName / IsActive，確保與代碼定義一致）
    var existingMods = db.MobileModules.ToList();
    var existingActIds = db.MobileModuleActions.Select(a => a.Id).ToHashSet();
    bool changed = false;
    foreach (var seed in seedModules)
    {
        var existing = existingMods.FirstOrDefault(m => m.Id == seed.Id);
        if (existing == null)
        {
            db.MobileModules.Add(seed);
            changed = true;
        }
        else if (existing.ModuleKey != seed.ModuleKey || existing.DisplayName != seed.DisplayName
                 || existing.IsActive != seed.IsActive || existing.SortOrder != seed.SortOrder)
        {
            existing.ModuleKey   = seed.ModuleKey;
            existing.DisplayName = seed.DisplayName;
            existing.IconClass   = seed.IconClass;
            existing.IsActive    = seed.IsActive;
            existing.SortOrder   = seed.SortOrder;
            changed = true;
        }
    }
    var newActs = seedActions.Where(a => !existingActIds.Contains(a.Id)).ToList();
    if (newActs.Count > 0) { db.MobileModuleActions.AddRange(newActs); changed = true; }

    // 新作業鍵回補（既有部署升級用）：
    //   新增作業鍵時，既有角色在 MobileRolePermissions 沒有對應列 → 一律視為未授權，
    //   會讓原本可用的入口在升級後整批消失。故對「該模組已有任一 IsAllowed=true」的角色
    //   自動補上新鍵並設為允許；已存在的列（含被刻意設為 false 者）一律不動。
    // ⚠️ 超量核准是「主管」權限，不回補：回補會讓所有有報工／收料權限的角色一升級就變主管。由管理者明確授予。
    var noBackfill = new HashSet<string> { "ACT-MF-OVERQTY", "ACT-IV-OVERQTY" };
    if (newActs.Any(a => !noBackfill.Contains(a.Id)))
    {
        var existingPerms = db.MobileRolePermissions
            .Select(p => new { p.RoleId, p.ModuleActionId, p.IsAllowed, p.ModuleAction.ModuleId })
            .ToList();

        foreach (var grp in newActs.Where(a => !noBackfill.Contains(a.Id)).GroupBy(a => a.ModuleId))
        {
            var rolesWithModule = existingPerms
                .Where(p => p.ModuleId == grp.Key && p.IsAllowed)
                .Select(p => p.RoleId)
                .Distinct()
                .ToList();

            foreach (var act in grp)
            foreach (var roleId in rolesWithModule)
            {
                if (existingPerms.Any(p => p.RoleId == roleId && p.ModuleActionId == act.Id)) continue;
                db.MobileRolePermissions.Add(new SmartFactory.MES.Models.MobileRolePermission
                {
                    RoleId = roleId,
                    ModuleActionId = act.Id,
                    IsAllowed = true
                });
                changed = true;
            }
        }
    }

    // 廢棄舊版 ModuleKey（前次 session 遺留），一律標 IsActive = false
    var deprecatedKeys = new[] { "WorkOrderExecution" };
    var deprecated = existingMods
        .Where(m => deprecatedKeys.Contains(m.ModuleKey) && m.IsActive)
        .ToList();
    foreach (var m in deprecated) { m.IsActive = false; changed = true; }

    if (changed) await db.SaveChangesAsync();
}

// ── 啟動時授權驗證 ────────────────────────────────────────────────────────
// 規則：任何環境都需要有效的 license.json，驗證失敗則拒絕啟動。
//   唯一例外是已驗簽的終止聲明 sunset.json（§18.9），有它就永久免授權。
//   - 核心開發者：使用 Internal License（clientCode: SMARTFACTORY-INTERNAL）
//     dotnet run --project SmartFactory.MES.LicenseAuthority -- issue --internal --private-key license-private.pem
//   - 顧問：使用企業 License（由 SmartFactory 授權中心核發）
if (LicenseSunset.IsActive(app.Configuration))
{
    // 終止聲明：永久解除，不讀授權檔（新安裝也免授權）
    app.Services.GetRequiredService<ILicenseStateService>().SetSunset();
    Log.Warning("授權服務已終止（sunset.json 驗簽通過），授權控管已永久解除");
}
else
{
    using var licScope = app.Services.CreateScope();
    var licCtx = licScope.ServiceProvider.GetRequiredService<ILicenseValidator>().Validate();
    // 若驗證失敗，拋出例外 → 應用程式終止（不會到達 app.Run()）。
    // 到期不在其列：轉唯讀、再 30 天停用（§18.8）。
    var licState = app.Services.GetRequiredService<ILicenseStateService>();
    licState.SetLicense(licCtx);
    // 系統參數「線上授權確認」（§18.15.8）：只能對 offline 授權加嚴，VM 強制與簽章要求不讀它
    licState.SetOnlineCheckOptIn(await LicenseOnlineCheck.ReadOptInAsync(licScope.ServiceProvider));
    // 線上租約：先讀回已存的 lease.json（重新驗簽）；續租由 LeaseRenewalService 啟動後立即進行。
    // offline 授權也讀——可能在 VM 上被強制、或系統參數已啟用
    if (LeaseRenewer.CanConfirm(licCtx))
        licScope.ServiceProvider.GetRequiredService<LeaseRenewer>().LoadStoredLease();
    if (licState.Current is { IsWriteBlocked: true } blocked)
        Log.Error("⛔ {Reason}", blocked.Reason);

    if (licCtx.IsInternal)
    {
        Log.Warning("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Log.Warning("⚠️  SmartFactory Internal License（開發授權）");
        Log.Warning("   到期：{Expiry:yyyy-MM-dd}", licCtx.ExpiresAt);
        Log.Warning("   此授權僅限 Development 環境，禁止部署至生產環境");
        Log.Warning("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
    }
}

app.Run();
// ── 連線字串 + ISecurityContext 解析函式 ─────────────────────────────────
// 回傳值中 dllCtx 不為 null 代表 Security DLL 已成功載入（Phase 2 模式）。
static (string cs, ISecurityContext ctx, object? dllCtx) ResolveConnectionStringAndContext(
    IConfiguration config, IWebHostEnvironment env)
{
    // 終止聲明（§18.9）：已永久解除控管，不需要授權檔，明文連線字串直接放行。
    // 只認本機已驗簽的 sunset.json；讀不到一律照常驗證（不得自動解鎖）。
    if (LicenseSunset.IsActive(config) && config.GetConnectionString("DefaultConnection") is { Length: > 0 } sunsetCs)
        return (sunsetCs, new FallbackSecurityContext(new LicenseContext
        {
            LicenseId = "SUNSET", ClientCode = "SUNSET", ExpiresAt = DateTimeOffset.MaxValue, AllowPlainConnection = true,
        }), null);

    var encrypted = config.GetConnectionString("DefaultEncrypted");

    if (!string.IsNullOrEmpty(encrypted))
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "Security", "SmartFactory.MES.Security.dll");
        if (!File.Exists(dllPath))
            dllPath = Path.Combine(AppContext.BaseDirectory, "SmartFactory.MES.Security.dll");

        if (File.Exists(dllPath))
        {
            try
            {
                var assembly = System.Reflection.Assembly.LoadFrom(dllPath);

                // 優先使用新版 SecurityAnchor（含進入點組件驗證）
                var anchorType = assembly.GetType("SmartFactory.MES.Security.SecurityAnchor");
                if (anchorType != null)
                {
                    var init = anchorType.GetMethod("Initialize",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
                    var dllCtx = init.Invoke(null, [null])
                        ?? throw new InvalidOperationException("SecurityAnchor.Initialize 回傳 null。");

                    var decryptM = dllCtx.GetType().GetMethod("DecryptConnectionString")!;
                    var cs = (string)(decryptM.Invoke(dllCtx, [encrypted])
                        ?? throw new InvalidOperationException("DecryptConnectionString 回傳 null。"));

                    return (cs, new SecurityContextAdapter(dllCtx), dllCtx);
                }

                // 向後相容：舊版 DLL（只有 ConnectionStringDecryptor）
                var legacyType = assembly.GetType("ConnectionStringDecryptor")
                    ?? throw new InvalidOperationException("Security DLL 格式錯誤：找不到已知類別。");
                var instance = Activator.CreateInstance(legacyType)!;
                var method   = legacyType.GetMethod("Decrypt")!;
                var legacyCs = (string)(method.Invoke(instance, [encrypted, null])
                    ?? throw new InvalidOperationException("Decrypt 回傳 null。"));

                return (legacyCs, new FallbackSecurityContext(new LicenseContext()), null);
            }
            catch (Exception ex) when (ex is not InvalidOperationException
                                          and not System.Security.SecurityException)
            {
                // 反射呼叫會把 DLL 內的例外包進 TargetInvocationException，
                // 不拆開的話第一行永遠只剩「target of an invocation」（見 SecurityDllErrors）
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(SecurityDllErrors.Translate(ex));
            }
        }

        if (!env.IsDevelopment())
            throw new InvalidOperationException(
                "找不到 Security DLL（Security/SmartFactory.MES.Security.dll），" +
                "但 DefaultEncrypted 已設定。\n" +
                "請向您的授權提供者索取此 DLL，或改用明文的 " +
                "ConnectionStrings:DefaultConnection。");
    }

    // Fallback：明文連線字串（Phase 1，未部署 Security DLL）
    // ⚠️  是否放行明文，改由「授權檔的 allowPlainConnection 旗標」決定，
    //   不再由 ASPNETCORE_ENVIRONMENT 決定 —— 後者是部署端完全可控的環境變數，
    //   複製部署的人只要把環境設成 Development 並自備一個空資料庫，
    //   就能整個繞開 DefaultEncrypted 這道密碼學鎖。
    //   allowPlainConnection 位於 RSA 簽章 payload 內，部署端無法自行開啟。
    //   見 SECURITY-OPERATIONS.md §14.3-C。
    var plainCs = config.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "找不到連線字串。請設定 ConnectionStrings:DefaultConnection（開發）" +
            "或 ConnectionStrings:DefaultEncrypted（生產）。");

    // 必須做完整驗證（簽章 + 演算法版本 + 有效期 + 指紋），不可只讀 JSON 欄位：
    // 未經驗簽的 allowPlainConnection 可被任意竄改，等於沒有這道閘門。
    var licCtx = new LicenseValidator(
            new EnvironmentFingerprintService(),
            config,
            NullLogger<LicenseValidator>.Instance,
            env)
        .Validate();

    // 走到這裡＝授權已通過完整驗證，只是還沒設定加密連線字串。
    // 不可再附「如何申請授權」：全新安裝的人照著它重新申請，授權明明是好的（2026-11-09 裝機測試）。
    if (!licCtx.AllowPlainConnection)
        throw new InvalidOperationException(
            $"授權有效（{licCtx.LicenseId}），但尚未設定加密連線字串 ConnectionStrings:DefaultEncrypted，" +
            "而此授權不允許明文連線字串（allowPlainConnection = false）。\n" +
            $"請在本機以授權編號 {licCtx.LicenseId} 執行 tools/EncryptConnectionString 加密連線字串，" +
            "將輸出的 ENC:v3:…（虛擬機／容器為 ENC:v2:…）填入 DefaultEncrypted，並清空 DefaultConnection。\n" +
            "不需要重新申請授權。");

    return (plainCs, new FallbackSecurityContext(licCtx), null);
}
public partial class Program { }
