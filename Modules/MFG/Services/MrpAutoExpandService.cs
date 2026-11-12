using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <inheritdoc cref="IMrpAutoExpandService"/>
    public class MrpAutoExpandService : IMrpAutoExpandService
    {
        private const string DocType = "MrpRun";

        private readonly ApplicationDbContext _db;
        private readonly IDateTimeService _dt;
        private readonly IMrpRunExecutionService _execution;
        private readonly IMfgSimplifiedModeService _simplified;
        private readonly IDocNumberService _docNumber;
        private readonly IUserContext _userContext;

        public MrpAutoExpandService(
            ApplicationDbContext db,
            IMrpRunExecutionService execution,
            IMfgSimplifiedModeService simplified,
            IDocNumberService docNumber,
            IUserContext userContext,
            IDateTimeService dt)
        {
            _db = db;
            _dt = dt;
            _execution = execution;
            _simplified = simplified;
            _docNumber = docNumber;
            _userContext = userContext;
        }

        public async Task<MrpAutoExpandResult> ExpandApprovedOrdersAsync(CancellationToken ct = default)
        {
            if (!await _simplified.IsEnabledAsync(_userContext.PlantId, ct))
                return new MrpAutoExpandResult(false, null, 0, null);

            var pending = await LoadPendingOrderLinesAsync(ct);
            if (pending.Count == 0)
                return new MrpAutoExpandResult(false, null, 0, null);

            var plantId = _userContext.PlantId;
            var runId = await _docNumber.NextAsync(DocType, plantId, _dt.GetLocalDate(), ct);
            var now = DateTime.UtcNow;

            var run = new MrpRun
            {
                MrpRunId  = runId,
                Status    = MrpRunStatus.Draft,
                PlantId   = plantId,
                Remarks   = "訂單核准自動展開（製造簡化模式）",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var seq = 0;
            foreach (var line in pending)
            {
                run.Lines.Add(new MrpRunLine
                {
                    MrpRunId          = runId,
                    LineSeq           = ++seq,
                    SalesOrderId      = line.OrderId,
                    SalesOrderLineSeq = line.LineNo,
                    ProductCode       = line.ProductCode,
                    RequiredQty       = line.Remaining,
                    RequiredDate      = line.RequiredDate,
                });
            }

            _db.MrpRuns.Add(run);
            await _db.SaveChangesAsync(ct);

            try
            {
                await _execution.ExecuteAsync(runId, ct);
            }
            catch (Exception ex)
            {
                // 訂單核准本身已成功落地，不可因自動展開失敗而讓呼叫端誤以為沒存檔。
                // 執行單留在 Draft，使用者可到 MRP 執行畫面查看原因並人工執行。
                return new MrpAutoExpandResult(true, runId, seq, InnermostMessage(ex));
            }

            return new MrpAutoExpandResult(true, runId, seq, null);
        }

        private readonly record struct PendingLine(string OrderId, int LineNo, string ProductCode,
            decimal Remaining, DateTime RequiredDate);

        /// <summary>
        /// 已核准訂單中「尚未展開」的剩餘量。
        ///
        /// 口徑刻意與 MRP 執行畫面的「可匯入訂單列」一致：
        /// 剩餘 = 訂單量 − Σ(該訂單明細在已執行 MRP 單的需求量，排除已作廢生產批)。
        /// 兩邊若各算各的，自動展開與人工建單會對同一筆需求給出不同的剩餘量。
        /// </summary>
        private async Task<List<PendingLine>> LoadPendingOrderLinesAsync(CancellationToken ct)
        {
            var executedRunIds = _db.MrpRuns
                .Where(r => r.Status == MrpRunStatus.Executed || r.Status == MrpRunStatus.Archived)
                .Select(r => r.MrpRunId);

            var committed = await _db.MrpRunLines.AsNoTracking()
                .Where(l => l.SalesOrderId != null && executedRunIds.Contains(l.MrpRunId))
                .Where(l => l.ProductionBatchId == null ||
                            !_db.ProductionBatches.Any(b => b.BatchId == l.ProductionBatchId
                                                         && b.Status == ProductionBatchStatus.Void))
                .GroupBy(l => new { l.SalesOrderId, l.SalesOrderLineSeq })
                .Select(g => new { g.Key.SalesOrderId, g.Key.SalesOrderLineSeq, Qty = g.Sum(x => x.RequiredQty) })
                .ToListAsync(ct);
            var committedMap = committed.ToDictionary(x => (x.SalesOrderId!, x.SalesOrderLineSeq ?? 0), x => x.Qty);

            var soLines = await _db.SalesOrderLines.AsNoTracking()
                .Where(sl => !sl.IsClosed && sl.OrderQty > 0)
                .Join(_db.SalesOrders.Where(so => so.ApprovalStatus == DocumentApprovalStatus.Approved),
                      sl => sl.SalesOrderId, so => so.SalesOrderId,
                      (sl, so) => new { OrderId = sl.SalesOrderId, sl.LineNo, sl.ProductCode, sl.OrderQty, sl.DeliveryDate })
                .ToListAsync(ct);

            var ioLines = await _db.InternalOrderLines.AsNoTracking()
                .Where(il => !il.IsClosed && il.OrderQty > 0)
                .Join(_db.InternalOrders.Where(io => io.ApprovalStatus == DocumentApprovalStatus.Approved),
                      il => il.InternalOrderId, io => io.InternalOrderId,
                      (il, io) => new { OrderId = il.InternalOrderId, il.LineNo, il.ProductCode, il.OrderQty, il.DeliveryDate })
                .ToListAsync(ct);

            var today = _dt.GetLocalDate();
            return soLines.Concat(ioLines)
                .Select(x =>
                {
                    committedMap.TryGetValue((x.OrderId, x.LineNo), out var done);
                    // 交期未填時以今天為需求日——寧可算成「立刻要」也不要因為缺日期而整列漏展
                    var due = x.DeliveryDate ?? today;
                    return new PendingLine(x.OrderId, x.LineNo, x.ProductCode, x.OrderQty - done,
                        DateTime.SpecifyKind(due.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc));
                })
                .Where(x => x.Remaining > 0)
                .OrderBy(x => x.RequiredDate).ThenBy(x => x.OrderId).ThenBy(x => x.LineNo)
                .ToList();
        }

        private static string InnermostMessage(Exception ex)
        {
            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            return inner.Message;
        }
    }
}
