using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Filters
{
    /// <summary>
    /// 擋下「畫面在 A 廠渲染、送出時人已經在 B 廠」的寫入，以及沒帶廠別快照的廠別寫入。
    ///
    /// <para><b>危害</b>：<c>ActivePlantId</c> 是使用者資料列上的欄位，桌機與手機共用；
    /// <c>ApplyGlobalValues</c> 在 <c>SaveChanges</c> 當下才填 <c>PlantId ??= CurrentPlantId</c>，
    /// 所以電腦開著 A 廠表單、手機切到 B、回電腦存檔，單據會靜默建到 B 廠。</para>
    ///
    /// <para><b>2026-11-06 起改為強制</b>（security-review-20261105 §5 #3）：廠別寫入（見
    /// <see cref="RequiresContext"/>）缺少或不一致都回 409，不寫入。快照由
    /// <c>wwwroot/js/plant-context.js</c>（桌機 fetch／jQuery／原生表單）、手機 <c>api.js</c> 與
    /// 離線佇列帶上。快照只防誤寫；存取授權仍由 IUserContext 與權限閘門負責。</para>
    /// </summary>
    public class StalePlantContextFilter : IAsyncActionFilter
    {
        /// <summary>畫面渲染當下的廠別；由 <c>_CrudPanelScripts.cshtml</c> 寫進 DOM、submitPanel 送出。</summary>
        public const string HeaderName = "X-Plant-Context";

        /// <summary>409 回應代碼：畫面開啟時的廠別與目前不同。</summary>
        public const string PlantContextChangedCode = "PLANT_CONTEXT_CHANGED";

        /// <summary>409 回應代碼：請求沒帶廠別快照。</summary>
        public const string PlantContextMissingCode = "PLANT_CONTEXT_MISSING";

        private readonly IUserContext _userContext;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly ILogger<StalePlantContextFilter> _logger;

        public StalePlantContextFilter(
            IUserContext userContext,
            IStringLocalizer<ProjectZSharedResource> localizer,
            ILogger<StalePlantContextFilter> logger)
        {
            _userContext = userContext;
            _localizer = localizer;
            _logger = logger;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            // Read form bodies asynchronously before checking the cached form collection.
            if (context.HttpContext.Request.HasFormContentType)
                await context.HttpContext.Request.ReadFormAsync(context.HttpContext.RequestAborted);
            OnActionExecuting(context);
            if (context.Result == null) await next();
        }

        public void OnActionExecuting(ActionExecutingContext context)
        {
            var request = context.HttpContext.Request;

            // 只管會寫入的動作。GET 拿到舊廠別的資料頂多是看到不該看的清單，
            // 而那已經由全域查詢過濾擋掉了；這裡要防的是**寫**進錯的廠。
            if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
                || HttpMethods.IsOptions(request.Method))
                return;

            var required = RequiresContext(context.ActionDescriptor as ControllerActionDescriptor);
            if (!required) return;

            var renderedPlant = request.Headers[HeaderName].ToString();
            if (string.IsNullOrEmpty(renderedPlant) && request.HasFormContentType)
                renderedPlant = request.Form["__PlantContext"].ToString();
            // 只接受廠別代碼的字元；其他內容一律當作缺少快照，也不回顯到訊息裡
            // （手機端以 innerHTML 顯示 toast，避免把請求標頭原樣送回畫面）。
            if (!IsPlantCode(renderedPlant)) renderedPlant = string.Empty;
            var currentPlant  = _userContext.PlantId ?? string.Empty;

            if (renderedPlant.Length > 0 && string.Equals(renderedPlant, currentPlant, StringComparison.Ordinal)) return;

            _logger.LogWarning(
                "擋下跨廠別的寫入：畫面渲染於 {RenderedPlant}，送出時使用者已在 {CurrentPlant}（{Path}）",
                renderedPlant, currentPlant, request.Path);

            // 回應格式與 DbUniqueViolationExceptionFilter 一致：submitPanel 收到
            // { success:false, message } 會把訊息顯示給使用者，而且**不會**關掉面板
            // ——使用者填的資料還在，重新整理後可以照原本的內容再送一次。
            // 手機離線佇列收到 409（沒有 CONCURRENCY_CONFLICT 代碼）會把該筆標成錯誤並保留，不會丟資料。
            var message = renderedPlant.Length == 0
                ? _localizer["PlantContextMissing"].Value
                : _localizer["PlantContextChanged", renderedPlant, currentPlant].Value;
            // code／renderedPlant：桌機面板據此提供「切回原廠別並照原內容存檔」，不必重新整理
            // ——重新整理會關掉面板、填的內容全部消失（2026-11-11 筆電驗收 C3）。
            context.Result = new JsonResult(new
                {
                    success = false,
                    message,
                    code = renderedPlant.Length == 0 ? PlantContextMissingCode : PlantContextChangedCode,
                    renderedPlant = renderedPlant.Length == 0 ? null : renderedPlant,
                })
                { StatusCode = StatusCodes.Status409Conflict };
        }

        private static bool IsPlantCode(string value) =>
            value.Length is > 0 and <= 50 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

        // ── 哪些寫入必須帶廠別快照 ───────────────────────────────────────────
        //
        // 以「控制器寫的是不是廠別資料（IPlantEntity）」分類。BAS 與手機 API 混有廠別與全域兩種，
        // 逐支列名；StalePlantContextFilterTests 會列舉這兩個命名空間的所有控制器，
        // **新增控制器沒有歸類就讓測試紅**，避免新的廠別寫入靜默落在防護之外。
        internal static readonly string[] PlantModules = ["MFG", "INV", "PUR", "ORD", "QCR", "FIN", "ACT"];

        internal static readonly HashSet<string> BasPlantControllers =
            ["Warehouse", "WorkCenter", "Tooling", "SpecialDay", "Shift", "ShiftGroup", "DocNumberConfig", "ContainerUnit"];

        /// <summary>全域主檔或唯讀：不要求快照（在舊分頁編輯幣別、品號不會寫錯廠）。</summary>
        internal static readonly HashSet<string> BasGlobalControllers =
            ["ChartOfAccount", "Company", "Container", "CurrencyApi", "Currency", "InventoryTransactionType",
             "PlantParamOverview", "ProductCategory", "Product", "Tax", "UomApi", "Uom"];

        internal static readonly HashSet<string> MobilePlantControllers =
            ["MobileMfg", "MobileInv", "MobileMaterial", "MobileApproval"];

        /// <summary>登入、切換廠別／角色、推播訂閱、模組清單：不寫廠別資料，或本身就是復原入口。</summary>
        internal static readonly HashSet<string> MobileOtherControllers =
            ["MobileAuth", "MobilePlant", "MobileRole", "MobilePush", "MobileModule"];

        internal static bool RequiresContext(ControllerActionDescriptor? action)
        {
            if (action == null) return false;
            var ns = action.ControllerTypeInfo.Namespace ?? "";
            if (ns == "SmartFactory.MES.Api.Mobile")
                return MobilePlantControllers.Contains(action.ControllerName);
            if (PlantModules.Any(module => ns == $"SmartFactory.MES.Modules.{module}.Controllers")) return true;
            return ns == "SmartFactory.MES.Modules.BAS.Controllers" && BasPlantControllers.Contains(action.ControllerName);
        }

    }
}
