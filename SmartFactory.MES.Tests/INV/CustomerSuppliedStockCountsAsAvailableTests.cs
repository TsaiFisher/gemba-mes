using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// 客供倉庫存**計入**廠級可用量（2026-09-24 釘住現況）。
///
/// <para><b>為什麼要特地釘一個「沒改任何東西」的行為</b>：
/// `GetPlantAvailableQtyAsync` 排除待驗倉、不排除客供倉，這個不對稱在 2026-09-22
/// 新增 `WarehouseType.CustomerSupplied` 時**不是被決定的，是被繼承的**——
/// 那一批處理的是計價，可用量那條路沒有人問過。沒有測試的沉默預設會在下一次
/// 有人「順手把客供倉也排掉」時無聲改變口徑，而症狀是 MRP 多買一批料。</para>
///
/// <para><b>計入是對的</b>：客供料本來就要能領出去做該客戶的訂單
/// （`WarehouseType.CustomerSupplied` 排除的是計價不是可用量，見 manual-test-plan §13.9）。
/// <b>但它帶著一個前提：客供料與消耗它的需求一對一。</b>
/// 第三支測試就是把前提破掉之後的形狀寫下來——它斷言的是**今天的行為**，
/// 不是「應該這樣」。哪天有人讓它紅了，紅的訊息會直接告訴他他改了什麼。</para>
/// </summary>
public class CustomerSuppliedStockCountsAsAvailableTests : TestBase
{
    private const string Plant   = "P001";
    private const string Product = "MAT-A";

    private readonly InventoryReservationService _svc;

    public CustomerSuppliedStockCountsAsAvailableTests()
    {
        // 全域廠別過濾：不設 CurrentPlantId 的話 Inventories 一筆都撈不到，
        // 而可用量會回 0——那是「沒庫存」的合理答案，看不出是過濾把資料藏起來了。
        _context.CurrentPlantId = Plant;
        _svc = new InventoryReservationService(_context);

        AddWarehouse("WH01", "原料倉",  WarehouseType.Raw,             "L1");
        AddWarehouse("WHQC", "待驗倉",  WarehouseType.Quarantine,      "QC1");
        // 一客戶一個客供倉——歸屬靠倉別分，品號與 BOM 都只有一套。
        AddWarehouse("WHCSA", "客供倉-甲", WarehouseType.CustomerSupplied, "CSA1");
        AddWarehouse("WHCSB", "客供倉-乙", WarehouseType.CustomerSupplied, "CSB1");
        _context.SaveChanges();
    }

    private void AddWarehouse(string wh, string name, WarehouseType type, string location)
    {
        _context.Warehouses.Add(new Warehouse
            { WhCode = wh, WhName = name, PlantId = Plant, WarehouseType = type });
        _context.StorageLocations.Add(new StorageLocation
            { WhCode = wh, LocationCode = location, LocationName = location });
    }

    private void AddStock(string location, decimal qty)
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = Plant, ProductCode = Product, LocationCode = location,
            LotNo = "", QtyOnHand = qty, UnitCost = 0m,
        });
        _context.SaveChanges();
    }

    /// <summary>
    /// <b>客供倉的料算得進廠級可用量</b>——它存在的用途就是被領去做該客戶的訂單。
    /// </summary>
    [Fact]
    public async Task 客供倉庫存計入廠級可用量()
    {
        AddStock("L1",   100m);
        AddStock("CSA1",  40m);

        Assert.Equal(140m, await _svc.GetPlantAvailableQtyAsync(Plant, Product));
    }

    /// <summary>
    /// <b>待驗倉不算</b>——兩種倉排除的東西不同，這個不對稱是刻意的：
    /// 待驗倉排的是可用量（IQC 沒放行不准動），客供倉排的是計價。
    /// 放在同一檔是為了讓「為什麼一個排一個不排」在同一個畫面上看得到。
    /// </summary>
    [Fact]
    public async Task 待驗倉庫存不計入廠級可用量()
    {
        AddStock("L1",  100m);
        AddStock("QC1",  40m);

        Assert.Equal(100m, await _svc.GetPlantAvailableQtyAsync(Plant, Product));
    }

    /// <summary>
    /// <b>前提破掉的形狀：兩個客供客戶的料在廠級加總裡分不開。</b>
    ///
    /// <para>甲供 40、乙供 25，廠級可用量是 65——`Inventory` 沒有所有權欄位，
    /// 加總看不出那 65 是誰的。於是 MRP 為**乙**的工單淨算時會把**甲**的 40 也算進去，
    /// 乙的料因此少買。症狀是「該買的沒買」，而報表上每個數字都合理。</para>
    ///
    /// <para><b>這支測試斷言的是今天的行為，不是應該的行為。</b>
    /// 修法不是把客供倉排掉（那會讓 MRP 去買甲已經給過我們的料，一樣錯），
    /// 而是讓需求端知道自己該用哪個客戶的客供料——那條線現在不存在。
    /// 詳見 `GetPlantAvailableQtyAsync` 的文件。</para>
    /// </summary>
    [Fact]
    public async Task 兩個客供客戶的料在廠級加總裡分不開_前提破掉的形狀()
    {
        AddStock("CSA1", 40m);   // 甲客戶供的料
        AddStock("CSB1", 25m);   // 乙客戶供的料

        var available = await _svc.GetPlantAvailableQtyAsync(Plant, Product);

        Assert.Equal(65m, available);
        Assert.True(available == 65m,
            "廠級可用量把兩個客供客戶的料加在一起了。這是現況而非結論——"
            + "若這條紅了，代表有人加了歸屬維度（或把客供倉整個排掉，而那是另一個錯）。"
            + "請一併更新 GetPlantAvailableQtyAsync 的文件與 manual-test-plan §13.9。");
    }
}
