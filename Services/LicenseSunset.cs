using System.Security;
using System.Security.Cryptography;

namespace SmartFactory.MES.Services;

/// <summary>
/// 終止聲明（SECURITY-OPERATIONS.md §18.9）：我方停止服務時，以根金鑰簽署 sunset.json，
/// 讀到簽章有效的聲明即<b>永久</b>解除授權、租約、到期與綁定檢查；新安裝也免授權啟動。
///
/// <para><b>絕不自動解鎖</b>：只有「讀到、且根金鑰驗簽通過、且範圍為 all」才算數。
/// 連不上、404、格式錯、簽章不符一律視為「沒有終止聲明」——否則用防火牆擋住授權伺服器就能解鎖。</para>
/// </summary>
public static class LicenseSunset
{
    public const string DefaultUrl =
        "https://raw.githubusercontent.com/TsaiFisher/smartfactory-licensing/main/sunset.json";

    /// <summary>本機位置：<c>License:SunsetPath</c>，預設在 license.json 旁。離線部署由管理人員放這裡。</summary>
    public static string LocalPath(IConfiguration configuration)
        => configuration["License:SunsetPath"]
           ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(LicenseValidator.ResolveLicensePath(configuration)))!, "sunset.json");

    /// <summary>本機有簽章有效的終止聲明嗎？任何失敗都回 false。</summary>
    public static bool IsActive(IConfiguration configuration, RSA? rootPublicKey = null)
    {
        var path = LocalPath(configuration);
        if (!File.Exists(path)) return false;
        try
        {
            LicenseDocuments.ParseSunset(File.ReadAllText(path), rootPublicKey ?? LicenseValidator.LoadEmbeddedPublicKey());
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}

/// <summary>
/// 每日讀取一次公開的 sunset.json（平常是 404）。讀到有效聲明就存到本機——之後斷網、重啟都仍然解除——
/// 並立即更新狀態。離線部署連不到也無妨，由管理人員手動放檔。
/// </summary>
public sealed class LicenseSunsetChecker(
    HttpClient http,
    IConfiguration configuration,
    ILicenseStateService state,
    ILogger<LicenseSunsetChecker> logger,
    RSA? rootPublicKeyOverride = null)
{
    public async Task<bool> CheckAsync(CancellationToken ct = default)
    {
        if (state.Current.State == LicenseState.Unlocked) return true;

        var url = configuration["License:SunsetUrl"] ?? LicenseSunset.DefaultUrl;
        string json;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var res = await http.GetAsync(url, cts.Token);
            if (!res.IsSuccessStatusCode) return false;   // 404 是常態
            json = await res.Content.ReadAsStringAsync(cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;   // 連不上 ≠ 終止
        }

        try
        {
            LicenseDocuments.ParseSunset(json, rootPublicKeyOverride ?? LicenseValidator.LoadEmbeddedPublicKey());
        }
        catch (Exception ex) when (ex is SecurityException or InvalidOperationException)
        {
            logger.LogWarning("[Sunset] {Url} 的內容不是有效的終止聲明，忽略：{Message}", url, ex.Message);
            return false;
        }

        var path = LicenseSunset.LocalPath(configuration);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, path, overwrite: true);
        state.SetSunset();
        logger.LogWarning("[Sunset] 已讀到有效的終止聲明，授權控管永久解除（已存至 {Path}）", path);
        return true;
    }
}

/// <summary>啟動後 2 分鐘檢查一次，之後每 24 小時（短睡長等，理由同 LeaseRenewalService）。</summary>
public sealed class LicenseSunsetService(IServiceScopeFactory scopes, TimeProvider time, ILogger<LicenseSunsetService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var due = time.GetUtcNow() + TimeSpan.FromMinutes(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (time.GetUtcNow() < due)
                {
                    var left = due - time.GetUtcNow();
                    await Task.Delay(left < TimeSpan.FromMinutes(10) ? left : TimeSpan.FromMinutes(10), stoppingToken);
                }
                using var scope = scopes.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<LicenseSunsetChecker>().CheckAsync(stoppingToken))
                    return;   // 已解除，不必再查
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { logger.LogError(ex, "[Sunset] 檢查終止聲明時發生錯誤"); }
            due = time.GetUtcNow() + TimeSpan.FromHours(24);
        }
    }
}
