namespace SmartFactory.MES.Services;

/// <summary>
/// 授權看門狗背景服務。
///
/// 每 24 小時重新執行 License 驗證，確保：
///   1. 到期後最晚 24 小時內系統開始記錄 Critical 日誌
///   2. /health 端點回傳 Unhealthy，觸發外部監控告警
///   3. 防止「啟動時驗證通過後，長期運行繞過到期檢查」的攻擊面
///   4. 檢查授權撤銷清單（CRL），使合約終止／安全事件後不必依賴對方配合刪檔
///
/// 本服務不會強制終止程序（避免影響線上生產環境）。到期、撤銷改由
/// <see cref="ILicenseStateService"/> 轉唯讀（SECURITY-OPERATIONS.md §18.8），
/// 並由外部監控（/health + Serilog）通知操作人員處理。
/// </summary>
public class LicenseWatchdogService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LicenseWatchdogService> _logger;
    private readonly IHostEnvironment _env;

    // 供 /health 端點查詢目前授權狀態
    public static bool IsHealthy { get; private set; } = true;
    public static string? LastError { get; private set; }

    public LicenseWatchdogService(
        IServiceScopeFactory scopeFactory,
        ILogger<LicenseWatchdogService> logger,
        IHostEnvironment env)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _env = env;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 等待應用程式完全啟動後再執行第一次檢查（避免干擾啟動流程）
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await ValidateAsync(stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常關機觸發，不視為錯誤
        }
    }

    /// <summary>
    /// 單輪驗證。<c>internal</c> 是為了讓測試直接驗這一輪的結果 ——
    /// 走 <see cref="ExecuteAsync"/> 要先等一分鐘，測不到也等不起。
    /// </summary>
    internal async Task ValidateAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();

            // 終止聲明生效後不再驗授權（授權檔可能根本不存在）
            if (scope.ServiceProvider.GetService<ILicenseStateService>()?.Current.State == LicenseState.Unlocked)
            {
                IsHealthy = true;
                LastError = null;
                return;
            }

            var validator = scope.ServiceProvider.GetRequiredService<ILicenseValidator>();
            var ctx = await Task.Run(validator.Validate, ct);

            // 唯讀／停用的判定在 LicenseStateService（§18.8）；這裡只餵它已驗簽的輸入
            var state = scope.ServiceProvider.GetService<ILicenseStateService>();
            state?.SetLicense(ctx);
            // 系統參數可能被直接改資料庫：每輪重讀（只影響 offline 授權的自選確認，§18.15.8）
            state?.SetOnlineCheckOptIn(await LicenseOnlineCheck.ReadOptInAsync(scope.ServiceProvider));

            // 撤銷清單檢查。未註冊或未啟用時立即回傳 Disabled，不會連網。
            var crlService = scope.ServiceProvider.GetService<ILicenseCrlService>();
            var crl = crlService is null
                ? new CrlEvaluation(CrlOutcome.Disabled, "未啟用撤銷清單檢查")
                : await crlService.CheckAsync(ctx.LicenseId, ct);

            // 已驗簽的撤銷：提前轉唯讀（單向加嚴，之後抓不到清單也不會恢復）
            if (crl.Outcome == CrlOutcome.Revoked && crl.RevokedAt is { } revokedAt)
                state?.SetRevoked(revokedAt, crl.RevokedReason ?? "");

            if (!crl.IsHealthy)
            {
                // 授權本身有效，但已被撤銷、或長期取不到撤銷清單。
                // 與到期一樣：只轉紅 + 告警，不終止程序。
                IsHealthy = false;
                LastError = crl.Message;
                _logger.LogCritical("[LicenseWatchdog] {Message}", crl.Message);
                return;
            }

            IsHealthy = true;
            LastError = null;

            if (crl.Outcome == CrlOutcome.Stale)
                _logger.LogWarning("[LicenseWatchdog] {Message}", crl.Message);

            if (state?.Current is { } status)
            {
                if (status.IsWriteBlocked)
                    _logger.LogCritical("[LicenseWatchdog] {Reason}", status.Reason);
                else if (status.State == LicenseState.Warning)
                    _logger.LogWarning("[LicenseWatchdog] {Reason}", status.Reason);
                else
                    _logger.LogDebug("[LicenseWatchdog] {Reason}", status.Reason);
                return;
            }

            var daysLeft = (ctx.ExpiresAt - DateTimeOffset.UtcNow).TotalDays;
            if (daysLeft <= 30)
                _logger.LogWarning(
                    "[LicenseWatchdog] 授權將於 {Days:F0} 天後到期（{Date:yyyy-MM-dd}），" + LicenseSupport.RenewShort,
                    daysLeft, ctx.ExpiresAt);
            else
                _logger.LogDebug(
                    "[LicenseWatchdog] 授權有效，剩餘 {Days:F0} 天",
                    daysLeft);
        }
        catch (Exception ex)
        {
            IsHealthy = false;
            LastError = ex.Message;
            // Critical 等級：會觸發 Serilog 的 File sink 告警，外部監控可掃描 log 關鍵字
            _logger.LogCritical(
                "[LicenseWatchdog] 授權驗證失敗，系統可能無法繼續提供服務！\n原因：{Message}",
                ex.Message);
        }
    }
}
