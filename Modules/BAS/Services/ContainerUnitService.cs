using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Modules.BAS.Services
{
    /// <summary>箱子目前內容的一列：某儲位上綁定批號的在手量。</summary>
    public sealed record ContainerUnitStock(string LocationCode, decimal Qty);

    /// <summary>
    /// 箱子在廠外：綁定批號是被哪一張「出廠」單據出完的（<see cref="ContainerUnitService.LeavePlantTypeCodes"/>）。
    /// <paramref name="Party"/>＝對象（客戶／供應商代碼，跨廠調撥為目的廠），查不到為 null。
    /// </summary>
    public sealed record ContainerUnitOffsite(string TypeCode, string DocNo, DateOnly MovementDate, string? Party);

    /// <summary>
    /// 跟著某張單據出廠的一箱：單據出掉的批號＋數量（扣掉作廢回沖）。<paramref name="Recovered"/>＝箱子已不再綁這個批號
    /// （回收、清空或改裝別的貨）。
    /// </summary>
    public sealed record ContainerUnitLeft(string UnitId, string ProductCode, string LotNo, decimal Qty, bool Recovered);

    /// <summary>回收一個箱子的結果；<paramref name="Error"/> 為 null＝成功。</summary>
    public sealed record ContainerUnitRecoverResult(string UnitId, string? Error);

    /// <summary>
    /// 箱子（容器個體）規則的單一入口（container-tracking.md §9）。
    /// 箱內數量一律從庫存帳推導（綁定批號在本廠的在手量），本服務不記數量。
    /// 在製箱（報工裝箱，§9.4）的數量則從報工容器紀錄推導，工單結案／作廢後視為空箱。
    /// </summary>
    public interface IContainerUnitService
    {
        /// <summary>批次建立箱子，箱號＝前綴-五位流水號（接續該前綴目前最大號，全廠共用不撞號）。</summary>
        Task<IReadOnlyList<string>> CreateBatchAsync(string containerCode, string prefix, int count, string? userId, CancellationToken ct = default);

        /// <summary>綁定批號在本廠的在手量合計；未綁定回 0。</summary>
        Task<decimal> BoundQtyAsync(ContainerUnit unit, CancellationToken ct = default);

        /// <summary>
        /// 在製箱目前的數量：這次綁定（<see cref="ContainerUnit.BoundAt"/> 起）以來，同工單同工序裝進這箱的報工合格數。
        /// 是「放進去時的紀錄」不是滾動餘額（§9.5）；不在製回 0。
        /// </summary>
        Task<decimal> WipQtyAsync(ContainerUnit unit, CancellationToken ct = default);

        /// <summary>在製綁定是否仍有效：有綁工單，且工單未結案、未作廢。</summary>
        Task<bool> IsInWipAsync(ContainerUnit unit, CancellationToken ct = default);

        /// <summary>依箱號找本廠的箱子（追蹤中）；不是箱號回 null。報工容器欄掃描用。</summary>
        Task<ContainerUnit?> FindAsync(string unitId, CancellationToken ct = default);

        /// <summary>
        /// 報工裝箱：把箱子綁到工單＋工序（不存檔，與報工同交易）。可裝的箱子：啟用，且是空箱、
        /// 或已綁同一張工單（同工序＝續裝累加；別的工序＝上一站整箱交過來，改綁到這一站）。
        /// 裝著庫存批號或別張工單在製品的一律丟 <see cref="InvalidOperationException"/>（不混裝，§9.5）。
        /// <paramref name="now"/> 須與報工紀錄的 CreatedAt 相同，<see cref="WipQtyAsync"/> 才數得到這一筆。
        /// </summary>
        Task LoadWipAsync(ContainerUnit unit, string workOrderId, int operationSeq, string productCode,
            decimal qty, string? userId, DateTime now, CancellationToken ct = default);

        /// <summary>
        /// 箱子是否在廠外（§9.4 出廠）：綁定批號在本廠已出完，且最後一筆出庫是出貨、委外發料、借出、
        /// 樣品出貨、退貨給供應商或跨廠調撥出等「離開本廠」的異動；否則回 null。
        /// 與在手量同樣是判斷時才推導，過帳不掛勾。
        /// </summary>
        Task<ContainerUnitOffsite?> OffsiteAsync(ContainerUnit unit, CancellationToken ct = default);

        /// <summary>
        /// 回收（容器回收作業）：箱子回到本廠、成為空箱。可回收：本廠在廠外的箱子；或別廠的箱子
        /// （跟貨過來，該廠不能還裝著貨或在製）——改屬本廠，若綁定批號在本廠有庫存（跨廠調撥收到了）
        /// 就保留綁定、直接是「使用中」。逐一處理、各自存檔，一個不行不影響其他。
        /// </summary>
        Task<IReadOnlyList<ContainerUnitRecoverResult>> RecoverAsync(IReadOnlyList<string> unitIds, string? userId, CancellationToken ct = default);

        /// <summary>
        /// 跟著這張單據出庫的箱子（單據上的「出廠箱號」）：單據在本廠出庫的批號，對到裝過該批號的箱子（歷程或目前綁定）
        /// （一箱一批號、每次裝箱都是新批號，所以對得回唯一一箱）。出庫被作廢回沖掉的不列；沒有箱子的批號不列。
        /// </summary>
        Task<IReadOnlyList<ContainerUnitLeft>> LeftWithDocAsync(string docNo, CancellationToken ct = default);

        /// <summary>本廠這個品號是否已用過這個批號（有任何異動）。裝箱的批號必須是新的，箱內數量才等於「這一箱」。</summary>
        Task<bool> LotUsedAsync(string productCode, string lotNo, CancellationToken ct = default);

        /// <summary>裝箱用的新批號：<paramref name="prefix"/>-01、-02…取第一個本廠此品號沒用過的。</summary>
        Task<string> NextLotAsync(string productCode, string prefix, CancellationToken ct = default);

        /// <summary>綁定批號在各儲位的在手量（只列非零）。</summary>
        Task<IReadOnlyList<ContainerUnitStock>> ContentAsync(ContainerUnit unit, CancellationToken ct = default);

        /// <summary>
        /// 取出要裝貨的箱子並逐一檢查：存在、啟用、種類相符（有指定時）、不重複、目前是空箱
        /// （未綁定，或綁定批號已出完）。任何一個不符就丟 <see cref="InvalidOperationException"/>，一個都不綁。
        /// 指定 <paramref name="workOrderId"/> 時，裝著該工單在製品的箱子也可以（完工入庫：在製箱直接轉成在庫）。
        /// 回傳的實體是追蹤中的，呼叫端在同一交易內 <see cref="Bind"/> 後存檔（xmin 擋並發綁定）。
        /// </summary>
        Task<IReadOnlyList<ContainerUnit>> TakeEmptyForBindingAsync(IReadOnlyList<string> unitIds, string? containerCode,
            string? workOrderId = null, CancellationToken ct = default);

        /// <summary>把箱子綁到新批號並寫歷程（不存檔）。</summary>
        void Bind(ContainerUnit unit, string productCode, string lotNo, decimal qty, string? docNo, string? userId);

        /// <summary>手動清空：解除綁定（實物已倒出但帳上還有的情況）。庫存帳不動。</summary>
        Task ClearAsync(string unitId, string? userId, CancellationToken ct = default);

        Task SetActiveAsync(string unitId, bool active, string? userId, CancellationToken ct = default);

        /// <summary>
        /// 本廠某種類目前可用（空箱）的箱號，依箱號排序；完工入庫「自動帶入空箱」用。
        /// 指定 <paramref name="workOrderId"/> 時，裝著該工單在製品的箱子排在最前（後段工序優先）。
        /// </summary>
        Task<IReadOnlyList<string>> EmptyUnitIdsAsync(string containerCode, int take, string? workOrderId = null, CancellationToken ct = default);
    }

    public class ContainerUnitService : IContainerUnitService
    {
        public const int MaxBatch = 500;

        /// <summary>
        /// 「離開本廠」的出庫異動類型：出貨、樣品出貨、客供退回、借出、借入退回、進貨退出、
        /// 委外進貨退出、委外發料、內部訂單出、跨廠調撥出。箱子的批號被這些異動出完＝箱子跟貨走了。
        /// 其餘出庫（領料、報廢、同廠調撥…）出完＝箱子還在廠內、是空箱。
        /// </summary>
        public static readonly string[] LeavePlantTypeCodes = { "GS", "SO", "CR", "IL", "BR", "PF", "OT", "OO", "NO", "XP" };
        private static readonly Regex PrefixPattern = new("^[A-Za-z0-9]{1,10}$", RegexOptions.Compiled);

        private readonly ApplicationDbContext _db;

        public ContainerUnitService(ApplicationDbContext db) => _db = db;

        public async Task<IReadOnlyList<string>> CreateBatchAsync(
            string containerCode, string prefix, int count, string? userId, CancellationToken ct = default)
        {
            prefix = (prefix ?? "").Trim().ToUpperInvariant();
            if (!PrefixPattern.IsMatch(prefix))
                throw new InvalidOperationException(PzText.Get("Bas_ContainerUnitPrefixInvalid"));
            if (count < 1 || count > MaxBatch)
                throw new InvalidOperationException(PzText.Get("Bas_ContainerUnitCountRangeFmt", MaxBatch));
            if (!await _db.Containers.AnyAsync(c => c.ContainerCode == containerCode && c.IsActive, ct))
                throw new InvalidOperationException(PzText.Get("Bas_ContainerTypeNotFoundFmt", containerCode));

            // 跨廠找最大號：箱號全域唯一（箱子會跟貨到別廠，§5 #4）
            var head = prefix + "-";
            var existing = await _db.ContainerUnits.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.ContainerUnitId.StartsWith(head))
                .Select(u => u.ContainerUnitId)
                .ToListAsync(ct);
            var max = existing
                .Select(id => int.TryParse(id[head.Length..], out var n) ? n : 0)
                .DefaultIfEmpty(0).Max();

            var now = DateTime.UtcNow;
            var ids = Enumerable.Range(max + 1, count).Select(n => $"{head}{n:D5}").ToList();
            foreach (var id in ids)
            {
                _db.ContainerUnits.Add(new ContainerUnit
                {
                    ContainerUnitId = id, ContainerCode = containerCode, IsActive = true,
                    CreatedAt = now, UpdatedAt = now, CreatedBy = userId,
                });
                _db.ContainerUnitHistories.Add(new ContainerUnitHistory
                {
                    ContainerUnitId = id, Action = ContainerUnitActions.Create, OccurredAt = now, UserId = userId,
                });
            }
            await _db.SaveChangesAsync(ct);
            return ids;
        }

        public async Task<decimal> BoundQtyAsync(ContainerUnit unit, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(unit.ProductCode) || string.IsNullOrEmpty(unit.LotNo)) return 0m;
            return await _db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.PlantId == unit.PlantId && i.ProductCode == unit.ProductCode && i.LotNo == unit.LotNo)
                .SumAsync(i => (decimal?)i.QtyOnHand, ct) ?? 0m;
        }

        public async Task<decimal> WipQtyAsync(ContainerUnit unit, CancellationToken ct = default)
        {
            if (unit.WorkOrderId == null || unit.OperationSeq == null || unit.BoundAt == null) return 0m;
            return await (
                from c in _db.ProductionReportContainers.AsNoTracking()
                join r in _db.ProductionReports.IgnoreQueryFilters() on c.ReportId equals r.ReportId
                join d in _db.Dispatches.IgnoreQueryFilters() on r.DispatchId equals d.DispatchId
                where c.ContainerUnitId == unit.ContainerUnitId && r.WorkOrderId == unit.WorkOrderId
                   && d.OperationSeq == unit.OperationSeq && r.CreatedAt >= unit.BoundAt
                select (decimal?)c.ActualQty).SumAsync(ct) ?? 0m;
        }

        public async Task<bool> IsInWipAsync(ContainerUnit unit, CancellationToken ct = default)
            => unit.WorkOrderId != null && await _db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(w => w.WorkOrderId == unit.WorkOrderId && w.PlantId == unit.PlantId
                            && w.Status != WorkOrderStatus.Closed && w.Status != WorkOrderStatus.Void, ct);

        public async Task<ContainerUnit?> FindAsync(string unitId, CancellationToken ct = default)
        {
            var id = (unitId ?? "").Trim().ToUpperInvariant();
            return id.Length == 0 ? null : await _db.ContainerUnits.FirstOrDefaultAsync(u => u.ContainerUnitId == id, ct);
        }

        public async Task LoadWipAsync(ContainerUnit unit, string workOrderId, int operationSeq, string productCode,
            decimal qty, string? userId, DateTime now, CancellationToken ct = default)
        {
            if (!unit.IsActive)
                throw new InvalidOperationException(PzText.Get("Bas_ContainerUnitInactiveFmt", unit.ContainerUnitId));
            if (await BusyReasonAsync(unit, workOrderId, ct) is { } busy)
                throw new InvalidOperationException(busy);

            // 同工單同工序＝續裝，沿用原綁定時間（數量從那時起累加）；其餘一律重新起算
            var continuing = unit.WorkOrderId == workOrderId && unit.OperationSeq == operationSeq
                             && unit.LotNo == null && unit.BoundAt != null;
            if (!continuing) unit.BoundAt = now;
            unit.ProductCode  = productCode;
            unit.LotNo        = null;
            unit.BoundDocNo   = null;
            unit.WorkOrderId  = workOrderId;
            unit.OperationSeq = operationSeq;
            unit.UpdatedAt    = now;
            _db.ContainerUnitHistories.Add(new ContainerUnitHistory
            {
                ContainerUnitId = unit.ContainerUnitId, PlantId = unit.PlantId, Action = ContainerUnitActions.WipLoad,
                ProductCode = productCode, Qty = qty, WorkOrderId = workOrderId, OperationSeq = operationSeq,
                OccurredAt = now, UserId = userId,
            });
        }

        public async Task<ContainerUnitOffsite?> OffsiteAsync(ContainerUnit unit, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(unit.ProductCode) || string.IsNullOrEmpty(unit.LotNo)) return null;
            if (await BoundQtyAsync(unit, ct) > 0) return null;
            var last = await _db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.PlantId == unit.PlantId && m.ProductCode == unit.ProductCode
                         && m.LotNo == unit.LotNo && m.Direction == -1)
                .OrderByDescending(m => m.CreatedAt)
                .Select(m => new { m.TypeCode, m.DocNo, m.MovementDate })
                .FirstOrDefaultAsync(ct);
            if (last == null || !LeavePlantTypeCodes.Contains(last.TypeCode)) return null;
            return new ContainerUnitOffsite(last.TypeCode, last.DocNo, last.MovementDate,
                await PartyAsync(last.TypeCode, last.DocNo, ct));
        }

        public async Task<IReadOnlyList<ContainerUnitLeft>> LeftWithDocAsync(string docNo, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(docNo)) return Array.Empty<ContainerUnitLeft>();
            var lots = await _db.StockMovements.AsNoTracking()
                .Where(m => m.DocNo == docNo && m.LotNo != "")
                .GroupBy(m => new { m.PlantId, m.ProductCode, m.LotNo })
                .Select(g => new { g.Key.PlantId, g.Key.ProductCode, g.Key.LotNo, Out = g.Sum(m => -m.Direction * m.StockQty) })
                .Where(x => x.Out > 0)
                .ToListAsync(ct);
            if (lots.Count == 0) return Array.Empty<ContainerUnitLeft>();

            var lotNos = lots.Select(l => l.LotNo).Distinct().ToList();
            var bound = await _db.ContainerUnitHistories.IgnoreQueryFilters().AsNoTracking()
                .Where(h => h.LotNo != null && lotNos.Contains(h.LotNo))      // 綁定、回收、清空的歷程都記著當時的批號
                .Select(h => new { h.ContainerUnitId, h.PlantId, h.ProductCode, h.LotNo })
                .Distinct().ToListAsync(ct);
            // 目前仍綁著的也算（沒有歷程的箱子，例如匯入或手動補登的資料）
            bound = bound.Union(await _db.ContainerUnits.IgnoreQueryFilters().AsNoTracking()
                    .Where(u => u.LotNo != null && lotNos.Contains(u.LotNo))
                    .Select(u => new { u.ContainerUnitId, u.PlantId, u.ProductCode, u.LotNo })
                    .ToListAsync(ct))
                .Distinct().ToList();
            var unitIds = bound.Select(b => b.ContainerUnitId).Distinct().ToList();
            var current = await _db.ContainerUnits.IgnoreQueryFilters().AsNoTracking()
                .Where(u => unitIds.Contains(u.ContainerUnitId))
                .ToDictionaryAsync(u => u.ContainerUnitId, u => (u.ProductCode, u.LotNo), ct);

            return lots
                .SelectMany(l => bound
                    .Where(b => b.PlantId == l.PlantId && b.ProductCode == l.ProductCode && b.LotNo == l.LotNo)
                    .Select(b => new ContainerUnitLeft(b.ContainerUnitId, l.ProductCode, l.LotNo, l.Out,
                        !current.TryGetValue(b.ContainerUnitId, out var c) || c.ProductCode != l.ProductCode || c.LotNo != l.LotNo)))
                .OrderBy(x => x.UnitId)
                .ToList();
        }

        /// <summary>出廠單據的對象：客戶／供應商／往來對象代碼，跨廠調撥為目的廠。</summary>
        private async Task<string?> PartyAsync(string typeCode, string docNo, CancellationToken ct) => typeCode switch
        {
            "GS"        => await _db.GoodsShipments.IgnoreQueryFilters().Where(d => d.GoodsShipmentId == docNo).Select(d => d.CompanyCode).FirstOrDefaultAsync(ct),
            "OO"        => await _db.OutsourceMaterialDocs.IgnoreQueryFilters().Where(d => d.OutsourceMaterialDocId == docNo).Select(d => d.SupplierCode).FirstOrDefaultAsync(ct),
            "IL" or "BR" => await _db.LoanDocs.IgnoreQueryFilters().Where(d => d.LoanDocId == docNo).Select(d => d.EntityCode).FirstOrDefaultAsync(ct),
            "SO" or "CR" => await _db.SampleDocs.IgnoreQueryFilters().Where(d => d.SampleDocId == docNo).Select(d => d.EntityCode).FirstOrDefaultAsync(ct),
            "PF"        => await _db.GoodsReturns.IgnoreQueryFilters().Where(d => d.GoodsReturnId == docNo).Select(d => d.SupplierCode).FirstOrDefaultAsync(ct),
            "XP"        => await _db.CrossPlantTransfers.IgnoreQueryFilters().Where(d => d.CrossPlantTransferId == docNo).Select(d => d.ToPlantId).FirstOrDefaultAsync(ct),
            _           => null,
        };

        public async Task<IReadOnlyList<ContainerUnitRecoverResult>> RecoverAsync(
            IReadOnlyList<string> unitIds, string? userId, CancellationToken ct = default)
        {
            var plant = _db.CurrentPlantId;
            var results = new List<ContainerUnitRecoverResult>();
            foreach (var raw in unitIds.Select(i => (i ?? "").Trim().ToUpperInvariant()).Where(i => i.Length > 0).Distinct())
            {
                // 跨廠找：箱子可能是跟著貨從別廠過來的
                var u = await _db.ContainerUnits.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.ContainerUnitId == raw, ct);
                if (u == null)
                {
                    results.Add(new(raw, PzText.Get("Bas_ContainerUnitNotFoundFmt", raw)));
                    continue;
                }
                var offsite = await OffsiteAsync(u, ct);
                string? error = null;
                if (u.PlantId == plant && offsite == null)
                    error = PzText.Get("Bas_ContainerUnitNotOffsiteFmt", u.ContainerUnitId);
                else if (u.PlantId != plant && offsite == null && await BusyReasonAsync(u, null, ct) is { } busy)
                    error = busy;      // 別廠的箱子在那邊還裝著貨／在製，不可能人在這裡
                if (error != null)
                {
                    results.Add(new(u.ContainerUnitId, error));
                    continue;
                }

                var now = DateTime.UtcNow;
                var fromPlant = u.PlantId;
                u.PlantId = plant;
                // 跨廠調撥過來、批號在本廠有貨：保留綁定（箱子裡的就是那批貨），否則清空
                var keep = !string.IsNullOrEmpty(u.LotNo) && await BoundQtyAsync(u, ct) > 0;
                _db.ContainerUnitHistories.Add(new ContainerUnitHistory
                {
                    ContainerUnitId = u.ContainerUnitId, PlantId = plant, Action = ContainerUnitActions.Recover,
                    ProductCode = u.ProductCode, LotNo = u.LotNo,
                    DocNo = offsite?.DocNo ?? (fromPlant != plant ? fromPlant : null),
                    OccurredAt = now, UserId = userId,
                });
                if (!keep)
                {
                    u.ProductCode = null; u.LotNo = null; u.BoundAt = null; u.BoundDocNo = null;
                    u.WorkOrderId = null; u.OperationSeq = null;
                }
                u.UpdatedAt = now;
                try
                {
                    await _db.SaveChangesAsync(ct);
                    results.Add(new(u.ContainerUnitId, null));
                }
                catch (DbUpdateConcurrencyException)
                {
                    _db.ChangeTracker.Clear();
                    results.Add(new(u.ContainerUnitId, PzText.Get("Bas_ContainerUnitConcurrencyFmt", u.ContainerUnitId)));
                }
            }
            return results;
        }

        /// <summary>
        /// 箱子不能裝新東西的原因；null＝可以裝。裝著庫存批號（餘額 &gt; 0）一律不行；
        /// 裝著在製品時，只有同一張工單（<paramref name="sameWorkOrderId"/>）可以接手。
        /// </summary>
        private async Task<string?> BusyReasonAsync(ContainerUnit u, string? sameWorkOrderId, CancellationToken ct)
        {
            var qty = await BoundQtyAsync(u, ct);
            if (qty > 0)
                return PzText.Get("Bas_ContainerUnitOccupiedFmt", u.ContainerUnitId, u.ProductCode!, u.LotNo!, qty);
            if (await OffsiteAsync(u, ct) is { } off)
                return PzText.Get("Bas_ContainerUnitOffsiteFmt", u.ContainerUnitId, off.DocNo);
            if (u.WorkOrderId != null && u.WorkOrderId != sameWorkOrderId && await IsInWipAsync(u, ct))
                return PzText.Get("Bas_ContainerUnitInWipFmt", u.ContainerUnitId, u.WorkOrderId, u.OperationSeq ?? 0);
            return null;
        }

        public Task<bool> LotUsedAsync(string productCode, string lotNo, CancellationToken ct = default)
            => _db.StockMovements.AsNoTracking()
                .AnyAsync(m => m.ProductCode == productCode && m.LotNo == lotNo, ct);

        public async Task<string> NextLotAsync(string productCode, string prefix, CancellationToken ct = default)
        {
            var head = prefix + "-";
            var used = (await _db.StockMovements.AsNoTracking()
                    .Where(m => m.ProductCode == productCode && m.LotNo.StartsWith(head))
                    .Select(m => m.LotNo).Distinct().ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            for (var n = 1; ; n++)
            {
                var lot = $"{head}{n:D2}";
                if (!used.Contains(lot)) return lot;
            }
        }

        public async Task<IReadOnlyList<ContainerUnitStock>> ContentAsync(ContainerUnit unit, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(unit.ProductCode) || string.IsNullOrEmpty(unit.LotNo))
                return Array.Empty<ContainerUnitStock>();
            return await _db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.PlantId == unit.PlantId && i.ProductCode == unit.ProductCode
                         && i.LotNo == unit.LotNo && i.QtyOnHand != 0)
                .OrderBy(i => i.LocationCode)
                .Select(i => new ContainerUnitStock(i.LocationCode, i.QtyOnHand))
                .ToListAsync(ct);
        }

        public async Task<IReadOnlyList<ContainerUnit>> TakeEmptyForBindingAsync(
            IReadOnlyList<string> unitIds, string? containerCode, string? workOrderId = null, CancellationToken ct = default)
        {
            var ids = unitIds.Select(i => (i ?? "").Trim()).Where(i => i.Length > 0).ToList();
            var dup = ids.GroupBy(i => i, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dup != null)
                throw new InvalidOperationException(PzText.Get("Bas_ContainerUnitDuplicateFmt", dup.Key));

            // 走廠別過濾：別廠的箱子在這裡等於不存在
            var units = await _db.ContainerUnits.Where(u => ids.Contains(u.ContainerUnitId)).ToListAsync(ct);
            var result = new List<ContainerUnit>(ids.Count);
            foreach (var id in ids)
            {
                var u = units.FirstOrDefault(x => string.Equals(x.ContainerUnitId, id, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException(PzText.Get("Bas_ContainerUnitNotFoundFmt", id));
                if (!u.IsActive)
                    throw new InvalidOperationException(PzText.Get("Bas_ContainerUnitInactiveFmt", u.ContainerUnitId));
                if (!string.IsNullOrEmpty(containerCode) && u.ContainerCode != containerCode)
                    throw new InvalidOperationException(PzText.Get("Bas_ContainerUnitTypeMismatchFmt", u.ContainerUnitId, u.ContainerCode, containerCode));
                if (await BusyReasonAsync(u, workOrderId, ct) is { } busy)
                    throw new InvalidOperationException(busy);
                result.Add(u);
            }
            return result;
        }

        public void Bind(ContainerUnit unit, string productCode, string lotNo, decimal qty, string? docNo, string? userId)
        {
            var now = DateTime.UtcNow;
            unit.ProductCode = productCode;
            unit.LotNo       = lotNo;
            unit.BoundAt     = now;
            unit.BoundDocNo  = docNo;
            unit.WorkOrderId = null;      // 在製箱轉在庫（完工入庫）：在製綁定到此結束
            unit.OperationSeq = null;
            unit.UpdatedAt   = now;
            _db.ContainerUnitHistories.Add(new ContainerUnitHistory
            {
                ContainerUnitId = unit.ContainerUnitId, PlantId = unit.PlantId, Action = ContainerUnitActions.Bind,
                ProductCode = productCode, LotNo = lotNo, Qty = qty, DocNo = docNo, OccurredAt = now, UserId = userId,
            });
        }

        public async Task ClearAsync(string unitId, string? userId, CancellationToken ct = default)
        {
            var u = await FindRequiredAsync(unitId, ct);
            if (string.IsNullOrEmpty(u.LotNo) && u.WorkOrderId == null) return;
            var now = DateTime.UtcNow;
            _db.ContainerUnitHistories.Add(new ContainerUnitHistory
            {
                ContainerUnitId = u.ContainerUnitId, PlantId = u.PlantId, Action = ContainerUnitActions.Clear,
                ProductCode = u.ProductCode, LotNo = u.LotNo,
                Qty = u.WorkOrderId != null ? await WipQtyAsync(u, ct) : await BoundQtyAsync(u, ct),
                WorkOrderId = u.WorkOrderId, OperationSeq = u.OperationSeq,
                OccurredAt = now, UserId = userId,
            });
            u.ProductCode = null; u.LotNo = null; u.BoundAt = null; u.BoundDocNo = null;
            u.WorkOrderId = null; u.OperationSeq = null; u.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
        }

        public async Task SetActiveAsync(string unitId, bool active, string? userId, CancellationToken ct = default)
        {
            var u = await FindRequiredAsync(unitId, ct);
            if (u.IsActive == active) return;
            var now = DateTime.UtcNow;
            u.IsActive = active; u.UpdatedAt = now;
            _db.ContainerUnitHistories.Add(new ContainerUnitHistory
            {
                ContainerUnitId = u.ContainerUnitId, PlantId = u.PlantId,
                Action = active ? ContainerUnitActions.Activate : ContainerUnitActions.Deactivate,
                OccurredAt = now, UserId = userId,
            });
            await _db.SaveChangesAsync(ct);
        }

        public async Task<IReadOnlyList<string>> EmptyUnitIdsAsync(string containerCode, int take, string? workOrderId = null, CancellationToken ct = default)
        {
            var candidates = (await _db.ContainerUnits.AsNoTracking()
                    .Where(u => u.ContainerCode == containerCode && u.IsActive)
                    .ToListAsync(ct))
                .OrderBy(u => workOrderId != null && u.WorkOrderId == workOrderId ? 0 : 1)
                .ThenByDescending(u => u.OperationSeq ?? 0)
                .ThenBy(u => u.ContainerUnitId, StringComparer.Ordinal);
            var result = new List<string>();
            foreach (var u in candidates)
            {
                if (result.Count >= take) break;
                if (await BusyReasonAsync(u, workOrderId, ct) == null) result.Add(u.ContainerUnitId);
            }
            return result;
        }

        private async Task<ContainerUnit> FindRequiredAsync(string unitId, CancellationToken ct)
            => await _db.ContainerUnits.FirstOrDefaultAsync(u => u.ContainerUnitId == unitId, ct)
               ?? throw new InvalidOperationException(PzText.Get("Bas_ContainerUnitNotFoundFmt", unitId));
    }
}
