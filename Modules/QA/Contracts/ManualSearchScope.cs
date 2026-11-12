namespace SmartFactory.MES.Modules.QA.Contracts;

/// <summary>
/// 手冊搜尋（靜態索引）的收錄範圍——**唯一來源**。
///
/// <para>索引產生器、錨點守門、每頁的腳本注入守門三處都問這裡。
/// 各自寫一份「排除 tutor」的判斷式，遲早會有一處漏改：症狀是索引裡多／少了一批頁，
/// 而搜尋照樣運作、照樣回得出結果，沒有任何一支測試會紅。</para>
/// </summary>
public static class ManualSearchScope
{
    /// <summary>手冊在站台上的根路徑。</summary>
    public const string Root = "/docs/user";

    /// <summary>
    /// 不收錄的子目錄。
    ///
    /// <para><c>tutor</c>：操作教學是「模擬演示」頁，內容是逐步截圖的旁白，
    /// 與說明文字高度重複。收進來的結果是同一個詞在搜尋結果裡出現兩次，
    /// 而教學頁那一筆點進去看到的是演示框而不是說明。</para>
    /// </summary>
    public static readonly IReadOnlyList<string> ExcludedDirs = new[] { "tutor" };

    /// <summary>
    /// 收錄頁數的下限。**防的是「規則形同關閉」**——目錄結構一變（或路徑解析寫錯），
    /// 索引會變成 0 頁而所有比對測試依然全綠（空集合對空集合）。
    /// 2026-09-22 實測 171 頁（173 頁扣掉 tutor 2 頁）。
    /// </summary>
    public const int MinimumPages = 160;

    /// <summary>
    /// 這個相對路徑（以 <c>/</c> 分隔，相對於手冊根）是否收錄。
    /// </summary>
    public static bool Includes(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (!normalized.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return false;

        var segments = normalized.Split('/');
        var inExcludedDir = segments.Length > 1
            && ExcludedDirs.Contains(segments[0], StringComparer.OrdinalIgnoreCase);

        return !inExcludedDir;
    }
}
