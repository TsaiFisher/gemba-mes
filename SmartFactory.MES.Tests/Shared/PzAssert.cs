using System.Text.RegularExpressions;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Tests.Shared;

/// <summary>
/// 斷言例外訊息是某個 ProjectZMessageResource 鍵產生的（佔位符視為萬用字元）。
/// 測試執行緒的文化不固定（本機／CI 皆可能是 en），直接比對中文片段會隨環境紅綠。
/// </summary>
public static class PzAssert
{
    public static void Message(string key, string actual)
    {
        var format = PzText.Get(key);
        Assert.NotEqual(key, format);   // 鍵必須存在
        var pattern = "^" + Regex.Replace(Regex.Escape(format), @"\\\{\d+(:[^}]*)?}", ".*") + "$";
        Assert.Matches(new Regex(pattern, RegexOptions.Singleline), actual);
    }

    /// <summary>同 <see cref="Message"/>，但只要求 <paramref name="text"/> 中「含有」該訊息（例：匿名物件的 ToString）。</summary>
    public static void ContainsMessage(string key, string text)
    {
        var format = PzText.Get(key);
        Assert.NotEqual(key, format);
        var pattern = Regex.Replace(Regex.Escape(format), @"\\\{\d+(:[^}]*)?}", ".*");
        Assert.Matches(new Regex(pattern, RegexOptions.Singleline), text);
    }

    /// <summary>不丟例外的版本，供 Assert.Contains(collection, predicate) 使用。</summary>
    public static bool IsFrom(string key, string text)
    {
        var format = PzText.Get(key);
        var pattern = Regex.Replace(Regex.Escape(format), @"\\\{\d+(:[^}]*)?}", ".*");
        return format != key && Regex.IsMatch(text, pattern, RegexOptions.Singleline);
    }
}
