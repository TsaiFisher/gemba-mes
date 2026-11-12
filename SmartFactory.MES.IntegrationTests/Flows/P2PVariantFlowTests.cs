using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// P2P 變異路徑的共用前置。
    ///
    /// <see cref="P2PFlowTests"/> 釘的是「整數單價、一次全額、本幣、一次付清」這條主幹；
    /// 主幹之外的四條路徑（部分認列、部分付款、外幣、小數位單價）先前沒有任何跨模組測試
    /// 走過，而人工驗收作業書 §4.1 的 1-4／1-6／1-8／1-9 正是這四條。把前置抽到這裡，
    /// 是因為四支測試的差別只在「金額怎麼算」，單據怎麼建完全一樣——重複三份會讓
    /// 真正在測的那幾行淹沒在建單樣板裡。
    /// </summary>
    public abstract class P2PVariantTestBase : FlowTestBase
    {
        protected const string Mat = "FLOW-MAT";
        protected const string Po  = "FLOW-PO-V";

        protected static readonly DateOnly OrderDate = new(2097, 5, 1);
        protected static readonly DateOnly RecvDate  = new(2097, 5, 10);
        protected static readonly DateOnly PayDate   = new(2097, 5, 20);

        protected P2PVariantTestBase(TestWebApplicationFactory factory) : base(factory)
            => ResetAsync((Mat, "流程測試原料")).GetAwaiter().GetResult();

        /// <summary>建一張已核准的採購單（單一明細）。</summary>
        protected async Task CreateApprovedPoAsync(
            decimal orderQty, decimal price, decimal taxRate, string? currency = null)
        {
            using var scope = NewScope(out var db);
            var po = new PurchaseOrder
            {
                PurchaseOrderId = Po, OrderDate = OrderDate, CreatedBy = UserId,
                SupplierCode = Supplier, ApprovalStatus = DocumentApprovalStatus.Approved,
                PlantId = Plant, CreatedByRoleId = RoleId,
            };
            po.Lines.Add(new PurchaseOrderLine
            {
                PurchaseOrderId = Po, LineNo = 1, ProductCode = Mat,
                OrderQty = orderQty, UomCode = Uom, UnitPrice = price, TaxRate = taxRate,
                CurrencyCode = currency,
            });
            db.PurchaseOrders.Add(po);
            await db.SaveChangesAsync();
        }

        /// <summary>由採購單拋轉一張進貨單，回傳進貨單號。</summary>
        protected async Task<string> CreateGoodsReceiptAsync(
            decimal qty, decimal price, decimal taxRate, string? currency = null, decimal rate = 1m)
        {
            using var scope = NewScope(out _);
            var purchase = Svc<IPurchaseService>(scope);
            var net   = qty * price;
            var gross = net * (1m + taxRate);
            var gr = await purchase.CreateGoodsReceiptAsync(new GoodsReceiptFormVm
            {
                ReceiptDate = RecvDate, CreatedBy = UserId, SupplierCode = Supplier,
                Lines =
                [
                    new GoodsReceiptLineVm
                    {
                        LineNo = 1, PurchaseOrderId = Po, PoLineNo = 1, ProductCode = Mat,
                        ReceiptQty = qty, StockQty = qty,
                        UomCode = Uom, StockUomCode = Uom, UnitPrice = price,
                        CurrencyCode = currency, ExchangeRate = rate,
                        TaxRate = taxRate, LineAmount = net, TotalAmount = gross,
                    }
                ],
            }, UserId, Plant);
            return gr.GoodsReceiptId;
        }

        /// <summary>
        /// 把進貨明細的一批量上架進正常倉（借 存貨／貸 進貨暫估應付）。
        ///
        /// 計價單價刻意不由呼叫端指定，而是與正式上架路徑走同一個解析點
        /// （<see cref="GoodsReceiptCostBasis"/>）——否則測試自己傳一個單價進來，
        /// 驗到的就不是系統實際會用的計價基礎。
        /// </summary>
        protected async Task PutAwayAsync(string grId, decimal qty, decimal rate = 1m)
        {
            using var scope = NewScope(out var db);
            var posting = Svc<IInventoryPostingService>(scope);
            var gr = await db.GoodsReceipts.IgnoreQueryFilters()
                .Include(g => g.Lines).SingleAsync(g => g.GoodsReceiptId == grId);
            var line = gr.Lines.Single();
            var price = await GoodsReceiptCostBasis.ResolveAccrualUnitPriceAsync(db, line);

            await posting.PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = grId, LineNo = line.LineNo,
                ProductCode = Mat, LocationCode = Loc, LotNo = "",
                Qty = qty, TypeCode = "PI", TypeCategory = "Purchase",
                MovementDate = RecvDate, UomCode = Uom, StockUomCode = Uom,
                UnitPrice = price, ExchangeRate = rate,
                UpdateQtyPutAwayAsync      = q => { line.QtyPutAway += q; return Task.CompletedTask; },
                CheckAllLinesCompleteAsync = () => Task.FromResult(line.QtyPutAway >= line.ReceiptQty),
                SetDocPosted               = () => gr.Status = DocStatus.Posted,
            });
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// 人工驗收作業書 §4.1 的 1-4（部分認列）、1-6（部分付款）、1-9（小數位單價）。
    ///
    /// 這三條共用同一個廠別。xUnit 每個 [Fact] 會重新建構一次測試類別，
    /// 建構子的 ResetAsync 會先清空本廠別，故彼此不會殘留互污。
    /// </summary>
    [Collection("Integration")]
    public class P2PVariantFlowTests : P2PVariantTestBase
    {
        public P2PVariantFlowTests(TestWebApplicationFactory factory) : base(factory) { }

        protected override string Plant => "ZZP2PV";

        /// <summary>
        /// 1-4：收料 100 全數上架，但只認列 40（發票量 &lt; 收料量）。
        ///
        /// 本系統沒有「開發票」這個動作——應付是由「進正常倉的實際量」逐次累加認列的
        /// （<see cref="IAPInvoiceService.AccrueForReceivedQtyAsync"/>），所以「部分核銷」
        /// 在這裡的語意是「已上架但尚未全部認列」。要釘的是暫估餘額**不可歸零**：
        /// 2152 借方（認列）只沖掉貸方（上架）的一部分，差額必須恰好等於未認列部分。
        /// </summary>
        [Fact]
        public async Task PartialAccrual_LeavesGrIrBalanceEqualToUnaccruedPortion()
        {
            const decimal qty = 100m, price = 250m, taxRate = 0.05m;
            const decimal accrued = 40m;

            await CreateApprovedPoAsync(qty, price, taxRate);
            var grId = await CreateGoodsReceiptAsync(qty, price, taxRate);
            await PutAwayAsync(grId, qty);

            // ① 只認列 40：暫估餘額必須剩下未認列的 60 × 250
            using (var scope = NewScope(out var db))
            {
                var invoice = await Svc<IAPInvoiceService>(scope)
                    .AccrueForReceivedQtyAsync(grId, 1, accrued);

                Assert.Equal(accrued * price * (1m + taxRate), invoice.TotalAmount);
                Assert.Equal(APInvoiceStatus.Open, invoice.Status);

                var gl = await GlNetByAccountAsync(db, Plant);
                // 上架貸 25,000、認列借 10,000 → 淨額為貸方 15,000（負值＝貸餘）
                Assert.Equal(-(qty - accrued) * price, Net(gl, StandardAccounts.GrIrAPClearingCode));
                // 《對帳規格》§1.1：暫估應付餘額 = 累計進貨暫估 − 累計發票核銷 − 已認列 PPV
                Assert.Equal(qty * price - accrued * price - 0m,
                             -Net(gl, StandardAccounts.GrIrAPClearingCode));
            }

            // ② 補認列剩餘 60：暫估必須精準歸零，不留尾差
            using (var scope = NewScope(out var db))
            {
                var invoice = await Svc<IAPInvoiceService>(scope)
                    .AccrueForReceivedQtyAsync(grId, 1, qty - accrued);

                Assert.Equal(qty * price * (1m + taxRate), invoice.TotalAmount);

                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.Equal(0m, Net(gl, StandardAccounts.GrIrAPClearingCode));

                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditPoOverReceiptAsync(Plant));
                Assert.Empty(await audit.AuditGrIrAsync(Plant));
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
            }
        }


        /// <summary>
        /// 1-3：進貨單價與採購單價不同 → 進料價差（PPV）必須認列為當期費用，
        /// **不得直接吃進存貨成本**。
        ///
        /// 這是本系統唯一存在的 PPV 入口：應付是由收貨單價逐次認列的（沒有「另外開一張
        /// 單價不同的發票」這個動作，AP 發票畫面只有檢視／作廢），所以價差只能來自
        /// 收貨當下把單價改掉。
        ///
        /// 斷言刻意不指定價差科目：科目表裡的 5151 是「進料費用差異」（運費等 landed cost
        /// 分攤用），並沒有專屬的採購價差科目。真正不可妥協的是存貨這一端——存貨必須停在
        /// 採購單估價，價差落在哪個損益科目是設計選擇，落進存貨則是錯的（後續出庫成本、
        /// 毛利、WIP 全部跟著偏）。
        ///
        /// 計價基礎＝採購單價，見 <see cref="Modules.PUR.Services.GoodsReceiptCostBasis"/>；
        /// 價差由 AccrueForReceivedQtyAsync 沖銷暫估時落到進料差異。
        /// </summary>
        [Fact]
        public async Task InvoicePriceDiffersFromPoPrice_RecognisesVarianceAsExpense_NotInventoryCost()
        {
            const decimal qty = 100m, poPrice = 250m, grPrice = 260m, taxRate = 0.05m;
            const decimal variance = qty * (grPrice - poPrice);   // 1,000 不利差異

            await CreateApprovedPoAsync(qty, poPrice, taxRate);
            var grId = await CreateGoodsReceiptAsync(qty, grPrice, taxRate);
            await PutAwayAsync(grId, qty);

            using (var scope = NewScope(out var db))
            {
                await Svc<IAPInvoiceService>(scope).AccrueForReceivedQtyAsync(grId, 1, qty);

                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);

                // 存貨停在採購單估價，價差不得混入
                Assert.Equal(qty * poPrice, Net(gl, StandardAccounts.RawMaterialInventoryCode));

                // 價差落在損益（5xxx 借方合計）
                var expenseNet = gl.Where(kv => kv.Key.StartsWith("5")).Sum(kv => kv.Value);
                Assert.Equal(variance, expenseNet);

                // 暫估仍須收斂：認列端沖掉的是含價差的實際應付
                Assert.Equal(0m, Net(gl, StandardAccounts.GrIrAPClearingCode));
            }
        }

        /// <summary>
        /// 1-6：先付一部分再補足。
        ///
        /// 兩個判準：部分付款期間**不可提前結清**（狀態必須是 PartialPaid、未結餘額必須
        /// 等於差額），補足後 2100 應付帳款必須精準收斂為 0——分兩次付款會產生兩張傳票，
        /// 任何一次的四捨五入寫錯都會在這裡留下殘值。
        /// </summary>
        [Fact]
        public async Task PartialPayment_DoesNotSettleEarly_AndConvergesAfterTopUp()
        {
            const decimal qty = 100m, price = 250m, taxRate = 0.05m;
            const decimal gross = qty * price * (1m + taxRate);   // 26,250
            const decimal first = 10_000m;

            await CreateApprovedPoAsync(qty, price, taxRate);
            var grId = await CreateGoodsReceiptAsync(qty, price, taxRate);
            await PutAwayAsync(grId, qty);

            string apId;
            using (var scope = NewScope(out _))
                apId = (await Svc<IAPInvoiceService>(scope).AccrueForReceivedQtyAsync(grId, 1, qty)).APInvoiceId;

            // ① 部分付款：不可提前結清
            using (var scope = NewScope(out var db))
            {
                var fin = Svc<IFinPaymentService>(scope);
                var pmt = await fin.CreateAPPaymentAsync(new APPaymentRequest(
                    Supplier, PayDate, PaymentMethod.Wire, null, 1m, first, Bank, null, null,
                    new List<PaymentAllocationLine> { new(apId, first) }), UserId, Plant);
                await fin.ConfirmAPPaymentAsync(pmt.APPaymentId);

                var inv = await db.APInvoices.IgnoreQueryFilters().SingleAsync(i => i.APInvoiceId == apId);
                Assert.Equal(APInvoiceStatus.PartialPaid, inv.Status);
                Assert.Equal(first, inv.PaidAmount);
                Assert.Equal(gross - first,
                    await Svc<IAPInvoiceService>(scope).GetOutstandingBalanceAsync(Supplier, Plant));
            }

            // ② 補足餘額：狀態轉已付、2100 收斂為 0
            using (var scope = NewScope(out var db))
            {
                var fin = Svc<IFinPaymentService>(scope);
                var pmt = await fin.CreateAPPaymentAsync(new APPaymentRequest(
                    Supplier, PayDate, PaymentMethod.Wire, null, 1m, gross - first, Bank, null, null,
                    new List<PaymentAllocationLine> { new(apId, gross - first) }), UserId, Plant);
                await fin.ConfirmAPPaymentAsync(pmt.APPaymentId);

                var inv = await db.APInvoices.IgnoreQueryFilters().SingleAsync(i => i.APInvoiceId == apId);
                Assert.Equal(APInvoiceStatus.Paid, inv.Status);
                Assert.Equal(gross, inv.PaidAmount);
                Assert.Equal(0m, await Svc<IAPInvoiceService>(scope).GetOutstandingBalanceAsync(Supplier, Plant));

                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.Equal(0m, Net(gl, StandardAccounts.AccountsPayableCode));
                Assert.Equal(0m, Net(gl, StandardAccounts.GrIrAPClearingCode));
                Assert.Equal(0m, gl.Values.Sum());
            }
        }

        /// <summary>
        /// 1-9：單價帶小數位，且分三批上架逐次認列。
        ///
        /// 單價 12.345 × 7 = 86.415，落在幣別小數位（2 位）之外——這是尾差最容易累積的
        /// 形狀。分批更是關鍵：認列金額是「重算累計後相減」得出增量，每一批各自進位一次，
        /// 若進位口徑與上架端不同，2152 會留下一個小到不會有人發現、但永遠平不掉的殘值。
        /// 出場條件就是《對帳規格》要的那一件事：全額認列後暫估**精準**為 0。
        /// </summary>
        [Fact]
        public async Task FractionalUnitPrice_BatchedAccrual_LeavesNoRoundingResidue()
        {
            const decimal price = 12.345m, taxRate = 0.05m;
            const decimal qty = 7m;
            decimal[] batches = [3m, 3m, 1m];

            await CreateApprovedPoAsync(qty, price, taxRate);
            var grId = await CreateGoodsReceiptAsync(qty, price, taxRate);

            foreach (var batch in batches)
            {
                await PutAwayAsync(grId, batch);
                using var scope = NewScope(out _);
                await Svc<IAPInvoiceService>(scope).AccrueForReceivedQtyAsync(grId, 1, batch);
            }

            using (var scope = NewScope(out var db))
            {
                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);

                // 全額上架且全額認列 → 暫估應付必須精準歸零（尾差不得累積）
                Assert.Equal(0m, Net(gl, StandardAccounts.GrIrAPClearingCode));

                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditGrIrAsync(Plant));
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
            }
        }

        /// <summary>
        /// 分批上架時，**中間態**也不得出現實質進料價差。
        ///
        /// 這是上面那支尾差測試看不到的另一半：它只斷言「全額認列後 2152 歸零」，
        /// 而錯誤的比例換算會在中間各批灌出鉅額假性價差、再於末批整數沖回——
        /// 末狀態完全正常，測試全綠。2026-09-04 人工驗收 1-9 實測到的形狀是
        /// 12.5 分兩批，第一批 20.81 的應付有 10.40（50%）被記進 5151，
        /// 而該筆採購收貨單價＝採購單價，真實價差是 0。
        ///
        /// 為什麼中間態值得單獨釘：分批到貨跨月結時，那筆假性價差會落在
        /// **已關帳期間**的損益表上，反向沖回落在下一期，兩期各憑空多一筆費用／收益；
        /// 借貸自始至終是平的，所以平衡稽核與 GR/IR 稽核都不會叫。
        ///
        /// 刻意用「收貨單價＝採購單價」，把真正的價差路徑排除在外：
        /// 唯一容許的殘值是幣別進位尾差，其餘一律是錯的。
        /// </summary>
        [Fact]
        public async Task BatchedPutAway_DoesNotBookPhantomVarianceOnIntermediateBatches()
        {
            const decimal price = 3.33m, taxRate = 0.05m, qty = 12.5m;
            decimal[] batches = [6.25m, 6.25m];

            await CreateApprovedPoAsync(qty, price, taxRate);
            var grId = await CreateGoodsReceiptAsync(qty, price, taxRate);

            decimal done = 0m;
            foreach (var batch in batches)
            {
                await PutAwayAsync(grId, batch);
                using (var scope = NewScope(out _))
                    await Svc<IAPInvoiceService>(scope).AccrueForReceivedQtyAsync(grId, 1, batch);
                done += batch;

                using var check = NewScope(out var db);
                var variance = Net(await GlNetByAccountAsync(db, Plant),
                                   StandardAccounts.LandedCostVarianceCode);

                // 容許值：每批最多一個幣別最小單位的進位尾差。修正前這裡是 10.40。
                Assert.True(Math.Abs(variance) <= 0.01m * batches.Length,
                    $"已上架 {done}／{qty}，5151 進料費用差異累計 {variance}——"
                    + "收貨單價與採購單價相同，不該有實質價差，只該有進位尾差。");
            }

            using (var scope = NewScope(out var db))
            {
                await AssertEveryEntryBalancedAsync(db, Plant);
                Assert.Equal(0m, Net(await GlNetByAccountAsync(db, Plant),
                                     StandardAccounts.GrIrAPClearingCode));
            }
        }

        /// <summary>
        /// 計價基礎必須在**建單時凍結**，不是上架時現查採購單。
        ///
        /// 已核准的採購單仍可改單價（<c>PurchaseOrderStateMachine.CanEdit</c> 只擋審核中），
        /// 而上架是逐批的——現查會讓「先上架一部分、改了採購價、再上架剩下」的同一批貨
        /// 進兩個不同成本，且事後無從得知當初用的是哪一個價：暫估掛帳那一腿是記錄值
        /// （見 AccrueForReceivedQtyAsync 的沖銷金額），存貨這一腿卻只能重算，
        /// 重算的答案又已經變了，對帳兩端於是不同源。
        ///
        /// 這裡刻意讓收貨單價＝採購單價，把價差路徑排除在外：唯一的變數是採購單事後改價。
        /// 見 <see cref="Modules.PUR.Services.GoodsReceiptCostBasis"/> 與
        /// <see cref="Modules.PUR.Models.GoodsReceiptLine.AccrualUnitPrice"/>。
        /// </summary>
        [Fact]
        public async Task PoPriceChangedBetweenPutAways_KeepsCostBasisFrozenAtReceiptTime()
        {
            const decimal qty = 100m, price = 250m, laterPrice = 240m, taxRate = 0m;
            decimal[] batches = [60m, 40m];

            await CreateApprovedPoAsync(qty, price, taxRate);
            var grId = await CreateGoodsReceiptAsync(qty, price, taxRate);

            // ① 建單當下就凍結：欄位有值，且等於當時的採購單價
            using (var scope = NewScope(out var db))
            {
                var line = await db.GoodsReceiptLines.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(l => l.GoodsReceiptId == grId);
                Assert.Equal(price, line.AccrualUnitPrice);
            }

            await PutAwayAsync(grId, batches[0]);

            // ② 兩批之間把採購單價改掉（已核准的採購單仍可修改）
            using (var scope = NewScope(out var db))
            {
                var poLine = await db.PurchaseOrderLines.IgnoreQueryFilters()
                    .SingleAsync(l => l.PurchaseOrderId == Po && l.LineNo == 1);
                poLine.UnitPrice = laterPrice;
                await db.SaveChangesAsync();
            }

            await PutAwayAsync(grId, batches[1]);

            using (var scope = NewScope(out var db))
            {
                // ③ 兩批都以收貨當下的計價基礎入庫——不是各進各的成本
                var moves = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .Where(m => m.DocNo == grId).ToListAsync();
                Assert.Equal(batches, moves.Select(m => m.StockQty).OrderByDescending(q => q).ToArray());
                Assert.All(moves, m => Assert.Equal(price, m.UnitCost));

                // ④ 存貨明細與總帳都停在 25,000（現查會得到 60×250 + 40×240 = 24,600）
                var ledger = await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                    .Where(i => i.PlantId == Plant && i.ProductCode == Mat)
                    .SumAsync(i => i.QtyOnHand * i.UnitCost);
                Assert.Equal(qty * price, ledger);

                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.Equal(qty * price, Net(gl, StandardAccounts.RawMaterialInventoryCode));
                Assert.Equal(-qty * price, Net(gl, StandardAccounts.GrIrAPClearingCode));
            }
        }

        /// <summary>
        /// 分批上架的**中間**發生退貨。這一支是為了合併 PR #131／#132 而補的：
        /// 兩支各自修了 <c>AccrueForReceivedQtyAsync</c> 的不同一半，
        /// #132 把認列上限改成「收貨量 − 已過帳退貨量」，
        /// #131 把沖銷比例的分母由收貨量改成**已上架量**，
        /// 而**沒有任何一支測試同時走過這兩條**——各自的測試裡退貨都發生在全部上架之後。
        ///
        /// 為什麼這個組合值得單獨釘：兩個修正都對，但它們讀的是**兩個不同的分母**。
        /// 退貨會讓「該認列多少」變小（#132 的上限），卻**不會**讓「已上架多少」變小
        /// （<c>postedReceiptEquiv</c> 只加總 Direction==1 的入庫異動，退貨出庫是 −1），
        /// 於是最後一批認列時 newQty 永遠追不上 postedReceiptEquiv，
        /// 「全額認列 → 完全沖平」那條捷徑走不到，只能落到按比例——比例卻是用
        /// 沒扣退貨的分母算的。
        ///
        /// 再加上 <c>alreadyCleared</c> 只認 <c>SourceDocType == "APInvoice"</c>，
        /// 而退貨回沖的分錄掛的是 <c>"APInvoiceReturn"</c>（同檔兩個常數），
        /// 那一腿對 2152 的回補在這裡是看不見的。
        ///
        /// 兩件事疊起來的後果就是下面兩個斷言要擋的：2152 沖不平、差額變成假性進料價差。
        /// 這條路徑在 `main` 上同樣不成立（不是合併造成的回歸），只是兩支 PR 都沒有覆蓋到。
        /// </summary>
        [Fact]
        public async Task ReturnBetweenPutAwayBatches_ClearsGrIrAndBooksNoPhantomVariance()
        {
            const decimal qty = 10m, price = 100m, taxRate = 0m;
            const decimal batch1 = 6m, rtnQty = 2m, batch2 = 4m;
            const string Grt = "FLOW-GRT-V";

            await CreateApprovedPoAsync(qty, price, taxRate);
            var grId = await CreateGoodsReceiptAsync(qty, price, taxRate);

            // ① 第一批上架並認列
            await PutAwayAsync(grId, batch1);
            using (var scope = NewScope(out _))
                await Svc<IAPInvoiceService>(scope).AccrueForReceivedQtyAsync(grId, 1, batch1);

            // ② 中途退貨：退貨出庫（借 2152）＋ 應付回沖（貸 2152）
            using (var scope = NewScope(out var db))
            {
                var grt = new GoodsReturn
                {
                    GoodsReturnId = Grt, ReturnDate = RecvDate, CreatedBy = UserId,
                    SupplierCode = Supplier, TypeCode = "PF", TypeCategory = "Purchase",
                    ApprovalStatus = DocumentApprovalStatus.Approved,
                    PlantId = Plant, CreatedByRoleId = RoleId,
                };
                grt.Lines.Add(new GoodsReturnLine
                {
                    GoodsReturnId = Grt, LineNo = 1, ProductCode = Mat,
                    GoodsReceiptId = grId, ReceiptLineNo = 1,
                    ReturnQty = rtnQty, UomCode = Uom, UnitPrice = price, TaxRate = taxRate,
                });
                db.GoodsReturns.Add(grt);
                await db.SaveChangesAsync();

                await Svc<IInventoryPostingService>(scope).PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                {
                    PlantId = Plant, DocNo = Grt, MovementDate = RecvDate,
                    TypeCode = "PF", TypeCategory = "Purchase", CreatedBy = UserId,
                    Lines = new List<SimplifiedPostLine>
                    {
                        new()
                        {
                            LineNo = 1, ProductCode = Mat, WhCode = Wh, LocationCode = Loc, LotNo = "",
                            Qty = rtnQty, UomCode = Uom, SetQtyPutAway = _ => { },
                        }
                    },
                    SetDocPosted = () => grt.Status = DocStatus.Posted,
                });
                await db.SaveChangesAsync();

                await Svc<IAPInvoiceService>(scope).ReverseForReturnedQtyAsync(grId, 1, rtnQty);
            }

            // ③ 剩下的量上架並認列（上限已被 #132 壓到 收貨量 − 已退貨量 = 8）
            await PutAwayAsync(grId, batch2);
            using (var scope = NewScope(out _))
                await Svc<IAPInvoiceService>(scope).AccrueForReceivedQtyAsync(grId, 1, batch2);

            using (var scope = NewScope(out var db))
            {
                var kept = qty - rtnQty;

                // 認列量收斂到「收貨量 − 已退貨量」，不多也不少
                var apLine = await db.APInvoiceLines.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(l => l.SourceLineNo == 1
                              && l.APInvoice!.SourceDocId == grId);
                Assert.Equal(kept, apLine.Qty);

                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);

                // 暫估應付必須歸零：入庫貸 10 批次、認列借 8、退貨借 2 → 0
                var grIr     = Net(gl, StandardAccounts.GrIrAPClearingCode);
                var variance = Net(gl, StandardAccounts.LandedCostVarianceCode);

                // 兩腿一起報，否則先紅的那一條會把另一條藏起來——這支測的正是「兩者是同一筆錢」
                Assert.True(grIr == 0m && variance == 0m,
                    $"2152 暫估應付結餘 {grIr}（應為 0），5151 進料費用差異 {variance}（應為 0）。"
                    + "收貨單價＝採購單價，全程沒有任何實質價差；沖不掉的暫估會等額變成假性價差。");

                // 存貨與應付都停在留存量
                Assert.Equal(kept * price, Net(gl, StandardAccounts.RawMaterialInventoryCode));
                Assert.Equal(-kept * price, Net(gl, StandardAccounts.AccountsPayableCode));
            }
        }
    }
}
