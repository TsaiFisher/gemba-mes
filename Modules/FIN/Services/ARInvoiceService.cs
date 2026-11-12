using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Models;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.FIN.Services
{
    public class ARInvoiceService : IARInvoiceService
    {
        private readonly ApplicationDbContext _db;
        private readonly IDocNumberService _docNo;
        private readonly IDateTimeService _dt;
        private readonly ICurrencyService _currency;

        private readonly Modules.ACT.Services.IAccountingPeriodService? _periodSvc;  // optional：ACT 模組未啟用時為 null
        private readonly Modules.ACT.Services.IJournalEntryService? _journalSvc;     // optional：同上

        public ARInvoiceService(
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


        /// <summary>應收開票分錄的來源單別；退貨回沖與作廢各自另立單別。</summary>
        private const string ArDocType       = "ARInvoice";
        private const string ArReturnDocType = "ARInvoiceReturn";
        private const string ArVoidDocType   = "ARInvoice-Void";

        /// <summary>期間鎖倉前置檢查（roadmap-fin-cst-sub.md §8）；ACT 模組未啟用時略過。</summary>
        private Task AssertPeriodOpenAsync(DateOnly date, string? plantId, CancellationToken ct)
            => _periodSvc?.AssertCanPostAsync(date, plantId ?? string.Empty, ct) ?? Task.CompletedTask;

        public async Task<ARInvoice> GenerateFromShipmentAsync(string shipmentId, CancellationToken ct = default)
        {
            // Idempotent：已有「未作廢」的就直接回傳。作廢過的不算——作廢後重新確認出貨要能開出新發票
            // （唯一索引自 2026-10-22 起只管未作廢的，見 FIN_InvoiceSourceUniqueExcludesVoid）
            var existing = await _db.ARInvoices
                .FirstOrDefaultAsync(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == shipmentId
                                       && i.Status != ARInvoiceStatus.Void, ct);
            if (existing != null) return existing;

            var ship = await _db.GoodsShipments
                .Include(s => s.Lines)
                .FirstOrDefaultAsync(s => s.GoodsShipmentId == shipmentId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Fin_ShipmentNotFoundFmt", shipmentId));

            await AssertPeriodOpenAsync(ship.ShipDate, ship.PlantId, ct);

            var currency = ship.Lines.FirstOrDefault()?.CurrencyCode;

            // 匯率一律沿用出貨單存檔當下鎖住的那一個（GoodsShipmentController
            // .ResolveLineRatesAsync 以後端權威**賣出匯率**覆寫並寫進明細）。
            //
            // 原本是在這裡重查一次，而且沒指定型別——GetRateAsync 的預設是 Buy。
            // 銷售鏈其他每一段都明寫 RateType.Sell（SO／出貨／銷退／收款），
            // 只有開票退回買匯，於是同一筆銷售有兩個本幣金額：出貨單畫面上的
            // 是賣匯、帳上的應收與營收是買匯。差額不會落在任何科目——收款當下的
            // 匯兌損益會把它整個吸收掉，所以總帳照樣平、應收照樣歸零，
            // 沒有任何既有檢查會叫，只有營收金額是錯的。
            //
            // 沿用而非重查，另外還解掉一個時序問題：存檔到確認之間若有人補了
            // 或改了當日匯率，重查會取到與出貨單不同的值。
            // ⚠️ 「快照缺漏」的判準是 `== 1m`，不是 `<= 0m`。
            // `GoodsShipmentLine.ExchangeRate` 的模型預設值就是 **1**，而
            // `GoodsShipmentController.ResolveLineRatesAsync` 是
            // `if (rate.HasValue) lv.ExchangeRate = rate.Value;`——查不到匯率時它**不寫**，
            // 於是那一列停在 1 而不是 0。用 `> 0m` 當判準的話下面那條 fallback 永遠走不到，
            // 沒有快照的外幣單會靜靜地以匯率 1 開票（本幣金額等於外幣金額，總帳照樣平）。
            // 代價是「真的就等於 1」的匯率會被多查一次表——那種情況查回來還是 1，
            // 查不到則 `?? 1m` 也還是 1，兩條路殊途同歸，所以這個代價是零。
            var lineRate = ship.Lines.FirstOrDefault()?.ExchangeRate ?? 0m;
            var rate = currency == null
                ? 1m
                : lineRate > 0m && lineRate != 1m
                    ? lineRate
                    : (await _currency.GetRateAsync(currency, ship.ShipDate, RateType.Sell) ?? 1m);

            var invoice = new ARInvoice
            {
                ARInvoiceId   = await _docNo.NextAsync("ARInvoice", ship.PlantId ?? "", ct: ct),
                CustomerCode  = ship.CompanyCode,
                InvoiceDate   = ship.ShipDate,
                // 依客戶付款條件（結帳日／收款天數）；原本一律 +30，帳齡與呆帳提列因此失真
                DueDate       = await PaymentTerms.DueDateAsync(_db, ship.CompanyCode, ship.ShipDate, ct),
                CurrencyCode  = currency,
                ExchangeRate  = rate,
                SourceDocType = "GoodsShipment",
                SourceDocId   = shipmentId,
                PlantId       = ship.PlantId,
                ApprovalStatus = DocumentApprovalStatus.Approved,
                CreatedAt     = DateTime.UtcNow,
                UpdatedAt     = DateTime.UtcNow,
            };

            var lineCurrency = currency ?? await _currency.GetBaseCurrencyAsync() ?? "";
            int lineNo = 1;
            // 開票量要扣掉「開票前就已經**過帳**的退貨量」。
            // 退貨過帳當下若發票還沒開，ReverseForReturnedQtyAsync 查無發票即什麼都不做
            // （回 NoInvoice），此後就沒有第二次機會——這裡不扣，那一段退回量
            // 會永遠掛在應收上。2026-09-05 由新稽核 RETURN_SUBLEDGER_UNREVERSED 在
            // 既有資料上抓到（AP 側 API-2607-00002：退貨 04-23、開票 07-24，多認列 15.75）。
            //
            // ⚠️ 不可以拿 GoodsShipmentLine.ReturnedQty 來扣，儘管那個欄位就擺在眼前：
            // 它在退貨單**存草稿**當下就被加上去了（SalesReturnController.ApplyGsReturnedQtyAsync），
            // 而回沖要等到過帳。若草稿先於開票存在，這裡扣一次、之後過帳回沖再扣一次＝**扣兩次**。
            // 只數已過帳（Posted）的：走到這裡代表本張來源單從來沒有發票（上面 idempotent
            // 守門已擋掉已存在的情形），所以那些退貨當初的回沖必然是空跑，不會再回來扣第二次；
            // 而尚未過帳的退貨，回沖會在它自己過帳時發生，那時發票已經在了。
            var postedReturned = await _db.SalesReturnLines.AsNoTracking()
                .Where(l => l.GoodsShipmentId == shipmentId && l.ShipmentLineNo != null
                         && l.SalesReturn!.Status == Modules.INV.Models.DocStatus.Posted)
                .GroupBy(l => l.ShipmentLineNo!.Value)
                .Select(g => new { LineNo = g.Key, Qty = g.Sum(x => x.ReturnQty) })
                .ToListAsync(ct);

            foreach (var l in ship.Lines)
            {
                var billQty = l.ShipQty
                            - (postedReturned.FirstOrDefault(r => r.LineNo == l.LineNo)?.Qty ?? 0m);
                if (billQty <= 0m) continue;
                var lineAmt = await _currency.RoundAsync(billQty * l.UnitPrice, lineCurrency);
                // Tax.TaxRate 以小數存放（0.05 = 5%，見 BAS/Models/Tax.cs），單據明細直接複製該值，
                // 因此稅額為 lineAmt × TaxRate；早期誤加 / 100 使發票稅額縮成百分之一。
                var taxAmt  = await _currency.RoundAsync(lineAmt * l.TaxRate, lineCurrency);
                invoice.Lines.Add(new ARInvoiceLine
                {
                    LineNo       = lineNo++,
                    ProductCode  = l.ProductCode,
                    Qty          = billQty,
                    UomCode      = l.UomCode,
                    UnitPrice    = l.UnitPrice,
                    LineAmount   = lineAmt,
                    TaxCode      = l.TaxCode,
                    TaxRate      = l.TaxRate,
                    TaxAmount    = taxAmt,
                    SourceLineNo = l.LineNo,
                });
            }

            invoice.TaxAmount      = invoice.Lines.Sum(l => l.TaxAmount);
            invoice.TotalAmount    = invoice.Lines.Sum(l => l.LineAmount) + invoice.TaxAmount;
            invoice.AmountLocal    = await _currency.RoundAsync(invoice.TotalAmount * rate, await _currency.GetBaseCurrencyAsync() ?? "");

            _db.ARInvoices.Add(invoice);
            await _db.SaveChangesAsync(ct);

            // 自動日記帳：借 應收帳款（含稅）／貸 銷貨收入（淨額）＋貸 銷項稅額（稅）。
            // 先前開票完全不入帳，但收款端卻貸記 1170 應收帳款 → 應收科目只有貸方、
            // 變成負餘額，且損益表完全沒有營收。與 AP 認列同一口徑補上。
            if (_journalSvc != null)
            {
                var baseCurrency = await _currency.GetBaseCurrencyAsync() ?? "";
                var netLocal = await _currency.RoundAsync(
                    invoice.Lines.Sum(l => l.LineAmount) * rate, baseCurrency);
                var taxLocal = await _currency.RoundAsync(invoice.TaxAmount * rate, baseCurrency);
                var gross    = netLocal + taxLocal;
                if (gross > 0m)
                {
                    var jLines = new List<Modules.ACT.Services.JournalLineRequest>
                    {
                        new(Modules.ACT.StandardAccounts.AccountsReceivableCode,
                            Modules.ACT.StandardAccounts.AccountsReceivableName, gross, 0m),
                        new(Modules.ACT.StandardAccounts.SalesRevenueCode,
                            Modules.ACT.StandardAccounts.SalesRevenueName, 0m, netLocal),
                    };
                    if (taxLocal > 0m)
                        jLines.Add(new(Modules.ACT.StandardAccounts.OutputVatCode,
                            Modules.ACT.StandardAccounts.OutputVatName, 0m, taxLocal));

                    await _journalSvc.CreateAutoEntryAsync(
                        ArDocType, invoice.ARInvoiceId, ship.ShipDate, ship.PlantId ?? "", jLines, ct: ct);
                }
            }

            return invoice;
        }

        public async Task<ReturnReversalResult> ReverseForReturnedQtyAsync(
            string shipmentId, int shipmentLineNo, decimal qty, CancellationToken ct = default)
        {
            if (qty <= 0m) return ReturnReversalResult.NoInvoice;

            // 同一張出貨可能有作廢過的舊發票與重開的新發票：先找未作廢的；只剩作廢的才回「發票已作廢」
            var invoice = await _db.ARInvoices
                .Include(i => i.Lines)
                .Where(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == shipmentId)
                .OrderBy(i => i.Status == ARInvoiceStatus.Void ? 1 : 0)
                .FirstOrDefaultAsync(ct);
            if (invoice == null) return ReturnReversalResult.NoInvoice;   // 來源出貨未開立 AR → 無可回沖

            // 以來源出貨行號對應 AR 明細（SourceLineNo 於 GenerateFromShipmentAsync 寫入）。
            // 同品號多行時，品號比對會固定命中第一行，量價因此錯位——故一律走行號。
            //
            // ⚠️ 明細查找**必須在狀態守門之前**：已收款時要算得出「沒沖掉多少」，
            // 而那個金額只能從明細的量價算。原本先擋狀態再找明細，於是缺口金額
            // 根本沒有機會被算出來，這正是這個洞無聲的原因。
            var arLine = invoice.Lines.FirstOrDefault(l => l.SourceLineNo == shipmentLineNo);
            if (arLine == null || arLine.Qty <= 0m)
                return new ReturnReversalResult(invoice.ARInvoiceId, 0m, 0m, ReturnReversalOutcome.LineNotFound);

            var reverseQty = Math.Min(qty, arLine.Qty);

            if (invoice.Status == ARInvoiceStatus.Void)
                return new ReturnReversalResult(invoice.ARInvoiceId, 0m, 0m, ReturnReversalOutcome.VoidInvoice);

            if (invoice.Status != ARInvoiceStatus.Open)
            {
                // 已（部分）收款：本方案不動已結清發票的金額——改金額會讓憑證與
                // 稅務申報前後不一致。缺口算出來交給呼叫端，由人另開折讓單處理。
                var baseCcy = await _currency.GetBaseCurrencyAsync() ?? "";
                var gap = await _currency.RoundAsync(
                    reverseQty * arLine.UnitPrice * (1m + arLine.TaxRate) * invoice.ExchangeRate, baseCcy);
                return new ReturnReversalResult(
                    invoice.ARInvoiceId, 0m, gap, ReturnReversalOutcome.SettledInvoice);
            }

            var oldLineAmt = arLine.LineAmount;
            var oldTaxAmt  = arLine.TaxAmount;
            var lineCurrency = invoice.CurrencyCode ?? await _currency.GetBaseCurrencyAsync() ?? "";
            arLine.Qty        -= reverseQty;
            arLine.LineAmount  = await _currency.RoundAsync(arLine.Qty * arLine.UnitPrice, lineCurrency);
            arLine.TaxAmount   = await _currency.RoundAsync(arLine.LineAmount * arLine.TaxRate, lineCurrency);

            invoice.TaxAmount   = invoice.Lines.Sum(l => l.TaxAmount);
            invoice.TotalAmount = invoice.Lines.Sum(l => l.LineAmount) + invoice.TaxAmount;
            var baseCurrency    = await _currency.GetBaseCurrencyAsync() ?? "";
            invoice.AmountLocal = await _currency.RoundAsync(invoice.TotalAmount * invoice.ExchangeRate, baseCurrency);
            invoice.UpdatedAt   = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);

            // 回沖金額（本位幣含稅）在分錄之外先算好：它同時是分錄金額與回傳值，
            // 算兩次遲早會分岔（_journalSvc 為 null 的測試路徑就會拿到 0）。
            var decNetLocal = await _currency.RoundAsync(
                (oldLineAmt - arLine.LineAmount) * invoice.ExchangeRate, baseCurrency);
            var decTaxLocal = await _currency.RoundAsync(
                (oldTaxAmt - arLine.TaxAmount) * invoice.ExchangeRate, baseCurrency);
            var decGrossLocal = decNetLocal + decTaxLocal;

            // 銷貨退回反向分錄：借 銷貨收入 ＋ 借 銷項稅額 ／ 貸 應收帳款（與開票相反）
            if (_journalSvc != null)
            {
                if (decGrossLocal > 0m)
                {
                    var jLines = new List<Modules.ACT.Services.JournalLineRequest>
                    {
                        new(Modules.ACT.StandardAccounts.SalesRevenueCode,
                            Modules.ACT.StandardAccounts.SalesRevenueName, decNetLocal, 0m),
                    };
                    if (decTaxLocal > 0m)
                        jLines.Add(new(Modules.ACT.StandardAccounts.OutputVatCode,
                            Modules.ACT.StandardAccounts.OutputVatName, decTaxLocal, 0m));
                    jLines.Add(new(Modules.ACT.StandardAccounts.AccountsReceivableCode,
                        Modules.ACT.StandardAccounts.AccountsReceivableName, 0m, decGrossLocal));

                    await _journalSvc.CreateAutoEntryAsync(
                        ArReturnDocType, invoice.ARInvoiceId, _dt.GetLocalDate(),
                        invoice.PlantId ?? "", jLines, ct: ct);
                }
            }

            return new ReturnReversalResult(
                invoice.ARInvoiceId, decGrossLocal, 0m, ReturnReversalOutcome.Reversed);
        }

        /// <summary>核准後補開手動發票的認列分錄（與 AP 側同構，冪等）。</summary>
        public async Task PostManualJournalAsync(string invoiceId, CancellationToken ct = default)
        {
            if (_journalSvc == null) return;

            var inv = await _db.ARInvoices.IgnoreQueryFilters()
                .Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.ARInvoiceId == invoiceId, ct);
            if (inv == null || inv.SourceDocType != "Manual") return;

            var already = await _db.JournalEntries.IgnoreQueryFilters()
                .AnyAsync(j => j.SourceDocType == ArDocType && j.SourceDocId == invoiceId, ct);
            if (already) return;

            var jLines = await BuildManualJournalLinesAsync(inv, ct);
            if (jLines.Count == 0) return;

            await _journalSvc.CreateAutoEntryAsync(
                ArDocType, inv.ARInvoiceId, inv.InvoiceDate, inv.PlantId ?? "", jLines, ct: ct);
        }

        /// <param name="approvalStatus">建立當下的簽核狀態，由呼叫端解析；理由見 AP 側同名參數
        /// （在服務裡注入 IApprovalService 會造成循環相依，整個 app 起不來）。</param>
        public async Task<ARInvoice> CreateManualAsync(ARInvoice invoice,
            DocumentApprovalStatus? approvalStatus = null, CancellationToken ct = default)
        {
            await AssertPeriodOpenAsync(invoice.InvoiceDate, invoice.PlantId, ct);

            // §5.5 EP Invalid: 發票明細數量與單價基本驗證
            foreach (var line in invoice.Lines.Where(l => l.Qty <= 0 || l.UnitPrice < 0))
                throw new InvalidOperationException(
                    PzText.Get("Fin_ARLineQtyPriceFmt", line.LineNo));

            invoice.ARInvoiceId   = await _docNo.NextAsync("ARInvoice", invoice.PlantId ?? "", ct: ct);
            invoice.SourceDocType = "Manual";
            invoice.CreatedAt     = DateTime.UtcNow;
            invoice.UpdatedAt     = DateTime.UtcNow;
            // 與 AP 對稱：狀態由呼叫端解析後傳入，沒傳＝沒有線上簽核。
            invoice.ApprovalStatus = approvalStatus ?? DocumentApprovalStatus.Approved;

            // 與 AP 對稱：科目在寫入之前先解出來，解不出就整張擋下。
            var jLines = await BuildManualJournalLinesAsync(invoice, ct);

            _db.ARInvoices.Add(invoice);
            await _db.SaveChangesAsync(ct);

            if (invoice.ApprovalStatus == DocumentApprovalStatus.Approved
                && _journalSvc != null && jLines.Count > 0)
                await _journalSvc.CreateAutoEntryAsync(
                    ArDocType, invoice.ARInvoiceId, invoice.InvoiceDate, invoice.PlantId ?? "", jLines, ct: ct);

            return invoice;
        }

        /// <summary>
        /// 手動 AR 發票的開票分錄：借 1170 應收帳款／貸 各明細的收入科目／貸 2155 銷項稅額。
        ///
        /// 收入科目來自明細指定的 <c>ChartOfAccount</c>（唯一來源）；
        /// 解不出來就指名道姓地擋下，理由同 AP（管理日記帳無科目主檔，硬編預設只是靜默進錯科目）。
        /// 應收方取各貸方腿的合計，保證傳票自身平衡。
        /// </summary>
        private async Task<List<Modules.ACT.Services.JournalLineRequest>> BuildManualJournalLinesAsync(
            ARInvoice invoice, CancellationToken ct)
        {
            if (_journalSvc == null) return new();

            var rate = invoice.ExchangeRate == 0m ? 1m : invoice.ExchangeRate;

            // 收入科目的唯一來源是明細指定的科目（產品類別 fallback 已於
            // 2026-09-03 移除，理由見 AP 側 RevenueAccountFor 的對應說明）。
            // 用途一起濾——把費用科目填進 AR 發票會產出一張「貸 5900 其他營業費用」的傳票，
            // 借貸依然平衡、稽核也不會叫。
            // AllowManualEntry 一起濾：科目主檔 2026-09-21 起含自動分錄專用的科目，
            // 那些科目的餘額由過帳維護，手動指定進來就會與子帳對不起來。
            var directCodes = invoice.Lines.Select(l => l.AccountCode)
                .Where(c => !string.IsNullOrEmpty(c)).Select(c => c!).Distinct().ToList();
            var direct = directCodes.Count == 0
                ? new Dictionary<string, (string Code, string Name)>()
                : await _db.Set<ChartOfAccount>().IgnoreQueryFilters().AsNoTracking()
                    .Where(a => directCodes.Contains(a.AccountCode)
                             && a.IsActive && a.AllowManualEntry
                             && (a.AccountType == AccountType.Revenue
                              || a.AccountType == AccountType.OtherIncome))
                    .ToDictionaryAsync(a => a.AccountCode, a => (a.AccountCode, a.AccountName), ct);

            var byAccount = new Dictionary<(string Code, string Name), decimal>();
            foreach (var line in invoice.Lines.OrderBy(l => l.LineNo))
            {
                var key = RevenueAccountFor(direct, line.AccountCode, line.LineNo);
                var local = Math.Round(line.LineAmount * rate, 4, MidpointRounding.AwayFromZero);
                byAccount[key] = byAccount.TryGetValue(key, out var acc) ? acc + local : local;
            }

            var jLines = byAccount
                .Select(kv => new Modules.ACT.Services.JournalLineRequest(kv.Key.Code, kv.Key.Name, 0m, kv.Value))
                .ToList();

            var taxLocal = Math.Round(invoice.Lines.Sum(l => l.TaxAmount) * rate, 4, MidpointRounding.AwayFromZero);
            if (taxLocal > 0m)
                jLines.Add(new(Modules.ACT.StandardAccounts.OutputVatCode,
                               Modules.ACT.StandardAccounts.OutputVatName, 0m, taxLocal));

            var receivable = jLines.Sum(l => l.Credit);
            jLines.Insert(0, new(Modules.ACT.StandardAccounts.AccountsReceivableCode,
                                 Modules.ACT.StandardAccounts.AccountsReceivableName, receivable, 0m));
            return jLines;
        }

        /// <summary>
        /// 明細 → (科目代碼, 名稱)。**唯一來源是明細指定的 <c>ChartOfAccount</c>**，與 AP 側對稱。
        /// 產品類別 fallback 已於 2026-09-03 連同 <c>ProductCategory</c> 的科目欄位一起移除——
        /// 手動建單畫面不送品號、認列型發票走自己的分錄產生器，那條路徑沒有呼叫端。
        /// </summary>
        private static (string Code, string Name) RevenueAccountFor(
            Dictionary<string, (string Code, string Name)> direct, string? accountCode, int lineNo)
        {
            if (string.IsNullOrEmpty(accountCode))
                throw new InvalidOperationException(
                    PzText.Get("Fin_ARLineNoAccountFmt", lineNo));

            if (!direct.TryGetValue(accountCode, out var d))
                throw new InvalidOperationException(
                    PzText.Get("Fin_ARLineAccountInvalidFmt", lineNo, accountCode));

            return d;
        }

        public async Task<ARInvoice> ReissueFromSourceAsync(string voidedInvoiceId, CancellationToken ct = default)
        {
            var inv = await _db.ARInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.ARInvoiceId == voidedInvoiceId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Fin_InvoiceNotFound"));
            if (inv.Status != ARInvoiceStatus.Void)
                throw new InvalidOperationException(PzText.Get("Fin_ReissueNotVoidFmt", inv.ARInvoiceId));
            if (inv.SourceDocType != "GoodsShipment" || string.IsNullOrEmpty(inv.SourceDocId))
                throw new InvalidOperationException(PzText.Get("Fin_ReissueSourceNotSupportedFmt", inv.ARInvoiceId));
            var ship = await _db.GoodsShipments.AsNoTracking().FirstOrDefaultAsync(s => s.GoodsShipmentId == inv.SourceDocId, ct);
            if (ship == null || ship.Status is not (Modules.INV.Models.DocStatus.Confirmed or Modules.INV.Models.DocStatus.Posted))
                throw new InvalidOperationException(PzText.Get("Fin_ReissueSourceNotActiveFmt", inv.SourceDocId));
            var live = await _db.ARInvoices.AsNoTracking()
                .Where(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == inv.SourceDocId && i.Status != ARInvoiceStatus.Void)
                .Select(i => i.ARInvoiceId).FirstOrDefaultAsync(ct);
            if (live != null)
                throw new InvalidOperationException(PzText.Get("Fin_ReissueLiveExistsFmt", inv.SourceDocId, live));
            return await GenerateFromShipmentAsync(inv.SourceDocId, ct);
        }

        public async Task VoidAsync(string invoiceId, CancellationToken ct = default)
        {
            var inv = await _db.ARInvoices.FindAsync(new object[] { invoiceId }, ct)
                ?? throw new InvalidOperationException(PzText.Get("Fin_InvoiceNotFound"));
            // 與 AP 對稱：只要收過款就得先沖掉收款單。部分收款被放行的話，
            // 錢已經進銀行、應收卻被整筆回沖，應收帳款會留下永遠沒人沖的貸餘。
            if (inv.Status is ARInvoiceStatus.Received or ARInvoiceStatus.PartialReceived || inv.ReceivedAmount > 0m)
                throw new InvalidOperationException(PzText.Get("Fin_ReceivedInvoiceCannotVoid"));
            // 同 AP：已確認的銷貨折讓要先作廢
            if (inv.CreditedAmount != 0m)
                throw new InvalidOperationException(PzText.Get("Fin_InvoiceWithNotesCannotVoidFmt", inv.ARInvoiceId, inv.CreditedAmount.ToString("N2")));
            await AssertPeriodOpenAsync(_dt.GetLocalDate(), inv.PlantId, ct);   // 作廢以當下日期入帳
            inv.Status    = ARInvoiceStatus.Void;
            inv.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await ReverseInvoiceJournalAsync(inv, ct);
        }

        /// <summary>
        /// 作廢沖銷開票分錄：把本張發票原本的自動分錄「借貸對調」寫一筆反向分錄。
        /// 同 AP：連退貨回沖分錄（ARInvoiceReturn）一併對調，淨額才會恰好歸零。
        /// </summary>
        private async Task ReverseInvoiceJournalAsync(ARInvoice inv, CancellationToken ct)
        {
            if (_journalSvc == null) return;

            var alreadyReversed = await _db.JournalEntries.IgnoreQueryFilters()
                .AnyAsync(j => j.SourceDocType == ArVoidDocType && j.SourceDocId == inv.ARInvoiceId, ct);
            if (alreadyReversed) return;

            var originalLines = await _db.JournalEntries.IgnoreQueryFilters()
                .Where(j => (j.SourceDocType == ArDocType || j.SourceDocType == ArReturnDocType)
                         && j.SourceDocId == inv.ARInvoiceId)
                .SelectMany(j => j.Lines)
                .ToListAsync(ct);
            if (originalLines.Count == 0) return;

            var reversal = originalLines
                .Select(l => new Modules.ACT.Services.JournalLineRequest(
                    l.AccountCode, l.AccountName,
                    Debit: l.Credit, Credit: l.Debit,
                    CurrencyCode: l.CurrencyCode, Amount: l.Amount,
                    ExchangeRate: l.ExchangeRate, AmountLocal: l.AmountLocal,
                    CostCenter: l.CostCenter, SourceRef: l.SourceRef))
                .ToList();

            await _journalSvc.CreateAutoEntryAsync(
                ArVoidDocType, inv.ARInvoiceId, _dt.GetLocalDate(), inv.PlantId ?? "", reversal, ct: ct);
        }

        public async Task<decimal> GetOutstandingBalanceAsync(string customerCode, string plantId, CancellationToken ct = default)
        {
            return await _db.ARInvoices
                .Where(i => i.CustomerCode == customerCode && i.PlantId == plantId
                         && i.Status != ARInvoiceStatus.Void
                         && (i.Status != ARInvoiceStatus.Received
                              // 已收訖後才開折讓＝客戶多付（負餘額），也要看得到
                              || i.TotalAmount - i.ReceivedAmount - i.CreditedAmount < 0))
                .SumAsync(i => i.AmountLocal - (i.ReceivedAmount + i.CreditedAmount) * i.ExchangeRate, ct);   // 折讓也要扣
        }
    }
}
