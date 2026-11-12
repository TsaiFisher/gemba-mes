using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services;

/// <summary>
/// 系統參數「線上授權確認」（SECURITY-OPERATIONS.md §18.15.8）。
///
/// <para>實際是否需要確認 = 授權檔簽章要求（lease／ci）OR 執行期偵測到 VM／容器 OR 這個參數。
/// 參數存在資料庫，持有資料庫的人改得動，所以它<b>只能加嚴</b>：VM 強制與簽章要求都不讀它，
/// 從資料庫把它改成停用，VM 一樣要確認。</para>
///
/// <para>啟用前必須已有有效的確認——讓「啟用」本身就證明這台機器確認得了，
/// 寬限期（確認的有效期限）從那一刻開始算；連不上不會轉唯讀，到期才唯讀。</para>
/// </summary>
public static class LicenseOnlineCheck
{
    /// <summary>讀系統參數。讀不到（資料庫問題）視同未啟用並記錄——自選的加嚴不能因為讀取失敗讓正版停擺。</summary>
    public static async Task<bool> ReadOptInAsync(IServiceProvider services)
    {
        try
        {
            var value = await services.GetRequiredService<ISystemParamService>().GetAsync(SystemParamKeys.LicenseOnlineCheck);
            return IsEnabledValue(value);
        }
        catch (Exception ex)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger(typeof(LicenseOnlineCheck))
                .LogWarning(ex, "[License] 無法讀取系統參數「線上授權確認」，視同未啟用");
            return false;
        }
    }

    public static bool IsEnabledValue(string? value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 檢查能否變更（純函式）。回傳 null 表示可以；否則為 SharedResource 鍵。
    /// </summary>
    public static string? CheckChange(LicenseStateInputs? inputs, bool enable, DateTimeOffset now)
    {
        if (inputs is null || inputs.BindingMode != LicenseBindingModes.Offline)
            return "Msg_LicenseOnlineCheckNotOffline";          // lease／ci 本來就要確認，參數不適用
        if (!enable)
            return inputs.LeaseForcedByHost ? "Msg_LicenseOnlineCheckForcedByHost" : null;
        var confirmed = inputs.LeaseNotAfter is { } notAfter && notAfter > now && inputs.LeaseNoticeAt is null;
        return confirmed ? null : "Msg_LicenseOnlineCheckConfirmFirst";
    }
}
