namespace SmartFactory.MES.Services
{
    public class MenuItem
    {
        public string Title { get; set; } = string.Empty;
        public string? Controller { get; set; }
        public string? Action { get; set; }
        public string? Page { get; set; }
        public string Icon { get; set; } = string.Empty;
        public string MenuKey { get; set; } = string.Empty;
        public List<string> Roles { get; set; } = new List<string>();
        public List<string> SupportedActions { get; set; } = new List<string> { "Read", "Create", "Update", "Delete", "Print" };
        public List<MenuItem> SubItems { get; set; } = new List<MenuItem>();
    }

    public interface IMenuService
    {
        List<MenuItem> GetFullMenu();
        List<MenuItem> GetFlattenedAccessibleMenus();
    }

    public class MenuService : IMenuService
    {
        private readonly IEnumerable<IMenuContributor> _contributors;

        public MenuService(IEnumerable<IMenuContributor> contributors)
        {
            _contributors = contributors;
        }

        /// <summary>
        /// 依 Order 串接各 contributor 的選單。不同 contributor 宣告**同一個 MenuKey 的群組**
        /// （沒有 Controller/Page、只有子項）時合併成一個：位置與標題取最先出現者，子項依序接在後面。
        /// 部署專案要把畫面掛進 Core 的群組（例如「管理系統」）就宣告同鍵群組，
        /// 不必改 CoreMenuContributor；不合併的話側邊欄與權限矩陣會長出兩個同名群組。
        /// </summary>
        public List<MenuItem> GetFullMenu()
        {
            var result = new List<MenuItem>();
            var groups = new Dictionary<string, MenuItem>();
            foreach (var item in _contributors.OrderBy(c => c.Order).SelectMany(c => c.GetMenuItems()))
            {
                if (!IsGroup(item))
                {
                    result.Add(item);
                    continue;
                }
                if (groups.TryGetValue(item.MenuKey, out var existing))
                {
                    existing.SubItems.AddRange(item.SubItems);
                    continue;
                }
                // 複製群組殼：合併只改複本，不回寫到 contributor 可能重複回傳的物件
                var shell = new MenuItem
                {
                    Title = item.Title, Icon = item.Icon, MenuKey = item.MenuKey, Roles = item.Roles,
                    SupportedActions = item.SupportedActions, SubItems = new List<MenuItem>(item.SubItems)
                };
                groups[item.MenuKey] = shell;
                result.Add(shell);
            }
            return result;
        }

        private static bool IsGroup(MenuItem item) =>
            !string.IsNullOrEmpty(item.MenuKey)
            && string.IsNullOrEmpty(item.Controller) && string.IsNullOrEmpty(item.Page);

        public List<MenuItem> GetFlattenedAccessibleMenus()
        {
            var flattened = new List<MenuItem>();
            Flatten(GetFullMenu(), flattened);
            return flattened.Where(m => !string.IsNullOrEmpty(m.Controller) || !string.IsNullOrEmpty(m.Page)).ToList();
        }

        private void Flatten(List<MenuItem> items, List<MenuItem> result)
        {
            foreach (var item in items)
            {
                result.Add(item);
                if (item.SubItems.Any())
                    Flatten(item.SubItems, result);
            }
        }
    }
}
