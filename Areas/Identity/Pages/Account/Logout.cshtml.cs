using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Areas.Identity.Pages.Account
{
    public class LogoutModel : PageModel
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly ApplicationDbContext _db;

        public LogoutModel(SignInManager<ApplicationUser> signInManager, ApplicationDbContext db)
        {
            _signInManager = signInManager;
            _db = db;
        }

        public async Task<IActionResult> OnPost(string? returnUrl = null)
        {
            var userId = _signInManager.UserManager.GetUserId(User);
            var userName = User.Identity?.Name;

            await _signInManager.SignOutAsync();

            _db.AuditLogs.Add(new AuditLog
            {
                Timestamp = DateTime.UtcNow,
                Action = AuditAction.Logout,
                EntityType = "Authentication",
                UserId = userId,
                UserName = userName,
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = HttpContext.Request.Headers["User-Agent"].ToString()
            });
            await _db.SaveChangesAsync();

            returnUrl ??= Url.Content("~/");
            return LocalRedirect(returnUrl);
        }
    }
}
