using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Models
{
    public enum ApproverType
    {
        Supervisor = 1,
        Role = 2,
        User = 3
    }

    public class ApprovalTemplate : IPlantEntity, IDataAuthEntity
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();
        [Required]
        public string Name { get; set; } = string.Empty;
        public string? TargetDocumentType { get; set; } // 例如 "WorkOrder"

        // 同一簽核人自動跳簽：步驟的簽核人（含申請人本人）已於先前步驟簽核過時，
        // 該步驟自動核准並寫入 Skipped 歷程。預設關閉，逐模板啟用。
        public bool SkipSameApprover { get; set; }

        public virtual ICollection<ApprovalStep> Steps { get; set; } = new List<ApprovalStep>();

        // IPlantEntity + IDataAuthEntity（自動由 DbContext 填入）
        public string? PlantId { get; set; }
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }
    }

    public class ApprovalStep
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string? TemplateId { get; set; }
        [ForeignKey("TemplateId")]
        public virtual ApprovalTemplate? Template { get; set; }

        public int Sequence { get; set; }
        public ApproverType ApproverType { get; set; }

        public string? TargetRoleId { get; set; }
        public string? TargetUserId { get; set; }

        // 保留欄位：條件跳簽尚未實作，引擎不會評估此值（UI 已收斂不再提供輸入）
        public string? Condition { get; set; }
    }

    public class ApprovalRequest : IPlantEntity, IDataAuthEntity
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string DocumentId { get; set; } = string.Empty;
        public string DocumentType { get; set; } = string.Empty;

        public string CurrentStepId { get; set; } = string.Empty;
        public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

        public string? RequesterId { get; set; }
        public string? RequesterRoleId { get; set; } // 記錄申請作業的角色（審核流程用）

        // 送審時快照的簽核人 ID（主管型步驟解析結果，或管理員重新指派後的覆寫值）
        public string? ActiveApproverId { get; set; }

        // 代理簽核時的原簽核人 ID：ActiveApproverId 因代理設定被改為代理人時記錄被代理者，
        // 供簽核歷程顯示「代理 X 簽核」。步驟推進或管理員重新指派時重新計算/清除。
        public string? ActiveDelegatorId { get; set; }

        // 送審時間（清單排序、時效觀察用）
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // IPlantEntity + IDataAuthEntity（自動由 DbContext 填入，用於資料可見性控制）
        public string? PlantId { get; set; }
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }
    }

    public enum ApprovalStatus
    {
        Pending,
        Approved,
        Rejected,
        Withdrawn,           // 建立者撤回
        ReturnedForRevision, // 審核者退回修改（申請人可重新送審）
        Skipped              // 自動跳簽（僅用於 ApprovalLog.Action，申請單狀態不會是此值）
    }

    public enum DocumentApprovalStatus
    {
        Draft = 0,           // 草稿（已儲存，尚未送審）
        PendingApproval = 1, // 待審核中
        Approved = 2,        // 已核准
        Rejected = 3         // 已退回（可修改後重新送審）
    }

    public class ApprovalLog : IPlantEntity
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string RequestId { get; set; } = string.Empty;
        public string ApproverId { get; set; } = string.Empty;
        public string ApproverRoleId { get; set; } = string.Empty;
        public ApprovalStatus Action { get; set; }
        public string? Comment { get; set; }
        public DateTime ActionTime { get; set; } = DateTime.UtcNow;

        // 代理簽核：實際操作者為 ApproverId，本欄記錄被代理的原簽核人（非代理簽核時為 null）
        public string? OnBehalfOfUserId { get; set; }

        // IPlantEntity（自動由 DbContext 填入，稽核記錄不套用層級過濾）
        public string? PlantId { get; set; }
    }

    /// <summary>
    /// 簽核代理人設定（全域，不分廠別——代理跟著「人」走）。
    /// 生效條件：IsActive 且今日落在 StartDate..EndDate 區間。
    /// 單層代理：解析時不遞迴展開（A→B、B→C 時 A 的單只到 B）。
    /// </summary>
    public class ApprovalDelegation
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>委託人（被代理者）UserId</summary>
        [Required]
        public string DelegatorId { get; set; } = string.Empty;

        /// <summary>代理人 UserId</summary>
        [Required]
        public string DelegateId { get; set; } = string.Empty;

        public DateOnly StartDate { get; set; }
        public DateOnly EndDate { get; set; }

        public bool IsActive { get; set; } = true;

        [MaxLength(200)]
        public string? Reason { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class DocumentApprovalConfig : IPlantEntity
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Required, MaxLength(100)]
        public string DocumentType { get; set; } = string.Empty;      // 內部鍵值 e.g. "PurchaseOrder"

        [Required, MaxLength(200)]
        public string DocumentTypeName { get; set; } = string.Empty;  // 顯示名稱 e.g. "請購單"

        public bool IsApprovalRequired { get; set; }

        public string? ApprovalTemplateId { get; set; }

        [ForeignKey("ApprovalTemplateId")]
        public virtual ApprovalTemplate? ApprovalTemplate { get; set; }

        // IPlantEntity（自動由 DbContext 填入）
        public string? PlantId { get; set; }
    }
}
