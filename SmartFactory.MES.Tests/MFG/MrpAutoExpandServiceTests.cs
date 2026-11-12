using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 訂單核准後自動展開 MRP（mfg-domain.md §25.5，Phase 3）。
///
/// 這裡的關鍵是**冪等**：服務以「已核准訂單的未展開剩餘量」判定，不記事件、不記旗標。
/// 若不冪等，同一張訂單每次存檔都會再展一次，量會翻倍累加——而且不會有任何錯誤訊息。
/// 因此重複呼叫、部分展開、訂單增量三種情境都必須逐一釘住。
/// </summary>
public class MrpAutoExpandServiceTests : TestBase
{
    private const string PlantId = "P001";
    private const string SoId = "SO-0001";

    private readonly Mock<IPlantParamService> _mockSysParam = new();
    private readonly Mock<IDocNumberService> _mockDocNo = new();
    private readonly Mock<IDateTimeService> _mockDt = new();
    private readonly Mock<IUserContext> _mockUser = new();
    private readonly Mock<IMrpRunExecutionService> _mockExecution = new();

    private int _runSeq;

    public MrpAutoExpandServiceTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns("usr1");
        _mockDt.Setup(d => d.Now).Returns(new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc));
        _mockSysParam.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _mockDocNo.Setup(d => d.NextAsync("MrpRun", It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(() => $"MRP-{++_runSeq:D4}");

        // 展開本身另有專屬測試；這裡只驗「有沒有建對執行單、有沒有重複建」，
        // 故把 ExecuteAsync 換成「把執行單標為 Executed」的替身——這正是冪等判定的依據。
        _mockExecution
            .Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string runId, CancellationToken ct) =>
            {
                var run = await _context.MrpRuns.FirstAsync(r => r.MrpRunId == runId, ct);
                run.Status = MrpRunStatus.Executed;
                await _context.SaveChangesAsync(ct);
                return new MrpRunResult { MrpRunId = runId };
            });
    }

    private void EnableSimplified() =>
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");

    private MrpAutoExpandService MakeService() => new(
        _context,
        _mockExecution.Object,
        new MfgSimplifiedModeService(_mockSysParam.Object),
        _mockDocNo.Object,
        _mockUser.Object,
        Timezone.TimezoneTestKit.FixedLocalDate(new DateOnly(2026, 7, 1)).Object);

    // ── 種子 ─────────────────────────────────────────────────────────────────

    private void SeedApprovedSalesOrder(
        DocumentApprovalStatus status = DocumentApprovalStatus.Approved,
        decimal qty = 100m, string productCode = "FG")
    {
        _context.SalesOrders.Add(new SalesOrder
        {
            SalesOrderId = SoId, PlantId = PlantId, ApprovalStatus = status,
            OrderDate = new DateOnly(2026, 5, 20), CompanyCode = "C1", CreatedBy = "usr1",
        });
        _context.SalesOrderLines.Add(new SalesOrderLine
        {
            SalesOrderId = SoId, LineNo = 1, ProductCode = productCode,
            OrderQty = qty, DeliveryDate = new DateOnly(2026, 6, 30), IsClosed = false,
        });
        _context.SaveChanges();
    }

    // ══════════════════════════════════════════════════════════════════════
    // 開關
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SimplifiedOff_DoesNothing()
    {
        SeedApprovedSalesOrder();

        var r = await MakeService().ExpandApprovedOrdersAsync();

        Assert.False(r.Triggered);
        Assert.Empty(await _context.MrpRuns.AsNoTracking().ToListAsync());
        _mockExecution.Verify(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SimplifiedOn_ApprovedOrder_CreatesAndExecutesRun()
    {
        EnableSimplified();
        SeedApprovedSalesOrder(qty: 100m);

        var r = await MakeService().ExpandApprovedOrdersAsync();

        Assert.True(r.Triggered);
        Assert.Equal(1, r.LineCount);
        Assert.Null(r.Error);

        var run = await _context.MrpRuns.AsNoTracking().Include(x => x.Lines).SingleAsync();
        Assert.Equal(r.MrpRunId, run.MrpRunId);
        Assert.Equal(PlantId, run.PlantId);
        var line = Assert.Single(run.Lines);
        Assert.Equal(SoId, line.SalesOrderId);          // 來源訂單號（SO/IO 共用此欄）
        Assert.Equal(1, line.SalesOrderLineSeq);
        Assert.Equal("FG", line.ProductCode);
        Assert.Equal(100m, line.RequiredQty);
        Assert.Equal(new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc), line.RequiredDate);
        _mockExecution.Verify(e => e.ExecuteAsync(run.MrpRunId, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>未核准的訂單不展開——這是「核准後」自動展開，不是「建立後」。</summary>
    [Fact]
    public async Task SimplifiedOn_DraftOrder_NotExpanded()
    {
        EnableSimplified();
        SeedApprovedSalesOrder(status: DocumentApprovalStatus.Draft);

        var r = await MakeService().ExpandApprovedOrdersAsync();

        Assert.False(r.Triggered);
        Assert.Empty(await _context.MrpRuns.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SimplifiedOn_PendingApprovalOrder_NotExpanded()
    {
        EnableSimplified();
        SeedApprovedSalesOrder(status: DocumentApprovalStatus.PendingApproval);

        Assert.False((await MakeService().ExpandApprovedOrdersAsync()).Triggered);
    }

    // ══════════════════════════════════════════════════════════════════════
    // 冪等（本節是整個 Phase 3 最容易出錯、也最不會報錯的地方）
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RepeatedCall_DoesNotExpandTwice()
    {
        EnableSimplified();
        SeedApprovedSalesOrder(qty: 100m);
        var svc = MakeService();

        var first  = await svc.ExpandApprovedOrdersAsync();
        var second = await svc.ExpandApprovedOrdersAsync();

        Assert.True(first.Triggered);
        Assert.False(second.Triggered);                 // 已無未展開剩餘量
        Assert.Single(await _context.MrpRuns.AsNoTracking().ToListAsync());
        Assert.Equal(100m, await _context.MrpRunLines.AsNoTracking().SumAsync(l => l.RequiredQty));
    }

    /// <summary>訂單量增加後只補展「增量」，不重展已展開的部分。</summary>
    [Fact]
    public async Task OrderQtyIncreased_ExpandsOnlyTheDelta()
    {
        EnableSimplified();
        SeedApprovedSalesOrder(qty: 100m);
        var svc = MakeService();
        await svc.ExpandApprovedOrdersAsync();

        var soLine = await _context.SalesOrderLines.FirstAsync();
        soLine.OrderQty = 150m;
        await _context.SaveChangesAsync();

        var r = await svc.ExpandApprovedOrdersAsync();

        Assert.True(r.Triggered);
        var newRun = await _context.MrpRuns.AsNoTracking().Include(x => x.Lines)
            .SingleAsync(x => x.MrpRunId == r.MrpRunId);
        Assert.Equal(50m, Assert.Single(newRun.Lines).RequiredQty);
    }

    /// <summary>作廢生產批對應的展開量不算數，需求會被重新展開（與畫面「可匯入」同口徑）。</summary>
    [Fact]
    public async Task VoidedBatchLine_CountsAsNotExpanded()
    {
        EnableSimplified();
        SeedApprovedSalesOrder(qty: 100m);
        var svc = MakeService();
        await svc.ExpandApprovedOrdersAsync();

        // 模擬展開產物被作廢
        _context.ProductionBatches.Add(new ProductionBatch
        {
            BatchId = "PB-VOID", ProductCode = "FG", Quantity = 100m,
            Status = ProductionBatchStatus.Void, PlantId = PlantId,
            PlannedEndDate = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc),
        });
        var mrpLine = await _context.MrpRunLines.FirstAsync();
        mrpLine.ProductionBatchId = "PB-VOID";
        await _context.SaveChangesAsync();

        var r = await svc.ExpandApprovedOrdersAsync();

        Assert.True(r.Triggered);
        Assert.Equal(100m, (await _context.MrpRuns.AsNoTracking().Include(x => x.Lines)
            .SingleAsync(x => x.MrpRunId == r.MrpRunId)).Lines.Single().RequiredQty);
    }

    /// <summary>已結案的訂單明細不再展開。</summary>
    [Fact]
    public async Task ClosedOrderLine_NotExpanded()
    {
        EnableSimplified();
        SeedApprovedSalesOrder();
        var line = await _context.SalesOrderLines.FirstAsync();
        line.IsClosed = true;
        await _context.SaveChangesAsync();

        Assert.False((await MakeService().ExpandApprovedOrdersAsync()).Triggered);
    }

    // ══════════════════════════════════════════════════════════════════════
    // 內部訂單與失敗處置
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task InternalOrder_AlsoExpanded()
    {
        EnableSimplified();
        _context.InternalOrders.Add(new InternalOrder
        {
            InternalOrderId = "IO-0001", PlantId = PlantId,
            ApprovalStatus = DocumentApprovalStatus.Approved,
            OrderDate = new DateOnly(2026, 5, 20), CreatedBy = "usr1",
        });
        _context.InternalOrderLines.Add(new InternalOrderLine
        {
            InternalOrderId = "IO-0001", LineNo = 1, ProductCode = "FG",
            OrderQty = 40m, DeliveryDate = new DateOnly(2026, 6, 15), IsClosed = false,
        });
        await _context.SaveChangesAsync();

        var r = await MakeService().ExpandApprovedOrdersAsync();

        Assert.True(r.Triggered);
        var line = (await _context.MrpRuns.AsNoTracking().Include(x => x.Lines).SingleAsync()).Lines.Single();
        Assert.Equal("IO-0001", line.SalesOrderId);
        Assert.Equal(40m, line.RequiredQty);
    }

    /// <summary>
    /// 展開失敗不可拋出——訂單核准已經成功落地，拋出會讓使用者以為整筆沒存檔。
    /// 執行單留在 Draft 供人工到 MRP 畫面處理。
    /// </summary>
    [Fact]
    public async Task ExecutionThrows_ReturnsErrorAndLeavesRunAsDraft()
    {
        EnableSimplified();
        SeedApprovedSalesOrder();
        _mockExecution.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new InvalidOperationException("展開失敗原因"));

        var r = await MakeService().ExpandApprovedOrdersAsync();

        Assert.True(r.Triggered);
        Assert.Equal("展開失敗原因", r.Error);
        var run = await _context.MrpRuns.AsNoTracking().SingleAsync();
        Assert.Equal(MrpRunStatus.Draft, run.Status);
    }

    /// <summary>沒有任何已核准訂單時不建空執行單。</summary>
    [Fact]
    public async Task NoApprovedOrders_CreatesNothing()
    {
        EnableSimplified();

        Assert.False((await MakeService().ExpandApprovedOrdersAsync()).Triggered);
        Assert.Empty(await _context.MrpRuns.AsNoTracking().ToListAsync());
    }
}
