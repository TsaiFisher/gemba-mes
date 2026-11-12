using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Api.Mobile;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.QCR.Services;
using SmartFactory.MES.Services;
using MfgM = SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.Tests.Mobile;

/// <summary>
/// 手機製程檢驗頁（mfg/ipqc-process.html）與檢驗共用查詢：IPQC 規格讀取、IPQC 送出、
/// 抽樣數查表、待檢清單的 FQC／RQC 段。2026-09-28 盤點時這幾支 0 支測試。
///
/// <para>IQC 送出與待檢清單的 IQC 段走 <see cref="IIqcInspectionService"/>，已由
/// <c>IqcInspectionServiceTests</c>／<see cref="MobileInvIqcControllerTests"/> 涵蓋，這裡 mock 掉。</para>
/// </summary>
public class MobileInvInspectionControllerTests : TestBase
{
    private const string PlantId = "P001";

    private readonly Mock<IUserContext> _mockUser = new();
    private readonly Mock<IIqcInspectionService> _mockIqc = new();
    private readonly Mock<IMobilePermissionService> _mockMobilePerm = new();

    public MobileInvInspectionControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns("qc1");
        _mockUser.Setup(u => u.ActiveRoleId).Returns("role1");
        _mockMobilePerm.Setup(m => m.CanExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        _mockIqc.Setup(s => s.GetPendingLinesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IqcPendingLine>());
    }

    private MobileInvController Controller() => new(
        _context, _mockUser.Object, _mockIqc.Object, _mockMobilePerm.Object, new Mock<IDocNumberService>().Object);

    // ── 抽樣數查表 ───────────────────────────────────────────────────────

    [Fact]
    public void 抽樣數_依批量與驗證等級查表()
    {
        var data = OkData(Controller().GetSampleSize(lotSize: 40, vl: 4));

        Assert.Equal(MilStd1916Service.GetSampleSize(40, MilStd1916Service.VerificationLevel.IV), Prop<int>(data, "sampleSize"));
        Assert.Equal(0, Prop<int>(data, "acceptNumber"));   // MIL-STD-1916 恆為 Ac=0
    }

    [Theory]
    [InlineData(0, 4, "INVALID_LOT")]
    [InlineData(40, 0, "INVALID_VL")]
    [InlineData(40, 8, "INVALID_VL")]
    public void 抽樣數_輸入超出範圍回明確錯誤(decimal lotSize, int vl, string code)
    {
        AssertError<BadRequestObjectResult>(Controller().GetSampleSize(lotSize, vl), code);
    }

    // ── IPQC 規格 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 規格_品號專屬優先於通用規格_且只帶啟用中的項目()
    {
        SeedSpec("SP-GEN", productCode: null, vl: 3, ("通用尺寸", true));
        SeedSpec("SP-P1", productCode: "P1", vl: 5, ("外徑", true), ("已停用項目", false), ("內徑", true));

        var data = OkData(await Controller().GetIpqcSpec("P1", lotSize: 100));

        Assert.Equal("SP-P1", Prop<string>(data, "SpecId"));
        Assert.Equal(5, Prop<int>(data, "VerificationLevel"));
        Assert.Equal(MilStd1916Service.GetSampleSize(100, MilStd1916Service.VerificationLevel.V), Prop<int>(data, "sampleSize"));
        var names = Prop<System.Collections.IEnumerable>(data, "items").Cast<object>()
            .Select(i => Prop<string>(i, "ItemName")).ToList();
        Assert.Equal(new[] { "外徑", "內徑" }, names);
    }

    [Fact]
    public async Task 規格_沒有品號專屬時用通用規格()
    {
        SeedSpec("SP-GEN", productCode: null, vl: 3, ("通用尺寸", true));

        var data = OkData(await Controller().GetIpqcSpec("P-OTHER", lotSize: 100));

        Assert.Equal("SP-GEN", Prop<string>(data, "SpecId"));
    }

    [Fact]
    public async Task 規格_別廠的規格不得套用_都沒有時回預設VL4空項目()
    {
        SeedSpec("SP-OTHER-PLANT", productCode: "P1", vl: 5, plantId: "P999", items: new[] { ("外徑", true) });

        var data = OkData(await Controller().GetIpqcSpec("P1", lotSize: 100));

        Assert.Null(Prop<string?>(data, "specId"));
        Assert.Equal(4, Prop<int>(data, "verificationLevel"));
        Assert.Empty(Prop<System.Collections.IEnumerable>(data, "items").Cast<object>());
    }

    [Fact]
    public async Task 規格_未帶品號回明確錯誤()
    {
        AssertError<BadRequestObjectResult>(await Controller().GetIpqcSpec("", 1), "MISSING_PRODUCT");
    }

    // ── IPQC 送出 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 送出合格_建立IPQC檢驗單且派工照常生產()
    {
        SeedDispatch("D1", PlantId, MfgM.DispatchStatus.InProgress);

        var data = OkData(await Controller().SubmitIpqc(Ipqc("D1", actualSampleQty: 5, failQty: 0,
            items: new IpqcItemRequest(1, "外徑", "mm", 9.9m, 10m, 10.1m, 10.05m))));

        Assert.Equal("Pass", Prop<string>(data, "Status"));
        var order = Assert.Single(_context.InspectionOrders.Include(o => o.Items));
        Assert.Equal("IPQC", order.InspectionType);
        Assert.Equal("qc1", order.InspectorId);
        Assert.True(Assert.Single(order.Items).IsConforming);
        Assert.Equal(MfgM.DispatchStatus.InProgress, (await _context.Dispatches.SingleAsync()).Status);
    }

    [Fact]
    public async Task 送出不良_生產中的派工轉暫停()
    {
        SeedDispatch("D1", PlantId, MfgM.DispatchStatus.InProgress);

        var data = OkData(await Controller().SubmitIpqc(Ipqc("D1", actualSampleQty: 5, failQty: 1)));

        Assert.Equal("Fail", Prop<string>(data, "Status"));
        Assert.Equal(MfgM.DispatchStatus.OnHold, (await _context.Dispatches.SingleAsync()).Status);
    }

    [Fact]
    public async Task 別廠或不存在的派工_拒絕且不建檢驗單()
    {
        // 原本查不到派工也照建檢驗單：任何字串都能掛一張「合格」的 IPQC
        SeedDispatch("D1", "P999", MfgM.DispatchStatus.InProgress);

        AssertError<NotFoundObjectResult>(
            await Controller().SubmitIpqc(Ipqc("D1", actualSampleQty: 5, failQty: 1)), "DISPATCH_NOT_FOUND");
        AssertError<NotFoundObjectResult>(
            await Controller().SubmitIpqc(Ipqc("NOPE", actualSampleQty: 5, failQty: 0)), "DISPATCH_NOT_FOUND");

        Assert.Empty(_context.InspectionOrders.IgnoreQueryFilters());
        var d = await _context.Dispatches.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(MfgM.DispatchStatus.InProgress, d.Status);
    }

    // ── 伺服器端判定（2026-09-29 安全盤點）──────────────────────────────────

    [Fact]
    public async Task 規格上下限以規格主檔為準_送來放寬的上下限不採用()
    {
        // 量測頁的 LSL/USL 原本是可改的輸入框，改寬了再送，超規格的量測值就判合格
        SeedDispatch("D1", PlantId, MfgM.DispatchStatus.InProgress, workOrderProduct: "P1");
        SeedSpec("SP-P1", "P1", vl: 4, PlantId, new[] { ("外徑", true) }, lower: 9.9m, upper: 10.1m);

        var data = OkData(await Controller().SubmitIpqc(Ipqc("D1", actualSampleQty: 5, failQty: 0,
            items: new IpqcItemRequest(1, "外徑", "mm", LowerSpec: 0m, TargetValue: 10m, UpperSpec: 99m, MeasuredValue: 12m))));

        Assert.Equal("Fail", Prop<string>(data, "Status"));
        var order = await _context.InspectionOrders.Include(o => o.Items).SingleAsync();
        var item  = Assert.Single(order.Items);
        Assert.Equal((9.9m, 10.1m), (item.LowerSpec, item.UpperSpec));
        Assert.False(item.IsConforming);
        Assert.Equal(1, order.FailQty);   // 不良數填 0 但量測超規格：不可存成「不良 0、Fail」
        Assert.Equal(MfgM.DispatchStatus.OnHold, (await _context.Dispatches.SingleAsync()).Status);
    }

    [Fact]
    public async Task 規格項目以ID對應_改名也躲不掉()
    {
        SeedDispatch("D1", PlantId, MfgM.DispatchStatus.InProgress, workOrderProduct: "P1");
        SeedSpec("SP-P1", "P1", vl: 4, PlantId, new[] { ("外徑", true) }, lower: 9.9m, upper: 10.1m);
        var specItemId = (await _context.InspectionSpecItems.SingleAsync()).SpecItemId;

        var data = OkData(await Controller().SubmitIpqc(Ipqc("D1", actualSampleQty: 5, failQty: 0,
            items: new IpqcItemRequest(1, "自訂", null, null, null, UpperSpec: 99m, MeasuredValue: 12m, SpecItemId: specItemId))));

        Assert.Equal("Fail", Prop<string>(data, "Status"));
        Assert.Equal("外徑", (await _context.InspectionItems.SingleAsync()).ItemName);
    }

    [Fact]
    public async Task 自訂項目沿用送來的上下限_只填一邊也算數()
    {
        SeedDispatch("D1", PlantId, MfgM.DispatchStatus.InProgress, workOrderProduct: "P1");

        var data = OkData(await Controller().SubmitIpqc(Ipqc("D1", actualSampleQty: 5, failQty: 0,
            items: new IpqcItemRequest(1, "毛邊", "mm", LowerSpec: null, TargetValue: null, UpperSpec: 0.2m, MeasuredValue: 0.1m))));

        Assert.Equal("Pass", Prop<string>(data, "Status"));
        Assert.True((await _context.InspectionItems.SingleAsync()).IsConforming);
    }

    [Fact]
    public async Task 品號批量與應抽樣數由伺服器決定_送來的不採用()
    {
        SeedDispatch("D1", PlantId, MfgM.DispatchStatus.InProgress, workOrderProduct: "P1", quantity: 500);

        await Controller().SubmitIpqc(Ipqc("D1", actualSampleQty: 5, failQty: 0) with
        {
            ProductCode = "P-FAKE", LotSize = 1, SampleSize = 1,
        });

        var order = await _context.InspectionOrders.SingleAsync();
        Assert.Equal("P1", order.ProductCode);
        Assert.Equal(500m, order.LotSize);
        Assert.Equal(MilStd1916Service.GetSampleSize(500, MilStd1916Service.VerificationLevel.IV), order.SampleSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public async Task 驗證等級超出範圍回明確錯誤(int vl)
    {
        SeedDispatch("D1", PlantId, MfgM.DispatchStatus.InProgress);

        AssertError<BadRequestObjectResult>(
            await Controller().SubmitIpqc(Ipqc("D1", 5, 0) with { VerificationLevel = vl }), "INVALID_VL");
        Assert.Empty(_context.InspectionOrders);
    }

    [Fact]
    public async Task 規格查詢帶派工_依工單品號取規格()
    {
        // 量測頁原本拿工單號當品號查規格，品號專屬規格從沒出現過
        SeedDispatch("D1", PlantId, MfgM.DispatchStatus.InProgress, workOrderProduct: "P1", quantity: 100);
        SeedSpec("SP-GEN", productCode: null, vl: 3, ("通用尺寸", true));
        SeedSpec("SP-P1", "P1", vl: 5, ("外徑", true));

        var data = OkData(await Controller().GetIpqcSpec(productCode: "WO1", lotSize: 1, dispatchId: "D1"));

        Assert.Equal("SP-P1", Prop<string>(data, "SpecId"));
        Assert.Equal(MilStd1916Service.GetSampleSize(100, MilStd1916Service.VerificationLevel.V), Prop<int>(data, "sampleSize"));
    }

    [Fact]
    public async Task 規格查詢帶別廠派工回404()
    {
        SeedDispatch("D1", "P999", MfgM.DispatchStatus.InProgress);

        AssertError<NotFoundObjectResult>(await Controller().GetIpqcSpec(null, 1, dispatchId: "D1"), "DISPATCH_NOT_FOUND");
    }

    [Fact]
    public async Task 不良數不是整數_一律判不合格()
    {
        // Ac=0：只要有不良就拒收。曾以「≥ 1」判定，0.5 會被當成合格；
        // IQC（MilStd1916Service.IsAccepted）與 FPI 都是「== 0 才合格」
        SeedDispatch("D1", PlantId, MfgM.DispatchStatus.InProgress);

        var data = OkData(await Controller().SubmitIpqc(Ipqc("D1", actualSampleQty: 5, failQty: 0.5m)));

        Assert.Equal("Fail", Prop<string>(data, "Status"));
        Assert.Equal(1, (await _context.InspectionOrders.SingleAsync()).FailQty);   // 不可存成 0 而狀態是 Fail
        Assert.Equal(MfgM.DispatchStatus.OnHold, (await _context.Dispatches.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(5, -1)]   // 負的不良數曾被判合格
    [InlineData(0, 0)]    // 沒抽樣就沒有檢驗結果
    [InlineData(-5, 0)]
    [InlineData(5, 6)]    // 不良數多於抽樣數
    public async Task 數量不合理_拒絕且不建檢驗單(decimal actualSampleQty, decimal failQty)
    {
        SeedDispatch("D1", PlantId, MfgM.DispatchStatus.InProgress);

        AssertError<BadRequestObjectResult>(
            await Controller().SubmitIpqc(Ipqc("D1", actualSampleQty, failQty)), "INVALID_QTY");

        Assert.Empty(_context.InspectionOrders);
        Assert.Equal(MfgM.DispatchStatus.InProgress, (await _context.Dispatches.SingleAsync()).Status);
    }

    // ── 待檢清單的 FQC／RQC 段 ───────────────────────────────────────────

    [Fact]
    public async Task 待檢清單_只列本廠待檢的FQC與RQC()
    {
        SeedOrder("FQC", "Pending", PlantId, "SRC-FQC");
        SeedOrder("RQC", "Pending", PlantId, "SRC-RQC");
        SeedOrder("FQC", "Pass", PlantId, "SRC-DONE");       // 已判定
        SeedOrder("IPQC", "Pending", PlantId, "SRC-IPQC");   // 製程檢驗不在這張清單
        SeedOrder("FQC", "Pending", "P999", "SRC-OTHER");    // 別廠

        var rows = ((System.Collections.IEnumerable)OkData(await Controller().GetIqcPending())!).Cast<object>().ToList();

        Assert.Equal(new[] { "SRC-FQC", "SRC-RQC" },
            rows.Select(r => Prop<string>(r, "sourceDocId")).OrderBy(x => x).ToArray());
    }

    // ── 輔助 ─────────────────────────────────────────────────────────────

    private static IpqcSubmitRequest Ipqc(string dispatchId, decimal actualSampleQty, decimal failQty,
        params IpqcItemRequest[] items) =>
        new("IPQC", dispatchId, "P1", LotSize: 100, SampleSize: 5, VerificationLevel: 4,
            actualSampleQty, failQty, Status: null, Remarks: null, items.ToList());

    private void SeedSpec(string specId, string? productCode, int vl,
        params (string Name, bool Active)[] items) => SeedSpec(specId, productCode, vl, PlantId, items);

    private void SeedSpec(string specId, string? productCode, int vl, string plantId,
        params (string Name, bool Active)[] items) => SeedSpec(specId, productCode, vl, plantId, items, null, null);

    private void SeedSpec(string specId, string? productCode, int vl, string plantId,
        (string Name, bool Active)[] items, decimal? lower, decimal? upper)
    {
        var spec = new InspectionSpec
        {
            SpecId = specId, SpecCode = specId, SpecName = specId, PlantId = plantId,
            ProductCode = productCode, VerificationLevel = vl, IsActive = true,
        };
        var seq = 1;
        foreach (var (name, active) in items)
            spec.Items.Add(new InspectionSpecItem
            {
                SpecId = specId, SeqNo = seq++, ItemName = name, IsActive = active, LowerSpec = lower, UpperSpec = upper,
            });
        _context.InspectionSpecs.Add(spec);
        _context.SaveChanges();
    }

    private void SeedDispatch(string dispatchId, string plantId, MfgM.DispatchStatus status,
        string? workOrderProduct = null, decimal quantity = 100)
    {
        if (workOrderProduct != null)
            _context.WorkOrders.Add(new MfgM.WorkOrder
            {
                WorkOrderId = "WO1", ProductionBatchId = "B1", ProductCode = workOrderProduct,
                RoutingId = "R1", Quantity = quantity, PlantId = plantId, Status = MfgM.WorkOrderStatus.InProgress,
            });
        _context.Dispatches.Add(new MfgM.Dispatch
        {
            DispatchId = dispatchId, WorkOrderId = "WO1", OperationSeq = 10, Quantity = quantity,
            Status = status, MachineCode = "M1", PlantId = plantId,
        });
        _context.SaveChanges();
    }

    private void SeedOrder(string type, string status, string plantId, string sourceDocId)
    {
        _context.InspectionOrders.Add(new InspectionOrder
        {
            InspectionType = type, Status = status, PlantId = plantId, SourceDocId = sourceDocId, ProductCode = "P1",
        });
        _context.SaveChanges();
    }

    private static object? OkData(IActionResult result) =>
        Assert.IsType<MobileOkResult>(Assert.IsType<OkObjectResult>(result).Value).Data;

    private static void AssertError<T>(IActionResult result, string code) where T : ObjectResult
    {
        var body = Assert.IsType<MobileErrorResult>(Assert.IsType<T>(result).Value);
        Assert.Equal(code, body.Error?.Code);
    }

    private static T Prop<T>(object? obj, string name) =>
        (T)obj!.GetType().GetProperty(name)!.GetValue(obj)!;
}
