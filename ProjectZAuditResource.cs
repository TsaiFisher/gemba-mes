namespace SmartFactory.MES
{
    /// <summary>
    /// 資料稽核發現描述的資源標記類別。鍵名見 <see cref="Modules.ACT.Services.AuditMessages"/>。
    ///
    /// <para><b>為什麼再分一份資源檔</b>（同 <see cref="ProjectZReportHeaderResource"/> 的理由）：
    /// 這些是**整句**，不是欄位名或選單標題。塞進 <see cref="ProjectZSharedResource"/> 會與
    /// 欄位名撞名，且混在一起之後沒有辦法針對「稽核訊息」單獨做完整性守門。</para>
    ///
    /// <para><b>有中性 resx（無語系後綴）</b>：產生稽核發現的是背景排程，
    /// 它的 <c>CurrentUICulture</c> 是 invariant——沒有中性資源時 <c>IStringLocalizer</c>
    /// 會回傳鍵名本身，於是 <c>ErrorDescription</c> 會變成「PoOverReceipt」這種東西存進資料庫。</para>
    /// </summary>
    public class ProjectZAuditResource { }
}
