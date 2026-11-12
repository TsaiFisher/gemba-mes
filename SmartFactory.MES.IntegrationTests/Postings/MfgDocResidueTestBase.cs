using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// MFG／委外三張單（發退料單、委外採購單、委外收貨單）共用的種子。
    ///
    /// 這三張單抽在一起不是為了省行數，是因為**它們共用同一條上游鏈**：
    /// 生產批次 → 工單 → 工序 → 工單用料，委外再從工單接出委外採購單。
    /// 發退料單的過帳要回寫 <c>WorkOrderMaterial.IssuedQty</c>、委外採購單刪除要清掉
    /// <c>WorkOrder.OutsourcePurchaseOrderId</c>、委外收貨單的在途沖銷掛在 OPO 行上——
    /// 三者驗的殘留方向不同，但少了任何一段上游都建不出可過帳的單。
    /// 分開寫等於把同一份種子寫三次，而且三份會各自漂移。
    ///
    /// 與 <see cref="InvDocResidueTestBase"/> 平行而非繼承：那個樣板假設
    /// 「Save 建單 → Confirm 過帳 → Delete 判 QtyPutAway」，這三張單沒有一張符合——
    /// 發退料單走 Draft→Approved→Picking→Confirmed→Posted 五段狀態機、過帳動作叫 Issue，
    /// 委外採購單的 Confirm 根本不碰庫存。硬套樣板只會逼出一堆覆寫。
    /// </summary>
    public abstract class MfgDocResidueTestBase : PostingResidueTestBase
    {
        protected MfgDocResidueTestBase(AuthedScheduleFactory factory) : base(factory) { }

        // ── 共用主檔 ────────────────────────────────────────────────────────

        protected const string MfgProduct  = Prefix + "-MP01";   // 產出品（工單品號、委外收回品）
        protected const string MfgMaterial = Prefix + "-MM01";   // 用料（領料、送外料）
        protected const string MfgCategory = Prefix + "-MCAT";
        protected const string MfgUom      = Prefix + "-MEA";
        protected const string MfgWh       = Prefix + "-MWH";
        protected const string MfgLoc      = Prefix + "-MLOC";
        protected const string MfgSupplier = Prefix + "-MSUP";
        protected const string MfgRouting  = Prefix + "-MRT";

        /// <summary>期間鎖會擋已關帳期間的過帳；用遠期日期避開（同 InvDocResidueTestBase）。</summary>
        protected static readonly DateOnly MfgPostDate = new(2097, 6, 15);

        /// <summary>
        /// 同一天的 UTC 時間戳，供工單／生產批次的 <c>DateTime</c> 欄位使用。
        ///
        /// 不能由 <c>MfgPostDate</c> 直接轉——那產出的 Kind 是 Unspecified，
        /// 而這些欄位在 PG 是 timestamptz，Npgsql 只收 UTC，會直接拋
        /// 「Cannot write DateTime with Kind=Unspecified」。這正是規則 R17 守的那個缺陷類別
        /// （見 test-hardening-plan.md），只是這次踩在測試種子而不是產品程式碼。
        /// </summary>
        protected static readonly DateTime MfgPostAt =
            DateTime.SpecifyKind(new DateTime(2097, 6, 15), DateTimeKind.Utc);

        protected const decimal MfgOnHand   = 1000m;
        protected const decimal MfgLineQty  = 10m;
        protected const decimal MfgUnitCost = 5m;

        /// <summary>
        /// 主檔與期初庫存。庫存每輪重設回基準量——同 collection 多支測試共用同一個
        /// 品號／儲位，不重設的話跑到後面會可用量不足而假紅（同 InvDocResidueTestBase 的教訓）。
        ///
        /// 交易類別一律用 HasData 內建的 IO／IR（生產領料／退料，Direction ∓1）：
        /// <c>MaterialSlipController.CategoryToTypeCode</c> 是**硬寫**的，不吃單據上的類別，
        /// 自建測試前綴類別會過不了帳。
        /// </summary>
        protected static async Task EnsureMfgMastersAsync(ApplicationDbContext db)
        {
            if (!await db.Uoms.IgnoreQueryFilters().AnyAsync(u => u.UomCode == MfgUom))
                db.Uoms.Add(new Modules.BAS.Models.Uom { UomCode = MfgUom, UomName = "個", DecimalPlaces = 4 });

            if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == MfgCategory))
                db.ProductCategories.Add(new Modules.BAS.Models.ProductCategory
                {
                    CategoryCode = MfgCategory, CategoryName = "MFG 殘留測試類別",
                    CostMethod = Modules.BAS.Models.CostMethod.MovingAverage, IsActive = true,
                });

            foreach (var code in new[] { MfgProduct, MfgMaterial })
                if (!await db.Products.IgnoreQueryFilters().AnyAsync(p => p.ProductCode == code))
                    db.Products.Add(new Modules.BAS.Models.Product
                    {
                        ProductCode = code, ProductName = $"MFG 殘留測試 {code}",
                        CategoryCode = MfgCategory,
                        PurchaseUom = MfgUom, StockUom = MfgUom, SalesUom = MfgUom,
                        IsActive = true,
                    });

            if (!await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.CompanyCode == MfgSupplier))
                db.Companies.Add(new Modules.BAS.Models.Company
                {
                    CompanyCode = MfgSupplier, CompanyName = "委外殘留測試廠商",
                    IsSupplier = true, IsCustomer = false, IsActive = true,
                });

            if (!await db.Warehouses.IgnoreQueryFilters().AnyAsync(w => w.WhCode == MfgWh))
                db.Warehouses.Add(new Modules.BAS.Models.Warehouse
                {
                    WhCode = MfgWh, WhName = "MFG 殘留測試倉", PlantId = Plant,
                });

            if (!await db.StorageLocations.IgnoreQueryFilters()
                    .AnyAsync(l => l.WhCode == MfgWh && l.LocationCode == MfgLoc))
                db.StorageLocations.Add(new Modules.BAS.Models.StorageLocation
                {
                    WhCode = MfgWh, LocationCode = MfgLoc, LocationName = "MFG 殘留測試儲位",
                });

            await db.SaveChangesAsync();

            foreach (var code in new[] { MfgProduct, MfgMaterial })
            {
                var inv = await db.Inventories.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(i => i.PlantId == Plant && i.ProductCode == code
                                              && i.LocationCode == MfgLoc && i.LotNo == "");
                if (inv == null)
                    db.Inventories.Add(new Modules.INV.Models.Inventory
                    {
                        PlantId = Plant, ProductCode = code, LocationCode = MfgLoc, LotNo = "",
                        QtyOnHand = MfgOnHand, QtyReserved = 0m, UnitCost = MfgUnitCost,
                    });
                else
                {
                    inv.QtyOnHand   = MfgOnHand;
                    inv.QtyReserved = 0m;
                    inv.UnitCost    = MfgUnitCost;
                }
            }

            await db.SaveChangesAsync();
        }

        // ── 工單鏈 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 建立「生產批次 → 工單 → 工序 1 → 工單用料 (1_1)」一整條，並回傳工單號。
        ///
        /// 用料的鍵刻意固定為 <c>OperationSeq=1、BomSeq=1</c>：發退料單以
        /// <c>SourceLineId = "1_1"</c> 對應回來（<c>BackfillWorkOrderMaterialQtyAsync</c> 的字串慣例），
        /// 這個對應斷掉時回寫會**靜默跳過**——沒有例外、沒有訊息，只有 IssuedQty 永遠是 0。
        /// </summary>
        protected static async Task<string> SeedWorkOrderChainAsync(
            ApplicationDbContext db, string woId, decimal requiredQty)
        {
            var batchId = woId + "-B";

            await ClearWorkOrderChainAsync(db, woId);

            db.ProductionBatches.Add(new ProductionBatch
            {
                BatchId = batchId, MrpRunId = woId + "-MRP", MrpRunLineSeq = 1,
                ProductCode = MfgProduct, Quantity = requiredQty,
                PlannedEndDate = MfgPostAt,
                PlantId = Plant, CreatedByRoleId = RoleId,
            });

            var wo = new WorkOrder
            {
                WorkOrderId = woId, ProductionBatchId = batchId,
                ProductCode = MfgProduct, RoutingId = MfgRouting,
                Quantity = requiredQty, GrossDemandQty = requiredQty,
                PlannedStartDate = MfgPostAt,
                PlannedEndDate   = MfgPostAt.AddDays(1),
                Status = WorkOrderStatus.Released,
                ApprovalStatus = Models.DocumentApprovalStatus.Approved,
                PlantId = Plant, CreatedByRoleId = RoleId,
            };
            wo.Operations.Add(new WorkOrderOperation
            {
                WorkOrderId = woId, OperationSeq = 1, OperationName = "殘留測試工序",
                WorkCenterId = Prefix + "-WC", UnitTime = 1m,
                Materials =
                {
                    new WorkOrderMaterial
                    {
                        WorkOrderId = woId, OperationSeq = 1, BomSeq = 1,
                        ProductCode = MfgMaterial, RequiredQty = requiredQty,
                        UomCode = MfgUom, UnitCost = MfgUnitCost,
                        IssuedQty = 0m, ReturnedQty = 0m,
                    },
                },
            });
            db.WorkOrders.Add(wo);

            await db.SaveChangesAsync();
            return woId;
        }

        /// <summary>整條工單鏈連同下游（發退料單、委外採購單）清空，讓測試可重跑。</summary>
        protected static async Task ClearWorkOrderChainAsync(ApplicationDbContext db, string woId)
        {
            var batchId = woId + "-B";

            var slipIds = await db.MaterialSlips.IgnoreQueryFilters()
                .Where(s => s.SourceDocId == woId).Select(s => s.SlipId).ToListAsync();
            if (slipIds.Count > 0)
            {
                await db.MaterialSlipLines.IgnoreQueryFilters()
                    .Where(l => slipIds.Contains(l.SlipId)).ExecuteDeleteAsync();
                await db.MaterialSlips.IgnoreQueryFilters()
                    .Where(s => slipIds.Contains(s.SlipId)).ExecuteDeleteAsync();
            }

            await db.WorkOrderMaterials.IgnoreQueryFilters()
                .Where(m => m.WorkOrderId == woId).ExecuteDeleteAsync();
            await db.WorkOrderOperations.IgnoreQueryFilters()
                .Where(o => o.WorkOrderId == woId).ExecuteDeleteAsync();
            await db.WorkOrders.IgnoreQueryFilters()
                .Where(w => w.WorkOrderId == woId).ExecuteDeleteAsync();
            await db.ProductionBatches.IgnoreQueryFilters()
                .Where(b => b.BatchId == batchId).ExecuteDeleteAsync();
        }

        // ── 委外鏈 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 在既有工單之上接出「委外建議 → 委外採購單（草稿，一行）」，回傳 (OPO 單號, 建議單號)。
        ///
        /// 工單一併標成整品委外（<c>IsOutsource</c> + 三個委外欄位），因為刪除委外採購單時
        /// 要清的正是那三個欄位——不標的話那段回沖邏輯不會被執行到，測試看似綠實則沒驗。
        ///
        /// <paramref name="transferredQty"/> 模擬 Convert 當下已累加的已轉量：
        /// 正式路徑是 <c>demand.TransferredQty += SplitQty</c>，這裡直接種到轉完的狀態。
        /// </summary>
        protected static async Task<(string OpoId, string DemandId)> SeedDraftOutsourcePoAsync(
            ApplicationDbContext db, string opoId, string woId, decimal qty, decimal transferredQty)
        {
            var demandId = opoId + "-D";

            await ClearOutsourcePoAsync(db, opoId);

            db.OutsourceDemands.Add(new OutsourceDemand
            {
                DemandId = demandId, WorkOrderId = woId,
                DemandType = OutsourceDemandType.WholeProduct,
                ProductCode = MfgProduct, DemandQty = qty,
                TransferredQty = transferredQty,
                RequiredDate = MfgPostDate, PlantId = Plant,
            });

            var po = new OutsourcePurchaseOrder
            {
                OutsourcePurchaseOrderId = opoId,
                SupplierCode = MfgSupplier,
                OrderDate = MfgPostDate,
                ExpectedDeliveryDate = MfgPostDate,
                Status = OutsourcePoStatus.Draft,
                PlantId = Plant, CreatedByRoleId = RoleId,
            };
            po.Lines.Add(new OutsourcePurchaseOrderLine
            {
                OutsourcePurchaseOrderId = opoId, LineNo = 1,
                DemandId = demandId, SourceWorkOrderId = woId, OperationSeq = 1,
                ProductCode = MfgProduct, Qty = qty, SplitQty = qty,
                UomCode = MfgUom, UnitPrice = MfgUnitCost,
            });
            db.OutsourcePurchaseOrders.Add(po);

            var wo = await db.WorkOrders.IgnoreQueryFilters().FirstAsync(w => w.WorkOrderId == woId);
            wo.IsOutsource = true;
            wo.OutsourcePurchaseOrderId = opoId;
            wo.OutsourceSupplierCode = MfgSupplier;
            wo.OutsourceExpectedDeliveryDate = MfgPostDate;

            await db.SaveChangesAsync();
            return (opoId, demandId);
        }

        /// <summary>委外採購單與其建議、下游收貨單清空，讓測試可重跑。</summary>
        protected static async Task ClearOutsourcePoAsync(ApplicationDbContext db, string opoId)
        {
            var receiptIds = await db.OutsourceReceipts.IgnoreQueryFilters()
                .Where(r => r.OutsourcePurchaseOrderId == opoId)
                .Select(r => r.OutsourceReceiptId).ToListAsync();
            if (receiptIds.Count > 0)
            {
                await db.OutsourceReceiptLines.IgnoreQueryFilters()
                    .Where(l => receiptIds.Contains(l.OutsourceReceiptId)).ExecuteDeleteAsync();
                await db.OutsourceReceipts.IgnoreQueryFilters()
                    .Where(r => receiptIds.Contains(r.OutsourceReceiptId)).ExecuteDeleteAsync();
            }

            await db.OutsourcePurchaseOrderLines.IgnoreQueryFilters()
                .Where(l => l.OutsourcePurchaseOrderId == opoId).ExecuteDeleteAsync();
            await db.OutsourcePurchaseOrders.IgnoreQueryFilters()
                .Where(o => o.OutsourcePurchaseOrderId == opoId).ExecuteDeleteAsync();
            await db.OutsourceDemands.IgnoreQueryFilters()
                .Where(d => d.DemandId == opoId + "-D").ExecuteDeleteAsync();
        }

        /// <summary>讀委外建議的已轉量——過帳足跡看不到，卻能永久卡死轉單與 MRP 重跑。</summary>
        protected async Task<decimal> ReadDemandTransferredQtyAsync(string demandId)
        {
            using var _ = NewScope(out var db);
            return await db.OutsourceDemands.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.DemandId == demandId)
                .Select(d => d.TransferredQty).FirstAsync();
        }

        /// <summary>讀工單回填的委外採購單號（刪除委外單時應被清成 null）。</summary>
        protected async Task<string?> ReadWorkOrderOutsourcePoIdAsync(string woId)
        {
            using var _ = NewScope(out var db);
            return await db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                .Where(w => w.WorkOrderId == woId)
                .Select(w => w.OutsourcePurchaseOrderId).FirstAsync();
        }

        /// <summary>讀工單用料 (1_1) 的累計發料量／退料量——過帳足跡看不到的第三種殘留。</summary>
        protected async Task<(decimal Issued, decimal Returned)> ReadWorkOrderMaterialQtyAsync(string woId)
        {
            using var _ = NewScope(out var db);
            var m = await db.WorkOrderMaterials.IgnoreQueryFilters().AsNoTracking()
                .FirstAsync(x => x.WorkOrderId == woId && x.OperationSeq == 1 && x.BomSeq == 1);
            return (m.IssuedQty, m.ReturnedQty);
        }

        // ── 單號 ────────────────────────────────────────────────────────────

        /// <summary>依測試方法名產生穩定且互不干擾的單號。</summary>
        protected string MfgDocNoFor(string caseName, string tag)
            => $"{Prefix}{tag}-{Math.Abs(caseName.GetHashCode()) % 100000:D5}";
    }
}
