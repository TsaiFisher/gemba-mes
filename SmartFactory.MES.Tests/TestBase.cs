using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using System.Security.Claims;

namespace SmartFactory.MES.Tests
{
    public class TestBase : IDisposable
    {
        protected ApplicationDbContext _context;

        public TestBase()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                // InMemory provider 不支援交易；服務層用 BeginTransactionAsync 包 UoW 時
                // 預設會把 TransactionIgnoredWarning 當錯誤拋。測試環境忽略即可（正式環境用 PostgreSQL 仍有真交易）。
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            _context = new ApplicationDbContext(options);
            _context.Database.EnsureCreated();
        }

        /// <summary>
        /// §25 製造簡化模式服務。<paramref name="enabled"/> 預設 false＝完整流程，
        /// 讓既有測試維持原本行為（也順帶成為「開關未開時行為不變」的守門）。
        /// </summary>
        protected static SmartFactory.MES.Modules.MFG.Services.MfgSimplifiedModeService MfgSimplified(bool enabled = false)
        {
            var sysParam = new Mock<SmartFactory.MES.Modules.BAS.Services.IPlantParamService>();
            sysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgSimplifiedMode,
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(enabled ? "true" : null);
            return new SmartFactory.MES.Modules.MFG.Services.MfgSimplifiedModeService(sysParam.Object);
        }

        /// <summary>
        /// §25.5 自動展開服務的「不觸發」替身（＝簡化模式關閉）。
        /// 不用 <c>Mock.Of&lt;T&gt;()</c>：Moq 對 <c>Task&lt;record&gt;</c> 的預設值是 null，
        /// 呼叫端取 <c>.Triggered</c> 會 NRE。
        /// </summary>
        protected static SmartFactory.MES.Modules.MFG.Services.IMrpAutoExpandService NoAutoExpand()
        {
            var m = new Mock<SmartFactory.MES.Modules.MFG.Services.IMrpAutoExpandService>();
            m.Setup(x => x.ExpandApprovedOrdersAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(new SmartFactory.MES.Modules.MFG.Services.MrpAutoExpandResult(false, null, 0, null));
            return m.Object;
        }

        protected Mock<UserManager<TUser>> GetMockUserManager<TUser>(List<TUser> lsData) where TUser : class
        {
            var store = new Mock<IUserStore<TUser>>();
            var mgr = new Mock<UserManager<TUser>>(store.Object, null, null, null, null, null, null, null, null);
            mgr.Object.UserValidators.Add(new UserValidator<TUser>());
            mgr.Object.PasswordValidators.Add(new PasswordValidator<TUser>());

            return mgr;
        }
        
        protected Mock<UserManager<ApplicationUser>> GetMockUserManager()
        {
             var store = new Mock<IUserStore<ApplicationUser>>();
             var mgr = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
             return mgr;
        }

        protected Mock<SignInManager<ApplicationUser>> GetMockSignInManager(UserManager<ApplicationUser> userManager)
        {
            var contextAccessor = new Mock<IHttpContextAccessor>();
            var claimsFactory = new Mock<IUserClaimsPrincipalFactory<ApplicationUser>>();
            return new Mock<SignInManager<ApplicationUser>>(userManager, contextAccessor.Object, claimsFactory.Object, null, null, null, null);
        }

        protected Mock<RoleManager<TRole>> GetMockRoleManager<TRole>() where TRole : class
        {
            var store = new Mock<IRoleStore<TRole>>();
            return new Mock<RoleManager<TRole>>(store.Object, null, null, null, null);
        }

        protected RoleManager<ApplicationRole> GetRoleManager()
        {
            var store = new Microsoft.AspNetCore.Identity.EntityFrameworkCore.RoleStore<ApplicationRole>(_context);
            return new RoleManager<ApplicationRole>(store, null, null, null, null);
        }
        
        protected Mock<IExportService> GetMockExportService()
        {
            var mock = new Mock<IExportService>();
            mock.Setup(x => x.ToExcel(It.IsAny<ExportRequest>())).Returns(Array.Empty<byte>());
            mock.Setup(x => x.ToCsv(It.IsAny<ExportRequest>())).Returns(Array.Empty<byte>());
            mock.Setup(x => x.ToJson(It.IsAny<ExportRequest>())).Returns(Array.Empty<byte>());
            return mock;
        }

        protected Mock<IDateTimeService> GetMockDateTimeService()
        {
            var mock = new Mock<IDateTimeService>();
            mock.Setup(x => x.ToLocalTime(It.IsAny<DateTime>())).Returns<DateTime>(dt => dt);
            // Now 沒設定就回 default(DateTime)，匯出檔名會靜靜變成 Xxx_00010101——
            // 不會有任何測試紅，但拿到的東西是錯的。給固定值讓斷言可重現。
            mock.Setup(x => x.Now).Returns(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified));
            return mock;
        }

        protected Mock<IUserContext> GetMockUserContext(string roleId = "Admin", string userId = "User1")
        {
            var mock = new Mock<IUserContext>();
            mock.Setup(x => x.ActiveRoleId).Returns(roleId);
            return mock;
        }

        protected Mock<ICurrencyService> GetMockCurrencyService(int defaultDp = 2, string baseCurrency = "TWD")
        {
            var mock = new Mock<ICurrencyService>();
            mock.Setup(x => x.GetBaseCurrencyAsync()).ReturnsAsync(baseCurrency);
            mock.Setup(x => x.GetAllDecimalPlacesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyDictionary<string, int>)new Dictionary<string, int>
                {
                    [baseCurrency] = defaultDp
                });
            mock.Setup(x => x.RoundAsync(It.IsAny<decimal>(), It.IsAny<string>()))
                .ReturnsAsync<decimal, string, ICurrencyService, decimal>((amount, _) => Math.Round(amount, defaultDp, MidpointRounding.AwayFromZero));
            return mock;
        }

        protected ControllerContext GetControllerContext(string userName = "testuser", string userId = "User1", string role = "Admin")
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, userName),
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Role, role)
            };
            var identity = new ClaimsIdentity(claims, "TestAuth");
            var claimsPrincipal = new ClaimsPrincipal(identity);

            return new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = claimsPrincipal }
            };
        }

        /// <summary>
        /// 含 RequestServices 的 ControllerContext：供 ModuleControllerBase 子類別
        /// 在 PrepareIndexPermissionsAsync 被提早呼叫時使用（IPermissionService + IUserContext）。
        /// </summary>
        protected ControllerContext GetControllerContextWithServices(
            string userName = "testuser", string userId = "User1", string role = "Admin",
            SmartFactory.MES.Services.IPermissionService? permService = null,
            SmartFactory.MES.Services.IUserContext? userContext = null,
            SmartFactory.MES.Services.IApprovalService? approvalService = null,
            Microsoft.Extensions.Localization.IStringLocalizer<ProjectZSharedResource>? sharedLocalizer = null,
            SmartFactory.MES.Services.IDateTimeService? dateTimeService = null,
            Action<IServiceCollection>? configureServices = null)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, userName),
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Role, role)
            };
            var identity = new ClaimsIdentity(claims, "TestAuth");
            var claimsPrincipal = new ClaimsPrincipal(identity);

            var mockPerm = new Mock<SmartFactory.MES.Services.IPermissionService>();
            // CanRead 是 Index 的讀取閘門（2026-08-03 補齊 29 支畫面）；漏設會讓所有 Index 測試拿到 Forbid。
            mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
            mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
            mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
            mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);
            mockPerm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(false);

            var mockUser = new Mock<SmartFactory.MES.Services.IUserContext>();
            mockUser.Setup(u => u.UserId).Returns(userId);

            // Mock ITempDataDictionaryFactory（Controller.View() 需要）
            var tempDataDict = new Mock<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionary>();
            var tempDataFactory = new Mock<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionaryFactory>();
            tempDataFactory
                .Setup(f => f.GetTempData(It.IsAny<Microsoft.AspNetCore.Http.HttpContext>()))
                .Returns(tempDataDict.Object);

            var services = new ServiceCollection();
            services.AddSingleton(_context);
            services.AddSingleton(permService ?? mockPerm.Object);
            services.AddSingleton(userContext ?? mockUser.Object);
            services.AddSingleton(tempDataFactory.Object);
            // ModuleControllerBase 的送/撤簽助手（SubmitForApprovalAsync/WithdrawApprovalAsync）
            // 經 RequestServices 解析 IApprovalService 與共用在地化器；測試傳入自己的 mock 即可，
            // 未傳則註冊預設 mock 以免 Svc 解析丟例外。
            services.AddSingleton(approvalService ?? new Mock<SmartFactory.MES.Services.IApprovalService>().Object);
            services.AddSingleton(sharedLocalizer ?? GetMockLocalizer<ProjectZSharedResource>().Object);
            // PreparePrintViewBagAsync 的列印時間戳走 IDateTimeService.Now（R22(b)：顯示用時間
            // 戳不得讀伺服器時鐘）。未註冊時 Svc 解析會丟例外，把 132 支 controller 的 Print
            // 測試一起打掉；預設給固定值讓斷言可重現，控制器另有注入同一介面的測試請傳進來，
            // 否則會拿到兩個不同實例（建構式一個、RequestServices 一個）。
            services.AddSingleton(dateTimeService ?? DefaultDateTimeService());
            services.AddOptions();
            services.AddLogging();
            // 測試可註冊控制器於 RequestServices 延遲解析的額外服務
            // （如 CreateReport 報工後以 GetRequiredService<IReportEtaNotifier> 觸發 ETA 重算）
            configureServices?.Invoke(services);
            var sp = services.BuildServiceProvider();

            var httpContext = new DefaultHttpContext
            {
                User            = claimsPrincipal,
                RequestServices = sp
            };
            return new ControllerContext { HttpContext = httpContext };
        }

        /// <summary>
        /// 測試用的預設 <see cref="SmartFactory.MES.Services.IDateTimeService"/>：
        /// 固定時刻 + 換算為 identity。時間相關斷言必須可重現，所以不用 DateTime.UtcNow。
        /// </summary>
        private static SmartFactory.MES.Services.IDateTimeService DefaultDateTimeService()
        {
            var fixedNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
            var mock = new Mock<SmartFactory.MES.Services.IDateTimeService>();
            mock.Setup(d => d.Now).Returns(fixedNow);
            mock.Setup(d => d.ToLocalTime(It.IsAny<DateTime>())).Returns((DateTime t) => t);
            mock.Setup(d => d.ToUtcTime(It.IsAny<DateTime>())).Returns((DateTime t) => t);
            mock.Setup(d => d.GetLocalDate(It.IsAny<string?>()))
                .Returns(DateOnly.FromDateTime(fixedNow));
            return mock.Object;
        }

        protected IServiceProvider BuildServiceProvider(params (Type serviceType, object instance)[] registrations)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddControllersWithViews();
            foreach (var (type, instance) in registrations)
                services.AddSingleton(type, _ => instance);
            return services.BuildServiceProvider();
        }

        protected static Mock<Microsoft.Extensions.Localization.IStringLocalizer<T>> GetMockLocalizer<T>()
        {
            var loc = new Mock<Microsoft.Extensions.Localization.IStringLocalizer<T>>();
            loc.Setup(x => x[It.IsAny<string>()])
               .Returns<string>(k => new Microsoft.Extensions.Localization.LocalizedString(k, k));
            loc.Setup(x => x[It.IsAny<string>(), It.IsAny<object[]>()])
               .Returns<string, object[]>((k, _) => new Microsoft.Extensions.Localization.LocalizedString(k, k));
            return loc;
        }

        protected Mock<Microsoft.Extensions.Localization.IStringLocalizerFactory> GetMockLocalizerFactory()
        {
            var loc = new Mock<Microsoft.Extensions.Localization.IStringLocalizer>();
            loc.Setup(x => x[It.IsAny<string>()])
               .Returns<string>(k => new Microsoft.Extensions.Localization.LocalizedString(k, k));
            loc.Setup(x => x[It.IsAny<string>(), It.IsAny<object[]>()])
               .Returns<string, object[]>((k, _) => new Microsoft.Extensions.Localization.LocalizedString(k, k));

            var factory = new Mock<Microsoft.Extensions.Localization.IStringLocalizerFactory>();
            factory.Setup(f => f.Create(It.IsAny<Type>())).Returns(loc.Object);
            factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<string>())).Returns(loc.Object);
            return factory;
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }
    }
}
