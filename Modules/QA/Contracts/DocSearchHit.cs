namespace SmartFactory.MES.Modules.QA.Contracts;

/// <summary>
/// 一筆手冊檢索結果（規格 §10.5）。<c>readonly record struct</c>：見 <c>ParsedChunk</c> 對 R5b 的說明。
/// </summary>
/// <param name="Rank">
/// 名次，自 1 起。**刻意暴露名次而非相似度分數**：S2 要以 Reciprocal Rank Fusion
/// 融合語意與詞彙兩個通道，RRF 用的就是名次（1/(k+rank)）而不是分數——
/// 兩個通道的分數尺度不可比，硬要正規化只會引入一組沒有依據的魔術參數。
/// </param>
/// <param name="ChunkId">來源 <c>DocChunk.Id</c>，供 RRF 對齊兩個通道的同一塊。</param>
/// <param name="Lang">"zh" 或 "en"。與提問語系必然相同（硬過濾，§10.4）。</param>
/// <param name="HeadingPath">麵包屑，直接作為引文標題。</param>
/// <param name="Text">chunk 全文，供組 prompt 使用。</param>
/// <param name="DocUrl">引文連結（英文答覆需另加 <c>?lang=en</c>，由呈現層處理）。</param>
/// <param name="MenuKey">對應畫面的選單鍵；空字串代表無對應畫面的目錄／首頁。</param>
/// <param name="ImagePaths">可直接放進 <c>&lt;img src&gt;</c> 的路徑，已依語系解析。</param>
/// <param name="Score">
/// 最終排序依據。兩個通道都在時是 **RRF 融合分數**（見 <c>ReciprocalRankFusion</c>），
/// 只有詞彙通道時是 <c>ts_rank</c>。**跨查詢不可比**——RRF 分數的量級只由名次決定，
/// 用它訂相關度門檻沒有意義，那要用 <see cref="SemanticScore"/>。
/// </param>
/// <param name="LexicalScore"><c>ts_rank</c> 原始分數；<c>0</c> 代表詞彙通道沒有命中這一塊。</param>
/// <param name="SemanticScore">
/// 餘弦相似度。<b><c>null</c> 代表語意通道當下不可用</b>（未設嵌入模型、推論服務連不上、
/// 索引尚未嵌入），**不是「不相似」**——這兩件事混為一談的話，§10.8 的門檻會把
/// 「服務掛了」當成「查無相關說明」，而使用者看到的訊息會完全誤導。
/// </param>
public readonly record struct DocSearchHit(
    int Rank,
    int ChunkId,
    string Lang,
    string HeadingPath,
    string Text,
    string DocUrl,
    string MenuKey,
    IReadOnlyList<string> ImagePaths,
    double Score,
    double LexicalScore = 0,
    double? SemanticScore = null);
