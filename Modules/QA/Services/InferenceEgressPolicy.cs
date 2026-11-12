using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SmartFactory.MES.Modules.QA.Services;

/// <summary>
/// 「目前設定的推論端點，可不可以承載**營運資料**」的判定（§17.11.8 待決 #49 的閘門）。
///
/// <para><b>它管的不是權限，是資料流的去向。</b>三個相鄰的旋鈕常被混為一談：</para>
/// <list type="bullet">
///   <item>選單權限（<c>CanRead</c>）回答「這個人能不能看」</item>
///   <item><c>AllowMonetaryTools</c> 回答「這支能不能用一句話問到」</item>
///   <item><b>本判定回答「這些列送不送得出這台機器」</b></item>
/// </list>
///
/// <para>🚨 <b>為什麼需要它</b>：<c>Features:QaAssistant:OllamaUrl</c> 是自由填寫的絕對網址
/// （<see cref="OllamaClient.ResolveBaseUrl"/> 只驗語法、不限主機），而 Phase 2 會把
/// §八 裁定為機密的營運資料整列送進推論服務。也就是說在本判定存在之前，
/// <b>改一個 URL 就等於把機密資料送到任何一台機器，而沒有任何程式攔得住</b>。
/// 預設值下 <c>AllowMonetaryTools=false</c> 讓含金額的三支不進目錄，看起來像有保護——
/// 但那是巧合不是保證：把它設成 <c>true</c> 的人改的是一個名字裡完全沒有「外部」兩個字的鍵。</para>
///
/// <para><b>Phase 1（手冊問答）刻意不受本判定約束</b>：它送的是產品手冊內容（內部級），
/// 不是營運資料。把兩者綁在一起會讓「換一台機器跑推論」這件正當的事變成不可能。</para>
///
/// <para>⚠️ <b>它擋得住什麼、擋不住什麼</b>——loopback 位址可能是一條 SSH 通道
/// （開發機的 <c>localhost:11435</c> 就是通到 Windows 端的 Ollama）。本判定看不穿通道，
/// 也不該假裝看得穿：它擋的是「改一個 URL 就靜默送出去」，
/// 不是主機層的出境控制。後者要靠防火牆，不是靠應用程式。</para>
/// </summary>
public interface IInferenceEgressPolicy
{
    /// <summary>營運資料（Phase 2）可否送往目前的推論端點。</summary>
    bool AllowsOperationalData { get; }

    /// <summary>供 log 與診斷用的端點描述（<c>scheme://host:port</c>，不含路徑）。</summary>
    string Endpoint { get; }
}

/// <inheritdoc />
public sealed class InferenceEgressPolicy : IInferenceEgressPolicy
{
    /// <summary>
    /// 除 loopback 之外，還信任哪些主機承載營運資料。
    ///
    /// <para>兩種寫法都吃：appsettings 的字串陣列，或**單一字串以逗號／分號分隔**——
    /// 後者是為了環境變數（<c>Features__QaAssistant__TrustedInferenceHosts</c> 只能給一個字串，
    /// 而正式環境的組態往往只有環境變數這一條路進得去）。</para>
    /// </summary>
    public const string TrustedHostsKey = "Features:QaAssistant:TrustedInferenceHosts";

    // ⚠️ **兩個屬性都寫成顯式介面實作**，不是為了風格：一致性規則 R5b 掃
    // `Modules.*` 底下所有 `class` 的非可空實值型別屬性，並要求每個屬性名在
    // SharedResource 有雙語驗證資源鍵。`AllowsOperationalData` 是判定結果、
    // 不是表單欄位，替它造一個假的欄位名鍵會讓資源檔失去意義；
    // 顯式實作不算公開屬性，因此不在該規則的掃描範圍內
    // （對照 `ReportScreenBinding` 選 record struct 的同一個理由）。
    bool IInferenceEgressPolicy.AllowsOperationalData => _allowsOperationalData;

    string IInferenceEgressPolicy.Endpoint => _endpoint;

    private readonly bool _allowsOperationalData;
    private readonly string _endpoint;

    public InferenceEgressPolicy(IConfiguration configuration, ILogger<InferenceEgressPolicy> log)
    {
        // 🚨 **必須與具名 HttpClient 讀的是同一份判讀**（QAModule 也呼叫 ResolveBaseUrl）。
        //    自己另解一次組態，就會出現「閘門判的 URL 與實際打出去的 URL 不是同一個」
        //    這種最糟的形狀：看起來有守門，守的卻是別人。
        var uri = OllamaClient.ResolveBaseUrl(configuration);
        var trusted = ResolveTrustedHosts(configuration);

        _endpoint = $"{uri.Scheme}://{uri.Host}:{uri.Port}";
        _allowsOperationalData = IsTrusted(uri, trusted);

        // §17.8：降級與停用都要出聲。少了這一段，症狀是「資料查詢模式怎麼問都說不能用」，
        // 而組態上看不出任何異常——OllamaUrl 明明填得好好的。
        if (!_allowsOperationalData)
            log.LogWarning(
                "推論端點 {Endpoint} 不在信任清單內，**自然語言查詢營運資料（Phase 2）已停用**（手冊問答不受影響）。" +
                "要放行請把主機加進 {Key}（可寫 host 或 host:port）——這是一次明示的資料出境決定，不是預設值。",
                _endpoint, TrustedHostsKey);
        else if (!uri.IsLoopback)
            log.LogWarning(
                "推論端點 {Endpoint} 不在本機，但已由 {Key} 明示信任：營運資料會離開這台機器。" +
                (uri.Scheme == Uri.UriSchemeHttps ? "" : "⚠️ 且走的是明文 HTTP。"),
                _endpoint, TrustedHostsKey);
    }

    /// <summary>
    /// 信任清單的組態判讀。<c>public static</c> 與 <see cref="OllamaClient.ResolveBaseUrl"/> 一致：
    /// 組態怎麼判讀是公開契約，測試與診斷都要能走同一段。
    /// </summary>
    public static IReadOnlyList<string> ResolveTrustedHosts(IConfiguration configuration)
    {
        var section = configuration.GetSection(TrustedHostsKey);

        var fromArray = section.GetChildren()
            .Select(c => c.Value?.Trim())
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToList();

        if (fromArray.Count > 0) return fromArray;

        return string.IsNullOrWhiteSpace(section.Value)
            ? []
            : section.Value.Split(new[] { ',', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// 端點是否可承載營運資料。
    ///
    /// <para><b>比對的是組態字面上的主機，不做 DNS 解析。</b>解析過的話，
    /// 同一份組態在不同網路下會有不同結論，而且判定會落在一個
    /// 部署方改得動、稽核看不到的地方（DNS）。字面比對的代價是
    /// <c>ollama.corp.local</c> 與它的 IP 要各自登錄一次——那正是我們要的：
    /// 每一種寫法都是一次明示。</para>
    /// </summary>
    public static bool IsTrusted(Uri endpoint, IReadOnlyList<string> trustedHosts)
    {
        // localhost／127.0.0.0/8／::1。開發機經 SSH 反向通道連 Windows 端 Ollama
        // （localhost:11435）也走這一條，所以現行的兩個來源都不需要任何組態。
        if (endpoint.IsLoopback) return true;

        var host = endpoint.Host;                       // IPv6 會是 "[::1]" 這種形式
        var hostPort = $"{host}:{endpoint.Port}";

        return trustedHosts.Any(h =>
            string.Equals(h, host, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(h, hostPort, StringComparison.OrdinalIgnoreCase));
    }
}
