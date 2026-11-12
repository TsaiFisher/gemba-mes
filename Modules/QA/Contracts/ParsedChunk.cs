namespace SmartFactory.MES.Modules.QA.Contracts;

/// <summary>
/// 手冊頁切塊後的一塊（規格 §10.2）。這是**解析結果**，還不是資料庫實體——
/// 檔案路徑、內容 hash、MenuKey 等由索引建置端補上（§10.3）。
///
/// 刻意是 <c>readonly record struct</c> 而非 <c>record</c>：R5b 掃 <c>Modules.*</c> 下所有
/// public class 的隱含必填值型別屬性並要求對應的多語鍵，<c>ChunkIndex</c> 這種內部欄位
/// 沒有任何畫面會顯示它，為它造一個假的欄位名鍵只會污染資源檔（同 <c>ReportScreenBinding</c> 的處理）。
/// </summary>
/// <param name="Lang">"zh" 或 "en"。**這是檢索的硬過濾條件，不是排序參考**（§10.4）。</param>
/// <param name="ChunkIndex">同一頁同一語系內的順序，自 0 起。</param>
/// <param name="HeadingPath">
/// 麵包屑（「交期達成率 › 欄位說明」）。同時是引文標題，也會併入詞彙索引——
/// 專有名詞多半出現在標題而非內文，§10.2-2 說「這對專有名詞檢索的幫助大於切塊粒度本身」。
/// </param>
/// <param name="Text">已正規化的純文字（單一語系）。</param>
/// <param name="Anchor">
/// 這一塊起始 <c>&lt;h2&gt;</c> 的 <c>id</c>，供手冊搜尋把使用者送到**段落**而不是頁首。
/// 空字串代表跳頁首：頁面開頭到第一個 h2 之前的那一塊沒有標題可錨定，
/// 而整頁併成一塊時（<c>Parse</c> 的短頁門檻）錨點也不成立——那一塊涵蓋整頁，
/// 指向其中任何一個 h2 都是錯的。<c>ParseSections</c> 不做合併，每一塊都錨得住。
/// </param>
/// <param name="ImagePaths">
/// 可直接放進 <c>&lt;img src&gt;</c> 的網站絕對路徑。英文塊存的是 <c>data-src-en</c> 解析後的路徑，
/// 且**已做過檔案存在與否的 fallback**——英文截圖允許缺漏（頁面本身靠 onerror 退回中文圖），
/// 索引若存一個不存在的檔名，答覆會出現破圖而不是任何錯誤。
/// </param>
public readonly record struct ParsedChunk(
    string Lang,
    int ChunkIndex,
    string HeadingPath,
    string Text,
    IReadOnlyList<string> ImagePaths,
    string Anchor = "");
