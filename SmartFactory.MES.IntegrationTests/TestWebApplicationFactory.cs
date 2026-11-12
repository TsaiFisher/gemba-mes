using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// 整合測試用的 WebApplicationFactory：
    /// - 透過環境變數在 Program.cs 啟動前清除 DefaultEncrypted，繞過 Security DLL 驗證
    /// - 替換 ILicenseValidator 為 CI 環境下永遠有效的 stub
    /// - 啟動時自動套用 EF Migration
    /// </summary>
    public class TestWebApplicationFactory : WebApplicationFactory<Program>
    {
        // 整個測試行程共用同一個 PostgreSQL 測試庫；不同 xUnit collection（例如
        // "Integration" 與 "ScheduleIntegration"）會各自建構工廠並平行執行。
        // 若每個工廠都呼叫 Database.Migrate()，多條連線會同時對同一庫跑 DDL，
        // 撞 pg_type / __EFMigrationsHistory 唯一鍵（23505）。故以行程級閘門確保只 migrate 一次。
        private static readonly object MigrationGate = new();
        private static bool _migrated;

        // 整合測試連線字串（本地 fallback）。
        // CI 由 workflow env 統一指定 ConnectionStrings__DefaultConnection=SmartFactoryMES_Test，
        // 此 fallback 僅在環境未設定時生效，DB 名稱對齊 CI（SmartFactoryMES_Test）。
        // Program.cs 的 ResolveConnectionStringAndContext() 在任何 ConfigureServices 之前執行，
        // 只有環境變數（ASPNETCORE_* / ConnectionStrings__*）能夠在那個時間點生效。
        private const string FallbackTestConnection =
            "Host=localhost;Database=SmartFactoryMES_Test;Username=dev;Password=Dev12345";

        /// <summary>
        /// 執行測試時外部指定的連線字串（CI 的 workflow env，或本機為了避開共用庫而指定的私有庫）。
        /// 在任何工廠改動環境變數之前取一次——靜態欄位初始化早於第一個建構子。
        ///
        /// ⚠️ 原本 Dispose 一律把 ConnectionStrings__DefaultConnection 清成 null：第一個 collection
        /// 的工廠一收掉，之後建構的工廠（其他 collection）就讀不到外部指定、靜靜退回共用的
        /// SmartFactoryMES_Test。只跑單一 collection 時一切正常，跑全套才有一半測試打到別的庫——
        /// 2026-10-05 實測：指定私有庫跑全套，36 支因「另一個分支已 migrate 共用庫、欄位被刪」而紅。
        /// </summary>
        private static readonly string? ExternalConnection =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");

        public TestWebApplicationFactory()
        {
            // ASPNETCORE_ENVIRONMENT=Development → builder.Environment.IsDevelopment() = true，
            // 使 ResolveConnectionStringAndContext() 在找不到 Security DLL 時走 fallback 路徑，
            // 而非拋出「找不到 Security DLL」錯誤。
            // Security DLL 已由 csproj AfterBuild Target 從測試輸出刪除，
            // 確保 File.Exists(dllPath) = false，跳過 SecurityAnchor.VerifyEntryAssembly()。
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultEncrypted", "");

            // 手冊 Q&A（選用模組，正式環境預設關閉）在整合測試一律開啟，理由有二：
            //  ① 它的守門測試需要 QaDbContext；
            //  ② 「開燈」路徑本身就該被測到——關著的話 QAModule 幾乎沒有程式碼被執行。
            // 必須用環境變數：QAModule.RegisterServices 讀的是 builder.Configuration，
            // 那個時間點早於任何 ConfigureAppConfiguration / ConfigureTestServices 回呼，
            // 只有環境變數來源已經就位（同上方 ConnectionStrings__ 的理由）。
            Environment.SetEnvironmentVariable("Features__QaAssistant__Enabled", "true");

            // 擷取物 manifest 目錄指到一個不存在的路徑，讓背景服務走「目錄不存在→什麼都不做」。
            //
            // **不設的話，受測應用程式會去匯入開發者工作樹裡真的那幾份 manifest**，
            // 而那不是測試的輸入，是當下剛好簽出什麼就吃什麼。實際踩到的樣子
            // （2026-08-14，`.claude/qa-extracts/currency.json` 進版控後第一次跑）：
            // 兩個 factory 幾乎同時啟動、各自的背景服務對同一個共用測試庫做
            // 「先刪再插」，撞出 `23505 IX_DocChunks_FilePath_Lang_ChunkIndex`。
            // 套件仍然全綠——背景服務把例外吃掉了（那是它該做的，見該檔註解），
            // 所以症狀只是日誌裡多一條沒人看的 ERR，外加擷取物列數會隨工作樹浮動。
            //
            // 正式機不會有這個問題：manifest 不進 publish 產物，目錄根本不存在（§10.11.6）。
            // 真要驗版控裡的 manifest，測試自己建一個 importer 指過去
            // （ExtractManifestImporterTests.版控裡的每一份manifest都要通得過格式檢查）。
            Environment.SetEnvironmentVariable(
                "Features__QaAssistant__ExtractManifestDir", "qa-extracts-not-used-in-tests");
            // 優先使用 CI 已設定的連線字串；未設定時才使用 fallback（對齊 CI DB 名稱）
            if (string.IsNullOrEmpty(
                    Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")))
                Environment.SetEnvironmentVariable(
                    "ConnectionStrings__DefaultConnection", FallbackTestConnection);
        }

        private readonly string? _previousEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        // 欄位初始化先於建構子本體，所以抓到的是「這個 factory 設定之前」的值。
        // ⚠️ Dispose 必須還原成這個值，不可一律清成 null：另開專屬 factory 的測試類別（IClassFixture）
        // 結束時會先 Dispose，清成 null 的話，之後才啟動的共用 factory 讀不到 QaAssistant 開關，
        // 隱私權頁的「資料查詢助理」段落整段消失（2026-11-12 PR #257 CI，AdminAccessFactory 觸發）。
        private readonly string? _previousQaEnabled = Environment.GetEnvironmentVariable("Features__QaAssistant__Enabled");
        private readonly string? _previousQaManifestDir = Environment.GetEnvironmentVariable("Features__QaAssistant__ExtractManifestDir");
        private readonly string? _previousEncrypted = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultEncrypted");

        protected override void Dispose(bool disposing)
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", _previousEnvironment);
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultEncrypted", _previousEncrypted);
            // 還原成外部指定的值（沒有就清掉），不可一律清成 null，理由見 ExternalConnection
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", ExternalConnection);
            Environment.SetEnvironmentVariable("Features__QaAssistant__Enabled", _previousQaEnabled);
            Environment.SetEnvironmentVariable("Features__QaAssistant__ExtractManifestDir", _previousQaManifestDir);
            base.Dispose(disposing);
        }

        // Simulate the P1 snapshot rendered for the seeded admin. Tests for another plant
        // must explicitly replace or remove this header; do not fetch the current plant per request.
        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Plant-Context", "P1");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            builder.ConfigureServices(services =>
            {
                // 替換 License 驗證：CI 環境無 license.json，使用永遠有效的 stub
                var licenseDesc = services.SingleOrDefault(
                    d => d.ServiceType == typeof(ILicenseValidator));
                if (licenseDesc != null)
                    services.Remove(licenseDesc);

                services.AddSingleton<ILicenseValidator, CiLicenseValidator>();

                // 測試程式直接寫入的種子缺主檔時補最小資料（ProjectZ 參照欄位檢查；HTTP 路徑照常檢查）
                services.AddSeedMasterStubs();

                // 套用 EF Migration（確保 Schema 存在）——行程級一次性，避免多 collection 平行 migrate 競態
                lock (MigrationGate)
                {
                    if (!_migrated)
                    {
                        var sp = services.BuildServiceProvider();
                        using var scope = sp.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        db.Database.Migrate();
                        _migrated = true;
                    }
                }
            });
        }
    }

    /// <summary>CI 環境用 License stub，永遠回傳有效的 Internal 授權。</summary>
    internal class CiLicenseValidator : ILicenseValidator
    {
        public LicenseContext Validate() => new()
        {
            LicenseId = "CI-TEST",
            ClientCode = "SMARTFACTORY-INTERNAL",
            ClientName = "CI Test",
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(10)
        };
    }

    [CollectionDefinition("Integration")]
    public class IntegrationCollection : ICollectionFixture<TestWebApplicationFactory> { }
}
