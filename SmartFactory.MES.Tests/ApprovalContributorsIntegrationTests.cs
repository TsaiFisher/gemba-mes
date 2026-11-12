using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.ORD.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Modules.QCR.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests;

/// <summary>
/// IApprovalSyncContributor v1.2 端對端整合測試。
/// 驗證各業務文件型別的 Contributor 都能透過 ApprovalService.ProcessApproval / WithdrawRequest 正確同步狀態。
/// 型別是否「全數涵蓋」由 ConventionTests.R15 掃描守門（本檔為手寫列舉，擋不住新單據漂移）。
/// </summary>
public class ApprovalContributorsIntegrationTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static async Task<(ApprovalService svc, string requestId)> SetupSingleStepRoleApprovalAsync(
        ApplicationDbContext db, string documentId, string documentType, params IApprovalSyncContributor[] contributors)
    {
        db.Roles.Add(new ApplicationRole { Id = "R-Approver", Name = "Approver" });
        db.Users.Add(new ApplicationUser { Id = "U-Submitter", UserName = "u", ActiveRoleId = "R-Approver" });
        var template = new ApprovalTemplate { Id = "T", Name = "Single" };
        template.Steps.Add(new ApprovalStep { Id = "S1", TemplateId = "T", Sequence = 1,
            ApproverType = ApproverType.Role, TargetRoleId = "R-Approver" });
        db.ApprovalTemplates.Add(template);
        await db.SaveChangesAsync();

        var svc = new ApprovalService(db, NullLogger<ApprovalService>.Instance, contributors);
        var req = await svc.SubmitForApproval(documentId, documentType, "T", "U-Submitter");
        return (svc, req.Id);
    }

    // ────────────────────────────────────────────────────────────────────
    // PUR 模組 — 4 個 Contributors
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PurchaseRequestApprovalSync_OnApproved_SyncsDoc()
    {
        using var db = NewDb();
        db.CurrentPlantId = "P1";
        db.PurchaseRequests.Add(new PurchaseRequest
        {
            PurchaseRequestId = "PR-001", PlantId = "P1", RequestDate = new DateOnly(2026, 5, 20),
            RequestedBy = "U-Submitter",
            ApprovalStatus = DocumentApprovalStatus.PendingApproval, ApprovalRequestId = "(set below)"
        });

        var contributor = new PurchaseRequestApprovalSync();
        var (svc, requestId) = await SetupSingleStepRoleApprovalAsync(db, "PR-001", "PurchaseRequest", contributor);

        // 將 PR 的 ApprovalRequestId 對應到實際建立的 request
        var pr = await db.PurchaseRequests.FindAsync("PR-001");
        pr!.ApprovalRequestId = requestId;
        await db.SaveChangesAsync();

        await svc.ProcessApproval(requestId, "U-Submitter", "R-Approver", ApprovalStatus.Approved, null);

        var refreshed = await db.PurchaseRequests.IgnoreQueryFilters().SingleAsync(x => x.PurchaseRequestId == "PR-001");
        Assert.Equal(DocumentApprovalStatus.Approved, refreshed.ApprovalStatus);
        Assert.NotNull(refreshed.ApprovalRequestId); // Approved 時保留 RequestId
    }

    [Fact]
    public async Task PurchaseOrderApprovalSync_OnWithdrawn_SyncsDocBackToDraft()
    {
        using var db = NewDb();
        db.CurrentPlantId = "P1";
        db.PurchaseOrders.Add(new PurchaseOrder
        {
            PurchaseOrderId = "PO-001", PlantId = "P1", OrderDate = new DateOnly(2026, 5, 20),
            CreatedBy = "U-Submitter", SupplierCode = "S001",
            ApprovalStatus = DocumentApprovalStatus.PendingApproval
        });

        var contributor = new PurchaseOrderApprovalSync();
        var (svc, requestId) = await SetupSingleStepRoleApprovalAsync(db, "PO-001", "PurchaseOrder", contributor);

        var po = await db.PurchaseOrders.FindAsync("PO-001");
        po!.ApprovalRequestId = requestId;
        await db.SaveChangesAsync();

        // 申請人撤回
        await svc.WithdrawRequest(requestId, "U-Submitter");

        var refreshed = await db.PurchaseOrders.IgnoreQueryFilters().SingleAsync(x => x.PurchaseOrderId == "PO-001");
        Assert.Equal(DocumentApprovalStatus.Draft, refreshed.ApprovalStatus);
        Assert.Null(refreshed.ApprovalRequestId); // Withdrawn 時清空 RequestId
    }

    [Fact]
    public async Task GoodsReceiptApprovalSync_OnRejected_SyncsDoc()
    {
        using var db = NewDb();
        db.CurrentPlantId = "P1";
        db.GoodsReceipts.Add(new GoodsReceipt
        {
            GoodsReceiptId = "GR-001", PlantId = "P1", ReceiptDate = new DateOnly(2026, 5, 20),
            CreatedBy = "U-Submitter", SupplierCode = "S001",
            ApprovalStatus = DocumentApprovalStatus.PendingApproval
        });

        var contributor = new GoodsReceiptApprovalSync();
        var (svc, requestId) = await SetupSingleStepRoleApprovalAsync(db, "GR-001", "GoodsReceipt", contributor);

        var gr = await db.GoodsReceipts.FindAsync("GR-001");
        gr!.ApprovalRequestId = requestId;
        await db.SaveChangesAsync();

        await svc.ProcessApproval(requestId, "U-Submitter", "R-Approver", ApprovalStatus.Rejected, "不通過");

        var refreshed = await db.GoodsReceipts.IgnoreQueryFilters().SingleAsync(x => x.GoodsReceiptId == "GR-001");
        Assert.Equal(DocumentApprovalStatus.Rejected, refreshed.ApprovalStatus);
    }

    [Fact]
    public async Task GoodsReturnApprovalSync_OnReturnedForRevision_SyncsBackToDraft()
    {
        using var db = NewDb();
        db.CurrentPlantId = "P1";
        db.GoodsReturns.Add(new GoodsReturn
        {
            GoodsReturnId = "GRT-001", PlantId = "P1", ReturnDate = new DateOnly(2026, 5, 20),
            CreatedBy = "U-Submitter", SupplierCode = "S001",
            ApprovalStatus = DocumentApprovalStatus.PendingApproval
        });

        var contributor = new GoodsReturnApprovalSync();
        var (svc, requestId) = await SetupSingleStepRoleApprovalAsync(db, "GRT-001", "GoodsReturn", contributor);

        var grt = await db.GoodsReturns.FindAsync("GRT-001");
        grt!.ApprovalRequestId = requestId;
        await db.SaveChangesAsync();

        await svc.ProcessApproval(requestId, "U-Submitter", "R-Approver", ApprovalStatus.ReturnedForRevision, "請補資料");

        var refreshed = await db.GoodsReturns.IgnoreQueryFilters().SingleAsync(x => x.GoodsReturnId == "GRT-001");
        Assert.Equal(DocumentApprovalStatus.Draft, refreshed.ApprovalStatus);
        Assert.Null(refreshed.ApprovalRequestId);
    }

    // ────────────────────────────────────────────────────────────────────
    // ORD 模組 — 4 個 Contributors
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SalesOrderApprovalSync_OnApproved_SyncsDoc()
    {
        using var db = NewDb();
        db.CurrentPlantId = "P1";
        db.SalesOrders.Add(new SalesOrder
        {
            SalesOrderId = "SO-001", PlantId = "P1", OrderDate = new DateOnly(2026, 5, 20),
            CompanyCode = "C001", CreatedBy = "U-Submitter",
            ApprovalStatus = DocumentApprovalStatus.PendingApproval
        });

        var (svc, requestId) = await SetupSingleStepRoleApprovalAsync(db, "SO-001", "SalesOrder", new SalesOrderApprovalSync());

        var so = await db.SalesOrders.FindAsync("SO-001");
        so!.ApprovalRequestId = requestId;
        await db.SaveChangesAsync();

        await svc.ProcessApproval(requestId, "U-Submitter", "R-Approver", ApprovalStatus.Approved, null);

        var refreshed = await db.SalesOrders.IgnoreQueryFilters().SingleAsync(x => x.SalesOrderId == "SO-001");
        Assert.Equal(DocumentApprovalStatus.Approved, refreshed.ApprovalStatus);
    }

    [Theory]
    [InlineData("SalesReturn")]
    [InlineData("GoodsShipment")]
    [InlineData("InternalOrder")]
    public async Task OrdContributors_CoverRemainingDocTypes(string docType)
    {
        // Pure-contributor smoke test：confirm each ORD contributor's DocumentType wires correctly.
        IApprovalSyncContributor contributor = docType switch
        {
            "SalesReturn"    => new SalesReturnApprovalSync(),
            "GoodsShipment"  => new GoodsShipmentApprovalSync(),
            "InternalOrder"  => new InternalOrderApprovalSync(),
            _ => throw new ArgumentException("unexpected type")
        };
        Assert.Equal(docType, contributor.DocumentType);
    }

    // ────────────────────────────────────────────────────────────────────
    // INV 模組 — 8 個 Contributors（用 Theory 涵蓋所有型別的 DocumentType 正確性）
    // ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("StockTransfer")]
    [InlineData("CrossPlantTransfer")]
    [InlineData("ScrapDoc")]
    [InlineData("LoanDoc")]
    [InlineData("SampleDoc")]
    // 型別字串必須等於「送審端實際傳入的 DocType」：ReworkMaterialController.DocType
    // 是 "ReworkMaterial"（非 "ReworkMaterialDoc"），OutsourceMaterial 同理。
    // 先前這裡寫的是實體類別名，等於把「contributor 永遠路由不到」的 bug 寫進斷言。
    [InlineData("ReworkMaterial")]
    [InlineData("OutsourceMaterial")]
    [InlineData("StockAdjustment")]
    [InlineData("MaterialStagingOrder")]
    [InlineData("LandedCostAllocation")]
    public async Task InvContributors_DocumentTypeMatches(string docType)
    {
        IApprovalSyncContributor contributor = docType switch
        {
            "StockTransfer"        => new StockTransferApprovalSync(),
            "CrossPlantTransfer"   => new CrossPlantTransferApprovalSync(),
            "ScrapDoc"             => new ScrapDocApprovalSync(),
            "LoanDoc"              => new LoanDocApprovalSync(),
            "SampleDoc"            => new SampleDocApprovalSync(),
            "ReworkMaterial"       => new ReworkMaterialDocApprovalSync(),
            "OutsourceMaterial"    => new OutsourceMaterialDocApprovalSync(),
            "StockAdjustment"      => new StockAdjustmentApprovalSync(),
            "MaterialStagingOrder" => new MaterialStagingOrderApprovalSync(),
            "LandedCostAllocation" => new LandedCostAllocationApprovalSync(),
            _ => throw new ArgumentException("unexpected type")
        };
        Assert.Equal(docType, contributor.DocumentType);
    }

    // 完整端對端：選一個 INV 文件型別驗證實際資料庫狀態回寫
    [Fact]
    public async Task StockTransferApprovalSync_FullFlow_OnApproved_SyncsDoc()
    {
        using var db = NewDb();
        db.CurrentPlantId = "P1";
        db.StockTransfers.Add(new StockTransfer
        {
            StockTransferId = "ST-001", PlantId = "P1", TransferDate = new DateOnly(2026, 5, 20),
            CreatedBy = "U-Submitter",
            ApprovalStatus = DocumentApprovalStatus.PendingApproval
        });

        var (svc, requestId) = await SetupSingleStepRoleApprovalAsync(db, "ST-001", "StockTransfer", new StockTransferApprovalSync());

        var doc = await db.StockTransfers.FindAsync("ST-001");
        doc!.ApprovalRequestId = requestId;
        await db.SaveChangesAsync();

        await svc.ProcessApproval(requestId, "U-Submitter", "R-Approver", ApprovalStatus.Approved, null);

        var refreshed = await db.StockTransfers.IgnoreQueryFilters().SingleAsync(x => x.StockTransferId == "ST-001");
        Assert.Equal(DocumentApprovalStatus.Approved, refreshed.ApprovalStatus);
    }

    // ────────────────────────────────────────────────────────────────────
    // R15 回補的型別 — 這些先前核准後完全不回寫，症狀從「主檔永不生效」到「流程死鎖」
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 委外收貨最嚴重：Confirm 要求 ApprovalStatus == Approved，
    /// 先前全模組沒有任何地方寫得回 Approved → 設了簽核流程即整條流程死鎖。
    /// </summary>
    [Fact]
    public async Task OutsourceReceiptApprovalSync_OnApproved_UnblocksConfirm()
    {
        using var db = NewDb();
        db.CurrentPlantId = "P1";
        db.OutsourceReceipts.Add(new OutsourceReceipt
        {
            OutsourceReceiptId = "OR-001", PlantId = "P1",
            OutsourcePurchaseOrderId = "OPO-001", SupplierCode = "S001",
            ReceiptDate = new DateOnly(2026, 7, 20), CreatedBy = "U-Submitter",
            ApprovalStatus = DocumentApprovalStatus.PendingApproval
        });

        var (svc, requestId) = await SetupSingleStepRoleApprovalAsync(
            db, "OR-001", "OutsourceReceipt", new OutsourceReceiptApprovalSync());

        var doc = await db.OutsourceReceipts.FindAsync("OR-001");
        doc!.ApprovalRequestId = requestId;
        await db.SaveChangesAsync();

        await svc.ProcessApproval(requestId, "U-Submitter", "R-Approver", ApprovalStatus.Approved, null);

        var refreshed = await db.OutsourceReceipts.IgnoreQueryFilters()
            .SingleAsync(x => x.OutsourceReceiptId == "OR-001");
        Assert.Equal(DocumentApprovalStatus.Approved, refreshed.ApprovalStatus);
    }

    /// <summary>NCR 有簽核與處置兩套狀態，核准必須兩者同步，否則 UseAsIs 的執行閘門永不放行。</summary>
    [Fact]
    public async Task NcrApprovalSync_OnApproved_SyncsBothApprovalStatusAndNcrStatus()
    {
        using var db = NewDb();
        db.CurrentPlantId = "P1";
        db.NonConformanceReports.Add(new NonConformanceReport
        {
            NcrId = "NCR-001", PlantId = "P1", ProductCode = "P-A", Qty = 5m,
            Disposition = "UseAsIs", NcrStatus = NcrStatus.PendingApproval,
            ApprovalStatus = DocumentApprovalStatus.PendingApproval
        });

        var (svc, requestId) = await SetupSingleStepRoleApprovalAsync(
            db, "NCR-001", "NonConformanceReport", new NcrApprovalSync());

        var ncr = await db.NonConformanceReports.FindAsync("NCR-001");
        ncr!.ApprovalRequestId = requestId;
        await db.SaveChangesAsync();

        await svc.ProcessApproval(requestId, "U-Submitter", "R-Approver", ApprovalStatus.Approved, null);

        var refreshed = await db.NonConformanceReports.IgnoreQueryFilters()
            .SingleAsync(x => x.NcrId == "NCR-001");
        Assert.Equal(DocumentApprovalStatus.Approved, refreshed.ApprovalStatus);
        Assert.Equal(NcrStatus.Approved, refreshed.NcrStatus);   // Execute 的前置條件
        Assert.NotNull(refreshed.ApprovedAt);
    }

    /// <summary>NCR 駁回須退回 Open，否則既不能執行也不能重送（Submit 要求 NcrStatus == Open）。</summary>
    [Fact]
    public async Task NcrApprovalSync_OnRejected_ReturnsToOpenSoItCanBeResubmitted()
    {
        using var db = NewDb();
        db.CurrentPlantId = "P1";
        db.NonConformanceReports.Add(new NonConformanceReport
        {
            NcrId = "NCR-002", PlantId = "P1", ProductCode = "P-A", Qty = 5m,
            Disposition = "UseAsIs", NcrStatus = NcrStatus.PendingApproval,
            ApprovalStatus = DocumentApprovalStatus.PendingApproval
        });

        var (svc, requestId) = await SetupSingleStepRoleApprovalAsync(
            db, "NCR-002", "NonConformanceReport", new NcrApprovalSync());

        var ncr = await db.NonConformanceReports.FindAsync("NCR-002");
        ncr!.ApprovalRequestId = requestId;
        await db.SaveChangesAsync();

        await svc.ProcessApproval(requestId, "U-Submitter", "R-Approver", ApprovalStatus.Rejected, "不同意讓步");

        var refreshed = await db.NonConformanceReports.IgnoreQueryFilters()
            .SingleAsync(x => x.NcrId == "NCR-002");
        Assert.Equal(DocumentApprovalStatus.Rejected, refreshed.ApprovalStatus);
        Assert.Equal(NcrStatus.Open, refreshed.NcrStatus);
    }

    /// <summary>
    /// BAS 主檔送審時被寫成 IsActive=false（「待核准後生效」），
    /// 核准必須恢復啟用，否則核准完的產品仍然選不到。
    /// </summary>
    [Fact]
    public async Task ProductApprovalSync_OnApproved_ReactivatesMaster()
    {
        using var db = NewDb();
        db.Products.Add(new SmartFactory.MES.Modules.BAS.Models.Product
        {
            ProductCode = "P-NEW", ProductName = "新品",
            IsActive = false,                                  // 送審時 Controller 寫入
            ApprovalStatus = DocumentApprovalStatus.PendingApproval
        });

        var (svc, requestId) = await SetupSingleStepRoleApprovalAsync(
            db, "P-NEW", "Product", new ProductApprovalSync());

        var p = await db.Products.FindAsync("P-NEW");
        p!.ApprovalRequestId = requestId;
        await db.SaveChangesAsync();

        await svc.ProcessApproval(requestId, "U-Submitter", "R-Approver", ApprovalStatus.Approved, null);

        var refreshed = await db.Products.IgnoreQueryFilters().SingleAsync(x => x.ProductCode == "P-NEW");
        Assert.Equal(DocumentApprovalStatus.Approved, refreshed.ApprovalStatus);
        Assert.True(refreshed.IsActive, "核准後主檔必須恢復啟用，否則核准形同無效");
    }

    /// <summary>駁回不得誤啟用主檔。</summary>
    [Fact]
    public async Task ProductApprovalSync_OnRejected_StaysInactive()
    {
        using var db = NewDb();
        db.Products.Add(new SmartFactory.MES.Modules.BAS.Models.Product
        {
            ProductCode = "P-REJ", ProductName = "退件品", IsActive = false,
            ApprovalStatus = DocumentApprovalStatus.PendingApproval
        });

        var (svc, requestId) = await SetupSingleStepRoleApprovalAsync(
            db, "P-REJ", "Product", new ProductApprovalSync());

        var p = await db.Products.FindAsync("P-REJ");
        p!.ApprovalRequestId = requestId;
        await db.SaveChangesAsync();

        await svc.ProcessApproval(requestId, "U-Submitter", "R-Approver", ApprovalStatus.Rejected, "資料不全");

        var refreshed = await db.Products.IgnoreQueryFilters().SingleAsync(x => x.ProductCode == "P-REJ");
        Assert.Equal(DocumentApprovalStatus.Rejected, refreshed.ApprovalStatus);
        Assert.False(refreshed.IsActive);
    }

    [Theory]
    [InlineData("Company")]
    [InlineData("Tax")]
    [InlineData("OutsourceReceipt")]
    [InlineData("NonConformanceReport")]
    public void R15BackfilledContributors_DocumentTypeMatches(string docType)
    {
        IApprovalSyncContributor contributor = docType switch
        {
            "Company"              => new SmartFactory.MES.Modules.BAS.Services.CompanyApprovalSync(),
            "Tax"                  => new SmartFactory.MES.Modules.BAS.Services.TaxApprovalSync(),
            "OutsourceReceipt"     => new OutsourceReceiptApprovalSync(),
            "NonConformanceReport" => new SmartFactory.MES.Modules.QCR.Services.NcrApprovalSync(),
            _ => throw new ArgumentException("unexpected type")
        };
        Assert.Equal(docType, contributor.DocumentType);
    }

    // ────────────────────────────────────────────────────────────────────
    // 多 Contributor 同時註冊時的路由正確性
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MultipleContributors_RoutesByDocumentType()
    {
        using var db = NewDb();
        db.CurrentPlantId = "P1";
        db.PurchaseOrders.Add(new PurchaseOrder
        {
            PurchaseOrderId = "PO-MIX", PlantId = "P1", OrderDate = new DateOnly(2026, 5, 20),
            CreatedBy = "U-Submitter", SupplierCode = "S001",
            ApprovalStatus = DocumentApprovalStatus.PendingApproval
        });
        db.SalesOrders.Add(new SalesOrder
        {
            SalesOrderId = "SO-MIX", PlantId = "P1", OrderDate = new DateOnly(2026, 5, 20),
            CompanyCode = "C001", CreatedBy = "U-Submitter",
            ApprovalStatus = DocumentApprovalStatus.PendingApproval
        });

        // 註冊多個 Contributor —— Service 應該按 DocumentType 正確路由
        var contributors = new IApprovalSyncContributor[]
        {
            new PurchaseOrderApprovalSync(),
            new SalesOrderApprovalSync(),
            new StockTransferApprovalSync(),
        };

        var (svc, poRequestId) = await SetupSingleStepRoleApprovalAsync(db, "PO-MIX", "PurchaseOrder", contributors);
        var po = await db.PurchaseOrders.FindAsync("PO-MIX");
        po!.ApprovalRequestId = poRequestId;
        await db.SaveChangesAsync();
        await svc.ProcessApproval(poRequestId, "U-Submitter", "R-Approver", ApprovalStatus.Approved, null);

        var refreshedPo = await db.PurchaseOrders.IgnoreQueryFilters().SingleAsync(x => x.PurchaseOrderId == "PO-MIX");
        var refreshedSo = await db.SalesOrders.IgnoreQueryFilters().SingleAsync(x => x.SalesOrderId == "SO-MIX");

        // 只有 PO 被同步，SO 不受影響
        Assert.Equal(DocumentApprovalStatus.Approved, refreshedPo.ApprovalStatus);
        Assert.Equal(DocumentApprovalStatus.PendingApproval, refreshedSo.ApprovalStatus);
    }
}
