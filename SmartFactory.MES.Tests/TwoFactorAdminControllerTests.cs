using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Moq;
using SmartFactory.MES.Controllers;
using SmartFactory.MES.Models;
using System.Security.Claims;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 管理員代重置他人 2FA（遺失驗證器救援）。2026-10-20 總檢時發現沒有任何測試。
    /// 這支能關掉別人的第二道驗證，守門（呼叫者須為 Admin 角色）一旦失效就是提權破口，
    /// 所以兩腿都驗：非 Admin 擋、Admin 真的有呼叫停用＋重置金鑰。
    /// </summary>
    public class TwoFactorAdminControllerTests : TestBase
    {
        private readonly Mock<UserManager<ApplicationUser>> _um = new(
            Mock.Of<IUserStore<ApplicationUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);
        private readonly ApplicationUser _admin  = new() { Id = "U-ADMIN", UserName = "admin", Email = "admin@x.test" };
        private readonly ApplicationUser _target = new() { Id = "U-T", UserName = "target", Email = "t@x.test", TwoFactorEnabled = true };

        public TwoFactorAdminControllerTests()
        {
            _context.Users.AddRange(_admin, _target);
            _context.SaveChanges();
            _um.Setup(u => u.Users).Returns(_context.Users);
            _um.Setup(u => u.FindByIdAsync(_target.Id)).ReturnsAsync(_target);
            _um.Setup(u => u.SetTwoFactorEnabledAsync(It.IsAny<ApplicationUser>(), false)).ReturnsAsync(IdentityResult.Success);
            _um.Setup(u => u.ResetAuthenticatorKeyAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(IdentityResult.Success);
        }

        /// <param name="roles">第一個是<b>作用中角色</b>名稱；守門只看作用中角色（AdminAccess），
        /// 其餘只是「也屬於」的角色，不影響結果。</param>
        private TwoFactorAdminController Ctrl(ApplicationUser? caller, params string[] roles)
        {
            _um.Setup(u => u.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(caller);
            if (caller != null) _um.Setup(u => u.GetRolesAsync(caller)).ReturnsAsync(roles.ToList());
            string? activeRoleId = null;
            if (caller != null && roles.Length > 0)
            {
                activeRoleId = "R-" + roles[0];
                if (!_context.Roles.Any(r => r.Id == activeRoleId))
                {
                    _context.Roles.Add(new ApplicationRole { Id = activeRoleId, Name = roles[0] });
                    _context.SaveChanges();
                }
            }
            var userContext = new Mock<SmartFactory.MES.Services.IUserContext>();
            userContext.Setup(u => u.ActiveRoleId).Returns(activeRoleId);
            var ctx = GetControllerContext();
            ctx.HttpContext.RequestServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
                .AddSingleton(userContext.Object).AddSingleton(_context).BuildServiceProvider();
            var c = new TwoFactorAdminController(_um.Object) { ControllerContext = ctx,
                Url = Mock.Of<Microsoft.AspNetCore.Mvc.IUrlHelper>() };
            c.TempData = new TempDataDictionary(c.ControllerContext.HttpContext, Mock.Of<ITempDataProvider>());
            return c;
        }

        private void AssertNothingReset()
        {
            _um.Verify(u => u.SetTwoFactorEnabledAsync(It.IsAny<ApplicationUser>(), It.IsAny<bool>()), Times.Never);
            _um.Verify(u => u.ResetAuthenticatorKeyAsync(It.IsAny<ApplicationUser>()), Times.Never);
        }

        [Theory]
        [InlineData("User")]
        [InlineData("admin")]   // 角色名稱大小寫不同不算 Admin
        public async Task NonAdmin_Forbidden_AndNothingReset(string role)
        {
            var caller = new ApplicationUser { Id = "U-X", UserName = "x" };

            Assert.IsType<ForbidResult>(await Ctrl(caller, role).Index(null));
            Assert.IsType<ForbidResult>(await Ctrl(caller, role).Reset(_target.Id));
            AssertNothingReset();
        }

        // 屬於 Admin、但作用中角色切成別的：選單已經看不到這頁，直接輸入網址也不能進（2026-11-12 起看作用中角色）
        [Fact]
        public async Task AdminMember_WithOtherActiveRole_Forbidden()
        {
            var caller = new ApplicationUser { Id = "U-Y", UserName = "y" };
            Assert.IsType<ForbidResult>(await Ctrl(caller, "Operator", "Admin").Reset(_target.Id));
            AssertNothingReset();
        }

        [Fact]
        public async Task NotSignedIn_Forbidden()
        {
            Assert.IsType<ForbidResult>(await Ctrl(null).Reset(_target.Id));
            AssertNothingReset();
        }

        [Fact]
        public async Task Admin_Reset_DisablesTwoFactor_AndResetsAuthenticatorKey()
        {
            var c = Ctrl(_admin, "Admin");

            Assert.IsType<RedirectToActionResult>(await c.Reset(_target.Id));

            _um.Verify(u => u.SetTwoFactorEnabledAsync(_target, false), Times.Once);
            _um.Verify(u => u.ResetAuthenticatorKeyAsync(_target), Times.Once);   // 舊金鑰與復原碼一併失效
            Assert.Contains("target", (string)c.TempData["SuccessMessage"]!);
        }

        [Fact]
        public async Task Admin_Reset_UnknownUser_NotFound()
        {
            Assert.IsType<NotFoundResult>(await Ctrl(_admin, "Admin").Reset("U-NOBODY"));
            AssertNothingReset();
        }

        [Theory]
        [InlineData("targ", "target")]
        [InlineData(" t@x ", "target")]   // 依 Email、前後空白去掉
        [InlineData("zzz", null)]
        public async Task Admin_Index_SearchesUserNameAndEmail(string search, string? expected)
        {
            var view = Assert.IsType<ViewResult>(await Ctrl(_admin, "Admin").Index(search));
            var rows = Assert.IsAssignableFrom<List<TwoFactorAdminController.UserRow>>(view.Model);
            if (expected == null) Assert.Empty(rows);
            else Assert.Equal(expected, Assert.Single(rows).UserName);
        }

        /// <summary>筆數上限 200 要讓人看得見：恰好 200 筆不標截斷，201 筆標截斷且只回 200。</summary>
        [Theory]
        [InlineData(198, false, 200)]   // 加上種子 2 人＝200
        [InlineData(199, true, 200)]    // ＝201
        public async Task Admin_Index_RowCap_IsVisible(int extra, bool truncated, int shown)
        {
            for (int i = 0; i < extra; i++)
                _context.Users.Add(new ApplicationUser { Id = $"U-{i:D4}", UserName = $"u{i:D4}" });
            _context.SaveChanges();

            var view = Assert.IsType<ViewResult>(await Ctrl(_admin, "Admin").Index(null));

            Assert.Equal(truncated, view.ViewData["Truncated"]);
            Assert.Equal(shown, Assert.IsAssignableFrom<List<TwoFactorAdminController.UserRow>>(view.Model).Count);
        }
    }
}
