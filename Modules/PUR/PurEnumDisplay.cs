using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules.PUR.Models;

namespace SmartFactory.MES.Modules.PUR
{
    /// <summary>
    /// PUR 專屬列舉 → 使用者語系顯示字串（共用的 DocStatus 走 _Shared.DocEnumDisplay）。
    /// resx key 全部寫成字面量（不動態串接），維持可靜態檢查。
    /// 對應 key 置於 Resources/ProjectZSharedResource.{zh-TW,en-US}.resx。
    /// </summary>
    public static class PurEnumDisplay
    {
        public static string Text(IStringLocalizer l, OutsourceReworkStatus s) => s switch
        {
            OutsourceReworkStatus.Draft    => l["OutsourceReworkStatusDraft"].Value,
            OutsourceReworkStatus.Sent     => l["OutsourceReworkStatusSent"].Value,
            OutsourceReworkStatus.Received => l["OutsourceReworkStatusReceived"].Value,
            OutsourceReworkStatus.Void     => l["OutsourceReworkStatusVoid"].Value,
            _                              => s.ToString()
        };
    }
}
