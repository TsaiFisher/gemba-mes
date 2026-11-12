namespace SmartFactory.MES
{
    /// <summary>
    /// 報表匯出的「欄位標題」資源標記類別（§16.1 S-5）。
    ///
    /// <para>鍵的格式是 <c>工具.欄位</c>（如 <c>PurchaseOverdue.OrderQty</c>），
    /// 由各工具的 <c>GetExportColumns</c> 宣告，畫面匯出與排程匯出共用同一份。</para>
    ///
    /// <para><b>為什麼再分一份資源檔</b>，而不是塞進 <see cref="ProjectZSharedResource"/>
    /// 或 <see cref="ProjectZValidationResource"/>：三者的語意不同且會撞名。
    /// <c>DefectRate</c> 在選單資源是「不良率報表」、在驗證資源是欄位名「不良率」；
    /// 報表標題要的是第三種東西（某一張報表的某一欄的欄頭）。混在一起的結果不是編譯錯誤，
    /// 是匯出檔的欄頭印出一個選單標題——分家的理由與 <c>ProjectZValidationResource</c> 相同。</para>
    ///
    /// <para><b>為什麼鍵要帶工具名而不共用泛用鍵</b>：同一個概念在不同報表的用詞本來就不同
    /// （庫存量／在手量、訂購量／採購量、客戶／客戶代號）。共用一個 <c>ProductCode</c> 鍵，
    /// 改一張報表的用詞會**默默改掉另一張**，而那種改動沒有人會去覆驗。</para>
    ///
    /// <para>由 <c>ReportExportHeaderKeysTests</c> 守門：每一支已登錄工具宣告的每一個鍵，
    /// 都必須在 zh-TW 與 en-US 兩份都有值。</para>
    /// </summary>
    public class ProjectZReportHeaderResource { }
}
