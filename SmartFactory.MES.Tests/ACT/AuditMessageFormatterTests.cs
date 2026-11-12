using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules.ACT.Services;
using Xunit;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// 稽核描述在顯示端才組句（<see cref="IAuditMessageFormatter"/>）。
///
/// <para>用真的 <c>ResourceManagerStringLocalizerFactory</c> 而不是 mock：這支要驗的正是
/// 「切到 en-US 會不會真的拿到英文」，mock 掉 localizer 等於把唯一的受測對象換掉，
/// 測試會恆綠而畫面仍是中文。</para>
/// </summary>
public class AuditMessageFormatterTests
{
    private static IAuditMessageFormatter New()
    {
        var services = new ServiceCollection();
        // ResourceManagerStringLocalizerFactory 要 ILoggerFactory，AddLocalization 不會帶。
        services.AddLogging();
        services.AddLocalization(o => o.ResourcesPath = "Resources");
        var sp = services.BuildServiceProvider();
        return new AuditMessageFormatter(
            sp.GetRequiredService<IStringLocalizer<ProjectZAuditResource>>());
    }

    private static T InCulture<T>(string culture, Func<T> f)
    {
        var prevUi = CultureInfo.CurrentUICulture;
        var prev   = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            CultureInfo.CurrentCulture   = new CultureInfo(culture);
            return f();
        }
        finally
        {
            CultureInfo.CurrentUICulture = prevUi;
            CultureInfo.CurrentCulture   = prev;
        }
    }

    private const string Args = """["10","8","2"]""";

    [Fact]
    public void 同一筆發現在兩種語系組出兩種語言()
    {
        var fmt = New();

        var zh = InCulture("zh-TW", () => fmt.Format(AuditMessages.PoOverReceipt, Args, "fallback"));
        var en = InCulture("en-US", () => fmt.Format(AuditMessages.PoOverReceipt, Args, "fallback"));

        Assert.Contains("超過採購量", zh);
        Assert.Contains("exceeds ordered qty", en);
        // 參數是存下來的字串，兩邊必須是同一組數字——會變的只有句子
        Assert.Contains("10", zh);
        Assert.Contains("10", en);
    }

    [Fact]
    public void 舊資料沒有訊息鍵時退回存檔描述()
    {
        var fmt = New();
        // 2026-09-21 之前產生的列：MessageKey 是空字串，只有當初存下的中文句子
        Assert.Equal("舊的中文描述",
            fmt.Format("", null, "舊的中文描述"));
        Assert.Equal("舊的中文描述",
            fmt.Format(null, Args, "舊的中文描述"));
    }

    [Fact]
    public void 參數JSON壞掉時退回存檔描述而不是擲例外()
    {
        var fmt = New();
        // 這條路徑跑在稽核報表上；為了一句描述讓整張報表 500 並不值得
        Assert.Equal("fallback", fmt.Format(AuditMessages.PoOverReceipt, "{壞掉的 JSON", "fallback"));
    }

    [Fact]
    public void 鍵不存在時退回存檔描述而不是印出鍵名()
    {
        var fmt = New();
        Assert.Equal("fallback", fmt.Format("NoSuchKey", Args, "fallback"));
    }

    [Fact]
    public void 參數個數不足時不擲例外()
    {
        var fmt = New();
        // PoOverReceipt 要 3 個參數，只給 1 個
        var r = fmt.Format(AuditMessages.PoOverReceipt, """["10"]""", "fallback");
        Assert.Equal("fallback", r);
    }

    [Fact]
    public void 存檔用的預設描述不隨顯示語系變動()
    {
        // ErrorDescription 是寫進資料庫的值。它若跟著「誰在看」而變，
        // 同一筆異常在關帳訊息與稽核報表會是兩種文字。
        var underEn = InCulture("en-US",
            () => AuditMessageTemplates.FormatDefault(AuditMessages.PoOverReceipt, ["10", "8", "2"]));
        var underZh = InCulture("zh-TW",
            () => AuditMessageTemplates.FormatDefault(AuditMessages.PoOverReceipt, ["10", "8", "2"]));

        Assert.Equal(underZh, underEn);
        Assert.Contains("超過採購量", underZh);
    }
}
