using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using Xunit;

namespace SmartFactory.MES.Tests.BAS;

/// <summary>
/// 倉庫／儲位預設值的廠別收斂（`.claude/specifications/plant-scoped-system-params.md` §2 ①）。
///
/// 背景：`mfg_fg_warehouse` 這類系統參數是**全域單值**，存的卻是倉庫代碼；
/// 而 `Warehouse` 主鍵是 `WhCode` 單欄（全域唯一）且為 IPlantEntity——一個代碼只屬於一個廠。
/// 所以同一個參數值在別的廠一定指不到、或指到別廠的倉。本服務把這種值收斂成「未設定」。
/// </summary>
public class PlantDefaultLocationResolverTests : TestBase
{
    private const string PlantA = "P001";
    private const string PlantB = "P002";

    private readonly PlantDefaultLocationResolver _svc;

    public PlantDefaultLocationResolverTests()
    {
        _svc = new PlantDefaultLocationResolver(_context);

        _context.Set<Warehouse>().AddRange(
            new Warehouse { WhCode = "FG01",     WhName = "A廠成品倉", PlantId = PlantA, IsActive = true },
            new Warehouse { WhCode = "FG-INACT", WhName = "已停用",    PlantId = PlantA, IsActive = false });
        _context.Set<StorageLocation>().AddRange(
            new StorageLocation { WhCode = "FG01", LocationCode = "FG01-01", LocationName = "成品位", IsActive = true },
            new StorageLocation { WhCode = "FG01", LocationCode = "FG01-X",  LocationName = "停用位", IsActive = false });
        _context.SaveChanges();
    }

    [Fact]
    public async Task Resolve_KeepsValueInOwnPlant()
    {
        var (wh, loc) = await _svc.ResolveAsync("FG01", "FG01-01", PlantA);

        Assert.Equal("FG01", wh);
        Assert.Equal("FG01-01", loc);
    }

    /// <summary>本案的核心：同一個全域參數值，在沒有這個代碼的廠必須退回未設定。</summary>
    [Fact]
    public async Task Resolve_DropsValueBelongingToAnotherPlant()
    {
        var (wh, loc) = await _svc.ResolveAsync("FG01", "FG01-01", PlantB);

        Assert.Null(wh);
        Assert.Null(loc);
    }

    [Fact]
    public async Task Resolve_DropsInactiveWarehouse()
    {
        var (wh, _) = await _svc.ResolveAsync("FG-INACT", null, PlantA);

        Assert.Null(wh);
    }

    /// <summary>儲位單獨判定：倉可用就保留倉，只清掉不可用的儲位。</summary>
    [Theory]
    [InlineData("FG01-X")]      // 停用
    [InlineData("NOPE-01")]     // 不存在
    public async Task Resolve_KeepsWarehouseWhenOnlyLocationUnusable(string locationCode)
    {
        var (wh, loc) = await _svc.ResolveAsync("FG01", locationCode, PlantA);

        Assert.Equal("FG01", wh);
        Assert.Null(loc);
    }

    [Fact]
    public async Task Resolve_TreatsBlankWarehouseAsUnset()
    {
        var (wh, loc) = await _svc.ResolveAsync("   ", "FG01-01", PlantA);

        Assert.Null(wh);
        Assert.Null(loc);   // 沒有倉就沒有儲位可談
    }

    /// <summary>
    /// 無 HTTP 上下文（CurrentPlantId 為 null）時仍須依傳入的 plantId 判斷。
    /// 廠別全域過濾是 `PlantId == CurrentPlantId` 而非放行，靠環境過濾會一筆都撈不到，
    /// 於是每個預設值都被誤判成不可用——所以實作必須 IgnoreQueryFilters 並明確比對。
    /// </summary>
    [Fact]
    public async Task Resolve_WorksWithoutAmbientPlantContext()
    {
        _context.CurrentPlantId = null;

        var (wh, loc) = await _svc.ResolveAsync("FG01", "FG01-01", PlantA);

        Assert.Equal("FG01", wh);
        Assert.Equal("FG01-01", loc);
    }
}
