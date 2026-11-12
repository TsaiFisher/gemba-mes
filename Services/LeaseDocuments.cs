using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartFactory.MES.Services;

/// <summary>
/// 租約伺服器回應的驗證（SECURITY-OPERATIONS.md §18.4、§18.6）。純函式。
///
/// <para>回應格式：<c>{ payload: "&lt;JSON 字串&gt;", signature: "&lt;base64 P1363&gt;", delegation: {...} }</c>。
/// 對 payload 的<b>原始位元組</b>驗簽——不重建、不正規化，兩種語言不必對 JSON 取得共識。</para>
///
/// <para>信任鏈：內嵌根公鑰 → 委任憑證（用途 lease）→ 從屬公鑰 → payload。委任憑證以
/// <b>payload 的 issuedAt</b> 判定效期（簽發當時有效即可），否則換從屬金鑰時，手上還有效的
/// 租約會在委任憑證到期那一刻全部失效。</para>
///
/// <para><c>lease.json</c> 存的就是這份回應原文，啟動時重新驗一次——改檔延長 notAfter 會驗不過。</para>
/// </summary>
public static class LeaseDocuments
{
    public const string TypeLease = "lease";
    public const string TypeNotice = "lease-notice";

    /// <summary>已驗簽、且屬於這張授權與這台機器的回應內容。</summary>
    public static LeasePayload Verify(string responseJson, RSA rootPublicKey, string licenseId, string fingerprint)
    {
        SignedResponse r;
        try
        {
            r = JsonSerializer.Deserialize<SignedResponse>(responseJson)
                ?? throw new SecurityException("租約回應為空。");
        }
        catch (JsonException ex)
        {
            throw new SecurityException($"租約回應格式錯誤：{ex.Message}");
        }
        if (string.IsNullOrEmpty(r.Payload) || string.IsNullOrEmpty(r.Signature) || r.Delegation is null)
            throw new SecurityException("租約回應缺少 payload、signature 或 delegation。");

        LeasePayload p;
        try
        {
            p = JsonSerializer.Deserialize<LeasePayload>(r.Payload)
                ?? throw new SecurityException("租約內容為空。");
        }
        catch (JsonException ex)
        {
            throw new SecurityException($"租約內容格式錯誤：{ex.Message}");
        }
        if (p.Version != 1 || p.Type is not (TypeLease or TypeNotice))
            throw new SecurityException($"不支援的租約格式（type={p.Type}, version={p.Version}）。");

        var cert = LicenseDocuments.ParseDelegation(r.Delegation.Value.GetRawText());
        using var sub = LicenseDocuments.VerifyDelegation(cert, rootPublicKey, LicenseDocuments.PurposeLease, p.IssuedAtOrServerTime);

        byte[] sig;
        try { sig = Convert.FromBase64String(r.Signature); }
        catch (FormatException) { throw new SecurityException("租約簽章格式錯誤。"); }
        if (!sub.VerifyData(Encoding.UTF8.GetBytes(r.Payload), sig, HashAlgorithmName.SHA256))
            throw new SecurityException("租約簽章無效。");

        // 簽章對了，還要是「這張授權、這台機器」的：別台的租約搬過來不能用
        if (!string.Equals(p.LicenseId, licenseId, StringComparison.Ordinal))
            throw new SecurityException($"租約屬於另一張授權（{p.LicenseId}）。");
        if (!string.Equals(p.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("租約屬於另一台機器。");

        if (p.Type == TypeLease && (p.NotAfter is null || p.Token is null))
            throw new SecurityException("租約缺少 notAfter 或啟用碼。");
        return p;
    }

    private sealed record SignedResponse(
        [property: JsonPropertyName("payload")]    string? Payload,
        [property: JsonPropertyName("signature")]  string? Signature,
        [property: JsonPropertyName("delegation")] JsonElement? Delegation);
}

/// <summary>租約或通知的內容（已驗簽後才會被使用）。</summary>
public sealed record LeasePayload(
    [property: JsonPropertyName("type")]        string Type,
    [property: JsonPropertyName("version")]     int Version,
    [property: JsonPropertyName("licenseId")]   string LicenseId,
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("status")]      string Status,
    [property: JsonPropertyName("serverTime")]  DateTimeOffset ServerTime,
    [property: JsonPropertyName("token")]       string? Token = null,
    [property: JsonPropertyName("issuedAt")]    DateTimeOffset? IssuedAt = null,
    [property: JsonPropertyName("notAfter")]    DateTimeOffset? NotAfter = null,
    [property: JsonPropertyName("reason")]      string? Reason = null)
{
    [JsonIgnore] public DateTimeOffset IssuedAtOrServerTime => IssuedAt ?? ServerTime;

    /// <summary>已簽章且足以提前轉唯讀的通知（§18.8：conflict 不算，被誤判的可能是原機）。</summary>
    [JsonIgnore] public bool IsTerminalNotice => Type == LeaseDocuments.TypeNotice && Status is "revoked" or "replaced";
}
