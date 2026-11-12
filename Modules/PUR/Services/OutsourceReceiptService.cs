using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.PUR.Services
{
    /// <summary>委外採購單一行的可收量（訂購量－已確認／過帳的收貨量；草稿中的另列）。</summary>
    public record OpoReceivableLine(int LineNo, string ProductCode, string? UomCode,
        decimal OrderedQty, decimal ReceivedQty, decimal DraftQty, decimal RemainingQty);

    /// <summary>手機收料的一行：對應委外採購單行，實收量與（簡化模式必填的）入庫位置。</summary>
    /// <param name="FailQty">委外收料當場驗收時的不良數（mfg_outsource_inspect_at_receipt）；其他情況不用。</param>
    public record MobileReceiveLine(int OpoLineNo, decimal Qty, string? LotNo, string? WhCode, string? LocationCode, decimal? FailQty = null);

    /// <summary>送出後的結果：已過帳／已確認待上架／送簽核中。</summary>
    public enum ReceiptOutcome { Posted, ConfirmedAwaitingPutAway, PendingApproval }

    /// <summary>
    /// 委外進貨（收料）的送審與確認——桌機 <c>OutsourceReceiptController</c> 與手機共用的單一實作。
    ///
    /// <para>確認收貨一次做完廢品罰扣、庫存過帳（簡化模式）、在途→在手、自動產生應付發票，
    /// 原本整段寫在 Controller 裡。手機若另寫一份，任何一段漂掉都是帳務不一致而且沒有訊號，
    /// 所以搬出來共用；Controller 只剩權限與回應格式。業務錯誤一律擲 <see cref="InvalidOperationException"/>。</para>
    /// </summary>
    public interface IOutsourceReceiptService
    {
        Task NormalizeLinesAsync(string? opoId, IEnumerable<OutsourceReceiptLine> lines);
        Task<decimal> ApplyLocalAmountsAsync(IEnumerable<OutsourceReceiptLine> lines, decimal headerRate, string? headerCurrency);

        /// <summary>送審：未設簽核流程時直接核准（回 true）；否則送出簽核（回 false）。</summary>
        Task<bool> SubmitAsync(string receiptId);

        /// <summary>確認收貨（需已核准）。簡化模式直接過帳，否則轉已確認待倉庫上架。</summary>
        Task<ReceiptOutcome> ConfirmAsync(string receiptId);

        Task<List<OpoReceivableLine>> GetReceivableLinesAsync(string opoId);

        /// <summary>
        /// 手機收料：依委外採購單建收貨單 → 送審 → 已核准就直接確認。單價／幣別／匯率一律取自委外採購單
        /// 與匯率主檔，手機不輸入金額。整段同一交易：任何一步失敗就整張不存在。
        /// </summary>
        Task<(string ReceiptId, ReceiptOutcome Outcome)> ReceiveFromMobileAsync(
            string opoId, DateOnly receiptDate, bool isFinalReceipt, IReadOnlyList<MobileReceiveLine> lines, string? remarks);

        Task<bool> RequiresLocationAtConfirmAsync();
        Task<bool> InspectAtReceiptEnabledAsync();

        /// <summary>這張委外採購單是否為工序委外（來源工單本身不是委外工單；收回不入庫，見 §19.7 C）。</summary>
        Task<bool> IsOperationOutsourceAsync(string opoId);
    }

    public class OutsourceReceiptService : IOutsourceReceiptService
    {
        private const string DocType = "OutsourceReceipt";

        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IApprovalService _approvalService;
        private readonly IDocNumberService _docNumberService;
        private readonly IInventoryPostingService _postingService;
        private readonly IPlantParamService _plantParams;
        private readonly IUomService _uomService;
        private readonly ICurrencyService _currencyService;
        private readonly IAPInvoiceService _apInvoiceService;
        private readonly IInventoryReservationService _reservations;
        private readonly IOutsourceReceiptSettlementService _settlementService;
        private readonly Modules.ACT.Services.IJournalEntryService _journalService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly SmartFactory.MES.Modules.MFG.Services.IOverQuantityPolicy _overQty;
        private readonly SmartFactory.MES.Modules.MFG.Services.IOutsourceProgressService _progress;
        private readonly SmartFactory.MES.Modules.MFG.Services.IMaterialSlipService? _slips;
        private readonly SmartFactory.MES.Modules.BAS.Services.IPlantDefaultLocationResolver? _locations;
        private readonly SmartFactory.MES.Modules.QCR.Services.IIqcInspectionService? _iqc;

        public OutsourceReceiptService(
            ApplicationDbContext context, IUserContext userContext,
            IApprovalService approvalService, IDocNumberService docNumberService,
            IInventoryPostingService postingService, IPlantParamService plantParams,
            IUomService uomService, ICurrencyService currencyService,
            IAPInvoiceService apInvoiceService, IInventoryReservationService reservations,
            IOutsourceReceiptSettlementService settlementService,
            Modules.ACT.Services.IJournalEntryService journalService,
            IStringLocalizer<ProjectZSharedResource> localizer,
            SmartFactory.MES.Modules.MFG.Services.IOverQuantityPolicy? overQty = null,
            SmartFactory.MES.Modules.MFG.Services.IOutsourceProgressService? progress = null,
            SmartFactory.MES.Modules.MFG.Services.IMaterialSlipService? slips = null,
            SmartFactory.MES.Modules.BAS.Services.IPlantDefaultLocationResolver? locations = null,
            SmartFactory.MES.Modules.QCR.Services.IIqcInspectionService? iqc = null)
        {
            _slips = slips; _locations = locations; _iqc = iqc;
            _progress = progress ?? new SmartFactory.MES.Modules.MFG.Services.OutsourceProgressService(context);
            // 選配：既有呼叫端（含測試）直接 new 時不必改；沒給就用同一套規則自己組一份
            _overQty = overQty ?? new SmartFactory.MES.Modules.MFG.Services.OverQuantityPolicy(
                plantParams, new SmartFactory.MES.Services.MobilePermissionService(context));
            _context = context; _userContext = userContext;
            _approvalService = approvalService; _docNumberService = docNumberService;
            _postingService = postingService; _plantParams = plantParams;
            _uomService = uomService; _currencyService = currencyService;
            _apInvoiceService = apInvoiceService; _reservations = reservations;
            _settlementService = settlementService; _journalService = journalService;
            _localizer = localizer;
        }

        public async Task<bool> RequiresLocationAtConfirmAsync() => string.Equals(
            await _plantParams.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, _userContext.PlantId),
            "true", StringComparison.OrdinalIgnoreCase);

        public async Task<bool> SubmitAsync(string receiptId)
        {
            var rec = await _context.OutsourceReceipts.Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.OutsourceReceiptId == receiptId) ?? throw Fail("RecordNotFound");
            if (rec.Status != DocStatus.Draft) throw Fail("DraftOnlyCanSubmit");
            if (!rec.Lines.Any()) throw Fail("AddLineFirst");

            var config = await _approvalService.GetApprovalConfig(DocType, _userContext.PlantId);
            // 未設簽核流程（乾淨部署的預設狀態，DocumentApprovalConfigs 沒有本類單據）時
            // 直接核准——與 ModuleControllerBase.SubmitForApprovalAsync 同一慣例。
            // 少了這道判斷，config 為 null 會在下一行擲 NullReferenceException（HTTP 500），
            // 而確認收貨的前提是 ApprovalStatus=Approved，整條委外流程就此走不下去。
            if (config?.ApprovalTemplateId == null)
            {
                rec.ApprovalStatus = DocumentApprovalStatus.Approved;
                rec.UpdatedAt      = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return true;
            }

            var request = await _approvalService.SubmitForApproval(receiptId, DocType, config.ApprovalTemplateId, _userContext.UserId ?? "");
            rec.ApprovalStatus    = DocumentApprovalStatus.PendingApproval;
            rec.ApprovalRequestId = request.Id;
            rec.UpdatedAt         = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return false;
        }

        public async Task<ReceiptOutcome> ConfirmAsync(string receiptId)
        {
            await using var tx = await _context.Database.BeginTransactionAsync();
            var outcome = await ConfirmCoreAsync(receiptId);
            await tx.CommitAsync();
            return outcome;
        }

        /// <summary>確認本體（呼叫端負責交易）。廢品罰扣、庫存過帳、在途→在手、應付發票為單一原子作業。</summary>
        /// <param name="atReceiptFail">
        /// 委外收料當場驗收（mfg_outsource_inspect_at_receipt，只有手機收料帶得進來）：收貨明細行號 → 不良數。
        /// 有值時不論簡化與否都在確認當下過帳：合格（不良 0）與免驗品入一般倉並回寫完工量，不合格整批入待驗倉待處置。
        /// null＝照原流程（簡化模式直接入庫；否則確認後上架待驗倉＋品檢）。
        /// </param>
        private async Task<ReceiptOutcome> ConfirmCoreAsync(string receiptId, IReadOnlyDictionary<int, decimal>? atReceiptFail = null)
        {
            var rec = await _context.OutsourceReceipts.Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.OutsourceReceiptId == receiptId) ?? throw Fail("RecordNotFound");
            if (rec.Status != DocStatus.Draft) throw Fail("StagingErrDraftToConfirm");
            if (rec.ApprovalStatus != DocumentApprovalStatus.Approved) throw Fail("StagingErrNeedApproval");
            if (!rec.Lines.Any()) throw Fail("NoLinesCannotConfirm");

            // 來源工單已作廢：收貨會把完工量、成本與應付掛到一張不再結算的工單上
            var opoLineNos = rec.Lines.Where(l => l.OpoLineNo != null).Select(l => l.OpoLineNo!.Value).ToList();
            var voidedWo = await _context.OutsourcePurchaseOrderLines.AsNoTracking()
                .Where(l => l.OutsourcePurchaseOrderId == rec.OutsourcePurchaseOrderId
                         && (opoLineNos.Count == 0 || opoLineNos.Contains(l.LineNo)))
                .Select(l => l.SourceWorkOrderId)
                .Where(woId => _context.WorkOrders.Any(w => w.WorkOrderId == woId && w.Status == WorkOrderStatus.Void))
                .FirstOrDefaultAsync();
            if (voidedWo != null)
                throw new InvalidOperationException(string.Format(_localizer["OsrSourceWorkOrderVoided"].Value, voidedWo));

            // 超收（mfg-domain.md §4.6）：確認是收貨真正入帳的那一刻，桌機確認、手機收料、簽核後確認都經過這裡。
            await ApplyOverReceiptRulesAsync(rec);

            var simplified = await RequiresLocationAtConfirmAsync();

            // 工序委外（§19.7 C）：收回的是在製品，回到廠內下一道工序，不入成品庫。整張必須同一種，混收擲錯。
            var opLines = await OperationOutsourceLineNosAsync(rec);
            if (opLines.Count > 0 && opLines.Count < rec.Lines.Count)
                throw new InvalidOperationException(PzText.Get("Osr_MixedOperationOutsource"));
            var isOperation = opLines.Count > 0;

            // 委外簡化：送外料收貨時依 BOM 自動扣（廠別參數）。必須在結算之前——結算讀的是已過帳的委外發料。
            // 工序委外一律扣該工序的投料（不看參數）：成品不入庫，料若不在這裡扣就再也沒有扣的時點；
            // 已用發料單發過的（含廠內領料）以 IssuedQty−ReturnedQty 抵掉，不會重扣。
            if (isOperation || await PlantFlagAsync(ProjectZSystemParamKeys.MfgOutsourceIssueOnReceipt, rec.PlantId))
                await AutoIssueSendMaterialsAsync(rec);

            // 在外原料歸屬與超耗扣款是同一筆錢的兩個去處，一次算完並就地寫回明細
            // （見 IOutsourceReceiptSettlementService）。
            var settlement = await _settlementService.SettleAsync(rec);

            // 當場驗收：先登錄品檢（與 IQC 同一套抽樣、判定、VL 切換），決定每行進一般倉還是待驗倉
            var goodLines = rec.Lines.Select(l => l.LineNo).ToHashSet();
            if (atReceiptFail != null && !isOperation)
                goodLines = await InspectAtReceiptAsync(rec, atReceiptFail);

            ReceiptOutcome outcome;
            if (isOperation)
            {
                await PostOperationOutsourceAsync(rec, settlement, atReceiptFail);
                outcome = ReceiptOutcome.Posted;
            }
            else if (simplified || atReceiptFail != null)
            {
                // 成品入庫價值 = 加工費（本次應付）+ 在外原料（早已在存貨科目裡，只換形態）。
                // 少了後者，成品單位成本就只剩加工費，庫存明細帳與總帳存貨科目
                // 永久相差被加工掉的那批料。
                var lines = rec.Lines.Select(l => new SimplifiedPostLine
                {
                    LineNo        = l.LineNo,
                    ProductCode   = l.ProductCode,
                    WhCode        = l.WhCode ?? "",
                    LocationCode  = l.LocationCode ?? "",
                    LotNo         = l.LotNo ?? "",
                    Qty           = l.StockQty,
                    UomCode       = l.StockUomCode ?? l.UomCode ?? "",
                    UnitPrice     = l.UnitCost,
                    CarriedInCost = settlement.GetValueOrDefault(l.LineNo).CarriedInCost,
                    ExchangeRate  = l.ExchangeRate,
                    SourceDocId   = rec.OutsourceReceiptId,
                    SourceLineNo  = l.LineNo,
                    SetQtyPutAway = q => l.QtyPutAway = q,
                }).ToList();

                await _postingService.PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                {
                    PlantId      = rec.PlantId ?? "",
                    DocNo        = rec.OutsourceReceiptId,
                    MovementDate = rec.ReceiptDate,
                    TypeCode     = rec.TypeCode,
                    TypeCategory = rec.TypeCategory,
                    CreatedBy    = _userContext.UserId,
                    Lines        = lines,
                    SetDocPosted = () => rec.Status = DocStatus.Posted,
                });
                await PostScrapPenaltyJournalAsync(rec, settlement);
                // 簡化模式確認＝直接過帳進一般倉（與一般進貨的簡化模式一樣不經待驗倉）→ 此刻就是合格入庫，
                // 回寫委外派工完工量；非簡化模式要等上架到一般倉或品檢合格放行（mfg-domain.md §19.3a）。
                // 當場驗收不合格、入了待驗倉的行不算。
                foreach (var l in rec.Lines.Where(l => goodLines.Contains(l.LineNo)))
                    await _progress.ApplyGoodQtyAsync(rec.OutsourceReceiptId, l.LineNo, l.StockQty);
                await _context.SaveChangesAsync();
                outcome = ReceiptOutcome.Posted;
            }
            else
            {
                // 非簡化：先上架待驗倉、品檢合格才放行到一般倉。放行的目的地就是明細上的倉儲；
                // 手機收貨不問倉儲，這裡就先補上品號類別的預設倉儲。補不到又要品檢的品號直接擋下——
                // 否則品檢會照樣判「合格」，料卻沒有地方可放而靜默留在待驗倉、派工也不累計（2026-10-14 驗收發現）。
                await FillReleaseTargetsAsync(rec);
                rec.Status    = DocStatus.Confirmed;
                rec.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                outcome = ReceiptOutcome.ConfirmedAwaitingPutAway;
            }

            await ApplyOutsourceSupplyPegAsync(rec, convert: true);   // §24.9(3) 在途→在手
            // 自動產生應付發票
            await _apInvoiceService.GenerateFromOutsourceReceiptAsync(receiptId);
            return outcome;
        }

        /// <summary>
        /// 超收判定與超收部分的加工費（2026-10-12）：
        /// ① 每個委外採購單行：已確認／過帳的收貨＋本張 vs 訂購量，交給 <see cref="SmartFactory.MES.Modules.MFG.Services.IOverQuantityPolicy"/>
        ///    （容許率內照收；超過依廠別參數拒收或限主管＝超量核准權限）。不過就擲出，整張不確認。
        /// ② 廠別參數設「超收部分不付加工費」時，把本行的加工單價改成「可計費量 × 合約單價 ÷ 實收量」：
        ///    應付發票（實收量 × 單價）、成品入庫價值、之後上架用的都是同一個數字，三邊自然一致；
        ///    合約單價與計費量寫進明細備註，事後查得到為什麼單價不同。
        /// </summary>
        private async Task ApplyOverReceiptRulesAsync(OutsourceReceipt rec)
        {
            if (string.IsNullOrEmpty(rec.OutsourcePurchaseOrderId)) return;
            var receivable = (await GetReceivableLinesAsync(rec.OutsourcePurchaseOrderId))
                .ToDictionary(r => r.LineNo);
            var pay = await _overQty.PayOverReceiptFeeAsync(rec.PlantId);
            var changed = false;

            foreach (var g in rec.Lines.Where(l => l.OpoLineNo.HasValue).GroupBy(l => l.OpoLineNo!.Value))
            {
                if (!receivable.TryGetValue(g.Key, out var r)) continue;
                var adding = g.Sum(l => l.ReceiptQty);
                var check = await _overQty.CheckOutsourceReceiptAsync(rec.PlantId, _userContext.ActiveRoleId,
                    r.OrderedQty, r.ReceivedQty, adding);
                if (!check.Allowed) throw new InvalidOperationException($"{r.ProductCode} - {check.Message}");

                if (pay) continue;
                var billable = Math.Max(0m, r.OrderedQty - r.ReceivedQty);   // 本張還能計費的量，依行號順序分給同一行的明細
                foreach (var l in g.OrderBy(l => l.LineNo))
                {
                    var billQty = Math.Min(l.ReceiptQty, billable);
                    billable -= billQty;
                    if (billQty >= l.ReceiptQty || l.ReceiptQty <= 0) continue;
                    var contract = l.UnitCost;
                    l.UnitCost = contract * billQty / l.ReceiptQty;
                    l.Remarks  = string.Join(" / ", new[] { l.Remarks, PzText.Get("Osr_OverReceiptNoFeeNoteFmt",
                        Fmt(contract), Fmt(billQty), Fmt(l.ReceiptQty)) }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    changed = true;
                }
            }
            if (changed)
                rec.TotalAmount = await ApplyLocalAmountsAsync(rec.Lines, rec.ExchangeRate, rec.CurrencyCode);

            static string Fmt(decimal d) => d.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <inheritdoc />
        public async Task<bool> IsOperationOutsourceAsync(string opoId)
        {
            var woIds = await _context.OutsourcePurchaseOrderLines.AsNoTracking()
                .Where(l => l.OutsourcePurchaseOrderId == opoId && l.SourceWorkOrderId != null)
                .Select(l => l.SourceWorkOrderId!).Distinct().ToListAsync();
            return woIds.Count > 0 && await _context.WorkOrders.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(w => woIds.Contains(w.WorkOrderId) && !w.IsOutsource);
        }

        /// <summary>收貨行中屬工序委外者（委外採購單行的來源工單存在且本身不是委外工單）。</summary>
        private async Task<HashSet<int>> OperationOutsourceLineNosAsync(OutsourceReceipt rec)
        {
            if (string.IsNullOrEmpty(rec.OutsourcePurchaseOrderId)) return [];
            var opoLines = await _context.OutsourcePurchaseOrderLines.AsNoTracking()
                .Where(l => l.OutsourcePurchaseOrderId == rec.OutsourcePurchaseOrderId && l.SourceWorkOrderId != null)
                .Select(l => new { l.LineNo, l.SourceWorkOrderId }).ToListAsync();
            var woIds = opoLines.Select(l => l.SourceWorkOrderId!).Distinct().ToList();
            var internalWos = (await _context.WorkOrders.AsNoTracking().IgnoreQueryFilters()
                .Where(w => woIds.Contains(w.WorkOrderId) && !w.IsOutsource)
                .Select(w => w.WorkOrderId).ToListAsync()).ToHashSet();
            var opoOp = opoLines.Where(l => internalWos.Contains(l.SourceWorkOrderId!)).Select(l => l.LineNo).ToHashSet();
            return rec.Lines.Where(l => l.OpoLineNo.HasValue && opoOp.Contains(l.OpoLineNo.Value))
                            .Select(l => l.LineNo).ToHashSet();
        }

        /// <summary>
        /// 工序委外收回（mfg-domain.md §19.7 C）：不入庫、直接過帳。
        ///  - 帳：加工費 借存貨／貸進貨暫估應付（與成品入庫同一組科目，應付發票照常借 2152 沖平）；
        ///    帶出去的料是委外發料，價值本來就留在存貨科目，不另做分錄。
        ///  - 工單成本：加工費＋帶出料（結算的 CarriedInCost）累計到 <c>WorkOrder.ActualOutsourceCost</c>，完工入庫時併入單位成本。
        ///  - 派工：回寫完工量＝收回量－不良數（手機當場驗收有填才扣）；不良品不登 IQC（在製品），交後續工序或 NCR 處理。
        /// </summary>
        private async Task PostOperationOutsourceAsync(
            OutsourceReceipt rec, IReadOnlyDictionary<int, OutsourceLineSettlement> settlement,
            IReadOnlyDictionary<int, decimal>? failByLine)
        {
            var opoLines = await _context.OutsourcePurchaseOrderLines.AsNoTracking()
                .Where(l => l.OutsourcePurchaseOrderId == rec.OutsourcePurchaseOrderId).ToListAsync();
            var fee = 0m;
            var costByWo = new Dictionary<string, decimal>();
            foreach (var l in rec.Lines)
            {
                var fail = failByLine?.GetValueOrDefault(l.LineNo) ?? 0m;
                if (fail < 0 || fail > l.ReceiptQty) throw new InvalidOperationException(PzText.Get("Mob_SampleQtyInvalid"));

                var lineFee = Math.Round(l.StockQty * l.UnitCost * l.ExchangeRate, 4, MidpointRounding.AwayFromZero);
                fee += lineFee;
                var woId = opoLines.First(o => o.LineNo == l.OpoLineNo).SourceWorkOrderId!;
                costByWo[woId] = costByWo.GetValueOrDefault(woId) + lineFee + settlement.GetValueOrDefault(l.LineNo).CarriedInCost;
                l.QtyPutAway = l.StockQty;

                var good = l.ReceiptQty > 0 ? l.StockQty * (l.ReceiptQty - fail) / l.ReceiptQty : 0m;
                await _progress.ApplyGoodQtyAsync(rec.OutsourceReceiptId, l.LineNo, good);
            }

            foreach (var (woId, cost) in costByWo)
            {
                var wo = await _context.WorkOrders.IgnoreQueryFilters().FirstAsync(w => w.WorkOrderId == woId);
                wo.ActualOutsourceCost += cost;
                wo.UpdatedAt = DateTime.UtcNow;
            }

            if (fee > 0m)
                await _journalService.CreateAutoEntryAsync(
                    "INV", rec.OutsourceReceiptId, rec.ReceiptDate, rec.PlantId ?? "",
                    new List<Modules.ACT.Services.JournalLineRequest>
                    {
                        new(Modules.ACT.StandardAccounts.RawMaterialInventoryCode,
                            Modules.ACT.StandardAccounts.RawMaterialInventoryName, fee, 0m),
                        new(Modules.ACT.StandardAccounts.GrIrAPClearingCode,
                            Modules.ACT.StandardAccounts.GrIrAPClearingName, 0m, fee),
                    },
                    description: PzText.Get("Osr_OperationFeeJournal"));
            await PostScrapPenaltyJournalAsync(rec, settlement);

            rec.Status    = DocStatus.Posted;
            rec.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        /// <summary>本廠是否啟用「委外收料當場驗收」（手機收料畫面據此顯示不良數欄位）。</summary>
        public Task<bool> InspectAtReceiptEnabledAsync()
            => PlantFlagAsync(ProjectZSystemParamKeys.MfgOutsourceInspectAtReceipt, _userContext.PlantId);

        /// <summary>
        /// 當場驗收（mfg-domain.md §19.7 B2）：逐行登錄 IQC（抽樣數依 MIL-STD-1916 VL-IV，Ac=0），並把每行的入庫倉儲定好——
        /// 合格與免驗品＝明細倉儲或品號類別預設倉儲；不合格＝本廠待驗倉。回傳合格（會回寫完工量）的行號。
        /// </summary>
        private async Task<HashSet<int>> InspectAtReceiptAsync(OutsourceReceipt rec, IReadOnlyDictionary<int, decimal> failByLine)
        {
            if (_iqc == null || _locations == null)
                throw new InvalidOperationException("OutsourceReceiptService: inspect-at-receipt requires IIqcInspectionService and IPlantDefaultLocationResolver");
            var plantId = rec.PlantId ?? "";
            var codes = rec.Lines.Select(l => l.ProductCode).Distinct().ToList();
            var exempt = (await _context.Products.AsNoTracking().Where(p => codes.Contains(p.ProductCode) && p.IqcExempt)
                .Select(p => p.ProductCode).ToListAsync()).ToHashSet();
            var quarantine = await _context.StorageLocations.AsNoTracking()
                .Join(_context.Warehouses.AsNoTracking().Where(w => w.WarehouseType == SmartFactory.MES.Modules.BAS.Models.WarehouseType.Quarantine && w.PlantId == plantId),
                    l => l.WhCode, w => w.WhCode, (l, w) => new { l.WhCode, l.LocationCode })
                .OrderBy(x => x.WhCode).ThenBy(x => x.LocationCode).FirstOrDefaultAsync();

            var good = new HashSet<int>();
            foreach (var l in rec.Lines)
            {
                var fail = failByLine.GetValueOrDefault(l.LineNo);
                if (fail < 0 || fail > l.ReceiptQty) throw new InvalidOperationException(PzText.Get("Mob_SampleQtyInvalid"));
                var passed = true;
                if (!exempt.Contains(l.ProductCode))
                {
                    var sample = Math.Max(1, Math.Min((int)Math.Ceiling(l.StockQty),
                        MilStd1916Service.GetSampleSize(l.StockQty, MilStd1916Service.VerificationLevel.IV)));
                    var outcome = await _iqc.SubmitAsync(new SmartFactory.MES.Modules.QCR.Services.IqcSubmitInput(
                        rec.OutsourceReceiptId, l.LineNo, l.ProductCode, l.StockQty,
                        Math.Max(sample, fail), fail, (int)MilStd1916Service.VerificationLevel.IV,
                        PzText.Get("Osr_InspectedAtReceipt")), plantId);
                    if (!outcome.Success) throw new InvalidOperationException(outcome.Message);
                    passed = outcome.Result == "Pass";
                }

                if (passed)
                {
                    if (string.IsNullOrWhiteSpace(l.WhCode) || string.IsNullOrWhiteSpace(l.LocationCode))
                    {
                        var (wh, loc) = await _locations.ResolveForProductAsync(l.ProductCode, plantId, null, null);
                        l.WhCode       = string.IsNullOrWhiteSpace(l.WhCode) ? wh : l.WhCode;
                        l.LocationCode = string.IsNullOrWhiteSpace(l.LocationCode) ? loc : l.LocationCode;
                    }
                    if (string.IsNullOrWhiteSpace(l.WhCode) || string.IsNullOrWhiteSpace(l.LocationCode))
                        throw Fail("OutsourceReceiveLocationRequired");
                    good.Add(l.LineNo);
                }
                else
                {
                    if (quarantine == null) throw new InvalidOperationException(PzText.Get("Osr_NoQuarantineLocation"));
                    l.WhCode = quarantine.WhCode; l.LocationCode = quarantine.LocationCode;
                }
            }
            return good;
        }

        /// <summary>
        /// 非簡化模式確認收貨：明細沒填倉儲的，補上品號類別的預設倉儲（品檢合格放行的目的地）。
        /// 免驗品上架時直接選一般倉，不需要；要品檢的品號補不到就擲錯，訊息點名品號。
        /// </summary>
        private async Task FillReleaseTargetsAsync(OutsourceReceipt rec)
        {
            var missing = rec.Lines.Where(l => string.IsNullOrWhiteSpace(l.WhCode) || string.IsNullOrWhiteSpace(l.LocationCode)).ToList();
            if (missing.Count == 0 || _locations == null) return;
            var codes = missing.Select(l => l.ProductCode).Distinct().ToList();
            var exempt = (await _context.Products.AsNoTracking().Where(p => codes.Contains(p.ProductCode) && p.IqcExempt)
                .Select(p => p.ProductCode).ToListAsync()).ToHashSet();
            foreach (var l in missing)
            {
                var (wh, loc) = await _locations.ResolveForProductAsync(l.ProductCode, rec.PlantId, null, null);
                if (!string.IsNullOrWhiteSpace(wh) && !string.IsNullOrWhiteSpace(loc))
                {
                    l.WhCode       = string.IsNullOrWhiteSpace(l.WhCode) ? wh : l.WhCode;
                    l.LocationCode = string.IsNullOrWhiteSpace(l.LocationCode) ? loc : l.LocationCode;
                }
                else if (!exempt.Contains(l.ProductCode))
                    throw new InvalidOperationException(PzText.Get("Osr_ReleaseTargetMissingFmt", l.ProductCode));
            }
        }

        private async Task<bool> PlantFlagAsync(string key, string? plantId) => string.Equals(
            (await _plantParams.GetAsync(key, plantId))?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 送外料收貨時自動扣（mfg-domain.md §19.7 B1）：依「工單用料 ×（累計收回量 ÷ 工單量）」算出到目前為止應發的量，
        /// 減掉已淨發（已發－已退）的量，差額由系統自動建一張委外發料單過帳。所以：
        ///  - 先前已用委外發料單手動發過的，不會再扣一次；分批收貨逐批補扣，收超過就依比例多扣。
        ///  - 整品委外：扣該工單全部用料；工序委外（工單本身不是委外）：只扣那一道工序的用料。
        ///  - 出庫倉儲＝品號類別預設倉儲；取不到就擋下並說明（不猜一個儲位出庫）。
        /// 收回量含不良品：料已經用掉了，不良由超耗扣款與不合格品處置處理。
        /// </summary>
        private async Task AutoIssueSendMaterialsAsync(OutsourceReceipt rec)
        {
            if (string.IsNullOrEmpty(rec.OutsourcePurchaseOrderId)) return;
            // 參數開了卻少了依賴＝組態錯誤：寧可擋下，也不要靜默不扣料（料帳會少一截而且沒有任何訊號）
            if (_slips == null || _locations == null)
                throw new InvalidOperationException("OutsourceReceiptService: auto-issue requires IMaterialSlipService and IPlantDefaultLocationResolver");

            var opoLines = await _context.OutsourcePurchaseOrderLines.AsNoTracking()
                .Where(l => l.OutsourcePurchaseOrderId == rec.OutsourcePurchaseOrderId).ToListAsync();
            var groups = rec.Lines.Where(l => l.OpoLineNo.HasValue)
                .Select(l => (Line: l, Opo: opoLines.FirstOrDefault(o => o.LineNo == l.OpoLineNo)))
                .Where(x => x.Opo != null && !string.IsNullOrEmpty(x.Opo.SourceWorkOrderId))
                .GroupBy(x => (Wo: x.Opo!.SourceWorkOrderId!, Op: x.Opo!.OperationSeq));

            foreach (var g in groups)
            {
                var wo = await _context.WorkOrders.AsNoTracking().IgnoreQueryFilters()
                    .FirstOrDefaultAsync(w => w.WorkOrderId == g.Key.Wo);
                if (wo == null || wo.Quantity <= 0) continue;

                // 同工單／工序的委外採購單行，其他已確認／過帳收貨的收回量＋本張
                var sameOpoLines = await _context.OutsourcePurchaseOrderLines.AsNoTracking()
                    .Where(o => o.SourceWorkOrderId == g.Key.Wo && o.OperationSeq == g.Key.Op)
                    .Select(o => new { o.OutsourcePurchaseOrderId, o.LineNo }).ToListAsync();
                var opoIds = sameOpoLines.Select(o => o.OutsourcePurchaseOrderId).Distinct().ToList();
                var earlier = await _context.OutsourceReceiptLines.AsNoTracking()
                    .Join(_context.OutsourceReceipts.AsNoTracking()
                            .Where(r => opoIds.Contains(r.OutsourcePurchaseOrderId)
                                     && r.OutsourceReceiptId != rec.OutsourceReceiptId
                                     && r.Status != DocStatus.Draft && r.Status != DocStatus.Void),
                        l => l.OutsourceReceiptId, r => r.OutsourceReceiptId,
                        (l, r) => new { r.OutsourcePurchaseOrderId, l.OpoLineNo, l.StockQty })
                    .ToListAsync();
                var cumReceived = earlier.Where(e => sameOpoLines.Any(o => o.OutsourcePurchaseOrderId == e.OutsourcePurchaseOrderId && o.LineNo == e.OpoLineNo))
                                         .Sum(e => e.StockQty)
                                + g.Sum(x => x.Line.StockQty);

                var mats = await _context.WorkOrderMaterials.AsNoTracking()
                    .Where(m => m.WorkOrderId == g.Key.Wo && (wo.IsOutsource || m.OperationSeq == g.Key.Op))
                    .ToListAsync();
                var lines = new List<SmartFactory.MES.Modules.MFG.Services.AutoIssueLine>();
                foreach (var m in mats.OrderBy(m => m.OperationSeq).ThenBy(m => m.BomSeq))
                {
                    var target = await _uomService.RoundQtyAsync(m.UomCode, m.RequiredQty * cumReceived / wo.Quantity);
                    var toIssue = target - (m.IssuedQty - m.ReturnedQty);
                    if (toIssue <= 0) continue;
                    var (wh, loc) = await _locations.ResolveForProductAsync(m.ProductCode, rec.PlantId ?? "", null, null);
                    if (string.IsNullOrWhiteSpace(wh) || string.IsNullOrWhiteSpace(loc))
                        throw new InvalidOperationException(PzText.Get("Osr_AutoIssueNoLocationFmt", m.ProductCode));
                    lines.Add(new(m.OperationSeq, m.BomSeq, m.ProductCode, toIssue, m.UomCode, wh!, loc!));
                }
                if (lines.Count == 0) continue;
                await _slips.CreateAndPostOutsourceIssueAsync(g.Key.Wo, rec.SupplierCode, lines,
                    PzText.Get("Osr_AutoIssueRemarkFmt", rec.OutsourceReceiptId));
            }
        }

        public async Task<List<OpoReceivableLine>> GetReceivableLinesAsync(string opoId)
        {
            var opoLines = await _context.OutsourcePurchaseOrderLines.AsNoTracking()
                .Where(l => l.OutsourcePurchaseOrderId == opoId)
                .OrderBy(l => l.LineNo)
                .ToListAsync();

            var received = await _context.OutsourceReceiptLines.AsNoTracking()
                .Join(_context.OutsourceReceipts, rl => rl.OutsourceReceiptId, r => r.OutsourceReceiptId,
                    (rl, r) => new { r.OutsourcePurchaseOrderId, r.Status, rl.OpoLineNo, rl.ProductCode, rl.ReceiptQty })
                .Where(x => x.OutsourcePurchaseOrderId == opoId && x.Status != DocStatus.Void)
                .ToListAsync();

            // 已收量：有 OpoLineNo 的直接歸行；沒有的（舊資料、同品號多行無法唯一對應）依行號順序分攤，
            // 口徑同委外進度追蹤（OutsourceProgressController）。草稿另列——還沒確認，但已經有人在收了。
            var result = new List<OpoReceivableLine>();
            var unassigned = received.Where(x => x.OpoLineNo == null)
                .GroupBy(x => (x.ProductCode, Draft: x.Status == DocStatus.Draft))
                .ToDictionary(g => g.Key, g => g.Sum(x => x.ReceiptQty));
            foreach (var l in opoLines)
            {
                var ordered = l.SplitQty > 0 ? l.SplitQty : l.Qty;
                var mine = received.Where(x => x.OpoLineNo == l.LineNo).ToList();
                var done  = mine.Where(x => x.Status != DocStatus.Draft).Sum(x => x.ReceiptQty);
                var draft = mine.Where(x => x.Status == DocStatus.Draft).Sum(x => x.ReceiptQty);
                foreach (var isDraft in new[] { false, true })
                {
                    var key = (l.ProductCode, isDraft);
                    if (!unassigned.TryGetValue(key, out var pool) || pool <= 0) continue;
                    var take = Math.Min(pool, Math.Max(0, ordered - done - draft));
                    unassigned[key] = pool - take;
                    if (isDraft) draft += take; else done += take;
                }
                result.Add(new OpoReceivableLine(l.LineNo, l.ProductCode, l.UomCode, ordered, done, draft,
                    Math.Max(0, ordered - done - draft)));
            }
            return result;
        }

        public async Task<(string ReceiptId, ReceiptOutcome Outcome)> ReceiveFromMobileAsync(
            string opoId, DateOnly receiptDate, bool isFinalReceipt, IReadOnlyList<MobileReceiveLine> lines, string? remarks)
        {
            if (lines.Any(l => l.Qty < 0)) throw new InvalidOperationException(PzText.Get("Mob_QtyNegative"));
            var input = lines.Where(l => l.Qty > 0).ToList();
            if (input.Count == 0) throw Fail("AddLineFirst");

            var opo = await _context.OutsourcePurchaseOrders.AsNoTracking().Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.OutsourcePurchaseOrderId == opoId && o.PlantId == _userContext.PlantId);
            if (opo == null)
                throw new InvalidOperationException(string.Format(_localizer["OutsourcePONotFound"].Value, opoId));
            if (opo.Status != OutsourcePoStatus.Confirmed) throw Fail("OutsourcePoNotReceivable");

            // 手機只回報儲位時由伺服器補倉庫（儲位代碼在廠內唯一）
            var locs = input.Where(l => string.IsNullOrWhiteSpace(l.WhCode) && !string.IsNullOrWhiteSpace(l.LocationCode))
                .Select(l => l.LocationCode!.Trim()).Distinct().ToList();
            if (locs.Count > 0)
            {
                var whOf = await _context.StorageLocations.ForCurrentPlant(_context).AsNoTracking()
                    .Where(x => locs.Contains(x.LocationCode)).ToDictionaryAsync(x => x.LocationCode, x => x.WhCode);
                input = input.Select(l => string.IsNullOrWhiteSpace(l.WhCode) && l.LocationCode != null
                    ? l with { LocationCode = l.LocationCode.Trim(), WhCode = whOf.GetValueOrDefault(l.LocationCode.Trim()) }
                    : l).ToList();
            }

            if (await RequiresLocationAtConfirmAsync() && !await IsOperationOutsourceAsync(opoId)
                && input.Any(l => string.IsNullOrWhiteSpace(l.WhCode) || string.IsNullOrWhiteSpace(l.LocationCode)))
                throw Fail("OutsourceReceiveLocationRequired");

            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            var rec = new OutsourceReceipt
            {
                OutsourcePurchaseOrderId = opoId,
                ReceiptDate    = receiptDate,
                SupplierCode   = opo.SupplierCode,
                IsFinalReceipt = isFinalReceipt,
                Remarks        = remarks,
                Status         = DocStatus.Draft,
                ApprovalStatus = DocumentApprovalStatus.Draft,
                PlantId        = _userContext.PlantId,
                CreatedBy      = _userContext.UserId ?? "",
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            };

            int no = 1;
            foreach (var l in input)
            {
                var opoLine = opo.Lines.FirstOrDefault(x => x.LineNo == l.OpoLineNo)
                              ?? throw Fail("OutsourcePoLineNotFound");
                // 加工費一律取委外採購單談好的單價（報價 → 採購單），幣別取該行、沒有就本幣；
                // 匯率取收貨日的買入匯率（與桌機表單預設的 rateType=Buy 一致）。
                var currency = string.IsNullOrWhiteSpace(opoLine.CurrencyCode) ? baseCurrency : opoLine.CurrencyCode;
                decimal rate = 1m;
                if (!string.IsNullOrWhiteSpace(currency) && !string.Equals(currency, baseCurrency, StringComparison.OrdinalIgnoreCase))
                    rate = await _currencyService.GetRateAsync(currency!, receiptDate, RateType.Buy)
                           ?? throw new InvalidOperationException(string.Format(_localizer["ExchangeRateNotFound"].Value, currency, receiptDate));

                rec.Lines.Add(new OutsourceReceiptLine
                {
                    LineNo       = no++,
                    OpoLineNo    = opoLine.LineNo,
                    ProductCode  = opoLine.ProductCode,
                    UomCode      = opoLine.UomCode,
                    ReceiptQty   = l.Qty,
                    UnitCost     = opoLine.UnitPrice,
                    CurrencyCode = currency,
                    ExchangeRate = rate,
                    LotNo        = string.IsNullOrWhiteSpace(l.LotNo) ? null : l.LotNo.Trim(),
                    WhCode       = string.IsNullOrWhiteSpace(l.WhCode) ? null : l.WhCode,
                    LocationCode = string.IsNullOrWhiteSpace(l.LocationCode) ? null : l.LocationCode,
                });
            }
            rec.CurrencyCode = rec.Lines.First().CurrencyCode;
            rec.ExchangeRate = rec.Lines.First().ExchangeRate;

            await using var tx = await _context.Database.BeginTransactionAsync();
            rec.OutsourceReceiptId = await _docNumberService.NextAsync(DocType, _userContext.PlantId ?? "");
            foreach (var l in rec.Lines) l.OutsourceReceiptId = rec.OutsourceReceiptId;
            await NormalizeLinesAsync(opoId, rec.Lines);
            rec.TotalAmount = await ApplyLocalAmountsAsync(rec.Lines, rec.ExchangeRate, rec.CurrencyCode);
            _context.OutsourceReceipts.Add(rec);
            await _context.SaveChangesAsync();

            // 當場驗收：行號 → 不良數（只有參數開著才帶；簽核中的單之後在別處確認，回到原流程）
            Dictionary<int, decimal>? failMap = null;
            if (await InspectAtReceiptEnabledAsync())
            {
                failMap = new();
                int ln = 1;
                foreach (var l in input) failMap[ln++] = l.FailQty ?? 0m;
            }

            var approved = await SubmitAsync(rec.OutsourceReceiptId);
            var outcome = approved ? await ConfirmCoreAsync(rec.OutsourceReceiptId, failMap) : ReceiptOutcome.PendingApproval;
            await tx.CommitAsync();
            return (rec.OutsourceReceiptId, outcome);
        }

        // ── 自 OutsourceReceiptController 搬來（行為不變）─────────────────────────

        /// <summary>
        /// mfg-domain.md §24.9(3)：委外進貨到貨／回退時，對應 OPO 行的供給端 peg 轉換／還原。
        /// convert=true 為到貨（Supply→OnHand），false 為回退（OnHand→Supply）。
        /// </summary>
        public async Task ApplyOutsourceSupplyPegAsync(OutsourceReceipt rec, bool convert)
        {
            foreach (var l in rec.Lines.Where(l => l.OpoLineNo.HasValue))
            {
                if (convert)
                    await _reservations.ConvertSupplyOnArrivalAsync(
                        "OutsourcePurchaseOrder", rec.OutsourcePurchaseOrderId, l.OpoLineNo);
                else
                    await _reservations.RevertSupplyOnUnreceiveAsync(
                        "OutsourcePurchaseOrder", rec.OutsourcePurchaseOrderId, l.OpoLineNo);
            }
        }

        /// <summary>
        /// 超耗扣款的存貨端分錄：借 2152 進貨暫估應付／貸 1401 存貨（完整理由見原 Controller 註解：
        /// 被報廢的料實體消失、價值還在存貨科目；借方掛 2152 讓應付發票算出的差異自然是 0）。
        /// </summary>
        private async Task PostScrapPenaltyJournalAsync(
            OutsourceReceipt rec, IReadOnlyDictionary<int, OutsourceLineSettlement> settlement)
        {
            var penalty = rec.Lines.Sum(l => settlement.GetValueOrDefault(l.LineNo).ExcessScrapPenalty);
            if (penalty <= 0m) return;

            await _journalService.CreateAutoEntryAsync(
                "INV", rec.OutsourceReceiptId, rec.ReceiptDate, rec.PlantId ?? "",
                new List<Modules.ACT.Services.JournalLineRequest>
                {
                    new(Modules.ACT.StandardAccounts.GrIrAPClearingCode,
                        Modules.ACT.StandardAccounts.GrIrAPClearingName, penalty, 0m),
                    new(Modules.ACT.StandardAccounts.RawMaterialInventoryCode,
                        Modules.ACT.StandardAccounts.RawMaterialInventoryName, 0m, penalty),
                },
                description: "委外超耗扣款（在外原料求償）");
        }

        /// <summary>
        /// 補齊明細上「表單畫不出來、但下游一定要有」的欄位：<c>OpoLineNo</c>、<c>StockQty</c>、
        /// 合約允收損耗率。缺任何一個，在外原料不會併回成品、超耗扣款永遠算成 0（W2-F11／W2-F14）。
        /// </summary>
        public async Task NormalizeLinesAsync(string? opoId, IEnumerable<OutsourceReceiptLine> lines)
        {
            var lineList = lines.ToList();
            if (lineList.Count == 0) return;

            var opoLines = string.IsNullOrEmpty(opoId)
                ? new List<OutsourcePurchaseOrderLine>()
                : await _context.OutsourcePurchaseOrderLines.AsNoTracking()
                    .Where(l => l.OutsourcePurchaseOrderId == opoId)
                    .ToListAsync();

            foreach (var l in lineList)
            {
                if (l.OpoLineNo == null)
                {
                    // 同一品號在同一張委外採購單上出現兩次時無法唯一決定，此時只能靠表單帶值。
                    var hits = opoLines.Where(o => o.ProductCode == l.ProductCode).ToList();
                    if (hits.Count == 1) l.OpoLineNo = hits[0].LineNo;
                }

                if (l.AllowedScrapRate == null && l.OpoLineNo != null)
                    l.AllowedScrapRate = opoLines
                        .FirstOrDefault(o => o.LineNo == l.OpoLineNo.Value)?.AllowedScrapRate;

                // 庫存單位與數量一律依品號主檔換算（2026-10-12）。原本表單有帶 StockUomCode／StockQty 就照收，
                // 而那兩欄是隱藏欄位、送什麼收什麼——與庫存單據同一個缺口。
                (l.StockUomCode, l.StockQty) = await _uomService.ToStockQtyAsync(l.ProductCode, l.UomCode, l.ReceiptQty);
            }
        }

        /// <summary>
        /// 後端權威重算每行金額並回傳表頭本幣合計：LineAmount = 數量 × 單價、TaxAmount = LineAmount × 稅率、
        /// AmountLocal = TotalAmount × 匯率（本幣 = 外幣 × Rate）。
        /// </summary>
        public async Task<decimal> ApplyLocalAmountsAsync(IEnumerable<OutsourceReceiptLine> lines, decimal headerRate, string? headerCurrency)
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            decimal total = 0m;
            foreach (var l in lines)
            {
                var rate     = l.ExchangeRate != 0m ? l.ExchangeRate : headerRate;
                var currency = l.CurrencyCode ?? headerCurrency;

                var lineAmt = l.ReceiptQty * l.UnitCost;
                var taxAmt  = lineAmt * l.TaxRate;
                if (!string.IsNullOrWhiteSpace(currency))
                {
                    lineAmt = await _currencyService.RoundAsync(lineAmt, currency);
                    taxAmt  = await _currencyService.RoundAsync(taxAmt, currency);
                }
                l.LineAmount  = lineAmt;
                l.TaxAmount   = taxAmt;
                l.TotalAmount = lineAmt + taxAmt;

                var local = l.TotalAmount * rate;
                l.AmountLocal = string.IsNullOrWhiteSpace(baseCurrency)
                    ? local
                    : await _currencyService.RoundAsync(local, baseCurrency);
                total += l.AmountLocal;
            }
            return total;
        }

        private InvalidOperationException Fail(string key) => new(_localizer[key].Value);
    }
}
