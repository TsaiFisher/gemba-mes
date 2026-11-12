using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.QCR.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.QCR.Controllers
{
    /// <summary>
    /// 檢驗儀器主檔（全域，對應舊系統 QIN01）。
    /// Instrument 無廠別隔離；InstrumentSerial 為廠別主檔，按當前廠別過濾。
    /// </summary>
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class InstrumentController : ModuleControllerBase
    {
        private const string MenuKey = "Instrument";
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        public InstrumentController(IStringLocalizer<ProjectZSharedResource> localizer)
            => _localizer = localizer;

        public async Task<IActionResult> Index(
            int? pageNumber = 1, string sortOrder = "code_asc",
            string searchCode = "", string searchName = "",
            bool searched = false, int pageSize = 20)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            ViewBag.CurrentSort     = sortOrder;
            ViewBag.SearchCode      = searchCode;
            ViewBag.InstrumentOptions = await Db.Instruments
                .AsNoTracking()
                .OrderBy(i => i.InstrumentCode)
                .Select(i => new { i.InstrumentCode, i.InstrumentName })
                .ToListAsync();
            ViewBag.SearchName      = searchName;
            ViewBag.Searched        = searched;
            ViewBag.CurrentPageSize = pageSize;

            await PrepareIndexPermissionsAsync(MenuKey);
            if (!searched) return View((PaginatedList<Instrument>?)null);

            // Instrument 為全域主檔，不過濾 PlantId；Include Serials 以取得數量
            var q = Db.Instruments.AsNoTracking()
                .Include(i => i.Serials)
                .AsQueryable();

            if (!string.IsNullOrEmpty(searchCode))
                q = q.Where(i => i.InstrumentCode == searchCode);

            if (!string.IsNullOrEmpty(searchName))
                q = q.Where(i => i.InstrumentName.Contains(searchName));

            q = sortOrder switch
            {
                "code_desc"  => q.OrderByDescending(i => i.InstrumentCode),
                "name_asc"   => q.OrderBy(i => i.InstrumentName),
                "name_desc"  => q.OrderByDescending(i => i.InstrumentName),
                _            => q.OrderBy(i => i.InstrumentCode)
            };

            return View(await PaginatedList<Instrument>.CreateAsync(q, pageNumber ?? 1, pageSize));
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id)) return Forbid();
            var plantId = UserContext.PlantId;
            var instrument = await Db.Instruments.AsNoTracking()
                .Include(i => i.Serials.Where(s => s.PlantId == plantId))
                .FirstOrDefaultAsync(i => i.InstrumentCode == id);

            if (instrument == null)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
                return NotFound();
            }

            ViewBag.IsReadOnly = true;
            return IsAjax ? PartialView("_Details", instrument) : View("_Details", instrument);
        }

        public async Task<IActionResult> Create()
        {
            if (!await Permissions.CanCreate(MenuKey)) return Forbid();
            await PrepareIndexPermissionsAsync(MenuKey);
            return IsAjax ? PartialView("_Form", new Instrument()) : View("_Form", new Instrument());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([FromForm] Instrument model)
        {
            if (!await Permissions.CanCreate(MenuKey))
                return JsonFail(_localizer["NoCreatePermission"].Value);

            if (!ModelState.IsValid)
                return JsonFail(string.Join("; ", ModelState.Values
                    .SelectMany(v => v.Errors).Select(e => e.ErrorMessage)));

            if (await Db.Instruments.AnyAsync(i => i.InstrumentCode == model.InstrumentCode))
                return JsonFail(_localizer["DuplicateRecord"].Value);

            model.CreatedAt = model.UpdatedAt = DateTime.UtcNow;
            Db.Instruments.Add(model);
            await Db.SaveChangesAsync();
            return JsonOk(message: _localizer["CreateSuccess"].Value);
        }

        public async Task<IActionResult> Edit(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();

            var instrument = await Db.Instruments.AsNoTracking()
                .FirstOrDefaultAsync(i => i.InstrumentCode == id);

            if (instrument == null)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
                return NotFound();
            }

            await PrepareIndexPermissionsAsync(MenuKey);
            return IsAjax ? PartialView("_Form", instrument) : View("_Form", instrument);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, [FromForm] Instrument model)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return JsonFail(_localizer["NoUpdatePermission"].Value);

            if (id != model.InstrumentCode) return JsonFail(_localizer["RecordNotFound"].Value);

            var existing = await Db.Instruments.FirstOrDefaultAsync(i => i.InstrumentCode == id);
            if (existing == null) return JsonFail(_localizer["RecordNotFound"].Value);

            existing.InstrumentName             = model.InstrumentName;
            existing.CalibrationFrequencyDays   = model.CalibrationFrequencyDays;
            existing.CalibrationMethod          = model.CalibrationMethod;
            existing.IsActive                   = model.IsActive;
            existing.Remarks                    = model.Remarks;
            existing.UpdatedAt                  = DateTime.UtcNow;

            await Db.SaveChangesAsync();
            return JsonOk(message: _localizer["UpdateSuccess"].Value);
        }

        // ── InstrumentSerial CRUD ─────────────────────────────────────────────────

        /// <summary>
        /// 保管部門／保管人下拉。原本是自由輸入文字框，值無從比對主檔（2026-10-02 參照欄位盤點）。
        /// 部門比照內部訂單：本廠的組織單位（廠別沿上層繼承）；保管人比照報表排程的使用者下拉。
        /// </summary>
        private async Task PrepareSerialDropdownsAsync()
        {
            var units = await Db.OrganizationUnits.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            var byId = units.ToDictionary(u => u.Id);
            string? Effective(OrganizationUnit u)
            {
                for (var depth = 0; depth < 32; depth++)
                {
                    if (!string.IsNullOrEmpty(u.PlantId)) return u.PlantId;
                    if (string.IsNullOrEmpty(u.ParentId) || !byId.TryGetValue(u.ParentId, out var parent)) return null;
                    u = parent;
                }
                return null;
            }
            ViewBag.DeptOptions = units.Where(u => Effective(u) == UserContext.PlantId)
                .OrderBy(u => u.Name).Select(u => new { u.Id, u.Name }).ToList();
            ViewBag.UserOptions = await Db.Users.AsNoTracking()
                .OrderBy(u => u.UserName)
                .Select(u => new { u.Id, Name = u.UserName ?? u.Id })
                .ToListAsync();
        }

        public async Task<IActionResult> CreateSerial(string instrumentCode)
        {
            if (!await Permissions.CanCreate(MenuKey)) return Forbid();
            var model = new InstrumentSerial
            {
                InstrumentCode = instrumentCode,
                PlantId        = UserContext.PlantId,
                Status         = "Active"
            };
            await PrepareSerialDropdownsAsync();
            return IsAjax ? PartialView("_SerialForm", model) : View("_SerialForm", model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateSerial([FromForm] InstrumentSerial model)
        {
            if (!await Permissions.CanCreate(MenuKey))
                return JsonFail(_localizer["NoCreatePermission"].Value);

            model.SerialId  = Guid.NewGuid().ToString("N");
            model.PlantId   = UserContext.PlantId;
            model.CreatedAt = model.UpdatedAt = DateTime.UtcNow;
            Db.InstrumentSerials.Add(model);
            await Db.SaveChangesAsync();
            return JsonOk(message: _localizer["CreateSuccess"].Value);
        }

        public async Task<IActionResult> EditSerial(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            var serial = await Db.InstrumentSerials.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SerialId == id);
            if (serial == null)
                return IsAjax ? JsonFail(_localizer["RecordNotFound"].Value) : NotFound();
            await PrepareSerialDropdownsAsync();
            return IsAjax ? PartialView("_SerialForm", serial) : View("_SerialForm", serial);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> EditSerial(string id, [FromForm] InstrumentSerial model)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return JsonFail(_localizer["NoUpdatePermission"].Value);

            var existing = await Db.InstrumentSerials.FirstOrDefaultAsync(s => s.SerialId == id);
            if (existing == null) return JsonFail(_localizer["RecordNotFound"].Value);

            existing.SerialNo                 = model.SerialNo;
            existing.DepartmentCode           = model.DepartmentCode;
            existing.CustodianId              = model.CustodianId;
            existing.EffectiveDate            = model.EffectiveDate;
            existing.ObsoleteDate             = model.ObsoleteDate;
            existing.Status                   = model.Status;
            existing.Brand                    = model.Brand;
            existing.Model                    = model.Model;
            existing.Spec                     = model.Spec;
            existing.Tolerance                = model.Tolerance;
            existing.Accuracy                 = model.Accuracy;
            existing.CalibrationFrequencyDays = model.CalibrationFrequencyDays;
            existing.CalibrationMethod        = model.CalibrationMethod;
            existing.LastCalibrationDate      = model.LastCalibrationDate;
            existing.NextCalibrationDate      = model.NextCalibrationDate;
            existing.Remarks                  = model.Remarks;
            existing.UpdatedAt                = DateTime.UtcNow;
            await Db.SaveChangesAsync();
            return JsonOk(message: _localizer["UpdateSuccess"].Value);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSerial(string id)
        {
            if (!await Permissions.CanDelete(MenuKey))
                return JsonFail(_localizer["NoDeletePermission"].Value);
            var serial = await Db.InstrumentSerials.FirstOrDefaultAsync(s => s.SerialId == id);
            if (serial == null) return JsonFail(_localizer["RecordNotFound"].Value);
            Db.InstrumentSerials.Remove(serial);
            await Db.SaveChangesAsync();
            return JsonOk(message: _localizer["DeleteSuccess"].Value);
        }

        /// <summary>快速執行校正：自動計算 LastCalibrationDate = today、NextCalibrationDate = today + 週期天數。</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Calibrate(string serialId, string? remarks)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return JsonFail(_localizer["NoUpdatePermission"].Value);

            var serial = await Db.InstrumentSerials
                .Include(s => s.Instrument)
                .FirstOrDefaultAsync(s => s.SerialId == serialId);
            if (serial == null)
                return JsonFail(_localizer["RecordNotFound"].Value);

            var today = DateOnly.FromDateTime(DateTime.Today);
            var freqDays = serial.CalibrationFrequencyDays ?? serial.Instrument?.CalibrationFrequencyDays ?? 365;

            serial.LastCalibrationDate = today;
            serial.NextCalibrationDate = today.AddDays(freqDays);
            serial.Status              = "Active";
            serial.Remarks             = string.IsNullOrWhiteSpace(remarks)
                                         ? serial.Remarks
                                         : $"[{today:yyyy-MM-dd}] {remarks}";
            serial.UpdatedAt           = DateTime.UtcNow;

            await Db.SaveChangesAsync();
            return JsonOk(message: _localizer["CalibrateSuccess"].Value,
                data: new { nextCalibrationDate = serial.NextCalibrationDate?.ToString("yyyy-MM-dd") });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            if (!await Permissions.CanDelete(MenuKey))
                return JsonFail(_localizer["NoDeletePermission"].Value);

            var instrument = await Db.Instruments
                .Include(i => i.Serials)
                .FirstOrDefaultAsync(i => i.InstrumentCode == id);

            if (instrument == null)
                return JsonFail(_localizer["RecordNotFound"].Value);

            if (instrument.Serials.Any())
                return JsonFail(_localizer["HasSerialsError"].Value);

            Db.Instruments.Remove(instrument);
            await Db.SaveChangesAsync();
            return JsonOk(message: _localizer["DeleteSuccess"].Value);
        }
    }
}
