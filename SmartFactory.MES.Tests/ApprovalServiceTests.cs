using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    public class ApprovalServiceTests
    {
        private ApplicationDbContext GetDbContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            return new ApplicationDbContext(options);
        }

        [Fact]
        public async Task SubmitForApproval_CorrectInitialStep_ReturnsRequest()
        {
            // Arrange
            var db = GetDbContext();
            var template = new ApprovalTemplate { Id = "T1", Name = "Test Template" };
            var step1 = new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1, ApproverType = ApproverType.User };
            db.ApprovalTemplates.Add(template);
            db.ApprovalSteps.Add(step1);
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            // Act
            var request = await service.SubmitForApproval("Doc1", "WorkOrder", "T1", "user1");

            // Assert
            Assert.NotNull(request);
            Assert.Equal("S1", request.CurrentStepId);
            Assert.Equal(ApprovalStatus.Pending, request.Status);
        }

        [Fact]
        public async Task ProcessApproval_ApproveLastStep_SetsStatusApproved()
        {
            // Arrange
            var db = GetDbContext();
            var template = new ApprovalTemplate { Id = "T1", Name = "Test Template" };
            var step1 = new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1, ApproverType = ApproverType.User, TargetUserId = "User1" };
            db.ApprovalTemplates.Add(template);
            db.ApprovalSteps.Add(step1);

            var request = new ApprovalRequest
            { 
                Id = "R1", 
                DocumentId = "Doc1", 
                DocumentType = "WorkOrder", 
                CurrentStepId = "S1", 
                Status = ApprovalStatus.Pending 
            };
            db.ApprovalRequests.Add(request);
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            // Act
            var result = await service.ProcessApproval("R1", "User1", "Role1", ApprovalStatus.Approved, "Looks good");

            // Assert
            Assert.True(result);
            var updatedRequest = await db.ApprovalRequests.FindAsync("R1");
            Assert.Equal(ApprovalStatus.Approved, updatedRequest?.Status);
        }

        [Fact]
        public async Task ProcessApproval_Reject_SetsStatusRejected()
        {
            // Arrange
            var db = GetDbContext();
            var request = new ApprovalRequest
            {
                Id = "R1",
                DocumentId = "Doc1",
                DocumentType = "WorkOrder",
                CurrentStepId = "S1",
                Status = ApprovalStatus.Pending
            };
            var step = new ApprovalStep { Id = "S1", Sequence = 1, ApproverType = ApproverType.User, TargetUserId = "User1" };
            db.ApprovalSteps.Add(step);
            db.ApprovalRequests.Add(request);
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            // Act
            await service.ProcessApproval("R1", "User1", "Role1", ApprovalStatus.Rejected, "Too expensive");

            // Assert
            var updatedRequest = await db.ApprovalRequests.FindAsync("R1");
            Assert.Equal(ApprovalStatus.Rejected, updatedRequest?.Status);
        }

        [Fact]
        public async Task ProcessApproval_MultiStep_ApprovesFirstStep_MovesToSecondStep()
        {
            // Arrange
            var db = GetDbContext();
            var template = new ApprovalTemplate { Id = "T1", Name = "Two-Step" };
            var step1 = new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1, ApproverType = ApproverType.Role, TargetRoleId = "Role1" };
            var step2 = new ApprovalStep { Id = "S2", TemplateId = "T1", Sequence = 2, ApproverType = ApproverType.Role, TargetRoleId = "Role2" };
            db.ApprovalTemplates.Add(template);
            db.ApprovalSteps.AddRange(step1, step2);

            var request = new ApprovalRequest
            {
                Id = "R1", DocumentId = "D1", DocumentType = "WO",
                CurrentStepId = "S1", Status = ApprovalStatus.Pending
            };
            db.ApprovalRequests.Add(request);
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            // Act — approve first step
            var result = await service.ProcessApproval("R1", "User1", "Role1", ApprovalStatus.Approved, null);

            // Assert — request still Pending, now at step 2
            Assert.True(result);
            var updated = await db.ApprovalRequests.FindAsync("R1");
            Assert.Equal(ApprovalStatus.Pending, updated!.Status);
            Assert.Equal("S2", updated.CurrentStepId);
        }

        [Fact]
        public async Task ProcessApproval_CreatesAuditLog()
        {
            // Arrange
            var db = GetDbContext();
            var step = new ApprovalStep { Id = "S1", Sequence = 1, ApproverType = ApproverType.Role, TargetRoleId = "RoleA" };
            var request = new ApprovalRequest
            {
                Id = "R1", DocumentId = "D1", DocumentType = "WO",
                CurrentStepId = "S1", Status = ApprovalStatus.Pending
            };
            db.ApprovalSteps.Add(step);
            db.ApprovalRequests.Add(request);
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            // Act
            await service.ProcessApproval("R1", "Approver1", "RoleA", ApprovalStatus.Approved, "OK");

            // Assert
            var log = db.ApprovalLogs.SingleOrDefault(l => l.RequestId == "R1");
            Assert.NotNull(log);
            Assert.Equal("Approver1", log!.ApproverId);
            Assert.Equal("RoleA", log.ApproverRoleId);
            Assert.Equal(ApprovalStatus.Approved, log.Action);
            Assert.Equal("OK", log.Comment);
        }

        [Fact]
        public async Task ProcessApproval_UnauthorizedApprover_ReturnsFalse()
        {
            // 安全回歸：非當前步驟指定的簽核人/角色不得核准（2026-06 安全修正）
            var db = GetDbContext();
            var step = new ApprovalStep { Id = "S1", Sequence = 1, ApproverType = ApproverType.Role, TargetRoleId = "ManagerRole" };
            var request = new ApprovalRequest
            {
                Id = "R1", DocumentId = "D1", DocumentType = "WO",
                CurrentStepId = "S1", Status = ApprovalStatus.Pending
            };
            db.ApprovalSteps.Add(step);
            db.ApprovalRequests.Add(request);
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            // Act — 以其他角色嘗試核准
            var result = await service.ProcessApproval("R1", "Intruder", "OtherRole", ApprovalStatus.Approved, null);

            // Assert — 拒絕執行、狀態不變、不留簽核 Log
            Assert.False(result);
            var updated = await db.ApprovalRequests.FindAsync("R1");
            Assert.Equal(ApprovalStatus.Pending, updated!.Status);
            Assert.Empty(db.ApprovalLogs.Where(l => l.RequestId == "R1"));
        }

        [Fact]
        public async Task ProcessApproval_RequestNotFound_ReturnsFalse()
        {
            var db = GetDbContext();
            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            // Act
            var result = await service.ProcessApproval("nonexistent", "U1", "R1", ApprovalStatus.Approved, null);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task SubmitForApproval_TemplateNotFound_ThrowsException()
        {
            var db = GetDbContext();
            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SubmitForApproval("D1", "WO", "nonexistent-template", "user1"));
        }

        [Fact]
        public async Task SubmitForApproval_TemplateWithNoSteps_ThrowsException()
        {
            // Arrange
            var db = GetDbContext();
            db.ApprovalTemplates.Add(new ApprovalTemplate { Id = "T1", Name = "Empty" });
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SubmitForApproval("D1", "WO", "T1", "user1"));
        }

        [Fact]
        public async Task SubmitForApproval_MultipleSteps_SetsFirstStepBySequence()
        {
            // Arrange — step S2 is Sequence=2, S1 is Sequence=1; S2 added first to db
            var db = GetDbContext();
            var template = new ApprovalTemplate { Id = "T1", Name = "T" };
            db.ApprovalTemplates.Add(template);
            db.ApprovalSteps.AddRange(
                new ApprovalStep { Id = "S2", TemplateId = "T1", Sequence = 2 },
                new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1 }
            );
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            // Act
            var request = await service.SubmitForApproval("D1", "WO", "T1", "user1");

            // Assert — must start at the step with lowest Sequence
            Assert.Equal("S1", request.CurrentStepId);
        }
    }
}
