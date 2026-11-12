using SmartFactory.MES.Modules._Shared;
﻿using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Modules.PUR.ViewModels;

namespace SmartFactory.MES.Services
{
    public class PurchaseService : IPurchaseService
    {
        private readonly ApplicationDbContext _db;
        private readonly IDocNumberService _docNumber;
        private readonly IDateTimeService _dt;
        private readonly IUomService _uom;
        private readonly ICurrencyService _currency;
        private readonly IInventoryReservationService _reservations;

        public PurchaseService(ApplicationDbContext db, IDocNumberService docNumber, IDateTimeService dt, IUomService uom, ICurrencyService currency, IInventoryReservationService reservations)
        {
            _db = db;
            _docNumber = docNumber;
            _dt = dt;
            _uom = uom;
            _currency = currency;
            _reservations = reservations;
        }

        /// <summary>依幣別小數位數對所有金額/單價欄位四捨五入；幣別空字串時回傳原值。</summary>
        private async Task<decimal> RoundMoneyAsync(decimal value, string? currencyCode)
        {
            if (string.IsNullOrWhiteSpace(currencyCode)) return value;
            return await _currency.RoundAsync(value, currencyCode);
        }

        /// <summary>
        /// 換算本位幣金額：外幣含稅總額 × 匯率，依本位幣小數位數四捨五入。
        /// 明細幣別可各自不同，表頭合計一律加總本欄（見各單據 Model 的 TotalAmount 註解）。
        /// </summary>
        private async Task<decimal> ToLocalAmountAsync(decimal foreignTotal, decimal exchangeRate)
        {
            var baseCurrency = await _currency.GetBaseCurrencyAsync();
            var local = foreignTotal * exchangeRate;
            return string.IsNullOrWhiteSpace(baseCurrency) ? local : await _currency.RoundAsync(local, baseCurrency);
        }

        /// <summary>
        /// 後端權威解析 ExchangeRate：依 CurrencyCode + documentDate 查 ≤ documentDate 最近一筆匯率。
        /// PUR 用買入匯率（公司付款給外幣廠商）。
        /// 找不到（且幣別非空、非本幣）時保留前端傳入值 fallback，避免覆寫為 1 而造成更糟的錯亂。
        /// </summary>
        private async Task<decimal> ResolvePurchaseRateAsync(
            string? currencyCode, DateOnly documentDate, decimal clientRate, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(currencyCode)) return clientRate;
            var rate = await _currency.GetRateAsync(currencyCode, documentDate, RateType.Buy);
            return rate ?? clientRate;
        }

        // ── PurchaseRequest ────────────────────────────────────────────────────

        public async Task<PurchaseRequest> CreatePurchaseRequestAsync(
            PurchaseRequestFormVm vm, string userId, string plantId, CancellationToken ct = default)
        {
            var id = await _docNumber.NextAsync("PurchaseRequest", plantId, vm.RequestDate, ct);
            var now = DateTime.UtcNow;

            var lines = new List<PurchaseRequestLine>();
            await UseMasterStockUomAsync(vm.Lines, x => x.ProductCode, (x, u) => x.StockUomCode = u, ct);
            foreach (var l in vm.Lines)
            {
                var reqQty   = await _uom.RoundQtyAsync(l.UomCode, l.RequestQty, ct);
                var stockQty = await _uom.RoundQtyAsync(l.StockUomCode,
                    await _uom.ConvertAsync(l.ProductCode, l.UomCode, l.StockUomCode, reqQty, ct), ct);
                lines.Add(new PurchaseRequestLine
                {
                    PurchaseRequestId = id,
                    LineNo = lines.Count + 1,
                    ProductCode = l.ProductCode,
                    DeliveryDate = l.DeliveryDate,
                    RequestQty = reqQty,
                    UomCode = l.UomCode,
                    StockQty = stockQty,
                    StockUomCode = l.StockUomCode,
                    Remarks = l.Remarks,
                });
            }

            var pr = new PurchaseRequest
            {
                PurchaseRequestId = id,
                RequestDate = vm.RequestDate,
                RequestedBy = userId,
                Remarks = vm.Remarks,
                SourceMrpRunId = vm.SourceMrpRunId,
                PlantId = plantId,
                CreatedAt = now,
                UpdatedAt = now,
                Lines = lines
            };

            _db.PurchaseRequests.Add(pr);
            await _db.SaveChangesAsync(ct);
            return pr;
        }

        public async Task<PurchaseRequest> UpdatePurchaseRequestAsync(
            PurchaseRequestFormVm vm, string userId, CancellationToken ct = default)
        {
            var pr = await _db.PurchaseRequests
                .Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.PurchaseRequestId == vm.PurchaseRequestId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Pur_RequestNotFoundFmt", vm.PurchaseRequestId));

            // ⚠️ xmin 樂觀鎖必須**在任何寫入之前**蓋章。
            // 原本是控制器在呼叫完本方法之後才蓋（`Entry(gr).Property(e => e.xmin)
            // .OriginalValue = vm.RowVersion;`），而本方法內部自己就 SaveChanges 過了——
            // 資料早已落地、xmin 也跟著換新，事後才拿前端帶回的舊版本去比，於是：
            //   ① 每一次正常修改都會丟 DbUpdateConcurrencyException，
            //      使用者看到「資料已被其他人修改，請重新載入」——**但東西其實存進去了**；
            //   ② 真正的並發完全沒有保護：第一次 SaveChanges 沒有版本檢查，
            //      兩個人同時改就是後蓋前，兩邊還都收到衝突訊息。
            // 2026-09-06 人工驗收 4-6／4-11 實測：帶著明顯過期的版本號 1 送出，
            // 資料照樣寫進去（進貨單 PE-2609-00013）。
            _db.Entry(pr).Property(e => e.xmin).OriginalValue = vm.RowVersion;

            pr.RequestDate = vm.RequestDate;
            pr.Remarks = vm.Remarks;
            pr.UpdatedAt = DateTime.UtcNow;

            // 重建明細（僅刪除未轉採購的明細）
            var orderedLineNos = pr.Lines
                .Where(l => l.OrderedQty > 0)
                .Select(l => l.LineNo)
                .ToHashSet();

            var linesToRemove = pr.Lines
                .Where(l => !orderedLineNos.Contains(l.LineNo)
                            && !vm.Lines.Any(v => v.LineNo == l.LineNo))
                .ToList();
            _db.PurchaseRequestLines.RemoveRange(linesToRemove);

            await UseMasterStockUomAsync(vm.Lines, x => x.ProductCode, (x, u) => x.StockUomCode = u, ct);
            foreach (var v in vm.Lines)
            {
                var existing = pr.Lines.FirstOrDefault(l => l.LineNo == v.LineNo);
                var reqQty   = await _uom.RoundQtyAsync(v.UomCode, v.RequestQty, ct);
                var stockQty = await _uom.RoundQtyAsync(v.StockUomCode,
                    await _uom.ConvertAsync(v.ProductCode, v.UomCode, v.StockUomCode, reqQty, ct), ct);
                if (existing != null)
                {
                    existing.ProductCode = v.ProductCode;
                    existing.DeliveryDate = v.DeliveryDate;
                    existing.RequestQty = reqQty;
                    existing.UomCode = v.UomCode;
                    existing.StockQty = stockQty;
                    existing.StockUomCode = v.StockUomCode;
                    existing.Remarks = v.Remarks;
                    existing.IsClosed = v.IsClosed;
                }
                else
                {
                    pr.Lines.Add(new PurchaseRequestLine
                    {
                        PurchaseRequestId = pr.PurchaseRequestId,
                        LineNo = v.LineNo,
                        ProductCode = v.ProductCode,
                        DeliveryDate = v.DeliveryDate,
                        RequestQty = reqQty,
                        UomCode = v.UomCode,
                        StockQty = stockQty,
                        StockUomCode = v.StockUomCode,
                        Remarks = v.Remarks,
                        IsClosed = v.IsClosed,
                    });
                }
            }

            await _db.SaveChangesAsync(ct);
            return pr;
        }

        public async Task DeletePurchaseRequestAsync(string id, CancellationToken ct = default)
        {
            var pr = await _db.PurchaseRequests
                .Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.PurchaseRequestId == id, ct)
                ?? throw new InvalidOperationException(PzText.Get("Pur_RequestNotFoundFmt", id));

            if (pr.Lines.Any(l => l.OrderedQty > 0))
                throw new InvalidOperationException(PzText.Get("Pur_RequestConvertedCannotDelete"));

            _db.PurchaseRequests.Remove(pr);
            await _db.SaveChangesAsync(ct);
        }

        // ── PurchaseOrder ──────────────────────────────────────────────────────

        public async Task<PurchaseOrder> CreatePurchaseOrderAsync(
            PurchaseOrderFormVm vm, string userId, string plantId, CancellationToken ct = default)
        {
            var id = await _docNumber.NextAsync("PurchaseOrder", plantId, vm.OrderDate, ct);
            var now = DateTime.UtcNow;
            var lines = await BuildPoLinesAsync(id, vm.OrderDate, vm.Lines, ct);

            var po = new PurchaseOrder
            {
                PurchaseOrderId = id,
                OrderDate = vm.OrderDate,
                CreatedBy = userId,
                SupplierCode = vm.SupplierCode,
                TradeTermCode   = vm.TradeTermCode,
                DeliveryAddress = vm.DeliveryAddress,
                TotalAmount = lines.Sum(l => l.AmountLocal),
                Remarks = vm.Remarks,
                PlantId = plantId,
                CreatedAt = now,
                UpdatedAt = now,
                Lines = lines,
            };

            _db.PurchaseOrders.Add(po);

            // 更新來源請購明細已轉採購數量
            await UpdatePrOrderedQtyAsync(vm.Lines, ct);

            await _db.SaveChangesAsync(ct);
            return po;
        }

        public async Task<PurchaseOrder> UpdatePurchaseOrderAsync(
            PurchaseOrderFormVm vm, string userId, CancellationToken ct = default)
        {
            var po = await _db.PurchaseOrders
                .Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.PurchaseOrderId == vm.PurchaseOrderId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Pur_OrderNotFoundFmt", vm.PurchaseOrderId));

            // ⚠️ xmin 樂觀鎖必須**在任何寫入之前**蓋章。
            // 原本是控制器在呼叫完本方法之後才蓋（`Entry(gr).Property(e => e.xmin)
            // .OriginalValue = vm.RowVersion;`），而本方法內部自己就 SaveChanges 過了——
            // 資料早已落地、xmin 也跟著換新，事後才拿前端帶回的舊版本去比，於是：
            //   ① 每一次正常修改都會丟 DbUpdateConcurrencyException，
            //      使用者看到「資料已被其他人修改，請重新載入」——**但東西其實存進去了**；
            //   ② 真正的並發完全沒有保護：第一次 SaveChanges 沒有版本檢查，
            //      兩個人同時改就是後蓋前，兩邊還都收到衝突訊息。
            // 2026-09-06 人工驗收 4-6／4-11 實測：帶著明顯過期的版本號 1 送出，
            // 資料照樣寫進去（進貨單 PE-2609-00013）。
            _db.Entry(po).Property(e => e.xmin).OriginalValue = vm.RowVersion;

            if (po.Lines.Any(l => l.ReceivedQty > 0))
                throw new InvalidOperationException(PzText.Get("Pur_OrderReceivedCannotEditLines"));

            // 還原舊的 PR 已轉採購量
            await RestorePrOrderedQtyAsync(po.Lines, ct);

            po.OrderDate = vm.OrderDate;
            po.SupplierCode = vm.SupplierCode;
            po.TradeTermCode   = vm.TradeTermCode;
            po.DeliveryAddress = vm.DeliveryAddress;
            po.Remarks = vm.Remarks;
            po.UpdatedAt = DateTime.UtcNow;

            _db.PurchaseOrderLines.RemoveRange(po.Lines);
            po.Lines = await BuildPoLinesAsync(po.PurchaseOrderId, vm.OrderDate, vm.Lines, ct);
            po.TotalAmount = po.Lines.Sum(l => l.AmountLocal);

            await UpdatePrOrderedQtyAsync(vm.Lines, ct);
            await _db.SaveChangesAsync(ct);
            return po;
        }

        public async Task DeletePurchaseOrderAsync(string id, CancellationToken ct = default)
        {
            var po = await _db.PurchaseOrders
                .Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.PurchaseOrderId == id, ct)
                ?? throw new InvalidOperationException(PzText.Get("Pur_OrderNotFoundFmt", id));

            if (po.Lines.Any(l => l.ReceivedQty > 0))
                throw new InvalidOperationException(PzText.Get("Pur_OrderReceivedCannotDelete"));

            await RestorePrOrderedQtyAsync(po.Lines, ct);
            _db.PurchaseOrders.Remove(po);
            await _db.SaveChangesAsync(ct);
        }

        public async Task<List<PurchaseRequestLine>> GetOpenPrLinesAsync(
            string purchaseRequestId, string plantId, CancellationToken ct = default)
        {
            return await _db.PurchaseRequestLines
                .Where(l => l.PurchaseRequestId == purchaseRequestId
                            && !l.IsClosed
                            && l.RequestQty > l.OrderedQty)
                .ToListAsync(ct);
        }

        // ── GoodsReceipt ───────────────────────────────────────────────────────

        public async Task<GoodsReceipt> CreateGoodsReceiptAsync(
            GoodsReceiptFormVm vm, string userId, string plantId, CancellationToken ct = default)
        {
            var id = await _docNumber.NextAsync("GoodsReceipt", plantId, vm.ReceiptDate, ct);
            var now = DateTime.UtcNow;

            var lines = new List<GoodsReceiptLine>();
            await UseMasterStockUomAsync(vm.Lines, x => x.ProductCode, (x, u) => x.StockUomCode = u, ct);
            foreach (var v in vm.Lines)
            {
                var receiptQty = await _uom.RoundQtyAsync(v.UomCode, v.ReceiptQty, ct);
                var stockQty   = await _uom.RoundQtyAsync(v.StockUomCode,
                    await _uom.ConvertAsync(v.ProductCode, v.UomCode, v.StockUomCode, receiptQty, ct), ct);
                var exchangeRate = await ResolvePurchaseRateAsync(v.CurrencyCode, vm.ReceiptDate, v.ExchangeRate, ct);
                lines.Add(new GoodsReceiptLine
                {
                    GoodsReceiptId = id,
                    LineNo = v.LineNo,
                    PurchaseOrderId = v.PurchaseOrderId,
                    PoLineNo = v.PoLineNo,
                    ProductCode = v.ProductCode,
                    ReceiptQty = receiptQty,
                    UomCode = v.UomCode,
                    StockQty = stockQty,
                    StockUomCode = v.StockUomCode,
                    UnitPrice = await RoundMoneyAsync(v.UnitPrice, v.CurrencyCode),
                    CurrencyCode = v.CurrencyCode,
                    ExchangeRate = exchangeRate,
                    TaxCode = v.TaxCode,
                    TaxRate = v.TaxRate,
                    LineAmount = await RoundMoneyAsync(v.LineAmount, v.CurrencyCode),
                    TaxAmount = await RoundMoneyAsync(v.TaxAmount, v.CurrencyCode),
                    TotalAmount = await RoundMoneyAsync(v.TotalAmount, v.CurrencyCode),
                    AmountLocal = await ToLocalAmountAsync(await RoundMoneyAsync(v.TotalAmount, v.CurrencyCode), exchangeRate),
                    // 目的倉別／儲位／批號原本兩條建行路徑都沒映射——表單收得到、模型有欄位、
                    // 編輯畫面也讀得出來，就是存不進去，於是永遠是 null。實害有兩層：
                    //   ① IQC 合格放行（ReleaseFromQuarantineAsync）在 line.WhCode 為空時
                    //      **靜默 return**，貨永遠留在待驗倉；而應付認列的時機正是「進正常倉」，
                    //      所以連帶 AP 也永遠不認列——整條進料鏈停在半路且沒有任何訊號。
                    //   ② 批號管控品的驗證讀的是 VM 的 LotNo（會過），存下來的卻是 null。
                    // 桌機簡化模式過帳看起來沒事，是因為它在 WhCode/LocationCode 為空時
                    // 會 fallback 到產品類別的預設倉儲——IQC 放行沒有那道 fallback。
                    WhCode = v.WhCode,
                    LocationCode = v.LocationCode,
                    LotNo = v.LotNo,
                    Remarks = v.Remarks,
                });
            }

            var gr = new GoodsReceipt
            {
                GoodsReceiptId = id,
                ReceiptDate    = vm.ReceiptDate,
                CreatedBy      = userId,
                SupplierCode   = vm.SupplierCode,
                TotalAmount    = lines.Sum(l => l.AmountLocal),
                Remarks        = vm.Remarks,
                SourceDocType  = vm.SourceDocType,
                SourceDocId    = vm.SourceDocId,
                PlantId        = plantId,
                CreatedAt      = now,
                UpdatedAt      = now,
                Lines          = lines,
            };

            // 驗證批號管控
            await ValidateLotTrackingAsync(vm.Lines.Select(l => l.ProductCode), vm.Lines.Select(l => l.LotNo), ct);

            await StampAccrualUnitPricesAsync(lines, ct);

            _db.GoodsReceipts.Add(gr);

            // 更新 PO 明細已進貨數量（傳實體：StockQty 已由伺服端換算，VM 上那份來自前端）
            await UpdatePoReceivedQtyAsync(lines, ct);

            await _db.SaveChangesAsync(ct);
            return gr;
        }

        public async Task<GoodsReceipt> UpdateGoodsReceiptAsync(
            GoodsReceiptFormVm vm, string userId, CancellationToken ct = default)
        {
            var gr = await _db.GoodsReceipts
                .Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.GoodsReceiptId == vm.GoodsReceiptId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Pur_ReceiptNotFoundFmt", vm.GoodsReceiptId));

            // ⚠️ xmin 樂觀鎖必須**在任何寫入之前**蓋章。
            // 原本是控制器在呼叫完本方法之後才蓋（`Entry(gr).Property(e => e.xmin)
            // .OriginalValue = vm.RowVersion;`），而本方法內部自己就 SaveChanges 過了——
            // 資料早已落地、xmin 也跟著換新，事後才拿前端帶回的舊版本去比，於是：
            //   ① 每一次正常修改都會丟 DbUpdateConcurrencyException，
            //      使用者看到「資料已被其他人修改，請重新載入」——**但東西其實存進去了**；
            //   ② 真正的並發完全沒有保護：第一次 SaveChanges 沒有版本檢查，
            //      兩個人同時改就是後蓋前，兩邊還都收到衝突訊息。
            // 2026-09-06 人工驗收 4-6／4-11 實測：帶著明顯過期的版本號 1 送出，
            // 資料照樣寫進去（進貨單 PE-2609-00013）。
            _db.Entry(gr).Property(e => e.xmin).OriginalValue = vm.RowVersion;

            // DocStatus 門控：僅 Draft 可修改
            if (gr.Status != DocStatus.Draft)
                throw new InvalidOperationException(PzText.Get("Pur_ReceiptNotEditable"));

            // 行級保護：已有倉庫作業的明細不可刪除或減量
            foreach (var existingLine in gr.Lines.Where(l => l.QtyPutAway > 0))
            {
                var vmLine = vm.Lines.FirstOrDefault(v => v.LineNo == existingLine.LineNo);
                if (vmLine == null)
                    throw new InvalidOperationException(PzText.Get("Pur_LineWarehouseWorkFmt", existingLine.LineNo, existingLine.QtyPutAway));
                if (vmLine.StockQty < existingLine.QtyPutAway)
                    throw new InvalidOperationException(PzText.Get("Pur_LineQtyBelowProcessedFmt", existingLine.LineNo, existingLine.QtyPutAway));
            }

            // 驗證批號管控
            await ValidateLotTrackingAsync(vm.Lines.Select(l => l.ProductCode), vm.Lines.Select(l => l.LotNo), ct);

            await RestorePoReceivedQtyAsync(gr.Lines, ct);
            _db.GoodsReceiptLines.RemoveRange(gr.Lines);

            var tempGr = await CreateGoodsReceiptLinesAsync(gr.GoodsReceiptId, vm, userId, gr.PlantId!, ct);
            gr.ReceiptDate   = vm.ReceiptDate;
            gr.SupplierCode  = vm.SupplierCode;
            gr.Remarks       = vm.Remarks;
            gr.SourceDocType = vm.SourceDocType;
            gr.SourceDocId   = vm.SourceDocId;
            gr.TotalAmount   = tempGr.Sum(l => l.AmountLocal);
            gr.UpdatedAt     = DateTime.UtcNow;
            gr.Lines         = tempGr;

            await _db.SaveChangesAsync(ct);
            return gr;
        }

        public async Task DeleteGoodsReceiptAsync(string id, CancellationToken ct = default)
        {
            var gr = await _db.GoodsReceipts
                .Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.GoodsReceiptId == id, ct)
                ?? throw new InvalidOperationException(PzText.Get("Pur_ReceiptNotFoundFmt", id));

            // DocStatus 門控：僅 Draft 可刪除
            if (gr.Status != DocStatus.Draft)
                throw new InvalidOperationException(PzText.Get("Pur_ReceiptNotDeletable"));

            // 行級保護：有倉庫作業的不可刪除
            if (gr.Lines.Any(l => l.QtyPutAway > 0))
                throw new InvalidOperationException(PzText.Get("Pur_ReceiptHasWarehouseWork"));

            await RestorePoReceivedQtyAsync(gr.Lines, ct);
            _db.GoodsReceipts.Remove(gr);
            await _db.SaveChangesAsync(ct);
        }

        // ── GoodsReturn ────────────────────────────────────────────────────────

        public async Task<GoodsReturn> CreateGoodsReturnAsync(
            GoodsReturnFormVm vm, string userId, string plantId, CancellationToken ct = default)
        {
            var id = await _docNumber.NextAsync("GoodsReturn", plantId, vm.ReturnDate, ct);
            var now = DateTime.UtcNow;

            var lines = new List<GoodsReturnLine>();
            await UseMasterStockUomAsync(vm.Lines, x => x.ProductCode, (x, u) => x.StockUomCode = u, ct);
            foreach (var v in vm.Lines)
            {
                var returnQty = await _uom.RoundQtyAsync(v.UomCode, v.ReturnQty, ct);
                var stockQty  = await _uom.RoundQtyAsync(v.StockUomCode,
                    await _uom.ConvertAsync(v.ProductCode, v.UomCode, v.StockUomCode, returnQty, ct), ct);
                var exchangeRate = await ResolvePurchaseRateAsync(v.CurrencyCode, vm.ReturnDate, v.ExchangeRate, ct);
                lines.Add(new GoodsReturnLine
                {
                    GoodsReturnId = id,
                    LineNo = v.LineNo,
                    GoodsReceiptId = v.GoodsReceiptId,
                    ReceiptLineNo = v.ReceiptLineNo,
                    ProductCode = v.ProductCode,
                    ReturnQty = returnQty,
                    UomCode = v.UomCode,
                    StockQty = stockQty,
                    StockUomCode = v.StockUomCode,
                    UnitPrice = await RoundMoneyAsync(v.UnitPrice, v.CurrencyCode),
                    CurrencyCode = v.CurrencyCode,
                    ExchangeRate = exchangeRate,
                    TaxCode = v.TaxCode,
                    TaxRate = v.TaxRate,
                    LineAmount = await RoundMoneyAsync(v.LineAmount, v.CurrencyCode),
                    TaxAmount = await RoundMoneyAsync(v.TaxAmount, v.CurrencyCode),
                    TotalAmount = await RoundMoneyAsync(v.TotalAmount, v.CurrencyCode),
                    AmountLocal = await ToLocalAmountAsync(await RoundMoneyAsync(v.TotalAmount, v.CurrencyCode), exchangeRate),
                    Remarks = v.Remarks,
                });
            }

            var ret = new GoodsReturn
            {
                GoodsReturnId = id,
                ReturnDate = vm.ReturnDate,
                CreatedBy = userId,
                SupplierCode = vm.SupplierCode,
                TotalAmount = lines.Sum(l => l.AmountLocal),
                Remarks = vm.Remarks,
                PlantId = plantId,
                CreatedAt = now,
                UpdatedAt = now,
                Lines = lines,
            };

            // 驗證批號管控
            await ValidateLotTrackingAsync(vm.Lines.Select(l => l.ProductCode), vm.Lines.Select(l => l.LotNo), ct);

            _db.GoodsReturns.Add(ret);

            // 更新進貨單已退貨數量
            await UpdateGrReturnedQtyAsync(vm.Lines, ct);

            await _db.SaveChangesAsync(ct);
            return ret;
        }

        public async Task<GoodsReturn> UpdateGoodsReturnAsync(
            GoodsReturnFormVm vm, string userId, CancellationToken ct = default)
        {
            var ret = await _db.GoodsReturns
                .Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.GoodsReturnId == vm.GoodsReturnId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Pur_ReturnNotFoundFmt", vm.GoodsReturnId));

            // ⚠️ xmin 樂觀鎖必須**在任何寫入之前**蓋章。
            // 原本是控制器在呼叫完本方法之後才蓋（`Entry(gr).Property(e => e.xmin)
            // .OriginalValue = vm.RowVersion;`），而本方法內部自己就 SaveChanges 過了——
            // 資料早已落地、xmin 也跟著換新，事後才拿前端帶回的舊版本去比，於是：
            //   ① 每一次正常修改都會丟 DbUpdateConcurrencyException，
            //      使用者看到「資料已被其他人修改，請重新載入」——**但東西其實存進去了**；
            //   ② 真正的並發完全沒有保護：第一次 SaveChanges 沒有版本檢查，
            //      兩個人同時改就是後蓋前，兩邊還都收到衝突訊息。
            // 2026-09-06 人工驗收 4-6／4-11 實測：帶著明顯過期的版本號 1 送出，
            // 資料照樣寫進去（進貨單 PE-2609-00013）。
            _db.Entry(ret).Property(e => e.xmin).OriginalValue = vm.RowVersion;

            // DocStatus 門控：僅 Draft 可修改
            if (ret.Status != DocStatus.Draft)
                throw new InvalidOperationException(PzText.Get("Pur_ReturnNotEditable"));

            // 行級保護
            foreach (var existingLine in ret.Lines.Where(l => l.QtyPutAway > 0))
            {
                var vmLine = vm.Lines.FirstOrDefault(v => v.LineNo == existingLine.LineNo);
                if (vmLine == null)
                    throw new InvalidOperationException(PzText.Get("Pur_LineWarehouseWorkFmt", existingLine.LineNo, existingLine.QtyPutAway));
                if (vmLine.StockQty < existingLine.QtyPutAway)
                    throw new InvalidOperationException(PzText.Get("Pur_LineQtyBelowProcessedFmt", existingLine.LineNo, existingLine.QtyPutAway));
            }

            // 驗證批號管控
            await ValidateLotTrackingAsync(vm.Lines.Select(l => l.ProductCode), vm.Lines.Select(l => l.LotNo), ct);

            await RestoreGrReturnedQtyAsync(ret.Lines, ct);
            _db.GoodsReturnLines.RemoveRange(ret.Lines);

            var lines = new List<GoodsReturnLine>();
            await UseMasterStockUomAsync(vm.Lines, x => x.ProductCode, (x, u) => x.StockUomCode = u, ct);
            foreach (var v in vm.Lines)
            {
                var returnQty = await _uom.RoundQtyAsync(v.UomCode, v.ReturnQty, ct);
                var stockQty  = await _uom.RoundQtyAsync(v.StockUomCode,
                    await _uom.ConvertAsync(v.ProductCode, v.UomCode, v.StockUomCode, returnQty, ct), ct);
                var exchangeRate = await ResolvePurchaseRateAsync(v.CurrencyCode, vm.ReturnDate, v.ExchangeRate, ct);
                lines.Add(new GoodsReturnLine
                {
                    GoodsReturnId = ret.GoodsReturnId,
                    LineNo = v.LineNo,
                    GoodsReceiptId = v.GoodsReceiptId,
                    ReceiptLineNo = v.ReceiptLineNo,
                    ProductCode = v.ProductCode,
                    ReturnQty = returnQty,
                    UomCode = v.UomCode,
                    StockQty = stockQty,
                    StockUomCode = v.StockUomCode,
                    UnitPrice = await RoundMoneyAsync(v.UnitPrice, v.CurrencyCode),
                    CurrencyCode = v.CurrencyCode,
                    ExchangeRate = exchangeRate,
                    TaxCode = v.TaxCode,
                    TaxRate = v.TaxRate,
                    LineAmount = await RoundMoneyAsync(v.LineAmount, v.CurrencyCode),
                    TaxAmount = await RoundMoneyAsync(v.TaxAmount, v.CurrencyCode),
                    TotalAmount = await RoundMoneyAsync(v.TotalAmount, v.CurrencyCode),
                    AmountLocal = await ToLocalAmountAsync(await RoundMoneyAsync(v.TotalAmount, v.CurrencyCode), exchangeRate),
                    Remarks = v.Remarks,
                });
            }

            ret.ReturnDate = vm.ReturnDate;
            ret.SupplierCode = vm.SupplierCode;
            ret.Remarks = vm.Remarks;
            ret.TotalAmount = lines.Sum(l => l.AmountLocal);
            ret.UpdatedAt = DateTime.UtcNow;
            ret.Lines = lines;

            await UpdateGrReturnedQtyAsync(vm.Lines, ct);
            await _db.SaveChangesAsync(ct);
            return ret;
        }

        public async Task DeleteGoodsReturnAsync(string id, CancellationToken ct = default)
        {
            var ret = await _db.GoodsReturns
                .Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.GoodsReturnId == id, ct)
                ?? throw new InvalidOperationException(PzText.Get("Pur_ReturnNotFoundFmt", id));

            // DocStatus 門控：僅 Draft 可刪除
            if (ret.Status != DocStatus.Draft)
                throw new InvalidOperationException(PzText.Get("Pur_ReturnNotDeletable"));

            if (ret.Lines.Any(l => l.QtyPutAway > 0))
                throw new InvalidOperationException(PzText.Get("Pur_ReturnHasWarehouseWork"));

            _db.GoodsReturns.Remove(ret);
            await _db.SaveChangesAsync(ct);
        }

        // ── Private Helpers ────────────────────────────────────────────────────

        private async Task<List<PurchaseOrderLine>> BuildPoLinesAsync(string poId, DateOnly documentDate, List<PurchaseOrderLineVm> vms, CancellationToken ct)
        {
            var lines = new List<PurchaseOrderLine>();
            await UseMasterStockUomAsync(vms, x => x.ProductCode, (x, u) => x.StockUomCode = u, ct);
            foreach (var v in vms)
            {
                var orderQty = await _uom.RoundQtyAsync(v.UomCode, v.OrderQty, ct);
                var stockQty = await _uom.RoundQtyAsync(v.StockUomCode,
                    await _uom.ConvertAsync(v.ProductCode, v.UomCode, v.StockUomCode, orderQty, ct), ct);
                var exchangeRate = await ResolvePurchaseRateAsync(v.CurrencyCode, documentDate, v.ExchangeRate, ct);
                lines.Add(new PurchaseOrderLine
                {
                    PurchaseOrderId = poId,
                    LineNo = lines.Count + 1,
                    PurchaseRequestId = v.PurchaseRequestId,
                    PrLineNo = v.PrLineNo,
                    ProductCode = v.ProductCode,
                    DeliveryDate = v.DeliveryDate,
                    OrderQty = orderQty,
                    UomCode = v.UomCode,
                    StockQty = stockQty,
                    StockUomCode = v.StockUomCode,
                    UnitPrice = await RoundMoneyAsync(v.UnitPrice, v.CurrencyCode),
                    CurrencyCode = v.CurrencyCode,
                    ExchangeRate = exchangeRate,
                    TaxCode = v.TaxCode,
                    TaxRate = v.TaxRate,
                    LineAmount = await RoundMoneyAsync(v.LineAmount, v.CurrencyCode),
                    TaxAmount = await RoundMoneyAsync(v.TaxAmount, v.CurrencyCode),
                    TotalAmount = await RoundMoneyAsync(v.TotalAmount, v.CurrencyCode),
                    AmountLocal = await ToLocalAmountAsync(await RoundMoneyAsync(v.TotalAmount, v.CurrencyCode), exchangeRate),
                    Remarks = v.Remarks,
                });
            }
            return lines;
        }

        /// <summary>
        /// 明細的庫存單位一律以**品號主檔目前的 StockUom** 為準，覆寫前端送來的值。
        ///
        /// <para>前端那一份可能是舊的：轉入請購單明細時帶的是請購當下記下的庫存單位。品號的庫存單位
        /// 事後改過（g → KG）的話，採購單畫面明明全是 KG，存檔卻一直報「找不到單位換算：KG → g」
        /// （2026-10-05 回報，請購單 PR260400002 的明細仍記著 g）。庫存數量是過帳依據，
        /// 換算基準不能交給瀏覽器或舊單據。主檔查不到的品號才沿用送來的值。</para>
        /// </summary>
        private async Task UseMasterStockUomAsync<T>(IEnumerable<T> lines, Func<T, string?> product,
            Action<T, string> setStockUom, CancellationToken ct)
        {
            var list = lines.ToList();
            var codes = list.Select(product).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
            if (codes.Count == 0) return;
            var master = await _db.Products.AsNoTracking()
                .Where(p => codes.Contains(p.ProductCode) && p.StockUom != null && p.StockUom != "")
                .ToDictionaryAsync(p => p.ProductCode, p => p.StockUom!, ct);
            foreach (var l in list)
                if (product(l) is { } code && master.TryGetValue(code, out var uom))
                    setStockUom(l, uom);
        }

        private async Task UpdatePrOrderedQtyAsync(List<PurchaseOrderLineVm> lines, CancellationToken ct)
        {
            foreach (var v in lines.Where(l => l.PurchaseRequestId != null && l.PrLineNo != null))
            {
                var prLine = await _db.PurchaseRequestLines.FindAsync(
                    [v.PurchaseRequestId, v.PrLineNo], ct);
                if (prLine == null) continue;
                if (prLine.IsClosed)
                    throw new InvalidOperationException(
                        PzText.Get("Pur_RequestLineClosedFmt", v.PurchaseRequestId, v.PrLineNo));
                if (prLine.OrderedQty + v.OrderQty > prLine.RequestQty)
                    throw new InvalidOperationException(
                        PzText.Get("Pur_ConvertExceedsRequestFmt", v.PurchaseRequestId, v.PrLineNo, prLine.OrderedQty + v.OrderQty, prLine.RequestQty));
                prLine.OrderedQty += v.OrderQty;
            }
        }

        private async Task RestorePrOrderedQtyAsync(IEnumerable<PurchaseOrderLine> lines, CancellationToken ct)
        {
            foreach (var l in lines.Where(l => l.PurchaseRequestId != null && l.PrLineNo != null))
            {
                var prLine = await _db.PurchaseRequestLines.FindAsync(
                    [l.PurchaseRequestId, l.PrLineNo], ct);
                if (prLine != null)
                    prLine.OrderedQty = Math.Max(0, prLine.OrderedQty - l.OrderQty);
            }
        }

        /// <summary>
        /// 累加採購單明細的已進貨量。
        ///
        /// 收的是**已建好的進貨明細實體**而非 ViewModel：實體上的 <c>StockQty</c> 是
        /// 剛剛才由 <see cref="IUomService"/> 依品號主檔換算出來的伺服端權威值，
        /// 而 VM 上那一份是前端送來的。守門既然改以庫存基準單位比對，
        /// 基準值就不能取自前端。
        /// </summary>
        private async Task UpdatePoReceivedQtyAsync(IEnumerable<GoodsReceiptLine> lines, CancellationToken ct)
        {
            foreach (var v in lines.Where(l => l.PurchaseOrderId != null && l.PoLineNo != null))
            {
                var poLine = await _db.PurchaseOrderLines.FindAsync(
                    [v.PurchaseOrderId, v.PoLineNo], ct);
                if (poLine != null)
                {
                    var docInPoUom = await ToPoLineUomAsync(
                        poLine, v.ProductCode, v.UomCode, v.ReceiptQty, v.StockUomCode, v.StockQty, ct);

                    // §5.9 EP Invalid：累計進貨數量不得超過採購單訂購數量。
                    //
                    // 首選以庫存基準單位比對——進貨行的 UomCode 可以與採購行不同
                    // （採購 PCS、進貨 BOX），舊寫法直接相加是把兩種單位混在一起算。
                    //
                    // 採購行的 StockQty 是 0 時退回以「採購行自己的單位」比對：舊資料、
                    // 種子、程式建單都可能沒填基準量，而 migration 只補得了單位相同的那些
                    // （單位不同時 SQL 拿不到換算率）。此時把進貨量換算進採購行單位再比，
                    // 一樣是同單位相比、一樣正確，只是多一層捨入；直接拿 0 當上限則會
                    // 把所有進貨都擋掉——那是拿舊資料的缺漏去罰使用者。
                    //
                    // 基準累計本身沒被回填時（舊資料：ReceivedQty 有值、ReceivedStockQty 還是 0）
                    // 同樣退回，否則守門會拿一個從 0 起算的累計去比上限，等於把已收的量全忘掉。
                    var baseAccumulatorReady = poLine.ReceivedStockQty > 0m || poLine.ReceivedQty == 0m;
                    var useStockBasis = poLine.StockQty > 0m && v.StockQty > 0m && baseAccumulatorReady;
                    if (useStockBasis
                        ? poLine.ReceivedStockQty + v.StockQty > poLine.StockQty
                        : poLine.ReceivedQty + docInPoUom > poLine.OrderQty)
                    {
                        var (acc, add, cap, uom) = useStockBasis
                            ? (poLine.ReceivedStockQty, v.StockQty, poLine.StockQty, poLine.StockUomCode)
                            : (poLine.ReceivedQty, docInPoUom, poLine.OrderQty, poLine.UomCode);
                        throw new InvalidOperationException(
                            PzText.Get("Pur_ReceiptExceedsOrderFmt", v.PurchaseOrderId, v.PoLineNo, acc + add, uom, cap, uom));
                    }

                    poLine.ReceivedStockQty += v.StockQty;
                    poLine.ReceivedQty      += docInPoUom;

                    // mfg-domain.md §24.9(3)：在途到貨 → 把投產時針對此 PO 行建立的 Category=Supply
                    // 預留轉為 OnHand（料已實際入庫，預留改鎖在手）。與在途量(OrderQty−ReceivedQty)同步遞減。
                    await _reservations.ConvertSupplyOnArrivalAsync(
                        "PurchaseOrder", v.PurchaseOrderId!, v.PoLineNo, ct);
                }
            }
        }

        /// <summary>
        /// 把進貨行的量換算成採購行自己的單位，供 <c>ReceivedQty</c>（顯示用累計）累加。
        /// 同單位時直接回傳，不繞換算——這是絕大多數情形，也避免無謂的捨入。
        /// </summary>
        private async Task<decimal> ToPoLineUomAsync(
            PurchaseOrderLine poLine, string productCode,
            string? docUom, decimal docQty, string? stockUom, decimal stockQty, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(poLine.UomCode) || poLine.UomCode == docUom) return docQty;
            if (string.IsNullOrEmpty(stockUom) || stockUom == poLine.UomCode)
                return await _uom.RoundQtyAsync(poLine.UomCode!, stockQty, ct);

            var converted = await _uom.ConvertAsync(productCode, stockUom!, poLine.UomCode!, stockQty, ct);
            return await _uom.RoundQtyAsync(poLine.UomCode!, converted, ct);
        }

        public async Task RestorePoReceivedQtyAsync(IEnumerable<GoodsReceiptLine> lines, CancellationToken ct = default)
        {
            foreach (var l in lines.Where(l => l.PurchaseOrderId != null && l.PoLineNo != null))
            {
                var poLine = await _db.PurchaseOrderLines.FindAsync(
                    [l.PurchaseOrderId, l.PoLineNo], ct);
                if (poLine != null)
                {
                    // 兩欄以與累加時完全相同的換算回沖，否則「進貨→作廢」一趟會留下殘值
                    poLine.ReceivedStockQty = Math.Max(0, poLine.ReceivedStockQty - l.StockQty);
                    poLine.ReceivedQty      = Math.Max(0, poLine.ReceivedQty
                        - await ToPoLineUomAsync(poLine, l.ProductCode, l.UomCode, l.ReceiptQty, l.StockUomCode, l.StockQty, ct));

                    // mfg-domain.md §24.9(3)：收貨回退（GR 作廢/取消）→ 把到貨時轉為 OnHand 的
                    // Supply peg 還原為在途（與在途量回升同步）。
                    await _reservations.RevertSupplyOnUnreceiveAsync(
                        "PurchaseOrder", l.PurchaseOrderId!, l.PoLineNo, ct);
                }
            }
        }

        private async Task<List<GoodsReceiptLine>> CreateGoodsReceiptLinesAsync(
            string receiptId, GoodsReceiptFormVm vm, string userId, string plantId, CancellationToken ct)
        {
            var lines = new List<GoodsReceiptLine>();
            await UseMasterStockUomAsync(vm.Lines, x => x.ProductCode, (x, u) => x.StockUomCode = u, ct);
            foreach (var v in vm.Lines)
            {
                var receiptQty = await _uom.RoundQtyAsync(v.UomCode, v.ReceiptQty, ct);
                var stockQty   = await _uom.RoundQtyAsync(v.StockUomCode,
                    await _uom.ConvertAsync(v.ProductCode, v.UomCode, v.StockUomCode, receiptQty, ct), ct);
                var exchangeRate = await ResolvePurchaseRateAsync(v.CurrencyCode, vm.ReceiptDate, v.ExchangeRate, ct);
                lines.Add(new GoodsReceiptLine
                {
                    GoodsReceiptId = receiptId,
                    LineNo = v.LineNo,
                    PurchaseOrderId = v.PurchaseOrderId,
                    PoLineNo = v.PoLineNo,
                    ProductCode = v.ProductCode,
                    ReceiptQty = receiptQty,
                    UomCode = v.UomCode,
                    StockQty = stockQty,
                    StockUomCode = v.StockUomCode,
                    UnitPrice = await RoundMoneyAsync(v.UnitPrice, v.CurrencyCode),
                    CurrencyCode = v.CurrencyCode,
                    ExchangeRate = exchangeRate,
                    TaxCode = v.TaxCode,
                    TaxRate = v.TaxRate,
                    LineAmount = await RoundMoneyAsync(v.LineAmount, v.CurrencyCode),
                    TaxAmount = await RoundMoneyAsync(v.TaxAmount, v.CurrencyCode),
                    TotalAmount   = await RoundMoneyAsync(v.TotalAmount, v.CurrencyCode),
                    AmountLocal   = await ToLocalAmountAsync(await RoundMoneyAsync(v.TotalAmount, v.CurrencyCode), exchangeRate),
                    // 見上方新建路徑的說明：兩條路徑都要映射，漏一條就是「編輯一次就洗掉」。
                    WhCode        = v.WhCode,
                    LocationCode  = v.LocationCode,
                    LotNo         = v.LotNo,
                    Remarks       = v.Remarks,
                    QtyGood       = v.QtyGood,
                    QtyRejected   = v.QtyRejected,
                });
            }

            await UpdatePoReceivedQtyAsync(lines, ct);
            await StampAccrualUnitPricesAsync(lines, ct);
            return lines;
        }

        /// <summary>
        /// 凍結每一行的存貨計價基礎（見 <see cref="GoodsReceiptCostBasis"/>）。
        ///
        /// 建行有兩份實作（新建走 <see cref="CreateGoodsReceiptAsync"/> 的內聯迴圈、
        /// 修改走 <see cref="CreateGoodsReceiptLinesAsync"/>），故凍結收在這裡由兩邊各呼叫一次；
        /// 漏掉一邊只會讓那條路徑退回舊行為（現查採購價），不會壞掉——但也就沒有凍結可言。
        /// </summary>
        private async Task StampAccrualUnitPricesAsync(List<GoodsReceiptLine> lines, CancellationToken ct)
        {
            foreach (var line in lines)
                await GoodsReceiptCostBasis.StampAsync(_db, line, ct);
        }

        private async Task ValidateLotTrackingAsync(IEnumerable<string> productCodes, IEnumerable<string?> lotNos, CancellationToken ct)
        {
            var codes = productCodes.Distinct().ToList();
            var productMap = await _db.Products.AsNoTracking().Include(p => p.Category)
                .Where(p => codes.Contains(p.ProductCode))
                .ToDictionaryAsync(p => p.ProductCode, p => p, ct);

            var codeLotPairs = productCodes.Zip(lotNos, (c, l) => new { Code = c, Lot = l });
            foreach (var pair in codeLotPairs)
            {
                if (productMap.TryGetValue(pair.Code, out var p))
                {
                    bool required = p.IsLotControlled || (p.Category?.EnableLotTracking ?? false);
                    if (required && string.IsNullOrWhiteSpace(pair.Lot))
                        throw new InvalidOperationException(PzText.Get("Inv_LotRequiredFmt", pair.Code));
                }
            }
        }

        private async Task UpdateGrReturnedQtyAsync(List<GoodsReturnLineVm> lines, CancellationToken ct)
        {
            foreach (var v in lines.Where(l => l.GoodsReceiptId != null && l.ReceiptLineNo != null))
            {
                var grLine = await _db.GoodsReceiptLines.FindAsync([v.GoodsReceiptId, v.ReceiptLineNo], ct);
                if (grLine == null) continue;
                // 上限＝進貨量：退貨不得多於進來的。同檔的 UpdatePoReceivedQtyAsync 早就有這道守門，
                // 退貨側一直沒有——超退會讓可退量（ReceiptQty − ReturnedQty）變負數，
                // 且退貨負向回沖來源 AP 發票，等於把應付沖成小於實際應付。
                if (grLine.ReturnedQty + v.ReturnQty > grLine.ReceiptQty)
                    throw new InvalidOperationException(
                        PzText.Get("Pur_ReturnExceedsReceiptFmt", v.GoodsReceiptId, v.ReceiptLineNo, grLine.ReturnedQty + v.ReturnQty, grLine.ReceiptQty));
                grLine.ReturnedQty += v.ReturnQty;
            }
        }

        public async Task RestoreGrReturnedQtyAsync(IEnumerable<GoodsReturnLine> lines, CancellationToken ct = default)
        {
            foreach (var l in lines.Where(l => l.GoodsReceiptId != null && l.ReceiptLineNo != null))
            {
                var grLine = await _db.GoodsReceiptLines.FindAsync([l.GoodsReceiptId, l.ReceiptLineNo], ct);
                if (grLine != null)
                    grLine.ReturnedQty = Math.Max(0, grLine.ReturnedQty - l.ReturnQty);
            }
        }
    }
}
