using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFactory.MES.Controllers;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 簽核強化（approval hardening）測試：
    /// 1. ProcessApproval 僅 Pending 可簽（撤回/駁回/核准後不得再操作）
    /// 2. SubmitForApproval 寫送審 log、CreatedAt、通知首位簽核人
    /// 3. 步驟推進/終局的通知；通知故障不中斷流程
    /// 4. GetMyPendingRequests / CountMyPending 判定語意
    /// 5. 模板步驟增刪改在有進行中簽核時被擋下
    /// 6. ReassignApprover 驗證新簽核人存在
    /// </summary>
    public class ApprovalHardeningTests
    {
        private static ApplicationDbContext GetDbContext(string? currentPlantId = null)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            return new ApplicationDbContext(options) { CurrentPlantId = currentPlantId };
        }

        private static ApprovalService GetService(ApplicationDbContext db, IApprovalNotifier? notifier = null)
            => new(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>(), notifier);

        /// <summary>單步驟模板 + Pending 申請單（Role 型，TargetRoleId = Role1）。</summary>
        private static async Task<ApprovalRequest> SeedPendingRequestAsync(ApplicationDbContext db, string requestId = "R1")
        {
            if (await db.ApprovalTemplates.FindAsync("T1") == null)
            {
                db.ApprovalTemplates.Add(new ApprovalTemplate { Id = "T1", Name = "單步驟" });
                db.ApprovalSteps.Add(new ApprovalStep
                {
                    Id = "S1", TemplateId = "T1", Sequence = 1,
                    ApproverType = ApproverType.Role, TargetRoleId = "Role1"
                });
            }
            var request = new ApprovalRequest
            {
                Id = requestId, DocumentId = "D-" + requestId, DocumentType = "WO",
                CurrentStepId = "S1", Status = ApprovalStatus.Pending
            };
            db.ApprovalRequests.Add(request);
            await db.SaveChangesAsync();
            return request;
        }

        // ── 1. 僅 Pending 可簽 ────────────────────────────────────────────────────

        [Theory]
        [InlineData(ApprovalStatus.Withdrawn)]
        [InlineData(ApprovalStatus.Rejected)]
        [InlineData(ApprovalStatus.Approved)]
        public async Task ProcessApproval_NonPendingRequest_ReturnsFalse_AndDoesNotFlipStatus(ApprovalStatus initial)
        {
            var db = GetDbContext();
            var request = await SeedPendingRequestAsync(db);
            request.Status = initial;
            await db.SaveChangesAsync();

            var service = GetService(db);
            // 簽核人身分正確（Role1），但狀態非 Pending → 必須拒絕
            var result = await service.ProcessApproval("R1", "User1", "Role1", ApprovalStatus.Approved, null);

            Assert.False(result);
            var reloaded = await db.ApprovalRequests.FindAsync("R1");
            Assert.Equal(initial, reloaded!.Status); // 不得被翻成 Approved
        }

        // ── 2. 送審 log + CreatedAt + 通知 ────────────────────────────────────────

        [Fact]
        public async Task SubmitForApproval_WritesSubmitLog_SetsCreatedAt_AndNotifiesFirstStep()
        {
            var db = GetDbContext();
            db.ApprovalTemplates.Add(new ApprovalTemplate { Id = "T1", Name = "單步驟" });
            db.ApprovalSteps.Add(new ApprovalStep
            {
                Id = "S1", TemplateId = "T1", Sequence = 1,
                ApproverType = ApproverType.Role, TargetRoleId = "Role1"
            });
            db.Users.Add(new ApplicationUser { Id = "Requester1", UserName = "requester" });
            await db.SaveChangesAsync();

            var notifier = new Mock<IApprovalNotifier>();
            var service = GetService(db, notifier.Object);
            var before = DateTime.UtcNow.AddSeconds(-5);

            var request = await service.SubmitForApproval("D1", "WO", "T1", "Requester1");

            Assert.True(request.CreatedAt >= before);
            var log = db.ApprovalLogs.SingleOrDefault(l => l.RequestId == request.Id);
            Assert.NotNull(log);
            Assert.Equal("Requester1", log!.ApproverId);
            Assert.Equal(ApprovalStatus.Pending, log.Action);
            notifier.Verify(n => n.NotifyPendingAsync(
                It.Is<ApprovalRequest>(r => r.Id == request.Id),
                It.Is<ApprovalStep>(s => s.Id == "S1")), Times.Once);
        }

        // ── 3. 推進/終局通知；通知故障不中斷 ─────────────────────────────────────

        [Fact]
        public async Task ProcessApproval_AdvanceToNextStep_NotifiesNextApprover()
        {
            var db = GetDbContext();
            db.ApprovalTemplates.Add(new ApprovalTemplate { Id = "T1", Name = "雙步驟" });
            db.ApprovalSteps.AddRange(
                new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1, ApproverType = ApproverType.Role, TargetRoleId = "Role1" },
                new ApprovalStep { Id = "S2", TemplateId = "T1", Sequence = 2, ApproverType = ApproverType.User, TargetUserId = "User2" });
            db.ApprovalRequests.Add(new ApprovalRequest
            {
                Id = "R1", DocumentId = "D1", DocumentType = "WO",
                CurrentStepId = "S1", Status = ApprovalStatus.Pending
            });
            await db.SaveChangesAsync();

            var notifier = new Mock<IApprovalNotifier>();
            var service = GetService(db, notifier.Object);

            var result = await service.ProcessApproval("R1", "User1", "Role1", ApprovalStatus.Approved, null);

            Assert.True(result);
            notifier.Verify(n => n.NotifyPendingAsync(
                It.Is<ApprovalRequest>(r => r.Id == "R1"),
                It.Is<ApprovalStep>(s => s.Id == "S2")), Times.Once);
            notifier.Verify(n => n.NotifyFinalizedAsync(It.IsAny<ApprovalRequest>(), It.IsAny<ApprovalStatus>()), Times.Never);
        }

        [Theory]
        [InlineData(ApprovalStatus.Approved)]
        [InlineData(ApprovalStatus.Rejected)]
        [InlineData(ApprovalStatus.ReturnedForRevision)]
        public async Task ProcessApproval_FinalStatus_NotifiesRequester(ApprovalStatus action)
        {
            var db = GetDbContext();
            await SeedPendingRequestAsync(db);

            var notifier = new Mock<IApprovalNotifier>();
            var service = GetService(db, notifier.Object);

            var result = await service.ProcessApproval("R1", "User1", "Role1", action, null);

            Assert.True(result);
            notifier.Verify(n => n.NotifyFinalizedAsync(
                It.Is<ApprovalRequest>(r => r.Id == "R1"), action), Times.Once);
        }

        [Fact]
        public async Task ProcessApproval_NotifierThrows_FlowStillSucceeds()
        {
            var db = GetDbContext();
            await SeedPendingRequestAsync(db);

            var notifier = new Mock<IApprovalNotifier>();
            notifier.Setup(n => n.NotifyFinalizedAsync(It.IsAny<ApprovalRequest>(), It.IsAny<ApprovalStatus>()))
                    .ThrowsAsync(new InvalidOperationException("通知管道故障"));
            var service = GetService(db, notifier.Object);

            var result = await service.ProcessApproval("R1", "User1", "Role1", ApprovalStatus.Approved, null);

            Assert.True(result); // 通知失敗不影響簽核落地
            var reloaded = await db.ApprovalRequests.FindAsync("R1");
            Assert.Equal(ApprovalStatus.Approved, reloaded!.Status);
        }

        // ── 4. 待我簽核查詢語意 ──────────────────────────────────────────────────

        [Fact]
        public async Task GetMyPendingRequests_ActiveApproverOverride_OnlyThatUserSeesIt()
        {
            var db = GetDbContext();
            var request = await SeedPendingRequestAsync(db);
            request.ActiveApproverId = "UserX"; // 重新指派後：Role1 成員不再輪到
            await db.SaveChangesAsync();

            var service = GetService(db);

            Assert.Empty(await service.GetMyPendingRequestsAsync("User1", "Role1"));
            var mine = await service.GetMyPendingRequestsAsync("UserX", "RoleZ");
            Assert.Single(mine);
            Assert.Equal("R1", mine[0].Id);
        }

        [Fact]
        public async Task GetMyPendingRequests_UnresolvedSupervisor_VisibleToNobody_ButCountedByUnassignedFilter()
        {
            var db = GetDbContext();
            db.ApprovalTemplates.Add(new ApprovalTemplate { Id = "T1", Name = "主管簽" });
            db.ApprovalSteps.Add(new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1, ApproverType = ApproverType.Supervisor });
            db.ApprovalRequests.Add(new ApprovalRequest
            {
                Id = "R1", DocumentId = "D1", DocumentType = "WO",
                CurrentStepId = "S1", Status = ApprovalStatus.Pending, ActiveApproverId = null
            });
            await db.SaveChangesAsync();

            var service = GetService(db);
            Assert.Empty(await service.GetMyPendingRequestsAsync("AnyUser", "AnyRole"));
            Assert.Equal(0, await service.CountMyPendingAsync("AnyUser", "AnyRole"));
        }

        [Fact]
        public async Task CountMyPending_MatchesListSemantics()
        {
            var db = GetDbContext();
            await SeedPendingRequestAsync(db, "R1");
            await SeedPendingRequestAsync(db, "R2");

            var service = GetService(db);
            Assert.Equal(2, await service.CountMyPendingAsync("User1", "Role1"));
            Assert.Equal(0, await service.CountMyPendingAsync("User1", "OtherRole"));
        }

        [Fact]
        public async Task GetMyPendingRequests_OrdersByCreatedAtAscending()
        {
            var db = GetDbContext();
            var r1 = await SeedPendingRequestAsync(db, "R1");
            var r2 = await SeedPendingRequestAsync(db, "R2");
            r1.CreatedAt = DateTime.UtcNow;              // 新
            r2.CreatedAt = DateTime.UtcNow.AddDays(-3);  // 舊（等最久）
            await db.SaveChangesAsync();

            var service = GetService(db);
            var list = await service.GetMyPendingRequestsAsync("User1", "Role1");

            Assert.Equal(new[] { "R2", "R1" }, list.Select(r => r.Id).ToArray());
        }

        // ── 5. 模板步驟增刪改守門 ────────────────────────────────────────────────

        private static ApprovalTemplateController GetTemplateController(ApplicationDbContext db)
        {
            var store = new Mock<IUserStore<ApplicationUser>>();
            var userManager = new Mock<UserManager<ApplicationUser>>(store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
            userManager.Setup(u => u.Users).Returns(new List<ApplicationUser>().AsQueryable());
            var roleStore = new Mock<IRoleStore<ApplicationRole>>();
            var roleManager = new Mock<RoleManager<ApplicationRole>>(roleStore.Object, null!, null!, null!, null!);
            roleManager.Setup(r => r.Roles).Returns(new List<ApplicationRole>().AsQueryable());
            var export = new Mock<IExportService>();
            var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            httpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest"; // 走 AJAX 分支（回 Json）
            return new ApprovalTemplateController(db, roleManager.Object, userManager.Object, export.Object,
                new Mock<IDateTimeService>().Object)
            {
                ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = httpContext }
            };
        }

        private static object? GetJsonProp(object jsonValue, string name)
            => jsonValue.GetType().GetProperty(name)?.GetValue(jsonValue);

        [Fact]
        public async Task AddStep_TemplateHasPendingRequest_Blocked()
        {
            var db = GetDbContext();
            await SeedPendingRequestAsync(db);
            var controller = GetTemplateController(db);

            var result = await controller.AddStep(new ApprovalStep { TemplateId = "T1", ApproverType = ApproverType.User, TargetUserId = "U9" });

            var json = Assert.IsType<Microsoft.AspNetCore.Mvc.JsonResult>(result);
            Assert.False((bool)GetJsonProp(json.Value!, "success")!);
            Assert.Equal(1, await db.ApprovalSteps.CountAsync(s => s.TemplateId == "T1")); // 未新增
        }

        [Fact]
        public async Task DeleteStep_TemplateHasPendingRequest_Blocked()
        {
            var db = GetDbContext();
            await SeedPendingRequestAsync(db);
            var controller = GetTemplateController(db);

            var result = await controller.DeleteStepConfirmed("S1", "T1");

            var json = Assert.IsType<Microsoft.AspNetCore.Mvc.JsonResult>(result);
            Assert.False((bool)GetJsonProp(json.Value!, "success")!);
            Assert.NotNull(await db.ApprovalSteps.FindAsync("S1")); // 步驟仍在，申請單不會孤兒化
        }

        [Fact]
        public async Task DeleteStep_ReferencedByHistoricalRequest_BlockedWithClearMessage()
        {
            var db = GetDbContext();
            var request = await SeedPendingRequestAsync(db);
            request.Status = ApprovalStatus.Approved; // 歷史單（非 Pending），仍以 CurrentStepId 引用 S1
            await db.SaveChangesAsync();
            var controller = GetTemplateController(db);

            var result = await controller.DeleteStepConfirmed("S1", "T1");

            var json = Assert.IsType<Microsoft.AspNetCore.Mvc.JsonResult>(result);
            Assert.False((bool)GetJsonProp(json.Value!, "success")!);
            Assert.Equal(SmartFactory.MES.Services.CoreText.Get("Msg_StepInUse"), (string)GetJsonProp(json.Value!, "message")!);
            Assert.NotNull(await db.ApprovalSteps.FindAsync("S1"));
        }

        [Fact]
        public async Task EditStep_TemplateHasPendingRequest_Blocked()
        {
            var db = GetDbContext();
            await SeedPendingRequestAsync(db);
            var controller = GetTemplateController(db);

            var result = await controller.EditStep("S1", "T1", (int)ApproverType.User, null, "U9");

            var json = Assert.IsType<Microsoft.AspNetCore.Mvc.JsonResult>(result);
            Assert.False((bool)GetJsonProp(json.Value!, "success")!);
            var step = await db.ApprovalSteps.FindAsync("S1");
            Assert.Equal(ApproverType.Role, step!.ApproverType); // 未被改動
        }

        [Fact]
        public async Task MoveStep_TemplateHasPendingRequest_Blocked()
        {
            var db = GetDbContext();
            await SeedPendingRequestAsync(db);
            db.ApprovalSteps.Add(new ApprovalStep { Id = "S2", TemplateId = "T1", Sequence = 2, ApproverType = ApproverType.User, TargetUserId = "U2" });
            await db.SaveChangesAsync();
            var controller = GetTemplateController(db);

            var result = await controller.MoveStep("S2", "T1", "up");

            var json = Assert.IsType<Microsoft.AspNetCore.Mvc.JsonResult>(result);
            Assert.False((bool)GetJsonProp(json.Value!, "success")!);
            Assert.Equal(2, (await db.ApprovalSteps.FindAsync("S2"))!.Sequence); // 順序未變
        }

        [Fact]
        public async Task AddStep_NoPendingRequests_StillAllowed()
        {
            var db = GetDbContext();
            var request = await SeedPendingRequestAsync(db);
            request.Status = ApprovalStatus.Approved; // 全部完結 → 可調整
            await db.SaveChangesAsync();
            var controller = GetTemplateController(db);

            var result = await controller.AddStep(new ApprovalStep { TemplateId = "T1", ApproverType = ApproverType.User, TargetUserId = "U9" });

            var json = Assert.IsType<Microsoft.AspNetCore.Mvc.JsonResult>(result);
            Assert.True((bool)GetJsonProp(json.Value!, "success")!);
            Assert.Equal(2, await db.ApprovalSteps.CountAsync(s => s.TemplateId == "T1"));
        }

        // ── 6. ReassignApprover 驗證 ──────────────────────────────────────────────

        [Fact]
        public async Task ReassignApprover_UnknownUser_ReturnsFalse()
        {
            var db = GetDbContext();
            await SeedPendingRequestAsync(db);

            var service = GetService(db);
            var result = await service.ReassignApprover("R1", "GhostUser", "Admin1", null);

            Assert.False(result);
            var reloaded = await db.ApprovalRequests.FindAsync("R1");
            Assert.Null(reloaded!.ActiveApproverId);
        }

        [Fact]
        public async Task ReassignApprover_ValidUser_NotifiesNewApprover()
        {
            var db = GetDbContext();
            await SeedPendingRequestAsync(db);
            db.Users.Add(new ApplicationUser { Id = "UserX", UserName = "userx" });
            await db.SaveChangesAsync();

            var notifier = new Mock<IApprovalNotifier>();
            var service = GetService(db, notifier.Object);

            var result = await service.ReassignApprover("R1", "UserX", "Admin1", "休假代理");

            Assert.True(result);
            notifier.Verify(n => n.NotifyPendingAsync(
                It.Is<ApprovalRequest>(r => r.Id == "R1" && r.ActiveApproverId == "UserX"),
                It.Is<ApprovalStep>(s => s.Id == "S1")), Times.Once);
        }
    }
}
