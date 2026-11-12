using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Api.Mobile;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.QCR.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Mobile;

/// <summary>
/// MobileInvController.SubmitIqc 品質隔離放行測試：Pass 時把待驗倉庫存轉出至一般倉；
/// 若「同廠調撥」單據類型設有簽核流程，只建立 Draft 待簽，不越權自動過帳。
/// </summary>
public class MobileInvIqcControllerTests : TestBase
{
    private const string PlantId = "P001";
    private const string GoodsReceiptId = "PI2026070001";

    private readonly Mock<IUserContext> _mockUser = new();
    private readonly Mock<ISamplingPlanService> _mockSampling = new();
    private readonly Mock<IMobilePermissionService> _mockMobilePerm = new();
    private readonly Mock<IDocNumberService> _mockDocNo = new();
    private readonly Mock<IApprovalService> _mockApproval = new();
    private readonly InventoryPostingService _postingSvc;

    public MobileInvIqcControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns("user1");
        _mockUser.Setup(u => u.ActiveRoleId).Returns("role1");
        _mockMobilePerm.Setup(m => m.CanExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("WT2026070001");

        var invSvc = new InventoryService(_context);
        var mockDt = new Mock<IDateTimeService>();
        mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(new DateOnly(2026, 7, 1));
        _postingSvc = new InventoryPostingService(_context, invSvc, new Mock<IInventoryCostLayerService>().Object,
            new Mock<ISystemParamService>().Object, mockDt.Object);

        SeedGoodsReceiptWithQuarantineStock();
    }

    /// <summary>模擬「收貨已上架到待驗倉」的前置狀態：GR 明細記錄目的一般倉 WH01/A-01，
    /// 但實際上架明細（GoodsReceiptLocationDetail）與 Inventory 都落在待驗倉 QC01/Q-01。</summary>
    private void SeedGoodsReceiptWithQuarantineStock()
    {
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "原料倉", PlantId = PlantId, WarehouseType = WarehouseType.Raw });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "A-01", LocationName = "A-01 位" });
        _context.Warehouses.Add(new Warehouse { WhCode = "QC01", WhName = "待驗倉", PlantId = PlantId, WarehouseType = WarehouseType.Quarantine });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "QC01", LocationCode = "Q-01", LocationName = "Q-01 位" });

        var gr = new GoodsReceipt
        {
            GoodsReceiptId = GoodsReceiptId, ReceiptDate = new DateOnly(2026, 7, 1), SupplierCode = "SUP001",
            Status = DocStatus.Confirmed, ApprovalStatus = DocumentApprovalStatus.Approved,
            PlantId = PlantId, TypeCode = "PI", TypeCategory = "Receipt",
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = GoodsReceiptId, LineNo = 1, ProductCode = "MAT001",
            ReceiptQty = 40, UomCode = "PC", StockQty = 40, StockUomCode = "PC",
            UnitPrice = 10, WhCode = "WH01", LocationCode = "A-01", QtyPutAway = 40,
        });
        _context.GoodsReceipts.Add(gr);

        _context.Set<GoodsReceiptLocationDetail>().Add(new GoodsReceiptLocationDetail
        {
            GoodsReceiptId = GoodsReceiptId, LineNo = 1, LocationCode = "Q-01", WhCode = "QC01", LotNo = "", Qty = 40,
        });
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001", LocationCode = "Q-01", LotNo = "", QtyOnHand = 40, UnitCost = 10,
        });
        _context.SaveChanges();
    }

    /// <summary>IQC 判定/放行邏輯已抽至 IqcInspectionService（桌機與手機共用），此處注入真實服務保留端到端覆蓋。</summary>
    private MobileInvController BuildController(
        SmartFactory.MES.Modules.FIN.Services.IAPInvoiceService? apInvoice = null)
    {
        var iqc = new IqcInspectionService(_context, _mockUser.Object, _mockSampling.Object,
            _mockDocNo.Object, _postingSvc, _mockApproval.Object,
            Timezone.TimezoneTestKit.FixedLocalDate(new DateOnly(2026, 7, 1)).Object, apInvoice);
        return new(_context, _mockUser.Object, iqc, _mockMobilePerm.Object, _mockDocNo.Object);
    }

    [Fact]
    public async Task SubmitIqc_Pass_AccruesApForReleasedQty()
    {
        // 放行進正常倉＝AP 認列點：就這批放行量呼叫 AccrueForReceivedQtyAsync。
        // 守住「放行→認列」這條連線，避免日後有人拿掉認列呼叫而測試照樣綠。
        _mockApproval.Setup(a => a.GetApprovalConfig("StockTransfer", PlantId)).ReturnsAsync((DocumentApprovalConfig?)null);
        var mockApInvoice = new Mock<SmartFactory.MES.Modules.FIN.Services.IAPInvoiceService>();
        mockApInvoice.Setup(a => a.AccrueForReceivedQtyAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartFactory.MES.Modules.FIN.Models.APInvoice());
        var ctrl = BuildController(mockApInvoice.Object);

        await ctrl.SubmitIqc(new IqcSubmitRequest(
            GoodsReceiptId: GoodsReceiptId, LineNo: 1, ProductCode: "MAT001",
            LotSize: 40, SampleQty: 5, FailQty: 0, VerificationLevel: 4, Remarks: null));

        mockApInvoice.Verify(a => a.AccrueForReceivedQtyAsync(GoodsReceiptId, 1, 40m, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubmitIqc_Pass_NonUnitUom_AccruesInReceiptUnits()
    {
        // 非 1:1 UOM：進貨 4 箱＝庫存 40 pcs。放行 40 pcs 進正常倉 → AP 認列基礎為進貨單位，
        // 應按 ReceiptQty/StockQty 比例換算 = 40 × 4/40 = 4（箱），而非直接用庫存量 40。
        var line = _context.GoodsReceiptLines.First(l => l.GoodsReceiptId == GoodsReceiptId && l.LineNo == 1);
        line.ReceiptQty = 4; line.UomCode = "BOX";   // StockQty 維持 40 pcs
        _context.SaveChanges();

        _mockApproval.Setup(a => a.GetApprovalConfig("StockTransfer", PlantId)).ReturnsAsync((DocumentApprovalConfig?)null);
        var mockApInvoice = new Mock<SmartFactory.MES.Modules.FIN.Services.IAPInvoiceService>();
        mockApInvoice.Setup(a => a.AccrueForReceivedQtyAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartFactory.MES.Modules.FIN.Models.APInvoice());
        var ctrl = BuildController(mockApInvoice.Object);

        await ctrl.SubmitIqc(new IqcSubmitRequest(
            GoodsReceiptId: GoodsReceiptId, LineNo: 1, ProductCode: "MAT001",
            LotSize: 40, SampleQty: 5, FailQty: 0, VerificationLevel: 4, Remarks: null));

        mockApInvoice.Verify(a => a.AccrueForReceivedQtyAsync(GoodsReceiptId, 1, 4m, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubmitIqc_Fail_DoesNotAccrueAp()
    {
        // 判退不放行、貨留待驗倉 → 不得認列應付。
        var mockApInvoice = new Mock<SmartFactory.MES.Modules.FIN.Services.IAPInvoiceService>();
        var ctrl = BuildController(mockApInvoice.Object);

        await ctrl.SubmitIqc(new IqcSubmitRequest(
            GoodsReceiptId: GoodsReceiptId, LineNo: 1, ProductCode: "MAT001",
            LotSize: 40, SampleQty: 5, FailQty: 1, VerificationLevel: 4, Remarks: null));

        mockApInvoice.Verify(a => a.AccrueForReceivedQtyAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SubmitIqc_Pass_NoApprovalConfigured_ReleasesQuarantineStockToNormalWarehouse()
    {
        _mockApproval.Setup(a => a.GetApprovalConfig("StockTransfer", PlantId)).ReturnsAsync((DocumentApprovalConfig?)null);
        var ctrl = BuildController();

        var result = await ctrl.SubmitIqc(new IqcSubmitRequest(
            GoodsReceiptId: GoodsReceiptId, LineNo: 1, ProductCode: "MAT001",
            LotSize: 40, SampleQty: 5, FailQty: 0, VerificationLevel: 4, Remarks: null));

        Assert.IsType<OkObjectResult>(result);

        var quarantineInv = _context.Inventories.FirstOrDefault(i => i.ProductCode == "MAT001" && i.LocationCode == "Q-01");
        Assert.Equal(0m, quarantineInv?.QtyOnHand ?? 0m);

        var normalInv = _context.Inventories.FirstOrDefault(i => i.ProductCode == "MAT001" && i.LocationCode == "A-01");
        Assert.NotNull(normalInv);
        Assert.Equal(40m, normalInv!.QtyOnHand);

        var transfer = _context.StockTransfers.FirstOrDefault();
        Assert.NotNull(transfer);
        Assert.Equal(DocStatus.Posted, transfer!.Status);
        Assert.Equal(DocumentApprovalStatus.Approved, transfer.ApprovalStatus);
    }

    [Fact]
    public async Task SubmitIqc_Pass_ApprovalConfigured_StillAutoPostsSystemRelease()
    {
        // 認列重構後：IQC 合格放行的調撥單是「系統把待驗庫存搬進正常倉」的系統單，
        // 一律免簽核、直接過帳——即使「同廠調撥」類型設有簽核流程亦不套用。
        _mockApproval.Setup(a => a.GetApprovalConfig("StockTransfer", PlantId))
            .ReturnsAsync(new DocumentApprovalConfig { DocumentType = "StockTransfer", PlantId = PlantId, ApprovalTemplateId = "TPL1" });
        var ctrl = BuildController();

        var result = await ctrl.SubmitIqc(new IqcSubmitRequest(
            GoodsReceiptId: GoodsReceiptId, LineNo: 1, ProductCode: "MAT001",
            LotSize: 40, SampleQty: 5, FailQty: 0, VerificationLevel: 4, Remarks: null));

        Assert.IsType<OkObjectResult>(result);

        // 待驗倉庫存已放行搬出（直接過帳，不因簽核設定而停在 Draft）
        var quarantineInv = _context.Inventories.FirstOrDefault(i => i.ProductCode == "MAT001" && i.LocationCode == "Q-01");
        Assert.Equal(0m, quarantineInv?.QtyOnHand ?? 0m);

        var transfer = _context.StockTransfers.FirstOrDefault();
        Assert.NotNull(transfer);
        Assert.Equal(DocStatus.Posted, transfer!.Status);
        Assert.Equal(DocumentApprovalStatus.Approved, transfer.ApprovalStatus);
    }

    [Fact]
    public async Task SubmitIqc_Fail_DoesNotReleaseQuarantineStock()
    {
        var ctrl = BuildController();

        var result = await ctrl.SubmitIqc(new IqcSubmitRequest(
            GoodsReceiptId: GoodsReceiptId, LineNo: 1, ProductCode: "MAT001",
            LotSize: 40, SampleQty: 5, FailQty: 1, VerificationLevel: 4, Remarks: null));

        Assert.IsType<OkObjectResult>(result);

        var quarantineInv = _context.Inventories.FirstOrDefault(i => i.ProductCode == "MAT001" && i.LocationCode == "Q-01");
        Assert.Equal(40m, quarantineInv?.QtyOnHand ?? 0m);
        Assert.Empty(_context.StockTransfers);
    }
}
