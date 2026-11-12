using Microsoft.EntityFrameworkCore;
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
/// §5.18 庫存異動 → 分錄借貸驗證：
/// TotalCost = UnitCost × Qty（過帳至 JournalEntry 的借貸金額基礎）。
/// 注意：JournalEntry 自動過帳尚未整合至 InventoryPostingService（roadmap 待辦）；
/// 本測試驗證 StockMovement.TotalCost 的計算正確性，作為未來分錄整合的契約基礎。
/// </summary>
public class StockMovementJournalTests : TestBase
{
    private const string PlantId = "P001";

    private readonly InventoryService                  _invSvc;
    private readonly Mock<IInventoryCostLayerService>  _mockCostSvc;
    private readonly Mock<ISystemParamService>         _mockSysParam;
    private readonly Mock<IDateTimeService>            _mockDt;
    private readonly Mock<IJournalEntryService>        _mockJournal = new();
    private readonly InventoryPostingService           _postingSvc;

    private static readonly DateOnly PostDate = new DateOnly(2026, 3, 1);

    public StockMovementJournalTests()
    {
        _context.CurrentPlantId = PlantId;
        _invSvc       = new InventoryService(_context);
        _mockCostSvc  = new Mock<IInventoryCostLayerService>();
        _mockSysParam = new Mock<ISystemParamService>();
        _mockDt       = new Mock<IDateTimeService>();
        _mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(PostDate);

        _postingSvc = new InventoryPostingService(
            _context, _invSvc, _mockCostSvc.Object, _mockSysParam.Object, _mockDt.Object,
            journalSvc: _mockJournal.Object);

        SeedCommonData();
    }

    private void SeedCommonData()
    {
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "原料倉", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "LOC1", LocationName = "LOC1" });

        var cat = new ProductCategory
        {
            CategoryCode = "RAW", CategoryName = "原料",
            CostMethod   = CostMethod.MovingAverage, IsActive = true
        };
        _context.ProductCategories.Add(cat);
        _context.Products.Add(new Product { ProductCode = "MAT-A", CategoryCode = "RAW", IsActive = true });
        _context.Products.Add(new Product { ProductCode = "MAT-B", CategoryCode = "RAW", IsActive = true });
        _context.SaveChanges();
    }

    private PostLineDetailRequest BuildInboundReq(string docNo, string productCode,
        decimal qty, decimal unitPrice, string? sourceDocId = null)
        => new PostLineDetailRequest
        {
            PlantId              = PlantId,
            DocNo                = docNo,
            LineNo               = 1,
            ProductCode          = productCode,
            LocationCode         = "LOC1",
            Qty                  = qty,
            TypeCode             = "PI",
            TypeCategory         = "Purchase",
            MovementDate         = PostDate,
            UomCode              = "PC",
            StockUomCode         = "PC",
            UnitPrice            = unitPrice,
            SourceDocId          = sourceDocId,
            UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
            CheckAllLinesCompleteAsync = () => Task.FromResult(false),
            SetDocPosted               = () => { },
        };

    private PostLineDetailRequest BuildOutboundReq(string docNo, string productCode, decimal qty)
        => new PostLineDetailRequest
        {
            PlantId              = PlantId,
            DocNo                = docNo,
            LineNo               = 1,
            ProductCode          = productCode,
            LocationCode         = "LOC1",
            Qty                  = qty,
            TypeCode             = "IJ",    // 報廢 (Direction=-1, RequireDoc=false)
            MovementDate         = PostDate,
            UomCode              = "PC",
            StockUomCode         = "PC",
            UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
            CheckAllLinesCompleteAsync = () => Task.FromResult(false),
            SetDocPosted               = () => { },
        };

    // ════════════════════════════════════════════════════════════════════════
    // F1：入庫 TotalCost = UnitPrice × Qty（分錄借方金額基礎）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.18 EP Valid: 入庫 100 個 @ 25 → TotalCost = 2500，Direction = 1</summary>
    [Fact]
    public async Task Inbound_TotalCost_EqualQtyTimesUnitPrice()
    {
        await _postingSvc.PostLineDetailAsync(BuildInboundReq("GR001", "MAT-A", qty: 100m, unitPrice: 25m));
        await _context.SaveChangesAsync();

        var sm = await _context.StockMovements.IgnoreQueryFilters()
            .FirstAsync(s => s.DocNo == "GR001");

        Assert.Equal(25m,   sm.UnitCost);
        Assert.Equal(2500m, sm.TotalCost);
        Assert.Equal(1,     sm.Direction);
    }

    // ════════════════════════════════════════════════════════════════════════
    // F2：出庫 TotalCost 使用 AVCO（移動平均成本）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.18 EP Valid: 先入庫 @ 40，出庫 → TotalCost 使用 AVCO=40，Direction=-1</summary>
    [Fact]
    public async Task Outbound_TotalCost_UsesAvgCost()
    {
        // 先入庫建立成本
        await _postingSvc.PostLineDetailAsync(BuildInboundReq("GR002", "MAT-B", qty: 200m, unitPrice: 40m));
        await _context.SaveChangesAsync();

        // 出庫
        await _postingSvc.PostLineDetailAsync(BuildOutboundReq("IS001", "MAT-B", qty: 50m));
        await _context.SaveChangesAsync();

        var sm = await _context.StockMovements.IgnoreQueryFilters()
            .FirstAsync(s => s.DocNo == "IS001");

        Assert.Equal(40m,   sm.UnitCost);
        Assert.Equal(2000m, sm.TotalCost);
        Assert.Equal(-1,    sm.Direction);
    }

    // ════════════════════════════════════════════════════════════════════════
    // F3：TotalCost 精度 – 小數 4 位
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.18 BVA Precision: 0.0001 × 99.9999 → TotalCost = 0.01（AVCO 整數精度）</summary>
    [Fact]
    public async Task Inbound_TotalCost_DecimalPrecision()
    {
        await _postingSvc.PostLineDetailAsync(
            BuildInboundReq("GR003", "MAT-A", qty: 0.5m, unitPrice: 0.0002m));
        await _context.SaveChangesAsync();

        var sm = await _context.StockMovements.IgnoreQueryFilters()
            .FirstAsync(s => s.DocNo == "GR003");

        // TotalCost = 0.5 × 0.0002 = 0.0001
        Assert.Equal(0.0001m, sm.TotalCost);
        Assert.Equal(0.0002m, sm.UnitCost);
    }

    // ════════════════════════════════════════════════════════════════════════
    // F4：SourceDocNo 追蹤回進貨單
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.18 EP Valid: StockMovement.SourceDocNo 對應來源進貨單號</summary>
    [Fact]
    public async Task Inbound_SourceDocNo_TracksBackToGR()
    {
        var req = BuildInboundReq("PE-SLIP-001", "MAT-A", qty: 10m, unitPrice: 100m, sourceDocId: "GR-SOURCE-001");
        req.SourceLineNo = 1;

        await _postingSvc.PostLineDetailAsync(req);
        await _context.SaveChangesAsync();

        var sm = await _context.StockMovements.IgnoreQueryFilters()
            .FirstAsync(s => s.DocNo == "PE-SLIP-001");

        Assert.Equal("GR-SOURCE-001", sm.SourceDocNo);
        Assert.Equal(1, sm.SourceDocLineNo);
    }

    // ════════════════════════════════════════════════════════════════════════
    // F5：多行同文件 – 各行 TotalCost 獨立計算
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.18 EP Valid: 同一憑證兩行各自 TotalCost 獨立（不相互影響）</summary>
    [Fact]
    public async Task Inbound_MultiLine_EachTotalCostIndependent()
    {
        var req1 = BuildInboundReq("GR005", "MAT-A", qty: 100m, unitPrice: 10m);
        var req2 = BuildInboundReq("GR005", "MAT-B", qty:  50m, unitPrice: 20m);
        req2.LineNo = 2;

        await _postingSvc.PostLineDetailAsync(req1);
        await _postingSvc.PostLineDetailAsync(req2);
        await _context.SaveChangesAsync();

        var movements = await _context.StockMovements.IgnoreQueryFilters()
            .Where(s => s.DocNo == "GR005")
            .OrderBy(s => s.DocLineNo)
            .ToListAsync();

        Assert.Equal(2, movements.Count);
        Assert.Equal(1000m, movements[0].TotalCost);  // 100 × 10
        Assert.Equal(1000m, movements[1].TotalCost);  // 50 × 20
    }

    // ════════════════════════════════════════════════════════════════════════
    // F6：最小有效金額不被截斷為零
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.18 BVA: TotalCost = 0.0001（最小有效單位不得截斷為 0）</summary>
    [Fact]
    public async Task Inbound_MinimumCost_NotTruncatedToZero()
    {
        await _postingSvc.PostLineDetailAsync(
            BuildInboundReq("GR006", "MAT-A", qty: 0.0001m, unitPrice: 1m));
        await _context.SaveChangesAsync();

        var sm = await _context.StockMovements.IgnoreQueryFilters()
            .FirstAsync(s => s.DocNo == "GR006");

        Assert.Equal(0.0001m, sm.TotalCost);
        Assert.NotEqual(0m,   sm.TotalCost);
    }

    // ════════════════════════════════════════════════════════════════════════
    // F7：借貸平衡驗證（TODO：JournalEntry 整合後啟用）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// §5.18 Journal Balance: StockMovement.TotalCost = JournalLine.Debit（存貨科目）。
    /// 入庫 50 個 @ 30 → TotalCost = 1500；
    /// IJournalEntryService 被呼叫一次，借 原料存貨 (1401) = 1500 / 貸 進貨暫估應付 (2152) = 1500。
    /// </summary>
    [Fact]
    public async Task Inbound_TotalCost_EqualsExpectedDebitAmount()
    {
        await _postingSvc.PostLineDetailAsync(
            BuildInboundReq("GR007", "MAT-A", qty: 50m, unitPrice: 30m));
        await _context.SaveChangesAsync();

        var sm = await _context.StockMovements.IgnoreQueryFilters()
            .FirstAsync(s => s.DocNo == "GR007");

        Assert.Equal(1500m, sm.TotalCost);

        // 驗證分錄已自動產生：借 原料存貨 1500 / 貸 進貨暫估應付 1500
        _mockJournal.Verify(j => j.CreateAutoEntryAsync(
            "INV",
            "GR007",
            It.IsAny<DateOnly>(),
            It.IsAny<string>(),
            It.Is<IList<JournalLineRequest>>(lines =>
                lines.Any(l => l.AccountCode == StandardAccounts.RawMaterialInventoryCode && l.Debit  == 1500m) &&
                lines.Any(l => l.AccountCode == StandardAccounts.GrIrAPClearingCode        && l.Credit == 1500m)),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 簡化模式過帳（PostDocSimplifiedAsync）也必須入總帳
    // ════════════════════════════════════════════════════════════════════════

    private PostDocSimplifiedRequest BuildSimplifiedReq(
        string docNo, string typeCode, string typeCategory,
        params (int LineNo, string Product, decimal Qty, decimal? UnitPrice)[] lines)
        => new()
        {
            PlantId      = PlantId,
            DocNo        = docNo,
            MovementDate = PostDate,
            TypeCode     = typeCode,
            TypeCategory = typeCategory,
            CreatedBy    = "tester",
            Lines = lines.Select(l => new SimplifiedPostLine
            {
                LineNo        = l.LineNo,
                ProductCode   = l.Product,
                WhCode        = "WH01",
                LocationCode  = "LOC1",
                LotNo         = "",
                Qty           = l.Qty,
                UomCode       = "PC",
                UnitPrice     = l.UnitPrice,
                SetQtyPutAway = _ => { },
            }).ToList(),
            SetDocPosted = () => { },
        };

    /// <summary>
    /// 簡化模式先前完全不入總帳：開了 inv_simplified_mode 的部署，
    /// 存貨與 GR-IR 在總帳上憑空消失，財報與庫存帳從第一天就對不起來。
    /// 多明細彙總成一張分錄，金額與方向須與逐行過帳同口徑。
    /// </summary>
    [Fact]
    public async Task SimplifiedInbound_PostsAggregatedJournal()
    {
        await _postingSvc.PostDocSimplifiedAsync(BuildSimplifiedReq(
            "GR-S1", "PI", "Purchase",
            (1, "MAT-A", 10m, 25m),      // 250
            (2, "MAT-B", 4m,  50m)));    // 200
        await _context.SaveChangesAsync();

        _mockJournal.Verify(j => j.CreateAutoEntryAsync(
            "INV", "GR-S1", PostDate, PlantId,
            It.Is<IList<JournalLineRequest>>(lines =>
                lines.Sum(l => l.Debit) == 450m &&
                lines.Sum(l => l.Credit) == 450m &&
                lines.Where(l => l.AccountCode == StandardAccounts.RawMaterialInventoryCode).Sum(l => l.Debit) == 450m &&
                lines.Where(l => l.AccountCode == StandardAccounts.GrIrAPClearingCode).Sum(l => l.Credit) == 450m),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>簡化模式出庫：銷售類走銷貨成本，其餘走報廢損失（與逐行過帳同規則）。</summary>
    [Fact]
    public async Task SimplifiedOutbound_SalesCategory_DebitsCogs()
    {
        // 先建立成本與庫存
        await _postingSvc.PostLineDetailAsync(BuildInboundReq("GR-S2", "MAT-A", qty: 100m, unitPrice: 20m));
        await _context.SaveChangesAsync();

        await _postingSvc.PostDocSimplifiedAsync(BuildSimplifiedReq(
            "GS-S1", "IJ", "Sales", (1, "MAT-A", 30m, null)));   // AVCO 20 × 30 = 600
        await _context.SaveChangesAsync();

        _mockJournal.Verify(j => j.CreateAutoEntryAsync(
            "INV", "GS-S1", PostDate, PlantId,
            It.Is<IList<JournalLineRequest>>(lines =>
                lines.Any(l => l.AccountCode == StandardAccounts.CogsCode && l.Debit == 600m) &&
                lines.Any(l => l.AccountCode == StandardAccounts.RawMaterialInventoryCode && l.Credit == 600m)),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// 生產類異動不入帳：領料與繳庫共用同一存貨科目，材料收料時已入存貨，
    /// 生產只是形態轉換；真正的加值（人工＋製費）由 §16.3 吸收分錄統一處理。
    /// 若這裡也入帳，存貨會被灌成兩份人工製費，且領料會被錯記為報廢損失、
    /// 繳庫被錯記為進貨暫估應付。
    /// </summary>
    [Fact]
    public async Task SimplifiedProduction_PostsNoJournal_LeftToAbsorptionEntry()
    {
        await _postingSvc.PostLineDetailAsync(BuildInboundReq("GR-S4", "MAT-A", qty: 100m, unitPrice: 20m));
        await _context.SaveChangesAsync();

        await _postingSvc.PostDocSimplifiedAsync(BuildSimplifiedReq(
            "WO-BF", "IO", "Production", (1, "MAT-A", 30m, null)));
        await _context.SaveChangesAsync();

        // 庫存照扣，只是不入總帳
        Assert.True(await _context.StockMovements.IgnoreQueryFilters().AnyAsync(m => m.DocNo == "WO-BF"));
        _mockJournal.Verify(j => j.CreateAutoEntryAsync(
            It.IsAny<string>(), "WO-BF", It.IsAny<DateOnly>(), It.IsAny<string>(),
            It.IsAny<IList<JournalLineRequest>>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// 退貨要沖回原本的對方科目，不能一律套「入庫貸暫估應付／出庫借報廢損失」：
    /// 進貨退出應借暫估應付（與 AP 退貨回沖對衝歸零），銷貨退回應貸銷貨成本。
    /// 套錯會讓退一次貨就在帳上多一筆假損失或假暫估。
    /// </summary>
    [Fact]
    public async Task SimplifiedPurchaseReturn_DebitsGrIrNotScrapLoss()
    {
        await _postingSvc.PostLineDetailAsync(BuildInboundReq("GR-S5", "MAT-A", qty: 50m, unitPrice: 40m));
        await _context.SaveChangesAsync();

        await _postingSvc.PostDocSimplifiedAsync(BuildSimplifiedReq(
            "GRT-S1", "PF", "Purchase", (1, "MAT-A", 10m, null)));   // AVCO 40 × 10 = 400
        await _context.SaveChangesAsync();

        _mockJournal.Verify(j => j.CreateAutoEntryAsync(
            "INV", "GRT-S1", PostDate, PlantId,
            It.Is<IList<JournalLineRequest>>(lines =>
                lines.Any(l => l.AccountCode == StandardAccounts.GrIrAPClearingCode && l.Debit == 400m) &&
                lines.Any(l => l.AccountCode == StandardAccounts.RawMaterialInventoryCode && l.Credit == 400m) &&
                !lines.Any(l => l.AccountCode == StandardAccounts.ScrapLossCode)),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SimplifiedSalesReturn_CreditsCogs()
    {
        await _postingSvc.PostDocSimplifiedAsync(BuildSimplifiedReq(
            "SRT-S1", "GR", "Sales", (1, "MAT-B", 5m, 60m)));   // 銷貨退回入庫 300
        await _context.SaveChangesAsync();

        _mockJournal.Verify(j => j.CreateAutoEntryAsync(
            "INV", "SRT-S1", PostDate, PlantId,
            It.Is<IList<JournalLineRequest>>(lines =>
                lines.Any(l => l.AccountCode == StandardAccounts.RawMaterialInventoryCode && l.Debit == 300m) &&
                lines.Any(l => l.AccountCode == StandardAccounts.CogsCode && l.Credit == 300m)),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>成本為 0（無單價、無庫存成本）時不產生分錄，避免落一張全 0 的空傳票。</summary>
    [Fact]
    public async Task SimplifiedInbound_ZeroCost_PostsNoJournal()
    {
        await _postingSvc.PostDocSimplifiedAsync(BuildSimplifiedReq(
            "GR-S3", "PI", "Purchase", (1, "MAT-B", 5m, 0m)));
        await _context.SaveChangesAsync();

        _mockJournal.Verify(j => j.CreateAutoEntryAsync(
            It.IsAny<string>(), "GR-S3", It.IsAny<DateOnly>(), It.IsAny<string>(),
            It.IsAny<IList<JournalLineRequest>>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
