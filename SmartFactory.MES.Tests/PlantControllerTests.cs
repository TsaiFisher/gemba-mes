using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Controllers;
using SmartFactory.MES.Models;
using Xunit;

namespace SmartFactory.MES.Tests
{
    public class PlantControllerTests : TestBase
    {
        private PlantController _controller;

        public PlantControllerTests()
        {
            _controller = new PlantController(_context, GetMockExportService().Object, GetMockDateTimeService().Object);
            _controller.ControllerContext = GetControllerContext();
        }

        [Fact]
        public async Task Index_ReturnsView_WithPlants()
        {
            // Arrange
            var plant1 = new Plant { Id = "P1", Name = "Plant 1" };
            var plant2 = new Plant { Id = "P2", Name = "Plant 2" };
            _context.Plants.AddRange(plant1, plant2);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Index(null, 1, 10, null, searched: true);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<PaginatedList<Plant>>(viewResult.Model);
            Assert.Equal(2, model.Count);
        }

        [Fact]
        public async Task Create_Post_ValidModel_RedirectsToIndex()
        {
            // Arrange
            var plant = new Plant { Id = "P1", Name = "New Plant" };

            // Act
            var result = await _controller.Create(plant);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(PlantController.Index), redirectResult.ActionName);

            var savedPlant = await _context.Plants.FindAsync("P1");
            Assert.NotNull(savedPlant);
        }

        [Fact]
        public async Task Edit_Post_ValidModel_UpdatesAndRedirects()
        {
            // Arrange
            var plant = new Plant { Id = "P1", Name = "Old Name" };
            _context.Plants.Add(plant);
            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear();

            plant.Name = "New Name";

            // Act
            var result = await _controller.Edit("P1", plant);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(PlantController.Index), redirectResult.ActionName);

            var updatedPlant = await _context.Plants.FindAsync("P1");
            Assert.Equal("New Name", updatedPlant.Name);
        }

        /// <summary>
        /// 編輯「自己目前所在的廠別」不得 500。
        ///
        /// 上面那支 <see cref="Edit_Post_ValidModel_UpdatesAndRedirects"/> 有一行
        /// <c>ChangeTracker.Clear()</c>——正是那一行讓這個缺陷藏了很久：實務上沒有人會清。
        /// IUserContext 解析當前使用者時 <c>.Include(u =&gt; u.ActivePlant)</c>，而管線在動作執行前
        /// 就會用到它，所以進到 Edit 時同一個 DbContext 已經追蹤著那個 Plant，
        /// 舊寫法的 <c>_context.Update(plant)</c> 會直接撞 identity map。
        /// </summary>
        [Fact]
        public async Task Edit_Post_PlantAlreadyTrackedInThisRequest_StillSaves()
        {
            _context.Plants.Add(new Plant { Id = "P1", Name = "Old Name" });
            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear();

            // 模擬 IUserContext 已把 ActivePlant 讀進來（動作執行前就發生）
            var tracked = await _context.Plants.FindAsync("P1");
            Assert.NotNull(tracked);

            // 模型繫結產生的是另一個實例，與 tracked 同鍵不同物件
            var posted = new Plant { Id = "P1", Name = "New Name", DefaultTimeZone = "Asia/Taipei" };

            var result = await _controller.Edit("P1", posted);

            Assert.IsType<RedirectToActionResult>(result);
            _context.ChangeTracker.Clear();
            var saved = await _context.Plants.FindAsync("P1");
            Assert.Equal("New Name", saved!.Name);
            Assert.Equal("Asia/Taipei", saved.DefaultTimeZone);
        }

        [Fact]
        public async Task Delete_Post_Confirmed_DeletesAndRedirects()
        {
            // Arrange
            var plant = new Plant { Id = "P1", Name = "To Delete" };
            _context.Plants.Add(plant);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.DeleteConfirmed("P1");

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(PlantController.Index), redirectResult.ActionName);

            var deletedPlant = await _context.Plants.FindAsync("P1");
            Assert.Null(deletedPlant);
        }
        [Fact]
        public async Task Index_ReturnsSortedPlants_WhenSortOrderIsProvided()
        {
            // Arrange
            _context.Plants.Add(new Plant { Id = "P2", Name = "B_Plant" });
            _context.Plants.Add(new Plant { Id = "P1", Name = "A_Plant" });
            await _context.SaveChangesAsync();

            // Act - Sort by Name Asc
            var result = await _controller.Index(null, 1, 10, "name_asc", searched: true);
            var model = Assert.IsAssignableFrom<PaginatedList<Plant>>(((ViewResult)result).Model);
            
            Assert.Equal("A_Plant", model[0].Name);
            Assert.Equal("B_Plant", model[1].Name);
        }
    }
}
