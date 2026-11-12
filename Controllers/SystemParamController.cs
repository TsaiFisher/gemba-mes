using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Controllers
{
    [Authorize]
    // 依權限矩陣把關（原本寫死 Admin，選單看得到卻點不進去；見 AdminAccess）。類別層級＝讀取，寫入動作另標
    [RequireMenuPermission("SystemParam")]
    public class SystemParamController : Controller
    {
        private readonly ISystemParamService _svc;
        private readonly IWebHostEnvironment _env;
        private const long MaxLogoBytes = 2 * 1024 * 1024; // 2 MB

        // M1：副檔名白名單 — 刻意排除 .svg（可內嵌 <script>）
        private static readonly HashSet<string> AllowedLogoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".webp"
        };

        public SystemParamController(ISystemParamService svc, IWebHostEnvironment env)
        {
            _svc = svc;
            _env = env;
        }

        public async Task<IActionResult> Index()
        {
            var all = await _svc.GetAllAsync();
            var vm = new SystemParamViewModel
            {
                CompanyNameZh = all.GetValueOrDefault(SystemParamKeys.CompanyNameZh),
                CompanyNameEn = all.GetValueOrDefault(SystemParamKeys.CompanyNameEn),
                CompanyShortZh = all.GetValueOrDefault(SystemParamKeys.CompanyShortZh),
                CompanyShortEn = all.GetValueOrDefault(SystemParamKeys.CompanyShortEn),
                LogoPath = all.GetValueOrDefault(SystemParamKeys.CompanyLogo),
            };
            return View(vm);
        }

        [HttpPost, ValidateAntiForgeryToken]
        [RequireMenuPermission("SystemParam", MenuPermissionAction.Update)]
        public async Task<IActionResult> Save(SystemParamViewModel vm)
        {
            // 目前的 Logo 一律以資料庫存的值為準。vm.LogoPath 來自表單隱藏欄位、可被竄改：
            // 原本拿它當「要刪的舊檔」與「沒上傳時要存回的值」，竄改即可刪掉 uploads 內任一檔，
            // 或把任意網址存成 Logo 出現在每一頁版頭（2026-10-20）。
            var logoPath = (await _svc.GetAllAsync()).GetValueOrDefault(SystemParamKeys.CompanyLogo);
            vm.LogoPath = logoPath;

            if (!ModelState.IsValid) return View("Index", vm);

            // ── Logo 處理 ──────────────────────────────────────────────────

            if (vm.RemoveLogo)
            {
                DeleteLogoFile(logoPath);
                logoPath = null;
            }
            else if (vm.LogoFile != null && vm.LogoFile.Length > 0)
            {
                if (vm.LogoFile.Length > MaxLogoBytes)
                {
                    ModelState.AddModelError("LogoFile", CoreText.Get("Msg_LogoTooLarge"));
                    vm.LogoPath = logoPath;
                    return View("Index", vm);
                }
                if (!vm.LogoFile.ContentType.StartsWith("image/"))
                {
                    ModelState.AddModelError("LogoFile", CoreText.Get("Msg_LogoNotImage"));
                    vm.LogoPath = logoPath;
                    return View("Index", vm);
                }

                // M1：以副檔名白名單為準（content-type 可偽造，且要明確排除 SVG）
                var ext = Path.GetExtension(vm.LogoFile.FileName).ToLowerInvariant();
                if (!AllowedLogoExtensions.Contains(ext))
                {
                    ModelState.AddModelError("LogoFile", CoreText.Get("Msg_LogoBadExtension"));
                    vm.LogoPath = logoPath;
                    return View("Index", vm);
                }

                // L5：magic bytes 驗證 — 副檔名與 content-type 皆可偽造，實際比對檔頭簽章，
                //     確保內容確為允許的點陣影像（擋改副檔名的 polyglot／偽裝檔）。
                if (!ImageSignatureValidator.HasAllowedImageSignature(vm.LogoFile))
                {
                    ModelState.AddModelError("LogoFile", CoreText.Get("Msg_LogoBadContent"));
                    vm.LogoPath = logoPath;
                    return View("Index", vm);
                }

                // 刪除舊 Logo
                DeleteLogoFile(logoPath);

                var uploadsDir = Path.Combine(_env.WebRootPath, "uploads");
                Directory.CreateDirectory(uploadsDir);

                var fileName = $"logo-{Guid.NewGuid():N}{ext}";
                var fullPath = Path.Combine(uploadsDir, fileName);

                using (var stream = new FileStream(fullPath, FileMode.Create))
                    await vm.LogoFile.CopyToAsync(stream);

                logoPath = $"/uploads/{fileName}";
            }

            await _svc.SetBatchAsync(new Dictionary<string, string?>
            {
                [SystemParamKeys.CompanyNameZh]  = vm.CompanyNameZh?.Trim(),
                [SystemParamKeys.CompanyNameEn]  = vm.CompanyNameEn?.Trim(),
                [SystemParamKeys.CompanyShortZh] = vm.CompanyShortZh?.Trim(),
                [SystemParamKeys.CompanyShortEn] = vm.CompanyShortEn?.Trim(),
                [SystemParamKeys.CompanyLogo]    = logoPath,
            });

            TempData["Success"] = CoreText.Get("Msg_SystemParamSaved");
            return RedirectToAction(nameof(Index));
        }

        private void DeleteLogoFile(string? logoPath)
        {
            if (string.IsNullOrWhiteSpace(logoPath)) return;

            // L1：確保最終路徑仍位於 wwwroot/uploads，避免從 DB 注入相對路徑
            //     （如 "../../Security/license.json"）誤刪檔案。
            var uploadsDir = Path.GetFullPath(Path.Combine(_env.WebRootPath, "uploads"));
            var candidate  = Path.GetFullPath(Path.Combine(_env.WebRootPath, logoPath.TrimStart('/')));
            if (!candidate.StartsWith(uploadsDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return;

            if (System.IO.File.Exists(candidate))
                System.IO.File.Delete(candidate);
        }
    }

    public class SystemParamViewModel
    {
        public string? CompanyNameZh  { get; set; }
        public string? CompanyNameEn  { get; set; }
        public string? CompanyShortZh { get; set; }
        public string? CompanyShortEn { get; set; }
        public string? LogoPath       { get; set; }
        public IFormFile? LogoFile    { get; set; }
        public bool RemoveLogo        { get; set; }
    }
}
