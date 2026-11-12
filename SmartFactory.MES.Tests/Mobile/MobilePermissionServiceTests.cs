using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.Mobile
{
    public class MobilePermissionServiceTests : TestBase
    {
        private readonly IMobilePermissionService _sut;

        public MobilePermissionServiceTests()
        {
            _sut = new MobilePermissionService(_context);
            SeedData();
        }

        private void SeedData()
        {
            // 模組：工單執行
            var module = new MobileModule
            {
                Id = "MOD-WO",
                ModuleKey = "WorkOrderExecution",
                DisplayName = "工單執行",
                IsActive = true,
                SortOrder = 1
            };
            _context.MobileModules.Add(module);

            // 作業項目
            var actionStart = new MobileModuleAction { Id = "ACT-START", ModuleId = "MOD-WO", ActionKey = "Start", DisplayName = "開始" };
            var actionComplete = new MobileModuleAction { Id = "ACT-COMPLETE", ModuleId = "MOD-WO", ActionKey = "Complete", DisplayName = "完工" };
            var actionScan = new MobileModuleAction { Id = "ACT-SCAN", ModuleId = "MOD-WO", ActionKey = "Scan", DisplayName = "掃描" };
            _context.MobileModuleActions.AddRange(actionStart, actionComplete, actionScan);

            // 角色
            _context.Roles.Add(new ApplicationRole { Id = "ROLE-OP", Name = "Operator", NormalizedName = "OPERATOR" });
            _context.Roles.Add(new ApplicationRole { Id = "ROLE-MG", Name = "Manager", NormalizedName = "MANAGER" });

            // 操機員：允許 Start + Scan，不允許 Complete
            _context.MobileRolePermissions.AddRange(
                new MobileRolePermission { RoleId = "ROLE-OP", ModuleActionId = "ACT-START", IsAllowed = true },
                new MobileRolePermission { RoleId = "ROLE-OP", ModuleActionId = "ACT-COMPLETE", IsAllowed = false },
                new MobileRolePermission { RoleId = "ROLE-OP", ModuleActionId = "ACT-SCAN", IsAllowed = true }
            );

            // 主管：全部允許
            _context.MobileRolePermissions.AddRange(
                new MobileRolePermission { RoleId = "ROLE-MG", ModuleActionId = "ACT-START", IsAllowed = true },
                new MobileRolePermission { RoleId = "ROLE-MG", ModuleActionId = "ACT-COMPLETE", IsAllowed = true },
                new MobileRolePermission { RoleId = "ROLE-MG", ModuleActionId = "ACT-SCAN", IsAllowed = true }
            );

            _context.SaveChanges();
        }

        [Fact]
        public async Task GetAllowedModulesAsync_WithAllowedRole_ReturnsModules()
        {
            var result = await _sut.GetAllowedModulesAsync("ROLE-OP");
            Assert.Single(result);
            Assert.Equal("WorkOrderExecution", result.First().ModuleKey);
        }

        [Fact]
        public async Task GetAllowedModulesAsync_WithUnknownRole_ReturnsEmpty()
        {
            var result = await _sut.GetAllowedModulesAsync("ROLE-UNKNOWN");
            Assert.Empty(result);
        }

        [Fact]
        public async Task GetAllowedModulesAsync_WithEmptyRoleId_ReturnsEmpty()
        {
            var result = await _sut.GetAllowedModulesAsync(string.Empty);
            Assert.Empty(result);
        }

        [Fact]
        public async Task CanExecuteAsync_AllowedAction_ReturnsTrue()
        {
            var result = await _sut.CanExecuteAsync("ROLE-OP", "WorkOrderExecution", "Start");
            Assert.True(result);
        }

        [Fact]
        public async Task CanExecuteAsync_DeniedAction_ReturnsFalse()
        {
            var result = await _sut.CanExecuteAsync("ROLE-OP", "WorkOrderExecution", "Complete");
            Assert.False(result);
        }

        [Fact]
        public async Task CanExecuteAsync_UnknownModuleKey_ReturnsFalse()
        {
            var result = await _sut.CanExecuteAsync("ROLE-OP", "UnknownModule", "Start");
            Assert.False(result);
        }

        [Fact]
        public async Task CanExecuteAsync_ManagerHasAllActions_ReturnsTrue()
        {
            Assert.True(await _sut.CanExecuteAsync("ROLE-MG", "WorkOrderExecution", "Start"));
            Assert.True(await _sut.CanExecuteAsync("ROLE-MG", "WorkOrderExecution", "Complete"));
            Assert.True(await _sut.CanExecuteAsync("ROLE-MG", "WorkOrderExecution", "Scan"));
        }

        [Fact]
        public async Task GetAllowedActionKeysAsync_ReturnsOnlyAllowedKeys()
        {
            var result = (await _sut.GetAllowedActionKeysAsync("ROLE-OP", "WorkOrderExecution")).ToList();
            Assert.Equal(2, result.Count);
            Assert.Contains("Start", result);
            Assert.Contains("Scan", result);
            Assert.DoesNotContain("Complete", result);
        }

        [Fact]
        public async Task GetAllowedActionKeysAsync_InactiveModule_ReturnsEmpty()
        {
            // 停用模組
            var mod = _context.MobileModules.Find("MOD-WO")!;
            mod.IsActive = false;
            _context.SaveChanges();

            var result = await _sut.GetAllowedActionKeysAsync("ROLE-OP", "WorkOrderExecution");
            Assert.Empty(result);
        }
    }
}
