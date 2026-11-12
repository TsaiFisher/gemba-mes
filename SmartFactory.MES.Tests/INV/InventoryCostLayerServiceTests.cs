using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using Moq;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// InventoryCostLayerService 單元測試：FIFO 成本層消耗順序、不足拋例外。
/// </summary>
public class InventoryCostLayerServiceTests : TestBase
{
    private readonly InventoryCostLayerService _svc;
    private const string PlantId    = "P001";
    private const string ProductCode = "MAT001";
    private const string LocationCode = "A-01";
    private const string LotNo = "";

    /// <summary>2026-01-<paramref name="day"/>：測試用的業務日，讓 FIFO 順序一眼可讀。</summary>
    private static DateOnly D(int day) => new(2026, 1, day);

    public InventoryCostLayerServiceTests()
    {
        // InventoryCostLayer 實作 IPlantEntity，DbContext 套 Global Query Filter（PlantId == CurrentPlantId）。
        // 未設 CurrentPlantId 會讓查詢被廠別過濾濾空 → FIFO 誤判層不足。對齊 MaterialStagingServiceTests。
        _context.CurrentPlantId = PlantId;
        _svc = new InventoryCostLayerService(_context);
    }

    // ── AddLayerAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task AddLayer_ShouldPersistLayer()
    {
        await _svc.AddLayerAsync(PlantId, ProductCode, LocationCode, LotNo, 100m, 50m, "PE-001", D(1));
        await _context.SaveChangesAsync();

        var layers = _context.Set<InventoryCostLayer>()
            .Where(l => l.PlantId == PlantId && l.ProductCode == ProductCode).ToList();
        Assert.Single(layers);
        Assert.Equal(100m, layers[0].QtyRemaining);
        Assert.Equal(50m, layers[0].UnitCost);
    }

    // ── ConsumeLayersAsync ──────────────────────────────────────────────

    [Fact]
    public async Task ConsumeLayers_SingleLayer_FullConsume_ShouldReturnUnitCost()
    {
        await _svc.AddLayerAsync(PlantId, ProductCode, LocationCode, LotNo, 100m, 50m, "PE-001", D(1));
        await _context.SaveChangesAsync();

        var avgCost = await _svc.ConsumeLayersAsync(PlantId, ProductCode, LocationCode, LotNo, 100m, "WT-001", D(5));

        Assert.Equal(50m, avgCost);
        var layer = _context.Set<InventoryCostLayer>().First();
        Assert.Equal(0m, layer.QtyRemaining);
    }

    [Fact]
    public async Task ConsumeLayers_MultiLayer_ShouldFollowFifo()
    {
        // 舊批（成本低）先進，新批（成本高）後進
        await _svc.AddLayerAsync(PlantId, ProductCode, LocationCode, LotNo, 60m, 40m, "PE-001", D(1));
        await _context.SaveChangesAsync();

        await _svc.AddLayerAsync(PlantId, ProductCode, LocationCode, LotNo, 80m, 60m, "PE-002", D(2));
        await _context.SaveChangesAsync();

        // 消耗 80：先扣完 PE-001 的 60，再扣 PE-002 的 20
        // 總成本 = 60*40 + 20*60 = 2400 + 1200 = 3600；avgCost = 3600/80 = 45
        var avgCost = await _svc.ConsumeLayersAsync(PlantId, ProductCode, LocationCode, LotNo, 80m, "WT-001", D(5));

        Assert.Equal(45m, avgCost);

        var layers = _context.Set<InventoryCostLayer>()
            .OrderBy(l => l.ReceiptDate).ToList();
        Assert.Equal(0m, layers[0].QtyRemaining);   // PE-001 耗盡
        Assert.Equal(60m, layers[1].QtyRemaining);  // PE-002 剩 60
    }

    /// <summary>
    /// **倒填**的入庫單：層雖然是後建的，但單據日較早，就必須排在既有層之前先被消耗。
    ///
    /// 這是「入庫日 vs 建層日」唯一會分岔的情境，而在 2026-09-07 之前它完全沒有測試——
    /// 既有的 FIFO 測試都是「先建的層日期也較早」，日期寫對寫錯都會綠。
    /// 舊行為下兩層都會拿到建層當天、日期相同，排序落到 Id（插入順序）上，
    /// 於是先吃到後進的那批（單價 100），本測試的期望值 40 會變成 100。
    ///
    /// 實務情境：月初補登上個月漏掉的一張進貨單。單據日期欄位是開放的
    /// <c>&lt;input type="date"&gt;</c>，只有期間鎖擋著，所以這不是邊角案例。
    /// </summary>
    [Fact]
    public async Task BackdatedLayer_IsConsumedBeforeEarlierCreatedButNewerLayers()
    {
        // 先建、單據日較「晚」的那批
        await _svc.AddLayerAsync(PlantId, ProductCode, LocationCode, LotNo, 50m, 100m, "PE-001", D(10));
        await _context.SaveChangesAsync();

        // 後建、單據日較「早」的那批（＝倒填補登）
        await _svc.AddLayerAsync(PlantId, ProductCode, LocationCode, LotNo, 50m, 40m, "PE-BACKDATED", D(1));
        await _context.SaveChangesAsync();

        var avgCost = await _svc.ConsumeLayersAsync(PlantId, ProductCode, LocationCode, LotNo, 50m, "WT-001", D(20));

        Assert.Equal(40m, avgCost);   // 取的是單據日較早那層；舊行為會取到 100

        var layers = _context.Set<InventoryCostLayer>().OrderBy(l => l.ReceiptDate).ThenBy(l => l.Id).ToList();
        Assert.Equal(0m,  layers[0].QtyRemaining);   // D(1) 那層被吃光
        Assert.Equal(50m, layers[1].QtyRemaining);   // D(10) 那層原封不動
    }

    [Fact]
    public async Task ConsumeLayers_MultiLayer_PartialConsume_LeavesRemainder()
    {
        await _svc.AddLayerAsync(PlantId, ProductCode, LocationCode, LotNo, 100m, 50m, "PE-001", D(1));
        await _context.SaveChangesAsync();

        var avgCost = await _svc.ConsumeLayersAsync(PlantId, ProductCode, LocationCode, LotNo, 40m, "WT-001", D(5));

        Assert.Equal(50m, avgCost);
        var layer = _context.Set<InventoryCostLayer>().First();
        Assert.Equal(60m, layer.QtyRemaining);
    }

    [Fact]
    public async Task ConsumeLayers_Insufficient_ShouldThrow()
    {
        await _svc.AddLayerAsync(PlantId, ProductCode, LocationCode, LotNo, 10m, 50m, "PE-001", D(1));
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ConsumeLayersAsync(PlantId, ProductCode, LocationCode, LotNo, 100m, "WT-001", D(5)));
    }

    [Fact]
    public async Task ConsumeLayers_NoLayers_ShouldThrow()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ConsumeLayersAsync(PlantId, ProductCode, LocationCode, LotNo, 1m, "WT-001", D(5)));
    }

    [Fact]
    public async Task ConsumeLayers_RecordsWhichLayersItTook()
    {
        await _svc.AddLayerAsync(PlantId, ProductCode, LocationCode, LotNo, 60m, 40m, "PE-001", D(1));
        await _context.SaveChangesAsync();
        await _svc.AddLayerAsync(PlantId, ProductCode, LocationCode, LotNo, 80m, 60m, "PE-002", D(2));
        await _context.SaveChangesAsync();

        await _svc.ConsumeLayersAsync(PlantId, ProductCode, LocationCode, LotNo, 80m, "WT-001", D(5));
        await _context.SaveChangesAsync();

        var recs = _context.Set<InventoryCostLayerConsumption>()
            .OrderBy(c => c.Id).ToList();
        Assert.Equal(2, recs.Count);
        Assert.Equal(60m, recs[0].Qty);   // PE-001 那層被扣 60
        Assert.Equal(40m, recs[0].UnitCost);
        Assert.Equal(20m, recs[1].Qty);   // PE-002 那層被扣 20
        Assert.Equal(60m, recs[1].UnitCost);
        Assert.All(recs, r => Assert.Equal("WT-001", r.DocNo));
        Assert.All(recs, r => Assert.Equal(0m, r.RestoredQty));
    }

    // ── RestoreConsumedAsync（W4-F14：撤回要還進原層，不是另建一層）──────

    /// <summary>
    /// 撤回後：層數不變、每層的剩餘量回到消耗前，ReceiptDate 原樣不動。
    ///
    /// 修正前是 AddLayerAsync 另建一層，總量與總價值都對，但最舊的貨還回來以後
    /// 排到最後面 → 之後每一筆出庫的 FIFO 取價順序整個位移，且沒有任何稽核看得到。
    /// </summary>
    [Fact]
    public async Task RestoreConsumed_PutsQtyBackIntoTheOriginalLayers()
    {
        await _svc.AddLayerAsync(PlantId, ProductCode, LocationCode, LotNo, 60m, 40m, "PE-001", D(1));
        await _context.SaveChangesAsync();
        await _svc.AddLayerAsync(PlantId, ProductCode, LocationCode, LotNo, 80m, 60m, "PE-002", D(2));
        await _context.SaveChangesAsync();

        await _svc.ConsumeLayersAsync(PlantId, ProductCode, LocationCode, LotNo, 80m, "WT-001", D(5));
        await _context.SaveChangesAsync();

        // 撤回發生在 1/10——若走 AddLayerAsync，這裡會多出一層 ReceiptDate = 1/10。
        var restored = await _svc.RestoreConsumedAsync(PlantId, ProductCode, LocationCode, LotNo, 80m, "WT-001");
        await _context.SaveChangesAsync();

        Assert.Equal(80m, restored);

        var layers = _context.Set<InventoryCostLayer>().OrderBy(l => l.ReceiptDate).ThenBy(l => l.Id).ToList();
        Assert.Equal(2, layers.Count);                        // 沒有多出第三層
        Assert.Equal(new DateOnly(2026, 1, 1), layers[0].ReceiptDate);
        Assert.Equal(60m, layers[0].QtyRemaining);            // 原封不動回來
        Assert.Equal(new DateOnly(2026, 1, 2), layers[1].ReceiptDate);
        Assert.Equal(80m, layers[1].QtyRemaining);

        Assert.All(_context.Set<InventoryCostLayerConsumption>().ToList(),
                   c => Assert.Equal(c.Qty, c.RestoredQty));
    }

    /// <summary>撤回之後再出一次貨，FIFO 仍然先吃最舊的那層——順序真的復原了。</summary>
    [Fact]
    public async Task RestoreConsumed_ThenConsumeAgain_StillTakesOldestLayerFirst()
    {
        await _svc.AddLayerAsync(PlantId, ProductCode, LocationCode, LotNo, 60m, 40m, "PE-001", D(1));
        await _context.SaveChangesAsync();
        await _svc.AddLayerAsync(PlantId, ProductCode, LocationCode, LotNo, 80m, 60m, "PE-002", D(2));
        await _context.SaveChangesAsync();

        await _svc.ConsumeLayersAsync(PlantId, ProductCode, LocationCode, LotNo, 60m, "WT-001", D(5));
        await _context.SaveChangesAsync();
        await _svc.RestoreConsumedAsync(PlantId, ProductCode, LocationCode, LotNo, 60m, "WT-001");
        await _context.SaveChangesAsync();

        // 再出 60：若順序復原，取的是 1/1 那層（單價 40）；若還進新層，取的會是 1/2 那層（60）。
        var avg = await _svc.ConsumeLayersAsync(PlantId, ProductCode, LocationCode, LotNo, 60m, "WT-002", D(5));

        Assert.Equal(40m, avg);
    }

    /// <summary>部分撤回：後扣的先還，與消耗順序對稱。</summary>
    [Fact]
    public async Task RestoreConsumed_Partial_UndoesTheMostRecentlyTakenLayerFirst()
    {
        await _svc.AddLayerAsync(PlantId, ProductCode, LocationCode, LotNo, 60m, 40m, "PE-001", D(1));
        await _context.SaveChangesAsync();
        await _svc.AddLayerAsync(PlantId, ProductCode, LocationCode, LotNo, 80m, 60m, "PE-002", D(2));
        await _context.SaveChangesAsync();

        await _svc.ConsumeLayersAsync(PlantId, ProductCode, LocationCode, LotNo, 80m, "WT-001", D(5));  // 60 + 20
        await _context.SaveChangesAsync();

        var restored = await _svc.RestoreConsumedAsync(PlantId, ProductCode, LocationCode, LotNo, 20m, "WT-001");
        await _context.SaveChangesAsync();

        Assert.Equal(20m, restored);
        var layers = _context.Set<InventoryCostLayer>().OrderBy(l => l.ReceiptDate).ToList();
        Assert.Equal(0m,  layers[0].QtyRemaining);   // 1/1 那層仍是空的
        Assert.Equal(80m, layers[1].QtyRemaining);   // 還回去的是後扣的 1/2 那層
    }

    /// <summary>
    /// 沒有消耗紀錄時回 0 而不是擲錯——這張表是後補的，歷史單據一筆紀錄都沒有，
    /// 在這裡擋下來等於所有舊單據都不能作廢。差額交回呼叫端退回舊行為。
    /// </summary>
    [Fact]
    public async Task RestoreConsumed_WithNoConsumptionRecord_ReturnsZero()
    {
        await _svc.AddLayerAsync(PlantId, ProductCode, LocationCode, LotNo, 60m, 40m, "PE-001", D(1));
        await _context.SaveChangesAsync();

        var restored = await _svc.RestoreConsumedAsync(PlantId, ProductCode, LocationCode, LotNo, 10m, "WT-LEGACY");

        Assert.Equal(0m, restored);
    }

    /// <summary>同一單重複撤回不得無限還量——RestoredQty 是上限。</summary>
    [Fact]
    public async Task RestoreConsumed_Twice_DoesNotGiveBackMoreThanWasTaken()
    {
        await _svc.AddLayerAsync(PlantId, ProductCode, LocationCode, LotNo, 60m, 40m, "PE-001", D(1));
        await _context.SaveChangesAsync();
        await _svc.ConsumeLayersAsync(PlantId, ProductCode, LocationCode, LotNo, 60m, "WT-001", D(5));
        await _context.SaveChangesAsync();

        Assert.Equal(60m, await _svc.RestoreConsumedAsync(PlantId, ProductCode, LocationCode, LotNo, 60m, "WT-001"));
        await _context.SaveChangesAsync();
        Assert.Equal(0m,  await _svc.RestoreConsumedAsync(PlantId, ProductCode, LocationCode, LotNo, 60m, "WT-001"));
        await _context.SaveChangesAsync();

        Assert.Equal(60m, _context.Set<InventoryCostLayer>().Single().QtyRemaining);
    }
}
