using System.Net;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 2026-10-27 補齊的 18 張作業單據列印（共用版型 _PrintDocument）。
    ///
    /// 每張單各種一筆帶品號／往來對象／幣別的資料，兩個語系各印一次，斷言：
    ///   ① 200（不是 500／404——CrossPlantTransfer 原本有 Print action 卻沒有 View，一按就 500）；
    ///   ② 代碼旁邊印得出主檔名稱（品名、往來對象名稱）——這是這次補列印的需求本身；
    ///   ③ 英文介面沒有漏出 resx 鍵名（<c>Print_</c> 開頭的字樣）——鍵少一個語系時
    ///      IStringLocalizer 回鍵名而不是丟例外，畫面不會壞、只是印出 Print_Xxx。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class DocumentPrintCoverageTests : PostingResidueTestBase
    {
        public DocumentPrintCoverageTests(AuthedScheduleFactory factory) : base(factory) { }

        private const string P = "PRN-";
        private const string Uom = "PRN-EA";
        private const string Ccy = "PRN";
        private const string Product = "PRN-P01";
        private const string ProductName = "列印補齊測試品名";
        private const string Company = "PRN-CO";
        private const string CompanyName = "列印補齊測試往來商";
        private const string Wh = "PRN-W1";
        private const string WhName = "列印補齊測試倉";
        private const string Wo = "PRN-WO-01";
        private static readonly DateOnly Day = new(2097, 7, 1);

        /// <summary>(語系, 網址, 這張單有沒有往來對象)</summary>
        public static TheoryData<string, string, bool> Docs()
        {
            var d = new TheoryData<string, string, bool>();
            foreach (var culture in new[] { "zh-TW", "en-US" })
            {
                d.Add(culture, $"/OutsourcePurchaseOrder/Print/{P}OPO-01", true);
                d.Add(culture, $"/OutsourceReceipt/Print/{P}OR-01", true);
                d.Add(culture, $"/OutsourceRework/Print/{P}ORW-01", true);
                d.Add(culture, $"/MaterialSlip/Print/{P}MS-01", true);
                d.Add(culture, $"/Dispatch/Print/{P}DP-01", false);
                d.Add(culture, $"/InventoryCount/Print/{P}IC-01", false);
                d.Add(culture, $"/LandedCostAllocation/Print/{P}LC-01", false);
                d.Add(culture, $"/MaterialStaging/Print/{P}STG-01", false);
                d.Add(culture, $"/CrossPlantTransfer/Print/{P}CPT-01", false);
                d.Add(culture, $"/InspectionOrder/Print/{P}IO-01", false);
                d.Add(culture, $"/Ncr/Print/{P}NCR-01", false);
                d.Add(culture, $"/JournalEntry/Print/{P}JE-01", false);
                d.Add(culture, $"/ARReceipt/Print/{P}RC-01", true);
                d.Add(culture, $"/APPayment/Print/{P}PY-01", true);
                d.Add(culture, $"/ARInvoice/Print/{P}ARI-01", true);
                d.Add(culture, $"/APInvoice/Print/{P}API-01", true);
                d.Add(culture, $"/ARCreditNote/Print/{P}CN-01", true);
                d.Add(culture, $"/APDebitNote/Print/{P}DN-01", true);
            }
            return d;
        }

        [Theory]
        [MemberData(nameof(Docs))]
        public async Task Print_Renders_WithMasterNames_InBothLanguages(string culture, string url, bool hasCompany)
        {
            await SeedAllAsync();
            var (client, _) = await NewAuthedClientAsync();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Cookie", $".AspNetCore.Culture=c={culture}|uic={culture}");
            var res = await client.SendAsync(req);
            var html = await res.Content.ReadAsStringAsync();

            Assert.True(res.StatusCode == HttpStatusCode.OK, $"{url} [{culture}] → {(int)res.StatusCode}");
            // Razor 會把中文編成 &#x...; —— 以解碼後的文字比對
            var text = WebUtility.HtmlDecode(html);
            if (url.StartsWith("/JournalEntry"))
                // 科目名稱依語系：英文介面取 AccountNameEn
                Assert.Contains(culture == "en-US" ? "Print Debit Account" : "列印補齊借方科目", text);
            else if (url.StartsWith("/ARReceipt") || url.StartsWith("/APPayment"))
                // 收付款沒有品號；明細是沖銷的發票
                Assert.Contains(url.StartsWith("/AR") ? $"{P}ARI-01" : $"{P}API-01", text);
            else
                Assert.Contains(ProductName, text);
            if (hasCompany)
                Assert.Contains(CompanyName, text);
            Assert.DoesNotContain("Print_", text);
        }

        /// <summary>帳面數不印在盤點表上（盤點人照抄帳面數就失去盤點的意義）。</summary>
        [Fact]
        public async Task InventoryCountPrint_DoesNotShowBookQty()
        {
            await SeedAllAsync();
            var (client, _) = await NewAuthedClientAsync();
            var text = WebUtility.HtmlDecode(await client.GetStringAsync($"/InventoryCount/Print/{P}IC-01"));
            Assert.DoesNotContain("987.65", text);
        }

        /// <summary>跨廠調撥兩端的倉庫各依自己的廠別查名：收貨廠的倉在本廠查不到，不能只印代碼。</summary>
        [Fact]
        public async Task CrossPlantTransferPrint_ShowsDestinationPlantWarehouseName()
        {
            await SeedAllAsync();
            var (client, _) = await NewAuthedClientAsync();
            var text = WebUtility.HtmlDecode(await client.GetStringAsync($"/CrossPlantTransfer/Print/{P}CPT-01"));
            Assert.Contains("列印補齊收貨倉", text);
            Assert.Contains(WhName, text);
        }

        /// <summary>兩位核准者：先簽的印「審核」、最後一位印「核准」；較早被駁回那一輪的簽核人不印。</summary>
        [Fact]
        public async Task SignArea_TwoApprovers_LastIsApproved_EarlierRoundIgnored()
        {
            await SeedAllAsync();
            var sign = await SignAreaAsync($"/MaterialStaging/Print/{P}STG-01");
            Assert.Equal("admin", sign["製作"]);
            Assert.Equal("prn.reviewer", sign["審核"]);
            Assert.Equal("admin", sign["核准"]);
            Assert.DoesNotContain("prn.old", string.Join("|", sign.Values));
        }

        /// <summary>只有一位核准者 → 印在「核准」，「審核」留白。</summary>
        [Fact]
        public async Task SignArea_SingleApprover_GoesToApproved()
        {
            await SeedAllAsync();
            var sign = await SignAreaAsync($"/Ncr/Print/{P}NCR-01");
            Assert.Equal("", sign["審核"]);
            Assert.Equal("admin", sign["核准"]);
        }

        /// <summary>簽核中：已核准者印「審核」、「核准」留白；工單沒有建單人欄位 → 製作取送審人。</summary>
        [Fact]
        public async Task SignArea_Pending_ApprovedLeftBlank_CreatorFromRequester()
        {
            await SeedAllAsync();
            var sign = await SignAreaAsync($"/WorkOrder/Print/{Wo}");
            Assert.Equal("admin", sign["製作"]);
            Assert.Equal("admin", sign["審核"]);
            Assert.Equal("", sign["核准"]);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", sign["日期"]);
        }

        /// <summary>工單列印改印各工序需領料（待發量＝需求−已發＋已退），不再印成本。</summary>
        [Fact]
        public async Task WorkOrderPrint_ShowsMaterialsToIssue_NotCosts()
        {
            await SeedAllAsync();
            var (client, _) = await NewAuthedClientAsync();
            var text = WebUtility.HtmlDecode(await client.GetStringAsync($"/WorkOrder/Print/{Wo}"));
            Assert.Contains("需領料", text);
            Assert.Contains("PRN-MAT", text);
            Assert.Contains("列印補齊用料", text);
            // 待發 8 − 3 + 1 = 6。小數位數取單位精度快取——共用測試庫在本測試種單位之前就載過快取時退回 4 位，
            // 只認數值、不認位數（2026-10-28 全新空庫全量跑時出現 6.0000）
            Assert.Matches(@"<td class=""text-end"">6(\.0+)?</td>", text);
            Assert.DoesNotContain("12,345.67", text);                    // 標準材料成本不再印
        }

        /// <summary>取簽核區四格（標籤 → 內容）。</summary>
        private async Task<Dictionary<string, string>> SignAreaAsync(string url)
        {
            var (client, _) = await NewAuthedClientAsync();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Cookie", ".AspNetCore.Culture=c=zh-TW|uic=zh-TW");
            var html = WebUtility.HtmlDecode(await (await client.SendAsync(req)).Content.ReadAsStringAsync());
            var cells = System.Text.RegularExpressions.Regex.Matches(html,
                @"<div class=""print-field-label"">([^<]*)</div><div class=""print-sign-line[^""]*"">([^<]*)</div>");
            Assert.True(cells.Count == 4, $"{url} 簽核區應有四格，實際 {cells.Count}");
            return cells.ToDictionary(m => m.Groups[1].Value.Trim(), m => m.Groups[2].Value.Trim());
        }

        // ── 佈景 ────────────────────────────────────────────────────────────

        private static readonly SemaphoreSlim SeedLock = new(1, 1);
        private static bool _seeded;

        private async Task SeedAllAsync()
        {
            await SeedLock.WaitAsync();
            try
            {
                if (_seeded) return;
                using var scope = NewScope(out var db);
                await PurgeAsync(db);
                await SeedMastersAsync(db);
                SeedDocs(db);
                await db.SaveChangesAsync();
                _seeded = true;
            }
            finally { SeedLock.Release(); }
        }

        private static async Task SeedMastersAsync(SmartFactory.MES.Data.ApplicationDbContext db)
        {
            if (!await db.Uoms.IgnoreQueryFilters().AnyAsync(x => x.UomCode == Uom))
                db.Uoms.Add(new Uom { UomCode = Uom, UomName = "個", DecimalPlaces = 2 });
            if (!await db.Currencies.IgnoreQueryFilters().AnyAsync(x => x.CurrencyCode == Ccy))
                db.Currencies.Add(new Currency { CurrencyCode = Ccy, CurrencyName = "列印幣", DecimalPlaces = 2, IsActive = true });
            if (!await db.Products.IgnoreQueryFilters().AnyAsync(x => x.ProductCode == Product))
                db.Products.Add(new Product
                {
                    ProductCode = Product, ProductName = ProductName,
                    PurchaseUom = Uom, StockUom = Uom, SalesUom = Uom, IsActive = true,
                });
            if (!await db.Products.IgnoreQueryFilters().AnyAsync(x => x.ProductCode == "PRN-MAT"))
                db.Products.Add(new Product
                {
                    ProductCode = "PRN-MAT", ProductName = "列印補齊用料",
                    PurchaseUom = Uom, StockUom = Uom, SalesUom = Uom, IsActive = true,
                });
            if (!await db.Companies.IgnoreQueryFilters().AnyAsync(x => x.CompanyCode == Company))
                db.Companies.Add(new Company
                {
                    CompanyCode = Company, CompanyName = CompanyName, IsSupplier = true, IsCustomer = true, IsActive = true,
                });
            foreach (var (code, zh, en, type) in new[]
            {
                ("9PRN1", "列印補齊借方科目", "Print Debit Account", AccountType.Asset),
                ("9PRN2", "列印補齊貸方科目", "Print Credit Account", AccountType.Revenue),
            })
                if (!await db.Set<ChartOfAccount>().AnyAsync(x => x.AccountCode == code))
                    db.Set<ChartOfAccount>().Add(new ChartOfAccount
                    {
                        AccountCode = code, AccountName = zh, AccountNameEn = en, AccountType = type,
                        AllowManualEntry = true, IsActive = true,
                    });
            if (!await db.Plants.AnyAsync(x => x.Id == "PRN2"))
                db.Plants.Add(new Plant { Id = "PRN2", Name = "列印補齊二廠" });
            if (!await db.Warehouses.IgnoreQueryFilters().AnyAsync(x => x.WhCode == Wh && x.PlantId == Plant))
                db.Warehouses.Add(new Warehouse { WhCode = Wh, WhName = WhName, PlantId = Plant, IsActive = true });
            if (!await db.Warehouses.IgnoreQueryFilters().AnyAsync(x => x.WhCode == "PRN-W2" && x.PlantId == "PRN2"))
                db.Warehouses.Add(new Warehouse { WhCode = "PRN-W2", WhName = "列印補齊收貨倉", PlantId = "PRN2", IsActive = true });
            await db.SaveChangesAsync();
        }

        private static void SeedDocs(SmartFactory.MES.Data.ApplicationDbContext db)
        {
            const string By = "admin";
            var adminId = db.Users.Where(u => u.UserName == "admin").Select(u => u.Id).First();
            foreach (var (uid, uname) in new[] { ("PRN-U1", "prn.reviewer"), ("PRN-OLD", "prn.old") })
                if (!db.Users.Any(u => u.Id == uid))
                    db.Users.Add(new ApplicationUser
                    {
                        Id = uid, UserName = uname, NormalizedUserName = uname.ToUpperInvariant(),
                        SecurityStamp = Guid.NewGuid().ToString(),
                    });
            if (!db.ApprovalSteps.Any(x => x.Id == "PRN-STEP"))
                db.ApprovalSteps.Add(new ApprovalStep { Id = "PRN-STEP", Sequence = 1 });
            SeedApproval(db, "PRN-AR-OLD", $"{P}STG-01", ApprovalStatus.Rejected, adminId, -2, ("PRN-OLD", -2));
            SeedApproval(db, "PRN-AR-STG", $"{P}STG-01", ApprovalStatus.Approved, adminId, -1, ("PRN-U1", -1), (adminId, 0));
            SeedApproval(db, "PRN-AR-NCR", $"{P}NCR-01", ApprovalStatus.Approved, adminId, -1, (adminId, 0));
            SeedApproval(db, "PRN-AR-WO", Wo, ApprovalStatus.Pending, adminId, -1, (adminId, 0));

            db.ProductionBatches.Add(new ProductionBatch
            {
                BatchId = "PRN-PB", MrpRunId = "PRN-MRP", MrpRunLineSeq = 1,
                ProductCode = Product, Quantity = 10m, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.WorkOrders.Add(new WorkOrder
            {
                WorkOrderId = Wo, ProductionBatchId = "PRN-PB", ProductCode = Product, RoutingId = "PRN-RT",
                Quantity = 10m, Status = WorkOrderStatus.Planned, PlantId = Plant, CreatedByRoleId = RoleId,   // Planned：不進排程，免得列印測試的種子讓共用測試庫的排程變成「過時」
                StandardMaterialCost = 12345.67m,
            });
            db.WorkOrderOperations.Add(new WorkOrderOperation
            {
                WorkOrderId = Wo, OperationSeq = 10, OperationName = "PRN-OP", WorkCenterId = "PRN-WC", UnitTime = 1m,
            });
            db.WorkOrderMaterials.Add(new WorkOrderMaterial
            {
                WorkOrderId = Wo, OperationSeq = 10, BomSeq = 1, ProductCode = "PRN-MAT", RequiredQty = 8m,
                IssuedQty = 3m, ReturnedQty = 1m, UomCode = Uom, IssueMethod = MaterialIssueMethod.Push,
            });

            var opo = new OutsourcePurchaseOrder
            {
                OutsourcePurchaseOrderId = $"{P}OPO-01", SupplierCode = Company, OrderDate = Day, PlantId = Plant,
                CreatedByRoleId = RoleId,
            };
            opo.Lines.Add(new OutsourcePurchaseOrderLine
            {
                OutsourcePurchaseOrderId = opo.OutsourcePurchaseOrderId, LineNo = 1, SourceWorkOrderId = Wo, OperationSeq = 10,
                ProductCode = Product, Qty = 5m, UomCode = Uom, UnitPrice = 12m, CurrencyCode = Ccy,
            });
            db.OutsourcePurchaseOrders.Add(opo);

            var orc = new OutsourceReceipt
            {
                OutsourceReceiptId = $"{P}OR-01", OutsourcePurchaseOrderId = opo.OutsourcePurchaseOrderId, ReceiptDate = Day,
                CreatedBy = By, SupplierCode = Company, TypeCode = "IP", TypeCategory = "Purchase", CurrencyCode = Ccy,
                ExchangeRate = 1m, PlantId = Plant, CreatedByRoleId = RoleId,
            };
            orc.Lines.Add(new OutsourceReceiptLine
            {
                OutsourceReceiptId = orc.OutsourceReceiptId, LineNo = 1, ProductCode = Product, ReceiptQty = 5m, UomCode = Uom,
                StockQty = 5m, StockUomCode = Uom, UnitCost = 12m, CurrencyCode = Ccy, ExchangeRate = 1m,
                LineAmount = 60m, TotalAmount = 60m, AmountLocal = 60m, WhCode = Wh,
            });
            db.OutsourceReceipts.Add(orc);

            db.Dispatches.Add(new Dispatch
            {
                DispatchId = $"{P}DP-01", WorkOrderId = Wo, OperationSeq = 10, Quantity = 5m,
                Status = DispatchStatus.Scheduled, PlantId = Plant, CreatedByRoleId = RoleId, IsOutsource = true,
            });
            db.DispatchMaterials.Add(new DispatchMaterial
            {
                DispatchId = $"{P}DP-01", BomSeq = 1, ProductCode = Product, RequiredQty = 5m, UomCode = Uom,
            });

            db.OutsourceReworks.Add(new OutsourceRework
            {
                OutsourceReworkId = $"{P}ORW-01", OriginalDispatchId = $"{P}DP-01",
                OutsourcePurchaseOrderId = opo.OutsourcePurchaseOrderId, ReworkQty = 2m, CreatedBy = By,
                PlantId = Plant, CreatedByRoleId = RoleId,
            });

            var ms = new MaterialSlip
            {
                SlipId = $"{P}MS-01", SlipNo = $"{P}MS-01", Category = MaterialSlipCategory.OutsourceIssue,
                Status = MaterialSlipStatus.Draft, SlipDate = Day, SourceDocType = "WorkOrder", SourceDocId = Wo,
                TargetSupplierCode = Company, PlantId = Plant,
            };
            ms.Lines.Add(new MaterialSlipLine
            {
                SlipId = ms.SlipId, LineNo = 1, ProductCode = Product, RequestedQty = 5m, UomCode = Uom, FromWarehouseCode = Wh,
            });
            db.MaterialSlips.Add(ms);

            var ic = new InventoryCountSheet
            {
                SheetId = $"{P}IC-01", PlantId = Plant, CountMode = "WithSheet", SheetStatus = "Counting",
                WhCodeFrom = Wh, WhCodeTo = Wh, CreatedBy = By, CreatedByRoleId = RoleId,
            };
            ic.Items.Add(new InventoryCountItem
            {
                SeqNo = 1, WhCode = Wh, LocationCode = "A1", LotNo = "", ProductCode = Product,
                BookQty = 987.65m, ItemStatus = "Pending",
            });
            db.InventoryCountSheets.Add(ic);

            var lc = new LandedCostAllocation
            {
                LandedCostAllocationId = $"{P}LC-01", DocDate = Day, Basis = SmartFactory.MES.Modules.INV.Services.LandedCostBasis.Amount, TotalLandedCost = 100m,
                CurrencyCode = Ccy, CreatedBy = By, PlantId = Plant, CreatedByRoleId = RoleId,
            };
            lc.Lines.Add(new LandedCostAllocationLine
            {
                LandedCostAllocationId = lc.LandedCostAllocationId, LineNo = 1, GoodsReceiptId = $"{P}GR-01",
                GoodsReceiptLineNo = 1, ProductCode = Product, ReceiptQty = 5m, PurchaseAmount = 60m, AllocatedCost = 100m,
            });
            db.LandedCostAllocations.Add(lc);

            var stg = new MaterialStagingOrder
            {
                StagingId = $"{P}STG-01", PlantId = Plant, SourceType = "Manual", Status = "Draft", StagingWhCode = Wh,
                CreatedBy = By, CreatedByRoleId = RoleId,
            };
            stg.Lines.Add(new MaterialStagingLine
            {
                StagingId = stg.StagingId, LineNo = 1, ProductCode = Product, UomCode = Uom, RequiredQty = 5m, FromWarehouseCode = Wh,
            });
            db.MaterialStagingOrders.Add(stg);

            var cpt = new CrossPlantTransfer
            {
                CrossPlantTransferId = $"{P}CPT-01", TransferDate = Day, FromWhCode = Wh, ToWhCode = "PRN-W2",
                PlantId = Plant, ToPlantId = "PRN2", CreatedBy = By, CreatedByRoleId = RoleId,
            };
            cpt.Lines.Add(new CrossPlantTransferLine
            {
                CrossPlantTransferId = cpt.CrossPlantTransferId, LineNo = 1, ProductCode = Product, Qty = 5m, UomCode = Uom,
                StockQty = 5m, StockUomCode = Uom,
            });
            db.CrossPlantTransfers.Add(cpt);

            var io = new InspectionOrder
            {
                InspectionOrderId = $"{P}IO-01", PlantId = Plant, InspectionType = "IQC", SourceDocId = $"{P}GR-01",
                ProductCode = Product, LotSize = 5m, SampleSize = 2, Status = "Pending",
            };
            io.Items.Add(new InspectionItem
            {
                ItemId = $"{P}IO-01-1", InspectionOrderId = io.InspectionOrderId, SeqNo = 1, ItemName = "PRN-ITEM",
                LowerSpec = 1m, UpperSpec = 2m,
            });
            db.InspectionOrders.Add(io);

            db.NonConformanceReports.Add(new NonConformanceReport
            {
                NcrId = $"{P}NCR-01", PlantId = Plant, SourceType = "Manual", ProductCode = Product, Qty = 1m, UomCode = Uom,
                Disposition = "Scrap", NcrStatus = "Open", CreatedBy = By, CreatedByRoleId = RoleId,
            });

            var je = new JournalEntry
            {
                JournalId = $"{P}JE-01", PeriodId = "209707", EntryDate = Day, SourceDocType = "Manual",
                Type = JournalEntryType.Manual, PlantId = Plant,
            };
            je.Lines.Add(new JournalLine { JournalId = je.JournalId, LineNo = 1, AccountCode = "9PRN1", AccountName = "PRN", Debit = 10m });
            je.Lines.Add(new JournalLine { JournalId = je.JournalId, LineNo = 2, AccountCode = "9PRN2", AccountName = "PRN", Credit = 10m });
            db.JournalEntries.Add(je);

            var ari = new ARInvoice
            {
                ARInvoiceId = $"{P}ARI-01", CustomerCode = Company, InvoiceDate = Day, DueDate = Day.AddDays(30),
                CurrencyCode = Ccy, ExchangeRate = 1m, TotalAmount = 105m, TaxAmount = 5m, AmountLocal = 105m,
                PlantId = Plant, CreatedByRoleId = RoleId,
            };
            ari.Lines.Add(new ARInvoiceLine
            {
                ARInvoiceId = ari.ARInvoiceId, LineNo = 1, ProductCode = Product, Qty = 5m, UomCode = Uom, UnitPrice = 20m,
                LineAmount = 100m, TaxAmount = 5m,
            });
            db.ARInvoices.Add(ari);

            var api = new APInvoice
            {
                APInvoiceId = $"{P}API-01", SupplierCode = Company, InvoiceDate = Day, DueDate = Day.AddDays(30),
                CurrencyCode = Ccy, ExchangeRate = 1m, TotalAmount = 105m, TaxAmount = 5m, AmountLocal = 105m,
                PlantId = Plant, CreatedByRoleId = RoleId,
            };
            api.Lines.Add(new APInvoiceLine
            {
                APInvoiceId = api.APInvoiceId, LineNo = 1, ProductCode = Product, Qty = 5m, UomCode = Uom, UnitPrice = 20m,
                LineAmount = 100m, TaxAmount = 5m,
            });
            db.APInvoices.Add(api);

            var rc = new ARReceipt
            {
                ARReceiptId = $"{P}RC-01", ReceiptDate = Day, CustomerCode = Company, CurrencyCode = Ccy, ExchangeRate = 1m,
                Amount = 50m, AmountLocal = 50m, PlantId = Plant, CreatedByRoleId = RoleId,
            };
            rc.Allocations.Add(new ARReceiptAllocation { ARInvoiceId = ari.ARInvoiceId, AllocatedAmount = 50m, AllocatedAmountLocal = 50m });
            db.ARReceipts.Add(rc);

            var py = new APPayment
            {
                APPaymentId = $"{P}PY-01", PaymentDate = Day, SupplierCode = Company, CurrencyCode = Ccy, ExchangeRate = 1m,
                Amount = 50m, AmountLocal = 50m, PlantId = Plant, CreatedByRoleId = RoleId,
            };
            py.Allocations.Add(new APPaymentAllocation { APInvoiceId = api.APInvoiceId, AllocatedAmount = 50m, AllocatedAmountLocal = 50m });
            db.APPayments.Add(py);

            var cn = new ARCreditNote
            {
                ARCreditNoteId = $"{P}CN-01", CustomerCode = Company, NoteDate = Day, CurrencyCode = Ccy, ExchangeRate = 1m,
                TotalAmount = 21m, TaxAmount = 1m, AmountLocal = 21m, CreatedBy = By, PlantId = Plant, CreatedByRoleId = RoleId,
            };
            cn.Lines.Add(new ARCreditNoteLine
            {
                ARCreditNoteId = cn.ARCreditNoteId, LineNo = 1, ARInvoiceId = ari.ARInvoiceId, InvoiceLineNo = 1,
                ProductCode = Product, Qty = 1m, UomCode = Uom, UnitPrice = 20m, LineAmount = 20m, TaxAmount = 1m,
            });
            db.ARCreditNotes.Add(cn);

            var dn = new APDebitNote
            {
                APDebitNoteId = $"{P}DN-01", SupplierCode = Company, NoteDate = Day, CurrencyCode = Ccy, ExchangeRate = 1m,
                TotalAmount = 21m, TaxAmount = 1m, AmountLocal = 21m, CreatedBy = By, PlantId = Plant, CreatedByRoleId = RoleId,
            };
            dn.Lines.Add(new APDebitNoteLine
            {
                APDebitNoteId = dn.APDebitNoteId, LineNo = 1, APInvoiceId = api.APInvoiceId, InvoiceLineNo = 1,
                ProductCode = Product, Qty = 1m, UomCode = Uom, UnitPrice = 20m, LineAmount = 20m, TaxAmount = 1m,
            });
            db.APDebitNotes.Add(dn);
        }

        private static void SeedApproval(SmartFactory.MES.Data.ApplicationDbContext db, string requestId, string docId,
            ApprovalStatus status, string requester, int hoursAgo, params (string Approver, int HoursAgo)[] approvals)
        {
            var now = DateTime.UtcNow;
            db.ApprovalRequests.Add(new ApprovalRequest
            {
                Id = requestId, DocumentId = docId, DocumentType = "PRN", Status = status, RequesterId = requester,
                CurrentStepId = "PRN-STEP",
                CreatedAt = now.AddHours(hoursAgo), PlantId = Plant, CreatedByRoleId = RoleId,
            });
            foreach (var (approver, h) in approvals)
                db.ApprovalLogs.Add(new ApprovalLog
                {
                    RequestId = requestId, ApproverId = approver, ApproverRoleId = RoleId,
                    Action = ApprovalStatus.Approved, ActionTime = now.AddHours(h).AddMinutes(1), PlantId = Plant,
                });
        }

        /// <summary>重跑前清掉上次的 PRN- 單據（子表先刪；同一筆種子跨測試共用，故只在第一次種時清）。</summary>
        private static async Task PurgeAsync(SmartFactory.MES.Data.ApplicationDbContext db)
        {
            const string like = P + "%";
            await db.ApprovalLogs.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.RequestId, "PRN-AR-%")).ExecuteDeleteAsync();
            await db.ApprovalRequests.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.Id, "PRN-AR-%")).ExecuteDeleteAsync();
            await db.WorkOrderMaterials.IgnoreQueryFilters().Where(x => x.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.ARCreditNotes.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.ARCreditNoteId, like)).ExecuteDeleteAsync();
            await db.APDebitNotes.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.APDebitNoteId, like)).ExecuteDeleteAsync();
            await db.ARReceipts.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.ARReceiptId, like)).ExecuteDeleteAsync();
            await db.APPayments.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.APPaymentId, like)).ExecuteDeleteAsync();
            await db.ARInvoices.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.ARInvoiceId, like)).ExecuteDeleteAsync();
            await db.APInvoices.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.APInvoiceId, like)).ExecuteDeleteAsync();
            await db.JournalEntries.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.JournalId, like)).ExecuteDeleteAsync();
            await db.NonConformanceReports.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.NcrId, like)).ExecuteDeleteAsync();
            await db.InspectionOrders.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.InspectionOrderId, like)).ExecuteDeleteAsync();
            await db.CrossPlantTransfers.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.CrossPlantTransferId, like)).ExecuteDeleteAsync();
            await db.MaterialStagingOrders.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.StagingId, like)).ExecuteDeleteAsync();
            await db.LandedCostAllocations.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.LandedCostAllocationId, like)).ExecuteDeleteAsync();
            await db.InventoryCountSheets.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.SheetId, like)).ExecuteDeleteAsync();
            await db.MaterialSlips.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.SlipId, like)).ExecuteDeleteAsync();
            await db.OutsourceReworks.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.OutsourceReworkId, like)).ExecuteDeleteAsync();
            await db.OutsourceReceipts.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.OutsourceReceiptId, like)).ExecuteDeleteAsync();
            await db.Dispatches.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.DispatchId, like)).ExecuteDeleteAsync();
            await db.OutsourcePurchaseOrders.IgnoreQueryFilters().Where(x => EF.Functions.Like(x.OutsourcePurchaseOrderId, like)).ExecuteDeleteAsync();
            await db.WorkOrderOperations.IgnoreQueryFilters().Where(x => x.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrders.IgnoreQueryFilters().Where(x => x.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.ProductionBatches.IgnoreQueryFilters().Where(x => x.BatchId == "PRN-PB").ExecuteDeleteAsync();
        }
    }
}
