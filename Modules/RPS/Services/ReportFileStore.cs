using Microsoft.Extensions.Configuration;

namespace SmartFactory.MES.Modules.RPS.Services;

/// <summary>
/// 排程產出的報表檔落地與讀回（§7.5）。
///
/// <para><b>絕不放 <c>wwwroot</c>。</b>那是靜態公開路徑，任何人猜到檔名就能直接下載、
/// 完全繞過 RBAC——機密報表放在那裡等於把 §八 整章作廢。專案唯一既有的落地點
/// <c>wwwroot/uploads</c> 存的是公司 logo，那本來就是公開的，不能當成先例。</para>
///
/// <para>因此：存到設定的 <c>Reports:StoragePath</c>，檔名用不可猜測的 GUID，
/// **一律經由 Controller 檢查權限後才 <c>File()</c> 回傳**（§7.8 S5 下載時再檢查一次）。</para>
///
/// <para>⚠️ <b>預設值 <c>App_Data/reports</c> 是相對路徑</b>，會落在行程的工作目錄下（通常就是
/// ContentRoot 內）。它不會被靜態檔中介軟體服務出去，所以安全性上沒破口，但**正式區必須把
/// <c>Reports:StoragePath</c> 設成 ContentRoot 之外的絕對路徑**（§7.5）：留在程式目錄內的話，
/// 重新部署會連同已產生的報表一起被覆蓋掉，而備份腳本也不會涵蓋它。
/// 開發區靠 <c>.gitignore</c> 擋住 <c>App_Data/</c>，避免產出被提交進版控。</para>
/// </summary>
public interface IReportFileStore
{
    /// <summary>寫入一份產出，回傳存進 <c>ReportRun.FilePath</c> 的**相對**路徑。</summary>
    Task<string> SaveAsync(byte[] content, string extension, CancellationToken ct = default);

    /// <summary>讀回檔案內容；檔案不存在（已過保留期或被清掉）時回 <c>null</c>。</summary>
    Task<byte[]?> ReadAsync(string relativePath, CancellationToken ct = default);

    /// <summary>刪除一份產出。檔案本來就不在時視為成功——保留期清理會重跑。</summary>
    void Delete(string relativePath);

    /// <summary>解析為絕對路徑，並確認它沒有跳出儲存根目錄。</summary>
    string? ResolveFullPath(string relativePath);
}

/// <inheritdoc />
public class ReportFileStore : IReportFileStore
{
    private const string DefaultStoragePath = "App_Data/reports";

    private readonly string _root;

    public ReportFileStore(IConfiguration configuration)
    {
        var configured = configuration.GetValue<string>("Reports:StoragePath");
        var path = string.IsNullOrWhiteSpace(configured) ? DefaultStoragePath : configured;

        _root = Path.GetFullPath(path);
    }

    public async Task<string> SaveAsync(byte[] content, string extension, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // 依日期分子目錄：單一目錄堆上萬個檔會讓保留期清理與人工排查都很痛苦。
        var day = DateTime.UtcNow.ToString("yyyyMMdd");

        // 檔名用 GUID 而非「報表名＋日期」：後者可猜，而下載端點若哪天漏了權限檢查，
        // 可猜的檔名就是直接的資料外洩；GUID 讓那個失誤不至於變成全庫可列舉。
        var name = $"{Guid.NewGuid():N}{NormalizeExtension(extension)}";
        var relative = $"{day}/{name}";

        var full = Path.Combine(_root, day, name);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, content, ct);

        return relative;
    }

    public async Task<byte[]?> ReadAsync(string relativePath, CancellationToken ct = default)
    {
        var full = ResolveFullPath(relativePath);
        if (full is null || !File.Exists(full)) return null;

        return await File.ReadAllBytesAsync(full, ct);
    }

    public void Delete(string relativePath)
    {
        var full = ResolveFullPath(relativePath);
        if (full is null || !File.Exists(full)) return;

        File.Delete(full);
    }

    /// <summary>
    /// <c>ReportRun.FilePath</c> 是資料庫內容。就算今天只有派工器寫得進去，
    /// 解析時仍必須確認結果**沒有跳出儲存根目錄**——少了這一步，一筆被竄改成
    /// <c>../../appsettings.json</c> 的紀錄就能讓下載端點讀出任意檔案。
    /// </summary>
    public string? ResolveFullPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;

        var full = Path.GetFullPath(Path.Combine(_root, relativePath));

        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        return full.StartsWith(rootWithSeparator, StringComparison.Ordinal) ? full : null;
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return ".bin";

        var ext = extension.Trim();
        if (!ext.StartsWith('.')) ext = "." + ext;

        // 副檔名最終會進檔名，不能讓它帶路徑分隔字元。
        return ext.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? ".bin" : ext;
    }
}
