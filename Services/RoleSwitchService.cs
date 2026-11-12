using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services;

public enum RoleSwitchOutcome { Switched, NotAllowed, UserNotFound, SaveFailed }

public interface IRoleSwitchService
{
    Task<RoleSwitchOutcome> TrySwitchAsync(ClaimsPrincipal principal, string roleId);
}

/// <summary>Both desktop routes change role and plant through the same authorization boundary.</summary>
public sealed class RoleSwitchService(
    ApplicationDbContext db, UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn) : IRoleSwitchService
{
    public async Task<RoleSwitchOutcome> TrySwitchAsync(ClaimsPrincipal principal, string roleId)
    {
        var user = await users.GetUserAsync(principal);
        if (user == null) return RoleSwitchOutcome.UserNotFound;
        if (string.IsNullOrWhiteSpace(roleId) ||
            !await db.UserRoles.AnyAsync(x => x.UserId == user.Id && x.RoleId == roleId))
            return RoleSwitchOutcome.NotAllowed;

        var role = await db.Roles.Include(x => x.PlantAssignments)
            .SingleOrDefaultAsync(x => x.Id == roleId);
        if (role == null) return RoleSwitchOutcome.NotAllowed;

        var oldRole = user.ActiveRoleId;
        var oldPlant = user.ActivePlantId;
        user.ActiveRoleId = role.Id;
        if (role.PlantAssignments.Count > 0 &&
            !role.PlantAssignments.Any(x => x.PlantId == user.ActivePlantId))
            user.ActivePlantId = role.PlantAssignments.OrderByDescending(x => x.IsDefault)
                .ThenBy(x => x.PlantId).First().PlantId;

        try
        {
            var result = await users.UpdateAsync(user);
            if (!result.Succeeded)
            {
                user.ActiveRoleId = oldRole;
                user.ActivePlantId = oldPlant;
                return RoleSwitchOutcome.SaveFailed;
            }
        }
        catch (DbUpdateException)
        {
            user.ActiveRoleId = oldRole;
            user.ActivePlantId = oldPlant;
            return RoleSwitchOutcome.SaveFailed;
        }

        await signIn.RefreshSignInAsync(user);
        return RoleSwitchOutcome.Switched;
    }
}
