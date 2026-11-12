using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    public class WorkCalendarService : IWorkCalendarService
    {
        private readonly ApplicationDbContext _context;

        public WorkCalendarService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<decimal> GetWorkHoursAsync(string? machineCode, string? workCenterId, string? plantId, DateOnly date)
        {
            if (!string.IsNullOrEmpty(machineCode) && !string.IsNullOrEmpty(workCenterId))
            {
                var machineLevel = await _context.Set<WorkCalendar>().FirstOrDefaultAsync(w =>
                    w.PlantId == plantId &&
                    w.WorkCenterId == workCenterId &&
                    w.MachineCode == machineCode &&
                    w.Date == date);

                if (machineLevel != null)
                    return machineLevel.TM_WKS + machineLevel.TM_WKO;
            }

            if (!string.IsNullOrEmpty(workCenterId))
            {
                var wcLevel = await _context.Set<WorkCalendar>().FirstOrDefaultAsync(w =>
                    w.PlantId == plantId &&
                    w.WorkCenterId == workCenterId &&
                    w.MachineCode == null &&
                    w.Date == date);

                if (wcLevel != null)
                    return wcLevel.TM_WKS + wcLevel.TM_WKO;
            }

            var plantLevel = await _context.Set<WorkCalendar>().FirstOrDefaultAsync(w =>
                w.PlantId == plantId &&
                w.WorkCenterId == null &&
                w.MachineCode == null &&
                w.Date == date);

            return plantLevel != null ? plantLevel.TM_WKS + plantLevel.TM_WKO : 0;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// 查詢一律 IgnoreQueryFilters（已以 plantId 明確過濾）：報工後的 ETA 重算在背景 scope 執行，
        /// 沒有登入使用者時全域過濾是 PlantId == null，原本永遠拿到空日曆、退回「直接加分鐘」。
        /// </remarks>
        public async Task<IReadOnlyList<(int Start, int End)>> GetSegmentsAsync(
            string? machineCode, string? workCenterId, string? plantId, DateOnly date)
        {
            IQueryable<WorkCalendarSegment> query;

            // 依優先級：機台 > 工作中心 > 廠別
            if (!string.IsNullOrEmpty(machineCode) && !string.IsNullOrEmpty(workCenterId))
            {
                query = _context.WorkCalendarSegments.IgnoreQueryFilters()
                    .Where(s => s.PlantId == plantId && s.WorkCenterId == workCenterId
                                && s.MachineCode == machineCode && s.Date == date);
                var machineSegs = await query.OrderBy(s => s.Seq)
                    .Select(s => new { s.StartMinute, s.EndMinute })
                    .ToListAsync();
                if (machineSegs.Count > 0)
                    return machineSegs.Select(s => (s.StartMinute, s.EndMinute)).ToList();
            }

            if (!string.IsNullOrEmpty(workCenterId))
            {
                query = _context.WorkCalendarSegments.IgnoreQueryFilters()
                    .Where(s => s.PlantId == plantId && s.WorkCenterId == workCenterId
                                && s.MachineCode == null && s.Date == date);
                var wcSegs = await query.OrderBy(s => s.Seq)
                    .Select(s => new { s.StartMinute, s.EndMinute })
                    .ToListAsync();
                if (wcSegs.Count > 0)
                    return wcSegs.Select(s => (s.StartMinute, s.EndMinute)).ToList();
            }

            // 廠別層
            query = _context.WorkCalendarSegments.IgnoreQueryFilters()
                .Where(s => s.PlantId == plantId && s.WorkCenterId == null
                            && s.MachineCode == null && s.Date == date);
            var plantSegs = await query.OrderBy(s => s.Seq)
                .Select(s => new { s.StartMinute, s.EndMinute })
                .ToListAsync();
            return plantSegs.Select(s => (s.StartMinute, s.EndMinute)).ToList();
        }
    }
}
