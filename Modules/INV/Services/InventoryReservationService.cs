using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Modules.INV.Services
{
    /// <inheritdoc cref="IInventoryReservationService"/>
    public class InventoryReservationService : IInventoryReservationService
    {
        private readonly ApplicationDbContext _db;

        public InventoryReservationService(ApplicationDbContext db)
        {
            _db = db;
        }

        /// <summary>
        /// 廠級自由可用量＝（在手 − 待驗倉）− 有效的在手預留，下限 0。
        ///
        /// <para><b>客供倉庫存「計入」可用量是刻意的，但那個「刻意」帶著一個前提。</b>
        /// 客供料是客戶提供自己的料做該客戶訂單之用，它本來就該領得出來去做那張單
        /// （`WarehouseType.CustomerSupplied` 排除的是**計價**不是**可用量**，
        /// 見該列舉的文件與 manual-test-plan §13.9）。所以計入是對的——
        /// <b>前提是客供料與消耗它的需求一對一</b>。</para>
        ///
        /// <para><b>前提破掉的那一天</b>：出現第二個客供客戶時，甲客戶供的料會出現在
        /// 同一個 `(PlantId, ProductCode)` 的加總裡，於是 MRP 為**乙**客戶的工單淨算時
        /// 把**甲**的料抵掉，乙的料因此不去買。症狀是「該買的沒買」，而報表上每個數字
        /// 都合理——`Inventory` 沒有任何所有權欄位（只有 `LocationCode` 與 `LotNo`），
        /// 加總看不出那 100 是誰的。</para>
        ///
        /// <para><b>而反過來排除客供倉也是錯的</b>：那會讓 MRP 為甲客戶的工單去買
        /// 甲已經給過我們的料。兩個方向都錯，所以這不是一個開關——
        /// 要對，需求端得知道「這張工單用的是哪個客戶的客供料」，而那條線現在不存在
        /// （客供料走 `SampleDoc`，`EntityCode` 記了客戶，但過帳進 `Inventory` 之後就掉了）。</para>
        ///
        /// <para><b>現行做法</b>：客供倉一客戶一個（`CS-甲`／`CS-乙`，同為
        /// `WarehouseType.CustomerSupplied`），歸屬靠倉別分。在只有一個客供客戶、
        /// 或客供料不經 MRP 淨算之前，計入是正確且成本最低的口徑。
        /// <b>第二個客供客戶出現時必須回來補歸屬</b>——`CustomerSuppliedStockCountsAsAvailableTests`
        /// 釘住的是現況，不是永久結論。</para>
        /// </summary>
        public async Task<decimal> GetPlantAvailableQtyAsync(string plantId, string productCode, CancellationToken ct = default)
        {
            // 排除待驗倉庫存（收貨品質隔離）：IQC 尚未放行的收貨不應被 MRP/領料/出貨預留視為可動用量。
            // 用 WarehouseType 而非 IsMrpWarehouse 判斷——後者歷史上從未被任何邏輯讀取，既有倉庫
            // 多半仍是預設值 false，若改依它過濾會把所有既有倉庫誤判為非 MRP 倉，零庫存可用。
            var quarantineLocationCodes = await _db.StorageLocations
                .Join(_db.Warehouses.Where(w => w.WarehouseType == WarehouseType.Quarantine),
                    l => l.WhCode, w => w.WhCode, (l, w) => l.LocationCode)
                .ToListAsync(ct);

            var onHand = await _db.Inventories
                .Where(i => i.PlantId == plantId && i.ProductCode == productCode
                            && !quarantineLocationCodes.Contains(i.LocationCode))
                .SumAsync(i => (decimal?)i.QtyOnHand, ct) ?? 0m;

            var reserved = await _db.Set<InventoryReservation>()
                .Where(r => r.PlantId == plantId
                            && r.ProductCode == productCode
                            && r.Category == ReservationCategory.OnHand
                            && r.Status == ReservationStatus.Active)
                .SumAsync(r => (decimal?)r.ReservedQty, ct) ?? 0m;

            var available = onHand - reserved;
            return available > 0 ? available : 0m;
        }

        public async Task<IReadOnlyDictionary<string, PlantAvailability>> GetPlantAvailabilityAsync(
            string plantId, IReadOnlyCollection<string> productCodes, CancellationToken ct = default)
        {
            if (productCodes.Count == 0) return new Dictionary<string, PlantAvailability>();
            var codes = productCodes.Distinct().ToList();

            // 與 GetPlantAvailableQtyAsync 同一組條件：排除待驗倉、只扣 Category=OnHand 且 Active 的預留
            var quarantineLocationCodes = await _db.StorageLocations
                .Join(_db.Warehouses.Where(w => w.WarehouseType == WarehouseType.Quarantine),
                    l => l.WhCode, w => w.WhCode, (l, w) => l.LocationCode)
                .ToListAsync(ct);

            var onHand = await _db.Inventories
                .Where(i => i.PlantId == plantId && codes.Contains(i.ProductCode)
                            && !quarantineLocationCodes.Contains(i.LocationCode))
                .GroupBy(i => i.ProductCode)
                .Select(g => new { g.Key, Qty = g.Sum(i => i.QtyOnHand) })
                .ToDictionaryAsync(x => x.Key, x => x.Qty, ct);

            var reserved = await _db.Set<InventoryReservation>()
                .Where(r => r.PlantId == plantId && codes.Contains(r.ProductCode)
                            && r.Category == ReservationCategory.OnHand
                            && r.Status == ReservationStatus.Active)
                .GroupBy(r => r.ProductCode)
                .Select(g => new { g.Key, Qty = g.Sum(r => r.ReservedQty) })
                .ToDictionaryAsync(x => x.Key, x => x.Qty, ct);

            return codes.ToDictionary(c => c,
                c => new PlantAvailability(onHand.GetValueOrDefault(c), reserved.GetValueOrDefault(c)));
        }

        public async Task<decimal> GetReservedBySourceAsync(
            ReservationSourceType sourceType, string sourceDocId, string productCode,
            CancellationToken ct = default)
        {
            return await _db.Set<InventoryReservation>()
                .Where(r => r.SourceType == sourceType
                            && r.SourceDocId == sourceDocId
                            && r.ProductCode == productCode
                            && r.Category == ReservationCategory.OnHand
                            && r.Status == ReservationStatus.Active)
                .SumAsync(r => (decimal?)r.ReservedQty, ct) ?? 0m;
        }

        public async Task<decimal> GetReservedForWorkOrderAsync(
            string workOrderId, string productCode, CancellationToken ct = default)
        {
            var own = await GetReservedBySourceAsync(
                ReservationSourceType.WorkOrderMaterial, workOrderId, productCode, ct);

            // 該工單的備料單（確認時已由工單預留轉移而來）。生產批來源的備料單一單橫跨多工單，
            // 必須逐行認工單再加總，否則會把別張工單的鎖量算成本工單已備妥而誤判「不缺料」。
            var staged = 0m;
            foreach (var (stagingId, headerMatched) in await GetStagingsForWorkOrderAsync(workOrderId, ct))
            {
                // 整張單就是為這張工單開的 → 全單鎖量都算這張工單的，不必逐行過濾
                if (headerMatched)
                {
                    staged += await GetReservedBySourceAsync(
                        ReservationSourceType.MaterialStagingOrder, stagingId, productCode, ct);
                    continue;
                }

                // 生產批備料單：只認掛本工單的明細行
                var lineNos = await GetStagingLineNosAsync(stagingId, workOrderId, productCode, ct);
                if (lineNos.Count == 0) continue;

                staged += await _db.Set<InventoryReservation>()
                    .Where(r => r.SourceType == ReservationSourceType.MaterialStagingOrder
                                && r.SourceDocId == stagingId
                                && r.ProductCode == productCode
                                && r.SourceLineSeq != null
                                && lineNos.Contains(r.SourceLineSeq!.Value)
                                && r.Category == ReservationCategory.OnHand
                                && r.Status == ReservationStatus.Active)
                    .SumAsync(r => (decimal?)r.ReservedQty, ct) ?? 0m;
            }

            return own + staged;
        }

        public async Task<decimal> ReduceBySourceAsync(
            ReservationSourceType sourceType, string sourceDocId, string productCode, decimal qty,
            CancellationToken ct = default)
        {
            if (qty <= 0) return 0m;

            var rows = await _db.Set<InventoryReservation>()
                .Where(r => r.SourceType == sourceType
                            && r.SourceDocId == sourceDocId
                            && r.ProductCode == productCode
                            && r.Category == ReservationCategory.OnHand
                            && r.Status == ReservationStatus.Active)
                .OrderBy(r => r.Id)
                .ToListAsync(ct);

            var remaining = qty;
            foreach (var r in rows)
            {
                if (remaining <= 0) break;
                var take = Math.Min(r.ReservedQty, remaining);
                r.ReservedQty -= take;
                r.UpdatedAt = DateTime.UtcNow;
                if (r.ReservedQty <= 0)
                    r.Status = ReservationStatus.Released;
                remaining -= take;
            }

            await _db.SaveChangesAsync(ct);
            return qty - remaining;
        }

        /// <summary>
        /// 該工單相關的備料單（依單號排序，先開的先消耗）。
        /// <c>HeaderMatched</c> 表示整張單就是為這張工單開的（表頭 SourceType=WorkOrder）——
        /// 此時明細即使沒填 SourceWorkOrderId（舊資料）也算這張工單的；
        /// 生產批來源的備料單則一單橫跨多工單，只認有填 SourceWorkOrderId 的那些行。
        /// </summary>
        private async Task<List<(string StagingId, bool HeaderMatched)>> GetStagingsForWorkOrderAsync(
            string workOrderId, CancellationToken ct)
        {
            var rows = await _db.Set<MaterialStagingOrder>()
                .Where(o => (o.SourceType == "WorkOrder" && o.SourceId == workOrderId)
                         || o.Lines.Any(l => l.SourceWorkOrderId == workOrderId))
                .OrderBy(o => o.StagingId)
                .Select(o => new { o.StagingId, o.SourceType, o.SourceId })
                .ToListAsync(ct);

            return rows
                .Select(o => (o.StagingId, o.SourceType == "WorkOrder" && o.SourceId == workOrderId))
                .ToList();
        }

        /// <summary>某備料單中掛在指定工單、且同品號的明細行號（生產批備料單逐行認工單用）。</summary>
        private Task<List<int>> GetStagingLineNosAsync(
            string stagingId, string workOrderId, string productCode, CancellationToken ct) =>
            _db.Set<MaterialStagingLine>()
                .Where(l => l.StagingId == stagingId
                            && l.ProductCode == productCode
                            && l.SourceWorkOrderId == workOrderId)
                .Select(l => l.LineNo)
                .ToListAsync(ct);

        /// <summary>
        /// 扣減某備料單中「屬於指定工單」之明細的 Active 在手預留（依明細行號比對 SourceLineSeq）。
        /// 與 <see cref="ReduceBySourceAsync"/> 同樣由舊到新逐筆扣、歸零轉 Released，
        /// 差別是多一層行號過濾，避免生產批備料單把別張工單的同品號鎖量一起扣掉。
        /// 整張單都屬於同一工單時不走這裡（見 <see cref="ConsumeForWorkOrderAsync"/>）。
        /// </summary>
        private async Task<decimal> ReduceStagingForWorkOrderAsync(
            string stagingId, string workOrderId, string productCode, decimal qty,
            CancellationToken ct)
        {
            if (qty <= 0) return 0m;

            var lineNos = await GetStagingLineNosAsync(stagingId, workOrderId, productCode, ct);
            if (lineNos.Count == 0) return 0m;

            var rows = await _db.Set<InventoryReservation>()
                .Where(r => r.SourceType == ReservationSourceType.MaterialStagingOrder
                            && r.SourceDocId == stagingId
                            && r.ProductCode == productCode
                            && r.SourceLineSeq != null
                            && lineNos.Contains(r.SourceLineSeq!.Value)
                            && r.Category == ReservationCategory.OnHand
                            && r.Status == ReservationStatus.Active)
                .OrderBy(r => r.Id)
                .ToListAsync(ct);

            var remaining = qty;
            foreach (var r in rows)
            {
                if (remaining <= 0) break;
                var take = Math.Min(r.ReservedQty, remaining);
                r.ReservedQty -= take;
                r.UpdatedAt = DateTime.UtcNow;
                if (r.ReservedQty <= 0)
                    r.Status = ReservationStatus.Released;
                remaining -= take;
            }

            await _db.SaveChangesAsync(ct);
            return qty - remaining;
        }

        public async Task<decimal> ConsumeForWorkOrderAsync(
            string workOrderId, string productCode, decimal qty, CancellationToken ct = default)
        {
            if (qty <= 0) return 0m;

            var remaining = qty;

            // 先消耗備料單預留：備料確認時工單預留已等額轉移過去，實際領的也是那批已備妥的料；
            // 反過來先扣工單預留會讓備料單的鎖量變成沒有對應庫存的孤兒，永遠釋放不掉。
            foreach (var (stagingId, headerMatched) in await GetStagingsForWorkOrderAsync(workOrderId, ct))
            {
                if (remaining <= 0) break;
                remaining -= headerMatched
                    // 整張單都是這張工單的 → 全單扣減
                    ? await ReduceBySourceAsync(
                        ReservationSourceType.MaterialStagingOrder, stagingId, productCode, remaining, ct)
                    // 生產批備料單 → 只扣掛本工單的明細行，別張工單的鎖量不能動
                    : await ReduceStagingForWorkOrderAsync(
                        stagingId, workOrderId, productCode, remaining, ct);
            }

            if (remaining > 0)
                remaining -= await ReduceBySourceAsync(
                    ReservationSourceType.WorkOrderMaterial, workOrderId, productCode, remaining, ct);

            return qty - remaining;
        }

        public async Task EnsureShipmentWithinFreeStockAsync(string plantId,
            IEnumerable<(string ProductCode, string? SalesOrderId, int? SoLineNo, decimal Qty)> lines,
            CancellationToken ct = default)
        {
            foreach (var g in lines.Where(l => l.Qty > 0).GroupBy(l => l.ProductCode))
            {
                var reserved = await _db.Set<InventoryReservation>()
                    .Where(r => r.PlantId == plantId && r.ProductCode == g.Key
                             && r.Category == ReservationCategory.OnHand && r.Status == ReservationStatus.Active)
                    .SumAsync(r => (decimal?)r.ReservedQty, ct) ?? 0m;
                if (reserved <= 0) continue;   // 沒有任何預留：維持原本只看儲位庫存的行為

                var own = 0m;
                foreach (var (so, line) in g.Where(l => !string.IsNullOrEmpty(l.SalesOrderId) && l.SoLineNo.HasValue)
                                            .Select(l => (l.SalesOrderId!, l.SoLineNo!.Value)).Distinct())
                    own += await ReservedForSalesOrderLineAsync(so, line, g.Key, ct);

                var quarantine = await _db.StorageLocations
                    .Join(_db.Warehouses.Where(w => w.WarehouseType == WarehouseType.Quarantine),
                        l => l.WhCode, w => w.WhCode, (l, w) => l.LocationCode)
                    .ToListAsync(ct);
                var onHand = await _db.Inventories
                    .Where(i => i.PlantId == plantId && i.ProductCode == g.Key && !quarantine.Contains(i.LocationCode))
                    .SumAsync(i => (decimal?)i.QtyOnHand, ct) ?? 0m;

                var others = Math.Max(0m, reserved - own);
                var shippable = Math.Max(0m, onHand - others);
                var needed = g.Sum(l => l.Qty);
                if (needed > shippable)
                    throw new InvalidOperationException(PzText.Get("Inv_ShipReservedForOthersFmt",
                        g.Key, Q(needed), Q(shippable), Q(others)));
            }

            static string Q(decimal d) => d.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>某銷貨訂單列（經 MRP 需求列）目前為該品號保留的在手量。口徑同 <see cref="ConsumeForSalesOrderLineAsync"/>。</summary>
        private async Task<decimal> ReservedForSalesOrderLineAsync(string salesOrderId, int lineNo, string productCode, CancellationToken ct)
        {
            var runLines = await _db.Set<MFG.Models.MrpRunLine>().AsNoTracking()
                .Where(l => l.SalesOrderId == salesOrderId && l.SalesOrderLineSeq == lineNo && l.ProductCode == productCode)
                .Select(l => new { l.MrpRunId, l.LineSeq })
                .ToListAsync(ct);
            if (runLines.Count == 0) return 0m;
            var runIds = runLines.Select(l => l.MrpRunId).Distinct().ToList();
            return (await _db.Set<InventoryReservation>().AsNoTracking()
                    .Where(r => r.SourceType == ReservationSourceType.MrpRunLine && runIds.Contains(r.SourceDocId)
                             && r.ProductCode == productCode && r.Category == ReservationCategory.OnHand
                             && r.Status == ReservationStatus.Active)
                    .Select(r => new { r.SourceDocId, r.SourceLineSeq, r.ReservedQty })
                    .ToListAsync(ct))
                .Where(r => runLines.Any(l => l.MrpRunId == r.SourceDocId && l.LineSeq == r.SourceLineSeq))
                .Sum(r => r.ReservedQty);
        }

        public async Task<decimal> ConsumeForSalesOrderLineAsync(
            string salesOrderId, int lineNo, string productCode, decimal qty, CancellationToken ct = default)
        {
            if (qty <= 0) return 0m;
            var runLines = await _db.Set<MFG.Models.MrpRunLine>().AsNoTracking()
                .Where(l => l.SalesOrderId == salesOrderId && l.SalesOrderLineSeq == lineNo && l.ProductCode == productCode)
                .Select(l => new { l.MrpRunId, l.LineSeq })
                .ToListAsync(ct);
            if (runLines.Count == 0) return 0m;
            var runIds = runLines.Select(l => l.MrpRunId).Distinct().ToList();

            var rows = (await _db.Set<InventoryReservation>()
                    .Where(r => r.SourceType == ReservationSourceType.MrpRunLine
                             && runIds.Contains(r.SourceDocId)
                             && r.ProductCode == productCode
                             && r.Category == ReservationCategory.OnHand
                             && r.Status == ReservationStatus.Active)
                    .OrderBy(r => r.Id)
                    .ToListAsync(ct))
                .Where(r => runLines.Any(l => l.MrpRunId == r.SourceDocId && l.LineSeq == r.SourceLineSeq))
                .ToList();

            var remaining = qty;
            foreach (var r in rows)
            {
                if (remaining <= 0) break;
                var take = Math.Min(r.ReservedQty, remaining);
                r.ReservedQty -= take;
                r.UpdatedAt = DateTime.UtcNow;
                if (r.ReservedQty <= 0) r.Status = ReservationStatus.Consumed;
                remaining -= take;
            }
            await _db.SaveChangesAsync(ct);
            return qty - remaining;
        }

        public async Task<decimal> RestoreForSalesOrderLineAsync(
            string plantId, string salesOrderId, int lineNo, string productCode, decimal qty, CancellationToken ct = default)
        {
            if (qty <= 0) return 0m;
            var soLine = await _db.Set<ORD.Models.SalesOrderLine>().AsNoTracking()
                .FirstOrDefaultAsync(l => l.SalesOrderId == salesOrderId && l.LineNo == lineNo, ct);
            if (soLine == null || soLine.IsClosed) return 0m;
            var runLines = await _db.Set<MFG.Models.MrpRunLine>().AsNoTracking()
                .Where(l => l.SalesOrderId == salesOrderId && l.SalesOrderLineSeq == lineNo && l.ProductCode == productCode)
                .OrderByDescending(l => l.MrpRunId)
                .Select(l => new { l.MrpRunId, l.LineSeq, l.RequiredDate })
                .ToListAsync(ct);
            if (runLines.Count == 0) return 0m;

            // 上限＝訂單列尚未出貨量 − 已鎖給它的量：出貨時沒有消耗到預留（例如那批貨本來就不是為它鎖的），
            // 作廢不能憑空多鎖出一筆；呼叫端須先把已出貨量回沖完再呼叫。
            var runIds = runLines.Select(l => l.MrpRunId).Distinct().ToList();
            var active = (await _db.Set<InventoryReservation>().AsNoTracking()
                    .Where(r => r.SourceType == ReservationSourceType.MrpRunLine
                             && runIds.Contains(r.SourceDocId)
                             && r.ProductCode == productCode
                             && r.Category == ReservationCategory.OnHand
                             && r.Status == ReservationStatus.Active)
                    .Select(r => new { r.SourceDocId, r.SourceLineSeq, r.ReservedQty })
                    .ToListAsync(ct))
                .Where(r => runLines.Any(l => l.MrpRunId == r.SourceDocId && l.LineSeq == r.SourceLineSeq))
                .Sum(r => r.ReservedQty);
            var lockQty = Math.Min(qty, Math.Max(0m, soLine.StockQty - soLine.ShippedStockQty - active));
            if (lockQty <= 0) return 0m;

            var latest = runLines[0];
            await ReserveAsync(plantId, productCode, lockQty, ReservationSourceType.MrpRunLine,
                latest.MrpRunId, latest.LineSeq, latest.RequiredDate, ct);
            return lockQty;
        }

        public async Task<int> ReleaseWorkOrderStagingAsync(string workOrderId, CancellationToken ct = default)
        {
            var released = 0;
            foreach (var (stagingId, headerMatched) in await GetStagingsForWorkOrderAsync(workOrderId, ct))
            {
                if (headerMatched)
                {
                    // 整張單都是這張工單的 → 全數釋放
                    released += await ReleaseBySourceAsync(
                        ReservationSourceType.MaterialStagingOrder, stagingId, ct);
                    continue;
                }

                // 生產批備料單：只釋放屬於這張工單的明細行，其他工單的鎖量不能動
                var lineNos = await _db.Set<MaterialStagingLine>()
                    .Where(l => l.StagingId == stagingId && l.SourceWorkOrderId == workOrderId)
                    .Select(l => l.LineNo)
                    .ToListAsync(ct);

                foreach (var lineNo in lineNos)
                    released += await ReleaseBySourceLineAsync(
                        ReservationSourceType.MaterialStagingOrder, stagingId, lineNo, ct);
            }
            return released;
        }

        public async Task<decimal> GetAvailableInTransitAsync(
            string plantId, string productCode, DateOnly byDate, CancellationToken ct = default)
        {
            // 已核准未結案 PO 行的在途量，到貨日 ≤ byDate。
            // 一律用**庫存單位**（StockQty − ReceivedStockQty）：回傳值要與 GetPlantAvailableQtyAsync（庫存單位）相加，
            // 原本取 OrderQty − ReceivedQty（採購單位），採購單位與庫存單位不同時（例：採購 KG、庫存 g）差上千倍（2026-10-27 修正）。
            var inTransit = await _db.Set<PUR.Models.PurchaseOrderLine>()
                .Where(l => l.ProductCode == productCode
                         && !l.IsClosed
                         && l.ReceivedStockQty < l.StockQty
                         && l.DeliveryDate.HasValue
                         && l.DeliveryDate.Value <= byDate)
                .Join(_db.Set<PUR.Models.PurchaseOrder>()
                        .Where(po => po.PlantId == plantId
                                  && po.ApprovalStatus == DocumentApprovalStatus.Approved),
                    l => l.PurchaseOrderId,
                    po => po.PurchaseOrderId,
                    (l, po) => l.StockQty - l.ReceivedStockQty)
                .SumAsync(x => (decimal?)x, ct) ?? 0m;

            // 已 peg 之 Active 採購 Supply 預留（避免重複計同一批在途；委外 peg 不在此扣）
            var pegged = await _db.Set<InventoryReservation>()
                .Where(r => r.PlantId == plantId
                         && r.ProductCode == productCode
                         && r.Category == ReservationCategory.Supply
                         && r.Status == ReservationStatus.Active
                         && r.SupplyDocType == "PurchaseOrder")
                .SumAsync(r => (decimal?)r.ReservedQty, ct) ?? 0m;

            var available = inTransit - pegged;
            return available > 0 ? available : 0m;
        }

        public async Task<decimal> GetAvailableOutsourceInTransitAsync(
            string plantId, string productCode, DateOnly byDate, CancellationToken ct = default)
        {
            // 已確認(未作廢)委外採購單行，委外應交日 ≤ byDate
            var opoLines = await _db.Set<MFG.Models.OutsourcePurchaseOrderLine>()
                .Where(l => l.ProductCode == productCode)
                .Join(_db.Set<MFG.Models.OutsourcePurchaseOrder>()
                        .Where(o => o.PlantId == plantId
                                 && o.Status == MFG.Models.OutsourcePoStatus.Confirmed
                                 && o.ExpectedDeliveryDate.HasValue
                                 && o.ExpectedDeliveryDate.Value <= byDate),
                    l => l.OutsourcePurchaseOrderId,
                    o => o.OutsourcePurchaseOrderId,
                    (l, o) => new { l.OutsourcePurchaseOrderId, l.LineNo, l.Qty })
                .ToListAsync(ct);

            decimal inTransit = 0m;
            if (opoLines.Count > 0)
            {
                var opoIds = opoLines.Select(x => x.OutsourcePurchaseOrderId).Distinct().ToList();
                var recv = await OutsourceReceivedByLineAsync(opoIds, ct);
                foreach (var l in opoLines)
                {
                    recv.TryGetValue((l.OutsourcePurchaseOrderId, l.LineNo), out var received);
                    var outstanding = l.Qty - received;
                    if (outstanding > 0) inTransit += outstanding;
                }
            }

            // 已 peg 之 Active 委外 Supply 預留
            var pegged = await _db.Set<InventoryReservation>()
                .Where(r => r.PlantId == plantId
                         && r.ProductCode == productCode
                         && r.Category == ReservationCategory.Supply
                         && r.Status == ReservationStatus.Active
                         && r.SupplyDocType == "OutsourcePurchaseOrder")
                .SumAsync(r => (decimal?)r.ReservedQty, ct) ?? 0m;

            var available = inTransit - pegged;
            return available > 0 ? available : 0m;
        }

        /// <summary>委外採購單各行已收量（Posted 委外進貨明細之 ReceiptQty，依 OpoLineNo 彙總）。</summary>
        private async Task<Dictionary<(string, int), decimal>> OutsourceReceivedByLineAsync(
            List<string> opoIds, CancellationToken ct)
        {
            var rows = await _db.Set<PUR.Models.OutsourceReceiptLine>()
                .Join(_db.Set<PUR.Models.OutsourceReceipt>().Where(r => r.Status == DocStatus.Posted),
                    orl => orl.OutsourceReceiptId,
                    or => or.OutsourceReceiptId,
                    (orl, or) => new { or.OutsourcePurchaseOrderId, orl.OpoLineNo, orl.ReceiptQty })
                .Where(x => opoIds.Contains(x.OutsourcePurchaseOrderId) && x.OpoLineNo != null)
                .GroupBy(x => new { x.OutsourcePurchaseOrderId, x.OpoLineNo })
                .Select(g => new { g.Key.OutsourcePurchaseOrderId, g.Key.OpoLineNo, Recv = g.Sum(x => x.ReceiptQty) })
                .ToListAsync(ct);
            return rows.ToDictionary(x => (x.OutsourcePurchaseOrderId, x.OpoLineNo!.Value), x => x.Recv);
        }

        public async Task<InventoryReservation> PegSupplyAsync(
            string plantId, string productCode, decimal qty,
            ReservationSourceType sourceType, string sourceDocId, int? sourceLineSeq, DateTime requiredDate,
            string supplyDocType, string supplyDocId, int? supplyLineNo, DateOnly? expectedArrivalDate,
            CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            var reservation = new InventoryReservation
            {
                PlantId = plantId,
                ProductCode = productCode,
                ReservedQty = qty,
                SourceType = sourceType,
                SourceDocId = sourceDocId,
                SourceLineSeq = sourceLineSeq,
                RequiredDate = requiredDate,
                Category = ReservationCategory.Supply,
                SupplyDocType = supplyDocType,
                SupplyDocId = supplyDocId,
                SupplyLineNo = supplyLineNo,
                ExpectedArrivalDate = expectedArrivalDate.HasValue
                    ? expectedArrivalDate.Value.ToDateTime(TimeOnly.MinValue)
                    : null,
                Status = ReservationStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            };

            _db.Set<InventoryReservation>().Add(reservation);
            await _db.SaveChangesAsync(ct);
            return reservation;
        }

        public async Task<int> ConvertSupplyOnArrivalAsync(
            string supplyDocType, string supplyDocId, int? supplyLineNo, CancellationToken ct = default)
        {
            var rows = await _db.Set<InventoryReservation>()
                .Where(r => r.Category == ReservationCategory.Supply
                         && r.Status == ReservationStatus.Active
                         && r.SupplyDocType == supplyDocType
                         && r.SupplyDocId == supplyDocId
                         && (supplyLineNo == null || r.SupplyLineNo == supplyLineNo))
                .ToListAsync(ct);

            foreach (var r in rows)
            {
                // 在途已實際入庫：peg 轉為在手預留（鎖在手），不再計入「可用在途」
                r.Category = ReservationCategory.OnHand;
                r.UpdatedAt = DateTime.UtcNow;
            }

            if (rows.Count > 0)
                await _db.SaveChangesAsync(ct);

            return rows.Count;
        }

        public async Task<int> RevertSupplyOnUnreceiveAsync(
            string supplyDocType, string supplyDocId, int? supplyLineNo, CancellationToken ct = default)
        {
            var rows = await _db.Set<InventoryReservation>()
                .Where(r => r.Category == ReservationCategory.OnHand
                         && r.Status == ReservationStatus.Active
                         && r.SupplyDocType == supplyDocType
                         && r.SupplyDocId == supplyDocId
                         && (supplyLineNo == null || r.SupplyLineNo == supplyLineNo))
                .ToListAsync(ct);

            foreach (var r in rows)
            {
                // 入庫回沖：先前到貨轉 OnHand 的 peg 還原為在途（Supply）
                r.Category = ReservationCategory.Supply;
                r.UpdatedAt = DateTime.UtcNow;
            }

            if (rows.Count > 0)
                await _db.SaveChangesAsync(ct);

            return rows.Count;
        }

        public async Task<InventoryReservation> ReserveAsync(
            string plantId, string productCode, decimal qty,
            ReservationSourceType sourceType, string sourceDocId, int? sourceLineSeq, DateTime requiredDate,
            CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            var reservation = new InventoryReservation
            {
                PlantId = plantId,
                ProductCode = productCode,
                ReservedQty = qty,
                SourceType = sourceType,
                SourceDocId = sourceDocId,
                SourceLineSeq = sourceLineSeq,
                RequiredDate = requiredDate,
                Status = ReservationStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            };

            _db.Set<InventoryReservation>().Add(reservation);
            // 立即持久化：同一次 MRP 展開中，後續同品號需求的可用量查詢需看得到此筆扣減。
            await _db.SaveChangesAsync(ct);
            return reservation;
        }

        public async Task<int> ReleaseBySourceAsync(ReservationSourceType sourceType, string sourceDocId, CancellationToken ct = default)
        {
            var rows = await _db.Set<InventoryReservation>()
                .Where(r => r.SourceType == sourceType
                            && r.SourceDocId == sourceDocId
                            && r.Status == ReservationStatus.Active)
                .ToListAsync(ct);

            foreach (var r in rows)
            {
                r.Status = ReservationStatus.Released;
                r.UpdatedAt = DateTime.UtcNow;
            }

            if (rows.Count > 0)
                await _db.SaveChangesAsync(ct);

            return rows.Count;
        }

        public async Task<int> ReleaseBySourceLineAsync(
            ReservationSourceType sourceType, string sourceDocId, int lineSeq,
            CancellationToken ct = default)
        {
            var rows = await _db.Set<InventoryReservation>()
                .Where(r => r.SourceType == sourceType
                            && r.SourceDocId == sourceDocId
                            && r.SourceLineSeq == lineSeq
                            && r.Status == ReservationStatus.Active)
                .ToListAsync(ct);

            foreach (var r in rows)
            {
                r.Status = ReservationStatus.Released;
                r.UpdatedAt = DateTime.UtcNow;
            }

            if (rows.Count > 0)
                await _db.SaveChangesAsync(ct);

            return rows.Count;
        }
    }
}
