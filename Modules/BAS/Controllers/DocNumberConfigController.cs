using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules._Shared;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.BAS.Controllers
{
    [Authorize]
    public class DocNumberConfigController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IDocNumberService _docNumberSvc;
        private readonly IPermissionService _permissionService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "DocNumberConfig";

        public DocNumberConfigController(ApplicationDbContext context,
            IDocNumberService docNumberSvc, IPermissionService permissionService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context;
            _docNumberSvc = docNumberSvc;
            _permissionService = permissionService;
            _localizer = localizer;
        }


        // GET: /DocNumberConfig
        public async Task<IActionResult> Index(
            string? searchTerm,
            int? pageNumber, int? pageSize,
            string? sortOrder,
            bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.SearchTerm  = searchTerm;
            ViewBag.Searched    = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort = sortOrder;
            ViewBag.DocTypeSort = string.IsNullOrEmpty(sortOrder) || sortOrder == "doctype_asc" ? "doctype_desc" : "doctype_asc";
            ViewBag.NameSort    = sortOrder == "name_asc" ? "name_desc" : "name_asc";

            ViewBag.CanCreate = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete = await _permissionService.CanDelete(MenuKey);
            ViewBag.PlantNames = await PlantNamesAsync();

            if (!searched) return View((PaginatedList<DocNumberConfig>?)null);

            var query = _context.DocNumberConfigs.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(c =>
                    c.DocType.ToLower().Contains(lower) ||
                    c.DocTypeName.ToLower().Contains(lower) ||
                    c.Prefix.ToLower().Contains(lower));
            }

            // 同 DocType 之下預設（""）在前、專屬廠別在後
            query = sortOrder switch
            {
                "doctype_desc" => query.OrderByDescending(c => c.DocType).ThenBy(c => c.PlantId),
                "name_asc"     => query.OrderBy(c => c.DocTypeName).ThenBy(c => c.PlantId),
                "name_desc"    => query.OrderByDescending(c => c.DocTypeName).ThenBy(c => c.PlantId),
                _              => query.OrderBy(c => c.DocType).ThenBy(c => c.PlantId),
            };

            var ps = pageSize ?? 20;
            return View(await PaginatedList<DocNumberConfig>.CreateAsync(query, pageNumber ?? 1, ps));
        }

        // GET: /DocNumberConfig/Create
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            ViewBag.IsEdit = false;
            ViewBag.SelectedPlantIds = new HashSet<string>();
            await PreparePlantOptionsAsync();
            var model = new DocNumberConfig { IsActive = true, SeqLength = 5, Separator = "-" };
            if (IsAjax) return PartialView("_DocNumberConfigForm", model);
            return View("_DocNumberConfigForm", model);
        }

        // POST: /DocNumberConfig/Create
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind(
            nameof(DocNumberConfig.DocType),
            nameof(DocNumberConfig.DocTypeName), nameof(DocNumberConfig.Prefix),
            nameof(DocNumberConfig.DateFormat), nameof(DocNumberConfig.SeqLength), nameof(DocNumberConfig.Separator),
            nameof(DocNumberConfig.ResetCycle), nameof(DocNumberConfig.IsActive))] DocNumberConfig model,
            string[]? plantIds = null)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            // 表單勾選來源：未勾任何廠別 = 建立「預設規則」（ScopeKey=""）；勾多個廠別＝多廠共用同一
            // 規則，正規化排序後存一筆。plantIds 為 null（如程式直接建構 model 呼叫，非經表單勾選）
            // 時保留 model.PlantId 原值，維持既有單一廠別建立方式相容。
            if (plantIds != null) model.PlantId = NormalizePlantIds(plantIds);
            else model.PlantId ??= "";
            // ScopeKey：不變的序號鍵。預設規則固定 ""；專屬規則指派 GUID，與可編輯的 PlantId 解耦，
            // 使日後增減適用廠別時序號 scope 不變、不重發舊號。
            model.ScopeKey = model.PlantIdList.Count == 0 ? "" : Guid.NewGuid().ToString("N");
            if (!ModelState.IsValid) return await CreateFormAsync(model, plantIds);

            if (model.PlantIdList.Count == 0)
            {
                // 每個 DocType 僅允許一筆預設規則
                var dupDefault = await _context.DocNumberConfigs.AnyAsync(c =>
                    c.DocType == model.DocType && c.ScopeKey == "");
                if (dupDefault)
                {
                    ModelState.AddModelError(nameof(model.DocType), _localizer["DuplicateCode"].Value);
                    return await CreateFormAsync(model, plantIds);
                }
            }
            else
            {
                var overlap = await FindOverlappingPlantAsync(model.DocType, model.PlantIdList, excludeSelf: null);
                if (overlap != null)
                {
                    ModelState.AddModelError(nameof(model.PlantId),
                        string.Format(_localizer["DocNumberPlantOverlap"].Value, overlap));
                    return await CreateFormAsync(model, plantIds);
                }
            }

            if (!DocNumberService.IsReplaySafe(model.DateFormat, model.ResetCycle))
            {
                ModelState.AddModelError(nameof(model.ResetCycle), _localizer["DocNumberResetFinerThanDate"].Value);
                return await CreateFormAsync(model, plantIds);
            }

            model.Separator ??= "";
            if (await HasStemConflictAsync(model))
            {
                ModelState.AddModelError(nameof(model.Prefix), _localizer["DocNumberStemDuplicate"].Value);
                return await CreateFormAsync(model, plantIds);
            }

            _context.DocNumberConfigs.Add(model);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["CreateFailedDuplicate"].Value });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["CreateSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // GET: /DocNumberConfig/Edit/{docType}?scopeKey=
        [HttpGet]
        public async Task<IActionResult> Edit(string docType, string scopeKey = "")
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var rec = await _context.DocNumberConfigs.FindAsync(docType, scopeKey ?? "");
            if (rec == null) return NotFound();

            ViewBag.IsEdit = true;
            ViewBag.PlantNames = await PlantNamesAsync();
            ViewBag.SelectedPlantIds = new HashSet<string>(rec.PlantIdList);
            await PreparePlantOptionsAsync();
            if (IsAjax) return PartialView("_DocNumberConfigForm", rec);
            return View("_DocNumberConfigForm", rec);
        }

        // POST: /DocNumberConfig/Edit/{docType}
        // 目標規則由表單隱藏欄位 updated.ScopeKey 定位（ScopeKey 為不變鍵）；適用廠別（PlantId）
        // 由勾選的 plantIds 決定，可增減。ScopeKey 不變故序號 scope 不變、增減廠別不重發舊號。
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string docType, DocNumberConfig updated, string[]? plantIds = null)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            updated.ScopeKey ??= "";

            var rec = await _context.DocNumberConfigs.FindAsync(docType, updated.ScopeKey);
            if (rec == null) return NotFound();

            // 適用廠別可編輯：預設規則（ScopeKey=""）永遠維持全廠、忽略勾選；專屬規則可增減廠別，
            // 但不得清空（清空＝改回預設，語意不清，應改用刪除）。plantIds 為 null（程式直接建構
            // 呼叫，非經表單）時沿用原 PlantId，維持既有測試/呼叫相容。
            bool isDefaultRule = string.IsNullOrEmpty(rec.ScopeKey);
            string newPlantId = isDefaultRule
                ? ""
                : (plantIds != null ? NormalizePlantIds(plantIds) : rec.PlantId);
            updated.PlantId = newPlantId;   // 供驗證失敗重繪與指紋/重疊比對使用

            if (!ModelState.IsValid) return await EditFormAsync(updated);

            if (!isDefaultRule && string.IsNullOrEmpty(newPlantId))
            {
                ModelState.AddModelError(nameof(updated.PlantId), _localizer["DocNumberPlantEmptyForbidden"].Value);
                return await EditFormAsync(updated);
            }

            if (!isDefaultRule)
            {
                var overlap = await FindOverlappingPlantAsync(docType, updated.PlantIdList, excludeSelf: rec.ScopeKey);
                if (overlap != null)
                {
                    ModelState.AddModelError(nameof(updated.PlantId),
                        string.Format(_localizer["DocNumberPlantOverlap"].Value, overlap));
                    return await EditFormAsync(updated);
                }
            }

            // 已編過號的規則：指紋欄位（Prefix／分隔符／日期格式）鎖定不可改。這些字元已印在既有單據上，
            // 改掉會「釋放」該指紋供他筆規則重用、序號從 1 重起而重發舊號撞 PK（HasStemConflict 只看
            // 當下其他規則、看不到歷史已印出的指紋，故此處以「該 scope 是否已編過號」為準額外守門）。
            bool stemChanged =
                rec.Prefix != updated.Prefix
                || (rec.Separator ?? "") != (updated.Separator ?? "")
                || rec.DateFormat != updated.DateFormat;
            if (stemChanged && await _context.DocNumberSequences
                    .AnyAsync(s => s.DocType == docType && s.PlantId == DocNumberService.ScopeOf(rec)))
            {
                ModelState.AddModelError(nameof(updated.Prefix), _localizer["DocNumberStemLockedInUse"].Value);
                return await EditFormAsync(updated);
            }

            if (!DocNumberService.IsReplaySafe(updated.DateFormat, updated.ResetCycle))
            {
                ModelState.AddModelError(nameof(updated.ResetCycle), _localizer["DocNumberResetFinerThanDate"].Value);
                return await EditFormAsync(updated);
            }

            updated.Separator ??= "";
            if (await HasStemConflictAsync(updated))
            {
                ModelState.AddModelError(nameof(updated.Prefix), _localizer["DocNumberStemDuplicate"].Value);
                return await EditFormAsync(updated);
            }

            var resetCycleChanged = rec.ResetCycle != updated.ResetCycle;

            rec.DocTypeName = updated.DocTypeName;
            rec.Prefix      = updated.Prefix;
            rec.DateFormat  = updated.DateFormat;
            rec.SeqLength   = updated.SeqLength;
            rec.Separator   = updated.Separator;
            rec.ResetCycle  = updated.ResetCycle;
            rec.IsActive    = updated.IsActive;
            rec.PlantId     = newPlantId;   // 適用廠別可變；ScopeKey（序號 scope）不變故序號延續

            try
            {
                // 週期變更會換序號期別鍵、從 1 重新起跳，但單號日期段未必跟著變
                // → 會重發已用過的號碼（實際案例：StockAdjustment 年改月後撞 PK）。
                // 校準與設定存檔包在同一交易：校準失敗時設定一併回滾，不留下危險狀態。
                await using var tx = await _context.Database.BeginTransactionAsync();
                await _context.SaveChangesAsync();
                if (resetCycleChanged)
                {
                    await _docNumberSvc.RealignSequencesAsync(docType, DocNumberService.ScopeOf(rec), updated.ResetCycle);
                    Logger.LogInformation(
                        "單號設定 {DocType}（scope '{Scope}'）重置週期變更，已校準序號至歷史最大值以避免重發單號",
                        docType, DocNumberService.ScopeOf(rec));
                }
                await tx.CommitAsync();
            }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["SaveFailed"].Value });
                return StatusCode(500);
            }

            if (IsAjax) return Json(new { success = true, message = _localizer["UpdateSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // POST: /DocNumberConfig/Delete
        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string docType, string scopeKey = "")
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var rec = await _context.DocNumberConfigs.FindAsync(docType, scopeKey ?? "");
            if (rec != null)
            {
                if (rec.IsSystemType)
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["SystemRecordCannotDelete"].Value });
                    return StatusCode(409);
                }

                // 已編過號（該 scope 的 DocNumberSequences 有紀錄）表示此規則已被實際使用：
                // 刪除後該廠回退預設規則、序號 scope 易主，若指紋相近會重發舊號，故擋下。
                if (await _context.DocNumberSequences.AnyAsync(s => s.DocType == docType && s.PlantId == DocNumberService.ScopeOf(rec)))
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["DocTypeInUseCannotDelete"].Value });
                    return StatusCode(409);
                }

                _context.DocNumberConfigs.Remove(rec);
                try { await _context.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["DeleteFailedReferenced"].Value });
                    return StatusCode(500);
                }
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // GET: /DocNumberConfig/Preview?docType=GoodsReceipt&plantId=
        [HttpGet]
        public async Task<IActionResult> Preview(string docType, string plantId = "")
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var config = await _docNumberSvc.ResolveForPreviewAsync(docType, plantId ?? "");
            if (config == null) return NotFound();

            var preview = _docNumberSvc.Preview(config, plantId ?? "", sampleSeq: 1);
            return Json(new { preview });
        }

        // ── helpers ─────────────────────────────────────────────────────────

        /// <summary>
        /// 同一 DocType 之下，其他設定（不含自身 scope）的編號規則指紋不得與本設定相同：
        /// 不同 scope 的序號各自起算，指紋相同必然產出相同單號字串（撞單據表 PK）。
        /// </summary>
        private async Task<bool> HasStemConflictAsync(DocNumberConfig model)
        {
            // 排除自身以 ScopeKey（不變鍵）比對：PlantId 可多值且可變，不能當唯一識別
            var siblings = await _context.DocNumberConfigs.AsNoTracking()
                .Where(c => c.DocType == model.DocType && c.ScopeKey != model.ScopeKey)
                .ToListAsync();
            return siblings.Any(s => DocNumberService.HasSameStem(s, model));
        }

        /// <summary>勾選的廠別代碼正規化為排序過的逗號分隔清單；未勾任何廠別＝空字串（預設規則）。</summary>
        private static string NormalizePlantIds(string[]? plantIds) =>
            plantIds == null || plantIds.Length == 0
                ? ""
                : string.Join(",", plantIds.Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct().OrderBy(p => p, StringComparer.Ordinal));

        /// <summary>
        /// 檢查勾選的廠別是否已被同 DocType 下其他設定（非預設規則）涵蓋——
        /// 一廠只能對應一筆專屬規則，否則 <see cref="DocNumberService.Applies"/> 比對會產生歧義。
        /// </summary>
        /// <param name="excludeSelf">編輯時排除自身這一筆的 ScopeKey（新增時傳 null 不排除任何列）</param>
        private async Task<string?> FindOverlappingPlantAsync(string docType, IReadOnlyList<string> plantIds, string? excludeSelf)
        {
            var siblings = await _context.DocNumberConfigs.AsNoTracking()
                .Where(c => c.DocType == docType && c.PlantId != "" && c.ScopeKey != excludeSelf)
                .ToListAsync();
            foreach (var sibling in siblings)
                foreach (var p in sibling.PlantIdList)
                    if (plantIds.Contains(p)) return p;
            return null;
        }

        private async Task<Dictionary<string, string>> PlantNamesAsync() =>
            (await _context.Plants.AsNoTracking().Select(p => new { p.Id, p.Name, p.NameEn }).ToListAsync())
                .ToDictionary(p => p.Id, p => SmartFactory.MES.Modules._Shared.PlantDisplay.Name(p.Name, p.NameEn, p.Id));

        private async Task PreparePlantOptionsAsync()
        {
            ViewBag.PlantOptions = (await _context.Plants.AsNoTracking()
                .OrderBy(p => p.Id)
                .Select(p => new { p.Id, p.Name, p.NameEn })
                .ToListAsync())
                .Select(p => new { p.Id, Name = SmartFactory.MES.Modules._Shared.PlantDisplay.Name(p.Name, p.NameEn, p.Id) })
                .ToList();
        }

        private async Task<IActionResult> CreateFormAsync(DocNumberConfig model, string[]? selectedPlantIds = null)
        {
            ViewBag.IsEdit = false;
            ViewBag.SelectedPlantIds = new HashSet<string>(selectedPlantIds ?? model.PlantIdList);
            await PreparePlantOptionsAsync();
            if (IsAjax) return PartialView("_DocNumberConfigForm", model);
            return View("_DocNumberConfigForm", model);
        }

        private async Task<IActionResult> EditFormAsync(DocNumberConfig model)
        {
            ViewBag.IsEdit = true;
            ViewBag.PlantNames = await PlantNamesAsync();
            ViewBag.SelectedPlantIds = new HashSet<string>(model.PlantIdList);
            await PreparePlantOptionsAsync();
            if (IsAjax) return PartialView("_DocNumberConfigForm", model);
            return View("_DocNumberConfigForm", model);
        }
    }
}
