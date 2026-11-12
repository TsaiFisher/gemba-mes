using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// WorkOrderApprovalSync.SyncAsync 整合測試（mfg-domain.md §17.3 / §18.2）。
/// 驗證 PendingApproval → Approved 路徑在非委外工單有 Push 料件時，
/// 自動建立 Draft MaterialSlip；其餘終局狀態/委外/無 Push 料件則不建立。
/// </summary>
public class WorkOrderApprovalSyncTests : TestBase
{
    private readonly Mock<IDocNumberService> _mockDocNo = new();
    private const string PlantId = "P001";
    private const string WoId    = "WO2026001";

    public WorkOrderApprovalSyncTests()
    {
        _context.CurrentPlantId = PlantId;

        _mockDocNo
            .Setup(d => d.NextAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("MI2026000001");
    }

    private WorkOrderApprovalSync BuildSync() => new(_mockDocNo.Object);

    private WorkOrder SeedWorkOrder(string id, bool isOutsource = false,
        DocumentApprovalStatus approval = DocumentApprovalStatus.PendingApproval)
    {
        var op = new WorkOrderOperation
        {
            WorkOrderId  = id,
            OperationSeq = 10,
            OperationName = "組裝",
            WorkCenterId = "WC001",
        };
        op.Materials.Add(new WorkOrderMaterial
        {
            WorkOrderId  = id,
            OperationSeq = 10,
            BomSeq       = 1,
            ProductCode  = "MAT001",
            RequiredQty  = 5m,
            UomCode      = "PC",
            IssueMethod  = MaterialIssueMethod.Push,
        });
        var wo = new WorkOrder
        {
            WorkOrderId      = id,
            ProductCode      = "FG001",
            Quantity         = 10m,
            PlantId          = PlantId,
            IsOutsource      = isOutsource,
            ApprovalStatus   = approval,
            PlannedStartDate = DateTime.UtcNow,
            PlannedEndDate   = DateTime.UtcNow.AddDays(7),
        };
        wo.Operations.Add(op);
        _context.WorkOrders.Add(wo);
        _context.SaveChanges();
        return wo;
    }

    // ── Approved 路徑 ─────────────────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_Approved_NonOutsource_WithPushMaterial_CreatesDraftSlip()
    {
        SeedWorkOrder(WoId, isOutsource: false);
        var sync = BuildSync();

        await sync.SyncAsync(_context, WoId, ApprovalStatus.Approved);
        await _context.SaveChangesAsync(); // 模擬 ApprovalService 的外層 save

        var slip = _context.MaterialSlips
            .FirstOrDefault(s => s.SourceDocId == WoId && s.SourceDocType == "WorkOrder");

        Assert.NotNull(slip);
        Assert.Equal(MaterialSlipStatus.Draft, slip!.Status);
        Assert.Equal(MaterialSlipCategory.InternalIssue, slip.Category);
        Assert.Equal("MI2026000001", slip.SlipId);
        Assert.Equal(PlantId, slip.PlantId);
    }

    [Fact]
    public async Task SyncAsync_Approved_NonOutsource_SlipLinesMatchPushMaterials()
    {
        SeedWorkOrder(WoId, isOutsource: false);
        var sync = BuildSync();

        await sync.SyncAsync(_context, WoId, ApprovalStatus.Approved);
        await _context.SaveChangesAsync();

        var lines = _context.MaterialSlipLines.Where(l => l.SlipId == "MI2026000001").ToList();
        Assert.Single(lines);
        Assert.Equal("MAT001", lines[0].ProductCode);
        Assert.Equal(5m, lines[0].RequestedQty);
        Assert.Equal("PC", lines[0].UomCode);
        Assert.Equal("10_1", lines[0].SourceLineId); // "{OperationSeq}_{BomSeq}"
    }

    [Fact]
    public async Task SyncAsync_Approved_Outsource_DoesNotCreateSlip()
    {
        SeedWorkOrder(WoId, isOutsource: true);
        var sync = BuildSync();

        await sync.SyncAsync(_context, WoId, ApprovalStatus.Approved);
        await _context.SaveChangesAsync();

        var count = _context.MaterialSlips.Count(s => s.SourceDocId == WoId);
        Assert.Equal(0, count);
        _mockDocNo.Verify(d => d.NextAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_Approved_NonOutsource_NoPushMaterials_DoesNotCreateSlip()
    {
        // 建立工單但料件全為 Backflush（無 Push）
        var op = new WorkOrderOperation
        {
            WorkOrderId = WoId, OperationSeq = 10, WorkCenterId = "WC001",
        };
        op.Materials.Add(new WorkOrderMaterial
        {
            WorkOrderId  = WoId,
            OperationSeq = 10,
            BomSeq       = 1,
            ProductCode  = "MAT001",
            RequiredQty  = 5m,
            UomCode      = "PC",
            IssueMethod  = MaterialIssueMethod.Backflush,
        });
        var wo = new WorkOrder
        {
            WorkOrderId    = WoId, ProductCode = "FG001", Quantity = 10m,
            PlantId        = PlantId, IsOutsource = false,
            PlannedStartDate = DateTime.UtcNow, PlannedEndDate = DateTime.UtcNow.AddDays(7),
        };
        wo.Operations.Add(op);
        _context.WorkOrders.Add(wo);
        _context.SaveChanges();

        var sync = BuildSync();
        await sync.SyncAsync(_context, WoId, ApprovalStatus.Approved);
        await _context.SaveChangesAsync();

        Assert.Equal(0, _context.MaterialSlips.Count(s => s.SourceDocId == WoId));
    }

    // ── 工單狀態回寫 ──────────────────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_Approved_SetsWoApprovalStatusToApproved()
    {
        SeedWorkOrder(WoId, isOutsource: false);
        var sync = BuildSync();

        await sync.SyncAsync(_context, WoId, ApprovalStatus.Approved);
        await _context.SaveChangesAsync();

        var wo = _context.WorkOrders.Find(WoId)!;
        Assert.Equal(DocumentApprovalStatus.Approved, wo.ApprovalStatus);
    }

    [Fact]
    public async Task SyncAsync_Rejected_SetsWoApprovalStatusToRejected_NoSlipCreated()
    {
        SeedWorkOrder(WoId, isOutsource: false);
        var sync = BuildSync();

        await sync.SyncAsync(_context, WoId, ApprovalStatus.Rejected);
        await _context.SaveChangesAsync();

        var wo = _context.WorkOrders.Find(WoId)!;
        Assert.Equal(DocumentApprovalStatus.Rejected, wo.ApprovalStatus);
        Assert.Equal(0, _context.MaterialSlips.Count());
    }

    /// <summary>
    /// 投產先寫 Released 再送審；未核准的結果要比照「撤回」退回 Planned，才能修正後重新投產。
    /// 留在 Released 的話重新投產（要 Planned）與撤回（要 PendingApproval）兩條路都不通。
    /// </summary>
    [Theory]
    [InlineData(ApprovalStatus.Rejected,            WorkOrderStatus.Planned)]
    [InlineData(ApprovalStatus.ReturnedForRevision, WorkOrderStatus.Planned)]
    [InlineData(ApprovalStatus.Withdrawn,           WorkOrderStatus.Planned)]
    [InlineData(ApprovalStatus.Approved,            WorkOrderStatus.Released)]
    public async Task SyncAsync_ReleasedWorkOrder_NotApproved_ReturnsToPlanned(
        ApprovalStatus final, WorkOrderStatus expected)
    {
        var wo = SeedWorkOrder(WoId, isOutsource: true);
        wo.Status = WorkOrderStatus.Released;
        _context.SaveChanges();

        await BuildSync().SyncAsync(_context, WoId, final);
        await _context.SaveChangesAsync();

        Assert.Equal(expected, _context.WorkOrders.Find(WoId)!.Status);
    }

    /// <summary>退回已規劃時，已排程未開工的派工一併退回待排程（不再佔產能）；其他狀態不動。</summary>
    [Fact]
    public async Task SyncAsync_Rejected_UnschedulesScheduledDispatchesOnly()
    {
        var wo = SeedWorkOrder(WoId, isOutsource: true);
        wo.Status = WorkOrderStatus.Released;
        _context.Dispatches.Add(new Dispatch { DispatchId = "D-S", WorkOrderId = WoId, OperationSeq = 10, Status = DispatchStatus.Scheduled, PlantId = PlantId });
        _context.Dispatches.Add(new Dispatch { DispatchId = "D-R", WorkOrderId = WoId, OperationSeq = 10, Status = DispatchStatus.ReadyToStart, PlantId = PlantId });
        _context.SaveChanges();

        await BuildSync().SyncAsync(_context, WoId, ApprovalStatus.Rejected);
        await _context.SaveChangesAsync();

        Assert.Equal(DispatchStatus.Pending,      _context.Dispatches.Find("D-S")!.Status);
        Assert.Equal(DispatchStatus.ReadyToStart, _context.Dispatches.Find("D-R")!.Status);
    }

    [Fact]
    public async Task SyncAsync_Rejected_InProgressWorkOrder_StatusUntouched()
    {
        var wo = SeedWorkOrder(WoId, isOutsource: true);
        wo.Status = WorkOrderStatus.InProgress;
        _context.SaveChanges();

        await BuildSync().SyncAsync(_context, WoId, ApprovalStatus.Rejected);
        await _context.SaveChangesAsync();

        Assert.Equal(WorkOrderStatus.InProgress, _context.WorkOrders.Find(WoId)!.Status);
    }

    [Fact]
    public async Task SyncAsync_Withdrawn_ClearsApprovalRequestId_NoSlipCreated()
    {
        SeedWorkOrder(WoId, isOutsource: false);
        var wo = _context.WorkOrders.Find(WoId)!;
        wo.ApprovalRequestId = "REQ001";
        _context.SaveChanges();

        var sync = BuildSync();
        await sync.SyncAsync(_context, WoId, ApprovalStatus.Withdrawn);
        await _context.SaveChangesAsync();

        var updated = _context.WorkOrders.Find(WoId)!;
        Assert.Equal(DocumentApprovalStatus.Draft, updated.ApprovalStatus);
        Assert.Null(updated.ApprovalRequestId);
        Assert.Equal(0, _context.MaterialSlips.Count());
    }

    [Fact]
    public async Task SyncAsync_UnknownDocument_DoesNotThrow()
    {
        var sync = BuildSync();
        // 不應拋出例外，只是靜默返回
        await sync.SyncAsync(_context, "NONEXISTENT", ApprovalStatus.Approved);
    }
}
