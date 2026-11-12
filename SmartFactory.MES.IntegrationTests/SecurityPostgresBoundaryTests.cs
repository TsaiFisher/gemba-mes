using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests;

public sealed class IsolatedSecurityPostgresFactAttribute : FactAttribute
{
    public IsolatedSecurityPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MES_SECURITY_TEST_CONNECTION")))
            Skip = "Set MES_SECURITY_TEST_CONNECTION to an isolated localhost mes_security_* database.";
    }
}

/// <summary>Opt-in real PostgreSQL check. Never uses the application's configured database.</summary>
public class SecurityPostgresBoundaryTests
{
    [IsolatedSecurityPostgresFact]
    public async Task StalePlantIsDenied_AndPermissionSnapshotExecutesOneSelect()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MES_SECURITY_TEST_CONNECTION"));
        Assert.StartsWith("mes_security_", connection.Database);
        Assert.Contains(connection.Host, new[] { "localhost", "127.0.0.1", "::1" });
        var counter = new PermissionQueryCounter();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connection.ConnectionString).AddInterceptors(counter).Options);
        await db.Database.EnsureCreatedAsync();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var a = "A-" + suffix;
        var b = "B-" + suffix;
        var roleId = "R-" + suffix;
        var user = new ApplicationUser { Id = "U-" + suffix, UserName = "U-" + suffix,
            ActiveRoleId = roleId, ActivePlantId = b };
        db.Plants.AddRange(new Plant { Id = a, Name = a }, new Plant { Id = b, Name = b });
        db.Roles.Add(new ApplicationRole { Id = roleId, Name = roleId });
        db.Users.Add(user);
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = roleId });
        db.RolePlantAssignments.Add(new RolePlantAssignment { RoleId = roleId, PlantId = a });
        db.MenuPermissions.Add(new MenuPermission { RoleId = roleId, MenuKey = "review", CanRead = true });
        db.OrganizationUnits.AddRange(new OrganizationUnit { Id = "OA-" + suffix, Name = "A", PlantId = a },
            new OrganizationUnit { Id = "OB-" + suffix, Name = "B", PlantId = b });
        await db.SaveChangesAsync();
        using var users = new UserManager<ApplicationUser>(new UserStore<ApplicationUser, ApplicationRole, ApplicationDbContext>(db),
            Options.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(), [], [],
            new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!, NullLogger<UserManager<ApplicationUser>>.Instance);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id) }, "review"));
        UserContext Context() => new(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } }, users, db);
        var stale = Context();
        await stale.EnsureLoadedAsync();
        Assert.Null(stale.PlantId);
        Assert.Null(stale.ActiveRoleId);
        Assert.Equal(PlantSwitchOutcome.NotAllowed, await new PlantSwitchService(db).TrySwitchAsync(user.Id, b));

        user.ActivePlantId = a;
        await db.SaveChangesAsync();
        var valid = Context();
        await valid.EnsureLoadedAsync();
        db.CurrentPlantId = valid.PlantId;
        Assert.Equal(new[] { "OA-" + suffix }, await db.OrganizationUnits.Where(x => x.Id.EndsWith(suffix)).Select(x => x.Id).ToArrayAsync());
        var permissions = new PermissionService(db, valid, new FallbackSecurityContext(new LicenseContext()));
        counter.Count = 0;
        Assert.True(await permissions.CanRead("review"));
        Assert.False(await permissions.CanCreate("review"));
        Assert.False(await permissions.CanUpdate("review"));
        Assert.False(await permissions.CanDelete("review"));
        Assert.False(await permissions.CanPrint("review"));
        Assert.Equal(1, counter.Count);

        await db.UserRoles.Where(x => x.UserId == user.Id).ExecuteDeleteAsync();
        var revoked = Context();
        await revoked.EnsureLoadedAsync();
        Assert.Null(revoked.ActiveRoleId);
        Assert.Empty((await new PlantSwitchService(db).GetSwitchablePlantsAsync(user.Id)).Plants);

        // 從未選過廠別的新使用者：補上角色預設廠別並真的落地（只寫 ActivePlantId 一欄）。
        var fresh = new ApplicationUser { Id = "N-" + suffix, UserName = "N-" + suffix, ActiveRoleId = roleId,
            Language = "en-US" };
        db.Users.Add(fresh);
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = fresh.Id, RoleId = roleId });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var freshPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, fresh.Id) }, "review"));
        var initializing = new UserContext(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = freshPrincipal } }, users, db);
        await initializing.EnsureLoadedAsync();
        Assert.True(initializing.HasStaleAuthorization);
        Assert.True(await initializing.TryInitializeDefaultPlantAsync());
        Assert.False(initializing.HasStaleAuthorization);
        Assert.Equal(a, initializing.PlantId);
        Assert.Equal(roleId, initializing.ActiveRoleId);
        await using var verify = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connection.ConnectionString).Options);
        var stored = await verify.Users.AsNoTracking().SingleAsync(x => x.Id == fresh.Id);
        Assert.Equal(a, stored.ActivePlantId);
        Assert.Equal("en-US", stored.Language);
    }

    private sealed class PermissionQueryCounter : DbCommandInterceptor
    {
        public int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"MenuPermissions\"", StringComparison.Ordinal)) Count++;
            return ValueTask.FromResult(result);
        }
    }
}
