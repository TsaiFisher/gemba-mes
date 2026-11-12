using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.ACT.Models
{
    public enum ChecklistItemStatus
    {
        Pending    = 0,
        InProgress = 1,
        Completed  = 2,
        Skipped    = 3
    }

    /// <summary>月結作業清單（Phase C，§4.2）。每個期間對每個廠別建立一份 10 項清單。</summary>
    public class MonthEndChecklist : IPlantEntity
    {
        [Key, MaxLength(40)]
        public string ChecklistId { get; set; } = string.Empty;  // PeriodId + PlantId

        [Required, MaxLength(6)]
        public string PeriodId { get; set; } = string.Empty;

        public bool IsCompleted { get; set; }
        public DateTime? CompletedAt { get; set; }
        [MaxLength(50)]
        public string? CompletedBy { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string? PlantId { get; set; }

        public ICollection<ChecklistItem> Items { get; set; } = new List<ChecklistItem>();
    }

    public class ChecklistItem
    {
        public int Id { get; set; }

        [Required, MaxLength(40)]
        public string ChecklistId { get; set; } = string.Empty;

        public int StepNo { get; set; }  // 1–10

        [Required, MaxLength(200)]
        public string StepName { get; set; } = string.Empty;

        public ChecklistItemStatus Status { get; set; } = ChecklistItemStatus.Pending;

        [MaxLength(500)]
        public string? Notes { get; set; }

        public DateTime? CompletedAt { get; set; }
        [MaxLength(50)]
        public string? CompletedBy { get; set; }

        [ForeignKey(nameof(ChecklistId))]
        public MonthEndChecklist? Checklist { get; set; }
    }
}
