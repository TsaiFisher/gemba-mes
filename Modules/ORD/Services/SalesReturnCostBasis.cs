using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;

namespace SmartFactory.MES.Modules.ORD.Services
{
    /// <summary>
    /// 銷貨退回的入庫計價基礎。
    ///
    /// 退回來的貨是**當初出去的那一批**，不是一批用售價買進來的新貨，所以入庫成本
    /// 必須取原出貨異動實際扣掉的單位成本（<c>StockMovement.UnitCost</c>），
    /// 而不是銷退單上的單價——那是售價，銷退單上根本沒有成本欄位可填。
    ///
    /// 拿售價當入庫成本會一次做錯三件事，而且三件都不會有任何守門叫：
    ///   ① 存貨科目與庫存明細帳一起被灌成售價（兩邊一致地錯，對帳不出來）
    ///   ② 反向沖回的銷貨成本大於當初認列的銷貨成本，毛利虛增
    ///   ③ 移動平均被汙染，之後**每一次**出貨的成本都跟著錯——錯誤會一直擴散
    ///
    /// 來源刻意取庫存異動而非「當時的移動平均」：均價是全廠、隨時在變的值，
    /// 事後重算不會回到出貨當下那個數；異動列記的是那一次實際扣掉多少，是唯一的事實。
    ///
    /// 找不到來源（手工加入、未連結出貨的退回行）回 null，由過帳端退回以現行單位成本入庫
    /// ——那條路徑不會動到移動平均，是無法得知原始成本時最不傷害既有計價的做法。
    /// </summary>
    public static class SalesReturnCostBasis
    {
        public static async Task<decimal?> ResolveUnitCostAsync(
            ApplicationDbContext db, string? shipmentId, int? shipmentLineNo,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(shipmentId) || shipmentLineNo == null) return null;

            // 取這一行出庫的**淨額加權平均**（出庫 − 沖銷）。同一行可能有多筆出庫列：
            // 完整模式分批揀貨、取消確認後再出貨，各筆扣的均價不同；原本只取任一筆出庫列，
            // 退回成本就看資料庫回傳順序（2026-10-23 跨廠序列抓到同型缺陷後一併修正）。
            // 已全數沖銷（淨量 0）的出貨本來就不該還能退貨，回 null 走現行單位成本。
            var net = await db.StockMovements.AsNoTracking()
                .Where(m => m.DocNo == shipmentId && m.DocLineNo == shipmentLineNo)
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Qty   = g.Sum(m => -m.Direction * m.StockQty),
                    Value = g.Sum(m => -m.Direction * m.StockQty * (m.UnitCost ?? 0m)),
                })
                .FirstOrDefaultAsync(ct);
            return net is { Qty: > 0m } ? Math.Round(net.Value / net.Qty, 6, MidpointRounding.AwayFromZero) : null;
        }
    }
}
