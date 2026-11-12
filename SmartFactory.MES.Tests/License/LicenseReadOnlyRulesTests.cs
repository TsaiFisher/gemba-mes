using SmartFactory.MES.Data;
using SmartFactory.MES.Middleware;
using SmartFactory.MES.Services;
using static SmartFactory.MES.Middleware.LicenseReadOnlyMiddleware;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 授權唯讀的兩個純函式：SQL 寫入判定、HTTP 放行判定（SECURITY-OPERATIONS.md §18.8）。
/// 資料庫層的實際攔截在 IntegrationTests 的 LicenseReadOnlyDbTests（真 PostgreSQL）。
/// </summary>
public class LicenseReadOnlyRulesTests
{
    [Theory]
    [InlineData("SELECT 1", false)]
    [InlineData("select * from t for update", false)]
    [InlineData("  -- c\nSELECT 1", false)]
    [InlineData("WITH a AS (SELECT 1) SELECT * FROM a", false)]
    [InlineData("EXPLAIN SELECT 1", false)]
    [InlineData("SAVEPOINT s1", false)]
    [InlineData("BEGIN", false)]
    [InlineData("", false)]
    [InlineData("INSERT INTO t VALUES (1)", true)]
    [InlineData("update t set a = 1", true)]
    [InlineData("DELETE FROM t", true)]
    [InlineData("/* x */ DELETE FROM t", true)]
    [InlineData("WITH a AS (DELETE FROM t RETURNING *) SELECT * FROM a", true)]
    [InlineData("TRUNCATE t", true)]
    [InlineData("ALTER TABLE t ADD c int", true)]
    [InlineData("DO $$ BEGIN END $$", true)]       // 不認得的一律當寫入：寧可誤擋
    public void IsWriteSql(string sql, bool expected)
        => Assert.Equal(expected, LicenseReadOnlyInterceptor.IsWriteSql(sql));

    [Theory]
    [InlineData(LicenseState.Normal,   "POST", "/PUR/Save",                 Decision.Allow)]
    [InlineData(LicenseState.Warning,  "POST", "/PUR/Save",                 Decision.Allow)]
    [InlineData(LicenseState.Unlocked, "POST", "/PUR/Save",                 Decision.Allow)]
    [InlineData(LicenseState.ReadOnly, "GET",  "/PUR/Index",                Decision.Allow)]
    [InlineData(LicenseState.ReadOnly, "POST", "/PUR/Save",                 Decision.Block)]
    [InlineData(LicenseState.ReadOnly, "PUT",  "/api/mobile/material/1",    Decision.Block)]
    [InlineData(LicenseState.ReadOnly, "DELETE", "/api/x",                  Decision.Block)]
    [InlineData(LicenseState.ReadOnly, "POST", "/Identity/Account/Login",   Decision.Allow)]
    [InlineData(LicenseState.ReadOnly, "POST", "/Identity/Account/Logout",  Decision.Allow)]
    [InlineData(LicenseState.ReadOnly, "POST", "/api/mobile/auth/login",    Decision.Allow)]
    [InlineData(LicenseState.ReadOnly, "POST", "/License/Import",           Decision.Allow)]
    [InlineData(LicenseState.ReadOnly, "POST", "/UserSettings/SetLanguage", Decision.Allow)]
    [InlineData(LicenseState.Disabled, "GET",  "/PUR/Index",                Decision.RedirectToStatus)]
    [InlineData(LicenseState.Disabled, "GET",  "/License",                  Decision.Allow)]
    [InlineData(LicenseState.Disabled, "GET",  "/css/site.css",             Decision.Allow)]
    [InlineData(LicenseState.Disabled, "GET",  "/health",                   Decision.Allow)]
    [InlineData(LicenseState.Disabled, "POST", "/Identity/Account/Login",   Decision.Allow)]
    [InlineData(LicenseState.Disabled, "POST", "/PUR/Save",                 Decision.RedirectToStatus)]
    public void MiddlewareDecision(LicenseState state, string method, string path, Decision expected)
        => Assert.Equal(expected, Decide(state, method, path));
}
