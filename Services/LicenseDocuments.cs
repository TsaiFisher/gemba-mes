using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartFactory.MES.Services;

/// <summary>
/// 根金鑰簽章的三種授權文件（SECURITY-OPERATIONS.md §18.4、§18.9）：
/// <list type="bullet">
///   <item><c>endpoints.json</c>：目前的租約伺服器網址。因為不註冊網域，換託管平台時只改這份。</item>
///   <item><c>sunset.json</c>：終止聲明。我方停止服務時永久解除控管。</item>
///   <item>委任憑證：授權從屬 ECDSA P-256 金鑰（Worker Secret）簽租約／TPM 綁定憑證。</item>
/// </list>
///
/// <para><b>共同規則</b>（與 <see cref="LicenseCrl"/> 同一套）：</para>
/// <list type="number">
///   <item>同一把根 RSA 金鑰，以已釘住的 <c>Resources/license-public.pem</c> 驗，不新增信任根。</item>
///   <item>payload 第一個欄位後緊接 <c>type</c>：簽章涵蓋文件種類，一份文件的簽章不能挪去當另一種用。</item>
///   <item>版本與種類檢查在驗簽之前，舊版或認錯檔不會被報成「被竄改」。</item>
///   <item>任何失敗一律丟例外。呼叫端對 sunset 必須把例外當成「沒有終止聲明」——
///         讀不到絕不能等於解除（§18.9：不得以連不上自動解除）。</item>
/// </list>
///
/// <para><b>⚠️ payload 有第二份實作</b>：<c>SmartFactory.MES.LicenseAuthority</c>（不在版控）的
/// <c>endpoints</c>／<c>sunset</c>／<c>delegate</c> 指令。欄位順序不同的症狀只有「簽章無效」，
/// 字面已由 <c>LicenseDocumentsTests</c> 釘住。</para>
/// </summary>
internal static class LicenseDocuments
{
    internal const int SupportedVersion = 1;

    internal const string TypeEndpoints  = "endpoints";
    internal const string TypeSunset     = "sunset";
    internal const string TypeDelegation = "delegation";

    /// <summary>終止聲明目前唯一的範圍：全部部署。</summary>
    internal const string SunsetScopeAll = "all";

    /// <summary>委任用途：簽租約與拒絕通知。</summary>
    internal const string PurposeLease      = "lease";
    /// <summary>委任用途：簽 TPM 綁定憑證（第二期）。</summary>
    internal const string PurposeTpmBinding = "tpm-binding";

    /// <summary>
    /// 委任憑證效期上限。§18.4 定為 1 年；多給 1 天容許核發當天的時區與時刻差。
    /// 上限在驗證端，不在核發端——從屬金鑰外洩時，損害期間由這裡封頂。
    /// </summary>
    internal static readonly TimeSpan MaxDelegationValidity = TimeSpan.FromDays(366);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    // ── payload ─────────────────────────────────────────────────────────────

    /// <summary>伺服器順序有意義（依序嘗試），所以<b>不排序</b>。</summary>
    internal static string BuildPayload(EndpointsDocument d) => JsonSerializer.Serialize(new
    {
        version     = d.Version,
        type        = d.Type,
        issuedAt    = d.IssuedAt,
        leaseServers = d.LeaseServers,
    }, JsonOptions);

    internal static string BuildPayload(SunsetDocument d) => JsonSerializer.Serialize(new
    {
        version  = d.Version,
        type     = d.Type,
        scope    = d.Scope,
        issuedAt = d.IssuedAt,
    }, JsonOptions);

    internal static string BuildPayload(DelegationCertificate d) => JsonSerializer.Serialize(new
    {
        version      = d.Version,
        type         = d.Type,
        purposes     = d.Purposes.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
        subPublicKey = d.SubPublicKey,
        notBefore    = d.NotBefore,
        notAfter     = d.NotAfter,
    }, JsonOptions);

    // ── 解析與驗證 ──────────────────────────────────────────────────────────

    internal static EndpointsDocument ParseEndpoints(string json, RSA rootPublicKey)
    {
        var d = Deserialize<EndpointsDocument>(json, "伺服器清單");
        EnsureHeader(d.Version, d.Type, TypeEndpoints, "伺服器清單");
        EnsureSigned(BuildPayload(d), d.Signature, rootPublicKey, "伺服器清單");

        if (d.LeaseServers is not { Length: > 0 })
            throw new SecurityException("伺服器清單沒有任何租約伺服器。");
        foreach (var url in d.LeaseServers)
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
                throw new SecurityException($"伺服器清單含非 https 網址：{url}");
        return d;
    }

    internal static SunsetDocument ParseSunset(string json, RSA rootPublicKey)
    {
        var d = Deserialize<SunsetDocument>(json, "終止聲明");
        EnsureHeader(d.Version, d.Type, TypeSunset, "終止聲明");
        EnsureSigned(BuildPayload(d), d.Signature, rootPublicKey, "終止聲明");

        // 未來若有部分範圍的聲明，舊版客戶端必須當成「不適用」，而不是「全部解除」
        if (d.Scope != SunsetScopeAll)
            throw new InvalidOperationException($"終止聲明範圍「{d.Scope}」本版本不支援。");
        return d;
    }

    /// <summary>
    /// 驗證委任憑證並回傳從屬公鑰。<paramref name="purpose"/> 是呼叫端要它做的事，
    /// 憑證沒授權這個用途就拒絕；<paramref name="at"/> 是要判定有效的時點
    /// （TPM 綁定憑證以「簽發當時」為準，§18.4）。
    /// </summary>
    internal static ECDsa VerifyDelegation(
        DelegationCertificate d, RSA rootPublicKey, string purpose, DateTimeOffset at)
    {
        EnsureHeader(d.Version, d.Type, TypeDelegation, "委任憑證");
        EnsureSigned(BuildPayload(d), d.Signature, rootPublicKey, "委任憑證");

        if (d.Purposes.Any(p => p is not (PurposeLease or PurposeTpmBinding)))
            throw new SecurityException("委任憑證含無法辨識的用途。");
        if (!d.Purposes.Contains(purpose))
            throw new SecurityException($"委任憑證未授權用途「{purpose}」。");
        if (d.NotAfter <= d.NotBefore || d.NotAfter - d.NotBefore > MaxDelegationValidity)
            throw new SecurityException("委任憑證效期不合法（必須大於 0 且不超過 1 年）。");
        if (at < d.NotBefore || at > d.NotAfter)
            throw new SecurityException(
                $"委任憑證在 {at:yyyy-MM-dd} 不在效期內（{d.NotBefore:yyyy-MM-dd}～{d.NotAfter:yyyy-MM-dd}）。");

        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(d.SubPublicKey), out _);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            key.Dispose();
            throw new SecurityException("委任憑證的從屬公鑰無法解析。");
        }
        if (key.KeySize != 256)
        {
            key.Dispose();
            throw new SecurityException($"委任憑證的從屬公鑰必須是 ECDSA P-256（實際 {key.KeySize} 位元）。");
        }
        return key;
    }

    internal static DelegationCertificate ParseDelegation(string json)
        => Deserialize<DelegationCertificate>(json, "委任憑證");

    private static T Deserialize<T>(string json, string what) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new InvalidOperationException($"{what}格式錯誤（反序列化結果為 null）。");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{what}解析失敗：{ex.Message}");
        }
    }

    private static void EnsureHeader(int version, string? type, string expectedType, string what)
    {
        if (type != expectedType)
            throw new InvalidOperationException($"這不是{what}（type = {type ?? "(無)"}）。");
        if (version != SupportedVersion)
            throw new InvalidOperationException(
                $"{what}格式版本為 {version}，本系統支援 {SupportedVersion}。請更新系統版本。");
    }

    private static void EnsureSigned(string payload, string? signature, RSA rootPublicKey, string what)
    {
        if (string.IsNullOrWhiteSpace(signature))
            throw new SecurityException($"{what}沒有簽章。");
        bool ok;
        try
        {
            ok = rootPublicKey.VerifyData(
                Encoding.UTF8.GetBytes(payload), Convert.FromBase64String(signature),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (FormatException)
        {
            ok = false;
        }
        if (!ok)
            throw new SecurityException($"{what}簽章無效，可能已被竄改或非 SmartFactory 簽發。");
    }
}

internal sealed record EndpointsDocument(
    [property: JsonPropertyName("version")]      int Version,
    [property: JsonPropertyName("type")]         string? Type,
    [property: JsonPropertyName("issuedAt")]     DateTimeOffset IssuedAt,
    [property: JsonPropertyName("leaseServers")] string[] LeaseServers,
    [property: JsonPropertyName("signature")]    string? Signature);

internal sealed record SunsetDocument(
    [property: JsonPropertyName("version")]   int Version,
    [property: JsonPropertyName("type")]      string? Type,
    [property: JsonPropertyName("scope")]     string? Scope,
    [property: JsonPropertyName("issuedAt")]  DateTimeOffset IssuedAt,
    [property: JsonPropertyName("signature")] string? Signature);

internal sealed record DelegationCertificate(
    [property: JsonPropertyName("version")]      int Version,
    [property: JsonPropertyName("type")]         string? Type,
    [property: JsonPropertyName("purposes")]     string[] Purposes,
    [property: JsonPropertyName("subPublicKey")] string SubPublicKey,
    [property: JsonPropertyName("notBefore")]    DateTimeOffset NotBefore,
    [property: JsonPropertyName("notAfter")]     DateTimeOffset NotAfter,
    [property: JsonPropertyName("signature")]    string? Signature);
