using System.Text;
using System.Text.Json;

namespace SmartFactory.MES.Services;

/// <summary>
/// 登記用的申請碼：base64url(JSON { fp, tpm, ver, deploy, hostId })，交給授權伺服器的 /register 頁。
/// <b>只含指紋與主機識別碼（皆為雜湊）</b>，錨定碼與主機板 UUID 原文不離機（§16.5、§18.15.4）。
/// <para>獨立成檔、只用 BCL：除主專案外也以 Link 共用於交付客戶的 MachineId 工具——
/// 全新安裝還沒有授權、系統起不來時，申請碼只能從那支工具取得。</para>
/// </summary>
public static class LicenseRequestCode
{
    public const string DefaultServerUrl = "https://license.gemba-mes.workers.dev";

    /// <summary>
    /// 申請碼：base64url(JSON { fp, tpm, ver, deploy, hostId })（SECURITY-OPERATIONS.md §18.15.4）。
    /// <c>hostId</c> 是雜湊、取不到時為 null；<b>不含錨定碼與主機板 UUID 原值</b>。
    /// 核發端（LicenseAuthority <c>--request</c>）依 deploy 決定綁定方式。
    /// </summary>
    public static string Build(string fingerprint, bool hasTpm, string appVersion, HostIdentityResult? host = null)
    {
        var json = JsonSerializer.Serialize(new
        {
            fp = fingerprint, tpm = hasTpm, ver = appVersion,
            deploy = host?.DeployCode ?? "unknown", hostId = host?.HostId,
        });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string RegisterUrl(string serverUrl, string code) => $"{serverUrl.TrimEnd('/')}/register?code={code}";

    /// <summary>換發頁（換電腦、換主機板）。帶申請碼時頁面自動帶入；沒有申請碼（錨定檔讀不到）時讓使用者貼上。</summary>
    public static string RenewUrl(string serverUrl, string? code = null) =>
        $"{serverUrl.TrimEnd('/')}/renew" + (code is null ? "" : $"?code={code}");
}
