using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV.Controllers
{
    /// <summary>
    /// WarehouseApiController 履約引擎（入庫上架 / 出庫下架）。
    /// 依 TypeCode 路由至各單據 handler，最終匯入 PutAwayCoreAsync / PickCoreAsync 單一過帳路徑。
    /// 查詢端點與欄位/建構式見 WarehouseApiController.cs。
    /// </summary>
    public partial class WarehouseApiController
    {

        public class PutAwayRequest
        {
            public string TypeCode    { get; set; } = "";
            /// <summary>
            /// 單別判別。委外收貨與一般進貨單共用 TypeCode "OI"，只看 TypeCode 會導到錯的
            /// handler 而查無此單。既有單別不帶此欄，維持原本的 TypeCode 路由。
            /// </summary>
            public string? DocType    { get; set; }
            public string DocNo       { get; set; } = "";
            public int    LineNo      { get; set; }
            public string? WhCode     { get; set; }
            public string LocationCode { get; set; } = "";
            public string LotNo       { get; set; } = "";
            public decimal Qty        { get; set; }

            /// <summary>
            /// 裝入箱子（container-tracking.md §9.4 進貨上架裝箱）：這次上架的量放進這個空箱，箱子綁到批號。
            /// 批號留空由系統產生（單號-行號-流水號）；自己填的話必須是本廠此品號沒用過的批號。
            /// </summary>
            public string? ContainerUnitId { get; set; }

            /// <summary>離線佇列去重鍵（mobile-pwa.md §8.3）。線上直接送出不帶。</summary>
            public string? ClientId   { get; set; }
            public DateTime? ClientTimestamp { get; set; }
        }

        /// <summary>入庫上架（依 TypeCode 路由至對應單據）。</summary>
        [HttpPost("put-away")]
        public async Task<IActionResult> PutAway([FromBody] PutAwayRequest req)
        {
            if (!await _permissionService.CanCreate(MenuKey))
                return Forbid();

            if (req.Qty <= 0) return BadRequest(new { success = false, message = _localizer["QtyMustBePositive"].Value });
            if (await BeginIdempotentAsync(req.ClientId, req.ClientTimestamp, "/api/warehouse/put-away") is { } replay)
                return replay;

            if (req.DocType == OutsourceReceiptDocType)
                return await PutAwayOutsourceReceipt(req);
            if (GoodsReceiptTypeCodes.Contains(req.TypeCode) || string.IsNullOrEmpty(req.TypeCode))
                return await PutAwayGoodsReceipt(req);
            if (LoanInTypeCodes.Contains(req.TypeCode))
                return await PutAwayLoanDoc(req);
            if (SampleInTypeCodes.Contains(req.TypeCode))
                return await PutAwaySampleDoc(req);
            if (SalesReturnTypeCodes.Contains(req.TypeCode))
                return await PutAwaySalesReturn(req);
            if (InternalOrderInTypeCodes.Contains(req.TypeCode))
                return await PutAwayInternalOrder(req);
            if (ReworkInTypeCodes.Contains(req.TypeCode))
                return await FulfillReworkMaterialAsync(req.DocNo, req.LineNo, (doc, line) => PutAwayCoreAsync(req, doc, line));
            if (OutsourceInTypeCodes.Contains(req.TypeCode))
                return await FulfillOutsourceMaterialAsync(req.DocNo, req.LineNo, (doc, line) => PutAwayCoreAsync(req, doc, line));

            return BadRequest(new { success = false, message = string.Format(_localizer["TypeCodeNotSupportPutaway"].Value, req.TypeCode) });
        }

        public class PickItem
        {
            public string LocationCode { get; set; } = "";
            public string LotNo        { get; set; } = "";
            public decimal Qty         { get; set; }
        }

        public class PickRequest
        {
            public string TypeCode    { get; set; } = "";
            public string DocNo       { get; set; } = "";
            public int    LineNo      { get; set; }
            public List<PickItem> Picks { get; set; } = new();

            /// <summary>離線佇列去重鍵（mobile-pwa.md §8.3）。線上直接送出不帶。</summary>
            public string? ClientId   { get; set; }
            public DateTime? ClientTimestamp { get; set; }
        }

        /// <summary>出庫下架（依 TypeCode 路由至對應單據，支援多儲位）。</summary>
        [HttpPost("pick")]
        public async Task<IActionResult> Pick([FromBody] PickRequest req)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Forbid();

            if (req.Picks == null || !req.Picks.Any())
                return BadRequest(new { success = false, message = _localizer["StocktakePickupNoLines"].Value });
            if (req.Picks.Any(p => p.Qty <= 0))
                return BadRequest(new { success = false, message = _localizer["PickQtyRequired"].Value });
            if (await BeginIdempotentAsync(req.ClientId, req.ClientTimestamp, "/api/warehouse/pick") is { } replay)
                return replay;

            if (GoodsReturnTypeCodes.Contains(req.TypeCode) || string.IsNullOrEmpty(req.TypeCode))
                return await PickGoodsReturn(req);
            if (LoanOutTypeCodes.Contains(req.TypeCode))
                return await PickLoanDoc(req);
            if (SampleOutTypeCodes.Contains(req.TypeCode))
                return await PickSampleDoc(req);
            if (InternalOrderOutTypeCodes.Contains(req.TypeCode))
                return await PickInternalOrder(req);
            if (ScrapTypeCodes.Contains(req.TypeCode))
                return await PickScrapDoc(req);
            if (GoodsShipmentTypeCodes.Contains(req.TypeCode))
                return await PickGoodsShipment(req);
            if (ReworkOutTypeCodes.Contains(req.TypeCode))
                return await FulfillReworkMaterialAsync(req.DocNo, req.LineNo, (doc, line) => PickCoreAsync(req, doc, line));
            if (OutsourceOutTypeCodes.Contains(req.TypeCode))
                return await FulfillOutsourceMaterialAsync(req.DocNo, req.LineNo, (doc, line) => PickCoreAsync(req, doc, line));

            return BadRequest(new { success = false, message = string.Format(_localizer["TypeCodeNotSupportPickup"].Value, req.TypeCode) });
        }

        /// <summary>
        /// 離線佇列的重送去重。回傳非 null＝這個 clientId 已經處理過，呼叫端直接回它。
        ///
        /// <para>原本這兩支端點完全沒有去重：佇列在「伺服端已處理、回應在路上斷線」時
        /// 保留 pending，恢復連線後**再送一次**，擋不擋得住只看剩餘量檢查——剩餘量夠的話
        /// 就是上架／下架兩次（庫存與應付認列一起多一份）。</para>
        ///
        /// <para><b>SyncLog 只「加入追蹤」不在這裡存檔</b>：它必須跟業務變更落在同一個交易
        /// （PutAwayCoreAsync／PickCoreAsync 的 BeginTransaction → SaveChanges → Commit）。
        /// 先存的話，業務失敗回滾之後 SyncLog 還在，下一次重送會被當成「已處理」而永遠送不出去；
        /// 驗證失敗（400／404）沒走到 SaveChanges，它就跟著被丟掉，重送照常處理。
        /// 兩個同 clientId 的請求同時到：第二個在 SaveChanges 撞主鍵、整個交易回滾（500），
        /// 佇列保留 pending，下一輪拿到的就是這裡的「已處理」。</para>
        ///
        /// <para>已處理時回 200 而不是 409：佇列把 2xx 當送出；409 在本專案是並發衝突
        /// （「沒存進去」），兩者混用正是佇列曾把衝突的報工當成送出的原因。</para>
        /// </summary>
        private async Task<IActionResult?> BeginIdempotentAsync(string? clientId, DateTime? clientTimestamp, string endpoint)
        {
            if (string.IsNullOrWhiteSpace(clientId)) return null;   // 線上直接送出，不去重
            if (clientId.Length > 80)
                return BadRequest(new { success = false, message = _localizer["OfflineClientIdInvalid"].Value });

            if (await _context.SyncLogs.AsNoTracking().AnyAsync(s => s.ClientId == clientId))
                return Ok(new { success = true, duplicate = true, message = _localizer["OfflineReplayAlreadyProcessed"].Value });

            _context.SyncLogs.Add(new Modules.MFG.Models.SyncLog
            {
                ClientId        = clientId,
                Endpoint        = endpoint,
                ClientTimestamp = clientTimestamp?.ToUniversalTime() ?? DateTime.UtcNow,
                CreatedAt       = DateTime.UtcNow,
            });
            return null;
        }

        // ─────────────── PutAway handlers ───────────────

        private async Task<IActionResult> PutAwayGoodsReceipt(PutAwayRequest req)
        {
            var gr = await _context.GoodsReceipts
                .Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.GoodsReceiptId == req.DocNo);
            if (gr == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });
            var line = gr.Lines.FirstOrDefault(l => l.LineNo == req.LineNo);
            if (line == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });

            // 品質隔離閘門：此明細尚未 IQC Pass 前，只能上架至待驗倉（WarehouseType.Quarantine）；
            // 通過後才放行至單據記錄的一般倉別，避免待驗/判退收貨繞過檢驗直接可用。
            // 免驗（IqcExempt）品號直放：不受此閘門限制，可直接上架至正常倉。
            var iqcPassed = await _context.MobileIqcRecords.AsNoTracking()
                .AnyAsync(r => r.GoodsReceiptId == gr.GoodsReceiptId && r.LineNo == line.LineNo && r.Result == "Pass");
            var iqcExempt = await _context.Products.AsNoTracking()
                .Where(p => p.ProductCode == line.ProductCode)
                .Select(p => (bool?)p.IqcExempt).FirstOrDefaultAsync() ?? false;
            var allowNormal = iqcPassed || iqcExempt;   // 可進正常倉

            // 目的倉別類型（判斷這次上架是否進正常倉，決定是否認列應付）。
            var loc = await ValidateLocation(req.LocationCode, req.WhCode);
            var targetWhType = loc == null ? (WarehouseType?)null
                : await _context.Warehouses.AsNoTracking()
                    .Where(w => w.WhCode == loc.Value.WhCode)
                    .Select(w => (WarehouseType?)w.WarehouseType)
                    .FirstOrDefaultAsync();
            if (!allowNormal && targetWhType != WarehouseType.Quarantine)
                return BadRequest(new { success = false, message = _localizer["GoodsReceiptMustPutawayToQuarantineFirst"].Value });

            // 存貨計價一律以採購單價為基礎（《對帳規格》§1.1）；收貨改價的差額
            // 由應付認列端落到進料差異，不回刷存貨。
            var accrualUnitPrice = await GoodsReceiptCostBasis.ResolveAccrualUnitPriceAsync(_context, line);

            var result = await PutAwayCoreAsync(req,
                BuildDoc(gr.PlantId, gr.GoodsReceiptId, gr.TypeCode, gr.TypeCategory, gr.ReceiptDate,
                    gr.Status, gr.ApprovalStatus, () => gr.Status.ToString(),
                    () => Task.FromResult(gr.Lines.All(l => l.QtyPutAway >= l.StockQty)),
                    () => gr.Status = DocStatus.Posted),
                new FulfillmentLine
                {
                    LineNo = line.LineNo, ProductCode = line.ProductCode, StockQty = line.StockQty,
                    QtyPutAway = () => line.QtyPutAway, UomCode = line.UomCode, StockUomCode = line.StockUomCode,
                    UnitPrice = accrualUnitPrice, ExchangeRate = line.ExchangeRate, SourceDocId = null, SourceLineNo = null,
                    // 尚未放行前允許暫存待驗倉，不受單據記錄的一般倉別限制；可進正常倉時才比對 line.WhCode
                    LineWhCode = allowNormal ? line.WhCode : null,
                    AddPutAway = qty => { line.QtyPutAway += qty; return Task.CompletedTask; },
                    AddDetail = (locCode, whCode, lotNo, qty) => _context.Set<GoodsReceiptLocationDetail>().Add(new GoodsReceiptLocationDetail
                    {
                        GoodsReceiptId = gr.GoodsReceiptId, LineNo = line.LineNo,
                        LocationCode = locCode, WhCode = whCode, LotNo = lotNo, Qty = qty,
                    }),
                });

            // 貨實際進正常倉（非待驗倉）→ 就這批上架量認列應付（免驗品直放的認列點；
            // 非免驗品走待驗倉時不在此認列，待 IQC 合格放行時才由 IqcInspectionService 認列）。
            // req.Qty 已於 PutAwayCoreAsync 內四捨五入為實際上架量（庫存單位）；AP 認列基礎為進貨
            // 單位，故按明細 ReceiptQty/StockQty 比例換算成進貨單位當量（1:1 時即原值）。
            if (_apInvoice != null && result is OkObjectResult && targetWhType != WarehouseType.Quarantine && req.Qty > 0m)
            {
                var receiptEquiv = line.StockQty > 0m
                    ? req.Qty * line.ReceiptQty / line.StockQty
                    : req.Qty;
                await _apInvoice.AccrueForReceivedQtyAsync(gr.GoodsReceiptId, line.LineNo, receiptEquiv);
            }

            return result;
        }

        /// <summary>
        /// 委外收成品上架（完整模式）。
        ///
        /// 簡化模式是在收貨確認當下就把成品過帳進庫存；完整模式的確認只把單據轉 Confirmed，
        /// 真正入庫由倉庫作業執行——這條路原本不存在，成品因此永遠進不了庫，
        /// 而委外發料早就把原料的數量扣掉了（金額留在存貨科目），
        /// 於是「總帳存貨科目」與「庫存明細帳」永久相差被加工掉的那批料錢。
        ///
        /// <para><b>在外原料的併回</b>：成品入庫價值 = 加工費（單價）+ 在外原料（早已在存貨科目裡，
        /// 只是換了形態）。後者由收貨確認時的結算寫在明細的
        /// <c>CarriedInMaterialCost</c>，此處按**本次上架量**攤分後併入單位成本、
        /// 並於分錄金額扣回（見 <c>PostLineDetailRequest.CarriedInCost</c>）。
        /// 少了它，成品只認加工費，庫存明細帳會永久少於總帳。</para>
        ///
        /// <para><b>超耗扣款分錄</b>（借 2152／貸 1401）於整張單全部上架完成時提一次。
        /// 簡化模式是在確認當下提；完整模式必須等庫存真的動了才提，
        /// 而且只能提一次——逐行提會在分批上架時重複入帳。</para>
        /// </summary>
        private async Task<IActionResult> PutAwayOutsourceReceipt(PutAwayRequest req)
        {
            var rec = await _context.OutsourceReceipts
                .Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.OutsourceReceiptId == req.DocNo);
            if (rec == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });
            var line = rec.Lines.FirstOrDefault(l => l.LineNo == req.LineNo);
            if (line == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });

            var wasPosted = rec.Status == DocStatus.Posted;

            // 品質隔離閘門（2026-10-12，比照一般進貨 PutAwayGoodsReceipt）：委外收回品要先品檢——
            // 尚未 IQC 合格前只能上架到待驗倉，合格後由 IqcInspectionService 自動放行到一般倉；免驗品號直放。
            // 原本委外收貨沒有這道閘門，收回來就能直接進一般倉被領用。
            var iqcPassed = await _context.MobileIqcRecords.AsNoTracking()
                .AnyAsync(r => r.GoodsReceiptId == rec.OutsourceReceiptId && r.LineNo == line.LineNo && r.Result == "Pass");
            var iqcExempt = await _context.Products.AsNoTracking()
                .Where(p => p.ProductCode == line.ProductCode)
                .Select(p => (bool?)p.IqcExempt).FirstOrDefaultAsync() ?? false;
            var allowNormal = iqcPassed || iqcExempt;
            var loc = await ValidateLocation(req.LocationCode, req.WhCode);
            var targetWhType = loc == null ? (WarehouseType?)null
                : await _context.Warehouses.AsNoTracking()
                    .Where(w => w.WhCode == loc.Value.WhCode)
                    .Select(w => (WarehouseType?)w.WarehouseType)
                    .FirstOrDefaultAsync();
            if (!allowNormal && targetWhType != WarehouseType.Quarantine)
                return BadRequest(new { success = false, message = _localizer["GoodsReceiptMustPutawayToQuarantineFirst"].Value });

            var result = await PutAwayCoreAsync(req,
                BuildDoc(rec.PlantId, rec.OutsourceReceiptId, rec.TypeCode, rec.TypeCategory, rec.ReceiptDate,
                    rec.Status, rec.ApprovalStatus, () => rec.Status.ToString(),
                    () => Task.FromResult(rec.Lines.All(l => l.QtyPutAway >= l.StockQty)),
                    () => rec.Status = DocStatus.Posted),
                new FulfillmentLine
                {
                    LineNo = line.LineNo, ProductCode = line.ProductCode, StockQty = line.StockQty,
                    QtyPutAway = () => line.QtyPutAway, UomCode = line.UomCode, StockUomCode = line.StockUomCode,
                    UnitPrice = line.UnitCost, ExchangeRate = line.ExchangeRate,
                    SourceDocId = rec.OutsourcePurchaseOrderId, SourceLineNo = line.OpoLineNo,
                    // 未放行前允許暫存待驗倉，不比對單據記錄的一般倉別（同一般進貨）
                    LineWhCode = allowNormal ? line.WhCode : null,
                    // 分批上架按量攤分：一次全給會讓第一批的成品單價暴衝、後面幾批不含料錢。
                    CarriedInCostFor = qty => line.StockQty > 0m
                        ? Math.Round(line.CarriedInMaterialCost * qty / line.StockQty, 4, MidpointRounding.AwayFromZero)
                        : line.CarriedInMaterialCost,
                    AddPutAway = qty => { line.QtyPutAway += qty; return Task.CompletedTask; },
                    AddDetail = (locCode, whCode, lotNo, qty) => _context.Set<OutsourceReceiptLocationDetail>().Add(new OutsourceReceiptLocationDetail
                    {
                        OutsourceReceiptId = rec.OutsourceReceiptId, LineNo = line.LineNo,
                        LocationCode = locCode, WhCode = whCode, LotNo = lotNo, Qty = qty,
                    }),
                });

            // 整張單剛好在這一次上架完成 → 補提超耗扣款分錄（只提一次）
            if (result is OkObjectResult && !wasPosted && rec.Status == DocStatus.Posted)
                await PostOutsourceScrapPenaltyAsync(rec);

            // 進到一般倉＝合格入庫（免驗品或已合格）→ 回寫委外派工完工量（mfg-domain.md §19.3a）。進待驗倉的不算。
            if (result is OkObjectResult && targetWhType != WarehouseType.Quarantine && req.Qty > 0m
                && HttpContext?.RequestServices?.GetService<SmartFactory.MES.Modules.MFG.Services.IOutsourceProgressService>() is { } progress)
            {
                await progress.ApplyGoodQtyAsync(rec.OutsourceReceiptId, line.LineNo, req.Qty);
                await _context.SaveChangesAsync();
            }

            return result;
        }

        /// <summary>
        /// 委外超耗扣款：借 2152 進貨暫估應付／貸 1401 存貨。
        ///
        /// 貸方刻意是存貨而不是進料差異：被報廢的那批料是**實體消失**，向廠商求償等於
        /// 拿應付換回這筆料錢；記成差異會讓損益憑空多一筆收益，而那筆料錢永遠留在存貨科目。
        /// 借 2152 則讓 APInvoiceService 算出來的差異自然歸零（它讀的是「這張單實際貸記了多少
        /// 2152」），兩端不必各自重算。與簡化模式的 PostScrapPenaltyJournalAsync 同一組分錄。
        /// </summary>
        private async Task PostOutsourceScrapPenaltyAsync(OutsourceReceipt rec)
        {
            if (_journalService == null) return;
            var penalty = rec.Lines.Sum(l => l.ExcessScrapPenalty);
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

        private async Task<IActionResult> PutAwayLoanDoc(PutAwayRequest req)
        {
            var doc = await _context.LoanDocs
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.LoanDocId == req.DocNo);
            if (doc == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });
            var line = doc.Lines.FirstOrDefault(l => l.LineNo == req.LineNo);
            if (line == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });

            return await PutAwayCoreAsync(req,
                BuildDoc(doc.PlantId, doc.LoanDocId, doc.TypeCode, doc.TypeCategory, doc.LoanDate,
                    doc.Status, doc.ApprovalStatus, () => doc.Status.ToString(),
                    () => Task.FromResult(doc.Lines.All(l => l.QtyPutAway >= l.StockQty)),
                    () => doc.Status = DocStatus.Posted),
                new FulfillmentLine
                {
                    LineNo = line.LineNo, ProductCode = line.ProductCode, StockQty = line.StockQty,
                    QtyPutAway = () => line.QtyPutAway, UomCode = line.UomCode, StockUomCode = line.StockUomCode,
                    UnitPrice = line.UnitPrice, SourceDocId = line.SourceDocId, SourceLineNo = line.SourceLineNo, LineWhCode = line.WhCode,
                    AddPutAway = qty => { line.QtyPutAway += qty; return Task.CompletedTask; },
                    AddDetail = (locCode, whCode, lotNo, qty) => _context.Set<LoanDocLocationDetail>().Add(new LoanDocLocationDetail
                    {
                        LoanDocId = doc.LoanDocId, LineNo = line.LineNo,
                        LocationCode = locCode, WhCode = whCode, LotNo = lotNo, Qty = qty,
                    }),
                });
        }

        private async Task<IActionResult> PutAwaySampleDoc(PutAwayRequest req)
        {
            var doc = await _context.SampleDocs
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.SampleDocId == req.DocNo);
            if (doc == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });
            var line = doc.Lines.FirstOrDefault(l => l.LineNo == req.LineNo);
            if (line == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });

            return await PutAwayCoreAsync(req,
                BuildDoc(doc.PlantId, doc.SampleDocId, doc.TypeCode, doc.TypeCategory, doc.SampleDate,
                    doc.Status, doc.ApprovalStatus, () => doc.Status.ToString(),
                    () => Task.FromResult(doc.Lines.All(l => l.QtyPutAway >= l.StockQty)),
                    () => doc.Status = DocStatus.Posted),
                new FulfillmentLine
                {
                    LineNo = line.LineNo, ProductCode = line.ProductCode, StockQty = line.StockQty,
                    QtyPutAway = () => line.QtyPutAway, UomCode = line.UomCode, StockUomCode = line.StockUomCode,
                    UnitPrice = line.UnitPrice, SourceDocId = line.SourceDocId, SourceLineNo = line.SourceLineNo, LineWhCode = line.WhCode,
                    AddPutAway = qty => { line.QtyPutAway += qty; return Task.CompletedTask; },
                    AddDetail = (locCode, whCode, lotNo, qty) => _context.Set<SampleDocLocationDetail>().Add(new SampleDocLocationDetail
                    {
                        SampleDocId = doc.SampleDocId, LineNo = line.LineNo,
                        LocationCode = locCode, WhCode = whCode, LotNo = lotNo, Qty = qty,
                    }),
                });
        }

        // ─────────────── Pick handlers ───────────────

        private async Task<IActionResult> PickGoodsReturn(PickRequest req)
        {
            var gr = await _context.GoodsReturns
                .Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.GoodsReturnId == req.DocNo);
            if (gr == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });
            var line = gr.Lines.FirstOrDefault(l => l.LineNo == req.LineNo);
            if (line == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });

            var result = await PickCoreAsync(req,
                BuildDoc(gr.PlantId, gr.GoodsReturnId, gr.TypeCode, gr.TypeCategory, gr.ReturnDate,
                    gr.Status, gr.ApprovalStatus, () => gr.Status.ToString(),
                    () => Task.FromResult(gr.Lines.All(l => l.QtyPutAway >= l.StockQty)),
                    () => gr.Status = DocStatus.Posted),
                new FulfillmentLine
                {
                    LineNo = line.LineNo, ProductCode = line.ProductCode, StockQty = line.StockQty,
                    QtyPutAway = () => line.QtyPutAway, UomCode = line.UomCode, StockUomCode = line.StockUomCode,
                    UnitPrice = line.UnitPrice, ExchangeRate = line.ExchangeRate, SourceDocId = line.GoodsReceiptId, SourceLineNo = line.ReceiptLineNo,
                    AddPutAway = qty => { line.QtyPutAway += qty; return Task.CompletedTask; },
                    AddDetail = (locCode, whCode, lotNo, qty) => _context.Set<GoodsReturnLocationDetail>().Add(new GoodsReturnLocationDetail
                    {
                        GoodsReturnId = gr.GoodsReturnId, LineNo = line.LineNo,
                        LocationCode = locCode, WhCode = whCode, LotNo = lotNo, Qty = qty,
                    }),
                });

            // 完整模式下架＝貨實際退出正常倉 → 就這批下架量負向回沖來源進貨單應付。
            // 下架量為庫存單位，按明細 ReturnQty/StockQty 比例換算成進貨單位（1:1 時即原值）。
            if (_apInvoice != null && result is OkObjectResult
                && !string.IsNullOrEmpty(line.GoodsReceiptId) && line.ReceiptLineNo.HasValue)
            {
                var pickedStock = req.Picks.Sum(p => p.Qty);
                var receiptEquiv = line.StockQty > 0m ? pickedStock * line.ReturnQty / line.StockQty : pickedStock;
                if (receiptEquiv > 0m)
                {
                    var rev = await _apInvoice.ReverseForReturnedQtyAsync(
                        line.GoodsReceiptId!, line.ReceiptLineNo.Value, receiptEquiv);
                    // 缺口存在就開折讓單草稿（W3-F3 Phase 2）。倉庫端是第二個退貨入口，
                    // 與 GoodsReturnController 各自呼叫——產生器冪等，兩邊都呼叫只會有一張。
                    var noteId = rev.UnreversedGrossLocal > 0m && _apDebitNote != null
                        ? (await _apDebitNote.CreateDraftFromGoodsReturnAsync(gr.GoodsReturnId))?.APDebitNoteId
                        : null;
                    result = WithGapWarning(result, rev.UnreversedGrossLocal, "ApNotReversedAfterSettlement", noteId);
                }
            }

            return result;
        }

        private async Task<IActionResult> PickLoanDoc(PickRequest req)
        {
            var doc = await _context.LoanDocs
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.LoanDocId == req.DocNo);
            if (doc == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });
            var line = doc.Lines.FirstOrDefault(l => l.LineNo == req.LineNo);
            if (line == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });

            return await PickCoreAsync(req,
                BuildDoc(doc.PlantId, doc.LoanDocId, doc.TypeCode, doc.TypeCategory, doc.LoanDate,
                    doc.Status, doc.ApprovalStatus, () => doc.Status.ToString(),
                    () => Task.FromResult(doc.Lines.All(l => l.QtyPutAway >= l.StockQty)),
                    () => doc.Status = DocStatus.Posted),
                new FulfillmentLine
                {
                    LineNo = line.LineNo, ProductCode = line.ProductCode, StockQty = line.StockQty,
                    QtyPutAway = () => line.QtyPutAway, UomCode = line.UomCode, StockUomCode = line.StockUomCode,
                    UnitPrice = line.UnitPrice, SourceDocId = line.SourceDocId, SourceLineNo = line.SourceLineNo,
                    AddPutAway = qty => { line.QtyPutAway += qty; return Task.CompletedTask; },
                    AddDetail = (locCode, whCode, lotNo, qty) => _context.Set<LoanDocLocationDetail>().Add(new LoanDocLocationDetail
                    {
                        LoanDocId = doc.LoanDocId, LineNo = line.LineNo,
                        LocationCode = locCode, WhCode = whCode, LotNo = lotNo, Qty = qty,
                    }),
                });
        }

        private async Task<IActionResult> PickSampleDoc(PickRequest req)
        {
            var doc = await _context.SampleDocs
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.SampleDocId == req.DocNo);
            if (doc == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });
            var line = doc.Lines.FirstOrDefault(l => l.LineNo == req.LineNo);
            if (line == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });

            return await PickCoreAsync(req,
                BuildDoc(doc.PlantId, doc.SampleDocId, doc.TypeCode, doc.TypeCategory, doc.SampleDate,
                    doc.Status, doc.ApprovalStatus, () => doc.Status.ToString(),
                    () => Task.FromResult(doc.Lines.All(l => l.QtyPutAway >= l.StockQty)),
                    () => doc.Status = DocStatus.Posted),
                new FulfillmentLine
                {
                    LineNo = line.LineNo, ProductCode = line.ProductCode, StockQty = line.StockQty,
                    QtyPutAway = () => line.QtyPutAway, UomCode = line.UomCode, StockUomCode = line.StockUomCode,
                    UnitPrice = line.UnitPrice, SourceDocId = line.SourceDocId, SourceLineNo = line.SourceLineNo,
                    AddPutAway = qty => { line.QtyPutAway += qty; return Task.CompletedTask; },
                    AddDetail = (locCode, whCode, lotNo, qty) => _context.Set<SampleDocLocationDetail>().Add(new SampleDocLocationDetail
                    {
                        SampleDocId = doc.SampleDocId, LineNo = line.LineNo,
                        LocationCode = locCode, WhCode = whCode, LotNo = lotNo, Qty = qty,
                    }),
                });
        }

        private async Task<IActionResult> PickInternalOrder(PickRequest req)
        {
            var doc = await _context.InternalOrders
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.InternalOrderId == req.DocNo);
            if (doc == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });
            var line = doc.Lines.FirstOrDefault(l => l.LineNo == req.LineNo);
            if (line == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });

            return await PickCoreAsync(req,
                BuildDoc(doc.PlantId, doc.InternalOrderId, doc.TypeCode, "InternalOrder", doc.OrderDate,
                    doc.Status, doc.ApprovalStatus, () => doc.Status.ToString(),
                    () => Task.FromResult(doc.Lines.All(l => l.QtyPutAway >= l.StockQty)),
                    () => doc.Status = DocStatus.Posted),
                new FulfillmentLine
                {
                    LineNo = line.LineNo, ProductCode = line.ProductCode, StockQty = line.StockQty,
                    QtyPutAway = () => line.QtyPutAway, UomCode = line.UomCode, StockUomCode = line.StockUomCode,
                    UnitPrice = null, SourceDocId = null, SourceLineNo = null,
                    AddPutAway = qty => { line.QtyPutAway += qty; return Task.CompletedTask; },
                    AddDetail = (locCode, whCode, lotNo, qty) => _context.Set<InternalOrderLocationDetail>().Add(new InternalOrderLocationDetail
                    {
                        InternalOrderId = doc.InternalOrderId, LineNo = line.LineNo,
                        LocationCode = locCode, WhCode = whCode, LotNo = lotNo, Qty = qty,
                    }),
                });
        }

        private async Task<IActionResult> PickScrapDoc(PickRequest req)
        {
            var doc = await _context.ScrapDocs
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.ScrapDocId == req.DocNo);
            if (doc == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });
            var line = doc.Lines.FirstOrDefault(l => l.LineNo == req.LineNo);
            if (line == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });

            return await PickCoreAsync(req,
                BuildDoc(doc.PlantId, doc.ScrapDocId, doc.TypeCode, doc.TypeCategory, doc.ScrapDate,
                    doc.Status, doc.ApprovalStatus, () => doc.Status.ToString(),
                    () => Task.FromResult(doc.Lines.All(l => l.QtyPutAway >= l.StockQty)),
                    () => doc.Status = DocStatus.Posted),
                new FulfillmentLine
                {
                    LineNo = line.LineNo, ProductCode = line.ProductCode, StockQty = line.StockQty,
                    QtyPutAway = () => line.QtyPutAway, UomCode = line.UomCode, StockUomCode = line.StockUomCode,
                    UnitPrice = line.UnitPrice, SourceDocId = null, SourceLineNo = null,
                    AddPutAway = qty => { line.QtyPutAway += qty; return Task.CompletedTask; },
                    AddDetail = (locCode, whCode, lotNo, qty) => _context.Set<ScrapDocLocationDetail>().Add(new ScrapDocLocationDetail
                    {
                        ScrapDocId = doc.ScrapDocId, LineNo = line.LineNo,
                        LocationCode = locCode, WhCode = whCode, LotNo = lotNo, Qty = qty,
                    }),
                });
        }

        /// <summary>庫存重工發退料單：RI 下架、RR 上架共用（退料帶來源發料單，回沖其沖銷量）。</summary>
        private async Task<IActionResult> FulfillReworkMaterialAsync(
            string docNo, int lineNo, Func<FulfillmentDoc, FulfillmentLine, Task<IActionResult>> core)
        {
            var doc = await _context.ReworkMaterialDocs
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.ReworkMaterialDocId == docNo);
            if (doc == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });
            var line = doc.Lines.FirstOrDefault(l => l.LineNo == lineNo);
            if (line == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });

            return await core(
                BuildDoc(doc.PlantId, doc.ReworkMaterialDocId, doc.TypeCode, doc.TypeCategory, doc.IssueDate,
                    doc.Status, doc.ApprovalStatus, () => doc.Status.ToString(),
                    () => Task.FromResult(doc.Lines.All(l => l.QtyPutAway >= l.StockQty)),
                    () => doc.Status = DocStatus.Posted),
                new FulfillmentLine
                {
                    LineNo = line.LineNo, ProductCode = line.ProductCode, StockQty = line.StockQty,
                    QtyPutAway = () => line.QtyPutAway, UomCode = line.UomCode, StockUomCode = line.StockUomCode,
                    UnitPrice = line.UnitPrice, SourceDocId = line.SourceDocId, SourceLineNo = line.SourceLineNo, LineWhCode = line.WhCode,
                    AddPutAway = qty => { line.QtyPutAway += qty; return Task.CompletedTask; },
                    AddDetail = (locCode, whCode, lotNo, qty) => _context.ReworkMaterialDocLocationDetails.Add(new ReworkMaterialDocLocationDetail
                    {
                        ReworkMaterialDocId = doc.ReworkMaterialDocId, LineNo = line.LineNo,
                        LocationCode = locCode, WhCode = whCode, LotNo = lotNo, Qty = qty,
                    }),
                });
        }

        /// <summary>庫存委外發退料單：OO 下架、OR 上架共用（退料帶來源發料單，回沖其沖銷量）。</summary>
        private async Task<IActionResult> FulfillOutsourceMaterialAsync(
            string docNo, int lineNo, Func<FulfillmentDoc, FulfillmentLine, Task<IActionResult>> core)
        {
            var doc = await _context.OutsourceMaterialDocs
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.OutsourceMaterialDocId == docNo);
            if (doc == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });
            var line = doc.Lines.FirstOrDefault(l => l.LineNo == lineNo);
            if (line == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });
            // 委外退料上架：以在外原料的淨加權成本收回（同桌機確認路徑）
            await new Modules.PUR.Services.OutsourceMaterialFlowReader(_context).FillReturnCostsAsync(doc);

            return await core(
                BuildDoc(doc.PlantId, doc.OutsourceMaterialDocId, doc.TypeCode, doc.TypeCategory, doc.IssueDate,
                    doc.Status, doc.ApprovalStatus, () => doc.Status.ToString(),
                    () => Task.FromResult(doc.Lines.All(l => l.QtyPutAway >= l.StockQty)),
                    () => doc.Status = DocStatus.Posted),
                new FulfillmentLine
                {
                    LineNo = line.LineNo, ProductCode = line.ProductCode, StockQty = line.StockQty,
                    QtyPutAway = () => line.QtyPutAway, UomCode = line.UomCode, StockUomCode = line.StockUomCode,
                    UnitPrice = line.UnitPrice, SourceDocId = line.SourceDocId, SourceLineNo = line.SourceLineNo, LineWhCode = line.WhCode,
                    AddPutAway = qty => { line.QtyPutAway += qty; return Task.CompletedTask; },
                    AddDetail = (locCode, whCode, lotNo, qty) => _context.OutsourceMaterialDocLocationDetails.Add(new OutsourceMaterialDocLocationDetail
                    {
                        OutsourceMaterialDocId = doc.OutsourceMaterialDocId, LineNo = line.LineNo,
                        LocationCode = locCode, WhCode = whCode, LotNo = lotNo, Qty = qty,
                    }),
                });
        }

        private async Task<IActionResult> PickGoodsShipment(PickRequest req)
        {
            var doc = await _context.GoodsShipments
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.GoodsShipmentId == req.DocNo);
            if (doc == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });
            var line = doc.Lines.FirstOrDefault(l => l.LineNo == req.LineNo);
            if (line == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });

            // 不得拿走保留給其他需求的成品（§24.2a）：預留只在廠級，揀貨看的儲位庫存不知道，揀之前另外擋
            if (_reservations != null)
            {
                try
                {
                    await _reservations.EnsureShipmentWithinFreeStockAsync(doc.PlantId ?? "",
                        new[] { (line.ProductCode, line.SalesOrderId, line.SoLineNo, req.Picks.Sum(p => p.Qty)) });
                }
                catch (InvalidOperationException ex)
                {
                    return BadRequest(new { success = false, message = ex.Message });
                }
            }

            return await PickCoreAsync(req,
                BuildDoc(doc.PlantId, doc.GoodsShipmentId, "GS", "Sales", doc.ShipDate,
                    doc.Status, doc.ApprovalStatus, () => doc.Status.ToString(),
                    () => Task.FromResult(doc.Lines.All(l => l.QtyPutAway >= l.StockQty)),
                    () => doc.Status = DocStatus.Posted),
                new FulfillmentLine
                {
                    LineNo = line.LineNo, ProductCode = line.ProductCode, StockQty = line.StockQty,
                    QtyPutAway = () => line.QtyPutAway, UomCode = line.UomCode, StockUomCode = line.StockUomCode,
                    UnitPrice = line.UnitPrice, SourceDocId = null, SourceLineNo = null,
                    // 揀貨即過帳出庫：同步消耗為該訂單列保留的成品（mfg-domain.md §24.2a）
                    AddPutAway = async qty =>
                    {
                        line.QtyPutAway += qty;
                        if (_reservations != null && !string.IsNullOrEmpty(line.SalesOrderId) && line.SoLineNo.HasValue)
                            await _reservations.ConsumeForSalesOrderLineAsync(line.SalesOrderId!, line.SoLineNo.Value, line.ProductCode, qty);
                    },
                    AddDetail = (locCode, whCode, lotNo, qty) => _context.Set<GoodsShipmentLocationDetail>().Add(new GoodsShipmentLocationDetail
                    {
                        GoodsShipmentId = doc.GoodsShipmentId, LineNo = line.LineNo,
                        LocationCode = locCode, WhCode = whCode, LotNo = lotNo, Qty = qty,
                    }),
                });
        }

        private async Task<IActionResult> PutAwaySalesReturn(PutAwayRequest req)
        {
            var doc = await _context.SalesReturns
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.SalesReturnId == req.DocNo);
            if (doc == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });
            var line = doc.Lines.FirstOrDefault(l => l.LineNo == req.LineNo);
            if (line == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });

            // 入庫成本取原出貨異動實際扣掉的單位成本，不是銷退單上的售價。
            // 銷退單上根本沒有成本欄位——畫面帶過來的 UnitPrice 是售價，
            // 直接拿它入庫會把存貨科目與庫存明細帳「一致地」灌成售價，而且
            // 移動平均被汙染之後，往後每一次出貨的 COGS 都跟著錯。
            // 簡化模式早就這樣做了（SalesReturnController.BuildSimplifiedLinesAsync
            // → SalesReturnCostBasis），完整模式的上架路徑一直漏掉這一段：
            // 同一張銷退單走哪個模式，入庫成本竟然是兩個數字。
            // 找不到來源出貨（手工加入、未連結的退回行）時回 null，
            // 由過帳端以現行單位成本入庫（該路徑不動移動平均）。
            var returnUnitCost = await Modules.ORD.Services.SalesReturnCostBasis.ResolveUnitCostAsync(
                _context, line.GoodsShipmentId, line.ShipmentLineNo);

            // 注意：銷退上架不檢查 line 倉別（原始行為，LineWhCode 留空 → 跳過比對）。
            var result = await PutAwayCoreAsync(req,
                BuildDoc(doc.PlantId, doc.SalesReturnId, "GR", "Sales", doc.ReturnDate,
                    doc.Status, doc.ApprovalStatus, () => doc.Status.ToString(),
                    () => Task.FromResult(doc.Lines.All(l => l.QtyPutAway >= l.StockQty)),
                    () => doc.Status = DocStatus.Posted),
                new FulfillmentLine
                {
                    LineNo = line.LineNo, ProductCode = line.ProductCode, StockQty = line.StockQty,
                    QtyPutAway = () => line.QtyPutAway, UomCode = line.UomCode, StockUomCode = line.StockUomCode,
                    // 成本基礎已是本幣（來自 StockMovement.UnitCost），故匯率固定 1。
                    // null 不退回 line.UnitPrice——那正是售價，退回去等於沒修；
                    // null 的語意是「查不到原始成本」，由過帳端以現行單位成本入庫，
                    // 與簡化模式逐字相同。
                    UnitPrice = returnUnitCost,
                    ExchangeRate = 1m,
                    SourceDocId = line.GoodsShipmentId, SourceLineNo = line.ShipmentLineNo,
                    AddPutAway = qty => { line.QtyPutAway += qty; return Task.CompletedTask; },
                    AddDetail = (locCode, whCode, lotNo, qty) => _context.Set<SalesReturnLocationDetail>().Add(new SalesReturnLocationDetail
                    {
                        SalesReturnId = doc.SalesReturnId, LineNo = line.LineNo,
                        LocationCode = locCode, WhCode = whCode, LotNo = lotNo, Qty = qty,
                    }),
                });

            // 完整模式上架＝退回貨實際入正常倉 → 就這批上架量負向回沖來源出貨單應收。
            // req.Qty 已於 PutAwayCoreAsync 內四捨五入為實際上架量（庫存單位）；AR 開立基礎為銷售
            // 單位，故按明細 ReturnQty/StockQty 比例換算成銷售單位當量（1:1 時即原值）。
            if (_arInvoice != null && result is OkObjectResult && req.Qty > 0m
                && !string.IsNullOrEmpty(line.GoodsShipmentId) && line.ShipmentLineNo.HasValue)
            {
                var salesEquiv = line.StockQty > 0m ? req.Qty * line.ReturnQty / line.StockQty : req.Qty;
                if (salesEquiv > 0m)
                {
                    var rev = await _arInvoice.ReverseForReturnedQtyAsync(
                        line.GoodsShipmentId!, line.ShipmentLineNo.Value, salesEquiv);
                    var noteId = rev.UnreversedGrossLocal > 0m && _arCreditNote != null
                        ? (await _arCreditNote.CreateDraftFromSalesReturnAsync(doc.SalesReturnId))?.ARCreditNoteId
                        : null;
                    result = WithGapWarning(result, rev.UnreversedGrossLocal, "ArNotReversedAfterSettlement", noteId);
                }
            }
            return result;
        }

        private async Task<IActionResult> PutAwayInternalOrder(PutAwayRequest req)
        {
            var doc = await _context.InternalOrders
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.InternalOrderId == req.DocNo);
            if (doc == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });
            var line = doc.Lines.FirstOrDefault(l => l.LineNo == req.LineNo);
            if (line == null) return NotFound(new { success = false, message = _localizer["RecordNotFound"].Value });

            return await PutAwayCoreAsync(req,
                BuildDoc(doc.PlantId, doc.InternalOrderId, doc.TypeCode, "InternalOrder", doc.OrderDate,
                    doc.Status, doc.ApprovalStatus, () => doc.Status.ToString(),
                    () => Task.FromResult(doc.Lines.All(l => l.QtyPutAway >= l.StockQty)),
                    () => doc.Status = DocStatus.Posted),
                new FulfillmentLine
                {
                    LineNo = line.LineNo, ProductCode = line.ProductCode, StockQty = line.StockQty,
                    QtyPutAway = () => line.QtyPutAway, UomCode = line.UomCode, StockUomCode = line.StockUomCode,
                    UnitPrice = null, SourceDocId = null, SourceLineNo = null,
                    AddPutAway = qty => { line.QtyPutAway += qty; return Task.CompletedTask; },
                    AddDetail = (locCode, whCode, lotNo, qty) => _context.Set<InternalOrderLocationDetail>().Add(new InternalOrderLocationDetail
                    {
                        InternalOrderId = doc.InternalOrderId, LineNo = line.LineNo,
                        LocationCode = locCode, WhCode = whCode, LotNo = lotNo, Qty = qty,
                    }),
                });
        }

        // ─────────────── 共用上架/下架核心（消除各單別樣板複製）───────────────

        private delegate void AddLocationDetailDelegate(string locationCode, string whCode, string lotNo, decimal qty);

        /// <summary>單據層抽象：承載過帳所需欄位與「狀態/完成判定」的活值委派。</summary>
        private sealed class FulfillmentDoc
        {
            public required string PlantId { get; init; }
            public required string DocNo { get; init; }
            public required string TypeCode { get; init; }
            public required string? TypeCategory { get; init; }
            public required DateOnly MovementDate { get; init; }
            public required DocStatus Status { get; init; }
            public required DocumentApprovalStatus ApprovalStatus { get; init; }
            public required Func<string> StatusText { get; init; }       // 回應時讀活值（過帳後可能已轉 Posted）
            public required Func<Task<bool>> AllLinesComplete { get; init; }
            public required Action SetPosted { get; init; }
        }

        /// <summary>行層抽象：承載過帳欄位、上架量累加與儲位明細建立委派。</summary>
        private sealed class FulfillmentLine
        {
            public required int LineNo { get; init; }
            public required string ProductCode { get; init; }
            public required decimal StockQty { get; init; }
            public required Func<decimal> QtyPutAway { get; init; }       // 活值讀取（驗算與回應用）
            public required string? UomCode { get; init; }
            public required string? StockUomCode { get; init; }
            public required decimal? UnitPrice { get; init; }
            /// <summary>單價的兌本位幣匯率；內部異動單據（調撥/借出/重工）無外幣，維持 1。</summary>
            public decimal ExchangeRate { get; init; } = 1m;
            public required string? SourceDocId { get; init; }
            public required int? SourceLineNo { get; init; }
            public string? LineWhCode { get; init; }                     // 僅上架用；null=跳過倉別比對
            /// <summary>
            /// 本次上架要併入單位成本、但不再產生分錄的既有存貨價值（本幣）。
            /// 傳入本次上架量、回傳該量對應的併入金額——分批上架要按量攤分，
            /// 一次全給會讓第一批的成品單價暴衝、後面幾批不含料錢。
            /// 目前唯一來源是委外收成品的在外原料（見 OutsourceReceiptSettlementService）。
            /// </summary>
            public Func<decimal, decimal>? CarriedInCostFor { get; init; }
            public required Func<decimal, Task> AddPutAway { get; init; }
            public required AddLocationDetailDelegate AddDetail { get; init; }
        }

        /// <summary>
        /// 倉庫作業的成功回應。原本是匿名型別——改成具名，是為了讓呼叫端能在回應
        /// **建立之後**補上「有多少錢沒被回沖」的警告；匿名型別只能靠反射改，不值得。
        /// 屬性名維持 PascalCase，序列化仍走預設 camelCase，前端讀到的 JSON 完全不變。
        ///
        /// 宣告成 **private nested**：它只在本類別內部使用，而 R5b（驗證欄位名必須有
        /// resx 鍵）掃的是模組命名空間下的 public class——一個純粹的回應 DTO 沒有任何
        /// Razor 檢視繫結它，補 `Success`／`QtyPutAway` 這種 resx 鍵等於造假欄位名。
        /// R5b 對 `IsNestedPrivate` 的豁免正是為這類型別寫的通則。
        /// </summary>
        private sealed record FulfillmentResponse(
            bool Success, string Message, string DocStatus, decimal QtyPutAway);

        /// <summary>
        /// 把「未回沖缺口」接在成功訊息後面。缺口 0（或回應不是成功）時原樣返回。
        ///
        /// 這一步是整個修正的重點：服務層本來就不沖已結清的發票（那是刻意的），
        /// 但呼叫端把回傳值丟掉，於是操作員按完上架只看到「上架完成」，
        /// 完全不知道有一筆錢沒有進到帳上。
        /// </summary>
        private IActionResult WithGapWarning(
            IActionResult result, decimal gap, string messageKey, string? noteId = null)
        {
            if (gap <= 0m || result is not OkObjectResult ok || ok.Value is not FulfillmentResponse resp)
                return result;
            var warning = string.Format(_localizer[messageKey].Value, gap.ToString("N2"), noteId ?? "-");
            return new OkObjectResult(resp with { Message = resp.Message + " " + warning });
        }

        private static FulfillmentDoc BuildDoc(
            string? plantId, string docNo, string typeCode, string? typeCategory, DateOnly movementDate,
            DocStatus status, DocumentApprovalStatus approvalStatus,
            Func<string> statusText, Func<Task<bool>> allLinesComplete, Action setPosted) => new()
        {
            PlantId = plantId ?? "", DocNo = docNo, TypeCode = typeCode, TypeCategory = typeCategory,
            MovementDate = movementDate, Status = status, ApprovalStatus = approvalStatus,
            StatusText = statusText, AllLinesComplete = allLinesComplete, SetPosted = setPosted,
        };

        /// <summary>入庫上架核心：單一儲位上架，含倉別比對（LineWhCode 非空時）。</summary>
        private async Task<IActionResult> PutAwayCoreAsync(PutAwayRequest req, FulfillmentDoc doc, FulfillmentLine line)
        {
            if (doc.Status != DocStatus.Confirmed)
                return BadRequest(new { success = false, message = _localizer["DocNotConfirmedCannotPutaway"].Value });
            if (doc.ApprovalStatus != DocumentApprovalStatus.Approved)
                return BadRequest(new { success = false, message = _localizer["DocPendingApprovalWarehouse"].Value });

            req.Qty = await _uomService.RoundQtyAsync(line.StockUomCode, req.Qty);
            var remaining = line.StockQty - line.QtyPutAway();
            if (req.Qty > remaining)
                return BadRequest(new { success = false, message = string.Format(_localizer["PutawayQtyExceeds"].Value, remaining) });

            var loc = await ValidateLocation(req.LocationCode, req.WhCode);
            if (loc == null) return BadRequest(new { success = false, message = _localizer["LocationNotInWarehouse"].Value });
            if (!string.IsNullOrWhiteSpace(line.LineWhCode) && line.LineWhCode != loc.Value.WhCode)
                return BadRequest(new { success = false, message = string.Format(_localizer["LocationWarehouseMismatch"].Value, loc.Value.WhCode, line.LineWhCode) });

            // 裝入箱子：交易前先驗完（空箱、啟用；批號必須是新的），一箱一個批號箱內數量才等於這一箱
            var units = new ContainerUnitService(_context);
            ContainerUnit? box = null;
            if (!string.IsNullOrWhiteSpace(req.ContainerUnitId))
            {
                try
                {
                    box = (await units.TakeEmptyForBindingAsync(new[] { req.ContainerUnitId.Trim().ToUpperInvariant() }, null))[0];
                    req.LotNo = (req.LotNo ?? "").Trim();
                    if (req.LotNo.Length == 0)
                        req.LotNo = await units.NextLotAsync(line.ProductCode, $"{doc.DocNo}-{line.LineNo:D2}");
                    else if (await units.LotUsedAsync(line.ProductCode, req.LotNo))
                        return BadRequest(new { success = false, message = PzText.Get("Bas_ContainerUnitLotUsedFmt", req.LotNo, box.ContainerUnitId) });
                }
                catch (InvalidOperationException ex) { return BadRequest(new { success = false, message = ex.Message }); }
            }

            // 過帳路徑內含日記帳自身 SaveChanges，須以顯式交易包住單筆上架過帳與後續存檔。
            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                line.AddDetail(req.LocationCode, loc.Value.WhCode, req.LotNo, req.Qty);
                await PostDetail(doc.PlantId, doc.DocNo, line.LineNo, line.ProductCode,
                    req.LocationCode, req.LotNo, req.Qty, doc.TypeCode, doc.TypeCategory,
                    doc.MovementDate, line.UomCode ?? "", line.StockUomCode ?? "", line.UnitPrice,
                    line.ExchangeRate, line.SourceDocId, line.SourceLineNo,
                    line.AddPutAway, doc.AllLinesComplete, doc.SetPosted,
                    line.CarriedInCostFor?.Invoke(req.Qty) ?? 0m);
                if (box != null)
                    units.Bind(box, line.ProductCode, req.LotNo, req.Qty, doc.DocNo, _userContext.UserId);

                await _context.SaveChangesAsync();
                await tx.CommitAsync();
                var message = box == null ? _localizer["PutawayCompleted"].Value
                    : PzText.Get("Inv_PutawayBoxedFmt", _localizer["PutawayCompleted"].Value, box.ContainerUnitId, req.LotNo);
                return Ok(new FulfillmentResponse(true, message, doc.StatusText(), line.QtyPutAway()));
            }
            catch (InvalidOperationException ex) { return BadRequest(new { success = false, message = ex.Message }); }
            catch (DbUpdateConcurrencyException) { return BadRequest(new { success = false, message = _localizer["ConcurrencyConflict"].Value }); }
        }

        /// <summary>出庫下架核心：支援多儲位（req.Picks），逐筆查儲位後過帳。</summary>
        private async Task<IActionResult> PickCoreAsync(PickRequest req, FulfillmentDoc doc, FulfillmentLine line)
        {
            if (doc.Status != DocStatus.Confirmed)
                return BadRequest(new { success = false, message = _localizer["DocNotConfirmedCannotPickup"].Value });
            if (doc.ApprovalStatus != DocumentApprovalStatus.Approved)
                return BadRequest(new { success = false, message = _localizer["DocPendingApprovalWarehouse"].Value });

            foreach (var p in req.Picks) p.Qty = await _uomService.RoundQtyAsync(line.StockUomCode, p.Qty);
            var remaining = line.StockQty - line.QtyPutAway();
            if (req.Picks.Sum(p => p.Qty) > remaining)
                return BadRequest(new { success = false, message = string.Format(_localizer["PickQtyExceeds"].Value, remaining) });

            // 多儲位逐筆過帳：每筆過帳路徑內含日記帳自身 SaveChanges，須以顯式交易包住整個迴圈與
            // 後續存檔，避免迴圈中途失敗造成部分儲位已過帳、其餘未過帳的不一致。
            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                foreach (var pick in req.Picks)
                {
                    var loc = await _context.StorageLocations.ForCurrentPlant(_context).AsNoTracking()
                        .FirstOrDefaultAsync(l => l.LocationCode == pick.LocationCode);
                    if (loc == null)
                        return BadRequest(new { success = false, message = string.Format(_localizer["LocationPickNotExist"].Value, pick.LocationCode) });

                    line.AddDetail(pick.LocationCode, loc.WhCode, pick.LotNo, pick.Qty);
                    await PostDetail(doc.PlantId, doc.DocNo, line.LineNo, line.ProductCode,
                        pick.LocationCode, pick.LotNo, pick.Qty, doc.TypeCode, doc.TypeCategory,
                        doc.MovementDate, line.UomCode ?? "", line.StockUomCode ?? "", line.UnitPrice,
                        line.ExchangeRate, line.SourceDocId, line.SourceLineNo,
                        line.AddPutAway, doc.AllLinesComplete, doc.SetPosted);
                }
                await _context.SaveChangesAsync();
                await tx.CommitAsync();
                return Ok(new FulfillmentResponse(true, _localizer["PickupCompleted"].Value, doc.StatusText(), line.QtyPutAway()));
            }
            catch (InvalidOperationException ex) { return BadRequest(new { success = false, message = ex.Message }); }
            catch (DbUpdateConcurrencyException) { return BadRequest(new { success = false, message = _localizer["ConcurrencyConflict"].Value }); }
        }

        // ─────────────── Shared helpers ───────────────

        private async Task<(string WhCode, string LocationCode)?> ValidateLocation(string locationCode, string? whCode)
        {
            var q = _context.StorageLocations.ForCurrentPlant(_context).AsNoTracking()
                .Where(l => l.LocationCode == locationCode);
            if (!string.IsNullOrWhiteSpace(whCode))
                q = q.Where(l => l.WhCode == whCode);
            var loc = await q.Select(l => new { l.WhCode, l.LocationCode }).FirstOrDefaultAsync();
            if (loc == null) return null;
            return (loc.WhCode, loc.LocationCode);
        }

        private Task PostDetail(
            string plantId, string docNo, int lineNo, string productCode,
            string locationCode, string lotNo, decimal qty,
            string typeCode, string? typeCategory, DateOnly movementDate,
            string uomCode, string stockUomCode, decimal? unitPrice, decimal exchangeRate,
            string? sourceDocId, int? sourceLineNo,
            Func<decimal, Task> updateQtyPutAway,
            Func<Task<bool>> checkAllComplete,
            Action setPosted,
            decimal carriedInCost = 0m)
        {
            return _postingService.PostLineDetailAsync(new PostLineDetailRequest
            {
                CarriedInCost    = carriedInCost,
                PlantId          = plantId,
                DocNo            = docNo,
                LineNo           = lineNo,
                ProductCode      = productCode,
                LocationCode     = locationCode,
                LotNo            = lotNo,
                Qty              = qty,
                TypeCode         = typeCode,
                TypeCategory     = typeCategory,
                MovementDate     = movementDate,
                UomCode          = uomCode,
                StockUomCode     = stockUomCode,
                UnitPrice        = unitPrice,
                ExchangeRate     = exchangeRate,
                SourceDocId      = sourceDocId,
                SourceLineNo     = sourceLineNo,
                CreatedBy        = _userContext.UserId,
                UpdateQtyPutAwayAsync    = updateQtyPutAway,
                CheckAllLinesCompleteAsync = checkAllComplete,
                SetDocPosted     = setPosted,
            });
        }
    }
}
