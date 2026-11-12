using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.BAS.Controllers;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.BAS;

public class DocNumberConfigControllerTests : TestBase
{
    private DocNumberConfigController CreateController(bool ajax = false)
    {
        var mockPerm = new Mock<IPermissionService>();
        mockPerm.Setup(x => x.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        mockPerm.Setup(x => x.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        mockPerm.Setup(x => x.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        mockPerm.Setup(x => x.CanDelete(It.IsAny<string>())).ReturnsAsync(true);
        mockPerm.Setup(x => x.CanPrint(It.IsAny<string>())).ReturnsAsync(true);

        var mockDocNumSvc = new Mock<IDocNumberService>();

        var ctrl = new DocNumberConfigController(_context, mockDocNumSvc.Object, mockPerm.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        ctrl.ControllerContext = GetControllerContext();
        if (ajax)
            ctrl.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        return ctrl;
    }

    // Delete_AlreadyIssuedSequence_ReturnsError
    [Fact]
    public async Task Delete_AlreadyIssuedSequence_ReturnsError()
    {
        _context.DocNumberConfigs.Add(new DocNumberConfig { DocType = "TESTDOC", DocTypeName = "Test", Prefix = "TD" });
        // 序號 scope = 設定的適用廠別：預設規則（""）發出的號碼記在 "" scope
        _context.DocNumberSequences.Add(new DocNumberSequence { DocType = "TESTDOC", PlantId = "", Period = "2026", LastSeq = 3 });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.DeleteConfirmed("TESTDOC");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(await _context.DocNumberConfigs.AnyAsync(c => c.DocType == "TESTDOC"));
    }

    // Delete_NeverIssued_Succeeds
    [Fact]
    public async Task Delete_NeverIssued_Succeeds()
    {
        _context.DocNumberConfigs.Add(new DocNumberConfig { DocType = "TESTDOC2", DocTypeName = "Test2", Prefix = "TD2" });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.DeleteConfirmed("TESTDOC2");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.False(await _context.DocNumberConfigs.AnyAsync(c => c.DocType == "TESTDOC2"));
    }

    // Delete_SystemType_ReturnsError
    [Fact]
    public async Task Delete_SystemType_ReturnsError()
    {
        _context.DocNumberConfigs.Add(new DocNumberConfig { DocType = "SYSDOC", DocTypeName = "System", Prefix = "SY", IsSystemType = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.DeleteConfirmed("SYSDOC");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(await _context.DocNumberConfigs.AnyAsync(c => c.DocType == "SYSDOC"));
    }
}
