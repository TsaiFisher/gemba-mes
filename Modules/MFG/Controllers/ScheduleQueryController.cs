using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.ViewModels;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class ScheduleQueryController : ModuleControllerBase
    {
        private const string MenuKey = "ScheduleQuery";
        private readonly IUomService _uomService;

        public ScheduleQueryController(IUomService uomService)
        {
            _uomService = uomService;
        }

        // GET: /ScheduleQuery
        public async Task<IActionResult> Index(
            string? productCode,
            string? workCenterId,
            string? machineCode,
            string? workOrderId,
            string? batchId,
            string? salesOrderId,
            string? dateFrom,
            string? dateTo,
            string? statusFilter,
            string  sortOrder  = "sched_start",
            bool    searched   = false,
            int     pageNumber = 1,
            int     pageSize   = 20)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            ViewBag.ProductCode   = productCode;
            ViewBag.WorkCenterId  = workCenterId;
            ViewBag.MachineCode   = machineCode;
            ViewBag.WorkOrderId   = workOrderId;
            ViewBag.BatchId       = batchId;
            ViewBag.SalesOrderId  = salesOrderId;
            ViewBag.DateFrom      = dateFrom;
            ViewBag.DateTo        = dateTo;
            ViewBag.StatusFilter  = statusFilter;
            ViewBag.CurrentSort   = sortOrder;
            ViewBag.CurrentPageSize = pageSize;

            // 下拉選單資料
            ViewBag.ProductOptions = await Db.Products
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName })
                .ToListAsync();

            ViewBag.WorkOrderOptions = await Db.WorkOrders
                .AsNoTracking()
                .OrderByDescending(wo => wo.CreatedAt)
                .Select(wo => wo.WorkOrderId)
                .ToListAsync();

            ViewBag.BatchOptions = await Db.ProductionBatches
                .AsNoTracking()
                .OrderByDescending(pb => pb.CreatedAt)
                .Select(pb => pb.BatchId)
                .ToListAsync();

            ViewBag.SalesOrderOptions = await Db.MrpRunLines
                .AsNoTracking()
                .Where(l => l.SalesOrderId != null)
                .Select(l => l.SalesOrderId!)
                .Distinct()
                .OrderBy(id => id)
                .ToListAsync();

            ViewBag.MachineOptions = await Db.WorkCenterMachines
                .AsNoTracking()
                .Where(m => m.WorkCenter!.PlantId == UserContext.PlantId)
                .OrderBy(m => m.MachineCode)
                .Select(m => new { m.MachineCode, m.MachineName })
                .ToListAsync();

            // 工作中心下拉
            ViewBag.WorkCenters = await Db.WorkCenters
                .AsNoTracking()
                .OrderBy(wc => wc.WorkCenterCode)
                .Select(wc => new { wc.WorkCenterCode, wc.WorkCenterName })
                .ToListAsync();

            await PrepareIndexPermissionsAsync(MenuKey);

            if (!searched)
                return View((PaginatedList<ScheduleQueryRowVm>?)null);

            // 基底 Join：Dispatch → WorkOrderOperation → WorkOrder → ProductionBatch
            var q = from d in Db.Dispatches.AsNoTracking()
                    join op in Db.WorkOrderOperations.AsNoTracking()
                        on new { d.WorkOrderId, d.OperationSeq } equals new { op.WorkOrderId, op.OperationSeq }
                        into opj
                    from op in opj.DefaultIfEmpty()
                    join wo in Db.WorkOrders.AsNoTracking()
                        on d.WorkOrderId equals wo.WorkOrderId
                    join pb in Db.ProductionBatches.AsNoTracking()
                        on wo.ProductionBatchId equals pb.BatchId
                    // 左連 MrpRunLine 取銷售訂單號
                    join mrl in Db.MrpRunLines.AsNoTracking()
                        on new { pb.MrpRunId, LineSeq = pb.MrpRunLineSeq } equals new { mrl.MrpRunId, mrl.LineSeq }
                        into mrlj
                    from mrl in mrlj.DefaultIfEmpty()
                    select new ScheduleQueryRowVm
                    {
                        DispatchId        = d.DispatchId,
                        WorkOrderId       = d.WorkOrderId,
                        ProductionBatchId = wo.ProductionBatchId,
                        ProductCode       = wo.ProductCode,
                        OperationSeq      = d.OperationSeq,
                        OperationName     = op != null ? op.OperationName ?? "" : "",
                        WorkCenterId      = op != null ? op.WorkCenterId : "",
                        MachineCode       = d.MachineCode,
                        ScheduledStart    = d.ScheduledStart,
                        ScheduledEnd      = d.ScheduledEnd,
                        Status            = d.Status,
                        MaterialRisk      = d.MaterialRisk,
                        ToolingCode       = d.ToolingCode,
                        IsPredictedLate   = pb.IsPredictedLate,
                        SalesOrderId      = mrl != null ? mrl.SalesOrderId : null,
                        Quantity          = d.Quantity,
                        QtyDone           = d.QtyDone,
                    };

            // 篩選
            if (!string.IsNullOrWhiteSpace(productCode))
                q = q.Where(v => v.ProductCode == productCode);

            if (!string.IsNullOrWhiteSpace(workCenterId))
                q = q.Where(v => v.WorkCenterId == workCenterId);

            if (!string.IsNullOrWhiteSpace(machineCode))
                q = q.Where(v => v.MachineCode == machineCode);

            if (!string.IsNullOrWhiteSpace(workOrderId))
                q = q.Where(v => v.WorkOrderId == workOrderId);

            if (!string.IsNullOrWhiteSpace(batchId))
                q = q.Where(v => v.ProductionBatchId == batchId);

            if (!string.IsNullOrWhiteSpace(salesOrderId))
                q = q.Where(v => v.SalesOrderId == salesOrderId);

            if (!string.IsNullOrWhiteSpace(statusFilter) && Enum.TryParse<DispatchStatus>(statusFilter, out var st))
                q = q.Where(v => v.Status == st);

            if (!string.IsNullOrWhiteSpace(dateFrom) && DateOnly.TryParse(dateFrom, out var dfDate))
            {
                var dfUtc = DateTime.SpecifyKind(dfDate.ToDateTime(new TimeOnly(0, 0)), DateTimeKind.Utc);
                q = q.Where(v => v.ScheduledStart >= dfUtc);
            }

            if (!string.IsNullOrWhiteSpace(dateTo) && DateOnly.TryParse(dateTo, out var dtDate))
            {
                var dtUtc = DateTime.SpecifyKind(dtDate.ToDateTime(new TimeOnly(23, 59)), DateTimeKind.Utc);
                q = q.Where(v => v.ScheduledStart <= dtUtc);
            }

            // 排序
            q = sortOrder switch
            {
                "sched_start_desc" => q.OrderByDescending(v => v.ScheduledStart),
                "work_order"       => q.OrderBy(v => v.WorkOrderId),
                "work_order_desc"  => q.OrderByDescending(v => v.WorkOrderId),
                "batch"            => q.OrderBy(v => v.ProductionBatchId),
                "batch_desc"       => q.OrderByDescending(v => v.ProductionBatchId),
                "machine"          => q.OrderBy(v => v.MachineCode),
                "machine_desc"     => q.OrderByDescending(v => v.MachineCode),
                _                  => q.OrderBy(v => v.ScheduledStart)
            };

            var list = await PaginatedList<ScheduleQueryRowVm>.CreateAsync(q, pageNumber, pageSize);
            return View(list);
        }

        // GET: /ScheduleQuery/Details/{id}  (AJAX side panel)
        [HttpGet]
        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id)) return Forbid();
            var vm = await (
                from d in Db.Dispatches.AsNoTracking()
                join op in Db.WorkOrderOperations.AsNoTracking()
                    on new { d.WorkOrderId, d.OperationSeq } equals new { op.WorkOrderId, op.OperationSeq }
                    into opj
                from op in opj.DefaultIfEmpty()
                join wo in Db.WorkOrders.AsNoTracking()
                    on d.WorkOrderId equals wo.WorkOrderId
                join pb in Db.ProductionBatches.AsNoTracking()
                    on wo.ProductionBatchId equals pb.BatchId
                join mrl in Db.MrpRunLines.AsNoTracking()
                    on new { pb.MrpRunId, LineSeq = pb.MrpRunLineSeq } equals new { mrl.MrpRunId, mrl.LineSeq }
                    into mrlj
                from mrl in mrlj.DefaultIfEmpty()
                where d.DispatchId == id
                select new ScheduleQueryRowVm
                {
                    DispatchId        = d.DispatchId,
                    WorkOrderId       = d.WorkOrderId,
                    ProductionBatchId = wo.ProductionBatchId,
                    ProductCode       = wo.ProductCode,
                    UomCode           = Db.Products.Where(p => p.ProductCode == wo.ProductCode).Select(p => p.StockUom).FirstOrDefault(),
                    OperationSeq      = d.OperationSeq,
                    OperationName     = op != null ? op.OperationName : "",
                    WorkCenterId      = op != null ? op.WorkCenterId : "",
                    MachineCode       = d.MachineCode,
                    ScheduledStart    = d.ScheduledStart,
                    ScheduledEnd      = d.ScheduledEnd,
                    Status            = d.Status,
                    MaterialRisk      = d.MaterialRisk,
                    ToolingCode       = d.ToolingCode,
                    IsPredictedLate   = pb.IsPredictedLate,
                    SalesOrderId      = mrl != null ? mrl.SalesOrderId : null,
                    Quantity          = d.Quantity,
                    QtyDone           = d.QtyDone,
                }
            ).FirstOrDefaultAsync();

            if (vm == null) return NotFound();
            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();
            return PartialView("_Details", vm);
        }
    }
}
