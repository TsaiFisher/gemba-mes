using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Controllers;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using System.Security.Claims;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 驗證簽核相關實體（ApprovalRequest、DocumentApprovalConfig）在多廠別環境下：
    /// 1. 全域查詢過濾器正確隔離廠別資料
    /// 2. PlantId 由 DbContext 自動填入
    /// 3. PendingIndex 切換廠別後只顯示當前廠別的待審單
    /// </summary>
    public class ApprovalMultiPlantTests
    {
        // 同一個 dbName → 同一個 InMemory 資料庫實例，方便 setup ctx 和 query ctx 共用資料
        private static ApplicationDbContext CreateContext(string dbName, string? currentPlantId = null)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;
            return new ApplicationDbContext(options) { CurrentPlantId = currentPlantId };
        }

        // ── ApprovalRequest 廠別隔離 ──────────────────────────────────────────────

        [Fact]
        public async Task ApprovalRequest_QueryFilter_OnlyReturnsCurrentPlantRequests()
        {
            var db = Guid.NewGuid().ToString();

            // Arrange：以 null PlantId 寫入資料（繞過全域過濾器）
            using var setupCtx = CreateContext(db, currentPlantId: null);
            var stepP1 = new ApprovalStep { Id = "S-P1", Sequence = 1 };
            var stepP2 = new ApprovalStep { Id = "S-P2", Sequence = 1 };
            setupCtx.ApprovalSteps.AddRange(stepP1, stepP2);
            setupCtx.ApprovalRequests.AddRange(
                new ApprovalRequest { Id = "R1", DocumentId = "D1", DocumentType = "WO", CurrentStepId = "S-P1", PlantId = "P1" },
                new ApprovalRequest { Id = "R2", DocumentId = "D2", DocumentType = "WO", CurrentStepId = "S-P1", PlantId = "P1" },
                new ApprovalRequest { Id = "R3", DocumentId = "D3", DocumentType = "WO", CurrentStepId = "S-P2", PlantId = "P2" }
            );
            await setupCtx.SaveChangesAsync();

            // Act：切換至 P1
            using var ctx = CreateContext(db, currentPlantId: "P1");
            var requests = await ctx.ApprovalRequests.ToListAsync();

            // Assert：只看到 P1 的兩筆
            Assert.Equal(2, requests.Count);
            Assert.All(requests, r => Assert.Equal("P1", r.PlantId));
        }

        [Fact]
        public async Task ApprovalRequest_QueryFilter_PlantSwitch_ShowsDifferentData()
        {
            var db = Guid.NewGuid().ToString();

            using var setupCtx = CreateContext(db, currentPlantId: null);
            var stepP1 = new ApprovalStep { Id = "S-P1", Sequence = 1 };
            var stepP2 = new ApprovalStep { Id = "S-P2", Sequence = 1 };
            setupCtx.ApprovalSteps.AddRange(stepP1, stepP2);
            setupCtx.ApprovalRequests.AddRange(
                new ApprovalRequest { Id = "R1", DocumentId = "D1", DocumentType = "WO", CurrentStepId = "S-P1", PlantId = "P1" },
                new ApprovalRequest { Id = "R2", DocumentId = "D2", DocumentType = "WO", CurrentStepId = "S-P2", PlantId = "P2" }
            );
            await setupCtx.SaveChangesAsync();

            // Act：分別以 P1 和 P2 查詢（同資料庫，不同過濾器）
            using var ctxP1 = CreateContext(db, currentPlantId: "P1");
            using var ctxP2 = CreateContext(db, currentPlantId: "P2");

            var p1Requests = await ctxP1.ApprovalRequests.ToListAsync();
            var p2Requests = await ctxP2.ApprovalRequests.ToListAsync();

            Assert.Single(p1Requests);
            Assert.Equal("R1", p1Requests[0].Id);

            Assert.Single(p2Requests);
            Assert.Equal("R2", p2Requests[0].Id);
        }

        [Fact]
        public async Task ApprovalRequest_AutoFillsPlantId_OnSave()
        {
            var db = Guid.NewGuid().ToString();
            using var ctx = CreateContext(db, currentPlantId: "P1");
            var step = new ApprovalStep { Id = "S1", Sequence = 1 };
            ctx.ApprovalSteps.Add(step);
            await ctx.SaveChangesAsync();

            var request = new ApprovalRequest
            {
                Id = "R1", DocumentId = "D1", DocumentType = "WO", CurrentStepId = "S1"
                // PlantId 未設定
            };
            ctx.ApprovalRequests.Add(request);
            await ctx.SaveChangesAsync();

            Assert.Equal("P1", request.PlantId);
        }

        // ── DocumentApprovalConfig 廠別隔離 ──────────────────────────────────────

        [Fact]
        public async Task DocumentApprovalConfig_QueryFilter_OnlyReturnsCurrentPlantConfigs()
        {
            var db = Guid.NewGuid().ToString();

            using var setupCtx = CreateContext(db, currentPlantId: null);
            setupCtx.DocumentApprovalConfigs.AddRange(
                new DocumentApprovalConfig { Id = "C1", DocumentType = "PO", DocumentTypeName = "請購單", PlantId = "P1" },
                new DocumentApprovalConfig { Id = "C2", DocumentType = "WO", DocumentTypeName = "工單",   PlantId = "P2" }
            );
            await setupCtx.SaveChangesAsync();

            using var ctx = CreateContext(db, currentPlantId: "P1");
            var configs = await ctx.DocumentApprovalConfigs.ToListAsync();

            Assert.Single(configs);
            Assert.Equal("C1", configs[0].Id);
        }

        [Fact]
        public async Task DocumentApprovalConfig_AutoFillsPlantId_OnSave()
        {
            var db = Guid.NewGuid().ToString();
            using var ctx = CreateContext(db, currentPlantId: "P1");

            var config = new DocumentApprovalConfig
            {
                DocumentType = "PurchaseOrder",
                DocumentTypeName = "請購單"
                // PlantId 未設定
            };
            ctx.DocumentApprovalConfigs.Add(config);
            await ctx.SaveChangesAsync();

            Assert.Equal("P1", config.PlantId);
        }

        [Fact]
        public async Task DocumentApprovalConfig_SamePlant_DifferentDocumentType_BothVisible()
        {
            var db = Guid.NewGuid().ToString();

            using var setupCtx = CreateContext(db, currentPlantId: null);
            setupCtx.DocumentApprovalConfigs.AddRange(
                new DocumentApprovalConfig { Id = "C1", DocumentType = "PO", DocumentTypeName = "請購單", PlantId = "P1" },
                new DocumentApprovalConfig { Id = "C2", DocumentType = "WO", DocumentTypeName = "工單",   PlantId = "P1" }
            );
            await setupCtx.SaveChangesAsync();

            using var ctx = CreateContext(db, currentPlantId: "P1");
            var configs = await ctx.DocumentApprovalConfigs.ToListAsync();

            Assert.Equal(2, configs.Count);
        }

        // ── PendingIndex 廠別切換 ─────────────────────────────────────────────────

        [Fact]
        public async Task PendingIndex_AfterPlantSwitch_OnlyShowsCurrentPlantRequests()
        {
            var db = Guid.NewGuid().ToString();

            using var setupCtx = CreateContext(db, currentPlantId: null);
            var stepP1 = new ApprovalStep { Id = "S-P1", Sequence = 1, ApproverType = ApproverType.Role, TargetRoleId = "Role1" };
            var stepP2 = new ApprovalStep { Id = "S-P2", Sequence = 1, ApproverType = ApproverType.Role, TargetRoleId = "Role1" };
            setupCtx.ApprovalSteps.AddRange(stepP1, stepP2);
            setupCtx.ApprovalRequests.AddRange(
                new ApprovalRequest { Id = "R-P1", DocumentId = "D1", DocumentType = "WO", CurrentStepId = "S-P1", Status = ApprovalStatus.Pending, PlantId = "P1" },
                new ApprovalRequest { Id = "R-P2", DocumentId = "D2", DocumentType = "WO", CurrentStepId = "S-P2", Status = ApprovalStatus.Pending, PlantId = "P2" }
            );
            await setupCtx.SaveChangesAsync();

            // Act：以廠別 P1 的 context 建立 Controller
            var ctxP1 = CreateContext(db, currentPlantId: "P1");
            var user = new ApplicationUser { Id = "User1", UserName = "testuser", ActiveRoleId = "Role1" };
            var mockUserManager = new Mock<UserManager<ApplicationUser>>(
                new Mock<Microsoft.AspNetCore.Identity.IUserStore<ApplicationUser>>().Object,
                null, null, null, null, null, null, null, null);
            mockUserManager
                .Setup(m => m.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
                .ReturnsAsync(user);

            var controller = new ApprovalController(ctxP1, mockUserManager.Object,
                BuildRealService(ctxP1), BuildPermissionMock());
            controller.ControllerContext = BuildControllerContext("testuser", "User1");

            var result = await controller.PendingIndex();

            // Assert：只看到 P1 的申請單
            var viewResult = Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<ApprovalRequest>>(viewResult.Model);
            Assert.Single(model);
            Assert.Equal("R-P1", model.First().Id);
        }

        [Fact]
        public async Task PendingIndex_NoPlantSet_ShowsNullPlantRequestsOnly()
        {
            // 當 CurrentPlantId = null 時，只會看到 PlantId = null 的記錄
            var db = Guid.NewGuid().ToString();

            using var setupCtx = CreateContext(db, currentPlantId: null);
            var stepNull = new ApprovalStep { Id = "S-NULL", Sequence = 1, ApproverType = ApproverType.Role, TargetRoleId = "Role1" };
            var stepP1   = new ApprovalStep { Id = "S-P1",   Sequence = 1, ApproverType = ApproverType.Role, TargetRoleId = "Role1" };
            setupCtx.ApprovalSteps.AddRange(stepNull, stepP1);
            setupCtx.ApprovalRequests.AddRange(
                new ApprovalRequest { Id = "R-NULL", DocumentId = "D1", DocumentType = "WO", CurrentStepId = "S-NULL", Status = ApprovalStatus.Pending, PlantId = null },
                new ApprovalRequest { Id = "R-P1",   DocumentId = "D2", DocumentType = "WO", CurrentStepId = "S-P1",   Status = ApprovalStatus.Pending, PlantId = "P1" }
            );
            await setupCtx.SaveChangesAsync();

            var ctxNoPlant = CreateContext(db, currentPlantId: null);
            var user = new ApplicationUser { Id = "User1", UserName = "testuser", ActiveRoleId = "Role1" };
            var mockUserManager = new Mock<UserManager<ApplicationUser>>(
                new Mock<Microsoft.AspNetCore.Identity.IUserStore<ApplicationUser>>().Object,
                null, null, null, null, null, null, null, null);
            mockUserManager
                .Setup(m => m.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
                .ReturnsAsync(user);

            var controller = new ApprovalController(ctxNoPlant, mockUserManager.Object,
                BuildRealService(ctxNoPlant), BuildPermissionMock());
            controller.ControllerContext = BuildControllerContext("testuser", "User1");

            var result = await controller.PendingIndex();

            var viewResult = Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<ApprovalRequest>>(viewResult.Model);
            Assert.Single(model);
            Assert.Equal("R-NULL", model.First().Id);
        }

        // ── helper ────────────────────────────────────────────────────────────────

        // PendingIndex 已委派 ApprovalService（DB 端過濾），廠別隔離語意由掛在對應 context 的真實服務驗證
        private static ApprovalService BuildRealService(ApplicationDbContext ctx)
            => new(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<ApprovalService>.Instance,
                   Array.Empty<IApprovalSyncContributor>());

        private static IPermissionService BuildPermissionMock()
        {
            var mock = new Mock<IPermissionService>();
            mock.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
            mock.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
            return mock.Object;
        }

        private static Microsoft.AspNetCore.Mvc.ControllerContext BuildControllerContext(string userName, string userId)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.Name, userName),
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Role, "Admin")
            };
            var identity = new ClaimsIdentity(claims, "TestAuth");
            return new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(identity)
                }
            };
        }
    }
}
