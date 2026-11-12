using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartFactory.MES.Services;

public class LicenseValidator : ILicenseValidator
{
    private readonly IEnvironmentFingerprintService _fingerprintService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LicenseValidator> _logger;
    private readonly IHostEnvironment _env;
    private readonly Func<HostIdentityResult> _hostDetector;

    // 公鑰在 build 時嵌入為 Embedded Resource（SmartFactory 管控，顧問無法替換私鑰）
    // Phase 2：此驗證邏輯移入 Security Anchor DLL，Strong Name 防止 DLL 被替換
    private static readonly RSA PublicKey = LoadEmbeddedPublicKey();

    // SMARTFACTORY-INTERNAL clientCode 識別字：此值在 RSA 簽章 payload 中，無法偽造
    internal const string InternalClientCode = "SMARTFACTORY-INTERNAL";

    /// <summary>
    /// 唯一接受的授權檔格式版本（SECURITY-OPERATIONS.md §18.5）。目前沒有已交付客戶，
    /// 不相容 v2；舊授權一律需要重新核發。
    /// </summary>
    internal const int CurrentFormatVersion = 3;

    public LicenseValidator(
        IEnvironmentFingerprintService fingerprintService,
        IConfiguration configuration,
        ILogger<LicenseValidator> logger,
        IHostEnvironment env,
        Func<HostIdentityResult>? hostDetector = null)
    {
        _hostDetector = hostDetector ?? HostIdentity.Detect;
        _fingerprintService = fingerprintService;
        _configuration = configuration;
        _logger = logger;
        _env = env;
    }

    /// <summary>授權檔位置：<c>License:Path</c>，未設定時為應用程式目錄下的 license.json。</summary>
    public static string ResolveLicensePath(IConfiguration configuration)
        => configuration["License:Path"] ?? Path.Combine(AppContext.BaseDirectory, "license.json");

    public LicenseContext Validate() => ValidateFile(ResolveLicensePath(_configuration));

    /// <summary>
    /// 驗證指定路徑的授權檔（簽章、格式、結構、指紋）。匯入新授權時先以暫存檔走一次，
    /// 驗過才覆蓋正式檔——不能讓一份驗不過的檔案把系統變成起不來。
    /// </summary>
    public LicenseContext ValidateFile(string licensePath)
    {
        if (!File.Exists(licensePath))
            throw new InvalidOperationException(
                $"找不到授權檔案：{licensePath}\n\n{LicenseSupport.HowToObtain}");

        LicenseFile license;
        try
        {
            var json = File.ReadAllText(licensePath);
            license = JsonSerializer.Deserialize<LicenseFile>(json, JsonOptions)
                ?? throw new InvalidOperationException("授權檔案格式錯誤（反序列化失敗）。");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"授權檔案解析失敗：{ex.Message}");
        }

        // 0. 驗證授權檔格式版本
        //    與下一步同理，必須在簽章驗證之前：v2 授權的 payload 少了 v3 欄位，簽章必定驗不過。
        EnsureFormatVersion(license.FormatVersion);

        // 1. 驗證指紋演算法版本
        //    必須在簽章驗證之前：舊版授權的 payload 欄位不同，簽章必定驗不過，
        //    若順序顛倒會得到「簽章無效、可能已被竄改」這個指向錯誤方向的訊息。
        if (!string.Equals(license.NormalizedAlgo, MachineAnchor.AlgoVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"此授權以舊版指紋演算法（{license.NormalizedAlgo}）核發，" +
                $"目前系統使用 {MachineAnchor.AlgoVersion}。\n" +
                "需要重新核發授權。\n\n" + LicenseSupport.HowToObtain);

        // 2. 驗證 RSA 簽章
        if (!VerifySignature(license))
            throw new SecurityException(
                "授權檔案簽章無效，可能已被竄改，需要重新核發。\n\n" + LicenseSupport.HowToObtain);

        // 3. 有效期限不在這裡擋（§18.8）：到期轉唯讀、再 30 天停用，由 LicenseStateService
        //    依 ExpiresAt 判定。這裡若拋例外，到期當天系統就起不來，連「唯讀查資料」都做不到。

        // 3b. 結構檢查：簽章驗得過只代表是我方簽的，不代表欄位組合合理
        EnsureStructure(license);

        // 4. 依綁定方式驗證這台機器
        var isInternal = string.Equals(license.ClientCode, InternalClientCode, StringComparison.OrdinalIgnoreCase);
        EnsureBindingModeSupported(license.BindingMode);

        // 4a. 錨定碼來源（所有模式）；讀不到錨定碼一律起不來——租約也是綁指紋發的
        EnsureAnchorSourceAllowed(_fingerprintService.IsAnchorFromEnvironmentVariable, license.ClientCode);
        var currentFingerprint = _fingerprintService.GetFingerprint();

        // 4b. offline：指紋必須在清單內（不支援萬用字元）。lease 的機器由租約決定（LeaseRenewer）
        if (license.BindingMode == LicenseBindingModes.Offline
            && !license.AllowedFingerprints.Contains(currentFingerprint, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogError("授權驗證失敗：環境指紋不符 (fingerprint: {Fp}...)",
                currentFingerprint[..Math.Min(16, currentFingerprint.Length)]);
            throw new SecurityException(
                $"目前環境未在授權清單中。\n請以下列指紋申請授權：\n{currentFingerprint}\n\n" + LicenseSupport.HowToObtain);
        }

        // 4c. 主機識別碼（§18.15.9）：授權有列主機才比；不符或讀不到一律擋下——系統不啟動、不連線資料庫，
        //     只開授權頁。改版前是「不符唯讀、讀不到提醒」，唯讀仍看得到全部資料
        HostIdentityResult? host;
        try
        {
            host = LicenseHostGate.Enforce(license.HostIds, _hostDetector);
        }
        catch (SecurityException ex) when (LicenseHostGate.ReasonOf(ex) is { } reason)
        {
            _logger.LogCritical("[License] 主機識別碼閘門擋下（{Reason}）：{Message}", reason, ex.Message);
            throw;
        }
        var hostCheck = host is null ? HostCheckResult.NotRequired : HostCheckResult.Match;
        var hostKind = host?.Kind ?? DeploymentKind.Unknown;

        // 5. Internal License 僅限 Development 環境使用
        if (isInternal && !_env.IsDevelopment())
            throw new SecurityException(
                "SmartFactory Internal License 僅限開發環境使用，生產環境需要正式授權。\n\n"
                + LicenseSupport.HowToObtain);

        if (isInternal)
            _logger.LogWarning(
                "[License] 使用 SmartFactory Internal License（開發用途）| 到期：{Expiry} | 禁止部署至生產環境",
                LicenseSupport.FormatExpiry(license.ExpiresAt));
        else
            _logger.LogInformation(
                "授權驗證通過 | 企業：{Client} | 到期：{Expiry}",
                license.ClientName, LicenseSupport.FormatExpiry(license.ExpiresAt));

        return new LicenseContext
        {
            LicenseId  = license.LicenseId,
            ClientCode = license.ClientCode,
            ClientName = license.ClientName,
            ExpiresAt  = license.ExpiresAt,
            AllowPlainConnection = license.AllowPlainConnection,
            BindingMode = license.BindingMode!,
            Seats      = license.Seats,
            HostCheck  = hostCheck,
            HostKind   = hostKind,
        };
    }

    /// <summary>格式版本不是 v3 時拒絕，訊息指向「重新核發」而不是「被竄改」。</summary>
    internal static void EnsureFormatVersion(int formatVersion)
    {
        if (formatVersion == CurrentFormatVersion) return;

        // 欄位缺省 = v3 之前核發的授權（v2 沒有這個欄位）
        var shown = formatVersion <= 0 ? "v2 或更早" : $"v{formatVersion}";
        throw new InvalidOperationException(
            $"此授權檔為舊格式（{shown}），本版本只接受 v{CurrentFormatVersion}，需要重新核發。\n\n" +
            LicenseSupport.HowToObtain);
    }

    /// <summary>
    /// 欄位組合檢查（純函式，供測試）。這些錯只會來自核發端的錯誤，但驗證端不能假設核發端永遠正確：
    /// 例如 <c>lease</c> 授權若帶了指紋清單，日後租約用戶端實作時就會出現「兩套規則哪個算數」的歧義。
    /// </summary>
    internal static void EnsureStructure(LicenseFile license)
    {
        if (!LicenseBindingModes.IsKnown(license.BindingMode))
            throw new SecurityException(
                $"授權檔的綁定方式「{license.BindingMode}」無法辨識，需要重新核發。");

        if (license.Seats < 1)
            throw new SecurityException($"授權檔的座數（{license.Seats}）無效，需要重新核發。");

        var hasFingerprints = license.AllowedFingerprints is { Length: > 0 };
        if (license.BindingMode == LicenseBindingModes.Offline && !hasFingerprints)
            throw new SecurityException("離線授權必須列出機器指紋，此授權檔沒有，需要重新核發。");
        if (license.BindingMode != LicenseBindingModes.Offline && hasFingerprints)
            throw new SecurityException(
                $"綁定方式為 {license.BindingMode} 的授權不得列出機器指紋（機器由租約或綁定憑證決定），需要重新核發。");

        // 主機識別碼（§18.15.5）：offline 正式授權必須綁主機——否則整顆磁碟複製到別台照樣能用。
        // 內部授權可省略（CI runner 每次都是不同機器）；lease／ci 由租約決定機器，不得同時列主機
        var hasHosts = license.HostIds is { Length: > 0 };
        var isInternal = string.Equals(license.ClientCode, InternalClientCode, StringComparison.OrdinalIgnoreCase);
        if (license.BindingMode == LicenseBindingModes.Offline && !hasHosts && !isInternal)
            throw new SecurityException("離線授權必須列出主機識別碼（hostIds），此授權檔沒有，需要重新核發。");
        if (license.BindingMode != LicenseBindingModes.Offline && hasHosts)
            throw new SecurityException(
                $"綁定方式為 {license.BindingMode} 的授權不得列出主機識別碼，需要重新核發。");
        if (hasHosts && license.HostIds!.Any(h => h is null || !HostIdPattern.IsMatch(h)))
            throw new SecurityException("授權檔的主機識別碼格式無效，需要重新核發。");
    }

    /// <summary>
    /// <c>offline</c> 以指紋清單放行；<c>lease</c> 放行啟動，但沒有有效租約時唯讀（由狀態機判定）。
    /// <c>tpm</c>（第二期）與 <c>ci</c>（OIDC 用戶端未實作）仍一律不放行——§18.5：「offline 以外沒有
    /// 租約或綁定憑證一律不放行」，不可以退回指紋比對。
    /// </summary>
    internal static void EnsureBindingModeSupported(string? bindingMode)
    {
        // lease：啟動放行，寫入與否由租約決定（沒有有效租約即唯讀，LicenseStateEvaluator）
        if (bindingMode is LicenseBindingModes.Offline or LicenseBindingModes.Lease) return;

        throw new SecurityException(
            $"此授權的綁定方式為 {bindingMode}，需要線上租約或 TPM 綁定，本版本尚未支援。\n" +
            "請改申請離線授權（bindingMode = offline）。\n\n" + LicenseSupport.HowToObtain);
    }

    /// <summary>
    /// 以環境變數 <c>SMARTFACTORY_INSTANCE_ID</c> 指定的錨定碼，只接受內部授權。
    /// <para>環境變數是部署端一行就能設的東西：讓正式授權接受它，等於在錨定檔
    /// （以及 Windows 上的 DPAPI 封裝）旁邊開一條不需要任何檔案的捷徑。
    /// 內部授權本身限 Development，CI 與測試照舊可用。SECURITY-OPERATIONS.md §18.11。</para>
    /// </summary>
    internal static void EnsureAnchorSourceAllowed(bool anchorFromEnvironmentVariable, string clientCode)
    {
        if (!anchorFromEnvironmentVariable) return;
        if (string.Equals(clientCode, InternalClientCode, StringComparison.OrdinalIgnoreCase)) return;

        throw new SecurityException(
            $"此授權不接受以環境變數 {MachineAnchor.InstanceIdEnvVar} 指定的錨定碼（僅限內部開發／CI 授權）。\n" +
            $"請移除該環境變數，改用錨定檔（SmartFactory.MES.MachineId --init；容器請以 volume 掛載錨定檔）。");
    }

    private static bool VerifySignature(LicenseFile license)
    {
        try
        {
            var payloadBytes   = Encoding.UTF8.GetBytes(BuildSignaturePayload(license));
            var signatureBytes = Convert.FromBase64String(license.Signature);
            return PublicKey.VerifyData(
                payloadBytes, signatureBytes,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 簽署 payload：排除 Signature 欄位，deterministic JSON，fingerprints 排序。
    /// <b>欄位順序與內容必須與 LicenseAuthority、SecurityAnchor 三處完全一致</b>，
    /// 任一處不同步都會導致簽章驗證失敗。
    /// </summary>
    internal static string BuildSignaturePayload(LicenseFile license)
    {
        return JsonSerializer.Serialize(new
        {
            formatVersion        = license.FormatVersion,
            licenseId            = license.LicenseId,
            clientCode           = license.ClientCode,
            clientName           = license.ClientName,
            issuedAt             = license.IssuedAt,
            expiresAt            = license.ExpiresAt,
            bindingMode          = license.BindingMode,
            seats                = license.Seats,
            allowedFingerprints  = (license.AllowedFingerprints ?? []).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            hostIds              = (license.HostIds ?? []).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            fingerprintAlgo      = license.NormalizedAlgo,
            allowPlainConnection = license.AllowPlainConnection
        }, JsonOptions);
    }

    /// <summary>
    /// 載入嵌入式公鑰。<c>internal</c> 是為了讓 <see cref="LicenseCrlService"/> 共用同一把
    /// 已釘住的公鑰 —— 撤銷清單不新增信任根。每個呼叫端各取一份實例（RSA 不保證執行緒安全）。
    /// </summary>
    internal static RSA LoadEmbeddedPublicKey()
        => LoadEmbeddedPublicKey("SmartFactory.MES.Resources.license-public.pem");

    private static RSA LoadEmbeddedPublicKey(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"找不到嵌入式公鑰資源 {resourceName}，請確認 csproj 中已設定 EmbeddedResource。");
        using var reader = new StreamReader(stream);
        var pem = reader.ReadToEnd();
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa;
    }

    private static readonly System.Text.RegularExpressions.Regex HostIdPattern = new("^[0-9A-Fa-f]{64}$");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}

internal record LicenseFile(
    [property: JsonPropertyName("licenseId")]           string LicenseId,
    [property: JsonPropertyName("clientCode")]          string ClientCode,
    [property: JsonPropertyName("clientName")]          string ClientName,
    [property: JsonPropertyName("issuedAt")]            DateTimeOffset IssuedAt,
    [property: JsonPropertyName("expiresAt")]           DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("allowedFingerprints")] string[] AllowedFingerprints,
    [property: JsonPropertyName("signature")]           string Signature,
    [property: JsonPropertyName("fingerprintAlgo")]      string? FingerprintAlgo = null,
    [property: JsonPropertyName("allowPlainConnection")] bool AllowPlainConnection = false,
    [property: JsonPropertyName("formatVersion")]        int FormatVersion = 0,
    [property: JsonPropertyName("bindingMode")]          string? BindingMode = null,
    [property: JsonPropertyName("seats")]                int Seats = 1,
    [property: JsonPropertyName("hostIds")]              string[]? HostIds = null
)
{
    /// <summary>欄位缺省時視為 v1（2026-09-14 之前核發的授權）。</summary>
    [JsonIgnore]
    internal string NormalizedAlgo =>
        string.IsNullOrWhiteSpace(FingerprintAlgo) ? "v1" : FingerprintAlgo.Trim();
}
