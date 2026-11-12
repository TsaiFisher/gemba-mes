using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.QCR.Services
{
    /// <summary>
    /// IQC 進料檢驗共用服務。原實作位於 Api/Mobile/MobileInvController，
    /// 為讓桌機（QCR/InspectionOrder）補登走同一條路而抽出，行為與原手機端一致。
    /// </summary>
    public class IqcInspectionService : IIqcInspectionService
    {
        private const string StockTransferDocType = "StockTransfer";

        private readonly ApplicationDbContext _db;
        private readonly IDateTimeService _dt;
        private readonly IUserContext _userContext;
        private readonly ISamplingPlanService _sampling;
        private readonly IDocNumberService _docNumber;
        private readonly IInventoryPostingService _postingService;
        private readonly IApprovalService _approvalService;
        private readonly Modules.FIN.Services.IAPInvoiceService? _apInvoice;   // optional：FIN 未啟用時為 null
        private readonly IOutsourceProgressService? _progress;                  // optional：沒給就就地組一份

        public IqcInspectionService(
            ApplicationDbContext db,
            IUserContext userContext,
            ISamplingPlanService sampling,
            IDocNumberService docNumber,
            IInventoryPostingService postingService,
            IApprovalService approvalService,
            IDateTimeService dt,
            Modules.FIN.Services.IAPInvoiceService? apInvoice = null,
            IOutsourceProgressService? progress = null)
        {
            _progress        = progress;
            _db              = db;
            _userContext     = userContext;
            _sampling        = sampling;
            _docNumber       = docNumber;
            _postingService  = postingService;
            _approvalService = approvalService;
            _dt              = dt;
            _apInvoice       = apInvoice;
        }

        public async Task<IReadOnlyList<IqcPendingLine>> GetPendingLinesAsync(string plantId, CancellationToken ct = default)
        {
            var doneKeys = await _db.MobileIqcRecords
                .Where(r => r.PlantId == plantId)
                .Select(r => new { r.GoodsReceiptId, r.LineNo })
                .ToListAsync(ct);
            var doneSet = doneKeys.Select(k => $"{k.GoodsReceiptId}|{k.LineNo}").ToHashSet();

            // 待檢＝已確認（待上架），或已上架但量還在待驗倉、尚未檢驗。
            // 原本只列已確認：完整模式先上架到待驗倉的進貨（狀態變已過帳）就從清單消失，
            // 貨卡在待驗倉，沒有品檢入口也沒有放行（2026-10-24 ADM PE-2609-00013 七週無人能驗；
            // 委外收貨 10/12 已補同一個情形）。
            var quarantineWhs = await _db.Warehouses.AsNoTracking()
                .Where(w => w.WarehouseType == WarehouseType.Quarantine).Select(w => w.WhCode).ToListAsync(ct);
            var grInQuarantine = (await _db.Set<GoodsReceiptLocationDetail>().AsNoTracking()
                    .Where(d => quarantineWhs.Contains(d.WhCode) && d.Qty > 0m)
                    .Select(d => new { d.GoodsReceiptId, d.LineNo }).ToListAsync(ct))
                .Select(k => $"{k.GoodsReceiptId}|{k.LineNo}").ToHashSet();
            var grIdsInQuarantine = grInQuarantine.Select(k => k.Split('|')[0]).Distinct().ToList();

            var receipts = await _db.GoodsReceipts.AsNoTracking()
                .Where(g => g.PlantId == plantId
                         && (g.Status == DocStatus.Confirmed
                          || (g.Status == DocStatus.Posted && grIdsInQuarantine.Contains(g.GoodsReceiptId))))
                .Include(g => g.Lines)
                .ToListAsync(ct);

            var rows = new List<IqcPendingLine>();
            foreach (var g in receipts)
            foreach (var l in g.Lines.Where(l => !doneSet.Contains($"{g.GoodsReceiptId}|{l.LineNo}")))
            {
                if (g.Status == DocStatus.Posted && !grInQuarantine.Contains($"{g.GoodsReceiptId}|{l.LineNo}")) continue;

                // 套用抽樣計畫（VL 切換 + 跳批）
                var decision = await _sampling.GetDecisionAsync(
                    l.ProductCode, g.SupplierCode, "IQC", l.StockQty, plantId, ct: ct);

                if (!decision.ShouldInspect) continue;  // 跳批：略過本批

                rows.Add(new IqcPendingLine(
                    GoodsReceiptId:        g.GoodsReceiptId,
                    TypeCode:              g.TypeCode,
                    ReceiptDate:           g.ReceiptDate,
                    LineNo:                l.LineNo,
                    ProductCode:           l.ProductCode,
                    Qty:                   l.StockQty,
                    UomCode:               l.StockUomCode,
                    VerificationLevel:     decision.VerificationLevel,
                    RecommendedSampleSize: decision.SampleSize,
                    SamplingState:         decision.SamplingState));
            }

            // ── 委外收貨（2026-10-12）：委外收回品一樣要先品檢才可進一般倉（使用者流程：收貨 → 品檢 → 入庫 → 領出）。
            // 待檢＝已確認（待上架）或已上架到待驗倉、尚未檢驗、且品號非免驗。簡化模式確認即過帳到一般倉，
            // 不經待驗倉（與一般進貨的簡化模式一致），所以不會出現在這裡。
            var quarantineWh = await _db.Warehouses.AsNoTracking()
                .Where(w => w.WarehouseType == WarehouseType.Quarantine).Select(w => w.WhCode).ToListAsync(ct);
            var osr = await _db.OutsourceReceipts.AsNoTracking()
                .Where(r => r.PlantId == plantId && (r.Status == DocStatus.Confirmed || r.Status == DocStatus.Posted))
                .Include(r => r.Lines)
                .ToListAsync(ct);
            var osrIds = osr.Select(r => r.OutsourceReceiptId).ToList();
            var inQuarantine = (await _db.Set<OutsourceReceiptLocationDetail>().AsNoTracking()
                    .Where(d => osrIds.Contains(d.OutsourceReceiptId) && quarantineWh.Contains(d.WhCode))
                    .Select(d => new { d.OutsourceReceiptId, d.LineNo }).ToListAsync(ct))
                .Select(k => $"{k.OutsourceReceiptId}|{k.LineNo}").ToHashSet();
            var codes = osr.SelectMany(r => r.Lines).Select(l => l.ProductCode).Distinct().ToList();
            var exempt = (await _db.Products.AsNoTracking()
                    .Where(p => codes.Contains(p.ProductCode) && p.IqcExempt).Select(p => p.ProductCode).ToListAsync(ct))
                .ToHashSet();
            foreach (var r in osr)
            foreach (var l in r.Lines)
            {
                var key = $"{r.OutsourceReceiptId}|{l.LineNo}";
                if (doneSet.Contains(key) || exempt.Contains(l.ProductCode)) continue;
                if (r.Status != DocStatus.Confirmed && !inQuarantine.Contains(key)) continue;

                var decision = await _sampling.GetDecisionAsync(
                    l.ProductCode, r.SupplierCode, "IQC", l.StockQty, plantId, ct: ct);
                if (!decision.ShouldInspect) continue;

                rows.Add(new IqcPendingLine(
                    GoodsReceiptId:        r.OutsourceReceiptId,
                    TypeCode:              r.TypeCode,
                    ReceiptDate:           r.ReceiptDate,
                    LineNo:                l.LineNo,
                    ProductCode:           l.ProductCode,
                    Qty:                   l.StockQty,
                    UomCode:               l.StockUomCode,
                    VerificationLevel:     decision.VerificationLevel,
                    RecommendedSampleSize: decision.SampleSize,
                    SamplingState:         decision.SamplingState,
                    SourceKind:            IqcSourceKinds.OutsourceReceipt));
            }

            return rows;
        }

        public async Task<IqcSubmitOutcome> SubmitAsync(IqcSubmitInput input, string plantId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(input.GoodsReceiptId))
                return new IqcSubmitOutcome(false, "INVALID_REQUEST", PzText.Get("Qcr_ReceiptIdRequired"));
            if (input.SampleQty <= 0)
                return new IqcSubmitOutcome(false, "INVALID_QTY", PzText.Get("Qcr_SampleQtyPositive"));

            // 重複檢查
            var existing = await _db.MobileIqcRecords.FirstOrDefaultAsync(
                r => r.GoodsReceiptId == input.GoodsReceiptId && r.LineNo == input.LineNo, ct);
            if (existing != null)
                return new IqcSubmitOutcome(false, "IQC_ALREADY_DONE", PzText.Get("Qcr_IqcAlreadyDone"));

            // MIL-STD-1916：Ac=0，failQty >= 1 即為 Fail
            var failQty = input.FailQty;
            var result  = MilStd1916Service.IsAccepted((int)failQty) ? "Pass" : "Fail";

            // 委外收回品已在待驗倉、判合格卻沒有放行目的地：先擋下並說明，不要記一筆「合格」卻不放行（2026-10-14 驗收發現）
            if (result == "Pass"
                && await _db.OutsourceReceipts.AsNoTracking().AnyAsync(r => r.OutsourceReceiptId == input.GoodsReceiptId, ct)
                && (await OutsourceQuarantineDetailsAsync(input.GoodsReceiptId, input.LineNo, ct)).Count > 0
                && (await OutsourceReleaseTargetAsync(input.GoodsReceiptId, input.LineNo, ct)).WhCode == null)
                return new IqcSubmitOutcome(false, "NO_RELEASE_TARGET", PzText.Get("Osr_ReleaseTargetMissingFmt", input.ProductCode));
            // 一般進貨同一道理（2026-10-24）：原本放行找不到目的地就靜默不動作——記成合格，貨卻留在待驗倉、應付也不認列
            if (result == "Pass"
                && (await GoodsReceiptQuarantineDetailsAsync(input.GoodsReceiptId, input.LineNo, ct)).Count > 0
                && (await GoodsReceiptReleaseTargetAsync(input.GoodsReceiptId, input.LineNo, ct)).WhCode == null)
                return new IqcSubmitOutcome(false, "NO_RELEASE_TARGET", PzText.Get("Qcr_GrReleaseTargetMissingFmt", input.ProductCode));

            var vl = (MilStd1916Service.VerificationLevel)(input.VerificationLevel is >= 1 and <= 7 ? input.VerificationLevel : 4);
            var n  = MilStd1916Service.GetSampleSize(input.LotSize, vl);

            var order = new InspectionOrder
            {
                InspectionType    = "IQC",
                PlantId           = plantId,
                SourceDocId       = input.GoodsReceiptId,
                SourceLineNo      = input.LineNo,
                ProductCode       = input.ProductCode,
                LotSize           = input.LotSize,
                SampleSize        = n,
                VerificationLevel = (int)vl,
                ActualSampleQty   = (int)input.SampleQty,
                FailQty           = (int)failQty,
                Status            = result,
                Remarks           = input.Remarks,
                InspectorId       = _userContext.UserId,
                InspectedAt       = DateTime.UtcNow,
                CreatedAt         = DateTime.UtcNow,
                UpdatedAt         = DateTime.UtcNow
            };
            _db.InspectionOrders.Add(order);

            // 同步更新 MobileIqcRecord（維持向下相容：入庫閘門仍以此表為準）
            var record = new MobileIqcRecord
            {
                GoodsReceiptId = input.GoodsReceiptId,
                LineNo         = input.LineNo,
                ProductCode    = input.ProductCode,
                PlantId        = plantId,
                SampleQty      = input.SampleQty,
                PassQty        = input.SampleQty - failQty,
                FailQty        = failQty,
                Result         = result,
                Remarks        = input.Remarks,
                InspectorId    = _userContext.UserId,
                InspectedAt    = DateTime.UtcNow,
                CreatedAt      = DateTime.UtcNow
            };
            _db.MobileIqcRecords.Add(record);
            await _db.SaveChangesAsync(ct);

            // 更新 VL 切換規則狀態（連續合格/不合格批數）
            await _sampling.RecordResultAsync(input.ProductCode, supplierCode: null, plantId, result == "Pass", ct: ct);

            // 品質隔離放行：Pass 時把先前上架到待驗倉的量轉出至單據記錄的一般倉別（收貨品質隔離）。
            // Fail 時待驗庫存原地不動，後續由既有 NCR 處置流程（Scrap/Rework/Return）接手。
            if (result == "Pass")
            {
                if (await _db.OutsourceReceipts.AsNoTracking().AnyAsync(r => r.OutsourceReceiptId == input.GoodsReceiptId, ct))
                    await ReleaseOutsourceFromQuarantineAsync(input.GoodsReceiptId, input.LineNo, plantId, ct);
                else
                    await ReleaseFromQuarantineAsync(input.GoodsReceiptId, input.LineNo, plantId, ct);
            }

            var msg = result == "Pass"
                ? $"合格（MIL-STD-1916 VL-{(int)vl}，n={n}，不良=0）"
                : $"不合格（MIL-STD-1916 Ac=0，發現 {(int)failQty} 件不良，批次拒批）";

            return new IqcSubmitOutcome(
                Success:           true,
                ErrorCode:         null,
                Message:           msg,
                RecordId:          record.Id,
                InspectionOrderId: order.InspectionOrderId,
                Result:            result,
                SampleSize:        n,
                FailQty:           failQty,
                VerificationLevel: (int)vl);
        }

        /// <summary>
        /// 委外收貨的品質隔離放行（2026-10-12）：待驗倉的量轉到收貨明細記錄的一般倉／儲位，
        /// 轉出的量即「品檢合格入庫」，回寫委外派工完工量（<see cref="IOutsourceProgressService"/>）。
        /// 應付不在此認列——委外的應付發票在收貨確認時就已產生。還沒上架（待驗倉沒有量）時不動作：
        /// 之後上架時閘門看到已合格，會直接放到一般倉並在那裡回寫完工量。
        /// </summary>
        private async Task ReleaseOutsourceFromQuarantineAsync(string receiptId, int lineNo, string plantId, CancellationToken ct)
        {
            var details = await OutsourceQuarantineDetailsAsync(receiptId, lineNo, ct);
            if (details.Count == 0) return;

            var (toWh, toLoc, line) = await OutsourceReleaseTargetAsync(receiptId, lineNo, ct);
            if (line == null) return;
            // SubmitAsync 記錄前已檢查過；走到這裡還沒有目的地＝檢查後資料被改了。擲錯讓人看得到，不再靜默略過
            if (toWh == null || toLoc == null)
                throw new InvalidOperationException(PzText.Get("Osr_ReleaseTargetMissingFmt", line.ProductCode));

            await PostQuarantineReleaseAsync(plantId, receiptId, lineNo, line.ProductCode, line.UomCode, line.StockUomCode,
                toWh!, toLoc!, details.Select(d => (d.WhCode, d.LocationCode, d.Qty)).ToList(), ct);

            var progress = _progress ?? new OutsourceProgressService(_db);
            await progress.ApplyGoodQtyAsync(receiptId, lineNo, details.Sum(d => d.Qty), ct);
            await _db.SaveChangesAsync(ct);
        }

        /// <summary>
        /// 品質隔離放行：把該進貨明細先前上架到待驗倉（WarehouseType.Quarantine）的量，
        /// 轉出至進貨明細記錄的目的倉別/儲位（GoodsReceiptLine.WhCode/LocationCode）。
        /// 找不到待驗倉明細（尚未上架）或明細未記錄目的倉別時，不動作——待日後上架/由人工調撥處理。
        /// 若「同廠調撥」單據類型設有簽核流程，只建立 Draft 待簽，不越權自動過帳。
        /// </summary>
        private async Task ReleaseFromQuarantineAsync(string goodsReceiptId, int lineNo, string plantId, CancellationToken ct)
        {
            var quarantineDetails = await GoodsReceiptQuarantineDetailsAsync(goodsReceiptId, lineNo, ct);
            if (quarantineDetails.Count == 0) return;

            var (toWh, toLoc, line) = await GoodsReceiptReleaseTargetAsync(goodsReceiptId, lineNo, ct);
            if (line == null) return;
            // SubmitAsync 記錄前已檢查過；走到這裡還沒有目的地＝檢查後資料被改了，擲錯讓人看得到
            if (toWh == null || toLoc == null)
                throw new InvalidOperationException(PzText.Get("Qcr_GrReleaseTargetMissingFmt", line.ProductCode));

            await PostQuarantineReleaseAsync(plantId, goodsReceiptId, lineNo, line.ProductCode, line.UomCode, line.StockUomCode,
                toWh, toLoc, quarantineDetails.Select(d => (d.WhCode, d.LocationCode, d.Qty)).ToList(), ct);

            // 貨已實際進正常倉 → 就這批放行量認列應付（見 IAPInvoiceService.AccrueForReceivedQtyAsync）。
            // AP 認列已由「GR 完成整批產生」改為「進正常倉逐次累加」，這是非免驗品的認列點。
            if (_apInvoice != null)
            {
                // quarantineDetails.Qty 是庫存單位量；AP 認列基礎為進貨單位（ReceiptQty × UnitPrice），
                // 故按明細 ReceiptQty/StockQty 比例換算成進貨單位當量（1:1 時即原值）。
                var releasedStock = quarantineDetails.Sum(d => d.Qty);
                var receiptEquiv = line.StockQty > 0m
                    ? releasedStock * line.ReceiptQty / line.StockQty
                    : releasedStock;
                if (receiptEquiv > 0m)
                    await _apInvoice.AccrueForReceivedQtyAsync(goodsReceiptId, lineNo, receiptEquiv, ct);
            }
        }

        /// <summary>
        /// 待驗倉 → 一般倉的系統調撥（免簽核、直接過帳）。一般進貨與委外收貨共用；
        /// 這是「IQC 合格 → 系統自動搬進正常倉」的系統單，不是人工調撥，同廠調撥設有簽核也不套用。
        /// </summary>
        /// <summary>該進貨明細目前在待驗倉的量（上架到待驗倉留下的位置明細）。</summary>
        private async Task<List<GoodsReceiptLocationDetail>> GoodsReceiptQuarantineDetailsAsync(string receiptId, int lineNo, CancellationToken ct)
            => await _db.Set<GoodsReceiptLocationDetail>().AsNoTracking()
                .Where(d => d.GoodsReceiptId == receiptId && d.LineNo == lineNo && d.Qty > 0m)
                .Join(_db.Warehouses.AsNoTracking().Where(w => w.WarehouseType == WarehouseType.Quarantine),
                    d => d.WhCode, w => w.WhCode, (d, w) => d)
                .ToListAsync(ct);

        /// <summary>
        /// 進貨品檢合格的放行目的地：進貨明細記錄的倉儲，沒有就退回品號類別的預設倉儲（與委外收貨、簡化模式過帳同一個 fallback）。
        /// 兩者都沒有回傳 (null, null, line)。
        /// </summary>
        private async Task<(string? WhCode, string? LocationCode, GoodsReceiptLine? Line)> GoodsReceiptReleaseTargetAsync(
            string receiptId, int lineNo, CancellationToken ct)
        {
            var line = await _db.Set<GoodsReceiptLine>().AsNoTracking()
                .FirstOrDefaultAsync(l => l.GoodsReceiptId == receiptId && l.LineNo == lineNo, ct);
            if (line == null) return (null, null, null);
            var (toWh, toLoc) = (line.WhCode, line.LocationCode);
            if (string.IsNullOrWhiteSpace(toWh) || string.IsNullOrWhiteSpace(toLoc))
            {
                var def = await _db.Products.AsNoTracking().Where(p => p.ProductCode == line.ProductCode)
                    .Join(_db.ProductCategories.AsNoTracking(), p => p.CategoryCode, c => c.CategoryCode,
                        (p, c) => new { c.DefaultWarehouseCode, c.DefaultLocationCode })
                    .FirstOrDefaultAsync(ct);
                toWh  = string.IsNullOrWhiteSpace(toWh) ? def?.DefaultWarehouseCode : toWh;
                toLoc = string.IsNullOrWhiteSpace(toLoc) ? def?.DefaultLocationCode : toLoc;
            }
            return string.IsNullOrWhiteSpace(toWh) || string.IsNullOrWhiteSpace(toLoc) ? (null, null, line) : (toWh, toLoc, line);
        }

        /// <summary>該委外收貨明細目前在待驗倉的量（上架到待驗倉留下的位置明細）。</summary>
        private async Task<List<OutsourceReceiptLocationDetail>> OutsourceQuarantineDetailsAsync(string receiptId, int lineNo, CancellationToken ct)
            => await _db.Set<OutsourceReceiptLocationDetail>().AsNoTracking()
                .Where(d => d.OutsourceReceiptId == receiptId && d.LineNo == lineNo)
                .Join(_db.Warehouses.AsNoTracking().Where(w => w.WarehouseType == WarehouseType.Quarantine),
                    d => d.WhCode, w => w.WhCode, (d, w) => d)
                .ToListAsync(ct);

        /// <summary>
        /// 委外收回品品檢合格的放行目的地：收貨明細記錄的倉儲，沒有就退回品號類別的預設倉儲
        /// （與一般進貨簡化模式過帳同一個 fallback）。兩者都沒有回傳 (null, null, line)。
        /// </summary>
        private async Task<(string? WhCode, string? LocationCode, OutsourceReceiptLine? Line)> OutsourceReleaseTargetAsync(
            string receiptId, int lineNo, CancellationToken ct)
        {
            var line = await _db.OutsourceReceiptLines.AsNoTracking()
                .FirstOrDefaultAsync(l => l.OutsourceReceiptId == receiptId && l.LineNo == lineNo, ct);
            if (line == null) return (null, null, null);
            var (toWh, toLoc) = (line.WhCode, line.LocationCode);
            if (string.IsNullOrWhiteSpace(toWh) || string.IsNullOrWhiteSpace(toLoc))
            {
                var def = await _db.Products.AsNoTracking().Where(p => p.ProductCode == line.ProductCode)
                    .Join(_db.ProductCategories.AsNoTracking(), p => p.CategoryCode, c => c.CategoryCode,
                        (p, c) => new { c.DefaultWarehouseCode, c.DefaultLocationCode })
                    .FirstOrDefaultAsync(ct);
                toWh  = string.IsNullOrWhiteSpace(toWh) ? def?.DefaultWarehouseCode : toWh;
                toLoc = string.IsNullOrWhiteSpace(toLoc) ? def?.DefaultLocationCode : toLoc;
            }
            return string.IsNullOrWhiteSpace(toWh) || string.IsNullOrWhiteSpace(toLoc) ? (null, null, line) : (toWh, toLoc, line);
        }

        private async Task PostQuarantineReleaseAsync(string plantId, string sourceDocId, int lineNo,
            string productCode, string? uomCode, string? stockUomCode, string toWh, string toLoc,
            IReadOnlyList<(string WhCode, string LocationCode, decimal Qty)> quarantineDetails, CancellationToken ct)
        {
            var userId = _userContext.UserId ?? "";
            var transferId = await _docNumber.NextAsync(StockTransferDocType, plantId, _dt.GetLocalDate());
            var doc = new StockTransfer
            {
                StockTransferId = transferId,
                TransferDate = _dt.GetLocalDate(),
                FromWhCode = quarantineDetails[0].WhCode,
                ToWhCode = toWh,
                Remarks = PzText.Get("Qcr_IqcAutoReleaseRemarkFmt", sourceDocId, lineNo),
                CreatedBy = userId,
                ApprovalStatus = DocumentApprovalStatus.Approved,
            };
            var lineSeq = 0;
            foreach (var d in quarantineDetails)
            {
                doc.Lines.Add(new StockTransferLine
                {
                    StockTransferId = transferId,
                    LineNo = ++lineSeq,
                    ProductCode = productCode,
                    Qty = d.Qty, UomCode = uomCode,
                    StockQty = d.Qty, StockUomCode = stockUomCode,
                    FromWhCode = d.WhCode, FromLocationCode = d.LocationCode,
                    ToWhCode = toWh, ToLocationCode = toLoc,
                });
            }
            _db.StockTransfers.Add(doc);

            await _postingService.PostSamePlantTransferAsync(new PostSamePlantTransferRequest
            {
                PlantId = plantId,
                DocNo = transferId,
                MovementDate = doc.TransferDate,
                CreatedBy = userId,
                Lines = doc.Lines.OrderBy(l => l.LineNo).Select(l => new TransferLine
                {
                    LineNo = l.LineNo, ProductCode = l.ProductCode,
                    FromLocationCode = l.FromLocationCode ?? "", ToLocationCode = l.ToLocationCode ?? "",
                    StockQty = l.StockQty, UomCode = l.UomCode ?? "", StockUomCode = l.StockUomCode ?? "",
                    SetQtyPutAway = qty => { l.QtyPutAway = qty; },
                }).ToList(),
                SetDocPosted = () => { doc.Status = DocStatus.Posted; },
            });

            await _db.SaveChangesAsync(ct);
        }
    }
}
