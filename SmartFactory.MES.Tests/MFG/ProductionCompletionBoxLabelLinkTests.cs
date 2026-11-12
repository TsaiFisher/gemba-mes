using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 分箱完工入庫後「列印箱標籤」（批號 QR）的提供條件（container-tracking.md §8.5，2026-11-05 使用者裁決）：
/// 有綁箱號時箱子身上已有固定 QR（箱號），不再給批號標籤——同一箱兩種 QR 現場不知道掃哪張，
/// 且批號標籤在換箱／換批號後就過時；沒綁箱號時批號標籤是分辨各箱的唯一方式，照舊提供。
/// 兩個方向都釘：只測「綁了不給」的話，把連結整個拿掉也會綠。
/// </summary>
public class ProductionCompletionBoxLabelLinkTests : TestBase
{
    private const string Plant = "P001";
    private const string WoId  = "WO2026BOXL";

    private readonly Mock<IProductionCompletionService> _completion = new();
    private readonly Mock<IUserContext> _user = new();

    public ProductionCompletionBoxLabelLinkTests()
    {
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = WoId, ProductCode = "FG-BOXL", Quantity = 60m,
            PlantId = Plant, Status = WorkOrderStatus.Completed,
        });
        _context.SaveChanges();
    }

    private ProductionCompletionController BuildController()
    {
        _context.CurrentPlantId = Plant;
        _user.Setup(u => u.PlantId).Returns(Plant);
        _user.Setup(u => u.UserId).Returns("usr1");

        var uom = new Mock<IUomService>();
        uom.Setup(u => u.RoundQtyAsync(It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync((string? _, decimal q, CancellationToken _) => q);

        var url = new Mock<IUrlHelper>();
        url.Setup(u => u.Action(It.IsAny<UrlActionContext>())).Returns("/ProductionCompletion/BoxLabels/" + WoId);

        return new ProductionCompletionController(
            GetMockLocalizer<ProjectZSharedResource>().Object,
            _completion.Object,
            new Mock<IPlantParamService>().Object,
            new Mock<ISamplingPlanService>().Object,
            uom.Object,
            new Mock<ICurrencyService>().Object,
            new PlantDefaultLocationResolver(_context))
        {
            ControllerContext = GetControllerContextWithServices(userContext: _user.Object),
            Url = url.Object,
        };
    }

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o);

    private async Task<(bool Success, string? LabelUrl)> CompleteByBoxAsync(string? boxUnitIds)
    {
        var json = (JsonResult)await BuildController().Complete(WoId, "F001", "F001-1", lotNo: null,
            packByBox: true, containerCode: "BOX", boxQty: 30m, boxUnitIds: boxUnitIds);
        var data = Prop(json.Value!, "data");
        return ((bool)Prop(json.Value!, "success")!, data == null ? null : (string?)Prop(data, "labelUrl"));
    }

    [Fact]
    public async Task 分箱沒綁箱號_給批號箱標籤連結()
    {
        var (ok, labelUrl) = await CompleteByBoxAsync(boxUnitIds: null);

        Assert.True(ok);
        Assert.Equal("/ProductionCompletion/BoxLabels/" + WoId, labelUrl);
    }

    [Fact]
    public async Task 分箱有綁箱號_不給批號箱標籤連結_箱號照樣送到服務()
    {
        var (ok, labelUrl) = await CompleteByBoxAsync(boxUnitIds: "BX-00001\nBX-00002");

        Assert.True(ok);
        Assert.Null(labelUrl);
        _completion.Verify(c => c.CompleteAsync(WoId, Plant, "F001", "F001-1", null,
            It.Is<CompletionBoxing?>(b => b != null && b.UnitIds!.SequenceEqual(new[] { "BX-00001", "BX-00002" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
