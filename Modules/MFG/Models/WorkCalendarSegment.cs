using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>
    /// 工作日曆可用時段（mfg-domain.md §產能模型）— WorkCalendar 的子表，批次展開時一併物化。
    /// 一天多段（多班別班距／午休缺口）= 多列；專供 Phase C 排程走步器讀取真實可用時刻。
    /// 時刻以「自當日 00:00 起算分鐘」表示（0..1440），避免 TimeOnly 無法表達 24:00 與跨午夜。
    /// 跨午夜班別的午夜後尾段，物化於「隔日」的時段（StartMinute 自 0 起算）。
    /// </summary>
    public class WorkCalendarSegment : IPlantEntity
    {
        public int Id { get; set; }

        public string? PlantId { get; set; }

        [MaxLength(30)]
        public string? WorkCenterId { get; set; }

        [MaxLength(20)]
        public string? MachineCode { get; set; }

        public DateOnly Date { get; set; }

        /// <summary>當日第幾段（1 起）。</summary>
        public int Seq { get; set; }

        /// <summary>區段起（自當日 00:00 起算分鐘，0..1440）。</summary>
        public int StartMinute { get; set; }

        /// <summary>區段迄（自當日 00:00 起算分鐘，0..1440；1440 = 當日結束）。</summary>
        public int EndMinute { get; set; }
    }
}
