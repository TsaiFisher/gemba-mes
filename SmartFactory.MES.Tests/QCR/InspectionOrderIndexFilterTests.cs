using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.QCR.Controllers;
using SmartFactory.MES.Modules.QCR.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QCR;

/// <summary>
/// 檢驗單查詢的「來源單號」條件（2026-10-27 驗收需求：檢驗單沒有自己的單號，改以來源單號找單）。
/// </summary>
public class InspectionOrderIndexFilterTests : TestBase
{
    private const string PlantId = "P001";

    private readonly InspectionOrderController _ctrl;

    public InspectionOrderIndexFilterTests()
    {
        _context.CurrentPlantId = PlantId;
        var user = new Mock<IUserContext>();
        user.Setup(u => u.PlantId).Returns(PlantId);

        _ctrl = new InspectionOrderController(
            GetMockLocalizer<ProjectZSharedResource>().Object,
            new Mock<IExportService>().Object,
            new Mock<IDateTimeService>().Object,
            new Mock<IIqcInspectionService>().Object,
            new Mock<IFpiGateService>().Object,
            new Mock<ISamplingPlanService>().Object)
        {
            ControllerContext = GetControllerContextWithServices(userContext: user.Object)
        };

        Seed("GR-001", PlantId, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        Seed("GR-001", PlantId, new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));
        Seed("WO-002", PlantId, new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc));
        Seed("GR-OTHER", "P002", new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc));
        _context.SaveChanges();
    }

    private void Seed(string source, string plant, DateTime createdAt) =>
        _context.InspectionOrders.Add(new InspectionOrder
        {
            PlantId = plant, InspectionType = "IQC", SourceDocId = source, ProductCode = "P", CreatedAt = createdAt
        });

    [Fact]
    public async Task 依來源單號篩選_只回該來源的檢驗單()
    {
        var result = Assert.IsType<ViewResult>(await _ctrl.Index(
            inspectionType: null, status: null, fromDate: null, toDate: null, searchProduct: null,
            searchSourceDoc: "GR-001", searched: true));

        var rows = Assert.IsAssignableFrom<PaginatedList<InspectionOrder>>(result.Model);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("GR-001", r.SourceDocId));
    }

    [Fact]
    public async Task 來源單號選項_只列本廠_不重複_新的在前()
    {
        await _ctrl.Index(inspectionType: null, status: null, fromDate: null, toDate: null, searchProduct: null);

        var options = Assert.IsAssignableFrom<IEnumerable<string>>((object)_ctrl.ViewBag.SourceDocOptions);
        Assert.Equal(new[] { "WO-002", "GR-001" }, options);
    }
}
