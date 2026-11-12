using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>手機揀料時逐行回報的實際來源（發料）或退回位置（退料）與數量。</summary>
    public record SlipPickLine(int LineNo, string? WhCode, string? LocationCode, string? LotNo, decimal Qty);

    /// <summary>從工單建領料／退料單的一行（品號＋數量；對應工單用料由伺服器導出）。</summary>
    public record SlipRequestLine(string ProductCode, decimal Qty);

    /// <summary>
    /// 工單某品號的現場領退料額度（只算廠內工序；委外工序的料走委外發料／委外退料）。
    /// <para><see cref="RequestableQty"/>：領料（Pull）用料的 需求 − 淨已發 − 已申請未過帳；非 Pull 為 0。</para>
    /// <para><see cref="ReturnableQty"/>：淨已發 − 已退料未過帳。</para>
    /// </summary>
    public record WorkOrderSlipQuota(string ProductCode, string UomCode, string IssueMethod,
        decimal RequiredQty, decimal IssuedQty, decimal ReturnedQty, decimal NetIssuedQty,
        bool IsPull, decimal RequestableQty, decimal ReturnableQty);

    /// <summary>
    /// 發退料單（MaterialSlip）的過帳與建單——桌機 <c>MaterialSlipController</c> 與手機共用的單一實作。
    ///
    /// <para>過帳原本整段寫在 Controller 裡。手機要能「掃描揀料後直接過帳」時若再寫一份，
    /// 就會重演手機報工那份複本（<c>AddReportCoreAsync</c>）漂移到連負數都沒擋的事。
    /// 庫存異動、工單用料累計回寫、預留消耗、退料成本這四件事只能有一個版本。</para>
    ///
    /// <para>業務錯誤一律擲 <see cref="InvalidOperationException"/>（訊息已在地化），呼叫端轉成各自的回應格式。</para>
    /// </summary>
    public interface IMaterialSlipService
    {
        /// <summary>過帳：Confirmed → Posted。自帶交易。</summary>
        Task PostAsync(string slipId, CancellationToken ct = default);

        /// <summary>
        /// 手機一次完成「揀料確認＋過帳」：寫入各行實際倉／儲位／批號／數量 → Confirmed → Posted，同一交易。
        /// 可處理的狀態見 <see cref="CanPickOnMobile"/>。
        /// </summary>
        Task PickAndPostAsync(string slipId, IReadOnlyList<SlipPickLine> lines, CancellationToken ct = default);

        /// <summary>
        /// 系統自動建立並過帳一張「委外發料」單（2026-10-12，委外簡化：送外料收貨時依 BOM 自動扣）。
        /// 不自帶交易——呼叫端（委外收貨確認）已在交易中。明細的 SourceLineId 指向工單用料，
        /// 過帳後工單用料已發量、預留消耗、在外原料結算（OutsourceMaterialFlowReader）與手動發料完全相同。
        /// </summary>
        Task<string> CreateAndPostOutsourceIssueAsync(string workOrderId, string? supplierCode,
            IReadOnlyList<AutoIssueLine> lines, string remarks, CancellationToken ct = default);

        /// <summary>從工單建單（領料申請 MR 送出成待核准；退料 MX 留草稿等倉庫收料）。回傳單號。</summary>
        Task<string> CreateForWorkOrderAsync(MaterialSlipCategory category, string workOrderId,
            IReadOnlyList<SlipRequestLine> lines, string? remarks, CancellationToken ct = default);

        /// <summary>
        /// 工單各品號的領退料額度（同品號跨工序合併）。建單守門與手機畫面共用。
        /// <paramref name="excludeSlipId"/>：編輯既有單時排除它自己，否則它的原數量會被當成「已申請未過帳」。
        /// </summary>
        Task<IReadOnlyList<WorkOrderSlipQuota>> GetWorkOrderSlipQuotaAsync(string workOrderId,
            string? excludeSlipId = null, CancellationToken ct = default);

        /// <summary>
        /// 領料（MR）／退料（MX）來源是工單時的數量守門：領料只限領料(Pull)用料且不超過可申請量，
        /// 退料不超過廠內可退量。其他類別或來源不是工單回 null。違規回在地化訊息。桌機存檔與手機建單共用。
        /// </summary>
        Task<string?> ValidateWorkOrderSlipQtyAsync(MaterialSlipCategory category, string workOrderId,
            IEnumerable<SlipRequestLine> lines, string? excludeSlipId = null, CancellationToken ct = default);

        /// <summary>
        /// 已備料的料要先從備料區扣（過帳時一定檢查）。桌機存檔時先檢查一次：發料單一離開草稿，明細儲位就不能改了，
        /// 等到過帳才擋會讓單子卡住。<paramref name="draft"/>＝true 時，還沒選儲位的品號先不判斷。違規回在地化訊息。
        /// </summary>
        Task<string?> ValidateIssueFromStagingFirstAsync(MaterialSlip slip, bool draft = false, CancellationToken ct = default);
    }

    /// <summary>自動發料的一行：工單用料（工序＋BOM 序）、數量（庫存單位）與出庫倉儲。</summary>
    public sealed record AutoIssueLine(int OperationSeq, int BomSeq, string ProductCode, decimal Qty, string? UomCode,
        string WhCode, string LocationCode);

    public class MaterialSlipService : IMaterialSlipService
    {
        private readonly ApplicationDbContext _db;
        private readonly IInventoryPostingService _posting;
        private readonly IInventoryReservationService _reservations;
        private readonly IDocNumberService _docNumber;
        private readonly IDateTimeService _dt;
        private readonly IUserContext _user;
        private readonly IStringLocalizer<ProjectZSharedResource> _loc;

        public MaterialSlipService(ApplicationDbContext db, IInventoryPostingService posting,
            IInventoryReservationService reservations, IDocNumberService docNumber, IDateTimeService dt,
            IUserContext user, IStringLocalizer<ProjectZSharedResource> loc,
            Modules.ACT.Services.IJournalEntryService? journal = null)
        {
            _db = db; _posting = posting; _reservations = reservations; _docNumber = docNumber;
            _dt = dt; _user = user; _loc = loc; _journal = journal;
        }

        private readonly Modules.ACT.Services.IJournalEntryService? _journal;

        /// <summary>結案後退料分錄的來源單別（見 PostCoreAsync）。</summary>
        internal const string ClosedWorkOrderReturnDocType = "MaterialSlip-ClosedWoReturn";

        /// <summary>
        /// 手機可直接揀料過帳的單：不需核准的類別（MR 以外）草稿即可；MR 必須已核准。
        /// 已在桌機推進到揀料中／已確認的也可以接手。與規格 mfg-domain §17.3「需核准？」欄一致。
        /// </summary>
        public static bool CanPickOnMobile(MaterialSlip s) => s.Status switch
        {
            MaterialSlipStatus.Draft => s.Category != MaterialSlipCategory.InternalRequisition,
            MaterialSlipStatus.Approved or MaterialSlipStatus.Picking or MaterialSlipStatus.Confirmed => true,
            _ => false,
        };

        public async Task PostAsync(string slipId, CancellationToken ct = default)
        {
            var slip = await LoadAsync(slipId, ct);
            if (slip.Status != MaterialSlipStatus.Confirmed) throw Fail("SlipNotConfirmed");

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await PostCoreAsync(slip, ct);
            await tx.CommitAsync(ct);
        }

        public async Task PickAndPostAsync(string slipId, IReadOnlyList<SlipPickLine> lines, CancellationToken ct = default)
        {
            var slip = await LoadAsync(slipId, ct);
            if (!CanPickOnMobile(slip)) throw Fail("SlipNotPickable");

            bool isReturn = MaterialSlipFlow.IsReturn(slip.Category);
            // 同一行回報多個儲位＝拆行（發料：備料區不夠時，不足的部分從其他儲位領；見 EnsureIssueFromStagingFirstAsync）。
            // 第一筆寫回原行，其餘各拆成一行新明細，沿用原行的品號、單位與工單用料對應。
            var nextLineNo = slip.Lines.Count == 0 ? 1 : slip.Lines.Max(l => l.LineNo) + 1;
            var expanded = new List<SlipPickLine>();
            foreach (var g in lines.GroupBy(l => l.LineNo))
            {
                var picksOfLine = g.Where(x => x.Qty != 0).ToList();
                if (picksOfLine.Count <= 1) { expanded.Add(g.Last()); continue; }
                var orig = slip.Lines.FirstOrDefault(l => l.LineNo == g.Key) ?? throw Fail("SlipNothingPicked");
                expanded.Add(picksOfLine[0]);
                foreach (var extra in picksOfLine.Skip(1))
                {
                    var split = new MaterialSlipLine
                    {
                        SlipId = slip.SlipId, LineNo = nextLineNo++, ProductCode = orig.ProductCode,
                        UomCode = orig.UomCode, SourceLineId = orig.SourceLineId,
                        RequestedQty = extra.Qty, ApprovedQty = extra.Qty,
                    };
                    slip.Lines.Add(split);
                    expanded.Add(extra with { LineNo = split.LineNo });
                }
            }
            lines = expanded;
            var byNo = lines.GroupBy(l => l.LineNo).ToDictionary(g => g.Key, g => g.Last());
            foreach (var ln in slip.Lines)
            {
                // 沒回報的行視為整行照申請量揀，但位置一定要有——由 PostCoreAsync 的守門擋下
                if (!byNo.TryGetValue(ln.LineNo, out var p)) continue;
                if (p.Qty < 0) throw new InvalidOperationException(PzText.Get("Mob_QtyNegative"));
                if (isReturn) { ln.ToWarehouseCode = Blank(p.WhCode); ln.ToLocationCode = Blank(p.LocationCode); }
                else          { ln.FromWarehouseCode = Blank(p.WhCode); ln.FromLocationCode = Blank(p.LocationCode); }
                if (p.LotNo != null) ln.LotNo = Blank(p.LotNo);
                ln.ConfirmedQty = p.Qty;
            }
            // 手機只回報儲位時由伺服器補倉庫（儲位代碼在廠內唯一，倉庫可唯一推得）
            var needWh = slip.Lines.Select(l => isReturn ? (l.ToWarehouseCode, l.ToLocationCode) : (l.FromWarehouseCode, l.FromLocationCode))
                .Where(x => string.IsNullOrEmpty(x.Item1) && !string.IsNullOrEmpty(x.Item2)).Select(x => x.Item2!).Distinct().ToList();
            if (needWh.Count > 0)
            {
                var whOf = await _db.StorageLocations.ForCurrentPlant(_db).AsNoTracking()
                    .Where(l => needWh.Contains(l.LocationCode)).ToDictionaryAsync(l => l.LocationCode, l => l.WhCode, ct);
                foreach (var ln in slip.Lines)
                {
                    if (isReturn && string.IsNullOrEmpty(ln.ToWarehouseCode) && ln.ToLocationCode != null)
                        ln.ToWarehouseCode = whOf.GetValueOrDefault(ln.ToLocationCode);
                    if (!isReturn && string.IsNullOrEmpty(ln.FromWarehouseCode) && ln.FromLocationCode != null)
                        ln.FromWarehouseCode = whOf.GetValueOrDefault(ln.FromLocationCode);
                }
            }

            // 數量 0 的行＝這次沒有揀到；整張都是 0 就沒有東西可過帳
            var picked = slip.Lines.Where(l => !byNo.TryGetValue(l.LineNo, out var p) || p.Qty > 0).ToList();
            if (picked.Count == 0) throw Fail("SlipNothingPicked");
            foreach (var zero in slip.Lines.Except(picked).ToList())
            {
                slip.Lines.Remove(zero);
                _db.MaterialSlipLines.Remove(zero);
            }
            foreach (var ln in slip.Lines.Where(l => !byNo.ContainsKey(l.LineNo)))
                ln.ConfirmedQty = ln.ApprovedQty ?? ln.RequestedQty;

            if (slip.Status is MaterialSlipStatus.Draft or MaterialSlipStatus.Approved)
            {
                slip.ApprovedBy ??= _user.UserId;
                slip.ApprovedAt ??= DateTime.UtcNow;
            }
            slip.Status    = MaterialSlipStatus.Confirmed;
            slip.UpdatedAt = DateTime.UtcNow;

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await PostCoreAsync(slip, ct);
            await tx.CommitAsync(ct);
        }

        public async Task<string> CreateAndPostOutsourceIssueAsync(string workOrderId, string? supplierCode,
            IReadOnlyList<AutoIssueLine> lines, string remarks, CancellationToken ct = default)
        {
            var wo = await _db.WorkOrders.AsNoTracking().IgnoreQueryFilters()
                .FirstOrDefaultAsync(w => w.WorkOrderId == workOrderId, ct) ?? throw Fail("RecordNotFound");
            var plantId = wo.PlantId ?? _user.PlantId ?? string.Empty;
            var today   = _dt.GetLocalDate();
            var id      = await _docNumber.NextAsync(CategoryToDocType(MaterialSlipCategory.OutsourceIssue), plantId, today);
            var now     = DateTime.UtcNow;
            var slip = new MaterialSlip
            {
                SlipId = id, SlipNo = id, Category = MaterialSlipCategory.OutsourceIssue,
                Status = MaterialSlipStatus.Confirmed, SlipDate = today,
                SourceDocType = "WorkOrder", SourceDocId = workOrderId, TargetSupplierCode = supplierCode,
                PlantId = plantId, RequestedBy = _user.UserId ?? string.Empty, RequestedAt = now,
                ApprovedBy = _user.UserId, ApprovedAt = now,
                Remarks = remarks, CreatedAt = now, UpdatedAt = now,
            };
            int no = 1;
            foreach (var l in lines)
                slip.Lines.Add(new MaterialSlipLine
                {
                    SlipId = id, LineNo = no++, SourceLineId = $"{l.OperationSeq}_{l.BomSeq}",
                    ProductCode = l.ProductCode, RequestedQty = l.Qty, ConfirmedQty = l.Qty, UomCode = l.UomCode ?? "",
                    FromWarehouseCode = l.WhCode, FromLocationCode = l.LocationCode,
                });
            _db.MaterialSlips.Add(slip);
            await _db.SaveChangesAsync(ct);
            await PostCoreAsync(slip, ct);
            return id;
        }

        public async Task<string> CreateForWorkOrderAsync(MaterialSlipCategory category, string workOrderId,
            IReadOnlyList<SlipRequestLine> lines, string? remarks, CancellationToken ct = default)
        {
            if (category is not (MaterialSlipCategory.InternalRequisition or MaterialSlipCategory.InternalReturn))
                throw Fail("SlipCategoryNotApplicable");
            var req = lines.Where(l => l.Qty > 0 && !string.IsNullOrWhiteSpace(l.ProductCode)).ToList();
            if (req.Count == 0) throw Fail("AddLineFirst");
            if (lines.Any(l => l.Qty < 0)) throw new InvalidOperationException(PzText.Get("Mob_QtyNegative"));

            var wo = await _db.WorkOrders.AsNoTracking()
                .FirstOrDefaultAsync(w => w.WorkOrderId == workOrderId, ct) ?? throw Fail("RecordNotFound");
            if (wo.ApprovalStatus != DocumentApprovalStatus.Approved) throw Fail("WorkOrderNotApproved");

            var mats = await _db.WorkOrderMaterials.AsNoTracking()
                .Where(m => m.WorkOrderId == workOrderId).ToListAsync(ct);
            if (await ValidateWorkOrderSlipQtyAsync(category, workOrderId, req, null, ct) is { } qtyError)
                throw new InvalidOperationException(qtyError);

            var plantId = wo.PlantId ?? _user.PlantId ?? string.Empty;
            var today   = _dt.GetLocalDate();
            var id      = await _docNumber.NextAsync(CategoryToDocType(category), plantId, today);
            var slip = new MaterialSlip
            {
                SlipId = id, SlipNo = id, Category = category, SlipDate = today,
                // 領料申請直接送出成待核准（與桌機「存檔→送審」兩步的結果相同）；退料不需核准，留草稿給倉庫收料
                Status = category == MaterialSlipCategory.InternalRequisition ? MaterialSlipStatus.Pending : MaterialSlipStatus.Draft,
                SourceDocType = "WorkOrder", SourceDocId = workOrderId, PlantId = plantId,
                RequestedBy = _user.UserId ?? string.Empty, RequestedAt = DateTime.UtcNow,
                Remarks = remarks, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            int no = 1;
            foreach (var l in req)
            {
                var uom = mats.FirstOrDefault(m => m.ProductCode == l.ProductCode)?.UomCode;
                slip.Lines.Add(new MaterialSlipLine
                {
                    SlipId = id, LineNo = no++, ProductCode = l.ProductCode, RequestedQty = l.Qty, UomCode = uom ?? "",
                });
            }
            await RelinkWorkOrderMaterialLinesAsync(slip, new(), ct);
            _db.MaterialSlips.Add(slip);
            await _db.SaveChangesAsync(ct);
            return id;
        }

        public async Task<string?> ValidateWorkOrderSlipQtyAsync(MaterialSlipCategory category, string workOrderId,
            IEnumerable<SlipRequestLine> lines, string? excludeSlipId = null, CancellationToken ct = default)
        {
            if (category is not (MaterialSlipCategory.InternalRequisition or MaterialSlipCategory.InternalReturn)
                || string.IsNullOrEmpty(workOrderId)) return null;

            // 2026-10-11 實測：桌機「來源單號」是自由輸入，對不上任何工單時額度整包是空的，
            // 畫面卻說「已發未退的 0」，看起來像帳錯。對不上就直接講對不上。
            if (!await _db.WorkOrders.AsNoTracking().AnyAsync(w => w.WorkOrderId == workOrderId, ct))
                return string.Format(_loc["SlipSourceWorkOrderNotFound"].Value, workOrderId);

            var quota = (await GetWorkOrderSlipQuotaAsync(workOrderId, excludeSlipId, ct)).ToDictionary(q => q.ProductCode);
            foreach (var g in lines.Where(l => !string.IsNullOrWhiteSpace(l.ProductCode)).GroupBy(l => l.ProductCode))
            {
                var qty = g.Sum(l => l.Qty);
                if (qty <= 0) continue;
                var q = quota.GetValueOrDefault(g.Key);
                if (q == null)
                    return string.Format(_loc["SlipProductNotInWorkOrder"].Value, g.Key, workOrderId);
                if (category == MaterialSlipCategory.InternalReturn)
                {
                    // 退料不得超過廠內已發未退（扣掉還沒過帳的退料單）：退多了＝憑空多出庫存，退料成本也取不到發料紀錄。
                    // 2026-10-10 實測：委外發料（OI）送到廠商的料曾被當成已發，用廠內退料退進倉庫。
                    var returnable = q.ReturnableQty;
                    if (qty > returnable)
                        return string.Format(_loc["SlipReturnExceedsIssued"].Value, g.Key, Qty(returnable));
                }
                else
                {
                    // 領料申請只適用領料（Pull）用料，且不得超過 需求 − 淨已發 − 已申請未過帳。
                    // 2026-10-10 實測：料已全數發完仍可一再申請，每張都停在待核准、加總遠超需求。
                    if (!q.IsPull)
                        return string.Format(_loc["SlipRequisitionPullOnly"].Value, g.Key);
                    if (qty > q.RequestableQty)
                        return string.Format(_loc["SlipRequisitionExceedsRemaining"].Value, g.Key, Qty(q.RequestableQty));
                }
            }
            return null;
        }

        /// <summary>訊息裡的數量：DB 讀回的 decimal 帶 6 位小數尺度，直接格式化會是「9.000000」。</summary>
        private static string Qty(decimal d) => d.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

        public async Task<IReadOnlyList<WorkOrderSlipQuota>> GetWorkOrderSlipQuotaAsync(string workOrderId,
            string? excludeSlipId = null, CancellationToken ct = default)
        {
            var outsourceOps = await _db.WorkOrderOperations.AsNoTracking()
                .Where(o => o.WorkOrderId == workOrderId && o.IsOutsource)
                .Select(o => o.OperationSeq).ToListAsync(ct);
            var mats = await _db.WorkOrderMaterials.AsNoTracking()
                .Where(m => m.WorkOrderId == workOrderId && !outsourceOps.Contains(m.OperationSeq))
                .OrderBy(m => m.OperationSeq).ThenBy(m => m.BomSeq)
                .ToListAsync(ct);
            if (mats.Count == 0) return Array.Empty<WorkOrderSlipQuota>();

            // 還沒過帳的領料／退料單：已經佔掉額度，否則同一批料可以一張接一張申請
            var open = await _db.MaterialSlips.AsNoTracking()
                .Where(s => s.SourceDocType == "WorkOrder" && s.SourceDocId == workOrderId
                         && (s.Category == MaterialSlipCategory.InternalRequisition || s.Category == MaterialSlipCategory.InternalReturn)
                         && s.Status != MaterialSlipStatus.Posted && s.Status != MaterialSlipStatus.Closed
                         && s.Status != MaterialSlipStatus.Cancelled
                         && (excludeSlipId == null || s.SlipId != excludeSlipId))
                .SelectMany(s => s.Lines.Select(l => new
                {
                    s.Category, l.ProductCode,
                    Qty = l.ConfirmedQty > 0 ? l.ConfirmedQty : l.RequestedQty,
                }))
                .ToListAsync(ct);
            decimal OpenQty(MaterialSlipCategory c, string p) =>
                open.Where(o => o.Category == c && o.ProductCode == p).Sum(o => o.Qty);

            return mats.GroupBy(m => new { m.ProductCode, m.UomCode }).Select(g =>
            {
                var pull = g.Where(m => m.IssueMethod == MaterialIssueMethod.Pull).ToList();
                var net  = g.Sum(m => m.IssuedQty - m.ReturnedQty);
                var requestable = pull.Count == 0 ? 0m
                    : pull.Sum(m => m.RequiredQty - (m.IssuedQty - m.ReturnedQty))
                      - OpenQty(MaterialSlipCategory.InternalRequisition, g.Key.ProductCode);
                var returnable = net - OpenQty(MaterialSlipCategory.InternalReturn, g.Key.ProductCode);
                return new WorkOrderSlipQuota(g.Key.ProductCode, g.Key.UomCode,
                    string.Join("/", g.Select(m => m.IssueMethod.ToString()).Distinct()),
                    g.Sum(m => m.RequiredQty), g.Sum(m => m.IssuedQty), g.Sum(m => m.ReturnedQty), net,
                    pull.Count > 0, Math.Max(0m, requestable), Math.Max(0m, returnable));
            }).ToList();
        }

        // ── 過帳本體（呼叫端負責交易）────────────────────────────────────────

        private async Task PostCoreAsync(MaterialSlip slip, CancellationToken ct)
        {
            bool isReturn = MaterialSlipFlow.IsReturn(slip.Category);
            var (typeCode, typeCategory) = CategoryToTypeCode(slip);

            // 作廢工單不再承接任何料（作廢時已取消可取消的領料單；這裡擋住作廢前就存在的殘單與任何繞道）。
            // 已結案（入庫完成、成本已結算）的工單只收退料：再發料進去，那批料成本永遠不會再被結算（A7）。
            WorkOrderStatus? woStatus = null;
            if (slip.SourceDocType == "WorkOrder" && !string.IsNullOrEmpty(slip.SourceDocId))
            {
                woStatus = await _db.WorkOrders.AsNoTracking()
                    .Where(w => w.WorkOrderId == slip.SourceDocId)
                    .Select(w => (WorkOrderStatus?)w.Status).FirstOrDefaultAsync(ct);
                if (woStatus == WorkOrderStatus.Void)
                    throw new InvalidOperationException(string.Format(
                        _loc["SlipSourceWorkOrderVoided"].Value, slip.SourceDocId));
                if (woStatus == WorkOrderStatus.Closed && !isReturn)
                    throw new InvalidOperationException(string.Format(
                        _loc["SlipSourceWorkOrderClosed"].Value, slip.SourceDocId));
            }

            // 發料類一定要有來源倉/儲位、退料類一定要有退回倉/儲位。原本空值會一路帶進過帳，
            // 在庫存服務深處才失敗，訊息指不出是哪一行少了什麼。
            var noLoc = slip.Lines.OrderBy(l => l.LineNo)
                .Where(l => isReturn
                    ? string.IsNullOrEmpty(l.ToLocationCode ?? l.FromLocationCode)
                    : string.IsNullOrEmpty(l.FromWarehouseCode) || string.IsNullOrEmpty(l.FromLocationCode))
                .Select(l => l.LineNo).ToList();
            if (noLoc.Count > 0)
                throw new InvalidOperationException(string.Format(
                    _loc[isReturn ? "SlipReturnLocationRequired" : "SlipSourceLocationRequired"].Value,
                    string.Join(", ", noLoc)));

            if (!isReturn) await EnsureIssueFromStagingFirstAsync(slip, ct);

            // 退料要帶回**發料當下**的單位成本（§1.3）。不帶成本的話，入庫會落到
            // InventoryPostingService 的無單價分支：FIFO 直接擲錯（見該處守門）、
            // 移動平均則悄悄改抓目的儲位的現行均價，兩者都不是「退回去的是哪一批料錢」。
            var returnUnitCosts = isReturn ? await ResolveReturnUnitCostsAsync(slip, ct) : new();
            if (isReturn)
            {
                var missing = slip.Lines.Select(l => l.ProductCode).Distinct()
                    .Where(p => !returnUnitCosts.ContainsKey(p)).ToList();
                if (missing.Count > 0)
                    throw new InvalidOperationException(string.Format(
                        _loc["ReturnWithoutIssueHistory"].Value, string.Join("、", missing)));
            }

            var postLines = slip.Lines.OrderBy(l => l.LineNo).Select(l =>
            {
                var line    = l;
                string wh   = isReturn ? (l.ToWarehouseCode  ?? l.FromWarehouseCode ?? "") : (l.FromWarehouseCode ?? "");
                string loc  = isReturn ? (l.ToLocationCode   ?? l.FromLocationCode  ?? "") : (l.FromLocationCode  ?? "");
                decimal qty = l.ConfirmedQty > 0 ? l.ConfirmedQty : l.RequestedQty;
                return new SimplifiedPostLine
                {
                    LineNo        = l.LineNo,
                    ProductCode   = l.ProductCode,
                    WhCode        = wh,
                    LocationCode  = loc,
                    LotNo         = l.LotNo ?? "",
                    Qty           = qty,
                    UomCode       = l.UomCode,
                    StockUomCode  = l.UomCode,
                    UnitPrice     = isReturn ? returnUnitCosts[l.ProductCode] : null,
                    SourceDocId   = slip.SlipId,
                    SourceLineNo  = l.LineNo,
                    SetQtyPutAway = q => line.ConfirmedQty = q,
                };
            }).ToList();

            await _posting.PostDocSimplifiedAsync(new PostDocSimplifiedRequest
            {
                PlantId      = slip.PlantId ?? "",
                DocNo        = slip.SlipId,
                MovementDate = slip.SlipDate,
                TypeCode     = typeCode,
                TypeCategory = typeCategory,
                CreatedBy    = _user.UserId,
                Lines        = postLines,
                // 工單的發退料只是料進出在製品，由完工入庫結算；重工工單的 RI／RR 原本在這裡就直接轉損失，
                // 完工良品的材料成本因此是 0（§16.4b）。
                IsWorkOrderMaterial = slip.SourceDocType == "WorkOrder" && !string.IsNullOrEmpty(slip.SourceDocId),
                SetDocPosted = () => slip.Status = MaterialSlipStatus.Posted,
            });

            // 已結案工單的退料（現場清出剩料，A7 刻意允許）：這批料的成本已在完工時算進成品，
            // 退回倉庫若不入帳，明細帳多出這批料、總帳沒有，結案工單在製變負數（INV_GL_WIP_CLOSED）。
            // 以退料成本 借 原料存貨／貸 製造成本差異，兩行都掛工單（2026-10-22 使用者裁決 (b)）。
            if (isReturn && woStatus == WorkOrderStatus.Closed && _journal != null)
            {
                var value = Math.Round(postLines.Sum(l => l.Qty * (l.UnitPrice ?? 0m)), 4, MidpointRounding.AwayFromZero);
                if (value > 0m)
                    await _journal.CreateAutoEntryAsync(
                        ClosedWorkOrderReturnDocType, slip.SlipId, slip.SlipDate, slip.PlantId ?? "",
                        new List<Modules.ACT.Services.JournalLineRequest>
                        {
                            new(Modules.ACT.StandardAccounts.RawMaterialInventoryCode, Modules.ACT.StandardAccounts.RawMaterialInventoryName,
                                value, 0m, SourceRef: slip.SourceDocId),
                            new(Modules.ACT.StandardAccounts.MfgCostVarianceCode, Modules.ACT.StandardAccounts.MfgCostVarianceName,
                                0m, value, SourceRef: slip.SourceDocId),
                        },
                        description: string.Format(_loc["SlipClosedWoReturnJournal"].Value, slip.SourceDocId, slip.SlipId), ct: ct);
            }

            slip.IssuedBy  = _user.UserId ?? string.Empty;
            slip.IssuedAt  ??= DateTime.UtcNow;
            slip.UpdatedAt = DateTime.UtcNow;

            // 回寫 WorkOrderMaterial 累計量（SourceLineId 慣例："{OperationSeq}_{BomSeq}"）
            if (slip.SourceDocType == "WorkOrder" && !string.IsNullOrEmpty(slip.SourceDocId))
                await BackfillWorkOrderMaterialQtyAsync(slip, isReturn);

            // 發料已實際扣掉在手庫存 → 等額消耗該工單保留的預留（先備料單、後工單自身）。
            // 不消耗的話，「在手已減少」與「預留仍掛著」會對同一批料重複扣減廠級可用量，
            // 用得越多可用量失真越嚴重，最終全廠工單誤判缺料。退料是反向補回庫存，不在此處理。
            if (!isReturn && slip.SourceDocType == "WorkOrder" && !string.IsNullOrEmpty(slip.SourceDocId))
            {
                foreach (var l in slip.Lines)
                {
                    var consumedQty = l.ConfirmedQty > 0 ? l.ConfirmedQty : l.RequestedQty;
                    await _reservations.ConsumeForWorkOrderAsync(slip.SourceDocId!, l.ProductCode, consumedQty);
                }
            }

            await _db.SaveChangesAsync(ct);
        }

        /// <summary>
        /// 已備料的料要先從備料區扣（2026-10-13 使用者裁決）：備料單移入備料儲位之後，這張工單的發料／領料
        /// 從備料儲位扣的量，至少要等於「這張工單在備料區還剩的量」（以備料儲位實際在庫為上限、以本單數量為上限）；
        /// 備料區不夠的部分才可以從其他儲位領。
        ///
        /// <para>不擋的話：料已移到備料區、發料卻從原倉位扣——原倉位被多扣一次，備料區的料留著沒人用，
        /// 而且工單鎖量在這一刻已經釋放，那批料變成沒有歸屬的可用庫存。帳面總量不變，兩個儲位都和實物不符，沒有任何訊號。</para>
        ///
        /// <para>放在過帳核心：桌機、手機揀料、投產自動產生的 Push 領料單都經過這裡。退料不受影響。</para>
        /// </summary>
        private async Task EnsureIssueFromStagingFirstAsync(MaterialSlip slip, CancellationToken ct)
        {
            if (await ValidateIssueFromStagingFirstAsync(slip, draft: false, ct) is { } msg)
                throw new InvalidOperationException(msg);
        }

        public async Task<string?> ValidateIssueFromStagingFirstAsync(MaterialSlip slip, bool draft = false, CancellationToken ct = default)
        {
            if (MaterialSlipFlow.IsReturn(slip.Category)) return null;
            if (slip.SourceDocType != "WorkOrder" || string.IsNullOrEmpty(slip.SourceDocId)) return null;
            var woId = slip.SourceDocId!;
            var products = slip.Lines.Select(l => l.ProductCode).Distinct().ToList();
            var staged = await _db.MaterialStagingLines.AsNoTracking()
                .Where(l => l.StagingOrder!.Status == "Completed" && l.StagingOrder.StagingLocationCode != null
                         && products.Contains(l.ProductCode)
                         && (l.SourceWorkOrderId == woId
                             || (l.SourceWorkOrderId == null && l.StagingOrder.SourceType == "WorkOrder" && l.StagingOrder.SourceId == woId)))
                .Select(l => new { l.ProductCode, l.PickedQty, Loc = l.StagingOrder!.StagingLocationCode! })
                .ToListAsync(ct);
            if (staged.Count == 0) return null;
            var plantId = slip.PlantId ?? _user.PlantId;   // 桌機新建的草稿存檔前還沒有廠別

            var earlierSlips = await _db.MaterialSlips.AsNoTracking()
                .Where(s => s.SourceDocType == "WorkOrder" && s.SourceDocId == woId
                         && s.Status == MaterialSlipStatus.Posted && s.SlipId != slip.SlipId)
                .Select(s => s.SlipId).ToListAsync(ct);

            static decimal Qty(MaterialSlipLine l) => l.ConfirmedQty > 0 ? l.ConfirmedQty : l.RequestedQty;
            foreach (var g in staged.GroupBy(x => x.ProductCode))
            {
                var locs = g.Select(x => x.Loc).Distinct().ToList();
                var issuedFromStaging = earlierSlips.Count == 0 ? 0m : await _db.StockMovements.AsNoTracking()
                    .Where(m => earlierSlips.Contains(m.DocNo) && m.ProductCode == g.Key
                             && locs.Contains(m.LocationCode) && m.Direction == -1)
                    .SumAsync(m => (decimal?)m.StockQty, ct) ?? 0m;
                var onHand = await _db.Inventories.AsNoTracking()
                    .Where(i => i.PlantId == plantId && i.ProductCode == g.Key && locs.Contains(i.LocationCode))
                    .SumAsync(i => (decimal?)i.QtyOnHand, ct) ?? 0m;

                var lines = slip.Lines.Where(l => l.ProductCode == g.Key).ToList();
                if (draft && lines.Any(l => string.IsNullOrEmpty(l.FromLocationCode))) continue;   // 還沒決定儲位
                var total = lines.Sum(Qty);
                var fromStaging = lines.Where(l => l.FromLocationCode != null && locs.Contains(l.FromLocationCode)).Sum(Qty);
                var must = Math.Min(Math.Min(Math.Max(0m, g.Sum(x => x.PickedQty) - issuedFromStaging), onHand), total);
                if (fromStaging < must)
                    return PzText.Get("Mfg_SlipIssueFromStagingFirstFmt", g.Key, string.Join("、", locs), must, fromStaging);
            }
            return null;
        }

        // ── helpers（自 MaterialSlipController 搬來，行為不變）──────────────────

        /// <summary>
        /// 重建明細對來源工單用料的對應（<c>SourceLineId</c>，慣例 "{OperationSeq}_{BomSeq}"）。
        /// 由伺服器維護、不隨表單往返——它決定過帳後 IssuedQty／ReturnedQty 記到哪一筆工單用料上。
        /// 對應順序：先沿用同列號且品號未變的舊值；其餘以品號回查工單用料，只有**唯一命中**才敢認。
        /// （完整背景見 MaterialSlipController 原註解：自動建單被編輯一次就失聯、WIP 短計。）
        /// </summary>
        public async Task RelinkWorkOrderMaterialLinesAsync(
            MaterialSlip slip, Dictionary<int, (string ProductCode, string? SourceLineId)> before, CancellationToken ct = default)
        {
            if (slip.SourceDocType != "WorkOrder" || string.IsNullOrEmpty(slip.SourceDocId)) return;

            foreach (var line in slip.Lines)
                if (before.TryGetValue(line.LineNo, out var prev) && prev.ProductCode == line.ProductCode)
                    line.SourceLineId = prev.SourceLineId;

            var pending = slip.Lines.Where(l => string.IsNullOrEmpty(l.SourceLineId)).ToList();
            if (pending.Count == 0) return;

            var products = pending.Select(l => l.ProductCode).Distinct().ToList();
            var woMats = await _db.WorkOrderMaterials.AsNoTracking()
                .Where(m => m.WorkOrderId == slip.SourceDocId && products.Contains(m.ProductCode))
                .Select(m => new { m.ProductCode, m.OperationSeq, m.BomSeq })
                .ToListAsync(ct);

            foreach (var line in pending)
            {
                var hits = woMats.Where(m => m.ProductCode == line.ProductCode).ToList();
                if (hits.Count == 1) line.SourceLineId = $"{hits[0].OperationSeq}_{hits[0].BomSeq}";
            }
        }

        /// <summary>
        /// 退料的單位成本＝同一來源單據**還在線上的料**的加權平均：先前已過帳發料的凍結成本，扣掉已過帳退料收回的價值。
        /// 與在製品金額恆等式同源（都讀 StockMovement 的凍結成本），不是回頭問現在的庫存值多少。
        ///
        /// <para>原本只取「所有發料」的平均、沒扣已退回的：發 10@30、退 10@30、再發 10@10 之後平均是 20，
        /// 再退 10 就以 20 收回——淨發料 0、在製卻是 −100，退回倉庫的價值比實際發出去的還多
        /// （2026-10-22 ProductionLedgerModelTests 發現，INV_GL_WIP_NEGATIVE 報出）。</para>
        /// </summary>
        private async Task<Dictionary<string, decimal>> ResolveReturnUnitCostsAsync(MaterialSlip slip, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(slip.SourceDocId)) return new();

            var postedSlips = await _db.MaterialSlips.AsNoTracking()
                .Where(s => s.PlantId == slip.PlantId
                         && s.SourceDocType == slip.SourceDocType
                         && s.SourceDocId == slip.SourceDocId
                         && s.Status == MaterialSlipStatus.Posted
                         && s.SlipId != slip.SlipId)
                .Select(s => s.SlipId)
                .ToListAsync(ct);
            if (postedSlips.Count == 0) return new();

            // 發料 −1（送出去的價值）、退料 +1（收回來的價值）；淨額就是還在線上的料
            var moves = await _db.StockMovements.AsNoTracking()
                .Where(m => postedSlips.Contains(m.DocNo))
                .Select(m => new { m.ProductCode, m.StockQty, m.Direction, m.UnitCost })
                .ToListAsync(ct);

            var result = new Dictionary<string, decimal>();
            foreach (var g in moves.GroupBy(m => m.ProductCode))
            {
                var issued = g.Where(m => m.Direction < 0).ToList();
                var issuedQty = issued.Sum(m => m.StockQty);
                if (issuedQty <= 0) continue;                       // 沒發過料：ReturnWithoutIssueHistory 守門
                var netQty   = issuedQty - g.Where(m => m.Direction > 0).Sum(m => m.StockQty);
                var netValue = issued.Sum(m => m.StockQty * (m.UnitCost ?? 0m))
                             - g.Where(m => m.Direction > 0).Sum(m => m.StockQty * (m.UnitCost ?? 0m));
                // 淨量已退完（退料額度守門應先擋下）時退回發料平均，避免除以 0
                result[g.Key] = netQty > 0 ? netValue / netQty : issued.Sum(m => m.StockQty * (m.UnitCost ?? 0m)) / issuedQty;
            }
            return result;
        }

        private async Task BackfillWorkOrderMaterialQtyAsync(MaterialSlip slip, bool isReturn)
        {
            foreach (var line in slip.Lines.Where(l => !string.IsNullOrEmpty(l.SourceLineId)))
            {
                var parts = line.SourceLineId!.Split('_');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out int opSeq)
                    && int.TryParse(parts[1], out int bomSeq))
                {
                    // 品號一致才回寫：不符代表對應已失真，此時記到別筆料上比不記更糟。
                    var wom = await _db.WorkOrderMaterials.FindAsync(slip.SourceDocId, opSeq, bomSeq);
                    if (wom != null && wom.ProductCode == line.ProductCode)
                    {
                        decimal qty = line.ConfirmedQty > 0 ? line.ConfirmedQty : line.RequestedQty;
                        if (isReturn) wom.ReturnedQty += qty;
                        else          wom.IssuedQty   += qty;
                    }
                }
            }
        }

        public static (string TypeCode, string TypeCategory) CategoryToTypeCode(MaterialSlip slip) =>
            slip.Category switch
            {
                MaterialSlipCategory.InternalIssue       => ("IO", "Production"),
                MaterialSlipCategory.InternalRequisition => ("IO", "Production"),
                MaterialSlipCategory.InternalReturn      => ("IR", "Production"),
                MaterialSlipCategory.OutsourceIssue      => ("OO", "Outsource"),
                MaterialSlipCategory.OutsourceReturn     => ("OR", "Outsource"),
                // 重工發料：有 TargetSupplierCode 表示委外重工 → OO；廠內重工 → RI
                MaterialSlipCategory.ReworkIssue         => string.IsNullOrEmpty(slip.TargetSupplierCode)
                                                            ? ("RI", "Rework")
                                                            : ("OO", "Outsource"),
                MaterialSlipCategory.ReworkReturn        => ("RR", "Rework"),
                _                                        => ("IO", "Production"),
            };

        public static string CategoryToDocType(MaterialSlipCategory cat) => cat switch
        {
            MaterialSlipCategory.InternalIssue       => "MaterialSlip_MI",
            MaterialSlipCategory.InternalRequisition => "MaterialSlip_MR",
            MaterialSlipCategory.InternalReturn      => "MaterialSlip_MX",
            MaterialSlipCategory.OutsourceIssue      => "MaterialSlip_OI",
            MaterialSlipCategory.OutsourceReturn     => "MaterialSlip_OR",
            MaterialSlipCategory.ReworkIssue         => "MaterialSlip_RI",
            MaterialSlipCategory.ReworkReturn        => "MaterialSlip_RX",
            _                                        => "MaterialSlip_MI"
        };

        private async Task<MaterialSlip> LoadAsync(string slipId, CancellationToken ct) =>
            await _db.MaterialSlips.Include(s => s.Lines).FirstOrDefaultAsync(s => s.SlipId == slipId, ct)
            ?? throw Fail("RecordNotFound");

        private InvalidOperationException Fail(string key) => new(_loc[key].Value);

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
