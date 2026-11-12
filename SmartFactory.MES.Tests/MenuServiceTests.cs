using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    public class MenuServiceTests
    {

        /// <summary>選單設定：報表工具開關開啟，讓 Phase 0-B 的三支畫面納入測試範圍。</summary>
        private static IConfiguration MenuConfig() =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [SmartFactory.MES.Modules.RPS.ReportToolsFeature.ConfigKey] = "true",
                })
                .Build();
        private readonly Mock<IStringLocalizer<SharedResource>> _mockLocalizer;
        private readonly MenuService _menuService;

        public MenuServiceTests()
        {
            _mockLocalizer = new Mock<IStringLocalizer<SharedResource>>();
            _mockLocalizer.Setup(l => l[It.IsAny<string>()])
                .Returns((string key) => new LocalizedString(key, key));

            var mockPzLocalizer = new Mock<IStringLocalizer<ProjectZSharedResource>>();
            mockPzLocalizer.Setup(l => l[It.IsAny<string>()])
                .Returns((string key) => new LocalizedString(key, key));

            var contributors = new IMenuContributor[]
            {
                new CoreMenuContributor(_mockLocalizer.Object),
                new ProjectZMenuContributor(mockPzLocalizer.Object, MenuConfig()),
                new UserSettingsMenuContributor(_mockLocalizer.Object)
            };
            _menuService = new MenuService(contributors);
        }

        [Fact]
        public void GetFullMenu_ReturnsExpectedStructure()
        {
            // Act
            var menu = _menuService.GetFullMenu();

            // Assert
            Assert.NotEmpty(menu);
            
            // Verify Home
            var home = menu.First(m => m.MenuKey == "Home");
            Assert.Equal("/Index", home.Page);
            
            // Verify AdminManagement
            var admin = menu.First(m => m.MenuKey == "AdminManagement");
            Assert.NotEmpty(admin.SubItems);
            Assert.Contains(admin.SubItems, m => m.MenuKey == "UserManagement");
            Assert.Contains(admin.SubItems, m => m.MenuKey == "RoleManagement");
        }

        [Fact]
        public void GetFlattenedAccessibleMenus_ReturnsFlatList()
        {
            // Act
            var flatMenu = _menuService.GetFlattenedAccessibleMenus();

            // Assert
            Assert.NotEmpty(flatMenu);
            
            // Should not contain container items that don't have a page/controller (like AdminManagement)
            // AdminManagement has no Controller/Action/Page in the definition, just SubItems
            Assert.DoesNotContain(flatMenu, m => m.MenuKey == "AdminManagement");

            // Should contain leaf items
            Assert.Contains(flatMenu, m => m.MenuKey == "UserManagement");
            Assert.Contains(flatMenu, m => m.MenuKey == "Home");
        }

        [Fact]
        public void FlattenedMenu_ItemsHaveCorrectKeys()
        {
             // Act
            var flatMenu = _menuService.GetFlattenedAccessibleMenus();

            // Assert
            var userMgmt = flatMenu.FirstOrDefault(m => m.MenuKey == "UserManagement");
            Assert.NotNull(userMgmt);
            Assert.Equal("User", userMgmt.Controller);
            Assert.Equal("Index", userMgmt.Action);
        }

        [Fact]
        public void BasManagement_ContainsProductCategoryWithCorrectKey()
        {
            var menu = _menuService.GetFullMenu();
            var bas = menu.First(m => m.MenuKey == "BasManagement");

            var item = bas.SubItems.FirstOrDefault(m => m.MenuKey == "ProductCategory");
            Assert.NotNull(item);
            Assert.Equal("ProductCategory", item.Controller);
        }

        [Fact]
        public void BasManagement_ContainsDocNumberConfig()
        {
            var menu = _menuService.GetFullMenu();
            var bas = menu.First(m => m.MenuKey == "BasManagement");

            var item = bas.SubItems.FirstOrDefault(m => m.MenuKey == "DocNumberConfig");
            Assert.NotNull(item);
            Assert.Equal("DocNumberConfig", item.Controller);
        }

        [Fact]
        public void BasManagement_ProductCategoryAppearsBeforeProductMaster()
        {
            var menu = _menuService.GetFullMenu();
            var bas = menu.First(m => m.MenuKey == "BasManagement");

            var catIdx  = bas.SubItems.FindIndex(m => m.MenuKey == "ProductCategory");
            var prodIdx = bas.SubItems.FindIndex(m => m.MenuKey == "ProductMaster");

            Assert.True(catIdx >= 0 && prodIdx >= 0, "兩個項目均應存在");
            Assert.True(catIdx < prodIdx, "ProductCategory 應在 ProductMaster 前");
        }

        [Fact]
        public void FlattenedMenu_ProductCategoryMenuKeyMatchesController()
        {
            // ProductCategoryController 使用 MenuKey = "ProductCategory"
            // MenuService 中登錄的 MenuKey 必須一致，否則權限對不上
            var flatMenu = _menuService.GetFlattenedAccessibleMenus();
            var item = flatMenu.FirstOrDefault(m => m.Controller == "ProductCategory");
            Assert.NotNull(item);
            Assert.Equal("ProductCategory", item.MenuKey);  // 不是 "ProductCategoryMaster"
        }

    
        /// <summary>部署專案把畫面掛進 Core 的群組：宣告同 MenuKey 的群組，子項接在 Core 子項之後。</summary>
        private sealed class ExtraAdminContributor : IMenuContributor
        {
            public int Order => 10;
            public List<MenuItem> GetMenuItems() => new()
            {
                new MenuItem
                {
                    Title = "Admin (dup)", MenuKey = "AdminManagement",
                    SubItems = new List<MenuItem> { new() { Title = "X", Controller = "Extra", Action = "Index", MenuKey = "ExtraScreen" } }
                },
                new MenuItem { Title = "Own", MenuKey = "OwnGroup", SubItems = new List<MenuItem> { new() { Title = "Y", Controller = "Own", Action = "Index", MenuKey = "OwnScreen" } } },
            };
        }

        [Fact]
        public void GetFullMenu_SameKeyGroupsFromDifferentContributors_MergeIntoFirst()
        {
            var localizer = new Mock<IStringLocalizer<SharedResource>>();
            localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));
            var svc = new MenuService(new IMenuContributor[] { new ExtraAdminContributor(), new CoreMenuContributor(localizer.Object) });

            var menu = svc.GetFullMenu();

            // 側邊欄與權限矩陣只會有一個「管理系統」，否則部署專案的子項會長成第二個同名群組
            var admins = menu.Where(m => m.MenuKey == "AdminManagement").ToList();
            Assert.Single(admins);
            var keys = admins[0].SubItems.Select(s => s.MenuKey).ToList();
            Assert.Equal("UserManagement", keys[0]);           // 依 Order：Core 先、部署專案後
            Assert.Equal("ExtraScreen", keys[^1]);
            Assert.Equal("AdminManagement", admins[0].Title);  // 標題取最先出現的那個
            Assert.Contains(menu, m => m.MenuKey == "OwnGroup");
            Assert.Single(svc.GetFlattenedAccessibleMenus(), m => m.MenuKey == "ExtraScreen");
        }

        private sealed class FixedContributor(List<MenuItem> items) : IMenuContributor
        {
            public int Order => 0;
            public List<MenuItem> GetMenuItems() => items;
        }

        [Fact]
        public void GetFullMenu_RepeatedCalls_DoNotMutateContributorItems()
        {
            var localizer = new Mock<IStringLocalizer<SharedResource>>();
            localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));
            // 回傳同一份清單的 contributor（例如自行快取）：合併若改到它的群組，每呼叫一次就多接一輪
            var cached = new CoreMenuContributor(localizer.Object).GetMenuItems();
            var before = cached.First(m => m.MenuKey == "AdminManagement").SubItems.Count;

            var svc = new MenuService(new IMenuContributor[] { new FixedContributor(cached), new ExtraAdminContributor() });
            svc.GetFullMenu();
            svc.GetFullMenu();

            // 合併不可回寫到 contributor 回傳的物件（多次呼叫不累加）
            Assert.Equal(before + 1, svc.GetFullMenu().Single(m => m.MenuKey == "AdminManagement").SubItems.Count);
        }
    }
}
