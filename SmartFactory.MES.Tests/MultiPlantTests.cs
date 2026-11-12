using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 測試 ApplicationDbContext 的多廠別 PlantId 過濾器（IPlantEntity global query filter）。
    /// 使用 ApprovalTemplate 作為測試資料實體（同樣實作 IPlantEntity）。
    /// </summary>
    public class MultiPlantTests
    {
        [Fact]
        public async Task PlantFilter_OnlyReturnsCurrentPlantData()
        {
            // Arrange
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: "MultiPlantTest_PlantFilter")
                .Options;

            using var context = new ApplicationDbContext(options);

            // 建立不同廠別的 ApprovalTemplate
            context.ApprovalTemplates.AddRange(
                new ApprovalTemplate { Id = "T-P1-001", Name = "Template P1-1", PlantId = "P1" },
                new ApprovalTemplate { Id = "T-P2-001", Name = "Template P2-1", PlantId = "P2" },
                new ApprovalTemplate { Id = "T-P1-002", Name = "Template P1-2", PlantId = "P1" }
            );
            await context.SaveChangesAsync();

            // Act - 設定 CurrentPlantId 為 P1
            context.CurrentPlantId = "P1";
            var templates = await context.ApprovalTemplates.ToListAsync();

            // Assert - 僅返回 P1 的資料
            Assert.Equal(2, templates.Count);
            Assert.All(templates, t => Assert.Equal("P1", t.PlantId));
        }

        [Fact]
        public async Task ApplyPlantId_AutoFillsPlantIdOnNewEntity()
        {
            // Arrange
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: "MultiPlantTest_ApplyPlantId")
                .Options;

            using var context = new ApplicationDbContext(options);
            context.CurrentPlantId = "P1";

            // Act - 新增 ApprovalTemplate 但不設定 PlantId
            var tmpl = new ApprovalTemplate { Id = "AUTO-001", Name = "Auto Template" };
            context.ApprovalTemplates.Add(tmpl);
            await context.SaveChangesAsync();

            // Assert - PlantId 應自動填充
            Assert.Equal("P1", tmpl.PlantId);
        }
    }
}
