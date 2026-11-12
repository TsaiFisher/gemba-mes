namespace SmartFactory.MES.Services;

/// <summary>
/// 主機識別碼比對結果（SECURITY-OPERATIONS.md §18.15.2、§18.15.9）。
/// 不符與讀不到在驗證時就擋下（<see cref="LicenseHostGate"/>，系統不啟動），不會成為已驗證授權的狀態。
/// </summary>
public enum HostCheckResult
{
    /// <summary>授權沒有列主機（lease／ci／內部 CI）。</summary>
    NotRequired = 0,
    /// <summary>本機主機識別碼在授權清單內。</summary>
    Match = 1,
}

/// <summary>
/// License 驗證成功後的授權上下文。
/// 由 ILicenseValidator.Validate() 回傳，注入至 DI 容器供應用程式使用。
/// </summary>
public class LicenseContext
{
    public string LicenseId { get; init; } = string.Empty;
    public string ClientCode { get; init; } = string.Empty;
    public string ClientName { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// 是否允許使用明文連線字串。此旗標在 RSA 簽章 payload 內，部署端無法自行開啟。
    /// 見 SECURITY-OPERATIONS.md §14.3-C。
    /// </summary>
    public bool AllowPlainConnection { get; init; }

    /// <summary>綁定方式：<c>lease</c>／<c>tpm</c>／<c>offline</c>／<c>ci</c>（SECURITY-OPERATIONS.md §18.5）。</summary>
    public string BindingMode { get; init; } = string.Empty;

    /// <summary>同時可正常寫入的機器數，預設 1。</summary>
    public int Seats { get; init; } = 1;

    /// <summary>主機識別碼比對結果；驗證時判定，看門狗每輪重新判定。</summary>
    public HostCheckResult HostCheck { get; init; } = HostCheckResult.NotRequired;

    /// <summary>
    /// 執行期偵測到的部署類型（只在授權有列主機識別碼時偵測，否則 Unknown）。
    /// offline 授權偵測到 VM／容器 → 強制線上確認（SECURITY-OPERATIONS.md §18.15.8）。
    /// </summary>
    public DeploymentKind HostKind { get; init; } = DeploymentKind.Unknown;

    /// <summary>永久授權（expiresAt 為 9999 年的哨兵值，§18.15.1）。</summary>
    public bool IsPerpetual => LicenseSupport.IsPerpetual(ExpiresAt);

    /// <summary>
    /// 是否為 SmartFactory 內部開發授權（clientCode == SMARTFACTORY-INTERNAL）。
    /// Internal License 在 Development 環境外使用時會拒絕啟動。
    /// </summary>
    public bool IsInternal =>
        string.Equals(ClientCode, "SMARTFACTORY-INTERNAL", StringComparison.OrdinalIgnoreCase);
}
