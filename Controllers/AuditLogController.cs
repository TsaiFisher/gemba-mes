using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Controllers
{
    [Authorize]
    // 依權限矩陣把關（原本寫死 Admin，選單看得到卻點不進去；見 AdminAccess）。類別層級＝讀取，寫入動作另標
    [RequireMenuPermission("AuditLog")]
    public class AuditLogController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTime;

        public AuditLogController(ApplicationDbContext context, IUserContext userContext,
            IExportService exportService, IDateTimeService dateTime)
        {
            _context = context;
            _userContext = userContext;
            _exportService = exportService;
            _dateTime = dateTime;
        }

        /// <summary>
        /// 起訖日期是使用者當地的日期（畫面上的時間也以當地時間顯示），界線換成 UTC 再比對。
        ///
        /// <para>原本把日期直接當成 UTC 午夜：在台灣（UTC+8）篩「10/21～10/21」實際涵蓋的是
        /// 當地 10/21 08:00 到 10/22 07:59，當天清晨 0～8 點的異動篩不到、隔天清晨的反而混進來，
        /// 而列表上顯示的時間又是對的，看不出漏了。迄日用「隔天 00:00 之前」而不是 23:59:59.9999999，
        /// 免得最後一個 tick 的取捨隨精度而變。</para>
        /// </summary>
        private IQueryable<AuditLog> ApplyDateRange(IQueryable<AuditLog> query, DateTime? startDate, DateTime? endDate)
        {
            if (startDate.HasValue)
            {
                var startUtc = _dateTime.ToUtcTime(DateTime.SpecifyKind(startDate.Value.Date, DateTimeKind.Unspecified));
                query = query.Where(a => a.Timestamp >= startUtc);
            }
            if (endDate.HasValue)
            {
                var endUtcExclusive = _dateTime.ToUtcTime(DateTime.SpecifyKind(endDate.Value.Date.AddDays(1), DateTimeKind.Unspecified));
                query = query.Where(a => a.Timestamp < endUtcExclusive);
            }
            return query;
        }

        // GET: AuditLog
        public async Task<IActionResult> Index(
            string? searchTerm,
            string? entityType,
            string? auditAction,
            DateTime? startDate,
            DateTime? endDate,
            int? pageNumber,
            int? pageSize,
            string? sortOrder = null,
            bool includeNoPlant = false,
            bool searched = false)
        {
            ViewBag.CurrentSort = sortOrder;
            ViewBag.TimestampSort = string.IsNullOrEmpty(sortOrder) || sortOrder == "timestamp_desc" ? "timestamp_asc" : "timestamp_desc";
            ViewBag.UserSort = sortOrder == "user_asc" ? "user_desc" : "user_asc";
            ViewBag.ActionSort = sortOrder == "action_asc" ? "action_desc" : "action_asc";
            ViewBag.EntityTypeSort = sortOrder == "type_asc" ? "type_desc" : "type_asc";
            ViewBag.EntityIdSort = sortOrder == "id_asc" ? "id_desc" : "id_asc";
            ViewBag.IpSort = sortOrder == "ip_asc" ? "ip_desc" : "ip_asc";

            ViewBag.SearchTerm = searchTerm;
            ViewBag.EntityType = entityType;
            ViewBag.Action = auditAction;
            ViewBag.StartDate = startDate;
            ViewBag.EndDate = endDate;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.Searched = searched;
            ViewBag.IncludeNoPlant = includeNoPlant;

            // EntityTypes 下拉：含無廠別選項時也一同列出
            var entityTypesQuery = includeNoPlant
                ? _context.AuditLogs.IgnoreQueryFilters()
                    .Where(a => a.PlantId == _userContext.PlantId || a.PlantId == null)
                : _context.AuditLogs.AsQueryable();
            ViewBag.EntityTypes = await entityTypesQuery
                .Select(a => a.EntityType)
                .Distinct()
                .OrderBy(e => e)
                .ToListAsync();

            if (!searched)
                return View((PaginatedList<AuditLog>?)null);

            var query = includeNoPlant
                ? _context.AuditLogs.IgnoreQueryFilters()
                    .Where(a => a.PlantId == _userContext.PlantId || a.PlantId == null)
                : _context.AuditLogs.AsQueryable();

            // 篩選條件
            if (!string.IsNullOrEmpty(searchTerm))
            {
                searchTerm = searchTerm.ToLower();
                query = query.Where(a =>
                    (a.UserName != null && a.UserName.ToLower().Contains(searchTerm)) ||
                    (a.EntityId != null && a.EntityId.ToLower().Contains(searchTerm)));
            }

            if (!string.IsNullOrEmpty(entityType))
            {
                query = query.Where(a => a.EntityType == entityType);
            }

            if (!string.IsNullOrEmpty(auditAction))
            {
                query = query.Where(a => a.Action == auditAction);
            }

            query = ApplyDateRange(query, startDate, endDate);

            // 排序
            query = sortOrder switch
            {
                "timestamp_asc" => query.OrderBy(a => a.Timestamp),
                "user_asc" => query.OrderBy(a => a.UserName),
                "user_desc" => query.OrderByDescending(a => a.UserName),
                "action_asc" => query.OrderBy(a => a.Action),
                "action_desc" => query.OrderByDescending(a => a.Action),
                "type_asc" => query.OrderBy(a => a.EntityType),
                "type_desc" => query.OrderByDescending(a => a.EntityType),
                "id_asc" => query.OrderBy(a => a.EntityId),
                "id_desc" => query.OrderByDescending(a => a.EntityId),
                "ip_asc" => query.OrderBy(a => a.IpAddress),
                "ip_desc" => query.OrderByDescending(a => a.IpAddress),
                _ => query.OrderByDescending(a => a.Timestamp),
            };


            return View(await PaginatedList<AuditLog>.CreateAsync(query.AsNoTracking(), pageNumber ?? 1, ViewBag.CurrentPageSize));
        }

        // GET: AuditLog/Export
        [HttpGet]
        [RequireMenuPermission("AuditLog", MenuPermissionAction.Print)]
        public async Task<IActionResult> Export(string? searchTerm, string? entityType, string? auditAction,
            DateTime? startDate, DateTime? endDate, string? sortOrder, bool includeNoPlant = false, string format = "xlsx")
        {
            var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
            var query = includeNoPlant
                ? _context.AuditLogs.IgnoreQueryFilters()
                    .Where(a => a.PlantId == _userContext.PlantId || a.PlantId == null)
                : _context.AuditLogs.AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(a =>
                    (a.UserName != null && a.UserName.ToLower().Contains(lower)) ||
                    (a.EntityId != null && a.EntityId.ToLower().Contains(lower)));
            }
            if (!string.IsNullOrEmpty(entityType)) query = query.Where(a => a.EntityType == entityType);
            if (!string.IsNullOrEmpty(auditAction)) query = query.Where(a => a.Action == auditAction);
            query = ApplyDateRange(query, startDate, endDate);

            query = sortOrder switch
            {
                "timestamp_asc" => query.OrderBy(a => a.Timestamp),
                "user_asc"      => query.OrderBy(a => a.UserName),
                "user_desc"     => query.OrderByDescending(a => a.UserName),
                "action_asc"    => query.OrderBy(a => a.Action),
                "action_desc"   => query.OrderByDescending(a => a.Action),
                _               => query.OrderByDescending(a => a.Timestamp),
            };

            var data = await query.AsNoTracking().ToListAsync();
            var now = _dateTime.Now;
            var req = new ExportRequest
            {
                Title = isZh ? "稽核日誌" : "Audit Log",
                Headers = isZh
                    ? ["時間", "使用者", "操作", "物件類型", "物件ID", "IP位址"]
                    : ["Timestamp", "User", "Action", "Entity Type", "Entity ID", "IP Address"],
                Rows = data.Select(a => new object?[]
                {
                    _dateTime.ToLocalTime(a.Timestamp).ToString("yyyy-MM-dd HH:mm:ss"),
                    a.UserName ?? "",
                    a.Action ?? "",
                    a.EntityType ?? "",
                    a.EntityId ?? "",
                    a.IpAddress ?? ""
                }),
            };
            return format switch
            {
                "csv"  => File(_exportService.ToCsv(req), "text/csv", Uri.EscapeDataString($"AuditLog_{now:yyyyMMdd}.csv")),
                "json" => File(_exportService.ToJson(req), "application/json", Uri.EscapeDataString($"AuditLog_{now:yyyyMMdd}.json")),
                _      => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                               Uri.EscapeDataString($"AuditLog_{now:yyyyMMdd_HHmm}.xlsx"))
            };
        }

        // GET: AuditLog/Details/5
        public async Task<IActionResult> Details(string id)
        {
            if (id == null) return NotFound();

            var auditLog = await _context.AuditLogs
                .FirstOrDefaultAsync(m => m.Id == id);

            if (auditLog == null) return NotFound();

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return PartialView("_AuditLogDetails", auditLog);

            return View(auditLog);
        }
    }
}
