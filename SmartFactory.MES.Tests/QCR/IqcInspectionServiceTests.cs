using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.QCR.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QCR;

/// <summary>
/// IqcInspectionService：桌機（QCR/InspectionOrder）與手機（api/mobile/inv）共用的 IQC 判定路徑。
/// 手機端的品質隔離放行情境已由 MobileInvIqcControllerTests 覆蓋，此處補待檢清單、
/// 重複登錄守門與 Fail 判定（不放行）三段桌機補登會踩到的行為。
/// </summary>
public class IqcInspectionServiceTests : TestBase
{
    private const string PlantId = "P001";
    private const string GoodsReceiptId = "PI2026070001";

    private readonly Mock<IUserContext> _mockUser = new();
    private readonly Mock<ISamplingPlanService> _mockSampling = new();
    private readonly Mock<IDocNumberService> _mockDocNo = new();
    private readonly Mock<IApprovalService> _mockApproval = new();
    private readonly Mock<IInventoryPostingService> _mockPosting = new();

    public IqcInspectionServiceTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns("user1");
        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("WT2026070001");
        _mockSampling.Setup(s => s.GetDecisionAsync(It.IsAny<string?>(), It.IsAny<string?>(), "IQC",
                It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SamplingDecision(ShouldInspect: true, VerificationLevel: 4, SampleSize: 5, SamplingState: "Normal"));

        SeedConfirmedReceipt();
    }

    private void SeedConfirmedReceipt()
    {
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = GoodsReceiptId, ReceiptDate = new DateOnly(2026, 7, 1), SupplierCode = "SUP001",
            Status = DocStatus.Confirmed, ApprovalStatus = DocumentApprovalStatus.Approved,
            PlantId = PlantId, TypeCode = "PI", TypeCategory = "Receipt",
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = GoodsReceiptId, LineNo = 1, ProductCode = "MAT001",
            ReceiptQty = 40, UomCode = "PC", StockQty = 40, StockUomCode = "PC", UnitPrice = 10,
        });
        _context.GoodsReceipts.Add(gr);
        _context.SaveChanges();
    }

    private IqcInspectionService BuildService()
        => new(_context, _mockUser.Object, _mockSampling.Object, _mockDocNo.Object,
            _mockPosting.Object, _mockApproval.Object,
            Timezone.TimezoneTestKit.FixedLocalDate(LocalToday).Object);

    /// <summary>IQC 放行建立的移轉單以此為 TransferDate（廠別當地日，非 UTC）。</summary>
    private static readonly DateOnly LocalToday = new(2026, 7, 1);

    private static IqcSubmitInput Input(decimal failQty)
        => new(GoodsReceiptId, 1, "MAT001", LotSize: 40, SampleQty: 5, FailQty: failQty,
            VerificationLevel: 4, Remarks: null);

    [Fact]
    public async Task GetPendingLines_ReturnsConfirmedReceiptLine_ThenExcludesItAfterInspection()
    {
        var svc = BuildService();

        var before = await svc.GetPendingLinesAsync(PlantId);
        var row = Assert.Single(before);
        Assert.Equal(GoodsReceiptId, row.GoodsReceiptId);
        Assert.Equal(5, row.RecommendedSampleSize);
        Assert.Equal(4, row.VerificationLevel);

        await svc.SubmitAsync(Input(failQty: 0), PlantId);

        Assert.Empty(await svc.GetPendingLinesAsync(PlantId));
    }

    [Fact]
    public async Task Submit_Twice_SecondIsRejectedAsAlreadyDone()
    {
        var svc = BuildService();

        var first = await svc.SubmitAsync(Input(failQty: 0), PlantId);
        Assert.True(first.Success);
        Assert.Equal("Pass", first.Result);

        var second = await svc.SubmitAsync(Input(failQty: 0), PlantId);
        Assert.False(second.Success);
        Assert.Equal("IQC_ALREADY_DONE", second.ErrorCode);
        Assert.Single(_context.InspectionOrders);
    }

    [Fact]
    public async Task Submit_ZeroSampleQty_IsRejected()
    {
        var svc = BuildService();

        var outcome = await svc.SubmitAsync(
            new IqcSubmitInput(GoodsReceiptId, 1, "MAT001", 40, SampleQty: 0, FailQty: 0, VerificationLevel: 4, Remarks: null),
            PlantId);

        Assert.False(outcome.Success);
        Assert.Equal("INVALID_QTY", outcome.ErrorCode);
        Assert.Empty(_context.InspectionOrders);
    }

    [Fact]
    public async Task Submit_WithDefect_RejectsLotAndSkipsQuarantineRelease()
    {
        var svc = BuildService();

        var outcome = await svc.SubmitAsync(Input(failQty: 1), PlantId);

        Assert.True(outcome.Success);
        Assert.Equal("Fail", outcome.Result);   // MIL-STD-1916 Ac=0
        Assert.Empty(_context.StockTransfers);

        var order = Assert.Single(_context.InspectionOrders);
        Assert.Equal("IQC", order.InspectionType);
        Assert.Equal("Fail", order.Status);
        Assert.Equal(5, order.SampleSize);      // 批量 40 + VL-4 → n=5
        Assert.Equal("user1", order.InspectorId);
    }
}
