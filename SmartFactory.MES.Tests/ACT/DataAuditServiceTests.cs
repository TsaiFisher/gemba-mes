using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Tests.Shared;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.PUR.Models;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// DataAuditService 資料流一致性稽核測試（business_data_flow_reconciliation.md §3/§4）。
/// 重點驗證：狀態過濾（草稿/作廢不計）、作廢發票排除、容忍值邊界、
/// 委外在外平衡（發料−退料−理論消耗）、庫存滾動平衡、重複立帳與關帳擋關整合。
/// </summary>
public class DataAuditServiceTests : TestBase
{
    private const string Plant = "P001";
    private readonly DataAuditService _svc;

    public DataAuditServiceTests()
    {
        _svc = new DataAuditService(_context);
    }

    // ── §3.1 採購超收 ────────────────────────────────────────────────────

    [Fact]
    public async Task PoOverReceipt_WithinOrderQty_NoFailure()
    {
        SeedPoLine("PO001", 1, "MAT-A", orderQty: 100m);
        SeedGrLine("GR001", DocStatus.Posted, "PO001", 1, receiptQty: 100m);

        var failures = await _svc.AuditPoOverReceiptAsync(Plant);

        Assert.Empty(failures);
    }

    [Fact]
    public async Task PoOverReceipt_OverByMinUnit_Fails()
    {
        SeedPoLine("PO001", 1, "MAT-A", orderQty: 100m);
        SeedGrLine("GR001", DocStatus.Posted, "PO001", 1, receiptQty: 100.001m);

        var failures = await _svc.AuditPoOverReceiptAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("PO_OVER_RECEIPT", f.AuditType);
        Assert.Equal(0.001m, f.DifferenceAmount);
    }

    [Fact]
    public async Task PoOverReceipt_DraftAndVoidGrExcluded_NoFailure()
    {
        // §3.1 修正點：原 T-SQL 未過濾 GR 狀態，草稿/作廢收料會誤報超收
        SeedPoLine("PO001", 1, "MAT-A", orderQty: 100m);
        SeedGrLine("GR001", DocStatus.Draft, "PO001", 1, receiptQty: 999m);
        SeedGrLine("GR002", DocStatus.Void,  "PO001", 1, receiptQty: 999m);

        var failures = await _svc.AuditPoOverReceiptAsync(Plant);

        Assert.Empty(failures);
    }

    [Fact]
    public async Task PoOverReceipt_ReturnedQtyDeducted_NoFailure()
    {
        SeedPoLine("PO001", 1, "MAT-A", orderQty: 100m);
        SeedGrLine("GR001", DocStatus.Posted, "PO001", 1, receiptQty: 120m, returnedQty: 30m);

        var failures = await _svc.AuditPoOverReceiptAsync(Plant);

        Assert.Empty(failures);
    }

    // ── §3.2 GR/IR ──────────────────────────────────────────────────────

    [Fact]
    public async Task GrIr_InvoiceQtyOverReceipt_Fails()
    {
        SeedGrLine("GR001", DocStatus.Confirmed, "PO001", 1, receiptQty: 10m, lineAmount: 1000m);
        SeedApInvoice("AP001", "GR001", qty: 12m, lineAmount: 1200m);

        var failures = await _svc.AuditGrIrAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("GRIR_OVER_INVOICE", f.AuditType);
    }

    [Fact]
    public async Task GrIr_VoidInvoiceExcluded_NoOverInvoice()
    {
        // §3.2 修正點：原 T-SQL 未排除作廢發票
        SeedGrLine("GR001", DocStatus.Confirmed, "PO001", 1, receiptQty: 10m, lineAmount: 1000m,
                   receiptDate: DateOnly.FromDateTime(DateTime.UtcNow));
        SeedApInvoice("AP001", "GR001", qty: 99m, lineAmount: 9900m, status: APInvoiceStatus.Void);

        var failures = await _svc.AuditGrIrAsync(Plant);

        Assert.Empty(failures);
    }

    [Fact]
    public async Task GrIr_Aged30DaysUnmatched_Fails()
    {
        SeedGrLine("GR001", DocStatus.Posted, "PO001", 1, receiptQty: 10m, lineAmount: 1000m,
                   receiptDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-40));

        var failures = await _svc.AuditGrIrAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("GRIR_AGING", f.AuditType);
        Assert.Equal(1000m, f.DifferenceAmount);
    }

    [Fact]
    public async Task GrIr_RecentUnmatched_NoAgingFailure()
    {
        SeedGrLine("GR001", DocStatus.Posted, "PO001", 1, receiptQty: 10m, lineAmount: 1000m,
                   receiptDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-5));

        var failures = await _svc.AuditGrIrAsync(Plant);

        Assert.Empty(failures);
    }

    /// <summary>
    /// 已過帳的採購退貨要從**暫估側**扣掉（W4-F15）。
    ///
    /// 暫估的實體是總帳 2152：上架貸記、AP 認列借回，而採購退貨過帳**同樣借記 2152**
    /// （BuildJournalLines 的 (-1, "Purchase") 分支）。所以退掉的那一段早就沖平了，
    /// 拿整張收貨單的金額當基準等於重複計算。
    ///
    /// 這個偏差原本看不到：認列側（AccrueForReceivedQtyAsync）也沒扣退貨量，
    /// 兩邊同額高估剛好相消。修好認列側（W4-F12）之後才顯形——實際資料
    /// PE260400005 收 2 退 1，認列正確變成 1 個，暫估卻還停在 2 個，憑空冒出 15 元掛帳。
    /// </summary>
    [Fact]
    public async Task GrIr_PostedReturn_IsNettedOffTheAccrualSide()
    {
        SeedGrLine("GR001", DocStatus.Posted, "PO001", 1, receiptQty: 2m, lineAmount: 30m,
                   unitPrice: 15m, receiptDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-40));
        SeedPurchaseReturn("GRT001", "GR001", 1m, DocStatus.Posted);
        SeedApInvoice("AP001", "GR001", qty: 1m, lineAmount: 15m);   // 認列＝收 2 − 退 1

        var failures = await _svc.AuditGrIrAsync(Plant);

        Assert.Empty(failures);   // 錯誤實作會報 GRIR_AGING 掛帳 15
    }

    /// <summary>退貨還是草稿時不扣——總帳的回沖要等過帳，提早扣會比總帳早一步。</summary>
    [Fact]
    public async Task GrIr_DraftReturn_IsNotNetted()
    {
        SeedGrLine("GR001", DocStatus.Posted, "PO001", 1, receiptQty: 2m, lineAmount: 30m,
                   unitPrice: 15m, receiptDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-40));
        SeedPurchaseReturn("GRT001", "GR001", 1m, DocStatus.Draft);
        SeedApInvoice("AP001", "GR001", qty: 1m, lineAmount: 15m);

        var failures = await _svc.AuditGrIrAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("GRIR_AGING", f.AuditType);
        Assert.Equal(15m, f.DifferenceAmount);
    }

    /// <summary>淨掉退貨之後，超過「應認列量」的開票仍要判溢開——下界不能連真缺口一起放掉。</summary>
    [Fact]
    public async Task GrIr_InvoiceOverNetOfReturn_StillFailsAsOverInvoice()
    {
        SeedGrLine("GR001", DocStatus.Posted, "PO001", 1, receiptQty: 2m, lineAmount: 30m,
                   unitPrice: 15m);
        SeedPurchaseReturn("GRT001", "GR001", 1m, DocStatus.Posted);
        SeedApInvoice("AP001", "GR001", qty: 2m, lineAmount: 30m);   // 該只認列 1 個

        var failures = await _svc.AuditGrIrAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("GRIR_OVER_INVOICE", f.AuditType);
        Assert.Equal(1m, f.DifferenceAmount);
    }

    [Fact]
    public async Task GrIr_PpvAmountDiffSameQty_NoFailure()
    {
        // 數量一致、金額不同 = 合法 PPV，不判溢開
        SeedGrLine("GR001", DocStatus.Posted, "PO001", 1, receiptQty: 10m, lineAmount: 1000m,
                   receiptDate: DateOnly.FromDateTime(DateTime.UtcNow));
        SeedApInvoice("AP001", "GR001", qty: 10m, lineAmount: 1080m);

        var failures = await _svc.AuditGrIrAsync(Plant);

        Assert.Empty(failures);
    }

    // ── §3.3 委外在外原料平衡 ───────────────────────────────────────────

    [Fact]
    public async Task Outsource_PositiveBalance_NoFailure()
    {
        SeedOutsourceChain(issuedQty: 100m, returnedQty: 0m, receiptQty: 9m, bomUsagePerUnit: 10m);

        var failures = await _svc.AuditOutsourceBalanceAsync(Plant);

        Assert.Empty(failures); // 100 − 90 = 10 在外，正常
    }

    [Fact]
    public async Task Outsource_ConsumedExceedsIssued_Fails()
    {
        SeedOutsourceChain(issuedQty: 100m, returnedQty: 0m, receiptQty: 15m, bomUsagePerUnit: 10m);

        var failures = await _svc.AuditOutsourceBalanceAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("OUTSOURCE_NEG_BALANCE", f.AuditType);
        Assert.Equal(-50m, f.DifferenceAmount); // 100 − 150
    }

    [Fact]
    public async Task Outsource_ReturnedQtyDeducted_Fails()
    {
        // §3.3 修正點：原 T-SQL 漏計退料（OR），退料後理論消耗超出淨發料須揪出
        SeedOutsourceChain(issuedQty: 100m, returnedQty: 20m, receiptQty: 9m, bomUsagePerUnit: 10m);

        var failures = await _svc.AuditOutsourceBalanceAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal(-10m, f.DifferenceAmount); // (100−20) − 90
    }

    /// <summary>
    /// 走 **MFG 發退料單**（Category=OutsourceIssue）發出去的料同樣要被檢查到。
    ///
    /// 委外發料有兩個入口：INV 的委外領退料單與 MFG 的發退料單，兩者產生的庫存異動
    /// 一模一樣（TypeCode OO／Direction −1）。本稽核原本只查前者，於是走發退料單那條路
    /// 發出去的料在稽核眼中根本不存在——在外原料變成負數也不會有人知道，
    /// 而每一張傳票都平衡、庫存數量也對（人工驗收 W2-F12）。
    /// </summary>
    [Fact]
    public async Task Outsource_IssuedViaMaterialSlip_IsAlsoAudited()
    {
        const string wo = "WO-MS-1", routing = "RT-MS-1", mat = "MAT-A", opo = "OPO-MS-1";

        _context.WorkOrders.Add(new WorkOrder { WorkOrderId = wo, RoutingId = routing, ProductCode = "FG-A", PlantId = Plant });
        _context.RoutingBoms.Add(new RoutingBom { RoutingId = routing, OperationSeq = 10, BomSeq = 1, ProductCode = mat, Quantity = 10m });

        // 發退料單，不是委外領退料單
        _context.MaterialSlips.Add(new SmartFactory.MES.Modules.MFG.Models.MaterialSlip
        {
            SlipId = "OI-MS-1", SlipNo = "OI-MS-1",
            Category = SmartFactory.MES.Modules.MFG.Models.MaterialSlipCategory.OutsourceIssue,
            Status = SmartFactory.MES.Modules.MFG.Models.MaterialSlipStatus.Posted,
            SourceDocType = "WorkOrder", SourceDocId = wo, PlantId = Plant,
        });
        SeedMovement("OI-MS-1", mat, "L01", qty: 100m, direction: -1, typeCode: "OO");

        _context.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
        { OutsourcePurchaseOrderId = opo, LineNo = 1, SourceWorkOrderId = wo, ProductCode = "FG-A" });
        _context.OutsourceReceipts.Add(new OutsourceReceipt
        { OutsourceReceiptId = "ORC-MS-1", OutsourcePurchaseOrderId = opo, SupplierCode = "S001", Status = DocStatus.Posted, PlantId = Plant });
        _context.OutsourceReceiptLines.Add(new OutsourceReceiptLine
        { OutsourceReceiptId = "ORC-MS-1", LineNo = 1, OpoLineNo = 1, ProductCode = "FG-A", ReceiptQty = 15m, StockQty = 15m });
        _context.SaveChanges();

        var failures = await _svc.AuditOutsourceBalanceAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("OUTSOURCE_NEG_BALANCE", f.AuditType);
        Assert.Equal(-50m, f.DifferenceAmount);   // 發 100、理論消耗 15×10=150
    }

    // ── §3.4 傳票借貸平衡 ───────────────────────────────────────────────

    [Fact]
    public async Task JournalBalance_Balanced_NoFailure()
    {
        SeedJournal("JE001", "APPayment", "PAY001", debit: 100m, credit: 100m);

        var failures = await _svc.AuditJournalBalanceAsync(Plant);

        Assert.Empty(failures);
    }

    [Fact]
    public async Task JournalBalance_Unbalanced_Fails()
    {
        SeedJournal("JE001", "APPayment", "PAY001", debit: 100m, credit: 99.5m);

        var failures = await _svc.AuditJournalBalanceAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("JOURNAL_UNBALANCED", f.AuditType);
        Assert.Equal(0.5m, f.DifferenceAmount);
    }

    // ── 重複立帳 ────────────────────────────────────────────────────────

    [Fact]
    public async Task DuplicateJournal_TwoEntriesSameSource_Fails()
    {
        SeedJournal("JE001", "APPayment", "PAY001", debit: 100m, credit: 100m);
        SeedJournal("JE002", "APPayment", "PAY001", debit: 100m, credit: 100m);

        var failures = await _svc.AuditDuplicateJournalAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("JOURNAL_DUPLICATE", f.AuditType);
        Assert.Contains("PAY001", f.DocId);
    }

    [Fact]
    public async Task DuplicateJournal_InvTypeMultipleEntries_NoFailure()
    {
        // "INV" 完整模式逐行過帳，同一單據多張傳票屬正常，不列入 1:1 檢核
        SeedJournal("JE001", "INV", "GR001", debit: 100m, credit: 100m);
        SeedJournal("JE002", "INV", "GR001", debit: 50m, credit: 50m);

        var failures = await _svc.AuditDuplicateJournalAsync(Plant);

        Assert.Empty(failures);
    }

    // ── 庫存滾動平衡 ────────────────────────────────────────────────────

    [Fact]
    public async Task InvRollForward_MovementsMatchOnHand_NoFailure()
    {
        SeedMovement("DOC1", "MAT-A", "L01", qty: 10m, direction: 1);
        SeedMovement("DOC2", "MAT-A", "L01", qty: 3m, direction: -1);
        SeedInventory("MAT-A", "L01", qtyOnHand: 7m);

        var failures = await _svc.AuditInventoryRollForwardAsync(Plant);

        Assert.Empty(failures);
    }

    [Fact]
    public async Task InvRollForward_OnHandDrift_Fails()
    {
        SeedMovement("DOC1", "MAT-A", "L01", qty: 10m, direction: 1);
        SeedInventory("MAT-A", "L01", qtyOnHand: 8m);

        var failures = await _svc.AuditInventoryRollForwardAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("INV_ROLLFORWARD", f.AuditType);
        Assert.Equal(2m, f.DifferenceAmount);
    }

    [Fact]
    public async Task InvRollForward_MovementWithoutInventoryRow_Fails()
    {
        SeedMovement("DOC1", "MAT-A", "L01", qty: 10m, direction: 1);

        var failures = await _svc.AuditInventoryRollForwardAsync(Plant);

        Assert.Single(failures);
    }

    // ── §1.2-1 銷售超出貨 ───────────────────────────────────────────────

    [Fact]
    public async Task SoOverShipment_NetOverOrder_Fails()
    {
        SeedSoLine("SO001", 1, "FG-A", orderQty: 50m);
        SeedGsLine("GS001", DocStatus.Posted, "SO001", 1, shipQty: 60m);

        var failures = await _svc.AuditSoOverShipmentAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("SO_OVER_SHIPMENT", f.AuditType);
        Assert.Equal(10m, f.DifferenceAmount);
    }

    [Fact]
    public async Task SoOverShipment_DraftGsExcluded_NoFailure()
    {
        SeedSoLine("SO001", 1, "FG-A", orderQty: 50m);
        SeedGsLine("GS001", DocStatus.Draft, "SO001", 1, shipQty: 999m);

        var failures = await _svc.AuditSoOverShipmentAsync(Plant);

        Assert.Empty(failures);
    }

    // ── §1.2-2 出貨未開 AR 發票 ─────────────────────────────────────────

    [Fact]
    public async Task ShipmentInvoiceGap_ConfirmedWithoutInvoice_Fails()
    {
        SeedGsLine("GS001", DocStatus.Confirmed, "SO001", 1, shipQty: 10m,
                   shipDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3));

        var failures = await _svc.AuditShipmentInvoiceGapAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("GS_NO_AR_INVOICE", f.AuditType);
    }

    [Fact]
    public async Task ShipmentInvoiceGap_WithValidInvoice_NoFailure()
    {
        SeedGsLine("GS001", DocStatus.Confirmed, "SO001", 1, shipQty: 10m,
                   shipDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3));
        SeedArInvoice("AR001", "GS001");

        var failures = await _svc.AuditShipmentInvoiceGapAsync(Plant);

        Assert.Empty(failures);
    }

    [Fact]
    public async Task ShipmentInvoiceGap_OnlyVoidInvoice_Fails()
    {
        SeedGsLine("GS001", DocStatus.Confirmed, "SO001", 1, shipQty: 10m,
                   shipDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3));
        SeedArInvoice("AR001", "GS001", status: ARInvoiceStatus.Void);

        var failures = await _svc.AuditShipmentInvoiceGapAsync(Plant);

        Assert.Single(failures);
    }

    /// <summary>
    /// 出貨日早於該廠**最早會計期間**：仍然列出來，但不擋關帳（W4-F13）。
    ///
    /// 本系統對「沒有期間列」的日期是明確放行的（AssertCanPostAsync 查無期間即通過），
    /// AccountingPeriodSeeder 也刻意不建立過去的期間。既然那段日期不在期間治理範圍內，
    /// 就不能拿那段的單據完整性去擋關帳——那是一個誰都過不了的閘門：期間建不出來、
    /// 歷史單據不能改，補開發票又會把營收記進一個不存在的期間、且沒有對應的銷貨成本。
    /// 2026-09-07 在 dev 庫實測 ADM 廠 GS-2605-00006／00007（出貨日 2026-05-13、
    /// 最早期間 202606）就是這個形狀。
    ///
    /// 刻意**不是**從結果裡濾掉：那些單據確實沒有發票，該讓人在稽核報表上看得到。
    /// </summary>
    [Fact]
    public async Task ShipmentInvoiceGap_BeforeEarliestPeriod_IsAdvisoryNotBlocking()
    {
        SeedPeriod("202606", new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        SeedGsLine("GS-PRE", DocStatus.Confirmed, "SO001", 1, shipQty: 10m,
                   shipDate: new DateOnly(2026, 5, 13));

        var failures = await _svc.AuditShipmentInvoiceGapAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("GS_NO_AR_INVOICE", f.AuditType);
        Assert.False(f.IsBlocking);                     // 列得出來，但不擋關帳
        Assert.Contains("期初前資料", f.ErrorDescription);
    }

    /// <summary>期間治理範圍**之內**的同一種缺口照樣擋關帳——下界不能順手把真缺口一起放掉。</summary>
    [Fact]
    public async Task ShipmentInvoiceGap_OnOrAfterEarliestPeriod_StaysBlocking()
    {
        SeedPeriod("202606", new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        SeedGsLine("GS-IN", DocStatus.Confirmed, "SO001", 1, shipQty: 10m,
                   shipDate: new DateOnly(2026, 6, 1));   // 恰好是起日 → 在範圍內

        var failures = await _svc.AuditShipmentInvoiceGapAsync(Plant);

        var f = Assert.Single(failures);
        Assert.True(f.IsBlocking);
    }

    /// <summary>完全沒有建立過任何期間的廠不設下界——否則等於整個稽核靜默關閉。</summary>
    [Fact]
    public async Task ShipmentInvoiceGap_NoPeriodsAtAll_StaysBlocking()
    {
        SeedGsLine("GS-NOPER", DocStatus.Confirmed, "SO001", 1, shipQty: 10m,
                   shipDate: new DateOnly(2020, 1, 1));

        var failures = await _svc.AuditShipmentInvoiceGapAsync(Plant);

        Assert.True(Assert.Single(failures).IsBlocking);
    }

    // ── §3.5 ERP 拋轉滯留 ──────────────────────────────────────────────

    [Fact]
    public async Task ErpSyncLag_PendingOver24h_Fails()
    {
        SeedApInvoice("AP001", "GR001", qty: 1m, lineAmount: 100m,
                      erpStatus: ErpSyncStatus.Pending, createdAt: DateTime.UtcNow.AddDays(-2));

        var failures = await _svc.AuditErpSyncLagAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("ERP_SYNC_LAG", f.AuditType);
    }

    [Fact]
    public async Task ErpSyncLag_SyncedOrRecentOrVoid_NoFailure()
    {
        SeedApInvoice("AP001", "GR001", qty: 1m, lineAmount: 100m,
                      erpStatus: ErpSyncStatus.Synced, createdAt: DateTime.UtcNow.AddDays(-2));
        SeedApInvoice("AP002", "GR002", qty: 1m, lineAmount: 100m,
                      erpStatus: ErpSyncStatus.Pending, createdAt: DateTime.UtcNow);
        SeedApInvoice("AP003", "GR003", qty: 1m, lineAmount: 100m, status: APInvoiceStatus.Void,
                      erpStatus: ErpSyncStatus.Pending, createdAt: DateTime.UtcNow.AddDays(-2));

        var failures = await _svc.AuditErpSyncLagAsync(Plant);

        Assert.Empty(failures);
    }

    [Fact]
    public async Task ErpSyncLag_ArInvoicePendingOver24h_Fails()
    {
        // §9 P-3（D3 最小範圍）：ARInvoice 對稱納入拋轉滯留稽核
        SeedArInvoice("AR001", "GS001", erpStatus: ErpSyncStatus.Pending,
                      createdAt: DateTime.UtcNow.AddDays(-2));

        var failures = await _svc.AuditErpSyncLagAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("ERP_SYNC_LAG", f.AuditType);
        Assert.Equal("AR001", f.DocId);
        Assert.Contains("AR 發票", f.ErrorDescription);
    }

    [Fact]
    public async Task ErpSyncLag_ArInvoiceSyncedOrRecentOrVoid_NoFailure()
    {
        SeedArInvoice("AR001", "GS001", erpStatus: ErpSyncStatus.Synced,
                      createdAt: DateTime.UtcNow.AddDays(-2));
        SeedArInvoice("AR002", "GS002", erpStatus: ErpSyncStatus.Pending,
                      createdAt: DateTime.UtcNow);
        SeedArInvoice("AR003", "GS003", status: ARInvoiceStatus.Void,
                      erpStatus: ErpSyncStatus.Pending, createdAt: DateTime.UtcNow.AddDays(-2));

        Assert.Empty(await _svc.AuditErpSyncLagAsync(Plant));
    }

    // ── §3.8 Backflush 後沖消耗核對（P-5）───────────────────────────────

    [Fact]
    public async Task Backflush_ActualMatchesStandard_NoFailure()
    {
        // 工單量 100、完工 80、單位用量 2 → 標準後沖 160；實際 IO 160
        SeedBackflushChain(woQty: 100m, requiredQty: 200m, completedQty: 80m, backflushedQty: 160m);

        var failures = await _svc.AuditBackflushConsumptionAsync(Plant);

        Assert.Empty(failures);
    }

    [Fact]
    public async Task Backflush_ActualDiffersFromStandard_Fails()
    {
        SeedBackflushChain(woQty: 100m, requiredQty: 200m, completedQty: 80m, backflushedQty: 150m);

        var failures = await _svc.AuditBackflushConsumptionAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("BACKFLUSH_MISMATCH", f.AuditType);
        Assert.Equal(-10m, f.DifferenceAmount); // 實際 150 − 標準 160
    }

    [Fact]
    public async Task Backflush_MissingBackflushMovement_Fails()
    {
        // 已入庫但完全沒有後沖 movement（後沖漏跑）
        SeedBackflushChain(woQty: 100m, requiredQty: 200m, completedQty: 80m, backflushedQty: 0m);

        var failures = await _svc.AuditBackflushConsumptionAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal(-160m, f.DifferenceAmount);
    }

    [Fact]
    public async Task Backflush_NotClosedWorkOrder_NotAudited()
    {
        // 未入庫（InProgress）的工單不在核對範圍（後沖發生於完工入庫時）
        SeedBackflushChain(woQty: 100m, requiredQty: 200m, completedQty: 80m, backflushedQty: 0m,
                           status: WorkOrderStatus.InProgress);

        var failures = await _svc.AuditBackflushConsumptionAsync(Plant);

        Assert.Empty(failures);
    }

    // ── §3.9 反正規化欄位漂移（P-6）─────────────────────────────────────

    [Fact]
    public async Task PoReceivedDrift_MatchesEffectiveGrSum_NoFailure()
    {
        // 含草稿（回寫發生在建單時）、排除作廢
        SeedPoLine("PO001", 1, "MAT-A", orderQty: 100m, receivedQty: 80m);
        SeedGrLine("GR001", DocStatus.Posted, "PO001", 1, receiptQty: 50m);
        SeedGrLine("GR002", DocStatus.Draft,  "PO001", 1, receiptQty: 30m);
        SeedGrLine("GR003", DocStatus.Void,   "PO001", 1, receiptQty: 20m);

        var failures = await _svc.AuditPoReceivedQtyDriftAsync(Plant);

        Assert.Empty(failures);
    }

    [Fact]
    public async Task PoReceivedDrift_MismatchedDenorm_Fails()
    {
        // 累計欄位 100 但有效進貨僅 80 → 漂移 +20（例如作廢未回沖的歷史資料）
        SeedPoLine("PO001", 1, "MAT-A", orderQty: 100m, receivedQty: 100m);
        SeedGrLine("GR001", DocStatus.Posted, "PO001", 1, receiptQty: 80m);

        var failures = await _svc.AuditPoReceivedQtyDriftAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("PO_RECEIVED_DRIFT", f.AuditType);
        Assert.Equal(20m, f.DifferenceAmount);
    }

    [Fact]
    public async Task PoReceivedDrift_NoGrAtAll_DenormNonZero_Fails()
    {
        SeedPoLine("PO001", 1, "MAT-A", orderQty: 100m, receivedQty: 5m);

        var failures = await _svc.AuditPoReceivedQtyDriftAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal(5m, f.DifferenceAmount);
    }

    [Fact]
    public async Task SoShippedDrift_MatchesEffectiveGsSum_NoFailure()
    {
        SeedSoLine("SO001", 1, "FG-A", orderQty: 100m, shippedQty: 60m);
        SeedGsLine("GS001", DocStatus.Posted, "SO001", 1, shipQty: 40m);
        SeedGsLine("GS002", DocStatus.Draft,  "SO001", 1, shipQty: 20m);
        SeedGsLine("GS003", DocStatus.Void,   "SO001", 1, shipQty: 15m);

        var failures = await _svc.AuditSoShippedQtyDriftAsync(Plant);

        Assert.Empty(failures);
    }

    [Fact]
    public async Task SoShippedDrift_MismatchedDenorm_Fails()
    {
        SeedSoLine("SO001", 1, "FG-A", orderQty: 100m, shippedQty: 70m);
        SeedGsLine("GS001", DocStatus.Posted, "SO001", 1, shipQty: 40m);

        var failures = await _svc.AuditSoShippedQtyDriftAsync(Plant);

        var f = Assert.Single(failures);
        Assert.Equal("SO_SHIPPED_DRIFT", f.AuditType);
        Assert.Equal(30m, f.DifferenceAmount);
    }

    // ── §3.10 WIP 期末快照一致性（P-1 結構性守門）───────────────────────

    [Fact]
    public async Task WipSnapshot_CoverageMatchesAndFinal_NoFailure()
    {
        // 結構性守門全過後會進入 §3.10-5 金額恆等式（D4），種子須同時平衡：
        // 期末 180 = 期初 200 + 發料 70 + 人工製費 90 − 完工 180
        SeedPeriod("202606", PeriodStatus.Closing);
        SeedWo("WO001", WorkOrderStatus.InProgress);
        SeedWipSnapshot("202606", "WO001", material: 100m, labor: 50m, overhead: 30m);
        SeedWoCost("WO002", CostStatus.Final, new DateOnly(2026, 6, 15));   // 100+50+30=180
        SeedWipSnapshot("202605", "WO001", material: 60m, labor: 20m, overhead: 10m);   // 期初 90
        SeedWipSnapshot("202605", "WO002", material: 70m, labor: 25m, overhead: 15m);   // 期初 110
        SeedProductionMovement("WO001-BF", new DateOnly(2026, 6, 10), qty: 4m, unitCost: 10m);  // 發料 40
        SeedProductionMovement("WO002-BF", new DateOnly(2026, 6, 12), qty: 3m, unitCost: 10m);  // 發料 30
        SeedDeptCost("202606", "WC1", labor: 60m, overhead: 30m);            // 投入 90

        var failures = await _svc.AuditWipSnapshotConsistencyAsync("202606", Plant);

        Assert.Empty(failures);
    }

    // ── §3.10-5 WIP 金額恆等式（D4 跨期結轉定案後啟用）──────────────────

    [Fact]
    public async Task WipIdentity_DeptCostWithoutAbsorption_Fails()
    {
        // 平衡場景（同上）再加一筆「有費用但無工時」的工作中心 → 費用無處吸收，恆等式失衡 −100
        SeedPeriod("202606", PeriodStatus.Closing);
        SeedWo("WO001", WorkOrderStatus.InProgress);
        SeedWipSnapshot("202606", "WO001", material: 100m, labor: 50m, overhead: 30m);
        SeedWoCost("WO002", CostStatus.Final, new DateOnly(2026, 6, 15));
        SeedWipSnapshot("202605", "WO001", material: 60m, labor: 20m, overhead: 10m);
        SeedWipSnapshot("202605", "WO002", material: 70m, labor: 25m, overhead: 15m);
        SeedProductionMovement("WO001-BF", new DateOnly(2026, 6, 10), qty: 4m, unitCost: 10m);
        SeedProductionMovement("WO002-BF", new DateOnly(2026, 6, 12), qty: 3m, unitCost: 10m);
        SeedDeptCost("202606", "WC1", labor: 60m, overhead: 30m);
        SeedDeptCost("202606", "WC-IDLE", labor: 100m, overhead: 0m);   // 無工時 WC

        var failures = await _svc.AuditWipSnapshotConsistencyAsync("202606", Plant);

        var f = Assert.Single(failures);
        Assert.Equal("WIP_IDENTITY_DRIFT", f.AuditType);
        Assert.Equal(-100m, f.DifferenceAmount);
    }

    [Fact]
    public async Task WipIdentity_VoidedPriorWo_ReportsLeakage_AndAdjustsBeginning()
    {
        // 前期在製工單本期作廢 → 期初 60 無成本出口：逐筆報 WIP_VOID_LEAKAGE，
        // 恆等式期初調節掉該額後仍平衡（不重複報 DRIFT）
        SeedPeriod("202606", PeriodStatus.Closing);
        SeedWo("WO001", WorkOrderStatus.InProgress);
        SeedWo("WO-V", WorkOrderStatus.Void);
        SeedWipSnapshot("202606", "WO001", material: 100m, labor: 50m, overhead: 30m);
        SeedWoCost("WO002", CostStatus.Final, new DateOnly(2026, 6, 15));
        SeedWipSnapshot("202605", "WO001", material: 60m, labor: 20m, overhead: 10m);
        SeedWipSnapshot("202605", "WO002", material: 70m, labor: 25m, overhead: 15m);
        SeedWipSnapshot("202605", "WO-V", material: 20m, labor: 30m, overhead: 10m);   // 期初 60，本期作廢
        SeedProductionMovement("WO001-BF", new DateOnly(2026, 6, 10), qty: 4m, unitCost: 10m);
        SeedProductionMovement("WO002-BF", new DateOnly(2026, 6, 12), qty: 3m, unitCost: 10m);
        SeedDeptCost("202606", "WC1", labor: 60m, overhead: 30m);

        var failures = await _svc.AuditWipSnapshotConsistencyAsync("202606", Plant);

        var f = Assert.Single(failures);
        Assert.Equal("WIP_VOID_LEAKAGE", f.AuditType);
        Assert.Equal("WO-V", f.DocId);
        Assert.Equal(60m, f.DifferenceAmount);
    }

    [Fact]
    public async Task WipIdentity_SkippedWhenStructuralFailure()
    {
        // 結構性守門有失敗（快照缺失）→ 恆等式不評估（避免必然失衡的噪音）
        SeedPeriod("202606", PeriodStatus.Closing);
        SeedWo("WO001", WorkOrderStatus.InProgress);          // 無本期快照 → MISSING
        SeedDeptCost("202606", "WC-IDLE", labor: 999m, overhead: 0m);   // 若恆等式跑了必失衡

        var failures = await _svc.AuditWipSnapshotConsistencyAsync("202606", Plant);

        var f = Assert.Single(failures);
        Assert.Equal("WIP_SNAPSHOT_MISSING", f.AuditType);
        Assert.DoesNotContain(failures, x => x.AuditType == "WIP_IDENTITY_DRIFT");
    }

    [Fact]
    public async Task WipSnapshot_InProgressWithoutSnapshot_Fails()
    {
        SeedPeriod("202606", PeriodStatus.Closing);
        SeedWo("WO001", WorkOrderStatus.InProgress);

        var failures = await _svc.AuditWipSnapshotConsistencyAsync("202606", Plant);

        var f = Assert.Single(failures);
        Assert.Equal("WIP_SNAPSHOT_MISSING", f.AuditType);
        Assert.Equal("WO001", f.DocId);
    }

    [Fact]
    public async Task WipSnapshot_SnapshotForNonInProgressWo_Fails()
    {
        // 重估後工單又完工（Closed）→ 快照過期
        SeedPeriod("202606", PeriodStatus.Closing);
        SeedWo("WO001", WorkOrderStatus.Closed);
        SeedWipSnapshot("202606", "WO001", material: 100m, labor: 50m, overhead: 30m);

        var failures = await _svc.AuditWipSnapshotConsistencyAsync("202606", Plant);

        var f = Assert.Single(failures);
        Assert.Equal("WIP_SNAPSHOT_STALE", f.AuditType);
    }

    [Fact]
    public async Task WipSnapshot_NegativeComponent_Fails()
    {
        SeedPeriod("202606", PeriodStatus.Closing);
        SeedWo("WO001", WorkOrderStatus.InProgress);
        SeedWipSnapshot("202606", "WO001", material: 100m, labor: -5m, overhead: 30m);

        var failures = await _svc.AuditWipSnapshotConsistencyAsync("202606", Plant);

        var f = Assert.Single(failures);
        Assert.Equal("WIP_SNAPSHOT_NEGATIVE", f.AuditType);
    }

    [Fact]
    public async Task WipCost_PreliminaryInPeriod_Fails()
    {
        // 期末重估未跑：期間內完工成本仍 Preliminary
        SeedPeriod("202606", PeriodStatus.Closing);
        SeedWoCost("WO001", CostStatus.Preliminary, new DateOnly(2026, 6, 20));

        var failures = await _svc.AuditWipSnapshotConsistencyAsync("202606", Plant);

        var f = Assert.Single(failures);
        Assert.Equal("WIP_COST_NOT_FINAL", f.AuditType);
    }

    [Fact]
    public async Task WipCost_PreliminaryOutsidePeriod_NotReported()
    {
        // 下期完工的 Preliminary 不屬本期關帳範圍
        SeedPeriod("202606", PeriodStatus.Closing);
        SeedWoCost("WO001", CostStatus.Preliminary, new DateOnly(2026, 7, 2));

        var failures = await _svc.AuditWipSnapshotConsistencyAsync("202606", Plant);

        Assert.Empty(failures);
    }

    [Fact]
    public async Task ClosePeriod_WipSnapshotMissing_Blocked()
    {
        // §4.1 關帳擋關涵蓋 §3.10：在製工單無本期快照即擋
        SeedPeriod("202606", PeriodStatus.Closing);
        SeedWo("WO001", WorkOrderStatus.InProgress);
        var periodSvc = new AccountingPeriodService(_context, _svc);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => periodSvc.ClosePeriodAsync("202606", Plant, "finance_mgr"));
        Assert.Equal(PeriodStatus.Closing, (await _context.AccountingPeriods.FindAsync("202606", Plant))!.Status);
    }

    // ── §9 P-7 容忍值系統參數 ───────────────────────────────────────────

    [Fact]
    public async Task Tolerance_AmountParamOverride_Applied()
    {
        // 借貸差 0.05 在預設 0.01 下為異常；audit_amount_tolerance 放寬為 0.1 後不報
        SeedJournal("JE001", "APPayment", "PAY001", debit: 100.05m, credit: 100m);
        var mockParams = new Moq.Mock<SmartFactory.MES.Services.ISystemParamService>();
        mockParams.Setup(p => p.GetAsync(ProjectZSystemParamKeys.AuditAmountTolerance)).ReturnsAsync("0.1");
        var svc = new DataAuditService(_context, mockParams.Object);

        Assert.Empty(await svc.AuditJournalBalanceAsync(Plant));
        Assert.Single(await _svc.AuditJournalBalanceAsync(Plant));   // 預設仍報（行為不變）
    }

    [Fact]
    public async Task Tolerance_QtyParamOverride_Applied()
    {
        // 超收 0.001 在預設 0.0001 下為異常；audit_qty_tolerance 放寬為 0.01 後不報
        SeedPoLine("PO001", 1, "MAT-A", orderQty: 100m);
        SeedGrLine("GR001", DocStatus.Posted, "PO001", 1, receiptQty: 100.001m);
        var mockParams = new Moq.Mock<SmartFactory.MES.Services.ISystemParamService>();
        mockParams.Setup(p => p.GetAsync(ProjectZSystemParamKeys.AuditQtyTolerance)).ReturnsAsync("0.01");
        var svc = new DataAuditService(_context, mockParams.Object);

        Assert.Empty(await svc.AuditPoOverReceiptAsync(Plant));
        Assert.Single(await _svc.AuditPoOverReceiptAsync(Plant));
    }

    [Fact]
    public async Task Tolerance_InvalidParamValue_FallsBackToDefault()
    {
        // 參數值不合法（非數字/非正數）→ 退回預設，異常仍報
        SeedJournal("JE001", "APPayment", "PAY001", debit: 100.05m, credit: 100m);
        var mockParams = new Moq.Mock<SmartFactory.MES.Services.ISystemParamService>();
        mockParams.Setup(p => p.GetAsync(ProjectZSystemParamKeys.AuditAmountTolerance)).ReturnsAsync("abc");
        var svc = new DataAuditService(_context, mockParams.Object);

        Assert.Single(await svc.AuditJournalBalanceAsync(Plant));
    }

    // ── 廠別範圍 ────────────────────────────────────────────────────────

    [Fact]
    public async Task PlantScope_FailureInOtherPlant_NotReported()
    {
        SeedJournal("JE001", "APPayment", "PAY001", debit: 100m, credit: 50m, plantId: "P999");

        Assert.Empty(await _svc.AuditJournalBalanceAsync(Plant));
        Assert.Single(await _svc.AuditJournalBalanceAsync(plantId: null)); // 全廠稽核仍可見
    }

    // ── §4 關帳擋關整合 ─────────────────────────────────────────────────

    [Fact]
    public async Task ClosePeriod_AuditFails_ThrowsAndStaysClosing()
    {
        SeedPeriod("202606", PeriodStatus.Closing);
        SeedJournal("JE001", "APPayment", "PAY001", debit: 100m, credit: 50m);
        var periodSvc = new AccountingPeriodService(_context, _svc);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => periodSvc.ClosePeriodAsync("202606", Plant, "finance_mgr"));

        PzAssert.Message("Act_PreCloseAuditFailedFmt", ex.Message);
        Assert.Equal(PeriodStatus.Closing, (await _context.AccountingPeriods.FindAsync("202606", Plant))!.Status);
    }

    [Fact]
    public async Task ClosePeriod_AuditPasses_Closes()
    {
        SeedPeriod("202606", PeriodStatus.Closing);
        // 傳票的來源單據必須真的存在，否則孤兒過帳稽核（已掛進 RunAllAsync）會擋關帳。
        // 這不是為了讓測試過：一張指向不存在單據的傳票本來就該擋，
        // 原本的種子只是在稽核還沒掛進來時看不出缺這一筆。
        _context.APPayments.Add(new SmartFactory.MES.Modules.FIN.Models.APPayment
        {
            APPaymentId = "PAY001", PaymentDate = new DateOnly(2026, 6, 15),
            SupplierCode = "S001", Amount = 100m, AmountLocal = 100m, PlantId = Plant,
        });
        _context.SaveChanges();
        SeedJournal("JE001", "APPayment", "PAY001", debit: 100m, credit: 100m);
        var periodSvc = new AccountingPeriodService(_context, _svc);

        await periodSvc.ClosePeriodAsync("202606", Plant, "finance_mgr");

        Assert.Equal(PeriodStatus.Closed, (await _context.AccountingPeriods.FindAsync("202606", Plant))!.Status);
    }

    [Fact]
    public async Task ClosePeriod_NoAuditService_ClosesWithoutAudit()
    {
        // 未注入稽核服務時維持原行為（既有測試/部署相容）
        SeedPeriod("202606", PeriodStatus.Closing);
        SeedJournal("JE001", "APPayment", "PAY001", debit: 100m, credit: 50m);
        var periodSvc = new AccountingPeriodService(_context);

        await periodSvc.ClosePeriodAsync("202606", Plant, "finance_mgr");

        Assert.Equal(PeriodStatus.Closed, (await _context.AccountingPeriods.FindAsync("202606", Plant))!.Status);
    }

    // ── §3.11 逐工單成本回溯（P-2；D2-A Final 腿；D1-A 材料腿改子帳）────────
    // SeedWoCost 預設 ActualMaterial=100 / Labor=50 / Overhead=30。
    // 吸收腿以吸收科目（5110/5120）淨額核（SeedAbsorptionJe / SeedReestAdjustment）；
    // 材料腿以庫存異動子帳淨成本核（SeedMaterialMovement，含 Push/Pull 發料單 SeedPostedSlip）。

    [Fact]
    public async Task WoCostJournalDrift_BothLegsMatch_NoFindings()
    {
        SeedWoCost("WO-J1", CostStatus.Preliminary, new DateOnly(2026, 6, 15));
        SeedMaterialMovement("WO-J1-BF", cost: 100m);                        // 材料腿子帳 100 = 100
        SeedAbsorptionJe("JEC1", "WO-J1", labor: 50m, overhead: 30m);        // 吸收腿 80 = 50+30

        Assert.Empty(await _svc.AuditWoCostJournalDriftAsync(Plant));
    }

    [Fact]
    public async Task WoCostJournalDrift_MaterialLegDrift_Reported()
    {
        SeedWoCost("WO-J2", CostStatus.Preliminary, new DateOnly(2026, 6, 15));
        SeedMaterialMovement("WO-J2-BF", cost: 90m);                         // 90 ≠ 100
        SeedAbsorptionJe("JEC1", "WO-J2", labor: 50m, overhead: 30m);        // 吸收腿吻合，僅材料漂移

        var f = Assert.Single(await _svc.AuditWoCostJournalDriftAsync(Plant));
        Assert.Equal("WO_COST_JOURNAL_DRIFT", f.AuditType);
        Assert.Equal("WO-J2", f.DocId);
        Assert.Equal(-10m, f.DifferenceAmount);
        Assert.Contains("材料腿", f.ErrorDescription);
    }

    [Fact]
    public async Task WoCostJournalDrift_MissingMaterialMovement_Reported()
    {
        SeedWoCost("WO-J3", CostStatus.Preliminary, new DateOnly(2026, 6, 15));
        SeedAbsorptionJe("JEC1", "WO-J3", labor: 50m, overhead: 30m);        // 吸收腿吻合
        // ActualMaterial=100 但無任何生產領料異動 → 材料腿差 −100

        var f = Assert.Single(await _svc.AuditWoCostJournalDriftAsync(Plant));
        Assert.Equal(-100m, f.DifferenceAmount);
        Assert.Contains("材料腿", f.ErrorDescription);
    }

    [Fact]
    public async Task WoCostJournalDrift_MaterialLegIncludesPushPullSlips_NoFindings()
    {
        // D1-A：材料腿子帳 = 後沖(WO-BF) ＋ Push/Pull 發料單(MaterialSlip)。
        // 後沖 40 ＋ 發料單 60 = 100 = ActualMaterial。
        SeedWoCost("WO-J7", CostStatus.Preliminary, new DateOnly(2026, 6, 15));
        SeedMaterialMovement("WO-J7-BF", cost: 40m);                        // 後沖
        SeedPostedSlip("SLIP-J7", "WO-J7");
        SeedMaterialMovement("SLIP-J7", cost: 60m);                        // Push/Pull 發料
        SeedAbsorptionJe("JEC1", "WO-J7", labor: 50m, overhead: 30m);

        Assert.Empty(await _svc.AuditWoCostJournalDriftAsync(Plant));
    }

    [Fact]
    public async Task WoCostJournalDrift_FinalReconcilesViaReestAdjustment_NoFindings()
    {
        // D2-A：Final 記錄的 Labor+Overhead(=80) 已含重估差額。吸收腿＝完工吸收(65)＋
        // 重估調整逐工單拆行(15，SourceRef=工單) = 80，對得起來 → 無異常。
        SeedWoCost("WO-J4", CostStatus.Final, new DateOnly(2026, 6, 15));
        SeedMaterialMovement("WO-J4-BF", cost: 100m);                       // 材料腿吻合
        SeedAbsorptionJe("JEC1", "WO-J4", labor: 40m, overhead: 25m);        // 完工吸收 65
        SeedReestAdjustment("JER1", "WO-J4", laborDelta: 10m, overheadDelta: 5m);  // 調整 +15 → 80

        Assert.Empty(await _svc.AuditWoCostJournalDriftAsync(Plant));
    }

    [Fact]
    public async Task WoCostJournalDrift_FinalWithoutReestAdjustment_Reported()
    {
        // 舊行為（跳過 Final）已改：Final 若缺重估調整分錄，吸收腿 65 ≠ 80 → 揪出。
        SeedWoCost("WO-J6", CostStatus.Final, new DateOnly(2026, 6, 15));
        SeedMaterialMovement("WO-J6-BF", cost: 100m);
        SeedAbsorptionJe("JEC1", "WO-J6", labor: 40m, overhead: 25m);        // 65，無重估調整

        var f = Assert.Single(await _svc.AuditWoCostJournalDriftAsync(Plant));
        Assert.Equal(-15m, f.DifferenceAmount);
        Assert.Contains("人工/製費腿", f.ErrorDescription);
    }

    [Fact]
    public async Task WoCostJournalDrift_PreliminaryConversionDrift_Reported()
    {
        SeedWoCost("WO-J5", CostStatus.Preliminary, new DateOnly(2026, 6, 15));
        SeedMaterialMovement("WO-J5-BF", cost: 100m);
        SeedAbsorptionJe("JEC1", "WO-J5", labor: 40m, overhead: 30m);        // 70 ≠ 80

        var f = Assert.Single(await _svc.AuditWoCostJournalDriftAsync(Plant));
        Assert.Equal(-10m, f.DifferenceAmount);
        Assert.Contains("人工/製費腿", f.ErrorDescription);
    }

    // ── Seed helpers ────────────────────────────────────────────────────

    /// <summary>生產領料異動（TypeCategory=Production，UnitCost=1、StockQty=cost、方向 −1 領料）。</summary>
    private void SeedMaterialMovement(string docNo, decimal cost, int direction = -1)
    {
        _context.StockMovements.Add(new StockMovement
        {
            DocNo = docNo, DocLineNo = 1, TypeCode = direction < 0 ? "IO" : "IR", TypeCategory = "Production",
            PlantId = Plant, ProductCode = "RM-1", LocationCode = "L1", LotNo = "",
            StockQty = cost, Qty = cost, StockUomCode = "PCS", UomCode = "PCS",
            Direction = direction, UnitCost = 1m, TotalCost = cost,
            MovementDate = new DateOnly(2026, 6, 15),
        });
        _context.SaveChanges();
    }

    /// <summary>已過帳工單發料單（MaterialSlip，SourceDocType=WorkOrder）。</summary>
    private void SeedPostedSlip(string slipId, string woId)
    {
        _context.MaterialSlips.Add(new MaterialSlip
        {
            SlipId = slipId, SourceDocType = "WorkOrder", SourceDocId = woId,
            Status = MaterialSlipStatus.Posted, PlantId = Plant,
        });
        _context.SaveChanges();
    }

    /// <summary>完工吸收分錄（WorkOrderCompletion）：借在製、貸 5110 人工/5120 製費已分配。</summary>
    private void SeedAbsorptionJe(string journalId, string woId, decimal labor, decimal overhead)
    {
        _context.JournalEntries.Add(new JournalEntry
        {
            JournalId = journalId, PeriodId = "202606", EntryDate = new DateOnly(2026, 6, 15),
            SourceDocType = "WorkOrderCompletion", SourceDocId = woId, PlantId = Plant,
            Lines =
            {
                new JournalLine { JournalId = journalId, LineNo = 1, AccountCode = "1401", AccountName = "在製", Debit = labor + overhead, Credit = 0m },
                new JournalLine { JournalId = journalId, LineNo = 2, AccountCode = SmartFactory.MES.Modules.ACT.StandardAccounts.DirectLaborAbsorbedCode, AccountName = "人工-已分配", Debit = 0m, Credit = labor },
                new JournalLine { JournalId = journalId, LineNo = 3, AccountCode = SmartFactory.MES.Modules.ACT.StandardAccounts.MfgOverheadAbsorbedCode, AccountName = "製費-已分配", Debit = 0m, Credit = overhead },
            },
        });
        _context.SaveChanges();
    }

    /// <summary>期末重估調整分錄（ActualRateReest，表頭為期間）：逐工單拆行、行帶 SourceRef=工單號（D2-A）。</summary>
    private void SeedReestAdjustment(string journalId, string woId, decimal laborDelta, decimal overheadDelta)
    {
        var laborCode = SmartFactory.MES.Modules.ACT.StandardAccounts.DirectLaborAbsorbedCode;
        var ovhCode   = SmartFactory.MES.Modules.ACT.StandardAccounts.MfgOverheadAbsorbedCode;
        var entry = new JournalEntry
        {
            JournalId = journalId, PeriodId = "202606", EntryDate = new DateOnly(2026, 6, 30),
            SourceDocType = "ActualRateReest", SourceDocId = "202606", PlantId = Plant,
        };
        entry.Lines.Add(laborDelta >= 0
            ? new JournalLine { JournalId = journalId, LineNo = 1, AccountCode = laborCode, AccountName = "人工-已分配", Debit = 0m, Credit = laborDelta, SourceRef = woId }
            : new JournalLine { JournalId = journalId, LineNo = 1, AccountCode = laborCode, AccountName = "人工-已分配", Debit = -laborDelta, Credit = 0m, SourceRef = woId });
        entry.Lines.Add(overheadDelta >= 0
            ? new JournalLine { JournalId = journalId, LineNo = 2, AccountCode = ovhCode, AccountName = "製費-已分配", Debit = 0m, Credit = overheadDelta, SourceRef = woId }
            : new JournalLine { JournalId = journalId, LineNo = 2, AccountCode = ovhCode, AccountName = "製費-已分配", Debit = -overheadDelta, Credit = 0m, SourceRef = woId });
        _context.JournalEntries.Add(entry);
        _context.SaveChanges();
    }

    private void SeedPeriod(string periodId, PeriodStatus status)
    {
        _context.AccountingPeriods.Add(new AccountingPeriod
        {
            PeriodId = periodId, PlantId = Plant, Year = 2026, Month = 6,
            StartDate = new DateOnly(2026, 6, 1), EndDate = new DateOnly(2026, 6, 30),
            Status = status,
        });
        _context.SaveChanges();
    }

    private void SeedPoLine(string poId, int lineNo, string productCode, decimal orderQty, decimal receivedQty = 0m)
    {
        if (_context.PurchaseOrders.Find(poId) == null)
            _context.PurchaseOrders.Add(new PurchaseOrder { PurchaseOrderId = poId, SupplierCode = "S001", PlantId = Plant });
        _context.PurchaseOrderLines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = poId, LineNo = lineNo, ProductCode = productCode, OrderQty = orderQty,
            ReceivedQty = receivedQty,
        });
        _context.SaveChanges();
    }

    private void SeedWo(string woId, WorkOrderStatus status)
    {
        _context.WorkOrders.Add(new WorkOrder
        { WorkOrderId = woId, RoutingId = "RT001", ProductCode = "FG-A", Quantity = 10m, Status = status, PlantId = Plant });
        _context.SaveChanges();
    }

    private void SeedWipSnapshot(string periodId, string woId, decimal material, decimal labor, decimal overhead)
    {
        _context.WipSnapshots.Add(new WipSnapshot
        {
            PeriodId = periodId, WorkOrderId = woId,
            WipMaterial = material, WipLabor = labor, WipOverhead = overhead,
            QtyCompleted = 0m, QtyInProcess = 10m, PlantId = Plant,
        });
        _context.SaveChanges();
    }

    private void SeedWoCost(string woId, CostStatus status, DateOnly costDate)
    {
        _context.WorkOrderActualCosts.Add(new WorkOrderActualCost
        {
            WorkOrderId = woId, Status = status, CostDate = costDate, PlantId = Plant,
            ActualMaterial = 100m, ActualLabor = 50m, ActualOverhead = 30m,
        });
        _context.SaveChanges();
    }

    /// <summary>§3.10-5 恆等式投入腿：月結輸入的部門實際人工/製費（費率重估分子）。</summary>
    private void SeedDeptCost(string periodId, string wc, decimal labor, decimal overhead)
    {
        _context.DepartmentActualCosts.Add(new DepartmentActualCost
        {
            PeriodId = periodId, WorkCenterCode = wc,
            ActualLaborCost = labor, ActualOverheadCost = overhead, PlantId = Plant,
        });
        _context.SaveChanges();
    }

    /// <summary>§3.10-5 恆等式發料腿：工單生產發料異動（DocNo=工單-BF 或發料單號，出庫 Direction=-1）。</summary>
    private void SeedProductionMovement(string docNo, DateOnly date, decimal qty, decimal unitCost, int direction = -1)
    {
        _context.StockMovements.Add(new StockMovement
        {
            TypeCode = "IO", TypeCategory = "Production", DocNo = docNo,
            MovementDate = date, PlantId = Plant, ProductCode = "MAT-A", LocationCode = "L1",
            Qty = qty, StockQty = qty, Direction = direction, UnitCost = unitCost,
        });
        _context.SaveChanges();
    }

    /// <summary>後沖全鏈種子：Closed 工單 + Backflush/AtCompletion 用料 + IT 繳庫 + IO 後沖（DocNo = 工單 + "-BF"）。</summary>
    private void SeedBackflushChain(decimal woQty, decimal requiredQty, decimal completedQty,
        decimal backflushedQty, WorkOrderStatus status = WorkOrderStatus.Closed)
    {
        const string wo = "WO-BF1", mat = "MAT-A";
        _context.WorkOrders.Add(new WorkOrder
        { WorkOrderId = wo, RoutingId = "RT001", ProductCode = "FG-A", Quantity = woQty, Status = status, PlantId = Plant });
        _context.WorkOrderOperations.Add(new WorkOrderOperation
        { WorkOrderId = wo, OperationSeq = 10, OperationName = "OP10" });
        _context.WorkOrderMaterials.Add(new WorkOrderMaterial
        {
            WorkOrderId = wo, OperationSeq = 10, BomSeq = 1, ProductCode = mat,
            RequiredQty = requiredQty, UomCode = "PC",
            IssueMethod = MaterialIssueMethod.Backflush, BackflushTiming = BackflushTiming.AtCompletion,
        });
        if (completedQty > 0)
            SeedMovement(wo, "FG-A", "L01", qty: completedQty, direction: 1, typeCode: "IT");
        if (backflushedQty > 0)
            SeedMovement(wo + "-BF", mat, "L01", qty: backflushedQty, direction: -1, typeCode: "IO");
        _context.SaveChanges();
    }

    /// <summary>已過帳／草稿的採購退貨，掛在 grId 的第 1 行上。</summary>
    private void SeedPurchaseReturn(string grtId, string grId, decimal qty, DocStatus status)
    {
        var grt = new GoodsReturn
        {
            GoodsReturnId = grtId, ReturnDate = DateOnly.FromDateTime(DateTime.UtcNow),
            SupplierCode = "S001", PlantId = Plant, Status = status,
        };
        grt.Lines.Add(new GoodsReturnLine
        {
            GoodsReturnId = grtId, LineNo = 1, ProductCode = "MAT-A",
            GoodsReceiptId = grId, ReceiptLineNo = 1, ReturnQty = qty,
        });
        _context.GoodsReturns.Add(grt);
        _context.SaveChanges();
    }

    private void SeedGrLine(string grId, DocStatus status, string poId, int poLineNo,
        decimal receiptQty, decimal returnedQty = 0m, decimal lineAmount = 0m, DateOnly? receiptDate = null,
        decimal unitPrice = 0m)
    {
        if (_context.GoodsReceipts.Find(grId) == null)
            _context.GoodsReceipts.Add(new GoodsReceipt
            {
                GoodsReceiptId = grId, SupplierCode = "S001", Status = status, PlantId = Plant,
                ReceiptDate = receiptDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            });
        _context.GoodsReceiptLines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = grId, LineNo = 1, ProductCode = "MAT-A",
            PurchaseOrderId = poId, PoLineNo = poLineNo,
            ReceiptQty = receiptQty, ReturnedQty = returnedQty, LineAmount = lineAmount,
            UnitPrice = unitPrice,
        });
        _context.SaveChanges();
    }

    private void SeedApInvoice(string apId, string sourceGrId, decimal qty, decimal lineAmount,
        APInvoiceStatus status = APInvoiceStatus.Open,
        ErpSyncStatus erpStatus = ErpSyncStatus.Synced, DateTime? createdAt = null)
    {
        _context.APInvoices.Add(new APInvoice
        {
            APInvoiceId = apId, SupplierCode = "S001", PlantId = Plant, Status = status,
            SourceDocType = "GoodsReceipt", SourceDocId = sourceGrId,
            TotalAmount = lineAmount, ErpSyncStatus = erpStatus,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            Lines = { new APInvoiceLine { APInvoiceId = apId, LineNo = 1, Qty = qty, LineAmount = lineAmount } },
        });
        _context.SaveChanges();
    }

    /// <summary>委外全鏈種子：OO 發料（Posted）＋OR 退料（Posted）＋OPO/收貨＋RoutingBom 用量。</summary>
    private void SeedOutsourceChain(decimal issuedQty, decimal returnedQty, decimal receiptQty, decimal bomUsagePerUnit)
    {
        const string wo = "WO001", routing = "RT001", mat = "MAT-A", opo = "OPO001";

        _context.WorkOrders.Add(new WorkOrder { WorkOrderId = wo, RoutingId = routing, ProductCode = "FG-A", PlantId = Plant });
        _context.RoutingBoms.Add(new RoutingBom { RoutingId = routing, OperationSeq = 10, BomSeq = 1, ProductCode = mat, Quantity = bomUsagePerUnit });

        _context.OutsourceMaterialDocs.Add(new OutsourceMaterialDoc
        { OutsourceMaterialDocId = "OO001", TypeCode = "OO", Status = DocStatus.Posted, WorkOrderCode = wo, SupplierCode = "S001", PlantId = Plant });
        SeedMovement("OO001", mat, "L01", qty: issuedQty, direction: -1, typeCode: "OO");

        if (returnedQty > 0)
        {
            _context.OutsourceMaterialDocs.Add(new OutsourceMaterialDoc
            { OutsourceMaterialDocId = "OR001", TypeCode = "OR", Status = DocStatus.Posted, WorkOrderCode = wo, SupplierCode = "S001", PlantId = Plant });
            SeedMovement("OR001", mat, "L01", qty: returnedQty, direction: 1, typeCode: "OR");
        }

        _context.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
        { OutsourcePurchaseOrderId = opo, LineNo = 1, SourceWorkOrderId = wo, ProductCode = "FG-A" });
        if (receiptQty > 0)
        {
            _context.OutsourceReceipts.Add(new OutsourceReceipt
            { OutsourceReceiptId = "ORC001", OutsourcePurchaseOrderId = opo, SupplierCode = "S001", Status = DocStatus.Posted, PlantId = Plant });
            _context.OutsourceReceiptLines.Add(new OutsourceReceiptLine
            { OutsourceReceiptId = "ORC001", LineNo = 1, OpoLineNo = 1, ProductCode = "FG-A", ReceiptQty = receiptQty, StockQty = receiptQty });
        }
        _context.SaveChanges();
    }

    private void SeedJournal(string journalId, string sourceDocType, string sourceDocId,
        decimal debit, decimal credit, string plantId = Plant)
    {
        _context.JournalEntries.Add(new JournalEntry
        {
            JournalId = journalId, PeriodId = "202606", EntryDate = new DateOnly(2026, 6, 15),
            SourceDocType = sourceDocType, SourceDocId = sourceDocId, PlantId = plantId,
            Lines =
            {
                new JournalLine { JournalId = journalId, LineNo = 1, AccountCode = "1130", AccountName = "銀行存款", Debit = debit, Credit = 0m },
                new JournalLine { JournalId = journalId, LineNo = 2, AccountCode = "2100", AccountName = "應付帳款", Debit = 0m, Credit = credit },
            },
        });
        _context.SaveChanges();
    }

    private void SeedMovement(string docNo, string productCode, string locationCode,
        decimal qty, int direction, string typeCode = "PI")
    {
        _context.StockMovements.Add(new StockMovement
        {
            DocNo = docNo, DocLineNo = 1, TypeCode = typeCode, PlantId = Plant,
            ProductCode = productCode, LocationCode = locationCode, LotNo = "",
            StockQty = qty, Qty = qty, Direction = direction,
            MovementDate = DateOnly.FromDateTime(DateTime.UtcNow),
        });
        _context.SaveChanges();
    }

    private void SeedInventory(string productCode, string locationCode, decimal qtyOnHand)
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = Plant, ProductCode = productCode, LocationCode = locationCode, LotNo = "",
            QtyOnHand = qtyOnHand,
        });
        _context.SaveChanges();
    }

    private void SeedSoLine(string soId, int lineNo, string productCode, decimal orderQty, decimal shippedQty = 0m)
    {
        if (_context.SalesOrders.Find(soId) == null)
            _context.SalesOrders.Add(new SalesOrder { SalesOrderId = soId, CompanyCode = "C001", PlantId = Plant });
        _context.SalesOrderLines.Add(new SalesOrderLine
        { SalesOrderId = soId, LineNo = lineNo, ProductCode = productCode, OrderQty = orderQty, ShippedQty = shippedQty });
        _context.SaveChanges();
    }

    private void SeedPeriod(string periodId, DateOnly start, DateOnly end)
    {
        _context.AccountingPeriods.Add(new Modules.ACT.Models.AccountingPeriod
        {
            PeriodId = periodId, PlantId = Plant,
            Year = start.Year, Month = start.Month,
            StartDate = start, EndDate = end,
        });
        _context.SaveChanges();
    }

    private void SeedGsLine(string gsId, DocStatus status, string soId, int soLineNo,
        decimal shipQty, DateOnly? shipDate = null)
    {
        if (_context.GoodsShipments.Find(gsId) == null)
            _context.GoodsShipments.Add(new GoodsShipment
            {
                GoodsShipmentId = gsId, CompanyCode = "C001", Status = status, PlantId = Plant,
                ShipDate = shipDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            });
        _context.GoodsShipmentLines.Add(new GoodsShipmentLine
        {
            GoodsShipmentId = gsId, LineNo = 1, ProductCode = "FG-A",
            SalesOrderId = soId, SoLineNo = soLineNo, ShipQty = shipQty,
        });
        _context.SaveChanges();
    }

    private void SeedArInvoice(string arId, string sourceGsId, ARInvoiceStatus status = ARInvoiceStatus.Open,
        ErpSyncStatus erpStatus = ErpSyncStatus.Synced, DateTime? createdAt = null)
    {
        _context.ARInvoices.Add(new ARInvoice
        {
            ARInvoiceId = arId, CustomerCode = "C001", PlantId = Plant, Status = status,
            SourceDocType = "GoodsShipment", SourceDocId = sourceGsId,
            ErpSyncStatus = erpStatus, CreatedAt = createdAt ?? DateTime.UtcNow,
        });
        _context.SaveChanges();
    }
}
