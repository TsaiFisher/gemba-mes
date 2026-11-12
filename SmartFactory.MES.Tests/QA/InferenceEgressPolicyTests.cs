using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Modules.QA.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QA;

/// <summary>
/// <see cref="InferenceEgressPolicy"/>：營運資料可否送往目前的推論端點（§17.11.8）。
///
/// <para>這一組測試守的是一件很容易被「順手改組態」破掉的事：
/// <c>OllamaUrl</c> 是自由填寫的網址，而 Phase 2 會把機密營運資料整列送進去。
/// <b>預設必須是只信任本機</b>，放行只能來自明示登錄。</para>
/// </summary>
public class InferenceEgressPolicyTests
{
    private static IConfiguration Cfg(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value))
            .Build();

    /// <summary>
    /// 回傳型別刻意是介面：兩個屬性是顯式介面實作（見 <see cref="InferenceEgressPolicy"/>
    /// 對 R5b 的說明），從具體型別上取不到。
    /// </summary>
    private static IInferenceEgressPolicy Sut(IConfiguration cfg) =>
        new InferenceEgressPolicy(cfg, NullLogger<InferenceEgressPolicy>.Instance);

    private const string UrlKey = "Features:QaAssistant:OllamaUrl";
    private const string HostsKey = InferenceEgressPolicy.TrustedHostsKey;

    // ────────────────────────────────────────────────────────────────
    // 預設：只信任本機
    // ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://localhost:11434")]   // WSL 端的 Ollama
    [InlineData("http://localhost:11435")]   // 經 SSH 反向通道連 Windows 端（§17.11.2）
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://[::1]:11434")]
    public void 現行兩個來源與其他loopback寫法_零組態即放行(string url)
    {
        // 這一則同時是「不要求現場改組態」的證據：兩個實際在用的來源都是 loopback。
        var sut = Sut(Cfg((UrlKey, url)));
        Assert.True(sut.AllowsOperationalData);
    }

    [Theory]
    [InlineData("http://10.0.0.20:11434")]           // 內網另一台
    [InlineData("http://ollama.corp.local:11434")]
    [InlineData("https://api.example.com")]          // 雲端
    public void 未登錄的主機一律擋下_不分內網或外網(string url)
    {
        // 內網也擋：閘門管的是「送不送得出這台機器」，而不是「有沒有出防火牆」。
        // 放寬成「私有網段自動信任」的話，一台被接進同網段的機器就等於自動取得機密資料。
        var sut = Sut(Cfg((UrlKey, url)));
        Assert.False(sut.AllowsOperationalData);
    }

    [Fact]
    public void 組態完全沒填時_退回預設的本機位址_仍然放行()
    {
        // ResolveBaseUrl 對空值與壞值都退回 localhost（刻意不 fail fast），
        // 所以「沒填」不該變成「Phase 2 被擋」——那會是一個沒人看得懂的失敗。
        Assert.True(Sut(Cfg()).AllowsOperationalData);
        Assert.True(Sut(Cfg((UrlKey, "這不是網址"))).AllowsOperationalData);
    }

    // ────────────────────────────────────────────────────────────────
    // 明示登錄
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void 登錄主機名即放行_可寫host也可寫hostPort()
    {
        Assert.True(Sut(Cfg((UrlKey, "http://10.0.0.20:11434"),
                            ($"{HostsKey}:0", "10.0.0.20"))).AllowsOperationalData);

        Assert.True(Sut(Cfg((UrlKey, "http://10.0.0.20:11434"),
                            ($"{HostsKey}:0", "10.0.0.20:11434"))).AllowsOperationalData);
    }

    [Fact]
    public void 登錄的埠不同就不算登錄()
    {
        // 寫了 host:port 就是連埠一起指定。同一台機器上另一個埠可能是完全不同的服務
        // ——例如一個對外的反向代理。
        var sut = Sut(Cfg((UrlKey, "http://10.0.0.20:11434"),
                          ($"{HostsKey}:0", "10.0.0.20:9999")));
        Assert.False(sut.AllowsOperationalData);
    }

    [Fact]
    public void 主機名比對不分大小寫_但不做DNS解析()
    {
        Assert.True(Sut(Cfg((UrlKey, "http://Ollama.Corp.Local:11434"),
                            ($"{HostsKey}:0", "ollama.corp.local"))).AllowsOperationalData);

        // 🚨 登錄了 IP 不等於登錄了指向它的主機名：字面比對是刻意的。
        //    否則判定會落在部署方改得動、稽核看不到的地方（DNS）。
        Assert.False(Sut(Cfg((UrlKey, "http://ollama.corp.local:11434"),
                             ($"{HostsKey}:0", "10.0.0.20"))).AllowsOperationalData);
    }

    [Fact]
    public void 環境變數的逗號分隔寫法與陣列等效()
    {
        // 正式環境的組態往往只有環境變數這一條路進得去，而
        // Features__QaAssistant__TrustedInferenceHosts 只能給一個字串。
        // 兩種寫法判定不同的話，現場會得到「照文件設了卻沒生效」。
        var scalar = Sut(Cfg((UrlKey, "http://10.0.0.20:11434"),
                             (HostsKey, "10.0.0.21, 10.0.0.20 ;10.0.0.22")));
        Assert.True(scalar.AllowsOperationalData);

        var array = Sut(Cfg((UrlKey, "http://10.0.0.20:11434"),
                            ($"{HostsKey}:0", "10.0.0.21"), ($"{HostsKey}:1", "10.0.0.20")));
        Assert.True(array.AllowsOperationalData);
    }

    [Fact]
    public void 信任清單只放行被登錄的那一台_不是一次開全部()
    {
        var cfg = Cfg((UrlKey, "https://api.example.com"), ($"{HostsKey}:0", "10.0.0.20"));
        Assert.False(Sut(cfg).AllowsOperationalData);
    }

    [Fact]
    public void Endpoint供稽核用_只有scheme主機與埠()
    {
        // 診斷訊息裡不該出現路徑或查詢字串：那可能帶著別的組態內容。
        var sut = Sut(Cfg((UrlKey, "http://10.0.0.20:11434/api/chat?x=1")));
        Assert.Equal("http://10.0.0.20:11434", sut.Endpoint);
    }
}
