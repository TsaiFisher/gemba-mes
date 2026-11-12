using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Models;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// 孤兒過帳稽核（AuditOrphanPostingAsync）。
///
/// 這支稽核守的是一條資料庫層完全沒有保護的縫：StockMovement 與 JournalEntry
/// 對單據只有索引、沒有外鍵，靠 DocNo / SourceDocId 字串弱關聯。所以「單據被刪掉、
/// 過帳留在原地」不會觸發任何約束錯誤，只能靠應用層守門攔——本稽核是最後一道防線。
///
/// 測試的重點有二：
///   ① 真孤兒要抓到（刪除已過帳單據、來源單消失的傳票與發票）
///   ② **更重要的是不能誤報**——本稽核未來會掛進 RunAllAsync，而 RunAllAsync 就是
///      關帳前稽核（RunPreClosingAuditAsync），一旦誤報就直接把期間卡在 Closing。
///      後沖 "-BF" 後綴與無法反查的來源型別這兩條，就是專門守誤報的。
/// </summary>
public class OrphanPostingAuditTests : TestBase
{
    private const string Plant = "P001";
    private readonly DataAuditService _svc;

    public OrphanPostingAuditTests()
    {
        _svc = new DataAuditService(_context);
    }

    // ── ① 庫存異動孤兒 ──────────────────────────────────────────────────

    [Fact]
    public async Task OrphanStockMovement_SourceDocDeleted_Reported()
    {
        // 報廢單已過帳產生庫存異動，之後單據被刪除（僅留異動）
        SeedMovement("SD001", "IJ");

        var failures = await _svc.AuditOrphanPostingAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("ORPHAN_STOCK_MOVEMENT", f.AuditType);
        Assert.Equal("SD001", f.DocId);
    }

    [Fact]
    public async Task OrphanStockMovement_SourceDocExists_NoFailure()
    {
        SeedScrapDoc("SD001");
        SeedMovement("SD001", "IJ");

        Assert.Empty(await _svc.AuditOrphanPostingAsync(Plant));
    }

    /// <summary>
    /// 後沖領料的異動單號是「工單號 + -BF」（ProductionCompletionService），
    /// 比對前不剝除後綴的話，**全廠每一筆後沖異動都會被報成孤兒**——
    /// 這是本稽核最容易誤報、後果也最嚴重的一條（會擋住關帳）。
    /// </summary>
    [Fact]
    public async Task OrphanStockMovement_BackflushSuffix_WorkOrderExists_NoFailure()
    {
        SeedWorkOrder("WO001");
        SeedMovement("WO001-BF", "IO");

        Assert.Empty(await _svc.AuditOrphanPostingAsync(Plant));
    }

    [Fact]
    public async Task OrphanStockMovement_BackflushSuffix_WorkOrderDeleted_Reported()
    {
        SeedMovement("WO001-BF", "IO");

        var f = Assert.Single(await _svc.AuditOrphanPostingAsync(Plant));
        Assert.Equal("ORPHAN_STOCK_MOVEMENT", f.AuditType);
        Assert.Equal("WO001-BF", f.DocId);
    }

    /// <summary>一張單多筆異動只報一筆，不逐行洗版；淨量帶在 DifferenceAmount 供追查。</summary>
    [Fact]
    public async Task OrphanStockMovement_MultipleLines_ReportedOnce()
    {
        SeedMovement("SD001", "IJ", lineNo: 1, qty: 10m);
        SeedMovement("SD001", "IJ", lineNo: 2, qty: 5m);

        var f = Assert.Single(await _svc.AuditOrphanPostingAsync(Plant));
        Assert.Equal(-15m, f.DifferenceAmount);   // Direction = -1
    }

    [Fact]
    public async Task OrphanStockMovement_OtherPlant_NotReportedWhenFiltered()
    {
        SeedMovement("SD001", "IJ", plantId: "P002");

        Assert.Empty(await _svc.AuditOrphanPostingAsync(Plant));
        Assert.Single(await _svc.AuditOrphanPostingAsync("P002"));
    }

    /// <summary>
    /// 跨廠調撥的異動落在一個廠、單據主檔可能歸屬另一個廠，
    /// 故單號存在性判定刻意不做廠別過濾——否則正常的跨廠資料會被誤報。
    /// </summary>
    [Fact]
    public async Task OrphanStockMovement_DocInAnotherPlant_NoFailure()
    {
        SeedScrapDoc("SD001", plantId: "P002");
        SeedMovement("SD001", "IJ", plantId: Plant);

        Assert.Empty(await _svc.AuditOrphanPostingAsync(Plant));
    }

    // ── ② 傳票孤兒 ──────────────────────────────────────────────────────

    [Fact]
    public async Task OrphanJournal_SourceDocDeleted_Reported()
    {
        SeedJournal("JE001", "GoodsReceipt", "GR001");

        var f = Assert.Single(await _svc.AuditOrphanPostingAsync(Plant));
        Assert.Equal("ORPHAN_JOURNAL_ENTRY", f.AuditType);
        Assert.Equal("JE001", f.DocId);
    }

    [Fact]
    public async Task OrphanJournal_SourceDocExists_NoFailure()
    {
        SeedGoodsReceipt("GR001");
        SeedJournal("JE001", "GoodsReceipt", "GR001");

        Assert.Empty(await _svc.AuditOrphanPostingAsync(Plant));
    }

    /// <summary>
    /// Manual（無來源單）、INV（庫存單據統稱，SourceDocId 可能來自任一庫存單據表）、
    /// ActualRateReest（來源是期間非單據）一律略過不判。
    /// 寧可漏報也不誤報：這支稽核會擋關帳。
    /// </summary>
    [Theory]
    [InlineData("Manual")]
    [InlineData("INV")]
    [InlineData("ActualRateReest")]
    [InlineData("APInvoiceReturn")]
    public async Task OrphanJournal_UnresolvableSourceType_NeverReported(string sourceDocType)
    {
        SeedJournal("JE001", sourceDocType, "WHATEVER-999");

        Assert.Empty(await _svc.AuditOrphanPostingAsync(Plant));
    }

    /// <summary>WorkOrderCompletion 與 WorkOrder 都反查工單表（同一批完工分錄共用工單號）。</summary>
    [Fact]
    public async Task OrphanJournal_WorkOrderCompletion_ResolvesToWorkOrder()
    {
        SeedWorkOrder("WO001");
        SeedJournal("JE001", "WorkOrderCompletion", "WO001");

        Assert.Empty(await _svc.AuditOrphanPostingAsync(Plant));
    }

    [Fact]
    public async Task OrphanJournal_NullSourceDocId_NotReported()
    {
        SeedJournal("JE001", "GoodsReceipt", sourceDocId: null);

        Assert.Empty(await _svc.AuditOrphanPostingAsync(Plant));
    }

    // ── ③ 應付／應收發票孤兒 ────────────────────────────────────────────

    [Fact]
    public async Task OrphanApInvoice_SourceReceiptDeleted_Reported()
    {
        SeedApInvoice("AP001", "GoodsReceipt", "GR001");

        var f = Assert.Single(await _svc.AuditOrphanPostingAsync(Plant));
        Assert.Equal("ORPHAN_INVOICE", f.AuditType);
        Assert.Equal("AP001", f.DocId);
        Assert.Contains("應付", f.ErrorDescription);
    }

    [Fact]
    public async Task OrphanApInvoice_SourceReceiptExists_NoFailure()
    {
        SeedGoodsReceipt("GR001");
        SeedApInvoice("AP001", "GoodsReceipt", "GR001");

        Assert.Empty(await _svc.AuditOrphanPostingAsync(Plant));
    }

    // ── ④ 全域 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task OrphanPosting_CleanDatabase_NoFailure()
    {
        SeedScrapDoc("SD001");
        SeedMovement("SD001", "IJ");
        SeedGoodsReceipt("GR001");
        SeedJournal("JE001", "GoodsReceipt", "GR001");
        SeedApInvoice("AP001", "GoodsReceipt", "GR001");

        Assert.Empty(await _svc.AuditOrphanPostingAsync(plantId: null));
    }

    // ── Seed helpers ────────────────────────────────────────────────────

    // ── ④ 已掛進 RunAllAsync（Phase 5）─────────────────────────────────────

    /// <summary>
    /// 稽核方法存在但沒被 <c>RunAllAsync</c> 呼叫，等於整支稽核從來不會跑——
    /// 而那正是它前四個月的狀態（Phase 0 刻意只交付方法、不掛入）。
    /// 這條釘住「有掛進去」這件事本身；被拿掉時上面所有測試都還是綠的。
    /// </summary>
    [Fact]
    public async Task RunAllAsync_IncludesOrphanPostingAudit()
    {
        SeedMovement("SD001", "IJ");   // 報廢單已過帳但單據已被刪除

        var result = await _svc.RunAllAsync(Plant);

        Assert.Contains(result.Failures, f => f.AuditType == "ORPHAN_STOCK_MOVEMENT"
                                              && f.DocId == "SD001");
    }

    /// <summary>對照組：沒有孤兒時 RunAllAsync 不得因為本稽核而多報任何一筆。</summary>
    [Fact]
    public async Task RunAllAsync_NoOrphans_ReportsNothingFromThisAudit()
    {
        SeedScrapDoc("SD001");
        SeedMovement("SD001", "IJ");

        var result = await _svc.RunAllAsync(Plant);

        Assert.DoesNotContain(result.Failures, f => f.AuditType.StartsWith("ORPHAN_"));
    }

    private void SeedMovement(
        string docNo, string typeCode, int lineNo = 1, decimal qty = 10m,
        string? plantId = null)
    {
        _context.StockMovements.Add(new StockMovement
        {
            DocNo = docNo, DocLineNo = lineNo, TypeCode = typeCode,
            PlantId = plantId ?? Plant, ProductCode = "RM-1", LocationCode = "L1", LotNo = "",
            StockQty = qty, Qty = qty, StockUomCode = "PCS", UomCode = "PCS",
            Direction = -1, UnitCost = 1m, TotalCost = qty,
            MovementDate = new DateOnly(2026, 6, 15),
        });
        _context.SaveChanges();
    }

    private void SeedScrapDoc(string id, string? plantId = null)
    {
        _context.ScrapDocs.Add(new ScrapDoc
        {
            ScrapDocId = id, PlantId = plantId ?? Plant, TypeCode = "IJ",
            ScrapDate = new DateOnly(2026, 6, 15),
        });
        _context.SaveChanges();
    }

    private void SeedWorkOrder(string id)
    {
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = id, PlantId = Plant, ProductCode = "FG-1", Quantity = 100m,
        });
        _context.SaveChanges();
    }

    private void SeedGoodsReceipt(string id)
    {
        _context.GoodsReceipts.Add(new GoodsReceipt
        {
            GoodsReceiptId = id, PlantId = Plant, Status = DocStatus.Posted,
            ReceiptDate = new DateOnly(2026, 6, 15),
        });
        _context.SaveChanges();
    }

    private void SeedJournal(string journalId, string sourceDocType, string? sourceDocId)
    {
        _context.JournalEntries.Add(new JournalEntry
        {
            JournalId = journalId, PeriodId = "202606", PlantId = Plant,
            EntryDate = new DateOnly(2026, 6, 15),
            SourceDocType = sourceDocType, SourceDocId = sourceDocId,
        });
        _context.SaveChanges();
    }

    /// <summary>
    /// 作廢之後不該再報。
    ///
    /// 本稽核的訊息就是叫人「來源單被刪除但發票未一併作廢」——作廢完卻還留在名單上，
    /// 這條稽核就會被當成永遠清不掉的噪音而被忽略（實機驗收時就是這樣：
    /// 照訊息把發票作廢，重跑稽核，同一筆原封不動）。
    /// 而它會掛進關帳前稽核，清不掉的異常等於期間永遠關不了帳。
    /// </summary>
    [Fact]
    public async Task OrphanApInvoice_Voided_NoFailure()
    {
        SeedApInvoice("AP001", "GoodsReceipt", "GR001", APInvoiceStatus.Void);

        Assert.Empty(await _svc.AuditOrphanPostingAsync(Plant));
    }

    private void SeedApInvoice(string id, string sourceDocType, string sourceDocId,
                               APInvoiceStatus status = APInvoiceStatus.Open)
    {
        _context.APInvoices.Add(new APInvoice
        {
            APInvoiceId = id, PlantId = Plant, Status = status,
            SourceDocType = sourceDocType, SourceDocId = sourceDocId,
        });
        _context.SaveChanges();
    }
}
