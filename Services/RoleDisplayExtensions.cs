using System.Globalization;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// Role 語系顯示名稱輔助擴充。語系直接讀 CultureInfo.CurrentUICulture（由 RequestLocalization cookie 驅動）；
    /// Controller / View 不得自行判斷語系。en-US 且 NameEn 有值時顯示英文，否則 fallback：NameZh → Name。
    /// </summary>
    public static class RoleDisplayExtensions
    {
        public static string GetDisplayName(this ApplicationRole role)
        {
            if (role == null) return string.Empty;
            bool useEn = CultureInfo.CurrentUICulture.Name == "en-US";
            if (useEn && !string.IsNullOrEmpty(role.NameEn)) return role.NameEn!;
            return role.NameZh ?? role.Name ?? string.Empty;
        }
    }
}
