using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Controllers;
using SmartFactory.MES.Models;
using Xunit;

namespace SmartFactory.MES.Tests
{
    public class ApprovalTemplateControllerTests : TestBase
    {
        private readonly ApprovalTemplateController _controller;
        private readonly Mock<RoleManager<ApplicationRole>> _mockRoleManager;
        private readonly Mock<UserManager<ApplicationUser>> _mockUserManager;

        public ApprovalTemplateControllerTests()
        {
            _mockRoleManager = GetMockRoleManager<ApplicationRole>();
            _mockUserManager = GetMockUserManager();

            _mockRoleManager.Setup(r => r.Roles)
                .Returns(new List<ApplicationRole>().AsQueryable());
            _mockUserManager.Setup(u => u.Users)
                .Returns(new List<ApplicationUser>().AsQueryable());

            _controller = new ApprovalTemplateController(_context, _mockRoleManager.Object, _mockUserManager.Object, GetMockExportService().Object, GetMockDateTimeService().Object);
            _controller.ControllerContext = GetControllerContext();
        }

        // ── Index ─────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Index_NoSearch_ReturnsNullModel()
        {
            // Act
            var result = await _controller.Index(null, null, null, searched: false);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.Null(viewResult.Model);
        }

        [Fact]
        public async Task Index_WithSearch_ReturnsMatchingTemplates()
        {
            // Arrange
            _context.ApprovalTemplates.AddRange(
                new ApprovalTemplate { Id = "T1", Name = "工單簽核" },
                new ApprovalTemplate { Id = "T2", Name = "採購簽核" }
            );
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Index("工單", null, null, searched: true);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<ApprovalTemplate>>(viewResult.Model);
            Assert.Single(model);
            Assert.Equal("T1", model.First().Id);
        }

        [Fact]
        public async Task Index_SearchEmpty_ReturnsAllTemplates()
        {
            // Arrange
            _context.ApprovalTemplates.AddRange(
                new ApprovalTemplate { Id = "T1", Name = "工單簽核" },
                new ApprovalTemplate { Id = "T2", Name = "採購簽核" }
            );
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Index(null, null, null, searched: true);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<ApprovalTemplate>>(viewResult.Model);
            Assert.Equal(2, model.Count());
        }

        // ── Create ────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Create_POST_ValidModel_SavesAndRedirects()
        {
            // Arrange
            var template = new ApprovalTemplate { Id = "T1", Name = "新模板", TargetDocumentType = "WorkOrder" };

            // Act
            var result = await _controller.Create(template);

            // Assert
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(ApprovalTemplateController.Index), redirect.ActionName);

            var saved = await _context.ApprovalTemplates.FindAsync("T1");
            Assert.NotNull(saved);
            Assert.Equal("新模板", saved.Name);
        }

        [Fact]
        public async Task Create_POST_AjaxRequest_ReturnsJson()
        {
            // Arrange
            var template = new ApprovalTemplate { Id = "T1", Name = "新模板" };
            SetAjaxHeader();

            // Act
            var result = await _controller.Create(template);

            // Assert
            var json = Assert.IsType<JsonResult>(result);
            dynamic data = json.Value!;
            Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        }

        // ── Edit ──────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Edit_GET_ExistingTemplate_ReturnsView()
        {
            // Arrange
            _context.ApprovalTemplates.Add(new ApprovalTemplate { Id = "T1", Name = "Old Name" });
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Edit("T1");

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<ApprovalTemplate>(viewResult.Model);
            Assert.Equal("T1", model.Id);
        }

        [Fact]
        public async Task Edit_GET_NotFound_Returns404()
        {
            // Act
            var result = await _controller.Edit("nonexistent");

            // Assert
            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task Edit_POST_ValidModel_UpdatesAndRedirects()
        {
            // Arrange
            _context.ApprovalTemplates.Add(new ApprovalTemplate { Id = "T1", Name = "Old Name" });
            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear();

            var updated = new ApprovalTemplate { Id = "T1", Name = "New Name", TargetDocumentType = "WO" };

            // Act
            var result = await _controller.Edit("T1", updated);

            // Assert
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(ApprovalTemplateController.Index), redirect.ActionName);

            var saved = await _context.ApprovalTemplates.FindAsync("T1");
            Assert.Equal("New Name", saved!.Name);
            Assert.Equal("WO", saved.TargetDocumentType);
        }

        [Fact]
        public async Task Edit_POST_IdMismatch_Returns404()
        {
            // Arrange
            var updated = new ApprovalTemplate { Id = "T2", Name = "New Name" };

            // Act
            var result = await _controller.Edit("T1", updated);

            // Assert
            Assert.IsType<NotFoundResult>(result);
        }

        // ── Delete ────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Delete_RemovesTemplateAndAllSteps()
        {
            // Arrange
            var template = new ApprovalTemplate { Id = "T1", Name = "Test" };
            var step1 = new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1 };
            var step2 = new ApprovalStep { Id = "S2", TemplateId = "T1", Sequence = 2 };
            _context.ApprovalTemplates.Add(template);
            _context.ApprovalSteps.AddRange(step1, step2);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.DeleteConfirmed("T1");

            // Assert
            Assert.Null(await _context.ApprovalTemplates.FindAsync("T1"));
            Assert.Empty(_context.ApprovalSteps.Where(s => s.TemplateId == "T1"));
        }

        [Fact]
        public async Task Delete_AjaxRequest_ReturnsSuccessJson()
        {
            // Arrange
            _context.ApprovalTemplates.Add(new ApprovalTemplate { Id = "T1", Name = "Test" });
            await _context.SaveChangesAsync();
            SetAjaxHeader();

            // Act
            var result = await _controller.DeleteConfirmed("T1");

            // Assert
            var json = Assert.IsType<JsonResult>(result);
            dynamic data = json.Value!;
            Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        }

        // ── AddStep ───────────────────────────────────────────────────────────────

        [Fact]
        public async Task AddStep_AutoAssignsSequence_StartingAt1()
        {
            // Arrange
            _context.ApprovalTemplates.Add(new ApprovalTemplate { Id = "T1", Name = "T" });
            await _context.SaveChangesAsync();

            var step = new ApprovalStep { TemplateId = "T1", ApproverType = ApproverType.User };

            // Act
            await _controller.AddStep(step);

            // Assert
            var saved = _context.ApprovalSteps.Single(s => s.TemplateId == "T1");
            Assert.Equal(1, saved.Sequence);
        }

        [Fact]
        public async Task AddStep_AutoSequence_AppendsAfterExisting()
        {
            // Arrange
            _context.ApprovalTemplates.Add(new ApprovalTemplate { Id = "T1", Name = "T" });
            _context.ApprovalSteps.AddRange(
                new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1 },
                new ApprovalStep { Id = "S2", TemplateId = "T1", Sequence = 2 }
            );
            await _context.SaveChangesAsync();

            var newStep = new ApprovalStep { TemplateId = "T1", ApproverType = ApproverType.User };

            // Act
            await _controller.AddStep(newStep);

            // Assert
            Assert.Equal(3, newStep.Sequence);
        }

        // ── DeleteStep ────────────────────────────────────────────────────────────

        [Fact]
        public async Task DeleteStep_RemovesStep_AndResequencesRemaining()
        {
            // Arrange
            _context.ApprovalTemplates.Add(new ApprovalTemplate { Id = "T1", Name = "T" });
            _context.ApprovalSteps.AddRange(
                new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1 },
                new ApprovalStep { Id = "S2", TemplateId = "T1", Sequence = 2 },
                new ApprovalStep { Id = "S3", TemplateId = "T1", Sequence = 3 }
            );
            await _context.SaveChangesAsync();

            // Act — delete the middle step
            await _controller.DeleteStepConfirmed("S2", "T1");

            // Assert
            var remaining = _context.ApprovalSteps
                .Where(s => s.TemplateId == "T1")
                .OrderBy(s => s.Sequence)
                .ToList();

            Assert.Equal(2, remaining.Count);
            Assert.Equal(1, remaining[0].Sequence);
            Assert.Equal(2, remaining[1].Sequence);   // S3 renumbered from 3 → 2
        }

        [Fact]
        public async Task DeleteStep_OnlyStep_LeavesEmptyList()
        {
            // Arrange
            _context.ApprovalTemplates.Add(new ApprovalTemplate { Id = "T1", Name = "T" });
            _context.ApprovalSteps.Add(new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1 });
            await _context.SaveChangesAsync();

            // Act
            await _controller.DeleteStepConfirmed("S1", "T1");

            // Assert
            Assert.Empty(_context.ApprovalSteps.Where(s => s.TemplateId == "T1"));
        }

        // ── MoveStep ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task MoveStep_Up_SwapsWithPreviousStep()
        {
            // Arrange
            _context.ApprovalTemplates.Add(new ApprovalTemplate { Id = "T1", Name = "T" });
            _context.ApprovalSteps.AddRange(
                new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1 },
                new ApprovalStep { Id = "S2", TemplateId = "T1", Sequence = 2 }
            );
            await _context.SaveChangesAsync();

            // Act — move S2 up (S2 should become Sequence=1, S1 becomes Sequence=2)
            var result = await _controller.MoveStep("S2", "T1", "up");

            // Assert
            var json = Assert.IsType<JsonResult>(result);
            dynamic data = json.Value!;
            Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);

            var s1 = await _context.ApprovalSteps.FindAsync("S1");
            var s2 = await _context.ApprovalSteps.FindAsync("S2");
            Assert.Equal(2, s1!.Sequence);
            Assert.Equal(1, s2!.Sequence);
        }

        [Fact]
        public async Task MoveStep_Down_SwapsWithNextStep()
        {
            // Arrange
            _context.ApprovalTemplates.Add(new ApprovalTemplate { Id = "T1", Name = "T" });
            _context.ApprovalSteps.AddRange(
                new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1 },
                new ApprovalStep { Id = "S2", TemplateId = "T1", Sequence = 2 }
            );
            await _context.SaveChangesAsync();

            // Act — move S1 down
            var result = await _controller.MoveStep("S1", "T1", "down");

            // Assert
            var s1 = await _context.ApprovalSteps.FindAsync("S1");
            var s2 = await _context.ApprovalSteps.FindAsync("S2");
            Assert.Equal(2, s1!.Sequence);
            Assert.Equal(1, s2!.Sequence);
        }

        [Fact]
        public async Task MoveStep_FirstStepMoveUp_ReturnsFalse()
        {
            // Arrange
            _context.ApprovalTemplates.Add(new ApprovalTemplate { Id = "T1", Name = "T" });
            _context.ApprovalSteps.Add(new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1 });
            await _context.SaveChangesAsync();

            // Act — cannot move the only/first step up
            var result = await _controller.MoveStep("S1", "T1", "up");

            // Assert
            var json = Assert.IsType<JsonResult>(result);
            dynamic data = json.Value!;
            Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        }

        [Fact]
        public async Task MoveStep_LastStepMoveDown_ReturnsFalse()
        {
            // Arrange
            _context.ApprovalTemplates.Add(new ApprovalTemplate { Id = "T1", Name = "T" });
            _context.ApprovalSteps.Add(new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1 });
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.MoveStep("S1", "T1", "down");

            // Assert
            var json = Assert.IsType<JsonResult>(result);
            dynamic data = json.Value!;
            Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        }

        // ── helpers ───────────────────────────────────────────────────────────────

        private void SetAjaxHeader()
        {
            _controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        }
    }
}
