namespace SmartFactory.MES.Modules._Shared.Printing;

/// <summary>
/// 共用列印版型（Views/Shared/_PrintDocument.cshtml）的資料：表頭欄位＋一到多個明細表＋合計。
///
/// <para>2026-10-27 補齊 20 張沒有列印的作業單據時採用：欄位組成各單據不同，但版面
/// （QR、標題、基本資料、明細、合計、簽核欄）完全一樣——各寫一支 Print.cshtml 只會把同一份
/// 版面抄 20 次。各 Controller 只負責填內容；文字一律由呼叫端依語系取好再放進來。</para>
/// </summary>
public sealed class PrintDocument
{
    public string Title { get; init; } = "";
    public string DocNo { get; init; } = "";
    /// <summary>表頭第二行的「日期」標籤與值（例如「收款日期：2026-10-27」）；標籤空白時不顯示。</summary>
    public string DateLabel { get; init; } = "";
    public string? DateValue { get; init; }

    public List<PrintField> Fields { get; } = new();
    public List<PrintTable> Tables { get; } = new();
    public List<string> Totals { get; } = new();

    /// <summary>加表頭欄位；值空白時略過（列印單不印空欄）。</summary>
    public PrintDocument Field(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Fields.Add(new PrintField(label, value));
        return this;
    }

    public PrintDocument Total(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)) Totals.Add(text);
        return this;
    }
}

public sealed record PrintField(string Label, string Value);

/// <summary>明細表；<see cref="NumericColumns"/> 標記靠右對齊的欄（數量、金額）。</summary>
public sealed class PrintTable
{
    public string Title { get; init; } = "";
    public List<string> Columns { get; } = new();
    public HashSet<int> NumericColumns { get; } = new();
    public List<string?[]> Rows { get; } = new();

    public PrintTable Col(string header, bool numeric = false)
    {
        if (numeric) NumericColumns.Add(Columns.Count);
        Columns.Add(header);
        return this;
    }

    public PrintTable Row(params string?[] cells)
    {
        Rows.Add(cells);
        return this;
    }
}
