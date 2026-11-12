using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// 開發環境下跳過**登入時**的兩階段驗證挑戰。
    ///
    /// <para><b>要補的缺口：</b><c>Security:TwoFactorEnforcementEnabled=false</c> 原本只關掉
    /// <see cref="Middleware.RequireTwoFactorForAdminMiddleware"/>——那一道管的是
    /// 「**還沒啟用** 2FA 的 Admin 一律導去 <c>/TwoFactor/Setup</c>」。帳號本身一旦
    /// <c>TwoFactorEnabled=true</c>，登入挑戰是 Identity 自己的流程，那個開關碰不到，
    /// 於是「開發環境關掉 2FA」實際上只關掉一半。</para>
    ///
    /// <para><b>實際踩到的形狀跨機器：</b>2026-09-25，筆電的 IIS 站台（Production，開關預設
    /// <c>true</c>）把 admin 導去設定並啟用了 2FA，而它連的是開發機同一顆資料庫——那一列同時
    /// 也是開發環境讀的那一列。開發機這邊怎麼改組態都沒用，因為**要關的狀態不在組態裡，
    /// 在帳號上**。當時只能直接改 DB，而那等於把筆電那台的 2FA 也一起關掉。</para>
    ///
    /// <para><b>兩個條件缺一不可</b>（見 <see cref="ShouldBypass"/>）：環境是 Development
    /// <b>且</b>組態明講不強制。只看組態旗標的話，任何一台正式機只要有人設了那個鍵就會關掉
    /// 登入 2FA；多這一道，正式環境在結構上跳不過去，不必仰賴「不會有人去設它」。</para>
    ///
    /// <para><b>不受影響的：</b><c>/TwoFactor/Setup</c> 的自助啟用與 <c>/TwoFactorAdmin</c>
    /// 的管理員重置都走 <see cref="UserManager{TUser}"/>，不經過本類別；開發環境照樣測得了
    /// 2FA 的設定與重置流程。被跳過的只有「登入時要不要出示 TOTP」這一步。</para>
    /// </summary>
    public class DevTwoFactorBypassSignInManager : SignInManager<ApplicationUser>
    {
        /// <summary>
        /// 與 <see cref="Middleware.RequireTwoFactorForAdminMiddleware"/> 同一個組態鍵。
        ///
        /// <para>⚠️ 那邊目前仍是字面字串，**這裡是刻意重複而不是回頭改它**：中介屬 Core，
        /// 部署專案有一份同名檔，動它會在部署專案那側變成「就地漂移」而擋下建置，
        /// 為了一個常數不值得。代價是改鍵名要動兩處——所以真要改名時，先 grep
        /// 這個字串而不是只 grep 這個常數。</para>
        /// </summary>
        public const string EnforcementConfigKey = "Security:TwoFactorEnforcementEnabled";

        private readonly bool _bypass;

        public DevTwoFactorBypassSignInManager(
            UserManager<ApplicationUser> userManager,
            IHttpContextAccessor contextAccessor,
            IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
            IOptions<IdentityOptions> optionsAccessor,
            ILogger<SignInManager<ApplicationUser>> logger,
            IAuthenticationSchemeProvider schemes,
            IUserConfirmation<ApplicationUser> confirmation,
            IWebHostEnvironment environment,
            IConfiguration configuration)
            : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
        {
            _bypass = ShouldBypass(
                environment.IsDevelopment(),
                configuration.GetValue(EnforcementConfigKey, true));
        }

        /// <summary>要不要跳過登入挑戰（純函式，供測試）。</summary>
        public static bool ShouldBypass(bool isDevelopment, bool enforcementEnabled)
            => isDevelopment && !enforcementEnabled;

        /// <summary>
        /// <c>PasswordSignInAsync</c> 會先問這個方法要不要轉去二階段挑戰
        /// （<c>SignInOrTwoFactorAsync</c>），回 <c>false</c> 就直接完成登入。
        ///
        /// <para><b>為什麼覆寫這裡而不是攔在登入頁：</b>登入頁不只一個（桌機的 Identity UI、
        /// <c>Api/Mobile/MobileAuthController</c>），而它們共用的是這一個決策點。攔在頁面上
        /// 會漏掉手機那條，而漏掉的症狀是「桌機不用輸、手機還要」——看起來像手機壞了，
        /// 離真因很遠。</para>
        /// </summary>
        public override Task<bool> IsTwoFactorEnabledAsync(ApplicationUser user)
            => _bypass ? Task.FromResult(false) : base.IsTwoFactorEnabledAsync(user);
    }

    /// <summary>
    /// 把 <see cref="DevTwoFactorBypassSignInManager"/> 掛上去。
    ///
    /// <para><b>為什麼走 <see cref="IAppModule"/> 而不是改 <c>Program.cs</c>：</b>
    /// Identity 在 <c>Program.cs</c> 第 41 行以 <c>TryAddScoped</c> 註冊
    /// <c>SignInManager&lt;ApplicationUser&gt;</c>，而模組自動掃描在第 195 行——排在後面，
    /// 同一個服務型別後註冊者勝出，所以這裡覆寫得掉。這樣 Core 與部署專案兩邊都不必動
    /// <c>Program.cs</c>；部署專案的那一份早已分歧，動它等於再添一筆 Core 漂移。</para>
    /// </summary>
    public class DevTwoFactorBypassModule : IAppModule
    {
        public void RegisterServices(IServiceCollection services, IConfiguration configuration)
        {
            // 無條件覆寫註冊，是否真的跳過由 SignInManager 自己在建構時判定。
            // 把判定留在服務內部而不是在這裡決定要不要註冊，是為了讓「組態說跳過、
            // 但環境不是 Development」這種情況仍然走得到下面那行 Warning 的對照邏輯，
            // 且行為只有一個來源（ShouldBypass）。
            services.AddScoped<SignInManager<ApplicationUser>, DevTwoFactorBypassSignInManager>();
        }

        public void Configure(WebApplication app)
        {
            // 🚨 **絕不讓它靜默生效。** 跳過登入 2FA 是安全姿態的實質改變，而它唯一的
            //    外顯症狀是「登入變順了」——那不會讓任何人起疑。組態若被誤帶到不該帶的
            //    環境，這一行是唯一的訊號。
            if (DevTwoFactorBypassSignInManager.ShouldBypass(
                    app.Environment.IsDevelopment(),
                    app.Configuration.GetValue(
                        DevTwoFactorBypassSignInManager.EnforcementConfigKey, true)))
            {
                app.Logger.LogWarning(
                    "⚠ 登入時的兩階段驗證挑戰已跳過（環境={Env}，{Key}=false）。" +
                    "已啟用 2FA 的帳號登入時也不會被要求輸入驗證碼。" +
                    "此行為僅在 Development 環境成立，正式環境不受組態影響。",
                    app.Environment.EnvironmentName,
                    DevTwoFactorBypassSignInManager.EnforcementConfigKey);
            }
        }
    }
}
