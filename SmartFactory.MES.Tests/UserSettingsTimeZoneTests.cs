using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Controllers;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using System.Reflection;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 個人時區的自助入口（§11 #14 留下的後續建議）。
    ///
    /// <para>時區鏈是「廠別 → 個人 → 預設」三層，但中間那層長期沒有任何入口：
    /// 管理員在使用者維護代設得了，本人設不了。這一組釘住的是這個入口的四件事——
    /// 存得進去、空白不能變成空字串、壞值不准落地、以及**生效值不得由這裡自己重算**。</para>
    /// </summary>
    public class UserSettingsTimeZoneTests : TestBase
    {
        private readonly Mock<UserManager<ApplicationUser>> _users;
        private readonly Mock<IPlantSwitchService> _plantSwitch = new();
        private readonly Mock<IUserContext> _userContext = new();

        private const string UserId = "User1";

        public UserSettingsTimeZoneTests()
        {
            _users = GetMockUserManager();
            _users.Setup(m => m.GetUserId(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
                  .Returns(UserId);
            // UserManager.Users 導到 InMemory 的 DbSet，Index() 的 Include 鏈才走得完。
            _users.Setup(m => m.Users).Returns(() => _context.Users);
            // 沒設定就回 null，而 Index() 拿它去 Contains——本組測的是時區，角色給空的即可。
            _users.Setup(m => m.GetRolesAsync(It.IsAny<ApplicationUser>()))
                  .ReturnsAsync(new List<string>());
            _plantSwitch.Setup(p => p.GetSwitchablePlantsAsync(It.IsAny<string?>()))
                        .ReturnsAsync(new SwitchablePlants(new List<Plant>(), false));
        }

        private UserSettingsController BuildController()
        {
            var c = new UserSettingsController(
                _users.Object, GetRoleManager(), _context, _plantSwitch.Object, _userContext.Object, null!)
            {
                ControllerContext = GetControllerContext()
            };
            c.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
                c.ControllerContext.HttpContext,
                Mock.Of<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider>());
            return c;
        }

        private ApplicationUser SeedUser(string? personalTz = null, string? plantTz = null)
        {
            var user = new ApplicationUser
            {
                Id = UserId, UserName = "testuser", TimeZoneId = personalTz,
            };
            if (plantTz != null)
            {
                var plant = new Plant { Id = "P1", Name = "廠一", DefaultTimeZone = plantTz };
                _context.Plants.Add(plant);
                user.ActivePlantId = plant.Id;
            }
            _context.Users.Add(user);
            _context.SaveChanges();
            return user;
        }

        private ApplicationUser Reload()
        {
            _context.ChangeTracker.Clear();
            return _context.Users.Single(u => u.Id == UserId);
        }

        // ── 存檔 ────────────────────────────────────────────────────────────

        [Fact]
        public async Task SetTimeZone_StoresTheChosenZone()
        {
            SeedUser();
            var result = await BuildController().SetTimeZone("America/New_York");

            Assert.Equal(nameof(UserSettingsController.Index),
                Assert.IsType<RedirectToActionResult>(result).ActionName);
            Assert.Equal("America/New_York", Reload().TimeZoneId);
        }

        [Fact]
        public async Task SetTimeZone_ClearingIt_StoresNull_NotAnEmptyString()
        {
            // 這一支釘的是 §11 #13 的形狀：空白字串會讓 UserContext 那條鏈誤判成「有值」，
            // 個人時區因此被跳過，而空字串進了 FindSystemTimeZoneById 只會被 catch 吞掉。
            // 「清除設定」必須落地成 null，不是 ""。
            SeedUser(personalTz: "Asia/Tokyo");
            await BuildController().SetTimeZone("   ");

            Assert.Null(Reload().TimeZoneId);
        }

        [Fact]
        public async Task SetTimeZone_NormalizesWindowsIdToIana()
        {
            // 存 Windows ID 的那筆資料換到 Linux 主機就解不開——儲存值不可以隨部署 OS 漂移。
            SeedUser();
            await BuildController().SetTimeZone("Taipei Standard Time");

            Assert.Equal("Asia/Taipei", Reload().TimeZoneId);
        }

        // ── 壞值 ────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("Asia/Taipeh")]      // 打錯字
        [InlineData("Not/AZone")]
        public async Task SetTimeZone_RejectsUnresolvableId_AndLeavesTheStoredValueAlone(string bad)
        {
            // 壞值不會拋到使用者面前：DateTimeService 接住後退回預設時區，畫面完全正常，
            // 只有日期差一天。所以它必須擋在寫入之前，而且不能順手清掉原本好好的設定。
            SeedUser(personalTz: "Asia/Tokyo");
            var result = await BuildController().SetTimeZone(bad);

            Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Asia/Tokyo", Reload().TimeZoneId);
        }

        [Fact]
        public async Task SetTimeZone_RejectionIsVisible_NotSilent()
        {
            SeedUser();
            var controller = BuildController();
            await controller.SetTimeZone("Not/AZone");

            // 「按了沒反應也沒訊息」正是 SwitchPlant 那段註解自己批評過的行為。
            Assert.NotNull(controller.TempData["TimeZoneError"]);
            Assert.Null(controller.TempData["TimeZoneSaved"]);
        }

        // ── 入口本身 ────────────────────────────────────────────────────────

        [Fact]
        public void SetTimeZone_IsPostOnly_AndValidatesAntiForgery()
        {
            var m = typeof(UserSettingsController).GetMethod(nameof(UserSettingsController.SetTimeZone))!;
            Assert.NotNull(m.GetCustomAttribute<HttpPostAttribute>());
            Assert.NotNull(m.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        }

        // ── 生效值的來源 ────────────────────────────────────────────────────

        [Fact]
        public async Task Index_TakesTheEffectiveZoneFromUserContext_NotItsOwnCopyOfTheChain()
        {
            // 刻意讓兩個來源都有值，且都**不等於** IUserContext 回的答案：
            // 若 Index 自己重算那條鏈，得到的會是 Asia/Shanghai（廠別優先），不可能是這個值。
            // §11 #13 的缺陷正是「同一條鏈有兩份實作，答案不同」——不要製造第三份。
            SeedUser(personalTz: "Asia/Tokyo", plantTz: "Asia/Shanghai");
            _userContext.Setup(u => u.TimeZoneId).Returns("Pacific/Auckland");

            var controller = BuildController();
            var result = await controller.Index();

            Assert.IsType<ViewResult>(result);
            Assert.Equal("Pacific/Auckland", controller.ViewBag.EffectiveTimeZoneId);
        }

        [Fact]
        public async Task Index_FallsBackToTheSameDefaultAsDateTimeService()
        {
            // 兩個來源都空時 IUserContext 回 null，而換算實際會用 DateTimeService 的預設值。
            // 畫面顯示的必須是真正會被用到的那一個，不是空白。
            SeedUser();
            _userContext.Setup(u => u.TimeZoneId).Returns((string?)null);

            var controller = BuildController();
            await controller.Index();

            Assert.Equal(DateTimeService.FallbackTimeZoneId, controller.ViewBag.EffectiveTimeZoneId);
        }
    }
}
