using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.BAS.Controllers;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.BAS;

/// <summary>
/// 單號重置週期變更的防撞測試（實際案例：StockAdjustment 年改月後
/// 新期別序號從 1 起跳，重發 IA-2607-00001 撞 PK）：
/// - IsReplaySafe：重置週期粒度不可比單號日期段細
/// - RealignSequencesAsync：週期變更時把當前期別序號墊高到歷史最大值
/// - DocNumberConfigController：組合驗證 + 週期變更觸發校準
/// </summary>
public class DocNumberRealignTests : TestBase
{
    private static string CurrentMonthPeriod => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyyMM");
    private static string CurrentYearPeriod  => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy");

    // ── IsReplaySafe ────────────────────────────────────────────────────

    [Theory]
    [InlineData(DocDateFormat.None,     DocSeqReset.Never,   true)]
    [InlineData(DocDateFormat.None,     DocSeqReset.Yearly,  false)]  // 每年重置但單號無年度 → 每年重發
    [InlineData(DocDateFormat.None,     DocSeqReset.Monthly, false)]
    [InlineData(DocDateFormat.YY,       DocSeqReset.Yearly,  true)]
    [InlineData(DocDateFormat.YY,       DocSeqReset.Monthly, false)]  // 每月重置但單號只有年 → 每月重發
    [InlineData(DocDateFormat.YYMM,     DocSeqReset.Monthly, true)]
    [InlineData(DocDateFormat.YYMM,     DocSeqReset.Yearly,  true)]
    [InlineData(DocDateFormat.YYYYMMDD, DocSeqReset.Monthly, true)]
    public void IsReplaySafe_重置粒度比日期段細的組合擋下(DocDateFormat fmt, DocSeqReset reset, bool expected)
        => Assert.Equal(expected, DocNumberService.IsReplaySafe(fmt, reset));

    // ── RealignSequencesAsync ───────────────────────────────────────────

    [Fact]
    public async Task Realign_年改月_新期別列以歷史最大序號建立()
    {
        _context.DocNumberSequences.Add(new DocNumberSequence
            { DocType = "SalesOrder", PlantId = "", Period = CurrentYearPeriod, LastSeq = 12 });
        await _context.SaveChangesAsync();

        await new DocNumberService(_context).RealignSequencesAsync("SalesOrder", "", DocSeqReset.Monthly);

        var row = await _context.DocNumberSequences.SingleAsync(s =>
            s.DocType == "SalesOrder" && s.PlantId == "" && s.Period == CurrentMonthPeriod);
        Assert.Equal(12, row.LastSeq);   // 下一號 = 13，不會重發 1..12
    }

    [Fact]
    public async Task Realign_既有低序號期別列_墊高至歷史最大()
    {
        _context.DocNumberSequences.AddRange(
            new DocNumberSequence { DocType = "MrpRun", PlantId = "", Period = CurrentYearPeriod,  LastSeq = 3 },
            new DocNumberSequence { DocType = "MrpRun", PlantId = "", Period = CurrentMonthPeriod, LastSeq = 2 });
        await _context.SaveChangesAsync();

        await new DocNumberService(_context).RealignSequencesAsync("MrpRun", "", DocSeqReset.Monthly);

        var row = await _context.DocNumberSequences.SingleAsync(s =>
            s.DocType == "MrpRun" && s.Period == CurrentMonthPeriod);
        Assert.Equal(3, row.LastSeq);
    }

    [Fact]
    public async Task Realign_當前期別已是最大_不動()
    {
        _context.DocNumberSequences.AddRange(
            new DocNumberSequence { DocType = "PurchaseRequest", PlantId = "", Period = CurrentYearPeriod,  LastSeq = 2 },
            new DocNumberSequence { DocType = "PurchaseRequest", PlantId = "", Period = CurrentMonthPeriod, LastSeq = 4 });
        await _context.SaveChangesAsync();

        await new DocNumberService(_context).RealignSequencesAsync("PurchaseRequest", "", DocSeqReset.Monthly);

        var row = await _context.DocNumberSequences.SingleAsync(s =>
            s.DocType == "PurchaseRequest" && s.Period == CurrentMonthPeriod);
        Assert.Equal(4, row.LastSeq);
    }

    [Fact]
    public async Task Realign_只動指定scope_他scope不受影響()
    {
        // scope=適用廠別："" 為共用預設規則的序號，"P002" 為專屬規則的序號
        _context.DocNumberSequences.AddRange(
            new DocNumberSequence { DocType = "ScrapDoc", PlantId = "",     Period = CurrentYearPeriod, LastSeq = 9 },
            new DocNumberSequence { DocType = "ScrapDoc", PlantId = "P002", Period = CurrentYearPeriod, LastSeq = 5 });
        await _context.SaveChangesAsync();

        await new DocNumberService(_context).RealignSequencesAsync("ScrapDoc", "", DocSeqReset.Monthly);

        Assert.Equal(9, (await _context.DocNumberSequences.SingleAsync(s =>
            s.DocType == "ScrapDoc" && s.PlantId == "" && s.Period == CurrentMonthPeriod)).LastSeq);
        Assert.False(await _context.DocNumberSequences.AnyAsync(s =>
            s.DocType == "ScrapDoc" && s.PlantId == "P002" && s.Period == CurrentMonthPeriod));
    }

    [Fact]
    public async Task Realign_其他單據型別不受影響()
    {
        _context.DocNumberSequences.AddRange(
            new DocNumberSequence { DocType = "LoanDoc",  PlantId = "", Period = CurrentYearPeriod, LastSeq = 11 },
            new DocNumberSequence { DocType = "SampleDoc", PlantId = "", Period = CurrentYearPeriod, LastSeq = 7 });
        await _context.SaveChangesAsync();

        await new DocNumberService(_context).RealignSequencesAsync("LoanDoc", "", DocSeqReset.Monthly);

        Assert.False(await _context.DocNumberSequences.AnyAsync(s =>
            s.DocType == "SampleDoc" && s.Period == CurrentMonthPeriod));
    }

    // ── DocNumberConfigController ───────────────────────────────────────

    private DocNumberConfigController BuildController(Mock<IDocNumberService> mockDocNo, bool ajax = true)
    {
        var mockPerm = new Mock<IPermissionService>();
        mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);

        var ctrl = new DocNumberConfigController(
            _context, mockDocNo.Object, mockPerm.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object)
        {
            ControllerContext = GetControllerContextWithServices(permService: mockPerm.Object)
        };
        if (ajax)
            ctrl.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        return ctrl;
    }

    private async Task<DocNumberConfig> SeedConfigAsync(
        DocSeqReset reset = DocSeqReset.Yearly, DocDateFormat fmt = DocDateFormat.YYMM)
    {
        var config = new DocNumberConfig
        {
            DocType = "TestDoc", DocTypeName = "庫存調整單", Prefix = "IA",
            DateFormat = fmt, ResetCycle = reset, SeqLength = 5, Separator = "-", IsActive = true,
        };
        _context.DocNumberConfigs.Add(config);
        await _context.SaveChangesAsync();
        return config;
    }

    [Fact]
    public async Task Edit_重置週期變更_觸發序號校準()
    {
        await SeedConfigAsync(DocSeqReset.Yearly);
        var mockDocNo = new Mock<IDocNumberService>();
        var ctrl = BuildController(mockDocNo);

        var result = await ctrl.Edit("TestDoc", new DocNumberConfig
        {
            DocType = "TestDoc", DocTypeName = "庫存調整單", Prefix = "IA",
            DateFormat = DocDateFormat.YYMM, ResetCycle = DocSeqReset.Monthly,
            SeqLength = 5, Separator = "-", IsActive = true,
        });

        Assert.IsType<JsonResult>(result);
        mockDocNo.Verify(d => d.RealignSequencesAsync(
            "TestDoc", "", DocSeqReset.Monthly, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Edit_重置週期未變_不觸發校準()
    {
        await SeedConfigAsync(DocSeqReset.Monthly);
        var mockDocNo = new Mock<IDocNumberService>();
        var ctrl = BuildController(mockDocNo);

        var result = await ctrl.Edit("TestDoc", new DocNumberConfig
        {
            DocType = "TestDoc", DocTypeName = "改名字而已", Prefix = "IA",
            DateFormat = DocDateFormat.YYMM, ResetCycle = DocSeqReset.Monthly,
            SeqLength = 5, Separator = "-", IsActive = true,
        });

        Assert.IsType<JsonResult>(result);
        mockDocNo.Verify(d => d.RealignSequencesAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DocSeqReset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Edit_月重置配無月份日期段_擋下()
    {
        await SeedConfigAsync(DocSeqReset.Yearly, DocDateFormat.YYMM);
        var ctrl = BuildController(new Mock<IDocNumberService>());

        var result = await ctrl.Edit("TestDoc", new DocNumberConfig
        {
            DocType = "TestDoc", DocTypeName = "庫存調整單", Prefix = "IA",
            DateFormat = DocDateFormat.YY, ResetCycle = DocSeqReset.Monthly,   // 月重置但單號只有年
            SeqLength = 5, Separator = "-", IsActive = true,
        });

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("_DocNumberConfigForm", partial.ViewName);
        Assert.Contains(ctrl.ModelState[nameof(DocNumberConfig.ResetCycle)]!.Errors,
            e => e.ErrorMessage == "DocNumberResetFinerThanDate");
    }

    [Fact]
    public async Task Create_月重置配無日期段_擋下()
    {
        var ctrl = BuildController(new Mock<IDocNumberService>());

        var result = await ctrl.Create(new DocNumberConfig
        {
            DocType = "NewDoc", DocTypeName = "新單據", Prefix = "ND",
            DateFormat = DocDateFormat.None, ResetCycle = DocSeqReset.Monthly,
            SeqLength = 5, Separator = "-", IsActive = true,
        });

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Contains(ctrl.ModelState[nameof(DocNumberConfig.ResetCycle)]!.Errors,
            e => e.ErrorMessage == "DocNumberResetFinerThanDate");
    }

    // ── 適用廠別解析與指紋守門 ──────────────────────────────────────────

    [Fact]
    public async Task NextAsync_廠別無專屬且無預設規則_擲明確錯誤()
    {
        var ex = await Assert.ThrowsAsync<DocNumberConfigNotFoundException>(() =>
            new DocNumberService(_context).NextAsync("NoSuchDoc", "P009"));
        Assert.Equal("NoSuchDoc", ex.DocType);
        Assert.Equal("P009", ex.PlantId);
    }

    [Fact]
    public async Task Create_同型別編號規則指紋重複_擋下()
    {
        await SeedConfigAsync(DocSeqReset.Yearly);   // TestDoc / 預設 / Prefix "IA"
        var ctrl = BuildController(new Mock<IDocNumberService>());

        var result = await ctrl.Create(new DocNumberConfig
        {
            DocType = "TestDoc", PlantId = "P002", DocTypeName = "P002 專屬", Prefix = "IA",
            DateFormat = DocDateFormat.YYMM, ResetCycle = DocSeqReset.Yearly,
            SeqLength = 5, Separator = "-", IsActive = true,
        });

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Contains(ctrl.ModelState[nameof(DocNumberConfig.Prefix)]!.Errors,
            e => e.ErrorMessage == "DocNumberStemDuplicate");
    }

    [Fact]
    public async Task Create_同型別不同前置碼_允許建立廠別專屬規則()
    {
        await SeedConfigAsync(DocSeqReset.Yearly);
        var ctrl = BuildController(new Mock<IDocNumberService>());

        var result = await ctrl.Create(new DocNumberConfig
        {
            DocType = "TestDoc", PlantId = "P002", DocTypeName = "P002 專屬", Prefix = "IA2",
            DateFormat = DocDateFormat.YYMM, ResetCycle = DocSeqReset.Yearly,
            SeqLength = 5, Separator = "-", IsActive = true,
        });

        Assert.IsType<JsonResult>(result);
        // 專屬規則 PK 為 (DocType, ScopeKey=GUID)，改以適用廠別欄位查詢
        Assert.NotNull(await _context.DocNumberConfigs
            .SingleOrDefaultAsync(c => c.DocType == "TestDoc" && c.PlantId == "P002"));
    }

    // ── 適用廠別 Checkbox 多選（同規則多廠共用一筆）─────────────────────

    [Fact]
    public async Task Create_勾選多廠別_正規化排序後存一筆()
    {
        await SeedConfigAsync(DocSeqReset.Yearly);   // TestDoc / 預設 / Prefix "IA"
        var ctrl = BuildController(new Mock<IDocNumberService>());

        var result = await ctrl.Create(new DocNumberConfig
        {
            DocType = "TestDoc", DocTypeName = "多廠共用規則", Prefix = "IA3",
            DateFormat = DocDateFormat.YYMM, ResetCycle = DocSeqReset.Yearly,
            SeqLength = 5, Separator = "-", IsActive = true,
        }, plantIds: ["P003", "P001", "P002"]);   // 勾選順序不影響儲存結果

        Assert.IsType<JsonResult>(result);
        var saved = await _context.DocNumberConfigs
            .SingleOrDefaultAsync(c => c.DocType == "TestDoc" && c.PlantId == "P001,P002,P003");
        Assert.NotNull(saved);
        Assert.Equal(new[] { "P001", "P002", "P003" }, saved!.PlantIdList);
        Assert.False(string.IsNullOrEmpty(saved.ScopeKey));   // 專屬規則指派了不變的 GUID scope 鍵
    }

    [Fact]
    public async Task Create_未勾任何廠別_建立為預設規則()
    {
        var ctrl = BuildController(new Mock<IDocNumberService>());

        var result = await ctrl.Create(new DocNumberConfig
        {
            DocType = "NewDefaultDoc", DocTypeName = "預設規則", Prefix = "ND",
            DateFormat = DocDateFormat.YY, ResetCycle = DocSeqReset.Yearly,
            SeqLength = 5, Separator = "-", IsActive = true,
        }, plantIds: null);

        Assert.IsType<JsonResult>(result);
        var saved = await _context.DocNumberConfigs.FindAsync("NewDefaultDoc", "");
        Assert.NotNull(saved);
    }

    [Fact]
    public async Task Create_勾選廠別已被其他規則涵蓋_擋下()
    {
        await SeedConfigAsync(DocSeqReset.Yearly);   // TestDoc / 預設 / Prefix "IA"
        var ctrl = BuildController(new Mock<IDocNumberService>());
        await ctrl.Create(new DocNumberConfig
        {
            DocType = "TestDoc", DocTypeName = "P001+P002 共用規則", Prefix = "IAX",
            DateFormat = DocDateFormat.YYMM, ResetCycle = DocSeqReset.Yearly,
            SeqLength = 5, Separator = "-", IsActive = true,
        }, plantIds: ["P001", "P002"]);

        // 另一筆規則想勾 P002（已被上一筆涵蓋）——應擋下，不建立
        var result = await ctrl.Create(new DocNumberConfig
        {
            DocType = "TestDoc", DocTypeName = "P002+P003 共用規則", Prefix = "IAY",
            DateFormat = DocDateFormat.YYMM, ResetCycle = DocSeqReset.Yearly,
            SeqLength = 5, Separator = "-", IsActive = true,
        }, plantIds: ["P002", "P003"]);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Contains(ctrl.ModelState[nameof(DocNumberConfig.PlantId)]!.Errors,
            e => e.ErrorMessage == "DocNumberPlantOverlap");
        Assert.Null(await _context.DocNumberConfigs
            .SingleOrDefaultAsync(c => c.DocType == "TestDoc" && c.PlantId == "P002,P003"));
    }

    // ── 適用廠別可編輯（ScopeKey 解耦後）─────────────────────────────────

    [Fact]
    public async Task Edit_專屬規則增減適用廠別_ScopeKey不變且PlantId更新()
    {
        await SeedConfigAsync(DocSeqReset.Yearly);   // TestDoc / 預設 / Prefix "IA"
        var ctrl = BuildController(new Mock<IDocNumberService>());
        // 先建一筆 P001,P002 共用的專屬規則
        await ctrl.Create(new DocNumberConfig
        {
            DocType = "TestDoc", DocTypeName = "P001+P002", Prefix = "IB",
            DateFormat = DocDateFormat.YYMM, ResetCycle = DocSeqReset.Yearly,
            SeqLength = 5, Separator = "-", IsActive = true,
        }, plantIds: ["P001", "P002"]);
        var rule = await _context.DocNumberConfigs
            .SingleAsync(c => c.DocType == "TestDoc" && c.PlantId == "P001,P002");
        var frozenScope = rule.ScopeKey;
        _context.ChangeTracker.Clear();

        // 編輯：把適用廠別改成 P001,P003（移除 P002、加入 P003）
        var result = await ctrl.Edit("TestDoc", new DocNumberConfig
        {
            DocType = "TestDoc", ScopeKey = frozenScope, DocTypeName = "P001+P003", Prefix = "IB",
            DateFormat = DocDateFormat.YYMM, ResetCycle = DocSeqReset.Yearly,
            SeqLength = 5, Separator = "-", IsActive = true,
        }, plantIds: ["P003", "P001"]);

        Assert.IsType<JsonResult>(result);
        var reloaded = await _context.DocNumberConfigs
            .SingleAsync(c => c.DocType == "TestDoc" && c.ScopeKey == frozenScope);
        Assert.Equal("P001,P003", reloaded.PlantId);        // 適用廠別已更新
        Assert.Equal(frozenScope, reloaded.ScopeKey);       // 序號 scope 鍵不變（序號延續）
    }

    [Fact]
    public async Task Edit_專屬規則清空適用廠別_擋下()
    {
        await SeedConfigAsync(DocSeqReset.Yearly);
        var ctrl = BuildController(new Mock<IDocNumberService>());
        await ctrl.Create(new DocNumberConfig
        {
            DocType = "TestDoc", DocTypeName = "P001 專屬", Prefix = "IB",
            DateFormat = DocDateFormat.YYMM, ResetCycle = DocSeqReset.Yearly,
            SeqLength = 5, Separator = "-", IsActive = true,
        }, plantIds: ["P001"]);
        var rule = await _context.DocNumberConfigs
            .SingleAsync(c => c.DocType == "TestDoc" && c.PlantId == "P001");
        _context.ChangeTracker.Clear();

        // 一個廠都不勾（清空）——專屬規則不允許清空，應擋下
        var result = await ctrl.Edit("TestDoc", new DocNumberConfig
        {
            DocType = "TestDoc", ScopeKey = rule.ScopeKey, DocTypeName = "P001 專屬", Prefix = "IB",
            DateFormat = DocDateFormat.YYMM, ResetCycle = DocSeqReset.Yearly,
            SeqLength = 5, Separator = "-", IsActive = true,
        }, plantIds: []);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Contains(ctrl.ModelState[nameof(DocNumberConfig.PlantId)]!.Errors,
            e => e.ErrorMessage == "DocNumberPlantEmptyForbidden");
    }

    [Fact]
    public async Task Edit_已編號規則改指紋欄位_擋下()
    {
        await SeedConfigAsync(DocSeqReset.Yearly);   // TestDoc / 預設 / Prefix "IA"
        // 該 scope（""）已編過號 → 指紋欄位鎖定
        _context.DocNumberSequences.Add(new DocNumberSequence
            { DocType = "TestDoc", PlantId = "", Period = CurrentYearPeriod, LastSeq = 5 });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var ctrl = BuildController(new Mock<IDocNumberService>());
        var result = await ctrl.Edit("TestDoc", new DocNumberConfig
        {
            DocType = "TestDoc", ScopeKey = "", DocTypeName = "改前置碼", Prefix = "IZ",   // 改指紋
            DateFormat = DocDateFormat.YYMM, ResetCycle = DocSeqReset.Yearly,
            SeqLength = 5, Separator = "-", IsActive = true,
        });

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Contains(ctrl.ModelState[nameof(DocNumberConfig.Prefix)]!.Errors,
            e => e.ErrorMessage == "DocNumberStemLockedInUse");
    }

    [Fact]
    public async Task Edit_已編號規則只改名稱_允許()
    {
        await SeedConfigAsync(DocSeqReset.Yearly);
        _context.DocNumberSequences.Add(new DocNumberSequence
            { DocType = "TestDoc", PlantId = "", Period = CurrentYearPeriod, LastSeq = 5 });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var ctrl = BuildController(new Mock<IDocNumberService>());
        var result = await ctrl.Edit("TestDoc", new DocNumberConfig
        {
            DocType = "TestDoc", ScopeKey = "", DocTypeName = "只改顯示名稱", Prefix = "IA",  // 指紋不變
            DateFormat = DocDateFormat.YYMM, ResetCycle = DocSeqReset.Yearly,
            SeqLength = 5, Separator = "-", IsActive = false,
        });

        Assert.IsType<JsonResult>(result);   // 名稱/啟用可改，不受指紋鎖限制
    }
}
