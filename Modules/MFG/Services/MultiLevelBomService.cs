using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.ViewModels;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <inheritdoc cref="IMultiLevelBomService" />
    public class MultiLevelBomService : IMultiLevelBomService
    {
        /// <summary>展開深度上限（防階層失控；正常 BOM 深度遠低於此）。</summary>
        public const int MaxDepth = 20;

        private readonly ApplicationDbContext _db;

        public MultiLevelBomService(ApplicationDbContext db) => _db = db;

        public async Task<MultiLevelExplosionResult> ExplodeAsync(
            string productCode, DateOnly today, CancellationToken ct = default)
        {
            var result = new MultiLevelExplosionResult { RootProductCode = productCode };

            var root = await _db.Products.AsNoTracking()
                .FirstOrDefaultAsync(p => p.ProductCode == productCode, ct);
            if (root == null) return result;

            result.RootProductName = root.ProductName;
            result.RootUomCode     = root.StockUom ?? "";

            // 產品/途程快取：同一品號在多處出現時不重複查詢（展開仍逐路徑進行，
            // 因為累計量與階層依路徑而異）。
            var productCache = new Dictionary<string, Product?> { [productCode] = root };
            var routingCache = new Dictionary<string, Routing?>();

            await ExpandAsync(root, level: 0, requiredQty: 1m,
                ancestors: new HashSet<string> { productCode },
                productCache, routingCache, result, today, ct);

            return result;
        }

        /// <summary>取品號的有效途程（IsActive + 生效區間含 today）；無 RoutingId 或查無 → null。</summary>
        private async Task<Routing?> GetActiveRoutingAsync(
            Product product, DateOnly today,
            Dictionary<string, Routing?> routingCache, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(product.RoutingId)) return null;
            if (routingCache.TryGetValue(product.RoutingId, out var cached)) return cached;

            var routing = await _db.Routings.AsNoTracking()
                .Include(r => r.Operations).ThenInclude(op => op.Boms)
                .FirstOrDefaultAsync(r => r.RoutingId == product.RoutingId && r.IsActive
                    && (r.EffectiveFrom == null || r.EffectiveFrom <= today)
                    && (r.EffectiveTo == null || r.EffectiveTo >= today), ct);

            routingCache[product.RoutingId] = routing;
            return routing;
        }

        private async Task ExpandAsync(
            Product product, int level, decimal requiredQty,
            HashSet<string> ancestors,
            Dictionary<string, Product?> productCache,
            Dictionary<string, Routing?> routingCache,
            MultiLevelExplosionResult result, DateOnly today, CancellationToken ct)
        {
            if (level >= MaxDepth)
            {
                result.Warnings.Add(PzText.Get("Mfg_BomDepthTruncatedFmt", product.ProductCode, MaxDepth));
                return;
            }

            var routing = await GetActiveRoutingAsync(product, today, routingCache, ct);
            if (routing == null) return;   // 採購件/無有效途程 → 葉節點

            var node = new MultiLevelRoutingNodeVm
            {
                Level       = level,
                ProductCode = product.ProductCode,
                ProductName = product.ProductName,
                UomCode     = product.StockUom,
                RoutingId   = routing.RoutingId,
                RoutingName = routing.RoutingName ?? "",
                RequiredQty = requiredQty,
            };
            result.RoutingNodes.Add(node);

            foreach (var op in routing.Operations.OrderBy(o => o.OperationSeq))
            {
                ct.ThrowIfCancellationRequested();

                var opVm = new MultiLevelRoutingOpVm
                {
                    OperationSeq  = op.OperationSeq,
                    OperationName = op.OperationName ?? "",
                    WorkCenterId  = op.WorkCenterId,
                    SetupTime     = op.TM_SETUP,
                    UnitTime      = op.TM_UNIT,
                    TimeUnit      = op.TimeUnit,
                    SetupTimeUnit = op.SetupTimeUnit,
                    YieldRate     = op.YieldRate,
                    IsOutsource   = op.IsOutsource,
                };
                node.Operations.Add(opVm);

                foreach (var bom in op.Boms.OrderBy(b => b.BomSeq))
                {
                    var matProduct = await GetProductAsync(bom.ProductCode, productCache, ct);
                    var matName    = matProduct?.ProductName ?? "";

                    // 循環偵測：本路徑祖先鏈已含此品號 → 記警告、不往下展（避免無窮遞迴）
                    var isCycle = ancestors.Contains(bom.ProductCode);
                    if (isCycle)
                        result.Warnings.Add(
                            PzText.Get("Mfg_BomBranchCycleFmt", product.ProductCode, bom.ProductCode));

                    var matRouting = isCycle || matProduct == null
                        ? null
                        : await GetActiveRoutingAsync(matProduct, today, routingCache, ct);
                    var isMake = matRouting != null;

                    // 累計需求量：父件累計量 × 單位用量，再套下角率／損耗率換算投入量（RoutingBom.ApplyLossRates）
                    var childQty = RoutingBom.ApplyLossRates(requiredQty * bom.Quantity, bom.OffcutRate, bom.ScrapRate);

                    opVm.Boms.Add(new MultiLevelRoutingOpBomVm
                    {
                        BomSeq      = bom.BomSeq,
                        ProductCode = bom.ProductCode,
                        ProductName = matName,
                        UomCode     = bom.UomCode,
                        Quantity    = bom.Quantity,
                        ScrapRate   = bom.ScrapRate,
                        OffcutRate  = bom.OffcutRate,
                        IsMake      = isMake,
                    });

                    result.BomRows.Add(new MultiLevelBomRowVm
                    {
                        Level             = level + 1,
                        ParentProductCode = product.ProductCode,
                        ProductCode       = bom.ProductCode,
                        ProductName       = matName,
                        UomCode           = bom.UomCode,
                        Quantity          = bom.Quantity,
                        ScrapRate         = bom.ScrapRate,
                        OffcutRate        = bom.OffcutRate,
                        RequiredQty       = childQty,
                        SourceType        = isMake ? "Make" : "Buy",
                        IssueMethod       = bom.IssueMethod,
                    });

                    if (isMake)
                    {
                        // 祖先鏈逐路徑複製：同一品號允許出現在不同分支，僅擋同路徑循環
                        var chain = new HashSet<string>(ancestors) { bom.ProductCode };
                        await ExpandAsync(matProduct!, level + 1, childQty,
                            chain, productCache, routingCache, result, today, ct);
                    }
                }
            }
        }

        private async Task<Product?> GetProductAsync(
            string productCode, Dictionary<string, Product?> cache, CancellationToken ct)
        {
            if (cache.TryGetValue(productCode, out var p)) return p;
            p = await _db.Products.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ProductCode == productCode, ct);
            cache[productCode] = p;
            return p;
        }
    }
}
