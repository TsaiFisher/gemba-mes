using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace SmartFactory.MES.Services;

/// <summary>匯入結果。<see cref="MessageKey"/> 是 SharedResource 的鍵（中英），<see cref="Detail"/> 是驗證端原文。</summary>
public sealed record LicenseImportResult(bool Success, string MessageKey, string? Detail = null);

/// <summary>
/// 換發授權的匯入（SECURITY-OPERATIONS.md §18.3、§18.8）：離線授權到期前換發、或唯讀／停用後
/// 管理員上傳新的 license.json，不必登入主機、也不必重啟。
///
/// <para><b>順序是安全性的一部分：</b>先以暫存檔完整驗證（簽章、格式、結構、本機指紋）→ 備份現行檔 →
/// 原子替換 → 以正式驗證器重讀並更新狀態；任一步失敗即還原。一份驗不過的檔案絕不能覆蓋現行授權，
/// 否則下次重啟系統就起不來。</para>
///
/// <para>Security DLL 部署（連線字串以 licenseId 推導金鑰加密）不得變更 licenseId：
/// 換了就解不開資料庫，必須先重新加密 DefaultEncrypted 再重啟。</para>
/// </summary>
public sealed class LicenseImportService
{
    public const int MaxBytes = 64 * 1024;

    private readonly IConfiguration configuration;
    private readonly ILicenseValidator validator;
    private readonly ILicenseStateService state;
    private readonly ILogger<LicenseImportService> logger;
    private readonly Func<string, LicenseContext> precheck;
    private readonly bool licenseIdBound;

    public LicenseImportService(
        IConfiguration configuration, IHostEnvironment env, ILicenseValidator validator,
        ILicenseStateService state, ILogger<LicenseImportService> logger)
        : this(configuration, validator, state, logger,
            path => new LicenseValidator(new EnvironmentFingerprintService(), configuration,
                NullLogger<LicenseValidator>.Instance, env).ValidateFile(path),
            licenseIdBound: validator is LicenseValidatorAdapter)
    {
    }

    /// <param name="precheck">以暫存檔做的完整驗證（測試可替換）。</param>
    /// <param name="licenseIdBound">連線字串以 licenseId 加密（Security DLL 部署）時為 true。</param>
    internal LicenseImportService(
        IConfiguration configuration, ILicenseValidator validator, ILicenseStateService state,
        ILogger<LicenseImportService> logger, Func<string, LicenseContext> precheck, bool licenseIdBound)
    {
        this.configuration = configuration;
        this.validator = validator;
        this.state = state;
        this.logger = logger;
        this.precheck = precheck;
        this.licenseIdBound = licenseIdBound;
    }

    public LicenseImportResult Import(byte[] content)
    {
        if (content.Length == 0 || content.Length > MaxBytes)
            return new(false, "Msg_LicenseImportInvalidFile");

        try
        {
            using var _ = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return new(false, "Msg_LicenseImportInvalidFile");
        }

        var target = LicenseValidator.ResolveLicensePath(configuration);
        var dir = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var temp = Path.Combine(dir, $".license-import-{Guid.NewGuid():N}.json");

        LicenseContext candidate;
        try
        {
            File.WriteAllBytes(temp, content);
            candidate = precheck(temp);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.SecurityException)
        {
            TryDelete(temp);
            logger.LogWarning("[License] 匯入的授權檔驗證失敗：{Message}", ex.Message);
            return new(false, "Msg_LicenseImportRejected", ex.Message);
        }

        // DLL 部署：licenseId 是連線字串金鑰的一部分
        if (licenseIdBound
            && File.Exists(target)
            && ReadLicenseId(target) is { } currentId
            && !string.Equals(currentId, candidate.LicenseId, StringComparison.Ordinal))
        {
            TryDelete(temp);
            return new(false, "Msg_LicenseImportLicenseIdChanged", $"{currentId} → {candidate.LicenseId}");
        }

        var backup = File.Exists(target) ? $"{target}.bak-{DateTime.UtcNow:yyyyMMddHHmmss}" : null;
        try
        {
            if (backup is not null) File.Copy(target, backup, overwrite: false);
            File.Move(temp, target, overwrite: true);

            var ctx = validator.Validate();
            state.SetLicense(ctx);
            logger.LogWarning("[License] 已匯入新授權 {LicenseId}，到期 {Expiry:yyyy-MM-dd}（備份：{Backup}）",
                ctx.LicenseId, ctx.ExpiresAt, backup ?? "無");
            return new(true, "Msg_LicenseImported", $"{ctx.LicenseId} · {LicenseSupport.FormatExpiry(ctx.ExpiresAt)}");
        }
        catch (Exception ex)
        {
            // 正式驗證器（例如 Security DLL）不認：還原舊檔
            if (backup is not null) File.Copy(backup, target, overwrite: true);
            TryDelete(temp);
            logger.LogError(ex, "[License] 匯入後重新驗證失敗，已還原原授權檔");
            return new(false, "Msg_LicenseImportRejected", ex.Message);
        }
    }

    private static string? ReadLicenseId(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return doc.RootElement.TryGetProperty("licenseId", out var id) ? id.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 暫存檔刪不掉不影響結果 */ }
    }
}
