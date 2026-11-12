using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 途程用料的發料方式（發料／領料／後沖）要能從畫面存進去，而且存檔不能把它洗掉。
/// 2026-10-10 查到：編輯畫面沒有這一欄、存檔整份重建用料時也沒帶它與後沖時機——
/// 種子或匯入設好的「領料」只要在桌機存一次途程就全部變回未設定（＝發料），
/// 手機領料申請（只開放領料用料）因此無料可申請。
/// </summary>
public class RoutingBomIssueMethodTests : TestBase
{
    private const string PlantId = "P001";

    public RoutingBomIssueMethodTests() => _context.CurrentPlantId = PlantId;

    private RoutingController BuildController() => new(GetMockLocalizer<ProjectZSharedResource>().Object)
    {
        ControllerContext = GetControllerContextWithServices(),
        Url = new Moq.Mock<IUrlHelper>().Object,
    };

    [Fact]
    public async Task Create_用料帶發料方式與後沖時機_原樣存入()
    {
        const string json = """
            [{"operationSeq":10,"workCenterId":"WC1","uomCode":"PC","boms":[
              {"bomSeq":10,"productCode":"M-PULL","quantity":1,"uomCode":"PC","issueMethod":1,"backflushTiming":0},
              {"bomSeq":20,"productCode":"M-BF","quantity":2,"uomCode":"PC","issueMethod":2,"backflushTiming":1}]}]
            """;

        Assert.IsType<RedirectToActionResult>(await BuildController().Create(
            new Routing { RoutingId = "R-IM", RoutingName = "發料方式", ProductCode = "FG-IM" }, json));

        var boms = await _context.Set<RoutingBom>().AsNoTracking()
            .Where(b => b.RoutingId == "R-IM").OrderBy(b => b.BomSeq).ToListAsync();
        Assert.Equal(2, boms.Count);
        Assert.Equal(MaterialIssueMethod.Pull, boms[0].IssueMethod);
        Assert.Equal(MaterialIssueMethod.Backflush, boms[1].IssueMethod);
        Assert.Equal(BackflushTiming.PerReport, boms[1].BackflushTiming);
    }
}
