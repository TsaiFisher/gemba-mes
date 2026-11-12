using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.ACT.Services
{
    /// <summary>
    /// 業務資料流一致性稽核服務實作（business_data_flow_reconciliation.md §3/§4）。
    /// 取代規格書早期的 T-SQL 稽核腳本：以 EF Core LINQ 實作，
    /// 修正原腳本的狀態過濾（草稿/作廢單誤計）、作廢發票未排除、廠別範圍與小數容忍值問題。
    /// </summary>
    public class DataAuditService : IDataAuditService
    {
        private readonly ApplicationDbContext _db;
        private readonly ISystemParamService? _sysParams;

        /// <summary>數量容忍值預設：NUMERIC(18,4) 最小單位（§9 P-7 可經 audit_qty_tolerance 覆寫）。</summary>
        internal const decimal DefaultQtyTolerance = 0.0001m;

        /// <summary>金額容忍值預設：本位幣最小精度（§5.15 尾差容忍同源；audit_amount_tolerance 可覆寫）。</summary>
        internal const decimal DefaultAmountTolerance = 0.01m;

        /// <summary>GR/IR 掛帳超期天數（§3.2）。</summary>
        internal const int GrIrAgingDays = 30;

        /// <summary>ERP 拋轉滯留時數（§2.2）。</summary>
        internal const int ErpSyncLagHours = 24;

        private decimal _qtyTolerance = DefaultQtyTolerance;
        private decimal _amountTolerance = DefaultAmountTolerance;
        private bool _tolerancesResolved;

        /// <summary>目前生效的數量容忍值（各稽核方法入口 ResolveTolerancesAsync 後有效）。</summary>
        private decimal QtyTolerance => _qtyTolerance;

        /// <summary>目前生效的金額容忍值。</summary>
        private decimal AmountTolerance => _amountTolerance;

        // 在外原料的讀取點（見 W2-F12）。選填以維持既有測試的建構方式；
        // 未注入時退回自建，語意與注入版本相同。
        private readonly Modules.PUR.Services.IOutsourceMaterialFlowReader _materialFlow;

        public DataAuditService(ApplicationDbContext db, ISystemParamService? sysParams = null,
            Modules.PUR.Services.IOutsourceMaterialFlowReader? materialFlow = null)
        {
            _db = db;
            _sysParams = sysParams;
            _materialFlow = materialFlow ?? new Modules.PUR.Services.OutsourceMaterialFlowReader(db);
        }

        /// <summary>
        /// 補上訊息鍵與參數，並以中性資源（正體中文）把 <c>ErrorDescription</c> 組出來。
        ///
        /// <para>刻意做成「包住 new 出來的物件」而不是多一個建構式：每個稽核的欄位組合都不一樣
        /// （有的沒 ProductCode、有的要 BlockingOverride），共用建構式的參數會長到沒人看得懂。</para>
        ///
        /// <para>參數一律先轉成字串：小數位數與日期格式是這筆異常的事實，不該隨顯示語系變動。
        /// 呼叫端該帶格式的自己帶（<c>ToString("F4")</c>／<c>ToString("yyyy-MM-dd")</c>），
        /// 其餘走不變文化的預設格式，避免同一筆資料在不同語系存出不同字串。</para>
        /// </summary>
        private static AuditFailureDetail Msg(
            AuditFailureDetail detail, string messageKey, params object?[] args)
        {
            var formatted = args
                .Select(a => a switch
                {
                    null              => "",
                    string str        => str,
                    IFormattable fmt  => fmt.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                    _                 => a.ToString() ?? "",
                })
                .ToArray();

            detail.MessageKey       = messageKey;
            detail.MessageArgs      = formatted;
            detail.ErrorDescription = AuditMessageTemplates.FormatDefault(messageKey, formatted);
            return detail;
        }

        /// <summary>§9 P-7：容忍值取系統參數，未設定/不合法（非正數）時退回預設；每個服務實例解析一次。</summary>
        private async Task ResolveTolerancesAsync()
        {
            if (_tolerancesResolved) return;
            _tolerancesResolved = true;
            if (_sysParams == null) return;

            if (decimal.TryParse(await _sysParams.GetAsync(ProjectZSystemParamKeys.AuditQtyTolerance), out var qty) && qty > 0)
                _qtyTolerance = qty;
            if (decimal.TryParse(await _sysParams.GetAsync(ProjectZSystemParamKeys.AuditAmountTolerance), out var amt) && amt > 0)
                _amountTolerance = amt;
        }

        public async Task<DataAuditResult> RunPreClosingAuditAsync(
            string periodId, string plantId, CancellationToken ct = default)
        {
            // IgnoreQueryFilters：稽核的是被關帳的那個廠，不是操作者當下切在哪個廠。
            _ = await _db.AccountingPeriods.AsNoTracking().IgnoreQueryFilters()
                    .FirstOrDefaultAsync(p => p.PeriodId == periodId && p.PlantId == plantId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Act_PeriodNotFoundFmt", plantId, periodId));
            // 限定本廠：原本傳 null 等於全公司一起稽核，期間分廠之後那會讓甲廠被乙廠的異常卡死。
            var result = await RunAllAsync(plantId, ct);
            // §3.10 WIP 期末快照一致性只在關帳前有意義（快照由期末重估建立，平時不存在）
            foreach (var f in await AuditWipSnapshotConsistencyAsync(periodId, plantId, ct))
                result.Failures.Add(f);
            return result;
        }

        public async Task<DataAuditResult> RunAllAsync(string? plantId = null, CancellationToken ct = default)
        {
            var result = new DataAuditResult();
            foreach (var findings in new[]
            {
                await AuditPoOverReceiptAsync(plantId, ct),
                await AuditGrIrAsync(plantId, ct),
                await AuditOutsourceBalanceAsync(plantId, ct),
                await AuditJournalBalanceAsync(plantId, ct),
                await AuditDuplicateJournalAsync(plantId, ct),
                await AuditInventoryRollForwardAsync(plantId, ct),
                await AuditSoOverShipmentAsync(plantId, ct),
                await AuditShipmentInvoiceGapAsync(plantId, ct),
                await AuditBackflushConsumptionAsync(plantId, ct),
                await AuditPoReceivedQtyDriftAsync(plantId, ct),
                await AuditSoShippedQtyDriftAsync(plantId, ct),
                await AuditErpSyncLagAsync(plantId, ct),
                await AuditWoCostJournalDriftAsync(plantId, ct),
                await AuditWorkOrderMaterialIssueAsync(plantId, ct),
                await AuditSalesReturnCostBasisAsync(plantId, ct),
                // 退貨已過帳但來源發票已結清 → 應收／應付沒被回沖。
                // 掛在這裡＝未處理就不准關帳：貨退回來了、錢還掛在帳上，
                // 而總帳每張傳票都平衡、帳齡看起來也正常，沒有別的地方會叫。
                await AuditUnreversedReturnSubledgerAsync(plantId, ct),
                // 孤兒過帳：唯讀盤查先行（dev 庫實跑 0 筆）才掛進來。
                // 掛在這裡的意思是「有孤兒就不准關帳」——庫存異動／傳票／發票對單據都是
                // 弱關聯（DocNo / SourceDocId 字串，無外鍵），資料庫層不會攔，
                // 對不起來的當下沒有任何地方會叫，只有月結才有機會發現。
                // 若某個部署歷史上已經有孤兒，關帳會被擋下並列出單號——那是刻意的，
                // 應該去清資料而不是把稽核關掉（故不做開關）。
                await AuditOrphanPostingAsync(plantId, ct),
                // FIFO 成本層總量 ≠ 在手量：計價地基歪掉，之後每一次出庫都取到錯的單價。
                // 掛在這裡＝沒清乾淨就不准關帳，理由與孤兒過帳同——數量型稽核與
                // 借貸平衡都看不到它，只有月結才有機會發現，而那時已經錯了一整期。
                await AuditFifoLayerBalanceAsync(plantId, ct),
                // 客供料落在非客供倉：客戶的料被算進我們的移動平均分母，
                // 自有料單價被壓低並擴散到之後每一張出貨的 COGS。
                // 掛在這裡＝沒調倉就不准關帳，理由與孤兒過帳同——數量正常、傳票平衡、
                // 其他十幾支稽核全部沉默，只有毛利莫名偏高，而那要到月結才看得出來。
                //
                // 2026-09-22 掛入。業主確認現行資料中沒有客供單（有也只是測試資料），
                // 所以不存在「既有部署被擋在月結當天」的風險——那正是唯讀盤查先行
                // （AuditOrphanPostingAsync 的前例）原本要規避的情況。
                await AuditCustomerSuppliedLocationAsync(plantId, ct),
                // 總帳存貨科目 ≠ 明細帳：某一類異動漏開傳票、或金額不同。數量全對、每張傳票都平衡，
                // 其他稽核全部沉默——穩定版 ADM 廠就這樣累積了 12.7 萬（2026-10-22 拆帳）。
                // 擋關帳：帳不平就不該結帳；歷史殘差以人工傳票更正後殘差歸零即解除。
                await AuditInventoryGlSubledgerAsync(plantId, ct),
            })
                foreach (var f in findings) result.Failures.Add(f);
            return result;
        }

        // ── §3.1 採購超收 ────────────────────────────────────────────────

        public async Task<List<AuditFailureDetail>> AuditPoOverReceiptAsync(string? plantId = null, CancellationToken ct = default)
        {
            await ResolveTolerancesAsync();
            // 有效收料 = 非草稿/作廢 GR 的（收料量 − 退貨量），依 PO 明細彙總
            var received = await (
                from grl in _db.GoodsReceiptLines.IgnoreQueryFilters().AsNoTracking()
                join gr in _db.GoodsReceipts.IgnoreQueryFilters().AsNoTracking()
                    on grl.GoodsReceiptId equals gr.GoodsReceiptId
                where grl.PurchaseOrderId != null && grl.PoLineNo != null
                      && gr.Status != DocStatus.Draft && gr.Status != DocStatus.Void
                      && (plantId == null || gr.PlantId == plantId)
                group grl by new { grl.PurchaseOrderId, grl.PoLineNo } into g
                select new
                {
                    g.Key.PurchaseOrderId,
                    g.Key.PoLineNo,
                    NetQty = g.Sum(x => x.ReceiptQty - x.ReturnedQty)
                }).ToListAsync(ct);

            if (received.Count == 0) return new List<AuditFailureDetail>();

            var poIds = received.Select(r => r.PurchaseOrderId!).Distinct().ToList();
            var poLines = await (
                from pol in _db.PurchaseOrderLines.IgnoreQueryFilters().AsNoTracking()
                join po in _db.PurchaseOrders.IgnoreQueryFilters().AsNoTracking()
                    on pol.PurchaseOrderId equals po.PurchaseOrderId
                where poIds.Contains(pol.PurchaseOrderId)
                select new { pol.PurchaseOrderId, pol.LineNo, pol.ProductCode, pol.OrderQty, po.PlantId })
                .ToListAsync(ct);
            var poMap = poLines.ToDictionary(p => (p.PurchaseOrderId, p.LineNo));

            var failures = new List<AuditFailureDetail>();
            foreach (var r in received)
            {
                if (!poMap.TryGetValue((r.PurchaseOrderId!, r.PoLineNo!.Value), out var pol)) continue;
                var over = r.NetQty - pol.OrderQty;
                if (over > QtyTolerance)
                    failures.Add(Msg(new AuditFailureDetail
                    {
                        AuditType = "PO_OVER_RECEIPT",
                        DocId = $"{r.PurchaseOrderId}#{r.PoLineNo}",
                        ProductCode = pol.ProductCode,
                        PlantId = pol.PlantId,
                        DifferenceAmount = over,
                    }, AuditMessages.PoOverReceipt, r.NetQty, pol.OrderQty, over));
            }
            return failures;
        }

        // ── §3.2 GR/IR 溢開與掛帳超期 ───────────────────────────────────

        public async Task<List<AuditFailureDetail>> AuditGrIrAsync(string? plantId = null, CancellationToken ct = default)
        {
            await ResolveTolerancesAsync();
            var grDocs = await (
                from gr in _db.GoodsReceipts.IgnoreQueryFilters().AsNoTracking()
                where (gr.Status == DocStatus.Confirmed || gr.Status == DocStatus.Posted)
                      && (plantId == null || gr.PlantId == plantId)
                select new
                {
                    gr.GoodsReceiptId,
                    gr.ReceiptDate,
                    gr.PlantId,
                    Qty = gr.Lines.Sum(l => l.ReceiptQty),
                    Amount = gr.Lines.Sum(l => l.LineAmount)
                }).ToListAsync(ct);

            if (grDocs.Count == 0) return new List<AuditFailureDetail>();

            var grIds = grDocs.Select(g => g.GoodsReceiptId).ToList();

            // 已過帳的採購退貨要從「暫估」側扣掉。
            //
            // 暫估的實體是總帳的 2152：上架時貸記（收貨量 × 單價），AP 認列時借回，
            // **而採購退貨過帳同樣借記 2152**（見 InventoryPostingService.BuildJournalLines
            // 的 (-1, "Purchase") 分支）。所以退掉的那一段在總帳上早就沖掉了，
            // 拿「整張收貨單的金額」當暫估基準，等於把已經沖掉的部分再算一次。
            //
            // 這個偏差原本看不出來：認列側（AccrueForReceivedQtyAsync）也沒有扣退貨量，
            // 兩邊同額高估剛好相消，GRIR_AGING 一次都沒叫過。2026-09-07 修好認列側
            // （W4-F12）之後才顯形——PE260400005 收 2 退 1，認列正確地變成 1 個，
            // 暫估卻還停在 2 個，於是憑空冒出 15 元的掛帳。
            //
            // 只數 Posted：草稿階段 GoodsReceiptLine.ReturnedQty 就被加上去了，但總帳的
            // 回沖要等到過帳，用那個欄位會比總帳早一步扣。與認列側同一個判準。
            var postedReturns = await _db.GoodsReturnLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.GoodsReceiptId != null && grIds.Contains(l.GoodsReceiptId)
                         && l.GoodsReturn!.Status == DocStatus.Posted)
                .GroupBy(l => l.GoodsReceiptId!)
                .Select(g => new { GoodsReceiptId = g.Key, Qty = g.Sum(x => x.ReturnQty) })
                .ToListAsync(ct);

            var returnedAmountByGr = new Dictionary<string, decimal>();
            var returnedQtyByGr    = new Dictionary<string, decimal>();
            if (postedReturns.Count > 0)
            {
                // 金額以**收貨單價**換算（＝上架當初貸記 2152 的口徑），不是退貨單自己的價。
                var returnLines = await _db.GoodsReturnLines.IgnoreQueryFilters().AsNoTracking()
                    .Where(l => l.GoodsReceiptId != null && grIds.Contains(l.GoodsReceiptId)
                             && l.ReceiptLineNo != null
                             && l.GoodsReturn!.Status == DocStatus.Posted)
                    .Select(l => new { l.GoodsReceiptId, l.ReceiptLineNo, l.ReturnQty })
                    .ToListAsync(ct);
                var grPrices = await _db.GoodsReceiptLines.IgnoreQueryFilters().AsNoTracking()
                    .Where(l => grIds.Contains(l.GoodsReceiptId))
                    .Select(l => new { l.GoodsReceiptId, l.LineNo, l.UnitPrice })
                    .ToListAsync(ct);

                foreach (var r in returnLines)
                {
                    var price = grPrices.FirstOrDefault(
                        x => x.GoodsReceiptId == r.GoodsReceiptId && x.LineNo == r.ReceiptLineNo!.Value)?.UnitPrice ?? 0m;
                    var key = r.GoodsReceiptId!;
                    returnedAmountByGr[key] = returnedAmountByGr.GetValueOrDefault(key) + r.ReturnQty * price;
                    returnedQtyByGr[key]    = returnedQtyByGr.GetValueOrDefault(key) + r.ReturnQty;
                }
            }
            var invoiced = await (
                from ap in _db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                where ap.Status != Modules.FIN.Models.APInvoiceStatus.Void
                      && ap.SourceDocType == "GoodsReceipt" && ap.SourceDocId != null
                      && grIds.Contains(ap.SourceDocId)
                select new
                {
                    ap.SourceDocId,
                    Qty = ap.Lines.Sum(l => l.Qty),
                    Amount = ap.Lines.Sum(l => l.LineAmount)
                }).ToListAsync(ct);
            var invMap = invoiced
                .GroupBy(i => i.SourceDocId!)
                .ToDictionary(g => g.Key, g => (Qty: g.Sum(x => x.Qty), Amount: g.Sum(x => x.Amount)));

            var agingCutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-GrIrAgingDays);
            var failures = new List<AuditFailureDetail>();
            foreach (var gr in grDocs)
            {
                invMap.TryGetValue(gr.GoodsReceiptId, out var inv);

                // 應認列量／暫估額都要淨掉已過帳的退貨（見上方 postedReturns 的說明）。
                var netQty    = gr.Qty    - returnedQtyByGr.GetValueOrDefault(gr.GoodsReceiptId);
                var netAmount = gr.Amount - returnedAmountByGr.GetValueOrDefault(gr.GoodsReceiptId);

                // 溢開（任何時點）：發票數量 > 應認列數量（金額差是合法 PPV，不以金額判溢開）
                var overQty = inv.Qty - netQty;
                if (overQty > QtyTolerance)
                    failures.Add(Msg(new AuditFailureDetail
                    {
                        AuditType = "GRIR_OVER_INVOICE",
                        DocId = gr.GoodsReceiptId,
                        PlantId = gr.PlantId,
                        DifferenceAmount = overQty,
                    }, AuditMessages.GrIrOverInvoice, inv.Qty, netQty, gr.Qty, gr.Qty - netQty));

                // 掛帳超期：收料超過 30 天，暫估金額仍未核銷完
                var openAmount = netAmount - inv.Amount;
                if (openAmount > AmountTolerance && gr.ReceiptDate < agingCutoff)
                    failures.Add(Msg(new AuditFailureDetail
                    {
                        AuditType = "GRIR_AGING",
                        DocId = gr.GoodsReceiptId,
                        PlantId = gr.PlantId,
                        DifferenceAmount = openAmount,
                    }, AuditMessages.GrIrAging,
                       gr.ReceiptDate.ToString("yyyy-MM-dd"), GrIrAgingDays, netAmount, inv.Amount, openAmount));
            }
            return failures;
        }

        // ── §3.3 委外在外原料平衡 ───────────────────────────────────────

        public async Task<List<AuditFailureDetail>> AuditOutsourceBalanceAsync(string? plantId = null, CancellationToken ct = default)
        {
            await ResolveTolerancesAsync();

            // (工單, 原料) → 淨發料量。讀取走 IOutsourceMaterialFlowReader：委外發料有兩個入口
            // （INV 委外領退料單／MFG 發退料單），兩者產生的庫存異動一模一樣；
            // 本稽核原本只查前者，於是走發退料單發出去的料**永遠不會被檢查到**（W2-F12）。
            var flows = await _materialFlow.NetIssuedByPlantAsync(plantId, ct);
            var balance = flows.ToDictionary(kv => kv.Key, kv => kv.Value.Qty);
            if (balance.Count == 0) return new List<AuditFailureDetail>();

            var woIds = balance.Keys.Select(k => k.Wo).Distinct().ToList();

            // 工單 → Routing → BOM 每單位用量（跨工序加總，對齊 OutsourceScrapPenaltyService）
            var wos = await _db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                .Where(w => woIds.Contains(w.WorkOrderId))
                .Select(w => new { w.WorkOrderId, w.RoutingId, w.PlantId })
                .ToListAsync(ct);
            var woMap = wos.ToDictionary(w => w.WorkOrderId);
            var routingIds = wos.Select(w => w.RoutingId).Distinct().ToList();
            var bomUsage = (await _db.RoutingBoms.IgnoreQueryFilters().AsNoTracking()
                    .Where(b => routingIds.Contains(b.RoutingId))
                    .Select(b => new { b.RoutingId, b.ProductCode, b.Quantity })
                    .ToListAsync(ct))
                .GroupBy(b => (b.RoutingId, b.ProductCode))
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

            // 委外收貨成品量（Confirmed/Posted），經 OPO 明細歸屬工單
            var opoLines = await _db.OutsourcePurchaseOrderLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => woIds.Contains(l.SourceWorkOrderId))
                .Select(l => new { l.OutsourcePurchaseOrderId, l.LineNo, l.SourceWorkOrderId })
                .ToListAsync(ct);
            var opoLineToWo = opoLines.ToDictionary(l => (l.OutsourcePurchaseOrderId, l.LineNo), l => l.SourceWorkOrderId);
            var opoIds = opoLines.Select(l => l.OutsourcePurchaseOrderId).Distinct().ToList();

            var receiptQtyByWo = new Dictionary<string, decimal>();
            if (opoIds.Count > 0)
            {
                var receiptLines = await (
                    from orl in _db.OutsourceReceiptLines.IgnoreQueryFilters().AsNoTracking()
                    join orc in _db.OutsourceReceipts.IgnoreQueryFilters().AsNoTracking()
                        on orl.OutsourceReceiptId equals orc.OutsourceReceiptId
                    where (orc.Status == DocStatus.Confirmed || orc.Status == DocStatus.Posted)
                          && opoIds.Contains(orc.OutsourcePurchaseOrderId)
                          && orl.OpoLineNo != null
                    select new { orc.OutsourcePurchaseOrderId, orl.OpoLineNo, orl.StockQty })
                    .ToListAsync(ct);
                foreach (var rl in receiptLines)
                {
                    if (!opoLineToWo.TryGetValue((rl.OutsourcePurchaseOrderId, rl.OpoLineNo!.Value), out var wo)) continue;
                    receiptQtyByWo[wo] = receiptQtyByWo.GetValueOrDefault(wo) + rl.StockQty;
                }
            }

            var failures = new List<AuditFailureDetail>();
            foreach (var ((wo, mat), netIssued) in balance)
            {
                if (!woMap.TryGetValue(wo, out var w)) continue;
                var usage = bomUsage.GetValueOrDefault((w.RoutingId, mat));
                var consumed = receiptQtyByWo.GetValueOrDefault(wo) * usage;
                var remaining = netIssued - consumed;
                if (remaining < -QtyTolerance)
                    failures.Add(Msg(new AuditFailureDetail
                    {
                        AuditType = "OUTSOURCE_NEG_BALANCE",
                        DocId = wo,
                        ProductCode = mat,
                        PlantId = w.PlantId,
                        DifferenceAmount = remaining,
                    }, AuditMessages.OutsourceNegBalance, netIssued, consumed, usage, remaining));
            }
            return failures;
        }

        // ── §3.4 傳票借貸平衡 ───────────────────────────────────────────

        public async Task<List<AuditFailureDetail>> AuditJournalBalanceAsync(string? plantId = null, CancellationToken ct = default)
        {
            await ResolveTolerancesAsync();
            var entries = await _db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
                .Where(e => plantId == null || e.PlantId == plantId)
                .Select(e => new
                {
                    e.JournalId,
                    e.PlantId,
                    Debit = e.Lines.Sum(l => l.Debit),
                    Credit = e.Lines.Sum(l => l.Credit)
                }).ToListAsync(ct);

            return entries
                .Where(e => Math.Abs(e.Debit - e.Credit) > AmountTolerance)
                .Select(e => Msg(new AuditFailureDetail
                {
                    AuditType = "JOURNAL_UNBALANCED",
                    DocId = e.JournalId,
                    PlantId = e.PlantId,
                    DifferenceAmount = e.Debit - e.Credit,
                }, AuditMessages.JournalUnbalanced, e.Debit, e.Credit, e.Debit - e.Credit)).ToList();
        }

        // ── 重複立帳（盲區補強）────────────────────────────────────────

        /// <summary>來源單據與自動傳票應為 1:1 的 SourceDocType（"INV" 逐行過帳可多張，不列入）。</summary>
        private static readonly string[] OneToOneJournalSources =
            { "APPayment", "ARReceipt", "WorkOrderCompletion", "LandedCostAllocation" };

        public async Task<List<AuditFailureDetail>> AuditDuplicateJournalAsync(string? plantId = null, CancellationToken ct = default)
        {
            var dups = await _db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.SourceDocId != null && OneToOneJournalSources.Contains(e.SourceDocType)
                            && (plantId == null || e.PlantId == plantId))
                .GroupBy(e => new { e.SourceDocType, e.SourceDocId, e.PlantId })
                .Where(g => g.Count() > 1)
                .Select(g => new { g.Key.SourceDocType, g.Key.SourceDocId, g.Key.PlantId, Count = g.Count() })
                .ToListAsync(ct);

            return dups.Select(d => Msg(new AuditFailureDetail
            {
                AuditType = "JOURNAL_DUPLICATE",
                DocId = $"{d.SourceDocType}:{d.SourceDocId}",
                PlantId = d.PlantId,
                DifferenceAmount = d.Count - 1,
            }, AuditMessages.JournalDuplicate, d.SourceDocType, d.SourceDocId, d.Count)).ToList();
        }

        // ── 庫存滾動平衡（盲區補強）────────────────────────────────────

        public async Task<List<AuditFailureDetail>> AuditInventoryRollForwardAsync(string? plantId = null, CancellationToken ct = default)
        {
            await ResolveTolerancesAsync();
            // 作廢單以反向 movement 沖銷（DocStatus.Void 註解），故全部 movement 直接淨額加總即可
            var moved = await _db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                .Where(m => plantId == null || m.PlantId == plantId)
                .GroupBy(m => new { m.PlantId, m.ProductCode, m.LocationCode, m.LotNo })
                .Select(g => new
                {
                    g.Key.PlantId,
                    g.Key.ProductCode,
                    g.Key.LocationCode,
                    g.Key.LotNo,
                    Qty = g.Sum(x => x.StockQty * x.Direction)
                }).ToListAsync(ct);

            var onHand = await _db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => plantId == null || i.PlantId == plantId)
                .Select(i => new { i.PlantId, i.ProductCode, i.LocationCode, i.LotNo, i.QtyOnHand })
                .ToListAsync(ct);

            var movedMap = moved.ToDictionary(m => (m.PlantId, m.ProductCode, m.LocationCode, m.LotNo), m => m.Qty);
            var onHandMap = onHand.ToDictionary(i => (i.PlantId, i.ProductCode, i.LocationCode, i.LotNo), i => i.QtyOnHand);

            var failures = new List<AuditFailureDetail>();
            foreach (var key in movedMap.Keys.Union(onHandMap.Keys))
            {
                var movedQty = movedMap.GetValueOrDefault(key);
                var onHandQty = onHandMap.GetValueOrDefault(key);
                var diff = movedQty - onHandQty;
                if (Math.Abs(diff) > QtyTolerance)
                    failures.Add(Msg(new AuditFailureDetail
                    {
                        AuditType = "INV_ROLLFORWARD",
                        DocId = $"{key.Item3}@{(string.IsNullOrEmpty(key.Item4) ? "-" : key.Item4)}",
                        ProductCode = key.Item2,
                        PlantId = key.Item1,
                        DifferenceAmount = diff,
                    },
                    // 「無批號」是兩句不同的話，不是一個叫「無」的批號——後者翻成英文會變成 lot "無"。
                    string.IsNullOrEmpty(key.Item4)
                        ? AuditMessages.InvRollforwardNoLot
                        : AuditMessages.InvRollforward,
                    movedQty, onHandQty, diff, key.Item3, key.Item4));
            }
            return failures;
        }

        // ── 總帳存貨科目 ↔ 庫存明細帳（2026-10-22）──────────────────────

        /// <summary>單據層級的差異最多列幾筆：殘差通常由少數幾張單造成，全列會把儀表板淹掉。</summary>
        internal const int MaxDocDiffFindings = 50;

        public async Task<List<AuditFailureDetail>> AuditInventoryGlSubledgerAsync(string? plantId = null, CancellationToken ct = default)
        {
            await ResolveTolerancesAsync();
            var defaultMethod = _sysParams == null ? null
                : await _sysParams.GetAsync(ProjectZSystemParamKeys.InvDefaultCostMethod);
            var bridges = await new InventoryGlReconciler(_db).ReconcileAsync(plantId, AmountTolerance, defaultMethod, ct);

            var failures = new List<AuditFailureDetail>();
            foreach (var b in bridges)
            {
                foreach (var w in b.WorkOrders)
                {
                    var closed = w.Status is Modules.MFG.Models.WorkOrderStatus.Closed or Modules.MFG.Models.WorkOrderStatus.Void;
                    if (closed && Math.Abs(w.Amount) > w.Tolerance)
                        failures.Add(Msg(new AuditFailureDetail
                        {
                            AuditType = "INV_GL_WIP_CLOSED", DocId = w.WorkOrderId, PlantId = b.PlantId, DifferenceAmount = w.Amount,
                        }, w.Status == Modules.MFG.Models.WorkOrderStatus.Void ? AuditMessages.InvGlWipVoided : AuditMessages.InvGlWipClosed,
                           w.WorkOrderId, w.Amount.ToString("N4")));
                    else if (w.Amount < -w.Tolerance)
                        failures.Add(Msg(new AuditFailureDetail
                        {
                            AuditType = "INV_GL_WIP_NEGATIVE", DocId = w.WorkOrderId, PlantId = b.PlantId, DifferenceAmount = w.Amount,
                        }, AuditMessages.InvGlWipNegative, w.WorkOrderId, w.Amount.ToString("N4")));
                }

                if (Math.Abs(b.Residual) <= b.Tolerance) continue;
                failures.Add(Msg(new AuditFailureDetail
                {
                    AuditType = "INV_GL_SUBLEDGER", DocId = StandardAccounts.RawMaterialInventoryCode, PlantId = b.PlantId,
                    DifferenceAmount = b.Residual,
                }, AuditMessages.InvGlSubledgerResidual,
                   b.GlBalance.ToString("N4"), b.SubledgerValue.ToString("N4"), b.WipInGl.ToString("N4"),
                   b.CustomerOwned.ToString("N4"), b.LentOut.ToString("N4"), b.Residual.ToString("N4")));

                // 找來源用的明細：只在殘差不為 0 時列，帳平了就不該再有提示（例如以人工傳票更正後）
                failures.AddRange(b.UnexplainedDocs.Take(MaxDocDiffFindings).Select(d => Msg(new AuditFailureDetail
                {
                    AuditType = "INV_GL_DOC_DIFF", DocId = d.DocNo, PlantId = b.PlantId, DifferenceAmount = d.Diff,
                }, AuditMessages.InvGlDocDiff, d.DocNo, d.TypeCodes, d.GlAmount.ToString("N4"),
                   d.MovementValue.ToString("N4"), d.Diff.ToString("N4"))));
            }
            return failures;
        }

        // ── §1.2-1 銷售超出貨 ───────────────────────────────────────────

        public async Task<List<AuditFailureDetail>> AuditSoOverShipmentAsync(string? plantId = null, CancellationToken ct = default)
        {
            await ResolveTolerancesAsync();
            var shipped = await (
                from gsl in _db.GoodsShipmentLines.IgnoreQueryFilters().AsNoTracking()
                join gs in _db.GoodsShipments.IgnoreQueryFilters().AsNoTracking()
                    on gsl.GoodsShipmentId equals gs.GoodsShipmentId
                where gsl.SalesOrderId != null && gsl.SoLineNo != null
                      && gs.Status != DocStatus.Draft && gs.Status != DocStatus.Void
                      && (plantId == null || gs.PlantId == plantId)
                group gsl by new { gsl.SalesOrderId, gsl.SoLineNo } into g
                select new
                {
                    g.Key.SalesOrderId,
                    g.Key.SoLineNo,
                    NetQty = g.Sum(x => x.ShipQty - x.ReturnedQty)
                }).ToListAsync(ct);

            if (shipped.Count == 0) return new List<AuditFailureDetail>();

            var soIds = shipped.Select(s => s.SalesOrderId!).Distinct().ToList();
            var soLines = await (
                from sol in _db.SalesOrderLines.IgnoreQueryFilters().AsNoTracking()
                join so in _db.SalesOrders.IgnoreQueryFilters().AsNoTracking()
                    on sol.SalesOrderId equals so.SalesOrderId
                where soIds.Contains(sol.SalesOrderId)
                select new { sol.SalesOrderId, sol.LineNo, sol.ProductCode, sol.OrderQty, so.PlantId })
                .ToListAsync(ct);
            var soMap = soLines.ToDictionary(s => (s.SalesOrderId, s.LineNo));

            var failures = new List<AuditFailureDetail>();
            foreach (var s in shipped)
            {
                if (!soMap.TryGetValue((s.SalesOrderId!, s.SoLineNo!.Value), out var sol)) continue;
                var over = s.NetQty - sol.OrderQty;
                if (over > QtyTolerance)
                    failures.Add(Msg(new AuditFailureDetail
                    {
                        AuditType = "SO_OVER_SHIPMENT",
                        DocId = $"{s.SalesOrderId}#{s.SoLineNo}",
                        ProductCode = sol.ProductCode,
                        PlantId = sol.PlantId,
                        DifferenceAmount = over,
                    }, AuditMessages.SoOverShipment, s.NetQty, sol.OrderQty, over));
            }
            return failures;
        }

        // ── §1.2-2 出貨確認未開立 AR 發票 ──────────────────────────────

        public async Task<List<AuditFailureDetail>> AuditShipmentInvoiceGapAsync(string? plantId = null, CancellationToken ct = default)
        {
            var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
            // 期間治理的下界：該廠最早會計期間的起日。早於它的單據仍然列出來（真的沒開發票），
            // 但只當提示，不擋關帳——理由見 BuildGovernanceStartAsync。
            var govStart = await BuildGovernanceStartAsync(plantId, ct);
            var gsDocs = await _db.GoodsShipments.IgnoreQueryFilters().AsNoTracking()
                .Where(gs => (gs.Status == DocStatus.Confirmed || gs.Status == DocStatus.Posted)
                             && gs.ShipDate <= cutoff
                             && (plantId == null || gs.PlantId == plantId))
                .Select(gs => new { gs.GoodsShipmentId, gs.ShipDate, gs.PlantId, gs.TotalAmount })
                .ToListAsync(ct);
            if (gsDocs.Count == 0) return new List<AuditFailureDetail>();

            var gsIds = gsDocs.Select(g => g.GoodsShipmentId).ToList();
            var invoicedIds = await _db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                .Where(ar => ar.Status != Modules.FIN.Models.ARInvoiceStatus.Void
                             && ar.SourceDocType == "GoodsShipment" && ar.SourceDocId != null
                             && gsIds.Contains(ar.SourceDocId))
                .Select(ar => ar.SourceDocId!)
                .Distinct()
                .ToListAsync(ct);
            var invoicedSet = invoicedIds.ToHashSet();

            return gsDocs
                .Where(gs => !invoicedSet.Contains(gs.GoodsShipmentId))
                .Select(gs =>
                {
                    var start   = govStart.TryGetValue(gs.PlantId ?? "", out var d) ? d : (DateOnly?)null;
                    var preOpen = start != null && gs.ShipDate < start.Value;
                    return Msg(new AuditFailureDetail
                    {
                        AuditType = "GS_NO_AR_INVOICE",
                        DocId = gs.GoodsShipmentId,
                        PlantId = gs.PlantId,
                        DifferenceAmount = gs.TotalAmount,
                        BlockingOverride = preOpen ? false : null,
                    },
                    // 期初前那句是整句而不是接在後面的片語：中文可以用「；」串，
                    // 英文接上去會變成兩個主詞黏在一起的病句。
                    preOpen ? AuditMessages.GsNoArInvoicePreOpen : AuditMessages.GsNoArInvoice,
                    gs.GoodsShipmentId,
                    gs.ShipDate.ToString("yyyy-MM-dd"),
                    start?.ToString("yyyy-MM-dd"));
                }).ToList();
        }

        /// <summary>
        /// 每廠「期間治理起日」＝該廠最早一筆 <c>AccountingPeriod</c> 的起日；沒有任何期間的廠不設下界。
        ///
        /// <para><b>為什麼要有下界</b>：本系統對「沒有期間列」的日期是**明確放行**的——
        /// <c>AccountingPeriodService.AssertCanPostAsync</c> 查無期間即靜默通過，而
        /// <c>AccountingPeriodSeeder</c> 又**刻意不建立過去的期間**（它的註解寫得很清楚：
        /// 補一個 Open 的過去期間，會讓早就該關帳的月份憑空多出一個可過帳的視窗）。
        /// 兩件事合起來的意思是：期初前的日期不在期間治理範圍內。</para>
        ///
        /// <para>但「單據完整性」型的稽核原本沒有任何下界，於是導入前的歷史單據
        /// （期初餘額承接的那些）會變成永久的關帳阻擋：期間建不出來、單據不能改，
        /// 補開發票又會把營收記進一個不存在的期間、且沒有對應的銷貨成本。
        /// 2026-09-07 在 dev 庫實測 ADM 廠 GS-2605-00006／00007（出貨日 2026-05-13、
        /// 最早期間 202606、總帳首筆 2026-05-28）就是這個形狀——和 W4-F7 的
        /// ERP_SYNC_LAG 同一種病：一個誰都過不了的閘門。</para>
        ///
        /// <para><b>只用於單據完整性型稽核</b>。餘額型稽核（FIFO 成本層、子帳未回沖）
        /// 談的是「今天的餘額對不對」，與來源單據的日期無關，一律不設下界。</para>
        /// </summary>
        private async Task<Dictionary<string, DateOnly>> BuildGovernanceStartAsync(
            string? plantId, CancellationToken ct)
            => await _db.AccountingPeriods.IgnoreQueryFilters().AsNoTracking()
                .Where(p => plantId == null || p.PlantId == plantId)
                .GroupBy(p => p.PlantId!)
                .Select(g => new { PlantId = g.Key, Start = g.Min(x => x.StartDate) })
                .ToDictionaryAsync(x => x.PlantId, x => x.Start, ct);

        // ── §3.8 Backflush 後沖消耗核對（P-5）────────────────────────────

        public async Task<List<AuditFailureDetail>> AuditBackflushConsumptionAsync(string? plantId = null, CancellationToken ct = default)
        {
            await ResolveTolerancesAsync();
            // 範圍：已入庫（Closed）工單的 Backflush 用料，**不分後沖時機**。
            // 完工入庫（ProductionCompletionService）以 DocNo = 工單號 + "-BF" 過帳 IO 生產領料，
            // 後沖量 = RequiredQty × (完工量 / 工單量)（CalcBackflushQty）；本檢核以同公式回算比對。
            //
            // 不篩 BackflushTiming 的理由：工單結案時的**累計**應沖量與時機無關——
            // AtCompletion 一次沖、PerReport 分次沖，總量都是同一個公式。原本只查 AtCompletion，
            // 於是 PerReport 的用料落進一個沒有任何程式碼負責的縫隙裡：投產不擋（後沖料免料況閘）、
            // 報工不沖（無實作）、完工不沖（被時機篩掉）、稽核不查——料就這樣從頭到尾沒被扣過，
            // 而工單成本少算一整項。這裡改為一併納入，至少讓它變成看得見的異常。
            var mats = await (
                from m in _db.WorkOrderMaterials.IgnoreQueryFilters().AsNoTracking()
                join w in _db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                    on m.WorkOrderId equals w.WorkOrderId
                where m.IssueMethod == Modules.MFG.Models.MaterialIssueMethod.Backflush
                      && w.Status == Modules.MFG.Models.WorkOrderStatus.Closed
                      && (plantId == null || w.PlantId == plantId)
                select new { m.WorkOrderId, m.ProductCode, m.RequiredQty, w.Quantity, w.PlantId })
                .ToListAsync(ct);
            if (mats.Count == 0) return new List<AuditFailureDetail>();

            var woIds = mats.Select(m => m.WorkOrderId).Distinct().ToList();
            var bfDocNos = woIds.Select(w => w + "-BF").ToList();

            // 完工量 = IT 工單繳庫淨額（StockQty 恆正、Direction 帶號；作廢以反向 movement 沖銷）
            var completed = (await _db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .Where(mv => mv.TypeCode == "IT" && woIds.Contains(mv.DocNo))
                    .Select(mv => new { mv.DocNo, mv.StockQty, mv.Direction })
                    .ToListAsync(ct))
                .GroupBy(x => x.DocNo)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.StockQty * x.Direction));

            // 實際後沖 = IO（DocNo = 工單 + "-BF"）出庫淨額（消耗取正值）
            var actual = (await _db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .Where(mv => mv.TypeCode == "IO" && bfDocNos.Contains(mv.DocNo))
                    .Select(mv => new { mv.DocNo, mv.ProductCode, mv.StockQty, mv.Direction })
                    .ToListAsync(ct))
                .GroupBy(x => (Wo: x.DocNo.Substring(0, x.DocNo.Length - 3), Mat: x.ProductCode))
                .ToDictionary(g => g.Key, g => g.Sum(x => -(x.StockQty * x.Direction)));

            // 標準後沖：跨工序同料聚合（同一 BF 單據的 movement 依品號合併，須同粒度比對）
            var expected = new Dictionary<(string Wo, string Mat), decimal>();
            var plantByWo = new Dictionary<string, string?>();
            foreach (var m in mats)
            {
                plantByWo[m.WorkOrderId] = m.PlantId;
                if (m.Quantity <= 0) continue;
                var key = (m.WorkOrderId, m.ProductCode);
                expected[key] = expected.GetValueOrDefault(key)
                                + m.RequiredQty * (completed.GetValueOrDefault(m.WorkOrderId) / m.Quantity);
            }

            var failures = new List<AuditFailureDetail>();
            foreach (var key in expected.Keys.Union(actual.Keys))
            {
                var exp = expected.GetValueOrDefault(key);
                var act = actual.GetValueOrDefault(key);
                var diff = act - exp;
                if (Math.Abs(diff) > QtyTolerance)
                    failures.Add(Msg(new AuditFailureDetail
                    {
                        AuditType = "BACKFLUSH_MISMATCH",
                        DocId = key.Wo,
                        ProductCode = key.Mat,
                        PlantId = plantByWo.GetValueOrDefault(key.Wo),
                        DifferenceAmount = diff,
                    }, AuditMessages.BackflushMismatch, act, exp, diff));
            }
            return failures;
        }

        // ── §3.12 應領未領（P-6）────────────────────────────────────────

        public async Task<List<AuditFailureDetail>> AuditWorkOrderMaterialIssueAsync(
            string? plantId = null, CancellationToken ct = default)
        {
            // 只查「完全沒領」：IssuedQty − ReturnedQty ≤ 0 而 RequiredQty > 0。
            // 少領多少不判——超領不設上限，少領同樣可能是正常的（見介面註解）。
            // Backflush 不在範圍：它本來就不經領料，由 §3.8 負責。
            var offenders = await (
                from m in _db.WorkOrderMaterials.IgnoreQueryFilters().AsNoTracking()
                join w in _db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                    on m.WorkOrderId equals w.WorkOrderId
                where m.IssueMethod != Modules.MFG.Models.MaterialIssueMethod.Backflush
                      && m.RequiredQty > 0
                      && m.IssuedQty - m.ReturnedQty <= 0
                      && w.Status == Modules.MFG.Models.WorkOrderStatus.Closed
                      && (plantId == null || w.PlantId == plantId)
                select new { m.WorkOrderId, m.ProductCode, m.RequiredQty, m.IssueMethod, w.PlantId })
                .ToListAsync(ct);

            return offenders.Select(o => Msg(new AuditFailureDetail
            {
                AuditType = "WO_MATERIAL_NOT_ISSUED",
                DocId = o.WorkOrderId,
                ProductCode = o.ProductCode,
                PlantId = o.PlantId,
                DifferenceAmount = o.RequiredQty,
            }, AuditMessages.WoMaterialNotIssued, o.IssueMethod, o.ProductCode, o.RequiredQty)).ToList();
        }

        // ── §3.13 銷退入庫成本偏離來源出貨 ──────────────────────────────

        public async Task<List<AuditFailureDetail>> AuditSalesReturnCostBasisAsync(
            string? plantId = null, CancellationToken ct = default)
        {
            await ResolveTolerancesAsync();

            // 銷退入庫（Sales 類、方向 +1）逐行對回來源出貨異動（同一行、方向 −1）。
            // 兩者的單位成本必須相同：退回來的是當初出去的那一批貨。
            //
            // 對「庫存異動」而不是對傳票：兩邊是同一筆數字的兩種寫法，出貨成本與
            // 銷退成本各自都會如實產生一張平衡的傳票——差異只在把哪個數字寫進去，
            // 所以借貸平衡稽核永遠不會叫，數量型的 INV_ROLLFORWARD 也不會叫。
            //
            // 這支稽核抓的是**歷史資料**：計價來源修好之後新單不會再偏，
            // 但已過帳的傳票不能改寫，只能列出來讓人決定怎麼調整。
            // ⚠️ 必須排除已作廢／草稿的銷退單：作廢會補一筆反向異動，兩筆淨額歸零、
            // 傳票也已沖回，帳上根本沒有偏差了——但原始那筆 +1 異動仍留在表裡，
            // 不濾掉就會被永遠報成缺口。而這條稽核是**擋月結**的（RunAllAsync），
            // 於是「發現計價錯誤 → 作廢重開」這個唯一的正確處置方式，會讓帳從此關不了。
            // 2026-09-05 Wave 3 實測到（GR-2609-00001 作廢後仍被列 1473.30）。
            //
            // 出貨端取該行的**淨額加權平均**（出庫 − 沖銷），與計價來源 SalesReturnCostBasis 同口徑：
            // 分批揀貨、取消確認後再出貨，同一行會有多筆單價不同的出庫列，逐筆配對會把
            // 正確的銷退報成偏差（2026-10-23 一併修正）。
            var returns = await (
                from r in _db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                join sr in _db.SalesReturns.IgnoreQueryFilters().AsNoTracking()
                    on r.DocNo equals sr.SalesReturnId
                where r.TypeCategory == "Sales" && r.Direction == 1
                      && r.SourceDocNo != null && r.SourceDocLineNo != null
                      && sr.Status != DocStatus.Void && sr.Status != DocStatus.Draft
                      && (plantId == null || r.PlantId == plantId)
                select new
                {
                    r.DocNo, r.ProductCode, r.PlantId, r.StockQty, ReturnCost = r.UnitCost,
                    ShipDoc = r.SourceDocNo!, ShipLine = r.SourceDocLineNo!.Value,
                })
                .ToListAsync(ct);

            var shipDocs = returns.Select(r => r.ShipDoc).Distinct().ToList();
            var shipCosts = (await _db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .Where(s => shipDocs.Contains(s.DocNo) && s.TypeCategory == "Sales")
                    .GroupBy(s => new { s.DocNo, s.DocLineNo })
                    .Select(g => new
                    {
                        g.Key.DocNo, g.Key.DocLineNo,
                        Qty   = g.Sum(s => -s.Direction * s.StockQty),
                        Value = g.Sum(s => -s.Direction * s.StockQty * (s.UnitCost ?? 0m)),
                    })
                    .ToListAsync(ct))
                .Where(g => g.Qty > 0m)
                .ToDictionary(g => (g.DocNo, g.DocLineNo), g => g.Value / g.Qty);

            var failures = new List<AuditFailureDetail>();
            foreach (var p in returns)
            {
                if (!shipCosts.TryGetValue((p.ShipDoc, p.ShipLine), out var shipCost)) continue;
                var diff = ((p.ReturnCost ?? 0m) - shipCost) * p.StockQty;
                if (Math.Abs(diff) <= AmountTolerance) continue;
                failures.Add(Msg(new AuditFailureDetail
                {
                    AuditType = "SALES_RETURN_COST_DRIFT",
                    DocId = p.DocNo,
                    ProductCode = p.ProductCode,
                    PlantId = p.PlantId,
                    DifferenceAmount = diff,
                }, AuditMessages.SalesReturnCostBasis,
                   p.ReturnCost?.ToString("F4"), p.ShipDoc, shipCost.ToString("F4"),
                   p.StockQty, diff.ToString("F2")));
            }
            return failures;
        }

        // ── §3.15 FIFO 成本層總量 vs 在手量 ──────────────────────────────

        public async Task<List<AuditFailureDetail>> AuditFifoLayerBalanceAsync(
            string? plantId = null, CancellationToken ct = default)
        {
            await ResolveTolerancesAsync();

            // 只查 FIFO 計價的品號：MA 品號根本不建成本層，全部拿來比會整批誤報。
            var fifoProducts = await (
                from p in _db.Products.IgnoreQueryFilters().AsNoTracking()
                join c in _db.ProductCategories.IgnoreQueryFilters().AsNoTracking()
                    on p.CategoryCode equals c.CategoryCode
                where c.CostMethod == Modules.BAS.Models.CostMethod.Fifo
                select p.ProductCode).ToListAsync(ct);
            if (fifoProducts.Count == 0) return new List<AuditFailureDetail>();

            // 逐 (廠, 品號, 儲位, 批) 比對——不是廠級彙總。
            //
            // 成本層與 ConsumeLayersAsync 的鍵都是「儲位＋批」，所以這才是出庫時真正被
            // 檢查的那個不變量。廠級彙總會漏掉「貨換了儲位、層留在原地」這一整類：
            // 兩個儲位一多一少剛好相消，廠級總量完全相等，而下一次從新儲位出庫
            // 必然撞上「FIFO 成本層不足以扣除」——在手量從頭到尾正確，現場只會看到
            // 一個莫名其妙的錯誤訊息。
            //
            // 這支曾經**刻意**做成廠級，理由是「備料移到暫存儲位只搬數量不搬層」。
            // 那句話描述的是 W4-F9 的缺陷本身，不是設計：同廠調撥（備料、IQC 上架都走
            // PostSamePlantTransferAsync）現在會把來源儲位的層扣掉、在目的儲位補一層同單價的。
            // 缺陷補掉之後，那個放寬就只剩下「把同一類缺陷繼續藏起來」的作用。
            var onHand = await _db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => fifoProducts.Contains(i.ProductCode)
                         && (plantId == null || i.PlantId == plantId))
                .GroupBy(i => new { i.PlantId, i.ProductCode, i.LocationCode, i.LotNo })
                .Select(g => new { g.Key.PlantId, g.Key.ProductCode, g.Key.LocationCode, g.Key.LotNo,
                                   Qty = g.Sum(x => x.QtyOnHand) })
                .ToListAsync(ct);

            var layers = await _db.Set<Modules.INV.Models.InventoryCostLayer>().IgnoreQueryFilters().AsNoTracking()
                .Where(l => fifoProducts.Contains(l.ProductCode)
                         && (plantId == null || l.PlantId == plantId))
                .GroupBy(l => new { l.PlantId, l.ProductCode, l.LocationCode, l.LotNo })
                .Select(g => new { g.Key.PlantId, g.Key.ProductCode, g.Key.LocationCode, g.Key.LotNo,
                                   Qty = g.Sum(x => x.QtyRemaining),
                                   Value = g.Sum(x => x.QtyRemaining * x.UnitCost) })
                .ToListAsync(ct);

            var failures = new List<AuditFailureDetail>();
            var keys = onHand.Select(o => (Plant: o.PlantId, Code: o.ProductCode, Loc: o.LocationCode, Lot: o.LotNo))
                             .Union(layers.Select(l => (Plant: l.PlantId, Code: l.ProductCode, Loc: l.LocationCode, Lot: l.LotNo)))
                             .ToList();

            foreach (var (pid, code, loc, lot) in keys)
            {
                var invQty   = onHand.FirstOrDefault(o => o.PlantId == pid && o.ProductCode == code
                                                       && o.LocationCode == loc && o.LotNo == lot)?.Qty ?? 0m;
                var layerRow = layers.FirstOrDefault(l => l.PlantId == pid && l.ProductCode == code
                                                       && l.LocationCode == loc && l.LotNo == lot);
                var layerQty = layerRow?.Qty ?? 0m;
                var diff     = invQty - layerQty;
                if (Math.Abs(diff) <= QtyTolerance) continue;

                // 差異金額取「缺／多的那些數量 × 現有層加權均價」——沒有層時只能報數量。
                var unit  = layerRow != null && layerQty > 0m ? layerRow.Value / layerQty : 0m;
                var where = string.IsNullOrEmpty(lot) ? loc : $"{loc}/{lot}";
                failures.Add(Msg(new AuditFailureDetail
                {
                    AuditType        = "FIFO_LAYER_QTY_MISMATCH",
                    DocId            = $"{code}@{where}",
                    ProductCode      = code,
                    PlantId          = pid,
                    DifferenceAmount = Math.Round(diff * unit, 4, MidpointRounding.AwayFromZero),
                }, AuditMessages.FifoLayerMismatch,
                   where, layerQty.ToString("F4"), invQty.ToString("F4"), diff.ToString("F4")));
            }
            return failures;
        }

        // ── 客供料落在非客供倉 ──────────────────────────────────────────

        /// <summary>
        /// 客供異動（`CI`／`CR`）落在不屬客供倉的儲位 → 客戶的料取得了成本。
        ///
        /// <para><b>為什麼隔離只能靠倉別</b>：移動平均的成本池是 **(PlantId, ProductCode)**,
        /// `UpdateMovingAverageCostAsync` 的分母取該品號全廠在手量、並把新均價寫回
        /// 該品號**所有** Inventory 列（見該方法註解「均價是全廠的，不是儲位的」）。
        /// 所以客供料必須從分母與寫回兩邊都被排除，而排除的依據就是
        /// <c>WarehouseType.CustomerSupplied</c>。沒隔離時：</para>
        /// <code>
        /// 自有 100 @10，客供 100 @0，再進自有 100 @20
        ///   正解 (100×10 + 100×20) / 200      = 15
        ///   實際 (200×10 + 100×20) / 300      = 13.33   ← 分母含了客戶的料
        /// </code>
        /// <para>而 13.33 是個看起來完全正常的單價，會讓之後每一張出貨的毛利莫名偏高。</para>
        ///
        /// <para><b>與過帳守門的分工</b>：`InventoryPostingService` 的 ③-b 擋的是**未來**
        /// （CI／CR 不在客供倉就不准過帳）；本稽核找的是**過去**——守門上線前
        /// 已經落在一般倉的遺留資料。處置是把那批庫存調撥到客供倉。</para>
        ///
        /// <para><b>2026-09-22 已掛進 <see cref="RunAllAsync"/></b>＝沒調倉就不准關帳。
        /// 業主確認現行資料沒有客供單（有也只是測試資料），所以不存在
        /// 「既有部署被擋在月結當天」的風險——那正是唯讀盤查先行
        /// （`AuditOrphanPostingAsync` 的前例）原本要規避的情況。</para>
        /// </summary>
        public async Task<List<AuditFailureDetail>> AuditCustomerSuppliedLocationAsync(
            string? plantId = null, CancellationToken ct = default)
        {
            await ResolveTolerancesAsync();

            var csLocations = await _db.StorageLocations
                .IgnoreQueryFilters().AsNoTracking()
                .Join(_db.Warehouses.IgnoreQueryFilters().AsNoTracking()
                        .Where(w => w.WarehouseType == Modules.BAS.Models.WarehouseType.CustomerSupplied),
                    l => l.WhCode, w => w.WhCode, (l, w) => l.LocationCode)
                .ToListAsync(ct);

            var offenders = await _db.Set<Modules.INV.Models.StockMovement>()
                .IgnoreQueryFilters().AsNoTracking()
                .Where(m => (m.TypeCode == "CI" || m.TypeCode == "CR")
                         && !csLocations.Contains(m.LocationCode)
                         && (plantId == null || m.PlantId == plantId))
                .Select(m => new { m.DocNo, m.PlantId, m.ProductCode, m.LocationCode, m.StockQty, m.Direction })
                .ToListAsync(ct);

            return offenders
                .Select(m => Msg(new AuditFailureDetail
                {
                    AuditType        = "CUSTOMER_SUPPLIED_WRONG_LOCATION",
                    DocId            = m.DocNo,
                    ProductCode      = m.ProductCode,
                    PlantId          = m.PlantId,
                    DifferenceAmount = m.StockQty * m.Direction,
                }, AuditMessages.CustomerSuppliedWrongLocation, m.DocNo, m.LocationCode))
                .ToList();
        }

        // ── §3.14 退貨已過帳但子帳未回沖 ────────────────────────────────

        public async Task<List<AuditFailureDetail>> AuditUnreversedReturnSubledgerAsync(
            string? plantId = null, CancellationToken ct = default)
        {
            await ResolveTolerancesAsync();
            var failures = new List<AuditFailureDetail>();

            // ── 銷售側：銷退 vs 出貨的 AR 發票 ──
            // 期望：AR 明細目前的 Qty ＝ 出貨量 − 已退回量。
            // 大於期望值＝有一段退回量沒有從應收扣掉。
            var soReturned = await _db.SalesReturnLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.GoodsShipmentId != null && l.ShipmentLineNo != null
                         && l.SalesReturn!.Status != DocStatus.Void
                         && l.SalesReturn.Status != DocStatus.Draft)
                .GroupBy(l => new { l.GoodsShipmentId, l.ShipmentLineNo })
                .Select(g => new { g.Key.GoodsShipmentId, g.Key.ShipmentLineNo, Qty = g.Sum(x => x.ReturnQty) })
                .ToListAsync(ct);

            if (soReturned.Count > 0)
            {
                var shipIds = soReturned.Select(r => r.GoodsShipmentId!).Distinct().ToList();
                var shipLines = await _db.GoodsShipmentLines.IgnoreQueryFilters().AsNoTracking()
                    .Where(l => shipIds.Contains(l.GoodsShipmentId))
                    .Select(l => new { l.GoodsShipmentId, l.LineNo, l.ShipQty, l.ProductCode })
                    .ToListAsync(ct);
                var arInvoices = await _db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                    .Include(i => i.Lines)
                    .Where(i => i.SourceDocType == "GoodsShipment" && shipIds.Contains(i.SourceDocId!)
                             && i.Status != Modules.FIN.Models.ARInvoiceStatus.Void
                             && (plantId == null || i.PlantId == plantId))
                    .ToListAsync(ct);

                // 已由折讓單處理掉的量（W3-F3 Phase 2）。**只認 Posted**——
                // 草稿還沒沖任何東西，這一筆必須繼續擋著關帳，否則自動產生草稿
                // 這件事本身就會把閘門關掉，而錢還掛在帳上。
                var arInvIds = arInvoices.Select(i => i.ARInvoiceId).ToList();
                var creditedQty = await _db.ARCreditNoteLines.IgnoreQueryFilters().AsNoTracking()
                    .Where(l => arInvIds.Contains(l.ARInvoiceId)
                             && l.ARCreditNote!.Status == Modules.FIN.Models.CreditNoteStatus.Posted)
                    .GroupBy(l => new { l.ARInvoiceId, l.InvoiceLineNo })
                    .Select(g => new { g.Key.ARInvoiceId, g.Key.InvoiceLineNo, Qty = g.Sum(x => x.Qty) })
                    .ToListAsync(ct);

                foreach (var inv in arInvoices)
                {
                    foreach (var arLine in inv.Lines)
                    {
                        if (arLine.SourceLineNo == null) continue;
                        var ship = shipLines.FirstOrDefault(
                            l => l.GoodsShipmentId == inv.SourceDocId && l.LineNo == arLine.SourceLineNo.Value);
                        if (ship == null) continue;
                        var returned = soReturned.FirstOrDefault(
                            r => r.GoodsShipmentId == inv.SourceDocId && r.ShipmentLineNo == arLine.SourceLineNo.Value)?.Qty ?? 0m;
                        if (returned <= 0m) continue;

                        var credited = creditedQty.FirstOrDefault(
                            c => c.ARInvoiceId == inv.ARInvoiceId && c.InvoiceLineNo == arLine.LineNo)?.Qty ?? 0m;
                        var expected = ship.ShipQty - returned;
                        var unreversedQty = arLine.Qty - credited - expected;
                        if (unreversedQty <= 0m) continue;

                        var gap = unreversedQty * arLine.UnitPrice * (1m + arLine.TaxRate) * inv.ExchangeRate;
                        if (Math.Abs(gap) <= AmountTolerance) continue;

                        failures.Add(Msg(new AuditFailureDetail
                        {
                            AuditType = "RETURN_SUBLEDGER_UNREVERSED",
                            DocId = inv.ARInvoiceId,
                            ProductCode = ship.ProductCode,
                            PlantId = inv.PlantId,
                            DifferenceAmount = gap,
                        }, AuditMessages.ArInvoiceUnreversed,
                           inv.SourceDocId, arLine.SourceLineNo, returned,
                           unreversedQty, gap.ToString("F2"), inv.Status));
                    }
                }
            }

            // ── 採購側：進貨退出 vs 進貨的 AP 發票（完全對稱）──
            var poReturned = await _db.GoodsReturnLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.GoodsReceiptId != null && l.ReceiptLineNo != null
                         && l.GoodsReturn!.Status != DocStatus.Void
                         && l.GoodsReturn.Status != DocStatus.Draft)
                .GroupBy(l => new { l.GoodsReceiptId, l.ReceiptLineNo })
                .Select(g => new { g.Key.GoodsReceiptId, g.Key.ReceiptLineNo, Qty = g.Sum(x => x.ReturnQty) })
                .ToListAsync(ct);

            if (poReturned.Count > 0)
            {
                var grIds = poReturned.Select(r => r.GoodsReceiptId!).Distinct().ToList();
                var grLines = await _db.GoodsReceiptLines.IgnoreQueryFilters().AsNoTracking()
                    .Where(l => grIds.Contains(l.GoodsReceiptId))
                    .Select(l => new { l.GoodsReceiptId, l.LineNo, l.ReceiptQty, l.ProductCode })
                    .ToListAsync(ct);
                var apInvoices = await _db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                    .Include(i => i.Lines)
                    .Where(i => i.SourceDocType == "GoodsReceipt" && grIds.Contains(i.SourceDocId!)
                             && i.Status != Modules.FIN.Models.APInvoiceStatus.Void
                             && (plantId == null || i.PlantId == plantId))
                    .ToListAsync(ct);

                // 折讓單處理掉的量（同 AR 側，只認 Posted）。
                var apInvIds = apInvoices.Select(i => i.APInvoiceId).ToList();
                var debitedQty = await _db.APDebitNoteLines.IgnoreQueryFilters().AsNoTracking()
                    .Where(l => apInvIds.Contains(l.APInvoiceId)
                             && l.APDebitNote!.Status == Modules.FIN.Models.CreditNoteStatus.Posted)
                    .GroupBy(l => new { l.APInvoiceId, l.InvoiceLineNo })
                    .Select(g => new { g.Key.APInvoiceId, g.Key.InvoiceLineNo, Qty = g.Sum(x => x.Qty) })
                    .ToListAsync(ct);

                foreach (var inv in apInvoices)
                {
                    foreach (var apLine in inv.Lines)
                    {
                        if (apLine.SourceLineNo == null) continue;
                        var gr = grLines.FirstOrDefault(
                            l => l.GoodsReceiptId == inv.SourceDocId && l.LineNo == apLine.SourceLineNo.Value);
                        if (gr == null) continue;
                        var returned = poReturned.FirstOrDefault(
                            r => r.GoodsReceiptId == inv.SourceDocId && r.ReceiptLineNo == apLine.SourceLineNo.Value)?.Qty ?? 0m;
                        if (returned <= 0m) continue;

                        // AP 認列是「按已收量分次認列」，故期望值以**已認列量**為上限：
                        // 認列 8 退 3 時期望 5，而不是拿進貨量 10 去減。
                        var debited = debitedQty.FirstOrDefault(
                            d => d.APInvoiceId == inv.APInvoiceId && d.InvoiceLineNo == apLine.LineNo)?.Qty ?? 0m;
                        var expected = Math.Max(0m, Math.Min(apLine.Qty + returned, gr.ReceiptQty) - returned);
                        var unreversedQty = apLine.Qty - debited - expected;
                        if (unreversedQty <= 0m) continue;

                        var gap = unreversedQty * apLine.UnitPrice * (1m + apLine.TaxRate) * inv.ExchangeRate;
                        if (Math.Abs(gap) <= AmountTolerance) continue;

                        failures.Add(Msg(new AuditFailureDetail
                        {
                            AuditType = "RETURN_SUBLEDGER_UNREVERSED",
                            DocId = inv.APInvoiceId,
                            ProductCode = gr.ProductCode,
                            PlantId = inv.PlantId,
                            DifferenceAmount = gap,
                        }, AuditMessages.ApInvoiceUnreversed,
                           inv.SourceDocId, apLine.SourceLineNo, returned,
                           unreversedQty, gap.ToString("F2"), inv.Status));
                    }
                }
            }

            return failures;
        }

        // ── §3.9 反正規化欄位漂移（P-6）──────────────────────────────────

        /// <summary>
        /// 採購明細「已收數量」應為多少：非作廢進貨單的 ReceiptQty 加總（鍵＝採購單號＋行號）。
        /// 回寫語意：ReceivedQty 於 GR 建立/修改時累加（含草稿）、刪除/作廢時回沖，
        /// 退貨不回沖（退貨記於 GoodsReceiptLine.ReturnedQty）。
        /// <para>稽核（PO_RECEIVED_DRIFT）與自動修正（DerivedFieldRepairService）共用這一份——兩邊各算一次就會漂。</para>
        /// </summary>
        public async Task<Dictionary<(string PurchaseOrderId, int LineNo), decimal>> ExpectedPoReceivedQtyAsync(
            string? plantId = null, CancellationToken ct = default)
        {
            var sums = await (
                from grl in _db.GoodsReceiptLines.IgnoreQueryFilters().AsNoTracking()
                join gr in _db.GoodsReceipts.IgnoreQueryFilters().AsNoTracking()
                    on grl.GoodsReceiptId equals gr.GoodsReceiptId
                where grl.PurchaseOrderId != null && grl.PoLineNo != null
                      && gr.Status != DocStatus.Void
                      && (plantId == null || gr.PlantId == plantId)
                group grl by new { grl.PurchaseOrderId, grl.PoLineNo } into g
                select new { g.Key.PurchaseOrderId, g.Key.PoLineNo, Qty = g.Sum(x => x.ReceiptQty) })
                .ToListAsync(ct);
            return sums.ToDictionary(s => (s.PurchaseOrderId!, s.PoLineNo!.Value), s => s.Qty);
        }

        /// <summary>
        /// 訂單明細「已出數量」應為多少：非作廢出貨單的 ShipQty 加總（鍵＝訂單號＋行號）。
        /// 回寫語意：ShippedQty 於 GS 儲存時累加（含草稿）、修改/刪除/作廢時回沖，
        /// 銷退不回沖（銷退記於 GoodsShipmentLine.ReturnedQty）。稽核與自動修正共用。
        /// </summary>
        public async Task<Dictionary<(string SalesOrderId, int LineNo), decimal>> ExpectedSoShippedQtyAsync(
            string? plantId = null, CancellationToken ct = default)
        {
            var sums = await (
                from gsl in _db.GoodsShipmentLines.IgnoreQueryFilters().AsNoTracking()
                join gs in _db.GoodsShipments.IgnoreQueryFilters().AsNoTracking()
                    on gsl.GoodsShipmentId equals gs.GoodsShipmentId
                where gsl.SalesOrderId != null && gsl.SoLineNo != null
                      && gs.Status != DocStatus.Void
                      && (plantId == null || gs.PlantId == plantId)
                group gsl by new { gsl.SalesOrderId, gsl.SoLineNo } into g
                select new { g.Key.SalesOrderId, g.Key.SoLineNo, Qty = g.Sum(x => x.ShipQty) })
                .ToListAsync(ct);
            return sums.ToDictionary(s => (s.SalesOrderId!, s.SoLineNo!.Value), s => s.Qty);
        }

        public async Task<List<AuditFailureDetail>> AuditPoReceivedQtyDriftAsync(string? plantId = null, CancellationToken ct = default)
        {
            await ResolveTolerancesAsync();
            var sumMap = await ExpectedPoReceivedQtyAsync(plantId, ct);

            var poLines = await (
                from pol in _db.PurchaseOrderLines.IgnoreQueryFilters().AsNoTracking()
                join po in _db.PurchaseOrders.IgnoreQueryFilters().AsNoTracking()
                    on pol.PurchaseOrderId equals po.PurchaseOrderId
                where plantId == null || po.PlantId == plantId
                select new { pol.PurchaseOrderId, pol.LineNo, pol.ProductCode, pol.ReceivedQty, po.PlantId })
                .ToListAsync(ct);

            var failures = new List<AuditFailureDetail>();
            var seen = new HashSet<(string, int)>();
            foreach (var pol in poLines)
            {
                var key = (pol.PurchaseOrderId, pol.LineNo);
                seen.Add(key);
                var exp = sumMap.GetValueOrDefault(key);
                var diff = pol.ReceivedQty - exp;
                if (Math.Abs(diff) > QtyTolerance)
                    failures.Add(Msg(new AuditFailureDetail
                    {
                        AuditType = "PO_RECEIVED_DRIFT",
                        DocId = $"{pol.PurchaseOrderId}#{pol.LineNo}",
                        ProductCode = pol.ProductCode,
                        PlantId = pol.PlantId,
                        DifferenceAmount = diff,
                    }, AuditMessages.PoReceivedDrift, pol.ReceivedQty, exp, diff));
            }
            // 有 GR 明細指向、但 PO 明細不存在（孤兒回寫目標）亦屬漂移
            foreach (var (key, qty) in sumMap)
                if (!seen.Contains(key) && Math.Abs(qty) > QtyTolerance)
                    failures.Add(Msg(new AuditFailureDetail
                    {
                        AuditType = "PO_RECEIVED_DRIFT",
                        DocId = $"{key.Item1}#{key.Item2}",
                        DifferenceAmount = -qty,
                    }, AuditMessages.PoReceivedOrphan, qty));
            return failures;
        }

        public async Task<List<AuditFailureDetail>> AuditSoShippedQtyDriftAsync(string? plantId = null, CancellationToken ct = default)
        {
            await ResolveTolerancesAsync();
            var sumMap = await ExpectedSoShippedQtyAsync(plantId, ct);

            var soLines = await (
                from sol in _db.SalesOrderLines.IgnoreQueryFilters().AsNoTracking()
                join so in _db.SalesOrders.IgnoreQueryFilters().AsNoTracking()
                    on sol.SalesOrderId equals so.SalesOrderId
                where plantId == null || so.PlantId == plantId
                select new { sol.SalesOrderId, sol.LineNo, sol.ProductCode, sol.ShippedQty, so.PlantId })
                .ToListAsync(ct);

            var failures = new List<AuditFailureDetail>();
            var seen = new HashSet<(string, int)>();
            foreach (var sol in soLines)
            {
                var key = (sol.SalesOrderId, sol.LineNo);
                seen.Add(key);
                var exp = sumMap.GetValueOrDefault(key);
                var diff = sol.ShippedQty - exp;
                if (Math.Abs(diff) > QtyTolerance)
                    failures.Add(Msg(new AuditFailureDetail
                    {
                        AuditType = "SO_SHIPPED_DRIFT",
                        DocId = $"{sol.SalesOrderId}#{sol.LineNo}",
                        ProductCode = sol.ProductCode,
                        PlantId = sol.PlantId,
                        DifferenceAmount = diff,
                    }, AuditMessages.SoShippedDrift, sol.ShippedQty, exp, diff));
            }
            foreach (var (key, qty) in sumMap)
                if (!seen.Contains(key) && Math.Abs(qty) > QtyTolerance)
                    failures.Add(Msg(new AuditFailureDetail
                    {
                        AuditType = "SO_SHIPPED_DRIFT",
                        DocId = $"{key.Item1}#{key.Item2}",
                        DifferenceAmount = -qty,
                    }, AuditMessages.SoShippedOrphan, qty));
            return failures;
        }

        // ── §3.10 WIP 期末快照一致性（P-1 結構性守門）────────────────────

        public async Task<List<AuditFailureDetail>> AuditWipSnapshotConsistencyAsync(
            string periodId, string? plantId = null, CancellationToken ct = default)
        {
            // 關帳前的 WIP 順序守門：
            // 1) 期間內完工成本必須已翻 Final（期末重估已跑）
            // 2) 在製工單必有本期 WipSnapshot（重估後才投產 → 需重跑重估）
            // 3) 快照不得指向已非在製的工單（重估後又完工/作廢 → 快照過期）
            // 4) 快照分量與在製量不得為負
            // 5) 金額恆等式（§9 P-1，D4 跨期結轉定案後啟用）：
            //    Σ期末快照 = Σ期初快照 + 本期發料淨值 + 本期部門人工/製費 − 本期完工成本
            //    僅於 1)–4) 全數通過時執行（結構性缺陷未清，恆等式必失衡、徒增噪音）。
            await ResolveTolerancesAsync();
            // 期間依廠別分開之後，這裡要找的是「被稽核的那個廠」的期間。
            // plantId 為 null 代表全公司稽核（RunAllAsync 的既有語意），此時任一廠的
            // 同一期起訖日都相同，取第一筆即可；IgnoreQueryFilters 是因為稽核不受
            // 操作者當下切在哪個廠影響。
            var period = await _db.AccountingPeriods.AsNoTracking().IgnoreQueryFilters()
                    .FirstOrDefaultAsync(p => p.PeriodId == periodId
                                           && (plantId == null || p.PlantId == plantId), ct)
                ?? throw new InvalidOperationException(
                       plantId == null ? PzText.Get("Act_PeriodNotFoundNoPlantFmt", periodId) : PzText.Get("Act_PeriodNotFoundFmt", plantId, periodId));

            var failures = new List<AuditFailureDetail>();

            var preliminary = await _db.WorkOrderActualCosts.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.Status == Modules.MFG.Models.CostStatus.Preliminary
                            && c.CostDate >= period.StartDate && c.CostDate <= period.EndDate
                            && (plantId == null || c.PlantId == plantId))
                .Select(c => new { c.WorkOrderId, c.PlantId, c.ActualMaterial, c.ActualLabor, c.ActualOverhead })
                .ToListAsync(ct);
            failures.AddRange(preliminary.Select(c => Msg(new AuditFailureDetail
            {
                AuditType = "WIP_COST_NOT_FINAL",
                DocId = c.WorkOrderId,
                PlantId = c.PlantId,
                DifferenceAmount = c.ActualMaterial + c.ActualLabor + c.ActualOverhead,
            }, AuditMessages.WipCostNotFinal, c.WorkOrderId)));

            var inProgress = await _db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                .Where(w => w.Status == Modules.MFG.Models.WorkOrderStatus.InProgress
                            && (plantId == null || w.PlantId == plantId))
                .Select(w => new { w.WorkOrderId, w.PlantId })
                .ToListAsync(ct);
            var snapshots = await _db.WipSnapshots.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.PeriodId == periodId && (plantId == null || s.PlantId == plantId))
                .ToListAsync(ct);
            var snapshotWoIds = snapshots.Select(s => s.WorkOrderId).ToHashSet();
            var inProgressIds = inProgress.Select(w => w.WorkOrderId).ToHashSet();

            failures.AddRange(inProgress
                .Where(w => !snapshotWoIds.Contains(w.WorkOrderId))
                .Select(w => Msg(new AuditFailureDetail
                {
                    AuditType = "WIP_SNAPSHOT_MISSING",
                    DocId = w.WorkOrderId,
                    PlantId = w.PlantId,
                }, AuditMessages.WipSnapshotMissing, w.WorkOrderId, periodId)));

            failures.AddRange(snapshots
                .Where(s => !inProgressIds.Contains(s.WorkOrderId))
                .Select(s => Msg(new AuditFailureDetail
                {
                    AuditType = "WIP_SNAPSHOT_STALE",
                    DocId = s.WorkOrderId,
                    PlantId = s.PlantId,
                    DifferenceAmount = s.WipTotal,
                }, AuditMessages.WipSnapshotStale, s.WorkOrderId)));

            failures.AddRange(snapshots
                .Where(s => s.WipMaterial < 0 || s.WipLabor < 0 || s.WipOverhead < 0 || s.QtyInProcess < 0)
                .Select(s => Msg(new AuditFailureDetail
                {
                    AuditType = "WIP_SNAPSHOT_NEGATIVE",
                    DocId = s.WorkOrderId,
                    PlantId = s.PlantId,
                    DifferenceAmount = s.WipTotal,
                }, AuditMessages.WipSnapshotNegative,
                   s.WipMaterial, s.WipLabor, s.WipOverhead, s.QtyInProcess)));

            // ── 5) 金額恆等式（WIP_IDENTITY_DRIFT，§5.8 公式的期間彙總級應用）────
            // 結構性守門有失敗即跳過：快照缺漏/過期/未 Final 時恆等式必失衡，先修結構再談金額。
            if (failures.Count > 0) return failures;

            failures.AddRange(await AuditWipIdentityAsync(period, plantId, ct));

            return failures;
        }

        /// <summary>
        /// §3.10-5 WIP 金額恆等式（reconciliation-decisions.md D4 定案後啟用）：
        /// 逐廠核 Σ期末快照 = Σ期初快照 + 本期發料淨值 + 本期部門人工/製費 − 本期完工成本。
        ///
        /// 各腿口徑（獨立來源，非由快照互相推導）：
        /// - 期初/期末＝前期/本期 WipSnapshot 彙總；期初排除本期已作廢工單（逐筆另報 WIP_VOID_LEAKAGE）。
        /// - 發料淨值＝工單後沖（DocNo=工單-BF）＋ Push/Pull 發退料單（MaterialSlip 二跳）對應
        ///   Production 異動，依方向淨計，限 MovementDate ∈ 期間（與 §3.11 材料腿同源、加期間切片）。
        /// - 人工/製費投入＝Σ DepartmentActualCost（月結輸入的重估分子）。失衡時優先檢查
        ///   「有費用但當月無工時的工作中心」（費用無處吸收）。
        /// - 完工成本＝WorkOrderActualCost（CostDate ∈ 期間，守門後應全 Final）三分量合計。
        /// - 報廢無獨立腿：成本唯一出口是完工（NG 成本經完工吸收進良品單位成本，無扣減流程）。
        /// </summary>
        private async Task<List<AuditFailureDetail>> AuditWipIdentityAsync(
            AccountingPeriod period, string? plantId, CancellationToken ct)
        {
            var failures = new List<AuditFailureDetail>();
            var priorPeriodId = period.Month == 1
                ? $"{period.Year - 1:D4}12"
                : $"{period.Year:D4}{period.Month - 1:D2}";

            var curSnaps = await _db.WipSnapshots.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.PeriodId == period.PeriodId && (plantId == null || s.PlantId == plantId))
                .Select(s => new { s.PlantId, s.WorkOrderId, Total = s.WipMaterial + s.WipLabor + s.WipOverhead })
                .ToListAsync(ct);
            var priorSnaps = await _db.WipSnapshots.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.PeriodId == priorPeriodId && (plantId == null || s.PlantId == plantId))
                .Select(s => new { s.PlantId, s.WorkOrderId, Total = s.WipMaterial + s.WipLabor + s.WipOverhead })
                .ToListAsync(ct);

            var completedCosts = await _db.WorkOrderActualCosts.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.CostDate >= period.StartDate && c.CostDate <= period.EndDate
                            && (plantId == null || c.PlantId == plantId))
                .Select(c => new { c.PlantId, c.WorkOrderId, Total = c.ActualMaterial + c.ActualLabor + c.ActualOverhead })
                .ToListAsync(ct);

            // 期初快照指向的工單本期作廢 → 成本無出口地離開恆等式，逐筆報洩漏並自期初調節
            var priorWoIds = priorSnaps.Select(s => s.WorkOrderId).Distinct().ToList();
            var voidedWoIds = (await _db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                .Where(w => priorWoIds.Contains(w.WorkOrderId)
                            && w.Status == Modules.MFG.Models.WorkOrderStatus.Void)
                .Select(w => w.WorkOrderId)
                .ToListAsync(ct)).ToHashSet();
            failures.AddRange(priorSnaps
                .Where(s => voidedWoIds.Contains(s.WorkOrderId))
                .Select(s => Msg(new AuditFailureDetail
                {
                    AuditType = "WIP_VOID_LEAKAGE",
                    DocId = s.WorkOrderId,
                    PlantId = s.PlantId,
                    DifferenceAmount = s.Total,
                }, AuditMessages.WipVoidLeakage, s.WorkOrderId, s.Total.ToString("N4"))));

            // 發料淨值：對象工單 = 期初 ∪ 期末快照 ∪ 本期完工（與 §3.11 材料腿同源，加期間切片）
            var woIds = priorWoIds
                .Concat(curSnaps.Select(s => s.WorkOrderId))
                .Concat(completedCosts.Select(c => c.WorkOrderId))
                .Distinct().ToList();
            var docToWo = new Dictionary<string, string>();
            foreach (var id in woIds) docToWo[id + "-BF"] = id;
            var slips = await _db.MaterialSlips.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.SourceDocType == "WorkOrder" && woIds.Contains(s.SourceDocId)
                            && s.Status == Modules.MFG.Models.MaterialSlipStatus.Posted)
                .Select(s => new { s.SlipId, s.SourceDocId })
                .ToListAsync(ct);
            foreach (var s in slips) docToWo[s.SlipId] = s.SourceDocId;

            var matDocNos = docToWo.Keys.ToList();
            var woPlant = curSnaps.Select(s => (s.WorkOrderId, s.PlantId))
                .Concat(priorSnaps.Select(s => (s.WorkOrderId, s.PlantId)))
                .Concat(completedCosts.Select(c => (c.WorkOrderId, c.PlantId)))
                .GroupBy(x => x.WorkOrderId)
                .ToDictionary(g => g.Key, g => g.First().PlantId);
            var issuedByPlant = (await _db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .Where(m => matDocNos.Contains(m.DocNo) && Modules.MFG.Services.WorkOrderMaterialMovement.Categories.Contains(m.TypeCategory)
                                && m.MovementDate >= period.StartDate && m.MovementDate <= period.EndDate)
                    .Select(m => new { m.DocNo, m.StockQty, m.Direction, m.UnitCost })
                    .ToListAsync(ct))
                .GroupBy(m => woPlant.GetValueOrDefault(docToWo[m.DocNo]))
                .ToDictionary(g => g.Key, g => g.Sum(m => -(m.StockQty * m.Direction) * (m.UnitCost ?? 0m)));

            var deptCostByPlant = (await _db.DepartmentActualCosts.IgnoreQueryFilters().AsNoTracking()
                    .Where(d => d.PeriodId == period.PeriodId && (plantId == null || d.PlantId == plantId))
                    .Select(d => new { d.PlantId, Total = d.ActualLaborCost + d.ActualOverheadCost })
                    .ToListAsync(ct))
                .GroupBy(d => d.PlantId)
                .ToDictionary(g => g.Key, g => g.Sum(d => d.Total));

            var plants = curSnaps.Select(s => s.PlantId)
                .Concat(priorSnaps.Select(s => s.PlantId))
                .Concat(completedCosts.Select(c => c.PlantId))
                .Concat(deptCostByPlant.Keys)
                .Distinct().ToList();

            foreach (var p in plants)
            {
                var beginning = priorSnaps.Where(s => s.PlantId == p && !voidedWoIds.Contains(s.WorkOrderId)).Sum(s => s.Total);
                var ending = curSnaps.Where(s => s.PlantId == p).Sum(s => s.Total);
                var issued = issuedByPlant.GetValueOrDefault(p);
                var laborOh = deptCostByPlant.GetValueOrDefault(p);
                var completed = completedCosts.Where(c => c.PlantId == p).Sum(c => c.Total);

                if (issued < 0)
                {
                    failures.Add(Msg(new AuditFailureDetail
                    {
                        AuditType = "WIP_IDENTITY_DRIFT",
                        DocId = period.PeriodId,
                        PlantId = p,
                        DifferenceAmount = issued,
                    }, AuditMessages.WipIssuedNegative, issued.ToString("N4")));
                    continue;
                }

                var expected = Modules.MFG.Services.WipBalanceCalculator.ComputeEndingWip(
                    beginning, issued, laborOh, completed);
                var drift = ending - expected;
                if (Math.Abs(drift) > AmountTolerance)
                {
                    failures.Add(Msg(new AuditFailureDetail
                    {
                        AuditType = "WIP_IDENTITY_DRIFT",
                        DocId = period.PeriodId,
                        PlantId = p,
                        DifferenceAmount = drift,
                    }, AuditMessages.WipIdentityDrift,
                       drift.ToString("N4"), ending.ToString("N4"), beginning.ToString("N4"),
                       issued.ToString("N4"), laborOh.ToString("N4"),
                       completed.ToString("N4"), expected.ToString("N4")));
                }
            }

            return failures;
        }

        // ── §3.5 ERP 拋轉滯留 ──────────────────────────────────────────

        public async Task<List<AuditFailureDetail>> AuditErpSyncLagAsync(string? plantId = null, CancellationToken ct = default)
        {
            var cutoff = DateTime.UtcNow.AddHours(-ErpSyncLagHours);
            var lagged = await _db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                .Where(ap => ap.Status != Modules.FIN.Models.APInvoiceStatus.Void
                             && (ap.ErpSyncStatus == ErpSyncStatus.Pending || ap.ErpSyncStatus == ErpSyncStatus.Failed)
                             && ap.CreatedAt < cutoff
                             && (plantId == null || ap.PlantId == plantId))
                .Select(ap => new { ap.APInvoiceId, ap.PlantId, ap.TotalAmount, ap.ErpSyncStatus, ap.ErpRetryCount, ap.ErpErrorMessage, ap.CreatedAt })
                .ToListAsync(ct);

            var failures = lagged.Select(ap => Msg(new AuditFailureDetail
            {
                AuditType = "ERP_SYNC_LAG",
                DocId = ap.APInvoiceId,
                PlantId = ap.PlantId,
                DifferenceAmount = ap.TotalAmount,
            },
            // 「、錯誤：」是句子的一部分，接字串會把它鎖在中文；有無錯誤訊息拆成兩個鍵。
            string.IsNullOrEmpty(ap.ErpErrorMessage)
                ? AuditMessages.ErpSyncLagAp
                : AuditMessages.ErpSyncLagApWithError,
            ap.CreatedAt.ToString("yyyy-MM-dd HH:mm"), ErpSyncLagHours,
            ap.ErpSyncStatus, ap.ErpRetryCount, ap.ErpErrorMessage)).ToList();

            // §9 P-3（reconciliation-decisions.md D3 最小範圍）：ARInvoice 對稱擴大
            var laggedAr = await _db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                .Where(ar => ar.Status != Modules.FIN.Models.ARInvoiceStatus.Void
                             && (ar.ErpSyncStatus == ErpSyncStatus.Pending || ar.ErpSyncStatus == ErpSyncStatus.Failed)
                             && ar.CreatedAt < cutoff
                             && (plantId == null || ar.PlantId == plantId))
                .Select(ar => new { ar.ARInvoiceId, ar.PlantId, ar.TotalAmount, ar.ErpSyncStatus, ar.ErpRetryCount, ar.ErpErrorMessage, ar.CreatedAt })
                .ToListAsync(ct);

            failures.AddRange(laggedAr.Select(ar => Msg(new AuditFailureDetail
            {
                AuditType = "ERP_SYNC_LAG",
                DocId = ar.ARInvoiceId,
                PlantId = ar.PlantId,
                DifferenceAmount = ar.TotalAmount,
            },
            string.IsNullOrEmpty(ar.ErpErrorMessage)
                ? AuditMessages.ErpSyncLagAr
                : AuditMessages.ErpSyncLagArWithError,
            ar.CreatedAt.ToString("yyyy-MM-dd HH:mm"), ErpSyncLagHours,
            ar.ErpSyncStatus, ar.ErpRetryCount, ar.ErpErrorMessage)));

            return failures;
        }

        // ── §3.11 逐工單成本回溯核對（P-2 選項 B：Preliminary 口徑，reconciliation-decisions.md D2）──

        public async Task<List<AuditFailureDetail>> AuditWoCostJournalDriftAsync(string? plantId = null, CancellationToken ct = default)
        {
            await ResolveTolerancesAsync();
            var failures = new List<AuditFailureDetail>();

            var costs = await _db.WorkOrderActualCosts.IgnoreQueryFilters().AsNoTracking()
                .Where(c => plantId == null || c.PlantId == plantId)
                .Select(c => new { c.WorkOrderId, c.ActualMaterial, c.ActualLabor, c.ActualOverhead, c.Status, c.PlantId })
                .ToListAsync(ct);
            if (costs.Count == 0) return failures;

            var woIds = costs.Select(c => c.WorkOrderId).ToList();

            var products = await _db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                .Where(w => woIds.Contains(w.WorkOrderId))
                .ToDictionaryAsync(w => w.WorkOrderId, w => w.ProductCode, ct);

            // 材料腿：ActualMaterial vs 本工單耗用材料的 StockMovement 淨成本。
            // 生產物料異動（後沖 IO、Push/Pull 發退料）走 PostDocSimplifiedAsync，不落日記帳，
            // 故對「庫存異動子帳」核對而非日記帳；來源＝後沖 DocNo=工單-BF ＋ Push/Pull 發退料單
            // （MaterialSlip.SourceDocId=工單）對應的 Production movement，依方向淨計（退料抵減）。
            var docToWo = new Dictionary<string, string>();
            foreach (var id in woIds) docToWo[id + "-BF"] = id;
            var slips = await _db.MaterialSlips.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.SourceDocType == "WorkOrder" && woIds.Contains(s.SourceDocId)
                         && s.Status == Modules.MFG.Models.MaterialSlipStatus.Posted)
                .Select(s => new { s.SlipId, s.SourceDocId })
                .ToListAsync(ct);
            foreach (var s in slips) docToWo[s.SlipId] = s.SourceDocId;

            var matDocNos = docToWo.Keys.ToList();
            var materialByWo = (await _db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .Where(m => matDocNos.Contains(m.DocNo) && Modules.MFG.Services.WorkOrderMaterialMovement.Categories.Contains(m.TypeCategory))
                    .Select(m => new { m.DocNo, m.StockQty, m.Direction, m.UnitCost })
                    .ToListAsync(ct))
                .GroupBy(m => docToWo[m.DocNo])
                .ToDictionary(g => g.Key, g => g.Sum(m => -(m.StockQty * m.Direction) * (m.UnitCost ?? 0m)));

            // 人工/製費腿：吸收科目（5110 人工-已分配 / 5120 製費-已分配）淨額（貸−借）
            // vs ActualLabor+ActualOverhead，Preliminary/Final 皆核（D2-A）。歸集兩條來源：
            //   (a) 完工吸收分錄 JE("WorkOrderCompletion", 工單號) 的吸收科目行；
            //   (b) 期末重估調整分錄逐工單拆行（JournalLine.SourceRef=工單號）——Final 記錄的
            //       ActualLabor/Overhead 已含重估差額，須連同調整行才對得起來。
            var absorbCodes = new[] { StandardAccounts.DirectLaborAbsorbedCode, StandardAccounts.MfgOverheadAbsorbedCode };

            var completionAbsorb = (await _db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
                .Where(j => j.SourceDocType == "WorkOrderCompletion" && j.SourceDocId != null && woIds.Contains(j.SourceDocId))
                .SelectMany(j => j.Lines
                    .Where(l => absorbCodes.Contains(l.AccountCode))
                    .Select(l => new { Wo = j.SourceDocId!, Net = l.Credit - l.Debit }))
                .ToListAsync(ct))
                .GroupBy(x => x.Wo).ToDictionary(g => g.Key, g => g.Sum(x => x.Net));

            var reestAbsorb = (await _db.JournalLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.SourceRef != null && woIds.Contains(l.SourceRef) && absorbCodes.Contains(l.AccountCode))
                .Select(l => new { Wo = l.SourceRef!, Net = l.Credit - l.Debit })
                .ToListAsync(ct))
                .GroupBy(x => x.Wo).ToDictionary(g => g.Key, g => g.Sum(x => x.Net));

            foreach (var c in costs)
            {
                var productCode = products.GetValueOrDefault(c.WorkOrderId);

                var subledgerMaterial = materialByWo.GetValueOrDefault(c.WorkOrderId);
                if (Math.Abs(subledgerMaterial - c.ActualMaterial) > AmountTolerance)
                {
                    failures.Add(Msg(new AuditFailureDetail
                    {
                        AuditType = "WO_COST_JOURNAL_DRIFT",
                        DocId = c.WorkOrderId,
                        ProductCode = productCode,
                        PlantId = c.PlantId,
                        DifferenceAmount = subledgerMaterial - c.ActualMaterial,
                    }, AuditMessages.WoCostMaterialLeg,
                       c.ActualMaterial.ToString("F4"), subledgerMaterial.ToString("F4"),
                       (subledgerMaterial - c.ActualMaterial).ToString("F4")));
                }

                var conversion = c.ActualLabor + c.ActualOverhead;
                var jeConversion = completionAbsorb.GetValueOrDefault(c.WorkOrderId)
                                 + reestAbsorb.GetValueOrDefault(c.WorkOrderId);
                if (Math.Abs(jeConversion - conversion) > AmountTolerance)
                {
                    failures.Add(Msg(new AuditFailureDetail
                    {
                        AuditType = "WO_COST_JOURNAL_DRIFT",
                        DocId = c.WorkOrderId,
                        ProductCode = productCode,
                        PlantId = c.PlantId,
                        DifferenceAmount = jeConversion - conversion,
                    }, AuditMessages.WoCostConversionLeg,
                       conversion.ToString("F4"), jeConversion.ToString("F4"),
                       (jeConversion - conversion).ToString("F4")));
                }
            }

            return failures;
        }

        // ── 孤兒過帳（弱關聯最後防線）────────────────────────────────────

        /// <summary>
        /// 後沖領料的異動單號是「工單號 + -BF」（ProductionCompletionService），
        /// 比對前必須剝除，否則全部後沖異動都會被誤報為孤兒。
        /// </summary>
        internal const string BackflushDocSuffix = "-BF";

        /// <summary>
        /// 傳票來源型別 → 該型別單號集合的對照鍵。
        ///
        /// 只列**能一對一反查到單一資料表**的型別：Manual 無來源單；INV 是庫存單據統稱，
        /// SourceDocId 可能來自任一庫存單據表；ActualRateReest 的來源是期間而非單據；
        /// APInvoiceReturn 無獨立單據表。這些一律略過不判——寧可漏報也不誤報，
        /// 因為本稽核一旦掛進 RunAllAsync 就會擋關帳。
        /// </summary>
        private static readonly Dictionary<string, string> JournalSourceToDocSet = new()
        {
            ["GoodsReceipt"]         = nameof(ApplicationDbContext.GoodsReceipts),
            ["GoodsShipment"]        = nameof(ApplicationDbContext.GoodsShipments),
            ["OutsourceReceipt"]     = nameof(ApplicationDbContext.OutsourceReceipts),
            ["WorkOrder"]            = nameof(ApplicationDbContext.WorkOrders),
            ["WorkOrderCompletion"]  = nameof(ApplicationDbContext.WorkOrders),
            ["APInvoice"]            = nameof(ApplicationDbContext.APInvoices),
            ["APPayment"]            = nameof(ApplicationDbContext.APPayments),
            ["ARInvoice"]            = nameof(ApplicationDbContext.ARInvoices),
            ["ARReceipt"]            = nameof(ApplicationDbContext.ARReceipts),
            ["LandedCostAllocation"] = nameof(ApplicationDbContext.LandedCostAllocations),
            [Modules.MFG.Services.MaterialSlipService.ClosedWorkOrderReturnDocType] = nameof(ApplicationDbContext.MaterialSlips),
        };

        public async Task<List<AuditFailureDetail>> AuditOrphanPostingAsync(string? plantId = null, CancellationToken ct = default)
        {
            var failures = new List<AuditFailureDetail>();
            var index = await LoadDocIdIndexAsync(ct);

            // ① StockMovement：DocNo 不屬於任何單據表即為孤兒。
            //    以 (DocNo, PlantId, TypeCode) 聚合，一張單只報一筆而非逐行報。
            var movements = await _db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                .Where(m => plantId == null || m.PlantId == plantId)
                .GroupBy(m => new { m.DocNo, m.PlantId, m.TypeCode })
                .Select(g => new
                {
                    g.Key.DocNo,
                    g.Key.PlantId,
                    g.Key.TypeCode,
                    LineCount = g.Count(),
                    NetQty = g.Sum(x => x.StockQty * x.Direction)
                })
                .ToListAsync(ct);

            foreach (var m in movements)
            {
                if (index.AllDocIds.Contains(NormalizeMovementDocNo(m.DocNo))) continue;
                failures.Add(Msg(new AuditFailureDetail
                {
                    AuditType = "ORPHAN_STOCK_MOVEMENT",
                    DocId = m.DocNo,
                    PlantId = m.PlantId,
                    DifferenceAmount = m.NetQty,
                }, AuditMessages.OrphanStockMovement,
                   m.DocNo, m.TypeCode, m.LineCount, m.NetQty.ToString("F4")));
            }

            // ② JournalEntry：僅檢查能一對一反查的來源型別。
            var entries = await _db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.SourceDocId != null && e.SourceDocId != ""
                            && (plantId == null || e.PlantId == plantId))
                .Select(e => new { e.JournalId, e.SourceDocType, e.SourceDocId, e.PlantId })
                .ToListAsync(ct);

            foreach (var e in entries)
            {
                if (!JournalSourceToDocSet.TryGetValue(e.SourceDocType, out var setKey)) continue;
                if (index.ByDocSet[setKey].Contains(e.SourceDocId!)) continue;
                failures.Add(Msg(new AuditFailureDetail
                {
                    AuditType = "ORPHAN_JOURNAL_ENTRY",
                    DocId = e.JournalId,
                    PlantId = e.PlantId,
                    DifferenceAmount = 0m,
                }, AuditMessages.OrphanJournalEntry,
                   e.JournalId, e.SourceDocType, e.SourceDocId));
            }

            // ③ AP/AR 發票：來源單據（進貨單／委外收貨／出貨單）已不存在。
            //    發票本身無 Delete action，孤兒只會來自來源端被刪除。
            foreach (var f in await AuditOrphanInvoicesAsync(index, plantId, ct))
                failures.Add(f);

            return failures;
        }

        /// <summary>AP/AR 發票的來源單據存在性（②的姊妹檢查，拆出以免主方法過長）。</summary>
        private async Task<List<AuditFailureDetail>> AuditOrphanInvoicesAsync(
            DocIdIndex index, string? plantId, CancellationToken ct)
        {
            var failures = new List<AuditFailureDetail>();

            // 已作廢的不算孤兒：本稽核的訊息就是叫人「一併作廢」，
            // 作廢完卻還在名單上的話，這條稽核就會被當成永遠清不掉的噪音而被忽略。
            var apInvoices = await _db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.SourceDocId != null && i.SourceDocId != ""
                            && i.Status != Modules.FIN.Models.APInvoiceStatus.Void
                            && (plantId == null || i.PlantId == plantId))
                .Select(i => new { i.APInvoiceId, i.SourceDocType, i.SourceDocId, i.PlantId })
                .ToListAsync(ct);

            var arInvoices = await _db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.SourceDocId != null && i.SourceDocId != ""
                            && i.Status != Modules.FIN.Models.ARInvoiceStatus.Void
                            && (plantId == null || i.PlantId == plantId))
                .Select(i => new { i.ARInvoiceId, i.SourceDocType, i.SourceDocId, i.PlantId })
                .ToListAsync(ct);

            foreach (var (invoiceId, docType, docId, plant, messageKey) in
                     apInvoices.Select(i => (i.APInvoiceId, i.SourceDocType, i.SourceDocId, i.PlantId, AuditMessages.OrphanInvoiceAp))
                     .Concat(arInvoices.Select(i => (i.ARInvoiceId, i.SourceDocType, i.SourceDocId, i.PlantId, AuditMessages.OrphanInvoiceAr))))
            {
                if (docType == null || !JournalSourceToDocSet.TryGetValue(docType, out var setKey)) continue;
                if (index.ByDocSet[setKey].Contains(docId!)) continue;
                failures.Add(Msg(new AuditFailureDetail
                {
                    AuditType = "ORPHAN_INVOICE",
                    DocId = invoiceId,
                    PlantId = plant,
                    DifferenceAmount = 0m,
                }, messageKey, invoiceId, docType, docId));
            }

            return failures;
        }

        /// <summary>剝除庫存異動單號的已知後綴，還原成單據主鍵。</summary>
        private static string NormalizeMovementDocNo(string docNo)
            => docNo.EndsWith(BackflushDocSuffix, StringComparison.Ordinal)
                ? docNo[..^BackflushDocSuffix.Length]
                : docNo;

        /// <summary>各單據表的單號集合；AllDocIds 為聯集，ByDocSet 供來源型別精確反查。</summary>
        private sealed class DocIdIndex
        {
            public HashSet<string> AllDocIds { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, HashSet<string>> ByDocSet { get; } = new(StringComparer.Ordinal);

            public void Add(string docSetName, IEnumerable<string> ids)
            {
                var set = new HashSet<string>(ids, StringComparer.Ordinal);
                ByDocSet[docSetName] = set;
                AllDocIds.UnionWith(set);
            }
        }

        /// <summary>
        /// 載入所有可能成為過帳來源的單號。
        ///
        /// 刻意**不做廠別過濾**：跨廠調撥的異動落在一個廠、單據主檔可能歸屬另一個廠，
        /// 過濾會把正常資料誤報成孤兒。存在性判定本來就與廠別無關。
        /// </summary>
        private async Task<DocIdIndex> LoadDocIdIndexAsync(CancellationToken ct)
        {
            var index = new DocIdIndex();

            index.Add(nameof(ApplicationDbContext.ScrapDocs),
                await _db.ScrapDocs.IgnoreQueryFilters().AsNoTracking().Select(d => d.ScrapDocId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.LoanDocs),
                await _db.LoanDocs.IgnoreQueryFilters().AsNoTracking().Select(d => d.LoanDocId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.SampleDocs),
                await _db.SampleDocs.IgnoreQueryFilters().AsNoTracking().Select(d => d.SampleDocId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.OutsourceMaterialDocs),
                await _db.OutsourceMaterialDocs.IgnoreQueryFilters().AsNoTracking().Select(d => d.OutsourceMaterialDocId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.ReworkMaterialDocs),
                await _db.ReworkMaterialDocs.IgnoreQueryFilters().AsNoTracking().Select(d => d.ReworkMaterialDocId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.StockAdjustments),
                await _db.StockAdjustments.IgnoreQueryFilters().AsNoTracking().Select(d => d.StockAdjustmentId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.StockTransfers),
                await _db.StockTransfers.IgnoreQueryFilters().AsNoTracking().Select(d => d.StockTransferId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.CrossPlantTransfers),
                await _db.CrossPlantTransfers.IgnoreQueryFilters().AsNoTracking().Select(d => d.CrossPlantTransferId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.MaterialStagingOrders),
                await _db.MaterialStagingOrders.IgnoreQueryFilters().AsNoTracking().Select(d => d.StagingId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.GoodsReceipts),
                await _db.GoodsReceipts.IgnoreQueryFilters().AsNoTracking().Select(d => d.GoodsReceiptId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.GoodsReturns),
                await _db.GoodsReturns.IgnoreQueryFilters().AsNoTracking().Select(d => d.GoodsReturnId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.OutsourceReceipts),
                await _db.OutsourceReceipts.IgnoreQueryFilters().AsNoTracking().Select(d => d.OutsourceReceiptId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.GoodsShipments),
                await _db.GoodsShipments.IgnoreQueryFilters().AsNoTracking().Select(d => d.GoodsShipmentId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.SalesReturns),
                await _db.SalesReturns.IgnoreQueryFilters().AsNoTracking().Select(d => d.SalesReturnId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.InternalOrders),
                await _db.InternalOrders.IgnoreQueryFilters().AsNoTracking().Select(d => d.InternalOrderId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.MaterialSlips),
                await _db.MaterialSlips.IgnoreQueryFilters().AsNoTracking().Select(d => d.SlipId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.WorkOrders),
                await _db.WorkOrders.IgnoreQueryFilters().AsNoTracking().Select(d => d.WorkOrderId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.LandedCostAllocations),
                await _db.LandedCostAllocations.IgnoreQueryFilters().AsNoTracking().Select(d => d.LandedCostAllocationId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.APInvoices),
                await _db.APInvoices.IgnoreQueryFilters().AsNoTracking().Select(d => d.APInvoiceId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.APPayments),
                await _db.APPayments.IgnoreQueryFilters().AsNoTracking().Select(d => d.APPaymentId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.ARInvoices),
                await _db.ARInvoices.IgnoreQueryFilters().AsNoTracking().Select(d => d.ARInvoiceId).ToListAsync(ct));
            index.Add(nameof(ApplicationDbContext.ARReceipts),
                await _db.ARReceipts.IgnoreQueryFilters().AsNoTracking().Select(d => d.ARReceiptId).ToListAsync(ct));

            return index;
        }
    }
}
