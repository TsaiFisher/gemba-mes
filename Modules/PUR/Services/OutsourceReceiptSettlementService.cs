using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.PUR.Models;

namespace SmartFactory.MES.Modules.PUR.Services
{
    /// <summary>一筆委外收貨明細的結算結果。</summary>
    /// <param name="CarriedInCost">本次應併入成品成本的在外原料價值（本幣總額，非單價）</param>
    /// <param name="ExcessScrapPenalty">本次應向廠商求償的超耗料款（委外幣＝本幣，不課稅）</param>
    /// <param name="IsFinal">本次是否為該工單的結案批（結案批才結算超耗與掃尾）</param>
    public readonly record struct OutsourceLineSettlement(
        decimal CarriedInCost, decimal ExcessScrapPenalty, bool IsFinal);

    /// <summary>
    /// 委外收貨的**成本結算**：在外原料歸屬（併入成品）與超耗扣款（向廠商求償），
    /// 一次算完。
    ///
    /// 為什麼合成一支：兩者是同一筆錢的兩個去處。委外發料屬 Outsource 類，庫存數量出
    /// 而總帳不動（<c>BuildJournalLines</c> 對 Outsource 回空），料的價值一直留在存貨
    /// 科目（1401）裡。收回成品時這筆料錢只有兩個合法落點——併進成品成本，或由廠商賠。
    /// 兩邊分開算的話一定會漏：舊實作各有一支服務、各自重跑一次幾乎相同的 DB 組裝，
    /// 而併回去的只有「理論消耗」（收貨量 × BOM），被報廢掉的差額既沒進成品也沒人賠，
    /// 就永久留在 1401——庫存明細帳合計從此小於總帳存貨科目，且不會讓任何一張傳票失衡。
    ///
    /// ── 粒度：一律以「累計」為基準 ────────────────────────────────────────────
    /// 舊實作拿**單筆收貨量**去和**該工單全部發料量**比，等於把「還在廠商那裡加工、
    /// 尚未收回的部分」當成已經報廢。分兩批收就罰兩次，批數越多罰得越兇，而且不會回頭
    /// 修正——一張完全沒有損耗的委外單，分兩批收可以被罰到超過整筆加工費。
    ///
    /// 正解是：超耗只有在委外單**結束**時才算得出來。中間批一律不扣款，只把理論消耗
    /// 併回成品；結案批以「累計收貨量 vs 淨發料量」一次算清，並把剩下的在外原料餘額
    /// 一次掃進成品（扣掉廠商要賠的那一份）。
    ///
    /// 結案批的判定：累計收貨量 ≥ 訂購量（自動），或使用者在收貨單上勾選
    /// <see cref="OutsourceReceipt.IsFinalReceipt"/>（短交結案）。系統無法自己分辨
    /// 「短交＝廠商還沒做完」與「短交＝做完了，剩下的是報廢」，所以後者必須有人說一聲。
    ///
    /// ── 已知邊界 ─────────────────────────────────────────────────────────────
    /// 委外發料單（OO/OR）只帶工單號、不帶工序，故同一工單有多個委外工序時，
    /// 料是共用一個池子攤在一起算的。要分到工序層級得先讓發料單記住工序，屬另案。
    ///
    /// 掃尾把允收範圍內的正常損耗全部灌在**結案批**的成品上，該批單位成本會比前幾批高。
    /// 這是刻意的：已過帳批次的成本不能回頭改寫，攤在最後一批至少讓總額是對的。
    /// </summary>
    public interface IOutsourceReceiptSettlementService
    {
        /// <summary>
        /// 就地寫回 <paramref name="rec"/> 各明細的 <c>ExcessScrapPenalty</c> 與
        /// <c>CarriedInMaterialCost</c>，並回傳「收貨明細 LineNo → 結算結果」。
        /// 呼叫端須已載入 <c>rec.Lines</c>，且在同一 DbContext 交易內於過帳後 SaveChanges。
        /// </summary>
        Task<Dictionary<int, OutsourceLineSettlement>> SettleAsync(
            OutsourceReceipt rec, CancellationToken ct = default);
    }

    public class OutsourceReceiptSettlementService : IOutsourceReceiptSettlementService
    {
        private readonly ApplicationDbContext _context;

        // 收貨量與訂購量的比較容差：兩者都是 decimal(18,4)/(18,6)，
        // 換算單位後的尾數不該讓「已收足」被判成「還沒收足」。
        private const decimal QtyTolerance = 0.000001m;

        private readonly IOutsourceMaterialFlowReader _materialFlow;

        public OutsourceReceiptSettlementService(
            ApplicationDbContext context, IOutsourceMaterialFlowReader materialFlow)
        {
            _context = context;
            _materialFlow = materialFlow;
        }

        public async Task<Dictionary<int, OutsourceLineSettlement>> SettleAsync(
            OutsourceReceipt rec, CancellationToken ct = default)
        {
            var result = new Dictionary<int, OutsourceLineSettlement>();

            var lines = rec.Lines.Where(l => l.OpoLineNo.HasValue && l.StockQty > 0).ToList();
            if (lines.Count == 0) return result;

            // 廠別明確取自單據，不依賴 HTTP 環境廠別（Confirm 時兩者本就相同，
            // 但本計算也會被批次/測試在沒有請求上下文的情況下呼叫）。
            var plantId = rec.PlantId;

            var opoLineNos = lines.Select(l => l.OpoLineNo!.Value).Distinct().ToList();
            var thisOpoLines = await _context.OutsourcePurchaseOrderLines
                .IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.OutsourcePurchaseOrderId == rec.OutsourcePurchaseOrderId
                         && opoLineNos.Contains(l.LineNo))
                .Select(l => new { l.LineNo, l.SourceWorkOrderId })
                .ToListAsync(ct);
            if (thisOpoLines.Count == 0) return result;
            var opoLineToWo = thisOpoLines.ToDictionary(l => l.LineNo, l => l.SourceWorkOrderId);

            var woIds = opoLineToWo.Values.Distinct().ToList();

            // 同一張工單可能被拆到多張委外採購單／多行，累計必須涵蓋全部，
            // 否則「換一張 OPO 再收一次」會被當成全新的一批重算。
            var allOpoLines = await _context.OutsourcePurchaseOrderLines
                .IgnoreQueryFilters().AsNoTracking()
                .Where(l => woIds.Contains(l.SourceWorkOrderId))
                .Select(l => new { l.OutsourcePurchaseOrderId, l.LineNo, l.SourceWorkOrderId, l.Qty, l.SplitQty })
                .ToListAsync(ct);

            // 訂購量口徑沿用全站慣例（GoodsReceiptController／OutsourceProgressController）：
            // 拆單行以 SplitQty 為準，未拆單則取 Qty。
            var orderedByWo = allOpoLines
                .GroupBy(l => l.SourceWorkOrderId)
                .ToDictionary(g => g.Key, g => g.Sum(l => l.SplitQty > 0 ? l.SplitQty : l.Qty));

            var wos = await _context.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                .Where(w => w.PlantId == plantId && woIds.Contains(w.WorkOrderId))
                .Select(w => new { w.WorkOrderId, w.RoutingId })
                .ToListAsync(ct);
            var woRouting = wos.ToDictionary(w => w.WorkOrderId, w => w.RoutingId);

            var routingIds = wos.Select(w => w.RoutingId).Distinct().ToList();
            var bomUsage = (await _context.RoutingBoms.IgnoreQueryFilters().AsNoTracking()
                    .Where(b => routingIds.Contains(b.RoutingId))
                    .Select(b => new { b.RoutingId, b.ProductCode, b.Quantity })
                    .ToListAsync(ct))
                .GroupBy(b => (b.RoutingId, b.ProductCode))
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

            // 走 IOutsourceMaterialFlowReader 而不是自己查單據表：委外發料有兩個入口
            // （INV 委外領退料單／MFG 發退料單），兩者產生的庫存異動一模一樣，
            // 只認其中一個會讓另一條路發出去的料在結算眼中完全不存在（W2-F12）。
            var issued = await _materialFlow.NetIssuedByWorkOrderAsync(plantId, woIds, ct);
            var prior  = await LoadPriorReceiptsAsync(rec, allOpoLines
                .Select(l => (l.OutsourcePurchaseOrderId, l.LineNo, l.SourceWorkOrderId)).ToList(), ct);

            foreach (var group in lines.GroupBy(l => opoLineToWo[l.OpoLineNo!.Value]))
            {
                var woId = group.Key;
                var groupLines = group.OrderBy(l => l.LineNo).ToList();

                if (!woRouting.TryGetValue(woId, out var routingId))
                {
                    foreach (var l in groupLines) Assign(result, l, 0m, 0m, false);
                    continue;
                }

                var materials = issued
                    .Where(kv => kv.Key.Wo == woId && kv.Value.Qty > 0)
                    .Select(kv => new OutsourceScrapCalculator.MaterialUsage(
                        IssuedQty:       kv.Value.Qty,
                        AvcoUnitCost:    kv.Value.Value / kv.Value.Qty,
                        BomUsagePerUnit: bomUsage.GetValueOrDefault((routingId, kv.Key.Mat))))
                    .ToList();

                var totalIssuedValue = materials.Sum(m => m.IssuedQty * m.AvcoUnitCost);
                var p = prior.GetValueOrDefault(woId);

                var thisRecvQty   = groupLines.Sum(l => l.ReceiptQty);
                var thisStockQty  = groupLines.Sum(l => l.StockQty);
                var orderedQty    = orderedByWo.GetValueOrDefault(woId);
                var cumRecvQty    = p.RecvQty + thisRecvQty;
                var cumStockQty   = p.StockQty + thisStockQty;

                // 收足訂購量自動結案；短交則靠使用者勾選（訂購量為 0 表示資料不全，
                // 不自動結案，寧可把餘額留著也不要在資訊不足時扣廠商的錢）。
                var isFinal = rec.IsFinalReceipt
                           || (orderedQty > 0m && cumRecvQty >= orderedQty - QtyTolerance);

                // 允收損耗率取本批各行的最大值：同一工單同一批本就該是同一份合約條款，
                // 真的不一致時取寬鬆的一方，寧可少扣也不要多扣。
                // null（合約未約定）不參與比較——它代表「不扣款」，不是「容忍度 0」。
                var rates = groupLines.Where(l => l.AllowedScrapRate.HasValue)
                                      .Select(l => l.AllowedScrapRate!.Value).ToList();
                decimal? allowedRate = rates.Count > 0 ? rates.Max() : null;

                decimal penalty = 0m;
                // 有約定才扣：null ＝ 合約沒談損耗條款，沒談過的錢不能自己扣；
                // 而 0 是「明確約定零容忍」，必須扣——這兩件事的財務結果完全相反，
                // 原本用 `allowedRate > 0` 判斷等於把兩者混為一談（W2-F14）。
                if (isFinal && allowedRate.HasValue && materials.Count > 0)
                {
                    var cumulative = OutsourceScrapCalculator.CalculateMultiMaterial(
                        cumStockQty, allowedRate.Value, materials);
                    // 減掉先前批次已扣的：新規則下中間批一律 0，但舊資料可能已被多扣，
                    // 這裡讓它自我收斂。已多扣的不在本次退回（發票負項不支援正項回沖），
                    // 故夾在 0 以上——歷史多扣由 §3.3 在外原料稽核在關帳時攔下來處理。
                    penalty = Math.Max(0m, cumulative - p.Penalty);
                }

                var remaining = totalIssuedValue - p.CarriedIn;
                decimal groupCarriedIn;
                if (isFinal)
                {
                    // 掃尾：剩下的在外原料一次併回成品，扣掉廠商要賠的那一份。
                    groupCarriedIn = Math.Max(0m, remaining - penalty);
                }
                else
                {
                    // 中間批只併理論消耗，且不得超過還在外面的餘額。
                    var theoretical = materials.Sum(m => thisStockQty * m.BomUsagePerUnit * m.AvcoUnitCost);
                    groupCarriedIn = Math.Min(theoretical, Math.Max(0m, remaining));
                }

                DistributeToLines(result, groupLines, thisStockQty, groupCarriedIn, penalty, isFinal);
            }

            foreach (var line in rec.Lines)
            {
                var s = result.GetValueOrDefault(line.LineNo);
                line.CarriedInMaterialCost = s.CarriedInCost;
                line.ExcessScrapPenalty    = s.ExcessScrapPenalty;
            }

            return result;
        }

        // (工單, 原料) → 淨發料量與淨值的讀取已抽到 IOutsourceMaterialFlowReader：
        // 委外發料有兩個入口，兩邊都要看得到（W2-F12）。

        /// <summary>
        /// 工單 → 先前批次的累計（不含本單）。狀態只認 Confirmed/Posted：
        /// 草稿還沒發生，作廢的已經回沖。
        /// </summary>
        private async Task<Dictionary<string, (decimal RecvQty, decimal StockQty, decimal CarriedIn, decimal Penalty)>>
            LoadPriorReceiptsAsync(
                OutsourceReceipt rec,
                List<(string Opo, int LineNo, string Wo)> opoLines,
                CancellationToken ct)
        {
            var map = new Dictionary<string, (decimal, decimal, decimal, decimal)>();
            if (opoLines.Count == 0) return map;

            var opoIds = opoLines.Select(l => l.Opo).Distinct().ToList();
            var rows = await (
                from rl in _context.OutsourceReceiptLines.IgnoreQueryFilters().AsNoTracking()
                join r in _context.OutsourceReceipts.IgnoreQueryFilters().AsNoTracking()
                    on rl.OutsourceReceiptId equals r.OutsourceReceiptId
                where r.OutsourceReceiptId != rec.OutsourceReceiptId
                      && (r.Status == DocStatus.Confirmed || r.Status == DocStatus.Posted)
                      && opoIds.Contains(r.OutsourcePurchaseOrderId)
                      && rl.OpoLineNo != null
                select new
                {
                    r.OutsourcePurchaseOrderId, rl.OpoLineNo,
                    rl.ReceiptQty, rl.StockQty, rl.CarriedInMaterialCost, rl.ExcessScrapPenalty,
                }).ToListAsync(ct);

            var lineToWo = opoLines.ToDictionary(l => (l.Opo, l.LineNo), l => l.Wo);
            foreach (var r in rows)
            {
                if (!lineToWo.TryGetValue((r.OutsourcePurchaseOrderId, r.OpoLineNo!.Value), out var wo)) continue;
                var cur = map.GetValueOrDefault(wo);
                map[wo] = (cur.Item1 + r.ReceiptQty, cur.Item2 + r.StockQty,
                           cur.Item3 + r.CarriedInMaterialCost, cur.Item4 + r.ExcessScrapPenalty);
            }
            return map;
        }

        /// <summary>
        /// 把工單層級的結算金額依 StockQty 比例攤到各收貨明細，尾差給最後一行——
        /// 攤完的合計必須與工單層級的金額**分毫不差**，否則掃尾就掃不乾淨。
        /// </summary>
        private static void DistributeToLines(
            Dictionary<int, OutsourceLineSettlement> result,
            List<OutsourceReceiptLine> groupLines,
            decimal totalStockQty, decimal carriedIn, decimal penalty, bool isFinal)
        {
            if (groupLines.Count == 1 || totalStockQty <= 0)
            {
                for (var i = 0; i < groupLines.Count; i++)
                    Assign(result, groupLines[i], i == 0 ? carriedIn : 0m, i == 0 ? penalty : 0m, isFinal);
                return;
            }

            decimal allocCarried = 0m, allocPenalty = 0m;
            for (var i = 0; i < groupLines.Count - 1; i++)
            {
                var share = groupLines[i].StockQty / totalStockQty;
                var c = Math.Round(carriedIn * share, 4, MidpointRounding.AwayFromZero);
                var p = Math.Round(penalty  * share, 4, MidpointRounding.AwayFromZero);
                allocCarried += c; allocPenalty += p;
                Assign(result, groupLines[i], c, p, isFinal);
            }
            Assign(result, groupLines[^1], carriedIn - allocCarried, penalty - allocPenalty, isFinal);
        }

        private static void Assign(
            Dictionary<int, OutsourceLineSettlement> result,
            OutsourceReceiptLine line, decimal carriedIn, decimal penalty, bool isFinal)
            => result[line.LineNo] = new OutsourceLineSettlement(
                Math.Round(carriedIn, 4, MidpointRounding.AwayFromZero),
                Math.Round(penalty,   4, MidpointRounding.AwayFromZero),
                isFinal);
    }
}
