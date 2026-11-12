using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.ViewComponents
{
    /// <summary>
    /// 導覽列「待我簽核」數字 badge：有待簽項目時顯示鉛筆圖示與筆數，點擊導向 PendingIndex。
    /// 判定語意與 ApprovalService.GetMyPendingRequestsAsync 一致（同一個查詢）。
    /// </summary>
    public class PendingApprovalBadgeViewComponent : ViewComponent
    {
        private readonly IApprovalService _approvalService;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly Data.ApplicationDbContext _context;

        public PendingApprovalBadgeViewComponent(
            IApprovalService approvalService,
            UserManager<ApplicationUser> userManager,
            Data.ApplicationDbContext context)
        {
            _approvalService = approvalService;
            _userManager = userManager;
            _context = context;
        }

        public async Task<IViewComponentResult> InvokeAsync()
        {
            var userId = _userManager.GetUserId(HttpContext.User);
            if (string.IsNullOrEmpty(userId)) return Content(string.Empty);

            var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return Content(string.Empty);

            var count = await _approvalService.CountMyPendingAsync(user.Id, user.ActiveRoleId);
            return View(count);
        }
    }
}
