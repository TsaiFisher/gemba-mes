using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Modules.PUR.Controllers;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.BAS.Services;
using Xunit;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// Phase F §4.2 委外重工成本計算器 — 責任歸屬＋雙路徑（EP/BVA）。
/// </summary>
public class OutsourceReworkCostCalculatorTests
{
    // ── EP Valid：三種責任歸屬 ───────────────────────────────────────────────

    /// <summary>廠商責任：占比固定 100，扣款＝重工成本全額，加工費 0。</summary>
    [Fact]
    public void Compute_VendorLiability_FullChargebackNoFee()
    {
        var r = OutsourceReworkCostCalculator.Compute(
            ReworkLiabilityParty.Vendor, vendorLiablePct: 30m /* 忽略 */, reworkCost: 1200m, processingFee: 800m);

        Assert.Equal(100m, r.VendorLiablePct);
        Assert.Equal(1200m, r.VendorChargebackAmt);
        Assert.Equal(0m, r.ReworkProcessingFee);
    }

    /// <summary>我方責任：占比固定 0，加工費全額，扣款 0。</summary>
    [Fact]
    public void Compute_UsLiability_FullFeeNoChargeback()
    {
        var r = OutsourceReworkCostCalculator.Compute(
            ReworkLiabilityParty.Us, vendorLiablePct: 70m /* 忽略 */, reworkCost: 1200m, processingFee: 800m);

        Assert.Equal(0m, r.VendorLiablePct);
        Assert.Equal(0m, r.VendorChargebackAmt);
        Assert.Equal(800m, r.ReworkProcessingFee);
    }

    /// <summary>共同責任：扣款＝成本×廠商占比、加工費＝加工費×我方占比。</summary>
    [Fact]
    public void Compute_SharedLiability_SplitsByPct()
    {
        var r = OutsourceReworkCostCalculator.Compute(
            ReworkLiabilityParty.Shared, vendorLiablePct: 60m, reworkCost: 1000m, processingFee: 500m);

        Assert.Equal(60m, r.VendorLiablePct);
        Assert.Equal(600m, r.VendorChargebackAmt);   // 1000 × 60%
        Assert.Equal(200m, r.ReworkProcessingFee);   // 500 × 40%
    }

    // ── BVA：占比邊界 0 / 100 ───────────────────────────────────────────────

    [Fact]
    public void Compute_SharedPctZero_EqualsUs()
    {
        var r = OutsourceReworkCostCalculator.Compute(
            ReworkLiabilityParty.Shared, 0m, 1000m, 500m);
        Assert.Equal(0m, r.VendorChargebackAmt);
        Assert.Equal(500m, r.ReworkProcessingFee);
    }

    [Fact]
    public void Compute_SharedPct100_EqualsVendor()
    {
        var r = OutsourceReworkCostCalculator.Compute(
            ReworkLiabilityParty.Shared, 100m, 1000m, 500m);
        Assert.Equal(1000m, r.VendorChargebackAmt);
        Assert.Equal(0m, r.ReworkProcessingFee);
    }

    // ── 捨入：本位幣小數位、AwayFromZero ────────────────────────────────────

    [Fact]
    public void Compute_RoundsToRequestedDecimals_AwayFromZero()
    {
        // 100.005 × 33.33% = 33.331666…；dp=2 → 33.33；fee 200.005 × 66.67% = 133.343…→ 133.34
        var r = OutsourceReworkCostCalculator.Compute(
            ReworkLiabilityParty.Shared, 33.33m, 100.005m, 200.005m, amountDecimals: 2);
        Assert.Equal(33.33m, r.VendorChargebackAmt);
        Assert.Equal(133.34m, r.ReworkProcessingFee);
    }

    // ── EP Invalid ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    public void Compute_SharedPctOutOfRange_Throws(decimal pct)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OutsourceReworkCostCalculator.Compute(ReworkLiabilityParty.Shared, pct, 100m, 100m));
    }

    [Fact]
    public void Compute_NegativeCost_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OutsourceReworkCostCalculator.Compute(ReworkLiabilityParty.Vendor, 100m, -1m, 0m));
    }

    [Fact]
    public void Compute_NegativeFee_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OutsourceReworkCostCalculator.Compute(ReworkLiabilityParty.Us, 0m, 0m, -1m));
    }
}

/// <summary>
/// OutsourceReworkController 狀態流轉守門：Draft→Sent→Received、Void、Delete。
/// </summary>
public class OutsourceReworkControllerTests : TestBase
{
    private readonly Mock<IUserContext>       _mockUser  = new();
    private readonly Mock<IPermissionService> _mockPerm  = new();
    private readonly Mock<IDateTimeService>   _mockDt    = new();
    private readonly Mock<IDocNumberService>  _mockDocNo = new();
    private readonly Mock<IExportService>     _mockExport = new();
    private readonly OutsourceReworkController _controller;

    private const string PlantId = "P001";

    public OutsourceReworkControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns("user1");
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);
        _mockDocNo.Setup(d => d.NextAsync("OutsourceRework", It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("RW-2607-00001");

        _controller = new OutsourceReworkController(
            _context, _mockUser.Object, _mockPerm.Object, _mockDt.Object,
            _mockDocNo.Object, GetMockCurrencyService().Object, _mockExport.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
    }

    private OutsourceRework Seed(OutsourceReworkStatus status, string id = "RW-2607-00099")
    {
        var rec = new OutsourceRework
        {
            OutsourceReworkId = id, PlantId = PlantId, CreatedBy = "user1",
            ReworkQty = 10m, LiabilityParty = ReworkLiabilityParty.Vendor,
            VendorLiablePct = 100m, ReworkCost = 100m, VendorChargebackAmt = 100m,
            Status = status,
        };
        _context.OutsourceReworks.Add(rec);
        _context.SaveChanges();
        return rec;
    }

    private static bool JsonSuccess(IActionResult result)
    {
        var json = Assert.IsType<JsonResult>(result);
        var prop = json.Value!.GetType().GetProperty("success");
        return (bool)prop!.GetValue(json.Value)!;
    }

    [Fact]
    public async Task Save_New_ComputesCostsAndAssignsDocNo()
    {
        var vm = new OutsourceRework
        {
            ReworkQty = 5m, LiabilityParty = ReworkLiabilityParty.Shared,
            VendorLiablePct = 60m, ReworkCost = 1000m, ProcessingFee = 500m,
        };
        var result = await _controller.Save(vm);

        Assert.IsType<ContentResult>(result);   // 成功回 toast script
        var saved = Assert.Single(_context.OutsourceReworks.ToList());
        Assert.Equal("RW-2607-00001", saved.OutsourceReworkId);
        Assert.Equal(OutsourceReworkStatus.Draft, saved.Status);
        Assert.Equal(600m, saved.VendorChargebackAmt);
        Assert.Equal(200m, saved.ReworkProcessingFee);
    }

    [Fact]
    public async Task Save_ReworkQtyZero_Rejected()
    {
        var vm = new OutsourceRework { ReworkQty = 0m };
        var result = await _controller.Save(vm);
        Assert.IsNotType<ContentResult>(result);   // 驗證失敗回 _Form partial
        Assert.Empty(_context.OutsourceReworks.ToList());
    }

    [Fact]
    public async Task Save_New_WithImplicitRequiredPkModelError_IsClearedAndSaves()
    {
        // 迴歸守門：HTTP 模型繫結會為「空的非可空字串 PK」（OutsourceReworkId，由伺服端採番）
        // 加上隱含 [Required] 錯誤；Save 若未先 ModelState.Remove 之，ModelState 恆 invalid →
        // 面板靜默重渲染、建不出單（此縫由 DocActionSmoke 實機揪出）。既有 Save_New 測試直呼
        // controller、ModelState 預設為空(valid) 故漏掉，須手動 AddModelError 重現繫結行為。
        var vm = new OutsourceRework { ReworkQty = 3m, LiabilityParty = ReworkLiabilityParty.Vendor };
        _controller.ModelState.AddModelError(nameof(OutsourceRework.OutsourceReworkId), "為必填欄位。");

        var result = await _controller.Save(vm);

        Assert.IsType<ContentResult>(result);   // 成功回 toast script（非 _Form partial）
        Assert.Single(_context.OutsourceReworks.ToList());
    }

    [Fact]
    public async Task Save_UnknownDispatch_Rejected()
    {
        var vm = new OutsourceRework
        {
            ReworkQty = 5m, OriginalDispatchId = "D-NOT-EXIST",
            LiabilityParty = ReworkLiabilityParty.Vendor,
        };
        var result = await _controller.Save(vm);
        Assert.False(JsonSuccess(result));
        Assert.Empty(_context.OutsourceReworks.ToList());
    }

    [Fact]
    public async Task Send_Draft_TransitionsToSent()
    {
        var rec = Seed(OutsourceReworkStatus.Draft);
        Assert.True(JsonSuccess(await _controller.Send(rec.OutsourceReworkId)));
        Assert.Equal(OutsourceReworkStatus.Sent, rec.Status);
    }

    [Theory]
    [InlineData(OutsourceReworkStatus.Sent)]
    [InlineData(OutsourceReworkStatus.Received)]
    [InlineData(OutsourceReworkStatus.Void)]
    public async Task Send_NonDraft_Rejected(OutsourceReworkStatus status)
    {
        var rec = Seed(status);
        Assert.False(JsonSuccess(await _controller.Send(rec.OutsourceReworkId)));
        Assert.Equal(status, rec.Status);
    }

    [Fact]
    public async Task Receive_Sent_TransitionsToReceived()
    {
        var rec = Seed(OutsourceReworkStatus.Sent);
        Assert.True(JsonSuccess(await _controller.Receive(rec.OutsourceReworkId)));
        Assert.Equal(OutsourceReworkStatus.Received, rec.Status);
    }

    [Theory]
    [InlineData(OutsourceReworkStatus.Draft)]
    [InlineData(OutsourceReworkStatus.Received)]
    [InlineData(OutsourceReworkStatus.Void)]
    public async Task Receive_NonSent_Rejected(OutsourceReworkStatus status)
    {
        var rec = Seed(status);
        Assert.False(JsonSuccess(await _controller.Receive(rec.OutsourceReworkId)));
        Assert.Equal(status, rec.Status);
    }

    [Theory]
    [InlineData(OutsourceReworkStatus.Draft)]
    [InlineData(OutsourceReworkStatus.Sent)]
    public async Task Void_DraftOrSent_Succeeds(OutsourceReworkStatus status)
    {
        var rec = Seed(status);
        Assert.True(JsonSuccess(await _controller.Void(rec.OutsourceReworkId)));
        Assert.Equal(OutsourceReworkStatus.Void, rec.Status);
    }

    [Fact]
    public async Task Void_Received_Rejected()
    {
        var rec = Seed(OutsourceReworkStatus.Received);
        Assert.False(JsonSuccess(await _controller.Void(rec.OutsourceReworkId)));
        Assert.Equal(OutsourceReworkStatus.Received, rec.Status);
    }

    [Fact]
    public async Task Delete_Draft_Succeeds()
    {
        var rec = Seed(OutsourceReworkStatus.Draft);
        Assert.True(JsonSuccess(await _controller.Delete(rec.OutsourceReworkId)));
        Assert.Empty(_context.OutsourceReworks.ToList());
    }

    [Fact]
    public async Task Delete_Sent_Rejected()
    {
        var rec = Seed(OutsourceReworkStatus.Sent);
        Assert.False(JsonSuccess(await _controller.Delete(rec.OutsourceReworkId)));
        Assert.Single(_context.OutsourceReworks.ToList());
    }
}
