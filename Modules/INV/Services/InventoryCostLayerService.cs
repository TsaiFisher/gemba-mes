using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV.Services
{
    public interface IInventoryCostLayerService
    {
        /// <summary>
        /// 建立一層 FIFO 成本層。
        ///
        /// <paramref name="receiptDate"/> 是**該筆過帳的業務日**（<c>MovementDate</c>／作廢的
        /// <c>VoidDate</c>），呼叫端必傳、且必須就是它寫進 <c>StockMovement.MovementDate</c>
        /// 的那一個值。<see cref="InventoryCostLayer.ReceiptDate"/> 是全系統唯一的 FIFO 排序鍵
        /// （<c>ConsumeLayersAsync</c>／<c>RemoveLayersAsync</c> 的 <c>OrderBy</c>），沒有第二個讀取點。
        ///
        /// <para>2026-09-07 之前這裡是自己呼叫 <c>IDateTimeService.GetLocalDate()</c> 取「建層當天」。
        /// 同一次過帳的其他每一樣落地——<c>StockMovement.MovementDate</c>、<c>JournalEntry.EntryDate</c>、
        /// 期間歸屬——用的都是單據日，只有成本層另外去問了一次時鐘，於是**倒填的單據**
        /// （單據日期欄位是開放的 <c>&lt;input type="date"&gt;</c>，只有期間鎖擋著）建出來的層會帶著
        /// 今天的日期排到隊尾。實際跑的規則因此變成「先過帳先出」而不是欄位名宣稱的先進先出，
        /// 兩者只在單據倒填時分岔——而分岔時沒有任何稽核會叫，只有出庫成本悄悄取錯層。</para>
        ///
        /// <para>所以這個參數刻意**沒有預設值**：漏傳就編不過，不會靜默退回舊行為。
        /// 本服務也刻意不再注入 <c>IDateTimeService</c>——它不該有能力自己生出一個業務日。</para>
        /// </summary>
        Task AddLayerAsync(string plantId, string productCode, string locationCode, string lotNo, decimal qty, decimal unitCost, string sourceDocNo, DateOnly receiptDate, CancellationToken ct = default);
        /// <summary>
        /// 依 FIFO 消耗成本層，回傳「實際被消耗的那些層」的加權平均單價。
        ///
        /// <paramref name="docNo"/> 是消耗這批層的單據號，會連同「哪一層、扣多少、什麼單價」
        /// 一起寫進 <see cref="InventoryCostLayerConsumption"/>，供
        /// <see cref="RestoreConsumedAsync"/> 在撤回／作廢時**還進原本被扣的那一層**。
        /// 沒有這個連結的話，撤回只能另建一層把量加回去，FIFO 順序會被打亂（W4-F10）。
        ///
        /// <paramref name="consumedDate"/> 同 <c>AddLayerAsync</c> 的 receiptDate：取該筆過帳的
        /// 業務日，不是系統當天。
        /// </summary>
        Task<decimal> ConsumeLayersAsync(string plantId, string productCode, string locationCode, string lotNo, decimal qty, string docNo, DateOnly consumedDate, CancellationToken ct = default);

        /// <summary>
        /// 撤回／作廢一筆**出庫**時，把量還進當初被扣的那些層（<see cref="ConsumeLayersAsync"/> 的逆操作）。
        ///
        /// <para>以 <paramref name="docNo"/> 回查該單的消耗紀錄，依<b>消耗的相反順序</b>（後扣的先還）
        /// 逐層回補，並累加各紀錄的 <c>RestoredQty</c>。回補的是原層的 <c>Id</c>，
        /// 所以 <c>ReceiptDate</c> 不變、FIFO 排序原樣復原。</para>
        ///
        /// <para><b>回傳值</b>：實際還回去的量。小於 <paramref name="qty"/> 代表有一部分找不到
        /// 對應的消耗紀錄（見下），呼叫端需自行處理剩餘量。</para>
        ///
        /// <para><b>找不到紀錄時不擲錯</b>：這張表是後來才加的，既有資料的消耗一筆紀錄都沒有；
        /// 若在這裡擋下來，等於所有歷史單據都不能作廢。差額交回呼叫端，由它退回舊行為
        /// （<see cref="AddLayerAsync"/> 另建一層）——結果與修正前相同，不會更糟。</para>
        /// </summary>
        Task<decimal> RestoreConsumedAsync(string plantId, string productCode, string locationCode, string lotNo, decimal qty, string docNo, CancellationToken ct = default);

        /// <summary>
        /// 作廢一筆**入庫**時，把該單當初建立的成本層收回來。
        /// 先扣自己那一層（以 SourceDocNo 認），不夠的部分才按 FIFO 順序往下扣——
        /// 那批貨已經被領走了，帳上要維持的是「成本層總量 ＝ 在手量」這個不變量。
        /// </summary>
        Task RemoveLayersAsync(string plantId, string productCode, string locationCode, string lotNo, decimal qty, string sourceDocNo, CancellationToken ct = default);

        /// <summary>
        /// 現存成本層的加權平均單價；沒有任何剩餘層時回 <c>null</c>。
        ///
        /// 給「入庫但沒有原始單價可帶」的情境用——目前唯一的呼叫端是盤點結帳的盤盈：
        /// 盤出來的多餘品項沒有進貨單、沒有供應商、沒有單價，但它就是同一批貨，
        /// 用現存層的加權平均是最貼近事實的成本基礎。
        ///
        /// 刻意**不**放進 <c>InventoryPostingService</c> 的 FIFO 入庫分支當 fallback：
        /// 那道「入庫未帶單價一律擲錯」的守門是 W2-F8 才補的，用來擋退料漏帶凍結成本，
        /// 一旦在服務層自動推導，那個洞就整個開回來。要用這個成本基礎的呼叫端
        /// 必須自己明講（把值填進 UnitPrice），語意才留在知道語意的那一層。
        /// </summary>
        Task<decimal?> GetCurrentUnitCostAsync(string plantId, string productCode, string locationCode, string lotNo, CancellationToken ct = default);
    }

    public class InventoryCostLayerService : IInventoryCostLayerService
    {
        private readonly ApplicationDbContext _context;

        // 刻意**不注入** IDateTimeService：本服務寫進去的每一個日期都是「某次過帳的業務日」，
        // 一律由呼叫端傳入。若它拿得到時鐘，就永遠有可能又生出一個與該次過帳不同的日期
        // ——那正是 2026-09-07 之前 ReceiptDate 的問題。拿掉依賴，讓這件事成為結構上的不可能。
        public InventoryCostLayerService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task AddLayerAsync(string plantId, string productCode, string locationCode, string lotNo, decimal qty, decimal unitCost, string sourceDocNo, DateOnly receiptDate, CancellationToken ct = default)
        {
            var layer = new InventoryCostLayer
            {
                PlantId = plantId,
                ProductCode = productCode,
                LocationCode = locationCode,
                LotNo = lotNo,
                ReceiptDate = receiptDate,
                SourceDocNo = sourceDocNo,
                QtyRemaining = qty,
                UnitCost = unitCost
            };
            _context.Set<InventoryCostLayer>().Add(layer);
            await Task.CompletedTask;
        }

        public async Task<decimal?> GetCurrentUnitCostAsync(string plantId, string productCode, string locationCode, string lotNo, CancellationToken ct = default)
        {
            var layers = await _context.Set<InventoryCostLayer>().AsNoTracking()
                .Where(l => l.PlantId == plantId && l.ProductCode == productCode
                         && l.LocationCode == locationCode && l.LotNo == lotNo
                         && l.QtyRemaining > 0m)
                .Select(l => new { l.QtyRemaining, l.UnitCost })
                .ToListAsync(ct);

            var qty = layers.Sum(l => l.QtyRemaining);
            if (qty <= 0m) return null;
            return Math.Round(layers.Sum(l => l.QtyRemaining * l.UnitCost) / qty, 6, MidpointRounding.AwayFromZero);
        }

        public async Task<decimal> ConsumeLayersAsync(string plantId, string productCode, string locationCode, string lotNo, decimal qty, string docNo, DateOnly consumedDate, CancellationToken ct = default)
        {
            var layers = await _context.Set<InventoryCostLayer>()
                .Where(l => l.PlantId == plantId && l.ProductCode == productCode && l.LocationCode == locationCode && l.LotNo == lotNo && l.QtyRemaining > 0)
                .OrderBy(l => l.ReceiptDate)
                .ThenBy(l => l.Id)
                .ToListAsync(ct);

            decimal remainingToConsume = qty;
            decimal totalCost = 0;
            var consumptions = new List<InventoryCostLayerConsumption>();

            foreach (var layer in layers)
            {
                if (remainingToConsume <= 0) break;

                var consumedQty = Math.Min(layer.QtyRemaining, remainingToConsume);
                totalCost += consumedQty * layer.UnitCost;

                layer.QtyRemaining -= consumedQty;
                remainingToConsume -= consumedQty;

                // 記下「這一單從這一層扣了多少」——撤回時要靠它還回原層，而不是另建一層。
                consumptions.Add(new InventoryCostLayerConsumption
                {
                    LayerId      = layer.Id,
                    PlantId      = plantId,
                    ProductCode  = productCode,
                    LocationCode = locationCode,
                    LotNo        = lotNo,
                    DocNo        = docNo,
                    ConsumedDate = consumedDate,
                    Qty          = consumedQty,
                    UnitCost     = layer.UnitCost,
                });
            }

            if (remainingToConsume > 0)
            {
                throw new InvalidOperationException(PzText.Get("Inv_FifoLayersInsufficientFmt", remainingToConsume));
            }

            // 全額扣得到才落紀錄：上面不足即擲錯，這裡不會留下半套的消耗軌跡。
            _context.Set<InventoryCostLayerConsumption>().AddRange(consumptions);

            return totalCost / qty; // 返回平均單位成本（針對本次出庫）
        }

        public async Task<decimal> RestoreConsumedAsync(string plantId, string productCode, string locationCode, string lotNo, decimal qty, string docNo, CancellationToken ct = default)
        {
            if (qty <= 0m) return 0m;

            // 後扣的先還：部分撤回時，還回去的是最後被拿走的那一段，與消耗順序對稱。
            var records = await _context.Set<InventoryCostLayerConsumption>()
                .Where(c => c.PlantId == plantId && c.DocNo == docNo
                         && c.ProductCode == productCode && c.LocationCode == locationCode
                         && c.LotNo == lotNo && c.RestoredQty < c.Qty)
                .OrderByDescending(c => c.Id)
                .ToListAsync(ct);
            if (records.Count == 0) return 0m;

            var layerIds = records.Select(r => r.LayerId).Distinct().ToList();
            var layers = await _context.Set<InventoryCostLayer>()
                .Where(l => layerIds.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id, ct);

            var remaining = qty;
            foreach (var rec in records)
            {
                if (remaining <= 0m) break;
                if (!layers.TryGetValue(rec.LayerId, out var layer)) continue;   // 層被刪了（不該發生）

                var give = Math.Min(rec.Qty - rec.RestoredQty, remaining);
                layer.QtyRemaining += give;
                rec.RestoredQty    += give;
                remaining          -= give;
            }

            return qty - remaining;
        }

        public async Task RemoveLayersAsync(string plantId, string productCode, string locationCode, string lotNo, decimal qty, string sourceDocNo, CancellationToken ct = default)
        {
            if (qty <= 0) return;

            var layers = await _context.Set<InventoryCostLayer>()
                .Where(l => l.PlantId == plantId && l.ProductCode == productCode
                         && l.LocationCode == locationCode && l.LotNo == lotNo && l.QtyRemaining > 0)
                .OrderBy(l => l.ReceiptDate)
                .ThenBy(l => l.Id)
                .ToListAsync(ct);

            decimal remaining = qty;

            // 先扣該單自己建立的層：作廢的是這一筆入庫，能還原就精確還原
            foreach (var layer in layers.Where(l => l.SourceDocNo == sourceDocNo))
            {
                if (remaining <= 0) break;
                var take = Math.Min(layer.QtyRemaining, remaining);
                layer.QtyRemaining -= take;
                remaining -= take;
            }

            // 自己那層已被後續出庫扣用掉一部分：不准直接作廢（2026-10-21 使用者裁決）。
            //
            // 原本改按 FIFO 往下扣別的層，數量是對了，但總帳沖回的是原單的凍結成本、成本層扣掉的卻是
            // 別層的成本——總帳存貨與明細帳從此對不上，每作廢一次差距再拉大一次，而現有稽核沒有一條看得到
            // （2026-10-21 隨機序列測試 InventoryLedgerModelTests 種子 2 第 26 步抓到：總帳 3805、成本層 3850）。
            // 被扣用的那部分已經以這一層的成本進了銷貨成本／損失；要撤回，得先作廢扣用它的出庫，
            // 或改用進貨退出／庫存調整。移動平均料不受影響（均價為負時另有保護）。
            if (remaining > 0)
                throw new InvalidOperationException(
                    PzText.Get("Inv_VoidFifoConsumedFmt", sourceDocNo, productCode, locationCode, remaining));
        }
    }
}
