using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services
{
    public interface IMobilePermissionService
    {
        /// <summary>取得角色可存取的手機模組清單（首頁顯示用）</summary>
        Task<IEnumerable<MobileModule>> GetAllowedModulesAsync(string roleId);

        /// <summary>判斷角色對特定作業是否允許（按鈕顯示 + API 二次驗證用）</summary>
        Task<bool> CanExecuteAsync(string roleId, string moduleKey, string actionKey);

        /// <summary>取得角色在指定模組中允許的作業鍵清單</summary>
        Task<IEnumerable<string>> GetAllowedActionKeysAsync(string roleId, string moduleKey);
    }

    public class MobilePermissionService : IMobilePermissionService
    {
        private readonly ApplicationDbContext _db;

        public MobilePermissionService(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<IEnumerable<MobileModule>> GetAllowedModulesAsync(string roleId)
        {
            if (string.IsNullOrEmpty(roleId))
                return Enumerable.Empty<MobileModule>();

            // 取出該角色在任何作業上有 IsAllowed=true 的模組
            var allowedModuleIds = await _db.MobileRolePermissions
                .IgnoreQueryFilters()
                .Where(p => p.RoleId == roleId && p.IsAllowed)
                .Select(p => p.ModuleAction.ModuleId)
                .Distinct()
                .ToListAsync();

            return await _db.MobileModules
                .IgnoreQueryFilters()
                .Where(m => m.IsActive && allowedModuleIds.Contains(m.Id))
                .OrderBy(m => m.SortOrder)
                .ThenBy(m => m.DisplayName)
                .ToListAsync();
        }

        public async Task<bool> CanExecuteAsync(string roleId, string moduleKey, string actionKey)
        {
            if (string.IsNullOrEmpty(roleId))
                return false;

            return await _db.MobileRolePermissions
                .IgnoreQueryFilters()
                .AnyAsync(p =>
                    p.RoleId == roleId &&
                    p.IsAllowed &&
                    p.ModuleAction.ActionKey == actionKey &&
                    p.ModuleAction.Module.ModuleKey == moduleKey &&
                    p.ModuleAction.Module.IsActive);
        }

        public async Task<IEnumerable<string>> GetAllowedActionKeysAsync(string roleId, string moduleKey)
        {
            if (string.IsNullOrEmpty(roleId))
                return Enumerable.Empty<string>();

            return await _db.MobileRolePermissions
                .IgnoreQueryFilters()
                .Where(p =>
                    p.RoleId == roleId &&
                    p.IsAllowed &&
                    p.ModuleAction.Module.ModuleKey == moduleKey &&
                    p.ModuleAction.Module.IsActive)
                .Select(p => p.ModuleAction.ActionKey)
                .ToListAsync();
        }
    }
}
