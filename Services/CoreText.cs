using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;

namespace SmartFactory.MES.Services;

/// <summary>
/// Controller／Service 回傳給畫面的訊息（JSON message、TempData、ModelState、例外）取自
/// Resources/SharedResource.{culture}.resx。不走 DI 的 IStringLocalizer：Core 的 Controller
/// 在單元測試是直接 new 出來的，改建構子會波及全部測試與下游部署專案的子類別。
/// 目前文化不是 en 時一律取 zh-TW（與 RequestLocalization 的預設文化一致；測試執行緒的
/// InvariantCulture 也落在這裡）。查無鍵時回傳鍵名，不丟例外。
/// </summary>
public static partial class CoreText
{
    private static readonly ResourceManager Rm =
        new("SmartFactory.MES.Resources.SharedResource", typeof(SharedResource).Assembly);
    private static readonly CultureInfo ZhTw = CultureInfo.GetCultureInfo("zh-TW");
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    public static string Get(string key, params object?[] args)
    {
        var culture = CultureInfo.CurrentUICulture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? EnUs : ZhTw;
        // 只查該文化本身、不往上找中性資源：本專案沒有中性 .resx，往上找會丟 MissingManifestResourceException
        var text = Rm.GetResourceSet(culture, createIfNotExists: true, tryParents: false)?.GetString(key) ?? key;
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }

    /// <summary>
    /// 寫入資料庫、日後要依檢視者語系顯示的系統文字（例：簽核歷程備註），存成 <c>[[Key|arg|arg]]</c>
    /// 而不是當下語系的字串。參數中的 | 與 ] 會被替換，避免破壞格式。
    /// </summary>
    public static string Token(string key, params object?[] args)
        => "[[" + key + string.Concat(args.Select(a => "|" + Sanitize(Convert.ToString(a, CultureInfo.InvariantCulture)))) + "]]";

    /// <summary>把文字中的 token 依目前語系展開；非 token 的文字（使用者輸入、舊資料）原樣保留。查無鍵或格式不符時保留原 token。</summary>
    public static string? Render(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("[[")) return text;
        return TokenRx().Replace(text, m =>
        {
            var key = m.Groups["key"].Value;
            var format = Get(key);
            if (format == key) return m.Value;
            var args = m.Groups["args"].Value.Split('|', StringSplitOptions.None).Skip(1).Cast<object?>().ToArray();
            try { return args.Length == 0 ? format : string.Format(CultureInfo.CurrentCulture, format, args); }
            catch (FormatException) { return m.Value; }
        });
    }

    private static string Sanitize(string? s) => (s ?? "").Replace("|", "/").Replace("]", ")");

    [GeneratedRegex(@"\[\[(?<key>\w+)(?<args>(?:\|[^|\]]*)*)\]\]")]
    private static partial Regex TokenRx();
}
