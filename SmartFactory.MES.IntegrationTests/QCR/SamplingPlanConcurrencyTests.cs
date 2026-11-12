using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.QCR
{
    /// <summary>
    /// SamplingPlanService 累加器並發保護驗證（B 階段唯一容忍項的方案 1 強化）。
    ///
    /// 背景：MIL-STD-1916 連續合格計數（SpecLevel 存於 InspectionSpec.ConsecutivePasses）是
    /// 無交易的 read-modify-write 累加器；並發同 spec 檢驗會 lost-update。已給 InspectionSpec
    /// 掛 shadow xmin，並在 RecordResultAsync 加「重讀最新值重算」樂觀重試。
    ///
    /// 本測試以「EF identity-map 持舊 xmin 快照」確定性製造衝突：
    /// ctxA 先載入 spec（xmin v1、count=0）→ ctxB 記一筆合格（DB count→1、xmin→v2）→
    /// ctxA 以仍持 v1 的追蹤快照記一筆合格：SaveChanges WHERE xmin=v1 命中 0 列 → 並發例外 →
    /// retry ReloadAsync 重讀 v2/count=1 重算 → 落 2。最終 count==2 證明兩筆增量皆未遺失。
    /// 舊 last-write-wins 行為會覆寫成 1（lost-update）→ 斷言失敗，故本測試具鑑別力。
    /// 真 PG 才有 xmin（InMemory 無此系統欄位），故置於整合測試。
    /// </summary>
    [Collection("Integration")]
    public class SamplingPlanConcurrencyTests
    {
        private const string SpecId  = "SPCONCUR-SPEC";
        private const string Product = "SPCONCUR-P";
        private const string Plant   = "P1";

        private readonly TestWebApplicationFactory _factory;

        public SamplingPlanConcurrencyTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed();
        }

        [Fact]
        public async Task RecordResultAsync_ConcurrentSpecModified_RetriesAndPreservesIncrement()
        {
            // ctxA：先把 spec 載入（持 xmin v1、ConsecutivePasses=0 的追蹤快照）
            using var scopeA = _factory.Services.CreateScope();
            var dbA = scopeA.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await dbA.InspectionSpecs.FirstAsync(s => s.SpecId == SpecId);

            // ctxB：另一 context 先記錄一筆合格 → DB ConsecutivePasses 0→1、xmin v1→v2
            using (var scopeB = _factory.Services.CreateScope())
            {
                var dbB = scopeB.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await new SamplingPlanService(dbB)
                    .RecordResultAsync(Product, supplierCode: null, Plant, passed: true);
            }

            // ctxA：以 identity-map 持有的 v1 快照記錄一筆合格
            // → SaveChanges WHERE xmin=v1 命中 0 列 → 並發例外 → retry ReloadAsync 重讀 v2 重算 → 落 2
            await new SamplingPlanService(dbA)
                .RecordResultAsync(Product, supplierCode: null, Plant, passed: true);

            // 驗證：兩筆增量皆保留（無 lost-update）
            using var scopeV = _factory.Services.CreateScope();
            var dbV = scopeV.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var fresh = await dbV.InspectionSpecs.AsNoTracking().FirstAsync(s => s.SpecId == SpecId);
            Assert.Equal(2, fresh.ConsecutivePasses);
            Assert.Equal("Normal", fresh.SamplingState);
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // 每次重建（可重跑），確保 ConsecutivePasses=0 起點
            var stale = db.InspectionSpecs.Where(s => s.SpecId == SpecId).ToList();
            if (stale.Count > 0) { db.InspectionSpecs.RemoveRange(stale); db.SaveChanges(); }

            db.InspectionSpecs.Add(new InspectionSpec
            {
                SpecId = SpecId, SpecCode = "SPCONCUR", SpecName = "SPCONCUR 並發測試",
                PlantId = Plant, ProductCode = Product, SupplierCode = null,
                IsActive = true,
                SamplingState = "Normal", ConsecutivePasses = 0, LotCounter = 0,
                PassesToReduce = 5, PassesToNormal = 3,
            });
            db.SaveChanges();
        }
    }
}
