using System.Net;
using System.Reflection;
using Serilog;

namespace SmartFactory.MES.Services;

/// <summary>
/// 授權頁模式（SECURITY-OPERATIONS.md §18.15.9）：主機識別碼閘門擋下時取代整個主系統。
///
/// <para><b>不解密連線字串、不連資料庫、不載入任何模組</b>——被複製的環境看不到任何資料，也沒有登入畫面。
/// 只回一頁說明與申請碼（指紋與主機識別碼都是雜湊，不含錨定碼與 UUID 原值），讓正版客戶換主機板後能重新申請；
/// 新授權放到原位置後重啟即恢復。刻意不提供上傳：這個模式沒有登入，任何人都能開這頁。</para>
///
/// <para><c>/health</c> 回 503，部署腳本的健康檢查會判失敗。</para>
/// </summary>
public static class LicenseBlockedHost
{
    public static async Task RunAsync(string[] args, Exception blocked)
    {
        var reason = LicenseHostGate.ReasonOf(blocked) ?? LicenseHostGate.Mismatch;
        var message = MessageOf(blocked);

        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));
        var app = builder.Build();

        var page = BuildPage(reason, message, TryBuildRequestCode(app.Configuration, renew: reason == LicenseHostGate.Mismatch, out var registerUrl), registerUrl);
        app.Logger.LogCritical(
            "[License] 主機識別碼閘門擋下（{Reason}），系統以授權頁模式啟動：不連線資料庫、不提供任何功能。{Message}",
            reason, message);

        app.Run(async ctx =>
        {
            var h = ctx.Response.Headers;
            h["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; form-action 'none'; frame-ancestors 'none'";
            h["X-Content-Type-Options"] = "nosniff";
            h["Cache-Control"] = "no-store";
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            if (ctx.Request.Path.StartsWithSegments("/health"))
            {
                await ctx.Response.WriteAsJsonAsync(new { status = "Unhealthy", license = "host-" + reason });
                return;
            }
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(page);
        });

        await app.RunAsync();
    }

    /// <summary>取閘門那一層的訊息（外層可能是反射包裝，訊息只剩「target of an invocation」）。</summary>
    internal static string MessageOf(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e.Data[LicenseHostGate.DataKey] is string) return e.Message;
        return ex.Message;
    }

    /// <param name="renew">主機不符＝已有授權、換了電腦或主機板 → 給換發網址；讀不到 → 給登記網址。</param>
    private static string? TryBuildRequestCode(IConfiguration config, bool renew, out string? registerUrl)
    {
        registerUrl = null;
        try
        {
            var fingerprint = MachineAnchor.GetFingerprint();
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "";
            // TPM 偵測是第二期（§18.7）；目前一律回報無
            var code = LicenseRequestCode.Build(fingerprint, hasTpm: false, version, HostIdentity.Detect());
            var server = config["License:ServerUrl"] ?? LicenseRequestCode.DefaultServerUrl;
            registerUrl = renew ? LicenseRequestCode.RenewUrl(server, code) : LicenseRequestCode.RegisterUrl(server, code);
            return code;
        }
        catch (InvalidOperationException)
        {
            return null;   // 錨定檔讀不到：頁面改提示用 MachineId 工具
        }
    }

    /// <summary>整頁 HTML（純函式，供測試）。這頁沒有登入、任何人都看得到，只放不含原值的資訊。</summary>
    internal static string BuildPage(string reason, string message, string? requestCode, string? registerUrl)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var isMismatch = reason == LicenseHostGate.Mismatch;
        var titleZh = isMismatch ? "此授權不屬於這台電腦" : "無法確認本機是授權機器";
        var titleEn = isMismatch ? "This license does not belong to this computer" : "Cannot verify this computer against the license";
        var stepsZh = isMismatch
            ? "<li>若系統是從別台電腦複製過來的，請改在原授權電腦上執行。</li>" +
              "<li>若已更換電腦或主機板，請開啟下方換發網址申請（或把申請碼交給授權提供者），收到新授權後以新的授權編號重新加密連線字串並重新啟動。</li>"
            : "<li>請依上方訊息修復主機識別碼的讀取（Windows 讀登錄、Linux 須由 systemd 寫入 /run/smartfactory/host-id）。</li>" +
              "<li>修復後重新啟動服務。</li>";
        var stepsEn = isMismatch
            ? "<li>If this system was copied from another computer, run it on the licensed computer instead.</li>" +
              "<li>If the computer or motherboard was replaced, open the re-issue link below (or send the request code to your license provider); after receiving the new license, re-encrypt the connection string with the new license ID and restart.</li>"
            : "<li>Fix host identifier access as described above (Windows registry; on Linux, systemd must write /run/smartfactory/host-id).</li>" +
              "<li>Restart the service afterwards.</li>";
        var code = requestCode is null
            ? "<p>無法讀取機器錨定碼，請以 SmartFactory.MES.MachineId 工具取得指紋。<br>Cannot read the machine anchor; use the SmartFactory.MES.MachineId tool to obtain the fingerprint.</p>"
            : $"<p><textarea readonly rows=\"4\" style=\"width:100%;font-family:monospace\">{E(requestCode)}</textarea></p>" +
              (registerUrl is null ? "" : $"<p><a href=\"{E(registerUrl)}\" rel=\"noreferrer\">{E(registerUrl)}</a></p>");

        return "<!doctype html><html lang=\"zh-Hant\"><meta charset=\"utf-8\">" +
               "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
               $"<title>{E(titleZh)} / {E(titleEn)}</title>" +
               "<body style=\"font-family:system-ui,sans-serif;max-width:46rem;margin:3rem auto;padding:0 1rem;line-height:1.6\">" +
               $"<h1 style=\"font-size:1.5rem\">{E(titleZh)}</h1><p style=\"color:#555\">{E(titleEn)}</p>" +
               $"<p style=\"white-space:pre-line\">{E(message)}</p>" +
               "<p>系統未連線資料庫，所有功能已停用。<br>The database is not connected; all functions are disabled.</p>" +
               $"<h2 style=\"font-size:1.1rem\">處理方式 / What to do</h2><ol>{stepsZh}</ol><ol>{stepsEn}</ol>" +
               $"<h2 style=\"font-size:1.1rem\">申請碼 / Request code</h2>{code}" +
               "<p style=\"color:#555\">申請碼只含指紋與主機識別碼的雜湊，不含錨定碼或主機板 UUID 原值。<br>" +
               "The request code contains only hashes; it does not include the anchor or the motherboard UUID.</p>" +
               "</body></html>";
    }
}
