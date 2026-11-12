using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>
    /// 班制每日排班明細（SI_D）— 複合主鍵：GroupCode + DayOfWeek
    /// DayOfWeek: 1=週一 … 7=週日
    /// </summary>
    public class ShiftGroupDay
    {
        [Required, MaxLength(20)]
        public string GroupCode { get; set; } = string.Empty;

        /// <summary>星期幾（1=週一 … 7=週日）</summary>
        public int DayOfWeek { get; set; }

        /// <summary>第 1 班別代號</summary>
        [MaxLength(20)]
        public string? Shift1Code { get; set; }

        /// <summary>第 2 班別代號</summary>
        [MaxLength(20)]
        public string? Shift2Code { get; set; }

        /// <summary>第 3 班別代號</summary>
        [MaxLength(20)]
        public string? Shift3Code { get; set; }

        /// <summary>第 4 班別代號</summary>
        [MaxLength(20)]
        public string? Shift4Code { get; set; }

        /// <summary>工作開始時間（本日整體開始，可與班別 StartTime 不同）</summary>
        public TimeOnly? WorkStart { get; set; }

        /// <summary>工作結束時間（若 WorkEnd &lt; WorkStart 表示跨午夜）</summary>
        public TimeOnly? WorkEnd { get; set; }

        /// <summary>正班工時（小時），批次展開時填入 WorkCalendar.TM_WKS</summary>
        [Column(TypeName = "decimal(5,2)")]
        public decimal DailyRegularHours { get; set; } = 0;

        /// <summary>加班工時上限（小時），TM_WKS + TM_WKO &lt;= 24</summary>
        [Column(TypeName = "decimal(5,2)")]
        public decimal DailyOvertimeHours { get; set; } = 0;

        // Navigation
        [ForeignKey(nameof(GroupCode))]
        public ShiftGroup? ShiftGroup { get; set; }
    }
}
