using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.QCR.Controllers;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QCR;

/// <summary>
/// NCR 處置「退件重工」（ReturnRework）：Execute 自動建立委外重工單草稿並雙向回填
/// （pur-domain.md §4.2 觸發時機第 1 點）。
/// </summary>
public class NcrReturnReworkTests : TestBase
{
    private readonly Mock<IDocNumberService>       _mockDocNo  = new();
    private readonly Mock<IApprovalService>        _mockAppr   = new();
    private readonly Mock<IInventoryPostingService> _mockPost  = new();
    private readonly Mock<IDateTimeService>        _mockDt     = new();
    private readonly Mock<Modules.QCR.Services.IQcrReturnService> _mockReturn = new();

    private const string PlantId = "P001";

    public NcrReturnReworkTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(new DateOnly(2026, 7, 10));
        _mockDocNo.Setup(d => d.NextAsync("OutsourceRework", It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("RW-2607-00001");
    }

    private NcrController BuildController()
    {
        var ctrl = new NcrController(
            GetMockLocalizer<ProjectZSharedResource>().Object,
            _mockDocNo.Object, _mockAppr.Object, _mockPost.Object,
            _mockDt.Object, _mockReturn.Object,
            new Mock<SmartFactory.MES.Modules.MFG.Services.IReworkWorkOrderService>().Object);

        var mockUser = new Mock<IUserContext>();
        mockUser.Setup(u => u.PlantId).Returns(PlantId);
        mockUser.Setup(u => u.UserId).Returns("user1");
        ctrl.ControllerContext = GetControllerContextWithServices(userContext: mockUser.Object);
        return ctrl;
    }

    private NonConformanceReport SeedNcr(decimal qty = 5m, string sourceType = "Manual", string? sourceId = null)
    {
        var ncr = new NonConformanceReport
        {
            NcrId = "NCR2607-00001", PlantId = PlantId, CreatedBy = "user1",
            SourceType = sourceType, SourceId = sourceId,
            ProductCode = "P-OS-01", Qty = qty,
            Disposition = "ReturnRework", NcrStatus = NcrStatus.Open,
            RootCause = "委外電鍍膜厚不足",
        };
        _context.NonConformanceReports.Add(ncr);
        _context.SaveChanges();
        return ncr;
    }

    private static bool JsonSuccess(IActionResult result)
    {
        var json = Assert.IsType<JsonResult>(result);
        var prop = json.Value!.GetType().GetProperty("success");
        return (bool)prop!.GetValue(json.Value)!;
    }

    [Fact]
    public async Task Execute_ReturnRework_CreatesDraftOutsourceReworkAndBacklinks()
    {
        var ncr = SeedNcr(qty: 5m);
        var ctrl = BuildController();

        var result = await ctrl.Execute(ncr.NcrId);

        Assert.True(JsonSuccess(result));
        var rw = Assert.Single(_context.OutsourceReworks.ToList());
        Assert.Equal("RW-2607-00001", rw.OutsourceReworkId);
        Assert.Equal(OutsourceReworkStatus.Draft, rw.Status);
        Assert.Equal(ncr.NcrId, rw.SourceNcrId);
        Assert.Equal(5m, rw.ReworkQty);
        Assert.Equal(ReworkLiabilityParty.Vendor, rw.LiabilityParty);
        Assert.Equal(100m, rw.VendorLiablePct);
        Assert.Equal("委外電鍍膜厚不足", rw.ReworkReason);
        Assert.Equal("RW-2607-00001", ncr.OutsourceReworkId);
        Assert.Equal(NcrStatus.Executed, ncr.NcrStatus);
    }

    [Fact]
    public async Task Execute_ReturnRework_FromOutsourceInspection_LinksOriginalDispatch()
    {
        _context.Dispatches.Add(new Dispatch
        {
            DispatchId = "DSP-OS-01", WorkOrderId = "WO-01", OperationSeq = 20,
            IsOutsource = true, PlantId = PlantId, Quantity = 5m,
        });
        _context.InspectionOrders.Add(new InspectionOrder
        {
            InspectionOrderId = "IQC-01", SourceDocId = "DSP-OS-01",
            ProductCode = "P-OS-01", PlantId = PlantId,
        });
        _context.SaveChanges();
        var ncr = SeedNcr(sourceType: "InspectionOrder", sourceId: "IQC-01");
        var ctrl = BuildController();

        Assert.True(JsonSuccess(await ctrl.Execute(ncr.NcrId)));
        var rw = Assert.Single(_context.OutsourceReworks.ToList());
        Assert.Equal("DSP-OS-01", rw.OriginalDispatchId);
    }

    [Fact]
    public async Task Execute_ReturnRework_ZeroQty_FailsWithoutCreating()
    {
        var ncr = SeedNcr(qty: 0m);
        var ctrl = BuildController();

        Assert.False(JsonSuccess(await ctrl.Execute(ncr.NcrId)));
        Assert.Empty(_context.OutsourceReworks.ToList());
        Assert.Equal(NcrStatus.Open, ncr.NcrStatus);
    }
}
