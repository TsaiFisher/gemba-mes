using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Data;
using SmartFactory.MES.Modules.QA.Models;

namespace SmartFactory.MES.Modules.QA.Services;

/// <summary>
/// 匯入結果。<c>readonly record struct</c>：見 <c>ParsedChunk</c> 對 R5b 的說明。
///
/// <para><see cref="EntriesTotal"/>／<see cref="EntriesApproved"/>／<see cref="EntriesStale"/>
/// 算的是**這一輪 manifest 涵蓋的全部段落**（含沿用未重寫的）——它們回答的是
/// 「索引現在長什麼樣」。<see cref="EntriesRewritten"/> 與 <see cref="ManifestsUnchanged"/>
/// 才是「這一輪實際動了什麼」，兩者分開是因為**動了什麼決定要不要重嵌**，
/// 而重嵌是以分鐘計的（§10.7）。</para>
/// </summary>
public readonly record struct ExtractImportResult(
    int ManifestsScanned,
    int ManifestsRemoved,
    int ManifestsUnchanged,
    int EntriesTotal,
    int EntriesRewritten,
    int EntriesApproved,
    int EntriesStale);

/// <summary>把 <c>.claude/qa-extracts/</c> 的 manifest 匯入成擷取物索引（規格 §10.11.9）。</summary>
public interface IExtractManifestImporter
{
    /// <summary>
    /// 依 manifest 更新擷取物語料，並依來源檔現況重算 <c>IsStale</c>。
    ///
    /// <para><b>逐份比對，內容一模一樣的 manifest 原地不動</b>——見
    /// <c>ExtractManifestImporter.SameAsIndexed</c>：重寫會把該份的向量一起清掉，
    /// 而重嵌是以分鐘計的。</para>
    ///
    /// <para><b>manifest 目錄不存在時什麼都不做</b>（連刪除都不做）——見實作說明，
    /// 這是正式機的正常狀態，不是錯誤。</para>
    /// </summary>
    Task<ExtractImportResult> ImportAsync(CancellationToken ct = default);
}

/// <summary>
/// <para><b>以 manifest 為單位比對，內容相同就原地不動。</b>
/// 原本是無條件全量重寫，理由是「數量以百計、省下哪些變了的判斷」——
/// <b>但那個理由沒有把重嵌算進去</b>：重寫會把該份的向量一起清掉，
/// 而 1288 段在開發機 CPU 上要重嵌約 30 分鐘，於是每一次啟動、每一次跑量測都付一次。
/// 匯入確實不在正式機的熱路徑上，但它在**開發與量測的熱路徑上**。</para>
///
/// <para><b>比對的是建好的列本身，不是 <c>ContentHash</c>。</b>
/// 那一欄只算 <c>text</c>，而**核可（<c>approved</c>）翻轉不會改動任何一個字**——
/// 拿它當依據會讓「退回一則」這件事安靜地不生效，那比每次重嵌糟得多。
/// 逐欄比對的漏洞是「加了新欄位卻忘了加進比對」，那道防線是
/// <c>DocChunkPersistedFieldsFacts</c>：它把 <c>DocChunk</c> 的欄位清單釘死，
/// 新增欄位就紅，逼人決定它算不算「內容變了」。</para>
///
/// <para><b>刻意不吞例外</b>，與 <see cref="DocIndexBuilder"/> 同一個立場：
/// 一份 manifest 壞掉就整批中止。略過壞掉那一份的後果是那些規則在 Q&amp;A 中永久消失，
/// 而且沒有任何訊號——使用者只會覺得助理不知道這件事。</para>
/// </summary>
public class ExtractManifestImporter : IExtractManifestImporter
{
    /// <summary>manifest 目錄，相對於 repo 根。可用組態覆寫（測試會指到暫存目錄）。</summary>
    public const string ManifestDirKey = "Features:QaAssistant:ExtractManifestDir";
    public const string DefaultManifestDir = ".claude/qa-extracts";

    /// <summary>擷取物的引文命名空間。頁碼形狀＝<c>rules/&lt;slug&gt;.html</c>，與手冊頁碼一致。</summary>
    public const string RulesUrlRoot = "/docs/user/rules";

    /// <summary><c>DocChunk.FilePath</c> 的前綴。<see cref="DocIndexBuilder"/> 只掃手冊，兩者不會撞。</summary>
    private const string FilePathPrefix = "extract/";

    private static readonly Regex SlugPattern = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);
    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly QaDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExtractManifestImporter> _log;

    public ExtractManifestImporter(
        QaDbContext db, IConfiguration configuration, ILogger<ExtractManifestImporter> log)
    {
        _db = db;
        _configuration = configuration;
        _log = log;
    }

    /// <summary>
    /// repo 根目錄。從目前工作目錄往上找 <c>.git</c>——manifest 與被擷取的來源檔都住在版控裡，
    /// 兩者的路徑都必須以 repo 根為基準，而 <c>IWebHostEnvironment.ContentRootPath</c>
    /// 在測試與正式機底下指向不同的地方。
    /// </summary>
    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
        return null;
    }

    public async Task<ExtractImportResult> ImportAsync(CancellationToken ct = default)
    {
        var root = FindRepoRoot();
        var configured = _configuration[ManifestDirKey];
        var dir = configured is { Length: > 0 } && Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(root ?? Directory.GetCurrentDirectory(), configured ?? DefaultManifestDir);

        if (!Directory.Exists(dir))
        {
            // **這裡必須直接返回，一列都不能刪。**
            //
            // 正式機沒有 manifest 目錄，這是 §10.11.6 的設計而非缺漏：擷取物在建置期就寫進 DB
            // 並隨 DB 出貨，來源與 manifest 都不進 publish 產物（實測 .cs 0 份、
            // .claude/specifications/ 一份都沒有）。
            //
            // 若在這裡照常執行「manifest 沒提到的就刪」，每一次正式機啟動都會把整批出貨的
            // 擷取物清空，而下一次啟動也補不回來——與 DocIndexBuilder 未收斂範圍時
            // 會清空擷取物是同一個形狀的錯，只是方向相反。
            _log.LogInformation(
                "找不到擷取物 manifest 目錄 {Dir}，略過匯入（正式機的正常狀態：索引隨 DB 出貨）。", dir);
            return default;
        }

        var files = Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var result = new ExtractImportResult(files.Count, 0, 0, 0, 0, 0, 0);
        var keptFilePaths = new List<string>();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileNameWithoutExtension(file);
            var manifest = await ReadAsync(file, ct);
            var filePath = FilePathPrefix + name;
            keptFilePaths.Add(filePath);

            var (isStale, staleReason) = await EvaluateStaleAsync(root, manifest, file, ct);
            var rows = BuildRows(filePath, manifest, isStale);

            var indexed = await _db.DocChunks
                .AsNoTracking()
                .Where(c => c.Corpus == DocCorpus.Extract && c.FilePath == filePath)
                .OrderBy(c => c.ChunkIndex)
                .ToListAsync(ct);

            var unchanged = SameAsIndexed(indexed, rows);
            if (!unchanged)
            {
                // 該份整份刪掉重寫，不做逐列 diff：一份的段落數會變（重跑擷取就會變），
                // 而「哪一列對應哪一列」在段落數變動之後沒有可靠的對法。
                await _db.DocChunks
                    .Where(c => c.Corpus == DocCorpus.Extract && c.FilePath == filePath)
                    .ExecuteDeleteAsync(ct);

                _db.DocChunks.AddRange(rows);
                await _db.SaveChangesAsync(ct);
            }

            result = result with
            {
                ManifestsUnchanged = result.ManifestsUnchanged + (unchanged ? 1 : 0),
                EntriesTotal = result.EntriesTotal + rows.Count,
                EntriesRewritten = result.EntriesRewritten + (unchanged ? 0 : rows.Count),
                EntriesApproved = result.EntriesApproved + rows.Count(r => r.Approved),
                EntriesStale = result.EntriesStale + (isStale ? rows.Count : 0),
            };

            if (isStale)
                _log.LogWarning(
                    "擷取物 {Manifest} 已過期（{Reason}），其 {Count} 段一律排除於檢索之外。" +
                    "請重跑擷取並重新審核。", name, staleReason, rows.Count);
        }

        // manifest 被刪掉的，索引也要跟著消失——否則 Q&A 會繼續引用一份已經沒有人維護的規則說明。
        var orphans = await _db.DocChunks
            .Where(c => c.Corpus == DocCorpus.Extract && !keptFilePaths.Contains(c.FilePath))
            .Select(c => c.FilePath)
            .Distinct()
            .ToListAsync(ct);

        if (orphans.Count > 0)
        {
            await _db.DocChunks
                .Where(c => c.Corpus == DocCorpus.Extract && orphans.Contains(c.FilePath))
                .ExecuteDeleteAsync(ct);

            result = result with { ManifestsRemoved = orphans.Count };
        }

        _log.LogInformation(
            "擷取物匯入完成：manifest {Scanned} 份（移除 {Removed} 份、沿用 {Unchanged} 份）、" +
            "共 {Total} 段（重寫 {Rewritten} 段），其中已核可 {Approved} 段、過期 {Stale} 段。",
            result.ManifestsScanned, result.ManifestsRemoved, result.ManifestsUnchanged,
            result.EntriesTotal, result.EntriesRewritten,
            result.EntriesApproved, result.EntriesStale);

        return result;
    }

    private static async Task<ExtractManifest> ReadAsync(string file, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(file, ct);

        var manifest = JsonSerializer.Deserialize<ExtractManifest>(json, JsonOptions)
            ?? throw new InvalidDataException($"擷取物 manifest 解析結果為 null：{file}");

        Validate(file, manifest);
        return manifest;
    }

    /// <summary>
    /// 格式檢查**一律拋例外，不略過**。
    ///
    /// <para>這裡擋的每一項壞掉之後都不會有錯誤，只會安靜地少答或答錯：
    /// slug 不合法會產出一個永遠對不上的引文頁碼；語系寫錯會讓那一段對兩種語系的使用者都消失
    /// （§10.4 是硬過濾）；空白內文會佔一列卻永遠檢索不到。</para>
    /// </summary>
    private static void Validate(string file, ExtractManifest m)
    {
        var name = Path.GetFileName(file);

        if (m.SourcePath.Length == 0)
            throw new InvalidDataException($"{name}：sourcePath 不可為空——沒有來源就無法判斷過期。");

        if (!Sha256Pattern.IsMatch(m.SourceHash))
            throw new InvalidDataException(
                $"{name}：sourceHash 必須是 64 字小寫十六進位，實得「{m.SourceHash}」。");

        if (m.Entries.Count == 0)
            throw new InvalidDataException($"{name}：entries 為空。要停用整份請刪檔，不要留一份空的。");

        var slugs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in m.Entries)
        {
            if (!SlugPattern.IsMatch(e.Slug))
                throw new InvalidDataException(
                    $"{name}：slug「{e.Slug}」不合法，只允許小寫英數與連字號（它會變成引文頁碼）。");

            // 同一份裡 slug 重複＝兩段共用一個引文頁碼，使用者點進去只會看到其中一段。
            if (!slugs.Add(e.Slug))
                throw new InvalidDataException($"{name}：slug「{e.Slug}」重複。");

            if (e.Lang is not ("zh" or "en"))
                throw new InvalidDataException($"{name}：slug「{e.Slug}」的 lang 必須是 zh 或 en，實得「{e.Lang}」。");

            if (e.Text.Trim().Length == 0)
                throw new InvalidDataException($"{name}：slug「{e.Slug}」的 text 為空。");
        }
    }

    /// <summary>
    /// 過期判定（§10.11.3）。**兩種情況都算過期**：來源檔內容變了，或來源檔根本找不到。
    ///
    /// <para>後者容易被寫成「找不到就跳過檢查」，那等於讓「檔案被改名或刪掉」變成
    /// 悄悄放行——而那正是最需要擋下來的一種來源變動。</para>
    /// </summary>
    private async Task<(bool IsStale, string Reason)> EvaluateStaleAsync(
        string? root, ExtractManifest manifest, string manifestFile, CancellationToken ct)
    {
        if (root is null)
            return (true, "找不到 repo 根目錄，無法驗證來源");

        var source = Path.Combine(root, manifest.SourcePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(source))
            return (true, $"來源檔不存在：{manifest.SourcePath}");

        var actual = Sha256(await File.ReadAllTextAsync(source, ct));
        if (!string.Equals(actual, manifest.SourceHash, StringComparison.OrdinalIgnoreCase))
            return (true, $"來源檔已變動（manifest 記 {Short(manifest.SourceHash)}，現檔 {Short(actual)}）");

        _log.LogDebug("擷取物 {Manifest} 的來源 {Source} 未變動。",
            Path.GetFileName(manifestFile), manifest.SourcePath);
        return (false, string.Empty);
    }

    private List<DocChunk> BuildRows(string filePath, ExtractManifest manifest, bool isStale)
    {
        var now = DateTime.UtcNow;   // R17／R22：*At 欄位存 UTC 瞬時，非業務日

        return manifest.Entries
            .Select((e, i) => new DocChunk
            {
                Corpus = DocCorpus.Extract,
                FilePath = filePath,
                Lang = e.Lang,
                ChunkIndex = i,

                // 擷取物的 ContentHash 算的是它自己這一段的文字，SourceHash 算的是它衍生自的檔案。
                // 兩個欄位分開，才能區分「說明被改寫了」與「原文變了」這兩件不同的事。
                ContentHash = Sha256(e.Text),
                HeadingPath = Truncate(e.HeadingPath, 400),

                // 擷取物不綁畫面（§10.11.5），故 MenuKey 與 Module 一律為空。
                // 權限走的是 Corpus=extract 那條分支，不會落到 MenuKey 的四個 OR 上。
                MenuKey = string.Empty,
                Module = string.Empty,

                DocUrl = Truncate($"{RulesUrlRoot}/{e.Slug}.html", 300),
                Text = e.Text.Trim(),
                // 標題與內文分欄切詞：詞彙通道給標題較高的權重（待決 #48）。
                // 擷取物的「標題」是那一則規則的標題路徑，專有名詞的密度比手冊麵包屑更高。
                Lexemes = CjkTokenizer.Tokenize(e.Text),
                HeadingLexemes = CjkTokenizer.Tokenize(e.HeadingPath),

                SourcePath = Truncate(manifest.SourcePath, 300),
                SourceHash = manifest.SourceHash.ToLowerInvariant(),
                Approved = e.Approved,
                IsStale = isStale,
                IndexedAt = now,
            })
            .ToList();
    }

    /// <summary>
    /// 這一份 manifest 建出來的列，與庫裡現有的列**每一個持久化欄位都相同**嗎？
    ///
    /// <para>相同就跳過刪除與寫入，於是該份的 <c>Embedding</c>／<c>EmbeddingModel</c>／
    /// <c>EmbeddingDim</c> 原封不動——那正是這個比對存在的唯一理由。</para>
    ///
    /// <para><b>刻意不比 <c>Id</c> 與 <c>IndexedAt</c>：</b>前者是資料庫給的，
    /// 後者每一次匯入都不一樣，拿它比等於永遠判定「變了」。
    /// <b>也刻意不比嵌入那三欄</b>：它們不是 manifest 的內容，而是這個比對要保住的東西。</para>
    ///
    /// <para>⚠️ 加欄位到 <c>DocChunk</c> 時要回來決定它算不算「內容變了」。
    /// 忘記的話症狀是「改了 manifest 卻沒生效」，而且不會有錯誤——
    /// 防線是 <c>DocChunkPersistedFieldsFacts</c>，它把欄位清單釘死，新增欄位就紅。</para>
    /// </summary>
    private static bool SameAsIndexed(IReadOnlyList<DocChunk> indexed, IReadOnlyList<DocChunk> incoming)
    {
        if (indexed.Count != incoming.Count) return false;

        for (var i = 0; i < incoming.Count; i++)
        {
            var a = indexed[i];
            var b = incoming[i];

            if (a.Corpus != b.Corpus ||
                a.FilePath != b.FilePath ||
                a.Lang != b.Lang ||
                a.ChunkIndex != b.ChunkIndex ||
                a.ContentHash != b.ContentHash ||
                a.HeadingPath != b.HeadingPath ||
                a.Module != b.Module ||
                a.MenuKey != b.MenuKey ||
                a.DocUrl != b.DocUrl ||
                a.Text != b.Text ||
                a.Lexemes != b.Lexemes ||
                a.HeadingLexemes != b.HeadingLexemes ||
                a.SourcePath != b.SourcePath ||
                a.SourceHash != b.SourceHash ||
                a.Approved != b.Approved ||
                a.IsStale != b.IsStale ||
                !a.ImagePaths.SequenceEqual(b.ImagePaths))
                return false;
        }

        return true;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];

    private static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
