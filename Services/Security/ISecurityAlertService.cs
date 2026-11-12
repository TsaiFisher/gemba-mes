namespace SmartFactory.MES.Services.Security
{
    /// <summary>
    /// M6（security-assessment-and-plan.md）：In-repo 安全事件告警骨架。
    /// 產生高嚴重度結構化日誌（SECURITY_ALERT 標記，供集中式日誌/SIEM 擷取），
    /// 並在設定 <c>Security:AlertWebhookUrl</c> 時以 fire-and-forget 方式 POST 通知。
    /// 同一 (kind:ip) 於冷卻窗內去重，避免暴力破解時洗版 webhook。
    /// </summary>
    public interface ISecurityAlertService
    {
        /// <summary>
        /// 發出一則安全告警。<paramref name="kind"/> 為事件類別（如 LoginRateLimitExceeded），
        /// <paramref name="detail"/> 為可讀說明，<paramref name="ipAddress"/> 為來源 IP（可為 null）。
        /// </summary>
        Task RaiseAsync(string kind, string detail, string? ipAddress);
    }
}
