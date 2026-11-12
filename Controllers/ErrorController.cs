using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Controllers;

[AllowAnonymous]
public class ErrorController : Controller
{
    [Route("Error/{statusCode:int}")]
    [PermissionGateExempt("錯誤頁；只回傳狀態碼與原始路徑，且必須在任何權限狀態下都到得了")]
    public IActionResult Index(int statusCode)
    {
        var feature = HttpContext.Features.Get<IStatusCodeReExecuteFeature>();
        var originalPath = feature?.OriginalPath ?? string.Empty;

        ViewBag.StatusCode   = statusCode;
        ViewBag.OriginalPath = originalPath;

        // 🚨 手機路徑的錯誤**不能**用桌機版面。
        //
        // 手機 PWA 是 standalone 啟動的：沒有網址列、沒有分頁列。一旦畫面換成桌機版面，
        // 使用者就**回不到手機殼**了——只剩 OS 的返回手勢，而桌機錯誤頁上那顆
        // 「返回首頁」指的是 `/`，按下去只會把他推得更深。
        //
        // 這條路徑不是假想的：手機首頁的功能磚是由前端用 `moduleKey.toLowerCase()`
        // 拼出 `/mobile/{key}/list.html` 的，只要某個模組沒有對應的頁面（Core 自身就
        // 一個模組頁都沒有），點下去就是 404 → 本控制器 → 桌機版面。
        return IsMobilePath(originalPath) ? View("Mobile") : View();
    }

    /// <summary>
    /// 這個路徑是不是手機殼裡的頁面。
    ///
    /// <para><c>/api/mobile/...</c> 刻意**不算**：那些是 fetch 呼叫，回應由
    /// <c>api.js</c> 處理，不會被當成畫面顯示；把它導到手機錯誤頁只會讓
    /// JSON 解析失敗變成看不懂的錯誤。這裡只認會被瀏覽器當頁面載入的路徑。</para>
    /// </summary>
    private static bool IsMobilePath(string path) =>
        path.StartsWith("/mobile/", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "/mobile", StringComparison.OrdinalIgnoreCase);
}
