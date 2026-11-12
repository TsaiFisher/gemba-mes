using SmartFactory.MES.Tests.Shared;
using Moq;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// 借入借出、客供與樣品六個異動類別的分錄落點。
///
/// 起因：`BuildJournalLines` 的科目原本只按 `(direction, typeCategory)` 決定，
/// 而 `Loan`／`Sample` 兩個類別**沒有自己的分支**，於是六個類別全部落到 fallback：
/// 入庫一律貸「進貨暫估應付 2152」、出庫一律借「報廢損失 5210」。
///
/// 兩件事因此同時錯：
/// ① **借入（IB）貸 2152**——那個科目的語意是「已收貨未收發票，等 AP 來沖」，
///    而借料沒有發票會來。掛進去等於長出一筆 AP 端不知道、永遠沒人沖的負債，
///    且 `GRIR_AGING` 稽核會每天報一筆假異常。
/// ② **還料（BR）與借出（IL）借 5210 報廢損失**——還料給人家記成報廢，
///    憑空多一筆費用而負債留在帳上；料借出去就認損失，而料明明還是我們的。
///
/// 現況之所以沒人踩到，是因為 `UnitPrice` 可為 null，留空時 `totalCost <= 0` 早退、
/// 一筆分錄都不產生。**那是靠使用者不填在保護，不是靠程式**——填了就錯。
/// 所以本檔每一支都刻意帶單價，走的正是原本沒人走過的那條路。
///
/// 語意分界（`TypeCategory` 分不出來，只能用 `TypeCode`）：
/// <list type="bullet">
///   <item><b>IB／BR 借入</b>：借貸關係。進存貨、掛 2170 其他應付款—借入物料，還料時借回</item>
///   <item><b>IL／LR 借出</b>：我們的料借給別人，**所有權沒移轉** → 零分錄</item>
///   <item><b>CI／CR 客供</b>：客戶的料，**不是我們的財產也不是我們的成本** → 零分錄</item>
///   <item><b>SO／SR 樣品</b>：我們自己的料送客戶＝推廣費用 → 5230 樣品費（不是報廢損失）</item>
/// </list>
/// ⚠️ `Sample` 這個 `TypeCategory` 裡同時裝著 CI/CR（別人的財產）與 SO/SR（我們的成本），
/// 兩者語意相反——這正是不能按類別分流的原因。
/// </summary>
public class LoanSampleJournalTests : TestBase
{
    private const string PlantId = "P001";
    private static readonly DateOnly PostDate = new(2026, 3, 1);

    private readonly Mock<IJournalEntryService>       _mockJournal = new();
    private readonly Mock<IInventoryCostLayerService> _mockCostSvc = new();
    private readonly InventoryPostingService    _postingSvc;

    public LoanSampleJournalTests()
    {
        _context.CurrentPlantId = PlantId;

        var dt = new Mock<IDateTimeService>();
        dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(PostDate);

        _postingSvc = new InventoryPostingService(
            _context, new InventoryService(_context),
            _mockCostSvc.Object,
            new Mock<ISystemParamService>().Object, dt.Object,
            journalSvc: _mockJournal.Object);

        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "原料倉", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "LOC1", LocationName = "LOC1" });
        // 客供倉：CI／CR 的唯一合法去處（過帳服務的 ③-b 守門），且本倉不計價
        _context.Warehouses.Add(new Warehouse
        {
            WhCode = "WHCS", WhName = "客供倉", PlantId = PlantId,
            WarehouseType = WarehouseType.CustomerSupplied,
        });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WHCS", LocationCode = "CS1", LocationName = "CS1" });
        _context.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "RAW", CategoryName = "原料",
            CostMethod = CostMethod.MovingAverage, IsActive = true,
        });
        _context.Products.Add(new Product { ProductCode = "MAT-A", CategoryCode = "RAW", IsActive = true });
        // FIFO 品號：客供料在 FIFO 下有一條 MA 路徑碰不到的守門（沒帶單價就丟例外）
        _context.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "RAWF", CategoryName = "原料(FIFO)",
            CostMethod = CostMethod.Fifo, IsActive = true,
        });
        _context.Products.Add(new Product { ProductCode = "MAT-F", CategoryCode = "RAWF", IsActive = true });
        _context.SaveChanges();
    }

    private PostLineDetailRequest Req(string typeCode, string? typeCategory,
                                      decimal qty, decimal? unitPrice, string location = "LOC1",
                                      string productCode = "MAT-A")
        => new()
        {
            PlantId = PlantId, DocNo = $"{typeCode}-001", LineNo = 1,
            ProductCode = productCode, LocationCode = location, Qty = qty,
            TypeCode = typeCode, TypeCategory = typeCategory,
            MovementDate = PostDate, UomCode = "PC", StockUomCode = "PC",
            UnitPrice = unitPrice,
            UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
            CheckAllLinesCompleteAsync = () => Task.FromResult(false),
            SetDocPosted               = () => { },
        };

    /// <summary>出庫測試的前置：先用採購進貨建立 100 個 @ 20 的庫存與均價。</summary>
    private async Task SeedStockAsync()
    {
        await _postingSvc.PostLineDetailAsync(Req("PI", "Purchase", 100m, 20m));
        await _context.SaveChangesAsync();
        _mockJournal.Invocations.Clear();
    }

    private void AssertJournal(string docNo, string debitCode, string creditCode, decimal amount)
        => _mockJournal.Verify(j => j.CreateAutoEntryAsync(
            "INV", docNo, PostDate, PlantId,
            It.Is<IList<JournalLineRequest>>(lines =>
                lines.Sum(l => l.Debit)  == amount &&
                lines.Sum(l => l.Credit) == amount &&
                lines.Where(l => l.AccountCode == debitCode).Sum(l => l.Debit)   == amount &&
                lines.Where(l => l.AccountCode == creditCode).Sum(l => l.Credit) == amount),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

    /// <summary>
    /// 零分錄要驗「一次都沒呼叫」，不是「金額為 0」——後者代表還是開了一張空傳票，
    /// 那會在傳票清單裡留下噪音，而 `AuditJournalBalance` 對 0=0 完全沉默。
    /// </summary>
    private void AssertNoJournal()
        => _mockJournal.Verify(j => j.CreateAutoEntryAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<string>(),
            It.IsAny<IList<JournalLineRequest>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);

    // ── 借入：進存貨 + 掛其他應付款 ──────────────────────────────────────

    [Fact]
    public async Task BorrowIn_DebitsInventory_AndCreditsLoanPayable()
    {
        await _postingSvc.PostLineDetailAsync(Req("IB", "Loan", 50m, 12m));
        await _context.SaveChangesAsync();

        AssertJournal("IB-001",
            StandardAccounts.RawMaterialInventoryCode,
            StandardAccounts.LoanPayableCode, 600m);
    }

    /// <summary>
    /// 還料是把負債還掉：借 2170／貸 1401。
    /// 🚨 原實作借的是 5210 報廢損失——還料給人家記成報廢，
    /// 帳上憑空多一筆費用，而 2170 那筆負債永遠留著。
    /// </summary>
    [Fact]
    public async Task BorrowInReturn_DebitsLoanPayable_AndCreditsInventory()
    {
        await SeedStockAsync();

        await _postingSvc.PostLineDetailAsync(Req("BR", "Loan", 10m, null));
        await _context.SaveChangesAsync();

        AssertJournal("BR-001",
            StandardAccounts.LoanPayableCode,
            StandardAccounts.RawMaterialInventoryCode, 200m);
    }

    /// <summary>
    /// 🚨 借入不得貸「進貨暫估應付」。那個科目由採購鏈的 AP 認列來沖，
    /// 借料沒有發票會來——掛進去是一筆 AP 端不知道、永遠沒人沖的負債，
    /// 而且 `GRIR_AGING` 會天天報一筆假異常。
    /// （反面斷言單獨一支：上面那支即使把 2170 寫成 2152 也會因為「借貸相等」而全綠，
    ///   只有指名道姓禁止 2152 才擋得住。）
    /// </summary>
    [Fact]
    public async Task BorrowIn_MustNotTouchTheGrIrAccrualAccount()
    {
        await _postingSvc.PostLineDetailAsync(Req("IB", "Loan", 50m, 12m));
        await _context.SaveChangesAsync();

        _mockJournal.Verify(j => j.CreateAutoEntryAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<string>(),
            It.Is<IList<JournalLineRequest>>(lines =>
                lines.Any(l => l.AccountCode == StandardAccounts.GrIrAPClearingCode)),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 借出：所有權未移轉 → 零分錄 ──────────────────────────────────────

    /// <summary>
    /// 借出是我們的料換了地點，所有權沒有移轉。
    /// 🚨 原實作借 5210 報廢損失——料借出去就認一筆損失，而料還在我們帳上；
    /// 等它還回來（LR）又貸一次存貨，兩趟下來存貨少一份、費用多一筆。
    /// </summary>
    [Fact]
    public async Task LendOut_ProducesNoJournal()
    {
        await SeedStockAsync();

        await _postingSvc.PostLineDetailAsync(Req("IL", "Loan", 10m, null));
        await _context.SaveChangesAsync();

        AssertNoJournal();
    }

    [Fact]
    public async Task LendOutReturn_ProducesNoJournal()
    {
        await _postingSvc.PostLineDetailAsync(Req("LR", "Loan", 10m, 20m));
        await _context.SaveChangesAsync();

        AssertNoJournal();
    }

    // ── 客供：不是我們的財產 → 零分錄 ────────────────────────────────────

    /// <summary>
    /// 客供料是客戶提供自己的料給我們加工，所有權屬客戶。進存貨科目等於
    /// 同時虛增資產與負債；我們只就加工費開發票。
    /// **刻意帶單價**：這一支要證明的正是「即使有人填了金額，也不會入帳」。
    /// </summary>
    [Fact]
    public async Task CustomerSuppliedInbound_ProducesNoJournal()
    {
        await _postingSvc.PostLineDetailAsync(Req("CI", "Sample", 100m, 15m, location: "CS1"));
        await _context.SaveChangesAsync();

        AssertNoJournal();
    }

    [Fact]
    public async Task CustomerSuppliedReturn_ProducesNoJournal()
    {
        await _postingSvc.PostLineDetailAsync(Req("CI", "Sample", 100m, 15m, location: "CS1"));
        await _context.SaveChangesAsync();
        _mockJournal.Invocations.Clear();

        await _postingSvc.PostLineDetailAsync(Req("CR", "Sample", 40m, null, location: "CS1"));
        await _context.SaveChangesAsync();

        AssertNoJournal();
    }

    /// <summary>
    /// 🚨 客供料**即使帶了單價也不得取得成本**。這是「不計入公司資產」的核心斷言：
    /// `Inventory.UnitCost` 恆為 0，六處存貨金額報表（評價／週轉／帳齡／呆滯／
    /// 儀表板／價值表）都是 `QtyOnHand × UnitCost`，所以它們不必逐一加排除條件。
    /// </summary>
    [Fact]
    public async Task CustomerSuppliedInbound_LeavesUnitCostZero_EvenWithAPrice()
    {
        await _postingSvc.PostLineDetailAsync(Req("CI", "Sample", 100m, 15m, location: "CS1"));
        await _context.SaveChangesAsync();

        var inv = _context.Inventories.IgnoreQueryFilters()
            .Single(i => i.LocationCode == "CS1" && i.ProductCode == "MAT-A");

        Assert.Equal(100m, inv.QtyOnHand);   // 數量要管
        Assert.Equal(0m, inv.UnitCost);      // 成本不算
    }

    /// <summary>
    /// 🚨 本次最重要的一支：客供料**不得進到自有料的移動平均分母**。
    ///
    /// 成本池是 (PlantId, ProductCode)，分倉分批都隔不開，所以這件事只能靠
    /// 「把客供倉排除在分母之外」來解。沒有排除時：
    /// <code>
    /// 自有 100 @10 → 客供 100 @0 → 再進自有 100 @20
    ///   正解 (100×10 + 100×20) / 200 = 15
    ///   錯誤 (200×10 + 100×20) / 300 = 13.33
    /// </code>
    /// 而 13.33 是個看起來完全正常的單價，會讓之後每一張出貨的毛利莫名偏高。
    /// </summary>
    [Fact]
    public async Task CustomerSuppliedStock_DoesNotDiluteOwnMaterialAverageCost()
    {
        await _postingSvc.PostLineDetailAsync(Req("PI", "Purchase", 100m, 10m));
        await _context.SaveChangesAsync();

        await _postingSvc.PostLineDetailAsync(Req("CI", "Sample", 100m, 0m, location: "CS1"));
        await _context.SaveChangesAsync();

        // 第二筆自有進貨：分母必須只有自有的 100，不含客供的 100
        await _postingSvc.PostLineDetailAsync(Req("PI", "Purchase", 100m, 20m));
        await _context.SaveChangesAsync();

        var own = _context.Inventories.IgnoreQueryFilters()
            .Single(i => i.LocationCode == "LOC1" && i.ProductCode == "MAT-A");

        Assert.Equal(15m, own.UnitCost);    // 不是 13.3333…

        // 客供倉不得被自有料的均價寫回——否則客戶的料就「有了成本」
        var cs = _context.Inventories.IgnoreQueryFilters()
            .Single(i => i.LocationCode == "CS1" && i.ProductCode == "MAT-A");
        Assert.Equal(0m, cs.UnitCost);
    }

    /// <summary>
    /// 客供進貨落在一般倉必須被擋（過帳服務 ③-b）。
    /// 這道守門是前一支的前提：沒有它，客供料還是會混進計價倉而稀釋均價，
    /// 而症狀是毛利莫名偏高、數量與傳票全部正常。
    /// </summary>
    [Fact]
    public async Task CustomerSuppliedInbound_IntoANormalWarehouse_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _postingSvc.PostLineDetailAsync(Req("CI", "Sample", 100m, 15m, location: "LOC1")));

        PzAssert.Message("Inv_CustomerSuppliedWrongWarehouseFmt", ex.Message);
        Assert.Empty(_context.StockMovements.IgnoreQueryFilters().Where(m => m.TypeCode == "CI"));
    }

    /// <summary>
    /// 🚨 **FIFO 品號的客供入庫必須過得了帳**，而且不留下成本層。
    ///
    /// 這是計價跳過那道前置在 `InventoryPostingService` 層獨有的價值：
    /// MA 路徑另有 `InventoryService` 的 early return 兜著，FIFO 沒有。
    /// 客供入庫本來就沒有單價可帶，而沒帶單價的入庫會被 FIFO 守門丟例外
    /// ——於是「客供料＋FIFO 品號」這個完全正常的組合**根本過不了帳**，
    /// 而錯誤訊息講的是成本層，跟客供料八竿子打不著。
    /// </summary>
    [Fact]
    public async Task CustomerSuppliedInbound_OnAFifoProduct_PostsWithoutACostLayer()
    {
        await _postingSvc.PostLineDetailAsync(
            Req("CI", "Sample", 100m, null, location: "CS1", productCode: "MAT-F"));
        await _context.SaveChangesAsync();

        var inv = _context.Inventories.IgnoreQueryFilters()
            .Single(i => i.LocationCode == "CS1" && i.ProductCode == "MAT-F");
        Assert.Equal(100m, inv.QtyOnHand);
        Assert.Equal(0m, inv.UnitCost);

        // 不得為客戶的料建成本層——建了就等於宣告它有取得成本
        _mockCostSvc.Verify(c => c.AddLayerAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<DateOnly>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// 反面守門：客供料要能被**領去生產**（`IO` 從客供倉出庫）。
    /// 守門只擋「客供進貨落在一般倉」一個方向；把它寫成「非 CI/CR 不得動客供倉」
    /// 會讓客供料領不出來，而客供料的用途就是做該客戶的訂單。
    /// </summary>
    [Fact]
    public async Task CustomerSuppliedStock_CanStillBeIssuedToProduction()
    {
        await _postingSvc.PostLineDetailAsync(Req("CI", "Sample", 100m, 0m, location: "CS1"));
        await _context.SaveChangesAsync();

        await _postingSvc.PostLineDetailAsync(Req("IO", "Production", 30m, null, location: "CS1"));
        await _context.SaveChangesAsync();

        var cs = _context.Inventories.IgnoreQueryFilters()
            .Single(i => i.LocationCode == "CS1" && i.ProductCode == "MAT-A");
        Assert.Equal(70m, cs.QtyOnHand);
    }

    // ── 樣品：我們的成本，但是推廣費用不是報廢損失 ──────────────────────

    /// <summary>
    /// 樣品出貨用的是我們自己的料，所以**要認成本**——這是它與客供料的分界。
    /// 但科目是樣品費而非報廢損失：樣品是照計畫送出去買機會，報廢是實體損壞認賠。
    /// 混在一起之後「報廢率」這個指標會隨業務拜訪量起伏。
    /// </summary>
    [Fact]
    public async Task SampleShipment_DebitsSampleExpense_NotScrapLoss()
    {
        await SeedStockAsync();

        await _postingSvc.PostLineDetailAsync(Req("SO", "Sample", 5m, null));
        await _context.SaveChangesAsync();

        AssertJournal("SO-001",
            StandardAccounts.SampleExpenseCode,
            StandardAccounts.RawMaterialInventoryCode, 100m);
    }

    /// <summary>樣品退回沖回樣品費，不是貸「進貨暫估應付」。</summary>
    [Fact]
    public async Task SampleReturn_CreditsSampleExpense()
    {
        await _postingSvc.PostLineDetailAsync(Req("SR", "Sample", 5m, 20m));
        await _context.SaveChangesAsync();

        AssertJournal("SR-001",
            StandardAccounts.RawMaterialInventoryCode,
            StandardAccounts.SampleExpenseCode, 100m);
    }

    // ── 回歸護欄：既有類別不得被這次改動波及 ────────────────────────────

    /// <summary>
    /// 採購進貨仍須貸「進貨暫估應付」。新增的 TypeCode 分流如果攔得太寬
    /// （例如誤把 `Purchase` 也算進「所有權未移轉」），這一支會紅。
    /// </summary>
    [Fact]
    public async Task PurchaseInbound_StillCreditsGrIrAccrual()
    {
        await _postingSvc.PostLineDetailAsync(Req("PI", "Purchase", 10m, 25m));
        await _context.SaveChangesAsync();

        AssertJournal("PI-001",
            StandardAccounts.RawMaterialInventoryCode,
            StandardAccounts.GrIrAPClearingCode, 250m);
    }

    /// <summary>報廢仍須借「報廢損失」——樣品費分支不得把它一起吃掉。</summary>
    [Fact]
    public async Task ScrapOutbound_StillDebitsScrapLoss()
    {
        await SeedStockAsync();

        await _postingSvc.PostLineDetailAsync(Req("IJ", "Scrap", 3m, null));
        await _context.SaveChangesAsync();

        AssertJournal("IJ-001",
            StandardAccounts.ScrapLossCode,
            StandardAccounts.RawMaterialInventoryCode, 60m);
    }
}
