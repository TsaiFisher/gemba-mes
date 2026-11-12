using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Filters
{
    /// <summary>
    /// 攔截 Core 的 <c>PlantController</c>，把該廠的參數覆寫掛進廠別維護面板
    /// （`.claude/specifications/plant-scoped-system-params.md` §3.5 方案丙）。
    /// 比照 <see cref="SystemParamEnhancementFilter"/>：Core 的 Controller 與 View 完全不動，
    /// 只靠 Core 的 <c>_PlantFormModuleExtensions</c> 擴充點與本 Filter 完成讀寫。
    ///
    /// <para>GET Create/Edit：把目前覆寫與全域值投影進 ViewData 供三態控制項渲染。</para>
    /// <para>POST Create/Edit（Core 已回報成功後）：從 <c>Request.Form</c> 讀值持久化。
    /// <c>Plant</c> 模型不含這些欄位，故多餘欄位不會被模型繫結吃掉。</para>
    /// </summary>
    public class PlantFormEnhancementFilter : IAsyncActionFilter
    {
        /// <summary>ViewData 鍵：該廠現有覆寫（key → value，含 value 為 null 的「明確設為空」）。</summary>
        public const string OverridesKey = "PlantParamOverrides";

        /// <summary>ViewData 鍵：全域值（key → value），供「繼承全域」時顯示對照。</summary>
        public const string GlobalsKey = "PlantParamGlobals";

        /// <summary>ViewData 鍵：目前編輯中的廠別代碼；新增時為空字串。</summary>
        public const string PlantIdKey = "PlantParamPlantId";

        /// <summary>
        /// 面板存在標記。`_ProjectZPlantParams` 會渲染一個同名 hidden 欄位；
        /// 沒有它就代表這次 POST 根本沒帶參數區塊，一律不動既有覆寫。
        ///
        /// <para>少了這道判斷，「沒有勾選」與「沒有這個區塊」在 Request.Form 裡長得一模一樣，
        /// 於是任何只帶 Id/Name 的 POST（腳本、外部整合、未來新增的精簡表單）
        /// 都會把該廠所有覆寫**安靜地**清光。2026-08-18 用 curl 補測試資料時實際踩到。</para>
        /// </summary>
        public const string PresentField = "pp_present";

        private readonly IPlantParamService _plantParams;
        private readonly ISystemParamService _systemParams;
        private readonly IUserContext _userContext;

        public PlantFormEnhancementFilter(
            IPlantParamService plantParams,
            ISystemParamService systemParams,
            IUserContext userContext)
        {
            _plantParams  = plantParams;
            _systemParams = systemParams;
            _userContext  = userContext;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var controller = context.RouteData.Values["controller"]?.ToString();
            if (controller != "Plant") { await next(); return; }

            var action = context.RouteData.Values["action"]?.ToString();
            var isForm = action is "Create" or "Edit";
            if (!isForm) { await next(); return; }

            var isPost = HttpMethods.IsPost(context.HttpContext.Request.Method);

            var executed = await next();

            if (!isPost)
            {
                if (executed.Result is ViewResult or PartialViewResult)
                    await ProjectFormAsync(executed, context);
                return;
            }

            // Core 的 Create/Edit 成功時回 Json{success=true}（AJAX）或 RedirectToAction（非 AJAX）。
            // 失敗會重新回傳表單 View，這時不可寫入——否則參數會在廠別本身沒存起來的情況下落地。
            if (!IsSuccess(executed.Result)) return;

            await PersistAsync(context.HttpContext.Request.Form, context.HttpContext.RequestAborted);
        }

        private static bool IsSuccess(IActionResult? result) => result switch
        {
            RedirectToActionResult => true,
            JsonResult json        => json.Value?.GetType().GetProperty("success")?.GetValue(json.Value) is true,
            _                      => false,
        };

        private async Task ProjectFormAsync(ActionExecutedContext executed, ActionExecutingContext context)
        {
            var viewData = executed.Result switch
            {
                ViewResult vr        => vr.ViewData,
                PartialViewResult pv => pv.ViewData,
                _                    => null,
            };
            if (viewData == null) return;

            // Edit 的廠別代碼來自路由/查詢字串；Create 尚無廠別，此時只顯示全域值、一律呈現「繼承」。
            var plantId = context.ActionArguments.TryGetValue("id", out var idArg) ? idArg as string : null;

            var globals = new Dictionary<string, string?>();
            foreach (var def in PlantParamCatalog.All)
                globals[def.Key] = await _systemParams.GetAsync(def.Key);

            viewData[GlobalsKey]   = globals;
            viewData[PlantIdKey]   = plantId ?? "";
            viewData[OverridesKey] = string.IsNullOrEmpty(plantId)
                ? new Dictionary<string, string?>()
                : await _plantParams.GetOverridesAsync(plantId, context.HttpContext.RequestAborted);
        }

        private async Task PersistAsync(IFormCollection form, CancellationToken ct)
        {
            // 這次 POST 有沒有帶參數區塊？沒帶就不是「全部取消覆寫」，而是「這張表單不管參數」。
            if (!form.ContainsKey(PresentField)) return;

            // 廠別代碼：Edit 與 Create 都由表單的 Id 欄位帶出（_PlantForm 的主鍵欄位）。
            var plantId = form["Id"].LastOrDefault()?.Trim();
            if (string.IsNullOrEmpty(plantId)) return;

            // §3.6：只能改自己有權的廠。PlantController 目前是 Admin-only，這一層是縱深防禦——
            // 哪天 Core 把授權放寬成選單權限，這裡不會跟著失守。
            var allowed = _userContext.AllowedPlantIds;
            if (allowed.Count > 0 && !allowed.Contains(plantId)) return;

            foreach (var def in PlantParamCatalog.All)
            {
                if (!IsChecked(form, def.OverrideField))
                {
                    await _plantParams.ClearAsync(def.Key, plantId, ct);
                    continue;
                }

                var value = def.Kind switch
                {
                    PlantParamKind.Bool   => IsChecked(form, def.ValueField) ? "true" : "false",
                    PlantParamKind.Number => NormalizeNumber(form[def.ValueField].LastOrDefault()),
                    PlantParamKind.Choice => NormalizeChoice(form[def.ValueField].LastOrDefault(), def.Choices),
                    _                     => Trimmed(form[def.ValueField].LastOrDefault()),
                };

                await _plantParams.SetAsync(def.Key, plantId, value, ct);
            }
        }

        /// <summary>
        /// 讀「hidden false + checkbox true」配對。
        /// <b>刻意用 <c>Any</c> 而非 <c>FirstOrDefault</c></b>：瀏覽器依 DOM 順序送出兩個同名欄位，
        /// 取第一個會被 hidden 的 "false" 蓋掉，勾了也存不進去（<c>SystemParamEnhancementFilter</c>
        /// 曾因此而失效）。這樣讀就與兩個 input 的先後無關。
        /// </summary>
        private static bool IsChecked(IFormCollection form, string field) =>
            form[field].Any(v => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));

        private static string? Trimmed(string? raw) =>
            string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

        /// <summary>非負數才收；解析不過或負數一律視為未填（存 null＝該廠明確不設閥值）。</summary>
        private static string? NormalizeNumber(string? raw) =>
            decimal.TryParse(raw, out var v) && v >= 0 ? v.ToString("0.####") : null;

        /// <summary>只收清單內的選項，擋掉竄改表單塞進來的值。</summary>
        private static string? NormalizeChoice(string? raw, string[]? choices)
        {
            var t = Trimmed(raw);
            if (t == null || choices == null) return null;
            return choices.FirstOrDefault(c => string.Equals(c, t, StringComparison.OrdinalIgnoreCase));
        }
    }
}
