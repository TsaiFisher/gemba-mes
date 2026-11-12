using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Data
{
    // 繼承 IdentityDbContext 以支援使用者管理，使用擴充後的 ApplicationRole
    public partial class ApplicationDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, string>
    {
        public string? CurrentPlantId { get; set; }
        public string? CurrentRoleId { get; set; }
        public string? CurrentRolePath { get; set; }

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<OrganizationUnit> OrganizationUnits { get; set; }
        public DbSet<ApprovalTemplate> ApprovalTemplates { get; set; }
        public DbSet<ApprovalStep> ApprovalSteps { get; set; }
        public DbSet<ApprovalRequest> ApprovalRequests { get; set; }
        public DbSet<ApprovalLog> ApprovalLogs { get; set; }
        public DbSet<DocumentApprovalConfig> DocumentApprovalConfigs { get; set; }
        public DbSet<ApprovalDelegation> ApprovalDelegations { get; set; }
        public DbSet<MenuPermission> MenuPermissions { get; set; }
        public DbSet<Plant> Plants { get; set; }
        public DbSet<AuditLog> AuditLogs { get; set; }
        public DbSet<MobileModule> MobileModules { get; set; }
        public DbSet<MobileModuleAction> MobileModuleActions { get; set; }
        public DbSet<MobileRolePermission> MobileRolePermissions { get; set; }
        public DbSet<CodeEntry> CodeEntries { get; set; }
        public DbSet<SystemParam> SystemParams { get; set; }
        public DbSet<RolePlantAssignment> RolePlantAssignments { get; set; }
        public DbSet<PasswordHistory> PasswordHistories { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // 初始角色種子資料
            // ConcurrencyStamp 明寫 null：Identity 10（實測）的 IdentityRole 建構子會給隨機值，模型每次都不同，
            // EF 9 起 Migrate() 會因此丟 PendingModelChangesWarning 而讓 app 起不來。null 與既有 snapshot 一致。
            builder.Entity<ApplicationRole>().HasData(
                new ApplicationRole { Id = "1", ConcurrencyStamp = null, Name = "Admin", NameEn = "Admin", NameZh = "管理員", NormalizedName = "ADMIN" },
                new ApplicationRole { Id = "2", ConcurrencyStamp = null, Name = "Manager", NameEn = "Manager", NameZh = "經理", NormalizedName = "MANAGER" },
                new ApplicationRole { Id = "3", ConcurrencyStamp = null, Name = "Operator", NameEn = "Operator", NameZh = "操機員", NormalizedName = "OPERATOR" }
            );

            // ApplicationUser.ActivePlantId FK
            builder.Entity<ApplicationUser>()
                .HasOne(u => u.ActivePlant)
                .WithMany()
                .HasForeignKey(u => u.ActivePlantId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);

            // MobileModule: ModuleKey 唯一
            builder.Entity<MobileModule>()
                .HasIndex(m => m.ModuleKey)
                .IsUnique();

            // MobileRolePermission: (RoleId, ModuleActionId) 唯一
            builder.Entity<MobileRolePermission>()
                .HasIndex(m => new { m.RoleId, m.ModuleActionId })
                .IsUnique();

            // MenuPermission: (RoleId, MenuKey) 唯一約束，防止重複設定
            builder.Entity<MenuPermission>()
                .HasIndex(m => new { m.RoleId, m.MenuKey })
                .IsUnique();

            // ApprovalRequest FK 約束
            builder.Entity<ApprovalRequest>()
                .HasOne<ApprovalStep>()
                .WithMany()
                .HasForeignKey(r => r.CurrentStepId)
                .OnDelete(DeleteBehavior.Restrict);

            // xmin 樂觀並發（PostgreSQL 系統欄位，無 DDL）：
            // 防兩位簽核人同時操作（或連點）造成核准/駁回互相覆蓋、步驟重複推進。
            // 後到的 UPDATE 帶 WHERE xmin 比對失敗 → DbUpdateConcurrencyException → ApprovalService 回 false。
            builder.Entity<ApprovalRequest>().UseXminAsConcurrencyToken();

            builder.Entity<ApprovalRequest>()
                .HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(r => r.RequesterId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);

            // ApprovalLog FK 約束
            builder.Entity<ApprovalLog>()
                .HasOne<ApprovalRequest>()
                .WithMany()
                .HasForeignKey(l => l.RequestId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<ApprovalLog>()
                .HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(l => l.ApproverId)
                .OnDelete(DeleteBehavior.Restrict);

            // ApprovalDelegation：委託人/代理人皆須為實際使用者；解析熱路徑以 DelegatorId 查詢
            builder.Entity<ApprovalDelegation>()
                .HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(d => d.DelegatorId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<ApprovalDelegation>()
                .HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(d => d.DelegateId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<ApprovalDelegation>()
                .HasIndex(d => d.DelegatorId);

            // DocumentApprovalConfig FK + 唯一索引
            builder.Entity<DocumentApprovalConfig>()
                .HasOne(c => c.ApprovalTemplate)
                .WithMany()
                .HasForeignKey(c => c.ApprovalTemplateId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);

            builder.Entity<DocumentApprovalConfig>()
                .HasIndex(c => new { c.PlantId, c.DocumentType })
                .IsUnique();

            // RolePlantAssignment: (RoleId, PlantId) 複合主鍵
            builder.Entity<RolePlantAssignment>()
                .HasKey(rpa => new { rpa.RoleId, rpa.PlantId });

            builder.Entity<RolePlantAssignment>()
                .HasOne(rpa => rpa.Role)
                .WithMany(r => r.PlantAssignments)
                .HasForeignKey(rpa => rpa.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<RolePlantAssignment>()
                .HasOne(rpa => rpa.Plant)
                .WithMany()
                .HasForeignKey(rpa => rpa.PlantId)
                .OnDelete(DeleteBehavior.Cascade);

            // CodeEntry: (Category, Code) 全域唯一索引
            builder.Entity<CodeEntry>()
                .HasIndex(c => new { c.Category, c.Code })
                .IsUnique();

            // L4：PasswordHistory — FK 掛 AspNetUsers（級聯刪除），(UserId, CreatedAt) 索引供取最近 N 筆
            builder.Entity<PasswordHistory>()
                .HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(h => h.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<PasswordHistory>()
                .HasIndex(h => new { h.UserId, h.CreatedAt });

            // 多廠別與層級權限全域查詢過濾
            foreach (var entityType in builder.Model.GetEntityTypes())
            {
                var parameter = System.Linq.Expressions.Expression.Parameter(entityType.ClrType, "e");
                System.Linq.Expressions.Expression? combinedFilter = null;

                // 1. 廠別過濾 (IPlantEntity)
                if (typeof(IPlantEntity).IsAssignableFrom(entityType.ClrType))
                {
                    var property = System.Linq.Expressions.Expression.Property(parameter, nameof(IPlantEntity.PlantId));
                    var currentPlantIdProperty = System.Linq.Expressions.Expression.Property(System.Linq.Expressions.Expression.Constant(this), nameof(CurrentPlantId));
                    combinedFilter = System.Linq.Expressions.Expression.Equal(property, currentPlantIdProperty);
                }

                // 2. 層級權限過濾 (IDataAuthEntity)
                if (typeof(IDataAuthEntity).IsAssignableFrom(entityType.ClrType))
                {
                    var pathProperty = System.Linq.Expressions.Expression.Property(parameter, nameof(IDataAuthEntity.OrganizationUnitPath));
                    var roleIdProperty = System.Linq.Expressions.Expression.Property(parameter, nameof(IDataAuthEntity.CreatedByRoleId));
                    var currentRolePathProperty = System.Linq.Expressions.Expression.Property(System.Linq.Expressions.Expression.Constant(this), nameof(CurrentRolePath));
                    var currentRoleIdProperty = System.Linq.Expressions.Expression.Property(System.Linq.Expressions.Expression.Constant(this), nameof(CurrentRoleId));
                    
                    var isNullOrEmptyMethod = typeof(string).GetMethod("IsNullOrEmpty", new[] { typeof(string) })!;
                    var startsWithMethod = typeof(string).GetMethod("StartsWith", new[] { typeof(string) })!;
                    
                    var isPathNull = System.Linq.Expressions.Expression.Call(isNullOrEmptyMethod, currentRolePathProperty);
                    
                    // 條件 1: 若 RolePath 為空，則僅比對 RoleId
                    var roleMatch = System.Linq.Expressions.Expression.Equal(roleIdProperty, currentRoleIdProperty);

                    // 條件 2: 若 RolePath 存在，則
                    //   (a) Path 比對開頭 OR
                    //   (b) 資料的 OrganizationUnitPath 為 null（DataAuth 啟用前的歷史資料，視為公開）
                    var startsWith = System.Linq.Expressions.Expression.Call(pathProperty, startsWithMethod, currentRolePathProperty);
                    var entityPathIsNull = System.Linq.Expressions.Expression.Call(isNullOrEmptyMethod, pathProperty);
                    var startsWithOrLegacy = System.Linq.Expressions.Expression.OrElse(entityPathIsNull, startsWith);

                    // 組合: string.IsNullOrEmpty(CurrentRolePath)
                    //          ? e.CreatedByRoleId == CurrentRoleId
                    //          : (string.IsNullOrEmpty(e.OrganizationUnitPath) || e.OrganizationUnitPath.StartsWith(CurrentRolePath))
                    var authFilter = System.Linq.Expressions.Expression.Condition(isPathNull, roleMatch, startsWithOrLegacy);
                    
                    combinedFilter = combinedFilter == null ? authFilter : System.Linq.Expressions.Expression.AndAlso(combinedFilter, authFilter);
                }

                if (combinedFilter != null)
                {
                    var filterExpression = System.Linq.Expressions.Expression.Lambda(combinedFilter, parameter);
                    entityType.SetQueryFilter(filterExpression);
                }
            }

            OnModelCreatingPartial(builder);
        }

        partial void OnModelCreatingPartial(ModelBuilder builder);

        public override int SaveChanges()
        {
            ApplyGlobalValues();
            return base.SaveChanges();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            ApplyGlobalValues();
            return base.SaveChangesAsync(cancellationToken);
        }

        private void ApplyGlobalValues()
        {
            // 處理廠別
            foreach (var entry in ChangeTracker.Entries<IPlantEntity>())
            {
                if (entry.State == EntityState.Added)
                {
                    entry.Entity.PlantId ??= CurrentPlantId;
                }
            }

            // 處理 UpdatedAt（BaseEntity 子類別）
            foreach (var entry in ChangeTracker.Entries<BaseEntity>())
            {
                if (entry.State == EntityState.Modified)
                {
                    entry.Entity.UpdatedAt = DateTime.UtcNow;
                }
            }

            // 處理層級權限
            foreach (var entry in ChangeTracker.Entries<IDataAuthEntity>())
            {
                if (entry.State == EntityState.Added)
                {
                    entry.Entity.CreatedByRoleId ??= CurrentRoleId;
                    entry.Entity.OrganizationUnitPath ??= CurrentRolePath;
                }
            }
        }
    }
}