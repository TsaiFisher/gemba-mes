using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>工作中心機台（WC_M）— 非生產線工作中心可設定多台機台</summary>
    public class WorkCenterMachine
    {
        [Required, MaxLength(20)]
        public string WorkCenterCode { get; set; } = string.Empty;

        [Required, MaxLength(20)]
        public string MachineCode { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string MachineName { get; set; } = string.Empty;

        public MachineStatus MachineStatus { get; set; } = MachineStatus.Available;

        [MaxLength(200)]
        public string? Remarks { get; set; }

        // Navigation
        [ForeignKey(nameof(WorkCenterCode))]
        public WorkCenter? WorkCenter { get; set; }
    }

    /// <summary>
    /// 機台狀態（對應 mfg-domain.md §13 定義，代碼 A-H）。
    /// 影響排程是否可將派工分派給該機台。
    /// </summary>
    public enum MachineStatus
    {
        Available = 0,      // A: 正常（可排程）
        Standby = 1,        // B: 待機（預設，系統參數可設定為可排程）
        ExternalRepair = 2, // C: 外修中（不可排程）
        BreakDown = 3,      // D: 故障停機（不可排程）
        Modification = 4,   // E: 改良/改裝（不可排程）
        Loaned = 5,         // F: 外借（不可排程）
        Retired = 6,        // G: 廢棄（永久，不再出現在選單）
        Maintenance = 7     // H: 保養中（不可排程）
    }
}
