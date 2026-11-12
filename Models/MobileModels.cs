using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Models
{
    /// <summary>手機功能模組定義（如：工單執行、品質檢查）</summary>
    public class MobileModule
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>模組識別鍵，e.g. "WorkOrderExecution"</summary>
        [Required, MaxLength(100)]
        public string ModuleKey { get; set; } = string.Empty;

        /// <summary>顯示名稱</summary>
        [Required, MaxLength(200)]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Bootstrap Icon class，e.g. "bi-clipboard-check"</summary>
        [MaxLength(100)]
        public string IconClass { get; set; } = "bi-grid";

        public int SortOrder { get; set; } = 0;

        public bool IsActive { get; set; } = true;

        public virtual ICollection<MobileModuleAction> Actions { get; set; } = new List<MobileModuleAction>();
    }

    /// <summary>模組內的作業項目（非固定 CRUD，支援 Scan、Start、Complete 等自訂作業）</summary>
    public class MobileModuleAction
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Required]
        public string ModuleId { get; set; } = string.Empty;

        [ForeignKey("ModuleId")]
        public virtual MobileModule Module { get; set; } = null!;

        /// <summary>作業鍵，e.g. "Start"、"Scan"、"Complete"、"RecordQty"</summary>
        [Required, MaxLength(100)]
        public string ActionKey { get; set; } = string.Empty;

        /// <summary>顯示名稱</summary>
        [Required, MaxLength(200)]
        public string DisplayName { get; set; } = string.Empty;

        public int SortOrder { get; set; } = 0;

        public virtual ICollection<MobileRolePermission> RolePermissions { get; set; } = new List<MobileRolePermission>();
    }

    /// <summary>角色 × 手機作業 = 是否允許</summary>
    public class MobileRolePermission
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Required]
        public string RoleId { get; set; } = string.Empty;

        [ForeignKey("RoleId")]
        public virtual ApplicationRole Role { get; set; } = null!;

        [Required]
        public string ModuleActionId { get; set; } = string.Empty;

        [ForeignKey("ModuleActionId")]
        public virtual MobileModuleAction ModuleAction { get; set; } = null!;

        public bool IsAllowed { get; set; } = false;
    }
}
