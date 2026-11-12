using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Api.Mobile
{
    /// <summary>手機版功能模組 API — 取得允許的模組與作業清單</summary>
    [Route("api/mobile/modules")]
    [ApiController]
    [Authorize(Policy = "AuthenticatedUser")]
    public class MobileModuleController : ControllerBase
    {
        private readonly IMobilePermissionService _mobilePermission;
        private readonly IUserContext _userContext;

        public MobileModuleController(
            IMobilePermissionService mobilePermission,
            IUserContext userContext)
        {
            _mobilePermission = mobilePermission;
            _userContext = userContext;
        }

        /// <summary>取得目前角色允許存取的功能模組清單（首頁格狀選單用）</summary>
        [HttpGet]
        public async Task<IActionResult> GetModules()
        {
            var roleId = _userContext.ActiveRoleId;
            if (string.IsNullOrEmpty(roleId))
                return Ok(new { modules = Array.Empty<object>() });

            var modules = await _mobilePermission.GetAllowedModulesAsync(roleId);

            return Ok(new
            {
                modules = modules.Select(m => new
                {
                    id = m.Id,
                    moduleKey = m.ModuleKey,
                    displayName = m.DisplayName,
                    iconClass = m.IconClass,
                    sortOrder = m.SortOrder
                })
            });
        }

        /// <summary>取得指定模組中目前角色允許的作業鍵清單（控制按鈕顯示用）</summary>
        [HttpGet("{moduleKey}/actions")]
        public async Task<IActionResult> GetAllowedActions(string moduleKey)
        {
            var roleId = _userContext.ActiveRoleId;
            if (string.IsNullOrEmpty(roleId))
                return Ok(new { allowedActions = Array.Empty<string>() });

            var actions = await _mobilePermission.GetAllowedActionKeysAsync(roleId, moduleKey);
            return Ok(new { allowedActions = actions });
        }
    }
}
