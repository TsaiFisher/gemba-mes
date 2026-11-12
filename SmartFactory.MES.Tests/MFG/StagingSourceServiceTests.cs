using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 備料單來源帶料：派工／工單／生產批展開、扣掉已發料與既有備料單鎖量，
/// 以及備料倉三層解析（開單指定 &gt; 工作中心設定 &gt; 系統參數）。
/// </summary>
public class StagingSourceServiceTests : TestBase
{
    private readonly Mock<IPlantParamService> _sysParam = new();
    private readonly Mock<IDocNumberService> _docNumber = new();
    private readonly Mock<IApprovalService> _approval = new();
    private readonly StagingSourceService _svc;

    private const string PlantId = "P001";

    public StagingSourceServiceTests()
    {
        _context.CurrentPlantId = PlantId;
        _docNumber.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("IM-NEW");
        // 廠別收斂用真實作而非 mock：這條規則的重點就是「值在不在這個廠」，
        // 用 mock 蓋掉等於把要驗的東西驗掉了。
        _svc = new StagingSourceService(_context, _sysParam.Object, _docNumber.Object, _approval.Object,
            new PlantDefaultLocationResolver(_context));
        Seed();
    }

    private void Seed()
    {
        _context.ProductCategories.Add(new ProductCategory
            { CategoryCode = "RAW", CategoryName = "原料", IsActive = true });
        _context.Products.Add(new Product
            { ProductCode = "MAT001", ProductName = "料件一", CategoryCode = "RAW", StockUom = "EA", IsActive = true });
        _context.Set<WorkCenter>().Add(new WorkCenter
        {
            WorkCenterCode = "WC1", WorkCenterName = "WC1", PlantId = PlantId,
            StagingWhCode = "STG", StagingLocationCode = "STG-01",
        });
        // 倉庫／儲位主檔：備料倉解析的每一層都要驗值在不在本廠，沒有主檔就一律退回未設定。
        // `Warehouse` 主鍵是 WhCode 單欄（全域唯一），故 OTHER-WH 屬於別廠這件事是可表達的。
        _context.Set<Warehouse>().AddRange(
            new Warehouse { WhCode = "STG",  WhName = "備料倉",   PlantId = PlantId, IsActive = true },
            new Warehouse { WhCode = "STG2", WhName = "備料倉二", PlantId = PlantId, IsActive = true },
            new Warehouse { WhCode = "OTHER-WH", WhName = "他廠倉", PlantId = "P999", IsActive = true });
        _context.Set<StorageLocation>().AddRange(
            new StorageLocation { WhCode = "STG",  LocationCode = "STG-01",  LocationName = "備料位",   IsActive = true },
            new StorageLocation { WhCode = "STG2", LocationCode = "STG2-01", LocationName = "備料位二", IsActive = true },
            new StorageLocation { WhCode = "OTHER-WH", LocationCode = "OTHER-01", LocationName = "他廠位", IsActive = true });
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = "WO001", ProductionBatchId = "PB-1", ProductCode = "P-001",
            Quantity = 10m, PlantId = PlantId, Status = WorkOrderStatus.Released,
        });
        _context.WorkOrderOperations.Add(new WorkOrderOperation
        {
            WorkOrderId = "WO001", OperationSeq = 1, WorkCenterId = "WC1", OperationName = "OP10",
        });
        _context.Dispatches.Add(new Dispatch
        {
            DispatchId = "D1", WorkOrderId = "WO001", OperationSeq = 1, PlantId = PlantId,
            Quantity = 10m, Status = DispatchStatus.Scheduled,
        });
        _context.Set<DispatchMaterial>().Add(new DispatchMaterial
        {
            DispatchId = "D1", BomSeq = 1, ProductCode = "MAT001",
            RequiredQty = 20m, IssuedQty = 5m, UomCode = "EA",
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task GetLines_Dispatch_NetsOffIssuedQty()
    {
        var lines = await _svc.GetLinesAsync("Dispatch", "D1", PlantId);

        var line = Assert.Single(lines);
        Assert.Equal("MAT001", line.ProductCode);
        Assert.Equal(15m, line.RequiredQty);           // 20 需求 − 5 已發
        Assert.Equal("WO001", line.SourceWorkOrderId);
        Assert.Equal("D1", line.SourceDispatchId);
        Assert.Equal("料件一", line.ProductName);
    }

    // 2026-10-09 Android 實測 N-3：派工的料已經在工單發料全部發完，從同一張派工建備料單仍帶出全量。
    // 發料過帳只累加 WorkOrderMaterial.IssuedQty，DispatchMaterial.IssuedQty 沒有人回寫，
    // 所以派工層級必須以工單用料的已發量為準。
    private void AddWorkOrderMaterial(decimal issued, MaterialIssueMethod method = MaterialIssueMethod.Push)
    {
        _context.WorkOrderMaterials.Add(new WorkOrderMaterial
        {
            WorkOrderId = "WO001", OperationSeq = 1, BomSeq = 1, ProductCode = "MAT001",
            RequiredQty = 20m, IssuedQty = issued, UomCode = "EA", IssueMethod = method,
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task GetLines_Dispatch_WorkOrderMaterialFullyIssued_ReturnsEmpty()
    {
        AddWorkOrderMaterial(issued: 20m);

        Assert.Empty(await _svc.GetLinesAsync("Dispatch", "D1", PlantId));
    }

    [Fact]
    public async Task GetLines_Dispatch_CappedByWorkOrderMaterialRemaining()
    {
        AddWorkOrderMaterial(issued: 12m);

        var line = Assert.Single(await _svc.GetLinesAsync("Dispatch", "D1", PlantId));
        Assert.Equal(8m, line.RequiredQty);            // min(派工 20−5, 工單 20−12)
    }

    [Fact]
    public async Task GetLines_Dispatch_BackflushMaterialExcluded()
    {
        AddWorkOrderMaterial(issued: 0m, MaterialIssueMethod.Backflush);

        Assert.Empty(await _svc.GetLinesAsync("Dispatch", "D1", PlantId));
    }

    [Fact]
    public async Task GetLines_DeductsExistingStagingOrder()
    {
        // 同一張派工重複開備料單時，要扣掉既有備料單已鎖的量，
        // 否則同一批料被鎖兩次，第二張確認時會因可用量不足而擋單。
        _context.MaterialStagingOrders.Add(new MaterialStagingOrder
        {
            StagingId = "IM-1", PlantId = PlantId, Status = "Confirmed", CreatedBy = "u1",
            SourceType = "Dispatch", SourceId = "D1",
            Lines =
            {
                new MaterialStagingLine
                {
                    StagingId = "IM-1", LineNo = 1, ProductCode = "MAT001",
                    UomCode = "EA", RequiredQty = 10m, SourceWorkOrderId = "WO001", SourceDispatchId = "D1",
                }
            }
        });
        await _context.SaveChangesAsync();

        var lines = await _svc.GetLinesAsync("Dispatch", "D1", PlantId);

        Assert.Equal(5m, Assert.Single(lines).RequiredQty);   // 15 待備 − 10 已開單
    }

    [Fact]
    public async Task GetLines_FullyStaged_ReturnsEmpty()
    {
        _context.MaterialStagingOrders.Add(new MaterialStagingOrder
        {
            StagingId = "IM-1", PlantId = PlantId, Status = "Completed", CreatedBy = "u1",
            SourceType = "Dispatch", SourceId = "D1",
            Lines =
            {
                new MaterialStagingLine
                {
                    StagingId = "IM-1", LineNo = 1, ProductCode = "MAT001",
                    UomCode = "EA", RequiredQty = 15m, SourceWorkOrderId = "WO001", SourceDispatchId = "D1",
                }
            }
        });
        await _context.SaveChangesAsync();

        Assert.Empty(await _svc.GetLinesAsync("Dispatch", "D1", PlantId));
    }

    [Fact]
    public async Task GetLines_VoidedStagingOrder_NotDeducted()
    {
        _context.MaterialStagingOrders.Add(new MaterialStagingOrder
        {
            StagingId = "IM-VOID", PlantId = PlantId, Status = "Void", CreatedBy = "u1",
            SourceType = "Dispatch", SourceId = "D1",
            Lines =
            {
                new MaterialStagingLine
                {
                    StagingId = "IM-VOID", LineNo = 1, ProductCode = "MAT001",
                    UomCode = "EA", RequiredQty = 15m, SourceWorkOrderId = "WO001", SourceDispatchId = "D1",
                }
            }
        });
        await _context.SaveChangesAsync();

        Assert.Equal(15m, Assert.Single(await _svc.GetLinesAsync("Dispatch", "D1", PlantId)).RequiredQty);
    }

    [Fact]
    public async Task ResolveStagingTarget_UsesWorkCenterSetting()
    {
        var (wh, loc) = await _svc.ResolveStagingTargetAsync("Dispatch", "D1", PlantId);

        Assert.Equal("STG", wh);
        Assert.Equal("STG-01", loc);
    }

    [Fact]
    public async Task ResolveStagingTarget_FallsBackToSystemParam()
    {
        var wc = _context.Set<WorkCenter>().Single(w => w.WorkCenterCode == "WC1");
        wc.StagingWhCode = null;
        wc.StagingLocationCode = null;
        await _context.SaveChangesAsync();

        _sysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgDefaultStagingWh, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("STG2");
        _sysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgDefaultStagingLocation, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("STG2-01");

        var (wh, loc) = await _svc.ResolveStagingTargetAsync("Dispatch", "D1", PlantId);

        Assert.Equal("STG2", wh);
        Assert.Equal("STG2-01", loc);
    }

    /// <summary>
    /// 系統參數是全域單值、倉庫代碼卻只屬於一個廠——參數指到別廠的倉時必須視同未設定，
    /// 不能把他廠倉庫帶進本廠的備料單。這是「該廠別化卻沒廠別化」的參數最先咬人的地方。
    /// </summary>
    [Fact]
    public async Task ResolveStagingTarget_IgnoresSystemParamPointingToAnotherPlant()
    {
        var wc = _context.Set<WorkCenter>().Single(w => w.WorkCenterCode == "WC1");
        wc.StagingWhCode = null;
        wc.StagingLocationCode = null;
        await _context.SaveChangesAsync();

        _sysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgDefaultStagingWh, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("OTHER-WH");
        _sysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgDefaultStagingLocation, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("OTHER-01");

        var (wh, loc) = await _svc.ResolveStagingTargetAsync("Dispatch", "D1", PlantId);

        Assert.Null(wh);
        Assert.Null(loc);
    }

    /// <summary>工作中心那一層同樣要驗：倉庫被停用後不該繼續帶入，應往系統參數那層找。</summary>
    [Fact]
    public async Task ResolveStagingTarget_FallsThroughWhenWorkCenterWarehouseInactive()
    {
        _context.Set<Warehouse>().Single(w => w.WhCode == "STG").IsActive = false;
        await _context.SaveChangesAsync();

        _sysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgDefaultStagingWh, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("STG2");
        _sysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgDefaultStagingLocation, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("STG2-01");

        var (wh, loc) = await _svc.ResolveStagingTargetAsync("Dispatch", "D1", PlantId);

        Assert.Equal("STG2", wh);
        Assert.Equal("STG2-01", loc);
    }

    /// <summary>
    /// 參數要用**傳進來的廠別**去問，不是當前使用者的廠別、也不是 null。
    /// 這條看似瑣碎，但它正是廠別化最容易靜默出錯的地方：問錯廠不會拋例外，
    /// 只會安靜地拿到別廠（或全域）的值，然後把錯的倉帶進備料單。
    /// </summary>
    [Fact]
    public async Task ResolveStagingTarget_AsksParamsForTheGivenPlant()
    {
        var wc = _context.Set<WorkCenter>().Single(w => w.WorkCenterCode == "WC1");
        wc.StagingWhCode = null;
        wc.StagingLocationCode = null;
        await _context.SaveChangesAsync();

        await _svc.ResolveStagingTargetAsync("Dispatch", "D1", PlantId);

        _sysParam.Verify(s => s.GetAsync(
            ProjectZSystemParamKeys.MfgDefaultStagingWh, PlantId, It.IsAny<CancellationToken>()), Times.Once);
        _sysParam.Verify(s => s.GetAsync(
            ProjectZSystemParamKeys.MfgDefaultStagingLocation, PlantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>倉庫可用但儲位不屬於它時，只清掉儲位、保留倉庫——否則整層退回等於連可用的倉也丟了。</summary>
    [Fact]
    public async Task ResolveStagingTarget_KeepsWarehouseWhenOnlyLocationInvalid()
    {
        var wc = _context.Set<WorkCenter>().Single(w => w.WorkCenterCode == "WC1");
        wc.StagingLocationCode = "OTHER-01";   // 屬於 OTHER-WH，不屬於 STG
        await _context.SaveChangesAsync();

        var (wh, loc) = await _svc.ResolveStagingTargetAsync("Dispatch", "D1", PlantId);

        Assert.Equal("STG", wh);
        Assert.Null(loc);
    }

    [Fact]
    public async Task CreateDraft_BuildsOrderWithLinesAndTarget()
    {
        var stagingId = await _svc.CreateDraftAsync("Dispatch", "D1", PlantId, "user1");

        var order = _context.MaterialStagingOrders.Single(o => o.StagingId == stagingId);
        Assert.Equal("Draft", order.Status);
        Assert.Equal("Dispatch", order.SourceType);
        Assert.Equal("D1", order.SourceId);
        Assert.Equal("STG", order.StagingWhCode);

        var line = Assert.Single(_context.MaterialStagingLines.Where(l => l.StagingId == stagingId));
        Assert.Equal("MAT001", line.ProductCode);
        Assert.Equal(15m, line.RequiredQty);
        Assert.Equal("WO001", line.SourceWorkOrderId);
        Assert.Equal("D1", line.SourceDispatchId);
    }

    [Fact]
    public async Task CreateDraft_NothingPending_Throws()
    {
        var dm = _context.Set<DispatchMaterial>().Single();
        dm.IssuedQty = dm.RequiredQty;
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.CreateDraftAsync("Dispatch", "D1", PlantId, "user1"));
    }

    [Fact]
    public async Task GetExistingOrders_FindsOrdersLinkedByLine()
    {
        // 工單層級開的備料單也備到了這張派工的料，現場不該再開一張
        _context.MaterialStagingOrders.Add(new MaterialStagingOrder
        {
            StagingId = "IM-WO", PlantId = PlantId, Status = "Confirmed", CreatedBy = "u1",
            SourceType = "WorkOrder", SourceId = "WO001",
            Lines =
            {
                new MaterialStagingLine
                {
                    StagingId = "IM-WO", LineNo = 1, ProductCode = "MAT001",
                    UomCode = "EA", RequiredQty = 5m, SourceWorkOrderId = "WO001", SourceDispatchId = "D1",
                }
            }
        });
        await _context.SaveChangesAsync();

        var orders = await _svc.GetExistingOrdersAsync("Dispatch", "D1", PlantId);

        var brief = Assert.Single(orders);
        Assert.Equal("IM-WO", brief.StagingId);
        Assert.Equal("Confirmed", brief.Status);
        Assert.Equal(1, brief.LineCount);
    }
}
