using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Controllers
{
    [Authorize]
    [RequireActiveAdmin]   // 只看作用中角色（見 AdminAccess）
    public class PlantController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTime;

        public PlantController(ApplicationDbContext context, IExportService exportService,
            IDateTimeService dateTime)
        {
            _context = context;
            _exportService = exportService;
            _dateTime = dateTime;
        }

        private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";

        public async Task<IActionResult> Index(string? searchTerm, int? pageNumber, int? pageSize, string? sortOrder = null, bool searched = false)
        {
            ViewBag.CurrentSort = sortOrder;
            ViewBag.CodeSort = string.IsNullOrEmpty(sortOrder) || sortOrder == "code_asc" ? "code_desc" : "code_asc";
            ViewBag.NameSort = sortOrder == "name_asc" ? "name_desc" : "name_asc";
            ViewBag.LangSort = sortOrder == "lang_asc" ? "lang_desc" : "lang_asc";
            ViewBag.TzSort = sortOrder == "tz_asc" ? "tz_desc" : "tz_asc";
            ViewBag.SearchTerm = searchTerm;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.Searched = searched;

            if (!searched)
                return View((PaginatedList<Plant>?)null);

            var query = _context.Plants.AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(p =>
                    p.Id.ToLower().Contains(lower) ||
                    (p.Name != null && p.Name.ToLower().Contains(lower)));
            }
            query = sortOrder switch
            {
                "code_asc"  => query.OrderBy(p => p.Id),
                "code_desc" => query.OrderByDescending(p => p.Id),
                "name_asc"  => query.OrderBy(p => p.Name),
                "name_desc" => query.OrderByDescending(p => p.Name),
                "lang_asc"  => query.OrderBy(p => p.DefaultLanguage),
                "lang_desc" => query.OrderByDescending(p => p.DefaultLanguage),
                "tz_asc"    => query.OrderBy(p => p.DefaultTimeZone),
                "tz_desc"   => query.OrderByDescending(p => p.DefaultTimeZone),
                _           => query.OrderBy(p => p.Id),
            };
            return View(await PaginatedList<Plant>.CreateAsync(query.AsNoTracking(), pageNumber ?? 1, ViewBag.CurrentPageSize));
        }

        [HttpGet]
        public async Task<IActionResult> Export(string? searchTerm, string? sortOrder, string format = "xlsx")
        {
            var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
            var query = _context.Plants.AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(p => p.Id.ToLower().Contains(lower) || (p.Name != null && p.Name.ToLower().Contains(lower)));
            }
            query = sortOrder switch
            {
                "code_asc"  => query.OrderBy(p => p.Id),
                "code_desc" => query.OrderByDescending(p => p.Id),
                "name_asc"  => query.OrderBy(p => p.Name),
                "name_desc" => query.OrderByDescending(p => p.Name),
                "lang_asc"  => query.OrderBy(p => p.DefaultLanguage),
                "lang_desc" => query.OrderByDescending(p => p.DefaultLanguage),
                "tz_asc"    => query.OrderBy(p => p.DefaultTimeZone),
                "tz_desc"   => query.OrderByDescending(p => p.DefaultTimeZone),
                _           => query.OrderBy(p => p.Id),
            };
            var data = await query.AsNoTracking().ToListAsync();
            var now = _dateTime.Now;
            var req = new ExportRequest
            {
                Title = isZh ? "廠別管理" : "Plant Management",
                Headers = isZh
                    ? ["廠別代碼", "廠別名稱", "預設語系", "預設時區"]
                    : ["Plant Code", "Plant Name", "Default Language", "Default Timezone"],
                Rows = data.Select(p => new object?[] { p.Id, p.Name ?? "", p.DefaultLanguage ?? "", p.DefaultTimeZone ?? "" })
            };
            return format switch
            {
                "csv"  => File(_exportService.ToCsv(req), "text/csv", Uri.EscapeDataString($"Plant_{now:yyyyMMdd}.csv")),
                "json" => File(_exportService.ToJson(req), "application/json", Uri.EscapeDataString($"Plant_{now:yyyyMMdd}.json")),
                _      => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                               Uri.EscapeDataString($"Plant_{now:yyyyMMdd_HHmm}.xlsx"))
            };
        }

        public IActionResult Create()
        {
            ViewBag.IsEdit = false;
            if (IsAjax) return PartialView("_PlantForm", new Plant());
            return View("_PlantForm", new Plant());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Plant plant)
        {
            if (ModelState.IsValid)
            {
                _context.Add(plant);
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_CreateFailed") });
                    return StatusCode(500);
                }
                if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_PlantCreated") });
                return RedirectToAction(nameof(Index), new { searched = true });
            }
            ViewBag.IsEdit = false;
            if (IsAjax) return PartialView("_PlantForm", plant);
            return View("_PlantForm", plant);
        }

        public async Task<IActionResult> Edit(string id)
        {
            var plant = await _context.Plants.FindAsync(id);
            if (plant == null) return NotFound();
            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_PlantForm", plant);
            return View("_PlantForm", plant);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, Plant plant)
        {
            if (id != plant.Id) return NotFound();
            if (ModelState.IsValid)
            {
                // 不可用 _context.Update(plant)：本次請求的 DbContext 極可能已經追蹤著同一個廠別——
                // IUserContext 解析當前使用者時 .Include(u => u.ActivePlant)，而管線在動作執行前就會用到它。
                // 於是「編輯自己目前所在的廠別」會撞 identity map，整個請求 500。
                // 改成取回已追蹤的實體再套值：FindAsync 命中 identity map 時不會再查一次資料庫。
                var existing = await _context.Plants.FindAsync(id);
                if (existing == null) return NotFound();

                try
                {
                    _context.Entry(existing).CurrentValues.SetValues(plant);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!PlantExists(plant.Id)) return NotFound();
                    else throw;
                }
                if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_PlantUpdated") });
                return RedirectToAction(nameof(Index), new { searched = true });
            }
            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_PlantForm", plant);
            return View("_PlantForm", plant);
        }

        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            var plant = await _context.Plants.FindAsync(id);
            if (plant == null)
            {
                if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_PlantNotFound") });
                return NotFound();
            }

            // ── 使用中檢查 ──
            var reasons = new List<string>();

            if (await _context.Users.AnyAsync(u => u.ActivePlantId == id))
                reasons.Add(CoreText.Get("Msg_PlantInUse_Users"));

            if (await _context.OrganizationUnits.IgnoreQueryFilters().AnyAsync(o => o.PlantId == id))
                reasons.Add(CoreText.Get("Msg_PlantInUse_Orgs"));

            if (await _context.RolePlantAssignments.AnyAsync(r => r.PlantId == id))
                reasons.Add(CoreText.Get("Msg_PlantInUse_RolePlants"));

            if (reasons.Count > 0)
            {
                var msg = CoreText.Get("Msg_PlantInUseFmt", string.Join(CoreText.Get("Msg_ListSeparator"), reasons));
                if (IsAjax) return Json(new { success = false, message = msg });
                return StatusCode(400);
            }

            _context.Plants.Remove(plant);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_DeleteFailed") });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_PlantDeleted") });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        private bool PlantExists(string id) => _context.Plants.Any(e => e.Id == id);
    }
}
