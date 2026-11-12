using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 補充 ApprovalServiceTests 未涵蓋的方法：
    /// GetApprovalConfig、WithdrawRequest、ReassignApprover
    /// </summary>
    public class ApprovalServiceExtendedTests
    {
        private ApplicationDbContext GetDbContext(string? currentPlantId = null)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            return new ApplicationDbContext(options) { CurrentPlantId = currentPlantId };
        }

        // ── GetApprovalConfig ─────────────────────────────────────────────────────

        [Fact]
        public async Task GetApprovalConfig_MatchingPlant_ReturnsConfig()
        {
            var db = GetDbContext(currentPlantId: "P1");
            db.DocumentApprovalConfigs.Add(new DocumentApprovalConfig
            {
                Id = "CFG1",
                DocumentType = "PurchaseOrder",
                DocumentTypeName = "請購單",
                PlantId = "P1",
                IsApprovalRequired = true
            });
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            var config = await service.GetApprovalConfig("PurchaseOrder", "P1");

            Assert.NotNull(config);
            Assert.Equal("CFG1", config!.Id);
            Assert.True(config.IsApprovalRequired);
        }

        [Fact]
        public async Task GetApprovalConfig_DifferentPlant_ReturnsNull()
        {
            var db = GetDbContext(currentPlantId: "P1");
            db.DocumentApprovalConfigs.Add(new DocumentApprovalConfig
            {
                Id = "CFG1",
                DocumentType = "PurchaseOrder",
                DocumentTypeName = "請購單",
                PlantId = "P2"     // 不同廠別
            });
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            var config = await service.GetApprovalConfig("PurchaseOrder", "P2");

            // 全域查詢過濾器 CurrentPlantId = "P1"，P2 的資料不可見
            Assert.Null(config);
        }

        [Fact]
        public async Task GetApprovalConfig_WrongDocumentType_ReturnsNull()
        {
            var db = GetDbContext(currentPlantId: "P1");
            db.DocumentApprovalConfigs.Add(new DocumentApprovalConfig
            {
                Id = "CFG1",
                DocumentType = "PurchaseOrder",
                DocumentTypeName = "請購單",
                PlantId = "P1"
            });
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            var config = await service.GetApprovalConfig("WorkOrder", "P1");

            Assert.Null(config);
        }

        [Fact]
        public async Task GetApprovalConfig_IsApprovalRequired_False_ReturnsConfig()
        {
            // 即使不需要簽核，設定本身仍然存在且可查詢
            var db = GetDbContext(currentPlantId: "P1");
            db.DocumentApprovalConfigs.Add(new DocumentApprovalConfig
            {
                Id = "CFG1",
                DocumentType = "WorkOrder",
                DocumentTypeName = "工單",
                PlantId = "P1",
                IsApprovalRequired = false
            });
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            var config = await service.GetApprovalConfig("WorkOrder", "P1");

            Assert.NotNull(config);
            Assert.False(config!.IsApprovalRequired);
        }

        // ── WithdrawRequest ───────────────────────────────────────────────────────

        [Fact]
        public async Task WithdrawRequest_ByRequester_SetsWithdrawnStatus()
        {
            var db = GetDbContext();
            var step = new ApprovalStep { Id = "S1", Sequence = 1 };
            var request = new ApprovalRequest
            {
                Id = "R1", DocumentId = "D1", DocumentType = "WO",
                CurrentStepId = "S1", Status = ApprovalStatus.Pending, RequesterId = "User1"
            };
            db.ApprovalSteps.Add(step);
            db.ApprovalRequests.Add(request);
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            var result = await service.WithdrawRequest("R1", "User1");

            Assert.True(result);
            var updated = await db.ApprovalRequests.FindAsync("R1");
            Assert.Equal(ApprovalStatus.Withdrawn, updated!.Status);
        }

        [Fact]
        public async Task WithdrawRequest_ByRequester_CreatesAuditLog()
        {
            var db = GetDbContext();
            var step = new ApprovalStep { Id = "S1", Sequence = 1 };
            var request = new ApprovalRequest
            {
                Id = "R1", DocumentId = "D1", DocumentType = "WO",
                CurrentStepId = "S1", Status = ApprovalStatus.Pending, RequesterId = "User1"
            };
            db.ApprovalSteps.Add(step);
            db.ApprovalRequests.Add(request);
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());
            await service.WithdrawRequest("R1", "User1");

            var log = db.ApprovalLogs.SingleOrDefault(l => l.RequestId == "R1");
            Assert.NotNull(log);
            Assert.Equal(ApprovalStatus.Withdrawn, log!.Action);
            Assert.Equal("User1", log.ApproverId);
        }

        [Fact]
        public async Task WithdrawRequest_ByNonRequester_ReturnsFalse_StatusUnchanged()
        {
            var db = GetDbContext();
            var step = new ApprovalStep { Id = "S1", Sequence = 1 };
            var request = new ApprovalRequest
            {
                Id = "R1", DocumentId = "D1", DocumentType = "WO",
                CurrentStepId = "S1", Status = ApprovalStatus.Pending, RequesterId = "User1"
            };
            db.ApprovalSteps.Add(step);
            db.ApprovalRequests.Add(request);
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            var result = await service.WithdrawRequest("R1", "User99");   // 不是申請人

            Assert.False(result);
            var updated = await db.ApprovalRequests.FindAsync("R1");
            Assert.Equal(ApprovalStatus.Pending, updated!.Status);
        }

        [Fact]
        public async Task WithdrawRequest_AlreadyApproved_ReturnsFalse()
        {
            var db = GetDbContext();
            var step = new ApprovalStep { Id = "S1", Sequence = 1 };
            var request = new ApprovalRequest
            {
                Id = "R1", DocumentId = "D1", DocumentType = "WO",
                CurrentStepId = "S1", Status = ApprovalStatus.Approved, RequesterId = "User1"
            };
            db.ApprovalSteps.Add(step);
            db.ApprovalRequests.Add(request);
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            var result = await service.WithdrawRequest("R1", "User1");

            Assert.False(result);
        }

        [Fact]
        public async Task WithdrawRequest_NotFound_ReturnsFalse()
        {
            var db = GetDbContext();
            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            var result = await service.WithdrawRequest("nonexistent", "User1");

            Assert.False(result);
        }

        // ── ReassignApprover ──────────────────────────────────────────────────────

        [Fact]
        public async Task ReassignApprover_PendingRequest_UpdatesActiveApprover()
        {
            var db = GetDbContext();
            var step = new ApprovalStep { Id = "S1", Sequence = 1 };
            var request = new ApprovalRequest
            {
                Id = "R1", DocumentId = "D1", DocumentType = "WO",
                CurrentStepId = "S1", Status = ApprovalStatus.Pending,
                ActiveApproverId = "OldUser"
            };
            db.ApprovalSteps.Add(step);
            db.ApprovalRequests.Add(request);
            db.Users.Add(new ApplicationUser { Id = "NewUser", UserName = "newuser" }); // 重新指派需驗證新簽核人存在
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            var result = await service.ReassignApprover("R1", "NewUser", "Admin1", "組織異動");

            Assert.True(result);
            var updated = await db.ApprovalRequests.FindAsync("R1");
            Assert.Equal("NewUser", updated!.ActiveApproverId);
        }

        [Fact]
        public async Task ReassignApprover_PendingRequest_CreatesAuditLog()
        {
            var db = GetDbContext();
            var step = new ApprovalStep { Id = "S1", Sequence = 1 };
            var request = new ApprovalRequest
            {
                Id = "R1", DocumentId = "D1", DocumentType = "WO",
                CurrentStepId = "S1", Status = ApprovalStatus.Pending
            };
            db.ApprovalSteps.Add(step);
            db.ApprovalRequests.Add(request);
            db.Users.Add(new ApplicationUser { Id = "NewUser", UserName = "newuser" }); // 重新指派需驗證新簽核人存在
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());
            await service.ReassignApprover("R1", "NewUser", "Admin1", "人員異動");

            var log = db.ApprovalLogs.SingleOrDefault(l => l.RequestId == "R1");
            Assert.NotNull(log);
            Assert.Equal("Admin1", log!.ApproverId);
            Assert.Contains("NewUser", log.Comment);
            Assert.Contains("人員異動", log.Comment);
        }

        [Fact]
        public async Task ReassignApprover_AlreadyApproved_ReturnsFalse()
        {
            var db = GetDbContext();
            var step = new ApprovalStep { Id = "S1", Sequence = 1 };
            var request = new ApprovalRequest
            {
                Id = "R1", DocumentId = "D1", DocumentType = "WO",
                CurrentStepId = "S1", Status = ApprovalStatus.Approved
            };
            db.ApprovalSteps.Add(step);
            db.ApprovalRequests.Add(request);
            await db.SaveChangesAsync();

            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            var result = await service.ReassignApprover("R1", "NewUser", "Admin1", null);

            Assert.False(result);
        }

        [Fact]
        public async Task ReassignApprover_NotFound_ReturnsFalse()
        {
            var db = GetDbContext();
            var service = new ApprovalService(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

            var result = await service.ReassignApprover("nonexistent", "NewUser", "Admin1", null);

            Assert.False(result);
        }
    }
}
