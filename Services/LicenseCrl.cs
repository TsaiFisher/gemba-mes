using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartFactory.MES.Services;

/// <summary>
/// 授權撤銷清單（CRL）的文件格式與**純邏輯**：簽章 payload、驗簽、撤銷查詢、狀態判定。
///
/// <para>
/// 設計約束（見 SECURITY-OPERATIONS.md §15）：
/// </para>
/// <list type="number">
///   <item><b>單向加嚴</b>：CRL 只能讓有效授權失效，永遠不能讓無效授權變有效。
///         抓不到、驗不過、格式不認得 —— 一律視為「沒有撤銷資訊」，不是「放行」。</item>
///   <item><b>不動 license.json</b>：撤銷以 <c>licenseId</c> 為鍵。
///         若把撤銷資訊塞進授權檔，<c>LicenseValidator.BuildSignaturePayload</c> 就得改，
///         而那會讓**所有既有授權的簽章同時失效**，且要同步四處實作。</item>
///   <item><b>同一把 RSA 私鑰</b>：CRL 用核發授權的同一把私鑰簽，客戶端用已釘住的
///         <c>Resources/license-public.pem</c> 驗。不新增任何金鑰、不新增信任根。</item>
/// </list>
/// </summary>
internal static class LicenseCrl
{
    /// <summary>目前支援的 CRL 格式版本。</summary>
    internal const int SupportedVersion = 1;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// 簽署 payload：排除 <c>signature</c> 欄位，deterministic JSON，撤銷項以 licenseId 排序。
    /// <b>欄位順序與內容必須與 LicenseAuthority 的 <c>revoke</c> 指令完全一致</b>，
    /// 任一處不同步都會導致驗簽失敗。
    /// </summary>
    internal static string BuildSignaturePayload(CrlDocument crl)
    {
        return JsonSerializer.Serialize(new
        {
            version    = crl.Version,
            issuedAt   = crl.IssuedAt,
            nextUpdate = crl.NextUpdate,
            revoked    = crl.Revoked
                .OrderBy(e => e.LicenseId, StringComparer.Ordinal)
                .Select(e => new
                {
                    licenseId = e.LicenseId,
                    reason    = e.Reason,
                    revokedAt = e.RevokedAt
                })
                .ToArray()
        }, JsonOptions);
    }

    /// <summary>
    /// 解析並驗簽。任何失敗都丟例外 —— 呼叫端必須把例外當成「沒有撤銷資訊」，
    /// 而不是「沒有被撤銷」。
    /// </summary>
    internal static CrlDocument ParseAndVerify(string json, RSA publicKey)
    {
        CrlDocument crl;
        try
        {
            crl = JsonSerializer.Deserialize<CrlDocument>(json, JsonOptions)
                ?? throw new InvalidOperationException("撤銷清單格式錯誤（反序列化結果為 null）。");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"撤銷清單解析失敗：{ex.Message}");
        }

        // 版本檢查必須在驗簽之前：未來版本的 payload 欄位不同，簽章必定驗不過，
        // 若順序顛倒會得到「簽章無效、可能已被竄改」這個指向錯誤方向的訊息。
        // （與 LicenseValidator 的演算法版本檢查同一個教訓，見 §14.9。）
        if (crl.Version != SupportedVersion)
            throw new InvalidOperationException(
                $"撤銷清單格式版本為 {crl.Version}，本系統支援 {SupportedVersion}。請更新系統版本。");

        if (string.IsNullOrWhiteSpace(crl.Signature))
            throw new SecurityException("撤銷清單沒有簽章。");

        if (!VerifySignature(crl, publicKey))
            throw new SecurityException("撤銷清單簽章無效，可能已被竄改或非 SmartFactory 簽發。");

        return crl;
    }

    private static bool VerifySignature(CrlDocument crl, RSA publicKey)
    {
        try
        {
            var payloadBytes   = Encoding.UTF8.GetBytes(BuildSignaturePayload(crl));
            var signatureBytes = Convert.FromBase64String(crl.Signature);
            return publicKey.VerifyData(
                payloadBytes, signatureBytes,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>licenseId 是否在撤銷清單中（大小寫不敏感，與指紋比對一致）。</summary>
    internal static CrlEntry? FindRevocation(CrlDocument crl, string licenseId)
        => crl.Revoked.FirstOrDefault(
            e => string.Equals(e.LicenseId, licenseId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 狀態判定。**純函式**：時間由呼叫端傳入，沒有隱藏的 <c>DateTimeOffset.UtcNow</c>。
    /// </summary>
    /// <param name="crl">目前可用的最新已驗簽清單（本輪抓到的或快取的）；null 表示完全沒有。</param>
    /// <param name="fetchedNow">本輪是否成功抓到一份「未過 nextUpdate」的清單。</param>
    /// <param name="lastSuccessUtc">上次成功取得清單的時間；從未成功則為 null。</param>
    /// <param name="anchorUtc">從未成功過時，寬限窗的計時起點（首次嘗試時間）。</param>
    internal static CrlEvaluation Evaluate(
        CrlDocument? crl,
        bool fetchedNow,
        DateTimeOffset? lastSuccessUtc,
        DateTimeOffset anchorUtc,
        string licenseId,
        int graceDays,
        DateTimeOffset now)
    {
        // 1. 撤銷優先於一切。即使這份清單是快取的、甚至已過 nextUpdate，
        //    「曾經看過一份合法簽章說這張授權被撤銷」就足以判定 —— 這是單向加嚴：
        //    斷網不會讓已撤銷的授權復活。
        if (crl is not null)
        {
            var hit = FindRevocation(crl, licenseId);
            if (hit is not null)
                return new CrlEvaluation(
                    CrlOutcome.Revoked,
                    $"授權 {licenseId} 已於 {hit.RevokedAt:yyyy-MM-dd} 被撤銷" +
                    $"（原因：{hit.Reason}）。\n\n{LicenseSupport.HowToObtain}",
                    hit.RevokedAt, hit.Reason);
        }

        // 2. 寬限窗。計時起點是「上次成功取得清單」，從未成功過則是首次嘗試時間。
        var since = lastSuccessUtc ?? anchorUtc;
        var age   = now - since;
        if (age > TimeSpan.FromDays(graceDays))
            return new CrlEvaluation(
                CrlOutcome.GraceExpired,
                $"已連續 {age.TotalDays:F0} 天無法取得授權撤銷清單（寬限 {graceDays} 天已用盡）。" +
                "請確認本機可連線至撤銷清單網址。\n\n" + LicenseSupport.HowToObtain);

        if (fetchedNow)
            return new CrlEvaluation(CrlOutcome.Ok, "授權未被撤銷");

        // 3. 抓不到但還在寬限窗內：照常運作，只留 Warning。
        //    產線系統不因外部網路故障停擺 —— 把「連不上」變成可偵測狀態，
        //    而不是可被利用的繞過點（見 §15 的 fail-open 決策）。
        var remaining = TimeSpan.FromDays(graceDays) - age;
        return new CrlEvaluation(
            CrlOutcome.Stale,
            $"已 {age.TotalDays:F0} 天未取得授權撤銷清單，寬限還剩 {remaining.TotalDays:F0} 天。");
    }
}

public enum CrlOutcome
{
    /// <summary>未啟用撤銷檢查（離線部署）。</summary>
    Disabled,
    /// <summary>本輪取得清單，授權未被撤銷。</summary>
    Ok,
    /// <summary>本輪取不到清單，但仍在寬限窗內 —— 不影響運作。</summary>
    Stale,
    /// <summary>授權已被撤銷。</summary>
    Revoked,
    /// <summary>連續取不到清單且寬限窗已用盡。</summary>
    GraceExpired
}

public readonly record struct CrlEvaluation(
    CrlOutcome Outcome, string Message, DateTimeOffset? RevokedAt = null, string? RevokedReason = null)
{
    /// <summary>是否應讓 /health 轉為 Unhealthy。</summary>
    public bool IsHealthy => Outcome is not (CrlOutcome.Revoked or CrlOutcome.GraceExpired);
}

internal sealed record CrlEntry(
    [property: JsonPropertyName("licenseId")] string LicenseId,
    [property: JsonPropertyName("reason")]    string Reason,
    [property: JsonPropertyName("revokedAt")] DateTimeOffset RevokedAt
);

internal sealed record CrlDocument(
    [property: JsonPropertyName("version")]    int Version,
    [property: JsonPropertyName("issuedAt")]   DateTimeOffset IssuedAt,
    [property: JsonPropertyName("nextUpdate")] DateTimeOffset NextUpdate,
    [property: JsonPropertyName("revoked")]    CrlEntry[] Revoked,
    [property: JsonPropertyName("signature")]  string Signature
);
