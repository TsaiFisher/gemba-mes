using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.ORD.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Modules.QCR.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 手機簽核摘要：簽核人在手機上看到的內容。
    ///
    /// <para><b>這些測試守的是「摘要說的是不是真話」</b>，不是「有沒有回東西」。
    /// 摘要的失效方式全都是安靜的——金額少一位、數量取錯欄位、明細漏掉幾行，
    /// 畫面上一樣是一張看起來正常的卡片，而簽核人會照著它按下核准。
    /// 這比 API 回 500 危險得多：500 至少有人會回報。</para>
    ///
    /// <para><b>最重要的一條是「沒有金額」與「金額是 0」必須分得出來。</b>
    /// 請購單與內部訂單**沒有金額欄位**（單價要到採購／出貨才決定），
    /// 而 <c>AmountLocal</c> 回 <c>0</c> 的話，畫面會顯示「0 元」——
    /// 那對簽核人是一句假話，而且是會影響決定的假話。</para>
    /// </summary>
    public class ApprovalSummaryProviderTests
    {
        private const string BaseCurrency = "TWD";

        /// <summary>本測試用的廠別。所有種下去的單據都掛這一廠。</summary>
        private const string Plant = "P1";

        /// <summary>
        /// ⚠️ <b><c>CurrentPlantId</c> 一定要設。</b>
        /// <c>ApplicationDbContext</c> 對 <c>IPlantEntity</c> 掛了全域查詢過濾
        /// （<c>PlantId == CurrentPlantId</c>），不設的話它是 <c>null</c>，
        /// 於是**只有 PlantId 為 null 的列查得到**——本測試第一版就是這樣：
        /// 沒設 PlantId 的那幾支綠、設了 <c>"P1"</c> 的四支紅，
        /// 而失敗訊息只是 <c>Assert.NotNull() Failure</c>，看不出是被過濾掉的。
        /// </summary>
        private static ApplicationDbContext NewDb()
        {
            var db = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options);
            db.CurrentPlantId = Plant;
            return db;
        }

        /// <summary>把鍵原樣回傳，讓斷言看得出用了哪個資源鍵（而不是被譯文遮住）。</summary>
        private sealed class KeyEchoLocalizer : IStringLocalizer<ProjectZSharedResource>
        {
            public LocalizedString this[string name] => new(name, name, false);
            public LocalizedString this[string name, params object[] arguments]
                => new(name, name + ":" + string.Join(",", arguments), false);
            public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
                => Array.Empty<LocalizedString>();
        }

        private static ICurrencyService Currency()
        {
            var m = new Mock<ICurrencyService>();
            m.Setup(c => c.GetBaseCurrencyAsync()).ReturnsAsync(BaseCurrency);
            return m.Object;
        }

        // ── 採購單：有金額、有明細、有往來對象 ───────────────────────────────

        [Fact]
        public async Task 採購單摘要取表頭本幣金額_不是把明細的交易幣加總()
        {
            // PurchaseOrder.TotalAmount 的實體註解逐字寫著「本位幣總金額（各明細 AmountLocal
            // 加總）。明細可為不同幣別，故表頭一律以本幣合計」。
            // 這裡刻意讓明細的 LineAmount（交易幣）與 AmountLocal（本幣）**不同**：
            // 一個「拿 LineAmount 加總」的實作會得到 300，而正確答案是表頭的 3300。
            using var db = NewDb();
            db.Companies.Add(new Company { CompanyCode = "S1", CompanyName = "供應商甲" });
            db.PurchaseOrders.Add(new PurchaseOrder
            {
                PurchaseOrderId = "PO-1", SupplierCode = "S1", PlantId = Plant,
                TotalAmount = 3300m, Remarks = "急件",
            });
            db.PurchaseOrderLines.AddRange(
                new PurchaseOrderLine { PurchaseOrderId = "PO-1", LineNo = 1, ProductCode = "A",
                                        OrderQty = 2, UomCode = "PCS", LineAmount = 100m, AmountLocal = 3000m },
                new PurchaseOrderLine { PurchaseOrderId = "PO-1", LineNo = 2, ProductCode = "B",
                                        OrderQty = 1, UomCode = "PCS", LineAmount = 200m, AmountLocal = 300m });
            await db.SaveChangesAsync();

            var s = await new PurchaseOrderApprovalSummary(new KeyEchoLocalizer(), Currency())
                .GetAsync(db, "PO-1");

            Assert.NotNull(s);
            Assert.Equal(3300m, s!.AmountLocal);
            Assert.Equal(BaseCurrency, s.CurrencyCode);   // 本位幣，不是某一行的交易幣
            Assert.Equal(Plant, s.PlantId);
            Assert.Equal("供應商甲", s.Counterparty);

            // 明細帶的是本幣金額，與表頭同一個口徑——混用會讓兩個數字加不起來。
            Assert.Equal(new decimal?[] { 3000m, 300m }, s.Lines.Select(l => l.Amount));
            Assert.Equal(new[] { "A", "B" }, s.Lines.Select(l => l.Description));
            Assert.Equal(2, s.TotalLineCount);

            // 供應商欄要看得出代碼**與**名稱：只給名稱時同名廠商分不出來，
            // 只給代碼則簽核人得自己記住 S1 是誰。
            var supplierRow = s.Rows.Single(r => r.Label == "SupplierCode");
            Assert.Contains("S1", supplierRow.Value, StringComparison.Ordinal);
            Assert.Contains("供應商甲", supplierRow.Value, StringComparison.Ordinal);
        }

        [Fact]
        public async Task 明細超過上限時要說得出還有幾筆()
        {
            // 靜靜只顯示前 20 筆，會讓簽核人以為整張單就這麼多——
            // 那是「摘要說了假話」最典型的一種。
            using var db = NewDb();
            db.PurchaseOrders.Add(new PurchaseOrder { PurchaseOrderId = "PO-2", SupplierCode = "S9", TotalAmount = 1m });
            for (int i = 1; i <= ApprovalSummary.MaxLines + 5; i++)
                db.PurchaseOrderLines.Add(new PurchaseOrderLine
                {
                    PurchaseOrderId = "PO-2", LineNo = i, ProductCode = $"P{i:00}", OrderQty = 1, AmountLocal = 1m,
                });
            await db.SaveChangesAsync();

            var s = await new PurchaseOrderApprovalSummary(new KeyEchoLocalizer(), Currency())
                .GetAsync(db, "PO-2");

            Assert.Equal(ApprovalSummary.MaxLines, s!.Lines.Count);
            Assert.Equal(ApprovalSummary.MaxLines + 5, s.TotalLineCount);
        }

        [Fact]
        public async Task 明細依行號排序_不是資料庫的插入順序()
        {
            // 亂序的明細會讓簽核人對不上紙本或桌機畫面，而那看起來像「資料錯了」。
            using var db = NewDb();
            db.PurchaseOrders.Add(new PurchaseOrder { PurchaseOrderId = "PO-3", SupplierCode = "S9", TotalAmount = 1m });
            foreach (var no in new[] { 3, 1, 2 })
                db.PurchaseOrderLines.Add(new PurchaseOrderLine
                {
                    PurchaseOrderId = "PO-3", LineNo = no, ProductCode = $"L{no}", OrderQty = 1, AmountLocal = 1m,
                });
            await db.SaveChangesAsync();

            var s = await new PurchaseOrderApprovalSummary(new KeyEchoLocalizer(), Currency())
                .GetAsync(db, "PO-3");

            Assert.Equal(new[] { "L1", "L2", "L3" }, s!.Lines.Select(l => l.Description));
        }

        // ── 沒有金額概念的單別 ───────────────────────────────────────────────

        [Fact]
        public async Task 請購單沒有金額欄位_回null而不是0()
        {
            // 🚨 這一條是整組測試裡最重要的。PurchaseRequest 上沒有 TotalAmount，
            //    明細只有數量沒有單價（採購價要到採購單才決定）。
            //    回 0 的話畫面會顯示「0 元」——那對簽核人是一句**會影響決定**的假話，
            //    而且「金額 0 元的採購單」與請購單在畫面上就分不出來了。
            using var db = NewDb();
            db.PurchaseRequests.Add(new PurchaseRequest
            {
                PurchaseRequestId = "PR-1", RequestedBy = "王小明", PlantId = Plant, Remarks = "產線急用",
            });
            db.PurchaseRequestLines.Add(new PurchaseRequestLine
            {
                PurchaseRequestId = "PR-1", LineNo = 1, ProductCode = "A", RequestQty = 5, UomCode = "KG",
            });
            await db.SaveChangesAsync();

            var s = await new PurchaseRequestApprovalSummary(new KeyEchoLocalizer()).GetAsync(db, "PR-1");

            Assert.NotNull(s);
            Assert.Null(s!.AmountLocal);          // ← 不是 0
            Assert.Null(s.CurrencyCode);
            Assert.Null(s.Counterparty);          // 請購沒有往來對象，供應商要到採購單才決定
            Assert.Null(s.Lines.Single().Amount); // 明細也一樣沒有金額
            Assert.Equal(5m, s.Lines.Single().Qty);
            Assert.Equal("KG", s.Lines.Single().UomCode);
        }

        [Fact]
        public async Task 內部訂單同樣沒有金額也沒有往來對象()
        {
            // 反面對照的另一半：兩個「沒有金額」的單別走的是不同實作
            // （InternalOrder 刻意不繼承客戶單據基底），兩邊都要驗。
            using var db = NewDb();
            db.InternalOrders.Add(new InternalOrder
            {
                InternalOrderId = "IO-1", DepartmentCode = "D1", PlantId = Plant, Remarks = "內部領用",
            });
            db.InternalOrderLines.Add(new InternalOrderLine
            {
                InternalOrderId = "IO-1", LineNo = 1, ProductCode = "X", OrderQty = 3, UomCode = "PCS",
            });
            await db.SaveChangesAsync();

            var s = await new InternalOrderApprovalSummary(new KeyEchoLocalizer()).GetAsync(db, "IO-1");

            Assert.Null(s!.AmountLocal);
            Assert.Null(s.Counterparty);
            Assert.Contains(s.Rows, r => r.Label == "DepartmentCode" && r.Value == "D1");
        }

        // ── 客戶端單據：標籤必須是客戶不是供應商 ─────────────────────────────

        [Fact]
        public async Task 訂單的往來對象標籤是客戶不是供應商()
        {
            // Companies 是同一張表（以 SupplierType 區分 S／C），所以查名稱的寫法一樣，
            // 但標籤用錯的話簽核人會把客戶看成供應商——那是兩件完全不同的事，
            // 而畫面上不會有任何跡象顯示標錯了。
            using var db = NewDb();
            db.Companies.Add(new Company { CompanyCode = "C1", CompanyName = "客戶乙" });
            db.SalesOrders.Add(new SalesOrder
            {
                SalesOrderId = "SO-1", CompanyCode = "C1", PlantId = Plant, TotalAmount = 500m,
            });
            db.SalesOrderLines.Add(new SalesOrderLine
            {
                SalesOrderId = "SO-1", LineNo = 1, ProductCode = "A", OrderQty = 2, UomCode = "PCS", AmountLocal = 500m,
            });
            await db.SaveChangesAsync();

            var s = await new SalesOrderApprovalSummary(new KeyEchoLocalizer(), Currency()).GetAsync(db, "SO-1");

            Assert.Contains(s!.Rows, r => r.Label == "CustomerCode");
            Assert.DoesNotContain(s.Rows, r => r.Label == "SupplierCode");
            Assert.Equal("客戶乙", s.Counterparty);
        }

        [Theory]
        [InlineData("GoodsShipment")]
        [InlineData("SalesReturn")]
        [InlineData("GoodsReceipt")]
        [InlineData("GoodsReturn")]
        [InlineData("OutsourceReceipt")]
        public async Task 出貨退回進貨的數量取的是各自的欄位(string docType)
        {
            // 這四張單共用基底，而**數量欄位的名字各不相同**
            // （ShipQty／ReturnQty／ReceiptQty／ReturnQty）。
            // 共用實作最容易錯的就是這裡：抄錯一個欄位名，數量會變成 0 或另一個欄位的值，
            // 而摘要照樣長得正常。所以每一張單都要各自驗一次。
            using var db = NewDb();
            db.Companies.Add(new Company { CompanyCode = "X1", CompanyName = "對象" });

            const decimal expectedQty = 7m;
            IApprovalSummaryProvider provider;

            switch (docType)
            {
                case "GoodsShipment":
                    db.GoodsShipments.Add(new GoodsShipment { GoodsShipmentId = "D1", CompanyCode = "X1", TotalAmount = 9m });
                    db.GoodsShipmentLines.Add(new GoodsShipmentLine { GoodsShipmentId = "D1", LineNo = 1, ProductCode = "A", ShipQty = expectedQty, AmountLocal = 9m });
                    provider = new GoodsShipmentApprovalSummary(new KeyEchoLocalizer(), Currency());
                    break;
                case "SalesReturn":
                    db.SalesReturns.Add(new SalesReturn { SalesReturnId = "D1", CompanyCode = "X1", TotalAmount = 9m });
                    db.SalesReturnLines.Add(new SalesReturnLine { SalesReturnId = "D1", LineNo = 1, ProductCode = "A", ReturnQty = expectedQty, AmountLocal = 9m });
                    provider = new SalesReturnApprovalSummary(new KeyEchoLocalizer(), Currency());
                    break;
                case "GoodsReceipt":
                    db.GoodsReceipts.Add(new GoodsReceipt { GoodsReceiptId = "D1", SupplierCode = "X1", TotalAmount = 9m });
                    db.GoodsReceiptLines.Add(new GoodsReceiptLine { GoodsReceiptId = "D1", LineNo = 1, ProductCode = "A", ReceiptQty = expectedQty, AmountLocal = 9m });
                    provider = new GoodsReceiptApprovalSummary(new KeyEchoLocalizer(), Currency());
                    break;
                case "OutsourceReceipt":
                    db.OutsourceReceipts.Add(new OutsourceReceipt { OutsourceReceiptId = "D1", SupplierCode = "X1", TotalAmount = 9m });
                    db.OutsourceReceiptLines.Add(new OutsourceReceiptLine { OutsourceReceiptId = "D1", LineNo = 1, ProductCode = "A", ReceiptQty = expectedQty, AmountLocal = 9m });
                    provider = new OutsourceReceiptApprovalSummary(new KeyEchoLocalizer(), Currency());
                    break;
                default:
                    db.GoodsReturns.Add(new GoodsReturn { GoodsReturnId = "D1", SupplierCode = "X1", TotalAmount = 9m });
                    db.GoodsReturnLines.Add(new GoodsReturnLine { GoodsReturnId = "D1", LineNo = 1, ProductCode = "A", ReturnQty = expectedQty, AmountLocal = 9m });
                    provider = new GoodsReturnApprovalSummary(new KeyEchoLocalizer(), Currency());
                    break;
            }
            await db.SaveChangesAsync();

            var s = await provider.GetAsync(db, "D1");

            Assert.NotNull(s);
            Assert.Equal(expectedQty, s!.Lines.Single().Qty);
            Assert.Equal(9m, s.AmountLocal);
        }

        // ── 工單：金額必須與「為什麼要簽」是同一個數字 ───────────────────────

        [Fact]
        public async Task 工單金額是標準料工費三段合計_與送審門檻比的是同一個數字()
        {
            // 🚨 WorkOrderReleaseService 投產時算出 (mat, lab, ovh)，拿 **mat + lab + ovh**
            //    去和 mfg_wo_approval_threshold 比，超過才送審（§18.2 快速通道）。
            //    摘要顯示別的數字——只顯示料成本、或顯示實際成本——會讓簽核人看到的金額
            //    與「這張單為什麼會出現在我清單裡」對不起來，而那種不一致沒有跡象可循。
            //
            //    這裡刻意把 Actual* 設成與 Standard* 完全不同的值：
            //    一個「拿實際成本」的實作會得到 999，正確答案是 60。
            using var db = NewDb();
            db.Products.Add(new Product { ProductCode = "FG1", ProductName = "成品甲" });
            db.WorkOrders.Add(new WorkOrder
            {
                WorkOrderId = "WO-1", ProductCode = "FG1", PlantId = Plant, Quantity = 10m,
                StandardMaterialCost = 30m, StandardLaborCost = 20m, StandardOverheadCost = 10m,
                ActualMaterialCost = 900m, ActualLaborCost = 60m, ActualOverheadCost = 39m,
            });
            db.WorkOrderMaterials.AddRange(
                new WorkOrderMaterial { WorkOrderId = "WO-1", OperationSeq = 2, BomSeq = 1,
                                        ProductCode = "RM2", RequiredQty = 3m, UomCode = "PCS", UnitCost = 5m },
                new WorkOrderMaterial { WorkOrderId = "WO-1", OperationSeq = 1, BomSeq = 1,
                                        ProductCode = "RM1", RequiredQty = 2m, UomCode = "KG", UnitCost = 7m });
            await db.SaveChangesAsync();

            var s = await new WorkOrderApprovalSummary(new KeyEchoLocalizer(), Currency())
                .GetAsync(db, "WO-1");

            Assert.NotNull(s);
            Assert.Equal(60m, s!.AmountLocal);          // 30 + 20 + 10，不是 999
            Assert.Equal(BaseCurrency, s.CurrencyCode);
            Assert.Equal("成品甲", s.Counterparty);      // 工單沒有往來對象，放產品當識別

            // 投料明細依工序序號排序——與桌機看到的順序一致，否則簽核人對不上。
            Assert.Equal(new[] { "RM1", "RM2" }, s.Lines.Select(l => l.Description));

            // 明細金額＝需求量 × 標準單價（2×7=14、3×5=15）。
            Assert.Equal(new decimal?[] { 14m, 15m }, s.Lines.Select(l => l.Amount));
        }

        [Fact]
        public async Task 工單沒有投料時仍給得出摘要()
        {
            // 反面對照：一個「明細空的就回 null」的實作會讓上面那支照樣綠，
            // 而那種實作的症狀是「這張工單在手機上簽不了」，看起來像單別不支援。
            using var db = NewDb();
            db.WorkOrders.Add(new WorkOrder
            {
                WorkOrderId = "WO-2", ProductCode = "FG9", PlantId = Plant, Quantity = 1m,
                StandardMaterialCost = 1m, StandardLaborCost = 1m, StandardOverheadCost = 1m,
            });
            await db.SaveChangesAsync();

            var s = await new WorkOrderApprovalSummary(new KeyEchoLocalizer(), Currency())
                .GetAsync(db, "WO-2");

            Assert.NotNull(s);
            Assert.Empty(s!.Lines);
            Assert.Equal(0, s.TotalLineCount);
            Assert.Equal(3m, s.AmountLocal);
            Assert.Equal("FG9", s.Counterparty);   // 查不到品名時退回品號，不是留空
        }

        // ── 供應商評鑑：內容是分數，不是金額 ─────────────────────────────────

        [Fact]
        public async Task 評鑑的分數不得塞進金額欄位()
        {
            // 🚨 AmountLocal 在畫面上會被格式化成金額（帶幣別、千分位）——
            //    把 TotalScore 塞進去，簽核人看到的會是「TWD 78」。78 分不是 78 元。
            //    分數要走 Rows，而 AmountLocal 必須是 null（＝這張單沒有金額概念）。
            using var db = NewDb();
            db.Companies.Add(new Company { CompanyCode = "V1", CompanyName = "供應商丙" });
            db.Set<VendorEvaluation>().Add(new VendorEvaluation
            {
                Id = 42, SupplierCode = "V1", EvaluationYear = 2026, EvaluationQuarter = 3,
                QualityScore = 20m, DeliveryScore = 18m, PriceScore = 20m, ServiceScore = 20m,
                TotalScore = 78m, ResultStatus = EvaluationResult.Conditional,
                Evaluator = "u1", FollowUpAction = "限期改善交期",
            });
            await db.SaveChangesAsync();

            var s = await new VendorEvaluationApprovalSummary(new KeyEchoLocalizer()).GetAsync(db, "42");

            Assert.NotNull(s);
            Assert.Null(s!.AmountLocal);       // ← 78 分不是 78 元
            Assert.Null(s.CurrencyCode);
            Assert.Empty(s.Lines);             // 評鑑是單一張評分卡，沒有明細

            // 四個構面與總分都要看得到——只給總分的話，簽核人無從判斷是哪一項拉低了。
            Assert.Contains(s.Rows, r => r.Label == "ColQuality"  && r.Value == "20");
            Assert.Contains(s.Rows, r => r.Label == "ColDelivery" && r.Value == "18");
            Assert.Contains(s.Rows, r => r.Label == "ColTotal"    && r.Value == "78");

            // 結果要譯過，不是印列舉名（Conditional 對使用者沒有意義）。
            Assert.Contains(s.Rows, r => r.Label == "ColResult" && r.Value == "EvalResultConditional");

            // 期間：有季別就要帶上，否則年度評鑑與季評鑑分不出來。
            Assert.Contains(s.Rows, r => r.Label == "ColPeriod" && r.Value == "2026 Q3");

            // Fail／Conditional 的改善措施是簽核判斷的關鍵，必須顯示。
            Assert.Contains(s.Rows, r => r.Label == "FollowUpAction" && r.Value == "限期改善交期");
        }

        [Fact]
        public async Task 年度評鑑沒有季別時期間不得多出一個Q()
        {
            using var db = NewDb();
            db.Set<VendorEvaluation>().Add(new VendorEvaluation
            {
                Id = 43, SupplierCode = "V9", EvaluationYear = 2026, EvaluationQuarter = null,
                TotalScore = 90m, ResultStatus = EvaluationResult.Pass, Evaluator = "u1",
            });
            await db.SaveChangesAsync();

            var s = await new VendorEvaluationApprovalSummary(new KeyEchoLocalizer()).GetAsync(db, "43");

            Assert.Equal("2026", s!.Rows.Single(r => r.Label == "ColPeriod").Value);
        }

        [Fact]
        public async Task 評鑑的documentId不是數字時回null而不是拋例外()
        {
            // documentId 是 VendorEvaluation.Id（int）字串化。拋例外的話整支摘要 API 會回 500，
            // 而症狀看起來像手機簽核整個壞掉——實際上只是這一張單的 id 形狀不對。
            using var db = NewDb();

            Assert.Null(await new VendorEvaluationApprovalSummary(new KeyEchoLocalizer()).GetAsync(db, "not-an-int"));
            Assert.Null(await new VendorEvaluationApprovalSummary(new KeyEchoLocalizer()).GetAsync(db, ""));
        }

        // ── 發票：金額語意與採購／訂單相反 ───────────────────────────────────

        [Fact]
        public async Task 外幣發票的金額取本幣欄位_不是交易幣的TotalAmount()
        {
            // 🚨 這一條守的是本批最容易犯、也最看不出來的錯。
            //    採購單／訂單那六張的表頭 TotalAmount **已經是本幣**，
            //    但發票的 TotalAmount 是「含稅總額（**交易幣**）」，AmountLocal 才是本幣。
            //    照抄那組基底（AmountLocal: head.TotalAmount）的話，
            //    一張 USD 1,200 的發票會顯示成「TWD 1,200」——數字小一個數量級以上，
            //    而畫面上完全看不出來，簽核人只會覺得「這張怎麼這麼便宜」。
            using var db = NewDb();
            db.Companies.Add(new Company { CompanyCode = "S1", CompanyName = "供應商甲" });
            db.APInvoices.Add(new APInvoice
            {
                APInvoiceId = "AP-1", SupplierCode = "S1", PlantId = Plant,
                CurrencyCode = "USD", ExchangeRate = 32m,
                TotalAmount = 1200m,      // 交易幣
                AmountLocal = 38400m,     // 本幣（1200 × 32）
            });
            db.APInvoiceLines.Add(new APInvoiceLine
            {
                APInvoiceId = "AP-1", LineNo = 1, ProductCode = "A", Qty = 2, UomCode = "PCS",
                LineAmount = 600m,        // 明細也是交易幣
            });
            await db.SaveChangesAsync();

            var s = await new APInvoiceApprovalSummary(new KeyEchoLocalizer(), Currency()).GetAsync(db, "AP-1");

            Assert.NotNull(s);
            Assert.Equal(38400m, s!.AmountLocal);       // ← 不是 1200
            Assert.Equal(BaseCurrency, s.CurrencyCode);

            // 明細乘過匯率，與表頭同一個口徑——混用會讓明細加總對不上表頭。
            Assert.Equal(600m * 32m, s.Lines.Single().Amount);

            // 外幣發票要另外列出交易幣金額：簽核人記得的是「USD 1,200」，
            // 只給換算後的本幣會讓他對不上手上的單據。
            Assert.Contains(s.Rows, r => r.Label == "TotalAmount" && r.Value.Contains("USD"));
        }

        [Fact]
        public async Task 本幣發票不重複列出交易幣金額()
        {
            // 反面對照：幣別等於本位幣時列出來只是同一個數字寫兩次，
            // 而多一列雜訊會讓真正重要的欄位被擠下去。
            using var db = NewDb();
            db.ARInvoices.Add(new ARInvoice
            {
                ARInvoiceId = "AR-1", CustomerCode = "C1", PlantId = Plant,
                CurrencyCode = BaseCurrency, ExchangeRate = 1m,
                TotalAmount = 500m, AmountLocal = 500m,
            });
            await db.SaveChangesAsync();

            var s = await new ARInvoiceApprovalSummary(new KeyEchoLocalizer(), Currency()).GetAsync(db, "AR-1");

            Assert.Equal(500m, s!.AmountLocal);
            Assert.DoesNotContain(s.Rows, r => r.Label == "TotalAmount");

            // 應收用客戶標籤，應付用供應商——兩者共用基底，標籤是唯一的差別。
            Assert.Contains(s.Rows, r => r.Label == "CustomerCode");
        }

        [Fact]
        public async Task 手開發票的明細沒有品號時退回行號()
        {
            // 非採購性費用的手開發票明細可以沒有品號。留空的話畫面上是一列
            // 看不出是什麼的資料，而簽核人無從判斷。
            using var db = NewDb();
            db.APInvoices.Add(new APInvoice
            {
                APInvoiceId = "AP-2", SupplierCode = "S9", PlantId = Plant,
                CurrencyCode = BaseCurrency, ExchangeRate = 1m, TotalAmount = 100m, AmountLocal = 100m,
            });
            db.APInvoiceLines.Add(new APInvoiceLine
            {
                APInvoiceId = "AP-2", LineNo = 7, ProductCode = null, Qty = 1, LineAmount = 100m,
            });
            await db.SaveChangesAsync();

            var s = await new APInvoiceApprovalSummary(new KeyEchoLocalizer(), Currency()).GetAsync(db, "AP-2");

            Assert.Equal("#7", s!.Lines.Single().Description);
        }

        // ── NCR：沒有金額，處置是關鍵 ────────────────────────────────────────

        [Fact]
        public async Task NCR沒有金額_處置方式必須顯示()
        {
            // NCR 上沒有任何金額欄位——處置的財務後果發生在**下游單據**上
            // （報廢單／重工工單／採購退貨），那些單各自另有簽核。
            // 在這裡湊一個金額，等於把下游的決定提前塞進這一張。
            using var db = NewDb();
            db.Products.Add(new Product { ProductCode = "P1", ProductName = "料件甲" });
            db.NonConformanceReports.Add(new NonConformanceReport
            {
                NcrId = "NCR-1", PlantId = Plant, ProductCode = "P1", Qty = 12m, UomCode = "PCS",
                Disposition = "Rework", DefectCode = "D01", LotNo = "L001",
                RootCause = "模具磨耗", CorrectiveAction = "更換模具並增加點檢",
            });
            await db.SaveChangesAsync();

            var s = await new NcrApprovalSummary(new KeyEchoLocalizer()).GetAsync(db, "NCR-1");

            Assert.NotNull(s);
            Assert.Null(s!.AmountLocal);
            Assert.Empty(s.Lines);

            // 處置是簽核人真正在核准的東西——報廢、重工、退貨的後果完全不同。
            Assert.Contains(s.Rows, r => r.Label == "Disposition" && r.Value == "Rework");
            Assert.Contains(s.Rows, r => r.Label == "RootCause");
            Assert.Contains(s.Rows, r => r.Label == "CorrectiveAction");
            Assert.Contains(s.Rows, r => r.Label == "LotNo" && r.Value == "L001");

            // 數量要帶單位：12 個與 12 公斤是完全不同的一件事。
            Assert.Contains(s.Rows, r => r.Label == "Qty" && r.Value == "12 PCS");
        }

        // ── 查不到單據 ───────────────────────────────────────────────────────

        [Fact]
        public async Task 查不到單據時回null_呼叫端才分得出與空白摘要的差別()
        {
            // 單據已刪除，或被全域廠別／資料權限過濾擋掉。
            // 回一個空的 ApprovalSummary 的話，手機會顯示成一張空白卡片配一顆核准鈕——
            // 那正是整個設計要防的事。
            using var db = NewDb();

            Assert.Null(await new PurchaseOrderApprovalSummary(new KeyEchoLocalizer(), Currency()).GetAsync(db, "NOPE"));
            Assert.Null(await new PurchaseRequestApprovalSummary(new KeyEchoLocalizer()).GetAsync(db, "NOPE"));
            Assert.Null(await new SalesOrderApprovalSummary(new KeyEchoLocalizer(), Currency()).GetAsync(db, "NOPE"));
            Assert.Null(await new InternalOrderApprovalSummary(new KeyEchoLocalizer()).GetAsync(db, "NOPE"));
            Assert.Null(await new WorkOrderApprovalSummary(new KeyEchoLocalizer(), Currency()).GetAsync(db, "NOPE"));
        }

        [Fact]
        public async Task 找不到往來對象名稱時退回代碼_不是留空()
        {
            // 主檔被刪或代碼打錯時，簽核人至少還看得到代碼。
            // 留空的話那一欄會消失，而「這張單沒有供應商」與「查不到名稱」是兩件事。
            using var db = NewDb();
            db.PurchaseOrders.Add(new PurchaseOrder { PurchaseOrderId = "PO-9", SupplierCode = "GHOST", TotalAmount = 1m });
            await db.SaveChangesAsync();

            var s = await new PurchaseOrderApprovalSummary(new KeyEchoLocalizer(), Currency()).GetAsync(db, "PO-9");

            Assert.Equal("GHOST", s!.Counterparty);
            Assert.Equal("GHOST", s.Rows.Single(r => r.Label == "SupplierCode").Value);
        }
    }
}
