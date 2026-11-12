using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>班別定義（SH）</summary>
    public class Shift : IPlantEntity
    {
        [Key, MaxLength(20)]
        public string ShiftCode { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string ShiftName { get; set; } = string.Empty;

        /// <summary>上班時間（例：08:00）</summary>
        public TimeOnly? StartTime { get; set; }

        /// <summary>下班時間（例：17:00）</summary>
        public TimeOnly? EndTime { get; set; }

        /// <summary>休息（午休）開始時間（例：12:00）。供未來甘特圖以實際時鐘呈現時跳過此區間。</summary>
        public TimeOnly? BreakStart { get; set; }

        /// <summary>休息（午休）結束時間（例：13:00）。BreakEnd-BreakStart = 休息時長。</summary>
        public TimeOnly? BreakEnd { get; set; }

        /// <summary>工作分鐘數（淨工時 = 班距 - 休息）</summary>
        public int? WorkMinutes { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // IPlantEntity
        public string? PlantId { get; set; }

        /// <summary>
        /// 由上下班時間扣除午休推算淨工時（分鐘）。跨午夜（End ≤ Start）自動 +24h。
        /// 缺上班或下班時間時回傳 null（無法推算，由呼叫端決定保留原值）。
        /// </summary>
        public static int? ComputeWorkMinutes(TimeOnly? start, TimeOnly? end, TimeOnly? breakStart, TimeOnly? breakEnd)
        {
            if (start is not { } s || end is not { } e) return null;

            double span = e.ToTimeSpan().TotalMinutes - s.ToTimeSpan().TotalMinutes;
            if (span <= 0) span += 1440; // 跨午夜班

            double brk = 0;
            if (breakStart is { } bs && breakEnd is { } be)
            {
                brk = be.ToTimeSpan().TotalMinutes - bs.ToTimeSpan().TotalMinutes;
                if (brk < 0) brk += 1440;
            }

            var net = span - brk;
            return net < 0 ? 0 : (int)Math.Round(net);
        }
    }
}
