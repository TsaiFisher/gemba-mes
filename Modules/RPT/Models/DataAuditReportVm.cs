namespace SmartFactory.MES.Modules.RPT.Models
{
    /// <summary>
    /// 資料稽核診斷儀表板 ViewModel（business_data_flow_reconciliation.md §9 P-8）。
    /// 呈現最近一次 DataAuditRun 的異常明細（廠別範圍內）與近期異常筆數趨勢。
    /// </summary>
    public class DataAuditReportVm
    {
        /// <summary>最近一次執行時間（已轉當地時區）；null = 尚無任何執行紀錄。</summary>
        public DateTime? LatestRunAtLocal { get; set; }

        /// <summary>最近一次執行的觸發方式（Scheduled / Manual）。</summary>
        public string? TriggerType { get; set; }

        /// <summary>最近一次執行、目前廠別範圍內的異常筆數。</summary>
        public int FailureCount { get; set; }

        /// <summary>異常涉及的稽核類型數。</summary>
        public int AuditTypeCount { get; set; }

        /// <summary>最近一次執行耗時（毫秒）。</summary>
        public long DurationMs { get; set; }

        /// <summary>近期各次執行的異常筆數趨勢（時間升冪）。</summary>
        public List<DataAuditTrendPoint> Trend { get; set; } = new();

        /// <summary>最近一次執行的異常明細（依 AuditType、DocId 排序）。</summary>
        public List<DataAuditFindingRow> Findings { get; set; } = new();

        /// <summary>可篩選的稽核類型（最近一次執行出現過的）。</summary>
        public List<string> AuditTypes { get; set; } = new();

        /// <summary>目前套用的稽核類型篩選；null = 全部。</summary>
        public string? SelectedAuditType { get; set; }
    }

    /// <summary>趨勢圖單點：一次執行的異常筆數。</summary>
    public class DataAuditTrendPoint
    {
        /// <summary>執行時間標籤（當地時區 MM-dd HH:mm）。</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>該次執行廠別範圍內的異常筆數。</summary>
        public int Count { get; set; }
    }

    /// <summary>異常明細列（DataAuditFinding 的顯示投影）。</summary>
    public class DataAuditFindingRow
    {
        public string AuditType { get; set; } = string.Empty;
        public string DocId { get; set; } = string.Empty;
        public string? ProductCode { get; set; }
        public string? PlantId { get; set; }
        public decimal DifferenceAmount { get; set; }

        /// <summary>產生當下的中文描述；只在 <see cref="MessageKey"/> 為空（舊資料）時才會被顯示。</summary>
        public string ErrorDescription { get; set; } = string.Empty;

        /// <summary>訊息鍵（<c>AuditMessages</c>）；空＝2026-09-21 之前產生的列。</summary>
        public string MessageKey { get; set; } = string.Empty;

        /// <summary>訊息參數 JSON。</summary>
        public string? MessageArgs { get; set; }

        /// <summary>
        /// 是否擋關帳。畫面必須分得出來，否則操作者看到一整片紅色卻不知道哪幾筆
        /// 才是「不修就結不了帳」的（見 <see cref="SmartFactory.MES.Modules.ACT.Services.AuditSeverityRules"/>）。
        /// 型別層級的判斷由 AuditType 現算、不入庫——嚴重度是規則，改判斷時舊紀錄不該還留著
        /// 舊答案；但**逐筆覆寫**是事實（那一筆的日期落在期間治理範圍外），所以隨紀錄存下來。
        /// </summary>
        public bool IsBlocking
            => BlockingOverride
               ?? SmartFactory.MES.Modules.ACT.Services.AuditSeverityRules.IsBlocking(AuditType);

        /// <summary>逐筆嚴重度覆寫（隨 DataAuditFinding 存；null＝照型別規則）。</summary>
        public bool? BlockingOverride { get; set; }
    }
}
