using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Data;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.QA.Services;

/// <summary>手冊詞彙檢索（規格 §10.5、§10.10）。S1 只有這一個通道，S2 起與語意通道以 RRF 融合。</summary>
public interface IManualSearchService
{
    /// <summary>
    /// 以目前登入者的身分檢索手冊。
    /// </summary>
    /// <param name="question">使用者的提問原文。</param>
    /// <param name="topK">最多回傳幾塊。</param>
    /// <param name="langOverride">指定語系；null 時依提問文字判定，判不出來才用 UI 語系。</param>
    Task<IReadOnlyList<DocSearchHit>> SearchAsync(
        string question, int topK = 5, string? langOverride = null, CancellationToken ct = default);
}

/// <summary>
/// <para><b>三層過濾，順序不可調換：</b></para>
/// <list type="number">
///   <item><b>語系硬過濾</b>（§10.4）。不是排序參考——multilingual embedding 會讓
///     「Sales Ranking」與「銷售排名」高度相似，交給模型分辨的結果是英文使用者拿到中文引文。</item>
///   <item><b>權限過濾</b>（§10.10）。每則提問**一次**取得使用者的可讀 MenuKey 集合，
///     然後在 SQL 的 WHERE 裡過濾——不是取回結果後再篩。差別在 <c>LIMIT</c>：
///     先 LIMIT 再篩會讓沒權限的塊佔掉名額，使用者拿到的結果數莫名其妙變少。</item>
///   <item><b>詞彙比對</b>。中文走重疊雙字 ＋ <c>simple</c> 組態，英文走 <c>english</c>（§10.5）。</item>
/// </list>
///
/// <para><b>權限判定與 <see cref="IPermissionService.CanRead"/> 必須等價。</b>
/// 這裡不呼叫它是為了避免每則提問 N 次查詢（既有缺口 G1），但語意刻意保持一致：
/// 「目前使用中的角色在 <c>MenuPermissions</c> 有該鍵且 <c>CanRead</c>」。
/// <c>PermissionService</c> 沒有 Admin 旁路，這裡也不能有。</para>
/// </summary>
public class ManualSearchService : IManualSearchService
{
    /// <summary>
    /// 每個通道取多少候選進融合。只取 <c>topK</c> 的話，兩個通道各自的第 6 名
    /// 就算在對方是第 1 名也進不了融合——RRF 會退化成「兩份 Top-5 的聯集」，
    /// 失去互補的意義。取 4 倍（下限 20）在 563 塊的語料上成本可忽略。
    /// </summary>
    private const int CandidateDepthFactor = 4;
    private const int MinCandidateDepth = 20;

    private readonly QaDbContext _qa;
    private readonly ApplicationDbContext _db;
    private readonly IUserContext _userContext;
    private readonly IEmbeddingClient _embedding;
    private readonly IDocVectorStore _vectors;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ManualSearchService> _log;

    public ManualSearchService(
        QaDbContext qa, ApplicationDbContext db, IUserContext userContext,
        IEmbeddingClient embedding, IDocVectorStore vectors, IConfiguration configuration,
        ILogger<ManualSearchService> log)
    {
        _qa = qa;
        _db = db;
        _userContext = userContext;
        _embedding = embedding;
        _vectors = vectors;
        _configuration = configuration;
        _log = log;
    }

    public async Task<IReadOnlyList<DocSearchHit>> SearchAsync(
        string question, int topK = 5, string? langOverride = null, CancellationToken ct = default)
    {
        if (topK <= 0) return Array.Empty<DocSearchHit>();

        // 判不出語系（純數字、純標點）才退回 UI 語系——§10.4：以提問文字為準。
        var lang = langOverride ?? CjkTokenizer.DetectLang(question) ?? HelpDocResolver.CurrentDocLang();
        var config = CjkTokenizer.ConfigFor(lang);

        // 索引期與查詢期共用同一個切詞函式。兩邊切法不一致的結果是永遠對不上，
        // 而且完全不會報錯——只是每一次檢索都回 0 筆（§10.5）。
        // OR 而非 AND：plainto_tsquery 會要求命中每一個詞，包含提問的語氣詞
        // （「怎麼」「麼看」），結果是每一則自然語言提問都回 0 筆。詳見 CjkTokenizer.ToOrTsQuery。
        var tsQuery = CjkTokenizer.ToOrTsQuery(CjkTokenizer.Tokenize(question));

        var (allowedKeys, allowedModules) = await ResolveReadableAsync(ct);
        var depth = Math.Max(topK * CandidateDepthFactor, MinCandidateDepth);

        // 一則提問只讀一次，再傳給兩個通道——各讀各的會讓同一次檢索的兩條路用不同條件。
        var crossLangExtract = _configuration.GetValue(ExtractCrossLangKey, true);
        var extractSlots = ExtractSlots();

        var lexical = tsQuery.Length == 0
            // 切不出任何詞（純數字、純標點）時詞彙通道無事可做，但語意通道還可以——
            // 這裡不再像 S1 那樣直接回空。
            ? new List<DocChunkHit>()
            : await LexicalAsync(lang, config, tsQuery, allowedKeys, allowedModules,
                                 crossLangExtract, depth, ct);

        var semantic = await SemanticAsync(question, lang, allowedKeys, allowedModules,
                                           crossLangExtract, depth, ct);

        // 語意通道不可用時退回純詞彙（S1 的行為）。SemanticScore 留 null 而不是 0——
        // 「服務不可用」與「不相似」是兩件事，混為一談會讓 §10.8 的門檻誤判。
        if (semantic is null)
            return lexical
                .Take(topK)
                .Select((r, i) => Hit(r, i + 1, r.Score, r.Score, null))
                .ToList();

        // 分艙時把融合結果切得比 topK 深——只切 topK 的話兩艙都只能從那 topK 個裡挑，
        // 手冊補不滿、擷取物也選不到第 N 段，分艙等於沒做。
        var fused = Combine(
            lexical.Select(r => r.Id).ToList(),
            semantic.Select(v => v.ChunkId).ToList(),
            extractSlots is null ? topK : depth);

        if (fused.Count == 0) return Array.Empty<DocSearchHit>();

        var lexicalById = lexical.ToDictionary(r => r.Id);
        var semanticById = semantic.ToDictionary(v => v.ChunkId, v => v.Score);

        // 只出現在語意通道的塊沒有欄位資料，補一次查詢。563 列的表，
        // 為此設計一套「兩個通道都回全欄位」的機制不划算。
        var missing = fused.Select(f => f.ChunkId).Where(id => !lexicalById.ContainsKey(id)).ToArray();
        if (missing.Length > 0)
            foreach (var row in await HydrateAsync(missing, ct))
                lexicalById[row.Id] = row;

        var kept = fused.Where(f => lexicalById.ContainsKey(f.ChunkId)).ToList();
        if (extractSlots is { } slots)
            kept = Partition(kept, lexicalById, topK, slots);

        return kept
            .Select((f, i) => Hit(
                lexicalById[f.ChunkId],
                i + 1,
                f.Score,
                lexicalById[f.ChunkId].Score,
                semanticById.TryGetValue(f.ChunkId, out var s) ? s : null))
            .ToList();
    }

    /// <summary>
    /// **擷取物與手冊共不共用同一個 <c>topK</c> 名額**（§17.9 缺口 G5）。
    ///
    /// <para>空／未設定＝<b>共用</b>（原行為）：<c>topK</c> 個名額由兩個語料別一起競爭。
    /// 設成整數 <c>N</c>＝<b>分艙</b>：手冊仍取 <c>topK</c> 塊，擷取物<b>另外</b>最多 <c>N</c> 段。</para>
    ///
    /// <para><b>為什麼需要分艙</b>（2026-08-27 實測，§10.11.15）：擷取物穩定拿走約四成名額，
    /// 而跨頁題天生要好幾格——擷取物不佔名額時跨頁題全中從 <b>4/12 升到 8/12</b>。
    /// 單頁題的 Recall@5 不受影響（34 對 34），所以這不是「擷取物有害」，
    /// 是<b>「一個固定大小的名額池對需要多格的提問不公平」</b>。</para>
    ///
    /// <para>🚨 <b>做出來了，量完之後的結論是「不要打開」</b>（2026-08-27，§10.11.15）。
    /// <b>它輸給一個本來就存在的旋鈕：直接加大 <c>topK</c>。</b>
    /// 兩者要到同樣的跨頁題 7/12，分艙 <c>N=5</c> 要 4201 字（35.1 秒），
    /// <c>topK=8</c> 只要 3516 字（29.4 秒）——**規則題、直白 @1、@5 三欄完全相同**，
    /// 分艙貴 5.7 秒且多一套程式。</para>
    ///
    /// <para><b>為什麼會輸，回頭看分診就知道</b>：漏掉的正解頁排在第 6／7／8／12／14／17／18／28／43 名，
    /// <b>前三題就卡在名額邊界上</b>。所以問題從來不是「誰佔了名額」而是「只看 5 名」——
    /// 「擷取物擠掉手冊」是相關不是因果。分艙只是一種比較貴的看深方法，
    /// 貴在它強迫多出來的深度只能給手冊，而手冊塊大七倍。</para>
    ///
    /// <para>⚠️ <b>成本原本被我估錯一個數量級，記在這裡免得有人重蹈</b>：
    /// 原估「多給 3 段約 300 字 ≒ 0.2 秒」只算了新增的擷取物（97 字），
    /// <b>沒算到分艙同時把原本被小塊佔住的 5 個主名額換成大塊手冊</b>（647 字）。
    /// 實測 2526 → 3886 字＝<b>+11.4 秒</b>；連 <c>N=0</c> 都比共用多 820 字。</para>
    ///
    /// <para><b>那為什麼還留著：</b>掃描測試要靠這個旋鈕才重跑得出對照
    /// （<c>ManualCrossPageDiagnosisTests</c>），而本專案的量測必須是可重跑的程式、
    /// 不是一次性的手工紀錄。<b>它是量具，不是功能。</b></para>
    ///
    /// <para>⚠️ 值設不出數字時<b>退回共用並出聲</b>——安靜地忽略設定，
    /// 症狀會是「我明明分艙了跨頁題還是答不全」（§17.8）。</para>
    /// </summary>
    public const string ExtractSlotsKey = "Features:QaAssistant:ExtractSlots";

    /// <summary>
    /// **擷取物分支是否略過語系比對**（§17.9 缺口 G4）。**預設 <c>true</c>**（2026-08-27 裁決）。
    ///
    /// <para><b>它只放寬擷取物那一邊，手冊永遠比語系</b>——理由見
    /// <c>DocChunkFilter.ExtractLangSql</c>。開啟之後英文提問會拿到<b>中文的</b>規則段落，
    /// 由生成模型用英文作答；引文頁仍然是中文的 <c>rules/*</c>。</para>
    ///
    /// <para><b>預設值是量出來的</b>（<c>ManualCrossLangExtractTests</c>，§10.11.15）：
    /// 英文規則題命中 <b>0/6 → 6/6</b>；英文手冊題 Recall 與 MRR 兩臂完全相同、
    /// 擷取物只佔 3/30 格（中文是 48%）。⚠️ <b>代價那一側的尺已經飽和</b>
    /// （英文手冊題只有 6 題、兩臂都 6/6、MRR 都 1.000），
    /// 所以「沒退步」的證據力有限——唯一沒飽和的證據是佔格數。
    /// 英文題庫擴大之後應該重跑一次。</para>
    ///
    /// <para>⚠️ <b>開啟不會讓詞彙通道跟著有東西。</b>英文提問走的是 <c>english</c> regconfig，
    /// 對中文的 <c>Lexemes</c> 切不出任何命中，所以擷取物只會經由**語意通道**進來。
    /// 語意通道不可用時（§10.7 的降級路徑）這個開關等於沒開，而那時不會有任何錯誤訊息。</para>
    /// </summary>
    public const string ExtractCrossLangKey = "Features:QaAssistant:ExtractCrossLang";

    /// <summary>
    /// 兩個通道的合併方式（<c>Features:QaAssistant:RetrievalMode</c>）。
    ///
    /// <para><b>預設是 <c>semantic-first</c> 而不是 RRF，這是實測改的</b>（2026-08-06，30 題題庫，
    /// 塊層級，見 §10.9 S2-c）：</para>
    /// <list type="bullet">
    ///   <item>純詞彙 Recall@1 <b>18/30</b>、MRR 0.748</item>
    ///   <item>純語意 Recall@1 <b>30/30</b>、MRR 1.000</item>
    ///   <item>RRF 融合 Recall@1 <b>27/30</b>、MRR 0.944 ← <b>比純語意差</b></item>
    /// </list>
    /// <para>掉的三題全是「詞彙通道把正解排在第 4／5／9 名」的情形，RRF 把那個錯誤名次
    /// 帶進來，於是別的塊被推上第 1。而**沒有任何一題是詞彙勝過語意的**——
    /// 連 <c>NCR</c>／<c>OEE</c>／<c>StockShortage</c> 這些原本認為「專有名詞要靠詞彙」的案例，
    /// 語意通道也都是第 1 名。</para>
    ///
    /// <para><b>但不因此刪掉詞彙通道</b>：它在推論服務不可用時是唯一的檢索方式，
    /// 而且 30 題是很小的樣本、題目又是自己出的。這裡的取捨是
    /// **實測到的代價 > 假設中的好處**，不是「詞彙檢索沒有用」。
    /// 語料或題庫長大後想改回 RRF，把這個鍵設成 <c>rrf</c> 再跑一次評測即可——
    /// 那時會有數據，現在沒有。</para>
    /// </summary>
    public const string RetrievalModeKey = "Features:QaAssistant:RetrievalMode";
    public const string ModeRrf = "rrf";

    private IReadOnlyList<(int ChunkId, double Score)> Combine(
        IReadOnlyList<int> lexicalIds, IReadOnlyList<int> semanticIds, int topK)
    {
        if (string.Equals(_configuration[RetrievalModeKey], ModeRrf, StringComparison.OrdinalIgnoreCase))
            return ReciprocalRankFusion.Fuse(topK, ReciprocalRankFusion.DefaultK, lexicalIds, semanticIds);

        // semantic-first：語意名次為主，詞彙只在語意湊不滿 topK 時補位。
        // 補位幾乎不會發生（語意通道一次取 topK×4 個候選），它是安全網不是排序機制。
        var ordered = new List<int>(semanticIds);
        var seen = new HashSet<int>(semanticIds);
        foreach (var id in lexicalIds)
            if (seen.Add(id)) ordered.Add(id);

        // 分數用 1/名次：只為了讓「分數遞減」這件事成立，
        // 它不是相似度——真正可用於門檻的是 DocSearchHit.SemanticScore。
        return ordered.Take(topK).Select((id, i) => (id, 1.0 / (i + 1))).ToList();
    }

    /// <summary>
    /// **排序後分艙**：手冊最多 <paramref name="topK"/> 塊、擷取物最多 <paramref name="extractSlots"/> 段。
    ///
    /// <para><b>原本的名次順序完全不動</b>，只是把超出各自配額的丟掉——
    /// 重新排序會讓「分艙」順帶變成「改排序」，兩個變因混在一起就量不出是哪一個造成的。</para>
    ///
    /// <para><b>不互相補位</b>：手冊只有 3 塊時，擷取物**不會**多拿那 2 格。
    /// 補位會讓「擷取物最多 N 段」在冷門提問上悄悄失效，而那正是稀釋最嚴重的一類。</para>
    /// </summary>
    private static List<(int ChunkId, double Score)> Partition(
        List<(int ChunkId, double Score)> ordered,
        IReadOnlyDictionary<int, DocChunkHit> byId, int topK, int extractSlots)
    {
        var manual = 0;
        var extract = 0;
        var kept = new List<(int ChunkId, double Score)>(topK + extractSlots);

        foreach (var f in ordered)
        {
            if (string.Equals(byId[f.ChunkId].Corpus, DocCorpus.Extract, StringComparison.Ordinal))
            {
                if (extract >= extractSlots) continue;
                extract++;
            }
            else
            {
                if (manual >= topK) continue;
                manual++;
            }

            kept.Add(f);
            if (manual >= topK && extract >= extractSlots) break;
        }

        return kept;
    }

    /// <summary>
    /// 解析分艙配額。**設不出數字就退回共用並出聲**，不丟例外——理由同 <see cref="LexicalWeights"/>：
    /// 這是排序參數，設錯的正確行為是退回原排序，而不是讓整個問答功能掛掉。
    /// </summary>
    private int? ExtractSlots()
    {
        var raw = _configuration[ExtractSlotsKey];
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0)
            return n;

        _log.LogWarning(
            "{Key} 的值 {Raw} 不是非負整數，本次改用共用名額（原行為）。", ExtractSlotsKey, raw);
        return null;
    }

    private static DocSearchHit Hit(DocChunkHit r, int rank, double score, double lexical, double? semantic) =>
        new(Rank: rank,
            ChunkId: r.Id,
            Lang: r.Lang,
            HeadingPath: r.HeadingPath,
            Text: r.Text,
            DocUrl: r.DocUrl,
            MenuKey: r.MenuKey,
            ImagePaths: r.ImagePaths,
            Score: score,
            LexicalScore: lexical,
            SemanticScore: semantic);

    /// <summary>
    /// <c>ts_rank</c> 的權重陣列，順序是 PostgreSQL 規定的 <c>{D, C, B, A}</c>
    /// ——本系統只用到兩端：<b>D＝內文、A＝標題</b>，B 與 C 沒有對應的來源，留著只是為了湊滿四格。
    ///
    /// <para><b>預設是等重（<c>1,1,1,1</c>），也就是「標題加權這個能力做好了但預設不開」。
    /// 這是實測改的，不是保守</b>（2026-08-26，<c>ManualLexicalWeightSweepTests</c>，35 題，
    /// 手冊 686 塊＋核可擷取物 1065 段，見 §10.9）：</para>
    /// <list type="bullet">
    ///   <item>等重　　Recall@1 18／@3 29／<b>@5 32</b>／MRR 0.669／掉出前 10 名 <b>1 題</b></item>
    ///   <item>標題 1.5 倍　@1 24／@3 30／@5 31／MRR 0.775／掉出前 10 名 2 題</item>
    ///   <item>標題 10 倍（<b>PostgreSQL 預設</b>）　@1 16／@3 21／<b>@5 23</b>／MRR 0.535／掉出前 10 名 <b>11 題</b></item>
    /// </list>
    ///
    /// <para><b>逐題看才知道加權在交易什麼：它拿口語題換直述題。</b>
    /// 「派工作業怎麼報工」「MRP 執行要怎麼跑」這種說得出功能名稱的提問，加權後一律升到第 1 名；
    /// 而「哪些客戶買最多」「倉庫之間要移轉庫存怎麼做」「不良品要報廢或重工怎麼處理」
    /// 這種**使用者不知道功能正式名稱**的提問，加權越重掉得越兇，10 倍時全部掉出前 10 名。
    /// 機制很直接：口語的用字只出現在內文，不出現在標題；壓低內文權重就是把這一類整個犧牲掉。
    /// 而題庫刻意大量收錄的正是這一類——「那才是使用者真正會打的字」。</para>
    ///
    /// <para><b>判準是 @5 不是 @1。</b>詞彙通道只在語意通道不可用時當唯一通道用，
    /// 那時 topK 塊會全部進 prompt——正解排第 1 還是第 5 對模型沒有差別，
    /// 掉出 topK 才是答不出來。~~照這個判準等重最好（@5 32、掉出 1 題）。~~</para>
    ///
    /// <para>🔄 <b>2026-09-24：預設值改為 1.5 倍</b>（<c>0.667,0.75,0.85,1.0</c>）。
    /// 上面那段文字自己寫著「改之前請重跑那支掃描——上面每一個數字都綁在『當下這一份語料』上，
    /// 而語料還在長」。語料從 686 塊長到 <b>863 塊</b>（＋26%）之後重跑，結論翻轉：</para>
    ///
    /// <list type="bullet">
    ///   <item>等重　　@1 21／@3 39／<b>@5 45</b>／MRR 0.590／掉出前 10 名 2 題</item>
    ///   <item><b>1.5 倍</b>　@1 29／@3 41／<b>@5 48</b>／MRR 0.701／掉出前 10 名 2 題</item>
    ///   <item>2 倍　　@1 31／@3 38／@5 45／MRR 0.702／掉出前 10 名 2 題</item>
    /// </list>
    ///
    /// <para><b>依本節自己訂的判準（@5 與掉出前 10 名）1.5 倍現在勝</b>：@5 48 vs 45，
    /// 掉出題數同為 2。2 倍的 @1 更高但 @5 掉回 45——它拿 @5 換 @1，方向與判準相反。</para>
    ///
    /// <para><b>⚠️ 分語言驗過，這次沒有跨語言取捨</b>（2026-08-31 擋下 1.5 倍的正是這一點，
    /// 且當時留下規矩：<b>題庫的「合計」列不可用於語言之間互相取捨的調校決定</b>）：</para>
    ///
    /// <list type="bullet">
    ///   <item>中文 29 題：@1 12→17、@3 23→24、<b>@5 26→26（持平）</b></item>
    ///   <item>英文 23 題：@1 9→12、@3 16→17、<b>@5 19→22</b></item>
    /// </list>
    ///
    /// <para>六格裡五格改善、一格持平，<b>沒有一格變差</b>。2026-08-31 擋下它的理由是
    /// 「中文 @5 等重勝 26 vs 25」——語料長大之後那一格變成平手，取捨消失了。
    /// @5 的增益全部來自英文，而中文毫髮無傷。</para>
    ///
    /// <para><b>為什麼語料長大會讓加權變有利</b>：這正是上面「拿口語題換直述題」那段的另一面。
    /// 語料每長一批，新頁面只要含有同樣的常用詞（「畫面」「設定」「客戶」）就會把正解擠下去，
    /// 而那些常用詞出現在<b>內文</b>不在標題。壓低內文權重，壓掉的正是這批雜訊。
    /// 語料越大，標題作為「這一頁到底在講什麼」的訊號就越值錢。</para>
    ///
    /// <para>⚠️ 同樣的話再說一次給下一個人：<b>改之前請重跑 `ManualLexicalWeightSweepTests`</b>。
    /// 上面每一個數字都綁在 863 塊這一份語料上，而語料還在長。</para>
    ///
    /// <para>⚠️ <b>不要因為「PostgreSQL 的預設值就是 0.1,0.2,0.4,1.0」而改回去。</b>
    /// 那組值是為了英文文件的 title／body／abstract 標記調的，
    /// 在這份語料上它是所有候選裡最差的一組——擷取物的「標題」是規則標題，
    /// 專有名詞密度遠高於手冊麵包屑，把標題加重等於把 1065 段規則整批推到手冊前面。</para>
    /// </summary>
    public const string LexicalWeightsKey = "Features:QaAssistant:LexicalWeights";
    private static readonly float[] DefaultLexicalWeights = { 0.667f, 0.75f, 0.85f, 1.0f };

    /// <summary>
    /// 解析權重陣列。**格式不對就用預設值並出聲**，不丟例外：
    /// 這是一個排序參數，設錯的正確行為是退回預設排序，而不是讓整個問答功能掛掉。
    /// 但一定要出聲——安靜地忽略設定，症狀會是「我明明調了權重卻沒有任何變化」（§17.8）。
    /// </summary>
    private float[] LexicalWeights()
    {
        var raw = _configuration[LexicalWeightsKey];
        if (string.IsNullOrWhiteSpace(raw)) return DefaultLexicalWeights;

        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 ||
            !parts.All(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            _log.LogWarning(
                "{Key} 的值 {Raw} 不是四個以逗號分隔的數字（順序 D,C,B,A），本次改用預設權重。",
                LexicalWeightsKey, raw);
            return DefaultLexicalWeights;
        }

        return parts.Select(p => float.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
    }

    /// <summary>
    /// 詞彙通道。語系與權限的條件與語意通道**共用同一份** SQL 片段（<c>DocChunkFilter</c>）。
    ///
    /// <para><b>標題與內文分別 <c>setweight</c> 之後再合成一個 tsvector</b>（待決 #48）。
    /// 兩者平鋪成一串時「畫面」「設定」「客戶」這種常用詞一多，正解就被新頁面擠下去，
    /// 而且**語料每長一批就再掉一截**——那不是排序程式碼的回歸，是稀釋。</para>
    ///
    /// <para>合成式寫在 <c>LATERAL</c> 裡而不是在 <c>SELECT</c> 與 <c>WHERE</c> 各寫一次：
    /// 兩處要完全相同，否則會出現「排序用的向量」與「決定命不命中的向量」不一致——
    /// 那種錯誤查得出結果、只是結果不對。</para>
    /// </summary>
    private async Task<List<DocChunkHit>> LexicalAsync(
        string lang, string config, string tsQuery,
        string[] allowedKeys, string[] allowedModules, bool crossLangExtract,
        int depth, CancellationToken ct)
    {
        // Id 作為第二排序鍵：同分時的順序必須穩定，否則同一個問題兩次問會拿到不同引文。
        var sql = $$"""
            SELECT c."Id", c."Lang", c."HeadingPath", c."DocUrl", c."MenuKey", c."Corpus", c."Text", c."ImagePaths",
                   ts_rank({6}, v.tsv, q.tsq) AS "Score"
            FROM "DocChunks" AS c
            CROSS JOIN LATERAL (
                SELECT to_tsquery(CAST({3} AS regconfig), {4}) AS tsq
            ) AS q
            CROSS JOIN LATERAL (
                SELECT setweight(to_tsvector(CAST({3} AS regconfig), c."HeadingLexemes"), 'A')
                    || setweight(to_tsvector(CAST({3} AS regconfig), c."Lexemes"), 'D') AS tsv
            ) AS v
            WHERE {{DocChunkFilter.LangAndPermission(crossLangExtract)}}
              AND v.tsv @@ q.tsq
            ORDER BY "Score" DESC, c."Id"
            LIMIT {5}
            """;

        return await _qa.Set<DocChunkHit>()
            .FromSqlRaw(sql, lang, allowedKeys, allowedModules, config, tsQuery, depth, LexicalWeights())
            .ToListAsync(ct);
    }

    /// <summary>
    /// 語意通道。**不可用時回 <c>null</c>，不是空清單**——空清單的意思是「查無相似內容」，
    /// 而這裡要表達的是「這個通道當下不存在」。兩者混用會讓呼叫端以為手冊裡沒有答案。
    ///
    /// <para>不可用是**降級不是錯誤**：詞彙通道照常運作，使用者仍拿得到答案，
    /// 只是品質退回 S1 的水準。但一定要出聲（§17.8），否則症狀只是「答得沒有以前準」。</para>
    /// </summary>
    private async Task<IReadOnlyList<VectorHit>?> SemanticAsync(
        string question, string lang, string[] allowedKeys, string[] allowedModules,
        bool crossLangExtract, int depth, CancellationToken ct)
    {
        if (_embedding.EmbeddingModelName.Length == 0) return null;

        try
        {
            var vector = await _embedding.EmbedAsync(new[] { question }, EmbeddingKind.Query, ct);
            return await _vectors.SearchAsync(
                vector[0], lang, allowedKeys, allowedModules, crossLangExtract, depth, ct);
        }
        catch (OllamaException ex)
        {
            _log.LogWarning(ex, "語意檢索不可用，本次提問退回純詞彙檢索。");
            return null;
        }
        catch (DocIndexOutOfDateException ex)
        {
            // 換了嵌入模型但索引還沒重嵌完。重啟後背景服務會補上，期間退回詞彙檢索。
            _log.LogWarning(ex, "手冊索引的向量與現行模型不符，本次提問退回純詞彙檢索。");
            return null;
        }
    }

    /// <summary>補齊只出現在語意通道的塊的欄位。</summary>
    private async Task<List<DocChunkHit>> HydrateAsync(int[] ids, CancellationToken ct) =>
        await _qa.Set<DocChunkHit>()
            .FromSqlRaw("""
                SELECT c."Id", c."Lang", c."HeadingPath", c."DocUrl", c."MenuKey", c."Corpus", c."Text", c."ImagePaths",
                       0::float8 AS "Score"
                FROM "DocChunks" AS c
                WHERE c."Id" = ANY({0})
                """, ids)
            .ToListAsync(ct);

    /// <summary>
    /// 目前角色可讀的選單鍵，以及由此推得的模組集合——**一次查詢**（§10.10 對缺口 G1 的處理）。
    ///
    /// 模組集合的用途是模組目錄頁（<c>ord/index.html</c> 這類 <c>MenuKey</c> 為空的頁面）：
    /// 它們列出整個模組有哪些畫面，對該模組毫無權限的人不該檢索得到——
    /// 那是系統功能結構的洩漏。手冊首頁（<c>Module</c> 也為空）只有通用 FAQ，維持公開。
    /// </summary>
    private async Task<(string[] Keys, string[] Modules)> ResolveReadableAsync(CancellationToken ct)
    {
        var roleId = _userContext.ActiveRoleId;
        if (string.IsNullOrEmpty(roleId))
            return (Array.Empty<string>(), Array.Empty<string>());

        var keys = await _db.MenuPermissions
            .AsNoTracking()
            .Where(p => p.RoleId == roleId && p.CanRead)
            .Select(p => p.MenuKey)
            .ToArrayAsync(ct);

        var modules = keys
            .Select(HelpDocResolver.ModuleOf)
            .Where(m => !string.IsNullOrEmpty(m))
            .Select(m => m!)
            .Distinct()
            .ToArray();

        return (keys, modules);
    }
}
