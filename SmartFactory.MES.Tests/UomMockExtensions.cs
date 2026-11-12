using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Services;

namespace SmartFactory.MES.Tests;

/// <summary>
/// 寬鬆的 <c>Mock&lt;IUomService&gt;</c> 對 <see cref="IUomService.ToStockQtyAsync"/> 回 <c>(null, 0)</c>：
/// 2026-10-12 起庫存單據／委外收貨的存檔改由這支換算庫存數量，沿用 mock 的測試會看到庫存數量全變 0。
/// 這裡照真實實作的規則（庫存單位取主檔、同單位不換算、不同單位走 <see cref="IUomService.ConvertAsync"/>）
/// 接回 mock 自己的 ConvertAsync，既有測試對換算的 Setup／Verify 照常有效。刻意不捨入（捨入不是這些測試的主題）。
/// </summary>
internal static class UomMockExtensions
{
    public static Mock<IUomService> WithStockConversion(this Mock<IUomService> mock, ApplicationDbContext db)
    {
        mock.Setup(u => u.ToStockQtyAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .Returns(async (string product, string? uom, decimal qty, CancellationToken ct) =>
            {
                var stock = await db.Products.AsNoTracking().IgnoreQueryFilters()
                    .Where(p => p.ProductCode == product).Select(p => p.StockUom).FirstOrDefaultAsync(ct);
                if (string.IsNullOrWhiteSpace(stock)) return (uom ?? "", qty);
                if (string.IsNullOrWhiteSpace(uom) || string.Equals(uom, stock, StringComparison.OrdinalIgnoreCase))
                    return (stock!, qty);
                return (stock!, await mock.Object.ConvertAsync(product, uom!, stock!, qty, ct));
            });
        return mock;
    }
}
