namespace SmartFactory.MES.Services;

/// <summary>
/// License 驗證服務介面。
/// Phase 2：實作移入 Security Anchor DLL，同時整合 DB 連線字串解密。
/// </summary>
public interface ILicenseValidator
{
    /// <summary>
    /// 驗證 license.json：RSA 簽章 → 有效期 → 環境指紋。
    /// 驗證失敗時拋出例外，應用程式應於 Program.cs 捕獲並呼叫 Environment.Exit(1)。
    /// </summary>
    LicenseContext Validate();
}
