using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests;

/// <summary>
/// v1.2 Roadmap 強化項目測試：
/// 1) Supervisor 自動解析（依組織樹 ParentId）
/// 2) CancelOnDelete 文件刪除連動撤回
/// 3) IApprovalSyncContributor 註冊機制
/// </summary>
public class ApprovalEnhancementsTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static ApprovalService NewService(ApplicationDbContext db, params IApprovalSyncContributor[] contributors)
        => new(db, NullLogger<ApprovalService>.Instance, contributors);

    // ────────────────────────────────────────────────────────────────────
    // 1. Supervisor 自動解析
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubmitForApproval_SupervisorStep_ResolvesParentOrgUser()
    {
        using var db = NewDb();

        // 組織樹：Root（總部）→ Sales（業務部）
        db.OrganizationUnits.Add(new OrganizationUnit { Id = "OU-Root", Name = "Root", ParentId = null });
        db.OrganizationUnits.Add(new OrganizationUnit { Id = "OU-Sales", Name = "Sales", ParentId = "OU-Root" });

        // Role：HQ-Manager 掛 Root，Sales-Member 掛 Sales
        db.Roles.Add(new ApplicationRole { Id = "R-HQ",     Name = "HQ-Manager",   OrganizationUnitId = "OU-Root" });
        db.Roles.Add(new ApplicationRole { Id = "R-Sales",  Name = "Sales-Member", OrganizationUnitId = "OU-Sales" });

        // User：boss 是主管（ActiveRoleId = HQ-Manager），alice 是申請者
        db.Users.Add(new ApplicationUser { Id = "U-Boss",  UserName = "boss",  ActiveRoleId = "R-HQ" });
        db.Users.Add(new ApplicationUser { Id = "U-Alice", UserName = "alice", ActiveRoleId = "R-Sales" });

        // 模板：第一步 Supervisor
        var template = new ApprovalTemplate { Id = "T1", Name = "主管簽核" };
        template.Steps.Add(new ApprovalStep { Id = "S1", TemplateId = "T1", Sequence = 1, ApproverType = ApproverType.Supervisor });
        db.ApprovalTemplates.Add(template);
        await db.SaveChangesAsync();

        var service = NewService(db);
        var request = await service.SubmitForApproval("DOC-001", "TestDoc", "T1", "U-Alice");

        Assert.Equal("U-Boss", request.ActiveApproverId);   // 自動找到主管
        Assert.Equal("R-Sales", request.RequesterRoleId);
    }

    [Fact]
    public async Task SubmitForApproval_SupervisorStep_NoParentOrg_ReturnsNullApprover()
    {
        using var db = NewDb();

        // 申請者直接掛 Root，沒有 Parent
        db.OrganizationUnits.Add(new OrganizationUnit { Id = "OU-Root", Name = "Root", ParentId = null });
        db.Roles.Add(new ApplicationRole { Id = "R-Root", Name = "RootRole", OrganizationUnitId = "OU-Root" });
        db.Users.Add(new ApplicationUser { Id = "U-Top", UserName = "top", ActiveRoleId = "R-Root" });

        var template = new ApprovalTemplate { Id = "T2", Name = "主管簽核" };
        template.Steps.Add(new ApprovalStep { Id = "S1", TemplateId = "T2", Sequence = 1, ApproverType = ApproverType.Supervisor });
        db.ApprovalTemplates.Add(template);
        await db.SaveChangesAsync();

        var service = NewService(db);
        var request = await service.SubmitForApproval("DOC-002", "TestDoc", "T2", "U-Top");

        Assert.Null(request.ActiveApproverId); // 找不到主管 → 保留 null（待人工指派）
    }

    [Fact]
    public async Task SubmitForApproval_SupervisorStep_MultipleManagers_PicksAlphabeticallyFirst()
    {
        using var db = NewDb();

        db.OrganizationUnits.Add(new OrganizationUnit { Id = "OU-Root", Name = "Root", ParentId = null });
        db.OrganizationUnits.Add(new OrganizationUnit { Id = "OU-Sub", Name = "Sub", ParentId = "OU-Root" });

        db.Roles.Add(new ApplicationRole { Id = "R-HQ",  Name = "HQ", OrganizationUnitId = "OU-Root" });
        db.Roles.Add(new ApplicationRole { Id = "R-Sub", Name = "Sub", OrganizationUnitId = "OU-Sub" });

        // 兩位主管：carol, anna（按 UserName 字典序 anna 較小）
        db.Users.Add(new ApplicationUser { Id = "U-Carol", UserName = "carol", ActiveRoleId = "R-HQ" });
        db.Users.Add(new ApplicationUser { Id = "U-Anna",  UserName = "anna",  ActiveRoleId = "R-HQ" });
        db.Users.Add(new ApplicationUser { Id = "U-Sub",   UserName = "subby", ActiveRoleId = "R-Sub" });

        var template = new ApprovalTemplate { Id = "T3", Name = "Sup" };
        template.Steps.Add(new ApprovalStep { Id = "S1", TemplateId = "T3", Sequence = 1, ApproverType = ApproverType.Supervisor });
        db.ApprovalTemplates.Add(template);
        await db.SaveChangesAsync();

        var service = NewService(db);
        var request = await service.SubmitForApproval("DOC-003", "TestDoc", "T3", "U-Sub");

        Assert.Equal("U-Anna", request.ActiveApproverId);
    }

    [Fact]
    public async Task ProcessApproval_NextStepIsSupervisor_ResolvesAutomatically()
    {
        using var db = NewDb();

        db.OrganizationUnits.Add(new OrganizationUnit { Id = "OU-Root", Name = "Root", ParentId = null });
        db.OrganizationUnits.Add(new OrganizationUnit { Id = "OU-Sub", Name = "Sub", ParentId = "OU-Root" });
        db.Roles.Add(new ApplicationRole { Id = "R-HQ",  Name = "HQ", OrganizationUnitId = "OU-Root" });
        db.Roles.Add(new ApplicationRole { Id = "R-Sub", Name = "Sub", OrganizationUnitId = "OU-Sub" });
        db.Users.Add(new ApplicationUser { Id = "U-Boss", UserName = "boss", ActiveRoleId = "R-HQ" });
        db.Users.Add(new ApplicationUser { Id = "U-Worker", UserName = "worker", ActiveRoleId = "R-Sub" });

        // 模板：Step1 = Role(Sub)，Step2 = Supervisor
        var template = new ApprovalTemplate { Id = "T4", Name = "兩階" };
        template.Steps.Add(new ApprovalStep { Id = "S1", TemplateId = "T4", Sequence = 1, ApproverType = ApproverType.Role, TargetRoleId = "R-Sub" });
        template.Steps.Add(new ApprovalStep { Id = "S2", TemplateId = "T4", Sequence = 2, ApproverType = ApproverType.Supervisor });
        db.ApprovalTemplates.Add(template);
        await db.SaveChangesAsync();

        var service = NewService(db);
        var request = await service.SubmitForApproval("DOC-004", "TestDoc", "T4", "U-Worker");
        Assert.Equal("S1", request.CurrentStepId);
        Assert.Null(request.ActiveApproverId); // Step1 is Role, no auto-resolve

        // Step1 核准 → 推進 Step2，Supervisor 自動解析
        await service.ProcessApproval(request.Id, "U-Worker", "R-Sub", ApprovalStatus.Approved, null);

        var refreshed = await db.ApprovalRequests.FindAsync(request.Id);
        Assert.Equal("S2", refreshed!.CurrentStepId);
        Assert.Equal("U-Boss", refreshed.ActiveApproverId);
    }

    // ────────────────────────────────────────────────────────────────────
    // 2. CancelOnDelete
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelOnDelete_PendingRequest_SetsWithdrawnAndLogs()
    {
        using var db = NewDb();
        db.ApprovalRequests.Add(new ApprovalRequest
        {
            Id = "REQ-1", DocumentId = "DOC-1", DocumentType = "Test", CurrentStepId = "S1",
            Status = ApprovalStatus.Pending, RequesterId = "U-Alice"
        });
        await db.SaveChangesAsync();

        var service = NewService(db);
        var ok = await service.CancelOnDelete("REQ-1", "U-Admin", "ad-hoc 刪除");

        Assert.True(ok);
        var req = await db.ApprovalRequests.FindAsync("REQ-1");
        Assert.Equal(ApprovalStatus.Withdrawn, req!.Status);

        var log = await db.ApprovalLogs.SingleAsync(l => l.RequestId == "REQ-1");
        Assert.Equal(ApprovalStatus.Withdrawn, log.Action);
        Assert.Equal("U-Admin", log.ApproverId);
        Assert.Contains("ad-hoc 刪除", log.Comment);
    }

    [Fact]
    public async Task CancelOnDelete_AlreadyTerminated_ReturnsFalse()
    {
        using var db = NewDb();
        db.ApprovalRequests.Add(new ApprovalRequest
        {
            Id = "REQ-2", DocumentId = "DOC-2", DocumentType = "Test", CurrentStepId = "S1",
            Status = ApprovalStatus.Approved, RequesterId = "U-Alice"
        });
        await db.SaveChangesAsync();

        var service = NewService(db);
        var ok = await service.CancelOnDelete("REQ-2", "U-Admin", null);

        Assert.False(ok);
        // 不應有新增 Log
        Assert.False(await db.ApprovalLogs.AnyAsync(l => l.RequestId == "REQ-2"));
    }

    [Fact]
    public async Task CancelOnDelete_NotFound_ReturnsFalse()
    {
        using var db = NewDb();
        var service = NewService(db);
        var ok = await service.CancelOnDelete("REQ-INEXISTENT", "U-Admin", null);
        Assert.False(ok);
    }

    // ────────────────────────────────────────────────────────────────────
    // 3. IApprovalSyncContributor 註冊機制
    // ────────────────────────────────────────────────────────────────────

    private sealed class FakeContributor : IApprovalSyncContributor
    {
        public string DocumentType { get; }
        public bool Called { get; private set; }
        public ApprovalStatus? ReceivedStatus { get; private set; }
        public string? ReceivedDocId { get; private set; }

        public FakeContributor(string docType) { DocumentType = docType; }

        public Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            Called = true;
            ReceivedDocId = documentId;
            ReceivedStatus = finalStatus;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ProcessApproval_FinalApproved_CallsRegisteredContributor()
    {
        using var db = NewDb();
        db.Roles.Add(new ApplicationRole { Id = "R1", Name = "R1" });
        db.Users.Add(new ApplicationUser { Id = "U-Alice", UserName = "alice", ActiveRoleId = "R1" });

        var template = new ApprovalTemplate { Id = "T-Sync", Name = "Single" };
        template.Steps.Add(new ApprovalStep { Id = "S1", TemplateId = "T-Sync", Sequence = 1,
            ApproverType = ApproverType.Role, TargetRoleId = "R1" });
        db.ApprovalTemplates.Add(template);
        await db.SaveChangesAsync();

        var contributor = new FakeContributor("MyDocType");
        var service = NewService(db, contributor);

        var request = await service.SubmitForApproval("DOC-MY-1", "MyDocType", "T-Sync", "U-Alice");
        await service.ProcessApproval(request.Id, "U-Alice", "R1", ApprovalStatus.Approved, null);

        Assert.True(contributor.Called);
        Assert.Equal("DOC-MY-1", contributor.ReceivedDocId);
        Assert.Equal(ApprovalStatus.Approved, contributor.ReceivedStatus);
    }

    [Fact]
    public async Task ProcessApproval_NoMatchingContributor_FallsBackToBuiltinSwitch()
    {
        // 沒註冊 Contributor 也不會出錯（Core CodeEntry fallback 不會觸發其他 DocumentType）
        using var db = NewDb();
        db.Roles.Add(new ApplicationRole { Id = "R1", Name = "R1" });
        db.Users.Add(new ApplicationUser { Id = "U-Alice", UserName = "alice", ActiveRoleId = "R1" });

        var template = new ApprovalTemplate { Id = "T-NB", Name = "NB" };
        template.Steps.Add(new ApprovalStep { Id = "S1", TemplateId = "T-NB", Sequence = 1,
            ApproverType = ApproverType.Role, TargetRoleId = "R1" });
        db.ApprovalTemplates.Add(template);
        await db.SaveChangesAsync();

        var service = NewService(db); // 沒給 Contributor
        var request = await service.SubmitForApproval("DOC-NB-1", "UnknownDocType", "T-NB", "U-Alice");
        var ok = await service.ProcessApproval(request.Id, "U-Alice", "R1", ApprovalStatus.Approved, null);

        Assert.True(ok); // 不報錯
        var refreshed = await db.ApprovalRequests.FindAsync(request.Id);
        Assert.Equal(ApprovalStatus.Approved, refreshed!.Status);
    }

    [Fact]
    public async Task ProcessApproval_ContributorPrioritizedOverBuiltinCodeEntry()
    {
        using var db = NewDb();
        db.Roles.Add(new ApplicationRole { Id = "R1", Name = "R1" });
        db.Users.Add(new ApplicationUser { Id = "U-Alice", UserName = "alice", ActiveRoleId = "R1" });

        var template = new ApprovalTemplate { Id = "T-Pri", Name = "Pri" };
        template.Steps.Add(new ApprovalStep { Id = "S1", TemplateId = "T-Pri", Sequence = 1,
            ApproverType = ApproverType.Role, TargetRoleId = "R1" });
        db.ApprovalTemplates.Add(template);

        // 預先建一筆 CodeEntry 看若 Contributor 攔截了，CodeEntry 應該不被內建 switch 處理
        db.CodeEntries.Add(new CodeEntry
        {
            Id = "CE-1", Category = "Test", Code = "T-001", NameZh = "測試",
            ApprovalStatus = DocumentApprovalStatus.PendingApproval, IsActive = false
        });
        await db.SaveChangesAsync();

        var customContributor = new FakeContributor("CodeEntry");
        var service = NewService(db, customContributor);

        var request = await service.SubmitForApproval("CE-1", "CodeEntry", "T-Pri", "U-Alice");
        await service.ProcessApproval(request.Id, "U-Alice", "R1", ApprovalStatus.Approved, null);

        Assert.True(customContributor.Called);
        // 內建 switch 不應該再執行 → CodeEntry 狀態保持 PendingApproval（沒被內建 case 改）
        var entry = await db.CodeEntries.IgnoreQueryFilters().SingleAsync(c => c.Id == "CE-1");
        Assert.Equal(DocumentApprovalStatus.PendingApproval, entry.ApprovalStatus);
    }
}
