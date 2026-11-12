using System.Text.Json;
using SmartFactory.MES.Modules.QA.Contracts;

namespace SmartFactory.MES.Modules.QA.Services;

/// <summary>索引產生結果。<c>readonly record struct</c>：見 <c>ParsedChunk</c> 對 R5b 的說明。</summary>
public readonly record struct DocSearchIndexResult(int Pages, int ZhChunks, int EnChunks);

/// <summary>
/// 把 <c>wwwroot/docs/user</c> 的手冊建成**靜態搜尋索引**（每語系一個 JSON，隨手冊一起送）。
///
/// <para><b>為什麼不走 <see cref="IManualSearchService"/>：</b>那條路要 <c>QaDbContext</c>、
/// 要登入、要 <c>Features:QaAssistant:Enabled</c>（預設關閉），還要嵌入模型才有語意通道。
/// 手冊搜尋的價值在於「隨時都在」——綁上那串相依等於在多數部署裡它根本不存在。
/// 這支產生的索引是純靜態檔，沒有資料庫、沒有推論服務、不需要登入。</para>
///
/// <para><b>切塊走 <see cref="IManualDocParser.ParseSections"/> 而非 <c>Parse</c>：</b>
/// 兩者粒度需求相反，理由見該方法的說明。</para>
///
/// <para><b>索引存的是整塊原文，不是摘要。</b>存前 N 字當摘要能省一半體積，
/// 但那樣「搜得到的」就只剩每塊開頭那幾行——使用者搜一個寫在欄位說明表第 20 列的詞
/// 會一無所獲，而搜尋框本身運作正常。摘要改由前端在命中位置即時裁切，
/// 順帶得到一個**保證含命中詞**的摘要。</para>
///
/// <para><b>分詞不在這裡做，也不在前端做。</b>中文直接子字串比對（CJK 沒有詞邊界，
/// <c>indexOf</c> 比任何切詞都準），英文在前端小寫化後比對。
/// <c>CjkTokenizer</c> 的重疊雙字是為了餵 PostgreSQL 的 <c>tsvector</c>——
/// 它需要 token，瀏覽器不需要。把它搬進索引只會讓檔案膨脹到原文的 1.5 倍。</para>
/// </summary>
public class DocSearchIndexBuilder
{
    /// <summary>
    /// 索引格式版號。**前端會擋掉不認得的版號**，而不是硬讀一個欄位含義已變的檔案：
    /// 手冊是靜態檔，使用者的瀏覽器可能拿著上一版的 JS 配這一版的索引。
    /// </summary>
    public const int FormatVersion = 1;

    /// <summary>某語系的索引檔名。分語系兩檔是為了只載當前語系——併成一檔等於每個人都載了兩倍。</summary>
    public static string FileNameFor(string lang) => $"search-index.{lang}.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        // 不縮排：索引是機器讀的，縮排會讓檔案大一成而沒有任何人會去讀它。
        WriteIndented = false,
        // 中文不轉 \uXXXX：轉了之後每個漢字從 3 bytes 變成 6，整份索引大一倍。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IManualDocParser _parser;

    public DocSearchIndexBuilder(IManualDocParser parser) => _parser = parser;

    /// <summary>
    /// 產生索引內容（不寫檔）。回傳 lang → JSON 字串。
    /// </summary>
    /// <param name="manualRoot">手冊根目錄的實體路徑（<c>wwwroot/docs/user</c>）。</param>
    public IReadOnlyDictionary<string, string> Build(string manualRoot, out DocSearchIndexResult result)
    {
        var entries = new Dictionary<string, List<IndexEntry>>
        {
            ["zh"] = new(),
            ["en"] = new(),
        };

        var pages = 0;

        // **排序是產物穩定性的前提**：檔案系統的列舉順序不保證一致，
        // 不排序的話同一份手冊在兩台機器會產出位元組不同的索引，
        // 而守門測試就是逐位元比對——那會變成一支隨機紅的測試。
        foreach (var file in Directory
                     .EnumerateFiles(manualRoot, "*.html", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(manualRoot, file).Replace('\\', '/');
            if (!ManualSearchScope.Includes(rel)) continue;

            pages++;
            var urlDir = UrlDirOf(rel);
            var chunks = _parser.ParseSections(urlDir, File.ReadAllText(file), w => AssetExists(manualRoot, w));

            foreach (var chunk in chunks)
            {
                if (chunk.Text.Length == 0) continue;
                if (!entries.TryGetValue(chunk.Lang, out var bucket)) continue;

                bucket.Add(new IndexEntry(rel, chunk.Anchor, chunk.HeadingPath, chunk.Text));
            }
        }

        result = new DocSearchIndexResult(pages, entries["zh"].Count, entries["en"].Count);

        return entries.ToDictionary(
            kv => kv.Key,
            kv => JsonSerializer.Serialize(
                new IndexFile(FormatVersion, kv.Key, pages, kv.Value), Json));
    }

    /// <summary>產生索引並寫進手冊目錄。回傳實際寫入（內容有變）的檔名。</summary>
    public IReadOnlyList<string> Write(string manualRoot, out DocSearchIndexResult result)
    {
        var written = new List<string>();

        foreach (var (lang, json) in Build(manualRoot, out result))
        {
            var path = Path.Combine(manualRoot, FileNameFor(lang));
            if (File.Exists(path) && File.ReadAllText(path) == json) continue;

            File.WriteAllText(path, json);
            written.Add(FileNameFor(lang));
        }

        return written;
    }

    /// <summary>把手冊根目錄下的相對路徑換算成該頁的網站目錄（<c>/docs/user/ord</c>）。</summary>
    private static string UrlDirOf(string relativePath)
    {
        var dir = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty;
        return dir.Length == 0 ? ManualSearchScope.Root : $"{ManualSearchScope.Root}/{dir}";
    }

    private static bool AssetExists(string manualRoot, string webPath)
    {
        const string prefix = ManualSearchScope.Root + "/";
        if (!webPath.StartsWith(prefix, StringComparison.Ordinal)) return false;

        return File.Exists(Path.Combine(
            manualRoot, webPath[prefix.Length..].Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// 索引檔的一筆。欄位名刻意是單字母——171 頁共數千塊，
    /// <c>"headingPath"</c> 這種欄位名重複幾千次就是白白幾十 KB 的傳輸量。
    /// </summary>
    /// <param name="U">頁面相對路徑（相對於 <c>/docs/user</c>）。</param>
    /// <param name="A">段落錨點；空字串代表跳頁首。</param>
    /// <param name="H">麵包屑，直接當搜尋結果的標題。</param>
    /// <param name="T">整塊原文，前端用它比對並裁切摘要。</param>
    internal readonly record struct IndexEntry(string U, string A, string H, string T);

    /// <param name="V">格式版號。</param>
    /// <param name="L">語系。</param>
    /// <param name="P">收錄頁數。</param>
    /// <param name="C">全部區塊。</param>
    /// <remarks>
    /// <c>readonly record struct</c> 而非 <c>record</c>：R5b 掃 <c>Modules.*</c> 的原始碼，
    /// 要求隱含必填的值型別屬性有對應的多語鍵——<c>V</c> 與 <c>P</c> 是 JSON 欄位名，
    /// 不是任何畫面上的欄位，替它們造多語鍵只會污染資源檔（同 <c>ParsedChunk</c> 的處理）。
    /// </remarks>
    internal readonly record struct IndexFile(int V, string L, int P, IReadOnlyList<IndexEntry> C);
}
