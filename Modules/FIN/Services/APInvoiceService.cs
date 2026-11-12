using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Models;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.FIN.Services
{
    public class APInvoiceService : IAPInvoiceService
    {
        private readonly ApplicationDbContext _db;
        private readonly IDocNumberService _docNo;
        private readonly IDateTimeService _dt;
        private readonly ICurrencyService _currency;
        private readonly Modules.ACT.Services.IAccountingPeriodService? _periodSvc;  // optional：ACT 模組未啟用時為 null
        private readonly Modules.ACT.Services.IJournalEntryService? _journalSvc;     // optional：同上

        public APInvoiceService(
            ApplicationDbContext db,
            IDocNumberService docNo,
            IDateTimeService dt,
            ICurrencyService currency,
            Modules.ACT.Services.IAccountingPeriodService? periodSvc = null,
            Modules.ACT.Services.IJournalEntryService? journalSvc = null)
        {
            _db = db;
            _docNo = docNo;
            _dt = dt;
            _currency = currency;
            _periodSvc = periodSvc;
            _journalSvc = journalSvc;
        }


        /// <summary>
        /// 期間鎖倉前置檢查（roadmap-fin-cst-sub.md §8）；ACT 模組未啟用時略過。
        /// plantId 取**單據自己的廠別**，不是操作者當下切的廠——期間依廠別分開之後
        /// 兩者不一定相同，取錯會檢查到別廠的期間。
        /// </summary>
        private Task AssertPeriodOpenAsync(DateOnly date, string? plantId, CancellationToken ct)
            => _periodSvc?.AssertCanPostAsync(date, plantId ?? string.Empty, ct) ?? Task.CompletedTask;

        public async Task<APInvoice> GenerateFromGoodsReceiptAsync(string goodsReceiptId, CancellationToken ct = default)
        {
            // Idempotent：已有「未作廢」的就直接回傳（作廢過的不算，見 FIN_InvoiceSourceUniqueExcludesVoid）
            var existing = await _db.APInvoices
                .FirstOrDefaultAsync(i => i.SourceDocType == "GoodsReceipt" && i.SourceDocId == goodsReceiptId
                                       && i.Status != APInvoiceStatus.Void, ct);
            if (existing != null) return existing;

            var gr = await _db.GoodsReceipts
                .Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.GoodsReceiptId == goodsReceiptId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Fin_GoodsReceiptNotFoundFmt", goodsReceiptId));

            await AssertPeriodOpenAsync(gr.ReceiptDate, gr.PlantId, ct);

            // 取得匯率（若幣別為空或本位幣，匯率 = 1）
            var currency = gr.Lines.FirstOrDefault()?.CurrencyCode;
            var rate = currency != null ? (await _currency.GetRateAsync(currency, gr.ReceiptDate) ?? 1m) : 1m;

            var invoice = new APInvoice
            {
                APInvoiceId   = await _docNo.NextAsync("APInvoice", gr.PlantId ?? "", ct: ct),
                SupplierCode  = gr.SupplierCode,
                InvoiceDate   = gr.ReceiptDate,
                DueDate       = await PaymentTerms.DueDateAsync(_db, gr.SupplierCode, gr.ReceiptDate, ct),   // 依廠商付款條件
                CurrencyCode  = currency,
                ExchangeRate  = rate,
                SourceDocType = "GoodsReceipt",
                SourceDocId   = goodsReceiptId,
                PlantId       = gr.PlantId,
                ApprovalStatus = DocumentApprovalStatus.Approved,
                CreatedAt     = DateTime.UtcNow,
                UpdatedAt     = DateTime.UtcNow,
            };

            // §5.1 + §5.9 EP Invalid：跨所有非作廢進貨單的累計進貨數量不得超過採購單訂購數量
            foreach (var l in gr.Lines.Where(l => l.PurchaseOrderId != null && l.PoLineNo != null))
            {
                var poLine = await _db.PurchaseOrderLines.FindAsync(
                    new object?[] { l.PurchaseOrderId, l.PoLineNo }, ct);
                if (poLine != null)
                {
                    var totalReceived = await _db.GoodsReceiptLines
                        .Where(gl => gl.PurchaseOrderId == l.PurchaseOrderId && gl.PoLineNo == l.PoLineNo)
                        .Join(_db.GoodsReceipts,
                              gl => gl.GoodsReceiptId,
                              g  => g.GoodsReceiptId,
                              (gl, g) => new { gl.ReceiptQty, g.Status })
                        .Where(x => x.Status != DocStatus.Void)
                        .SumAsync(x => x.ReceiptQty, ct);

                    if (totalReceived > poLine.OrderQty)
                        throw new InvalidOperationException(
                            PzText.Get("Fin_ReceiptExceedsOrderFmt", l.LineNo, l.PurchaseOrderId, l.PoLineNo, totalReceived, poLine.OrderQty));
                }
            }

            var lineCurrency = currency ?? await _currency.GetBaseCurrencyAsync() ?? "";
            int lineNo = 1;
            // 開票量要扣掉「開票前就已經**過帳**的退貨量」——理由與「為何不能用
            // GoodsReceiptLine.ReturnedQty」的完整說明見 ARInvoiceService.GenerateFromShipmentAsync。
            var postedReturned = await _db.GoodsReturnLines.AsNoTracking()
                .Where(l => l.GoodsReceiptId == goodsReceiptId && l.ReceiptLineNo != null
                         && l.GoodsReturn!.Status == DocStatus.Posted)
                .GroupBy(l => l.ReceiptLineNo!.Value)
                .Select(g => new { LineNo = g.Key, Qty = g.Sum(x => x.ReturnQty) })
                .ToListAsync(ct);

            foreach (var l in gr.Lines)
            {
                var billQty = l.ReceiptQty
                            - (postedReturned.FirstOrDefault(r => r.LineNo == l.LineNo)?.Qty ?? 0m);
                if (billQty <= 0m) continue;
                var lineAmt = await _currency.RoundAsync(billQty * l.UnitPrice, lineCurrency);
                // Tax.TaxRate 以小數存放（0.05 = 5%，見 BAS/Models/Tax.cs），單據明細直接複製該值，
                // 因此稅額為 lineAmt × TaxRate；早期誤加 / 100 使發票稅額縮成百分之一。
                var taxAmt  = await _currency.RoundAsync(lineAmt * l.TaxRate, lineCurrency);
                invoice.Lines.Add(new APInvoiceLine
                {
                    LineNo      = lineNo++,
                    ProductCode = l.ProductCode,
                    Qty         = billQty,
                    UomCode     = l.UomCode,
                    UnitPrice   = l.UnitPrice,
                    LineAmount  = lineAmt,
                    TaxCode     = l.TaxCode,
                    TaxRate     = l.TaxRate,
                    TaxAmount   = taxAmt,
                    SourceLineNo = l.LineNo,
                });
            }

            invoice.TaxAmount   = invoice.Lines.Sum(l => l.TaxAmount);
            invoice.TotalAmount = invoice.Lines.Sum(l => l.LineAmount) + invoice.TaxAmount;
            invoice.AmountLocal = await _currency.RoundAsync(invoice.TotalAmount * rate, await _currency.GetBaseCurrencyAsync() ?? "");

            _db.APInvoices.Add(invoice);
            await _db.SaveChangesAsync(ct);
            return invoice;
        }

        public async Task<APInvoice> AccrueForReceivedQtyAsync(
            string goodsReceiptId, int goodsReceiptLineNo, decimal qty, CancellationToken ct = default)
        {
            if (qty <= 0) throw new InvalidOperationException(PzText.Get("Fin_AccrualQtyMustBePositive"));

            var gr = await _db.GoodsReceipts
                .Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.GoodsReceiptId == goodsReceiptId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Fin_GoodsReceiptNotFoundFmt", goodsReceiptId));

            var grLine = gr.Lines.FirstOrDefault(l => l.LineNo == goodsReceiptLineNo)
                ?? throw new InvalidOperationException(PzText.Get("Fin_ReceiptLineMissingFmt", goodsReceiptId, goodsReceiptLineNo));

            await AssertPeriodOpenAsync(gr.ReceiptDate, gr.PlantId, ct);

            var lineCurrency = grLine.CurrencyCode
                ?? gr.Lines.FirstOrDefault()?.CurrencyCode
                ?? await _currency.GetBaseCurrencyAsync() ?? "";
            var baseCurrency = await _currency.GetBaseCurrencyAsync() ?? "";
            var rate = string.IsNullOrEmpty(lineCurrency)
                ? 1m
                : (await _currency.GetRateAsync(lineCurrency, gr.ReceiptDate) ?? 1m);

            // 找不到「未作廢」的就建一張空 AP（表頭比照 GenerateFromGoodsReceiptAsync）。
            // 作廢過的不算：原本會找到作廢那張而丟「不可再追加」，完整模式分批上架的下一批整個收不進來（2026-10-22）。
            var invoice = await _db.APInvoices
                .Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.SourceDocType == "GoodsReceipt" && i.SourceDocId == goodsReceiptId
                                       && i.Status != APInvoiceStatus.Void, ct);
            if (invoice == null)
            {
                invoice = new APInvoice
                {
                    APInvoiceId   = await _docNo.NextAsync("APInvoice", gr.PlantId ?? "", ct: ct),
                    SupplierCode  = gr.SupplierCode,
                    InvoiceDate   = gr.ReceiptDate,
                    DueDate       = await PaymentTerms.DueDateAsync(_db, gr.SupplierCode, gr.ReceiptDate, ct),
                    CurrencyCode  = grLine.CurrencyCode ?? gr.Lines.FirstOrDefault()?.CurrencyCode,
                    ExchangeRate  = rate,
                    SourceDocType = "GoodsReceipt",
                    SourceDocId   = goodsReceiptId,
                    PlantId       = gr.PlantId,
                    ApprovalStatus = DocumentApprovalStatus.Approved,
                    CreatedAt     = DateTime.UtcNow,
                    UpdatedAt     = DateTime.UtcNow,
                };
                _db.APInvoices.Add(invoice);
            }
            else if (invoice.Status != APInvoiceStatus.Open)
            {
                // 已進入付款/作廢狀態的發票不可再追加認列，否則會與已核銷金額不一致。
                throw new InvalidOperationException(
                    PzText.Get("Fin_APInvoiceNoMoreAccrualFmt", invoice.APInvoiceId, invoice.Status));
            }

            // 累計認列量不得超過該進貨明細的「應認列量」＝收貨量 − 已過帳退貨量。
            // 截到剩餘量而非拋錯：呼叫端傳入的量是實體進倉量按 UOM 比例換算的「進貨單位當量」，
            // 逐批比例換算可能有微小進位誤差、最後一批略微超出——截到剩餘量可讓「全部進倉」時
            // 累計恰為應認列量（自動 true-up、無進位漂移），且永不超額認列、也不擋住實體作業。
            //
            // ⚠️ 退貨量**必須**從上限扣掉，兩個方向都會出事：
            //   ① 退貨過帳在認列之前 → ReverseForReturnedQtyAsync 當下查無發票即回 NoInvoice
            //      什麼都不做，此後沒有第二次機會；這裡若不扣，那段退回量就被永久多認列。
            //      2026-09-07 由 RETURN_SUBLEDGER_UNREVERSED 在既有資料上抓到
            //      （API-2607-00002：進貨 04-14、退貨過帳 04-23、上架認列 07-24，多認列 15.75）。
            //      同一個洞的開票版本（GenerateFromShipmentAsync／GenerateFromGoodsReceiptAsync）
            //      早已修掉，但**逐次認列**這條路徑沒有跟上——而完整模式下走的正是這一條。
            //   ② 退貨過帳在認列之後 → 回沖已經把 apLine.Qty 扣掉了；上限若只看 ReceiptQty，
            //      下一批進倉的認列會把剛沖掉的量再加回來（沖了等於沒沖）。
            // 扣了之後兩個方向都收斂到同一個不變量：apLine.Qty ≤ 收貨量 − 已退貨量。
            //
            // 只數**已過帳**的退貨：草稿階段就被加上去的是 GoodsReceiptLine.ReturnedQty
            // （GoodsReturnController 存草稿即累加），拿它當基準會與尚未發生的回沖重複扣一次。
            var postedReturned = await _db.GoodsReturnLines.AsNoTracking()
                .Where(l => l.GoodsReceiptId == goodsReceiptId
                         && l.ReceiptLineNo == goodsReceiptLineNo
                         && l.GoodsReturn!.Status == Modules.INV.Models.DocStatus.Posted)
                .SumAsync(l => (decimal?)l.ReturnQty, ct) ?? 0m;

            var apLine = invoice.Lines.FirstOrDefault(l => l.SourceLineNo == goodsReceiptLineNo);
            var already = apLine?.Qty ?? 0m;
            var remaining = grLine.ReceiptQty - postedReturned - already;
            if (remaining <= 0m) return invoice;      // 該明細已全額認列，無可再加
            if (qty > remaining) qty = remaining;

            var oldLineAmt = apLine?.LineAmount ?? 0m;
            var oldTaxAmt  = apLine?.TaxAmount ?? 0m;

            var newQty  = already + qty;
            var lineAmt = await _currency.RoundAsync(newQty * grLine.UnitPrice, lineCurrency);
            var taxAmt  = await _currency.RoundAsync(lineAmt * grLine.TaxRate, lineCurrency);

            if (apLine == null)
            {
                apLine = new APInvoiceLine
                {
                    LineNo       = invoice.Lines.Count == 0 ? 1 : invoice.Lines.Max(l => l.LineNo) + 1,
                    ProductCode  = grLine.ProductCode,
                    UomCode      = grLine.UomCode,
                    UnitPrice    = grLine.UnitPrice,
                    TaxCode      = grLine.TaxCode,
                    TaxRate      = grLine.TaxRate,
                    SourceLineNo = goodsReceiptLineNo,
                };
                invoice.Lines.Add(apLine);
            }
            apLine.Qty        = newQty;
            apLine.LineAmount = lineAmt;
            apLine.TaxAmount  = taxAmt;

            invoice.TaxAmount   = invoice.Lines.Sum(l => l.TaxAmount);
            invoice.TotalAmount = invoice.Lines.Sum(l => l.LineAmount) + invoice.TaxAmount;
            invoice.AmountLocal = await _currency.RoundAsync(invoice.TotalAmount * rate, baseCurrency);
            invoice.UpdatedAt   = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);

            // 自動日記帳：本次認列增量 借 GR/IR 暫估應付 + 進項稅額（+ 進料差異）/ 貸 應付帳款。
            //
            // 借記 2152 的金額**不重算**，而是取「當初上架時實際貸記了多少」。
            // 原本兩端各算各的：上架端記存貨成本（成本精度 4 位、且外幣單據要先換匯），
            // 認列端記應付淨額（幣別精度 2 位）——只要單價帶小數，兩個數字就不會相等，
            // 差額永久留在 2152。清算科目沖不回 0，GR/IR 稽核與月結都會卡在那個尾數上，
            // 而它小到不會有人懷疑是系統問題（實測 12.345 × 7 分三批＝殘留 0.005）。
            //
            // 差額的正確歸屬是進料差異（當期費用），不是掛在清算科目上。
            if (_journalSvc != null)
            {
                var incrTaxLocal   = await _currency.RoundAsync((taxAmt - oldTaxAmt) * rate, baseCurrency);
                var incrNetPayable = await _currency.RoundAsync((lineAmt - oldLineAmt) * rate, baseCurrency);
                var incrGross      = incrNetPayable + incrTaxLocal;

                // 本張發票已借記 2152 的淨額（退貨回沖走貸方，故取 Debit − Credit）
                // 本張發票已經沖掉的暫估，**兩種單別都要算**：
                // 認列走 ApDocType，退貨回沖走 ApReturnDocType（同檔兩個常數），
                // 而回沖那一腿是把 2152 **貸回去**——它降低的正是「本發票已沖掉多少」。
                // 只認 ApDocType 的話，退貨後的下一批認列會以為自己已經沖了比實際更多，
                // 於是少沖等額的暫估，而少掉的那筆會原封不動變成假性進料價差。
                // 2026-09-08 由 ReturnBetweenPutAwayBatches 抓到（10 收、6 上架認列、
                // 退 2、再上架 4：2152 剩 −200 沖不掉，5151 同時多出 +200——同一筆錢）。
                // ⚠️ 這條路徑要「分批上架」與「中途退貨」同時成立才會走到，
                // 兩者各自都有測試、交集沒有，所以在 main 上活了很久（已於 main 覆驗同樣是 −200／+200）。
                // AR 側的同型查詢（ARInvoiceService 的未沖銷判斷）本來就是兩種單別一起數，
                // 這裡是唯一漏掉的一個。
                var alreadyCleared = await _db.JournalLines
                    .Where(l => l.AccountCode == Modules.ACT.StandardAccounts.GrIrAPClearingCode
                             && (l.JournalEntry!.SourceDocType == ApDocType
                              || l.JournalEntry.SourceDocType == ApReturnDocType)
                             && l.JournalEntry.SourceDocId == invoice.APInvoiceId)
                    .SumAsync(l => (decimal?)(l.Debit - l.Credit), ct) ?? 0m;

                // 該進貨明細上架時實際貸記 2152 的總額（＝寫進 StockMovement 的本幣成本），
                // 以及那筆金額**對應到多少量**——兩個數字必須同源，否則按比例換算時會拿
                // 「已上架的金額」去配「整張收貨單的量」，見下方 postedReceiptEquiv。
                var posted = await _db.StockMovements
                    .Where(m => m.DocNo == goodsReceiptId
                             && m.DocLineNo == goodsReceiptLineNo
                             && m.Direction == 1)
                    .GroupBy(m => 1)
                    .Select(g => new { Cost = g.Sum(m => m.TotalCost), Qty = g.Sum(m => m.Qty) })
                    .FirstOrDefaultAsync(ct);
                var postedGrIr = posted?.Cost ?? 0m;

                // StockMovement.Qty 是庫存單位，而認列量是進貨單位——換算口徑與
                // WarehouseApiController.Fulfillment 的 receiptEquiv 相同（1:1 時即原值）。
                var postedReceiptEquiv = grLine.StockQty > 0m
                    ? (posted?.Qty ?? 0m) * grLine.ReceiptQty / grLine.StockQty
                    : (posted?.Qty ?? 0m);

                decimal incrCleared;
                if (postedGrIr == 0m)
                {
                    // 查無該進貨明細的入庫異動（尚未上架就先認列、或來源不經 StockMovement）。
                    // 此時退回「以應付淨額沖銷」的原口徑：沒有暫估可沖，就不該把整筆金額
                    // 誤判成價差丟進差異科目——那會讓一張正常的認列單看起來像是全額價差。
                    incrCleared = incrNetPayable;
                }
                else
                {
                    // 全額認列 → 完全沖平（最後一次把尾數一次補齊）；未全額 → 依認列比例。
                    //
                    // 比例的分母是**已上架量**，不是收貨量。postedGrIr 只加總已經存在的入庫異動，
                    // 是隨上架逐批長大的；拿它去除以整張收貨單的量，等於把同一個比例乘了兩次，
                    // 沖掉的暫估遠低於該沖的數，差額被當成價差灌進 5151。
                    // 2026-09-04 人工驗收 1-9 實測：12.5 分 6.25 + 6.25 兩批上架，
                    // 第一批 20.81 的應付有 10.40（整整 50%）被記成進料價差，而這筆採購
                    // 收貨單價＝採購單價，一毛錢價差都沒有。末批會沖回、帳最後是平的，
                    // 所以只斷言末狀態的測試看不到它——分批到貨一跨月結，那筆假性價差
                    // 就落在已關帳期間的損益表上，且借貸恆平、稽核抓不到。
                    //
                    // 分母改成已上架量後，1-4「全部上架、只認列一部分」仍走原本的比例
                    // （分母＝收貨量，因為已全部上架），兩種部分認列於是共用同一個式子。
                    var targetCleared = newQty >= grLine.ReceiptQty || newQty >= postedReceiptEquiv
                        ? postedGrIr
                        : await _currency.RoundAsync(postedGrIr * newQty / postedReceiptEquiv, baseCurrency);
                    incrCleared = targetCleared - alreadyCleared;
                }

                // 應付與暫估的差額＝進料差異（正＝應付高於暫估，列費用）
                var variance = incrNetPayable - incrCleared;

                if (incrGross > 0m || incrCleared != 0m)
                {
                    var jLines = new List<Modules.ACT.Services.JournalLineRequest>();
                    if (incrCleared >= 0m)
                        jLines.Add(new(Modules.ACT.StandardAccounts.GrIrAPClearingCode, Modules.ACT.StandardAccounts.GrIrAPClearingName, incrCleared, 0m));
                    else
                        jLines.Add(new(Modules.ACT.StandardAccounts.GrIrAPClearingCode, Modules.ACT.StandardAccounts.GrIrAPClearingName, 0m, -incrCleared));

                    if (incrTaxLocal > 0m)
                        jLines.Add(new(Modules.ACT.StandardAccounts.InputVatCode, Modules.ACT.StandardAccounts.InputVatName, incrTaxLocal, 0m));

                    if (variance > 0m)
                        jLines.Add(new(Modules.ACT.StandardAccounts.LandedCostVarianceCode, Modules.ACT.StandardAccounts.LandedCostVarianceName, variance, 0m));
                    else if (variance < 0m)
                        jLines.Add(new(Modules.ACT.StandardAccounts.LandedCostVarianceCode, Modules.ACT.StandardAccounts.LandedCostVarianceName, 0m, -variance));

                    jLines.Add(new(Modules.ACT.StandardAccounts.AccountsPayableCode, Modules.ACT.StandardAccounts.AccountsPayableName, 0m, incrGross));

                    await _journalSvc.CreateAutoEntryAsync(
                        ApDocType, invoice.APInvoiceId, gr.ReceiptDate, gr.PlantId ?? "", jLines, ct: ct);
                }
            }

            return invoice;
        }

        public async Task<ReturnReversalResult> ReverseForReturnedQtyAsync(
            string goodsReceiptId, int goodsReceiptLineNo, decimal qty, CancellationToken ct = default)
        {
            if (qty <= 0m) return ReturnReversalResult.NoInvoice;

            // 先找未作廢的；只剩作廢的才回「發票已作廢」（同一張進貨可能有作廢舊單與重開新單）
            var invoice = await _db.APInvoices
                .Include(i => i.Lines)
                .Where(i => i.SourceDocType == "GoodsReceipt" && i.SourceDocId == goodsReceiptId)
                .OrderBy(i => i.Status == APInvoiceStatus.Void ? 1 : 0)
                .FirstOrDefaultAsync(ct);
            // 來源進貨未認列（可能全走待驗未放行）→ 無可回沖
            if (invoice == null) return ReturnReversalResult.NoInvoice;

            // ⚠️ 明細查找**必須在狀態守門之前**：已付款時要算得出「沒沖掉多少」，
            // 而那個金額只能從明細的量價算。與 AR 側同一個修法、同一個理由。
            var apLine = invoice.Lines.FirstOrDefault(l => l.SourceLineNo == goodsReceiptLineNo);
            if (apLine == null || apLine.Qty <= 0m)
                return new ReturnReversalResult(invoice.APInvoiceId, 0m, 0m, ReturnReversalOutcome.LineNotFound);

            var reverseQty = Math.Min(qty, apLine.Qty);        // 不回沖超過已認列量

            if (invoice.Status == APInvoiceStatus.Void)
                return new ReturnReversalResult(invoice.APInvoiceId, 0m, 0m, ReturnReversalOutcome.VoidInvoice);

            if (invoice.Status != APInvoiceStatus.Open)
            {
                // 已（部分）付款：本方案不動已結清發票的金額，缺口交給呼叫端，由人另開折讓單。
                var baseCcy = await _currency.GetBaseCurrencyAsync() ?? "";
                var gap = await _currency.RoundAsync(
                    reverseQty * apLine.UnitPrice * (1m + apLine.TaxRate) * invoice.ExchangeRate, baseCcy);
                return new ReturnReversalResult(
                    invoice.APInvoiceId, 0m, gap, ReturnReversalOutcome.SettledInvoice);
            }

            var oldLineAmt = apLine.LineAmount;
            var oldTaxAmt  = apLine.TaxAmount;

            var lineCurrency = apLine.UnitPrice == 0m ? "" : (invoice.CurrencyCode ?? await _currency.GetBaseCurrencyAsync() ?? "");
            lineCurrency ??= await _currency.GetBaseCurrencyAsync() ?? "";
            var newQty  = apLine.Qty - reverseQty;
            apLine.Qty        = newQty;
            apLine.LineAmount = await _currency.RoundAsync(newQty * apLine.UnitPrice, lineCurrency);
            apLine.TaxAmount  = await _currency.RoundAsync(apLine.LineAmount * apLine.TaxRate, lineCurrency);

            invoice.TaxAmount   = invoice.Lines.Sum(l => l.TaxAmount);
            invoice.TotalAmount = invoice.Lines.Sum(l => l.LineAmount) + invoice.TaxAmount;
            var baseCurrency = await _currency.GetBaseCurrencyAsync() ?? "";
            invoice.AmountLocal = await _currency.RoundAsync(invoice.TotalAmount * invoice.ExchangeRate, baseCurrency);
            invoice.UpdatedAt   = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);

            // 回沖金額（本位幣含稅）在分錄之外先算好：它同時是分錄金額與回傳值，
            // 算兩次遲早會分岔（_journalSvc 為 null 的測試路徑就會拿到 0）。
            var decNetLocal = await _currency.RoundAsync((oldLineAmt - apLine.LineAmount) * invoice.ExchangeRate, baseCurrency);
            var decTaxLocal = await _currency.RoundAsync((oldTaxAmt - apLine.TaxAmount) * invoice.ExchangeRate, baseCurrency);
            var decGrossLocal = decNetLocal + decTaxLocal;

            // 反向分錄：借 應付帳款(含稅) ／ 貸 進貨暫估應付(淨額)＋進項稅額(稅)——與認列分錄相反。
            if (_journalSvc != null)
            {
                if (decGrossLocal > 0m)
                {
                    var jLines = new List<Modules.ACT.Services.JournalLineRequest>
                    {
                        new(Modules.ACT.StandardAccounts.AccountsPayableCode, Modules.ACT.StandardAccounts.AccountsPayableName, decGrossLocal, 0m),
                        new(Modules.ACT.StandardAccounts.GrIrAPClearingCode, Modules.ACT.StandardAccounts.GrIrAPClearingName, 0m, decNetLocal),
                    };
                    if (decTaxLocal > 0m)
                        jLines.Add(new(Modules.ACT.StandardAccounts.InputVatCode, Modules.ACT.StandardAccounts.InputVatName, 0m, decTaxLocal));

                    await _journalSvc.CreateAutoEntryAsync(
                        // 分錄日必須是廠別當地日：同檔作廢路徑（VoidAsync）用的就是 _dt.GetLocalDate()，
                        // 這裡若用 UTC，UTC+8 廠別在當地 1 號 07:00 退貨會把分錄記到上月最後一天——
                        // 輕則撞期間鎖，重則落進已結帳月份。
                        ApReturnDocType, invoice.APInvoiceId, _dt.GetLocalDate(), invoice.PlantId ?? "", jLines, ct: ct);
                }
            }

            return new ReturnReversalResult(
                invoice.APInvoiceId, decGrossLocal, 0m, ReturnReversalOutcome.Reversed);
        }

        public async Task<APInvoice> GenerateFromOutsourceReceiptAsync(string receiptId, CancellationToken ct = default)
        {
            var existing = await _db.APInvoices
                .FirstOrDefaultAsync(i => i.SourceDocType == "OutsourceReceipt" && i.SourceDocId == receiptId
                                       && i.Status != APInvoiceStatus.Void, ct);
            if (existing != null) return existing;

            var or = await _db.OutsourceReceipts
                .Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.OutsourceReceiptId == receiptId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Fin_OutsourceReceiptNotFoundFmt", receiptId));

            await AssertPeriodOpenAsync(or.ReceiptDate, or.PlantId, ct);

            var rate = or.ExchangeRate;

            var invoice = new APInvoice
            {
                APInvoiceId   = await _docNo.NextAsync("APInvoice", or.PlantId ?? "", ct: ct),
                SupplierCode  = or.SupplierCode,
                InvoiceDate   = or.ReceiptDate,
                DueDate       = await PaymentTerms.DueDateAsync(_db, or.SupplierCode, or.ReceiptDate, ct),
                CurrencyCode  = or.CurrencyCode,
                ExchangeRate  = rate,
                SourceDocType = "OutsourceReceipt",
                SourceDocId   = receiptId,
                PlantId       = or.PlantId,
                ApprovalStatus = DocumentApprovalStatus.Approved,
                CreatedAt     = DateTime.UtcNow,
                UpdatedAt     = DateTime.UtcNow,
            };

            var lineCurrency = or.CurrencyCode ?? await _currency.GetBaseCurrencyAsync() ?? "";
            int lineNo = 1;
            foreach (var l in or.Lines)
            {
                var lineAmt = await _currency.RoundAsync(l.ReceiptQty * l.UnitCost, lineCurrency);
                // Tax.TaxRate 以小數存放（0.05 = 5%，見 BAS/Models/Tax.cs），單據明細直接複製該值，
                // 因此稅額為 lineAmt × TaxRate；早期誤加 / 100 使發票稅額縮成百分之一。
                var taxAmt  = await _currency.RoundAsync(lineAmt * l.TaxRate, lineCurrency);
                invoice.Lines.Add(new APInvoiceLine
                {
                    LineNo       = lineNo++,
                    ProductCode  = l.ProductCode,
                    Qty          = l.ReceiptQty,
                    UomCode      = l.UomCode,
                    UnitPrice    = l.UnitCost,
                    LineAmount   = lineAmt,
                    TaxCode      = l.TaxCode,
                    TaxRate      = l.TaxRate,
                    TaxAmount    = taxAmt,
                    SourceLineNo = l.LineNo,
                });

                // 委外超耗扣款（規格 §1.5 / §4.4）：作為發票負項直接扣減應付加工費，不另開折讓單。
                // 金額視為與發票同幣別（委外幣）；不課稅。SourceLineNo 溯源至收貨明細。
                if (l.ExcessScrapPenalty > 0m)
                {
                    invoice.Lines.Add(new APInvoiceLine
                    {
                        LineNo       = lineNo++,
                        ProductCode  = null,
                        Qty          = 1,
                        UomCode      = null,
                        UnitPrice    = -l.ExcessScrapPenalty,
                        LineAmount   = -l.ExcessScrapPenalty,
                        TaxCode      = null,
                        TaxRate      = 0m,
                        TaxAmount    = 0m,
                        SourceLineNo = l.LineNo,
                        Remarks      = "委外超耗扣款（§1.5）",
                    });
                }
            }

            invoice.TaxAmount   = invoice.Lines.Sum(l => l.TaxAmount);
            invoice.TotalAmount = invoice.Lines.Sum(l => l.LineAmount) + invoice.TaxAmount;
            invoice.AmountLocal = await _currency.RoundAsync(invoice.TotalAmount * rate, await _currency.GetBaseCurrencyAsync() ?? "");

            _db.APInvoices.Add(invoice);
            await _db.SaveChangesAsync(ct);

            // 自動日記帳（與進貨 AP 認列同口徑）：借 2152 進貨暫估應付／借 1155 進項稅額
            // ／貸 2100 應付帳款（含稅）。委外超耗扣款是發票負項，已含在 TotalAmount 內，
            // 罰扣自然沖減應付。先前此路徑完全不入帳，委外的應付與存貨在總帳上憑空消失。
            //
            // 借記 2152 的金額**不重算**，而是取「收貨過帳當下實際貸記了多少」。
            // 兩端各算各的話，超耗扣款會讓應付低於暫估，差額就永久留在 2152——
            // 清算科目沖不回 0，月結時只看得到一個沖不掉的餘額，卻沒有任何一張傳票失衡。
            // 這與進貨端的暫估尾差是同一個形狀（見 AccrueForReceivedQtyAsync 的同一段），
            // 差別只在這裡不是尾數而是整筆扣款。差額的正確歸屬是進料差異（當期損益）：
            // 廠商賠的是我們被報廢掉的料錢，不是加工費的一部分。
            //
            // 來源刻意取傳票而非 StockMovement.TotalCost：委外成品的入庫成本含「在外原料」
            // （SimplifiedPostLine.CarriedInCost），那一段本來就沒有貸記 2152，
            // 拿 TotalCost 當暫估會多沖一筆料錢。
            if (_journalSvc != null)
            {
                var baseCurrency = await _currency.GetBaseCurrencyAsync() ?? "";
                var netLocal = await _currency.RoundAsync(
                    invoice.Lines.Sum(l => l.LineAmount) * rate, baseCurrency);
                var taxLocal = await _currency.RoundAsync(invoice.TaxAmount * rate, baseCurrency);
                var gross    = netLocal + taxLocal;

                var postedGrIr = await _db.JournalLines
                    .Where(l => l.AccountCode == Modules.ACT.StandardAccounts.GrIrAPClearingCode
                             && l.JournalEntry!.SourceDocType == InvDocType
                             && l.JournalEntry.SourceDocId == receiptId)
                    .SumAsync(l => (decimal?)(l.Credit - l.Debit), ct) ?? 0m;

                // 查無入庫傳票（非簡化模式：收貨只到 Confirmed，庫存尚未過帳）→ 沒有暫估可沖，
                // 退回「以應付淨額沖銷」的原口徑，否則整筆金額會被誤判成差異。
                var cleared  = postedGrIr == 0m ? netLocal : postedGrIr;
                var variance = netLocal - cleared;   // 負＝應付低於暫估（超耗扣款）

                if (gross > 0m || cleared != 0m)
                {
                    var jLines = new List<Modules.ACT.Services.JournalLineRequest>();
                    if (cleared >= 0m)
                        jLines.Add(new(Modules.ACT.StandardAccounts.GrIrAPClearingCode,
                            Modules.ACT.StandardAccounts.GrIrAPClearingName, cleared, 0m));
                    else
                        jLines.Add(new(Modules.ACT.StandardAccounts.GrIrAPClearingCode,
                            Modules.ACT.StandardAccounts.GrIrAPClearingName, 0m, -cleared));

                    if (taxLocal > 0m)
                        jLines.Add(new(Modules.ACT.StandardAccounts.InputVatCode,
                            Modules.ACT.StandardAccounts.InputVatName, taxLocal, 0m));

                    if (variance > 0m)
                        jLines.Add(new(Modules.ACT.StandardAccounts.LandedCostVarianceCode,
                            Modules.ACT.StandardAccounts.LandedCostVarianceName, variance, 0m));
                    else if (variance < 0m)
                        jLines.Add(new(Modules.ACT.StandardAccounts.LandedCostVarianceCode,
                            Modules.ACT.StandardAccounts.LandedCostVarianceName, 0m, -variance));

                    jLines.Add(new(Modules.ACT.StandardAccounts.AccountsPayableCode,
                        Modules.ACT.StandardAccounts.AccountsPayableName, 0m, gross));

                    await _journalSvc.CreateAutoEntryAsync(
                        ApDocType, invoice.APInvoiceId, or.ReceiptDate, or.PlantId ?? "", jLines, ct: ct);
                }
            }

            return invoice;
        }

        /// <param name="approvalStatus">
        /// 建立當下的簽核狀態，**由呼叫端解析**（`IApprovalService.ResolveSaveStatusAsync`）。
        ///
        /// 為什麼不在這裡注入 `IApprovalService`：`ApprovalService` 依賴
        /// `IEnumerable&lt;IApprovalSyncContributor&gt;`，而本服務的 contributor 又依賴本服務——
        /// 注進來就是一圈循環相依，`ValidateOnBuild` 會讓**整個 app 起不來**（不是這條路徑壞掉，
        /// 是全站啟動失敗）。單元測試抓不到這個，只有真的把 app 跑起來才會知道。
        /// 由呼叫端解析也與既有作法一致（`ProductController` 就是在 Controller 判斷）。
        ///
        /// 傳 null＝沒有線上簽核，建立即核准並立刻入總帳（本參數存在之前的行為）。
        /// </param>
        public async Task<APInvoice> CreateManualAsync(APInvoice invoice,
            DocumentApprovalStatus? approvalStatus = null, CancellationToken ct = default)
        {
            await AssertPeriodOpenAsync(invoice.InvoiceDate, invoice.PlantId, ct);

            // §5.1 EP Invalid：主明細 Qty 必須 > 0，UnitPrice 必須 ≥ 0
            foreach (var line in invoice.Lines)
            {
                if (line.Qty <= 0)
                    throw new InvalidOperationException(
                        PzText.Get("Fin_InvoiceLineQtyPositiveFmt", line.LineNo, line.Qty));
                if (line.UnitPrice < 0)
                    throw new InvalidOperationException(
                        PzText.Get("Fin_InvoiceLinePriceNegativeFmt", line.LineNo, line.UnitPrice));
            }

            invoice.APInvoiceId   = await _docNo.NextAsync("APInvoice", invoice.PlantId ?? "", ct: ct);
            invoice.SourceDocType = "Manual";
            invoice.CreatedAt     = DateTime.UtcNow;
            invoice.UpdatedAt     = DateTime.UtcNow;

            // 手動發票是**全系統唯一能憑空產生應付的路徑**——沒有採購單、沒有進貨單、
            // 沒有三方比對，只有一個人打字。原本這裡寫死 Approved，等於這條路徑沒有簽核，
            // 而採購單／工單／出貨單全都有。狀態改由設定決定；沒設定或未注入簽核服務時
            // 仍是 Approved，行為與此變更之前相同。
            invoice.ApprovalStatus = approvalStatus ?? DocumentApprovalStatus.Approved;

            // 科目要在寫入之前先解出來：解不出就整張擋下，不要留下一張「子帳看得到、
            // 總帳看不到」的發票（manual-test-plan §12.4 #1 原本的現況就是這樣）。
            // **未核准的發票也要先解**——留到核准當下才發現科目沒設，等於把錯誤丟給
            // 簽核者，而他手上沒有任何能修的東西。
            var jLines = await BuildManualJournalLinesAsync(invoice, ct);

            _db.APInvoices.Add(invoice);
            await _db.SaveChangesAsync(ct);

            // 未核准不進總帳：一張還沒人簽的發票掛上應付，等於簽核只是個表面流程。
            // 核准後由 APInvoiceApprovalSync 呼叫 PostManualJournalAsync 補上。
            if (invoice.ApprovalStatus == DocumentApprovalStatus.Approved
                && _journalSvc != null && jLines.Count > 0)
                await _journalSvc.CreateAutoEntryAsync(
                    ApDocType, invoice.APInvoiceId, invoice.InvoiceDate, invoice.PlantId ?? "", jLines, ct: ct);

            return invoice;
        }

        /// <summary>
        /// 核准後補開手動發票的認列分錄（由 <c>APInvoiceApprovalSync</c> 在回寫時呼叫）。
        ///
        /// **冪等**：已經有分錄就直接返回。簽核回寫可能因為重試、代理人重複核准或
        /// 同一筆申請被再次同步而跑第二次，不擋的話同一張發票會掛兩次應付，
        /// 而兩張傳票各自都平衡——總帳不會有任何訊號。
        ///
        /// 只處理手動發票：認列型（由進貨單產生）的分錄在收貨當下就開好了，
        /// 不經過簽核，也不該被這裡碰到。
        /// </summary>
        public async Task PostManualJournalAsync(string invoiceId, CancellationToken ct = default)
        {
            if (_journalSvc == null) return;

            var inv = await _db.APInvoices.IgnoreQueryFilters()
                .Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.APInvoiceId == invoiceId, ct);
            if (inv == null || inv.SourceDocType != "Manual") return;

            var already = await _db.JournalEntries.IgnoreQueryFilters()
                .AnyAsync(j => j.SourceDocType == ApDocType && j.SourceDocId == invoiceId, ct);
            if (already) return;

            var jLines = await BuildManualJournalLinesAsync(inv, ct);
            if (jLines.Count == 0) return;

            await _journalSvc.CreateAutoEntryAsync(
                ApDocType, inv.APInvoiceId, inv.InvoiceDate, inv.PlantId ?? "", jLines, ct: ct);
        }

        /// <summary>
        /// 手動 AP 發票的認列分錄：借 各明細的費用科目／借 1155 進項稅額／貸 2100 應付帳款。
        ///
        /// 費用科目來自明細指定的 <see cref="ChartOfAccount"/>（唯一來源）。
        /// 解不出來時**指名道姓地擋下**，不會自己挑一個科目——本系統採管理日記帳、沒有科目主檔，
        /// 硬編一個預設科目只是把「不知道該進哪個科目」換成「靜默進錯科目」。
        ///
        /// 應付方金額取「各費用腿＋稅額腿」的合計而不是 <c>AmountLocal</c>：
        /// 兩者理論上相等，但用合計可以保證這張傳票自身必然平衡，不會因為換匯進位差一元。
        /// </summary>
        private async Task<List<Modules.ACT.Services.JournalLineRequest>> BuildManualJournalLinesAsync(
            APInvoice invoice, CancellationToken ct)
        {
            if (_journalSvc == null) return new();

            var rate = invoice.ExchangeRate == 0m ? 1m : invoice.ExchangeRate;
            var direct = await ResolveExpenseAccountsAsync(
                invoice.Lines.Select(l => l.AccountCode), ct);

            // 同科目合併，免得一張十行的發票產出十條同科目的傳票行。
            var byAccount = new Dictionary<(string Code, string Name), decimal>();
            foreach (var line in invoice.Lines.OrderBy(l => l.LineNo))
            {
                var key = ExpenseAccountFor(direct, line.AccountCode, line.LineNo);
                var local = Math.Round(line.LineAmount * rate, 4, MidpointRounding.AwayFromZero);
                byAccount[key] = byAccount.TryGetValue(key, out var acc) ? acc + local : local;
            }

            var jLines = byAccount
                .Select(kv => new Modules.ACT.Services.JournalLineRequest(kv.Key.Code, kv.Key.Name, kv.Value, 0m))
                .ToList();

            var taxLocal = Math.Round(invoice.Lines.Sum(l => l.TaxAmount) * rate, 4, MidpointRounding.AwayFromZero);
            if (taxLocal > 0m)
                jLines.Add(new(Modules.ACT.StandardAccounts.InputVatCode,
                               Modules.ACT.StandardAccounts.InputVatName, taxLocal, 0m));

            var payable = jLines.Sum(l => l.Debit);
            jLines.Add(new(Modules.ACT.StandardAccounts.AccountsPayableCode,
                           Modules.ACT.StandardAccounts.AccountsPayableName, 0m, payable));
            return jLines;
        }

        /// <summary>
        /// 科目代碼 → (代碼, 名稱)，只收**啟用中、可手動選用、且是費用類（5／6／8）**的。
        ///
        /// 用途要一起濾：把收入科目填進 AP 發票會產出一張「借 4100 銷貨收入」的傳票，
        /// 借貸依然平衡、稽核也不會叫——錯得完全沒有訊號。停用中的同理，
        /// 停用是為了不讓人再選它，讀取端不濾等於停用只是視覺效果。
        ///
        /// <c>AllowManualEntry</c> 是 2026-09-21 科目升級為完整主檔之後才有的閘門：
        /// 主檔現在含 1401 存貨、2152 暫估應付這些由自動分錄維護餘額的科目，
        /// 手動指定一筆進來，帳面平衡但與子帳對不起來。
        /// </summary>
        /// <summary>
        /// 例外：由自動分錄「貸方暫估」、要靠廠商發票來沖的過渡科目，手動應付發票可以直接指定。
        /// 2153 暫估應付運費由進料費用分攤確認時貸記，費用發票到了以「借 2153／貸 應付」沖平；
        /// 原本手動發票只收費用類科目，2153 沒有任何沖銷入口（2026-10-23 使用者裁決開放）。
        /// </summary>
        public static readonly IReadOnlyCollection<string> ManualClearingAccountCodes =
            new[] { Modules.ACT.StandardAccounts.AccruedFreightCode };

        private async Task<Dictionary<string, (string Code, string Name)>> ResolveExpenseAccountsAsync(
            IEnumerable<string?> accountCodes, CancellationToken ct)
        {
            var codes = accountCodes.Where(c => !string.IsNullOrEmpty(c)).Select(c => c!).Distinct().ToList();
            if (codes.Count == 0) return new();

            var clearing = ManualClearingAccountCodes.ToList();
            return await _db.Set<ChartOfAccount>().IgnoreQueryFilters().AsNoTracking()
                .Where(a => codes.Contains(a.AccountCode) && a.IsActive
                         && (clearing.Contains(a.AccountCode)
                          || (a.AllowManualEntry
                           && (a.AccountType == AccountType.Cogs
                            || a.AccountType == AccountType.OperatingExpense
                            || a.AccountType == AccountType.OtherExpense))))
                .ToDictionaryAsync(a => a.AccountCode, a => (a.AccountCode, a.AccountName), ct);
        }

        /// <summary>
        /// 明細 → (科目代碼, 名稱)。**唯一來源是明細指定的 <see cref="ChartOfAccount"/>。**
        ///
        /// 2026-09-03 移除了原本「沒指定就退回品號的產品類別」那條 fallback，
        /// 連同 <c>ProductCategory</c> 上的兩組科目欄位一起：手動建單畫面**不送品號**
        /// （那是刻意的，見 <c>ManualAPInvoiceViewModel</c>），而認列型發票走自己的分錄
        /// 產生器——那條 fallback **沒有任何呼叫端會走到**。留著只會讓人以為科目有兩個
        /// 來源，實際上永遠只有一個，而「看起來有、其實走不到」正是這一整條待決項的病灶。
        /// </summary>
        private static (string Code, string Name) ExpenseAccountFor(
            Dictionary<string, (string Code, string Name)> direct, string? accountCode, int lineNo)
        {
            if (string.IsNullOrEmpty(accountCode))
                throw new InvalidOperationException(
                    PzText.Get("Fin_APLineNoAccountFmt", lineNo));

            if (!direct.TryGetValue(accountCode, out var d))
                throw new InvalidOperationException(
                    PzText.Get("Fin_APLineAccountInvalidFmt", lineNo, accountCode));

            return d;
        }

        public async Task<APInvoice> ReissueFromSourceAsync(string voidedInvoiceId, CancellationToken ct = default)
        {
            var inv = await _db.APInvoices.AsNoTracking().Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.APInvoiceId == voidedInvoiceId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Fin_InvoiceNotFound"));
            if (inv.Status != APInvoiceStatus.Void)
                throw new InvalidOperationException(PzText.Get("Fin_ReissueNotVoidFmt", inv.APInvoiceId));
            if (inv.SourceDocType != "GoodsReceipt" || string.IsNullOrEmpty(inv.SourceDocId))
                throw new InvalidOperationException(PzText.Get("Fin_ReissueSourceNotSupportedFmt", inv.APInvoiceId));
            var grId = inv.SourceDocId;
            var gr = await _db.GoodsReceipts.AsNoTracking().FirstOrDefaultAsync(g => g.GoodsReceiptId == grId, ct);
            if (gr == null || gr.Status is not (Modules.INV.Models.DocStatus.Confirmed or Modules.INV.Models.DocStatus.Posted))
                throw new InvalidOperationException(PzText.Get("Fin_ReissueSourceNotActiveFmt", grId));
            var live = await _db.APInvoices.AsNoTracking()
                .Where(i => i.SourceDocType == "GoodsReceipt" && i.SourceDocId == grId && i.Status != APInvoiceStatus.Void)
                .Select(i => i.APInvoiceId).FirstOrDefaultAsync(ct);
            if (live != null)
                throw new InvalidOperationException(PzText.Get("Fin_ReissueLiveExistsFmt", grId, live));
            // 作廢之後才過帳的退出：當下回沖找不到未作廢的發票、什麼都沒沖；作廢那張上的認列量沒扣到它
            var lateReturn = await _db.GoodsReturnLines.AsNoTracking()
                .Where(l => l.GoodsReceiptId == grId && l.GoodsReturn!.Status == Modules.INV.Models.DocStatus.Posted
                         && l.GoodsReturn.UpdatedAt > inv.UpdatedAt)
                .Select(l => l.GoodsReturnId).FirstOrDefaultAsync(ct);
            if (lateReturn != null)
                throw new InvalidOperationException(PzText.Get("Fin_ReissueReturnAfterVoidFmt", inv.APInvoiceId, lateReturn));

            var lines = inv.Lines.Where(l => l.SourceLineNo != null && l.Qty > 0m).ToList();
            if (lines.Count == 0)
                throw new InvalidOperationException(PzText.Get("Fin_ReissueNothingFmt", inv.APInvoiceId));

            // 多行逐行認列、每行各開分錄：包成一個交易，失敗不留半張
            var ownTx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync(ct) : null;
            try
            {
                APInvoice? created = null;
                foreach (var l in lines)
                    created = await AccrueForReceivedQtyAsync(grId, l.SourceLineNo!.Value, l.Qty, ct);
                if (ownTx != null) await ownTx.CommitAsync(ct);
                return created!;
            }
            finally { if (ownTx != null) await ownTx.DisposeAsync(); }
        }

        public async Task VoidAsync(string invoiceId, CancellationToken ct = default)
        {
            var inv = await _db.APInvoices.FindAsync(new object[] { invoiceId }, ct)
                ?? throw new InvalidOperationException(PzText.Get("Fin_InvoiceNotFound"));
            // 只要核銷過——不管付清沒付清——都得先把付款單沖掉。
            // 原本只擋 Paid：部分付款的發票放行後，作廢會把整張認列分錄借貸對調（借 2100 全額），
            // 而那筆付款自己的 借 2100 還留著，應付帳款就變成借餘——錢付出去了、負債被沖光，
            // 再也沒有任何東西會來沖那筆差額。守門自己的訊息就寫著「請先沖銷付款」，
            // 少的只是 PartialPaid 這個狀態。
            if (inv.Status is APInvoiceStatus.Paid or APInvoiceStatus.PartialPaid || inv.PaidAmount > 0m)
                throw new InvalidOperationException(PzText.Get("Fin_PaidInvoiceCannotVoid"));
            // 已確認的進貨折讓有自己的分錄，作廢發票只沖發票那幾張——不先作廢折讓，
            // 應付就留一筆沖不掉的借方（2026-10-22 隨機序列發現）
            if (inv.DebitedAmount != 0m)
                throw new InvalidOperationException(PzText.Get("Fin_InvoiceWithNotesCannotVoidFmt", inv.APInvoiceId, inv.DebitedAmount.ToString("N2")));
            await AssertPeriodOpenAsync(_dt.GetLocalDate(), inv.PlantId, ct);   // 作廢以當下日期入帳
            inv.Status    = APInvoiceStatus.Void;
            inv.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await ReverseAccrualJournalAsync(inv, ct);
        }

        /// <summary>
        /// 作廢沖銷認列分錄：把本張發票原本的自動分錄「借貸對調」寫一筆反向分錄。
        ///
        /// 先前 VoidAsync 只改 Status（雖已檢查期間鎖、註解也寫「作廢以當下日期入帳」），
        /// 實際沒有任何分錄 → 認列時的 借2152 暫估應付／借1155 進項稅／貸2100 應付
        /// 全數殘留，2100 總帳與 AP 子帳（未清餘額已排除 Void）從此脫節。
        /// 對照組：FinPaymentService.VoidAPPaymentAsync 一直都有正確回沖。
        ///
        /// 以「對調原分錄」而非重算：認列是逐次累加的（進正常倉逐筆增量入帳），
        /// 重算金額無法還原每次增量的稅額拆分，只有對調能精準歸零。
        /// </summary>
        private async Task ReverseAccrualJournalAsync(APInvoice inv, CancellationToken ct)
        {
            if (_journalSvc == null) return;

            var alreadyReversed = await _db.JournalEntries.IgnoreQueryFilters()
                .AnyAsync(j => j.SourceDocType == ApVoidDocType && j.SourceDocId == inv.APInvoiceId, ct);
            if (alreadyReversed) return;

            // 同時納入退貨回沖分錄（APInvoiceReturn）：對調「認列＋已回沖」的全部行，
            // 淨額才會恰好歸零；只對調認列行會把退貨已沖掉的部分再沖一次。
            var originalLines = await _db.JournalEntries.IgnoreQueryFilters()
                .Where(j => (j.SourceDocType == ApDocType || j.SourceDocType == ApReturnDocType)
                         && j.SourceDocId == inv.APInvoiceId)
                .SelectMany(j => j.Lines)
                .ToListAsync(ct);
            if (originalLines.Count == 0) return;

            var reversal = originalLines
                .Select(l => new Modules.ACT.Services.JournalLineRequest(
                    l.AccountCode, l.AccountName,
                    Debit: l.Credit, Credit: l.Debit,       // 借貸對調
                    CurrencyCode: l.CurrencyCode, Amount: l.Amount,
                    ExchangeRate: l.ExchangeRate, AmountLocal: l.AmountLocal,
                    CostCenter: l.CostCenter, SourceRef: l.SourceRef))
                .ToList();

            await _journalSvc.CreateAutoEntryAsync(
                ApVoidDocType, inv.APInvoiceId, _dt.GetLocalDate(), inv.PlantId ?? "", reversal, ct: ct);
        }

        /// <summary>應付認列分錄的來源單別；反向分錄另立單別，避免再次作廢時被重複對調。</summary>
        private const string ApDocType       = "APInvoice";
        private const string ApReturnDocType = "APInvoiceReturn";
        private const string ApVoidDocType   = "APInvoice-Void";

        /// <summary>庫存過帳分錄的來源單別（<c>InventoryPostingService.InvDocType</c>）。</summary>
        private const string InvDocType      = "INV";

        public async Task<decimal> GetOutstandingBalanceAsync(string supplierCode, string plantId, CancellationToken ct = default)
        {
            return await _db.APInvoices
                .Where(i => i.SupplierCode == supplierCode && i.PlantId == plantId
                         && i.Status != APInvoiceStatus.Void
                         && (i.Status != APInvoiceStatus.Paid
                              // 已付清後才開折讓＝供應商欠我們（負餘額），也要看得到
                              || i.TotalAmount - i.PaidAmount - i.DebitedAmount < 0))
                .SumAsync(i => i.AmountLocal - (i.PaidAmount + i.DebitedAmount) * i.ExchangeRate, ct);   // 折讓也要扣
        }
    }
}
