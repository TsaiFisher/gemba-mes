using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// 客供料落在非客供倉的盤查稽核（`CUSTOMER_SUPPLIED_WRONG_LOCATION`）。
///
/// 客供料屬客戶財產，不是我們的資產也不是我們的成本。但移動平均的成本池是
/// **(PlantId, ProductCode)**——分母取該品號全廠在手量，新均價寫回該品號**所有**
/// Inventory 列。所以**分批號隔不開**（批號不在成本池的鍵裡），而隔離維度取
/// `WarehouseType.CustomerSupplied`。⚠️ 2026-09-24 訂正：原本這句把「分品號」也寫成
/// 隔不開是錯的——鍵裡本來就有 `ProductCode`，換品號隔得開，只是代價高（見 §13.9）：
///
/// <code>
/// 自有 100 @10，客供 100 @0，再進自有 100 @20
///   正解 (100×10 + 100×20) / 200 = 15
///   實際 (200×10 + 100×20) / 300 = 13.33   ← 分母含了客戶的料
/// </code>
///
/// **與過帳守門的分工**：`InventoryPostingService` 的 ③-b 擋未來（CI／CR 不在客供倉
/// 就不准過帳）；本稽核找過去——守門上線前已經落在一般倉的遺留資料。
///
/// 2026-09-22 起已掛進 `RunAllAsync`＝沒調倉就不准關帳（業主確認現行資料沒有客供單，
/// 所以不存在「既有部署被擋在月結當天」的風險）。
/// </summary>
public class CustomerSuppliedLocationAuditTests : TestBase
{
    private const string Plant = "P001";
    private readonly DataAuditService _svc;

    public CustomerSuppliedLocationAuditTests()
    {
        _svc = new DataAuditService(_context);

        _context.Warehouses.Add(new Warehouse
            { WhCode = "WH01", WhName = "原料倉", PlantId = Plant, WarehouseType = WarehouseType.Raw });
        _context.StorageLocations.Add(new StorageLocation
            { WhCode = "WH01", LocationCode = "L1", LocationName = "L1" });

        _context.Warehouses.Add(new Warehouse
            { WhCode = "WHCS", WhName = "客供倉", PlantId = Plant, WarehouseType = WarehouseType.CustomerSupplied });
        _context.StorageLocations.Add(new StorageLocation
            { WhCode = "WHCS", LocationCode = "CS1", LocationName = "CS1" });
        _context.SaveChanges();
    }

    private void SeedMovement(string typeCode, string location, decimal qty = 100m,
                              int direction = 1, string productCode = "MAT-A", string plant = Plant)
    {
        _context.StockMovements.Add(new StockMovement
        {
            DocNo = $"{typeCode}-{location}-{Guid.NewGuid():N}".Substring(0, 14), DocLineNo = 1,
            TypeCode = typeCode, PlantId = plant, ProductCode = productCode,
            LocationCode = location, LotNo = "",
            StockQty = qty, Qty = qty, StockUomCode = "PCS", UomCode = "PCS",
            Direction = direction, UnitCost = 0m, TotalCost = 0m,
            MovementDate = new DateOnly(2026, 6, 15),
        });
        _context.SaveChanges();
    }

    /// <summary>客供進貨落在一般倉＝遺留資料，要報。這是本稽核存在的理由。</summary>
    [Fact]
    public async Task CustomerSuppliedInNormalWarehouse_IsReported()
    {
        SeedMovement("CI", "L1");

        var f = Assert.Single(await _svc.AuditCustomerSuppliedLocationAsync(Plant));

        Assert.Equal("CUSTOMER_SUPPLIED_WRONG_LOCATION", f.AuditType);
        Assert.Equal("MAT-A", f.ProductCode);
        Assert.Equal(100m, f.DifferenceAmount);
    }

    /// <summary>
    /// 客供料在客供倉＝正解，不得報。
    /// 少了這一支，一支「只要是 CI／CR 就報」的稽核也會通過上面那支，
    /// 而那會讓每個做對的部署天天收到假異常。
    /// </summary>
    [Fact]
    public async Task CustomerSuppliedInCustomerSuppliedWarehouse_IsNotReported()
    {
        SeedMovement("CI", "CS1");
        SeedMovement("CR", "CS1", qty: 40m, direction: -1);

        Assert.Empty(await _svc.AuditCustomerSuppliedLocationAsync(Plant));
    }

    /// <summary>
    /// 🚨 **品號共用不再是缺陷**。隔離維度改成倉別之後，客供料與自有料
    /// 可以用同一個品號（它們本來就是同一個物料，只是所有權不同）——
    /// 只要客供的那批在客供倉。這一支釘住這個設計決定，
    /// 否則日後很容易又「順手」把品號區分加回成強制要求。
    /// </summary>
    [Fact]
    public async Task SameProductCode_InBothWarehouses_IsNotReported()
    {
        SeedMovement("CI", "CS1");                      // 客供的那批在客供倉
        SeedMovement("PI", "L1");                       // 自有的同品號在一般倉

        Assert.Empty(await _svc.AuditCustomerSuppliedLocationAsync(Plant));
    }

    /// <summary>純自有料（絕大多數）不得報。</summary>
    [Fact]
    public async Task OwnMaterialOnly_IsNotReported()
    {
        SeedMovement("PI", "L1");
        SeedMovement("IO", "L1", qty: 30m, direction: -1);

        Assert.Empty(await _svc.AuditCustomerSuppliedLocationAsync(Plant));
    }

    /// <summary>
    /// 客供料被領去生產（`IO` 從客供倉出庫）是**正常用途**，不得報。
    /// 客供料的存在目的就是做該客戶的訂單。
    /// </summary>
    [Fact]
    public async Task CustomerSuppliedIssuedToProduction_IsNotReported()
    {
        SeedMovement("CI", "CS1");
        SeedMovement("IO", "CS1", qty: 60m, direction: -1);

        Assert.Empty(await _svc.AuditCustomerSuppliedLocationAsync(Plant));
    }

    /// <summary>客供退回也要查，不是只查進貨。</summary>
    [Fact]
    public async Task CustomerSuppliedReturnFromNormalWarehouse_IsReported()
    {
        SeedMovement("CR", "L1", qty: 25m, direction: -1);

        var f = Assert.Single(await _svc.AuditCustomerSuppliedLocationAsync(Plant));
        Assert.Equal(-25m, f.DifferenceAmount);
    }

    /// <summary>
    /// 廠別參數要生效。稽核一律 `IgnoreQueryFilters` 後自己寫廠別條件，
    /// 漏一支的後果是「乙廠有爛帳、甲廠關不了帳」且畫面看不出原因（§13.5 的 5-3）。
    /// </summary>
    [Fact]
    public async Task PlantFilter_DoesNotLeakAnotherPlant()
    {
        SeedMovement("CI", "L1");

        Assert.Single(await _svc.AuditCustomerSuppliedLocationAsync(Plant));
        Assert.Empty(await _svc.AuditCustomerSuppliedLocationAsync("P999"));
    }

    /// <summary>
    /// 本稽核必須在 `RunAllAsync` 裡，而且是**擋關帳**的等級。
    ///
    /// 掛載本身是一個決定，不是實作細節：寫好一支稽核卻忘了註冊，
    /// 它就只是一個沒有人呼叫的方法——全綠、零訊號，和沒寫一樣
    /// （§13.8 的「有讀取點但結果被丟掉＝沒有讀取點」是同一種形狀）。
    /// </summary>
    [Fact]
    public async Task Audit_IsWiredIntoRunAll_AndBlocksPeriodClose()
    {
        SeedMovement("CI", "L1");

        var result = await _svc.RunAllAsync(Plant);

        var f = Assert.Single(result.Failures,
            x => x.AuditType == "CUSTOMER_SUPPLIED_WRONG_LOCATION");
        Assert.True(f.IsBlocking);      // 不在 AdvisoryAuditTypes 裡
        Assert.False(result.IsPassed);  // 關帳會被擋下
    }

    /// <summary>`plantId` 為 null ＝ 全公司盤查（`RunAllAsync` 的既有語意）。</summary>
    [Fact]
    public async Task NullPlant_ScansEveryPlant()
    {
        SeedMovement("CI", "L1");
        SeedMovement("CI", "L9", productCode: "MAT-F", plant: "P002");   // 別廠、未登錄的儲位

        var all = await _svc.AuditCustomerSuppliedLocationAsync();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, f => f.PlantId == Plant  && f.ProductCode == "MAT-A");
        Assert.Contains(all, f => f.PlantId == "P002" && f.ProductCode == "MAT-F");
    }
}
