using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 簽核代理人＋同一簽核人自動跳簽（v1.4）測試：
    /// 1. SkipSameApprover 關閉時行為不變；開啟時 User/Role 步驟依實際簽核人自動跳簽
    /// 2. 申請人視同已簽核（送審時輪到本人的步驟直接跳過；全跳＝送審即核准）
    /// 3. 代理人解析（User/Supervisor 型）、迴避原則、期間判定、Role 步驟不適用
    /// 4. 代理簽核的 OnBehalfOf 稽核、ApplyDelegationToPending 回掃改派
    /// 5. SyncSourceDocument CodeEntry 以 Category/Code 自然鍵回寫（缺陷修正回歸）
    /// </summary>
    public class ApprovalDelegationSkipTests
    {
        private static ApplicationDbContext NewDb()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            return new ApplicationDbContext(options);
        }

        private static ApprovalService NewService(ApplicationDbContext db)
            => new(db, NullLogger<ApprovalService>.Instance, Array.Empty<IApprovalSyncContributor>());

        /// <summary>建立模板：steps 依序為 (ApproverType, target)。User 型 target=UserId，Role 型 target=RoleId。</summary>
        private static ApprovalTemplate SeedTemplate(ApplicationDbContext db, bool skipSameApprover,
            params (ApproverType Type, string Target)[] steps)
        {
            var template = new ApprovalTemplate { Id = "T1", Name = "測試模板", SkipSameApprover = skipSameApprover };
            db.ApprovalTemplates.Add(template);
            for (var i = 0; i < steps.Length; i++)
            {
                db.ApprovalSteps.Add(new ApprovalStep
                {
                    Id = $"S{i + 1}",
                    TemplateId = "T1",
                    Sequence = i + 1,
                    ApproverType = steps[i].Type,
                    TargetRoleId = steps[i].Type == ApproverType.Role ? steps[i].Target : null,
                    TargetUserId = steps[i].Type == ApproverType.User ? steps[i].Target : null
                });
            }
            return template;
        }

        private static void SeedDelegation(ApplicationDbContext db, string delegator, string delegate_,
            int startOffsetDays = -1, int endOffsetDays = 1, bool isActive = true)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            db.ApprovalDelegations.Add(new ApprovalDelegation
            {
                DelegatorId = delegator,
                DelegateId = delegate_,
                StartDate = today.AddDays(startOffsetDays),
                EndDate = today.AddDays(endOffsetDays),
                IsActive = isActive
            });
        }

        // ── 同一簽核人自動跳簽 ────────────────────────────────────────────────────

        [Fact]
        public async Task SkipOff_DuplicateUserStep_StillRequiresSecondSignature()
        {
            using var db = NewDb();
            SeedTemplate(db, skipSameApprover: false,
                (ApproverType.User, "U-B"), (ApproverType.User, "U-B"));
            await db.SaveChangesAsync();
            var service = NewService(db);

            var request = await service.SubmitForApproval("DOC-1", "WO", "T1", "U-A");
            Assert.True(await service.ProcessApproval(request.Id, "U-B", "R-x", ApprovalStatus.Approved, null));

            var reloaded = await db.ApprovalRequests.FindAsync(request.Id);
            Assert.Equal(ApprovalStatus.Pending, reloaded!.Status); // 未跳簽，停在第二步
            Assert.Equal("S2", reloaded.CurrentStepId);
            Assert.DoesNotContain(await db.ApprovalLogs.ToListAsync(), l => l.Action == ApprovalStatus.Skipped);
        }

        [Fact]
        public async Task SkipOn_DuplicateUserStep_AutoSkipsAndFinalizes()
        {
            using var db = NewDb();
            SeedTemplate(db, skipSameApprover: true,
                (ApproverType.User, "U-B"), (ApproverType.User, "U-B"));
            await db.SaveChangesAsync();
            var service = NewService(db);

            var request = await service.SubmitForApproval("DOC-1", "WO", "T1", "U-A");
            Assert.True(await service.ProcessApproval(request.Id, "U-B", "R-x", ApprovalStatus.Approved, null));

            var reloaded = await db.ApprovalRequests.FindAsync(request.Id);
            Assert.Equal(ApprovalStatus.Approved, reloaded!.Status); // 第二步跳過 → 終局核准

            var skipLog = Assert.Single(await db.ApprovalLogs
                .Where(l => l.Action == ApprovalStatus.Skipped).ToListAsync());
            Assert.Equal("U-B", skipLog.ApproverId);
        }

        [Fact]
        public async Task SkipOn_RequesterIsFirstStepApprover_SkippedAtSubmit()
        {
            using var db = NewDb();
            SeedTemplate(db, skipSameApprover: true,
                (ApproverType.User, "U-A"), (ApproverType.User, "U-B"));
            await db.SaveChangesAsync();
            var service = NewService(db);

            var request = await service.SubmitForApproval("DOC-1", "WO", "T1", "U-A");

            Assert.Equal(ApprovalStatus.Pending, request.Status);
            Assert.Equal("S2", request.CurrentStepId); // 第一步（申請人本人）已跳過
            var skipLog = Assert.Single(await db.ApprovalLogs
                .Where(l => l.Action == ApprovalStatus.Skipped).ToListAsync());
            Assert.Equal("U-A", skipLog.ApproverId);
            Assert.Equal(CoreText.Token("ApprovalNote_SkipRequester"), skipLog.Comment);
        }

        [Fact]
        public async Task SkipOn_AllStepsSkipped_SubmitAutoApproves_AndSyncsCodeEntryByNaturalKey()
        {
            using var db = NewDb();
            SeedTemplate(db, skipSameApprover: true, (ApproverType.User, "U-A"));
            db.CodeEntries.Add(new CodeEntry
            {
                Id = "CE-uuid-1", Category = "unit", Code = "PCS", NameZh = "個",
                ApprovalStatus = DocumentApprovalStatus.PendingApproval, IsActive = false
            });
            await db.SaveChangesAsync();
            var service = NewService(db);

            var request = await service.SubmitForApproval("unit/PCS", "CodeEntry", "T1", "U-A");

            Assert.Equal(ApprovalStatus.Approved, request.Status); // 送審即終局核准
            var entry = await db.CodeEntries.IgnoreQueryFilters().SingleAsync(c => c.Id == "CE-uuid-1");
            Assert.Equal(DocumentApprovalStatus.Approved, entry.ApprovalStatus);
            Assert.True(entry.IsActive);
        }

        [Fact]
        public async Task SkipOn_RoleStep_PriorSignerIsMember_Skipped()
        {
            using var db = NewDb();
            SeedTemplate(db, skipSameApprover: true,
                (ApproverType.User, "U-B"), (ApproverType.Role, "RoleX"));
            db.UserRoles.Add(new IdentityUserRole<string> { UserId = "U-B", RoleId = "RoleX" });
            await db.SaveChangesAsync();
            var service = NewService(db);

            var request = await service.SubmitForApproval("DOC-1", "WO", "T1", "U-A");
            Assert.True(await service.ProcessApproval(request.Id, "U-B", "R-x", ApprovalStatus.Approved, null));

            var reloaded = await db.ApprovalRequests.FindAsync(request.Id);
            Assert.Equal(ApprovalStatus.Approved, reloaded!.Status); // 角色步驟成員已簽 → 跳過
            var skipLog = Assert.Single(await db.ApprovalLogs
                .Where(l => l.Action == ApprovalStatus.Skipped).ToListAsync());
            Assert.Equal("U-B", skipLog.ApproverId);
            Assert.Equal("RoleX", skipLog.ApproverRoleId);
        }

        [Fact]
        public async Task SkipOn_RoleStep_PriorSignerNotMember_NotSkipped()
        {
            using var db = NewDb();
            SeedTemplate(db, skipSameApprover: true,
                (ApproverType.User, "U-B"), (ApproverType.Role, "RoleX"));
            await db.SaveChangesAsync(); // U-B 不是 RoleX 成員
            var service = NewService(db);

            var request = await service.SubmitForApproval("DOC-1", "WO", "T1", "U-A");
            Assert.True(await service.ProcessApproval(request.Id, "U-B", "R-x", ApprovalStatus.Approved, null));

            var reloaded = await db.ApprovalRequests.FindAsync(request.Id);
            Assert.Equal(ApprovalStatus.Pending, reloaded!.Status);
            Assert.Equal("S2", reloaded.CurrentStepId);
        }

        // ── SyncSourceDocument CodeEntry 自然鍵回寫（缺陷修正回歸） ────────────────

        [Fact]
        public async Task Sync_CodeEntry_NaturalKeyDocumentId_WriteBackWorks()
        {
            using var db = NewDb();
            SeedTemplate(db, skipSameApprover: false, (ApproverType.Role, "Role1"));
            db.CodeEntries.Add(new CodeEntry
            {
                Id = "CE-uuid-2", Category = "unit", Code = "BOX", NameZh = "箱",
                ApprovalStatus = DocumentApprovalStatus.PendingApproval, IsActive = false
            });
            await db.SaveChangesAsync();
            var service = NewService(db);

            // CodeEntryController 的送審格式為 "Category/Code"（非 UUID）
            var request = await service.SubmitForApproval("unit/BOX", "CodeEntry", "T1", "U-A");
            Assert.True(await service.ProcessApproval(request.Id, "U-B", "Role1", ApprovalStatus.Approved, null));

            var entry = await db.CodeEntries.IgnoreQueryFilters().SingleAsync(c => c.Id == "CE-uuid-2");
            Assert.Equal(DocumentApprovalStatus.Approved, entry.ApprovalStatus); // 修正前：查無 → 永不生效
            Assert.True(entry.IsActive);
        }

        [Fact]
        public async Task Sync_CodeEntry_LegacyUuidDocumentId_StillWorks()
        {
            using var db = NewDb();
            SeedTemplate(db, skipSameApprover: false, (ApproverType.Role, "Role1"));
            db.CodeEntries.Add(new CodeEntry
            {
                Id = "CE-uuid-3", Category = "unit", Code = "KG", NameZh = "公斤",
                ApprovalStatus = DocumentApprovalStatus.PendingApproval, IsActive = false
            });
            await db.SaveChangesAsync();
            var service = NewService(db);

            var request = await service.SubmitForApproval("CE-uuid-3", "CodeEntry", "T1", "U-A");
            Assert.True(await service.ProcessApproval(request.Id, "U-B", "Role1", ApprovalStatus.Approved, null));

            var entry = await db.CodeEntries.IgnoreQueryFilters().SingleAsync(c => c.Id == "CE-uuid-3");
            Assert.Equal(DocumentApprovalStatus.Approved, entry.ApprovalStatus);
        }

        // ── 簽核代理人 ────────────────────────────────────────────────────────────

        [Fact]
        public async Task Delegation_UserStep_AssignsDelegate_AndOnlyDelegateSees()
        {
            using var db = NewDb();
            SeedTemplate(db, skipSameApprover: false, (ApproverType.User, "U-A"));
            SeedDelegation(db, "U-A", "U-B");
            await db.SaveChangesAsync();
            var service = NewService(db);

            var request = await service.SubmitForApproval("DOC-1", "WO", "T1", "U-req");

            Assert.Equal("U-B", request.ActiveApproverId);
            Assert.Equal("U-A", request.ActiveDelegatorId);
            Assert.Single(await service.GetMyPendingRequestsAsync("U-B", null));  // 代理人看得到
            Assert.Empty(await service.GetMyPendingRequestsAsync("U-A", null));   // 委託人已改派
        }

        [Fact]
        public async Task Delegation_SignByDelegate_LogsOnBehalfOf_AndDelegatorCannotSign()
        {
            using var db = NewDb();
            SeedTemplate(db, skipSameApprover: false, (ApproverType.User, "U-A"));
            SeedDelegation(db, "U-A", "U-B");
            await db.SaveChangesAsync();
            var service = NewService(db);

            var request = await service.SubmitForApproval("DOC-1", "WO", "T1", "U-req");

            // 已改派給代理人 → 委託人本人反而不能簽（ActiveApproverId 判定）
            Assert.False(await service.ProcessApproval(request.Id, "U-A", "R-x", ApprovalStatus.Approved, null));
            Assert.True(await service.ProcessApproval(request.Id, "U-B", "R-x", ApprovalStatus.Approved, null));

            var signLog = Assert.Single(await db.ApprovalLogs
                .Where(l => l.Action == ApprovalStatus.Approved).ToListAsync());
            Assert.Equal("U-B", signLog.ApproverId);
            Assert.Equal("U-A", signLog.OnBehalfOfUserId); // 代理簽核稽核
        }

        [Fact]
        public async Task Delegation_DelegateIsRequester_NotApplied()
        {
            using var db = NewDb();
            SeedTemplate(db, skipSameApprover: false, (ApproverType.User, "U-A"));
            SeedDelegation(db, "U-A", "U-req"); // 代理人＝申請人 → 迴避
            await db.SaveChangesAsync();
            var service = NewService(db);

            var request = await service.SubmitForApproval("DOC-1", "WO", "T1", "U-req");

            Assert.Null(request.ActiveApproverId); // 維持原簽核人（步驟目標 U-A）
            Assert.Null(request.ActiveDelegatorId);
            Assert.Single(await service.GetMyPendingRequestsAsync("U-A", null));
        }

        [Theory]
        [InlineData(-10, -5, true)]   // 已過期
        [InlineData(5, 10, true)]     // 未到期間
        [InlineData(-1, 1, false)]    // 停用
        public async Task Delegation_NotEffective_NotApplied(int startOffset, int endOffset, bool isActive)
        {
            using var db = NewDb();
            SeedTemplate(db, skipSameApprover: false, (ApproverType.User, "U-A"));
            SeedDelegation(db, "U-A", "U-B", startOffset, endOffset, isActive);
            await db.SaveChangesAsync();
            var service = NewService(db);

            var request = await service.SubmitForApproval("DOC-1", "WO", "T1", "U-req");

            Assert.Null(request.ActiveApproverId);
            Assert.Null(request.ActiveDelegatorId);
        }

        [Fact]
        public async Task Delegation_RoleStep_NotApplied()
        {
            using var db = NewDb();
            SeedTemplate(db, skipSameApprover: false, (ApproverType.Role, "Role1"));
            SeedDelegation(db, "U-A", "U-B"); // 即使角色成員 U-A 有代理設定
            await db.SaveChangesAsync();
            var service = NewService(db);

            var request = await service.SubmitForApproval("DOC-1", "WO", "T1", "U-req");

            Assert.Null(request.ActiveApproverId); // 角色步驟不套用代理
            Assert.Null(request.ActiveDelegatorId);
        }

        [Fact]
        public async Task Delegation_DelegateAlreadySigned_SkipOn_StepSkippedWithOnBehalfOf()
        {
            using var db = NewDb();
            // 第一步 U-B 簽；第二步目標 U-A，但 U-A 已委派給 U-B → 有效簽核人 U-B 已簽 → 跳過
            SeedTemplate(db, skipSameApprover: true,
                (ApproverType.User, "U-B"), (ApproverType.User, "U-A"));
            SeedDelegation(db, "U-A", "U-B");
            await db.SaveChangesAsync();
            var service = NewService(db);

            var request = await service.SubmitForApproval("DOC-1", "WO", "T1", "U-req");
            Assert.True(await service.ProcessApproval(request.Id, "U-B", "R-x", ApprovalStatus.Approved, null));

            var reloaded = await db.ApprovalRequests.FindAsync(request.Id);
            Assert.Equal(ApprovalStatus.Approved, reloaded!.Status);
            var skipLog = Assert.Single(await db.ApprovalLogs
                .Where(l => l.Action == ApprovalStatus.Skipped).ToListAsync());
            Assert.Equal("U-B", skipLog.ApproverId);
            Assert.Equal("U-A", skipLog.OnBehalfOfUserId);
        }

        // ── ApplyDelegationToPending 回掃改派 ─────────────────────────────────────

        [Fact]
        public async Task ApplyDelegationToPending_MovesUserStepAndActiveApproverRequests()
        {
            using var db = NewDb();
            SeedTemplate(db, skipSameApprover: false, (ApproverType.User, "U-A"));
            await db.SaveChangesAsync();
            var service = NewService(db);

            // 先送兩張單（此時尚無代理設定，卡在 U-A 名下）
            var r1 = await service.SubmitForApproval("DOC-1", "WO", "T1", "U-req");
            var r2 = await service.SubmitForApproval("DOC-2", "WO", "T1", "U-req2");
            Assert.Null(r1.ActiveApproverId);

            // 建立代理設定後回掃
            SeedDelegation(db, "U-A", "U-B");
            await db.SaveChangesAsync();
            var moved = await service.ApplyDelegationToPendingAsync("U-A", "U-A");

            Assert.Equal(2, moved);
            foreach (var id in new[] { r1.Id, r2.Id })
            {
                var req = await db.ApprovalRequests.FindAsync(id);
                Assert.Equal("U-B", req!.ActiveApproverId);
                Assert.Equal("U-A", req.ActiveDelegatorId);
            }
            Assert.Equal(2, await service.CountMyPendingAsync("U-B", null));
            Assert.Equal(0, await service.CountMyPendingAsync("U-A", null));
        }

        [Fact]
        public async Task ApplyDelegationToPending_SkipsRequestsWhereDelegateIsRequester()
        {
            using var db = NewDb();
            SeedTemplate(db, skipSameApprover: false, (ApproverType.User, "U-A"));
            await db.SaveChangesAsync();
            var service = NewService(db);

            var mine = await service.SubmitForApproval("DOC-1", "WO", "T1", "U-B");   // 申請人＝將來的代理人
            var other = await service.SubmitForApproval("DOC-2", "WO", "T1", "U-req");

            SeedDelegation(db, "U-A", "U-B");
            await db.SaveChangesAsync();
            var moved = await service.ApplyDelegationToPendingAsync("U-A", "U-A");

            Assert.Equal(1, moved); // U-B 自己申請的單不改派給 U-B（迴避）
            Assert.Null((await db.ApprovalRequests.FindAsync(mine.Id))!.ActiveApproverId);
            Assert.Equal("U-B", (await db.ApprovalRequests.FindAsync(other.Id))!.ActiveApproverId);
        }

        [Fact]
        public async Task ApplyDelegationToPending_NoEffectiveDelegation_ReturnsZero()
        {
            using var db = NewDb();
            SeedTemplate(db, skipSameApprover: false, (ApproverType.User, "U-A"));
            await db.SaveChangesAsync();
            var service = NewService(db);
            await service.SubmitForApproval("DOC-1", "WO", "T1", "U-req");

            Assert.Equal(0, await service.ApplyDelegationToPendingAsync("U-A", "U-A"));
        }

        // ── 重新指派清除代理脈絡 ──────────────────────────────────────────────────

        [Fact]
        public async Task Reassign_ClearsDelegatorContext()
        {
            using var db = NewDb();
            SeedTemplate(db, skipSameApprover: false, (ApproverType.User, "U-A"));
            SeedDelegation(db, "U-A", "U-B");
            db.Users.Add(new ApplicationUser { Id = "U-C", UserName = "charlie" });
            await db.SaveChangesAsync();
            var service = NewService(db);

            var request = await service.SubmitForApproval("DOC-1", "WO", "T1", "U-req");
            Assert.Equal("U-A", request.ActiveDelegatorId);

            Assert.True(await service.ReassignApprover(request.Id, "U-C", "U-admin", "測試"));

            var reloaded = await db.ApprovalRequests.FindAsync(request.Id);
            Assert.Equal("U-C", reloaded!.ActiveApproverId);
            Assert.Null(reloaded.ActiveDelegatorId); // 管理員明確指派 → 清除代理脈絡

            // 之後 U-C 簽核不得再被標為代理
            Assert.True(await service.ProcessApproval(request.Id, "U-C", "R-x", ApprovalStatus.Approved, null));
            var signLog = Assert.Single(await db.ApprovalLogs
                .Where(l => l.Action == ApprovalStatus.Approved).ToListAsync());
            Assert.Null(signLog.OnBehalfOfUserId);
        }
    }
}
