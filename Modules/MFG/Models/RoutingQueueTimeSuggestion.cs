using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>
    /// 途程工序「等候時間」建議值（mfg-domain.md §23.2-2）。每晚由實際報工統計，**不自動改途程主檔**——
    /// 途程編輯畫面在工序的等候時間旁顯示建議與「套用」，由工藝人員確認後存檔才生效。
    ///
    /// 只存最近一次計算結果（每次整批覆蓋）；樣本不足 <see cref="Services.QueueTimeSuggestionService.MinSampleSize"/> 的工序不會有列。
    /// 鍵與 <see cref="RoutingOperation"/> 相同（RoutingId 全域唯一）。
    /// </summary>
    public class RoutingQueueTimeSuggestion
    {
        [MaxLength(30)]
        public string RoutingId { get; set; } = string.Empty;

        public int OperationSeq { get; set; }

        /// <summary>建議等候時間（分鐘）＝工序間實際等候（工作時間）中位數 − 本工序搬運時間，下限 0。畫面依工序時間單位換算顯示。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal SuggestedQueueMinutes { get; set; }

        /// <summary>工序間實際間隔（工作時間，分鐘）的中位數——含搬運，供人工判讀。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal MedianGapMinutes { get; set; }

        public int SampleCount { get; set; }

        /// <summary>取樣期間天數（近 N 天開工的派工）。</summary>
        public int PeriodDays { get; set; }

        /// <summary>計算時刻（UTC）。</summary>
        public DateTime CalculatedAt { get; set; }
    }
}
