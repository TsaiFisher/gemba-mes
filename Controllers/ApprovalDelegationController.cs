using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Controllers
{
    /// <summary>
    /// 簽核代理人自助設定（休假委派）。
    /// 個人層級設定，任何登入使用者都可維護「自己的」代理人，不需選單權限
    /// （語言偏好同級的個人設定）；他人的設定一律不可見、不可改。
    /// 設定生效（IsActive 且涵蓋今日）時即時將既有待簽單改派給代理人
    /// （ApprovalService.ApplyDelegationToPendingAsync），新單由送審/推進時解析。
    /// </summary>
    [Authorize]
    public class ApprovalDelegationController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IApprovalService _approvalService;
        private readonly IDateTimeService _dateTimeService;

        public ApprovalDelegationController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IApprovalService approvalService,
            IDateTimeService dateTimeService)
        {
            _context = context;
            _userManager = userManager;
            _approvalService = approvalService;
            _dateTimeService = dateTimeService;
        }

        private DateOnly Today => DateOnly.FromDateTime(_dateTimeService.Now);

        [PermissionGateExempt("只顯示呼叫者自己設定的代理，以及目前代理到自己身上的委託（皆以 user.Id 過濾）")]
        public async Task<IActionResult> Index(string? edit)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Challenge();

            var mine = await _context.ApprovalDelegations.AsNoTracking()
                .Where(d => d.DelegatorId == user.Id)
                .OrderByDescending(d => d.StartDate)
                .ToListAsync();

            // 我目前代理誰（僅顯示生效中的，供對照）
            var today = Today;
            var delegatedToMe = await _context.ApprovalDelegations.AsNoTracking()
                .Where(d => d.DelegateId == user.Id && d.IsActive
                         && d.StartDate <= today && d.EndDate >= today)
                .ToListAsync();

            var userIds = mine.Select(d => d.DelegateId)
                .Concat(delegatedToMe.Select(d => d.DelegatorId))
                .Distinct().ToList();
            ViewBag.UserNames = await _context.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);

            ViewBag.UserOptions = await _context.Users.AsNoTracking()
                .Where(u => u.Id != user.Id)
                .OrderBy(u => u.UserName)
                .Select(u => new { u.Id, u.UserName })
                .ToListAsync();

            ViewBag.Today = today;
            ViewBag.DelegatedToMe = delegatedToMe;
            ViewBag.EditModel = !string.IsNullOrEmpty(edit)
                ? mine.FirstOrDefault(d => d.Id == edit)
                : null;
            return View(mine);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(ApprovalDelegation input)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Challenge();

            var error = await ValidateAsync(user.Id, input);
            if (error != null)
            {
                TempData["ErrorMessage"] = error;
                return RedirectToAction(nameof(Index));
            }

            ApprovalDelegation target;
            if (string.IsNullOrEmpty(input.Id)
                || await _context.ApprovalDelegations.FindAsync(input.Id) is not { } existing)
            {
                target = new ApprovalDelegation { DelegatorId = user.Id };
                _context.ApprovalDelegations.Add(target);
            }
            else
            {
                if (existing.DelegatorId != user.Id) return Forbid(); // 只能改自己的設定
                target = existing;
            }

            target.DelegateId = input.DelegateId;
            target.StartDate = input.StartDate;
            target.EndDate = input.EndDate;
            target.IsActive = input.IsActive;
            target.Reason = input.Reason;

            await _context.SaveChangesAsync();
            var moved = await SweepIfEffectiveAsync(user.Id, target);
            TempData["SuccessMessage"] = moved > 0 ? CoreText.Get("Msg_DelegationSavedMovedFmt", moved) : CoreText.Get("Msg_DelegationSaved");
            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Toggle(string id)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Challenge();

            var delegation = await _context.ApprovalDelegations.FindAsync(id);
            if (delegation == null) return NotFound();
            if (delegation.DelegatorId != user.Id) return Forbid();

            delegation.IsActive = !delegation.IsActive;
            await _context.SaveChangesAsync();
            var moved = delegation.IsActive ? await SweepIfEffectiveAsync(user.Id, delegation) : 0;
            TempData["SuccessMessage"] = delegation.IsActive
                ? (moved > 0 ? CoreText.Get("Msg_DelegationEnabledMovedFmt", moved) : CoreText.Get("Msg_DelegationEnabled"))
                : CoreText.Get("Msg_DelegationDisabled");
            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Challenge();

            var delegation = await _context.ApprovalDelegations.FindAsync(id);
            if (delegation == null) return NotFound();
            if (delegation.DelegatorId != user.Id) return Forbid();

            _context.ApprovalDelegations.Remove(delegation);
            await _context.SaveChangesAsync();
            TempData["SuccessMessage"] = CoreText.Get("Msg_DelegationDeleted");
            return RedirectToAction(nameof(Index));
        }

        private async Task<string?> ValidateAsync(string delegatorId, ApprovalDelegation input)
        {
            if (string.IsNullOrEmpty(input.DelegateId))
                return CoreText.Get("Msg_DelegateRequired");
            if (input.DelegateId == delegatorId)
                return CoreText.Get("Msg_DelegateNotSelf");
            if (!await _context.Users.AsNoTracking().AnyAsync(u => u.Id == input.DelegateId))
                return CoreText.Get("Msg_DelegateNotFound");
            if (input.EndDate < input.StartDate)
                return CoreText.Get("Msg_DelegationEndBeforeStart");

            // 同一時段僅允許一筆生效設定（避免解析歧義；停用中的設定不擋）
            if (input.IsActive && await _context.ApprovalDelegations.AsNoTracking()
                    .AnyAsync(d => d.DelegatorId == delegatorId && d.Id != input.Id && d.IsActive
                                && d.StartDate <= input.EndDate && d.EndDate >= input.StartDate))
                return CoreText.Get("Msg_DelegationOverlap");

            return null;
        }

        /// <summary>設定當下已生效（IsActive 且涵蓋今日）→ 即時改派既有待簽單。</summary>
        private async Task<int> SweepIfEffectiveAsync(string delegatorId, ApprovalDelegation delegation)
        {
            if (!delegation.IsActive) return 0;
            var today = Today;
            if (delegation.StartDate > today || delegation.EndDate < today) return 0;
            return await _approvalService.ApplyDelegationToPendingAsync(delegatorId, delegatorId);
        }
    }
}
