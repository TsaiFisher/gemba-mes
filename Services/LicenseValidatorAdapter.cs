using System.Reflection;
using System.Security;

namespace SmartFactory.MES.Services;

/// <summary>
/// Phase 2：ILicenseValidator 實作，委派至 Security Anchor DLL 的 SecurityContext.ReValidate()。
/// 當 Security DLL 已載入時，由 Program.cs 替代 LicenseValidator 注冊至 DI。
///
/// 透過反射呼叫 DLL 方法，避免主專案對 Security DLL 產生直接相依。
/// </summary>
public sealed class LicenseValidatorAdapter : ILicenseValidator
{
    private readonly object _securityContext;
    private readonly bool _isProduction;

    /// <param name="securityContext">SecurityAnchor.Initialize() 回傳的 SecurityContext（DLL 內部型別，透過反射存取）。</param>
    /// <param name="isProduction">是否為生產環境。Internal License 在生產環境使用時拒絕啟動。</param>
    public LicenseValidatorAdapter(object securityContext, bool isProduction)
    {
        _securityContext = securityContext;
        _isProduction    = isProduction;
    }

    public LicenseContext Validate()
    {
        // 重新執行完整驗證（簽章 + 有效期 + 指紋）
        _securityContext.GetType()
            .GetMethod("ReValidate", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(_securityContext, [null]);

        var t = _securityContext.GetType();
        // 舊版 DLL 不比對主機：複製出來的環境換上它就繞過閘門，不可放行
        var hostBound = t.GetProperty("HostBound")
            ?? throw new SecurityException(
                "Security DLL 版本過舊（不含主機識別碼檢查），請更新 Security/SmartFactory.MES.Security.dll。\n\n"
                + LicenseSupport.HowToObtain);
        var licCtx = new LicenseContext
        {
            LicenseId  = (string)t.GetProperty("LicenseId")!.GetValue(_securityContext)!,
            ClientCode = (string)t.GetProperty("ClientCode")!.GetValue(_securityContext)!,
            ClientName = (string)(t.GetProperty("ClientName")?.GetValue(_securityContext) ?? ""),
            ExpiresAt  = (DateTimeOffset)t.GetProperty("ExpiresAt")!.GetValue(_securityContext)!,
            AllowPlainConnection =
                (bool)(t.GetProperty("AllowPlainConnection")?.GetValue(_securityContext) ?? false),
            // v3 欄位：舊版 DLL 沒有這兩個屬性時視為離線、1 座
            BindingMode = (string)(t.GetProperty("BindingMode")?.GetValue(_securityContext) ?? LicenseBindingModes.Offline),
            Seats       = (int)(t.GetProperty("Seats")?.GetValue(_securityContext) ?? 1),
            // 主機識別碼閘門在 DLL 裡（§18.15.9）：不符或讀不到時 ReValidate 已拋例外，走到這裡只會是通過或不需要。
            // VM 強制線上確認看 HostKind——改版前這兩項沒填，DLL 模式下主機比對與 VM 偵測整段失效
            HostCheck = (bool)hostBound.GetValue(_securityContext)! ? HostCheckResult.Match : HostCheckResult.NotRequired,
            HostKind  = ParseDeployKind((string?)t.GetProperty("HostDeploy")?.GetValue(_securityContext)),
        };

        // Internal License 僅限 Development 環境（DLL 無 IHostEnvironment，由此處強制）
        if (licCtx.IsInternal && _isProduction)
            throw new SecurityException(
                "SmartFactory Internal License 僅限開發環境使用，生產環境需要正式授權。\n\n"
                + LicenseSupport.HowToObtain);

        return licCtx;
    }

    /// <summary>DLL 回傳申請碼用的部署類型字串（<see cref="HostIdentityResult.DeployCode"/>）。</summary>
    internal static DeploymentKind ParseDeployKind(string? code) => code switch
    {
        "physical"  => DeploymentKind.Physical,
        "vm"        => DeploymentKind.Vm,
        "wsl"       => DeploymentKind.Wsl,
        "container" => DeploymentKind.Container,
        _           => DeploymentKind.Unknown,
    };
}
