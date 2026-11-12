using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>工單實際費率期末重估實作。
    ///
    /// 分子 = 各工作中心當月實際費用（由 IDepartmentActualCostProvider 提供，月結手動輸入並持久化）。
    /// 分母 = 各工作中心當月實際報工工時（由 ProductionSession / ProductionSetup 還原，工時未持久化故以 StartAt/EndAt 重建）。
    ///   - 人工工時池 = Σ 報工 session 工時
    ///   - 製費工時池 = Σ 報工 session 工時 + Σ setup 工時（與 DispatchController 累計口徑一致：setup 僅入製費）
    /// 實際人工費率 = 部門實際人工 ÷ 人工工時池；實際製費率 = 部門實際製費 ÷ 製費工時池。
    ///
    /// 回刷：
    ///   - 完工工單（WorkOrderActualCost Preliminary）→ 以實際費率重算 ActualLabor/ActualOverhead 與差異，翻 Final。
    ///   - 在製工單（WorkOrderStatus.InProgress）→ 以實際費率重估 WipLabor/WipOverhead，連同已實現 WipMaterial 寫 WipSnapshot。
    /// 調整分錄：彙總完工工單人工/製費調整淨額，產生 Adjustment 型分錄（Closing 期間唯一可入帳型別）。
    /// 材料不適用費率重估（無「費率」可重估），WipMaterial 取本工單**已過帳發料淨值**（累計，不切期間）。
    ///
    /// 跨期 WIP 人工/製費結轉（reconciliation-decisions.md D4，期初快照結轉法）：
    ///   本期人工/製費 = 前期 WipSnapshot 凍結層（前期實際費率，前期已關帳不可重述）
    ///                 + 本期工時 × 本期實際費率。
    ///   WIP 快照腿與完工回刷腿一致套用——跨期完工工單的 Final 成本因此涵蓋全生命週期，
    ///   而非只剩完工當期工時（否則前期已投人工在 Preliminary→Final 時被沖掉，Final 低估）。
    ///   材料無此問題（發料淨值本為跨期累計），不經快照結轉。
    ///   無前期快照的工單期初 = 0（本期才投產者天然正確；跨期而缺快照者由
    ///   §3.10 WIP_SNAPSHOT_MISSING 於前期關帳時擋下）。
    /// </summary>
    public class ActualRateReestimationService : IActualRateReestimationService
    {
        private readonly ApplicationDbContext _db;
        private readonly IAccountingPeriodService _periods;
        private readonly IDepartmentActualCostProvider _deptCosts;
        private readonly IJournalEntryService _journal;
        private readonly IWorkOrderIssuedMaterialCostReader _issuedMaterial;

        public ActualRateReestimationService(
            ApplicationDbContext db,
            IAccountingPeriodService periods,
            IDepartmentActualCostProvider deptCosts,
            IJournalEntryService journal,
            IWorkOrderIssuedMaterialCostReader issuedMaterial)
        {
            _db = db;
            _periods = periods;
            _deptCosts = deptCosts;
            _journal = journal;
            _issuedMaterial = issuedMaterial;
        }

        public async Task<ActualRateReestimationResult> RunAsync(
            string periodId, string plantId, CancellationToken ct = default)
        {
            var period = await _periods.GetByIdAsync(periodId, plantId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Act_AccountingPeriodNotFoundFmt", plantId, periodId));
            if (period.Status == PeriodStatus.Closed)
                throw new InvalidOperationException(PzText.Get("Mfg_PeriodClosedNoReestimateFmt", plantId, periodId));

            var startDt = period.StartDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var endDt = period.EndDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);

            // ── 1. 分子：各工作中心當月實際費用 ─────────────────────
            var deptCosts = await _deptCosts.GetAsync(periodId, plantId, ct);

            // ── 2. 還原工時並對應工作中心 ───────────────────────────
            var sessions = await _db.ProductionSessions.IgnoreQueryFilters()
                .Where(s => s.PlantId == plantId && s.EndAt != null
                         && s.EndAt >= startDt && s.EndAt <= endDt)
                .Select(s => new { s.DispatchId, s.WorkOrderId, s.StartAt, s.EndAt })
                .ToListAsync(ct);

            var setups = await _db.ProductionSetups.IgnoreQueryFilters()
                .Where(s => s.PlantId == plantId && s.SetupEndAt != null
                         && s.SetupEndAt >= startDt && s.SetupEndAt <= endDt)
                .Select(s => new { s.DispatchId, s.SetupStartAt, s.SetupEndAt })
                .ToListAsync(ct);

            var dispatchIds = sessions.Select(s => s.DispatchId)
                .Concat(setups.Select(s => s.DispatchId)).Distinct().ToList();

            var dispatches = await _db.Dispatches.IgnoreQueryFilters()
                .Where(d => dispatchIds.Contains(d.DispatchId))
                .Select(d => new { d.DispatchId, d.WorkOrderId, d.OperationSeq })
                .ToListAsync(ct);
            var dispatchMap = dispatches.ToDictionary(d => d.DispatchId);

            var woIds = dispatches.Select(d => d.WorkOrderId).Distinct().ToList();
            var ops = await _db.WorkOrderOperations.IgnoreQueryFilters()
                .Where(o => woIds.Contains(o.WorkOrderId))
                .Select(o => new { o.WorkOrderId, o.OperationSeq, o.WorkCenterId })
                .ToListAsync(ct);
            var opMap = ops.ToDictionary(o => (o.WorkOrderId, o.OperationSeq), o => o.WorkCenterId);

            string? WcOf(string dispatchId)
            {
                if (!dispatchMap.TryGetValue(dispatchId, out var d)) return null;
                return opMap.TryGetValue((d.WorkOrderId, d.OperationSeq), out var wc) ? wc : null;
            }

            // 工時記錄：labor = session；overhead = session + setup。鍵 (workOrderId, workCenterCode)
            var laborHours = new Dictionary<(string Wo, string Wc), decimal>();
            var overheadHours = new Dictionary<(string Wo, string Wc), decimal>();

            void Add(Dictionary<(string, string), decimal> map, string wo, string wc, decimal h)
            {
                var k = (wo, wc);
                map[k] = (map.TryGetValue(k, out var v) ? v : 0m) + h;
            }

            foreach (var s in sessions)
            {
                var wc = WcOf(s.DispatchId);
                if (wc == null) continue;
                var h = (decimal)(s.EndAt!.Value - s.StartAt).TotalHours;
                if (h <= 0) continue;
                Add(laborHours, s.WorkOrderId, wc, h);
                Add(overheadHours, s.WorkOrderId, wc, h);
            }
            foreach (var s in setups)
            {
                if (!dispatchMap.TryGetValue(s.DispatchId, out var d)) continue;
                var wc = WcOf(s.DispatchId);
                if (wc == null) continue;
                var h = (decimal)(s.SetupEndAt!.Value - s.SetupStartAt).TotalHours;
                if (h <= 0) continue;
                Add(overheadHours, d.WorkOrderId, wc, h);
            }

            // ── 3. 實際費率（部門費用 ÷ 工時池）─────────────────────
            var laborPool = laborHours.GroupBy(kv => kv.Key.Wc)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Value));
            var overheadPool = overheadHours.GroupBy(kv => kv.Key.Wc)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Value));

            // 有工時卻沒輸入部門費用的工作中心：費率會算成 0，回刷把該中心的人工/製費整筆沖掉、
            // 調整分錄跟著開出去，而且沒有任何訊號——漏打一行就等於宣告那個中心本月零成本。
            // 真的是零成本就明確輸入 0（有輸入的鍵就放行）。
            var missing = overheadPool.Where(kv => kv.Value > 0 && !deptCosts.ContainsKey(kv.Key))
                .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    PzText.Get("Mfg_MissingDeptCostFmt", string.Join("、", missing)));

            decimal LaborRate(string wc)
                => deptCosts.TryGetValue(wc, out var c) && laborPool.TryGetValue(wc, out var p) && p > 0
                    ? c.ActualLaborCost / p : 0m;
            decimal OverheadRate(string wc)
                => deptCosts.TryGetValue(wc, out var c) && overheadPool.TryGetValue(wc, out var p) && p > 0
                    ? c.ActualOverheadCost / p : 0m;

            decimal WoActualLabor(string wo) => Math.Round(
                laborHours.Where(kv => kv.Key.Wo == wo).Sum(kv => kv.Value * LaborRate(kv.Key.Wc)), 4);
            decimal WoActualOverhead(string wo) => Math.Round(
                overheadHours.Where(kv => kv.Key.Wo == wo).Sum(kv => kv.Value * OverheadRate(kv.Key.Wc)), 4);

            // ── 3b. 期初層：前期 WipSnapshot 凍結的人工/製費（D4 跨期結轉）──
            // 前期已關帳、快照不可變，作為跨期工單的期初層；無前期快照者期初 = 0。
            var priorPeriodId = period.Month == 1
                ? $"{period.Year - 1:D4}12"
                : $"{period.Year:D4}{period.Month - 1:D2}";
            var priorWip = await _db.WipSnapshots.IgnoreQueryFilters().AsNoTracking()
                .Where(w => w.PeriodId == priorPeriodId && w.PlantId == plantId)
                .Select(w => new { w.WorkOrderId, w.WipLabor, w.WipOverhead })
                .ToListAsync(ct);
            var priorLabor = priorWip.ToDictionary(w => w.WorkOrderId, w => w.WipLabor);
            var priorOverhead = priorWip.ToDictionary(w => w.WorkOrderId, w => w.WipOverhead);

            decimal WoTotalLabor(string wo)
                => Math.Round(priorLabor.GetValueOrDefault(wo) + WoActualLabor(wo), 4);
            decimal WoTotalOverhead(string wo)
                => Math.Round(priorOverhead.GetValueOrDefault(wo) + WoActualOverhead(wo), 4);

            // ── 4. 回刷完工工單（Preliminary→Final）─────────────────
            // 刻意**不**篩 Status。原本只撈 Preliminary，於是重估對順序極度敏感：
            // 月結步驟 4 的「完成」呼叫的是 WorkOrderCostFinalizationService.FinalizeAsync，
            // 那支只把 Preliminary 翻成 Final、不重估也不產生任何分錄；先按完成再跑重估，
            // 這裡就一筆都撈不到，回傳 0 筆、沒有調整分錄、**完全沒有訊號**——
            // 工單成本從此停在標準費率算出來的值，實際費率的差額永遠不入帳。
            // 而月結畫面上步驟 4 根本沒有執行重估的按鈕（只有步驟 6/7 有），
            // 所以「先鎖定再重估」不是使用者可能弄錯，而是正常操作路徑的唯一走法。
            //
            // 改以「本期的成本記錄」為範圍是冪等的：差額取「新值 − 現值」，
            // 已經重估過的記錄第二次算出來就是 0，不會重複回刷也不會重複產生分錄
            // （見 Reestimation_RunTwice_ProducesNoSecondAdjustment）。
            var costs = await _db.WorkOrderActualCosts.IgnoreQueryFilters()
                .Where(c => c.PlantId == plantId
                         && c.CostDate >= period.StartDate
                         && c.CostDate <= period.EndDate)
                .ToListAsync(ct);

            decimal laborAdj = 0m, overheadAdj = 0m;
            // §9 P-2（D2-A）：逐工單記錄調整額，供分錄逐工單拆行（SourceRef=工單號）
            var woAdjustments = new List<(string WorkOrderId, decimal LaborDelta, decimal OverheadDelta)>();
            foreach (var c in costs)
            {
                // D4：期初凍結層＋本期層——跨期完工工單的 Final 涵蓋全生命週期人工/製費
                var newLabor = WoTotalLabor(c.WorkOrderId);
                var newOverhead = WoTotalOverhead(c.WorkOrderId);
                var laborDelta = Math.Round(newLabor - c.ActualLabor, 4);
                var overheadDelta = Math.Round(newOverhead - c.ActualOverhead, 4);
                laborAdj += laborDelta;
                overheadAdj += overheadDelta;
                if (laborDelta != 0m || overheadDelta != 0m)
                    woAdjustments.Add((c.WorkOrderId, laborDelta, overheadDelta));

                c.ActualLabor = newLabor;
                c.ActualOverhead = newOverhead;
                c.VarLaborEfficiency = newLabor - c.StandardLabor;
                c.VarOverheadRate = newOverhead - c.StandardOverhead;
                c.Status = CostStatus.Final;
            }
            laborAdj = Math.Round(laborAdj, 4);
            overheadAdj = Math.Round(overheadAdj, 4);

            // ── 5. 在製品 WIP 快照（以實際費率重估，刪舊重建）────────
            var oldWip = await _db.WipSnapshots.IgnoreQueryFilters()
                .Where(w => w.PeriodId == periodId && w.PlantId == plantId)
                .ToListAsync(ct);
            _db.WipSnapshots.RemoveRange(oldWip);

            var wipWos = await _db.WorkOrders.IgnoreQueryFilters()
                .Where(w => w.PlantId == plantId && w.Status == WorkOrderStatus.InProgress)
                .Select(w => new { w.WorkOrderId, w.Quantity })
                .ToListAsync(ct);

            var wipWoIds = wipWos.Select(w => w.WorkOrderId).ToList();
            var wipMaterialByWo = await _issuedMaterial.IssuedNetCostByWorkOrderAsync(wipWoIds, ct);
            var goodQty = await _db.ProductionReports.IgnoreQueryFilters()
                .Where(r => wipWoIds.Contains(r.WorkOrderId))
                .GroupBy(r => r.WorkOrderId)
                .Select(g => new { WorkOrderId = g.Key, Qty = g.Sum(x => x.QtyOk) })
                .ToListAsync(ct);
            var goodQtyMap = goodQty.ToDictionary(x => x.WorkOrderId, x => x.Qty);

            int wipCount = 0;
            foreach (var w in wipWos)
            {
                // D4：期初凍結層＋本期層（本期無活動的跨期在製工單 → 原值結轉）
                var wipLabor = WoTotalLabor(w.WorkOrderId);
                var wipOverhead = WoTotalOverhead(w.WorkOrderId);
                var completed = goodQtyMap.TryGetValue(w.WorkOrderId, out var q) ? q : 0m;

                _db.WipSnapshots.Add(new WipSnapshot
                {
                    PeriodId = periodId,
                    WorkOrderId = w.WorkOrderId,
                    WipMaterial = wipMaterialByWo.GetValueOrDefault(w.WorkOrderId),
                    WipLabor = wipLabor,
                    WipOverhead = wipOverhead,
                    QtyCompleted = completed,
                    QtyInProcess = Math.Max(w.Quantity - completed, 0m),
                    SnapshotAt = DateTime.UtcNow,
                    PlantId = plantId,
                });
                wipCount++;
            }

            // ── 6. 差異調整分錄（Adjustment 型；金額為零則略過）──────
            // §9 P-2（D2-A）：逐工單拆行、行帶 SourceRef=工單號——表頭 SourceDocId 為期間 ID，
            // 工單維度由行級補足，§3.11 稽核據此核 Final 工單的人工/製費腿。
            string? journalId = null;
            var lines = new List<JournalLineRequest>();
            foreach (var (woId, laborDelta, overheadDelta) in woAdjustments)
            {
                if (laborDelta != 0m)
                    lines.Add(laborDelta > 0
                        ? new(StandardAccounts.DirectLaborAbsorbedCode, StandardAccounts.DirectLaborAbsorbedName, 0m, laborDelta, SourceRef: woId)
                        : new(StandardAccounts.DirectLaborAbsorbedCode, StandardAccounts.DirectLaborAbsorbedName, -laborDelta, 0m, SourceRef: woId));
                if (overheadDelta != 0m)
                    lines.Add(overheadDelta > 0
                        ? new(StandardAccounts.MfgOverheadAbsorbedCode, StandardAccounts.MfgOverheadAbsorbedName, 0m, overheadDelta, SourceRef: woId)
                        : new(StandardAccounts.MfgOverheadAbsorbedCode, StandardAccounts.MfgOverheadAbsorbedName, -overheadDelta, 0m, SourceRef: woId));
                var woNet = laborDelta + overheadDelta;
                if (woNet != 0m)
                    lines.Add(woNet > 0
                        ? new(StandardAccounts.MfgCostVarianceCode, StandardAccounts.MfgCostVarianceName, woNet, 0m, SourceRef: woId)
                        : new(StandardAccounts.MfgCostVarianceCode, StandardAccounts.MfgCostVarianceName, 0m, -woNet, SourceRef: woId));
            }

            await _db.SaveChangesAsync(ct);

            if (lines.Count > 0)
            {
                var entry = await _journal.CreateAdjustmentEntryAsync(
                    "ActualRateReest", periodId, period.EndDate, plantId, lines,
                    $"工單實際費率期末重估 {periodId}", ct);
                journalId = entry.JournalId;
            }

            return new ActualRateReestimationResult(costs.Count, wipCount, laborAdj, overheadAdj, journalId);
        }

        // 材料腿的實作已抽到 IWorkOrderIssuedMaterialCostReader —— 在製品報表畫面要用同一份，
        // 兩邊各留一份的下場見人工驗收紀錄 W2-F7。
    }
}
