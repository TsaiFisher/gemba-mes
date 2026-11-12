using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.RPS.Controllers;

/// <summary>
/// 常用查詢（§7.9 L2）。
///
/// **本 controller 沒有自己的 MenuKey**（§7.9-4）：常用查詢只是參數容器，
/// 執行時檢查的是**目標報表的 MenuKey**，由 <see cref="IReportToolCatalog"/> 逐支判定。
/// 為一個附屬功能新增權限維度只會讓授權矩陣更難維護。
///
/// 因此它也不登錄選單——入口在各報表畫面上（§7.9-6）。
/// </summary>
[Authorize]
public class SavedQueryController : Controller
{
    private readonly ISavedQueryService _savedQueries;
    private readonly IReportToolCatalog  _catalog;

    public SavedQueryController(ISavedQueryService savedQueries, IReportToolCatalog catalog)
    {
        _savedQueries = savedQueries;
        _catalog      = catalog;
    }

    /// <summary>
    /// 目前使用者自己的常用查詢清單（JSON，供報表畫面的側欄取用）。
    /// </summary>
    [HttpGet]
    [PermissionGateExempt(
        "只回傳「目前登入者自己建立的」常用查詢：ISavedQueryService.ListAsync 以 " +
        "OwnerUserId == 當下使用者 在查詢層過濾，取不到他人資料，無 MenuKey 可檢查。" +
        "清單內容本身也只是參數（報表名稱＋期間），實際數字要點進目標報表才查得到，" +
        "而那一步由 ReportToolCatalog 依該報表的 MenuKey 重新把關。")]
    public async Task<IActionResult> List(string? toolKey)
    {
        var items = await _savedQueries.ListAsync(toolKey);
        return Json(items.Select(i => new
        {
            id           = i.Id,
            name         = i.Name,
            toolKey      = i.ToolKey,
            period       = i.Period?.ToString(),
            isAuthorized = i.IsAuthorized,
            isValid      = i.IsValid,
            reason       = i.InvalidReason,
        }));
    }

    /// <summary>儲存／同名覆寫。</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        string toolKey, string name, RelativePeriod? period,
        [FromForm] Dictionary<string, string?>? filters)
    {
        // 閘門寫在 action 本體而不是只留在 service：R13 掃的是 action，
        // 而「權限檢查藏在下一層」正是它要防的樣態。service 內另有一份同樣的檢查，
        // 那是給非 HTTP 呼叫端的防線，不是這裡可以省略的理由。
        if (!await _catalog.IsAuthorizedAsync(toolKey)) return Forbid();

        var result = await _savedQueries.SaveAsync(
            new SavedQueryInput(toolKey, name, period,
                                filters ?? new Dictionary<string, string?>()));

        return Json(new { success = result.Success, id = result.Id, message = result.ErrorMessage });
    }

    /// <summary>
    /// 刪除自己的常用查詢。這裡的授權依據是**擁有者**而非 MenuKey——
    /// service 以 <c>OwnerUserId == 當下使用者</c> 在查詢層限定，刪不到別人的。
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        // 「不是你的」與「不存在」一律回同一個結果：分開回報等於提供一個
        // 以 id 探測他人是否存有某筆常用查詢的管道。
        if (!await _savedQueries.DeleteAsync(id)) return Forbid();
        return Json(new { success = true });
    }

    /// <summary>
    /// 執行：解析成目標報表畫面的網址後轉導。
    ///
    /// 這是 GET 但**不寫入任何資料**（R26）：它只讀定義、解算期間、組網址。
    /// 權限在 <see cref="ISavedQueryService.BuildRunUrlAsync"/> 內以當下使用者重新檢查——
    /// 存檔時有權限不代表現在還有（§7.9-3）。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Run(int id)
    {
        var result = await _savedQueries.BuildRunUrlAsync(id);
        if (!result.Success)
        {
            if (result.ErrorMessage == "SavedQuery_Forbidden") return Forbid();
            if (result.ErrorMessage == "SavedQuery_NotFound")   return NotFound();

            TempData["ErrorMessage"] = result.ErrorMessage;
            return RedirectToAction("Index", "Home");
        }

        // LocalRedirect：Url 全由白名單繫結組出，但仍以框架層再擋一次開放轉導。
        return LocalRedirect(result.Url!);
    }
}
