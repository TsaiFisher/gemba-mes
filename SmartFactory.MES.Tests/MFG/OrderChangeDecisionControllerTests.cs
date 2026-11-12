using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// OrderChangeDecisionController（B2 / §21.2 決策清單）測試。
/// 驗證唯讀清單只列出投產中（InProgress/Completed）的來源訂單、待同步篩選、SO/IO 型別解析。
/// </summary>
public class OrderChangeDecisionControllerTests : TestBase
{
    private const string PlantId = "P001";

    private readonly Mock<IUserContext>       _user = new();
    private readonly Mock<IPermissionService> _perm = new();

    public OrderChangeDecisionControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _user.Setup(u => u.PlantId).Returns(PlantId);
        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
    }

    private OrderChangeDecisionController BuildController()
        => new(_context, _user.Object, _perm.Object, new Mock<IUomService>().Object);

    [Fact]
    public async Task Index_NotSearched_ReturnsNullModel()
    {
        var view = Assert.IsType<ViewResult>(await BuildController().Index(null));
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task Index_ListsOnlyInProductionOrders()
    {
        // 投產中（InProgress）→ 入列；尚未投產（Planned）→ 不入列
        Seed("MRP1", 1, "SO-1", "B1", WorkOrderStatus.InProgress, dirty: false);
        Seed("MRP2", 1, "SO-2", "B2", WorkOrderStatus.Planned,    dirty: false);

        var view = Assert.IsType<ViewResult>(await BuildController().Index(null, searched: true));
        var list = Assert.IsType<PaginatedList<OrderChangeDecisionRow>>(view.Model);

        Assert.Single(list);
        Assert.Equal("SO-1", list[0].SourceOrderId);
        Assert.Equal("SO", list[0].SourceType);
    }

    [Fact]
    public async Task Index_OnlyDirtyFilter_ReturnsDirtyOnly()
    {
        Seed("MRP1", 1, "SO-1", "B1", WorkOrderStatus.InProgress, dirty: true);
        Seed("MRP2", 1, "SO-2", "B2", WorkOrderStatus.Completed,  dirty: false);

        var view = Assert.IsType<ViewResult>(await BuildController().Index(null, onlyDirty: true, searched: true));
        var list = Assert.IsType<PaginatedList<OrderChangeDecisionRow>>(view.Model);

        Assert.Single(list);
        Assert.True(list[0].IsDirty);
    }

    [Fact]
    public async Task Index_ResolvesIoSourceType()
    {
        Seed("MRP1", 1, "IO-9", "B1", WorkOrderStatus.InProgress, dirty: false, isIo: true);

        var view = Assert.IsType<ViewResult>(await BuildController().Index(null, searched: true));
        var list = Assert.IsType<PaginatedList<OrderChangeDecisionRow>>(view.Model);

        Assert.Equal("IO", Assert.Single(list).SourceType);
    }

    private void Seed(string mrpRunId, int lineSeq, string sourceOrderId, string batchId,
        WorkOrderStatus woStatus, bool dirty, bool isIo = false)
    {
        _context.MrpRuns.Add(new MrpRun { MrpRunId = mrpRunId, PlantId = PlantId, IsDirty = dirty });
        _context.MrpRunLines.Add(new MrpRunLine
        {
            MrpRunId = mrpRunId, LineSeq = lineSeq, SalesOrderId = sourceOrderId,
            ProductCode = "PRD001", ProductionBatchId = batchId,
        });
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = mrpRunId + "-WO", ProductCode = "PRD001", Quantity = 10m,
            Status = woStatus, PlantId = PlantId, ProductionBatchId = batchId,
        });
        if (isIo)
            _context.InternalOrders.Add(new InternalOrder { InternalOrderId = sourceOrderId, PlantId = PlantId, CreatedBy = "u" });
        else
            _context.SalesOrders.Add(new SalesOrder { SalesOrderId = sourceOrderId, PlantId = PlantId, CreatedBy = "u" });
        _context.SaveChanges();
    }
}
