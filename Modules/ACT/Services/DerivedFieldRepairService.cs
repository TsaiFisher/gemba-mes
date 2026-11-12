using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;

namespace SmartFactory.MES.Modules.ACT.Services
{
    /// <summary>一筆可自動修正的累計欄位：現值與應為值（應為值由來源單據推回）。</summary>
    public sealed record DerivedFieldRepairProposal(
        string RepairType, string DocId, int LineNo, string? ProductCode, string? PlantId, decimal Current, decimal Expected)
    {
        public decimal Difference => Current - Expected;
        /// <summary>畫面勾選與套用時用的鍵。</summary>
        public string Key => $"{RepairType}:{DocId}#{LineNo}";
    }

    public sealed record DerivedFieldRepairResult(int Applied, int Unchanged, int NotFound);

    public interface IDerivedFieldRepairService
    {
        /// <summary>目前廠別裡可自動修正的累計欄位漂移（只列、不改）。</summary>
        Task<List<DerivedFieldRepairProposal>> PreviewAsync(string plantId, CancellationToken ct = default);

        /// <summary>修正勾選的項目：鎖住目標明細、<b>當下重新計算</b>應為值後寫入。</summary>
        Task<DerivedFieldRepairResult> ApplyAsync(string plantId, IReadOnlyCollection<string> keys, CancellationToken ct = default);
    }

    /// <summary>
    /// 資料自癒——<b>只修</b>「可由來源單據推回的累計欄位」，而且要人核可（2026-10-21 使用者裁決）。
    ///
    /// <para><b>範圍</b>：採購明細的已收數量（＝非作廢進貨單加總，稽核 PO_RECEIVED_DRIFT）、
    /// 訂單明細的已出數量（＝非作廢出貨單加總，SO_SHIPPED_DRIFT）。兩者都只是單據上的進度欄位，
    /// 由來源單據完全決定，改了不動任何帳。傳票、庫存與成本<b>只偵測不自動改</b>——那些錯了要找出原因，
    /// 直接把數字改對會把證據一起抹掉。派工完成數不在範圍：除了報工，委外進度與拆派工也會動它，不是單一來源。</para>
    ///
    /// <para><b>怎麼保證不改錯</b>：①應為值只有一份計算（<see cref="IDataAuditService.ExpectedPoReceivedQtyAsync"/>），
    /// 與每日稽核共用；②套用時在交易裡先 <c>SELECT … FOR UPDATE</c> 鎖住目標明細，鎖住之後才重新計算，
    /// 不採信預覽當時的數字（預覽到核可之間可能又進了貨）；③經 EF 寫入，稽核攔截器記下前後值與操作者；
    /// ④只改目前廠別的單據。</para>
    /// </summary>
    public sealed class DerivedFieldRepairService : IDerivedFieldRepairService
    {
        public const string PoReceivedQty = "PO_RECEIVED_QTY";
        public const string SoShippedQty = "SO_SHIPPED_QTY";

        private readonly ApplicationDbContext _db;
        private readonly IDataAuditService _audit;
        private readonly ILogger<DerivedFieldRepairService> _logger;

        public DerivedFieldRepairService(ApplicationDbContext db, IDataAuditService audit, ILogger<DerivedFieldRepairService> logger)
        {
            _db = db;
            _audit = audit;
            _logger = logger;
        }

        public async Task<List<DerivedFieldRepairProposal>> PreviewAsync(string plantId, CancellationToken ct = default)
        {
            // 偵測沿用每日稽核（同一套容忍值與判定），這裡只把「找得到目標明細」的那些轉成可修正項目；
            // 孤兒（進貨單指向不存在的採購明細）沒有東西可以改，留在稽核清單給人處理
            var result = new List<DerivedFieldRepairProposal>();

            var poDrift = await _audit.AuditPoReceivedQtyDriftAsync(plantId, ct);
            if (poDrift.Count > 0)
            {
                var expected = await _audit.ExpectedPoReceivedQtyAsync(plantId, ct);
                var keys = poDrift.Select(f => ParseKey(f.DocId)).Where(k => k != null).Select(k => k!.Value).ToList();
                var ids = keys.Select(k => k.Doc).Distinct().ToList();
                var lines = await (from l in _db.PurchaseOrderLines.IgnoreQueryFilters().AsNoTracking()
                                   join h in _db.PurchaseOrders.IgnoreQueryFilters().AsNoTracking() on l.PurchaseOrderId equals h.PurchaseOrderId
                                   where ids.Contains(l.PurchaseOrderId) && h.PlantId == plantId
                                   select new { l.PurchaseOrderId, l.LineNo, l.ProductCode, l.ReceivedQty, h.PlantId }).ToListAsync(ct);
                foreach (var l in lines.Where(l => keys.Contains((l.PurchaseOrderId, l.LineNo))))
                    result.Add(new(PoReceivedQty, l.PurchaseOrderId, l.LineNo, l.ProductCode, l.PlantId,
                        l.ReceivedQty, expected.GetValueOrDefault((l.PurchaseOrderId, l.LineNo))));
            }

            var soDrift = await _audit.AuditSoShippedQtyDriftAsync(plantId, ct);
            if (soDrift.Count > 0)
            {
                var expected = await _audit.ExpectedSoShippedQtyAsync(plantId, ct);
                var keys = soDrift.Select(f => ParseKey(f.DocId)).Where(k => k != null).Select(k => k!.Value).ToList();
                var ids = keys.Select(k => k.Doc).Distinct().ToList();
                var lines = await (from l in _db.SalesOrderLines.IgnoreQueryFilters().AsNoTracking()
                                   join h in _db.SalesOrders.IgnoreQueryFilters().AsNoTracking() on l.SalesOrderId equals h.SalesOrderId
                                   where ids.Contains(l.SalesOrderId) && h.PlantId == plantId
                                   select new { l.SalesOrderId, l.LineNo, l.ProductCode, l.ShippedQty, h.PlantId }).ToListAsync(ct);
                foreach (var l in lines.Where(l => keys.Contains((l.SalesOrderId, l.LineNo))))
                    result.Add(new(SoShippedQty, l.SalesOrderId, l.LineNo, l.ProductCode, l.PlantId,
                        l.ShippedQty, expected.GetValueOrDefault((l.SalesOrderId, l.LineNo))));
            }

            return result.OrderBy(r => r.RepairType).ThenBy(r => r.DocId).ThenBy(r => r.LineNo).ToList();
        }

        public async Task<DerivedFieldRepairResult> ApplyAsync(string plantId, IReadOnlyCollection<string> keys, CancellationToken ct = default)
        {
            var parsed = keys.Select(ParseProposalKey).Where(k => k != null).Select(k => k!.Value).Distinct().ToList();
            int applied = 0, unchanged = 0, notFound = 0;
            if (parsed.Count == 0) return new(0, 0, 0);

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var po = parsed.Where(k => k.Type == PoReceivedQty).ToList();
            if (po.Count > 0)
            {
                // 先鎖再算：鎖住之後才重新計算應為值，預覽到核可之間新進的貨也算得進去
                foreach (var k in po)
                    await _db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT 1 FROM \"PurchaseOrderLines\" WHERE \"PurchaseOrderId\" = {k.Doc} AND \"LineNo\" = {k.Line} FOR UPDATE", ct);
                var expected = await _audit.ExpectedPoReceivedQtyAsync(plantId, ct);
                var ids = po.Select(k => k.Doc).Distinct().ToList();
                var ownIds = await _db.PurchaseOrders.IgnoreQueryFilters().AsNoTracking()
                    .Where(h => ids.Contains(h.PurchaseOrderId) && h.PlantId == plantId).Select(h => h.PurchaseOrderId).ToListAsync(ct);
                var lines = await _db.PurchaseOrderLines.IgnoreQueryFilters()
                    .Where(l => ownIds.Contains(l.PurchaseOrderId)).ToListAsync(ct);
                foreach (var k in po)
                {
                    var line = lines.FirstOrDefault(l => l.PurchaseOrderId == k.Doc && l.LineNo == k.Line);
                    if (line == null) { notFound++; continue; }
                    var exp = expected.GetValueOrDefault((k.Doc, k.Line));
                    if (line.ReceivedQty == exp) { unchanged++; continue; }
                    line.ReceivedQty = exp;
                    applied++;
                }
            }

            var so = parsed.Where(k => k.Type == SoShippedQty).ToList();
            if (so.Count > 0)
            {
                foreach (var k in so)
                    await _db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT 1 FROM \"SalesOrderLines\" WHERE \"SalesOrderId\" = {k.Doc} AND \"LineNo\" = {k.Line} FOR UPDATE", ct);
                var expected = await _audit.ExpectedSoShippedQtyAsync(plantId, ct);
                var ids = so.Select(k => k.Doc).Distinct().ToList();
                var ownIds = await _db.SalesOrders.IgnoreQueryFilters().AsNoTracking()
                    .Where(h => ids.Contains(h.SalesOrderId) && h.PlantId == plantId).Select(h => h.SalesOrderId).ToListAsync(ct);
                var lines = await _db.SalesOrderLines.IgnoreQueryFilters()
                    .Where(l => ownIds.Contains(l.SalesOrderId)).ToListAsync(ct);
                foreach (var k in so)
                {
                    var line = lines.FirstOrDefault(l => l.SalesOrderId == k.Doc && l.LineNo == k.Line);
                    if (line == null) { notFound++; continue; }
                    var exp = expected.GetValueOrDefault((k.Doc, k.Line));
                    if (line.ShippedQty == exp) { unchanged++; continue; }
                    line.ShippedQty = exp;
                    applied++;
                }
            }

            await _db.SaveChangesAsync(ct);   // 稽核攔截器記下每一筆的前後值與操作者
            await tx.CommitAsync(ct);
            _logger.LogInformation("累計欄位自動修正：廠別 {PlantId} 修正 {Applied} 筆、已一致 {Unchanged} 筆、找不到 {NotFound} 筆",
                plantId, applied, unchanged, notFound);
            return new(applied, unchanged, notFound);
        }

        /// <summary>稽核的 DocId 形如「單號#行號」。</summary>
        private static (string Doc, int Line)? ParseKey(string? docId)
        {
            if (string.IsNullOrEmpty(docId)) return null;
            var i = docId.LastIndexOf('#');
            return i > 0 && int.TryParse(docId[(i + 1)..], out var line) ? (docId[..i], line) : null;
        }

        private static (string Type, string Doc, int Line)? ParseProposalKey(string key)
        {
            var c = key.IndexOf(':');
            if (c <= 0) return null;
            var type = key[..c];
            if (type != PoReceivedQty && type != SoShippedQty) return null;
            return ParseKey(key[(c + 1)..]) is { } k ? (type, k.Doc, k.Line) : null;
        }
    }
}
