using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.PUR.Services
{
    /// <summary>各（工單, 原料）的**在外原料淨額**：委外發料（OO）減委外退料（OR）。</summary>
    /// <param name="Qty">淨發料量（庫存單位）</param>
    /// <param name="Value">淨發料價值（本幣，取異動當下的凍結成本）</param>
    public readonly record struct OutsourceMaterialFlow(decimal Qty, decimal Value);

    /// <summary>
    /// 「發到廠商那裡的料」的單一讀取點。
    ///
    /// <para><b>為什麼需要</b>：委外發料有**兩個**入口，兩者產生的庫存異動完全相同
    /// （TypeCode OO／Direction −1）：</para>
    /// <list type="bullet">
    ///   <item>INV 的委外領退料單 <c>OutsourceMaterialDoc</c></item>
    ///   <item>MFG 的發退料單 <c>MaterialSlip</c>（Category = OutsourceIssue／OutsourceReturn，
    ///         見 <c>MaterialSlipController.CategoryToTypeCode</c>）</item>
    /// </list>
    /// <para>下游原本只認得前者，於是走發退料單發出去的料，在結算與稽核眼中**根本不存在**：
    /// 在外原料不會併回成品（成品只認加工費）、超耗扣款恆為 0、
    /// <c>OUTSOURCE_NEG_BALANCE</c> 也永遠不會響——而每一張傳票都平衡，
    /// 庫存數量也對，沒有任何地方會叫（人工驗收 W2-F12）。</para>
    ///
    /// <para>因此改以**庫存異動**為準（那是物理事實），單據表只用來把異動歸屬到工單。
    /// 新的呼叫端請一律走這裡，不要再各自查一份單據表。</para>
    /// </summary>
    public interface IOutsourceMaterialFlowReader
    {
        Task<Dictionary<(string Wo, string Mat), OutsourceMaterialFlow>> NetIssuedByWorkOrderAsync(
            string? plantId, IReadOnlyCollection<string> workOrderIds, CancellationToken ct = default);

        /// <summary>
        /// 本廠所有有委外發料紀錄的工單。工單集合由**單據**反推，不是先挑
        /// <c>WorkOrder.IsOutsource</c>——工序委外的工單整張並不是委外工單，
        /// 先挑旗標會把它們整批漏掉。
        /// </summary>
        Task<Dictionary<(string Wo, string Mat), OutsourceMaterialFlow>> NetIssuedByPlantAsync(
            string? plantId, CancellationToken ct = default);

        /// <summary>
        /// 委外退料（OR）單價空白時，以該工單此料「還在廠商那裡」的淨加權平均補上（2026-10-22）。
        /// <para>原本委外領退料單的單價欄位在畫面上是隱藏的、新增時為空：移動平均料以「現在的庫存均價」入庫
        /// （不是當初發出去的成本，差額被帶進成品），FIFO 料則因入庫沒有單價被過帳守門擋下、根本退不回來。
        /// 與廠內退料（MaterialSlipService.ResolveReturnUnitCostsAsync）同一原則。</para>
        /// 沒有連結工單、或該工單此料沒有在外淨額時不動（維持原行為）。
        /// </summary>
        Task FillReturnCostsAsync(Modules.INV.Models.OutsourceMaterialDoc doc, CancellationToken ct = default);
    }

    /// <inheritdoc cref="IOutsourceMaterialFlowReader"/>
    public class OutsourceMaterialFlowReader : IOutsourceMaterialFlowReader
    {
        private readonly ApplicationDbContext _db;

        public OutsourceMaterialFlowReader(ApplicationDbContext db) => _db = db;

        public Task<Dictionary<(string Wo, string Mat), OutsourceMaterialFlow>> NetIssuedByWorkOrderAsync(
            string? plantId, IReadOnlyCollection<string> workOrderIds, CancellationToken ct = default)
            => workOrderIds.Count == 0
                ? Task.FromResult(new Dictionary<(string Wo, string Mat), OutsourceMaterialFlow>())
                : AggregateAsync(plantId, workOrderIds.ToList(), ct);

        public Task<Dictionary<(string Wo, string Mat), OutsourceMaterialFlow>> NetIssuedByPlantAsync(
            string? plantId, CancellationToken ct = default)
            => AggregateAsync(plantId, null, ct);

        public async Task FillReturnCostsAsync(Modules.INV.Models.OutsourceMaterialDoc doc, CancellationToken ct = default)
        {
            if (doc.TypeCode != "OR" || string.IsNullOrEmpty(doc.WorkOrderCode)) return;
            var missing = doc.Lines.Where(l => l.UnitPrice == null).ToList();
            if (missing.Count == 0) return;
            var flows = await AggregateAsync(doc.PlantId, new List<string> { doc.WorkOrderCode! }, ct);
            foreach (var l in missing)
                if (flows.TryGetValue((doc.WorkOrderCode!, l.ProductCode), out var f) && f.Qty > 0m)
                    l.UnitPrice = f.Value / f.Qty;
        }

        private async Task<Dictionary<(string Wo, string Mat), OutsourceMaterialFlow>> AggregateAsync(
            string? plantId, List<string>? woIds, CancellationToken ct)
        {
            var agg = new Dictionary<(string Wo, string Mat), OutsourceMaterialFlow>();

            // 單號 → 工單。兩個入口併在一起：單號全域唯一，不會互撞。
            var docToWo = new Dictionary<string, string>();

            foreach (var d in await _db.OutsourceMaterialDocs.IgnoreQueryFilters().AsNoTracking()
                         .Where(d => d.Status == DocStatus.Posted && d.WorkOrderCode != null
                                  && (d.TypeCode == "OO" || d.TypeCode == "OR")
                                  && (plantId == null || d.PlantId == plantId)
                                  && (woIds == null || woIds.Contains(d.WorkOrderCode!)))
                         .Select(d => new { d.OutsourceMaterialDocId, WorkOrderCode = d.WorkOrderCode! })
                         .ToListAsync(ct))
                docToWo[d.OutsourceMaterialDocId] = d.WorkOrderCode;

            foreach (var s in await _db.MaterialSlips.IgnoreQueryFilters().AsNoTracking()
                         .Where(s => s.Status == MaterialSlipStatus.Posted
                                  && s.SourceDocType == "WorkOrder" && s.SourceDocId != null
                                  && (s.Category == MaterialSlipCategory.OutsourceIssue
                                   || s.Category == MaterialSlipCategory.OutsourceReturn)
                                  && (plantId == null || s.PlantId == plantId)
                                  && (woIds == null || woIds.Contains(s.SourceDocId!)))
                         .Select(s => new { s.SlipId, SourceDocId = s.SourceDocId! })
                         .ToListAsync(ct))
                docToWo[s.SlipId] = s.SourceDocId;

            if (docToWo.Count == 0) return agg;
            var docNos = docToWo.Keys.ToList();

            // 方向以異動為準（OO 出、OR 入），不看單別——同一張單不會同時有兩種方向。
            var movements = await _db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                .Where(m => docNos.Contains(m.DocNo)
                         && ((m.TypeCode == "OO" && m.Direction < 0)
                          || (m.TypeCode == "OR" && m.Direction > 0)))
                .Select(m => new { m.DocNo, m.ProductCode, m.StockQty, m.TypeCode, m.UnitCost })
                .ToListAsync(ct);

            foreach (var m in movements)
            {
                if (!docToWo.TryGetValue(m.DocNo, out var wo)) continue;
                var qty = Math.Abs(m.StockQty);
                if (qty <= 0) continue;
                var sign = m.TypeCode == "OO" ? 1m : -1m;
                var key  = (wo, m.ProductCode ?? "");
                var cur  = agg.GetValueOrDefault(key);
                agg[key] = new OutsourceMaterialFlow(
                    cur.Qty   + sign * qty,
                    cur.Value + sign * qty * (m.UnitCost ?? 0m));
            }

            return agg;
        }
    }
}
