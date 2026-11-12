using Microsoft.Extensions.Localization;

namespace SmartFactory.MES.Services
{
    public class CoreMenuContributor : IMenuContributor
    {
        private readonly IStringLocalizer<SharedResource> _localizer;

        public int Order => 0;

        /// <summary>
        /// 只限作用中角色為 Admin（見 <see cref="AdminAccess"/>）：帳號、角色、組織、廠別、權限設定、2FA 重置。
        /// 這些畫面能改的是「誰有什麼權限」，交給權限矩陣等於讓能進權限設定的人替自己升權，
        /// 所以不列入權限矩陣、側邊欄也只在作用中角色為 Admin 時顯示。每次取新清單，避免共用的 List 被改動。
        /// </summary>
        private static List<string> AdminOnly => new() { AdminAccess.RoleName };

        public CoreMenuContributor(IStringLocalizer<SharedResource> localizer)
        {
            _localizer = localizer;
        }

        public List<MenuItem> GetMenuItems()
        {
            return new List<MenuItem>
            {
                new MenuItem { Title = _localizer["Home"], Page = "/Index", Icon = "bi-house-door", MenuKey = "Home", SupportedActions = new List<string> { "Read" } },

                new MenuItem
                {
                    Title = _localizer["AdminManagement"],
                    Icon = "bi-shield-lock",
                    MenuKey = "AdminManagement",
                    SubItems = new List<MenuItem>
                    {
                        new MenuItem { Title = _localizer["UserManagement"], Controller = "User", Action = "Index", Icon = "bi-people", MenuKey = "UserManagement", Roles = AdminOnly },
                        new MenuItem { Title = _localizer["RoleManagement"], Controller = "Role", Action = "Index", Icon = "bi-person-badge", MenuKey = "RoleManagement", Roles = AdminOnly },
                        new MenuItem { Title = _localizer["OrgStructure"], Controller = "Organization", Action = "Index", Icon = "bi-diagram-3", MenuKey = "OrgStructure", Roles = AdminOnly },
                        new MenuItem { Title = _localizer["PlantManagement"], Controller = "Plant", Action = "Index", Icon = "bi-building", MenuKey = "PlantManagement", Roles = AdminOnly },
                        new MenuItem {
                            Title = _localizer["PermissionSettings"],
                            Controller = "Permission",
                            Action = "Index",
                            Icon = "bi-key",
                            MenuKey = "PermissionSettings",
                           Roles = AdminOnly,
                            SupportedActions = new List<string> { "Read", "Update", "Print" }
                        },
                        new MenuItem {
                            Title = _localizer["MobilePermissionSettings"],
                            Controller = "MobilePermission",
                            Action = "Index",
                            Icon = "bi-phone",
                            MenuKey = "MobilePermissionSettings",
                           Roles = AdminOnly,
                            SupportedActions = new List<string> { "Read", "Update" }
                        },
                        new MenuItem {
                            Title = _localizer["AuditLog"],
                            Controller = "AuditLog",
                            Action = "Index",
                            Icon = "bi-clock-history",
                            MenuKey = "AuditLog",
                            SupportedActions = new List<string> { "Read", "Print" }
                        },
                        // M3：管理員重置他人 2FA（遺失驗證器救援）。Admin 走 catch-all 全權。
                        new MenuItem { Title = _localizer["TwoFactorAdmin"], Controller = "TwoFactorAdmin", Action = "Index", Icon = "bi-shield-lock", MenuKey = "TwoFactorAdmin", Roles = AdminOnly },
                        // 授權狀態：原本只在授權有狀況時的提示條上有連結，平常找不到入口，
                        // 安裝說明卻多處叫管理員到這一頁確認授權（2026-11-12 rc4 裝機回報）。
                        // 頁面本身人人可看（只顯示狀態），換發／匯入／確認等動作只限 Admin，入口也只給 Admin。
                        new MenuItem { Title = _localizer["LicenseStatus"], Controller = "License", Action = "Index", Icon = "bi-patch-check", MenuKey = "LicenseStatus", Roles = AdminOnly },
                        new MenuItem {
                            Title = _localizer["CodeEntry"],
                            Controller = "CodeEntry",
                            Action = "Index",
                            Icon = "bi-card-list",
                            MenuKey = "CodeEntry"
                        },
                        new MenuItem {
                            Title = _localizer["SystemParam"],
                            Controller = "SystemParam",
                            Action = "Index",
                            Icon = "bi-sliders",
                            MenuKey = "SystemParam",
                            SupportedActions = new List<string> { "Read", "Update" }
                        }
                    }
                },

                new MenuItem
                {
                    Title = _localizer["ApprovalManagement"],
                    Icon = "bi-pen",
                    MenuKey = "ApprovalManagement",
                    SubItems = new List<MenuItem>
                    {
                        new MenuItem
                        {
                            Title = _localizer["PendingApprovals"],
                            Controller = "Approval",
                            Action = "PendingIndex",
                            Icon = "bi-pen-fill",
                            MenuKey = "PendingApprovals",
                            SupportedActions = new List<string> { "Read", "Update" }
                        },
                        new MenuItem
                        {
                            Title = _localizer["AllApprovalRequests"],
                            Controller = "Approval",
                            Action = "AllRequests",
                            Icon = "bi-list-check",
                            MenuKey = "AllApprovalRequests",
                            SupportedActions = new List<string> { "Read", "Update" }
                        },
                        new MenuItem
                        {
                            Title = _localizer["ApprovalTemplateSetting"],
                            Controller = "ApprovalTemplate",
                            Action = "Index",
                            Icon = "bi-diagram-2",
                            MenuKey = "ApprovalTemplateSetting"
                        },
                        new MenuItem
                        {
                            Title = _localizer["DocumentApprovalConfig"],
                            Controller = "DocumentApprovalConfig",
                            Action = "Index",
                            Icon = "bi-gear-wide-connected",
                            MenuKey = "DocumentApprovalConfig"
                        },
                    }
                },

            };
        }
    }
}
