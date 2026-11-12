using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartFactory.MES.Services;

/// <summary>撤銷清單檢查的組態（<c>License:Crl</c>）。</summary>
public class LicenseCrlOptions
{
    public const string SectionName = "License:Crl";

    /// <summary>
    /// 是否啟用。**預設關閉** —— 完全離線的廠區部署不該因為連不到網路而在寬限窗後轉紅；
    /// 那類客戶改用短效期授權（見 §15）。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>撤銷清單網址（HTTPS 靜態檔）。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>連續取不到清單多少天後轉為 Unhealthy。</summary>
    public int GraceDays { get; set; } = 14;

    /// <summary>單次抓取逾時秒數。</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>本機快取路徑；留空則放在應用程式目錄下的 license-crl-cache.json。</summary>
    public string CachePath { get; set; } = string.Empty;
}

public interface ILicenseCrlService
{
    Task<CrlEvaluation> CheckAsync(string licenseId, CancellationToken ct = default);
}

/// <summary>
/// 授權撤銷清單檢查。抓取 → 驗簽 → 與本機快取合併 → 交給 <see cref="LicenseCrl.Evaluate"/> 判定。
///
/// <para>
/// 本服務**不會**阻擋啟動，也不會終止程序。它唯一的輸出是一個判定結果，
/// 由 <see cref="LicenseWatchdogService"/> 轉成 /health 狀態與 Serilog 告警。
/// 理由見 SECURITY-OPERATIONS.md §15：MES 斷網停線的代價遠大於盜用，
/// 因此「被撤銷」要做成**可偵測狀態**，而不是可被利用的繞過點。
/// </para>
/// </summary>
public class LicenseCrlService : ILicenseCrlService
{
    private readonly LicenseCrlOptions _options;
    private readonly ILogger<LicenseCrlService> _logger;
    private readonly HttpClient _http;
    private readonly RSA _publicKey;

    public LicenseCrlService(LicenseCrlOptions options, ILogger<LicenseCrlService> logger)
        : this(options, logger, handler: null) { }

    /// <summary>
    /// 測試用建構式（<c>internal</c>，僅測試組件可見）：
    /// 注入 <see cref="HttpMessageHandler"/> 以避免真的連網，
    /// 並可改用測試金鑰對 —— 正式簽發私鑰不在版控中，測試簽不出合法清單。
    /// 正式路徑（public 建構式）永遠使用嵌入的 <c>license-public.pem</c>。
    /// </summary>
    internal LicenseCrlService(
        LicenseCrlOptions options,
        ILogger<LicenseCrlService> logger,
        HttpMessageHandler? handler,
        RSA? publicKeyOverride = null)
    {
        _options = options;
        _logger  = logger;
        _http    = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));

        // 與授權驗證共用同一把已釘住的公鑰（Embedded Resource），不新增信任根。
        // 取一份自己的 RSA 實例：RSA 物件不保證執行緒安全。
        _publicKey = publicKeyOverride ?? LicenseValidator.LoadEmbeddedPublicKey();
    }

    private string CachePath => string.IsNullOrWhiteSpace(_options.CachePath)
        ? Path.Combine(AppContext.BaseDirectory, "license-crl-cache.json")
        : _options.CachePath;

    public async Task<CrlEvaluation> CheckAsync(string licenseId, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return new CrlEvaluation(CrlOutcome.Disabled, "未啟用撤銷清單檢查");

        if (string.IsNullOrWhiteSpace(_options.Url))
        {
            // 啟用了卻沒給網址是組態錯誤，不是「沒有撤銷資訊」—— 要讓它明確可見。
            _logger.LogError(
                "[LicenseCrl] 已啟用撤銷清單檢查，但 {Key}:Url 未設定。請補上網址或將 Enabled 設為 false。",
                LicenseCrlOptions.SectionName);
            return new CrlEvaluation(CrlOutcome.Disabled, "撤銷清單網址未設定");
        }

        var now   = DateTimeOffset.UtcNow;
        var state = LoadState() ?? new CrlState { FirstAttemptUtc = now };

        CrlDocument? fetched = null;
        var fetchedIsFresh = false;

        try
        {
            var json = await _http.GetStringAsync(_options.Url, ct);
            fetched  = LicenseCrl.ParseAndVerify(json, _publicKey);

            if (fetched.NextUpdate >= now)
            {
                fetchedIsFresh       = true;
                state.LastSuccessUtc = now;
                state.Crl            = fetched;
            }
            else
            {
                // 已過 nextUpdate 的清單不更新「上次成功」時間。
                // 這是防重放：把一份舊的、尚未列入該授權的合法清單釘在本機代理上，
                // 就能永遠讓撤銷檢查看起來正常 —— nextUpdate 為此設下時限。
                _logger.LogWarning(
                    "[LicenseCrl] 取得的撤銷清單已於 {NextUpdate:yyyy-MM-dd HH:mm} 逾期，不視為一次成功更新。",
                    fetched.NextUpdate);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 抓不到／驗不過一律當「沒有撤銷資訊」，絕不當成「沒有被撤銷」。
            _logger.LogWarning("[LicenseCrl] 取得撤銷清單失敗：{Message}", ex.Message);
        }

        // 以 issuedAt 較新者為準。本輪抓到的通常最新；若快取反而較新，
        // 表示線上那份被回退了 —— 採用較新的那份是比較安全的讀法。
        var effective = PickNewer(fetched, state.Crl);

        var evaluation = LicenseCrl.Evaluate(
            effective, fetchedIsFresh, state.LastSuccessUtc, state.FirstAttemptUtc,
            licenseId, _options.GraceDays, now);

        SaveState(state);
        return evaluation;
    }

    private static CrlDocument? PickNewer(CrlDocument? fetched, CrlDocument? cached)
    {
        if (fetched is null) return cached;
        if (cached  is null) return fetched;
        return cached.IssuedAt > fetched.IssuedAt ? cached : fetched;
    }

    private CrlState? LoadState()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;

            var state = JsonSerializer.Deserialize<CrlState>(
                File.ReadAllText(CachePath), LicenseCrl.JsonOptions);
            if (state is null) return null;

            // 快取裡的清單一樣要驗簽：竄改快取檔不能製造出一份假的「未撤銷」證據。
            if (state.Crl is not null)
            {
                try
                {
                    state.Crl = LicenseCrl.ParseAndVerify(
                        JsonSerializer.Serialize(state.Crl, LicenseCrl.JsonOptions), _publicKey);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[LicenseCrl] 本機快取的撤銷清單驗簽失敗，已捨棄：{Message}", ex.Message);
                    state.Crl = null;
                }
            }

            return state;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[LicenseCrl] 讀取撤銷清單快取失敗：{Message}", ex.Message);
            return null;
        }
    }

    private void SaveState(CrlState state)
    {
        try
        {
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 寫不進快取不影響本輪判定，只是下次會少一份離線依據。
            _logger.LogWarning("[LicenseCrl] 寫入撤銷清單快取失敗：{Message}", ex.Message);
        }
    }

    /// <summary>本機狀態：寬限窗的計時基準 + 最後一份驗過簽的清單。</summary>
    internal sealed class CrlState
    {
        /// <summary>首次嘗試檢查的時間 —— 從未成功過時，寬限窗由此起算。</summary>
        [JsonPropertyName("firstAttemptUtc")]
        public DateTimeOffset FirstAttemptUtc { get; set; }

        /// <summary>上次成功取得「未逾期」清單的時間。</summary>
        [JsonPropertyName("lastSuccessUtc")]
        public DateTimeOffset? LastSuccessUtc { get; set; }

        /// <summary>最後一份驗過簽的清單，供離線期間繼續判定撤銷。</summary>
        [JsonPropertyName("crl")]
        public CrlDocument? Crl { get; set; }
    }
}
