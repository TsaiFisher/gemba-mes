using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.ViewComponents
{
    /// <summary>
    /// 授權狀態橫幅（SECURITY-OPERATIONS.md §18.8）：唯讀／停用對所有人顯示（不然存檔被擋時不知原因）；
    /// 提醒只給 Admin（換發是管理員的事，一般使用者看了也做不了什麼）。
    /// </summary>
    public class LicenseBannerViewComponent(ILicenseStateService state) : ViewComponent
    {
        public IViewComponentResult Invoke()
        {
            var s = state.Current;
            var show = s.IsWriteBlocked || (s.State == LicenseState.Warning && User.IsInRole("Admin"));
            return show ? View(s) : Content(string.Empty);
        }
    }
}
