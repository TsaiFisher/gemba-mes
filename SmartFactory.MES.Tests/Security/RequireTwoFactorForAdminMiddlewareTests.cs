using SmartFactory.MES.Middleware;
using Xunit;

namespace SmartFactory.MES.Tests.Security
{
    /// <summary>
    /// M3：強制 2FA 中介的判定邏輯（路徑白名單 × 角色命中）。
    /// </summary>
    public class RequireTwoFactorForAdminMiddlewareTests
    {
        // ── 白名單路徑 ────────────────────────────────────────────────────────────
        [Theory]
        [InlineData("/TwoFactor/Setup")]
        [InlineData("/twofactor/setup")]        // 大小寫不敏感
        [InlineData("/Identity/Account/Logout")]
        [InlineData("/Identity/Account/Login")]
        [InlineData("/css/site.css")]
        [InlineData("/js/app.js")]
        [InlineData("/lib/bootstrap/x.js")]
        [InlineData("/switch-role")]
        public void IsAllowedPath_WhitelistedPrefixes_ReturnTrue(string path)
        {
            Assert.True(RequireTwoFactorForAdminMiddleware.IsAllowedPath(path));
        }

        [Theory]
        [InlineData("/")]
        [InlineData("/Home/Index")]
        [InlineData("/WorkOrder/Index")]
        [InlineData("/UserSettings")]           // 非 /UserSettings/SetLanguage
        public void IsAllowedPath_NormalPages_ReturnFalse(string path)
        {
            Assert.False(RequireTwoFactorForAdminMiddleware.IsAllowedPath(path));
        }

        // ── 角色命中 ──────────────────────────────────────────────────────────────
        [Fact]
        public void RolesRequire2Fa_AdminInRequired_True()
        {
            Assert.True(RequireTwoFactorForAdminMiddleware.RolesRequire2Fa(
                new[] { "Operator", "Admin" }, new[] { "Admin" }));
        }

        [Fact]
        public void RolesRequire2Fa_CaseInsensitive_True()
        {
            Assert.True(RequireTwoFactorForAdminMiddleware.RolesRequire2Fa(
                new[] { "admin" }, new[] { "Admin" }));
        }

        [Fact]
        public void RolesRequire2Fa_ConfiguredExtraRole_True()
        {
            Assert.True(RequireTwoFactorForAdminMiddleware.RolesRequire2Fa(
                new[] { "Finance" }, new[] { "Admin", "Finance" }));
        }

        [Fact]
        public void RolesRequire2Fa_NoOverlap_False()
        {
            Assert.False(RequireTwoFactorForAdminMiddleware.RolesRequire2Fa(
                new[] { "Operator", "Viewer" }, new[] { "Admin" }));
        }

        [Fact]
        public void RolesRequire2Fa_NoRoles_False()
        {
            Assert.False(RequireTwoFactorForAdminMiddleware.RolesRequire2Fa(
                System.Array.Empty<string>(), new[] { "Admin" }));
        }

        // ── 強制開關（開發/測試環境可全域停用）──────────────────────────────────────
        [Fact]
        public void ResolveRequiredRoles_EnforcementDisabled_ReturnsEmpty_RegardlessOfConfiguredRoles()
        {
            Assert.Empty(RequireTwoFactorForAdminMiddleware.ResolveRequiredRoles(
                enforcementEnabled: false, configuredRoles: new[] { "Admin", "Finance" }));
        }

        [Fact]
        public void ResolveRequiredRoles_EnforcementEnabled_NoConfiguredRoles_DefaultsToAdmin()
        {
            Assert.Equal(new[] { "Admin" }, RequireTwoFactorForAdminMiddleware.ResolveRequiredRoles(
                enforcementEnabled: true, configuredRoles: null));
        }

        [Fact]
        public void ResolveRequiredRoles_EnforcementEnabled_EmptyConfiguredRoles_DefaultsToAdmin()
        {
            Assert.Equal(new[] { "Admin" }, RequireTwoFactorForAdminMiddleware.ResolveRequiredRoles(
                enforcementEnabled: true, configuredRoles: System.Array.Empty<string>()));
        }

        [Fact]
        public void ResolveRequiredRoles_EnforcementEnabled_ConfiguredRoles_UsesConfigured()
        {
            Assert.Equal(new[] { "Admin", "Finance" }, RequireTwoFactorForAdminMiddleware.ResolveRequiredRoles(
                enforcementEnabled: true, configuredRoles: new[] { "Admin", "Finance" }));
        }
    }
}
