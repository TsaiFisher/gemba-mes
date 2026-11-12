using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.MFG;
using SmartFactory.MES.Modules._Shared.Printing;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    /// <summary>
    /// 發退料單（MaterialSlip）— 統一七類倉庫作業畫面（mfg-domain.md §17.3）。
    /// 狀態機：Draft [→Pending（MR 限）] → Approved → Picking → Confirmed → Posted。
    /// 過帳動作呼叫 IInventoryPostingService 建立 InventoryMovement，並回寫 WorkOrderMaterial.IssuedQty/ReturnedQty。
    /// </summary>
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class MaterialSlipController : ModuleControllerBase
    {
        private const string MenuKey = "MaterialSlip";
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly IDateTimeService _dt;
        private readonly IDocNumberService _docNumber;
        private readonly IInventoryPostingService _postingService;
        private readonly IInventoryReservationService _reservations;

        // 過帳／建單的單一實作（手機也走它）。由本控制器既有的相依組出來而不是另外注入：
        // 這樣既有測試與流程驅動（MaterialSlipDriver）的建構式完全不必改，行為等同 DI 那一份。
        private MaterialSlipService? _slips;
        private MaterialSlipService Slips => _slips ??= new MaterialSlipService(
            Db, _postingService, _reservations, _docNumber, _dt, UserContext, _localizer);

        public MaterialSlipController(
            IStringLocalizer<ProjectZSharedResource> localizer,
            IDocNumberService docNumber,
            IInventoryPostingService postingService,
            IInventoryReservationService reservations,
            IDateTimeService dt)
        {
            _dt             = dt;
            _localizer      = localizer;
            _docNumber      = docNumber;
            _postingService = postingService;
            _reservations   = reservations;
        }

        public async Task<IActionResult> Index(
            int? pageNumber = 1,
            string sortOrder = "date_desc",
            string searchText = "",
            string categoryFilter = "InternalIssue",
            string statusFilter = "",
            bool searched = false,
            int pageSize = 20)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            ViewBag.CurrentSort     = sortOrder;
            ViewBag.SearchText      = searchText;
            ViewBag.CategoryFilter  = categoryFilter;
            ViewBag.StatusFilter    = statusFilter;
            ViewBag.Searched        = searched;
            ViewBag.CurrentPageSize = pageSize;

            await PrepareIndexPermissionsAsync(MenuKey);
            if (!searched) return View((PaginatedList<MaterialSlip>?)null);

            // 列表要顯示明細筆數（Index.cshtml:139 的 @s.Lines.Count），
            // 不 Include 的話延遲載入是關的、集合恆空，那一欄永遠印 0 而且不會報錯。
            var q = Db.MaterialSlips.AsNoTracking().Include(s => s.Lines).AsQueryable();

            if (Enum.TryParse<MaterialSlipCategory>(categoryFilter, out var cat))
                q = q.Where(s => s.Category == cat);

            if (!string.IsNullOrWhiteSpace(searchText))
                q = q.Where(s => s.SlipId.Contains(searchText) || s.SlipNo.Contains(searchText)
                    || (s.SourceDocId != null && s.SourceDocId.Contains(searchText)));

            if (!string.IsNullOrWhiteSpace(statusFilter) && Enum.TryParse<MaterialSlipStatus>(statusFilter, out var st))
                q = q.Where(s => s.Status == st);

            q = sortOrder switch
            {
                "slip_id"   => q.OrderBy(s => s.SlipId),
                "date_asc"  => q.OrderBy(s => s.SlipDate),
                _           => q.OrderByDescending(s => s.SlipDate).ThenByDescending(s => s.SlipId)
            };

            return View(await PaginatedList<MaterialSlip>.CreateAsync(q, pageNumber ?? 1, pageSize));
        }

        public async Task<IActionResult> Form(string? id, string? category = "InternalIssue")
        {
            MaterialSlip model;
            if (!string.IsNullOrEmpty(id))
            {
                if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
                var slip = await Db.MaterialSlips.Include(s => s.Lines).FirstOrDefaultAsync(s => s.SlipId == id);
                if (slip == null) return IsAjax ? JsonFail(_localizer["RecordNotFound"].Value) : NotFound();
                model = slip;
            }
            else
            {
                if (!await Permissions.CanCreate(MenuKey)) return Forbid();
                var cat = Enum.TryParse<MaterialSlipCategory>(category, out var c) ? c : MaterialSlipCategory.InternalIssue;
                model = new MaterialSlip
                {
                    Category = cat,
                    Status   = MaterialSlipStatus.Draft,
                    SlipDate = _dt.GetLocalDate()
                };
            }

            if (!IsAjax)
                // 這張表單只在面板裡是完整的：核准／揀料／過帳那幾個動作的送出要靠面板腳本
                // 攔成 AJAX 才會有 toast 與錯誤訊息（見 crud-panel.js 的 confirmSubmitPanel）。
                // 直接開網址時面板腳本沒載入，同樣的按鈕會原生送出、把使用者丟到一頁原始 JSON
                // ——單據狀態已經變了，畫面上卻什麼都沒說（W2-F3）。導回列表，那裡有入口。
                return RedirectToAction(nameof(Index), new { searched = true, searchText = id ?? "" });

            await PrepareDropdowns(model);
            return PartialView("_Form", model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(MaterialSlip model)
        {
            // SlipNo（表頭採番）與各明細 SlipId 都由伺服器端於存檔時產生，Create 面板不會、
            // 也不該帶這些值；但實體上它們是 [Required]，直接綁 entity 會讓「新單」的 ModelState
            // 恆為 invalid，使「新增備料/領料單」面板存檔靜默失敗、永遠建不出單。下方存檔邏輯
            // 會賦上正確值，這裡先移除這些伺服器產生欄位的驗證。（編輯時它們由隱藏欄位帶入。）
            ModelState.Remove(nameof(MaterialSlip.SlipNo));
            // 來源類別／單號是自由輸入：頭尾空白會讓工單比對整個落空（2026-10-11 實測）
            model.SourceDocType = model.SourceDocType?.Trim();
            model.SourceDocId   = model.SourceDocId?.Trim();
            for (int i = 0; i < (model.Lines?.Count ?? 0); i++)
                ModelState.Remove($"{nameof(MaterialSlip.Lines)}[{i}].{nameof(MaterialSlipLine.SlipId)}");

            if (!ModelState.IsValid)
            {
                await PrepareDropdowns(model);
                return IsAjax ? PartialView("_Form", model) : View("_Form", model);
            }

            var plantId = UserContext.PlantId ?? string.Empty;
            var today   = _dt.GetLocalDate();
            var docType = MaterialSlipService.CategoryToDocType(model.Category);

            // 工單領料／退料的數量守門與手機共用（2026-10-10：桌機也要擋超領、超退與委外工序的料）。
            // 編輯時排除本單自己，否則它的原數量會被當成「已申請未過帳」重複扣。
            if (model.SourceDocType == "WorkOrder" && !string.IsNullOrEmpty(model.SourceDocId)
                && await Slips.ValidateWorkOrderSlipQtyAsync(model.Category, model.SourceDocId,
                       (model.Lines ?? Enumerable.Empty<MaterialSlipLine>()).Select(l => new SlipRequestLine(l.ProductCode, l.RequestedQty)),
                       string.IsNullOrEmpty(model.SlipId) ? null : model.SlipId) is { } qtyError)
            {
                ModelState.AddModelError("", qtyError);
                await PrepareDropdowns(model);
                return IsAjax ? PartialView("_Form", model) : View("_Form", model);
            }

            // 已備料的料要先從備料區扣（2026-10-13）：離開草稿後明細儲位就不能改，等到過帳才擋會讓單子卡住
            if (await Slips.ValidateIssueFromStagingFirstAsync(model, draft: true) is { } stagingError)
            {
                ModelState.AddModelError("", stagingError);
                await PrepareDropdowns(model);
                return IsAjax ? PartialView("_Form", model) : View("_Form", model);
            }

            // 明細單位必須是品號的庫存單位（2026-10-12）：工單用料、領退額度、手機揀料與過帳全以庫存單位計，
            // 過帳也直接把明細單位當庫存單位——選了別的單位（箱）就是把箱數當個數入帳。沒選的補上庫存單位。
            if (await CheckLinesStockUomAsync(model.Lines) is { } uomError)
            {
                ModelState.AddModelError("", uomError);
                await PrepareDropdowns(model);
                return IsAjax ? PartialView("_Form", model) : View("_Form", model);
            }

            if (string.IsNullOrEmpty(model.SlipId))
            {
                if (!await Permissions.CanCreate(MenuKey))
                {
                    ModelState.AddModelError("", _localizer["NoCreatePermission"].Value);
                    await PrepareDropdowns(model);
                    return IsAjax ? PartialView("_Form", model) : View("_Form", model);
                }

                // Push/重工發料需來源工單已核准（mfg-domain.md §17.3）
                bool isIssueCategory = model.Category is MaterialSlipCategory.InternalIssue
                    or MaterialSlipCategory.OutsourceIssue
                    or MaterialSlipCategory.ReworkIssue;
                if (isIssueCategory && model.SourceDocType == "WorkOrder"
                    && !string.IsNullOrEmpty(model.SourceDocId))
                {
                    var srcWo = await Db.WorkOrders.AsNoTracking()
                        .Select(w => new { w.WorkOrderId, w.ApprovalStatus })
                        .FirstOrDefaultAsync(w => w.WorkOrderId == model.SourceDocId);
                    if (srcWo == null)
                    {
                        ModelState.AddModelError("", _localizer["RecordNotFound"].Value);
                        await PrepareDropdowns(model);
                        return IsAjax ? PartialView("_Form", model) : View("_Form", model);
                    }
                    if (srcWo.ApprovalStatus != DocumentApprovalStatus.Approved)
                    {
                        ModelState.AddModelError("", _localizer["WorkOrderNotApproved"].Value);
                        await PrepareDropdowns(model);
                        return IsAjax ? PartialView("_Form", model) : View("_Form", model);
                    }
                }

                model.SlipId  = await _docNumber.NextAsync(docType, plantId, today);
                model.SlipNo  = model.SlipId;
                model.PlantId = plantId;
                model.Status  = MaterialSlipStatus.Draft;
                model.CreatedAt = DateTime.UtcNow;
                model.UpdatedAt = DateTime.UtcNow;
                model.RequestedBy = UserContext.UserId ?? string.Empty;
                model.RequestedAt = DateTime.UtcNow;

                for (int i = 0; i < model.Lines.Count; i++)
                {
                    model.Lines[i].SlipId = model.SlipId;
                    model.Lines[i].LineNo = i + 1;
                }
                await Slips.RelinkWorkOrderMaterialLinesAsync(model, new());

                Db.MaterialSlips.Add(model);
            }
            else
            {
                if (!await Permissions.CanUpdate(MenuKey))
                {
                    ModelState.AddModelError("", _localizer["NoUpdatePermission"].Value);
                    await PrepareDropdowns(model);
                    return IsAjax ? PartialView("_Form", model) : View("_Form", model);
                }

                var existing = await Db.MaterialSlips.Include(s => s.Lines)
                    .FirstOrDefaultAsync(s => s.SlipId == model.SlipId);
                if (existing == null)
                {
                    ModelState.AddModelError("", _localizer["RecordNotFound"].Value);
                    await PrepareDropdowns(model);
                    return IsAjax ? PartialView("_Form", model) : View("_Form", model);
                }
                if (existing.Status != MaterialSlipStatus.Draft)
                {
                    ModelState.AddModelError("", _localizer["SlipNotDraft"].Value);
                    await PrepareDropdowns(model);
                    return IsAjax ? PartialView("_Form", model) : View("_Form", model);
                }

                existing.SlipDate           = model.SlipDate;
                existing.SourceDocType      = model.SourceDocType;
                existing.SourceDocId        = model.SourceDocId;
                existing.TargetSupplierCode = model.TargetSupplierCode;
                existing.Remarks            = model.Remarks;
                existing.UpdatedAt          = DateTime.UtcNow;

                var before = existing.Lines.ToDictionary(
                    l => l.LineNo, l => (l.ProductCode, l.SourceLineId));

                Db.MaterialSlipLines.RemoveRange(existing.Lines);
                existing.Lines.Clear();
                for (int i = 0; i < model.Lines.Count; i++)
                {
                    model.Lines[i].SlipId = existing.SlipId;
                    model.Lines[i].LineNo = i + 1;
                    existing.Lines.Add(model.Lines[i]);
                }
                await Slips.RelinkWorkOrderMaterialLinesAsync(existing, before);
            }

            await Db.SaveChangesAsync();
            return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
        }

        /// <summary>送審（InternalRequisition）：Draft → Pending</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(string id)
        {
            if (!await Permissions.CanCreate(MenuKey)) return JsonFail(_localizer["NoCreatePermission"].Value);
            var slip = await Db.MaterialSlips.FirstOrDefaultAsync(s => s.SlipId == id);
            if (slip == null) return JsonFail(_localizer["RecordNotFound"].Value);
            if (slip.Category != MaterialSlipCategory.InternalRequisition)
                return JsonFail(_localizer["SlipCategoryNotApplicable"].Value);
            if (slip.Status != MaterialSlipStatus.Draft) return JsonFail(_localizer["SlipNotDraft"].Value);

            slip.Status    = MaterialSlipStatus.Pending;
            slip.UpdatedAt = DateTime.UtcNow;
            await Db.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["SlipSubmitted"].Value });
        }

        /// <summary>核准：Pending→Approved（InternalRequisition）或 Draft→Approved（其他類別）</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Approve(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return JsonFail(_localizer["NoUpdatePermission"].Value);
            var slip = await Db.MaterialSlips.FirstOrDefaultAsync(s => s.SlipId == id);
            if (slip == null) return JsonFail(_localizer["RecordNotFound"].Value);

            bool isMr = slip.Category == MaterialSlipCategory.InternalRequisition;
            var expected = isMr ? MaterialSlipStatus.Pending : MaterialSlipStatus.Draft;
            if (slip.Status != expected)
                return JsonFail(isMr ? _localizer["SlipNotPending"].Value : _localizer["SlipNotDraft"].Value);

            slip.Status     = MaterialSlipStatus.Approved;
            slip.ApprovedBy = UserContext.UserId ?? string.Empty;
            slip.ApprovedAt = DateTime.UtcNow;
            slip.UpdatedAt  = DateTime.UtcNow;
            await Db.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["SlipApproved"].Value });
        }

        /// <summary>開始揀料：Approved → Picking</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkPicking(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return JsonFail(_localizer["NoUpdatePermission"].Value);
            var slip = await Db.MaterialSlips.FirstOrDefaultAsync(s => s.SlipId == id);
            if (slip == null) return JsonFail(_localizer["RecordNotFound"].Value);
            if (slip.Status != MaterialSlipStatus.Approved) return JsonFail(_localizer["SlipNotApproved"].Value);

            slip.Status    = MaterialSlipStatus.Picking;
            slip.UpdatedAt = DateTime.UtcNow;
            await Db.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["SlipPickingStarted"].Value });
        }

        /// <summary>倉庫確認揀料完成：Picking → Confirmed，ConfirmedQty = ApprovedQty ?? RequestedQty</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Confirm(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return JsonFail(_localizer["NoUpdatePermission"].Value);
            var slip = await Db.MaterialSlips.Include(s => s.Lines).FirstOrDefaultAsync(s => s.SlipId == id);
            if (slip == null) return JsonFail(_localizer["RecordNotFound"].Value);
            if (slip.Status != MaterialSlipStatus.Picking) return JsonFail(_localizer["SlipNotPicking"].Value);

            foreach (var ln in slip.Lines)
                ln.ConfirmedQty = ln.ApprovedQty ?? ln.RequestedQty;

            slip.Status    = MaterialSlipStatus.Confirmed;
            slip.IssuedAt  = DateTime.UtcNow;
            slip.UpdatedAt = DateTime.UtcNow;
            await Db.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["SlipConfirmed"].Value });
        }

        /// <summary>過帳發料：Confirmed → Posted（建立 InventoryMovement + 回寫 WorkOrderMaterial.IssuedQty/ReturnedQty）</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Issue(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return JsonFail(_localizer["NoUpdatePermission"].Value);
            try
            {
                await Slips.PostAsync(id);
            }
            catch (InvalidOperationException ex) { return JsonFail(ex.Message); }
            catch (DbUpdateConcurrencyException) { return JsonFail(_localizer["ConcurrencyConflict"].Value); }

            return Json(new { success = true, message = _localizer["SlipIssued"].Value });
        }

        /// <summary>
        /// 取消：Draft/Pending/Approved/Picking/Confirmed → Cancelled。
        /// 已確認（揀料完成、尚未過帳）原本不能取消，工單要作廢只能先過帳再開退料單，
        /// 平白多兩筆庫存異動（2026-10-20 A6）。確認只改狀態、沒有庫存異動，取消安全；
        /// 揀出的料由倉庫放回原儲位。已過帳／已結案才擋。
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Cancel(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return JsonFail(_localizer["NoUpdatePermission"].Value);
            var slip = await Db.MaterialSlips.FirstOrDefaultAsync(s => s.SlipId == id);
            if (slip == null) return JsonFail(_localizer["RecordNotFound"].Value);
            if (slip.Status is MaterialSlipStatus.Posted or MaterialSlipStatus.Closed or MaterialSlipStatus.Cancelled)
                return JsonFail(_localizer["SlipAlreadyIssued"].Value);

            slip.Status    = MaterialSlipStatus.Cancelled;
            slip.UpdatedAt = DateTime.UtcNow;
            await Db.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["SlipCancelled"].Value });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            if (!await Permissions.CanDelete(MenuKey)) return JsonFail(_localizer["NoDeletePermission"].Value);
            var slip = await Db.MaterialSlips.Include(s => s.Lines).FirstOrDefaultAsync(s => s.SlipId == id);
            if (slip == null) return JsonFail(_localizer["RecordNotFound"].Value);
            if (slip.Status != MaterialSlipStatus.Draft && slip.Status != MaterialSlipStatus.Cancelled)
                return JsonFail(_localizer["SlipCannotDelete"].Value);

            Db.MaterialSlips.Remove(slip);
            await Db.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
        }

        // ── helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// 來源單號下拉（依來源類別）：只列還會發／退料的單——工單排除未釋出、已入庫結案與作廢；
        /// 發料類（發／領／委外發／重工發）連「完工」也排除，退料類保留（完工後退回超領是常態）。
        /// 委外採購單只列已確認。目前選的那一張就算已結案也帶回，編輯舊單時下拉才不會變空。
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> SourceDocs(string type, string? category, string? current)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();
            var isReturn = Enum.TryParse<MaterialSlipCategory>(category, out var cat) && MaterialSlipFlow.IsReturn(cat);
            return Json(await SourceDocOptionsAsync(type, isReturn, current));
        }

        private async Task<List<SourceDocOption>> SourceDocOptionsAsync(string? type, bool isReturn, string? current)
        {
            if (type == SourceTypeOpo)
            {
                return await Db.OutsourcePurchaseOrders.AsNoTracking()
                    .Where(o => o.Status == OutsourcePoStatus.Confirmed || o.OutsourcePurchaseOrderId == current)
                    .OrderByDescending(o => o.OutsourcePurchaseOrderId).Take(500)
                    .Select(o => new SourceDocOption(o.OutsourcePurchaseOrderId,
                        o.OutsourcePurchaseOrderId + " — " + o.SupplierCode))
                    .ToListAsync();
            }

            var open = isReturn
                ? new[] { WorkOrderStatus.Released, WorkOrderStatus.InProgress, WorkOrderStatus.Completed }
                : new[] { WorkOrderStatus.Released, WorkOrderStatus.InProgress };
            return await Db.WorkOrders.AsNoTracking()
                .Where(w => open.Contains(w.Status) || w.WorkOrderId == current)
                .OrderByDescending(w => w.WorkOrderId).Take(500)
                .Select(w => new SourceDocOption(w.WorkOrderId, w.WorkOrderId + " — " + w.ProductCode))
                .ToListAsync();
        }

        internal sealed record SourceDocOption(string Id, string Text);

        private const string SourceTypeWo  = "WorkOrder";
        private const string SourceTypeOpo = "OutsourcePurchaseOrder";

        private async Task<string?> CheckLinesStockUomAsync(IList<MaterialSlipLine>? lines)
        {
            if (lines == null || lines.Count == 0) return null;
            var codes = lines.Select(l => l.ProductCode).Distinct().ToList();
            var stock = await Db.Set<Product>().AsNoTracking()
                .Where(p => codes.Contains(p.ProductCode))
                .ToDictionaryAsync(p => p.ProductCode, p => p.StockUom);
            foreach (var l in lines)
            {
                var su = stock.GetValueOrDefault(l.ProductCode);
                if (string.IsNullOrWhiteSpace(su)) continue;          // 品號沒設庫存單位：維持原行為
                if (string.IsNullOrWhiteSpace(l.UomCode)) { l.UomCode = su!; continue; }
                if (!string.Equals(l.UomCode, su, StringComparison.OrdinalIgnoreCase))
                    return string.Format(_localizer["SlipUomMustBeStock"].Value, l.ProductCode, l.UomCode, su);
            }
            return null;
        }

        private async Task PrepareDropdowns(MaterialSlip model)
        {
            ViewBag.SourceDocOptions = await SourceDocOptionsAsync(
                string.IsNullOrEmpty(model.SourceDocType) ? SourceTypeWo : model.SourceDocType,
                MaterialSlipFlow.IsReturn(model.Category), model.SourceDocId);

            ViewBag.ProductOptions = await Db.Set<Product>().AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName })
                .ToListAsync();

            ViewBag.WarehouseOptions = await Db.Set<Warehouse>().AsNoTracking()
                .Where(w => w.IsActive && w.PlantId == UserContext.PlantId)
                .OrderBy(w => w.WhCode)
                .Select(w => new { w.WhCode, w.WhName })
                .ToListAsync();

            ViewBag.LocationOptions = await Db.Set<StorageLocation>().ForCurrentPlant(Db).AsNoTracking()
                .OrderBy(l => l.WhCode).ThenBy(l => l.LocationCode)
                .Select(l => new { l.WhCode, l.LocationCode, l.LocationName })
                .ToListAsync();

            var uomRows = await Db.Set<Uom>().AsNoTracking()
                .Where(u => u.IsActive)
                .OrderBy(u => u.UomCode)
                .Select(u => new { u.UomCode, u.UomName, u.DecimalPlaces })
                .ToListAsync();
            ViewBag.UomOptions = uomRows.Select(u => new { u.UomCode, u.UomName }).ToList();
            // 數量要照單位的小數位數顯示。沒有這份對照時畫面是直接印 decimal(18,6)，
            // 於是以「箱」（小數 0 位）計的品項會顯示成 12.000000。
            ViewBag.UomDecimals = (IReadOnlyDictionary<string, int>)uomRows
                .ToDictionary(u => u.UomCode, u => u.DecimalPlaces);

            ViewBag.SupplierOptions = await Db.Set<Company>().AsNoTracking()
                .Where(c => c.IsSupplier && c.IsActive)
                .OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName })
                .ToListAsync();
        }

        private IActionResult JsonFail(string message)
            => Json(new { success = false, message });

        // GET: /MaterialSlip/Print/{id}（2026-10-27：發退料單紙本，倉庫備料／簽收用）
        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await Permissions.CanPrint(MenuKey)) return Forbid();
            var doc = await Db.MaterialSlips.AsNoTracking().Include(s => s.Lines)
                .FirstOrDefaultAsync(s => s.SlipId == id);
            if (doc == null) return NotFound();
            await PreparePrintViewBagAsync(Url.Action("Form", null, new { id }, Request.Scheme)!,
                new[] { doc.RequestedBy, doc.ApprovedBy, doc.IssuedBy });
            await PreparePrintSignAsync(id, doc.RequestedBy);
            var n = await PrintNames.ForAsync(doc);
            var f = await PrintFormatsAsync();
            var L = PrintL;
            var t = ScreenText("MFG", "MaterialSlip");
            var p = new PrintDocument
            {
                Title = L["Print_Title_MaterialSlip"], DocNo = doc.SlipNo,
                DateLabel = L["Print_DocDate"], DateValue = doc.SlipDate.ToString("yyyy-MM-dd"),
            }
                .Field(L["Print_Category"], t["Cat" + doc.Category])
                .Field(L["Print_Status"], MfgEnumDisplay.Text(L, doc.Status))
                .Field(L["Print_SourceDoc"], $"{doc.SourceDocType} {doc.SourceDocId}".Trim())
                .Field(L["Print_Supplier"], n.CompanyWith(doc.TargetSupplierCode))
                .Field(L["Print_RequestedBy"], PrintUser(doc.RequestedBy))
                .Field(L["Print_ApprovedBy"], PrintUser(doc.ApprovedBy))
                .Field(L["Print_IssuedBy"], PrintUser(doc.IssuedBy))
                .Field(L["Print_IssuedAt"], PrintTime(doc.IssuedAt))
                .Field(L["Print_Remarks"], doc.Remarks);
            var tbl = new PrintTable()
                .Col(L["Print_LineNo"]).Col(L["Print_ProductCode"]).Col(L["Print_ProductName"])
                .Col(L["Print_RequestedQty"], true).Col(L["Print_ApprovedQty"], true).Col(L["Print_ConfirmedQty"], true)
                .Col(L["Print_Uom"]).Col(L["Print_From"]).Col(L["Print_To"]).Col(L["Print_LotNo"]);
            foreach (var l in doc.Lines.OrderBy(x => x.LineNo))
                tbl.Row(l.LineNo.ToString(), l.ProductCode, n.Product(l.ProductCode),
                    f.Qty(l.RequestedQty, l.UomCode), f.Qty(l.ApprovedQty, l.UomCode), f.Qty(l.ConfirmedQty, l.UomCode),
                    n.UomWith(l.UomCode), n.PlaceWith(l.FromWarehouseCode, l.FromLocationCode),
                    n.PlaceWith(l.ToWarehouseCode, l.ToLocationCode), l.LotNo);
            p.Tables.Add(tbl);
            return PrintDocumentView(p);
        }
    }
}
