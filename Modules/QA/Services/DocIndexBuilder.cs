using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Data;
using SmartFactory.MES.Modules.QA.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.QA.Services;

/// <summary>索引建置結果。<c>readonly record struct</c>：見 <c>ParsedChunk</c> 對 R5b 的說明。</summary>
public readonly record struct DocIndexResult(
    int FilesScanned,
    int FilesReindexed,
    int FilesRemoved,
    int ChunksWritten,
    int ChunksDeleted,
    int PagesWithoutMenuKey);

/// <summary>把 <c>wwwroot/docs/user</c> 的手冊建成 <see cref="DocChunk"/> 索引（規格 §10.3）。</summary>
public interface IDocIndexBuilder
{
    /// <summary>
    /// 增量重建：只重做內容 hash 有變的檔案，並清掉已不存在的檔案的列。
    /// </summary>
    Task<DocIndexResult> RebuildAsync(CancellationToken ct = default);

    /// <summary>
    /// 把還沒有向量、或向量出自別的模型的塊補嵌入（規格 §10.7）。
    ///
    /// <para><b>與 <see cref="RebuildAsync"/> 分開是刻意的</b>：詞彙通道不需要任何模型，
    /// 推論服務掛掉時它必須照常運作。兩者綁在一起的話，Ollama 沒開會讓整個手冊索引建不起來——
    /// 那是把選用相依變成必要相依。</para>
    /// </summary>
    /// <returns>(已嵌入的塊數, 仍待嵌入的塊數)。後者 &gt; 0 代表中途失敗或被取消。</returns>
    Task<(int Embedded, int Pending)> EmbedPendingAsync(CancellationToken ct = default);
}

/// <summary>
/// <para><b>增量的依據是整份 HTML 的 SHA256，不是 mtime，也不是人工版號</b>（§10.3）。
/// mtime 會被 <c>git checkout</c>／部署複製任意改動，人工版號的宿命是沒人記得 bump；
/// 兩者的失敗方向相反但一樣糟——前者每次部署都全量重建（S2 起要重跑 embedding，很貴），
/// 後者永遠不重建（答案停留在舊版手冊，而且看起來很有自信）。</para>
///
/// <para><b>索引建置刻意不吞例外。</b>單一頁面解析失敗就整批中止，
/// 因為「略過那一頁」的後果是該畫面在 Q&amp;A 中永久消失，而且沒有任何訊號——
/// 使用者只會覺得助理不知道這個功能。寧可整批失敗、留下例外，也不要部分成功。</para>
/// </summary>
public class DocIndexBuilder : IDocIndexBuilder
{
    /// <summary>
    /// **索引格式版號**——摻進 <see cref="DocChunk.ContentHash"/> 的雜湊輸入，不是內容的一部分。
    ///
    /// <para>增量重建的判斷依據是「這個檔的 HTML 變了沒有」。<b>但 HTML 沒變、
    /// 這支程式建列的方式變了的時候，增量重建會一列都不重做</b>——舊列留著舊格式，
    /// 而唯一的症狀是排序沒有照預期改善。2026-08-26 把 <c>Lexemes</c> 拆成
    /// 標題／內文兩欄（待決 #48）就正好是這種變更：不加這個版號的話，
    /// 既有的手冊列會全部帶著空的 <c>HeadingLexemes</c>，標題權重完全不生效。</para>
    ///
    /// <para>這與本類別上方否定的「人工 <c>DocVersion</c>」不同，兩者管的是不同的東西：
    /// 那個要人跟著**內容**改而人不會記得；這個跟著**這支程式**改，
    /// 而改建列邏輯的人正在讀這個檔案。忘記 bump 的防線是
    /// <c>ManualIndexIntegrationTests.每一塊都要有可檢索的詞</c>——
    /// 它斷言有標題的列必須有 <c>HeadingLexemes</c>，舊格式的列會讓它紅。</para>
    /// </summary>
    private const string IndexFormatVersion = "2026-08-26-heading-lexemes";

    /// <summary>
    /// 手冊根目錄（相對於 wwwroot）。與 <see cref="HelpDocResolver"/> 的路徑慣例對齊。
    ///
    /// <para><b>2026-08-31 使用者裁決：<c>docs/admin</c> 與 <c>docs/maintenance</c> 不納入語料。</b>
    /// 那兩份的讀者是 IT 與系統管理者、內容是指令與部署路徑（備份還原、遷移、疑難排解），
    /// 與本助理服務的對象不同；而它們既沒有對應 controller，也就沒有 <c>MenuKey</c> 可過濾，
    /// 收進來只能走 <see cref="Contracts.ManualCorpusPolicy.PublicModules"/> 那條公開分支
    /// ——等於對所有登入者公開伺服器路徑與維運指令。
    /// <b>代價要講清楚</b>：這兩份手冊的內容，助理一律答不出來，
    /// 且症狀與「手冊裡本來就沒寫」完全一樣（§17.9 的 G4 是同一個形狀）。</para>
    /// </summary>
    private const string ManualRelativeRoot = "docs/user";
    private const string ManualUrlRoot = "/docs/user";

    private readonly QaDbContext _db;
    private readonly IManualDocParser _parser;
    private readonly IWebHostEnvironment _env;
    private readonly IEmbeddingClient _embedding;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DocIndexBuilder> _log;

    public DocIndexBuilder(
        QaDbContext db, IManualDocParser parser, IWebHostEnvironment env,
        IEmbeddingClient embedding, IConfiguration configuration, ILogger<DocIndexBuilder> log)
    {
        _db = db;
        _parser = parser;
        _env = env;
        _embedding = embedding;
        _configuration = configuration;
        _log = log;
    }

    /// <summary>
    /// 同時只允許一次重建。
    ///
    /// 兩個重建併行跑同一批檔案時，各自「先刪該檔的列、再插新列」會交錯，
    /// 撞 <c>(FilePath, Lang, ChunkIndex)</c> 唯一鍵而其中一邊整個失敗。
    /// 實際會發生的場景不只理論：啟動時的背景服務與手動觸發同時進行就是。
    ///
    /// <b>這是行程內的鎖，不涵蓋多實例部署。</b>目前本系統是單一實例；
    /// 日後若要多實例，這裡要換成 PostgreSQL 的 advisory lock，
    /// 而不是把這個 semaphore 拿掉了事。
    /// </summary>
    private static readonly SemaphoreSlim RebuildGate = new(1, 1);

    public async Task<DocIndexResult> RebuildAsync(CancellationToken ct = default)
    {
        await RebuildGate.WaitAsync(ct);
        try
        {
            return await RebuildCoreAsync(ct);
        }
        finally
        {
            RebuildGate.Release();
        }
    }

    private async Task<DocIndexResult> RebuildCoreAsync(CancellationToken ct)
    {
        var root = Path.Combine(_env.WebRootPath, ManualRelativeRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"找不到使用手冊目錄：{root}");

        var onDisk = Directory
            .EnumerateFiles(root, "*.html", SearchOption.AllDirectories)
            .ToDictionary(RelativeOf, f => f, StringComparer.OrdinalIgnoreCase);

        // 既有索引的「每檔一個 hash」。同一檔的所有列 hash 取自同一份 HTML 故必然相同，
        // Max 只是把 GroupBy 投影成單一值的手段。
        //
        // 單一檔案的「刪舊列 → 寫新列」不在同一個交易內，但這不需要修：中途失敗會讓該檔
        // 一列都不剩，於是下一輪掃描找不到它的 hash，整檔重建——失敗方向是自我修復的。
        var indexed = await ManualRows()
            .GroupBy(c => c.FilePath)
            .Select(g => new { FilePath = g.Key, Hash = g.Max(x => x.ContentHash) })
            .ToDictionaryAsync(x => x.FilePath, x => x.Hash, StringComparer.OrdinalIgnoreCase, ct);

        var result = new DocIndexResult(onDisk.Count, 0, 0, 0, 0, 0);
        var noMenuKey = new List<string>();

        foreach (var (relative, fullPath) in onDisk.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            var html = await File.ReadAllTextAsync(fullPath, ct);
            var hash = Sha256(html);

            if (indexed.TryGetValue(relative, out var existing) && existing == hash) continue;

            var deleted = await ManualRows()
                .Where(c => c.FilePath == relative)
                .ExecuteDeleteAsync(ct);

            var menuKey = HelpDocResolver.ResolveController(relative) ?? string.Empty;
            if (menuKey.Length == 0) noMenuKey.Add(relative);

            var rows = BuildRows(relative, html, hash, menuKey);
            _db.DocChunks.AddRange(rows);
            await _db.SaveChangesAsync(ct);

            result = result with
            {
                FilesReindexed = result.FilesReindexed + 1,
                ChunksWritten = result.ChunksWritten + rows.Count,
                ChunksDeleted = result.ChunksDeleted + deleted,
            };
        }

        // 手冊頁被刪除時，索引也要跟著消失——否則 Q&A 會引用一個 404 的連結。
        var stale = indexed.Keys.Where(k => !onDisk.ContainsKey(k)).ToList();
        if (stale.Count > 0)
        {
            var removed = await ManualRows()
                .Where(c => stale.Contains(c.FilePath))
                .ExecuteDeleteAsync(ct);

            result = result with
            {
                FilesRemoved = stale.Count,
                ChunksDeleted = result.ChunksDeleted + removed,
            };
        }

        result = result with { PagesWithoutMenuKey = noMenuKey.Count };

        _log.LogInformation(
            "手冊索引重建完成：掃描 {Scanned} 檔、重建 {Reindexed} 檔、移除 {Removed} 檔、" +
            "寫入 {Written} 塊、刪除 {Deleted} 塊。",
            result.FilesScanned, result.FilesReindexed, result.FilesRemoved,
            result.ChunksWritten, result.ChunksDeleted);

        return result;
    }

    /// <summary>
    /// 一次送幾塊給 <c>/api/embed</c>。批次太小會被每次請求的固定成本吃掉，
    /// 太大則讓「中途失敗」浪費掉更多已完成的工作。開發機實測 8 塊約 3 秒，取 16 為預設。
    /// </summary>
    public const string BatchSizeKey = "Features:QaAssistant:EmbedBatchSize";
    private const int DefaultBatchSize = 16;

    public async Task<(int Embedded, int Pending)> EmbedPendingAsync(CancellationToken ct = default)
    {
        var model = _embedding.EmbeddingModelName;
        if (model.Length == 0)
        {
            // §17.8：降級要出聲。沒設模型時語意通道整個不存在，而使用者只會覺得「答得不太準」。
            _log.LogWarning(
                "未設定 {Key}，手冊索引不會產生向量，Q&A 只有詞彙檢索可用。",
                OllamaClient.EmbeddingModelKey);
            return (0, await PendingQuery(model).CountAsync(ct));
        }

        var configuredBatch = _configuration.GetValue<int?>(BatchSizeKey);
        var batchSize = configuredBatch is > 0 ? configuredBatch.Value : DefaultBatchSize;
        var embedded = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // 每輪重新查詢而不是一次撈完 Id 清單：整批 563 塊要跑數分鐘，
            // 期間可能有另一次重建改動了資料列。以「還缺什麼」為準最單純，
            // 而且中途失敗後下一次啟動會自然接續——不需要任何進度紀錄。
            var batch = await PendingQuery(model)
                .OrderBy(c => c.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            // 索引期用 Passage、查詢期用 Query。兩邊用錯前綴不會報錯，只會讓檢索默默變差（§10.7）。
            var vectors = await _embedding.EmbedAsync(
                batch.Select(c => $"{c.HeadingPath} {c.Text}").ToList(), EmbeddingKind.Passage, ct);

            for (var i = 0; i < batch.Count; i++)
            {
                batch[i].Embedding = vectors[i];
                batch[i].EmbeddingModel = model;
                batch[i].EmbeddingDim = vectors[i].Length;
            }

            await _db.SaveChangesAsync(ct);
            embedded += batch.Count;
        }

        return (embedded, 0);
    }

    /// <summary>
    /// 待嵌入的塊：沒有向量的，以及**向量出自別的模型的**。
    /// 後者是換模型時的重嵌來源——少了它，換模型後索引會永遠停在舊向量，
    /// 而 <see cref="DocVectorStore"/> 會因此一直拒絕檢索。
    ///
    /// <para><b>刻意不依 <c>Corpus</c> 過濾，也不排除未核可的擷取物</b>（§10.11）：
    /// 這裡是本系統唯一的重嵌入口，漏掉哪一種語料的後果就是那一半永遠停在舊向量，
    /// 而 <c>DocVectorStore</c> 的一致性檢查會把整個語意通道一起擋掉。
    /// 未核可的塊先嵌好也是對的——核可是一個布林值的翻轉，不該連帶觸發一次數分鐘的重嵌；
    /// 而未核可期間它被 <c>DocChunkFilter</c> 擋在檢索之外，多一份向量沒有任何外洩風險。</para>
    /// </summary>
    private IQueryable<DocChunk> PendingQuery(string model) =>
        _db.DocChunks.Where(c => c.Embedding == null || c.EmbeddingModel != model);

    /// <summary>
    /// 本建置器的作用範圍＝**只有手冊語料**（§10.11）。
    ///
    /// <para>少了這個條件，<see cref="RebuildAsync"/> 的「先刪該檔全部列」會把擷取物一起刪掉：
    /// 擷取物的 <c>FilePath</c> 若與某個手冊頁重疊就當場消失，而即使不重疊，
    /// 「掃描不到的檔案就刪」那一段也會把**每一列擷取物**判為已刪除的手冊頁——
    /// 因為它們的來源檔本來就不在 <c>wwwroot/docs/user</c> 底下。
    /// 那是一次靜默的全語料清空，而下一次重建不會把它們補回來（重建只讀手冊）。</para>
    /// </summary>
    private IQueryable<DocChunk> ManualRows() =>
        _db.DocChunks.Where(c => c.Corpus == DocCorpus.Manual);

    private List<DocChunk> BuildRows(string relative, string html, string hash, string menuKey)
    {
        var urlDir = $"{ManualUrlRoot}/{PosixDirOf(relative)}".TrimEnd('/');
        var docUrl = $"{ManualUrlRoot}/{relative}";
        var module = PosixDirOf(relative).Trim('/');
        var now = DateTime.UtcNow;   // R17／R22：*At 欄位存 UTC 瞬時，非業務日

        return _parser.Parse(urlDir, html, AssetExists)
            .Select(c => new DocChunk
            {
                // 明寫而不倚賴屬性預設值：這一行是 ManualRows() 的作用範圍所依據的唯一事實，
                // 預設值日後被改動時，錯誤會出現在離這裡很遠的地方。
                Corpus = DocCorpus.Manual,
                FilePath = relative,
                Lang = c.Lang,
                ChunkIndex = c.ChunkIndex,
                ContentHash = hash,
                HeadingPath = Truncate(c.HeadingPath, 400),
                Module = Truncate(module, 10),
                MenuKey = menuKey,
                DocUrl = Truncate(docUrl, 300),
                Text = c.Text,

                // 麵包屑**分欄**進詞彙索引：專有名詞多半出現在標題而非內文（§10.2-2），
                // 分開存才有辦法在 ts_rank 給它較高的權重（待決 #48）。
                // 切詞一定要走 CjkTokenizer——查詢期用的是同一個方法，
                // 兩邊切法不一致的結果是永遠對不上，而且不會有任何錯誤訊息（§10.5）。
                Lexemes = CjkTokenizer.Tokenize(c.Text),
                HeadingLexemes = CjkTokenizer.Tokenize(c.HeadingPath),

                ImagePaths = c.ImagePaths.ToList(),
                IndexedAt = now,
            })
            .ToList();
    }

    /// <summary>網站絕對路徑 → 該靜態檔是否存在。英文截圖缺漏時的 fallback 依據。</summary>
    private bool AssetExists(string webPath)
    {
        if (!webPath.StartsWith('/')) return false;
        var relative = webPath[1..].Replace('/', Path.DirectorySeparatorChar);
        return File.Exists(Path.Combine(_env.WebRootPath, relative));
    }

    private string RelativeOf(string fullPath) =>
        Path.GetRelativePath(Path.Combine(_env.WebRootPath, ManualRelativeRoot), fullPath)
            .Replace('\\', '/');

    private static string PosixDirOf(string relative)
    {
        var idx = relative.LastIndexOf('/');
        return idx < 0 ? string.Empty : relative[..idx];
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>整份 HTML ＋ <see cref="IndexFormatVersion"/> 的雜湊。版號在前，兩者以換行分隔。</summary>
    private static string Sha256(string content) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{IndexFormatVersion}\n{content}")))
        .ToLowerInvariant();
}
