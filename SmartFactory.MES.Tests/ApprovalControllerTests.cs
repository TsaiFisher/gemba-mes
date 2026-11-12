using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFactory.MES.Controllers;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using System.Security.Claims;
using Xunit;

namespace SmartFactory.MES.Tests
{
    public class ApprovalControllerTests : TestBase
    {
        private readonly Mock<UserManager<ApplicationUser>> _mockUserManager;
        private readonly Mock<IApprovalService> _mockApprovalService;
        private readonly Mock<IPermissionService> _mockPermissionService;
        private readonly ApprovalController _controller;
        private readonly ApplicationUser _testUser;

        public ApprovalControllerTests()
        {
            _mockUserManager = GetMockUserManager();
            _mockApprovalService = new Mock<IApprovalService>();
            _mockPermissionService = new Mock<IPermissionService>();
            _mockPermissionService.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
            _mockPermissionService.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);

            _testUser = new ApplicationUser
            {
                Id = "User1",
                UserName = "testuser",
                ActiveRoleId = "Role1"
            };
            _mockUserManager
                .Setup(m => m.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
                .ReturnsAsync(_testUser);

            // PendingIndex 已改為委派 ApprovalService.GetMyPendingRequestsAsync：
            // 這裡讓 mock 轉呼叫掛在同一個 InMemory context 上的真實服務，維持原測試對過濾語意的驗證。
            _mockApprovalService
                .Setup(s => s.GetMyPendingRequestsAsync(It.IsAny<string>(), It.IsAny<string?>()))
                .Returns((string uid, string? rid) =>
                    new ApprovalService(_context, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>())
                        .GetMyPendingRequestsAsync(uid, rid));

            _controller = new ApprovalController(_context, _mockUserManager.Object, _mockApprovalService.Object, _mockPermissionService.Object);
            _controller.ControllerContext = GetControllerContext();
        }

        // ── PendingIndex ──────────────────────────────────────────────────────────

        [Fact]
        public async Task PendingIndex_ReturnsOnlyRequestsForCurrentUser_ByRole()
        {
            // Arrange: step targets Role1 (matches _testUser.ActiveRoleId)
            var step = new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1, ApproverType = ApproverType.Role, TargetRoleId = "Role1" };
            var myRequest = new ApprovalRequest { Id = "R1", DocumentId = "D1", DocumentType = "WO", CurrentStepId = "S1", Status = ApprovalStatus.Pending };
            // step targets a different role
            var otherStep = new ApprovalStep { Id = "S2", TemplateId = "T1", Sequence = 1, ApproverType = ApproverType.Role, TargetRoleId = "Role99" };
            var otherRequest = new ApprovalRequest { Id = "R2", DocumentId = "D2", DocumentType = "WO", CurrentStepId = "S2", Status = ApprovalStatus.Pending };

            _context.ApprovalSteps.AddRange(step, otherStep);
            _context.ApprovalRequests.AddRange(myRequest, otherRequest);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.PendingIndex();

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<ApprovalRequest>>(viewResult.Model);
            Assert.Single(model);
            Assert.Equal("R1", model.First().Id);
        }

        [Fact]
        public async Task PendingIndex_ReturnsOnlyRequestsForCurrentUser_ByUserId()
        {
            // Arrange: step targets User1 directly
            var step = new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1, ApproverType = ApproverType.User, TargetUserId = "User1" };
            var myRequest = new ApprovalRequest { Id = "R1", DocumentId = "D1", DocumentType = "WO", CurrentStepId = "S1", Status = ApprovalStatus.Pending };
            // step targets another user
            var otherStep = new ApprovalStep { Id = "S2", TemplateId = "T1", Sequence = 1, ApproverType = ApproverType.User, TargetUserId = "User99" };
            var otherRequest = new ApprovalRequest { Id = "R2", DocumentId = "D2", DocumentType = "WO", CurrentStepId = "S2", Status = ApprovalStatus.Pending };

            _context.ApprovalSteps.AddRange(step, otherStep);
            _context.ApprovalRequests.AddRange(myRequest, otherRequest);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.PendingIndex();

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<ApprovalRequest>>(viewResult.Model);
            Assert.Single(model);
            Assert.Equal("R1", model.First().Id);
        }

        [Fact]
        public async Task PendingIndex_ExcludesNonPendingRequests()
        {
            // Arrange: approved request with matching role step should NOT appear
            var step = new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1, ApproverType = ApproverType.Role, TargetRoleId = "Role1" };
            var approvedRequest = new ApprovalRequest { Id = "R1", DocumentId = "D1", DocumentType = "WO", CurrentStepId = "S1", Status = ApprovalStatus.Approved };

            _context.ApprovalSteps.Add(step);
            _context.ApprovalRequests.Add(approvedRequest);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.PendingIndex();

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<ApprovalRequest>>(viewResult.Model);
            Assert.Empty(model);
        }

        // ── AllRequests ───────────────────────────────────────────────────────────

        [Fact]
        public async Task AllRequests_NoFilter_ReturnsAllRequests()
        {
            // Arrange
            _context.ApprovalRequests.AddRange(
                new ApprovalRequest { Id = "R1", DocumentId = "D1", DocumentType = "WO", Status = ApprovalStatus.Pending },
                new ApprovalRequest { Id = "R2", DocumentId = "D2", DocumentType = "WO", Status = ApprovalStatus.Approved },
                new ApprovalRequest { Id = "R3", DocumentId = "D3", DocumentType = "PO", Status = ApprovalStatus.Rejected }
            );
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.AllRequests(null, null);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<ApprovalRequest>>(viewResult.Model);
            Assert.Equal(3, model.Count());
        }

        [Fact]
        public async Task AllRequests_FilterByStatus_ReturnsMatchingOnly()
        {
            // Arrange
            _context.ApprovalRequests.AddRange(
                new ApprovalRequest { Id = "R1", DocumentId = "D1", DocumentType = "WO", Status = ApprovalStatus.Pending },
                new ApprovalRequest { Id = "R2", DocumentId = "D2", DocumentType = "WO", Status = ApprovalStatus.Approved }
            );
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.AllRequests("Approved", null);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<ApprovalRequest>>(viewResult.Model);
            Assert.Single(model);
            Assert.Equal(ApprovalStatus.Approved, model.First().Status);
        }

        [Fact]
        public async Task AllRequests_FilterByDocumentType_ReturnsMatchingOnly()
        {
            // Arrange
            _context.ApprovalRequests.AddRange(
                new ApprovalRequest { Id = "R1", DocumentId = "D1", DocumentType = "WorkOrder", Status = ApprovalStatus.Pending },
                new ApprovalRequest { Id = "R2", DocumentId = "D2", DocumentType = "PurchaseOrder", Status = ApprovalStatus.Pending }
            );
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.AllRequests(null, "WorkOrder");

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<ApprovalRequest>>(viewResult.Model);
            Assert.Single(model);
            Assert.Equal("WorkOrder", model.First().DocumentType);
        }

        // ── Process GET ───────────────────────────────────────────────────────────

        [Fact]
        public async Task Process_GET_ExistingPendingRequest_ReturnsView()
        {
            // Arrange
            var step = new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1 };
            var request = new ApprovalRequest { Id = "R1", DocumentId = "D1", DocumentType = "WO", CurrentStepId = "S1", Status = ApprovalStatus.Pending };
            _context.ApprovalSteps.Add(step);
            _context.ApprovalRequests.Add(request);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Process("R1");

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.NotNull(viewResult.Model);
        }

        [Fact]
        public async Task Process_GET_NotFound_Returns404()
        {
            // Act
            var result = await _controller.Process("nonexistent");

            // Assert
            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task Process_GET_AlreadyApproved_ReturnsBadRequest()
        {
            // Arrange
            var request = new ApprovalRequest { Id = "R1", DocumentId = "D1", DocumentType = "WO", CurrentStepId = "S1", Status = ApprovalStatus.Approved };
            _context.ApprovalRequests.Add(request);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Process("R1");

            // Assert
            Assert.IsType<BadRequestObjectResult>(result);
        }

        // ── Process POST ──────────────────────────────────────────────────────────

        [Fact]
        public async Task Process_POST_Approve_Success_ReturnsJson()
        {
            // Arrange
            _mockApprovalService
                .Setup(s => s.ProcessApproval("R1", "User1", "Role1", ApprovalStatus.Approved, It.IsAny<string?>()))
                .ReturnsAsync(true);

            SetAjaxHeader();

            // Act
            var result = await _controller.Process("R1", ApprovalStatus.Approved, "Looks good");

            // Assert
            var jsonResult = Assert.IsType<JsonResult>(result);
            dynamic data = jsonResult.Value!;
            Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        }

        [Fact]
        public async Task Process_POST_Reject_Success_ReturnsJsonWithMessage()
        {
            // Arrange
            _mockApprovalService
                .Setup(s => s.ProcessApproval("R1", "User1", "Role1", ApprovalStatus.Rejected, "Too expensive"))
                .ReturnsAsync(true);

            SetAjaxHeader();

            // Act
            var result = await _controller.Process("R1", ApprovalStatus.Rejected, "Too expensive");

            // Assert
            var jsonResult = Assert.IsType<JsonResult>(result);
            dynamic data = jsonResult.Value!;
            Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
            string message = (string)data.GetType().GetProperty("message")!.GetValue(data)!;
            Assert.Equal(SmartFactory.MES.Services.CoreText.Get("Msg_ApprovalDoneRejected"), message);
        }

        [Fact]
        public async Task Process_POST_ServiceFails_ReturnsFailureJson()
        {
            // Arrange
            _mockApprovalService
                .Setup(s => s.ProcessApproval(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ApprovalStatus>(), It.IsAny<string?>()))
                .ReturnsAsync(false);

            SetAjaxHeader();

            // Act
            var result = await _controller.Process("R1", ApprovalStatus.Approved, null);

            // Assert
            var jsonResult = Assert.IsType<JsonResult>(result);
            dynamic data = jsonResult.Value!;
            Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        }

        // ── History ───────────────────────────────────────────────────────────────

        [Fact]
        public async Task History_ExistingRequest_ReturnsLogs()
        {
            // Arrange — add user to the real DbContext so Users DbSet supports ToListAsync
            _context.Users.Add(_testUser);
            var request = new ApprovalRequest { Id = "R1", DocumentId = "D1", DocumentType = "WO", Status = ApprovalStatus.Approved };
            var log1 = new ApprovalLog { Id = "L1", RequestId = "R1", ApproverId = "User1", Action = ApprovalStatus.Approved, ActionTime = DateTime.UtcNow };
            var log2 = new ApprovalLog { Id = "L2", RequestId = "R1", ApproverId = "User1", Action = ApprovalStatus.Approved, ActionTime = DateTime.UtcNow };
            _context.ApprovalRequests.Add(request);
            _context.ApprovalLogs.AddRange(log1, log2);
            await _context.SaveChangesAsync();

            // Use the real DbSet (supports IAsyncEnumerable) instead of List.AsQueryable()
            _mockUserManager.Setup(m => m.Users).Returns(_context.Users);

            // Act
            var result = await _controller.History("R1");

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<ApprovalLog>>(viewResult.Model);
            Assert.Equal(2, model.Count());
        }

        [Fact]
        public async Task History_NotFound_Returns404()
        {
            // Act
            var result = await _controller.History("nonexistent");

            // Assert
            Assert.IsType<NotFoundResult>(result);
        }

        // ── helpers ───────────────────────────────────────────────────────────────

        private void SetAjaxHeader()
        {
            _controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        }
    }
}
