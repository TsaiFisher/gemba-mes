using System.Security.Claims;

namespace SmartFactory.MES.Modules.RPS.Contracts;

/// <summary>
/// 報表工具契約（§5.1）。把目前散在各 Controller 私有 <c>BuildQueryAsync</c> 的查詢邏輯
/// 抽成可注入的服務，供**畫面、排程、未來的 LLM** 三方共用——不得複製邏輯，否則必然漂移。
///
/// ⚠️ <paramref name="user"/> **不會改變資料過濾行為**。廠別與資料層級過濾是由
/// <c>ApplicationDbContext</c> 的 global query filter 讀取 <c>CurrentPlantId</c>／
/// <c>CurrentRoleId</c>／<c>CurrentRolePath</c> 生效，那三個值來自執行 scope。
/// 傳 <c>ClaimsPrincipal</c> 只影響工具內部的顯式檢查。排程等無 HTTP 上下文的路徑
/// 必須在 scope 內顯式設定那三個屬性，否則**會靜默查到 0 筆**（§5.5 T2）。
/// </summary>
public interface IReportTool<in TRequest, TResponse>
    where TRequest : ReportRequestBase
{
    /// <summary>唯一鍵，對齊該報表的 MenuKey，如 <c>"SalesRanking"</c>。</summary>
    string ToolKey { get; }

    /// <summary>供未來 LLM function-calling schema 使用的描述。</summary>
    string Description { get; }

    Task<TResponse> ExecuteAsync(TRequest request, ClaimsPrincipal? user, CancellationToken ct = default);
}

/// <summary>
/// 目錄登錄用的非泛型面。<see cref="ReportToolCatalog"/> 以此列舉工具而不需知道泛型參數。
/// </summary>
public interface IReportToolDescriptor
{
    string ToolKey { get; }
    string Description { get; }

    /// <summary>
    /// 執行本工具所需的權限。**必須逐一對齊該報表畫面現行的實際檢查**，不可套統一慣例。
    ///
    /// 2026-08-14 裁決後 30 支報表的畫面閘門**恰好都是** <c>CanRead</c>
    /// （原本 7 支的 <c>CanCreate</c> 已改掉），但這個枚舉刻意保留其他成員、
    /// 也刻意不給預設值：「照抄畫面」是規則，「目前全都是 CanRead」只是現況。
    /// 一旦有畫面另立閘門，這裡必須跟著改，而不是反過來假設全站統一。
    /// </summary>
    ReportToolPermission Permission { get; }

    /// <summary>
    /// 本工具的輸出是否含金額。**這不是權限，也不改變任何存取判定。**
    ///
    /// <para>§14 定案 #2 已明言「機密**不用動詞表達**，分級由 <c>MenuPermission</c> 資料承載」——
    /// 那條仍然成立且未被本屬性取代：誰看得到這支報表，答案永遠只在權限資料裡。</para>
    ///
    /// <para>本屬性回答的是**另一個問題**，只有自然語言入口才問得到：
    /// §十三 Phase 2 明定「機密等級最高的報表（銷售金額、毛利、成本）**第一版排除在 NL 目錄外**，
    /// 只留畫面入口」。有權限的人照樣能開畫面，差別只在能不能用一句話問到它。
    /// 而 §8.5 的彙總洩漏正是 NL 入口獨有的風險：金額型報表用自然語言連問幾次就能逼近個別數字，
    /// 走畫面要一步步點，走 NL 是一句話。</para>
    ///
    /// <para>⚠️ 刻意**不給預設值**，與 <see cref="Permission"/> 同一個理由：
    /// 新工具的作者必須明確回答，而不是繼承一個「剛好安全」的預設值。
    /// 判定準則是可查證的事實而非主觀分級——**回傳型別裡有沒有金額欄位**
    /// （§八「含金額＝機密」，母體見 <c>report-permission-inventory.md</c>）。</para>
    /// </summary>
    bool ContainsMonetaryValues { get; }

    /// <summary>
    /// 本工具是否需要期間參數才能執行。
    ///
    /// <para><b>為什麼排程非知道這件事不可</b>：<c>RelativePeriodResolver</c> 在
    /// <c>Period</c> 與 <c>AbsolutePeriod</c> 都沒給時會**拋例外**，而排程表單若不曉得
    /// 哪些報表要期間，就只有兩個都不對的選擇——一律必填（不吃期間的報表上長出一個
    /// 永遠不生效的欄位），或一律不填（13 支吃期間的報表排程出去全部失敗）。
    /// 2026-09-17 實測：排程表單當時把 <c>ParamsJson</c> 寫死成 <c>{}</c>，
    /// 於是吃期間的 13 支直接 <c>Failed</c>、不吃期間的 4 支安靜地產出零筆。</para>
    ///
    /// <para>⚠️ 刻意<b>不給預設值</b>，與 <see cref="Permission"/>／
    /// <see cref="ContainsMonetaryValues"/> 同一個理由：新工具的作者必須明確回答。
    /// 判定準則是可查證的事實——<b>實作裡有沒有呼叫 <c>IRelativePeriodResolver</c></b>。
    /// 已登錄畫面繫結的工具另由 <c>ReportToolPeriodContractTests</c> 與
    /// <c>ReportScreenBindings.UsesDateRange</c> 對帳，防止兩處各說各話。</para>
    /// </summary>
    bool RequiresPeriod { get; }

    Type RequestType { get; }
    Type ResponseType { get; }

    /// <summary>
    /// 匯出／排程用的**整份**資料列（不分頁）。
    ///
    /// <para><b>為什麼要有這個成員</b>：排程與匯出只拿得到一個 <c>ToolKey</c> 字串，
    /// 必須有辦法把 <see cref="ResponseType"/> 化約成「一列一列的資料」。原先的做法是
    /// 由 <c>ReportToolRunner</c> 直接把回應當 <c>IEnumerable</c> 攤平，於是回傳
    /// <b>包裝型別</b>的工具（如 <c>InventoryValuationResult(Page, GrandTotal)</c>）
    /// 會落進「不是集合」那一支而**靜默產出空白報表**——沒有例外、沒有日誌，
    /// 收件匣裡是一份格式正確但零筆的檔案。2026-09-14 Wave R 實測撞到。</para>
    ///
    /// <para>改由各工具自己回答之後，「資料列在哪」不再靠猜。新工具的作者必須明確實作，
    /// 而不是繼承一個碰巧對一半的預設行為。</para>
    ///
    /// <para>⚠️ <b>分頁契約</b>：本方法一律回整份。<see cref="ReportRequestBase.PageNumber"/>／
    /// <see cref="ReportRequestBase.PageSize"/> 是畫面的概念，排程輸出的是完整報表——
    /// 實作時不可沿用畫面路徑的分頁預設值，否則排程會安靜地只寄出第一頁。</para>
    ///
    /// <para>⚠️ <b>查不到資料與拒絕執行必須分得開</b>：因為超出掃描上限之類的原因無法產出時，
    /// 請<b>拋例外</b>（訊息會進 <c>ReportRun.ErrorMessage</c>），不可回空集合——
    /// 空集合的語意是「條件下確實沒有資料」，兩者在收件匣裡長得一模一樣。</para>
    /// </summary>
    Task<IReadOnlyList<object>> GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct = default);

    /// <summary>
    /// 匯出的欄位：順序、標題資源鍵、取值。**畫面匯出與排程匯出共用這一份**（§16.1 S-5）。
    ///
    /// <para><b>為什麼要有這個成員</b>：在此之前，排程匯出的標題是
    /// <c>ReportToolRunner.Flatten</c> 以反射取的**屬性名**（`ProductCode`、`OverdueDays`…），
    /// 而畫面匯出的標題是各 Controller 的 <c>Export</c> 動作裡硬編的雙語陣列。
    /// 同一張報表因此有兩種長相，且**兩邊都「正確」只是不一樣**——沒有任何測試會發現。
    /// R-7（收件人語系）更把這件事推到荒謬：派工器認真地依收件人語系產出兩份檔，
    /// 而兩份檔的欄位標題都是英文屬性名。</para>
    ///
    /// <para><b>為什麼是方法不是屬性</b>：欄位可能依請求而變。`SalesRanking` 的第二欄
    /// 隨 <c>GroupBy</c> 在「客戶」與「品項」之間切換——靜態清單表達不了。</para>
    ///
    /// <para><b>鍵的命名是「工具.欄位」</b>（如 <c>PurchaseOverdue.OrderQty</c>）而非共用
    /// <c>ProductCode</c> 這種泛用鍵：同一個概念在不同報表的用詞本來就不同
    /// （庫存量／在手量、訂購量／採購量），共用鍵會讓改一張報表的用詞**默默改掉另一張**。
    /// 鍵住在 <c>ProjectZReportHeaderResource</c>，與選單用語和驗證欄位名都分家，
    /// 理由同 <c>ProjectZValidationResource</c> 的分家理由。</para>
    ///
    /// <para>⚠️ 回傳的順序就是欄序，且**必須與 <see cref="GetExportRowsAsync"/> 回傳的
    /// 每一列對得上**——取值是逐欄的 <c>Func</c>，所以對不上就是拿錯欄位的值，
    /// 不是少一欄。實作請直接照抄該報表畫面現行的匯出欄位，不要順手增刪。</para>
    /// </summary>
    IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request);
}

/// <summary>
/// 匯出的一欄：標題資源鍵 ＋ 從一列資料取出該欄的值。
///
/// <para>做成 <c>record</c> 而非兩個平行陣列（標題一組、取值一組）是刻意的：
/// 平行陣列會因為其中一邊多加或漏加一個元素而整排錯位，而錯位的匯出檔
/// **看起來完全正常**——每一欄都有值，只是值屬於隔壁那一欄。</para>
/// </summary>
/// <param name="HeaderKey">`ProjectZReportHeaderResource` 的鍵，格式為「工具.欄位」。</param>
/// <param name="ValueOf">從一列資料取出該欄的值；<c>null</c> 代表該格留白。</param>
public sealed record ReportExportColumn(string HeaderKey, Func<object, object?> ValueOf);

/// <summary>
/// <see cref="IReportToolDescriptor.GetExportColumns"/> 的實作輔助：讓工具用自己的列型別
/// 寫取值運算式，不必在每一欄手動轉型。
/// </summary>
public static class ReportExportColumnFactory
{
    /// <summary>宣告一欄。<typeparamref name="TRow"/> 是該工具匯出列的型別。</summary>
    public static ReportExportColumn Column<TRow>(string headerKey, Func<TRow, object?> valueOf)
        => new(headerKey, row => valueOf((TRow)row));
}

/// <summary>
/// <see cref="IReportToolDescriptor.GetExportRowsAsync"/> 的實作輔助：把基底請求轉回
/// 工具自己的請求型別。<c>ReportToolRunner</c> 依 <see cref="IReportToolDescriptor.RequestType"/>
/// 反序列化，正常情況下型別必定相符；轉型失敗代表工具宣告的 <c>RequestType</c> 與它實作的
/// <c>IReportTool&lt;,&gt;</c> 不一致，訊息必須<b>指名道姓</b>說出是哪一支、預期什麼、拿到什麼。
/// </summary>
public static class ReportRequestCastExtensions
{
    public static TRequest AsToolRequest<TRequest>(this ReportRequestBase request, string toolKey)
        where TRequest : ReportRequestBase
        => request as TRequest
           ?? throw new InvalidOperationException(
               $"工具 {toolKey} 收到的請求型別是 {request.GetType().Name}，預期 {typeof(TRequest).Name}。" +
               "請檢查該工具的 RequestType 宣告是否與它實作的 IReportTool<,> 相符。");
}

/// <summary>
/// 工具的權限宣告動詞。刻意不提供「預設值」——每支工具都必須明寫，
/// 因為統一成 <c>CanRead</c> 正是 §8.1 點名的錯誤做法。
/// </summary>
public enum ReportToolPermission
{
    CanRead,
    CanCreate,
    CanPrint,
}
