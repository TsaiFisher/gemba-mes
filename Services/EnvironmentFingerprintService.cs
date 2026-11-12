namespace SmartFactory.MES.Services;

/// <summary>
/// 環境指紋服務 Phase 1 實作（原始碼在主專案中，顧問可見）。
/// Phase 2：SmartFactory.MES.Security.dll 的 SecurityAnchor 使用**同一個**
/// <see cref="MachineAnchor"/> 原始檔（以 Link 方式共用），因此兩者結果必然一致，
/// 不存在實作漂移的可能。
///
/// <para>
/// 指紋組成（v2）：<c>SHA256(UTF8("v2:" + anchorId))</c>
/// </para>
///
/// <para>
/// v1 的三分支環境偵測（Docker / WSL2 / 實體機 MAC）已於 2026-09-14 移除。
/// 移除原因與證據見 <see cref="MachineAnchor"/> 的類別註解與 SECURITY-OPERATIONS.md §14。
/// </para>
/// </summary>
public class EnvironmentFingerprintService : IEnvironmentFingerprintService
{
    /// <summary>
    /// 取得目前環境的指紋。
    /// 找不到機器錨定碼時拋出 <see cref="InvalidOperationException"/>，
    /// 訊息包含已嘗試的路徑與建立指令。
    /// </summary>
    public string GetFingerprint() => MachineAnchor.GetFingerprint();

    /// <inheritdoc />
    public bool IsAnchorFromEnvironmentVariable => MachineAnchor.IsEnvironmentOverrideActive();
}
