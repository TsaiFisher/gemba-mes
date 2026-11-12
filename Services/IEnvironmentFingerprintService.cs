namespace SmartFactory.MES.Services;

/// <summary>
/// 環境指紋服務介面。
/// Phase 1：由 EnvironmentFingerprintService 實作（原始碼可見）。
/// Phase 2：實作移入 Security Anchor DLL（SmartFactory.MES.Security），主專案只保留此介面。
/// </summary>
public interface IEnvironmentFingerprintService
{
    /// <summary>
    /// 取得目前機器的環境指紋（SHA-256 hex）。
    /// 演算法：SHA256( machine-id : hostname : primary-mac )
    /// </summary>
    string GetFingerprint();

    /// <summary>
    /// 指紋是否由環境變數 <c>SMARTFACTORY_INSTANCE_ID</c> 直接指定（而非錨定檔）。
    /// 只有內部授權接受此來源（SECURITY-OPERATIONS.md §18.11）。
    /// 預設 <c>false</c>：測試替身與自訂實作不受影響。
    /// </summary>
    bool IsAnchorFromEnvironmentVariable => false;
}
