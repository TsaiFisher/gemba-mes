using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// DocNumberService.NextAsync 對真實 PostgreSQL 連線的整合測試（InMemory 無法測，
    /// NextAsync 內部用原生 PostgreSQL UPSERT + RETURNING）。
    /// 回歸涵蓋：舊實作對 EF 內部連線手動 conn.OpenAsync() 且從不 Close，
    /// 只要外層已開顯式 transaction（MRP 執行、倉庫下架/上架皆是如此），
    /// 或同一 scope 內連續呼叫兩次，就會炸「Connection already open」。
    /// 修法改用 Database.OpenConnectionAsync/CloseConnectionAsync（參照計數）。
    /// </summary>
    [Collection("Integration")]
    public class DocNumberServiceIntegrationTests
    {
        private const string DocType = "IT_DOCNO_TEST";
        private const string Plant = "ZZDNT";

        private readonly TestWebApplicationFactory _factory;

        public DocNumberServiceIntegrationTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task NextAsync_InsideExplicitTransaction_DoesNotThrowConnectionAlreadyOpen()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var docNumber = scope.ServiceProvider.GetRequiredService<IDocNumberService>();
            Purge(db);
            await SeedConfigAsync(db);

            // 模擬 MRP 執行 / 倉庫下架上架的呼叫模式：先開交易（連線已開），
            // 交易內再呼叫 NextAsync —— 這正是回歸案例最初炸掉的路徑。
            await using var tx = await db.Database.BeginTransactionAsync();

            var first = await docNumber.NextAsync(DocType, Plant);
            Assert.Equal($"{DocType}-00001", first);

            // 交易仍開著時，其他 EF 查詢須能正常執行（驗證連線未被誤關）
            var count = await db.Set<DocNumberConfig>().CountAsync(c => c.DocType == DocType);
            Assert.Equal(1, count);

            await tx.CommitAsync();

            Purge(db);
        }

        [Fact]
        public async Task NextAsync_CalledTwiceInSameScope_ReturnsSequentialNumbers()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var docNumber = scope.ServiceProvider.GetRequiredService<IDocNumberService>();
            Purge(db);
            await SeedConfigAsync(db);

            var first = await docNumber.NextAsync(DocType, Plant);
            var second = await docNumber.NextAsync(DocType, Plant);

            Assert.Equal($"{DocType}-00001", first);
            Assert.Equal($"{DocType}-00002", second);

            Purge(db);
        }

        [Fact]
        public async Task NextAsync_重置週期改過_取號自動接續不重發舊號()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var docNumber = scope.ServiceProvider.GetRequiredService<IDocNumberService>();
            Purge(db);
            // 實際案例（StockAdjustment）：YYMM＋年重置 → 改月重置編到 00006 → 又改回年重置，
            // 年期別列仍停在 1 → 重發 IA-2607-00002…撞既有單據。
            await SeedConfigAsync(db, DocDateFormat.YYMM, DocSeqReset.Yearly);
            var date = new DateOnly(2026, 7, 22);

            db.Set<DocNumberSequence>().AddRange(
                new DocNumberSequence { DocType = DocType, PlantId = "", Period = "2026",   LastSeq = 1 },
                new DocNumberSequence { DocType = DocType, PlantId = "", Period = "202607", LastSeq = 6 });
            await db.SaveChangesAsync();

            var next = await docNumber.NextAsync(DocType, Plant, date);

            // 兄弟期別歷史最大 6 → 接 00007，不重發 00002
            Assert.Equal($"{DocType}-2607-00007", next);

            Purge(db);
        }

        [Fact]
        public async Task NextAsync_跨年重置_不受前一年序號影響()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var docNumber = scope.ServiceProvider.GetRequiredService<IDocNumberService>();
            Purge(db);
            await SeedConfigAsync(db, DocDateFormat.YYMM, DocSeqReset.Yearly);

            db.Set<DocNumberSequence>().Add(
                new DocNumberSequence { DocType = DocType, PlantId = "", Period = "2026", LastSeq = 500 });
            await db.SaveChangesAsync();

            // 防重發只比對「同一業務日期」推得的兄弟期別，故新年度仍正常從 1 起編。
            var next = await docNumber.NextAsync(DocType, Plant, new DateOnly(2027, 1, 5));

            Assert.Equal($"{DocType}-2701-00001", next);

            Purge(db);
        }

        private static async Task SeedConfigAsync(ApplicationDbContext db,
            DocDateFormat dateFormat = DocDateFormat.None,
            DocSeqReset resetCycle = DocSeqReset.Never)
        {
            db.Set<DocNumberConfig>().Add(new DocNumberConfig
            {
                DocType     = DocType,
                DocTypeName = "Integration Test DocType",
                Prefix      = DocType,
                Separator   = "-",
                DateFormat  = dateFormat,
                SeqLength   = 5,
                ResetCycle  = resetCycle,
                IsActive    = true,
            });
            await db.SaveChangesAsync();
        }

        private static void Purge(ApplicationDbContext db)
        {
            // 序號 scope = 設定的適用廠別（共用預設規則時為 ""），故一律以 DocType 全清
            db.Set<DocNumberSequence>().IgnoreQueryFilters()
                .Where(s => s.DocType == DocType)
                .ExecuteDelete();
            db.Set<DocNumberConfig>().IgnoreQueryFilters()
                .Where(c => c.DocType == DocType)
                .ExecuteDelete();
        }

        [Fact]
        public async Task NextAsync_兩廠共用預設規則_序號連續編不重號()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var docNumber = scope.ServiceProvider.GetRequiredService<IDocNumberService>();
            Purge(db);
            await SeedConfigAsync(db);

            // 過往缺陷：序號 per-plant 各自從 1 起、單號不含廠別 → 兩廠同產 00001 撞單據 PK。
            // 新設計：未建立專屬規則的廠別共用預設規則的同一條序號。
            var a = await docNumber.NextAsync(DocType, "ZZDNT-A");
            var b = await docNumber.NextAsync(DocType, "ZZDNT-B");

            Assert.Equal($"{DocType}-00001", a);
            Assert.Equal($"{DocType}-00002", b);

            Purge(db);
        }

        [Fact]
        public async Task NextAsync_廠別專屬規則_獨立前置碼與序號()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var docNumber = scope.ServiceProvider.GetRequiredService<IDocNumberService>();
            Purge(db);
            await SeedConfigAsync(db);
            db.Set<DocNumberConfig>().Add(new DocNumberConfig
            {
                DocType     = DocType,
                ScopeKey    = Plant,           // 專屬規則的不變 scope 鍵（與 "" 預設規則區隔 PK）
                PlantId     = Plant,
                DocTypeName = "Plant-specific rule",
                Prefix      = $"{DocType}X",   // 指紋須與預設不同
                Separator   = "-",
                DateFormat  = DocDateFormat.None,
                SeqLength   = 5,
                ResetCycle  = DocSeqReset.Never,
                IsActive    = true,
            });
            await db.SaveChangesAsync();

            var shared = await docNumber.NextAsync(DocType, "ZZDNT-A");   // 走預設規則
            var own1   = await docNumber.NextAsync(DocType, Plant);       // 走專屬規則
            var own2   = await docNumber.NextAsync(DocType, Plant);

            Assert.Equal($"{DocType}-00001",  shared);
            Assert.Equal($"{DocType}X-00001", own1);   // 專屬序號獨立起算
            Assert.Equal($"{DocType}X-00002", own2);

            Purge(db);
        }

        [Fact]
        public async Task NextAsync_多廠共用一筆規則_共用同一條序號()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var docNumber = scope.ServiceProvider.GetRequiredService<IDocNumberService>();
            Purge(db);
            await SeedConfigAsync(db);

            // 「適用廠別」勾選多廠共用同一規則時只存一筆，PlantId 為逗號分隔清單，
            // 兩廠都應解析到這筆規則，且共用同一條序號（規則相同故合併儲存，非各廠獨立起算）。
            const string PlantA = "ZZDNT-C1", PlantB = "ZZDNT-C2";
            db.Set<DocNumberConfig>().Add(new DocNumberConfig
            {
                DocType     = DocType,
                ScopeKey    = $"{PlantA},{PlantB}",
                PlantId     = $"{PlantA},{PlantB}",
                DocTypeName = "Multi-plant shared rule",
                Prefix      = $"{DocType}M",
                Separator   = "-",
                DateFormat  = DocDateFormat.None,
                SeqLength   = 5,
                ResetCycle  = DocSeqReset.Never,
                IsActive    = true,
            });
            await db.SaveChangesAsync();

            var a = await docNumber.NextAsync(DocType, PlantA);
            var b = await docNumber.NextAsync(DocType, PlantB);

            Assert.Equal($"{DocType}M-00001", a);
            Assert.Equal($"{DocType}M-00002", b);   // 序號延續，非各廠從 1 起

            Purge(db);
        }

        [Fact]
        public async Task NextAsync_編輯適用廠別後_ScopeKey不變_序號跨編輯延續()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var docNumber = scope.ServiceProvider.GetRequiredService<IDocNumberService>();
            Purge(db);
            await SeedConfigAsync(db);

            // 專屬規則：ScopeKey 為不變鍵，PlantId（適用廠別）可編輯。
            const string ScopeKey = "SK-EDIT-TEST", PlantA = "ZZDNT-E1", PlantC = "ZZDNT-E3";
            var rule = new DocNumberConfig
            {
                DocType     = DocType,
                ScopeKey    = ScopeKey,
                PlantId     = PlantA,               // 起初僅適用 A
                DocTypeName = "Editable plant rule",
                Prefix      = $"{DocType}E",
                Separator   = "-",
                DateFormat  = DocDateFormat.None,
                SeqLength   = 5,
                ResetCycle  = DocSeqReset.Never,
                IsActive    = true,
            };
            db.Set<DocNumberConfig>().Add(rule);
            await db.SaveChangesAsync();

            var a1 = await docNumber.NextAsync(DocType, PlantA);   // E-00001

            // 模擬編輯：把適用廠別改為 A,C（ScopeKey 不變），存回
            rule.PlantId = $"{PlantA},{PlantC}";
            await db.SaveChangesAsync();

            var c1 = await docNumber.NextAsync(DocType, PlantC);   // 新加入的 C 走同一 scope
            var a2 = await docNumber.NextAsync(DocType, PlantA);

            Assert.Equal($"{DocType}E-00001", a1);
            Assert.Equal($"{DocType}E-00002", c1);   // 未從 1 重起 —— 序號跨編輯延續、不重發
            Assert.Equal($"{DocType}E-00003", a2);

            Purge(db);
        }
    }
}
