using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.PUR.Models;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 對帳差異決策清單（§10-5 P2）。重點在「一鍵結案」的伺服器端重驗證：
/// 差異項是重跑當下的快照，期間明細可能已被轉採購或送審，不可信任前端送來的 id 直接結案。
/// </summary>
public class MrpVarianceDecisionControllerTests : TestBase
{
    private const string PlantId = "P001";
    private const string RunId   = "MRP-0001";
    private const string PrId    = "PR-0001";

    private readonly Mock<IUomService> _mockUom = new();
    private readonly Mock<IPermissionService> _mockPerm = new();

    private MrpVarianceDecisionController BuildController()
    {
        _context.CurrentPlantId = PlantId;
        _mockUom.Setup(u => u.GetAllDecimalPlacesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>());
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);

        var ctrl = new MrpVarianceDecisionController(
            GetMockLocalizer<ProjectZSharedResource>().Object, _mockUom.Object);
        ctrl.ControllerContext = GetControllerContextWithServices(permService: _mockPerm.Object);
        return ctrl;
    }

    /// <summary>建立一張已核准請購單 + 一筆未結清的 ModeChanged 差異項。</summary>
    private string SeedVariance(decimal orderedQty = 0m,
        DocumentApprovalStatus status = DocumentApprovalStatus.Approved,
        MrpPurchaseVarianceKind kind = MrpPurchaseVarianceKind.ModeChanged)
    {
        _context.PurchaseRequests.Add(new PurchaseRequest
        {
            PurchaseRequestId = PrId, SourceMrpRunId = RunId, PlantId = PlantId,
            RequestDate = DateOnly.FromDateTime(DateTime.UtcNow), RequestedBy = "u1",
            ApprovalStatus = status,
            Lines =
            {
                new PurchaseRequestLine
                {
                    PurchaseRequestId = PrId, LineNo = 1, ProductCode = "PA2103",
                    RequestQty = 100m, OrderedQty = orderedQty, SourceMrpRunId = RunId
                }
            }
        });
        var rec = new MrpPurchaseVarianceRecord
        {
            MrpRunId = RunId, ProductCode = "PA2103", PurchaseRequestId = PrId, LineNo = 1,
            Kind = kind, Disposition = MrpPurchaseVarianceDisposition.PendingClose,
            Status = MrpVarianceStatus.Open, PreviousQty = 100m, CurrentQty = 0m,
            Description = "test", PlantId = PlantId
        };
        _context.MrpPurchaseVarianceRecords.Add(rec);
        _context.SaveChanges();
        return rec.VarianceId;
    }

    private static bool IsSuccess(IActionResult r)
    {
        var json = Assert.IsType<JsonResult>(r);
        return (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
    }

    [Fact]
    public async Task Index_NotSearched_ReturnsNullModel()
    {
        var view = Assert.IsType<ViewResult>(await BuildController().Index(null, null, searched: false));
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task CloseLines_SafeLine_ClosesAndResolves()
    {
        var id = SeedVariance();

        Assert.True(IsSuccess(await BuildController().CloseLines(new[] { id })));

        Assert.True((await _context.PurchaseRequestLines.AsNoTracking().SingleAsync()).IsClosed);
        var rec = await _context.MrpPurchaseVarianceRecords.AsNoTracking().SingleAsync();
        Assert.Equal(MrpVarianceStatus.Resolved, rec.Status);
        Assert.NotNull(rec.ResolvedAt);
    }

    [Fact]
    public async Task CloseLines_LineOrderedAfterDetection_IsSkipped()
    {
        // 差異偵測後才被轉採購 → 伺服器端重驗證必須擋下，不可依快照結案
        var id = SeedVariance(orderedQty: 40m);

        Assert.True(IsSuccess(await BuildController().CloseLines(new[] { id })));

        Assert.False((await _context.PurchaseRequestLines.AsNoTracking().SingleAsync()).IsClosed);
        Assert.Equal(MrpVarianceStatus.Open,
            (await _context.MrpPurchaseVarianceRecords.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task CloseLines_PurchaseRequestPendingApproval_IsSkipped()
    {
        var id = SeedVariance(status: DocumentApprovalStatus.PendingApproval);

        Assert.True(IsSuccess(await BuildController().CloseLines(new[] { id })));

        Assert.False((await _context.PurchaseRequestLines.AsNoTracking().SingleAsync()).IsClosed);
        Assert.Equal(MrpVarianceStatus.Open,
            (await _context.MrpPurchaseVarianceRecords.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task CloseLines_NonRetirableKind_IsSkipped()
    {
        // 數量變動不是「退場」情境，不可用結案處理
        var id = SeedVariance(kind: MrpPurchaseVarianceKind.Decreased);

        Assert.True(IsSuccess(await BuildController().CloseLines(new[] { id })));

        Assert.False((await _context.PurchaseRequestLines.AsNoTracking().SingleAsync()).IsClosed);
        Assert.Equal(MrpVarianceStatus.Open,
            (await _context.MrpPurchaseVarianceRecords.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task CloseLines_NoIds_ReturnsFailure()
    {
        Assert.False(IsSuccess(await BuildController().CloseLines(Array.Empty<string>())));
    }

    [Fact]
    public async Task Acknowledge_ResolvesWithoutTouchingDocuments()
    {
        var id = SeedVariance(orderedQty: 40m);

        Assert.True(IsSuccess(await BuildController().Acknowledge(new[] { id }, "已改由工單供給")));

        Assert.False((await _context.PurchaseRequestLines.AsNoTracking().SingleAsync()).IsClosed);
        var rec = await _context.MrpPurchaseVarianceRecords.AsNoTracking().SingleAsync();
        Assert.Equal(MrpVarianceStatus.Resolved, rec.Status);
        Assert.Equal("已改由工單供給", rec.ResolutionNote);
    }
}
