using System.Text.RegularExpressions;
using SmartFactory.MES.Modules._Shared;
using Xunit;

namespace SmartFactory.MES.IntegrationTests;

/// <summary>
/// 斷言訊息是某個 ProjectZMessageResource 鍵產生的（佔位符視為萬用字元）。
/// 直接呼叫 Controller／Service 時沒有 RequestLocalization，文化是測試進程的（常為 en），不可比對中文片段。
/// </summary>
public static class PzAssert
{
    public static void Message(string key, string? actual)
    {
        var format = PzText.Get(key);
        Assert.NotEqual(key, format);
        var pattern = "^" + Regex.Replace(Regex.Escape(format), @"\\\{\d+(:[^}]*)?}", ".*") + "$";
        Assert.Matches(new Regex(pattern, RegexOptions.Singleline), actual ?? "");
    }

    /// <summary>只要求 <paramref name="text"/> 中含有該鍵產生的訊息。</summary>
    public static void ContainsMessage(string key, string? text)
    {
        var format = PzText.Get(key);
        Assert.NotEqual(key, format);
        var pattern = Regex.Replace(Regex.Escape(format), @"\\\{\d+(:[^}]*)?}", ".*");
        Assert.Matches(new Regex(pattern, RegexOptions.Singleline), text ?? "");
    }
}
