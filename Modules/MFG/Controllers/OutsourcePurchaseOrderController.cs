using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using System.Text.Json;
using SmartFactory.MES.Modules._Shared.Printing;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    /// <summary>
    /// 委外採購單 / 轉委外 / 拆單（mfg-domain.md §19）。
    /// Phase G：透過 OutsourceDemand 統一整品委外與工序委外兩條路徑；
    /// 支援同一委外建議拆單至不同廠商/交期（SplitQty &lt; DemandQty）；
    /// 建單時自動查詢 OutsourceQuotation 帶入委外單價。
    /// </summary>
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class OutsourcePurchaseOrderController : ModuleControllerBase
    {
        private const string MenuKey = "OutsourcePurchaseOrder";
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly IDocNumberService _docNumber;
        private readonly IUomService _uomService;
        private readonly IOutsourceQuotationResolver _quotationResolver;

        public OutsourcePurchaseOrderController(
            IStringLocalizer<ProjectZSharedResource> localizer, IDocNumberService docNumber,
            IUomService uomService, IOutsourceQuotationResolver quotationResolver)
        {
            _localizer = localizer;
            _docNumber = docNumber;
            _uomService = uomService;
            _quotationResolver = quotationResolver;
        }

        public async Task<IActionResult> Index(int? pageNumber = 1, string sortOrder = "id_desc",
            string searchText = "", string statusFilter = "", bool searched = false, int pageSize = 20)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            ViewBag.CurrentSort = sortOrder;
            ViewBag.SearchText = searchText;
            ViewBag.StatusFilter = statusFilter;
            ViewBag.Searched = searched;
            ViewBag.CurrentPageSize = pageSize;

            await PrepareIndexPermissionsAsync(MenuKey);
            if (!searched) return View((PaginatedList<OutsourcePurchaseOrder>?)null);

            IQueryable<OutsourcePurchaseOrder> query = Db.OutsourcePurchaseOrders.AsNoTracking().Include(o => o.Lines);

            if (!string.IsNullOrEmpty(searchText))
                query = query.Where(o => o.OutsourcePurchaseOrderId.Contains(searchText) || o.SupplierCode.Contains(searchText));

            if (!string.IsNullOrEmpty(statusFilter) && Enum.TryParse<OutsourcePoStatus>(statusFilter, out var st))
                query = query.Where(o => o.Status == st);

            query = sortOrder switch
            {
                "id"            => query.OrderBy(o => o.OutsourcePurchaseOrderId),
                "order_date"    => query.OrderBy(o => o.OrderDate),
                "order_date_desc" => query.OrderByDescending(o => o.OrderDate),
                _               => query.OrderByDescending(o => o.OutsourcePurchaseOrderId)
            };

            var list = await PaginatedList<OutsourcePurchaseOrder>.CreateAsync(query, pageNumber ?? 1, pageSize);
            ViewBag.SupplierNames = await SupplierNamesAsync(list.Select(o => o.SupplierCode));
            return View(list);
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id)) return Forbid();
            var po = await Db.OutsourcePurchaseOrders.AsNoTracking()
                .Include(o => o.Lines.OrderBy(l => l.LineNo))
                .FirstOrDefaultAsync(o => o.OutsourcePurchaseOrderId == id);
            if (po == null)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
                return NotFound();
            }

            // 送外料：以 (來源工單-工序) = 派工單號，對應 DispatchMaterial 衍生顯示
            var dispatchIds = po.Lines.Select(l => $"{l.SourceWorkOrderId}-{l.OperationSeq}").ToList();
            var mats = await Db.DispatchMaterials.AsNoTracking()
                .Where(m => dispatchIds.Contains(m.DispatchId))
                .Select(m => new { m.DispatchId, m.ProductCode, m.RequiredQty, m.UomCode })
                .ToListAsync();
            ViewBag.SendMaterials = mats.GroupBy(m => m.DispatchId)
                .ToDictionary(g => g.Key, g => g.Cast<object>().ToList());
            ViewBag.SupplierNames = await SupplierNamesAsync(new[] { po.SupplierCode });

            ViewBag.IsReadOnly = true;
            await PrepareIndexPermissionsAsync(MenuKey);
            return IsAjax ? PartialView("_Details", po) : View("_Details", po);
        }

        /// <summary>
        /// 轉委外清單：整合 OutsourceDemand，顯示未完全轉換的委外建議（DemandQty - TransferredQty > 0）。
        /// 相容舊路徑：若委外派工尚無 OutsourceDemand，保留顯示供手動轉換。
        /// </summary>
        public async Task<IActionResult> Worklist()
        {
            if (!await Permissions.CanCreate(MenuKey)) return Forbid();

            // Phase G：載入尚未完全轉換的 OutsourceDemand（用 TransferredQty < DemandQty 避開 [NotMapped] 屬性）
            var rawDemands = await Db.OutsourceDemands.AsNoTracking()
                .Where(d => d.PlantId == UserContext.PlantId && d.TransferredQty < d.DemandQty)
                .Join(Db.WorkOrders.Where(w => w.Status != WorkOrderStatus.Closed && w.Status != WorkOrderStatus.Void),
                      d => d.WorkOrderId, w => w.WorkOrderId,
                      (d, w) => new
                      {
                          d.DemandId, d.WorkOrderId, d.DemandQty, d.TransferredQty,
                          d.RequiredDate, d.DemandType, d.ProductCode
                      })
                .OrderBy(x => x.RequiredDate)
                .ToListAsync();

            // 相容路徑：無 OutsourceDemand 的舊委外派工
            var rawLegacy = await Db.Dispatches.AsNoTracking()
                .Where(d => d.IsOutsource
                    && !Db.OutsourceDemands.Any(od => od.WorkOrderId == d.WorkOrderId && od.DispatchId == d.DispatchId)
                    && !Db.OutsourcePurchaseOrderLines.Any(l => l.SourceWorkOrderId == d.WorkOrderId && l.OperationSeq == d.OperationSeq))
                .Join(Db.WorkOrders.Where(w => w.Status != WorkOrderStatus.Closed && w.Status != WorkOrderStatus.Void),
                      d => d.WorkOrderId, w => w.WorkOrderId,
                      (d, w) => new
                      {
                          DemandId = (string?)null, d.WorkOrderId, OperationSeq = d.OperationSeq,
                          DemandQty = d.Quantity, TransferredQty = 0M,
                          RequiredDate = w.PlannedEndDate,
                          DemandType = w.IsOutsource ? OutsourceDemandType.WholeProduct : OutsourceDemandType.Operation,
                          w.ProductCode
                      })
                .ToListAsync();

            var productUomMap = await Db.Products.AsNoTracking()
                .Select(p => new { p.ProductCode, p.StockUom }).ToDictionaryAsync(p => p.ProductCode, p => p.StockUom);

            var pendingRows = rawDemands.Select(x => new
            {
                x.DemandId,
                x.WorkOrderId,
                OperationSeq = 0,
                x.ProductCode,
                UomCode = productUomMap.GetValueOrDefault(x.ProductCode),
                x.DemandQty,
                x.TransferredQty,
                RemainingQty = x.DemandQty - x.TransferredQty,
                RequiredDate = x.RequiredDate.ToString("yyyy-MM-dd"),
                Kind = x.DemandType == OutsourceDemandType.WholeProduct ? _localizer["OutsourceWhole"].Value : _localizer["OutsourceOp"].Value
            }).Concat(rawLegacy.Select(x => new
            {
                x.DemandId,
                x.WorkOrderId,
                x.OperationSeq,
                x.ProductCode,
                UomCode = productUomMap.GetValueOrDefault(x.ProductCode),
                x.DemandQty,
                x.TransferredQty,
                RemainingQty = x.DemandQty,
                RequiredDate = x.RequiredDate.ToString("yyyy-MM-dd"),
                Kind = x.DemandType == OutsourceDemandType.WholeProduct ? _localizer["OutsourceWhole"].Value : _localizer["OutsourceOp"].Value
            })).ToList();

            ViewBag.Pending = pendingRows.Cast<object>().ToList();
            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();

            ViewBag.Suppliers = await Db.Set<Company>().AsNoTracking()
                .Where(c => c.IsSupplier && c.IsActive)
                .OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName })
                .ToListAsync();

            return PartialView("_Worklist");
        }

        /// <summary>
        /// 建立委外採購單（Phase G 拆單版）。
        /// 支援格式 demandId|woId|opSeq|splitQty，同一 demandId 可拆至不同廠商/交期。
        /// 若 demandId 為空（舊路徑），自動建立 OutsourceDemand 並整筆轉換。
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Convert(string[] selectedKeys, string supplierCode, DateOnly? expectedDate)
        {
            if (!await Permissions.CanCreate(MenuKey))
                return Content($"<script>alert({JsonSerializer.Serialize(_localizer["NoCreatePermission"].Value)});</script>", "text/html");

            if (selectedKeys == null || selectedKeys.Length == 0)
                return Content($"<script>alert({JsonSerializer.Serialize(_localizer["OutsourceNoSelection"].Value)});</script>", "text/html");
            if (string.IsNullOrWhiteSpace(supplierCode))
                return Content($"<script>alert({JsonSerializer.Serialize(_localizer["OutsourceSupplierRequired"].Value)});</script>", "text/html");

            // 解析格式：demandId|woId|opSeq|splitQty（demandId 可為空）
            var items = selectedKeys
                .Select(k => k.Split('|'))
                .Where(p => p.Length >= 3 && int.TryParse(p[2], out _))
                .Select(p => (
                    DemandId:  p.Length > 3 && !string.IsNullOrEmpty(p[0]) ? p[0] : null,
                    WoId:      p[1],
                    Op:        int.Parse(p[2]),
                    SplitQty:  p.Length > 3 && decimal.TryParse(p[3], out var q) ? q : 0M
                ))
                .ToList();

            // 舊格式相容：woId|opSeq
            if (!items.Any())
            {
                items = selectedKeys
                    .Select(k => k.Split('|'))
                    .Where(p => p.Length == 2 && int.TryParse(p[1], out _))
                    .Select(p => (DemandId: (string?)null, WoId: p[0], Op: int.Parse(p[1]), SplitQty: 0M))
                    .ToList();
            }

            if (items.Count == 0)
                return Content($"<script>alert({JsonSerializer.Serialize(_localizer["OutsourceNoSelection"].Value)});</script>", "text/html");

            var plantId = UserContext.PlantId ?? string.Empty;
            var today   = DateOnly.FromDateTime(DateTime.UtcNow);

            var woIds = items.Select(i => i.WoId).Distinct().ToList();
            var wos = await Db.WorkOrders.Where(w => woIds.Contains(w.WorkOrderId)).ToListAsync();
            var dispatches = await Db.Dispatches.AsNoTracking()
                .Where(d => woIds.Contains(d.WorkOrderId) && d.IsOutsource).ToListAsync();
            var productCodes = wos.Select(w => w.ProductCode).Distinct().ToList();
            var stockUoms = await Db.Set<Product>().AsNoTracking()
                .Where(p => productCodes.Contains(p.ProductCode))
                .ToDictionaryAsync(p => p.ProductCode, p => p.StockUom);

            // 取得報價（4-level 自動帶入）。明細多的單只載一次，逐行再挑，避免 N+1。
            var quotationLookup = await _quotationResolver.LoadActiveAsync(supplierCode, today);

            var poId = await _docNumber.NextAsync("OutsourcePurchaseOrder", plantId, today);
            var po = new OutsourcePurchaseOrder
            {
                OutsourcePurchaseOrderId = poId,
                SupplierCode = supplierCode,
                OrderDate    = today,
                ExpectedDeliveryDate = expectedDate,
                Status  = OutsourcePoStatus.Draft,
                PlantId = plantId
            };

            var lineNo = 0;
            var demandUpdates = new List<OutsourceDemand>();

            foreach (var (demandIdParam, woId, op, splitQtyParam) in items)
            {
                var wo = wos.FirstOrDefault(w => w.WorkOrderId == woId);
                var dispatch = dispatches.FirstOrDefault(d => d.WorkOrderId == woId && d.OperationSeq == op);
                if (wo == null) continue;

                // 取得或建立 OutsourceDemand
                OutsourceDemand? demand = null;
                if (!string.IsNullOrEmpty(demandIdParam))
                {
                    demand = await Db.OutsourceDemands.FirstOrDefaultAsync(d => d.DemandId == demandIdParam);
                }
                if (demand == null)
                {
                    // 相容：自動建立（舊路徑，或 MRP 尚未執行的委外派工）
                    var dispatchId = dispatch?.DispatchId;
                    var existingDemand = await Db.OutsourceDemands
                        .FirstOrDefaultAsync(d => d.WorkOrderId == woId
                            && (op == 0 || d.DispatchId == dispatchId));
                    if (existingDemand != null)
                    {
                        demand = existingDemand;
                    }
                    else
                    {
                        var qty = dispatch?.Quantity ?? wo.Quantity;
                        demand = new OutsourceDemand
                        {
                            DemandId    = await _docNumber.NextAsync("OutsourceDemand", plantId, today),
                            WorkOrderId = woId,
                            DispatchId  = dispatch?.DispatchId,
                            DemandType  = wo.IsOutsource ? OutsourceDemandType.WholeProduct : OutsourceDemandType.Operation,
                            ProductCode = wo.ProductCode,
                            DemandQty   = qty,
                            RequiredDate = DateOnly.FromDateTime(wo.PlannedEndDate),
                            PlantId     = plantId
                        };
                        Db.OutsourceDemands.Add(demand);
                        await Db.SaveChangesAsync();
                    }
                }

                var effectiveQty = splitQtyParam > 0 ? splitQtyParam : (dispatch?.Quantity ?? wo.Quantity);
                var remaining = demand.DemandQty - demand.TransferredQty;
                if (effectiveQty > remaining) effectiveQty = remaining;
                if (effectiveQty <= 0) continue;

                // 4-level 報價查詢。實作與理由（為什麼不能只靠排序）見
                // IOutsourceQuotationResolver.Resolve——2026-09-23 收斂成單一來源，
                // 因為委外價格分析報表是第三個呼叫端。
                var bestQuote = _quotationResolver.Resolve(
                    quotationLookup, wo.ProductCode, wo.RoutingId, op);

                var uom = stockUoms.TryGetValue(wo.ProductCode, out var u) ? u : null;

                po.Lines.Add(new OutsourcePurchaseOrderLine
                {
                    OutsourcePurchaseOrderId = poId,
                    LineNo       = ++lineNo,
                    DemandId     = demand.DemandId,
                    SourceWorkOrderId = woId,
                    OperationSeq = op,
                    ProductCode  = wo.ProductCode,
                    Qty          = effectiveQty,
                    SplitQty     = effectiveQty,
                    UomCode      = bestQuote?.UomCode ?? uom,
                    UnitPrice    = bestQuote?.UnitPrice ?? 0M,
                    // 合約允收損耗率與單價一起快照：兩者是同一份報價談出來的條件。
                    // 沒有報價（或報價沒填）時留 null ＝ 未約定 → 收貨時不扣款。
                    AllowedScrapRate = bestQuote?.AllowedScrapRate,
                    CurrencyCode = bestQuote?.CurrencyCode
                });

                // 更新委外建議已轉量
                demand.TransferredQty += effectiveQty;
                demand.UpdatedAt = DateTime.UtcNow;

                // 整品委外工單：回填委外欄（mfg-domain.md §19.2）
                if (wo.IsOutsource)
                {
                    wo.OutsourcePurchaseOrderId = poId;
                    wo.OutsourceSupplierCode = supplierCode;
                    wo.OutsourceExpectedDeliveryDate = expectedDate;
                    wo.UpdatedAt = DateTime.UtcNow;
                }
            }

            if (po.Lines.Count == 0)
                return Content($"<script>alert({JsonSerializer.Serialize(_localizer["OutsourceNoSelection"].Value)});</script>", "text/html");

            Db.OutsourcePurchaseOrders.Add(po);
            await Db.SaveChangesAsync();

            return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Confirm(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });

            var po = await Db.OutsourcePurchaseOrders.FirstOrDefaultAsync(o => o.OutsourcePurchaseOrderId == id);
            if (po == null) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
            if (po.Status != OutsourcePoStatus.Draft)
                return Json(new { success = false, message = _localizer["OutsourceNotDraft"].Value });

            po.Status = OutsourcePoStatus.Confirmed;
            po.UpdatedAt = DateTime.UtcNow;
            await Db.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["OutsourceConfirmed"].Value });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            if (!await Permissions.CanDelete(MenuKey))
                return Json(new { success = false, message = _localizer["NoDeletePermission"].Value });

            var po = await Db.OutsourcePurchaseOrders.Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.OutsourcePurchaseOrderId == id);
            if (po == null) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
            if (po.Status != OutsourcePoStatus.Draft)
                return Json(new { success = false, message = _localizer["OutsourceNotDraft"].Value });

            await ReleaseLinksAsync(po);

            Db.OutsourcePurchaseOrders.Remove(po);   // 級聯刪除明細
            await Db.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
        }

        /// <summary>
        /// 作廢已確認的委外採購單（2026-10-20 A5）：原本已確認者既不能刪也不能作廢，廠商取消時
        /// 委外單與來源工單都收不了尾（工單作廢會被它擋住）。只在還沒有任何收貨、
        /// 送到廠商的料已全數退回時允許——否則在外原料與已收貨的帳無處承接。
        /// 作廢後比照刪除解除工單連結、回沖委外建議已轉量，委外建議可重新轉單。
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Void(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });

            var po = await Db.OutsourcePurchaseOrders.Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.OutsourcePurchaseOrderId == id);
            if (po == null) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
            if (po.Status != OutsourcePoStatus.Confirmed)
                return Json(new { success = false, message = _localizer["OpoVoidNotConfirmed"].Value });

            var receipts = await Db.OutsourceReceipts.AsNoTracking()
                .Where(r => r.OutsourcePurchaseOrderId == id && r.Status != DocStatus.Void)
                .Select(r => r.OutsourceReceiptId).ToListAsync();
            if (receipts.Count > 0)
                return Json(new { success = false, message = string.Format(
                    _localizer["OpoVoidHasReceipts"].Value, string.Join(", ", receipts)) });

            // 送到廠商的料：已過帳的委外發料 − 委外退料，任一工單、任一料號 > 0 就擋
            var woIds = po.Lines.Select(l => l.SourceWorkOrderId).Distinct().ToList();
            var moved = await Db.MaterialSlips.AsNoTracking()
                .Where(s => s.SourceDocType == "WorkOrder" && s.SourceDocId != null && woIds.Contains(s.SourceDocId)
                         && s.Status == MaterialSlipStatus.Posted
                         && (s.Category == MaterialSlipCategory.OutsourceIssue || s.Category == MaterialSlipCategory.OutsourceReturn))
                .SelectMany(s => s.Lines.Select(l => new
                {
                    s.SourceDocId, s.Category, l.ProductCode,
                    Qty = l.ConfirmedQty > 0 ? l.ConfirmedQty : l.RequestedQty,
                }))
                .ToListAsync();
            var atVendor = moved
                .GroupBy(m => (m.SourceDocId, m.ProductCode))
                .Where(g => g.Sum(m => m.Category == MaterialSlipCategory.OutsourceIssue ? m.Qty : -m.Qty) > 0)
                .Select(g => g.Key.ProductCode).Distinct().ToList();
            if (atVendor.Count > 0)
                return Json(new { success = false, message = string.Format(
                    _localizer["OpoVoidMaterialAtVendor"].Value, string.Join(", ", atVendor)) });

            await ReleaseLinksAsync(po);
            po.Status = OutsourcePoStatus.Void;
            po.UpdatedAt = DateTime.UtcNow;
            await Db.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["OpoVoided"].Value });
        }

        /// <summary>刪除與作廢共用：解除整品委外工單的回填連結、回沖委外建議已轉量。不呼叫 SaveChanges。</summary>
        private async Task ReleaseLinksAsync(OutsourcePurchaseOrder po)
        {
            var id = po.OutsourcePurchaseOrderId;
            // 解除整品委外工單的回填連結
            var woIds = po.Lines.Select(l => l.SourceWorkOrderId).Distinct().ToList();
            var wos = await Db.WorkOrders.Where(w => woIds.Contains(w.WorkOrderId) && w.OutsourcePurchaseOrderId == id).ToListAsync();
            foreach (var wo in wos)
            {
                wo.OutsourcePurchaseOrderId = null;
                wo.OutsourceSupplierCode = null;
                wo.OutsourceExpectedDeliveryDate = null;
            }

            // 回沖委外建議的已轉量（Convert 時 TransferredQty += SplitQty）。
            //
            // 少了這段，刪掉委外採購單會讓來源建議永久卡死，而且是兩處同時卡：
            //   ① Worklist 只列 TransferredQty < DemandQty 的建議 → 這筆從轉單清單消失，
            //      再也轉不出新的委外採購單，但料其實根本沒訂出去。
            //   ② MrpEngine 把 TransferredQty > 0 判為「已轉委外＝對外承諾」並 Blocked 擋下重跑，
            //      理由指向一張已經不存在的採購單——這個阻擋沒有任何畫面能解除。
            // 兩者都不留異動也不留傳票，任何以單號查過帳足跡的檢查都看不見。
            // 下限 0 與 PurchaseService.RestorePrOrderedQtyAsync 等回沖同一寫法。
            var demandIds = po.Lines.Where(l => !string.IsNullOrEmpty(l.DemandId))
                .Select(l => l.DemandId!).Distinct().ToList();
            if (demandIds.Count > 0)
            {
                var demands = await Db.OutsourceDemands
                    .Where(d => demandIds.Contains(d.DemandId)).ToListAsync();
                foreach (var line in po.Lines.Where(l => !string.IsNullOrEmpty(l.DemandId)))
                {
                    var demand = demands.FirstOrDefault(d => d.DemandId == line.DemandId);
                    if (demand == null) continue;
                    demand.TransferredQty = Math.Max(0m, demand.TransferredQty - line.SplitQty);
                    demand.UpdatedAt = DateTime.UtcNow;
                }
            }
        }

        private async Task<Dictionary<string, string>> SupplierNamesAsync(IEnumerable<string> codes)
        {
            var list = codes.Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
            if (list.Count == 0) return new Dictionary<string, string>();
            return await Db.Set<Company>().AsNoTracking()
                .Where(c => list.Contains(c.CompanyCode))
                .ToDictionaryAsync(c => c.CompanyCode, c => c.CompanyName);
        }

        // GET: /OutsourcePurchaseOrder/Print/{id}（2026-10-27：送交委外廠商的紙本）
        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await Permissions.CanPrint(MenuKey)) return Forbid();
            var doc = await Db.OutsourcePurchaseOrders.AsNoTracking().Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.OutsourcePurchaseOrderId == id);
            if (doc == null) return NotFound();
            await PreparePrintViewBagAsync(Url.Action("Details", null, new { id }, Request.Scheme)!);
            await PreparePrintSignAsync(id, null);
            var n = await PrintNames.ForAsync(doc);
            var f = await PrintFormatsAsync();
            var L = PrintL;
            var t = ScreenText("MFG", "OutsourcePurchaseOrder");
            var p = new PrintDocument
            {
                Title = L["Print_Title_OutsourcePurchaseOrder"], DocNo = doc.OutsourcePurchaseOrderId,
                DateLabel = L["Print_OrderDate"], DateValue = doc.OrderDate.ToString("yyyy-MM-dd"),
            }
                .Field(L["Print_Supplier"], n.CompanyWith(doc.SupplierCode))
                .Field(L["Print_ExpectedDate"], doc.ExpectedDeliveryDate?.ToString("yyyy-MM-dd"))
                .Field(L["Print_Status"], t["Status" + doc.Status])
                .Field(L["Print_CreatedAt"], PrintTime(doc.CreatedAt))
                .Field(L["Print_Remarks"], doc.Remarks);
            var tbl = new PrintTable()
                .Col(L["Print_LineNo"]).Col(L["Print_WorkOrder"]).Col(L["Print_OperationSeq"])
                .Col(L["Print_ProductCode"]).Col(L["Print_ProductName"])
                .Col(L["Print_Qty"], true).Col(L["Print_Uom"]).Col(L["Print_Currency"])
                .Col(L["Print_UnitPrice"], true).Col(L["Print_Amount"], true);
            foreach (var l in doc.Lines.OrderBy(x => x.LineNo))
                tbl.Row(l.LineNo.ToString(), l.SourceWorkOrderId, l.OperationSeq.ToString(),
                    l.ProductCode, n.Product(l.ProductCode),
                    f.Qty(l.Qty, l.UomCode), n.UomWith(l.UomCode), n.CurrencyWith(l.CurrencyCode),
                    f.Money(l.UnitPrice, l.CurrencyCode), f.Money(l.Qty * l.UnitPrice, l.CurrencyCode));
            p.Tables.Add(tbl);
            foreach (var g in doc.Lines.GroupBy(x => x.CurrencyCode ?? "").OrderBy(g => g.Key))
                p.Total($"{L["Print_Total"]} {n.CurrencyWith(g.Key)}：{f.Money(g.Sum(x => x.Qty * x.UnitPrice), g.Key)}");
            return PrintDocumentView(p);
        }
    }
}
