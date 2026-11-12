using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using System.Security.Claims;
using Xunit;

namespace SmartFactory.MES.Tests.Services
{
    public class UserContextTests : TestBase
    {
        private readonly Mock<IHttpContextAccessor> _mockHttpContextAccessor;
        private readonly UserManager<ApplicationUser> _userManager;

        public UserContextTests()
        {
            _mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
            
            // 使用真實的 UserManager 與 InMemoryDatabase 配合，以測試 Include 邏輯
            var userStore = new UserStore<ApplicationUser>(_context);
            _userManager = new UserManager<ApplicationUser>(
                userStore, null, new PasswordHasher<ApplicationUser>(), null, null, null, null, null, null);
        }

        [Fact]
        public void UserId_ReturnsNull_WhenUserNotAuthenticated()
        {
            // Arrange
            var context = new DefaultHttpContext();
            // Identity.IsAuthenticated 預設為 false
            _mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(context);
            var userContext = new UserContext(_mockHttpContextAccessor.Object, _userManager, _context);

            // Act & Assert
            Assert.Null(userContext.UserId);
            Assert.Null(userContext.ActiveRoleId);
            Assert.Null(userContext.ActiveRolePath);
        }

        [Fact]
        public async Task Properties_ReturnCorrectValues_WhenUserAuthenticated()
        {
            // Arrange
            var organization = new OrganizationUnit { Id = "org-1", Name = "Org 1", Path = "Root/Org1" };
            _context.OrganizationUnits.Add(organization);
            
            var role = new ApplicationRole { Id = "role-1", Name = "Role 1", OrganizationUnitId = "org-1", OrganizationUnit = organization };
            _context.Roles.Add(role);

            var user = new ApplicationUser
            {
                Id = "user-123",
                UserName = "testuser",
                Email = "test@test.com",
                ActivePlantId = "Plant1",
                Language = "zh-TW",
                TimeZoneId = "Asia/Taipei",
                ActiveRoleId = "role-1"
            };
            await _userManager.CreateAsync(user);
            _context.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = "role-1" });
            await _context.SaveChangesAsync();

            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Id) };
            var identity = new ClaimsIdentity(claims, "TestAuth");
            var claimsPrincipal = new ClaimsPrincipal(identity);

            var httpContext = new DefaultHttpContext { User = claimsPrincipal };
            _mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

            var userContext = new UserContext(_mockHttpContextAccessor.Object, _userManager, _context);

            // Act & Assert
            Assert.Equal("user-123", userContext.UserId);
            Assert.Equal("Plant1", userContext.PlantId);
            Assert.Equal("zh-TW", userContext.Language);
            Assert.Equal("Asia/Taipei", userContext.TimeZoneId);
            Assert.Equal("role-1", userContext.ActiveRoleId);
            Assert.Equal("Root/Org1", userContext.ActiveRolePath);
        }

        [Fact]
        public async Task ActiveRolePath_ReturnsNull_WhenRoleHasNoOrganizationUnit()
        {
            // Arrange
            var role = new ApplicationRole { Id = "role-no-org", Name = "Standalone Role" };
            _context.Roles.Add(role);

            var user = new ApplicationUser
            {
                Id = "user-456",
                UserName = "standaloneuser",
                Email = "standalone@test.com",
                ActiveRoleId = "role-no-org"
            };
            await _userManager.CreateAsync(user);
            _context.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = "role-no-org" });
            await _context.SaveChangesAsync();

            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Id) };
            var identity = new ClaimsIdentity(claims, "TestAuth");
            var claimsPrincipal = new ClaimsPrincipal(identity);

            var httpContext = new DefaultHttpContext { User = claimsPrincipal };
            _mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

            var userContext = new UserContext(_mockHttpContextAccessor.Object, _userManager, _context);

            // Act & Assert
            Assert.Equal("role-no-org", userContext.ActiveRoleId);
            Assert.Null(userContext.ActiveRolePath);
        }

        /// <summary>
        /// 時區優先序：廠別時區贏過個人時區。兩個來源刻意給不同值，
        /// 否則「回傳哪一層」在斷言上分不出來。
        /// </summary>
        [Fact]
        public async Task TimeZoneId_PrefersPlantOverPersonal()
        {
            var userContext = await ArrangeUserContextAsync(
                userId: "tz-user-1",
                plantTimeZone: "Pacific/Kiritimati",
                personalTimeZone: "Pacific/Midway");

            Assert.Equal("Pacific/Kiritimati", userContext.TimeZoneId);
        }

        /// <summary>
        /// 廠別沒設時區時，才輪到個人時區。
        /// </summary>
        [Fact]
        public async Task TimeZoneId_FallsBackToPersonal_WhenPlantHasNone()
        {
            var userContext = await ArrangeUserContextAsync(
                userId: "tz-user-2",
                plantTimeZone: null,
                personalTimeZone: "Pacific/Midway");

            Assert.Equal("Pacific/Midway", userContext.TimeZoneId);
        }

        /// <summary>
        /// 這支是空白感知的守門：廠別時區是全空白字串時，等同未設定，必須退到個人時區。
        /// 把實作改回 <c>??</c>（只擋 null）會讓這支轉紅——那正是它存在的理由，
        /// 因為線上的症狀（個人時區被無聲跳過、時間退回預設時區）不會有任何錯誤訊息。
        /// </summary>
        [Fact]
        public async Task TimeZoneId_TreatsBlankPlantTimeZoneAsUnset()
        {
            var userContext = await ArrangeUserContextAsync(
                userId: "tz-user-3",
                plantTimeZone: "   ",
                personalTimeZone: "Pacific/Midway");

            Assert.Equal("Pacific/Midway", userContext.TimeZoneId);
        }

        /// <summary>
        /// 兩層都沒有就回 null，交給 <see cref="DateTimeService"/> 決定預設時區——
        /// UserContext 這一層不應該自己編一個代碼出來。
        /// </summary>
        [Fact]
        public async Task TimeZoneId_ReturnsNull_WhenNeitherSet()
        {
            var userContext = await ArrangeUserContextAsync(
                userId: "tz-user-4",
                plantTimeZone: null,
                personalTimeZone: null);

            Assert.Null(userContext.TimeZoneId);
        }

        /// <summary>
        /// 已被移出的角色不算數：ActiveRoleId 與角色相依的屬性一律當作沒有角色（default-deny）。
        /// 2026-10-07 實測：dev 2 月被改成只有 Operator，ActiveRoleId 停在 Admin，手機選單與桌機權限全照 Admin 給。
        /// </summary>
        [Fact]
        public async Task ActiveRoleId_已被移出該角色_回傳null()
        {
            var org = new OrganizationUnit { Id = "org-a", Name = "A", Path = "Root/A" };
            _context.OrganizationUnits.Add(org);
            _context.Roles.Add(new ApplicationRole { Id = "stale-admin", Name = "StaleAdmin", OrganizationUnitId = "org-a" });
            _context.Roles.Add(new ApplicationRole { Id = "stale-op", Name = "StaleOperator" });

            var user = new ApplicationUser { Id = "dev", UserName = "dev", ActiveRoleId = "stale-admin" };
            await _userManager.CreateAsync(user);
            _context.UserRoles.Add(new IdentityUserRole<string> { UserId = "dev", RoleId = "stale-op" });   // 只剩 Operator
            await _context.SaveChangesAsync();

            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "dev") }, "TestAuth"))
            };
            _mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

            var userContext = new UserContext(_mockHttpContextAccessor.Object, _userManager, _context);

            Assert.Equal("dev", userContext.UserId);
            Assert.Null(userContext.ActiveRoleId);
            Assert.Null(userContext.ActiveRolePath);
            // 資料本身不動：修復交給角色異動的寫入點，讀取端只負責不信任它。
            Assert.Equal("stale-admin", (await _context.Users.FindAsync("dev"))!.ActiveRoleId);
        }

        // ── 非同步預先載入（UserPreferenceMiddleware 每個請求先呼叫）──────────────

        [Fact]
        public async Task EnsureLoadedAsync_之後讀屬性不再查資料庫_值與同步路徑相同()
        {
            var org = new OrganizationUnit { Id = "org-async", Name = "A", Path = "Root/Async" };
            _context.OrganizationUnits.Add(org);
            _context.Roles.Add(new ApplicationRole { Id = "role-async", Name = "RoleAsync", OrganizationUnitId = "org-async" });
            var user = new ApplicationUser { Id = "u-async", UserName = "u-async", ActivePlantId = "P9", Language = "en-US", ActiveRoleId = "role-async" };
            await _userManager.CreateAsync(user);
            _context.UserRoles.Add(new IdentityUserRole<string> { UserId = "u-async", RoleId = "role-async" });
            await _context.SaveChangesAsync();
            _mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "u-async") }, "TestAuth"))
            });

            var userContext = new UserContext(_mockHttpContextAccessor.Object, _userManager, _context);
            await userContext.EnsureLoadedAsync();

            // 載入後把資料庫改掉：屬性若還在查資料庫，就會讀到改過的值
            var row = await _context.Users.FindAsync("u-async");
            row!.ActivePlantId = "CHANGED";
            _context.UserRoles.RemoveRange(_context.UserRoles.Where(r => r.UserId == "u-async"));
            await _context.SaveChangesAsync();

            Assert.Equal("u-async", userContext.UserId);
            Assert.Equal("role-async", userContext.ActiveRoleId);   // 角色檢查是在載入當下做的，不是讀屬性時
            Assert.Equal("Root/Async", userContext.ActiveRolePath);
            Assert.Equal("en-US", userContext.Language);
        }

        [Fact]
        public async Task EnsureLoadedAsync_已被移出該角色_ActiveRoleId同樣是null()
        {
            _context.Roles.Add(new ApplicationRole { Id = "gone-admin", Name = "GoneAdmin" });
            await _userManager.CreateAsync(new ApplicationUser { Id = "dev2", UserName = "dev2", ActiveRoleId = "gone-admin" });
            await _context.SaveChangesAsync();
            _mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "dev2") }, "TestAuth"))
            });

            var userContext = new UserContext(_mockHttpContextAccessor.Object, _userManager, _context);
            await userContext.EnsureLoadedAsync();

            Assert.Equal("dev2", userContext.UserId);
            Assert.Null(userContext.ActiveRoleId);       // default-deny 不能因為換了載入路徑就漏掉
        }

        [Fact]
        public async Task EnsureLoadedAsync_未登入_屬性皆為null()
        {
            _mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(new DefaultHttpContext());
            var userContext = new UserContext(_mockHttpContextAccessor.Object, _userManager, _context);
            await userContext.EnsureLoadedAsync();
            Assert.Null(userContext.UserId);
            Assert.Null(userContext.PlantId);
        }

        private async Task<UserContext> ArrangeUserContextAsync(
            string userId, string? plantTimeZone, string? personalTimeZone)
        {
            var plant = new Plant
            {
                Id = $"plant-{userId}",
                Name = "測試廠",
                DefaultTimeZone = plantTimeZone
            };
            _context.Plants.Add(plant);

            var user = new ApplicationUser
            {
                Id = userId,
                UserName = userId,
                Email = $"{userId}@test.com",
                ActivePlantId = plant.Id,
                TimeZoneId = personalTimeZone
            };
            await _userManager.CreateAsync(user);
            await _context.SaveChangesAsync();

            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Id) };
            var identity = new ClaimsIdentity(claims, "TestAuth");
            var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
            _mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

            return new UserContext(_mockHttpContextAccessor.Object, _userManager, _context);
        }
    }
}
