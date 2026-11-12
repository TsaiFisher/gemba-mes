using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 完工入庫的 FQC 閘控（mfg-domain.md §14.1 / Phase G-H）。
///
/// 這道閘門在 <c>ProductionCompletionController.Complete</c> 裡，是**成品入庫前的最後一關**：
/// <c>InspectionSpec.RequiresFQC = true</c> 時，沒有一張 Pass 的 FQC 檢驗單就不准繳庫。
/// 它失效的樣子是「未經檢驗的成品照樣進倉」——數量對、成本對、傳票平，
/// 17 支資料稽核沒有一支看品質，所以**沒有任何下游會叫**。
///
/// 閘門的判定由四個條件 AND 起來（型別 / 來源單 / 狀態 / 規格啟用與廠別），
/// 任何一個寫鬆都不會讓測試變紅而只會讓閘門變成裝飾，故逐條釘死。
/// 其中最危險的是**型別**：FPI（首件）與 FQC（完工）共用 <c>InspectionOrders</c> 一張表、
/// 來源單同樣是工單號，而首件檢驗本來就會在完工前留下一張 Pass——
/// 漏掉 <c>InspectionType == "FQC"</c> 這個條件，閘門會對每一張有做首件的工單自動放行。
/// </summary>
public class ProductionCompletionFqcGateTests : TestBase
{
    private const string Plant   = "P001";
    private const string OtherPlant = "P002";
    private const string WoId    = "WO2026FQC1";
    private const string Product = "FG-001";
    private const string Wh      = "F001";
    private const string Loc     = "F001-1";

    private readonly Mock<IProductionCompletionService> _completion = new();
    private readonly Mock<IUserContext> _user = new();

    public ProductionCompletionFqcGateTests()
    {
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = WoId, ProductCode = Product, Quantity = 10m,
            PlantId = Plant, Status = WorkOrderStatus.Completed,
        });
        _context.SaveChanges();
    }

    private ProductionCompletionController BuildController()
    {
        _context.CurrentPlantId = Plant;
        _user.Setup(u => u.PlantId).Returns(Plant);
        _user.Setup(u => u.UserId).Returns("usr1");

        return new ProductionCompletionController(
            GetMockLocalizer<ProjectZSharedResource>().Object,
            _completion.Object,
            new Mock<IPlantParamService>().Object,
            new Mock<ISamplingPlanService>().Object,
            new Mock<IUomService>().Object,
            new Mock<ICurrencyService>().Object,
            new PlantDefaultLocationResolver(_context))
        {
            ControllerContext = GetControllerContextWithServices(userContext: _user.Object),
        };
    }

    private void SeedSpec(bool requiresFqc = true, string? productCode = Product,
                          bool isActive = true, string? plantId = Plant)
    {
        _context.InspectionSpecs.Add(new InspectionSpec
        {
            SpecId = Guid.NewGuid().ToString("N"),
            SpecCode = "SPEC-FQC", SpecName = "完工檢驗規格",
            PlantId = plantId, ProductCode = productCode,
            RequiresFQC = requiresFqc, IsActive = isActive,
        });
        _context.SaveChanges();
    }

    private void SeedInspection(string inspectionType, string status, string sourceDocId = WoId)
    {
        _context.InspectionOrders.Add(new InspectionOrder
        {
            InspectionOrderId = Guid.NewGuid().ToString("N"),
            PlantId = Plant, InspectionType = inspectionType,
            SourceDocId = sourceDocId, ProductCode = Product,
            LotSize = 10m, Status = status,
        });
        _context.SaveChanges();
    }

    private async Task<JsonResult> CompleteAsync()
        => (JsonResult)await BuildController().Complete(WoId, Wh, Loc, lotNo: null);

    private static bool IsSuccess(JsonResult json)
    {
        var data = json.Value!;
        return (bool)data.GetType().GetProperty("success")!.GetValue(data)!;
    }

    /// <summary>
    /// 被擋下來時**不得有任何入庫動作**。只斷言回傳 success=false 是不夠的：
    /// 若順序寫反（先繳庫再檢查），畫面上照樣看到一則錯誤訊息，而貨已經進倉了。
    /// </summary>
    private void AssertNotStocked()
        => _completion.Verify(c => c.CompleteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

    private void AssertStocked()
        => _completion.Verify(c => c.CompleteAsync(
            WoId, Plant, Wh, Loc, null, It.IsAny<CancellationToken>()), Times.Once);

    // ── 閘門的兩個方向 ──────────────────────────────────────────────────

    [Fact]
    public async Task Complete_RequiresFqc_WithoutAnyInspection_IsBlocked()
    {
        SeedSpec();

        Assert.False(IsSuccess(await CompleteAsync()));
        AssertNotStocked();
    }

    [Fact]
    public async Task Complete_RequiresFqc_WithPassedFqc_Proceeds()
    {
        SeedSpec();
        SeedInspection("FQC", "Pass");

        Assert.True(IsSuccess(await CompleteAsync()));
        AssertStocked();
    }

    /// <summary>
    /// 反面守門：沒有任何 RequiresFQC 規格的品號不該被擋。
    /// 少了這一支，把閘門寫成「一律需要 FQC」也會全綠，
    /// 而那個錯誤的症狀是所有工單都入不了庫。
    /// </summary>
    [Fact]
    public async Task Complete_NoFqcSpec_Proceeds()
    {
        Assert.True(IsSuccess(await CompleteAsync()));
        AssertStocked();
    }

    // ── 四個判定條件的鑑別 ──────────────────────────────────────────────

    /// <summary>
    /// 🚨 本檔最重要的一支。FPI（首件）與 FQC（完工）共用 <c>InspectionOrders</c>，
    /// 來源單都是工單號；有做首件的工單在完工時**必然**already 有一張 Pass。
    /// 漏掉型別條件 → 閘門對所有做過首件的工單自動放行，而那正是絕大多數工單。
    /// </summary>
    [Theory]
    [InlineData("FPI")]
    [InlineData("IPQC")]
    [InlineData("IQC")]
    public async Task Complete_RequiresFqc_PassOfAnotherInspectionType_DoesNotOpenTheGate(string otherType)
    {
        SeedSpec();
        SeedInspection(otherType, "Pass");

        Assert.False(IsSuccess(await CompleteAsync()));
        AssertNotStocked();
    }

    /// <summary>
    /// 檢驗單必須對到**這一張**工單。漏掉來源單條件的症狀是
    /// 「全廠只要有過一張 FQC Pass，之後每一張工單都免檢」。
    /// </summary>
    [Fact]
    public async Task Complete_RequiresFqc_PassedFqcOnAnotherWorkOrder_DoesNotOpenTheGate()
    {
        SeedSpec();
        SeedInspection("FQC", "Pass", sourceDocId: "WO2026OTHER");

        Assert.False(IsSuccess(await CompleteAsync()));
        AssertNotStocked();
    }

    /// <summary>
    /// 只有 Pass 算數。Pending 是「單建了還沒驗」——把它當通過，
    /// 等於用「有沒有人排檢驗」取代「驗過沒有」。
    /// </summary>
    [Theory]
    [InlineData("Pending")]
    [InlineData("Fail")]
    public async Task Complete_RequiresFqc_FqcNotPassed_IsBlocked(string status)
    {
        SeedSpec();
        SeedInspection("FQC", status);

        Assert.False(IsSuccess(await CompleteAsync()));
        AssertNotStocked();
    }

    /// <summary>
    /// 品號專屬規格查不到時退回「全品號通用」規格（<c>ProductCode == null</c>）。
    /// 這條 fallback 是閘門的實際涵蓋面——多數廠只會建一條通用規格，
    /// 它若失效，閘門對所有沒有專屬規格的品號靜默關閉。
    /// </summary>
    [Fact]
    public async Task Complete_NoProductSpecificSpec_FallsBackToTheAllProductSpec()
    {
        SeedSpec(productCode: null);

        Assert.False(IsSuccess(await CompleteAsync()));
        AssertNotStocked();
    }

    /// <summary>
    /// 停用的規格與別廠的規格都不該生效。兩者都是「規格存在但不適用」，
    /// 寫鬆的方向是把別廠的規格拿來擋本廠的工單。
    /// </summary>
    [Fact]
    public async Task Complete_InactiveSpec_DoesNotTriggerTheGate()
    {
        SeedSpec(isActive: false);

        Assert.True(IsSuccess(await CompleteAsync()));
        AssertStocked();
    }

    [Fact]
    public async Task Complete_SpecOfAnotherPlant_DoesNotTriggerTheGate()
    {
        SeedSpec(plantId: OtherPlant);

        Assert.True(IsSuccess(await CompleteAsync()));
        AssertStocked();
    }
}
