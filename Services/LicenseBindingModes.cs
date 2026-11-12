namespace SmartFactory.MES.Services;

// 獨立成檔：Security DLL 以 <Compile Include Link> 共用 HostIdentity.cs，它用到這裡的常數。
/// <summary>授權檔 <c>bindingMode</c> 的合法值（SECURITY-OPERATIONS.md §18.2、§18.5）。</summary>
internal static class LicenseBindingModes
{
    /// <summary>線上租約：每日續租，機器由租約決定。</summary>
    internal const string Lease   = "lease";
    /// <summary>TPM 綁定：機器由綁定憑證決定，可長期離線（第二期）。</summary>
    internal const string Tpm     = "tpm";
    /// <summary>離線：人工核發，機器由 <c>allowedFingerprints</c> 決定。</summary>
    internal const string Offline = "offline";
    /// <summary>CI：GitHub OIDC 換短期租約。</summary>
    internal const string Ci      = "ci";

    internal static bool IsKnown(string? mode) => mode is Lease or Tpm or Offline or Ci;
}
