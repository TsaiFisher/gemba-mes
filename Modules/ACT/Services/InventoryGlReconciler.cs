using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.ACT.Services
{
    /// <summary>一張工單留在總帳存貨科目、但已不在庫存明細帳裡的金額（在製）。</summary>
    /// <param name="Amount">總帳 − 明細帳；正數＝料已發出還沒繳庫，負數＝回到倉庫的價值比發出去的多</param>
    /// <param name="Tolerance">捨入容忍：單位成本 6 位小數 × 該工單異動量的半個最小單位，加上金額容忍值</param>
    public sealed record WipInGlRow(string WorkOrderId, WorkOrderStatus? Status, decimal Amount, decimal Tolerance);

    /// <summary>一張單據的總帳存貨科目淨額與庫存異動價值不相等。</summary>
    public sealed record DocGlDiffRow(string DocNo, string TypeCodes, decimal GlAmount, decimal MovementValue)
    {
        public decimal Diff => GlAmount - MovementValue;
    }

    /// <summary>
    /// 一個廠的「總帳存貨科目 ↔ 庫存明細帳」對帳式。
    ///
    /// <code>
    ///   總帳 1401
    /// − 明細帳價值（FIFO 成本層＋移動平均在手價值）
    /// − 在製（料已發出、還沒繳庫：生產與委外不入總帳，價值仍在 1401）
    /// − 客供料（CI／CR：所有權屬客戶，只管數量不入總帳）
    /// − 借出在外（IL／LR：所有權沒移轉，只換了地點）
    /// ＝ 殘差（必須為 0）
    /// </code>
    /// </summary>
    public sealed record InventoryGlBridge(
        string PlantId,
        decimal GlBalance,
        decimal SubledgerValue,
        decimal WipInGl,
        decimal CustomerOwned,
        decimal LentOut,
        decimal Tolerance,
        IReadOnlyList<WipInGlRow> WorkOrders,
        IReadOnlyList<DocGlDiffRow> UnexplainedDocs)
    {
        public decimal Residual => GlBalance - SubledgerValue - WipInGl - CustomerOwned - LentOut;
    }

    /// <summary>
    /// 總帳存貨科目對庫存明細帳的對帳（2026-10-22）。
    ///
    /// <para><b>為什麼需要</b>：既有稽核全是數量型（INV_ROLLFORWARD、FIFO 成本層＝在手）或傳票型
    /// （借貸平衡、重複立帳）。某一類異動漏開傳票、或傳票金額與異動不同，數量全對、每張傳票都平衡，
    /// 沒有任何稽核會叫——穩定版 ADM 廠的總帳就這樣比明細帳多出 12.7 萬，才被隨機序列測試順帶發現。</para>
    ///
    /// <para><b>對帳調整項只有三種，全部是過帳引擎「刻意不入帳」的類別</b>
    /// （<c>InventoryPostingService.BuildJournalLines</c>）：生產／委外（形態轉換，價值留在存貨科目）、
    /// 客供料、借出。三者都以單據逐張算出「總帳 − 異動價值」再歸屬，不是拿總差額去倒推，
    /// 所以殘差不會被調整項吸收掉。</para>
    ///
    /// <para><b>在製逐工單歸屬</b>：工單號（完工入庫與吸收分錄）、工單號-BF（後沖）、
    /// 來源是該工單的發退料單、委外領退料單、委外收貨（經委外採購明細的來源工單）；
    /// 傳票明細的 <c>SourceRef</c> 是工單號的也算該工單（月結重估與人工更正走這裡）。
    /// 一張工單從發料到繳庫，總帳與明細帳的淨差必須回到 0：結案了還留著、或變成負數，
    /// 都是帳錯了——前者是有料沒算進成品，後者是退回倉庫的料比發出去的多。</para>
    ///
    /// <para><b>金額一律以 數量 × 單位成本 計</b>，不用 <c>TotalCost</c>：作廢沖銷的異動
    /// 方向已反轉、<c>TotalCost</c> 又寫成負數，用「方向 × TotalCost」加總會把作廢算成同向。</para>
    /// </summary>
    public class InventoryGlReconciler
    {
        private readonly ApplicationDbContext _db;

        /// <summary>單位成本存 6 位小數：每單位最多差半個最小單位。</summary>
        internal const decimal UnitCostHalfUlp = 0.0000005m;

        /// <summary>客供料：所有權屬客戶（與 BuildJournalLines 的 OwnershipUnchangedTypeCodes 同一組語意）。</summary>
        internal static readonly IReadOnlySet<string> CustomerOwnedTypeCodes = new HashSet<string>(StringComparer.Ordinal) { "CI", "CR" };

        /// <summary>借出在外：所有權沒有移轉。</summary>
        internal static readonly IReadOnlySet<string> LentOutTypeCodes = new HashSet<string>(StringComparer.Ordinal) { "IL", "LR" };

        /// <summary>只動總帳、不寫庫存異動、而由明細帳單價同步承接的來源（進料費用分攤直接調成本層／均價）。</summary>
        internal static readonly IReadOnlySet<string> ValuationOnlySourceTypes = new HashSet<string>(StringComparer.Ordinal) { "LandedCostAllocation", "LandedCostAllocation-Void" };

        public InventoryGlReconciler(ApplicationDbContext db) => _db = db;

        /// <param name="defaultCostMethod">系統參數 <c>inv_default_cost_method</c> 的值（類別為「系統預設」的品號依此計價）</param>
        public async Task<List<InventoryGlBridge>> ReconcileAsync(
            string? plantId, decimal amountTolerance, string? defaultCostMethod, CancellationToken ct = default)
        {
            var account = StandardAccounts.RawMaterialInventoryCode;

            // ── 總帳：逐「來源單據」彙總（明細 SourceRef 是工單號時歸到工單）──
            var glLines = await _db.JournalLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.AccountCode == account
                         && (plantId == null || l.JournalEntry!.PlantId == plantId))
                .Select(l => new
                {
                    PlantId = l.JournalEntry!.PlantId ?? "",
                    l.JournalEntry!.SourceDocType,
                    DocNo = l.JournalEntry!.SourceDocId ?? l.JournalId,
                    l.SourceRef,
                    Net = l.Debit - l.Credit,
                })
                .ToListAsync(ct);

            // ── 異動：逐單據彙總；作廢沖銷列與原列同一單號，淨額自然抵銷 ──
            var moves = await _db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                .Where(m => plantId == null || m.PlantId == plantId)
                .Select(m => new
                {
                    m.PlantId, m.DocNo, m.DocLineNo, m.TypeCode,
                    Value = m.Direction * m.StockQty * (m.UnitCost ?? 0m),
                    Qty = m.StockQty,
                })
                .ToListAsync(ct);

            var plants = glLines.Select(l => l.PlantId).Concat(moves.Select(m => m.PlantId))
                .Where(p => p != "").Distinct().ToList();
            if (plants.Count == 0) return new();

            var woIndex = await LoadWorkOrderIndexAsync(plantId, ct);
            var valuation = await SubledgerValueAsync(plantId, defaultCostMethod, ct);

            var result = new List<InventoryGlBridge>();
            foreach (var p in plants.OrderBy(x => x, StringComparer.Ordinal))
            {
                var pGl = glLines.Where(l => l.PlantId == p).ToList();
                var pMoves = moves.Where(m => m.PlantId == p).ToList();

                // 單據 → 工單；委外收貨可能一張收多張工單的貨，逐行拆
                var woAmount = new Dictionary<string, decimal>(StringComparer.Ordinal);
                var woQty = new Dictionary<string, decimal>(StringComparer.Ordinal);
                void AddWo(string wo, decimal amount, decimal qty)
                {
                    woAmount[wo] = woAmount.GetValueOrDefault(wo) + amount;
                    woQty[wo] = woQty.GetValueOrDefault(wo) + qty;
                }

                var glByDoc = new Dictionary<string, decimal>(StringComparer.Ordinal);
                var glOnlyValuation = new HashSet<string>(StringComparer.Ordinal);
                foreach (var l in pGl)
                {
                    if (l.SourceRef != null && woIndex.WorkOrders.ContainsKey(l.SourceRef))
                    {
                        AddWo(l.SourceRef, l.Net, 0m);
                        continue;
                    }
                    glByDoc[l.DocNo] = glByDoc.GetValueOrDefault(l.DocNo) + l.Net;
                    if (ValuationOnlySourceTypes.Contains(l.SourceDocType)) glOnlyValuation.Add(l.DocNo);
                }

                var movesByDoc = pMoves.GroupBy(m => m.DocNo, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

                decimal customerOwned = 0m, lentOut = 0m;
                var unexplained = new List<DocGlDiffRow>();

                foreach (var doc in glByDoc.Keys.Union(movesByDoc.Keys, StringComparer.Ordinal))
                {
                    var gl = glByDoc.GetValueOrDefault(doc);
                    var docMoves = movesByDoc.GetValueOrDefault(doc) ?? new();
                    var moveValue = docMoves.Sum(m => m.Value);

                    if (woIndex.ReceiptLineWo.TryGetValue(doc, out var lineWo) && lineWo.Values.Distinct().Count() > 1)
                    {
                        // 一張委外收貨收了多張工單的貨：異動逐行歸屬，傳票（加工費）按各行異動價值比例攤
                        var total = moveValue;
                        foreach (var g in docMoves.GroupBy(m => m.DocLineNo))
                        {
                            var wo = lineWo.GetValueOrDefault(g.Key) ?? lineWo.Values.First();
                            var v = g.Sum(m => m.Value);
                            var share = total != 0m ? gl * v / total : gl / docMoves.Select(m => m.DocLineNo).Distinct().Count();
                            AddWo(wo, share - v, g.Sum(m => m.Qty));
                        }
                        continue;
                    }

                    var diff = gl - moveValue;
                    if (woIndex.DocToWo.TryGetValue(doc, out var woId))
                    {
                        AddWo(woId, diff, docMoves.Sum(m => m.Qty));
                        continue;
                    }
                    if (Math.Abs(diff) <= amountTolerance) continue;

                    var types = docMoves.Select(m => m.TypeCode).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();
                    if (types.Count > 0 && types.All(CustomerOwnedTypeCodes.Contains)) { customerOwned += diff; continue; }
                    if (types.Count > 0 && types.All(LentOutTypeCodes.Contains)) { lentOut += diff; continue; }
                    // 進料費用分攤只有傳票沒有異動，由明細帳單價承接；出現在殘差清單裡只會誤導
                    if (docMoves.Count == 0 && glOnlyValuation.Contains(doc)) continue;

                    unexplained.Add(new DocGlDiffRow(doc, string.Join(",", types), gl, moveValue));
                }

                var wos = woAmount
                    .Select(kv => new WipInGlRow(kv.Key, woIndex.WorkOrders.GetValueOrDefault(kv.Key),
                        kv.Value, amountTolerance + woQty.GetValueOrDefault(kv.Key) * UnitCostHalfUlp))
                    .OrderBy(w => w.WorkOrderId, StringComparer.Ordinal)
                    .ToList();

                var v0 = valuation.GetValueOrDefault(p);
                result.Add(new InventoryGlBridge(
                    PlantId: p,
                    GlBalance: pGl.Sum(l => l.Net),
                    SubledgerValue: v0.Value,
                    WipInGl: wos.Sum(w => w.Amount),
                    CustomerOwned: customerOwned,
                    LentOut: lentOut,
                    Tolerance: amountTolerance + v0.MovingAverageQty * UnitCostHalfUlp,
                    WorkOrders: wos,
                    UnexplainedDocs: unexplained.OrderByDescending(d => Math.Abs(d.Diff)).ToList()));
            }
            return result;
        }

        /// <summary>
        /// 明細帳價值：FIFO 品取成本層（剩餘量 × 層單價），移動平均品取在手量 × 均價。
        /// 計價方式依品號類別，類別為「系統預設」時讀 <c>inv_default_cost_method</c>（同 InventoryPostingService）。
        /// </summary>
        private async Task<Dictionary<string, (decimal Value, decimal MovingAverageQty)>> SubledgerValueAsync(
            string? plantId, string? defaultCostMethod, CancellationToken ct)
        {
            var globalFifo = string.Equals(defaultCostMethod, "FIFO", StringComparison.OrdinalIgnoreCase);

            var methods = await _db.Products.IgnoreQueryFilters().AsNoTracking()
                .Select(p => new { p.ProductCode, Method = p.Category == null ? CostMethod.SystemDefault : p.Category.CostMethod })
                .ToDictionaryAsync(p => p.ProductCode, p => p.Method, ct);
            bool IsFifo(string product)
            {
                var m = methods.GetValueOrDefault(product, CostMethod.SystemDefault);
                return m == CostMethod.Fifo || (m == CostMethod.SystemDefault && globalFifo);
            }

            var layers = await _db.InventoryCostLayers.IgnoreQueryFilters().AsNoTracking()
                .Where(l => plantId == null || l.PlantId == plantId)
                .GroupBy(l => new { l.PlantId, l.ProductCode })
                .Select(g => new { g.Key.PlantId, g.Key.ProductCode, Value = g.Sum(l => l.QtyRemaining * l.UnitCost) })
                .ToListAsync(ct);
            var onHand = await _db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => plantId == null || i.PlantId == plantId)
                .Select(i => new { i.PlantId, i.ProductCode, i.QtyOnHand, i.UnitCost })
                .ToListAsync(ct);

            var result = new Dictionary<string, (decimal Value, decimal MovingAverageQty)>(StringComparer.Ordinal);
            foreach (var l in layers.Where(l => IsFifo(l.ProductCode)))
            {
                var cur = result.GetValueOrDefault(l.PlantId);
                result[l.PlantId] = (cur.Value + l.Value, cur.MovingAverageQty);
            }
            foreach (var i in onHand.Where(i => !IsFifo(i.ProductCode)))
            {
                var cur = result.GetValueOrDefault(i.PlantId);
                result[i.PlantId] = (cur.Value + i.QtyOnHand * i.UnitCost, cur.MovingAverageQty + Math.Abs(i.QtyOnHand));
            }
            return result;
        }

        private sealed record WorkOrderIndex(
            Dictionary<string, WorkOrderStatus?> WorkOrders,
            Dictionary<string, string> DocToWo,
            Dictionary<string, Dictionary<int, string>> ReceiptLineWo);

        /// <summary>單號 → 工單。不看單據狀態：作廢單的原異動與沖銷異動同單號，淨額本來就是 0。</summary>
        private async Task<WorkOrderIndex> LoadWorkOrderIndexAsync(string? plantId, CancellationToken ct)
        {
            var wos = await _db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                .Where(w => plantId == null || w.PlantId == plantId)
                .Select(w => new { w.WorkOrderId, w.Status })
                .ToListAsync(ct);
            var woMap = wos.GroupBy(w => w.WorkOrderId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (WorkOrderStatus?)g.First().Status, StringComparer.Ordinal);

            var docToWo = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in woMap.Keys)
            {
                docToWo[id] = id;                                         // 完工入庫 IT、完工吸收分錄
                docToWo[id + DataAuditService.BackflushDocSuffix] = id;   // 後沖 IO
            }

            foreach (var s in await _db.MaterialSlips.IgnoreQueryFilters().AsNoTracking()
                         .Where(s => s.SourceDocType == "WorkOrder" && (plantId == null || s.PlantId == plantId))
                         .Select(s => new { s.SlipId, s.SlipNo, s.SourceDocId })
                         .ToListAsync(ct))
            {
                docToWo[s.SlipId] = s.SourceDocId;
                docToWo[s.SlipNo] = s.SourceDocId;
            }

            foreach (var d in await _db.OutsourceMaterialDocs.IgnoreQueryFilters().AsNoTracking()
                         .Where(d => d.WorkOrderCode != null && (plantId == null || d.PlantId == plantId))
                         .Select(d => new { d.OutsourceMaterialDocId, d.WorkOrderCode })
                         .ToListAsync(ct))
                docToWo[d.OutsourceMaterialDocId] = d.WorkOrderCode!;

            // 委外收貨 → 委外採購明細的來源工單
            var receiptLines = await (
                    from rl in _db.OutsourceReceiptLines.IgnoreQueryFilters().AsNoTracking()
                    join r in _db.OutsourceReceipts.IgnoreQueryFilters().AsNoTracking()
                        on rl.OutsourceReceiptId equals r.OutsourceReceiptId
                    join ol in _db.OutsourcePurchaseOrderLines.IgnoreQueryFilters().AsNoTracking()
                        on new { Opo = r.OutsourcePurchaseOrderId, LineNo = rl.OpoLineNo ?? -1 }
                        equals new { Opo = ol.OutsourcePurchaseOrderId, ol.LineNo }
                    where plantId == null || r.PlantId == plantId
                    select new { rl.OutsourceReceiptId, rl.LineNo, ol.SourceWorkOrderId })
                .ToListAsync(ct);
            var receiptLineWo = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
            foreach (var g in receiptLines.GroupBy(x => x.OutsourceReceiptId, StringComparer.Ordinal))
            {
                receiptLineWo[g.Key] = g.ToDictionary(x => x.LineNo, x => x.SourceWorkOrderId);
                docToWo[g.Key] = g.First().SourceWorkOrderId;
            }

            return new WorkOrderIndex(woMap, docToWo, receiptLineWo);
        }
    }
}
