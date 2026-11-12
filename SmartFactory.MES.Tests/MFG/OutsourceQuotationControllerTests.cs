using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// OutsourceQuotationController.Save 新增守門——聚焦「伺服端採番單號的隱含必填縫」。
///
/// QuotationId 為非可空字串、由 DocNumberService 採番、Create 面板以 hidden 帶空字串送出；
/// HTTP 模型繫結會對其空值加隱含 [Required] 錯誤，Save 若未先 ModelState.Remove，新增 ModelState
/// 恆 invalid → 面板靜默重渲染建不出委外報價單（與 OutsourceReceipt/Rework 同縫，稽核時揪出）。
/// </summary>
public class OutsourceQuotationControllerTests : TestBase
{
    private readonly OutsourceQuotationController _controller;

    public OutsourceQuotationControllerTests()
    {
        var docNo = new Mock<IDocNumberService>();
        docNo.Setup(d => d.NextAsync("OutsourceQuotation", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OQ-2607-00001");

        _controller = new OutsourceQuotationController(
            GetMockLocalizer<ProjectZSharedResource>().Object,
            docNo.Object,
            new Mock<IUomService>().Object,
            GetMockCurrencyService().Object,
            // 真實實作而非 mock：本檔測的就是查價選中哪一筆，換成 mock 等於把待測行為
            // 換掉，測試會在 Resolve 壞掉時照樣全綠。
            new OutsourceQuotationResolver(_context));

        var perm = new Mock<IPermissionService>();
        perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        perm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        perm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _controller.ControllerContext = GetControllerContextWithServices(permService: perm.Object);
    }

    [Fact]
    public async Task Save_New_WithImplicitRequiredPkModelError_IsClearedAndSaves()
    {
        var model = new OutsourceQuotation
        {
            SupplierCode = "SUP001", ProductCode = "P-1",
            CurrencyCode = "TWD", UomCode = "EA",
        };
        _controller.ModelState.AddModelError(nameof(OutsourceQuotation.QuotationId), "為必填欄位。");

        var result = await _controller.Save(model);

        Assert.IsType<ContentResult>(result);   // 成功回 toast script（非 _Form partial）
        Assert.Single(_context.OutsourceQuotations.ToList());
    }

    /// <summary>
    /// 編輯路徑是逐欄位手抄到 existing 的，漏抄一欄不會有任何錯誤。
    ///
    /// 合約允收損耗率漏抄的話，改過一次備註的報價會靜靜變回「未約定」，
    /// 之後由它轉出去的委外採購單全部不扣款——而畫面上看起來完全正常。
    /// （同一個縫在委外收貨的 IsFinalReceipt 上已經踩過一次。）
    /// </summary>
    [Fact]
    public async Task Save_Edit_KeepsAllowedScrapRate()
    {
        _context.OutsourceQuotations.Add(new OutsourceQuotation
        {
            QuotationId = "OQ-2607-00009", SupplierCode = "SUP001", ProductCode = "P-1",
            CurrencyCode = "TWD", UomCode = "EA", UnitPrice = 25m, AllowedScrapRate = 0.05m,
        });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();

        var result = await _controller.Save(new OutsourceQuotation
        {
            QuotationId = "OQ-2607-00009", SupplierCode = "SUP001", ProductCode = "P-1",
            CurrencyCode = "TWD", UomCode = "EA", UnitPrice = 25m, AllowedScrapRate = 0.05m,
            Remarks = "改個備註",
        });

        Assert.IsType<ContentResult>(result);
        _context.ChangeTracker.Clear();
        Assert.Equal(0.05m,
            _context.OutsourceQuotations.Single(q => q.QuotationId == "OQ-2607-00009").AllowedScrapRate);
    }

    /// <summary>留白就是留白：null ＝ 合約未約定損耗條款，不可以被存成 0（＝零容忍）。</summary>
    [Fact]
    public async Task Save_New_AllowsNullAllowedScrapRate()
    {
        var result = await _controller.Save(new OutsourceQuotation
        {
            SupplierCode = "SUP001", ProductCode = "P-1",
            CurrencyCode = "TWD", UomCode = "EA", AllowedScrapRate = null,
        });

        Assert.IsType<ContentResult>(result);
        Assert.Null(Assert.Single(_context.OutsourceQuotations.ToList()).AllowedScrapRate);
    }

    /// <summary>
    /// 畫面自動帶入單價（<c>LookupPrice</c>）在**沒有任何一筆報價適用**時必須回
    /// <c>found=false</c>，不得退而求其次回傳別的品號的報價。
    ///
    /// <para>這是 2026-09-23 修掉的真缺陷：這支 API 原本只排序不過濾，於是
    /// 「廠商只報過 P-OTHER，現在要查 P-WANTED」這個現場最常見的狀態下，
    /// <c>FirstOrDefault</c> 照樣回傳 P-OTHER 的單價——畫面帶進一個**看起來完全正常**
    /// 的金額，一路快照到委外單、收貨明細、加工費暫估與 AP。</para>
    ///
    /// <para>同一段邏輯在批次產生委外單那一支早就修好了（<c>先過濾再排序</c>），
    /// 兩份實作於是對同一組條件給出不同答案，而兩個都不報錯。現已收斂到
    /// <c>IOutsourceQuotationResolver</c>，本測試守的是畫面這一側。</para>
    /// </summary>
    [Fact]
    public async Task LookupPrice_ReturnsNotFound_WhenNoQuotationApplies()
    {
        _context.OutsourceQuotations.Add(new OutsourceQuotation
        {
            QuotationId = "OQ-OTHER", SupplierCode = "SUP001", ProductCode = "P-OTHER",
            UnitPrice = 999m, CurrencyCode = "TWD", UomCode = "EA", IsActive = true,
            EffectiveFrom = new DateOnly(2020, 1, 1),
        });
        _context.SaveChanges();

        var json = Assert.IsType<JsonResult>(
            await _controller.LookupPrice("SUP001", "P-WANTED", routingId: null, operationSeq: null));

        Assert.False((bool)json.Value!.GetType().GetProperty("found")!.GetValue(json.Value)!);
    }

    /// <summary>
    /// 反面：廠商層級通用報價（<c>ProductCode == null</c> ＝ 不限品號）**該**被選中。
    /// 與上一支合起來才分得出「正確地不回傳」與「什麼都不回傳」——只有上一支的話，
    /// 把 Resolve 改成永遠回 null 也會全綠。
    /// </summary>
    [Fact]
    public async Task LookupPrice_FallsBackToSupplierLevelQuotation()
    {
        _context.OutsourceQuotations.Add(new OutsourceQuotation
        {
            QuotationId = "OQ-ANY", SupplierCode = "SUP001", ProductCode = null,
            UnitPrice = 12.5m, CurrencyCode = "TWD", UomCode = "EA", IsActive = true,
            EffectiveFrom = new DateOnly(2020, 1, 1),
        });
        _context.SaveChanges();

        var json = Assert.IsType<JsonResult>(
            await _controller.LookupPrice("SUP001", "P-WANTED", routingId: null, operationSeq: null));

        Assert.True((bool)json.Value!.GetType().GetProperty("found")!.GetValue(json.Value)!);
        Assert.Equal(12.5m, (decimal)json.Value!.GetType().GetProperty("unitPrice")!.GetValue(json.Value)!);
    }
}
