using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>
    /// 機台狀態異動事件代碼（mfg-domain.md §13.3）。
    /// 對應舊系統 CD_MTR。
    /// </summary>
    public enum MachineEventType
    {
        CI = 0,  // 恢復正常 → Available
        II = 1,  // 設為待機 → Standby
        DI = 2,  // 故障停機 → BreakDown
        DO = 3,  // 故障排除 → Available/Standby
        HI = 4,  // 開始保養 → Maintenance
        HO = 5,  // 保養完成 → Available/Standby
        EO = 6,  // 進入改良 → Modification
        FO = 7,  // 外借出廠 → Loaned
        FI = 8,  // 外借回廠 → Available/Standby
        GO = 9,  // 除役廢棄 → Retired
        CO = 10  // 送出外修 → ExternalRepair
    }

    /// <summary>
    /// 機台異動單主檔（mfg-domain.md §13.2 / §13.3）。
    /// 一張異動單對應一次事件，下含明細記錄受影響的機台。
    /// </summary>
    public class MachineStatusOrder : IPlantEntity
    {
        [Key, MaxLength(40)]
        public string OrderId { get; set; } = string.Empty;

        public MachineEventType EventType { get; set; }

        public DateTime EventAt { get; set; }

        [MaxLength(50)]
        public string? RequestedBy { get; set; }

        [MaxLength(200)]
        public string? Note { get; set; }

        public string? PlantId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<MachineStatusOrderLine> Lines { get; set; } = new List<MachineStatusOrderLine>();
    }

    /// <summary>
    /// 機台異動明細（mfg-domain.md §13.2）。
    /// 記錄每台機台的目標狀態，寫入時同步更新 WorkCenterMachine.MachineStatus
    /// 並處理排程副作用（Scheduled → Pending、InProgress → OnHold）。
    /// </summary>
    public class MachineStatusOrderLine
    {
        [Required, MaxLength(40)]
        public string OrderId { get; set; } = string.Empty;

        public int LineSeq { get; set; }

        [Required, MaxLength(20)]
        public string MachineCode { get; set; } = string.Empty;

        [Required, MaxLength(20)]
        public string WorkCenterCode { get; set; } = string.Empty;

        public MachineStatus TargetStatus { get; set; }

        [MaxLength(30)]
        public string? SourceDocId { get; set; }

        [MaxLength(200)]
        public string? Note { get; set; }

        public MachineStatusOrder? Order { get; set; }
    }
}
