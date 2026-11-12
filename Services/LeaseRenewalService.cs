using System.Net.Http.Json;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartFactory.MES.Services;

/// <summary>租約用戶端設定（<c>License</c> 區段）。</summary>
public sealed class LeaseOptions
{
    /// <summary>直接指定伺服器（開發／測試用）。設定後不讀 endpoints.json。</summary>
    public string? ServerUrl { get; set; }

    /// <summary>根金鑰簽章的伺服器清單（§18.4：不註冊網域，換託管平台只改這份）。</summary>
    public string EndpointsUrl { get; set; } =
        "https://raw.githubusercontent.com/TsaiFisher/smartfactory-licensing/main/endpoints.json";

    /// <summary>
    /// 租約檔位置。預設在 license.json 旁邊——錨定檔所在的 /etc/smartfactory 通常只有 root 可寫。
    /// 容器部署必須指到持久化 volume，否則每次重建都要重新啟用。
    /// </summary>
    public string? LeasePath { get; set; }

    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// 確認的傳輸方式（SECURITY-OPERATIONS.md §18.15.3）：<c>relay</c>（預設）＝MES 本身不連外，
    /// 由管理員在授權狀態頁按「確認授權」、經瀏覽器代為確認；<c>direct</c>＝客戶允許時由 MES 每日自動續租。
    /// </summary>
    public string Transport { get; set; } = "relay";

    public bool IsRelay => !string.Equals(Transport, "direct", StringComparison.OrdinalIgnoreCase);
}

/// <summary>一次續租的結果。</summary>
public enum LeaseOutcome { Leased, Notice, Rejected, Unreachable, NotApplicable }

/// <summary>
/// 向租約伺服器續租一次（SECURITY-OPERATIONS.md §18.6）。不含排程，排程在 <see cref="LeaseRenewalService"/>。
///
/// <para>送出：license.json 原文、指紋、上一份租約的啟用碼、版本、作業系統、本機時間。
/// <b>不送錨定碼、連線字串、業務資料</b>（§16.5）。</para>
/// </summary>
public sealed class LeaseRenewer(
    HttpClient http,
    IConfiguration configuration,
    LeaseOptions options,
    ILicenseStateService state,
    TimeProvider time,
    ILogger<LeaseRenewer> logger,
    RSA? rootPublicKeyOverride = null,
    Func<string>? fingerprintOverride = null)
{
    private RSA RootKey => rootPublicKeyOverride ?? LicenseValidator.LoadEmbeddedPublicKey();
    private string Fingerprint() => fingerprintOverride?.Invoke() ?? MachineAnchor.GetFingerprint();

    public string LeasePath => options.LeasePath
        ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(LicenseValidator.ResolveLicensePath(configuration)))!, "lease.json");

    /// <summary>本次續租取得的租約到期時間（排程據以決定下次何時續租）。</summary>
    public DateTimeOffset? LastNotAfter { get; private set; }

    private string EndpointsCachePath => Path.Combine(Path.GetDirectoryName(LeasePath)!, "license-endpoints.cache.json");

    /// <summary>啟動時讀回已存的租約，重新驗簽後交給狀態服務。改過的檔驗不過，視同沒有租約。</summary>
    public LeasePayload? LoadStoredLease()
    {
        var lic = state.License;
        if (lic is null || !File.Exists(LeasePath)) return null;
        try
        {
            var p = LeaseDocuments.Verify(File.ReadAllText(LeasePath), RootKey, lic.LicenseId, Fingerprint());
            if (p.Type != LeaseDocuments.TypeLease) return null;
            state.SetLease(p.NotAfter!.Value, p.ServerTime);
            return p;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or InvalidOperationException)
        {
            logger.LogWarning("[Lease] 既有 lease.json 無效，忽略：{Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 確認／續租請求的內容（與 <c>POST /lease</c> 相同）。relay 模式由授權狀態頁顯示並交給瀏覽器送出；
    /// direct 模式由 <see cref="RenewAsync"/> 直接送出。lease 與 offline 授權都可確認
    /// （offline 在系統參數啟用前要先確認一次，§18.15.8）；其餘回 null。
    /// </summary>
    public async Task<JsonObject?> BuildRequestAsync(CancellationToken ct = default)
    {
        var lic = state.License;
        if (lic is null || !CanConfirm(lic)) return null;

        var fingerprint = Fingerprint();
        string? token = null;
        if (File.Exists(LeasePath))
        {
            try { token = LeaseDocuments.Verify(File.ReadAllText(LeasePath), RootKey, lic.LicenseId, fingerprint).Token; }
            catch (Exception ex) when (ex is SecurityException or IOException) { /* 無效就當沒有：伺服器會判 conflict 或 restore */ }
        }

        return new JsonObject
        {
            ["license"] = JsonNode.Parse(await File.ReadAllTextAsync(LicenseValidator.ResolveLicensePath(configuration), ct)),
            ["fingerprint"] = fingerprint,
            ["token"] = token,
            ["appVersion"] = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
            ["os"] = Environment.OSVersion.Platform.ToString(),
            ["clientTime"] = time.GetUtcNow().ToString("O"),
        };
    }

    /// <summary>relay：瀏覽器要開的確認頁網址。請求放在 # 後面，不會出現在伺服器存取紀錄（§18.15.3）。</summary>
    public static string CheckinUrl(string server, JsonObject request)
        => $"{server.TrimEnd('/')}/checkin#" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(request.ToJsonString()))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>relay 用的伺服器：設定值 → 已快取且驗過簽的 endpoints.json → 內建預設。<b>不連網</b>。</summary>
    public string ResolveServerOffline()
    {
        if (!string.IsNullOrWhiteSpace(options.ServerUrl)) return options.ServerUrl;
        try
        {
            if (File.Exists(EndpointsCachePath))
            {
                var servers = LicenseDocuments.ParseEndpoints(File.ReadAllText(EndpointsCachePath), RootKey).LeaseServers;
                if (servers.Any()) return servers.First();
            }
        }
        catch (Exception ex) when (ex is SecurityException or InvalidOperationException or IOException) { }
        return LicenseRequestCode.DefaultServerUrl;
    }

    /// <summary>可向伺服器確認的綁定方式：lease，以及 offline（VM 強制或系統參數啟用時需要）。</summary>
    internal static bool CanConfirm(LicenseContext lic)
        => lic.BindingMode is LicenseBindingModes.Lease or LicenseBindingModes.Offline;

    public async Task<LeaseOutcome> RenewAsync(CancellationToken ct = default)
    {
        // relay：MES 本身不連外，由管理員經瀏覽器確認（ApplyResponse）
        if (options.IsRelay) return LeaseOutcome.NotApplicable;
        // 不需要確認（offline 授權、非 VM、系統參數未啟用）：不連外——客戶允許 direct 不等於要替不需要的授權連線
        if (!state.LeaseRequired) return LeaseOutcome.NotApplicable;
        var body = await BuildRequestAsync(ct);
        if (body is null) return LeaseOutcome.NotApplicable;

        foreach (var server in await ResolveServersAsync(ct))
        {
            HttpResponseMessage res;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                res = await http.PostAsJsonAsync($"{server.TrimEnd('/')}/lease", body, cts.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("[Lease] 無法連線 {Server}：{Message}", server, ex.Message);
                continue;   // 下一個伺服器
            }

            var text = await res.Content.ReadAsStringAsync(ct);
            return ApplyResponse(text, (int)res.StatusCode);
        }
        return LeaseOutcome.Unreachable;
    }

    /// <summary>
    /// 套用伺服器回覆（direct 續租與 relay 確認共用）：驗簽、寫 lease.json、更新狀態。
    /// relay 的回覆經過瀏覽器，來源不可信——<b>驗簽是唯一依據</b>，與 direct 完全相同。
    /// </summary>
    public LeaseOutcome ApplyResponse(string text, int? httpStatus = null)
    {
        var lic = state.License;
        if (lic is null || !CanConfirm(lic)) return LeaseOutcome.NotApplicable;
        var fingerprint = Fingerprint();
        {
            LeasePayload p;
            try
            {
                p = LeaseDocuments.Verify(text, RootKey, lic.LicenseId, fingerprint);
            }
            catch (SecurityException ex)
            {
                // 沒簽章的錯誤（過期、未登記、授權檔無效）或簽章不對：只記錄，不改狀態（單向加嚴）
                var msg = $"授權伺服器拒絕確認{(httpStatus is { } st ? $"（HTTP {st}）" : "")}：{TryError(text) ?? ex.Message}";
                logger.LogWarning("[Lease] {Message}", msg);
                state.SetLeaseWarning(msg);
                return LeaseOutcome.Rejected;
            }

            if (p.Type == LeaseDocuments.TypeLease)
            {
                WriteAtomic(LeasePath, text);
                state.SetLease(p.NotAfter!.Value, p.ServerTime);
                LastNotAfter = p.NotAfter;
                logger.LogInformation("[Lease] 已續租，租約至 {NotAfter:yyyy-MM-dd}", p.NotAfter);
                return LeaseOutcome.Leased;
            }

            if (p.IsTerminalNotice)
            {
                state.SetLeaseNotice(p.Status, p.ServerTime);
                logger.LogCritical("[Lease] 授權伺服器通知 {Status}，系統轉為唯讀", p.Status);
            }
            else
            {
                var msg = p.Status == "seat-taken"
                    ? "這張授權已在另一台機器上使用中（座數已滿）。如為換機，請等舊機停用 3 天後自動轉移，或聯繫授權提供者。"
                    : "授權伺服器偵測到同一台機器身分在另一處使用（疑似整台複製）。若非預期，請聯繫授權提供者。";
                state.SetLeaseWarning(msg);
                logger.LogWarning("[Lease] {Status}：{Message}", p.Status, msg);
            }
            return LeaseOutcome.Notice;
        }
    }

    /// <summary>
    /// 伺服器清單：設定值 → 已驗簽的 endpoints.json（成功即快取）→ 快取 → 內建預設。
    /// 抓不到或驗不過一律往下退，不會因此停止續租。
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolveServersAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(options.ServerUrl)) return [options.ServerUrl];

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            var json = await http.GetStringAsync(options.EndpointsUrl, cts.Token);
            var doc = LicenseDocuments.ParseEndpoints(json, RootKey);
            WriteAtomic(EndpointsCachePath, json);
            return doc.LeaseServers;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SecurityException or InvalidOperationException
                                   && !ct.IsCancellationRequested)
        {
            logger.LogDebug("[Lease] 取不到 endpoints.json：{Message}", ex.Message);
        }

        try
        {
            if (File.Exists(EndpointsCachePath))
                return LicenseDocuments.ParseEndpoints(File.ReadAllText(EndpointsCachePath), RootKey).LeaseServers;
        }
        catch (Exception ex) when (ex is SecurityException or InvalidOperationException or IOException) { }

        return [LicenseRequestCode.DefaultServerUrl];
    }

    private static string? TryError(string text)
    {
        try { return JsonDocument.Parse(text).RootElement.GetProperty("error").GetString(); }
        catch { return null; }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// 續租排程（§18.6）：啟動時立即一次；成功後每 24 小時加 0–6 小時隨機抖動
/// （避免所有客戶在同一時刻打伺服器）；失敗重試 1→2→4→6 小時。不需要確認時每小時檢查一次
/// （匯入後可能換成 lease、系統參數可能被啟用）。
/// </summary>
public sealed class LeaseRenewalService(IServiceScopeFactory scopes, TimeProvider time, ILogger<LeaseRenewalService> logger) : BackgroundService
{
    /// <summary>
    /// 長延遲拆成短睡：Task.Delay 的計時在主機睡眠／休眠時不前進，睡一次 24 小時可能實際隔了好幾天
    /// 才醒——續租間隔就這樣靜默變長。改成每 10 分鐘醒來以牆上時鐘判斷是否到期。
    /// </summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);

    /// <param name="leaseRemaining">剛取得的租約還剩多久。租約比續租週期短時（例如驗收用的 2 小時租約、
    /// 或伺服器對個別授權縮短租約），改在剩一半時續租，否則自己的租約會在兩次續租之間到期。</param>
    internal static TimeSpan NextDelay(LeaseOutcome outcome, int failures, double jitter01, TimeSpan? leaseRemaining = null) => outcome switch
    {
        LeaseOutcome.Leased when leaseRemaining is { } r && r / 2 < TimeSpan.FromHours(24)
            => r / 2 < TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : r / 2,
        LeaseOutcome.Leased => TimeSpan.FromHours(24 + 6 * jitter01),
        LeaseOutcome.NotApplicable => TimeSpan.FromHours(1),
        _ => TimeSpan.FromHours(Math.Min(6, Math.Pow(2, Math.Max(0, failures - 1)))),
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            LeaseOutcome outcome;
            TimeSpan? remaining = null;
            try
            {
                using var scope = scopes.CreateScope();
                var renewer = scope.ServiceProvider.GetRequiredService<LeaseRenewer>();
                outcome = await renewer.RenewAsync(stoppingToken);
                if (renewer.LastNotAfter is { } na) remaining = na - time.GetUtcNow();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Lease] 續租發生未預期錯誤");
                outcome = LeaseOutcome.Unreachable;
            }

            failures = outcome is LeaseOutcome.Leased or LeaseOutcome.NotApplicable ? 0 : failures + 1;
            var due = time.GetUtcNow() + NextDelay(outcome, failures, Random.Shared.NextDouble(), remaining);
            try
            {
                while (time.GetUtcNow() < due)
                {
                    var left = due - time.GetUtcNow();
                    await Task.Delay(left < Tick ? left : Tick, stoppingToken);
                }
            }
            catch (OperationCanceledException) { return; }
        }
    }
}
