using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.PUR.Models;

namespace SmartFactory.MES.Modules.PUR.Services
{
    /// <summary>
    /// 進貨上架的存貨計價基礎。
    ///
    /// 《對帳規格》§1.1 的暫估口徑是 <c>ReceiptQty × PurchaseOrderLine.UnitPrice</c>
    /// ——**採購單價**，而不是收貨當下可被改動的收貨單價。收貨改價造成的差額屬於
    /// 進料差異（當期費用），由應付認列端沖銷暫估時落位；價差一旦吃進存貨成本，
    /// 後續出庫成本、毛利與 WIP 會全部跟著偏，而且事後無從分辨哪一段是價差。
    ///
    /// 直接進貨（無來源採購單）沒有採購單價可依，退回收貨單價——此時兩端同價、
    /// 價差恆為 0，語意與規格一致。
    ///
    /// 這個值**建單／存檔時就決定並凍結**在 <see cref="GoodsReceiptLine.AccrualUnitPrice"/>
    /// （<see cref="StampAsync"/>），上架時只讀不算（<see cref="ResolveAccrualUnitPriceAsync"/>）。
    /// 理由見該欄位的註解：已核准的採購單仍可改價，而上架是逐批的。
    /// </summary>
    public static class GoodsReceiptCostBasis
    {
        /// <summary>
        /// 建單／存檔時決定該明細的計價基礎並寫入 <see cref="GoodsReceiptLine.AccrualUnitPrice"/>。
        /// 收貨單只有草稿、且尚未有任何上架量時才可存檔，故重存＝重新凍結是正確語意；
        /// 一旦倉庫動過（<c>QtyPutAway &gt; 0</c>）單據就不可修改，這個值即隨之定住。
        /// </summary>
        public static async Task<decimal> StampAsync(
            ApplicationDbContext db, GoodsReceiptLine line, CancellationToken ct = default)
        {
            var price = await LookupPoUnitPriceAsync(db, line, ct);
            line.AccrualUnitPrice = price;
            return price;
        }

        /// <summary>取得該收貨明細用於存貨計價與暫估掛帳的單價（交易幣）。</summary>
        public static async Task<decimal> ResolveAccrualUnitPriceAsync(
            ApplicationDbContext db, GoodsReceiptLine line, CancellationToken ct = default)
        {
            // 舊資料沒有凍結值，回退為現查——行為與本欄位存在之前完全相同，故不需回填。
            return line.AccrualUnitPrice ?? await LookupPoUnitPriceAsync(db, line, ct);
        }

        private static async Task<decimal> LookupPoUnitPriceAsync(
            ApplicationDbContext db, GoodsReceiptLine line, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(line.PurchaseOrderId) || line.PoLineNo == null)
                return line.UnitPrice;

            var poPrice = await db.PurchaseOrderLines.AsNoTracking()
                .Where(l => l.PurchaseOrderId == line.PurchaseOrderId && l.LineNo == line.PoLineNo)
                .Select(l => (decimal?)l.UnitPrice)
                .FirstOrDefaultAsync(ct);

            return poPrice ?? line.UnitPrice;
        }
    }
}
