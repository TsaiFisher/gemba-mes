using System.Globalization;

namespace SmartFactory.MES.Modules._Shared
{
    /// <summary>
    /// 廠別顯示名稱：英文介面且有英文名時用英文名，否則用中文名，兩者都沒有退回代碼。
    ///
    /// <para>規則與 Core 版面的廠別切換（Pages/Shared/_LoginPartial.cshtml）一致；語系讀
    /// CultureInfo.CurrentUICulture（RequestLocalization cookie 驅動），同 CodeEntryDisplayExtensions。
    /// 2026-10-15 英文介面巡訪發現廠別參數總覽、單號設定、合併報表、跨廠調撥都直接印中文名。</para>
    ///
    /// <para>EF 查詢裡不要呼叫它（翻不成 SQL）：投影時一併取 Name／NameEn，取回後再套。</para>
    /// </summary>
    public static class PlantDisplay
    {
        public static string Name(string? name, string? nameEn, string id)
            => CultureInfo.CurrentUICulture.Name == "en-US" && !string.IsNullOrWhiteSpace(nameEn)
                ? nameEn!
                : string.IsNullOrWhiteSpace(name) ? id : name!;
    }
}
