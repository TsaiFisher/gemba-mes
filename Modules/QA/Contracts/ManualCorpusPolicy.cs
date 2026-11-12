namespace SmartFactory.MES.Modules.QA.Contracts;

/// <summary>
/// 手冊語料的收錄政策常數（規格 §10.10）。
///
/// <para>之所以獨立成一個 public 型別、而不是留在
/// <c>DocChunkFilter</c>（internal）裡：這份清單是**守門測試必須讀得到的知識**。
/// 測試自己抄一份字面值的話，兩份會漂移，而漂移的方向恰好是「測試仍綠、實際檢索不到」。</para>
/// </summary>
public static class ManualCorpusPolicy
{
    /// <summary>
    /// 沒有對應 MVC 畫面、但仍應開放檢索的手冊目錄（<c>DocChunk.Module</c> 的值）。
    ///
    /// <para><b>為什麼需要這份白名單：</b><c>Module</c> 取自手冊的目錄名，
    /// <c>MenuKey</c> 則由 <see cref="SmartFactory.MES.Services.HelpDocResolver"/> 反查 controller 而來。
    /// 手機 PWA 的畫面是靜態 HTML、沒有 controller，於是 <c>mobile/</c> 底下每一頁都是
    /// 「<c>MenuKey</c> 空、<c>Module</c> 非空」——落在權限過濾的「模組目錄頁」分支，
    /// 而該分支比對的可讀模組集合是由使用者的 MenuKey 推得的，
    /// <b>永遠不可能包含 <c>mobile</c></b>。結果是手機說明索引得進去、
    /// 卻對每一個使用者都檢索不到，而且完全沒有訊號。</para>
    ///
    /// <para><b>為什麼是白名單而不是「反查不到 controller 就公開」：</b>後者會讓日後任何
    /// 新增的手冊目錄自動變成公開內容——包含不該公開的。這裡要的是顯式列舉：
    /// 新目錄要嘛替它的畫面登錄 controller，要嘛明白寫進這份清單，
    /// 兩者都沒做就會被 <c>ManualReachabilityTests</c> 擋下來。</para>
    ///
    /// <para><b>手機說明適合公開的理由：</b>它講的是現場人員的操作步驟，
    /// 而手機的功能可見性走的是另一套權限（<c>MobileModule</c>／<c>ModuleAction</c>／
    /// 角色手機權限），根本無法用 <c>MenuPermissions</c> 的鍵表達。
    /// 要做到逐畫面對齊手機權限，得替 <c>DocChunk</c> 另立欄位與第二個過濾維度——
    /// 那是另一個量級的改動，且在手機說明只有操作步驟、不含資料的前提下並不划算。</para>
    /// </summary>
    /// <para><b>為什麼是集合而不是單一常數：</b>教學系統（<c>tutor/</c>）會是第二個同類目錄——
    /// 靜態 HTML、無 controller、內容是操作步驟而非資料。第二個成員出現時再改型別，
    /// 會同時牽動 SQL 片段與三處守門斷言；先開成集合，之後只是多一個字串。</para>
    ///
    /// <para><b>成員必須是小寫英數字</b>：它會被字面拼進 SQL（見
    /// <c>DocChunkFilter.LangAndPermission</c>），且 <c>DocChunk.Module</c> 是
    /// <c>varchar(10)</c>。兩項都由 <c>ManualReachabilityTests</c> 斷言。</para>
    /// <para><b><c>tutor</c>（操作教學）適合公開的理由：</b>它的內容是「手冊裡已經有的操作步驟，
    /// 換成逐步演示」——素材全部衍生自手冊本身，沒有任何一個字是教學獨有的，
    /// 也不含任何營運資料。課程總覽會列出全系統有哪些作業流程，
    /// 但那與手冊首頁（既有的公開分支 2）列出的是同一份資訊，不構成新的結構洩漏。</para>
    public static readonly IReadOnlyList<string> PublicModules = new[] { "mobile", "tutor" };
}
