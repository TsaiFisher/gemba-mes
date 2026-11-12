using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.ACT.Models
{
    /// <summary>
    /// 資料稽核執行紀錄（business_data_flow_reconciliation.md §9 P-8）。
    /// 由每日背景排程（DailyDataAuditJobService）或稽核儀表板手動觸發寫入，
    /// 供 RPT 稽核診斷畫面查詢最新結果與趨勢，不必等月結才發現異常。
    /// </summary>
    public class DataAuditRun
    {
        [Key]
        public int RunId { get; set; }

        /// <summary>執行時間（UTC）。</summary>
        public DateTime RunAt { get; set; }

        /// <summary>觸發方式：Scheduled（每日排程）/ Manual（畫面手動）。</summary>
        [MaxLength(20)]
        public string TriggerType { get; set; } = "Scheduled";

        /// <summary>稽核廠別範圍；null = 全廠。</summary>
        [MaxLength(10)]
        public string? PlantId { get; set; }

        /// <summary>異常總筆數（全部檢核合計）。</summary>
        public int FailureCount { get; set; }

        /// <summary>執行耗時（毫秒）。</summary>
        public long DurationMs { get; set; }

        /// <summary>觸發者；排程為 "SYSTEM"。</summary>
        [MaxLength(50)]
        public string CreatedBy { get; set; } = "SYSTEM";

        public List<DataAuditFinding> Findings { get; set; } = new();
    }

    /// <summary>單筆稽核異常（AuditFailureDetail 的持久化形態，欄位一對一對應）。</summary>
    public class DataAuditFinding
    {
        [Key]
        public long FindingId { get; set; }

        public int RunId { get; set; }
        public DataAuditRun? Run { get; set; }

        /// <summary>稽核類型代碼（如 PO_OVER_RECEIPT / BACKFLUSH_MISMATCH）。</summary>
        [MaxLength(40)]
        public string AuditType { get; set; } = string.Empty;

        /// <summary>異常單據號或聚合鍵。</summary>
        [MaxLength(100)]
        public string DocId { get; set; } = string.Empty;

        [MaxLength(50)]
        public string? ProductCode { get; set; }

        [MaxLength(10)]
        public string? PlantId { get; set; }

        /// <summary>一致性差額（數量或金額，依 AuditType 而定）。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal DifferenceAmount { get; set; }

        /// <summary>
        /// 錯誤描述（含期望值與實際值），產生當下的正體中文。
        /// 保留它有兩個理由：① 2026-09-21 之前的列只有這個欄位；
        /// ② 鍵被改名或翻譯漏補時的退路。顯示一律優先走 <see cref="MessageKey"/>。
        /// </summary>
        [MaxLength(1000)]
        public string ErrorDescription { get; set; } = string.Empty;

        /// <summary>
        /// 訊息鍵（<c>AuditMessages</c> 的常數）。空字串＝舊資料，顯示時退回
        /// <see cref="ErrorDescription"/>。
        /// </summary>
        [MaxLength(60)]
        public string MessageKey { get; set; } = string.Empty;

        /// <summary>訊息參數的 JSON 字串陣列；與 <see cref="MessageKey"/> 的模板搭配組句。</summary>
        [MaxLength(2000)]
        public string? MessageArgs { get; set; }

        /// <summary>
        /// 逐筆嚴重度覆寫；<c>null</c>＝照 <c>AuditSeverityRules</c> 的型別規則走。
        ///
        /// 「嚴重度是規則不是事實，所以不入庫」這個原則對**型別層級**成立，但覆寫不是規則，
        /// 是關於這一筆的事實（例如出貨日早於本廠最早會計期間，見
        /// <c>DataAuditService.AuditShipmentInvoiceGapAsync</c>）。不存的話，稽核報表會用
        /// 型別規則重算而顯示「擋關帳」，關帳閘門卻放行——同一筆異常兩個答案。
        /// </summary>
        public bool? BlockingOverride { get; set; }
    }
}
