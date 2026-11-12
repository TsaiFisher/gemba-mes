namespace SmartFactory.MES.Services;

/// <summary>
/// 授權安全上下文介面。
///
/// 提供給 PermissionService 等核心服務查詢授權到期時間。
///
/// 實作有兩種：
///   1. SecurityContextAdapter  — Phase 2：從 Security Anchor DLL 反射取得（生產環境）
///   2. FallbackSecurityContext — Phase 1：從 LicenseContext 取得（開發環境 / DLL 不存在時）
/// </summary>
public interface ISecurityContext
{
    string LicenseId { get; }
    string ClientCode { get; }
    DateTimeOffset ExpiresAt { get; }

    /// <summary>是否允許明文連線字串（由授權簽章決定，非環境變數）。</summary>
    bool AllowPlainConnection { get; }
}

/// <summary>
/// Phase 1 / 開發環境 fallback 實作。
/// DLL 不存在時，從 LicenseContext 取得資訊，IsModuleAllowed 預設全部允許。
/// </summary>
public sealed class FallbackSecurityContext : ISecurityContext
{
    private readonly LicenseContext _ctx;

    public FallbackSecurityContext(LicenseContext ctx) => _ctx = ctx;

    public string LicenseId  => _ctx.LicenseId;
    public string ClientCode => _ctx.ClientCode;
    public DateTimeOffset ExpiresAt => _ctx.ExpiresAt;
    public bool AllowPlainConnection => _ctx.AllowPlainConnection;
}

/// <summary>
/// Phase 2：包裝從 Security Anchor DLL 反射取得的 SecurityContext 物件。
/// </summary>
public sealed class SecurityContextAdapter : ISecurityContext
{
    private readonly string _licenseId;
    private readonly string _clientCode;
    private readonly DateTimeOffset _expiresAt;
    private readonly bool _allowPlainConnection;

    /// <param name="dllSecurityContext">
    ///   SecurityAnchor.Initialize() 回傳的 SecurityContext 物件（透過反射讀取屬性）。
    /// </param>
    public SecurityContextAdapter(object dllSecurityContext)
    {
        var t = dllSecurityContext.GetType();
        _licenseId  = (string)(t.GetProperty("LicenseId")!.GetValue(dllSecurityContext)!);
        _clientCode = (string)(t.GetProperty("ClientCode")!.GetValue(dllSecurityContext)!);
        _expiresAt  = (DateTimeOffset)(t.GetProperty("ExpiresAt")!.GetValue(dllSecurityContext)!);
        _allowPlainConnection =
            (bool)(t.GetProperty("AllowPlainConnection")?.GetValue(dllSecurityContext) ?? false);
    }

    public string LicenseId  => _licenseId;
    public string ClientCode => _clientCode;
    public DateTimeOffset ExpiresAt => _expiresAt;
    public bool AllowPlainConnection => _allowPlainConnection;
}
