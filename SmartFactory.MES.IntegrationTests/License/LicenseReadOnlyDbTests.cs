using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartFactory.MES.Data;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.License;

/// <summary>
/// 授權唯讀的資料庫層強制，在真的 PostgreSQL 上逐路徑釘住（SECURITY-OPERATIONS.md §18.8、§18.13 #7）。
///
/// <para>用自己的小 DbContext 與<b>專用暫存資料庫</b>（每次執行一個新庫，結束即刪），
/// 不碰共用的 SmartFactoryMES 測試庫——跨 worktree 共用庫會互相覆蓋。</para>
///
/// <para>InMemory 測不到這裡：ExecuteUpdate／ExecuteSqlRaw 的攔截點是 SQL 指令，InMemory 不產生 SQL。</para>
/// </summary>
public class LicenseReadOnlyDbTests : IAsyncLifetime
{
    private const string Server = "Host=localhost;Username=dev;Password=Dev12345";
    private readonly string _db = $"license_ro_test_{Guid.NewGuid():N}";
    private readonly SwitchableState _state = new();
    private LicenseReadOnlyInterceptor _interceptor = null!;

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Server + ";Database=postgres"))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_db}\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var options = new LicenseReadOnlyOptions();
        options.ExemptEntityTypes.Add(typeof(LoginCounter));
        _interceptor = new LicenseReadOnlyInterceptor(_state, options);

        await using var ctx = NewContext();
        await ctx.Database.EnsureCreatedAsync();
        ctx.Orders.Add(new Order { Id = 1, Qty = 1 });
        ctx.Counters.Add(new LoginCounter { Id = 1, Failed = 0 });
        await ctx.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(Server + ";Database=postgres");
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_db}\" WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private RoContext NewContext() => new(
        new DbContextOptionsBuilder<RoContext>()
            .UseNpgsql($"{Server};Database={_db}")
            .AddInterceptors(_interceptor)
            .Options);

    private async Task<int> Qty()
    {
        _state.Blocked = false;
        await using var ctx = NewContext();
        var qty = (await ctx.Orders.SingleAsync()).Qty;
        _state.Blocked = true;
        return qty;
    }

    [Fact]
    public async Task Normal_AllWritesPass()
    {
        await using var ctx = NewContext();
        (await ctx.Orders.SingleAsync()).Qty = 5;
        await ctx.SaveChangesAsync();
        await ctx.Orders.ExecuteUpdateAsync(s => s.SetProperty(o => o.Qty, 6));
        Assert.Equal(6, await Qty());
    }

    [Fact]
    public async Task ReadOnly_Reads_StillWork()
    {
        _state.Blocked = true;
        await using var ctx = NewContext();
        Assert.Equal(1, await ctx.Orders.CountAsync());
        Assert.Equal(1, await ctx.Database.SqlQueryRaw<int>("SELECT \"Qty\" AS \"Value\" FROM \"Orders\"").SingleAsync());
    }

    [Fact]
    public async Task ReadOnly_SaveChanges_OnBusinessEntity_IsRejected()
    {
        _state.Blocked = true;
        await using var ctx = NewContext();
        (await ctx.Orders.SingleAsync()).Qty = 99;

        var ex = await Assert.ThrowsAsync<LicenseReadOnlyException>(() => ctx.SaveChangesAsync());
        Assert.Contains(nameof(Order), ex.Message);
        Assert.Equal(1, await Qty());
    }

    [Fact]
    public async Task ReadOnly_MixedBatch_IsRejectedWhole()
    {
        // 豁免實體不能當成夾帶業務資料的掩護
        _state.Blocked = true;
        await using var ctx = NewContext();
        (await ctx.Counters.SingleAsync()).Failed = 3;
        (await ctx.Orders.SingleAsync()).Qty = 99;

        await Assert.ThrowsAsync<LicenseReadOnlyException>(() => ctx.SaveChangesAsync());
        Assert.Equal(1, await Qty());
    }

    [Fact]
    public async Task ReadOnly_ExemptEntity_CanBeSaved()
    {
        // 登入失敗計數、安全戳記這類寫入必須通過，否則唯讀期間連登入都不行
        _state.Blocked = true;
        await using var ctx = NewContext();
        (await ctx.Counters.SingleAsync()).Failed = 3;
        await ctx.SaveChangesAsync();

        await using var check = NewContext();
        Assert.Equal(3, (await check.Counters.SingleAsync()).Failed);
    }

    [Fact]
    public async Task ReadOnly_ExecuteUpdate_IsRejected()
    {
        _state.Blocked = true;
        await using var ctx = NewContext();
        await Assert.ThrowsAsync<LicenseReadOnlyException>(
            () => ctx.Orders.ExecuteUpdateAsync(s => s.SetProperty(o => o.Qty, 99)));
        Assert.Equal(1, await Qty());
    }

    [Fact]
    public async Task ReadOnly_ExecuteDelete_IsRejected()
    {
        _state.Blocked = true;
        await using var ctx = NewContext();
        await Assert.ThrowsAsync<LicenseReadOnlyException>(() => ctx.Orders.ExecuteDeleteAsync());
        Assert.Equal(1, await Qty());
    }

    [Theory]
    [InlineData("UPDATE \"Orders\" SET \"Qty\" = 99")]
    [InlineData("  /* 註解 */ insert into \"Orders\" (\"Id\",\"Qty\") values (2, 2)")]
    [InlineData("WITH x AS (UPDATE \"Orders\" SET \"Qty\" = 99 RETURNING 1) SELECT count(*) FROM x")]
    [InlineData("TRUNCATE \"Orders\"")]
    public async Task ReadOnly_RawSql_IsRejected(string sql)
    {
        _state.Blocked = true;
        await using var ctx = NewContext();
        await Assert.ThrowsAsync<LicenseReadOnlyException>(() => ctx.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(1, await Qty());
    }

    [Fact]
    public async Task ReadOnly_ExemptSave_DoesNotLeaveDoorOpen_ForLaterRawSql()
    {
        // 放行一次豁免存檔之後，同一個 DbContext 的下一句寫入 SQL 仍要被擋
        _state.Blocked = true;
        await using var ctx = NewContext();
        (await ctx.Counters.SingleAsync()).Failed = 4;
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<LicenseReadOnlyException>(
            () => ctx.Database.ExecuteSqlRawAsync("UPDATE \"Orders\" SET \"Qty\" = 99"));
        Assert.Equal(1, await Qty());
    }

    // ── 測試用模型 ───────────────────────────────────────────────────────

    private sealed class SwitchableState : ILicenseStateService
    {
        public bool Blocked { get; set; }
        public LicenseStatus Current => Blocked
            ? new LicenseStatus(LicenseState.ReadOnly, "測試：唯讀", null, null, null)
            : new LicenseStatus(LicenseState.Normal, "測試：正常", null, null, null);
        public void SetLicense(LicenseContext license) { }
        public void SetRevoked(DateTimeOffset revokedAt, string reason) { }
        public void SetSunset() { }
    }

    public sealed class Order { public int Id { get; set; } public int Qty { get; set; } }
    public sealed class LoginCounter { public int Id { get; set; } public int Failed { get; set; } }

    private sealed class RoContext(DbContextOptions<RoContext> options) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<LoginCounter> Counters => Set<LoginCounter>();
    }
}
