namespace SmartFactory.MES.Modules.RPS.ViewModels;

/// <summary>
/// 收件匣一列（S-3）。
///
/// <para>刻意**不含 <c>FilePath</c>**：它只在下載端點內部使用，
/// 送到畫面上等於把儲存路徑外露，而那條路徑是下載端點唯一的輸入。</para>
/// </summary>
public class ReportInboxItemVm
{
    public long RunId { get; set; }

    public int DefinitionId { get; set; }

    public string Name { get; set; } = "";

    public string ToolKey { get; set; } = "";

    /// <summary>排程所屬廠別（報表只查這個廠別的資料）。排程已刪時為 null。</summary>
    public string? PlantId { get; set; }

    /// <summary>UTC。</summary>
    public DateTime RunAt { get; set; }

    /// <summary>產出語系。中英混合的收件清單會產兩份，這一欄讓收件人分得出哪一份是自己的。</summary>
    public string? Language { get; set; }

    public int RowCount { get; set; }

    /// <summary>§5.2.1 規則 4：解算後的絕對區間必須回顯，否則使用者無法確認「上個月」是哪一段。</summary>
    public DateOnly? ResolvedFrom { get; set; }

    public DateOnly? ResolvedTo { get; set; }
}

/// <summary>
/// 執行紀錄一列（S-4）。與收件匣的差別是**它會列出失敗與略過的那些**——
/// 收件匣只給成功且有檔案的，這裡要看得到「為什麼沒有報表」。
/// </summary>
public class ReportRunHistoryItemVm
{
    public long RunId { get; set; }

    public int DefinitionId { get; set; }

    public string Name { get; set; } = "";

    public string ToolKey { get; set; } = "";

    /// <summary>排程所屬廠別（報表只查這個廠別的資料）。排程已刪時為 null。</summary>
    public string? PlantId { get; set; }

    public DateTime RunAt { get; set; }

    public string TriggerType { get; set; } = "";

    public string Status { get; set; } = "";

    public int RowCount { get; set; }

    public long DurationMs { get; set; }

    public string? Language { get; set; }

    /// <summary>失敗或略過的原因。§7.8 S2 的「不得靜默停止」就靠這一欄兌現。</summary>
    public string? ErrorMessage { get; set; }
}
