using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Controllers
{
    /// <summary>
    /// 授權狀態頁（SECURITY-OPERATIONS.md §18.3、§18.8）：狀態與理由、申請授權（登記頁）、
    /// 離線換發、匯入新授權檔。唯讀與停用時 LicenseReadOnlyMiddleware 一律放行這裡。
    /// 授權編號、指紋與匯入只給 Admin。
    /// </summary>
    [Authorize(Policy = "AuthenticatedUser")]
    public class LicenseController(
        ILicenseStateService state,
        IConfiguration configuration,
        LicenseImportService importer,
        LeaseRenewer leases,
        LeaseOptions leaseOptions) : Controller
    {
        /// <summary>確認回覆的大小上限（已簽章的租約或通知約 1–2 KB）。</summary>
        private const int MaxCheckinResponseBytes = 16 * 1024;

        [PermissionGateExempt("授權狀態不含業務資料；授權編號、指紋與匯入限 Admin")]
        public async Task<IActionResult> Index()
        {
            var isAdmin = await AdminAccess.IsActiveAdminAsync(HttpContext);   // 作用中角色，與 [RequireActiveAdmin] 一致
            var serverUrl = configuration["License:ServerUrl"] ?? LicenseRequestCode.DefaultServerUrl;
            var vm = new LicenseStatusViewModel { Status = state.Current, IsAdmin = isAdmin, Inputs = state.Inputs };

            if (isAdmin)
            {
                try
                {
                    vm.Fingerprint = MachineAnchor.GetFingerprint();
                    var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "";
                    vm.Host = HostIdentity.Detect();
                    // TPM 偵測是第二期（§18.7）；目前一律回報無
                    vm.RequestCode = LicenseRequestCode.Build(vm.Fingerprint, hasTpm: false, version, vm.Host);
                    vm.RegisterUrl = LicenseRequestCode.RegisterUrl(serverUrl, vm.RequestCode);
                }
                catch (InvalidOperationException)
                {
                    vm.Fingerprint = null;   // 錨定檔讀不到：畫面提示改用 MachineId 工具
                }
                vm.RenewUrl = LicenseRequestCode.RenewUrl(serverUrl, vm.RequestCode);

                // VM 授權每 90 天確認（§18.15.3）：relay 由這頁經瀏覽器送出，MES 本身不連外
                if (await leases.BuildRequestAsync() is { } request)
                {
                    var server = leases.ResolveServerOffline();
                    vm.CheckinUrl = LeaseRenewer.CheckinUrl(server, request);
                    vm.CheckinOrigin = new Uri(server).GetLeftPart(UriPartial.Authority);
                    vm.CheckinRequestJson = request.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    vm.IsRelay = leaseOptions.IsRelay;
                }
            }
            return View(vm);
        }

        /// <summary>
        /// relay 確認：授權狀態頁收到確認頁 postMessage 傳回的回覆後送到這裡。回覆經過瀏覽器、來源不可信，
        /// 由 <see cref="LeaseRenewer.ApplyResponse"/> 驗簽——驗不過不寫入、不改狀態。
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        [RequireActiveAdmin]
        [RequestSizeLimit(MaxCheckinResponseBytes + 4096)]
        public IActionResult CheckinResponse([FromForm] string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxCheckinResponseBytes)
                return Json(new { success = false, message = CoreText.Get("Msg_LicenseCheckinInvalid") });
            var outcome = leases.ApplyResponse(text);
            return Json(new { success = outcome == LeaseOutcome.Leased, message = CheckinMessage(outcome) });
        }

        /// <summary>備案：管理員電腦不能上網時，用手機開確認連結、下載回覆檔，在這裡匯入。</summary>
        [HttpPost, ValidateAntiForgeryToken]
        [RequireActiveAdmin]
        [RequestSizeLimit(MaxCheckinResponseBytes + 4096)]
        public async Task<IActionResult> ImportCheckin(IFormFile? file)
        {
            LeaseOutcome outcome = LeaseOutcome.Rejected;
            if (file is { Length: > 0 and <= MaxCheckinResponseBytes })
            {
                using var reader = new StreamReader(file.OpenReadStream());
                outcome = leases.ApplyResponse(await reader.ReadToEndAsync());
            }
            TempData[outcome == LeaseOutcome.Leased ? "LicenseImportOk" : "LicenseImportError"] = CheckinMessage(outcome);
            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// 系統參數「線上授權確認」（§18.15.8）：只對 offline 授權；啟用前須已確認成功，
        /// VM／容器上不可停用（停用了也無效——VM 強制不讀這個參數，這裡擋只是不讓畫面說謊）。
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        [RequireActiveAdmin]
        public async Task<IActionResult> OnlineCheck(bool enable, [FromServices] ISystemParamService parameters, [FromServices] TimeProvider time)
        {
            var error = LicenseOnlineCheck.CheckChange(state.Inputs, enable, time.GetUtcNow());
            if (error is not null)
            {
                TempData["LicenseImportError"] = CoreText.Get(error);
                return RedirectToAction(nameof(Index));
            }
            await parameters.SetAsync(Models.SystemParamKeys.LicenseOnlineCheck, enable ? "true" : "false");
            state.SetOnlineCheckOptIn(enable);
            TempData["LicenseImportOk"] = CoreText.Get(enable ? "Msg_LicenseOnlineCheckEnabled" : "Msg_LicenseOnlineCheckDisabled");
            return RedirectToAction(nameof(Index));
        }

        private string CheckinMessage(LeaseOutcome outcome) => outcome switch
        {
            LeaseOutcome.Leased => CoreText.Get("Msg_LicenseCheckinOk"),
            LeaseOutcome.Notice => CoreText.Get("Msg_LicenseCheckinNotice") + "\n" + state.Current.Reason,
            LeaseOutcome.NotApplicable => CoreText.Get("Msg_LicenseCheckinNotApplicable"),
            _ => CoreText.Get("Msg_LicenseCheckinInvalid"),
        };

        [HttpPost, ValidateAntiForgeryToken]
        [RequireActiveAdmin]
        [RequestSizeLimit(LicenseImportService.MaxBytes + 4096)]
        public async Task<IActionResult> Import(IFormFile? file)
        {
            if (file is null || file.Length == 0)
            {
                TempData["LicenseImportError"] = CoreText.Get("Msg_LicenseImportInvalidFile");
                return RedirectToAction(nameof(Index));
            }

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var r = importer.Import(ms.ToArray());

            var text = CoreText.Get(r.MessageKey) + (r.Detail is null ? "" : $"\n{r.Detail}");
            TempData[r.Success ? "LicenseImportOk" : "LicenseImportError"] = text;
            return RedirectToAction(nameof(Index));
        }
    }

    public sealed class LicenseStatusViewModel
    {
        public required LicenseStatus Status { get; init; }
        public bool IsAdmin { get; init; }
        /// <summary>判定輸入：線上確認的三個來源（簽章、VM 偵測、系統參數）與確認期限。</summary>
        public LicenseStateInputs? Inputs { get; init; }
        public string? Fingerprint { get; set; }
        public HostIdentityResult? Host { get; set; }
        public string? RequestCode { get; set; }
        public string? RegisterUrl { get; set; }
        public string? RenewUrl { get; set; }
        /// <summary>確認頁網址（請求在 # 之後）；lease 與 offline 授權才有，其餘為 null。</summary>
        public string? CheckinUrl { get; set; }
        public string? CheckinOrigin { get; set; }
        /// <summary>確認時送出的完整內容，畫面上可逐字檢視（§18.15.6）。</summary>
        public string? CheckinRequestJson { get; set; }
        public bool IsRelay { get; set; } = true;
    }
}
