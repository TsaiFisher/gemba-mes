using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using InvM = SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// ScrapDoc 表頭 xmin 樂觀並發守門驗證（報廢單 Confirm 過帳防同單並發雙重扣帳）——INV 單據試點。
    ///
    /// 背景：ScrapDocController.Confirm 走 IInventoryPostingService.PostDocSimplifiedAsync 扣 Inventory
    /// （Inventory 有 xmin），再 SetDocPosted 將表頭轉 Posted、SaveChanges，並 catch DbUpdateConcurrencyException。
    /// 但 ScrapDoc「表頭原本無自身 xmin」→ 同單並發雙重過帳只能靠 Inventory xmin「間接」攔截：
    /// 若第二筆的庫存 SELECT 落在第一筆 COMMIT 之後（讀寫不交錯），Inventory 看到新 xmin → 不衝突 →
    /// 可能重複扣帳。表頭加 xmin 後可「確定性」擋下同單並發過帳（不依賴庫存讀寫時機）。
    ///
    /// 本測試以兩個 DbContext 載同一筆 ScrapDoc 模擬並發：先存者成功，後存者「應」拋並發例外。
    /// 真 PG 才有 xmin（InMemory 無此系統欄位），故置於整合測試。
    /// </summary>
    [Collection("Integration")]
    public class ScrapDocConcurrencyTests
    {
        private const string DocId  = "SDCONCUR-S";
        private const string Plant  = "P1";
        private const string RoleId = "1";

        private readonly TestWebApplicationFactory _factory;

        public ScrapDocConcurrencyTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed();
        }

        [Fact]
        public async Task ScrapDoc_ConcurrentConfirm_SecondSaveThrowsConcurrency()
        {
            using var scopeA = _factory.Services.CreateScope();
            using var scopeB = _factory.Services.CreateScope();
            var dbA = scopeA.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var dbB = scopeB.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // 兩個 context 各自載入同一筆報廢單（持相同並發快照）
            var sA = await dbA.ScrapDocs.IgnoreQueryFilters().FirstAsync(s => s.ScrapDocId == DocId);
            var sB = await dbB.ScrapDocs.IgnoreQueryFilters().FirstAsync(s => s.ScrapDocId == DocId);

            // A 先過帳（→ Posted）並提交
            sA.Status = InvM.DocStatus.Posted;
            sA.UpdatedAt = DateTime.UtcNow;
            await dbA.SaveChangesAsync();

            // B 持舊快照過帳 → 表頭 xmin 比對 0 列 → 樂觀並發例外。
            // 修法前 ScrapDoc 表頭無 xmin，B 會靜默成功。
            sB.Status = InvM.DocStatus.Posted;
            sB.UpdatedAt = DateTime.UtcNow;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // 每次重建為 Draft+Approved，確保可重跑
            var stale = db.ScrapDocs.IgnoreQueryFilters()
                .Where(s => s.ScrapDocId == DocId).ToList();
            if (stale.Count > 0) { db.ScrapDocs.RemoveRange(stale); db.SaveChanges(); }

            db.ScrapDocs.Add(new InvM.ScrapDoc
            {
                ScrapDocId      = DocId,
                ScrapDate       = DateOnly.FromDateTime(DateTime.UtcNow),
                TypeCode        = "IJ",
                TypeCategory    = "Scrap",
                Status          = InvM.DocStatus.Draft,
                ApprovalStatus  = DocumentApprovalStatus.Approved,
                CreatedBy       = "admin",
                PlantId         = Plant,
                CreatedByRoleId = RoleId,
            });
            db.SaveChanges();
        }
    }
}
