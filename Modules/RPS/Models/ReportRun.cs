using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Modules.RPS.Models;

/// <summary>報表執行的觸發來源。字串落庫，見 <see cref="ReportRun.TriggerType"/>。</summary>
public static class ReportTriggerType
{
    public const string Scheduled = "Scheduled";
    public const string Manual    = "Manual";
    /// <summary>Phase 2 的 NL 觸發。現階段沒有寫入者，先訂好字面值避免日後各寫各的。</summary>
    public const string Nl        = "Nl";
}

/// <summary>報表執行結果。<see cref="ReportRun.Status"/> 的合法值。</summary>
public static class ReportRunStatus
{
    public const string Success = "Success";
    public const string Failed  = "Failed";

    /// <summary>
    /// owner 權限已失效而未執行（§7.8 S2）。
    /// **與 Failed 分開是必要的**：Failed 是系統壞了要修，這個是人事異動的正常結果，
    /// 混在一起會讓管理者在一堆權限異動裡找不到真正的故障。
    /// </summary>
    public const string SkippedNoPermission = "SkippedNoPermission";
}

/// <summary>
/// 報表排程的執行紀錄（§7.4）。比照既有 <c>DataAuditRun</c> 的欄位範式。
///
/// <para><b>這張表是硬需求，不是為了好看</b>，三個理由各自獨立：</para>
/// <list type="number">
///   <item>§8.6(c) 全量稽核要求機密查詢事後可追——排程執行也是查詢。</item>
///   <item>§7.8 S2（owner 權限失效要停排程）需要一個地方記錄「為什麼停了」。
///         沒有它，使用者只會看到排程莫名不再送出。</item>
///   <item>§5.2.1 規則 4：解算後的絕對區間必須回顯，否則收到報表的人無法確認
///         「上個月」被解成哪一段。</item>
/// </list>
///
/// <para>與 <see cref="ReportDefinition"/> 同樣**刻意不實作 <c>IPlantEntity</c>／<c>IDataAuthEntity</c>**
/// （§7.0.1-a）：派工器要跨廠別寫入，納入 global filter 會讓紀錄寫不進去或查不出來。</para>
///
/// <para>語系去重（§7.3 定案 #16）：同一次排程若收件人有中英兩種語系，會產生兩個檔，
/// 對應**兩列** <see cref="ReportRun"/>（<see cref="RunAt"/> 相同、<see cref="FilePath"/> 與
/// <see cref="Language"/> 不同）。</para>
/// </summary>
public class ReportRun
{
    public long RunId { get; set; }

    public int DefinitionId { get; set; }

    /// <summary>UTC。</summary>
    public DateTime RunAt { get; set; }

    /// <summary>見 <see cref="ReportTriggerType"/>。</summary>
    [Required]
    [StringLength(20)]
    public string TriggerType { get; set; } = "";

    /// <summary>見 <see cref="ReportRunStatus"/>。</summary>
    [Required]
    [StringLength(30)]
    public string Status { get; set; } = "";

    /// <summary>
    /// <c>RelativePeriod</c> 解算後的實際區間（§5.2.1 規則 4）。
    /// 沒有期間概念的工具留 <c>null</c>——用 <c>default</c> 會落庫成 0001-01-01，
    /// 在畫面上看起來像資料壞掉。
    /// </summary>
    public DateOnly? ResolvedFrom { get; set; }

    public DateOnly? ResolvedTo { get; set; }

    public int RowCount { get; set; }

    public long DurationMs { get; set; }

    /// <summary>
    /// 產出檔的相對路徑（相對於 <c>Reports:StoragePath</c>）。
    /// **只存相對路徑**：存絕對路徑的話換機器部署、換掛載點就全部指向不存在的檔。
    /// </summary>
    [StringLength(260)]
    public string? FilePath { get; set; }

    /// <summary>產出語系（<c>zh-TW</c>／<c>en-US</c>）。語系去重的分組鍵。</summary>
    [StringLength(10)]
    public string? Language { get; set; }

    [StringLength(1000)]
    public string? ErrorMessage { get; set; }

    [Required]
    [StringLength(450)]
    public string CreatedBy { get; set; } = "SYSTEM";
}
