using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using Xunit;

namespace SmartFactory.MES.Tests.Conventions
{
    /// <summary>
    /// `scripts/seed-*.sql` 裡每個 `ON CONFLICT (…)` 的欄位組，
    /// 都必須對得上該資料表**實際存在的主鍵或唯一索引**。
    ///
    /// 為什麼需要這條——2026-08-29 起 nightly E2E 連紅四晚才被發現：
    /// `AccountingPeriods` 的主鍵改成廠別化的 `("PeriodId","PlantId")`
    /// （甲廠關帳不該把乙廠一起關掉），但種子仍寫 `ON CONFLICT ("PeriodId")`
    /// 且完全沒插 `PlantId`。PostgreSQL 丟
    /// 「there is no unique or exclusion constraint matching the ON CONFLICT specification」，
    /// psql 以 exit 3 中止整支種子，Playwright **連一個測試都還沒跑**整個 job 就紅了。
    ///
    /// 關鍵在於**它為什麼拖了四晚**：改主鍵那個 PR 全綠合併，因為
    /// **PR／push 的 CI 不跑 E2E**——種子只在 nightly 被執行。
    /// 所以守門必須前移到「每次 PR 都跑得到」的地方，也就是這裡。
    /// 這與 `manual-test-plan.md` §12.7 記的教訓同一類：拿子集合當閘門，
    /// 等於預設子集合以外不會壞。
    ///
    /// ⚠️ **2026-09-24 放寬掃描範圍**：本規則原本只讀 `seed-e2e-fixture.sql` 一支檔名，
    /// 而上面那段註解自己就在講「拿子集合當閘門」——規則本身就是那個形狀。
    /// 新增 `scripts/seed-vendor-pricelist.sql` 時發現它完全不在守門視野內：
    /// 種子腳本是一個**會長大的家族**，而下一支寫錯 ON CONFLICT 的代價與 2026-08-29
    /// 那次一模一樣（psql exit 3、整支種子中止、跑它的 job 在任何測試之前就紅）。
    /// 現改為掃 `scripts/seed-*.sql` 全部——新增種子腳本不必記得回來改這支測試。
    ///
    /// 作法刻意**不連資料庫**：用假連線字串建 Npgsql 的關聯式模型即可拿到
    /// 資料表名、主鍵與唯一索引。連庫會讓這支測試依賴本機環境，
    /// 而它要守的東西（模型 vs 種子）本來就都在版控裡。
    /// </summary>
    public class SeedFixtureOnConflictTests
    {
        private static readonly string RepoRoot = FindRepoRoot();

        /// <summary>`INSERT INTO "T" … ON CONFLICT (a, b)`；`.*?` 跨行非貪婪到最近的 ON CONFLICT。</summary>
        private static readonly Regex InsertOnConflict = new(
            @"INSERT\s+INTO\s+""(?<table>\w+)""(?<body>.*?)ON\s+CONFLICT\s*\((?<cols>[^)]*)\)",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        [Fact]
        public void EveryOnConflictTargetsARealKeyOrUniqueIndex()
        {
            var scriptDir = Path.Combine(RepoRoot, "scripts");
            var sqlPaths = Directory.GetFiles(scriptDir, "seed-*.sql")
                                    .OrderBy(f => f, StringComparer.Ordinal)
                                    .ToList();

            // 家族至少要有 e2e 那一支；掃到空目錄而靜靜通過是這條規則最可能的失效方式。
            Assert.Contains(sqlPaths, f => Path.GetFileName(f) == "seed-e2e-fixture.sql");

            var keys = UniqueKeySetsByTable();
            var violations = new List<string>();
            var totalMatches = 0;

            foreach (var sqlPath in sqlPaths)
            {
                var fileName = Path.GetFileName(sqlPath);
                var sql = File.ReadAllText(sqlPath);
                var matches = InsertOnConflict.Matches(sql);
                totalMatches += matches.Count;

                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    var table = m.Groups["table"].Value;
                    var cols = ColumnSet(m.Groups["cols"].Value);
                    var line = sql.Take(m.Groups["cols"].Index).Count(c => c == '\n') + 1;

                    // 模型裡沒有的資料表不判定：種子也會塞 Identity 之類非本模型管的表。
                    if (!keys.TryGetValue(table, out var candidates)) continue;

                    if (!candidates.Any(k => k.SetEquals(cols)))
                        violations.Add(
                            $"{fileName}:{line}  {table} ON CONFLICT ({string.Join(",", cols.OrderBy(x => x))})"
                            + $"\n      實際可用的鍵：{string.Join(" | ", candidates.Select(k => string.Join(",", k.OrderBy(x => x))))}");
                }
            }

            // 門檻訂在聚合數：e2e 那一支本來就 20+，正規式失效時會一次掉到個位數。
            Assert.True(totalMatches >= 20,
                $"{sqlPaths.Count} 支種子腳本只解析到 {totalMatches} 個 ON CONFLICT"
                + "——正規式已失效，規則形同關閉");

            Assert.True(violations.Count == 0,
                "以下 ON CONFLICT 對不上任何主鍵或唯一索引，psql 會以 exit 3 中止整支種子"
                + "（nightly E2E 會在跑任何測試之前就整個 job 紅）：\n  "
                + string.Join("\n  ", violations));
        }

        /// <summary>
        /// 反向驗證：這條規則真的在看東西。
        /// 只斷言「沒有違規」的掃描很容易因為正規式或查表寫錯而恆綠、形同關閉。
        /// </summary>
        [Fact]
        public void TheCheckItselfIsNotVacuous()
        {
            var keys = UniqueKeySetsByTable();

            // ① 模型真的建起來了，而且抓得到廠別化之後的複合主鍵
            Assert.True(keys.ContainsKey("AccountingPeriods"), "模型裡找不到 AccountingPeriods");
            Assert.Contains(keys["AccountingPeriods"],
                k => k.SetEquals(ColumnSet("\"PeriodId\",\"PlantId\"")));

            // ② 而單獨的 PeriodId **不是**可用的鍵——這正是 2026-08-29 那次的失敗形狀
            Assert.DoesNotContain(keys["AccountingPeriods"],
                k => k.SetEquals(ColumnSet("\"PeriodId\"")));

            // ③ 唯一索引也要算進來，不是只有主鍵（Inventories 的四欄唯一索引就不是 PK）
            Assert.Contains(keys["Inventories"],
                k => k.SetEquals(ColumnSet("\"PlantId\",\"ProductCode\",\"LocationCode\",\"LotNo\"")));

            // ④ 正規式抓得到實際寫法
            var probe = InsertOnConflict.Match(
                "INSERT INTO \"T\" (\"a\")\nVALUES (1)\nON CONFLICT (\"a\",\"b\") DO NOTHING;");
            Assert.True(probe.Success);
            Assert.Equal("T", probe.Groups["table"].Value);
            Assert.Equal(2, ColumnSet(probe.Groups["cols"].Value).Count);
        }

        // ── 輔助方法 ─────────────────────────────────────────────────────────

        private static HashSet<string> ColumnSet(string raw)
            => raw.Split(',')
                  .Select(c => c.Trim().Trim('"').Trim())
                  .Where(c => c.Length > 0)
                  .ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// 每個資料表可用於 ON CONFLICT 的欄位組＝主鍵 ∪ 唯一索引。
        /// 用假連線字串建模型；EF 建模不連線，故無需資料庫。
        /// </summary>
        private static Dictionary<string, List<HashSet<string>>> UniqueKeySetsByTable()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql("Host=localhost;Database=modelonly;Username=none;Password=none")
                .Options;
            using var ctx = new ApplicationDbContext(options);

            var result = new Dictionary<string, List<HashSet<string>>>(StringComparer.Ordinal);

            foreach (var entity in ctx.Model.GetEntityTypes())
            {
                var table = entity.GetTableName();
                if (string.IsNullOrEmpty(table)) continue;
                if (!result.TryGetValue(table, out var list))
                    result[table] = list = new List<HashSet<string>>();

                foreach (var key in entity.GetKeys())
                    list.Add(ColumnNames(key.Properties, entity));

                foreach (var ix in entity.GetIndexes().Where(i => i.IsUnique))
                    list.Add(ColumnNames(ix.Properties, entity));
            }

            return result;
        }

        private static HashSet<string> ColumnNames(
            IEnumerable<Microsoft.EntityFrameworkCore.Metadata.IReadOnlyProperty> props,
            Microsoft.EntityFrameworkCore.Metadata.IReadOnlyEntityType entity)
        {
            var id = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier
                .Table(entity.GetTableName()!, entity.GetSchema());
            return props.Select(p => p.GetColumnName(id) ?? p.Name)
                        .ToHashSet(StringComparer.Ordinal);
        }

        private static string FindRepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "SmartFactory.MES.sln")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("找不到 SmartFactory.MES.sln，無法定位 Repo 根目錄。");
        }
    }
}
