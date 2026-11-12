using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared.ReferenceValidation;
using SmartFactory.MES.Modules.QCR.Controllers;
using SmartFactory.MES.Modules.QCR.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QCR;

/// <summary>
/// 量具序號的保管部門／保管人：2026-10-02 由自由輸入文字框改為下拉（參照欄位盤點的發現），
/// 並登錄參照檢查規則。
/// </summary>
public class InstrumentSerialDropdownTests : TestBase
{
    private InstrumentController MakeController(string plant)
    {
        var user = new Mock<IUserContext>();
        user.Setup(u => u.PlantId).Returns(plant);
        return new InstrumentController(new Mock<IStringLocalizer<ProjectZSharedResource>>().Object)
        {
            ControllerContext = GetControllerContextWithServices(userContext: user.Object),
        };
    }

    [Fact]
    public async Task 部門下拉只列本廠單位_含沿上層繼承廠別者_保管人下拉列使用者()
    {
        _context.OrganizationUnits.AddRange(
            new OrganizationUnit { Id = "U-P1", Name = "品保課", PlantId = "P1" },
            new OrganizationUnit { Id = "U-P1-CHILD", Name = "量測組", ParentId = "U-P1" },
            new OrganizationUnit { Id = "U-P2", Name = "別廠", PlantId = "P2" });
        _context.Users.Add(new ApplicationUser { Id = "user-1", UserName = "alice" });
        await _context.SaveChangesAsync();
        // 存檔會把空廠別填成目前廠別；子單位改回空值，模擬「廠別沿上層繼承」的資料
        _context.OrganizationUnits.Single(u => u.Id == "U-P1-CHILD").PlantId = null;
        await _context.SaveChangesAsync();

        var result = Assert.IsType<ViewResult>(await MakeController("P1").CreateSerial("INS-1"));

        var deptIds = ((IEnumerable<dynamic>)result.ViewData["DeptOptions"]!).Select(d => (string)d.Id).ToList();
        Assert.Equal(new[] { "U-P1-CHILD", "U-P1" }.OrderBy(x => x), deptIds.OrderBy(x => x));
        var userIds = ((IEnumerable<dynamic>)result.ViewData["UserOptions"]!).Select(u => (string)u.Id).ToList();
        Assert.Contains("user-1", userIds);
    }

    [Fact]
    public async Task 保管人不是系統使用者_參照檢查擋下()
    {
        _context.CurrentPlantId = "P1";
        _context.InstrumentSerials.Add(new InstrumentSerial
        {
            SerialId = "S1", InstrumentCode = "INS-1", SerialNo = "001", PlantId = "P1", CustodianId = "王小明",
        });

        var v = Assert.Single(await ReferenceValidator.ValidateAsync(_context));
        Assert.Equal((nameof(InstrumentSerial.CustodianId), ReferenceMaster.User, ReferenceProblem.NotFound),
                     (v.Property, v.Master, v.Problem));
    }
}
