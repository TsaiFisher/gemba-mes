using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using InvM = SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// INV 單據表頭 xmin 全面鋪開驗證（LoanDoc/SampleDoc/OutsourceMaterialDoc/ReworkMaterialDoc/
    /// StockAdjustment/StockTransfer/CrossPlantTransfer/InventoryCountSheet）。
    ///
    /// 這 8 支單據的 Confirm/Post/Void 都走 IInventoryPostingService 扣 Inventory（Inventory 有 xmin），
    /// 但表頭原本無自身 xmin → 同單並發雙重過帳僅靠 Inventory 讀寫交錯機率攔截。補表頭 xmin 後可確定性
    /// 擋下：兩 context 載同筆、A 先存（xmin 遞增）、B 持舊快照存 → 拋 DbUpdateConcurrencyException。
    /// 真 PG 才有 xmin（InMemory 無此系統欄位），故置於整合測試。
    /// </summary>
    [Collection("Integration")]
    public class InvDocHeaderXminTests
    {
        private const string Plant  = "P1";
        private const string RoleId = "1";

        private readonly TestWebApplicationFactory _factory;

        public InvDocHeaderXminTests(TestWebApplicationFactory factory) => _factory = factory;

        /// <summary>
        /// 泛型守門：seed 一筆 → 兩 context 載同筆 → A bump+save → B 持舊 xmin bump+save 必拋並發例外。
        /// </summary>
        private async Task AssertHeaderXminGuards<T>(
            T seed, Expression<Func<T, bool>> match, Action<T> bump) where T : class
        {
            // seed（可重跑：先刪同鍵）
            using (var s = _factory.Services.CreateScope())
            {
                var db = s.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var existing = await db.Set<T>().IgnoreQueryFilters().Where(match).ToListAsync();
                if (existing.Count > 0) { db.Set<T>().RemoveRange(existing); await db.SaveChangesAsync(); }
                db.Set<T>().Add(seed);
                await db.SaveChangesAsync();
            }

            using var scopeA = _factory.Services.CreateScope();
            using var scopeB = _factory.Services.CreateScope();
            var dbA = scopeA.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var dbB = scopeB.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var a = await dbA.Set<T>().IgnoreQueryFilters().FirstAsync(match);
            var b = await dbB.Set<T>().IgnoreQueryFilters().FirstAsync(match);

            bump(a);
            await dbA.SaveChangesAsync();

            bump(b);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
        }

        [Fact]
        public Task LoanDoc_HeaderXmin_Guards() => AssertHeaderXminGuards(
            new InvM.LoanDoc { LoanDocId = "XMR-LOAN", LoanDate = DateOnly.FromDateTime(DateTime.UtcNow),
                CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId },
            d => d.LoanDocId == "XMR-LOAN", d => d.UpdatedAt = DateTime.UtcNow);

        [Fact]
        public Task SampleDoc_HeaderXmin_Guards() => AssertHeaderXminGuards(
            new InvM.SampleDoc { SampleDocId = "XMR-SAMP", SampleDate = DateOnly.FromDateTime(DateTime.UtcNow),
                CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId },
            d => d.SampleDocId == "XMR-SAMP", d => d.UpdatedAt = DateTime.UtcNow);

        [Fact]
        public Task OutsourceMaterialDoc_HeaderXmin_Guards() => AssertHeaderXminGuards(
            new InvM.OutsourceMaterialDoc { OutsourceMaterialDocId = "XMR-OMD", IssueDate = DateOnly.FromDateTime(DateTime.UtcNow),
                CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId },
            d => d.OutsourceMaterialDocId == "XMR-OMD", d => d.UpdatedAt = DateTime.UtcNow);

        [Fact]
        public Task ReworkMaterialDoc_HeaderXmin_Guards() => AssertHeaderXminGuards(
            new InvM.ReworkMaterialDoc { ReworkMaterialDocId = "XMR-RMD", IssueDate = DateOnly.FromDateTime(DateTime.UtcNow),
                CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId },
            d => d.ReworkMaterialDocId == "XMR-RMD", d => d.UpdatedAt = DateTime.UtcNow);

        [Fact]
        public Task StockAdjustment_HeaderXmin_Guards() => AssertHeaderXminGuards(
            new InvM.StockAdjustment { StockAdjustmentId = "XMR-ADJ", AdjustDate = DateOnly.FromDateTime(DateTime.UtcNow),
                CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId },
            d => d.StockAdjustmentId == "XMR-ADJ", d => d.UpdatedAt = DateTime.UtcNow);

        [Fact]
        public Task StockTransfer_HeaderXmin_Guards() => AssertHeaderXminGuards(
            new InvM.StockTransfer { StockTransferId = "XMR-WT", TransferDate = DateOnly.FromDateTime(DateTime.UtcNow),
                CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId },
            d => d.StockTransferId == "XMR-WT", d => d.UpdatedAt = DateTime.UtcNow);

        [Fact]
        public Task CrossPlantTransfer_HeaderXmin_Guards() => AssertHeaderXminGuards(
            new InvM.CrossPlantTransfer { CrossPlantTransferId = "XMR-XPT", TransferDate = DateOnly.FromDateTime(DateTime.UtcNow),
                CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId },
            d => d.CrossPlantTransferId == "XMR-XPT", d => d.UpdatedAt = DateTime.UtcNow);

        [Fact]
        public Task InventoryCountSheet_HeaderXmin_Guards() => AssertHeaderXminGuards(
            new InventoryCountSheet { SheetId = "XMR-IC", CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId },
            d => d.SheetId == "XMR-IC", d => d.UpdatedAt = DateTime.UtcNow);
    }
}
