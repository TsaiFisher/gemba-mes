using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV.Controllers
{
    /// <summary>
    /// 庫存異動帳本（唯讀）。指定單一品號 + 期間，呈現期初結存、逐筆入/出庫與逐筆結存，
    /// 可選儲位 / 批號縮小範圍。資料來源為不可變的 StockMovement（庫存單位）。
    /// </summary>
    [Authorize]
    public class StockLedgerController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IExportService _exportService;

        private const string MenuKey = "StockLedger";

        /// <summary>明細列載入安全上限：防止單品號長期間無上限 ToListAsync 造成 OOM。
        /// 彙總（期初/期末/入出合計）改由 SQL 聚合計算，不受此截斷影響。</summary>
        private const int MaxLedgerRows = 5000;

        public StockLedgerController(
            ApplicationDbContext context,
            IUserContext userContext,
            IPermissionService permissionService,
            IExportService exportService)
        {
            _context = context;
            _userContext = userContext;
            _permissionService = permissionService;
            _exportService = exportService;
        }

        public async Task<IActionResult> Index(
            string? productCode = null,
            DateOnly? dateFrom = null,
            DateOnly? dateTo = null,
            string? locationCode = null,
            string? lotNo = null,
            bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.ProductCode  = productCode;
            ViewBag.DateFrom     = dateFrom;
            ViewBag.DateTo       = dateTo;
            ViewBag.LocationCode = locationCode;
            ViewBag.LotNo        = lotNo;
            ViewBag.Searched     = searched;
            ViewBag.CanPrint     = await _permissionService.CanPrint(MenuKey);

            ViewBag.ProductOptions = await _context.Products
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName })
                .ToListAsync();

            ViewBag.LocationOptions = await _context.StorageLocations
                .ForCurrentPlant(_context)
                .AsNoTracking()
                .Where(l => l.IsActive)
                .OrderBy(l => l.WhCode).ThenBy(l => l.LocationCode)
                .Select(l => new { l.LocationCode, l.LocationName, l.WhCode })
                .ToListAsync();

            if (!searched || string.IsNullOrWhiteSpace(productCode))
                return View((StockLedgerVm?)null);

            var vm = await BuildLedgerAsync(productCode, dateFrom, dateTo, locationCode, lotNo);
            return View(vm);
        }

        public async Task<IActionResult> Export(
            string format = "xlsx",
            string? productCode = null,
            DateOnly? dateFrom = null,
            DateOnly? dateTo = null,
            string? locationCode = null,
            string? lotNo = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            if (string.IsNullOrWhiteSpace(productCode)) return BadRequest();

            var vm = await BuildLedgerAsync(productCode, dateFrom, dateTo, locationCode, lotNo);

            var headers = PzText.Get("Inv_ExportHeaders_Ledger").Split('|');
            var body = new List<object?[]>
            {
                new object?[] { "", PzText.Get("Inv_Ledger_Opening"), "", "", "", "", vm.OpeningBalance, "", "" }
            };
            body.AddRange(vm.Rows.Select(r => new object?[]
            {
                r.MovementDate.ToString("yyyy-MM-dd"),
                r.Summary, r.DocNo, r.SourceDocNo,
                r.InQty == 0 ? null : r.InQty,
                r.OutQty == 0 ? null : r.OutQty,
                r.Balance, r.Handler, r.Remark,
            }));
            body.Add(new object?[] { "", PzText.Get("Inv_Ledger_Closing"), "", "", vm.TotalIn, vm.TotalOut, vm.ClosingBalance, "", "" });

            var req = new ExportRequest { Headers = headers, Rows = body, Title = $"StockLedger_{productCode}" };
            return format?.ToLowerInvariant() switch
            {
                "csv"  => File(_exportService.ToCsv(req),  "text/csv",         $"StockLedger_{productCode}.csv"),
                "json" => File(_exportService.ToJson(req), "application/json", $"StockLedger_{productCode}.json"),
                _      => File(_exportService.ToExcel(req),
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    $"StockLedger_{productCode}.xlsx"),
            };
        }

        public async Task<IActionResult> Print(
            string? productCode = null,
            DateOnly? dateFrom = null,
            DateOnly? dateTo = null,
            string? locationCode = null,
            string? lotNo = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            if (string.IsNullOrWhiteSpace(productCode)) return BadRequest();

            var vm = await BuildLedgerAsync(productCode, dateFrom, dateTo, locationCode, lotNo);
            var qr = Url.Action("Index", null,
                new { productCode, dateFrom, dateTo, locationCode, lotNo, searched = true }, Request.Scheme)!;
            await PreparePrintViewBagAsync(qr);
            return View(vm);
        }

        /// <summary>建立帳本：期初結存 + 期間逐筆入/出/結存。依廠別（全域過濾器）+ 品號 +（可選）儲位/批號。</summary>
        private async Task<StockLedgerVm> BuildLedgerAsync(
            string productCode, DateOnly? dateFrom, DateOnly? dateTo, string? locationCode, string? lotNo)
        {
            var product = await _context.Products.AsNoTracking()
                .FirstOrDefaultAsync(p => p.ProductCode == productCode);

            var vm = new StockLedgerVm
            {
                ProductCode  = productCode,
                ProductName  = product?.ProductName ?? "",
                StockUomCode = product?.StockUom ?? "",
                DateFrom     = dateFrom,
                DateTo       = dateTo,
                LocationCode = locationCode,
                LotNo        = lotNo,
            };

            // 共用篩選（廠別由 Global Query Filter 自動套用）
            var baseQuery = _context.StockMovements.AsNoTracking()
                .Where(m => m.ProductCode == productCode);
            if (!string.IsNullOrWhiteSpace(locationCode))
                baseQuery = baseQuery.Where(m => m.LocationCode == locationCode);
            if (!string.IsNullOrWhiteSpace(lotNo))
                baseQuery = baseQuery.Where(m => m.LotNo == lotNo);

            // 期初結存：起始日之前的累計（Σ StockQty × Direction）
            if (dateFrom.HasValue)
            {
                vm.OpeningBalance = await baseQuery
                    .Where(m => m.MovementDate < dateFrom.Value)
                    .SumAsync(m => (decimal?)(m.StockQty * m.Direction)) ?? 0m;
            }

            // 期間內逐筆
            var periodQuery = baseQuery;
            if (dateFrom.HasValue) periodQuery = periodQuery.Where(m => m.MovementDate >= dateFrom.Value);
            if (dateTo.HasValue)   periodQuery = periodQuery.Where(m => m.MovementDate <= dateTo.Value);

            // 彙總（入/出合計、期末結存）一律以 SQL 聚合計算，與明細列載入解耦，
            // 即使明細因上限被截斷，頁尾數字仍為全期間正確值。
            vm.TotalIn  = await periodQuery.Where(m => m.Direction >= 0).SumAsync(m => (decimal?)m.StockQty) ?? 0m;
            vm.TotalOut = await periodQuery.Where(m => m.Direction <  0).SumAsync(m => (decimal?)m.StockQty) ?? 0m;
            var periodNet = await periodQuery.SumAsync(m => (decimal?)(m.StockQty * m.Direction)) ?? 0m;
            vm.ClosingBalance = vm.OpeningBalance + periodNet;

            // 明細列：載入至上限 + 1 以偵測截斷，避免無上限載入造成 OOM。
            vm.RowCap = MaxLedgerRows;
            var raw = await periodQuery
                .OrderBy(m => m.MovementDate).ThenBy(m => m.CreatedAt)
                .Select(m => new
                {
                    m.MovementDate, m.TypeCode, m.DocNo, m.SourceDocNo,
                    m.StockQty, m.Direction, m.CreatedBy, m.Remark,
                })
                .Take(MaxLedgerRows + 1)
                .ToListAsync();
            if (raw.Count > MaxLedgerRows)
            {
                vm.Truncated = true;
                raw = raw.Take(MaxLedgerRows).ToList();
            }

            var typeMap = await _context.InventoryTransactionTypes.AsNoTracking()
                .Where(t => raw.Select(r => r.TypeCode).Contains(t.TypeCode))
                .ToDictionaryAsync(t => t.TypeCode, t => t.TypeName);

            var userIds = raw.Where(r => !string.IsNullOrEmpty(r.CreatedBy)).Select(r => r.CreatedBy!).Distinct().ToList();
            var userMap = userIds.Count == 0
                ? new Dictionary<string, string>()
                : await _context.Users.AsNoTracking()
                    .Where(u => userIds.Contains(u.Id))
                    .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);

            var running = vm.OpeningBalance;
            foreach (var r in raw)
            {
                var inQty  = r.Direction >= 0 ? r.StockQty : 0m;
                var outQty = r.Direction <  0 ? r.StockQty : 0m;
                running += r.StockQty * r.Direction;
                vm.Rows.Add(new StockLedgerRowVm
                {
                    MovementDate = r.MovementDate,
                    TypeCode     = r.TypeCode,
                    Summary      = typeMap.TryGetValue(r.TypeCode, out var tn) ? tn : r.TypeCode,
                    DocNo        = r.DocNo,
                    SourceDocNo  = r.SourceDocNo,
                    InQty        = inQty,
                    OutQty       = outQty,
                    Balance      = running,
                    Handler      = r.CreatedBy != null && userMap.TryGetValue(r.CreatedBy, out var un) ? un : r.CreatedBy,
                    Remark       = r.Remark,
                });
            }

            return vm;
        }
    }
}
