using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;
using Microsoft.Extensions.Localization;

namespace SmartFactory.MES.Modules.INV.Controllers
{
    /// <summary>
    /// 倉庫作業 API（供 PC 倉庫作業畫面與 PWA 手機使用）。
    /// 負責：待處理明細查詢、儲位庫存查詢、入庫上架、出庫下架。
    /// 支援 Phase 1 (GoodsReceipt/GoodsReturn) 及 Phase 2 (LoanDoc/SampleDoc/ScrapDoc) 及 Phase 3 (GoodsShipment/SalesReturn)。
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/warehouse")]
    public partial class WarehouseApiController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IInventoryPostingService _postingService;
        private readonly IInventoryService _invService;
        private readonly IPermissionService _permissionService;
        private readonly IUomService _uomService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly Modules.FIN.Services.IAPInvoiceService? _apInvoice;   // optional：FIN 未啟用時為 null
        // optional：委外超耗扣款分錄用（完整模式下於上架完成時補提，見 PutAwayOutsourceReceipt）
        private readonly Modules.ACT.Services.IJournalEntryService? _journalService;
        private readonly Modules.FIN.Services.IARInvoiceService? _arInvoice;   // optional：FIN 未啟用時為 null
        private readonly Modules.FIN.Services.IARCreditNoteService? _arCreditNote;  // 同上（W3-F3 Phase 2）
        private readonly Modules.FIN.Services.IAPDebitNoteService? _apDebitNote;    // 同上
        private const string MenuKey = "WarehouseOps";

        // 共用查詢端點的合法呼叫端白名單（R24）。
        //
        // 品號／儲位／庫存查詢不是「倉庫作業畫面專用」——INV 的 9 張單據 _Form 都靠它們
        // 帶出可用量與標準成本（見 Views/*/_Form.cshtml 的 /api/warehouse/stock 呼叫）。
        // 若這幾支只鎖 MenuKey（WarehouseOps），「有調撥單但沒有倉庫作業」的角色會在
        // 開單時**靜默壞掉**：fetch 收到 403，畫面只是可用量空白、儲位下拉沒東西，
        // 沒有任何錯誤訊息，而開發／測試都用全權限帳號永遠重現不出來。
        // 故改判「這些畫面任一可讀」——仍然是白名單，不在名單上的角色照擋。
        private static readonly string[] StockLookupKeys =
        {
            MenuKey, "InventoryCount", "StockTransfer", "StockAdjustment", "ScrapDoc",
            "LoanDoc", "SampleDoc", "ReworkMaterial", "OutsourceMaterial", "CrossPlantTransfer",
        };

        // TypeCode 路由表（依 Direction：+1=入庫→上架，-1=出庫→下架）
        private static readonly HashSet<string> GoodsReceiptTypeCodes = new() { "PI", "OI" };  // +1 採購進貨/其他進貨 → 上架
        private static readonly HashSet<string> GoodsReturnTypeCodes  = new() { "PF", "OT" };  // -1 採購退貨/其他退貨 → 下架
        private static readonly HashSet<string> LoanInTypeCodes       = new() { "IB", "LR" };  // +1 借入/借出退回 → 上架
        private static readonly HashSet<string> LoanOutTypeCodes      = new() { "BR", "IL" };  // -1 借入退回/借出 → 下架
        private static readonly HashSet<string> SampleInTypeCodes     = new() { "CI", "SR" };  // +1 客供進貨/樣品退回 → 上架
        private static readonly HashSet<string> SampleOutTypeCodes    = new() { "CR", "SO" };  // -1 客供退回/樣品出貨 → 下架
        private static readonly HashSet<string> ScrapTypeCodes        = new() { "IJ" };
        // 庫存重工發退料單（/ReworkMaterial）：RI 發料 → 下架、RR 退料 → 上架。2026-10-16 前沒有路由——
        // 完整模式下確認後永遠進不了倉庫作業、停在「已確認」（簡化模式確認即過帳，所以沒人發現）。
        // ⚠️ 工單發退料單（MaterialSlip）也用 RI／RR 異動類別，但它走自己的發料動作，不在這兩份待辦清單裡。
        private static readonly HashSet<string> ReworkOutTypeCodes    = new() { "RI" };
        private static readonly HashSet<string> ReworkInTypeCodes     = new() { "RR" };
        // 庫存委外發退料單（/OutsourceMaterial）：OO 發料 → 下架、OR 退料 → 上架。與重工發退料單同一個缺口（2026-10-16 補）。
        // ⚠️ 工單發退料單的委外發料／退料也用 OO／OR，同樣不在這兩份清單裡。
        private static readonly HashSet<string> OutsourceOutTypeCodes = new() { "OO" };
        private static readonly HashSet<string> OutsourceInTypeCodes  = new() { "OR" };
        private static readonly HashSet<string> GoodsShipmentTypeCodes = new() { "GS" }; // -1 出貨下架
        private static readonly HashSet<string> SalesReturnTypeCodes   = new() { "GR" }; // +1 銷退上架
        private static readonly HashSet<string> InternalOrderOutTypeCodes = new() { "NO" }; // -1 內部訂單領用 → 下架（mfg-domain.md / inventory.md）
        private static readonly HashSet<string> InternalOrderInTypeCodes  = new() { "NR" }; // +1 內部訂單退料 → 上架（沖銷 NO 領用；ord-module.md §退料訂單）

        /// <summary>
        /// 單別判別欄。委外收貨與一般進貨單**共用 TypeCode "OI"**，只靠 TypeCode 路由會把
        /// 委外收貨導到 GoodsReceipt handler 而查無此單。既有單別不帶此欄（維持原路由）。
        /// </summary>
        public const string OutsourceReceiptDocType = "OutsourceReceipt";

        private readonly IInventoryReservationService? _reservations;

        public WarehouseApiController(
            ApplicationDbContext context,
            IUserContext userContext,
            IInventoryPostingService postingService,
            IInventoryService invService,
            IPermissionService permissionService,
            IUomService uomService,
            IStringLocalizer<ProjectZSharedResource> localizer,
            Modules.FIN.Services.IAPInvoiceService? apInvoice = null,
            Modules.FIN.Services.IARInvoiceService? arInvoice = null,
            Modules.ACT.Services.IJournalEntryService? journalService = null,
            Modules.FIN.Services.IARCreditNoteService? arCreditNote = null,
            Modules.FIN.Services.IAPDebitNoteService? apDebitNote = null,
            IInventoryReservationService? reservations = null)
        {
            _reservations = reservations;
            _context = context;
            _userContext = userContext;
            _postingService = postingService;
            _invService = invService;
            _permissionService = permissionService;
            _uomService = uomService;
            _apInvoice = apInvoice;
            _arInvoice = arInvoice;
            _arCreditNote = arCreditNote;
            _apDebitNote = apDebitNote;
            _journalService = journalService;
            _localizer = localizer;
        }

        // ───────────────────── Query ─────────────────────

        private static object ToFlatRow(string docNo, string typeCode, DateOnly docDate, dynamic l) => new
        {
            docNo,
            typeCode,
            docDate       = docDate.ToString("yyyy-MM-dd"),
            lineNo        = (int)l.LineNo,
            productCode   = (string)l.ProductCode,
            stockQty      = (decimal)l.StockQty,
            qtyPutAway    = (decimal)l.QtyPutAway,
            pending       = (decimal)l.Pending,
            stockUomCode  = (string?)l.StockUomCode,
            uomCode       = (string?)l.UomCode,
            whCode        = (string?)l.WhCode,
        };

        /// <summary>待入庫明細：Status=Confirmed 且 QtyPutAway &lt; StockQty。</summary>
        [HttpGet("pending-inbound")]
        public async Task<IActionResult> PendingInbound([FromQuery] string? typeCode = null, [FromQuery] string? search = null)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            var rows = new List<object>();

            // ── GoodsReceipt (PI / OI) ──
            if (typeCode == null || GoodsReceiptTypeCodes.Contains(typeCode))
            {
                var q = _context.GoodsReceipts.AsNoTracking()
                    .Where(g => g.Status == DocStatus.Confirmed);
                if (typeCode != null) q = q.Where(g => g.TypeCode == typeCode);
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var kw = search.ToLower();
                    q = q.Where(g => g.GoodsReceiptId.ToLower().Contains(kw)
                                  || g.Lines.Any(l => l.ProductCode.ToLower().Contains(kw)));
                }
                var heads = await q.Select(g => new
                {
                    g.GoodsReceiptId, g.TypeCode, g.ReceiptDate,
                    Lines = g.Lines.Where(l => l.QtyPutAway < l.StockQty).Select(l => new
                    {
                        l.LineNo, l.ProductCode, l.StockQty, l.QtyPutAway, l.StockUomCode, l.UomCode,
                        l.WhCode, Pending = l.StockQty - l.QtyPutAway,
                    }).ToList(),
                }).ToListAsync();

                rows.AddRange(heads.SelectMany(h => h.Lines.Select(l => (object)new
                {
                    docNo        = h.GoodsReceiptId,
                    typeCode     = h.TypeCode,
                    docDate      = h.ReceiptDate.ToString("yyyy-MM-dd"),
                    lineNo       = l.LineNo,
                    productCode  = l.ProductCode,
                    stockQty     = l.StockQty,
                    qtyPutAway   = l.QtyPutAway,
                    pending      = l.Pending,
                    stockUomCode = l.StockUomCode,
                    uomCode      = l.UomCode,
                    whCode       = l.WhCode,
                })));
            }

            // ── OutsourceReceipt (OI) ──
            // 完整模式下委外收貨確認只把單據轉 Confirmed，成品要等倉庫上架才真正進庫存
            // （簡化模式是在確認當下就過帳）。少了這一段，收回來的成品永遠不會出現在
            // 任何待辦裡：單據停在「等待倉庫作業」，而倉庫作業畫面看不到它（人工驗收 Wave 2）。
            if (typeCode == null || GoodsReceiptTypeCodes.Contains(typeCode))
            {
                var q = _context.OutsourceReceipts.AsNoTracking()
                    .Where(r => r.Status == DocStatus.Confirmed);
                if (typeCode != null) q = q.Where(r => r.TypeCode == typeCode);
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var kw = search.ToLower();
                    q = q.Where(r => r.OutsourceReceiptId.ToLower().Contains(kw)
                                  || r.Lines.Any(l => l.ProductCode.ToLower().Contains(kw)));
                }
                var heads = await q.Select(r => new
                {
                    r.OutsourceReceiptId, r.TypeCode, r.ReceiptDate,
                    Lines = r.Lines.Where(l => l.QtyPutAway < l.StockQty).Select(l => new
                    {
                        l.LineNo, l.ProductCode, l.StockQty, l.QtyPutAway, l.StockUomCode, l.UomCode,
                        l.WhCode, Pending = l.StockQty - l.QtyPutAway,
                    }).ToList(),
                }).ToListAsync();

                rows.AddRange(heads.SelectMany(h => h.Lines.Select(l => (object)new
                {
                    docNo        = h.OutsourceReceiptId,
                    docType      = OutsourceReceiptDocType,
                    typeCode     = h.TypeCode,
                    docDate      = h.ReceiptDate.ToString("yyyy-MM-dd"),
                    lineNo       = l.LineNo,
                    productCode  = l.ProductCode,
                    stockQty     = l.StockQty,
                    qtyPutAway   = l.QtyPutAway,
                    pending      = l.Pending,
                    stockUomCode = l.StockUomCode,
                    uomCode      = l.UomCode,
                    whCode       = l.WhCode,
                })));
            }

            // ── OutsourceMaterialDoc (OR) ──
            if (typeCode == null || OutsourceInTypeCodes.Contains(typeCode))
            {
                var q = _context.OutsourceMaterialDocs.AsNoTracking()
                    .Where(d => d.Status == DocStatus.Confirmed
                             && OutsourceInTypeCodes.Contains(d.TypeCode));
                if (typeCode != null) q = q.Where(d => d.TypeCode == typeCode);
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var kw = search.ToLower();
                    q = q.Where(d => d.OutsourceMaterialDocId.ToLower().Contains(kw)
                                  || d.Lines.Any(l => l.ProductCode.ToLower().Contains(kw)));
                }
                var heads = await q.Select(d => new
                {
                    d.OutsourceMaterialDocId, d.TypeCode, d.IssueDate,
                    Lines = d.Lines.Where(l => l.QtyPutAway < l.StockQty).Select(l => new
                    {
                        l.LineNo, l.ProductCode, l.StockQty, l.QtyPutAway, l.StockUomCode, l.UomCode,
                        l.WhCode, Pending = l.StockQty - l.QtyPutAway,
                    }).ToList(),
                }).ToListAsync();

                rows.AddRange(heads.SelectMany(h => h.Lines.Select(l => (object)new
                {
                    docNo        = h.OutsourceMaterialDocId,
                    typeCode     = h.TypeCode,
                    docDate      = h.IssueDate.ToString("yyyy-MM-dd"),
                    lineNo       = l.LineNo,
                    productCode  = l.ProductCode,
                    stockQty     = l.StockQty,
                    qtyPutAway   = l.QtyPutAway,
                    pending      = l.Pending,
                    stockUomCode = l.StockUomCode,
                    uomCode      = l.UomCode,
                    whCode       = l.WhCode,
                })));
            }

            // ── ReworkMaterialDoc (RR) ──
            if (typeCode == null || ReworkInTypeCodes.Contains(typeCode))
            {
                var q = _context.ReworkMaterialDocs.AsNoTracking()
                    .Where(d => d.Status == DocStatus.Confirmed
                             && ReworkInTypeCodes.Contains(d.TypeCode));
                if (typeCode != null) q = q.Where(d => d.TypeCode == typeCode);
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var kw = search.ToLower();
                    q = q.Where(d => d.ReworkMaterialDocId.ToLower().Contains(kw)
                                  || d.Lines.Any(l => l.ProductCode.ToLower().Contains(kw)));
                }
                var heads = await q.Select(d => new
                {
                    d.ReworkMaterialDocId, d.TypeCode, d.IssueDate,
                    Lines = d.Lines.Where(l => l.QtyPutAway < l.StockQty).Select(l => new
                    {
                        l.LineNo, l.ProductCode, l.StockQty, l.QtyPutAway, l.StockUomCode, l.UomCode,
                        l.WhCode, Pending = l.StockQty - l.QtyPutAway,
                    }).ToList(),
                }).ToListAsync();

                rows.AddRange(heads.SelectMany(h => h.Lines.Select(l => (object)new
                {
                    docNo        = h.ReworkMaterialDocId,
                    typeCode     = h.TypeCode,
                    docDate      = h.IssueDate.ToString("yyyy-MM-dd"),
                    lineNo       = l.LineNo,
                    productCode  = l.ProductCode,
                    stockQty     = l.StockQty,
                    qtyPutAway   = l.QtyPutAway,
                    pending      = l.Pending,
                    stockUomCode = l.StockUomCode,
                    uomCode      = l.UomCode,
                    whCode       = l.WhCode,
                })));
            }

            // ── LoanDoc (IB / LR) ──
            if (typeCode == null || LoanInTypeCodes.Contains(typeCode))
            {
                var q = _context.LoanDocs.AsNoTracking()
                    .Where(d => d.Status == DocStatus.Confirmed
                             && LoanInTypeCodes.Contains(d.TypeCode));
                if (typeCode != null) q = q.Where(d => d.TypeCode == typeCode);
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var kw = search.ToLower();
                    q = q.Where(d => d.LoanDocId.ToLower().Contains(kw)
                                  || d.Lines.Any(l => l.ProductCode.ToLower().Contains(kw)));
                }
                var heads = await q.Select(d => new
                {
                    d.LoanDocId, d.TypeCode, d.LoanDate,
                    Lines = d.Lines.Where(l => l.QtyPutAway < l.StockQty).Select(l => new
                    {
                        l.LineNo, l.ProductCode, l.StockQty, l.QtyPutAway, l.StockUomCode, l.UomCode,
                        l.WhCode, Pending = l.StockQty - l.QtyPutAway,
                    }).ToList(),
                }).ToListAsync();

                rows.AddRange(heads.SelectMany(h => h.Lines.Select(l => (object)new
                {
                    docNo        = h.LoanDocId,
                    typeCode     = h.TypeCode,
                    docDate      = h.LoanDate.ToString("yyyy-MM-dd"),
                    lineNo       = l.LineNo,
                    productCode  = l.ProductCode,
                    stockQty     = l.StockQty,
                    qtyPutAway   = l.QtyPutAway,
                    pending      = l.Pending,
                    stockUomCode = l.StockUomCode,
                    uomCode      = l.UomCode,
                    whCode       = l.WhCode,
                })));
            }

            // ── SampleDoc (CI / SR) ──
            if (typeCode == null || SampleInTypeCodes.Contains(typeCode))
            {
                var q = _context.SampleDocs.AsNoTracking()
                    .Where(d => d.Status == DocStatus.Confirmed
                             && SampleInTypeCodes.Contains(d.TypeCode));
                if (typeCode != null) q = q.Where(d => d.TypeCode == typeCode);
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var kw = search.ToLower();
                    q = q.Where(d => d.SampleDocId.ToLower().Contains(kw)
                                  || d.Lines.Any(l => l.ProductCode.ToLower().Contains(kw)));
                }
                var heads = await q.Select(d => new
                {
                    d.SampleDocId, d.TypeCode, d.SampleDate,
                    Lines = d.Lines.Where(l => l.QtyPutAway < l.StockQty).Select(l => new
                    {
                        l.LineNo, l.ProductCode, l.StockQty, l.QtyPutAway, l.StockUomCode, l.UomCode,
                        l.WhCode, Pending = l.StockQty - l.QtyPutAway,
                    }).ToList(),
                }).ToListAsync();

                rows.AddRange(heads.SelectMany(h => h.Lines.Select(l => (object)new
                {
                    docNo        = h.SampleDocId,
                    typeCode     = h.TypeCode,
                    docDate      = h.SampleDate.ToString("yyyy-MM-dd"),
                    lineNo       = l.LineNo,
                    productCode  = l.ProductCode,
                    stockQty     = l.StockQty,
                    qtyPutAway   = l.QtyPutAway,
                    pending      = l.Pending,
                    stockUomCode = l.StockUomCode,
                    uomCode      = l.UomCode,
                    whCode       = l.WhCode,
                })));
            }

            // ── SalesReturn (GR) ──
            if (typeCode == null || SalesReturnTypeCodes.Contains(typeCode))
            {
                var q = _context.SalesReturns.AsNoTracking()
                    .Where(d => d.Status == DocStatus.Confirmed);
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var kw = search.ToLower();
                    q = q.Where(d => d.SalesReturnId.ToLower().Contains(kw)
                                  || d.Lines.Any(l => l.ProductCode.ToLower().Contains(kw)));
                }
                var heads = await q.Select(d => new
                {
                    d.SalesReturnId, d.ReturnDate,
                    Lines = d.Lines.Where(l => l.QtyPutAway < l.StockQty).Select(l => new
                    {
                        l.LineNo, l.ProductCode, l.StockQty, l.QtyPutAway, l.StockUomCode, l.UomCode,
                        Pending = l.StockQty - l.QtyPutAway,
                    }).ToList(),
                }).ToListAsync();

                rows.AddRange(heads.SelectMany(h => h.Lines.Select(l => (object)new
                {
                    docNo        = h.SalesReturnId,
                    typeCode     = "GR",
                    docDate      = h.ReturnDate.ToString("yyyy-MM-dd"),
                    lineNo       = l.LineNo,
                    productCode  = l.ProductCode,
                    stockQty     = l.StockQty,
                    qtyPutAway   = l.QtyPutAway,
                    pending      = l.Pending,
                    stockUomCode = l.StockUomCode,
                    uomCode      = l.UomCode,
                    whCode       = (string?)null,
                })));
            }

            // ── InternalOrder (NR) ── 內部訂單退料 → 沖銷 NO 領用（ord-module.md §退料訂單）
            if (typeCode == null || InternalOrderInTypeCodes.Contains(typeCode))
            {
                var q = _context.InternalOrders.AsNoTracking()
                    .Where(d => d.Status == DocStatus.Confirmed
                             && InternalOrderInTypeCodes.Contains(d.TypeCode));
                if (typeCode != null) q = q.Where(d => d.TypeCode == typeCode);
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var kw = search.ToLower();
                    q = q.Where(d => d.InternalOrderId.ToLower().Contains(kw)
                                  || d.Lines.Any(l => l.ProductCode.ToLower().Contains(kw)));
                }
                var heads = await q.Select(d => new
                {
                    d.InternalOrderId, d.TypeCode, d.OrderDate,
                    Lines = d.Lines.Where(l => l.QtyPutAway < l.StockQty).Select(l => new
                    {
                        l.LineNo, l.ProductCode, l.StockQty, l.QtyPutAway, l.StockUomCode, l.UomCode,
                        l.WhCode, Pending = l.StockQty - l.QtyPutAway,
                    }).ToList(),
                }).ToListAsync();

                rows.AddRange(heads.SelectMany(h => h.Lines.Select(l => (object)new
                {
                    docNo        = h.InternalOrderId,
                    typeCode     = h.TypeCode,
                    docDate      = h.OrderDate.ToString("yyyy-MM-dd"),
                    lineNo       = l.LineNo,
                    productCode  = l.ProductCode,
                    stockQty     = l.StockQty,
                    qtyPutAway   = l.QtyPutAway,
                    pending      = l.Pending,
                    stockUomCode = l.StockUomCode,
                    uomCode      = l.UomCode,
                    whCode       = l.WhCode,
                })));
            }

            return Ok(rows);
        }

        /// <summary>待出庫明細：Status=Confirmed 且 QtyPutAway &lt; StockQty。</summary>
        [HttpGet("pending-outbound")]
        public async Task<IActionResult> PendingOutbound([FromQuery] string? typeCode = null, [FromQuery] string? search = null)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            var rows = new List<object>();

            // ── GoodsReturn (PF / OT) ──
            if (typeCode == null || GoodsReturnTypeCodes.Contains(typeCode))
            {
                var q = _context.GoodsReturns.AsNoTracking()
                    .Where(g => g.Status == DocStatus.Confirmed);
                if (typeCode != null) q = q.Where(g => g.TypeCode == typeCode);
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var kw = search.ToLower();
                    q = q.Where(g => g.GoodsReturnId.ToLower().Contains(kw)
                                  || g.Lines.Any(l => l.ProductCode.ToLower().Contains(kw)));
                }
                var heads = await q.Select(g => new
                {
                    g.GoodsReturnId, g.TypeCode, g.ReturnDate,
                    Lines = g.Lines.Where(l => l.QtyPutAway < l.StockQty).Select(l => new
                    {
                        l.LineNo, l.ProductCode, l.StockQty, l.QtyPutAway, l.StockUomCode, l.UomCode,
                        WhCode = (string?)null, Pending = l.StockQty - l.QtyPutAway,
                    }).ToList(),
                }).ToListAsync();

                rows.AddRange(heads.SelectMany(h => h.Lines.Select(l => (object)new
                {
                    docNo        = h.GoodsReturnId,
                    typeCode     = h.TypeCode,
                    docDate      = h.ReturnDate.ToString("yyyy-MM-dd"),
                    lineNo       = l.LineNo,
                    productCode  = l.ProductCode,
                    stockQty     = l.StockQty,
                    qtyPutAway   = l.QtyPutAway,
                    pending      = l.Pending,
                    stockUomCode = l.StockUomCode,
                    uomCode      = l.UomCode,
                    whCode       = l.WhCode,
                })));
            }

            // ── LoanDoc (BR / IL) ──
            if (typeCode == null || LoanOutTypeCodes.Contains(typeCode))
            {
                var q = _context.LoanDocs.AsNoTracking()
                    .Where(d => d.Status == DocStatus.Confirmed
                             && LoanOutTypeCodes.Contains(d.TypeCode));
                if (typeCode != null) q = q.Where(d => d.TypeCode == typeCode);
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var kw = search.ToLower();
                    q = q.Where(d => d.LoanDocId.ToLower().Contains(kw)
                                  || d.Lines.Any(l => l.ProductCode.ToLower().Contains(kw)));
                }
                var heads = await q.Select(d => new
                {
                    d.LoanDocId, d.TypeCode, d.LoanDate,
                    Lines = d.Lines.Where(l => l.QtyPutAway < l.StockQty).Select(l => new
                    {
                        l.LineNo, l.ProductCode, l.StockQty, l.QtyPutAway, l.StockUomCode, l.UomCode,
                        l.WhCode, Pending = l.StockQty - l.QtyPutAway,
                    }).ToList(),
                }).ToListAsync();

                rows.AddRange(heads.SelectMany(h => h.Lines.Select(l => (object)new
                {
                    docNo        = h.LoanDocId,
                    typeCode     = h.TypeCode,
                    docDate      = h.LoanDate.ToString("yyyy-MM-dd"),
                    lineNo       = l.LineNo,
                    productCode  = l.ProductCode,
                    stockQty     = l.StockQty,
                    qtyPutAway   = l.QtyPutAway,
                    pending      = l.Pending,
                    stockUomCode = l.StockUomCode,
                    uomCode      = l.UomCode,
                    whCode       = l.WhCode,
                })));
            }

            // ── SampleDoc (CR / SO) ──
            if (typeCode == null || SampleOutTypeCodes.Contains(typeCode))
            {
                var q = _context.SampleDocs.AsNoTracking()
                    .Where(d => d.Status == DocStatus.Confirmed
                             && SampleOutTypeCodes.Contains(d.TypeCode));
                if (typeCode != null) q = q.Where(d => d.TypeCode == typeCode);
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var kw = search.ToLower();
                    q = q.Where(d => d.SampleDocId.ToLower().Contains(kw)
                                  || d.Lines.Any(l => l.ProductCode.ToLower().Contains(kw)));
                }
                var heads = await q.Select(d => new
                {
                    d.SampleDocId, d.TypeCode, d.SampleDate,
                    Lines = d.Lines.Where(l => l.QtyPutAway < l.StockQty).Select(l => new
                    {
                        l.LineNo, l.ProductCode, l.StockQty, l.QtyPutAway, l.StockUomCode, l.UomCode,
                        l.WhCode, Pending = l.StockQty - l.QtyPutAway,
                    }).ToList(),
                }).ToListAsync();

                rows.AddRange(heads.SelectMany(h => h.Lines.Select(l => (object)new
                {
                    docNo        = h.SampleDocId,
                    typeCode     = h.TypeCode,
                    docDate      = h.SampleDate.ToString("yyyy-MM-dd"),
                    lineNo       = l.LineNo,
                    productCode  = l.ProductCode,
                    stockQty     = l.StockQty,
                    qtyPutAway   = l.QtyPutAway,
                    pending      = l.Pending,
                    stockUomCode = l.StockUomCode,
                    uomCode      = l.UomCode,
                    whCode       = l.WhCode,
                })));
            }

            // ── InternalOrder (NO) ── 內部訂單領用（mfg-domain.md ②/§11 / inventory.md）
            if (typeCode == null || InternalOrderOutTypeCodes.Contains(typeCode))
            {
                var q = _context.InternalOrders.AsNoTracking()
                    .Where(d => d.Status == DocStatus.Confirmed
                             && InternalOrderOutTypeCodes.Contains(d.TypeCode));
                if (typeCode != null) q = q.Where(d => d.TypeCode == typeCode);
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var kw = search.ToLower();
                    q = q.Where(d => d.InternalOrderId.ToLower().Contains(kw)
                                  || d.Lines.Any(l => l.ProductCode.ToLower().Contains(kw)));
                }
                var heads = await q.Select(d => new
                {
                    d.InternalOrderId, d.TypeCode, d.OrderDate,
                    Lines = d.Lines.Where(l => l.QtyPutAway < l.StockQty).Select(l => new
                    {
                        l.LineNo, l.ProductCode, l.StockQty, l.QtyPutAway, l.StockUomCode, l.UomCode,
                        l.WhCode, Pending = l.StockQty - l.QtyPutAway,
                    }).ToList(),
                }).ToListAsync();

                rows.AddRange(heads.SelectMany(h => h.Lines.Select(l => (object)new
                {
                    docNo        = h.InternalOrderId,
                    typeCode     = h.TypeCode,
                    docDate      = h.OrderDate.ToString("yyyy-MM-dd"),
                    lineNo       = l.LineNo,
                    productCode  = l.ProductCode,
                    stockQty     = l.StockQty,
                    qtyPutAway   = l.QtyPutAway,
                    pending      = l.Pending,
                    stockUomCode = l.StockUomCode,
                    uomCode      = l.UomCode,
                    whCode       = l.WhCode,
                })));
            }

            // ── ScrapDoc (IJ) ──
            if (typeCode == null || ScrapTypeCodes.Contains(typeCode))
            {
                var q = _context.ScrapDocs.AsNoTracking()
                    .Where(d => d.Status == DocStatus.Confirmed);
                if (typeCode != null) q = q.Where(d => d.TypeCode == typeCode);
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var kw = search.ToLower();
                    q = q.Where(d => d.ScrapDocId.ToLower().Contains(kw)
                                  || d.Lines.Any(l => l.ProductCode.ToLower().Contains(kw)));
                }
                var heads = await q.Select(d => new
                {
                    d.ScrapDocId, d.TypeCode, d.ScrapDate,
                    Lines = d.Lines.Where(l => l.QtyPutAway < l.StockQty).Select(l => new
                    {
                        l.LineNo, l.ProductCode, l.StockQty, l.QtyPutAway, l.StockUomCode, l.UomCode,
                        l.WhCode, Pending = l.StockQty - l.QtyPutAway,
                    }).ToList(),
                }).ToListAsync();

                rows.AddRange(heads.SelectMany(h => h.Lines.Select(l => (object)new
                {
                    docNo        = h.ScrapDocId,
                    typeCode     = h.TypeCode,
                    docDate      = h.ScrapDate.ToString("yyyy-MM-dd"),
                    lineNo       = l.LineNo,
                    productCode  = l.ProductCode,
                    stockQty     = l.StockQty,
                    qtyPutAway   = l.QtyPutAway,
                    pending      = l.Pending,
                    stockUomCode = l.StockUomCode,
                    uomCode      = l.UomCode,
                    whCode       = l.WhCode,
                })));
            }

            // ── OutsourceMaterialDoc (OO) ──
            if (typeCode == null || OutsourceOutTypeCodes.Contains(typeCode))
            {
                var q = _context.OutsourceMaterialDocs.AsNoTracking()
                    .Where(d => d.Status == DocStatus.Confirmed
                             && OutsourceOutTypeCodes.Contains(d.TypeCode));
                if (typeCode != null) q = q.Where(d => d.TypeCode == typeCode);
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var kw = search.ToLower();
                    q = q.Where(d => d.OutsourceMaterialDocId.ToLower().Contains(kw)
                                  || d.Lines.Any(l => l.ProductCode.ToLower().Contains(kw)));
                }
                var heads = await q.Select(d => new
                {
                    d.OutsourceMaterialDocId, d.TypeCode, d.IssueDate,
                    Lines = d.Lines.Where(l => l.QtyPutAway < l.StockQty).Select(l => new
                    {
                        l.LineNo, l.ProductCode, l.StockQty, l.QtyPutAway, l.StockUomCode, l.UomCode,
                        l.WhCode, Pending = l.StockQty - l.QtyPutAway,
                    }).ToList(),
                }).ToListAsync();

                rows.AddRange(heads.SelectMany(h => h.Lines.Select(l => (object)new
                {
                    docNo        = h.OutsourceMaterialDocId,
                    typeCode     = h.TypeCode,
                    docDate      = h.IssueDate.ToString("yyyy-MM-dd"),
                    lineNo       = l.LineNo,
                    productCode  = l.ProductCode,
                    stockQty     = l.StockQty,
                    qtyPutAway   = l.QtyPutAway,
                    pending      = l.Pending,
                    stockUomCode = l.StockUomCode,
                    uomCode      = l.UomCode,
                    whCode       = l.WhCode,
                })));
            }

            // ── ReworkMaterialDoc (RI) ──
            if (typeCode == null || ReworkOutTypeCodes.Contains(typeCode))
            {
                var q = _context.ReworkMaterialDocs.AsNoTracking()
                    .Where(d => d.Status == DocStatus.Confirmed
                             && ReworkOutTypeCodes.Contains(d.TypeCode));
                if (typeCode != null) q = q.Where(d => d.TypeCode == typeCode);
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var kw = search.ToLower();
                    q = q.Where(d => d.ReworkMaterialDocId.ToLower().Contains(kw)
                                  || d.Lines.Any(l => l.ProductCode.ToLower().Contains(kw)));
                }
                var heads = await q.Select(d => new
                {
                    d.ReworkMaterialDocId, d.TypeCode, d.IssueDate,
                    Lines = d.Lines.Where(l => l.QtyPutAway < l.StockQty).Select(l => new
                    {
                        l.LineNo, l.ProductCode, l.StockQty, l.QtyPutAway, l.StockUomCode, l.UomCode,
                        l.WhCode, Pending = l.StockQty - l.QtyPutAway,
                    }).ToList(),
                }).ToListAsync();

                rows.AddRange(heads.SelectMany(h => h.Lines.Select(l => (object)new
                {
                    docNo        = h.ReworkMaterialDocId,
                    typeCode     = h.TypeCode,
                    docDate      = h.IssueDate.ToString("yyyy-MM-dd"),
                    lineNo       = l.LineNo,
                    productCode  = l.ProductCode,
                    stockQty     = l.StockQty,
                    qtyPutAway   = l.QtyPutAway,
                    pending      = l.Pending,
                    stockUomCode = l.StockUomCode,
                    uomCode      = l.UomCode,
                    whCode       = l.WhCode,
                })));
            }

            // ── GoodsShipment (GS) ──
            if (typeCode == null || GoodsShipmentTypeCodes.Contains(typeCode))
            {
                var q = _context.GoodsShipments.AsNoTracking()
                    .Where(d => d.Status == DocStatus.Confirmed);
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var kw = search.ToLower();
                    q = q.Where(d => d.GoodsShipmentId.ToLower().Contains(kw)
                                  || d.Lines.Any(l => l.ProductCode.ToLower().Contains(kw)));
                }
                var heads = await q.Select(d => new
                {
                    d.GoodsShipmentId, d.ShipDate,
                    Lines = d.Lines.Where(l => l.QtyPutAway < l.StockQty).Select(l => new
                    {
                        l.LineNo, l.ProductCode, l.StockQty, l.QtyPutAway, l.StockUomCode, l.UomCode,
                        Pending = l.StockQty - l.QtyPutAway,
                    }).ToList(),
                }).ToListAsync();

                rows.AddRange(heads.SelectMany(h => h.Lines.Select(l => (object)new
                {
                    docNo        = h.GoodsShipmentId,
                    typeCode     = "GS",
                    docDate      = h.ShipDate.ToString("yyyy-MM-dd"),
                    lineNo       = l.LineNo,
                    productCode  = l.ProductCode,
                    stockQty     = l.StockQty,
                    qtyPutAway   = l.QtyPutAway,
                    pending      = l.Pending,
                    stockUomCode = l.StockUomCode,
                    uomCode      = l.UomCode,
                    whCode       = (string?)null,
                })));
            }

            return Ok(rows);
        }

        /// <summary>
        /// 掃到的是箱號（container-tracking.md §9 固定 QR）時換成箱子目前綁定的品號＋批號與所在儲位。
        /// 不是箱號、或箱子目前是空箱 → 404，呼叫端照原樣把輸入當批號。數量取庫存帳，與箱子畫面同口徑。
        /// </summary>
        [HttpGet("container-unit/{code}")]
        public async Task<IActionResult> ContainerUnit(string code)
        {
            if (!await _permissionService.CanReadAny(StockLookupKeys)) return Forbid();

            var unit = await _context.ContainerUnits.AsNoTracking()
                .FirstOrDefaultAsync(u => u.ContainerUnitId == code.Trim().ToUpperInvariant());
            if (unit == null || string.IsNullOrEmpty(unit.LotNo)) return NotFound();

            var plantId = _userContext.PlantId ?? "";
            var invs = await _context.Inventories.AsNoTracking()
                .Where(i => i.PlantId == plantId && i.ProductCode == unit.ProductCode && i.LotNo == unit.LotNo && i.QtyOnHand > 0)
                .Select(i => new { i.LocationCode, i.QtyOnHand })
                .ToListAsync();
            if (invs.Count == 0) return NotFound();

            var locs = await _context.StorageLocations.ForCurrentPlant(_context).AsNoTracking()
                .Where(l => invs.Select(x => x.LocationCode).Contains(l.LocationCode))
                .ToDictionaryAsync(l => l.LocationCode, l => l.WhCode);

            return Ok(new
            {
                containerUnitId = unit.ContainerUnitId,
                productCode = unit.ProductCode,
                lotNo = unit.LotNo,
                locations = invs.Select(i => new
                {
                    whCode = locs.TryGetValue(i.LocationCode, out var wh) ? wh : "",
                    locationCode = i.LocationCode,
                    qty = i.QtyOnHand,
                }).OrderBy(l => l.locationCode).ToList(),
            });
        }

        /// <summary>品號各儲位可用庫存量（出庫查詢用）。</summary>
        [HttpGet("stock/{productCode}")]
        public async Task<IActionResult> Stock(string productCode)
        {
            if (!await _permissionService.CanReadAny(StockLookupKeys)) return Forbid();

            var plantId = _userContext.PlantId ?? "";
            var invs = await _context.Inventories.AsNoTracking()
                .Where(i => i.PlantId == plantId && i.ProductCode == productCode && i.QtyOnHand > 0)
                .Select(i => new { i.LocationCode, i.LotNo, i.QtyOnHand, i.QtyReserved })
                .ToListAsync();

            var locs = await _context.StorageLocations.ForCurrentPlant(_context).AsNoTracking()
                .Where(l => invs.Select(x => x.LocationCode).Contains(l.LocationCode))
                .ToDictionaryAsync(l => l.LocationCode, l => l.WhCode);

            var rows = invs.Select(i => new
            {
                locationCode = i.LocationCode,
                whCode = locs.TryGetValue(i.LocationCode, out var wh) ? wh : "",
                lotNo = i.LotNo,
                qtyOnHand = i.QtyOnHand,
                qtyReserved = i.QtyReserved,
                qtyAvailable = i.QtyOnHand - i.QtyReserved,
            }).OrderBy(r => r.whCode).ThenBy(r => r.locationCode).ToList();

            return Ok(rows);
        }

        /// <summary>
        /// 品號在本廠「保留給訂單／工單」的在手量（廠級預留，不落儲位）。
        /// <para>2026-10-19 驗收 X-8：上面 <see cref="Stock"/> 的可用量只扣儲位鎖量，看不到這種保留——
        /// 選擇庫存畫面照樣顯示可用，選了之後出貨才被 <c>EnsureShipmentWithinFreeStockAsync</c> 擋。
        /// 另開端點而不改 <see cref="Stock"/> 的回傳形狀：它是陣列，手機與十幾張單據都直接拿來用。</para>
        /// 訂單需求列（MrpRunLine）回報銷貨訂單號，其餘回報來源單號；口徑與出貨檢查相同（OnHand＋Active）。
        /// </summary>
        [HttpGet("stock-reservations/{productCode}")]
        public async Task<IActionResult> StockReservations(string productCode)
        {
            if (!await _permissionService.CanReadAny(StockLookupKeys)) return Forbid();

            var plantId = _userContext.PlantId ?? "";
            var res = await _context.Set<InventoryReservation>().AsNoTracking()
                .Where(r => r.PlantId == plantId && r.ProductCode == productCode
                         && r.Category == ReservationCategory.OnHand && r.Status == ReservationStatus.Active
                         && r.ReservedQty > 0)
                .Select(r => new { r.SourceType, r.SourceDocId, r.SourceLineSeq, r.ReservedQty })
                .ToListAsync();
            if (res.Count == 0) return Ok(new { total = 0m, sources = Array.Empty<object>() });

            // MRP 需求列的 SourceDocId 是 MrpRunId，使用者認得的是銷貨訂單號
            var runIds = res.Where(r => r.SourceType == ReservationSourceType.MrpRunLine)
                            .Select(r => r.SourceDocId).Distinct().ToList();
            var soByRunLine = runIds.Count == 0
                ? new Dictionary<(string, int), string?>()
                : (await _context.Set<Modules.MFG.Models.MrpRunLine>().AsNoTracking()
                    .Where(l => runIds.Contains(l.MrpRunId))
                    .Select(l => new { l.MrpRunId, l.LineSeq, l.SalesOrderId })
                    .ToListAsync())
                  .ToDictionary(l => (l.MrpRunId, l.LineSeq), l => l.SalesOrderId);

            var sources = res
                .Select(r =>
                {
                    var docId = r.SourceDocId;
                    var type = r.SourceType.ToString();
                    if (r.SourceType == ReservationSourceType.MrpRunLine && r.SourceLineSeq is int seq
                        && soByRunLine.TryGetValue((r.SourceDocId, seq), out var so) && !string.IsNullOrEmpty(so))
                    { docId = so!; type = "SalesOrder"; }
                    return new { type, docId, qty = r.ReservedQty };
                })
                .GroupBy(s => (s.type, s.docId))
                .Select(g => new { type = g.Key.type, docId = g.Key.docId, qty = g.Sum(x => x.qty) })
                .OrderByDescending(s => s.qty)
                .ToList();

            return Ok(new { total = sources.Sum(s => s.qty), sources });
        }

        /// <summary>查詢指定儲位的所有品項庫存（手機庫存查詢 — 依儲位模式）。</summary>
        [HttpGet("stock-by-location/{locationCode}")]
        public async Task<IActionResult> StockByLocation(string locationCode)
        {
            if (!await _permissionService.CanReadAny(StockLookupKeys)) return Forbid();

            var plantId = _userContext.PlantId ?? "";
            var invs = await _context.Inventories.AsNoTracking()
                .Where(i => i.PlantId == plantId && i.LocationCode == locationCode && i.QtyOnHand > 0)
                .Select(i => new { i.ProductCode, i.LotNo, i.QtyOnHand, i.QtyReserved })
                .ToListAsync();

            var productCodes = invs.Select(i => i.ProductCode).Distinct().ToList();
            var products = await _context.Products.AsNoTracking()
                .Where(p => productCodes.Contains(p.ProductCode))
                .Select(p => new { p.ProductCode, p.ProductName, p.StockUom })
                .ToDictionaryAsync(p => p.ProductCode);

            var rows = invs.Select(i => new
            {
                productCode  = i.ProductCode,
                productName  = products.TryGetValue(i.ProductCode, out var p) ? p.ProductName : "",
                uomCode      = products.TryGetValue(i.ProductCode, out var p2) ? p2.StockUom : "",
                lotNo        = i.LotNo,
                qtyOnHand    = i.QtyOnHand,
                qtyReserved  = i.QtyReserved,
                qtyAvailable = i.QtyOnHand - i.QtyReserved,
            }).OrderBy(r => r.productCode).ToList();

            return Ok(rows);
        }

        /// <summary>取得所有啟用中的儲位，可選擇性依倉庫過濾。</summary>
        [HttpGet("locations")]
        public async Task<IActionResult> GetLocations([FromQuery] string? whCode = null)
        {
            if (!await _permissionService.CanReadAny(StockLookupKeys)) return Forbid();

            var q = _context.StorageLocations.ForCurrentPlant(_context).AsNoTracking().Where(l => l.IsActive);
            if (!string.IsNullOrWhiteSpace(whCode))
                q = q.Where(l => l.WhCode == whCode);

            var list = await q.OrderBy(l => l.WhCode).ThenBy(l => l.LocationCode)
                .Select(l => new { whCode = l.WhCode, locationCode = l.LocationCode, locationName = l.LocationName })
                .ToListAsync();

            return Ok(list);
        }

        /// <summary>驗證儲位代號（掃碼後呼叫）。</summary>
        [HttpGet("location/{locationCode}")]
        public async Task<IActionResult> GetLocation(string locationCode)
        {
            if (!await _permissionService.CanReadAny(StockLookupKeys)) return Forbid();

            var loc = await _context.StorageLocations.ForCurrentPlant(_context).AsNoTracking()
                .Where(l => l.LocationCode == locationCode)
                .Select(l => new { l.LocationCode, l.LocationName, l.WhCode })
                .FirstOrDefaultAsync();
            if (loc == null) return NotFound(new { success = false, message = _localizer["LocationNotExist"].Value });

            var wh = await _context.Warehouses.AsNoTracking()
                .Where(w => w.WhCode == loc.WhCode)
                .Select(w => w.WhName)
                .FirstOrDefaultAsync();

            return Ok(new { loc.LocationCode, loc.LocationName, loc.WhCode, WhName = wh ?? "" });
        }

        /// <summary>驗證品號（掃碼後呼叫）。</summary>
        [HttpGet("product/{productCode}")]
        public async Task<IActionResult> GetProduct(string productCode)
        {
            if (!await _permissionService.CanReadAny(StockLookupKeys)) return Forbid();

            var p = await _context.Products.AsNoTracking()
                .Where(p => p.ProductCode == productCode)
                .Select(p => new { p.ProductCode, p.ProductName, p.StockUom })
                .FirstOrDefaultAsync();
            if (p == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });
            return Ok(p);
        }

        /// <summary>取得品號詳細資訊（包含標準成本）。</summary>
        [HttpGet("product-detail")]
        public async Task<IActionResult> GetProductDetail([FromQuery] string productCode)
        {
            // 本支會吐 StandardCost（成本），比其他查詢端點更該擋。
            if (!await _permissionService.CanReadAny(StockLookupKeys)) return Forbid();

            var p = await _context.Products.AsNoTracking()
                .Where(p => p.ProductCode == productCode)
                .Select(p => new { p.ProductCode, p.ProductName, p.StockUom, p.StandardCost })
                .FirstOrDefaultAsync();
            if (p == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });
            return Ok(p);
        }

        /// <summary>取得來源單據尚未沖銷的明細（用於 BR 沖銷 IB 等情境）。</summary>
        [HttpGet("outstanding-source-lines")]
        public async Task<IActionResult> GetOutstandingSourceLines(
            [FromQuery] string sourceTypeCode,
            [FromQuery] string? entityCode = null,
            [FromQuery] string? productCode = null)
        {
            if (!await _permissionService.CanReadAny(StockLookupKeys)) return Forbid();

            var sourceType = await _context.InventoryTransactionTypes
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.TypeCode == sourceTypeCode);
            if (sourceType == null)
                return BadRequest(new { success = false, message = string.Format(_localizer["InventoryTypeCodeNotFound"].Value, sourceTypeCode) });

            var category = sourceType.TypeCategory ?? string.Empty;
            var rows = new List<object>();

            switch (category)
            {
                case "Loan":
                {
                    var q = _context.LoanDocs.AsNoTracking().Include(d => d.Lines)
                        .Where(d => d.Status == DocStatus.Posted && d.TypeCode == sourceTypeCode);
                    if (!string.IsNullOrEmpty(entityCode)) q = q.Where(d => d.EntityCode == entityCode);
                    var loanDocs = await q.ToListAsync();
                    var loanDocIds = loanDocs.Select(d => d.LoanDocId).ToList();
                    var loanPending = loanDocIds.Count == 0 ? new Dictionary<(string, int), decimal>() :
                        (await _context.LoanDocLines.AsNoTracking()
                            .Where(r => r.SourceDocId != null && loanDocIds.Contains(r.SourceDocId)
                                     && r.SourceLineNo != null
                                     && r.LoanDoc!.Status != DocStatus.Posted
                                     && r.LoanDoc!.Status != DocStatus.Void)
                            .GroupBy(r => new { r.SourceDocId, r.SourceLineNo })
                            .Select(g => new { g.Key.SourceDocId, g.Key.SourceLineNo, Qty = g.Sum(r => r.Qty) })
                            .ToListAsync())
                        .ToDictionary(p => (p.SourceDocId!, p.SourceLineNo!.Value), p => p.Qty);
                    foreach (var h in loanDocs)
                        foreach (var l in h.Lines)
                        {
                            if (!string.IsNullOrEmpty(productCode) && l.ProductCode != productCode) continue;
                            loanPending.TryGetValue((h.LoanDocId, l.LineNo), out var loanPendingQty);
                            var remaining = l.Qty - l.QtySourceCleared - loanPendingQty;
                            if (remaining <= 0) continue;
                            rows.Add(new {
                                docNo = h.LoanDocId, lineNo = l.LineNo, typeCode = h.TypeCode,
                                productCode = l.ProductCode, qty = l.Qty, remaining,
                                uomCode = l.UomCode, whCode = l.WhCode, locationCode = l.LocationCode
                            });
                        }
                    break;
                }
                case "Sample":
                {
                    var q = _context.SampleDocs.AsNoTracking().Include(d => d.Lines)
                        .Where(d => d.Status == DocStatus.Posted && d.TypeCode == sourceTypeCode);
                    if (!string.IsNullOrEmpty(entityCode)) q = q.Where(d => d.EntityCode == entityCode);
                    var sampleDocs = await q.ToListAsync();
                    var sampleDocIds = sampleDocs.Select(d => d.SampleDocId).ToList();
                    var samplePending = sampleDocIds.Count == 0 ? new Dictionary<(string, int), decimal>() :
                        (await _context.SampleDocLines.AsNoTracking()
                            .Where(r => r.SourceDocId != null && sampleDocIds.Contains(r.SourceDocId)
                                     && r.SourceLineNo != null
                                     && r.SampleDoc!.Status != DocStatus.Posted
                                     && r.SampleDoc!.Status != DocStatus.Void)
                            .GroupBy(r => new { r.SourceDocId, r.SourceLineNo })
                            .Select(g => new { g.Key.SourceDocId, g.Key.SourceLineNo, Qty = g.Sum(r => r.Qty) })
                            .ToListAsync())
                        .ToDictionary(p => (p.SourceDocId!, p.SourceLineNo!.Value), p => p.Qty);
                    foreach (var h in sampleDocs)
                        foreach (var l in h.Lines)
                        {
                            if (!string.IsNullOrEmpty(productCode) && l.ProductCode != productCode) continue;
                            samplePending.TryGetValue((h.SampleDocId, l.LineNo), out var samplePendingQty);
                            var remaining = l.Qty - l.QtySourceCleared - samplePendingQty;
                            if (remaining <= 0) continue;
                            rows.Add(new {
                                docNo = h.SampleDocId, lineNo = l.LineNo, typeCode = h.TypeCode,
                                productCode = l.ProductCode, qty = l.Qty, remaining,
                                uomCode = l.UomCode, whCode = l.WhCode, locationCode = l.LocationCode
                            });
                        }
                    break;
                }
                case "Purchase":
                {
                    var q = _context.GoodsReceipts.AsNoTracking().Include(d => d.Lines)
                        .Where(d => d.Status == DocStatus.Posted && d.TypeCode == sourceTypeCode);
                    foreach (var h in await q.ToListAsync())
                        foreach (var l in h.Lines.Where(l => l.StockQty > l.QtySourceCleared))
                        {
                            if (!string.IsNullOrEmpty(productCode) && l.ProductCode != productCode) continue;
                            rows.Add(new {
                                docNo = h.GoodsReceiptId, lineNo = l.LineNo, typeCode = h.TypeCode,
                                productCode = l.ProductCode, qty = l.StockQty, remaining = l.StockQty - l.QtySourceCleared,
                                uomCode = l.UomCode, whCode = l.WhCode, locationCode = (string?)null
                            });
                        }
                    break;
                }
                case "Scrap":
                {
                    var q = _context.ScrapDocs.AsNoTracking().Include(d => d.Lines)
                        .Where(d => d.Status == DocStatus.Posted && d.TypeCode == sourceTypeCode);
                    if (!string.IsNullOrEmpty(entityCode)) q = q.Where(d => d.DepartmentCode == entityCode);
                    foreach (var h in await q.ToListAsync())
                        foreach (var l in h.Lines.Where(l => l.Qty > l.QtySourceCleared))
                        {
                            if (!string.IsNullOrEmpty(productCode) && l.ProductCode != productCode) continue;
                            rows.Add(new {
                                docNo = h.ScrapDocId, lineNo = l.LineNo, typeCode = h.TypeCode,
                                productCode = l.ProductCode, qty = l.Qty, remaining = l.Qty - l.QtySourceCleared,
                                uomCode = l.UomCode, whCode = l.WhCode, locationCode = l.LocationCode
                            });
                        }
                    break;
                }
                case "Adjustment":
                {
                    var q = _context.StockAdjustments.AsNoTracking().Include(d => d.Lines)
                        .Where(d => d.Status == DocStatus.Posted && d.TypeCode == sourceTypeCode);
                    if (!string.IsNullOrEmpty(entityCode)) q = q.Where(d => d.DepartmentCode == entityCode);
                    foreach (var h in await q.ToListAsync())
                        foreach (var l in h.Lines.Where(l => l.Qty > l.QtySourceCleared))
                        {
                            if (!string.IsNullOrEmpty(productCode) && l.ProductCode != productCode) continue;
                            rows.Add(new {
                                docNo = h.StockAdjustmentId, lineNo = l.LineNo, typeCode = h.TypeCode,
                                productCode = l.ProductCode, qty = l.Qty, remaining = l.Qty - l.QtySourceCleared,
                                uomCode = l.UomCode, whCode = l.WhCode, locationCode = l.LocationCode
                            });
                        }
                    break;
                }
                case "Outsource":
                {
                    var q = _context.OutsourceMaterialDocs.AsNoTracking().Include(d => d.Lines)
                        .Where(d => d.Status == DocStatus.Posted && d.TypeCode == sourceTypeCode);
                    if (!string.IsNullOrEmpty(entityCode)) q = q.Where(d => d.SupplierCode == entityCode);
                    var outsourceDocs = await q.ToListAsync();
                    var outsourceDocIds = outsourceDocs.Select(d => d.OutsourceMaterialDocId).ToList();
                    var outsourcePending = outsourceDocIds.Count == 0 ? new Dictionary<(string, int), decimal>() :
                        (await _context.OutsourceMaterialDocLines.AsNoTracking()
                            .Where(r => r.SourceDocId != null && outsourceDocIds.Contains(r.SourceDocId)
                                     && r.SourceLineNo != null
                                     && r.OutsourceMaterialDoc!.Status != DocStatus.Posted
                                     && r.OutsourceMaterialDoc!.Status != DocStatus.Void)
                            .GroupBy(r => new { r.SourceDocId, r.SourceLineNo })
                            .Select(g => new { g.Key.SourceDocId, g.Key.SourceLineNo, Qty = g.Sum(r => r.Qty) })
                            .ToListAsync())
                        .ToDictionary(p => (p.SourceDocId!, p.SourceLineNo!.Value), p => p.Qty);
                    foreach (var h in outsourceDocs)
                        foreach (var l in h.Lines)
                        {
                            if (!string.IsNullOrEmpty(productCode) && l.ProductCode != productCode) continue;
                            outsourcePending.TryGetValue((h.OutsourceMaterialDocId, l.LineNo), out var outsourcePendingQty);
                            var remaining = l.Qty - l.QtySourceCleared - outsourcePendingQty;
                            if (remaining <= 0) continue;
                            rows.Add(new {
                                docNo = h.OutsourceMaterialDocId, lineNo = l.LineNo, typeCode = h.TypeCode,
                                productCode = l.ProductCode, qty = l.Qty, remaining,
                                uomCode = l.UomCode, whCode = l.WhCode, locationCode = l.LocationCode
                            });
                        }
                    break;
                }
                case "Rework":
                {
                    var q = _context.ReworkMaterialDocs.AsNoTracking().Include(d => d.Lines)
                        .Where(d => d.Status == DocStatus.Posted && d.TypeCode == sourceTypeCode);
                    if (!string.IsNullOrEmpty(entityCode)) q = q.Where(d => d.WorkCenterCode == entityCode);
                    var reworkDocs = await q.ToListAsync();
                    var reworkDocIds = reworkDocs.Select(d => d.ReworkMaterialDocId).ToList();
                    var reworkPending = reworkDocIds.Count == 0 ? new Dictionary<(string, int), decimal>() :
                        (await _context.ReworkMaterialDocLines.AsNoTracking()
                            .Where(r => r.SourceDocId != null && reworkDocIds.Contains(r.SourceDocId)
                                     && r.SourceLineNo != null
                                     && r.ReworkMaterialDoc!.Status != DocStatus.Posted
                                     && r.ReworkMaterialDoc!.Status != DocStatus.Void)
                            .GroupBy(r => new { r.SourceDocId, r.SourceLineNo })
                            .Select(g => new { g.Key.SourceDocId, g.Key.SourceLineNo, Qty = g.Sum(r => r.Qty) })
                            .ToListAsync())
                        .ToDictionary(p => (p.SourceDocId!, p.SourceLineNo!.Value), p => p.Qty);
                    foreach (var h in reworkDocs)
                        foreach (var l in h.Lines)
                        {
                            if (!string.IsNullOrEmpty(productCode) && l.ProductCode != productCode) continue;
                            reworkPending.TryGetValue((h.ReworkMaterialDocId, l.LineNo), out var reworkPendingQty);
                            var remaining = l.Qty - l.QtySourceCleared - reworkPendingQty;
                            if (remaining <= 0) continue;
                            rows.Add(new {
                                docNo = h.ReworkMaterialDocId, lineNo = l.LineNo, typeCode = h.TypeCode,
                                productCode = l.ProductCode, qty = l.Qty, remaining,
                                uomCode = l.UomCode, whCode = l.WhCode, locationCode = l.LocationCode
                            });
                        }
                    break;
                }
                case "Transfer":
                {
                    var q = _context.StockTransfers.AsNoTracking().Include(d => d.Lines)
                        .Where(d => d.Status == DocStatus.Posted && d.TypeCode == sourceTypeCode);
                    foreach (var h in await q.ToListAsync())
                        foreach (var l in h.Lines.Where(l => l.Qty > l.QtySourceCleared))
                        {
                            if (!string.IsNullOrEmpty(productCode) && l.ProductCode != productCode) continue;
                            rows.Add(new {
                                docNo = h.StockTransferId, lineNo = l.LineNo, typeCode = h.TypeCode,
                                productCode = l.ProductCode, qty = l.Qty, remaining = l.Qty - l.QtySourceCleared,
                                uomCode = l.UomCode, whCode = l.FromWhCode, locationCode = l.FromLocationCode
                            });
                        }
                    break;
                }
            }

            return Ok(rows);
        }

    }
}
