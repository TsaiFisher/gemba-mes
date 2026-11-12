using System.Globalization;
using System.Resources;

namespace SmartFactory.MES.Modules._Shared;

/// <summary>
/// 模組服務層丟給畫面的訊息取自 Resources/ProjectZMessageResource.{culture}.resx（Core 對應的是 CoreText）。
/// 服務層多半沒有 IStringLocalizer、單元測試也直接 new 服務，故走 ResourceManager。
/// 目前文化不是 en 時一律取 zh-TW（與 RequestLocalization 預設文化一致）；查無鍵回傳鍵名。
/// </summary>
public static class PzText
{
    private static readonly ResourceManager Rm =
        new("SmartFactory.MES.Resources.ProjectZMessageResource", typeof(ProjectZMessageResource).Assembly);
    private static readonly CultureInfo ZhTw = CultureInfo.GetCultureInfo("zh-TW");
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    public static string Get(string key, params object?[] args)
    {
        var culture = CultureInfo.CurrentUICulture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? EnUs : ZhTw;
        // 只查該文化本身、不往上找中性資源：沒有中性 .resx，往上找會丟 MissingManifestResourceException
        var text = Rm.GetResourceSet(culture, createIfNotExists: true, tryParents: false)?.GetString(key) ?? key;
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }
}
