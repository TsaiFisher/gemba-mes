using Npgsql;

namespace SmartFactory.MES.UITests
{
    /// <summary>
    /// E2E 種子的共用重置工具：把 E2E- 那組資料歸零回基準值（含庫存餘量）。
    ///
    /// 兩個地方需要它：
    ///   • 流程測試（<see cref="FlowTestBase"/>）每次開跑前，把單據狀態重置回起點。
    ///   • 依賴基準庫存的單據測試（<see cref="INV.InvLineDocActionSmokeTestBase"/>）開跑前，
    ///     把庫存補回——否則若某個流程測試先跑並吃掉了 E2E-RM01，後跑的報廢／調撥等
    ///     單據測試會撞「可用量不足」。原則：需要基準庫存的測試自己負責建立基準，
    ///     不倚賴前一個測試有沒有清乾淨。
    /// </summary>
    internal static class SeedFixture
    {
        /// <summary>
        /// 重新執行 E2E 種子。連線字串取自 <c>TestDbConnection</c>，未設定時退回
        /// <c>ConnectionStrings__DefaultConnection</c>（CI 的 job 層 env 已有這個）。
        /// </summary>
        public static async Task ResetAsync()
        {
            var conn = ConnectionString();

            var sqlPath = LocateSeedScript();
            var sql = await File.ReadAllTextAsync(sqlPath);

            // psql 的 meta command（\set、\echo）不是 SQL，Npgsql 會語法錯誤，先剔除。
            var statements = string.Join('\n',
                sql.Split('\n').Where(line => !line.TrimStart().StartsWith('\\')));

            await using var db = new NpgsqlConnection(conn);
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand(statements, db);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// 對測試庫執行一句純量查詢。用來在畫面動作之後直接驗資料庫——
        /// 畫面上的「成功」toast 只代表 API 回了 success，不代表帳真的過進去了。
        /// </summary>
        public static async Task<T?> ScalarAsync<T>(string sql)
        {
            await using var db = new NpgsqlConnection(ConnectionString());
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, db);
            var value = await cmd.ExecuteScalarAsync();
            return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
        }

        /// <summary>對測試庫執行任意 SQL（測試自備的小型種子；不含 psql meta command）。</summary>
        public static async Task ExecAsync(string sql)
        {
            await using var db = new NpgsqlConnection(ConnectionString());
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, db);
            await cmd.ExecuteNonQueryAsync();
        }

        private static string ConnectionString()
        {
            var conn = Environment.GetEnvironmentVariable("TestDbConnection")
                    ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");

            if (string.IsNullOrWhiteSpace(conn))
                throw new InvalidOperationException(
                    "E2E 測試需要資料庫連線才能把資料重置回起點，但 TestDbConnection 與 " +
                    "ConnectionStrings__DefaultConnection 都沒有設定。請指向 app 正在使用的那個測試資料庫，" +
                    "例如 TestDbConnection=\"Host=localhost;Database=SmartFactoryMES_Test;Username=dev;Password=Dev12345\"。");

            return conn;
        }

        /// <summary>
        /// 從測試執行目錄（bin/Debug/net10.0）往上找 repo 根目錄的種子腳本。
        /// 不寫死相對層數——Debug/Release 與不同執行方式的層數不一定相同。
        /// </summary>
        private static string LocateSeedScript()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "scripts", "seed-e2e-fixture.sql");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new FileNotFoundException(
                $"找不到 scripts/seed-e2e-fixture.sql（從 {AppContext.BaseDirectory} 往上找）。");
        }
    }
}
