namespace SmartFactory.MES
{
    /// <summary>
    /// ProjectZ 顧問模組的「驗證欄位名」資源標記類別。
    ///
    /// 專供 Core 驗證在地化鏈式查找的 fallback 使用（BASModule 以
    /// <c>AddValidationResourceFallback&lt;ProjectZValidationResource&gt;()</c> 登記），
    /// 內容是模組 Model/ViewModel 的屬性名 → 中英欄位名，讓驗證訊息的 {0} 隨語系顯示。
    ///
    /// 刻意與 <see cref="ProjectZSharedResource"/> 分家：後者是選單／畫面用語，
    /// 兩者有同名但語意不同的鍵（如 DefectRate＝「不良率」欄位 vs「不良率報表」選單），
    /// 混在同一份檔案會讓驗證訊息顯示成選單標題。
    /// </summary>
    public class ProjectZValidationResource { }
}
