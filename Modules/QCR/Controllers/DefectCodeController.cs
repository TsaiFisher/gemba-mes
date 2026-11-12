using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.QCR.Controllers
{
    /// <summary>
    /// 不良項目代碼（全域主檔，對應舊系統 QNI01）。
    /// 四個旗標（FgIQC/FgIPQC/FgFQC/FgRQC）決定各檢驗類型可選用的不良原因。
    /// </summary>
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class DefectCodeController : ModuleControllerBase
    {
        private const string MenuKey = "DefectCode";
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        public DefectCodeController(IStringLocalizer<ProjectZSharedResource> localizer)
            => _localizer = localizer;

        public async Task<IActionResult> Index(
            int? pageNumber = 1, string sortOrder = "code_asc",
            string searchText = "", bool searched = false, int pageSize = 20)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            ViewBag.CurrentSort    = sortOrder;
            ViewBag.SearchText     = searchText;
            ViewBag.Searched       = searched;
            ViewBag.CurrentPageSize = pageSize;

            await PrepareIndexPermissionsAsync(MenuKey);
            if (!searched) return View((PaginatedList<DefectCode>?)null);

            var q = Db.DefectCodes.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(searchText))
                q = q.Where(d => d.Code.Contains(searchText) || d.Name.Contains(searchText));

            q = sortOrder switch
            {
                "name_asc"  => q.OrderBy(d => d.Name),
                "name_desc" => q.OrderByDescending(d => d.Name),
                "code_desc" => q.OrderByDescending(d => d.Code),
                _           => q.OrderBy(d => d.Code)
            };

            return View(await PaginatedList<DefectCode>.CreateAsync(q, pageNumber ?? 1, pageSize));
        }

        public async Task<IActionResult> Create()
        {
            if (!await Permissions.CanCreate(MenuKey)) return Forbid();
            await PrepareIndexPermissionsAsync(MenuKey);
            return IsAjax ? PartialView("_Form", new DefectCode()) : View("_Form", new DefectCode());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([FromForm] DefectCode model)
        {
            if (!await Permissions.CanCreate(MenuKey))
                return JsonFail(_localizer["NoCreatePermission"].Value);

            if (!ModelState.IsValid)
                return JsonFail(string.Join("; ", ModelState.Values
                    .SelectMany(v => v.Errors).Select(e => e.ErrorMessage)));

            if (await Db.DefectCodes.AnyAsync(d => d.Code == model.Code))
                return JsonFail(_localizer["DuplicateCode"].Value);

            model.CreatedAt = model.UpdatedAt = DateTime.UtcNow;
            Db.DefectCodes.Add(model);
            await Db.SaveChangesAsync();
            return JsonOk(message: _localizer["SaveSuccess"].Value);
        }

        public async Task<IActionResult> Edit(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            var rec = await Db.DefectCodes.AsNoTracking().FirstOrDefaultAsync(d => d.Code == id);
            if (rec == null) return IsAjax ? JsonFail(_localizer["RecordNotFound"].Value) : NotFound();

            await PrepareIndexPermissionsAsync(MenuKey);
            return IsAjax ? PartialView("_Form", rec) : View("_Form", rec);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit([FromForm] DefectCode model)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return JsonFail(_localizer["NoUpdatePermission"].Value);

            var rec = await Db.DefectCodes.FirstOrDefaultAsync(d => d.Code == model.Code);
            if (rec == null) return JsonFail(_localizer["RecordNotFound"].Value);

            rec.Name      = model.Name;
            rec.FgIQC     = model.FgIQC;
            rec.FgIPQC    = model.FgIPQC;
            rec.FgFQC     = model.FgFQC;
            rec.FgRQC     = model.FgRQC;
            rec.IsActive  = model.IsActive;
            rec.Remarks   = model.Remarks;
            rec.UpdatedAt = DateTime.UtcNow;
            await Db.SaveChangesAsync();
            return JsonOk(message: _localizer["SaveSuccess"].Value);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            if (!await Permissions.CanDelete(MenuKey))
                return JsonFail(_localizer["NoDeletePermission"].Value);

            var rec = await Db.DefectCodes.FirstOrDefaultAsync(d => d.Code == id);
            if (rec == null) return JsonFail(_localizer["RecordNotFound"].Value);

            Db.DefectCodes.Remove(rec);
            await Db.SaveChangesAsync();
            return JsonOk(message: _localizer["DeleteSuccess"].Value);
        }
    }
}
