using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.QCR.Controllers;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QCR;

/// <summary>
/// 報廢處置存檔就要求倉庫與儲位（2026-10-27）。原本存得進去，到「執行處置」才因沒有儲位失敗，
/// 而面板把失敗訊息吞掉（呼叫不存在的 window.showToast），看起來像按了沒反應。
/// </summary>
public class NcrScrapLocationValidationTests : TestBase
{
    private const string PlantId = "P001";
    private readonly Mock<IDocNumberService> _docNo = new();

    public NcrScrapLocationValidationTests()
    {
        _context.CurrentPlantId = PlantId;
        _docNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync("NCR-2610-00001");
    }

    private NcrController Build()
    {
        var dt = new Mock<IDateTimeService>();
        dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(new DateOnly(2026, 10, 27));
        var ctrl = new NcrController(
            GetMockLocalizer<ProjectZSharedResource>().Object, _docNo.Object,
            new Mock<IApprovalService>().Object, new Mock<IInventoryPostingService>().Object,
            dt.Object, new Mock<Modules.QCR.Services.IQcrReturnService>().Object,
            new Mock<SmartFactory.MES.Modules.MFG.Services.IReworkWorkOrderService>().Object);
        var user = new Mock<IUserContext>();
        user.Setup(u => u.PlantId).Returns(PlantId);
        user.Setup(u => u.UserId).Returns("user1");
        ctrl.ControllerContext = GetControllerContextWithServices(userContext: user.Object);
        return ctrl;
    }

    private static NonConformanceReport Ncr(string disposition, string? wh = null, string? loc = null) => new()
    {
        SourceType = "Manual", ProductCode = "P", Qty = 1, UomCode = "EA",
        Disposition = disposition, WhCode = wh, LocationCode = loc
    };

    [Fact]
    public async Task 報廢沒填倉庫儲位_存檔被擋_不建單()
    {
        var ctrl = Build();
        await ctrl.Create(Ncr("Scrap"));

        Assert.True(ctrl.ModelState.ContainsKey(nameof(NonConformanceReport.WhCode)));
        Assert.True(ctrl.ModelState.ContainsKey(nameof(NonConformanceReport.LocationCode)));
        Assert.Empty(_context.NonConformanceReports);
    }

    [Fact]
    public async Task 報廢有填倉庫儲位_照常建單()
    {
        var ctrl = Build();
        await ctrl.Create(Ncr("Scrap", "WH1", "L1"));

        Assert.True(ctrl.ModelState.IsValid);
        Assert.Single(_context.NonConformanceReports);
    }

    [Theory]
    [InlineData("Rework")]
    [InlineData("Return")]
    [InlineData("UseAsIs")]
    public async Task 其他處置不要求儲位(string disposition)
    {
        var ctrl = Build();
        await ctrl.Create(Ncr(disposition));

        Assert.True(ctrl.ModelState.IsValid);
        Assert.Single(_context.NonConformanceReports);
    }
}
