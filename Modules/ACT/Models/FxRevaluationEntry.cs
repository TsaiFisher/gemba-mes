using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.ACT.Models
{
    public enum RevaluationDocType { APInvoice, ARInvoice }

    /// <summary>IAS 21 期末匯率重評記錄（Phase C，§4.5）。
    /// 月末對未結 AP/AR Invoice 以期末匯率重評，差額計入匯兌損益。</summary>
    public class FxRevaluationEntry : IPlantEntity
    {
        public int Id { get; set; }

        [Required, MaxLength(6)]
        public string PeriodId { get; set; } = string.Empty;

        public RevaluationDocType DocType { get; set; }

        [Required, MaxLength(30)]
        public string DocId { get; set; } = string.Empty;

        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        /// <summary>帳面匯率（原始入帳匯率）</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal BookRate { get; set; }

        /// <summary>期末匯率（重評使用）</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal PeriodEndRate { get; set; }

        /// <summary>外幣金額（未結餘額）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal OutstandingAmount { get; set; }

        /// <summary>帳面本位幣（OutstandingAmount × BookRate）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal BookAmountLocal { get; set; }

        /// <summary>重評本位幣（OutstandingAmount × PeriodEndRate）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal RevaluedAmountLocal { get; set; }

        /// <summary>匯兌損益，以公司立場記：正數＝匯兌利益、負數＝損失。
        /// AR（資產）＝ RevaluedAmountLocal − BookAmountLocal；AP（負債）＝ BookAmountLocal − RevaluedAmountLocal。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ExchangeGainLoss { get; set; }

        public DateTime RevaluedAt { get; set; } = DateTime.UtcNow;
        public string? PlantId { get; set; }
    }
}
