using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Filters
{
    /// <summary>
    /// 攔截 SystemParamController，注入 BAS 專屬的幣別資料。
    /// Core 的 SystemParamController 完全不動，此 Filter 在 ProjectZ 側補充邏輯：
    /// - GET Index：將 CurrencyCodes 與 BaseCurrency 注入 ViewData
    /// - POST Save（成功重導向後）：將 BaseCurrency 單獨儲存至 SystemParams
    /// </summary>
    public class SystemParamEnhancementFilter : IAsyncActionFilter
    {
        private readonly ApplicationDbContext _context;
        private readonly ISystemParamService _svc;

        public SystemParamEnhancementFilter(ApplicationDbContext context, ISystemParamService svc)
        {
            _context = context;
            _svc = svc;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var controller = context.RouteData.Values["controller"]?.ToString();
            if (controller != "SystemParam") { await next(); return; }

            var action = context.RouteData.Values["action"]?.ToString();
            var executed = await next();

            if (action == "Index" && executed.Result is ViewResult vr)
            {
                // 注入幣別清單與本幣設定至 ViewData
                vr.ViewData["CurrencyCodes"] = await _context.Currencies
                    .IgnoreQueryFilters()
                    .Where(c => c.IsActive)
                    .OrderBy(c => c.CurrencyCode)
                    .Select(c => c.CurrencyCode)
                    .ToListAsync();

                vr.ViewData["BaseCurrency"] = await _svc.GetAsync(ProjectZSystemParamKeys.BaseCurrency);

                // INV 簡化模式
                var invSimplified = await _svc.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode);
                vr.ViewData["InvSimplifiedMode"] = string.Equals(invSimplified, "true", StringComparison.OrdinalIgnoreCase);

                // INV 預設計價方式
                vr.ViewData["InvDefaultCostMethod"] = await _svc.GetAsync(ProjectZSystemParamKeys.InvDefaultCostMethod) ?? "MA";

                // MFG 簡化模式（mfg-domain.md §25）
                var mfgSimplified = await _svc.GetAsync(ProjectZSystemParamKeys.MfgSimplifiedMode);
                vr.ViewData["MfgSimplifiedMode"] = string.Equals(mfgSimplified, "true", StringComparison.OrdinalIgnoreCase);

                // 自動重排排程（全域預設；各廠於廠別參數覆寫）
                vr.ViewData["MfgAutoScheduleOnChange"] = string.Equals(
                    await _svc.GetAsync(ProjectZSystemParamKeys.MfgAutoScheduleOnChange), "true", StringComparison.OrdinalIgnoreCase);
                vr.ViewData["MfgAutoScheduleBeforeDispatch"] = string.Equals(
                    await _svc.GetAsync(ProjectZSystemParamKeys.MfgAutoScheduleBeforeDispatch), "true", StringComparison.OrdinalIgnoreCase);
                vr.ViewData["MfgAutoScheduleDailyTime"] = await _svc.GetAsync(ProjectZSystemParamKeys.MfgAutoScheduleDailyTime) ?? "";
                vr.ViewData["MfgAutoScheduleRule"] = Modules.MFG.Services.AutoScheduleSettings.NormalizeRule(
                    await _svc.GetAsync(ProjectZSystemParamKeys.MfgAutoScheduleRule)) ?? "";
                vr.ViewData["MfgAutoScheduleFreezeHours"] = Modules.MFG.Services.AutoScheduleSettings.NormalizeFreezeHours(
                    await _svc.GetAsync(ProjectZSystemParamKeys.MfgAutoScheduleFreezeHours)) ?? "";

                // SRM 供應商評分卡權重（pur-domain.md §6，預設 30/30/20/20）
                vr.ViewData["SrmWeightQuality"]  = await _svc.GetAsync(ProjectZSystemParamKeys.SrmWeightQuality)  ?? "30";
                vr.ViewData["SrmWeightDelivery"] = await _svc.GetAsync(ProjectZSystemParamKeys.SrmWeightDelivery) ?? "30";
                vr.ViewData["SrmWeightPrice"]    = await _svc.GetAsync(ProjectZSystemParamKeys.SrmWeightPrice)    ?? "20";
                vr.ViewData["SrmWeightService"]  = await _svc.GetAsync(ProjectZSystemParamKeys.SrmWeightService)  ?? "20";
            }
            else if (action == "Save"
                     && context.HttpContext.Request.Method == "POST"
                     && executed.Result is RedirectToActionResult)
            {
                var form = context.HttpContext.Request.Form;

                // Core 的 Save 成功（已重導向）→ 補存 BaseCurrency
                var baseCurrency = form["BaseCurrency"].FirstOrDefault();
                await _svc.SetAsync(
                    ProjectZSystemParamKeys.BaseCurrency,
                    string.IsNullOrWhiteSpace(baseCurrency) ? null : baseCurrency.Trim());

                // INV 簡化模式
                await _svc.SetAsync(
                    ProjectZSystemParamKeys.InvSimplifiedMode,
                    IsChecked(form, "InvSimplifiedMode") ? "true" : "false");

                // INV 預設計價方式
                var invCostMethod = form["InvDefaultCostMethod"].FirstOrDefault();
                await _svc.SetAsync(
                    ProjectZSystemParamKeys.InvDefaultCostMethod,
                    string.IsNullOrWhiteSpace(invCostMethod) ? "MA" : invCostMethod.Trim());

                // MFG 簡化模式（mfg-domain.md §25）
                await _svc.SetAsync(
                    ProjectZSystemParamKeys.MfgSimplifiedMode,
                    IsChecked(form, "MfgSimplifiedMode") ? "true" : "false");

                // 自動重排排程：時刻只收 HH:mm，其餘（含空白）一律清除＝不使用
                await _svc.SetAsync(ProjectZSystemParamKeys.MfgAutoScheduleOnChange,
                    IsChecked(form, "MfgAutoScheduleOnChange") ? "true" : "false");
                await _svc.SetAsync(ProjectZSystemParamKeys.MfgAutoScheduleBeforeDispatch,
                    IsChecked(form, "MfgAutoScheduleBeforeDispatch") ? "true" : "false");
                await _svc.SetAsync(ProjectZSystemParamKeys.MfgAutoScheduleDailyTime,
                    Modules.MFG.Services.AutoScheduleSettings.NormalizeDailyTime(form["MfgAutoScheduleDailyTime"].FirstOrDefault()));
                // 規則只收列舉名稱、時界只收正數（上限一週）；空白或不合法一律清除＝原行為（Priority／不凍結）
                await _svc.SetAsync(ProjectZSystemParamKeys.MfgAutoScheduleRule,
                    Modules.MFG.Services.AutoScheduleSettings.NormalizeRule(form["MfgAutoScheduleRule"].FirstOrDefault()));
                await _svc.SetAsync(ProjectZSystemParamKeys.MfgAutoScheduleFreezeHours,
                    Modules.MFG.Services.AutoScheduleSettings.NormalizeFreezeHours(form["MfgAutoScheduleFreezeHours"].FirstOrDefault()));

                // SRM 供應商評分卡權重（非負數字才儲存；空值＝清除回預設）
                foreach (var (formKey, paramKey) in new (string, string)[]
                {
                    ("SrmWeightQuality",  ProjectZSystemParamKeys.SrmWeightQuality),
                    ("SrmWeightDelivery", ProjectZSystemParamKeys.SrmWeightDelivery),
                    ("SrmWeightPrice",    ProjectZSystemParamKeys.SrmWeightPrice),
                    ("SrmWeightService",  ProjectZSystemParamKeys.SrmWeightService),
                })
                {
                    var raw = form[formKey].FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(raw))
                        await _svc.SetAsync(paramKey, null);
                    else if (decimal.TryParse(raw, out var v) && v >= 0)
                        await _svc.SetAsync(paramKey, v.ToString("0.##"));
                }
            }
        }

        /// <summary>
        /// 讀「hidden false + checkbox true」配對。
        /// <b>刻意用 <c>Any</c> 而非 <c>FirstOrDefault</c></b>：_ProjectZSystemParams.cshtml 把 hidden
        /// 寫在 checkbox 之前，瀏覽器依 DOM 順序送出兩個同名欄位，取第一個永遠拿到 hidden 的
        /// "false" —— 勾了也存不進去。原有單元測試每次只塞一個值，剛好測不到這條路徑。
        /// 這樣讀就與兩個 input 的先後無關。
        /// </summary>
        private static bool IsChecked(IFormCollection form, string field) =>
            form[field].Any(v => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));

    }
}
