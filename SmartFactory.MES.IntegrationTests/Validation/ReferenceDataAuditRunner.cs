using System.Text;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared.ReferenceValidation;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.IntegrationTests.Validation
{
    /// <summary>
    /// 既有資料盤點的執行入口（上線前／啟用前，對**指定的資料庫**跑 <see cref="ReferenceValidator.AuditAsync"/>）。
    /// 平常沒設環境變數時什麼都不做，直接通過。
    ///
    /// <code>
    /// REFAUDIT_CONNECTION="Host=...;Database=...;Username=...;Password=..." \
    /// REFAUDIT_OUT=~/.cache/refaudit.md \
    ///   dotnet test SmartFactory.MES.IntegrationTests --filter ReferenceDataAuditRunner
    /// </code>
    /// 唯讀：只有查詢。建議對正式庫的備份還原副本跑。報告依「不存在／屬他廠」在前、「已停用」在後排列；
    /// 停用在舊單據上屬正常（只驗變更值，不影響修改其他欄位），要處理的是前兩類。
    /// </summary>
    public class ReferenceDataAuditRunner(ITestOutputHelper output)
    {
        [Fact]
        public async Task 盤點指定資料庫()
        {
            var conn = Environment.GetEnvironmentVariable("REFAUDIT_CONNECTION");
            if (string.IsNullOrWhiteSpace(conn)) return;

            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(conn).Options;
            await using var db = new ApplicationDbContext(options);
            var findings = await ReferenceValidator.AuditAsync(db);

            var sb = new StringBuilder();
            var blocking = findings.Count(f => f.Violation.Problem != ReferenceProblem.Inactive);
            sb.AppendLine($"# Reference data audit — {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine();
            sb.AppendLine($"- Database: `{new Npgsql.NpgsqlConnectionStringBuilder(conn).Database}`");
            sb.AppendLine($"- NotFound / OtherPlant: **{blocking}** distinct values ({findings.Where(f => f.Violation.Problem != ReferenceProblem.Inactive).Sum(f => f.Count)} rows)");
            sb.AppendLine($"- Inactive: {findings.Count - blocking} distinct values ({findings.Where(f => f.Violation.Problem == ReferenceProblem.Inactive).Sum(f => f.Count)} rows)");
            sb.AppendLine();
            sb.AppendLine("| Problem | Entity.Property | Master | Value | Plant | Rows |");
            sb.AppendLine("|---|---|---|---|---|---:|");
            foreach (var f in findings)
                sb.AppendLine($"| {f.Violation.Problem} | {f.Violation.Entity}.{f.Violation.Property} | {f.Violation.Master} | `{f.Violation.Value}` | {f.Plant} | {f.Count} |");

            var report = sb.ToString();
            var outPath = Environment.GetEnvironmentVariable("REFAUDIT_OUT");
            if (!string.IsNullOrWhiteSpace(outPath)) await File.WriteAllTextAsync(outPath, report);
            output.WriteLine(report);
        }
    }
}
