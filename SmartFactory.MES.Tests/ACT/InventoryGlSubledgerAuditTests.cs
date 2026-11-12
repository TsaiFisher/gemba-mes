using SmartFactory.MES.Tests.Shared;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// 總帳存貨科目 ↔ 庫存明細帳（INV_GL_*，2026-10-22）。
///
/// <para>每個情境都對應穩定版 ADM 廠拆帳時實際看到的一種來源：漏開傳票的單據、
/// 作廢沖銷的負數 TotalCost、工單在製（發料不入帳、完工只入人工製費）、退料超過實際發料、
/// 客供料依設計不入帳，以及人工傳票更正後殘差歸零。</para>
/// </summary>
public class InventoryGlSubledgerAuditTests : TestBase
{
    private const string Plant = "P001";
    private const string Loc = "L1";
    private readonly DataAuditService _svc;
    private int _jeSeq;

    public InventoryGlSubledgerAuditTests() => _svc = new DataAuditService(_context);

    [Fact]
    public async Task 進貨有傳票_總帳等於明細帳_無異常()
    {
        Move("PI-1", "PI", "Purchase", "MAT-A", +1, 10, 10m);
        Journal("INV", "PI-1", 100m);
        OnHand("MAT-A", 10, 10m);
        await _context.SaveChangesAsync();

        Assert.Empty(await _svc.AuditInventoryGlSubledgerAsync(Plant));
    }

    [Fact]
    public async Task 異動漏開傳票_報殘差並列出單據_以人工傳票更正後解除()
    {
        Move("PI-1", "PI", "Purchase", "MAT-A", +1, 10, 10m);   // 有傳票
        Journal("INV", "PI-1", 100m);
        Move("IA-9", "IA", "Adjustment", "MAT-A", +1, 3, 10m);  // 漏開
        OnHand("MAT-A", 13, 10m);
        await _context.SaveChangesAsync();

        var failures = await _svc.AuditInventoryGlSubledgerAsync(Plant);

        var residual = Assert.Single(failures, f => f.AuditType == "INV_GL_SUBLEDGER");
        Assert.Equal(-30m, residual.DifferenceAmount);
        Assert.True(residual.IsBlocking);
        var doc = Assert.Single(failures, f => f.AuditType == "INV_GL_DOC_DIFF");
        Assert.Equal("IA-9", doc.DocId);
        Assert.Equal(-30m, doc.DifferenceAmount);
        Assert.False(doc.IsBlocking);   // 指路用，擋關帳的是殘差那一筆

        // 人工更正：借存貨 30（來源不必是原單——殘差歸零即解除，明細也跟著消失）
        Journal("Manual", null, 30m);
        await _context.SaveChangesAsync();

        Assert.Empty(await _svc.AuditInventoryGlSubledgerAsync(Plant));
    }

    [Fact]
    public async Task 作廢沖銷列TotalCost為負數_不會被算成同向()
    {
        // 作廢沖銷：方向反轉、TotalCost 又寫成負數。用「方向 × TotalCost」加總會得到 +4000
        Move("GR-1", "GR", "Sales", "MAT-A", +1, 1, 2000m);
        Move("GR-1", "GR", "Sales", "MAT-A", -1, 1, 2000m, totalCost: -2000m);
        Journal("INV", "GR-1", 2000m);
        Journal("INV-Void", "GR-1", -2000m);
        await _context.SaveChangesAsync();

        Assert.Empty(await _svc.AuditInventoryGlSubledgerAsync(Plant));
    }

    [Fact]
    public async Task 工單結案_發料加人工製費等於繳庫_在製歸零()
    {
        Move("PI-1", "PI", "Purchase", "MAT-A", +1, 10, 30m);
        Journal("INV", "PI-1", 300m);
        Wo("WO-1", WorkOrderStatus.Closed);
        Slip("MI-1", MaterialSlipCategory.InternalIssue, "WO-1");
        Move("MI-1", "IO", "Production", "MAT-A", -1, 10, 30m);   // 生產發料不入帳
        Move("WO-1", "IT", "Production", "FG-A", +1, 5, 70m);     // 繳庫 350 ＝ 料 300 ＋ 人工製費 50
        Journal("WorkOrderCompletion", "WO-1", 50m);
        OnHand("FG-A", 5, 70m);
        await _context.SaveChangesAsync();

        Assert.Empty(await _svc.AuditInventoryGlSubledgerAsync(Plant));
    }

    [Fact]
    public async Task 工單未完工_料在總帳裡是在製_不算殘差()
    {
        Move("PI-1", "PI", "Purchase", "MAT-A", +1, 10, 30m);
        Journal("INV", "PI-1", 300m);
        Wo("WO-1", WorkOrderStatus.InProgress);
        Slip("MI-1", MaterialSlipCategory.InternalIssue, "WO-1");
        Move("MI-1", "IO", "Production", "MAT-A", -1, 4, 30m);
        OnHand("MAT-A", 6, 30m);
        await _context.SaveChangesAsync();

        Assert.Empty(await _svc.AuditInventoryGlSubledgerAsync(Plant));
        var bridge = Assert.Single(await new InventoryGlReconciler(_context).ReconcileAsync(Plant, 0.01m, null));
        Assert.Equal(120m, bridge.WipInGl);
        Assert.Equal(0m, bridge.Residual);
    }

    [Fact]
    public async Task 工單結案仍留在製_報INV_GL_WIP_CLOSED()
    {
        Move("PI-1", "PI", "Purchase", "MAT-A", +1, 10, 30m);
        Journal("INV", "PI-1", 300m);
        Wo("WO-1", WorkOrderStatus.Closed);
        Slip("MI-1", MaterialSlipCategory.InternalIssue, "WO-1");
        Move("MI-1", "IO", "Production", "MAT-A", -1, 10, 30m);
        Move("WO-1", "IT", "Production", "FG-A", +1, 5, 60m);     // 繳庫只算了 300，人工製費 50 沒進成品
        Journal("WorkOrderCompletion", "WO-1", 50m);
        OnHand("FG-A", 5, 60m);
        await _context.SaveChangesAsync();

        var f = Assert.Single(await _svc.AuditInventoryGlSubledgerAsync(Plant));
        Assert.Equal("INV_GL_WIP_CLOSED", f.AuditType);
        Assert.Equal("WO-1", f.DocId);
        Assert.Equal(50m, f.DifferenceAmount);
    }

    [Fact]
    public async Task 退料超過實際發料_在製為負_報INV_GL_WIP_NEGATIVE()
    {
        // ADM PB-2609-00019_01 的形狀：料走委外發料出去、已併進委外收回品，又被廠內退料單退回倉庫
        Wo("WO-1", WorkOrderStatus.InProgress);
        Slip("MX-1", MaterialSlipCategory.InternalReturn, "WO-1");
        Move("MX-1", "IR", "Production", "MAT-A", +1, 9, 40m);
        OnHand("MAT-A", 9, 40m);
        await _context.SaveChangesAsync();

        var f = Assert.Single(await _svc.AuditInventoryGlSubledgerAsync(Plant));
        Assert.Equal("INV_GL_WIP_NEGATIVE", f.AuditType);
        Assert.Equal(-360m, f.DifferenceAmount);
    }

    [Fact]
    public async Task 客供料不入總帳_屬調整項_不報殘差()
    {
        Move("SM-1", "CI", "Sample", "CUST-A", +1, 2, 15m);
        OnHand("CUST-A", 2, 15m);
        await _context.SaveChangesAsync();

        Assert.Empty(await _svc.AuditInventoryGlSubledgerAsync(Plant));
        var bridge = Assert.Single(await new InventoryGlReconciler(_context).ReconcileAsync(Plant, 0.01m, null));
        Assert.Equal(-30m, bridge.CustomerOwned);
    }

    [Fact]
    public async Task FIFO品以成本層計價_不看庫存列單價()
    {
        _context.ProductCategories.Add(new ProductCategory { CategoryCode = "C-FIFO", CategoryName = "FIFO", CostMethod = CostMethod.Fifo });
        _context.Products.Add(new Product { ProductCode = "MAT-F", ProductName = "F", CategoryCode = "C-FIFO", StockUom = "PCS" });
        Move("PI-1", "PI", "Purchase", "MAT-F", +1, 2, 50m);
        Journal("INV", "PI-1", 100m);
        OnHand("MAT-F", 2, 999m);   // FIFO 品的庫存列單價不是計價依據
        _context.InventoryCostLayers.Add(new InventoryCostLayer
        {
            PlantId = Plant, ProductCode = "MAT-F", LocationCode = Loc, LotNo = "",
            ReceiptDate = new DateOnly(2026, 10, 1), SourceDocNo = "PI-1", QtyRemaining = 2, UnitCost = 50m,
        });
        await _context.SaveChangesAsync();

        Assert.Empty(await _svc.AuditInventoryGlSubledgerAsync(Plant));
    }

    [Fact]
    public async Task RunAllAsync_殘差擋關帳()
    {
        Move("IA-9", "IA", "Adjustment", "MAT-A", +1, 3, 10m);
        OnHand("MAT-A", 3, 10m);
        await _context.SaveChangesAsync();

        var result = await _svc.RunAllAsync(Plant);

        Assert.False(result.IsPassed);
        Assert.Contains(result.BlockingFailures, f => f.AuditType == "INV_GL_SUBLEDGER");
        Assert.DoesNotContain(result.BlockingFailures, f => f.AuditType == "INV_GL_DOC_DIFF");
    }

    // ── 種資料 ───────────────────────────────────────────────────────────

    private void Move(string docNo, string typeCode, string category, string product,
        int direction, decimal qty, decimal unitCost, decimal? totalCost = null)
        => _context.StockMovements.Add(new StockMovement
        {
            MovementId = Guid.NewGuid(), DocNo = docNo, DocLineNo = 1, TypeCode = typeCode, TypeCategory = category,
            MovementDate = new DateOnly(2026, 10, 1), PlantId = Plant, ProductCode = product, LocationCode = Loc,
            LotNo = "", UomCode = "PCS", StockUomCode = "PCS", Qty = qty, StockQty = qty, Direction = direction,
            UnitCost = unitCost, TotalCost = totalCost ?? qty * unitCost, CreatedAt = DateTime.UtcNow,
        });

    /// <summary>一張只有存貨科目與對方科目兩行的傳票；<paramref name="inventoryNet"/> 為存貨科目借減貸。</summary>
    private void Journal(string sourceType, string? sourceId, decimal inventoryNet)
    {
        var id = $"JE-{++_jeSeq}";
        var amt = Math.Abs(inventoryNet);
        _context.JournalEntries.Add(new JournalEntry
        {
            JournalId = id, PeriodId = "202610", EntryDate = new DateOnly(2026, 10, 1), SourceDocType = sourceType,
            SourceDocId = sourceId, PlantId = Plant,
            Lines =
            {
                new JournalLine { JournalId = id, LineNo = 1, AccountCode = StandardAccounts.RawMaterialInventoryCode, AccountName = "存貨",
                    Debit = inventoryNet > 0 ? amt : 0, Credit = inventoryNet < 0 ? amt : 0 },
                new JournalLine { JournalId = id, LineNo = 2, AccountCode = StandardAccounts.GrIrAPClearingCode, AccountName = "對方",
                    Debit = inventoryNet < 0 ? amt : 0, Credit = inventoryNet > 0 ? amt : 0 },
            },
        });
    }

    private void OnHand(string product, decimal qty, decimal unitCost)
        => _context.Inventories.Add(new Inventory
        {
            PlantId = Plant, ProductCode = product, LocationCode = Loc, LotNo = "", QtyOnHand = qty, UnitCost = unitCost,
        });

    private void Wo(string id, WorkOrderStatus status)
        => _context.WorkOrders.Add(new WorkOrder { WorkOrderId = id, ProductCode = "FG-A", Status = status, PlantId = Plant });

    private void Slip(string id, MaterialSlipCategory category, string woId)
        => _context.MaterialSlips.Add(new MaterialSlip
        {
            SlipId = id, SlipNo = id, Category = category, Status = MaterialSlipStatus.Posted,
            SlipDate = new DateOnly(2026, 10, 1), SourceDocType = "WorkOrder", SourceDocId = woId, PlantId = Plant,
        });
}
