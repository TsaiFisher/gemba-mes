using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.License;

/// <summary>
/// 授權狀態頁與橫幅在整個 app 管線上渲染（Razor、view resx、授權政策、唯讀 middleware 放行）。
/// 測試身分只帶 claims、不對應資料庫使用者——不改共用測試庫的 admin 帳號（跨 session 會互相覆蓋）。
/// </summary>
public class LicenseStatusPageFactory : TestWebApplicationFactory
{
    public static readonly LicenseStatus ReadOnly = new(LicenseState.ReadOnly, "測試：授權已於 2027-01-01 到期。",
        new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2027, 1, 31, 0, 0, 0, TimeSpan.Zero));

    /// <summary>授權載入後的狀態；子類別覆寫成其他狀態（例如正常）。</summary>
    protected virtual LicenseStatus LicensedStatus => ReadOnly;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<ILicenseStateService>(new FixedState(LicensedStatus));
            // 管理員判斷看「作用中角色」（AdminAccess），不看角色宣告；這裡沒有資料庫使用者，
            // 改由標頭決定作用中角色：X-Test-Role: Admin → 指向名為 Admin 的角色。
            s.AddScoped<IUserContext, ClaimsOnlyUserContext>();
            s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ClaimsOnlyAuth>("ClaimsOnly", _ => { });
            s.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultAuthenticateScheme = "ClaimsOnly";
                o.DefaultChallengeScheme = "ClaimsOnly";
                o.DefaultScheme = "ClaimsOnly";
            });
        });
    }

    private sealed class FixedState(LicenseStatus licensed) : ILicenseStateService
    {
        // 與正式啟動順序一致：Program.cs 先跑 Migrate 與各項啟動種子，之後才 SetLicense；在那之前狀態是
        // 「授權尚未驗證、不擋」。一開始就回唯讀的話，啟動種子（例如待驗倉）要不要寫入取決於共用測試庫當下的資料，
        // 測試結果會隨執行順序浮動，也測不到正式環境的真實行為。
        private volatile bool _licenseSet;
        public LicenseStatus Current => _licenseSet ? licensed : new LicenseStatus(LicenseState.Normal, "授權尚未驗證。", null, null, null);
        public LicenseContext? License => new() { LicenseId = "LIC-TEST-PAGE", ClientCode = "ACME", ClientName = "Acme", BindingMode = "offline", Seats = 1 };
        /// <summary>offline、實體機、未確認：線上授權確認卡片顯示「未啟用」，啟用鈕停用並提示先確認。</summary>
        public LicenseStateInputs? Inputs => new(new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero), "offline",
            HostCheck: HostCheckResult.Match, HostKind: DeploymentKind.Physical);
        public void SetLicense(LicenseContext license) => _licenseSet = true;
        public void SetRevoked(DateTimeOffset revokedAt, string reason) { }
        public void SetSunset() { }
    }

    private sealed class ClaimsOnlyUserContext(IHttpContextAccessor http, SmartFactory.MES.Data.ApplicationDbContext db) : IUserContext
    {
        private string? _roleId;
        private bool _resolved;
        public string? PlantId => null;
        public string? TimeZoneId => null;
        public string? Language => null;
        public string? UserId => http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        public string? ActiveRolePath => null;
        public IReadOnlyList<string> AllowedPlantIds => [];
        public string? ActiveRoleId
        {
            get
            {
                if (_resolved) return _roleId;
                _resolved = true;
                if (http.HttpContext?.Request.Headers["X-Test-Role"].ToString() != AdminAccess.RoleName) return null;
                _roleId = db.Roles.Where(r => r.Name == AdminAccess.RoleName).Select(r => r.Id).FirstOrDefault();
                if (_roleId == null)
                {
                    var role = new SmartFactory.MES.Models.ApplicationRole { Name = AdminAccess.RoleName, NormalizedName = "ADMIN" };
                    db.Roles.Add(role);
                    db.SaveChanges();
                    _roleId = role.Id;
                }
                return _roleId;
            }
        }
    }

    /// <summary>X-Test-Role 標頭決定角色；沒帶就是未登入。</summary>
    private sealed class ClaimsOnlyAuth(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
        : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Role", out var role)) return Task.FromResult(AuthenticateResult.NoResult());
            var id = new ClaimsIdentity([new Claim(ClaimTypes.Name, "page-test"), new Claim(ClaimTypes.NameIdentifier, "no-such-user"),
                new Claim(ClaimTypes.Role, role.ToString())], "ClaimsOnly");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), "ClaimsOnly")));
        }
    }
}

// 收進 Integration collection：它有自己的測試主機，平行跑的話會和主 collection 在乾淨的 CI 資料庫上
// 同時 migrate（pg_type 撞名、欄位已存在），本機庫早已 migrate 過所以撞不到（2026-10-16 Core #64）。
[Collection("Integration")]
public class LicenseStatusPageTests(LicenseStatusPageFactory factory) : IClassFixture<LicenseStatusPageFactory>
{
    private async Task<(HttpStatusCode, string)> Get(string path, string? role, string culture = "zh-TW")
    {
        var c = factory.CreateClient(new() { AllowAutoRedirect = false });
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (role is not null) req.Headers.Add("X-Test-Role", role);
        req.Headers.Add("Cookie", $".AspNetCore.Culture=c={culture}|uic={culture}");
        var res = await c.SendAsync(req);
        return (res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Admin_SeesState_Details_RegisterLink_AndImportForm()
    {
        var (code, html) = await Get("/License", "Admin");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Contains("唯讀", html);
        Assert.Contains("LIC-TEST-PAGE", html);
        Assert.Contains("https://license.gemba-mes.workers.dev/renew", html);
        Assert.Contains("action=\"/License/Import\"", html);
        Assert.Contains("授權已失效，系統目前為唯讀", html);   // 橫幅
    }

    [Fact]
    public async Task NonAdmin_SeesState_ButNoLicenseIdOrImport()
    {
        var (code, html) = await Get("/License", "User");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Contains("唯讀", html);
        Assert.DoesNotContain("LIC-TEST-PAGE", html);
        Assert.DoesNotContain("/License/Import", html);
    }

    [Fact]
    public async Task English_UI_HasNoMissingKeys()
    {
        var (_, html) = await Get("/License", "Admin", "en-US");
        Assert.Contains("License status", html);
        Assert.Contains("Read-only", html);
        Assert.DoesNotContain("State_ReadOnly", html);
        Assert.DoesNotContain("Banner_ReadOnly", html);
        Assert.DoesNotContain("Binding_offline", html);
    }

    [Fact]
    public async Task Admin_OfflineLicense_SeesOnlineCheckCard_EnableNeedsConfirmationFirst()
    {
        var (_, html) = await Get("/License", "Admin");
        Assert.Contains("線上授權確認", html);
        Assert.Contains("未啟用（僅離線驗證）", html);
        Assert.Contains("action=\"/License/OnlineCheck\"", html);
        Assert.Contains("啟用前請先按上方「確認授權」完成一次確認。", html);
        Assert.Contains("action=\"/License/CheckinResponse\"", html);   // offline 也可以先確認

        var (_, en) = await Get("/License", "Admin", "en-US");
        Assert.Contains("Online license check", en);
        Assert.DoesNotContain("OnlineCheck", en.Replace("/License/OnlineCheck", ""));
    }

    [Fact]
    public async Task Anonymous_IsChallenged()
    {
        var (code, _) = await Get("/License", null);
        Assert.NotEqual(HttpStatusCode.OK, code);
    }
}

/// <summary>授權正常（offline、綁定本機、永久）。</summary>
public class NormalLicensePageFactory : LicenseStatusPageFactory
{
    protected override LicenseStatus LicensedStatus => new(LicenseState.Normal, "授權有效。",
        new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero), null, null);
}

/// <summary>
/// 授權正常時，綠色框標題是「授權有效」，內文原本也是「授權有效。」——重複且沒有資訊（2026-11-12 rc4 裝機回報）。
/// 改依授權內容說明，並隨語系顯示。
/// </summary>
[Collection("Integration")]
public class LicenseNormalPageTests(NormalLicensePageFactory factory) : IClassFixture<NormalLicensePageFactory>
{
    private async Task<string> Get(string culture)
    {
        var c = factory.CreateClient(new() { AllowAutoRedirect = false });
        var req = new HttpRequestMessage(HttpMethod.Get, "/License");
        req.Headers.Add("X-Test-Role", "Admin");
        req.Headers.Add("Cookie", $".AspNetCore.Culture=c={culture}|uic={culture}");
        var res = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 正常時說明綁定與效期_不重複標題()
    {
        var zh = await Get("zh-TW");
        Assert.Contains("已綁定本機，永久有效", zh);
        Assert.DoesNotContain("授權有效。", zh);

        var en = await Get("en-US");
        Assert.Contains("Bound to this machine; No expiry date", en);
        Assert.DoesNotContain("授權有效", en);
    }
}
