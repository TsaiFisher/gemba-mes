using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 各工單**已過帳發料淨值**（發料 − 退料，取異動當下的凍結成本；累計，不切期間）。
    ///
    /// 抽成共用讀取器的理由：在製品的材料腿曾經有兩份實作，
    /// 月結快照那份改對了、畫面那份沒跟著改，同一個「WIP」在系統裡有兩個答案
    /// （在製品報表的 WIP 金額恆不含材料，見人工驗收紀錄 W2-F7）。
    /// 兩腿只要來源不同，§3.10-5 金額恆等式驗的就不是資料一致性，而是兩份計算有沒有巧合。
    /// </summary>
    public interface IWorkOrderIssuedMaterialCostReader
    {
        Task<Dictionary<string, decimal>> IssuedNetCostByWorkOrderAsync(
            IReadOnlyCollection<string> workOrderIds, CancellationToken ct = default);
    }

    /// <inheritdoc cref="IWorkOrderIssuedMaterialCostReader"/>
    public class WorkOrderIssuedMaterialCostReader : IWorkOrderIssuedMaterialCostReader
    {
        private readonly ApplicationDbContext _db;

        public WorkOrderIssuedMaterialCostReader(ApplicationDbContext db) => _db = db;

        public async Task<Dictionary<string, decimal>> IssuedNetCostByWorkOrderAsync(
            IReadOnlyCollection<string> workOrderIds, CancellationToken ct = default)
        {
            if (workOrderIds.Count == 0) return new();
            var woIds = workOrderIds.ToList();

            var slips = await _db.MaterialSlips.IgnoreQueryFilters().AsNoTracking()
                .Where(sl => sl.SourceDocType == "WorkOrder"
                          && sl.SourceDocId != null && woIds.Contains(sl.SourceDocId)
                          && sl.Status == MaterialSlipStatus.Posted)
                .Select(sl => new { sl.SlipId, sl.SourceDocId })
                .ToListAsync(ct);

            var docToWo = slips.ToDictionary(x => x.SlipId, x => x.SourceDocId!);
            // 完工後沖單的單號慣例："{工單}-BF"，它沒有對應的發退料單。
            foreach (var id in woIds) docToWo[id + "-BF"] = id;

            var docNos = docToWo.Keys.ToList();
            return (await _db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .Where(m => docNos.Contains(m.DocNo) && WorkOrderMaterialMovement.Categories.Contains(m.TypeCategory))
                    .Select(m => new { m.DocNo, m.StockQty, m.Direction, m.UnitCost })
                    .ToListAsync(ct))
                .GroupBy(m => docToWo[m.DocNo])
                .ToDictionary(g => g.Key, g => g.Sum(m => -(m.StockQty * m.Direction) * (m.UnitCost ?? 0m)));
        }
    }
}
