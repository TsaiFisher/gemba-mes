using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using MfgM = SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// MaterialSlip 樂觀並發守門驗證（發退料過帳 Issue 防並發雙重過帳）。
    ///
    /// 背景：MaterialSlipController.Issue 以追蹤實體載入 Confirmed 領退料單，呼叫 PostDocSimplifiedAsync
    /// 過帳 InventoryMovement、回寫 WorkOrderMaterial.IssuedQty/ReturnedQty，再 SaveChanges 轉 Posted，
    /// 並 catch DbUpdateConcurrencyException 回友善訊息。但 MaterialSlip 原本「沒有任何並發權杖」→
    /// 該 catch 是死碼：兩個並發 Issue 都通過 Status==Confirmed 檢查 → 各自過帳一次（庫存扣兩次、
    /// IssuedQty 回寫兩次）→ 雙重發料。
    ///
    /// 修法：MaterialSlip.UseXminAsConcurrencyToken()——收尾 UPDATE 帶 WHERE xmin，後到者比對失敗
    /// 拋 DbUpdateConcurrencyException → 整段交易（含過帳）回滾，未重複過帳；Controller catch 轉友善訊息。
    ///
    /// 本測試以兩個 DbContext 載同一筆領退料單模擬並發：先存者成功，後存者「應」拋並發例外。
    /// 真 PG 才有 xmin（InMemory 無此系統欄位），故置於整合測試。
    /// </summary>
    [Collection("Integration")]
    public class MaterialSlipConcurrencyTests
    {
        private const string SlipId = "MSCONCUR-S";
        private const string Plant  = "P1";

        private readonly TestWebApplicationFactory _factory;

        public MaterialSlipConcurrencyTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed();
        }

        [Fact]
        public async Task MaterialSlip_ConcurrentIssue_SecondSaveThrowsConcurrency()
        {
            using var scopeA = _factory.Services.CreateScope();
            using var scopeB = _factory.Services.CreateScope();
            var dbA = scopeA.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var dbB = scopeB.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // 兩個 context 各自載入同一筆 Confirmed 領退料單（持相同並發快照）
            var sA = await dbA.MaterialSlips.IgnoreQueryFilters().FirstAsync(s => s.SlipId == SlipId);
            var sB = await dbB.MaterialSlips.IgnoreQueryFilters().FirstAsync(s => s.SlipId == SlipId);

            // A 先過帳（Confirmed → Posted）並提交
            sA.Status = MfgM.MaterialSlipStatus.Posted;
            sA.UpdatedAt = DateTime.UtcNow;
            await dbA.SaveChangesAsync();

            // B 持舊快照過帳 → 若有有效並發權杖，WHERE xmin 比對 0 列 → 樂觀並發例外。
            // 修法前 MaterialSlip 無並發權杖，B 會靜默成功（雙重過帳）。
            sB.Status = MfgM.MaterialSlipStatus.Posted;
            sB.UpdatedAt = DateTime.UtcNow;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // 每次重建為 Confirmed，確保可重跑
            var stale = db.MaterialSlips.IgnoreQueryFilters()
                .Where(s => s.SlipId == SlipId).ToList();
            if (stale.Count > 0) { db.MaterialSlips.RemoveRange(stale); db.SaveChanges(); }

            db.MaterialSlips.Add(new MfgM.MaterialSlip
            {
                SlipId        = SlipId,
                SlipNo        = "MSCONCUR-NO",
                Category      = MfgM.MaterialSlipCategory.InternalIssue,
                Status        = MfgM.MaterialSlipStatus.Confirmed,
                SlipDate      = DateOnly.FromDateTime(DateTime.UtcNow),
                SourceDocType = "WorkOrder",
                SourceDocId   = "MSCONCUR-WO",
                PlantId       = Plant,
            });
            db.SaveChanges();
        }
    }
}
