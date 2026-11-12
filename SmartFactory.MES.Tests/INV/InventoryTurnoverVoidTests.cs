using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// 存貨週轉的出庫成本遇到作廢單（2026-10-22）。作廢沖銷列方向反轉、TotalCost 寫成負數，
/// 原本只看方向 −1：已作廢的出庫照算，作廢進貨的沖銷（方向 −1、金額負）被當成負的出庫。
/// </summary>
public class InventoryTurnoverVoidTests : TestBase
{
    private const string Plant = "P001";
    private static readonly DateOnly D = new(2026, 6, 10);

    public InventoryTurnoverVoidTests() => _context.CurrentPlantId = Plant;

    private InventoryTurnoverController Ctrl()
    {
        var user = new Mock<IUserContext>();
        user.Setup(u => u.PlantId).Returns(Plant);
        var perm = new Mock<IPermissionService>();
        perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        return new InventoryTurnoverController(_context, user.Object, perm.Object, new Mock<IExportService>().Object,
            new Mock<IDateTimeService>().Object, GetMockLocalizer<ProjectZSharedResource>().Object)
        {
            ControllerContext = GetControllerContext(),
        };
    }

    private void Move(string doc, string type, string category, int dir, decimal qty, decimal cost, bool voidRow = false)
        => _context.StockMovements.Add(new StockMovement
        {
            MovementId = Guid.NewGuid(), DocNo = doc, DocLineNo = 1, TypeCode = type, TypeCategory = category,
            PlantId = Plant, ProductCode = "MAT-T", LocationCode = "L1", LotNo = "", MovementDate = D,
            Qty = qty, StockQty = qty, Direction = dir, UnitCost = cost,
            // 作廢沖銷列的慣例：方向反轉、金額寫成負數（InventoryPostingService.VoidDocAsync）
            TotalCost = voidRow ? -qty * cost : qty * cost,
        });

    [Fact]
    public async Task OutboundCost_NetsVoidedIssues_AndIgnoresVoidedReceipts()
    {
        _context.Products.Add(new Product { ProductCode = "MAT-T", ProductName = "T", CategoryCode = "C1", StockUom = "PC" });
        _context.Inventories.Add(new Inventory { PlantId = Plant, ProductCode = "MAT-T", LocationCode = "L1", LotNo = "", QtyOnHand = 10, UnitCost = 10m });
        Move("GS-1", "GS", "Sales", -1, 5, 10m);                       // 有效出貨 50
        Move("GS-2", "GS", "Sales", -1, 3, 10m);                       // 出貨 30……
        Move("GS-2", "GS", "Sales", +1, 3, 10m, voidRow: true);        // ……後來作廢
        Move("PI-1", "PI", "Purchase", +1, 7, 10m);                    // 進貨 70……
        Move("PI-1", "PI", "Purchase", -1, 7, 10m, voidRow: true);     // ……後來作廢（不是出庫）
        await _context.SaveChangesAsync();

        var view = Assert.IsType<ViewResult>(await Ctrl().Index("2026-06-01", "2026-06-30", null, searched: true));
        var row = Assert.Single(Assert.IsType<List<InventoryTurnoverRowVm>>(view.Model));

        Assert.Equal(50m, row.OutboundCost);
    }
}
