using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Modules.RPS.Models;

/// <summary>排程週期（§7.2）。<c>null</c> ＝ 純常用查詢，不自動執行。</summary>
public enum ReportScheduleKind
{
    Daily,
    Weekly,
    Monthly,
}

/// <summary>
/// 「工具＋固定參數（＋排程）」的組合（§7.0）。
/// **常用查詢與例行排程報表是同一張表的兩種狀態**，差別只在
/// <see cref="ScheduleKind"/> 是不是 <c>null</c>。
///
/// ⚠️⚠️ <b>本實體刻意不實作 <c>IPlantEntity</c> 與 <c>IDataAuthEntity</c></b>（§7.0.1-a）。
/// 這與 <c>CLAUDE.md</c>〈Entity 資料範圍規範〉「作業單據一律實作 <c>IPlantEntity</c>」的通則**相反**，
/// 是經過決議的例外，不是漏寫——請不要依通則「修正」它。
///
/// 理由：日後的派工器必須掃描**全部使用者、全部廠別**的到期定義，但它自己的 scope
/// 只有一組 <c>CurrentPlantId</c>／<c>CurrentRoleId</c>。一旦納入 global query filter：
/// <code>
/// dispatcher scope 未設上下文 → CurrentPlantId = null
///   → 過濾條件 e.PlantId == null（無 null 旁路）
///   → 但 ApplyGlobalValues 新增時已 ??= 填入 PlantId
///   → 畫面建立的排程一律有 PlantId → dispatcher 一筆都掃不到
///   → 排程「全面靜默不執行」，日誌乾淨、無例外、無警告
/// </code>
/// 唯一的替代方案是掃描時裸用 <c>IgnoreQueryFilters</c>，那直接違反 §5.5 T1。
///
/// → 正解：當普通表。<see cref="PlantId"/>／<see cref="OwnerRoleId"/>／
/// <see cref="CreatedByRoleId"/> 只是一般欄位，權限由 Controller 顯式 <c>Where</c> 收斂，
/// 執行該筆定義時再把上下文設進 scope。
/// </summary>
public class ReportDefinition
{
    public int Id { get; set; }

    /// <summary>使用者取的名字，如「上月銷售排行（含金額）」。與 <see cref="OwnerUserId"/> 組成唯一鍵。</summary>
    [Required]
    [StringLength(100)]
    public string Name { get; set; } = "";

    [Required]
    [StringLength(450)]   // 對齊 AspNetUsers.Id
    public string OwnerUserId { get; set; } = "";

    /// <summary>
    /// 建立當下的角色。**排程觸發**時據此解析授權範圍（§7.8 S4）。
    ///
    /// ⚠️ **互動觸發（使用者點開常用查詢）絕不可用這個欄位**——必須以當下使用者的
    /// 角色解析。兩條路徑混用就是權限提升管道（§7.9 L3 的定案約束）。
    /// </summary>
    [StringLength(450)]
    public string OwnerRoleId { get; set; } = "";

    /// <summary>
    /// 廠別代碼。<c>string</c> 而非 <c>int</c>：<c>Plant.Id</c> 在本專案是
    /// <c>string(20)</c> 的代碼（P1／P2），規格 §7.0 的程式碼片段寫成 <c>int?</c> 是筆誤。
    /// </summary>
    [StringLength(20)]
    public string? PlantId { get; set; }

    /// <summary>對應 <c>IReportTool.ToolKey</c>，同時是查權限用的 MenuKey。</summary>
    [Required]
    [StringLength(100)]
    public string ToolKey { get; set; } = "";

    /// <summary>
    /// 結構化參數，**不是 SQL**。期間一律存 <c>RelativePeriod</c> 而非絕對日期（§5.2.1、§7.9-1）：
    /// 存成 <c>2026-07-01~2026-07-31</c> 的「上月銷售排行」，下個月點開還是七月的資料。
    ///
    /// 反序列化必須走 <c>ReportToolCatalog</c> 的 ToolKey→Type 白名單映射，
    /// **不得用 <c>Type.GetType()</c> 或多型反序列化**（§7.0.1-c）。
    /// </summary>
    public string ParamsJson { get; set; } = "";

    // ── 排程欄位（§7.0）──────────────────────────────────────────
    // 目前階段只交付常用查詢（ScheduleKind 恆為 null），以下欄位一併建好但尚無寫入者。
    // 一次建齊是為了避免排程階段再對同一張表做一輪 ALTER；它們全部可空，不影響常用查詢。

    /// <summary><c>null</c> ＝ 純常用查詢（現階段一律如此）。</summary>
    public ReportScheduleKind? ScheduleKind { get; set; }

    /// <summary>0–23。</summary>
    public int? ScheduleHour { get; set; }

    /// <summary>0–59。</summary>
    public int? ScheduleMinute { get; set; }

    /// <summary>Weekly 用。</summary>
    public int? ScheduleDayOfWeek { get; set; }

    /// <summary>Monthly 用。</summary>
    public int? ScheduleDayOfMonth { get; set; }

    /// <summary>UTC，派工器掃這一欄。</summary>
    public DateTime? NextRunAt { get; set; }

    /// <summary>權限失效時由 §7.8 S2 停用。停用要看得見，不可靜默隱藏。</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// 「立即執行」按下後由 <c>ReportScheduleController.RunNow</c> 設為 <c>true</c>，
    /// 派工器跑完那一輪後清回 <c>false</c>。
    ///
    /// <para><b>為什麼需要這一欄</b>：RunNow 不同步執行報表（那會讓大報表卡住請求執行緒），
    /// 它只是把 <see cref="NextRunAt"/> 拉到現在讓派工器撈。於是在派工器眼裡，
    /// 「排程到期」與「有人按了按鈕」長得一模一樣——<c>ReportTriggerType.Manual</c>
    /// 因此從來沒有寫入者，執行紀錄畫面上那個分支是死的。</para>
    ///
    /// <para>執行紀錄是稽核軌跡（排程刪掉後紀錄刻意保留），所以「誰在什麼情況下產了這份報表」
    /// 必須分得出來。</para>
    /// </summary>
    public bool ManualRunRequested { get; set; }

    /// <summary>Excel／Csv／畫面。Email 不實作（§7.3 定案 E1）。</summary>
    [StringLength(20)]
    public string OutputFormat { get; set; } = "";

    /// <summary>沿用既有 <c>IDataAuthEntity</c> 的欄位慣例——但本實體**不**實作該介面（見類別說明）。</summary>
    [StringLength(450)]
    public string CreatedByRoleId { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// 收件人（§7.0.1-b）。純常用查詢為空集合——它沒有「收件人」概念，只有 owner 自己點開。
    /// </summary>
    public ICollection<ReportRecipient> Recipients { get; set; } = new List<ReportRecipient>();
}
