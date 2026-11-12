using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 製造簡化模式 end-to-end（mfg-domain.md §25）：MRP 展開 → 自動投產。
///
/// 這裡刻意用**真實** <see cref="WorkOrderReleaseService"/>（非 mock）串起 MrpEngine，
/// 因為 §25 的風險不在「有沒有呼叫」，而在「投產時該保留的下游副作用有沒有一起發生」——
/// 標準成本快照、Push 料領料單這兩件事漏掉時不會拋錯，只會在幾週後以錯帳的形式浮現。
///
/// 每個案例都成對驗證兩種模式：關閉時必須與既有行為**逐項相同**（工單停在 Planned、無領料單）。
/// </summary>
public class MfgSimplifiedModeTests : TestBase
{
    private const string PlantId = "P001";
    private static readonly DateTime Today = new(2026, 5, 25, 8, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IDocNumberService> _mockDocNo = new();
    private readonly Mock<IDateTimeService> _mockDt = new();
    private readonly Mock<IPlantParamService> _mockSysParam = new();
    private readonly Mock<IApprovalService> _mockApproval = new();
    private readonly Mock<IUserContext> _mockUser = new();

    public MfgSimplifiedModeTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockDt.Setup(d => d.Now).Returns(Today);
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns("usr1");
        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string?>()))
                     .ReturnsAsync((DocumentApprovalConfig?)null);

        var seq = 0;
        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(),
                      It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync((string type, string _, DateOnly? __, CancellationToken ___) => $"{type}-{++seq:D4}");

        // 未設定的參數一律回 null（＝乾淨部署），個別案例再覆寫需要的鍵
        _mockSysParam.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("false");
    }

    /// <summary>切換 §25 製造簡化模式開關。<c>null</c> ＝ 從未設定過（乾淨部署）。</summary>
    private void SetMfgSimplified(string? value) =>
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(value);

    private MrpEngine MakeEngine()
    {
        var reservations = new InventoryReservationService(_context);
        var release = new WorkOrderReleaseService(
            _context, _mockApproval.Object, _mockSysParam.Object, _mockDocNo.Object, reservations, _mockUser.Object,
            _mockDt.Object);
        return new MrpEngine(_context, reservations, _mockDocNo.Object, _mockDt.Object, _mockSysParam.Object, release);
    }

    // ── 測試資料 ────────────────────────────────────────────────────────────

    private void AddProduct(string code, string mrpMode, string? routingId = null, decimal standardCost = 0m)
        => _context.Products.Add(new Product
        {
            ProductCode = code, ProductName = code, StockUom = "PC",
            MrpMode = mrpMode, RoutingId = routingId, StandardCost = standardCost
        });

    private void AddRouting(string routingId, string productCode, params (string mat, decimal qty)[] boms)
    {
        var op = new RoutingOperation
        {
            RoutingId = routingId, OperationSeq = 10, OperationName = "OP", WorkCenterId = "WC1",
            TM_SETUP = 0, TM_UNIT = 0, CD_BOR = OperationMode.WholeOrder, QT_BOR = 1, UomCode = "PC"
        };
        var seq = 0;
        foreach (var (mat, qty) in boms)
            op.Boms.Add(new RoutingBom
            {
                RoutingId = routingId, OperationSeq = 10, BomSeq = ++seq,
                ProductCode = mat, Quantity = qty, UomCode = "PC"
            });

        _context.Routings.Add(new Routing
        {
            RoutingId = routingId, ProductCode = productCode, PlantId = PlantId,
            BatchSize = 1, BufferDays = 0, IsActive = true,
            Operations = new List<RoutingOperation> { op }
        });
    }

    private string AddRun(string product, decimal qty)
    {
        const string runId = "MRP-0001";
        var run = new MrpRun { MrpRunId = runId, PlantId = PlantId, Status = MrpRunStatus.Draft };
        run.Lines.Add(new MrpRunLine
        {
            MrpRunId = runId, LineSeq = 1, ProductCode = product,
            RequiredQty = qty, RequiredDate = Today.AddDays(365)
        });
        _context.MrpRuns.Add(run);
        return runId;
    }

    private void AddInventory(string product, decimal onHand)
        => _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = product, LocationCode = "L1", LotNo = "", QtyOnHand = onHand
        });

    /// <summary>FG(自製，用料 RM 2 個) ← MRP 需求 10；RM 備妥。</summary>
    private async Task<string> SeedSingleLevelAsync(decimal rmOnHand = 999m)
    {
        AddProduct("FG", "M", "R_FG");
        AddProduct("RM", "P", standardCost: 3m);
        AddRouting("R_FG", "FG", ("RM", 2));
        AddInventory("RM", rmOnHand);
        var runId = AddRun("FG", 10m);
        await _context.SaveChangesAsync();
        return runId;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 開關關閉 ＝ 既有行為必須完全不變
    // ══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null)]      // 從未設定（乾淨部署／既有部署升級）
    [InlineData("false")]
    [InlineData("")]
    public async Task Expand_SimplifiedOff_WorkOrderStaysPlanned(string? paramValue)
    {
        SetMfgSimplified(paramValue);
        var runId = await SeedSingleLevelAsync();

        await MakeEngine().RunAsync(runId);

        var wo = await _context.WorkOrders.AsNoTracking().SingleAsync();
        Assert.Equal(WorkOrderStatus.Planned, wo.Status);
        Assert.Equal(0m, wo.StandardMaterialCost);                     // 成本快照在投產時才拍
        Assert.Empty(await _context.MaterialSlips.AsNoTracking().ToListAsync());
    }

    // ══════════════════════════════════════════════════════════════════════
    // 開關開啟 ＝ 展開即投產，且下游必需品同時發生
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Expand_SimplifiedOn_AutoReleasesWorkOrder()
    {
        SetMfgSimplified("true");
        var runId = await SeedSingleLevelAsync();

        var result = await MakeEngine().RunAsync(runId);

        var wo = await _context.WorkOrders.AsNoTracking().SingleAsync();
        Assert.Equal(WorkOrderStatus.Released, wo.Status);
        Assert.Equal(DocumentApprovalStatus.Approved, wo.ApprovalStatus);   // §25.3-⑤ 跳過簽核
        Assert.Equal(1, result.BatchCount);
    }

    /// <summary>§25.3-⑥：領料單漏產生不會拋錯，只會讓料帳從此對不起來——必須有測試釘住。</summary>
    [Fact]
    public async Task Expand_SimplifiedOn_CreatesPushMaterialSlip()
    {
        SetMfgSimplified("true");
        var runId = await SeedSingleLevelAsync();

        await MakeEngine().RunAsync(runId);

        var wo = await _context.WorkOrders.AsNoTracking().SingleAsync();
        var slip = await _context.MaterialSlips.AsNoTracking().Include(s => s.Lines)
            .SingleAsync(s => s.SourceDocType == "WorkOrder" && s.SourceDocId == wo.WorkOrderId);
        Assert.Equal(MaterialSlipStatus.Draft, slip.Status);
        var line = Assert.Single(slip.Lines);
        Assert.Equal("RM", line.ProductCode);
        Assert.Equal(20m, line.RequestedQty);      // 10 × 2
    }

    /// <summary>§25.3-④：成本快照漏拍會讓成本差異分析與 CST 成本鏈全空。</summary>
    [Fact]
    public async Task Expand_SimplifiedOn_TakesStandardCostSnapshot()
    {
        SetMfgSimplified("true");
        var runId = await SeedSingleLevelAsync();

        await MakeEngine().RunAsync(runId);

        var wo = await _context.WorkOrders.AsNoTracking().SingleAsync();
        Assert.Equal(60m, wo.StandardMaterialCost);   // 20 個 × 標準成本 3
    }

    /// <summary>§25.3-②：缺料不擋單（改由開工時的 mfg_material_gate 軟閘接手），但必須據實回報。</summary>
    [Fact]
    public async Task Expand_SimplifiedOn_MaterialShortage_ReleasesWithWarning()
    {
        SetMfgSimplified("true");
        var runId = await SeedSingleLevelAsync(rmOnHand: 0m);

        var result = await MakeEngine().RunAsync(runId);

        var wo = await _context.WorkOrders.AsNoTracking().SingleAsync();
        Assert.Equal(WorkOrderStatus.Released, wo.Status);
        Assert.Equal(MaterialReadinessStatus.Shortage, wo.MaterialReadinessStatus);
        Assert.Contains(result.Warnings, w => w.Contains(wo.WorkOrderId) && w.Contains("RM"));
    }

    /// <summary>多階 BOM：整條鏈都要投產，且半成品先於成品（與 §24.1 整批投產同序）。</summary>
    [Fact]
    public async Task Expand_SimplifiedOn_MultiLevelBom_ReleasesWholeChain()
    {
        SetMfgSimplified("true");
        AddProduct("FG", "M", "R_FG");
        AddProduct("SA", "M", "R_SA");
        AddProduct("RM", "P", standardCost: 1m);
        AddRouting("R_FG", "FG", ("SA", 1));
        AddRouting("R_SA", "SA", ("RM", 2));
        AddInventory("RM", 999m);
        var runId = AddRun("FG", 10m);
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(runId);

        var wos = await _context.WorkOrders.AsNoTracking().ToListAsync();
        Assert.Equal(2, wos.Count);
        Assert.All(wos, w => Assert.Equal(WorkOrderStatus.Released, w.Status));
    }

    /// <summary>展開沒產出任何生產批時不得誤觸自動投產（例如需求全被現有庫存吃掉）。</summary>
    [Fact]
    public async Task Expand_SimplifiedOn_NothingExpanded_NoRelease()
    {
        SetMfgSimplified("true");
        AddProduct("FG", "M", "R_FG");
        AddProduct("RM", "P");
        AddRouting("R_FG", "FG", ("RM", 2));
        AddInventory("FG", 999m);       // 成品庫存足以覆蓋需求 → 無淨需求
        var runId = AddRun("FG", 10m);
        await _context.SaveChangesAsync();

        var result = await MakeEngine().RunAsync(runId);

        Assert.Empty(await _context.WorkOrders.AsNoTracking().ToListAsync());
        Assert.Empty(result.Warnings);
    }
}
