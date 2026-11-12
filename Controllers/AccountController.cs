using SmartFactory.MES.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Models;
using System.Security.Claims;

namespace SmartFactory.MES.Controllers
{
    [Authorize(Policy = "AuthenticatedUser")]
    public class AccountController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly IRoleSwitchService _roleSwitch;

        public AccountController(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager,
                                 IRoleSwitchService roleSwitch)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _roleSwitch = roleSwitch;
        }

        [HttpGet]
        public IActionResult ForceChangePassword() => View();

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForceChangePassword(string newPassword, string confirmPassword)
        {
            if (string.IsNullOrWhiteSpace(newPassword) || newPassword != confirmPassword)
            {
                ModelState.AddModelError(string.Empty, CoreText.Get("Msg_PasswordMismatchOrInvalid"));
                return View();
            }

            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();

            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var result = await _userManager.ResetPasswordAsync(user, token, newPassword);

            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                    ModelState.AddModelError(string.Empty, error.Description);
                return View();
            }

            user.MustChangePassword = false;
            await _userManager.UpdateAsync(user);
            await _signInManager.RefreshSignInAsync(user);

            return RedirectToAction("Index", "Home");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Route("/switch-role")]
        public async Task<IActionResult> SwitchRole(string roleId)
        {
            var outcome = await _roleSwitch.TrySwitchAsync(User, roleId);
            return outcome switch
            {
                RoleSwitchOutcome.Switched => LocalRedirect("/"),
                RoleSwitchOutcome.UserNotFound => NotFound(),
                RoleSwitchOutcome.NotAllowed => Forbid(),
                _ => BadRequest(CoreText.Get("Msg_SwitchRoleFailed"))
            };
        }
    }
}
