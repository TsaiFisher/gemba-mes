using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>
    /// 排程執行紀錄（每次全量排程一列，手動與自動都記）。排程頁「排程紀錄」頁籤據此回答
    /// 「這次重排動了什麼」——自動重排開啟後，生管不再親手按排程，沒有這份紀錄就看不出派工為何移動。
    /// 校準（01:00 ETA，<see cref="Services.ScheduleRunMode.PreserveAssignment"/>）不記：它不換機台、不改先後。
    ///
    /// 與 <see cref="ScheduleRunState"/> 同理刻意不實作 IPlantEntity：寫入端是沒有登入使用者的背景服務，
    /// 全域廠別過濾會讓讀寫靜默落空。一律以明確的 PlantId 讀寫。保留 <see cref="RetentionDays"/> 天。
    /// </summary>
    public class ScheduleRunLog
    {
        /// <summary>紀錄保留天數；每次寫入時順帶刪除本廠更舊的紀錄。</summary>
        public const int RetentionDays = 60;

        /// <summary>單次紀錄最多保存的異動明細筆數；筆數統計欄位仍是全量。</summary>
        public const int MaxChangeRows = 1000;

        [Key]
        public long RunLogId { get; set; }

        [Required, MaxLength(20)]
        public string PlantId { get; set; } = string.Empty;

        /// <summary>開始讀資料的時刻（UTC，＝<see cref="ScheduleRunState.DataAsOf"/>）。</summary>
        public DateTime StartedAt { get; set; }

        /// <summary>完成時刻（UTC）。</summary>
        public DateTime FinishedAt { get; set; }

        /// <summary>觸發方式：Manual／OnChange／BeforeDispatch／Daily。</summary>
        [Required, MaxLength(20)]
        public string Trigger { get; set; } = string.Empty;

        /// <summary>執行者（使用者 ID）；自動重排為 null。</summary>
        [MaxLength(50)]
        public string? RunBy { get; set; }

        /// <summary>排序規則（<see cref="Services.ScheduleRule"/> 名稱）。</summary>
        [Required, MaxLength(20)]
        public string Rule { get; set; } = string.Empty;

        /// <summary>凍結時界（小時）；手動排程或未設定為 null。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal? FreezeHours { get; set; }

        public int ScheduledCount { get; set; }
        public int SkippedCount { get; set; }

        /// <summary>因凍結時界而保留原機台與時段的已排派工數。</summary>
        public int FencedCount { get; set; }

        public int NewlyScheduledCount { get; set; }
        public int MovedCount { get; set; }
        public int MachineChangedCount { get; set; }
        public int UnscheduledCount { get; set; }

        /// <summary>異動前或異動後落在明天（廠別當地日）的派工數——現場可能已照 17:00 提醒備料、備模。</summary>
        public int TomorrowChangedCount { get; set; }

        /// <summary>明細是否因超過 <see cref="MaxChangeRows"/> 而截斷。</summary>
        public bool ChangesTruncated { get; set; }

        public List<ScheduleRunChange> Changes { get; set; } = new();
    }

    /// <summary>排程異動種類。</summary>
    public enum ScheduleChangeKind
    {
        /// <summary>原本待排，這次排入。</summary>
        NewlyScheduled,
        /// <summary>機台不變、開始時刻改變。</summary>
        Moved,
        /// <summary>改排到另一台機台。</summary>
        MachineChanged,
        /// <summary>原本已排，這次排不進（無可用機台等）退回待排。</summary>
        Unscheduled,
    }

    /// <summary>單次排程中一張派工的異動。時刻數字即廠別當地時間（Kind=Utc 只是標籤，見 mfg-domain.md UTC 地雷）。</summary>
    public class ScheduleRunChange
    {
        [Key]
        public long ChangeId { get; set; }

        public long RunLogId { get; set; }
        public ScheduleRunLog? RunLog { get; set; }

        [Required, MaxLength(40)]
        public string DispatchId { get; set; } = string.Empty;

        [Required, MaxLength(40)]
        public string WorkOrderId { get; set; } = string.Empty;

        public int OperationSeq { get; set; }

        public ScheduleChangeKind Kind { get; set; }

        [MaxLength(40)]
        public string? OldMachineCode { get; set; }
        public DateTime? OldStart { get; set; }

        [MaxLength(40)]
        public string? NewMachineCode { get; set; }
        public DateTime? NewStart { get; set; }
    }
}
