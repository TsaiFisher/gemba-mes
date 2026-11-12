using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules.INV.Services
{
    public class InventoryService : IInventoryService
    {
        private readonly ApplicationDbContext _context;

        public InventoryService(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// 客供倉的儲位代碼（<see cref="WarehouseType.CustomerSupplied"/>）。
        ///
        /// <para>客供料屬客戶財產，**不參與我們的存貨計價**。它必須從移動平均的
        /// 分母與寫回範圍兩邊都排除：成本池的鍵是 (PlantId, ProductCode)，
        /// 分倉、分儲位、分批號都隔不開——只排除分母不排除寫回，
        /// 會把自有料的均價寫到客供料上（客供料於是「有了成本」）；
        /// 只排除寫回不排除分母，客供數量仍會把自有料單價壓低。</para>
        ///
        /// <para>寫法與 `InventoryReservationService.GetPlantAvailableQtyAsync` 排除待驗倉
        /// 同一個模式（Join Warehouses 取儲位清單）。兩者排除的東西不同：
        /// 待驗倉排可用量、客供倉排計價。</para>
        /// </summary>
        public async Task<bool> IsCustomerSuppliedLocationAsync(string locationCode, CancellationToken ct = default)
            => !string.IsNullOrEmpty(locationCode)
            && (await GetCustomerSuppliedLocationsAsync(ct)).Contains(locationCode);

        private async Task<List<string>> GetCustomerSuppliedLocationsAsync(CancellationToken ct)
            => await _context.StorageLocations
                .IgnoreQueryFilters().AsNoTracking()
                .Join(_context.Warehouses.IgnoreQueryFilters().AsNoTracking()
                        .Where(w => w.WarehouseType == WarehouseType.CustomerSupplied),
                    l => l.WhCode, w => w.WhCode, (l, w) => l.LocationCode)
                .ToListAsync(ct);

        public async Task<decimal> GetQtyOnHandAsync(string plantId, string productCode, string locationCode, string lotNo = "", CancellationToken ct = default)
        {
            var inv = await FindInventoryAsync(plantId, productCode, locationCode, lotNo, ct);
            return inv?.QtyOnHand ?? 0;
        }

        public async Task<decimal> GetAvailableQtyAsync(string plantId, string productCode, string locationCode, string lotNo = "", CancellationToken ct = default)
        {
            var inv = await FindInventoryAsync(plantId, productCode, locationCode, lotNo, ct);
            return inv?.QtyAvailable ?? 0;
        }

        public async Task UpdateQtyAsync(string plantId, string productCode, string locationCode, string lotNo, decimal deltaQty, CancellationToken ct = default)
        {
            var inv = await GetOrCreateInventoryAsync(plantId, productCode, locationCode, lotNo, ct);
            inv.QtyOnHand += deltaQty;

            // 嚴禁負庫存：擋下繞過可用量檢查的呼叫路徑（並發競態則由 Inventory 的
            // xmin 樂觀鎖在 SaveChanges 時以 DbUpdateConcurrencyException 擋下，
            // 呼叫端不得吞掉該例外）。
            if (inv.QtyOnHand < 0)
                throw new InvalidOperationException(
                    PzText.Get("Inv_StockNegativeFmt", locationCode, productCode,
                        string.IsNullOrEmpty(lotNo) ? "" : PzText.Get("Inv_LotSuffixFmt", lotNo), inv.QtyOnHand));

            inv.LastUpdatedAt = DateTime.UtcNow;
        }

        public async Task UpdateMovingAverageCostAsync(string plantId, string productCode, string locationCode, string lotNo, decimal addQty, decimal inUnitCost, CancellationToken ct = default)
        {
            if (addQty > 0 && inUnitCost < 0)
                throw new InvalidOperationException(
                    PzText.Get("Inv_MovingAvgNegativeInFmt", inUnitCost));

            // 出庫不改動移動均價（AVCO 僅於入庫時重算）
            if (addQty <= 0)
                return;

            // 取得當前儲位庫存（主要為了觸發 GetOrCreate 並確保該儲位有紀錄）
            var inv = await GetOrCreateInventoryAsync(plantId, productCode, locationCode, lotNo, ct);

            // 客供料不參與計價：進客供倉的入庫一律留 UnitCost = 0 並直接返回。
            // 客戶的料沒有取得成本，給它一個單價等於把客戶的財產記成我們的存貨價值；
            // 而 UnitCost 恆 0 也讓六處存貨金額報表（評價／週轉／帳齡／呆滯／儀表板）
            // 不必逐一加排除條件就自動正確。
            var csLocations = await GetCustomerSuppliedLocationsAsync(ct);
            if (csLocations.Contains(locationCode))
            {
                inv.UnitCost = 0m;
                inv.LastUpdatedAt = DateTime.UtcNow;
                return;
            }

            // 【新規範】：計算 MA 需考慮全廠總庫存（排除客供倉——那不是我們的存貨）
            var plantStock = await _context.Inventories
                .IgnoreQueryFilters()
                .Where(i => i.PlantId == plantId && i.ProductCode == productCode
                         && !csLocations.Contains(i.LocationCode))
                .Select(i => new { i.QtyOnHand, i.UnitCost })
                .ToListAsync(ct);

            decimal totalQty = plantStock.Sum(s => s.QtyOnHand);
            // 均價通常採一致性，這裡取其中一筆或是加權算出的目前均價
            // 在「廠別級 MA」規範下，全廠同一產品的 UnitCost 應保持同步
            decimal currentMa = plantStock.FirstOrDefault()?.UnitCost ?? 0;

            if (totalQty + addQty != 0)
            {
                decimal newMa = (totalQty * currentMa + addQty * inUnitCost) / (totalQty + addQty);

                // 先直接更新 inv（可能是 EntityState.Added、尚未 SaveChanges 的新紀錄）
                inv.UnitCost = newMa;
                inv.LastUpdatedAt = DateTime.UtcNow;

                // 再更新其他已存在 DB 的同廠同品號儲位（全廠 MA 一致性），排除已更新的 inv。
                // 客供倉一併排除：把自有料的均價寫過去，等於讓客戶的料「有了成本」，
                // 而那筆金額會出現在存貨評價報表裡。
                var allInv = await _context.Inventories
                    .IgnoreQueryFilters()
                    .Where(i => i.PlantId == plantId && i.ProductCode == productCode
                             && !(i.LocationCode == locationCode && i.LotNo == lotNo)
                             && !csLocations.Contains(i.LocationCode))
                    .ToListAsync(ct);

                foreach (var item in allInv)
                {
                    item.UnitCost = newMa;
                    item.LastUpdatedAt = DateTime.UtcNow;
                }
            }
        }

        public async Task ReverseMovingAverageCostAsync(string plantId, string productCode, string locationCode, string lotNo, decimal removeQty, decimal inUnitCost, CancellationToken ct = default)
        {
            if (removeQty <= 0) return;

            await GetOrCreateInventoryAsync(plantId, productCode, locationCode, lotNo, ct);

            // 與 UpdateMovingAverageCostAsync 同一個口徑（含客供倉的排除）：
            // 均價是**全廠**的，不是儲位的；而客供料不參與計價。
            // 作廢客供料的入庫時本來就沒有均價可沖——當初那筆也沒進過均價。
            var csLocations = await GetCustomerSuppliedLocationsAsync(ct);
            if (csLocations.Contains(locationCode)) return;

            var plantStock = await _context.Inventories
                .IgnoreQueryFilters()
                .Where(i => i.PlantId == plantId && i.ProductCode == productCode
                         && !csLocations.Contains(i.LocationCode))
                .Select(i => new { i.QtyOnHand, i.UnitCost })
                .ToListAsync(ct);

            decimal totalQty  = plantStock.Sum(s => s.QtyOnHand);
            decimal currentMa = plantStock.FirstOrDefault()?.UnitCost ?? 0;
            decimal remaining = totalQty - removeQty;

            // 沖完就沒庫存了：此刻帳上剩的價值必須剛好等於要收回的價值，否則總帳存貨會殘留一筆
            // 數量為 0、永遠沖不掉的餘額（總帳照原單沖 removeQty×inUnitCost，明細帳只剩 totalQty×currentMa）。
            // 兩者不等＝這張入庫已與別的入庫混算均價、又被後續出庫用掉——與 FIFO 已扣用的入庫同一件事，
            // 一樣擋下，請先處理後續出庫（2026-10-21 使用者裁決；隨機序列測試 InventoryLedgerModelTests 種子 56 抓到：
            // 剩 112.76、要收回 160，原本放行後總帳殘留 −47.24）。
            // 相等時均價留在原值即可——下一筆入庫的分母是 0，新均價會直接等於那筆的單價。
            if (remaining <= 0)
            {
                // 沒有凍結單價的舊異動（inUnitCost 以 0 處理）：沖銷本來就不動價值，維持原行為放行
                if (inUnitCost != 0m && Math.Abs(totalQty * currentMa - removeQty * inUnitCost) > 0.01m)
                    throw new InvalidOperationException(
                        PzText.Get("Inv_VoidMovingAvgResidualFmt", productCode, removeQty, inUnitCost, totalQty, currentMa));
                return;
            }

            decimal newMa = (totalQty * currentMa - removeQty * inUnitCost) / remaining;

            // 負均價只可能來自「作廢之前帳就已經不一致」（例如均價被別的路徑改過）。
            // 這裡不靜靜地把它寫進去——負存貨單價會讓之後每一筆出庫的 COGS 變成負數。
            if (newMa < 0)
                throw new InvalidOperationException(
                    PzText.Get("Inv_VoidMovingAvgNegativeFmt", productCode, removeQty, inUnitCost, totalQty, currentMa));

            var allInv = await _context.Inventories
                .IgnoreQueryFilters()
                .Where(i => i.PlantId == plantId && i.ProductCode == productCode
                         && !csLocations.Contains(i.LocationCode))
                .ToListAsync(ct);
            foreach (var item in allInv)
            {
                item.UnitCost = newMa;
                item.LastUpdatedAt = DateTime.UtcNow;
            }
        }

        public async Task<StorageLocation?> GetLocationAsync(string locationCode, CancellationToken ct = default)
        {
            return await _context.StorageLocations
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(sl => sl.LocationCode == locationCode, ct);
        }

        public async Task<Inventory> GetOrCreateInventoryAsync(string plantId, string productCode, string locationCode, string lotNo, CancellationToken ct = default)
        {
            var inv = await FindInventoryAsync(plantId, productCode, locationCode, lotNo, ct);
            if (inv != null) return inv;

            inv = new Inventory
            {
                PlantId = plantId,
                ProductCode = productCode,
                LocationCode = locationCode,
                LotNo = lotNo,
                // 新開的儲位庫存承接該廠現行單位成本，不從 0 起算。
                //
                // 「廠別級 MA」的前提是全廠同一品號的 UnitCost 一致（見
                // UpdateMovingAverageCostAsync：重算時直接取 plantStock.FirstOrDefault()
                // 當現行均價）。從 0 起算會同時壞掉兩件事：
                //   ① 沒有帶單價的入庫——生產／委外／重工退料、儲位調撥的入庫腳——
                //      計價時取的就是這一列的 UnitCost，料退回倉庫了、成本卻是 0，
                //      工單被多扣一次料錢，而生產類異動根本不入總帳（BuildJournalLines
                //      對 Production/Outsource 回空），沒有任何一張傳票會不平衡。
                //   ② 全廠出現兩種單位成本後，下一次入庫重算均價會取到「任意一列」，
                //      成本自此隨資料列順序漂移。
                UnitCost = await ResolvePlantUnitCostAsync(plantId, productCode, ct),
            };
            _context.Inventories.Add(inv);
            return inv;
        }

        /// <summary>
        /// 該廠該品號的現行單位成本；全新品號（全廠尚無任何庫存列）回 0，
        /// 由入庫端的移動均價／成本層計算接手。
        ///
        /// 以在手量高者優先，只是為了在既有部署已經出現成本分歧時取一個有意義的值，
        /// 不是預期會有分歧——不變式仍然是「全廠一致」。
        /// </summary>
        private async Task<decimal> ResolvePlantUnitCostAsync(
            string plantId, string productCode, CancellationToken ct)
        {
            var persisted = await _context.Inventories
                .IgnoreQueryFilters()
                .Where(i => i.PlantId == plantId && i.ProductCode == productCode)
                .OrderByDescending(i => i.QtyOnHand)
                .Select(i => (decimal?)i.UnitCost)
                .FirstOrDefaultAsync(ct);
            if (persisted.HasValue) return persisted.Value;

            // 同一次過帳的前幾行剛建的列還沒進資料庫，只存在追蹤器裡。
            return _context.Inventories.Local
                .Where(i => i.PlantId == plantId && i.ProductCode == productCode)
                .OrderByDescending(i => i.QtyOnHand)
                .Select(i => i.UnitCost)
                .FirstOrDefault();
        }

        private async Task<Inventory?> FindInventoryAsync(string plantId, string productCode, string locationCode, string lotNo, CancellationToken ct)
        {
            // 先查 Change Tracker（含未 SaveChanges 的 Add 狀態實體）
            var local = _context.Inventories.Local
                .FirstOrDefault(i =>
                    i.PlantId == plantId &&
                    i.ProductCode == productCode &&
                    i.LocationCode == locationCode &&
                    i.LotNo == lotNo);
            if (local != null) return local;

            return await _context.Inventories
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(i =>
                    i.PlantId == plantId &&
                    i.ProductCode == productCode &&
                    i.LocationCode == locationCode &&
                    i.LotNo == lotNo, ct);
        }
    }
}
