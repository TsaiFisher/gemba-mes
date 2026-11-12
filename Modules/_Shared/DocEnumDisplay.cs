using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules._Shared
{
    /// <summary>
    /// 跨模組共用單據列舉 → 使用者語系顯示字串（INV／ORD／PUR 單據皆用 <see cref="DocStatus"/>）。
    /// 查詢列下拉、清單徽章、唯讀表單、列印一律透過本類別取字，避免直接輸出 enum 名稱（英文）。
    /// resx key 全部寫成字面量（不動態串接），維持可靜態檢查。
    /// 對應 key 置於 Resources/ProjectZSharedResource.{zh-TW,en-US}.resx。
    /// </summary>
    public static class DocEnumDisplay
    {
        public static string Text(IStringLocalizer l, DocStatus s) => s switch
        {
            DocStatus.Draft     => l["DocStatusDraft"].Value,
            DocStatus.Confirmed => l["DocStatusConfirmed"].Value,
            DocStatus.InTransit => l["DocStatusInTransit"].Value,
            DocStatus.Posted    => l["DocStatusPosted"].Value,
            DocStatus.Void      => l["DocStatusVoid"].Value,
            _                   => s.ToString()
        };
    }
}
