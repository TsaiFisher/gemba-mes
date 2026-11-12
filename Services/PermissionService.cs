using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services
{
    public interface IPermissionService
    {
        Task<bool> CanViewData(string ownerRoleId);
        IQueryable<T> FilterByPermission<T>(IQueryable<T> query) where T : class, IOwnedByRole;

        Task<bool> CanRead(string menuKey);
        Task<bool> CanCreate(string menuKey);
        Task<bool> CanUpdate(string menuKey);
        Task<bool> CanDelete(string menuKey);
        Task<bool> CanPrint(string menuKey);
    }

    public interface IOwnedByRole
    {
        string? OwnerRoleId { get; set; }
    }

    public class PermissionService : IPermissionService
    {
        private readonly ApplicationDbContext _db;
        private readonly IUserContext _userContext;
        private readonly ISecurityContext _securityContext;

        public PermissionService(
            ApplicationDbContext db,
            IUserContext userContext,
            ISecurityContext securityContext)
        {
            _db = db;
            _userContext = userContext;
            _securityContext = securityContext;
        }

        public async Task<bool> CanViewData(string ownerRoleId)
        {
            var activeRoleId = _userContext.ActiveRoleId;
            if (string.IsNullOrEmpty(activeRoleId)) return false;
            if (activeRoleId == ownerRoleId) return true;

            var currentRole = await _db.Roles
                .Include(r => r.OrganizationUnit)
                .FirstOrDefaultAsync(r => r.Id == activeRoleId);

            var targetRole = await _db.Roles
                .Include(r => r.OrganizationUnit)
                .FirstOrDefaultAsync(r => r.Id == ownerRoleId);

            if (currentRole == null || targetRole == null) return false;

            if (currentRole.Name == "Admin" && currentRole.OrganizationUnitId == null) return true;

            if (targetRole.OrganizationUnit != null && currentRole.OrganizationUnit != null)
            {
                string currentPath = currentRole.OrganizationUnit.Path ?? "";
                string targetPath  = targetRole.OrganizationUnit.Path ?? "";
                if (targetPath.StartsWith(currentPath) && targetPath != currentPath)
                    return true;
            }

            return false;
        }

        public IQueryable<T> FilterByPermission<T>(IQueryable<T> query) where T : class, IOwnedByRole
        {
            var activeRoleId = _userContext.ActiveRoleId;
            if (string.IsNullOrEmpty(activeRoleId)) return query.Where(x => false);
            return query;
        }

        // Scoped service: one immutable permission snapshot per role and request.
        // The role is part of the key because a request may switch roles.
        private readonly Dictionary<string, Task<Dictionary<string, MenuPermission>>> _snapshots = new();

        private async Task<bool> HasPermission(string menuKey, Func<MenuPermission, bool> allows)
        {
            if (!IsModuleLicensed(menuKey)) return false;
            var roleId = _userContext.ActiveRoleId;
            if (string.IsNullOrEmpty(roleId)) return false;
            if (!_snapshots.TryGetValue(roleId, out var pending))
            {
                pending = _db.MenuPermissions.AsNoTracking().Where(p => p.RoleId == roleId)
                    .ToDictionaryAsync(p => p.MenuKey);
                _snapshots.Add(roleId, pending);
            }
            var permissions = await pending;
            return permissions.TryGetValue(menuKey, out var permission) && allows(permission);
        }

        public Task<bool> CanRead(string menuKey) => HasPermission(menuKey, p => p.CanRead);
        public Task<bool> CanCreate(string menuKey) => HasPermission(menuKey, p => p.CanCreate);
        public Task<bool> CanUpdate(string menuKey) => HasPermission(menuKey, p => p.CanUpdate);
        public Task<bool> CanDelete(string menuKey) => HasPermission(menuKey, p => p.CanDelete);
        public Task<bool> CanPrint(string menuKey) => HasPermission(menuKey, p => p.CanPrint);

        // 模組授權由 Repo 隔離機制管控（每個企業獨立 Repo），不在 License 層限制。
        private static bool IsModuleLicensed(string menuKey) => true;
    }
}
