using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Models.Jobs;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.RPS.Models;
using SmartFactory.MES.Modules.RPT.Models;

namespace SmartFactory.MES.Data
{
    /// <summary>
    /// 顧問模組對 ApplicationDbContext 的 partial class 擴充。
    /// 新增 BAS / PUR 相關 DbSet 與 Model 設定，Core 的 ApplicationDbContext.cs 完全不需異動。
    /// </summary>
    public partial class ApplicationDbContext
    {
        // === BAS Module DbSets ===
        public DbSet<Company> Companies { get; set; }
        public DbSet<CompanyContact> CompanyContacts { get; set; }
        public DbSet<Currency> Currencies { get; set; }
        public DbSet<CurrencyRate> CurrencyRates { get; set; }
        public DbSet<Container> Containers { get; set; }
        public DbSet<ContainerUnit> ContainerUnits { get; set; }
        public DbSet<ContainerUnitHistory> ContainerUnitHistories { get; set; }
        public DbSet<InventoryTransactionType> InventoryTransactionTypes { get; set; }
        public DbSet<Tooling> Toolings { get; set; }
        public DbSet<Product> Products { get; set; }
        public DbSet<ProductCategory> ProductCategories { get; set; }
        public DbSet<ProductCategoryTag> ProductCategoryTags { get; set; }
        public DbSet<ChartOfAccount> ChartOfAccounts { get; set; }
        public DbSet<SpecialDay> SpecialDays { get; set; }
        public DbSet<Shift> Shifts { get; set; }
        public DbSet<ShiftGroup> ShiftGroups { get; set; }
        public DbSet<ShiftGroupDay> ShiftGroupDays { get; set; }
        public DbSet<Tax> Taxes { get; set; }
        public DbSet<Uom> Uoms { get; set; }
        public DbSet<UomConversion> UomConversions { get; set; }
        public DbSet<ProductUomConversion> ProductUomConversions { get; set; }
        public DbSet<WorkCenter> WorkCenters { get; set; }
        public DbSet<WorkCenterMachine> WorkCenterMachines { get; set; }
        public DbSet<Warehouse> Warehouses { get; set; }
        public DbSet<StorageLocation> StorageLocations { get; set; }
        public DbSet<DocNumberConfig> DocNumberConfigs { get; set; }
        public DbSet<DocNumberSequence> DocNumberSequences { get; set; }

        // === INV Module DbSets ===
        public DbSet<Inventory> Inventories { get; set; }
        public DbSet<StockMovement> StockMovements { get; set; }
        public DbSet<InventoryCostLayer> InventoryCostLayers { get; set; }
        public DbSet<InventoryCostLayerConsumption> InventoryCostLayerConsumptions { get; set; }
        public DbSet<InventoryReservation> InventoryReservations { get; set; }
        // CST §3.4：採購分攤費用（Landed Cost）分攤單
        public DbSet<LandedCostAllocation> LandedCostAllocations { get; set; }
        public DbSet<LandedCostAllocationLine> LandedCostAllocationLines { get; set; }

        // === PUR Module DbSets ===
        public DbSet<PurchaseRequest> PurchaseRequests { get; set; }
        public DbSet<PurchaseRequestLine> PurchaseRequestLines { get; set; }
        public DbSet<PurchaseOrder> PurchaseOrders { get; set; }
        public DbSet<PurchaseOrderLine> PurchaseOrderLines { get; set; }
        public DbSet<GoodsReceipt> GoodsReceipts { get; set; }
        public DbSet<GoodsReceiptLine> GoodsReceiptLines { get; set; }
        public DbSet<GoodsReturn> GoodsReturns { get; set; }
        public DbSet<GoodsReturnLine> GoodsReturnLines { get; set; }
        public DbSet<GoodsReceiptLocationDetail> GoodsReceiptLocationDetails { get; set; }
        public DbSet<GoodsReturnLocationDetail> GoodsReturnLocationDetails { get; set; }
        public DbSet<VendorPriceList> VendorPriceLists { get; set; }
        public DbSet<VendorEvaluation> VendorEvaluations { get; set; } // Phase G+：合格供應商評分卡（SRM）
        // PUR Phase A：委外完工收貨
        public DbSet<OutsourceReceipt> OutsourceReceipts { get; set; }
        public DbSet<OutsourceReceiptLine> OutsourceReceiptLines { get; set; }
        public DbSet<OutsourceReceiptLocationDetail> OutsourceReceiptLocationDetails { get; set; }
        public DbSet<OutsourceRework> OutsourceReworks { get; set; } // Phase F：委外重工單

        // === FIN Module DbSets (Phase A) ===
        public DbSet<APInvoice> APInvoices { get; set; }
        public DbSet<APInvoiceLine> APInvoiceLines { get; set; }
        public DbSet<ARInvoice> ARInvoices { get; set; }
        public DbSet<ARInvoiceLine> ARInvoiceLines { get; set; }
        public DbSet<APPayment> APPayments { get; set; }
        public DbSet<APPaymentAllocation> APPaymentAllocations { get; set; }
        public DbSet<ARReceipt> ARReceipts { get; set; }
        public DbSet<ARCreditNote> ARCreditNotes { get; set; }
        public DbSet<ARCreditNoteLine> ARCreditNoteLines { get; set; }
        public DbSet<APDebitNote> APDebitNotes { get; set; }
        public DbSet<APDebitNoteLine> APDebitNoteLines { get; set; }
        public DbSet<ARReceiptAllocation> ARReceiptAllocations { get; set; }
        public DbSet<BankAccount> BankAccounts { get; set; }

        // === ACT Module DbSets (Phase C) ===
        public DbSet<AccountingPeriod>      AccountingPeriods     { get; set; }
        public DbSet<JournalEntry>          JournalEntries        { get; set; }
        public DbSet<JournalLine>           JournalLines          { get; set; }
        public DbSet<InventoryNrvAssessment> InventoryNrvAssessments { get; set; }
        public DbSet<FxRevaluationEntry>    FxRevaluationEntries  { get; set; }
        public DbSet<EclAllowance>          EclAllowances         { get; set; }
        public DbSet<EclLossRate>           EclLossRates          { get; set; }
        public DbSet<MonthEndChecklist>     MonthEndChecklists    { get; set; }
        public DbSet<ChecklistItem>         ChecklistItems        { get; set; }
        // §9 P-8：資料稽核執行紀錄（每日排程 / 儀表板手動觸發）
        public DbSet<DataAuditRun>          DataAuditRuns         { get; set; }

        // === 每日排程執行紀錄（跨模組共用，見 Models/Jobs/ScheduledJobRun.cs）===
        public DbSet<ScheduledJobRun>       ScheduledJobRuns      { get; set; }
        public DbSet<DataAuditFinding>      DataAuditFindings     { get; set; }

        // 現場裝置遙測（前端錯誤、API 耗時、斷線、手機佇列）；寫入走 ClientTelemetryWriter 的 SQL，不經 SaveChanges
        public DbSet<ClientTelemetryEvent>  ClientTelemetryEvents { get; set; }

        // === INV Phase 2 Module DbSets ===
        public DbSet<LoanDoc> LoanDocs { get; set; }
        public DbSet<LoanDocLine> LoanDocLines { get; set; }
        public DbSet<LoanDocLocationDetail> LoanDocLocationDetails { get; set; }
        public DbSet<SampleDoc> SampleDocs { get; set; }
        public DbSet<SampleDocLine> SampleDocLines { get; set; }
        public DbSet<SampleDocLocationDetail> SampleDocLocationDetails { get; set; }
        public DbSet<ScrapDoc> ScrapDocs { get; set; }
        public DbSet<ScrapDocLine> ScrapDocLines { get; set; }
        public DbSet<ScrapDocLocationDetail> ScrapDocLocationDetails { get; set; }
        public DbSet<MaterialStagingOrder> MaterialStagingOrders { get; set; }
        public DbSet<MaterialStagingLine> MaterialStagingLines { get; set; }

        // === MFG Module DbSets ===
        public DbSet<Routing> Routings { get; set; }
        public DbSet<RoutingOperation> RoutingOperations { get; set; }
        public DbSet<RoutingBom> RoutingBoms { get; set; }
        public DbSet<WorkCalendar> WorkCalendars { get; set; }
        public DbSet<WorkCalendarSegment> WorkCalendarSegments { get; set; }
        public DbSet<PlannedDowntime> PlannedDowntimes { get; set; }
        // MFG Phase B：MRP + 工單
        public DbSet<MrpRun> MrpRuns { get; set; }
        public DbSet<MrpRunLine> MrpRunLines { get; set; }
        public DbSet<MrpPurchaseVarianceRecord> MrpPurchaseVarianceRecords { get; set; }
        public DbSet<ProductionBatch> ProductionBatches { get; set; }
        public DbSet<WorkOrder> WorkOrders { get; set; }
        public DbSet<WorkOrderOperation> WorkOrderOperations { get; set; }
        public DbSet<WorkOrderMaterial> WorkOrderMaterials { get; set; }
        public DbSet<WorkOrderActualCost> WorkOrderActualCosts { get; set; }
        // CST §3.3 / §3.5：期末實際費率重估（部門實際費用輸入 + 在製品快照）
        public DbSet<DepartmentActualCost> DepartmentActualCosts { get; set; }
        public DbSet<WipSnapshot> WipSnapshots { get; set; }
        public DbSet<Dispatch> Dispatches { get; set; }
        public DbSet<ScheduleRunState> ScheduleRunStates { get; set; }
        public DbSet<ScheduleRunLog> ScheduleRunLogs { get; set; }
        public DbSet<ScheduleRunChange> ScheduleRunChanges { get; set; }
        public DbSet<RoutingQueueTimeSuggestion> RoutingQueueTimeSuggestions { get; set; }
        public DbSet<DispatchMaterial> DispatchMaterials { get; set; }
        public DbSet<OutsourcePurchaseOrder> OutsourcePurchaseOrders { get; set; }
        public DbSet<OutsourcePurchaseOrderLine> OutsourcePurchaseOrderLines { get; set; }
        // MFG Phase D：上工報工
        public DbSet<ProductionSetup> ProductionSetups { get; set; }
        public DbSet<ProductionSession> ProductionSessions { get; set; }
        public DbSet<ProductionReport> ProductionReports { get; set; }
        public DbSet<ProductionReportContainer> ProductionReportContainers { get; set; }
        // MFG Phase E：手機 API + 機台狀態管理
        public DbSet<SyncLog> SyncLogs { get; set; }
        public DbSet<MachineStatusOrder> MachineStatusOrders { get; set; }
        public DbSet<MachineStatusOrderLine> MachineStatusOrderLines { get; set; }
        // Quality Phase G-H：MIL-STD-1916 檢驗規格 + 規格項目 + 檢驗單
        public DbSet<InspectionSpec>                    InspectionSpecs            { get; set; }
        public DbSet<InspectionSpecItem>                InspectionSpecItems        { get; set; }
        public DbSet<InspectionSpecSamplingCounter>     InspectionSpecSamplingCounters { get; set; }
        public DbSet<InspectionOrder>                   InspectionOrders           { get; set; }
        public DbSet<InspectionItem>                    InspectionItems            { get; set; }
        // QCR 模組：不良代碼 + 儀器管理（對應舊系統 QNI01 / QIN01）
        public DbSet<DefectCode>         DefectCodes         { get; set; }
        public DbSet<SmartFactory.MES.Modules.QCR.Models.Instrument>       Instruments       { get; set; }
        public DbSet<SmartFactory.MES.Modules.QCR.Models.InstrumentSerial> InstrumentSerials { get; set; }
        // QCR Phase I：不合格品單
        public DbSet<NonConformanceReport> NonConformanceReports { get; set; }
        // INV §12：盤點清冊
        public DbSet<InventoryCountSheet> InventoryCountSheets { get; set; }
        public DbSet<InventoryCountItem>  InventoryCountItems  { get; set; }
        // Mobile Phase E4：IQC 進料檢驗紀錄
        public DbSet<MobileIqcRecord> MobileIqcRecords { get; set; }
        // Mobile Phase E5：Web Push 訂閱
        public DbSet<PushSubscriptionRecord> PushSubscriptions { get; set; }
        // MFG Phase G：委外作業（OutsourceDemand / MaterialSlip / OutsourceQuotation）
        // (MaterialIssueSlip / MaterialRequisition 已由統一 MaterialSlip 取代，migration 已刪表)
        public DbSet<OutsourceDemand> OutsourceDemands { get; set; }
        public DbSet<MaterialSlip> MaterialSlips { get; set; }
        public DbSet<MaterialSlipLine> MaterialSlipLines { get; set; }
        public DbSet<OutsourceQuotation> OutsourceQuotations { get; set; }

        // === ORD Module DbSets ===
        public DbSet<SalesOrder> SalesOrders { get; set; }
        public DbSet<SalesOrderLine> SalesOrderLines { get; set; }
        public DbSet<GoodsShipment> GoodsShipments { get; set; }
        public DbSet<GoodsShipmentLine> GoodsShipmentLines { get; set; }
        public DbSet<GoodsShipmentLocationDetail> GoodsShipmentLocationDetails { get; set; }
        public DbSet<SalesReturn> SalesReturns { get; set; }
        public DbSet<SalesReturnLine> SalesReturnLines { get; set; }
        public DbSet<SalesReturnLocationDetail> SalesReturnLocationDetails { get; set; }
        public DbSet<InternalOrder> InternalOrders { get; set; }
        public DbSet<InternalOrderLine> InternalOrderLines { get; set; }
        public DbSet<InternalOrderLocationDetail> InternalOrderLocationDetails { get; set; }

        // === INV Phase 3 Module DbSets ===
        public DbSet<StockAdjustment> StockAdjustments { get; set; }
        public DbSet<StockAdjustmentLine> StockAdjustmentLines { get; set; }
        public DbSet<StockAdjustmentLocationDetail> StockAdjustmentLocationDetails { get; set; }
        public DbSet<OutsourceMaterialDoc> OutsourceMaterialDocs { get; set; }
        public DbSet<OutsourceMaterialDocLine> OutsourceMaterialDocLines { get; set; }
        public DbSet<OutsourceMaterialDocLocationDetail> OutsourceMaterialDocLocationDetails { get; set; }
        public DbSet<ReworkMaterialDoc> ReworkMaterialDocs { get; set; }
        public DbSet<ReworkMaterialDocLine> ReworkMaterialDocLines { get; set; }
        public DbSet<ReworkMaterialDocLocationDetail> ReworkMaterialDocLocationDetails { get; set; }
        public DbSet<StockTransfer> StockTransfers { get; set; }
        public DbSet<StockTransferLine> StockTransferLines { get; set; }
        public DbSet<CrossPlantTransfer> CrossPlantTransfers { get; set; }
        public DbSet<CrossPlantTransferLine> CrossPlantTransferLines { get; set; }

        // === RPS Module DbSets ===
        public DbSet<ReportDefinition> ReportDefinitions { get; set; }
        public DbSet<ReportRecipient> ReportRecipients { get; set; }
        public DbSet<ReportRun> ReportRuns { get; set; }

        // === 廠別參數覆寫（plant-scoped-system-params.md §3.1）===
        // 只放「本廠覆寫」；全域值仍在 Core 的 SystemParams（儲存方案甲）。
        public DbSet<PlantParam> PlantParams { get; set; }

        partial void OnModelCreatingPartial(ModelBuilder builder)
        {
            // 廠別參數覆寫：一鍵一廠一列。刻意不是 IPlantEntity——設定畫面要能編輯指定廠別，
            // 套上全域過濾就讀不到別廠的覆寫值了（plant-scoped-system-params.md §3.1）。
            builder.Entity<PlantParam>()
                .HasKey(p => new { p.Key, p.PlantId });

            // 會計期間：一期一廠一列。原本是全域單列，一個廠關帳等於關掉全公司的帳，
            // 而月結清單本來就是每廠一份，兩者對不起來（manual-test-plan §12.4 #2／#3）。
            builder.Entity<Modules.ACT.Models.AccountingPeriod>()
                .HasKey(p => new { p.PeriodId, p.PlantId });

            // ── RPS §7.0：報表定義（常用查詢 ＋ 日後的排程，同一張表）─────────

            // 唯一鍵刻意「不含 PlantId」：本實體不是廠別實體（§7.0.1-a），
            // 加了 PlantId 會讓同一人在不同廠別各存一個同名查詢，違反 §7.9-5 的原意。
            builder.Entity<ReportDefinition>()
                .HasIndex(d => new { d.OwnerUserId, d.Name })
                .IsUnique();

            // 派工器掃描的欄位。
            builder.Entity<ReportDefinition>()
                .HasIndex(d => d.NextRunAt);

            // ── RPS §7.0.1-b：收件人子表 ───────────────────────────────────────

            // 同一份定義同一個人只能有一列——重複列會讓語系去重分出兩組，
            // 同一個人收到兩份內容相同的檔。
            builder.Entity<ReportRecipient>()
                .HasIndex(r => new { r.DefinitionId, r.UserId })
                .IsUnique();

            // 刪定義時一併刪收件人：留著的孤兒列會被日後的權限稽核當成「還在寄」。
            builder.Entity<ReportRecipient>()
                .HasOne(r => r.Definition)
                .WithMany(d => d.Recipients)
                .HasForeignKey(r => r.DefinitionId)
                .OnDelete(DeleteBehavior.Cascade);

            // ── RPS §7.4：執行紀錄 ─────────────────────────────────────────────

            // 主鍵須明寫：EF 慣例認的是 Id 或 ReportRunId，欄位名 RunId 對齊既有
            // DataAuditRun 的命名，慣例掃不到（design-time 會直接拒絕建模型）。
            builder.Entity<ReportRun>()
                .HasKey(r => r.RunId);

            // 收件匣與執行紀錄畫面都是「某份定義的最近幾筆」，兩欄一起建才走得到索引。
            builder.Entity<ReportRun>()
                .HasIndex(r => new { r.DefinitionId, r.RunAt });

            // 保留期清理是「掃全表找過期的」，只依 RunAt。
            builder.Entity<ReportRun>()
                .HasIndex(r => r.RunAt);

            // ⚠️ ReportRun **刻意不建往 ReportDefinition 的 FK**。
            // 執行紀錄是稽核軌跡（§8.6-c），定義被刪掉之後那些紀錄仍必須留著；
            // 建了 FK 就得在 cascade 與 restrict 之間二選一，兩個都不對：
            // cascade 會讓刪一筆排程等於湮滅它的歷史，restrict 會讓排程永遠刪不掉。

            // ── ACT §9 P-8：資料稽核執行紀錄 ───────────────────────────────────

            builder.Entity<DataAuditRun>()
                .HasIndex(r => r.RunAt);

            // 現場遙測：儀表板一律「本廠＋期間」查，裝置現況則依裝置取最新一筆
            builder.Entity<ClientTelemetryEvent>()
                .HasIndex(e => new { e.PlantId, e.ReceivedAt });
            builder.Entity<ClientTelemetryEvent>()
                .HasIndex(e => new { e.DeviceId, e.ReceivedAt });

            // ── 每日排程執行紀錄 ───────────────────────────────────────────────

            // 唯一的查詢形狀就是「這支排程在這一天成功跑過了沒」，兩欄一起建才走得到索引。
            // 刻意**不是**唯一索引：失敗的那次也會寫一列，加唯一約束會讓當天的重試
            // 直接撞 23505 寫不進來，而那個例外會被記成「排程失敗」，真因完全看不出來。
            builder.Entity<ScheduledJobRun>()
                .HasIndex(r => new { r.JobName, r.JobDate });

            builder.Entity<DataAuditFinding>()
                .HasOne(f => f.Run)
                .WithMany(r => r.Findings)
                .HasForeignKey(f => f.RunId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<DataAuditFinding>()
                .HasIndex(f => new { f.RunId, f.AuditType });

            // ── MFG ────────────────────────────────────────────────────────────

            // DepartmentActualCost: 期間 × 廠別 × 工作中心 唯一（費率重估分子，月結 upsert）
            builder.Entity<DepartmentActualCost>()
                .HasIndex(d => new { d.PlantId, d.PeriodId, d.WorkCenterCode })
                .IsUnique();

            // WipSnapshot: 期間 × 廠別 × 工單 唯一（重跑刪舊重建）
            builder.Entity<WipSnapshot>()
                .HasIndex(w => new { w.PlantId, w.PeriodId, w.WorkOrderId })
                .IsUnique();

            // Routing: 複合唯一
            builder.Entity<Routing>()
                .HasIndex(r => new { r.PlantId, r.RoutingId })
                .IsUnique();

            // RoutingOperation: 複合主鍵
            builder.Entity<RoutingOperation>()
                .HasKey(op => new { op.RoutingId, op.OperationSeq });
            builder.Entity<RoutingQueueTimeSuggestion>()
                .HasKey(x => new { x.RoutingId, x.OperationSeq });

            // 排程紀錄：排程頁依廠別列出最近幾次、保留期清理也依 (PlantId, StartedAt) 掃
            builder.Entity<ScheduleRunLog>()
                .HasIndex(l => new { l.PlantId, l.StartedAt });
            // 清理舊紀錄時明細一併刪（資料庫層 ON DELETE CASCADE，ExecuteDelete 才帶得走）
            builder.Entity<ScheduleRunChange>()
                .HasOne(c => c.RunLog)
                .WithMany(l => l.Changes)
                .HasForeignKey(c => c.RunLogId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<RoutingOperation>()
                .HasOne(op => op.Routing)
                .WithMany(r => r.Operations)
                .HasForeignKey(op => op.RoutingId)
                .OnDelete(DeleteBehavior.Cascade);

            // RoutingBom: 三層複合主鍵
            builder.Entity<RoutingBom>()
                .HasKey(b => new { b.RoutingId, b.OperationSeq, b.BomSeq });

            builder.Entity<RoutingBom>()
                .HasOne(b => b.Operation)
                .WithMany(op => op.Boms)
                .HasForeignKey(b => new { b.RoutingId, b.OperationSeq })
                .OnDelete(DeleteBehavior.Cascade);

            // WorkCalendar: 複合唯一索引
            builder.Entity<WorkCalendar>()
                .HasIndex(wc => new { wc.PlantId, wc.WorkCenterId, wc.MachineCode, wc.Date })
                .IsUnique();

            // WorkCalendarSegment: 同範圍同日同序號唯一
            builder.Entity<WorkCalendarSegment>()
                .HasIndex(s => new { s.PlantId, s.WorkCenterId, s.MachineCode, s.Date, s.Seq })
                .IsUnique();

            // PlannedDowntime: 依範疇+起始時間查詢（非唯一）
            builder.Entity<PlannedDowntime>()
                .HasIndex(p => new { p.PlantId, p.WorkCenterId, p.MachineCode, p.StartTime });

            // ── MFG Phase B：MRP + 工單 ────────────────────────────────────────

            // MrpRun: 廠別複合唯一
            builder.Entity<MrpRun>()
                .HasIndex(r => new { r.PlantId, r.MrpRunId })
                .IsUnique();

            // xmin 樂觀並發（PostgreSQL 系統欄位，無 DDL；防雙擊「執行/重跑」造成重複展開＋重複請購單）
            builder.Entity<MrpRun>().UseXminAsConcurrencyToken();

            // 對帳差異項：以執行單 + 狀態查詢（決策清單主要進入點）
            builder.Entity<MrpPurchaseVarianceRecord>()
                .HasIndex(v => new { v.PlantId, v.MrpRunId, v.Status });

            // MrpRunLine: 複合主鍵
            builder.Entity<MrpRunLine>()
                .HasKey(l => new { l.MrpRunId, l.LineSeq });

            builder.Entity<MrpRunLine>()
                .HasOne(l => l.MrpRun)
                .WithMany(r => r.Lines)
                .HasForeignKey(l => l.MrpRunId)
                .OnDelete(DeleteBehavior.Cascade);

            // ProductionBatch: 廠別複合唯一
            builder.Entity<ProductionBatch>()
                .HasIndex(b => new { b.PlantId, b.BatchId })
                .IsUnique();

            // WorkOrder: 廠別複合唯一；ProductionBatch 1:N
            builder.Entity<WorkOrder>()
                .HasIndex(w => new { w.PlantId, w.WorkOrderId })
                .IsUnique();

            // xmin 樂觀並發（PostgreSQL 系統欄位，無 DDL；見 ord-module.md §13）。
            // 防完工入庫並發雙重繳庫：兩個並發 Complete 都讀到 Completed → 各自過帳，
            // 但收尾 UPDATE WorkOrder 帶 WHERE xmin，後到者比對失敗拋 DbUpdateConcurrencyException → 整段交易回滾。
            builder.Entity<WorkOrder>().UseXminAsConcurrencyToken();

            // 箱子（container-tracking.md §9）：同一個空箱被兩張完工入庫同時綁定時，後到者 xmin 比對失敗整段回滾
            builder.Entity<ContainerUnit>().UseXminAsConcurrencyToken();
            builder.Entity<ContainerUnit>().HasIndex(u => new { u.PlantId, u.ContainerCode });
            builder.Entity<ContainerUnit>().HasIndex(u => new { u.PlantId, u.ProductCode, u.LotNo });
            builder.Entity<ContainerUnit>().HasIndex(u => u.WorkOrderId);
            builder.Entity<ContainerUnitHistory>().HasIndex(h => h.ContainerUnitId);
            builder.Entity<ProductionReportContainer>().HasIndex(c => c.ContainerUnitId);

            builder.Entity<WorkOrder>()
                .HasOne(w => w.ProductionBatch)
                .WithMany(b => b.WorkOrders)
                .HasForeignKey(w => w.ProductionBatchId)
                .HasPrincipalKey(b => b.BatchId)
                .OnDelete(DeleteBehavior.Restrict);

            // WorkOrderOperation: 複合主鍵；WorkOrder 1:N
            builder.Entity<WorkOrderOperation>()
                .HasKey(op => new { op.WorkOrderId, op.OperationSeq });

            builder.Entity<WorkOrderOperation>()
                .HasOne(op => op.WorkOrder)
                .WithMany(w => w.Operations)
                .HasForeignKey(op => op.WorkOrderId)
                .HasPrincipalKey(w => w.WorkOrderId)
                .OnDelete(DeleteBehavior.Cascade);

            // WorkOrderMaterial: 三層複合主鍵；WorkOrderOperation 1:N
            builder.Entity<WorkOrderMaterial>()
                .HasKey(m => new { m.WorkOrderId, m.OperationSeq, m.BomSeq });

            builder.Entity<WorkOrderMaterial>()
                .HasOne(m => m.Operation)
                .WithMany(op => op.Materials)
                .HasForeignKey(m => new { m.WorkOrderId, m.OperationSeq })
                .OnDelete(DeleteBehavior.Cascade);

            // Dispatch: 廠別複合唯一；與 WorkOrderOperation 1:1
            builder.Entity<Dispatch>()
                .HasIndex(d => new { d.PlantId, d.DispatchId })
                .IsUnique();

            // xmin 樂觀並發（PostgreSQL 系統欄位，無 DDL；見 ord-module.md §13）。
            // 防報工並發雙重累計 QtyDone：原 byte[] RowVersion 在 Npgsql 不產值、形同無保護
            // （DispatchConcurrencyTests 已實證），改 xmin → CreateReport 收尾 UPDATE 帶 WHERE xmin，
            // 後到者比對失敗拋 DbUpdateConcurrencyException → Controller 轉 409。
            builder.Entity<Dispatch>().UseXminAsConcurrencyToken();

            // 工序 1:N 派工（拆分 §10-7 同工序兄弟派工）。FK (WorkOrderId, OperationSeq) 改非唯一索引。
            builder.Entity<WorkOrderOperation>()
                .HasMany(op => op.Dispatches)
                .WithOne(d => d.Operation)
                .HasForeignKey(d => new { d.WorkOrderId, d.OperationSeq })
                .HasPrincipalKey(op => new { op.WorkOrderId, op.OperationSeq })
                .OnDelete(DeleteBehavior.Cascade);

            // DispatchMaterial: 複合主鍵；Dispatch 1:N
            builder.Entity<DispatchMaterial>()
                .HasKey(m => new { m.DispatchId, m.BomSeq });

            builder.Entity<DispatchMaterial>()
                .HasOne(m => m.Dispatch)
                .WithMany(d => d.Materials)
                .HasForeignKey(m => m.DispatchId)
                .HasPrincipalKey(d => d.DispatchId)
                .OnDelete(DeleteBehavior.Cascade);

            // OutsourcePurchaseOrder: 廠別複合唯一；明細複合主鍵 + Cascade
            builder.Entity<OutsourcePurchaseOrder>()
                .HasIndex(o => new { o.PlantId, o.OutsourcePurchaseOrderId })
                .IsUnique();

            builder.Entity<OutsourcePurchaseOrderLine>()
                .HasKey(l => new { l.OutsourcePurchaseOrderId, l.LineNo });

            builder.Entity<OutsourcePurchaseOrderLine>()
                .HasOne(l => l.OutsourcePurchaseOrder)
                .WithMany(o => o.Lines)
                .HasForeignKey(l => l.OutsourcePurchaseOrderId)
                .HasPrincipalKey(o => o.OutsourcePurchaseOrderId)
                .OnDelete(DeleteBehavior.Cascade);

            // ── MFG Phase D：上工報工 ──────────────────────────────────────────

            // ProductionSetup: 廠別索引；與 Dispatch 1:N（一張派工最多一次 Setup）
            builder.Entity<ProductionSetup>()
                .HasIndex(s => new { s.PlantId, s.SetupId })
                .IsUnique();

            builder.Entity<ProductionSetup>()
                .HasOne(s => s.Dispatch)
                .WithMany()
                .HasForeignKey(s => s.DispatchId)
                .HasPrincipalKey(d => d.DispatchId)
                .OnDelete(DeleteBehavior.Restrict);

            // ProductionSession: 廠別索引；與 Dispatch 1:N（一張派工可有多次上工）
            builder.Entity<ProductionSession>()
                .HasIndex(s => new { s.PlantId, s.SessionId })
                .IsUnique();

            builder.Entity<ProductionSession>()
                .HasOne(s => s.Dispatch)
                .WithMany()
                .HasForeignKey(s => s.DispatchId)
                .HasPrincipalKey(d => d.DispatchId)
                .OnDelete(DeleteBehavior.Restrict);

            // ProductionReport: 廠別索引；與 ProductionSession 1:N；xmin 並發控制
            builder.Entity<ProductionReport>()
                .HasIndex(r => new { r.PlantId, r.ReportId })
                .IsUnique();

            builder.Entity<ProductionReport>()
                .HasOne(r => r.Session)
                .WithMany(s => s.Reports)
                .HasForeignKey(r => r.SessionId)
                .HasPrincipalKey(s => s.SessionId)
                .OnDelete(DeleteBehavior.Restrict);

            // xmin 樂觀並發（PostgreSQL 系統欄位，無 DDL；見 ord-module.md §13）
            builder.Entity<ProductionReport>().UseXminAsConcurrencyToken();

            // ProductionReportContainer: 複合主鍵；與 ProductionReport 1:N
            builder.Entity<ProductionReportContainer>()
                .HasKey(c => new { c.ReportId, c.ContainerSeq });

            builder.Entity<ProductionReportContainer>()
                .HasOne(c => c.Report)
                .WithMany(r => r.Containers)
                .HasForeignKey(c => c.ReportId)
                .HasPrincipalKey(r => r.ReportId)
                .OnDelete(DeleteBehavior.Cascade);

            // ── MFG Phase E：手機 API + 機台狀態管理 ─────────────────────────────

            // SyncLog: ClientId 為 PK（去重鍵），廠別+端點索引加速查詢
            builder.Entity<SyncLog>()
                .HasIndex(s => new { s.Endpoint, s.CreatedAt });

            // InspectionSpec: 明確 PK + SpecCode 唯一
            builder.Entity<InspectionSpec>().HasKey(s => s.SpecId);
            builder.Entity<InspectionSpec>()
                .HasIndex(s => new { s.PlantId, s.SpecCode }).IsUnique();
            builder.Entity<InspectionSpec>()
                .HasIndex(s => new { s.PlantId, s.ProductCode, s.SupplierCode });
            // SpecLevel 抽樣狀態（SamplingState/ConsecutivePasses/LotCounter）就地存於本實體，
            // 由 SamplingPlanService 以 read-modify-write 累加 → shadow xmin 防 lost-update。
            // 連帶：主檔 Edit 並發改設定時亦受權杖約束（已於 controller 補 catch→友善訊息）。
            builder.Entity<InspectionSpec>().UseXminAsConcurrencyToken();

            // InspectionOrder: 明確 PK + 索引
            builder.Entity<InspectionOrder>().HasKey(o => o.InspectionOrderId);
            builder.Entity<InspectionOrder>()
                .HasIndex(o => new { o.PlantId, o.InspectionType, o.SourceDocId, o.SourceLineNo });

            // InspectionItem: 明確 PK + 關聯
            builder.Entity<InspectionItem>().HasKey(i => i.ItemId);
            builder.Entity<InspectionItem>()
                .HasOne(i => i.Order)
                .WithMany(o => o.Items)
                .HasForeignKey(i => i.InspectionOrderId)
                .OnDelete(DeleteBehavior.Cascade);

            // InspectionSpecItem: 明確 PK + FK → InspectionSpec（規格項目）
            builder.Entity<InspectionSpecItem>().HasKey(si => si.SpecItemId);
            builder.Entity<InspectionSpecItem>()
                .HasOne(si => si.Spec)
                .WithMany(s => s.Items)
                .HasForeignKey(si => si.SpecId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Entity<InspectionSpecItem>()
                .HasIndex(si => new { si.SpecId, si.SeqNo }).IsUnique();

            // InspectionSpecSamplingCounter: (SpecId, ScopeKey) 複合唯一（Phase H）
            builder.Entity<InspectionSpecSamplingCounter>().HasKey(c => c.CounterId);
            builder.Entity<InspectionSpecSamplingCounter>()
                .HasOne(c => c.Spec)
                .WithMany()
                .HasForeignKey(c => c.SpecId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Entity<InspectionSpecSamplingCounter>()
                .HasIndex(c => new { c.SpecId, c.ScopeKey }).IsUnique();
            // Machine/Tooling 分區計數同為累加器 → shadow xmin 防並發 lost-update。
            builder.Entity<InspectionSpecSamplingCounter>().UseXminAsConcurrencyToken();

            // QCR：DefectCode（全域，Code 為 PK）
            builder.Entity<DefectCode>().HasKey(d => d.Code);
            builder.Entity<DefectCode>()
                .HasIndex(d => d.Name);

            // QCR：Instrument（全域，InstrumentCode 為 PK）
            var instrType = typeof(SmartFactory.MES.Modules.QCR.Models.Instrument);
            builder.Entity<SmartFactory.MES.Modules.QCR.Models.Instrument>()
                .HasKey(i => i.InstrumentCode);

            // QCR：InstrumentSerial（廠別，SerialId PK；InstrumentCode + SerialNo 複合唯一）
            builder.Entity<SmartFactory.MES.Modules.QCR.Models.InstrumentSerial>()
                .HasKey(s => s.SerialId);
            builder.Entity<SmartFactory.MES.Modules.QCR.Models.InstrumentSerial>()
                .HasIndex(s => new { s.PlantId, s.InstrumentCode, s.SerialNo }).IsUnique();
            builder.Entity<SmartFactory.MES.Modules.QCR.Models.InstrumentSerial>()
                .HasOne(s => s.Instrument)
                .WithMany(i => i.Serials)
                .HasForeignKey(s => s.InstrumentCode)
                .OnDelete(DeleteBehavior.Restrict);

            // INV §12：InventoryCountSheet + InventoryCountItem
            builder.Entity<InventoryCountSheet>()
                .HasKey(s => s.SheetId);
            builder.Entity<InventoryCountSheet>()
                .HasIndex(s => new { s.PlantId, s.SheetStatus });
            builder.Entity<InventoryCountSheet>()
                .HasIndex(s => new { s.PlantId, s.CreatedAt });
            // 一張盤盈虧調整單只能屬於一張清冊——把結帳冪等釘在 DB 層，
            // 應用層守門被繞過（改狀態重跑）時仍會以 23505 擋下，而不是靜默扣兩次庫存。
            // 部分索引：未結帳的清冊 AdjustmentDocId 為 NULL，不參與唯一性。
            builder.Entity<InventoryCountSheet>()
                .HasIndex(s => new { s.PlantId, s.AdjustmentDocId })
                .IsUnique()
                .HasFilter("\"AdjustmentDocId\" IS NOT NULL");
            builder.Entity<InventoryCountItem>()
                .HasKey(i => i.ItemId);
            builder.Entity<InventoryCountItem>()
                .HasIndex(i => new { i.SheetId, i.SeqNo }).IsUnique();
            builder.Entity<InventoryCountItem>()
                .HasIndex(i => new { i.SheetId, i.ProductCode, i.LocationCode, i.LotNo });
            builder.Entity<InventoryCountItem>()
                .HasOne(i => i.Sheet)
                .WithMany(s => s.Items)
                .HasForeignKey(i => i.SheetId)
                .OnDelete(DeleteBehavior.Cascade);

            // QCR Phase I：NonConformanceReport（廠別，NcrId PK）
            builder.Entity<NonConformanceReport>()
                .HasKey(n => n.NcrId);
            builder.Entity<NonConformanceReport>()
                .HasIndex(n => new { n.PlantId, n.CreatedAt });
            builder.Entity<NonConformanceReport>()
                .HasIndex(n => new { n.PlantId, n.NcrStatus });

            // MobileIqcRecord: GoodsReceiptId + LineNo 唯一（同單同行只有一筆 IQC）
            builder.Entity<MobileIqcRecord>()
                .HasIndex(r => new { r.GoodsReceiptId, r.LineNo }).IsUnique();
            builder.Entity<MobileIqcRecord>()
                .HasIndex(r => new { r.PlantId, r.InspectedAt });

            // PushSubscriptionRecord: Endpoint 唯一（一個瀏覽器一筆）
            builder.Entity<PushSubscriptionRecord>()
                .HasIndex(p => p.Endpoint).IsUnique();
            builder.Entity<PushSubscriptionRecord>()
                .HasIndex(p => p.UserId);

            // MachineStatusOrder: 廠別複合唯一
            builder.Entity<MachineStatusOrder>()
                .HasIndex(o => new { o.PlantId, o.OrderId })
                .IsUnique();

            // MachineStatusOrderLine: 複合主鍵；Order 1:N Cascade
            builder.Entity<MachineStatusOrderLine>()
                .HasKey(l => new { l.OrderId, l.LineSeq });

            builder.Entity<MachineStatusOrderLine>()
                .HasOne(l => l.Order)
                .WithMany(o => o.Lines)
                .HasForeignKey(l => l.OrderId)
                .HasPrincipalKey(o => o.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            // ── MFG Phase F：完工入庫 + 發料三模式 ────────────────────────────────

            // ── MFG Phase G：委外作業 ──────────────────────────────────────────────

            // OutsourceDemand: 廠別複合唯一；FK → WorkOrder（可為 null 時用 Restrict）
            builder.Entity<OutsourceDemand>()
                .HasIndex(d => new { d.PlantId, d.DemandId })
                .IsUnique();

            builder.Entity<OutsourceDemand>()
                .HasIndex(d => new { d.PlantId, d.WorkOrderId });

            builder.Entity<OutsourceDemand>()
                .HasOne(d => d.WorkOrder)
                .WithMany()
                .HasForeignKey(d => d.WorkOrderId)
                .HasPrincipalKey(w => w.WorkOrderId)
                .OnDelete(DeleteBehavior.Restrict);

            // MaterialSlip: 廠別複合唯一；明細複合主鍵；SlipNo 全域唯一
            builder.Entity<MaterialSlip>()
                .HasIndex(s => new { s.PlantId, s.SlipId })
                .IsUnique();

            builder.Entity<MaterialSlip>()
                .HasIndex(s => s.SlipNo)
                .IsUnique();

            builder.Entity<MaterialSlip>()
                .HasIndex(s => new { s.PlantId, s.SourceDocType, s.SourceDocId });

            // xmin 樂觀並發（PostgreSQL 系統欄位，無 DDL；見 ord-module.md §13）。
            // 防 Issue 過帳並發雙重發料：收尾 UPDATE 帶 WHERE xmin → 後到者拋
            // DbUpdateConcurrencyException → 過帳交易回滾（Issue 已 catch 轉友善訊息）。
            builder.Entity<MaterialSlip>().UseXminAsConcurrencyToken();

            builder.Entity<MaterialSlipLine>()
                .HasKey(l => new { l.SlipId, l.LineNo });

            builder.Entity<MaterialSlipLine>()
                .HasOne(l => l.Slip)
                .WithMany(s => s.Lines)
                .HasForeignKey(l => l.SlipId)
                .HasPrincipalKey(s => s.SlipId)
                .OnDelete(DeleteBehavior.Cascade);

            // OutsourceQuotation: 全域主檔；4-level 查詢索引
            builder.Entity<OutsourceQuotation>()
                .HasIndex(q => new { q.SupplierCode, q.ProductCode, q.RoutingId, q.OperationSeq, q.EffectiveFrom });

            builder.Entity<OutsourceQuotation>()
                .HasIndex(q => new { q.IsActive, q.EffectiveFrom, q.EffectiveTo });

            // OutsourcePurchaseOrderLine: DemandId FK → OutsourceDemand（可空）
            builder.Entity<OutsourcePurchaseOrderLine>()
                .HasOne(l => l.Demand)
                .WithMany()
                .HasForeignKey(l => l.DemandId)
                .HasPrincipalKey(d => d.DemandId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Restrict);

            // MFG 採番設定（MRP 引擎以 IDocNumberService 產生批號/執行單號）
            builder.Entity<DocNumberConfig>().HasData(
                new DocNumberConfig { DocType="MrpRun",          DocTypeName="MRP執行單", Prefix="MRP", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="ProductionBatch", DocTypeName="生產批",    Prefix="PB",  DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="OutsourcePurchaseOrder", DocTypeName="委外採購單", Prefix="OP", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="OutsourceDemand",  DocTypeName="委外建議單", Prefix="OD", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="MaterialSlip_MI",  DocTypeName="廠內發料單", Prefix="MI", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="MaterialSlip_MR",  DocTypeName="廠內領料單", Prefix="MR", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="MaterialSlip_MX",  DocTypeName="廠內退料單", Prefix="MX", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="MaterialSlip_OI",  DocTypeName="委外發料單", Prefix="OI", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="MaterialSlip_OR",  DocTypeName="委外退料單", Prefix="OR", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="MaterialSlip_RI",  DocTypeName="重工發料單", Prefix="RI", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="MaterialSlip_RX",  DocTypeName="重工退料單", Prefix="RX", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="OutsourceQuotation", DocTypeName="委外報價主檔", Prefix="OQ", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true }
            );

            // ── BAS ────────────────────────────────────────────────────────────

            // Company: CompanyCode 全域唯一
            builder.Entity<Company>()
                .HasIndex(c => c.CompanyCode)
                .IsUnique();

            builder.Entity<Company>()
                .HasMany(c => c.Contacts)
                .WithOne(d => d.Company)
                .HasForeignKey(d => d.CompanyCode)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<CompanyContact>()
                .HasIndex(d => new { d.CompanyCode, d.SeqNo })
                .IsUnique();

            // Currency: CurrencyCode 全域唯一
            builder.Entity<Currency>()
                .HasIndex(c => c.CurrencyCode)
                .IsUnique();

            // CurrencyRate: 複合主鍵
            builder.Entity<CurrencyRate>()
                .HasKey(r => new { r.CurrencyCode, r.RateDate });

            builder.Entity<CurrencyRate>()
                .HasOne(r => r.Currency)
                .WithMany(c => c.Rates)
                .HasForeignKey(r => r.CurrencyCode)
                .OnDelete(DeleteBehavior.Cascade);

            // Container: ContainerCode 全域唯一
            builder.Entity<Container>()
                .HasIndex(c => c.ContainerCode)
                .IsUnique();

            // InventoryTransactionType: TypeCode 全域唯一 + 自我參照（不刪除限制）
            builder.Entity<InventoryTransactionType>()
                .HasIndex(i => i.TypeCode)
                .IsUnique();

            builder.Entity<InventoryTransactionType>()
                .HasOne(i => i.SourceType)
                .WithMany()
                .HasForeignKey(i => i.SourceTypeCode)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Restrict);

            var _seed = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            builder.Entity<InventoryTransactionType>().HasData(
                // ── Purchase ───────────────────────────────────────────────────────
                new InventoryTransactionType { TypeCode="PI", TypeName="進貨",         TypeCategory="Purchase",      Direction= 1, IsSystemType=true, RequireDoc=true,  SourceTypeCode=null,  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                new InventoryTransactionType { TypeCode="PF", TypeName="進貨退出",      TypeCategory="Purchase",      Direction=-1, IsSystemType=true, RequireDoc=true,  SourceTypeCode="PI",  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                new InventoryTransactionType { TypeCode="OI", TypeName="委外進貨",      TypeCategory="Purchase",      Direction= 1, IsSystemType=true, RequireDoc=true,  SourceTypeCode=null,  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                new InventoryTransactionType { TypeCode="OT", TypeName="委外進貨退出",  TypeCategory="Purchase",      Direction=-1, IsSystemType=true, RequireDoc=true,  SourceTypeCode="OI",  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                // ── Transfer ───────────────────────────────────────────────────────
                new InventoryTransactionType { TypeCode="WP", TypeName="調撥撥出",     TypeCategory="Transfer",      Direction=-1, IsSystemType=true, RequireDoc=true,  SourceTypeCode=null,  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                new InventoryTransactionType { TypeCode="WI", TypeName="調撥撥入",     TypeCategory="Transfer",      Direction= 1, IsSystemType=true, RequireDoc=true,  SourceTypeCode="WP",  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                // ── Adjustment ─────────────────────────────────────────────────────
                new InventoryTransactionType { TypeCode="IA", TypeName="庫存調整",     TypeCategory="Adjustment",    Direction= 1, IsSystemType=true, RequireDoc=false, SourceTypeCode=null,  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                // 盤點結帳（InventoryCountController.Close）硬寫 TypeCode="IC"，卻從來沒有人把它種進來：
                // 乾淨部署只要盤出任何差異，過帳第一行就丟「找不到異動類別 IC」，整張清冊結不了帳。
                // 之所以長期沒被發現，是因為既有的殘留測試自己補了一筆到共用測試庫（假綠）。
                // 方向由每一行的 DirectionOverride 決定（盤盈 +1／盤虧 −1），此處的 Direction 只是預設值。
                new InventoryTransactionType { TypeCode="IC", TypeName="盤點盈虧",     TypeCategory="Adjustment",    Direction= 1, IsSystemType=true, RequireDoc=true,  SourceTypeCode=null,  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                // ── Scrap ──────────────────────────────────────────────────────────
                new InventoryTransactionType { TypeCode="IJ", TypeName="報廢",         TypeCategory="Scrap",         Direction=-1, IsSystemType=true, RequireDoc=false, SourceTypeCode=null,  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                // ── Production ─────────────────────────────────────────────────────
                new InventoryTransactionType { TypeCode="IO", TypeName="生產領料",     TypeCategory="Production",    Direction=-1, IsSystemType=true, RequireDoc=true,  SourceTypeCode=null,  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                new InventoryTransactionType { TypeCode="IR", TypeName="生產退料",     TypeCategory="Production",    Direction= 1, IsSystemType=true, RequireDoc=true,  SourceTypeCode="IO",  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                new InventoryTransactionType { TypeCode="IP", TypeName="生產投入",     TypeCategory="Production",    Direction= 1, IsSystemType=true, RequireDoc=true,  SourceTypeCode=null,  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                new InventoryTransactionType { TypeCode="IT", TypeName="工單繳庫",     TypeCategory="Production",    Direction= 1, IsSystemType=true, RequireDoc=true,  SourceTypeCode=null,  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                // ── Outsource ──────────────────────────────────────────────────────
                new InventoryTransactionType { TypeCode="OO", TypeName="委外發料",     TypeCategory="Outsource",     Direction=-1, IsSystemType=true, RequireDoc=true,  SourceTypeCode=null,  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                new InventoryTransactionType { TypeCode="OR", TypeName="委外退料",     TypeCategory="Outsource",     Direction= 1, IsSystemType=true, RequireDoc=true,  SourceTypeCode="OO",  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                // ── Rework ─────────────────────────────────────────────────────────
                new InventoryTransactionType { TypeCode="RI", TypeName="重工領料",     TypeCategory="Rework",        Direction=-1, IsSystemType=true, RequireDoc=true,  SourceTypeCode=null,  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                new InventoryTransactionType { TypeCode="RR", TypeName="重工退料",     TypeCategory="Rework",        Direction= 1, IsSystemType=true, RequireDoc=true,  SourceTypeCode="RI",  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                // ── Loan ───────────────────────────────────────────────────────────
                new InventoryTransactionType { TypeCode="IB", TypeName="借入",         TypeCategory="Loan",          Direction= 1, IsSystemType=true, RequireDoc=true,  SourceTypeCode=null,  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                new InventoryTransactionType { TypeCode="BR", TypeName="借入退回",     TypeCategory="Loan",          Direction=-1, IsSystemType=true, RequireDoc=true,  SourceTypeCode="IB",  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                new InventoryTransactionType { TypeCode="IL", TypeName="借出",         TypeCategory="Loan",          Direction=-1, IsSystemType=true, RequireDoc=true,  SourceTypeCode=null,  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                new InventoryTransactionType { TypeCode="LR", TypeName="借出退回",     TypeCategory="Loan",          Direction= 1, IsSystemType=true, RequireDoc=true,  SourceTypeCode="IL",  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                // ── Sample ─────────────────────────────────────────────────────────
                new InventoryTransactionType { TypeCode="CI", TypeName="客供進貨",     TypeCategory="Sample",        Direction= 1, IsSystemType=true, RequireDoc=true,  SourceTypeCode=null,  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                new InventoryTransactionType { TypeCode="CR", TypeName="客供退回",     TypeCategory="Sample",        Direction=-1, IsSystemType=true, RequireDoc=true,  SourceTypeCode="CI",  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                // ── Consignment ────────────────────────────────────────────────────
                new InventoryTransactionType { TypeCode="SO", TypeName="樣品出貨",     TypeCategory="Sample",        Direction=-1, IsSystemType=true, RequireDoc=true,  SourceTypeCode=null,  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                new InventoryTransactionType { TypeCode="SR", TypeName="樣品退回",     TypeCategory="Sample",        Direction= 1, IsSystemType=true, RequireDoc=true,  SourceTypeCode="SO",  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                // ── Sales（延後實作，需 ORD 模組）──────────────────────────────────
                new InventoryTransactionType { TypeCode="GS", TypeName="訂單出貨",     TypeCategory="Sales",         Direction=-1, IsSystemType=true, RequireDoc=true,  SourceTypeCode=null,  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                new InventoryTransactionType { TypeCode="GR", TypeName="銷貨退回",     TypeCategory="Sales",         Direction= 1, IsSystemType=true, RequireDoc=true,  SourceTypeCode="GS",  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                // ── InternalOrder（延後實作）───────────────────────────────────────
                new InventoryTransactionType { TypeCode="NO", TypeName="內部訂單出",   TypeCategory="InternalOrder", Direction=-1, IsSystemType=true, RequireDoc=true,  SourceTypeCode=null,  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                new InventoryTransactionType { TypeCode="NR", TypeName="內部訂單退",   TypeCategory="InternalOrder", Direction= 1, IsSystemType=true, RequireDoc=true,  SourceTypeCode="NO",  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                // ── CrossPlantTransfer ─────────────────────────────────────────────
                new InventoryTransactionType { TypeCode="XP", TypeName="跨廠調撥出",   TypeCategory="Transfer",      Direction=-1, IsSystemType=true, RequireDoc=true,  SourceTypeCode=null,  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed },
                new InventoryTransactionType { TypeCode="XI", TypeName="跨廠調撥入",   TypeCategory="Transfer",      Direction= 1, IsSystemType=true, RequireDoc=true,  SourceTypeCode="XP",  IsActive=true, CreatedAt=_seed, UpdatedAt=_seed }
            );

            // Tooling: 複合唯一
            builder.Entity<Tooling>()
                .HasIndex(t => new { t.PlantId, t.ToolingCode })
                .IsUnique();

            // Product: ProductCode 全域唯一
            builder.Entity<Product>()
                .HasIndex(p => p.ProductCode)
                .IsUnique();

            // StockUom 於應用層為必填（[Required] 驗證），但 DB 欄位維持可為 null，
            // 以相容既有資料、避免對 Products 既有列做有風險的 NOT NULL 變更。
            builder.Entity<Product>()
                .Property(p => p.StockUom)
                .IsRequired(false);

            // ProductCategory: CategoryCode 全域唯一
            builder.Entity<ProductCategory>()
                .HasIndex(pc => pc.CategoryCode)
                .IsUnique();

            // ChartOfAccount: 會計科目主檔（全公司共用，不分廠）。
            // 原為 InvoiceAccount「發票科目」，2026-09-21 升級為完整科目表，見該類別註解。
            builder.Entity<ChartOfAccount>()
                .HasIndex(a => a.AccountCode)
                .IsUnique();

            // ProductCategoryTag: 多對多標籤，複合主鍵
            builder.Entity<ProductCategoryTag>()
                .HasKey(pt => new { pt.ProductCode, pt.CategoryCode });

            builder.Entity<ProductCategoryTag>()
                .HasOne(pt => pt.Product)
                .WithMany(p => p.Tags)
                .HasForeignKey(pt => pt.ProductCode)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<ProductCategoryTag>()
                .HasOne(pt => pt.Category)
                .WithMany()
                .HasForeignKey(pt => pt.CategoryCode)
                .OnDelete(DeleteBehavior.Restrict);

            // Product → ProductCategory FK
            builder.Entity<Product>()
                .HasOne(p => p.Category)
                .WithMany()
                .HasForeignKey(p => p.CategoryCode)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);

            // SpecialDay: (PlantId, Date) 唯一
            builder.Entity<SpecialDay>()
                .HasIndex(s => new { s.PlantId, s.Date })
                .IsUnique();

            // Shift: 複合唯一
            builder.Entity<Shift>()
                .HasIndex(s => new { s.PlantId, s.ShiftCode })
                .IsUnique();

            // ShiftGroup: 複合唯一
            builder.Entity<ShiftGroup>()
                .HasIndex(sg => new { sg.PlantId, sg.GroupCode })
                .IsUnique();

            // ShiftGroupDay: 複合主鍵
            builder.Entity<ShiftGroupDay>()
                .HasKey(d => new { d.GroupCode, d.DayOfWeek });

            builder.Entity<ShiftGroupDay>()
                .HasOne(d => d.ShiftGroup)
                .WithMany(sg => sg.Days)
                .HasForeignKey(d => d.GroupCode)
                .OnDelete(DeleteBehavior.Cascade);

            // Tax: TaxCode 全域唯一
            builder.Entity<Tax>()
                .HasIndex(t => t.TaxCode)
                .IsUnique();

            // Uom: UomCode 全域唯一
            builder.Entity<Uom>()
                .HasIndex(u => u.UomCode)
                .IsUnique();

            // UomConversion: 複合主鍵
            builder.Entity<UomConversion>()
                .HasKey(uc => new { uc.FromUomCode, uc.ToUomCode });

            builder.Entity<UomConversion>()
                .HasOne(uc => uc.FromUom)
                .WithMany(u => u.Conversions)
                .HasForeignKey(uc => uc.FromUomCode)
                .OnDelete(DeleteBehavior.Cascade);

            // ProductUomConversion: 複合主鍵（品號＋來源單位＋目標單位）。
            // 刻意不建往 Uom／Product 的 FK cascade：這張表是「覆寫全域換算」的例外資料，
            // 停用一個單位不該連帶把品號的換算悄悄刪掉——UomController.DeleteConfirmed
            // 已有「仍被換算引用就不准刪」的顯式檢查，本表一併納入該檢查即可。
            builder.Entity<ProductUomConversion>()
                .HasKey(pc => new { pc.ProductCode, pc.FromUomCode, pc.ToUomCode });

            // WorkCenter: 複合唯一
            builder.Entity<WorkCenter>()
                .HasIndex(wc => new { wc.PlantId, wc.WorkCenterCode })
                .IsUnique();

            // WorkCenterMachine: 複合主鍵 + Cascade Delete
            builder.Entity<WorkCenterMachine>()
                .HasKey(m => new { m.WorkCenterCode, m.MachineCode });

            builder.Entity<WorkCenterMachine>()
                .HasOne(m => m.WorkCenter)
                .WithMany(wc => wc.Machines)
                .HasForeignKey(m => m.WorkCenterCode)
                .OnDelete(DeleteBehavior.Cascade);

            // Warehouse: 複合唯一
            builder.Entity<Warehouse>()
                .HasIndex(wh => new { wh.PlantId, wh.WhCode })
                .IsUnique();

            // DocNumberConfig: 複合主鍵 (DocType, ScopeKey)；ScopeKey="" 為預設規則（不套廠別過濾）
            // ScopeKey 為不變的序號鍵，與可編輯的 PlantId（適用廠別）解耦
            builder.Entity<DocNumberConfig>()
                .HasKey(c => new { c.DocType, c.ScopeKey });

            // DocNumberSequence: 複合主鍵 (DocType, PlantId, Period)
            // PlantId 欄位存「序號 scope」＝解析到的設定之 ScopeKey（""=共用預設規則的所有廠別）
            builder.Entity<DocNumberSequence>()
                .HasKey(s => new { s.DocType, s.PlantId, s.Period });

            builder.Entity<DocNumberConfig>().HasData(
                new DocNumberConfig { DocType="LoanDoc",          DocTypeName="借入借出單",   Prefix="LD", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly,  IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="SampleDoc",        DocTypeName="樣品客供單",   Prefix="SM", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly,  IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="ScrapDoc",         DocTypeName="報廢單",       Prefix="IJ",  DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="NonConformanceReport",  DocTypeName="不合格品單", Prefix="NCR", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="InventoryCountSheet",   DocTypeName="盤點清冊",   Prefix="IC",  DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="StockAdjustment",  DocTypeName="調整單",       Prefix="IA", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly,  IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="OutsourceMaterial",DocTypeName="委外發退料單", Prefix="OM", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly,  IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="ReworkMaterial",   DocTypeName="重工發退料單", Prefix="RM", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly,  IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="StockTransfer",       DocTypeName="同廠調撥單",   Prefix="WT", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly,  IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="MaterialStagingOrder", DocTypeName="備料單",       Prefix="IM", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly,  IsSystemType=true, IsActive=true }
            );

            // StorageLocation: 複合主鍵 + LocationCode 全域唯一索引
            builder.Entity<StorageLocation>()
                .HasKey(sl => new { sl.WhCode, sl.LocationCode });

            builder.Entity<StorageLocation>()
                .HasIndex(sl => sl.LocationCode)
                .IsUnique();

            builder.Entity<StorageLocation>()
                .HasOne(sl => sl.Warehouse)
                .WithMany(wh => wh.Locations)
                .HasForeignKey(sl => sl.WhCode)
                .OnDelete(DeleteBehavior.Cascade);

            // ── PUR ────────────────────────────────────────────────────────────

            // VendorPriceList: 關係映射 + 唯一索引（廠商 × 品號 × 幣別 × 有效起日）
            builder.Entity<VendorPriceList>()
                .HasOne(v => v.Supplier)
                .WithMany()
                .HasForeignKey(v => v.SupplierCode)
                .HasPrincipalKey(c => c.CompanyCode)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<VendorPriceList>()
                .HasOne(v => v.Product)
                .WithMany()
                .HasForeignKey(v => v.ProductCode)
                .HasPrincipalKey(p => p.ProductCode)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<VendorPriceList>()
                .HasOne(v => v.Currency)
                .WithMany()
                .HasForeignKey(v => v.CurrencyCode)
                .HasPrincipalKey(c => c.CurrencyCode)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<VendorPriceList>()
                .HasIndex(v => new { v.SupplierCode, v.ProductCode, v.CurrencyCode, v.ValidFrom })
                .IsUnique();

            // VendorEvaluation（Phase G+ SRM）：廠商 FK + 查詢索引。
            // 廠商 × 年度 × 季度的重複檢查在 Controller 端（季度 null=年度評核，
            // PG 唯一索引視 NULL 互異，索引擋不住年度評核重複，故不設唯一）。
            builder.Entity<VendorEvaluation>()
                .HasOne(v => v.Supplier)
                .WithMany()
                .HasForeignKey(v => v.SupplierCode)
                .HasPrincipalKey(c => c.CompanyCode)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<VendorEvaluation>()
                .HasIndex(v => new { v.SupplierCode, v.EvaluationYear, v.EvaluationQuarter });

            // PurchaseRequest: 複合主鍵索引
            builder.Entity<PurchaseRequest>()
                .HasIndex(pr => new { pr.PlantId, pr.PurchaseRequestId })
                .IsUnique();

            // 來源 MRP 執行單號（重跑去重比對用；非唯一，理論上每個 MrpRunId 至多一筆有效請購單）
            builder.Entity<PurchaseRequest>()
                .HasIndex(pr => pr.SourceMrpRunId);

            // xmin 樂觀並發（PostgreSQL 系統欄位，無 DDL；見 ord-module.md §13）
            builder.Entity<PurchaseRequest>().UseXminAsConcurrencyToken();

            builder.Entity<PurchaseRequest>()
                .HasMany(pr => pr.Lines)
                .WithOne(l => l.PurchaseRequest)
                .HasForeignKey(l => l.PurchaseRequestId)
                .OnDelete(DeleteBehavior.Cascade);

            // PurchaseRequestLine: 複合主鍵 (PurchaseRequestId, LineNo)
            builder.Entity<PurchaseRequestLine>()
                .HasKey(l => new { l.PurchaseRequestId, l.LineNo });

            // PurchaseOrder: 複合唯一索引
            builder.Entity<PurchaseOrder>()
                .HasIndex(po => new { po.PlantId, po.PurchaseOrderId })
                .IsUnique();

            // xmin 樂觀並發（PostgreSQL 系統欄位，無 DDL；見 ord-module.md §13）
            builder.Entity<PurchaseOrder>().UseXminAsConcurrencyToken();

            builder.Entity<PurchaseOrder>()
                .HasMany(po => po.Lines)
                .WithOne(l => l.PurchaseOrder)
                .HasForeignKey(l => l.PurchaseOrderId)
                .OnDelete(DeleteBehavior.Cascade);

            // PurchaseOrderLine: 複合主鍵
            builder.Entity<PurchaseOrderLine>()
                .HasKey(l => new { l.PurchaseOrderId, l.LineNo });

            // GoodsReceipt: 複合唯一索引
            builder.Entity<GoodsReceipt>()
                .HasIndex(gr => new { gr.PlantId, gr.GoodsReceiptId })
                .IsUnique();

            // xmin 樂觀並發（PostgreSQL 系統欄位，無 DDL；見 ord-module.md §13）
            builder.Entity<GoodsReceipt>().UseXminAsConcurrencyToken();

            builder.Entity<GoodsReceipt>()
                .HasMany(gr => gr.Lines)
                .WithOne(l => l.GoodsReceipt)
                .HasForeignKey(l => l.GoodsReceiptId)
                .OnDelete(DeleteBehavior.Cascade);

            // GoodsReceiptLine: 複合主鍵
            builder.Entity<GoodsReceiptLine>()
                .HasKey(l => new { l.GoodsReceiptId, l.LineNo });

            // GoodsReturn: 複合唯一索引
            builder.Entity<GoodsReturn>()
                .HasIndex(gr => new { gr.PlantId, gr.GoodsReturnId })
                .IsUnique();

            // xmin 樂觀並發（PostgreSQL 系統欄位，無 DDL；見 ord-module.md §13）
            builder.Entity<GoodsReturn>().UseXminAsConcurrencyToken();

            builder.Entity<GoodsReturn>()
                .HasMany(gr => gr.Lines)
                .WithOne(l => l.GoodsReturn)
                .HasForeignKey(l => l.GoodsReturnId)
                .OnDelete(DeleteBehavior.Cascade);

            // GoodsReturnLine: 複合主鍵
            builder.Entity<GoodsReturnLine>()
                .HasKey(l => new { l.GoodsReturnId, l.LineNo });

            // GoodsReceiptLocationDetail: FK → GoodsReceiptLine + Cascade Delete
            builder.Entity<GoodsReceiptLocationDetail>()
                .HasOne(d => d.Line)
                .WithMany(l => l.LocationDetails)
                .HasForeignKey(d => new { d.GoodsReceiptId, d.LineNo })
                .OnDelete(DeleteBehavior.Cascade);

            // GoodsReturnLocationDetail: FK → GoodsReturnLine + Cascade Delete
            builder.Entity<GoodsReturnLocationDetail>()
                .HasOne(d => d.Line)
                .WithMany(l => l.LocationDetails)
                .HasForeignKey(d => new { d.GoodsReturnId, d.LineNo })
                .OnDelete(DeleteBehavior.Cascade);

            // ── INV ────────────────────────────────────────────────────────────

            // Inventory: 複合唯一（PlantId + ProductCode + LocationCode + LotNo）
            builder.Entity<Inventory>()
                .HasIndex(i => new { i.PlantId, i.ProductCode, i.LocationCode, i.LotNo })
                .IsUnique();

            // Inventory: 併發控制使用 PostgreSQL 系統欄位 xmin（由 Npgsql 映射為 shadow property）
            builder.Entity<Inventory>()
                .UseXminAsConcurrencyToken();

            // StockMovement: DocNo + DocLineNo 索引（查詢用）
            builder.Entity<StockMovement>()
                .HasIndex(sm => new { sm.DocNo, sm.DocLineNo });

            // StockMovement: PlantId + MovementDate 索引（期間查詢）
            builder.Entity<StockMovement>()
                .HasIndex(sm => new { sm.PlantId, sm.MovementDate });

            // StockMovement: ProductCode 索引（品號查詢）
            builder.Entity<StockMovement>()
                .HasIndex(sm => new { sm.PlantId, sm.ProductCode });

            // InventoryCostLayer: 查詢索引（FIFO 消耗順序）
            builder.Entity<InventoryCostLayer>()
                .HasIndex(l => new { l.PlantId, l.ProductCode, l.LocationCode, l.LotNo, l.ReceiptDate });

            // InventoryCostLayerConsumption: 撤回時以「單號 + 品號儲位批號」回查該單消耗過哪些層。
            builder.Entity<InventoryCostLayerConsumption>()
                .HasIndex(c => new { c.PlantId, c.DocNo, c.ProductCode, c.LocationCode, c.LotNo });

            // 層被刪除時消耗紀錄一併消失（層目前不會被刪，僅扣到 0；此處是防禦性設定）。
            builder.Entity<InventoryCostLayerConsumption>()
                .HasOne(c => c.Layer)
                .WithMany()
                .HasForeignKey(c => c.LayerId)
                .OnDelete(DeleteBehavior.Cascade);

            // InventoryReservation: 可用量彙總查詢（品號 + 狀態）+ 來源釋放查詢
            builder.Entity<InventoryReservation>()
                .HasIndex(r => new { r.PlantId, r.ProductCode, r.Status });

            builder.Entity<InventoryReservation>()
                .HasIndex(r => new { r.SourceType, r.SourceDocId });

            // §24.4 供給端在途 peg：依供給來源單查詢已 peg 量
            builder.Entity<InventoryReservation>()
                .HasIndex(r => new { r.SupplyDocType, r.SupplyDocId });

            // §24.9(3) 並發保護（到貨沖銷）：xmin 系統欄位，無 DDL（見 ord-module.md §13）
            builder.Entity<InventoryReservation>().UseXminAsConcurrencyToken();

            // MaterialStagingOrder / MaterialStagingLine
            builder.Entity<MaterialStagingLine>()
                .HasKey(l => new { l.StagingId, l.LineNo });

            builder.Entity<MaterialStagingLine>()
                .HasOne(l => l.StagingOrder)
                .WithMany(d => d.Lines)
                .HasForeignKey(l => l.StagingId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<MaterialStagingLine>()
                .HasOne(l => l.Reservation)
                .WithMany()
                .HasForeignKey(l => l.ReservationId)
                .OnDelete(DeleteBehavior.SetNull);

            builder.Entity<MaterialStagingOrder>()
                .HasIndex(o => new { o.PlantId, o.StagingId })
                .IsUnique();

            // ── INV Phase 2 ────────────────────────────────────────────────────

            // LoanDoc: 複合主鍵 (LoanDocId 為 PK，明細用複合鍵)
            builder.Entity<LoanDocLine>()
                .HasKey(l => new { l.LoanDocId, l.LineNo });

            builder.Entity<LoanDocLine>()
                .HasOne(l => l.LoanDoc)
                .WithMany(d => d.Lines)
                .HasForeignKey(l => l.LoanDocId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<LoanDocLocationDetail>()
                .HasOne(d => d.Line)
                .WithMany(l => l.LocationDetails)
                .HasForeignKey(d => new { d.LoanDocId, d.LineNo })
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<LoanDoc>()
                .HasIndex(d => new { d.PlantId, d.TypeCode, d.LoanDate });

            // SampleDoc
            builder.Entity<SampleDocLine>()
                .HasKey(l => new { l.SampleDocId, l.LineNo });

            builder.Entity<SampleDocLine>()
                .HasOne(l => l.SampleDoc)
                .WithMany(d => d.Lines)
                .HasForeignKey(l => l.SampleDocId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<SampleDocLocationDetail>()
                .HasOne(d => d.Line)
                .WithMany(l => l.LocationDetails)
                .HasForeignKey(d => new { d.SampleDocId, d.LineNo })
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<SampleDoc>()
                .HasIndex(d => new { d.PlantId, d.TypeCode, d.SampleDate });

            // ScrapDoc
            builder.Entity<ScrapDocLine>()
                .HasKey(l => new { l.ScrapDocId, l.LineNo });

            builder.Entity<ScrapDocLine>()
                .HasOne(l => l.ScrapDoc)
                .WithMany(d => d.Lines)
                .HasForeignKey(l => l.ScrapDocId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<ScrapDocLocationDetail>()
                .HasOne(d => d.Line)
                .WithMany(l => l.LocationDetails)
                .HasForeignKey(d => new { d.ScrapDocId, d.LineNo })
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<ScrapDoc>()
                .HasIndex(d => new { d.PlantId, d.TypeCode, d.ScrapDate });

            // xmin 樂觀並發（PostgreSQL 系統欄位，無 DDL；見 ord-module.md §13、
            // reference-concurrency-catch-audit）。INV 單據表頭補 xmin 試點：確定性擋下同單
            // 並發過帳雙重扣帳（不依賴 Inventory xmin 的讀寫交錯）。
            builder.Entity<ScrapDoc>().UseXminAsConcurrencyToken();

            // INV 單據表頭 xmin 全面鋪開（其餘 8 支，同 ScrapDoc 試點）：
            // 各單據 Confirm/Post/Void 都走 IInventoryPostingService 扣 Inventory（Inventory 已有 xmin），
            // 但表頭原本無自身 xmin → 同單並發雙重過帳僅靠 Inventory 讀寫交錯機率攔截；補表頭 xmin 後確定性擋下。
            builder.Entity<LoanDoc>().UseXminAsConcurrencyToken();
            builder.Entity<SampleDoc>().UseXminAsConcurrencyToken();
            builder.Entity<OutsourceMaterialDoc>().UseXminAsConcurrencyToken();
            builder.Entity<ReworkMaterialDoc>().UseXminAsConcurrencyToken();
            builder.Entity<StockAdjustment>().UseXminAsConcurrencyToken();
            builder.Entity<StockTransfer>().UseXminAsConcurrencyToken();
            builder.Entity<CrossPlantTransfer>().UseXminAsConcurrencyToken();
            builder.Entity<InventoryCountSheet>().UseXminAsConcurrencyToken();

            // ── INV Phase 3 ────────────────────────────────────────────────────

            // StockAdjustment
            builder.Entity<StockAdjustmentLine>()
                .HasKey(l => new { l.StockAdjustmentId, l.LineNo });

            builder.Entity<StockAdjustmentLine>()
                .HasOne(l => l.StockAdjustment)
                .WithMany(d => d.Lines)
                .HasForeignKey(l => l.StockAdjustmentId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<StockAdjustmentLocationDetail>()
                .HasOne(d => d.Line)
                .WithMany(l => l.LocationDetails)
                .HasForeignKey(d => new { d.StockAdjustmentId, d.LineNo })
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<StockAdjustment>()
                .HasIndex(d => new { d.PlantId, d.TypeCode, d.AdjustDate });

            // OutsourceMaterialDoc
            builder.Entity<OutsourceMaterialDocLine>()
                .HasKey(l => new { l.OutsourceMaterialDocId, l.LineNo });

            builder.Entity<OutsourceMaterialDocLine>()
                .HasOne(l => l.OutsourceMaterialDoc)
                .WithMany(d => d.Lines)
                .HasForeignKey(l => l.OutsourceMaterialDocId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<OutsourceMaterialDocLocationDetail>()
                .HasOne(d => d.Line)
                .WithMany(l => l.LocationDetails)
                .HasForeignKey(d => new { d.OutsourceMaterialDocId, d.LineNo })
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<OutsourceMaterialDoc>()
                .HasIndex(d => new { d.PlantId, d.TypeCode, d.IssueDate });

            // ReworkMaterialDoc
            builder.Entity<ReworkMaterialDocLine>()
                .HasKey(l => new { l.ReworkMaterialDocId, l.LineNo });

            builder.Entity<ReworkMaterialDocLine>()
                .HasOne(l => l.ReworkMaterialDoc)
                .WithMany(d => d.Lines)
                .HasForeignKey(l => l.ReworkMaterialDocId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<ReworkMaterialDocLocationDetail>()
                .HasOne(d => d.Line)
                .WithMany(l => l.LocationDetails)
                .HasForeignKey(d => new { d.ReworkMaterialDocId, d.LineNo })
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<ReworkMaterialDoc>()
                .HasIndex(d => new { d.PlantId, d.TypeCode, d.IssueDate });

            // StockTransfer
            builder.Entity<StockTransferLine>()
                .HasKey(l => new { l.StockTransferId, l.LineNo });

            builder.Entity<StockTransferLine>()
                .HasOne(l => l.StockTransfer)
                .WithMany(d => d.Lines)
                .HasForeignKey(l => l.StockTransferId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<StockTransfer>()
                .HasIndex(d => new { d.PlantId, d.TransferDate });

            // ── ORD ────────────────────────────────────────────────────────────

            // SalesOrder
            builder.Entity<SalesOrderLine>()
                .HasKey(l => new { l.SalesOrderId, l.LineNo });

            builder.Entity<SalesOrderLine>()
                .HasOne(l => l.SalesOrder)
                .WithMany(d => d.Lines)
                .HasForeignKey(l => l.SalesOrderId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<SalesOrder>()
                .HasIndex(d => new { d.PlantId, d.OrderDate });

            // xmin 樂觀並發（PostgreSQL 系統欄位，無 DDL；見 ord-module.md §13）
            builder.Entity<SalesOrder>().UseXminAsConcurrencyToken();

            // GoodsShipment
            builder.Entity<GoodsShipmentLine>()
                .HasKey(l => new { l.GoodsShipmentId, l.LineNo });

            builder.Entity<GoodsShipmentLine>()
                .HasOne(l => l.GoodsShipment)
                .WithMany(d => d.Lines)
                .HasForeignKey(l => l.GoodsShipmentId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<GoodsShipment>()
                .HasIndex(d => new { d.PlantId, d.ShipDate });

            // xmin 樂觀並發（PostgreSQL 系統欄位，無 DDL；見 ord-module.md §13）
            builder.Entity<GoodsShipment>().UseXminAsConcurrencyToken();

            // GoodsShipmentLocationDetail: FK → GoodsShipmentLine + Cascade Delete
            builder.Entity<GoodsShipmentLocationDetail>()
                .HasOne(d => d.Line)
                .WithMany(l => l.LocationDetails)
                .HasForeignKey(d => new { d.GoodsShipmentId, d.LineNo })
                .OnDelete(DeleteBehavior.Cascade);

            // SalesReturn
            builder.Entity<SalesReturnLine>()
                .HasKey(l => new { l.SalesReturnId, l.LineNo });

            builder.Entity<SalesReturnLine>()
                .HasOne(l => l.SalesReturn)
                .WithMany(d => d.Lines)
                .HasForeignKey(l => l.SalesReturnId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<SalesReturn>()
                .HasIndex(d => new { d.PlantId, d.ReturnDate });

            // xmin 樂觀並發（PostgreSQL 系統欄位，無 DDL；見 ord-module.md §13）
            builder.Entity<SalesReturn>().UseXminAsConcurrencyToken();

            // SalesReturnLocationDetail: FK → SalesReturnLine + Cascade Delete
            builder.Entity<SalesReturnLocationDetail>()
                .HasOne(d => d.Line)
                .WithMany(l => l.LocationDetails)
                .HasForeignKey(d => new { d.SalesReturnId, d.LineNo })
                .OnDelete(DeleteBehavior.Cascade);

            // InternalOrder
            builder.Entity<InternalOrderLine>()
                .HasKey(l => new { l.InternalOrderId, l.LineNo });

            builder.Entity<InternalOrderLine>()
                .HasOne(l => l.InternalOrder)
                .WithMany(d => d.Lines)
                .HasForeignKey(l => l.InternalOrderId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<InternalOrder>()
                .HasIndex(d => new { d.PlantId, d.TypeCode, d.OrderDate });

            // xmin 樂觀並發（PostgreSQL 系統欄位，無 DDL；見 ord-module.md §13）
            builder.Entity<InternalOrder>().UseXminAsConcurrencyToken();

            builder.Entity<InternalOrderLocationDetail>()
                .HasOne(d => d.Line)
                .WithMany(l => l.LocationDetails)
                .HasForeignKey(d => new { d.InternalOrderId, d.LineNo })
                .OnDelete(DeleteBehavior.Cascade);

            // ORD DocNumberConfig seeds
            builder.Entity<DocNumberConfig>().HasData(
                new DocNumberConfig { DocType="SalesOrder",    DocTypeName="銷售訂單", Prefix="SO", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="GoodsShipment", DocTypeName="出貨單",   Prefix="GS", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="SalesReturn",   DocTypeName="銷退單",   Prefix="GR", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="InternalOrder",       DocTypeName="內部訂單",   Prefix="NO", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="CrossPlantTransfer",  DocTypeName="跨廠調撥單", Prefix="XP", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true }
            );

            // ── PUR Phase A：OutsourceReceipt ──────────────────────────────────

            builder.Entity<OutsourceReceipt>()
                .HasIndex(or => new { or.PlantId, or.OutsourceReceiptId })
                .IsUnique();

            // xmin 樂觀並發（PostgreSQL 系統欄位，無 DDL；見 ord-module.md §13）
            builder.Entity<OutsourceReceipt>().UseXminAsConcurrencyToken();

            // (SourceDocType, SourceDocId) 對應 APInvoice；OutsourceReceipt 本身無此欄位，
            // 防重由 APInvoice 的唯一索引負責
            builder.Entity<OutsourceReceipt>()
                .HasMany(or => or.Lines)
                .WithOne(l => l.OutsourceReceipt)
                .HasForeignKey(l => l.OutsourceReceiptId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<OutsourceReceiptLine>()
                .HasKey(l => new { l.OutsourceReceiptId, l.LineNo });

            builder.Entity<OutsourceReceiptLocationDetail>()
                .HasOne(d => d.Line)
                .WithMany(l => l.LocationDetails)
                .HasForeignKey(d => new { d.OutsourceReceiptId, d.LineNo })
                .OnDelete(DeleteBehavior.Cascade);

            // DocNumberConfig seed for OutsourceReceipt
            builder.Entity<DocNumberConfig>().HasData(
                new DocNumberConfig { DocType="OutsourceReceipt", DocTypeName="委外收貨單", Prefix="OR", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true }
            );

            // DocNumberConfig seed for OutsourceRework（Phase F §4.2）
            builder.Entity<DocNumberConfig>().HasData(
                new DocNumberConfig { DocType="OutsourceRework", DocTypeName="委外重工單", Prefix="RW", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true }
            );

            // ── CST §3.4：採購分攤費用（Landed Cost）分攤單 ────────────────────
            builder.Entity<LandedCostAllocation>()
                .HasIndex(a => new { a.PlantId, a.LandedCostAllocationId })
                .IsUnique();

            builder.Entity<LandedCostAllocation>().UseXminAsConcurrencyToken();

            builder.Entity<LandedCostAllocation>()
                .HasMany(a => a.Lines)
                .WithOne(l => l.LandedCostAllocation)
                .HasForeignKey(l => l.LandedCostAllocationId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<LandedCostAllocationLine>()
                .HasKey(l => new { l.LandedCostAllocationId, l.LineNo });

            builder.Entity<DocNumberConfig>().HasData(
                new DocNumberConfig { DocType="LandedCostAllocation", DocTypeName="採購分攤費用單", Prefix="LCA", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true }
            );

            // ── FIN Module（Phase A）──────────────────────────────────────────

            // APInvoice: (SourceDocType, SourceDocId) 唯一 → 防重複自動開票
            builder.Entity<APInvoice>()
                .HasIndex(i => new { i.PlantId, i.APInvoiceId })
                .IsUnique();

            // 一張來源單同時只能有一張「未作廢」的系統發票（Status 3＝Void）。原本不分狀態：作廢後那張出貨／進貨
            // 再也開不出新發票，GS_NO_AR_INVOICE 永遠清不掉（擋月結），完整模式分批上架的第二批更直接失敗（2026-10-22）。
            builder.Entity<APInvoice>()
                .HasIndex(i => new { i.SourceDocType, i.SourceDocId })
                .IsUnique()
                .HasFilter("\"SourceDocType\" IS NOT NULL AND \"SourceDocId\" IS NOT NULL AND \"Status\" <> 3");

            // xmin 樂觀並發（PG 系統欄位，零 DDL）：防「兩張付款單並行核銷同一張發票」
            // ——兩者都通過未結餘額驗證後各自寫入絕對值，後到者覆蓋先到者，
            // 發票只認一次付款而銀行帳戶被扣兩次（見 APInvoice.xmin 註解）。
            builder.Entity<APInvoice>().UseXminAsConcurrencyToken();

            builder.Entity<APInvoiceLine>()
                .HasKey(l => new { l.APInvoiceId, l.LineNo });

            builder.Entity<APInvoiceLine>()
                .HasOne(l => l.APInvoice)
                .WithMany(i => i.Lines)
                .HasForeignKey(l => l.APInvoiceId)
                .OnDelete(DeleteBehavior.Cascade);

            // ARInvoice: (SourceDocType, SourceDocId) 唯一
            builder.Entity<ARInvoice>()
                .HasIndex(i => new { i.PlantId, i.ARInvoiceId })
                .IsUnique();

            // 一張來源單同時只能有一張「未作廢」的系統發票（Status 3＝Void）。原本不分狀態：作廢後那張出貨／進貨
            // 再也開不出新發票，GS_NO_AR_INVOICE 永遠清不掉（擋月結），完整模式分批上架的第二批更直接失敗（2026-10-22）。
            builder.Entity<ARInvoice>()
                .HasIndex(i => new { i.SourceDocType, i.SourceDocId })
                .IsUnique()
                .HasFilter("\"SourceDocType\" IS NOT NULL AND \"SourceDocId\" IS NOT NULL AND \"Status\" <> 3");

            // 同上，收款端對稱：兩張收款單並行核銷同一張應收發票。
            builder.Entity<ARInvoice>().UseXminAsConcurrencyToken();

            builder.Entity<ARInvoiceLine>()
                .HasKey(l => new { l.ARInvoiceId, l.LineNo });

            builder.Entity<ARInvoiceLine>()
                .HasOne(l => l.ARInvoice)
                .WithMany(i => i.Lines)
                .HasForeignKey(l => l.ARInvoiceId)
                .OnDelete(DeleteBehavior.Cascade);

            // ── 折讓單（W3-F3 Phase 2）────────────────────────────────────────
            // 一張退貨單只會產生一張折讓單：唯一索引擋掉「退貨過帳被重試／多入口
            // 各產生一張」的重複草稿。四個退貨入口都會呼叫產生器，沒有這條索引
            // 就得靠每個呼叫端自己記得先查——那正是這個系統一再出事的形狀。
            builder.Entity<ARCreditNote>()
                .HasIndex(n => new { n.SourceDocType, n.SourceDocId })
                .IsUnique()
                .HasFilter("\"SourceDocType\" IS NOT NULL AND \"SourceDocId\" IS NOT NULL");

            builder.Entity<ARCreditNote>().HasIndex(n => new { n.PlantId, n.Status, n.NoteDate });

            // 確認時要把原發票的 CreditedAmount 累加回去（讀改寫），與收款核銷同構，
            // 故同樣以 xmin 擋兩張折讓單並行沖抵同一張發票。
            builder.Entity<ARCreditNote>().UseXminAsConcurrencyToken();

            builder.Entity<ARCreditNoteLine>().HasKey(l => new { l.ARCreditNoteId, l.LineNo });
            builder.Entity<ARCreditNoteLine>()
                .HasOne(l => l.ARCreditNote).WithMany(n => n.Lines)
                .HasForeignKey(l => l.ARCreditNoteId).OnDelete(DeleteBehavior.Cascade);
            builder.Entity<ARCreditNoteLine>().HasIndex(l => l.ARInvoiceId);

            builder.Entity<APDebitNote>()
                .HasIndex(n => new { n.SourceDocType, n.SourceDocId })
                .IsUnique()
                .HasFilter("\"SourceDocType\" IS NOT NULL AND \"SourceDocId\" IS NOT NULL");

            builder.Entity<APDebitNote>().HasIndex(n => new { n.PlantId, n.Status, n.NoteDate });
            builder.Entity<APDebitNote>().UseXminAsConcurrencyToken();

            builder.Entity<APDebitNoteLine>().HasKey(l => new { l.APDebitNoteId, l.LineNo });
            builder.Entity<APDebitNoteLine>()
                .HasOne(l => l.APDebitNote).WithMany(n => n.Lines)
                .HasForeignKey(l => l.APDebitNoteId).OnDelete(DeleteBehavior.Cascade);
            builder.Entity<APDebitNoteLine>().HasIndex(l => l.APInvoiceId);

            // APPayment: 廠別唯一；Allocations FK
            builder.Entity<APPayment>()
                .HasIndex(p => new { p.PlantId, p.APPaymentId })
                .IsUnique();

            builder.Entity<APPaymentAllocation>()
                .HasOne(a => a.APPayment)
                .WithMany(p => p.Allocations)
                .HasForeignKey(a => a.APPaymentId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<APPaymentAllocation>()
                .HasOne(a => a.APInvoice)
                .WithMany(i => i.Allocations)
                .HasForeignKey(a => a.APInvoiceId)
                .OnDelete(DeleteBehavior.Restrict);

            // ARReceipt: 廠別唯一；Allocations FK
            builder.Entity<ARReceipt>()
                .HasIndex(r => new { r.PlantId, r.ARReceiptId })
                .IsUnique();

            builder.Entity<ARReceiptAllocation>()
                .HasOne(a => a.ARReceipt)
                .WithMany(r => r.Allocations)
                .HasForeignKey(a => a.ARReceiptId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<ARReceiptAllocation>()
                .HasOne(a => a.ARInvoice)
                .WithMany(i => i.Allocations)
                .HasForeignKey(a => a.ARInvoiceId)
                .OnDelete(DeleteBehavior.Restrict);

            // BankAccount: 廠別唯一 + xmin 樂觀鎖（防並發餘額覆蓋）
            builder.Entity<BankAccount>()
                .HasIndex(b => new { b.PlantId, b.AccountId })
                .IsUnique();

            builder.Entity<BankAccount>()
                .UseXminAsConcurrencyToken();

            // FIN DocNumberConfig seeds
            builder.Entity<DocNumberConfig>().HasData(
                new DocNumberConfig { DocType="APInvoice",  DocTypeName="應付發票", Prefix="API", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="ARInvoice",  DocTypeName="應收發票", Prefix="ARI", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="ARCreditNote", DocTypeName="銷貨折讓單", Prefix="ARC", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="APDebitNote",  DocTypeName="進貨折讓單", Prefix="APD", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="APPayment",  DocTypeName="付款單",   Prefix="APP", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true },
                new DocNumberConfig { DocType="ARReceipt",  DocTypeName="收款單",   Prefix="ARR", DateFormat=DocDateFormat.YYMM, SeqLength=5, Separator="-", ResetCycle=DocSeqReset.Yearly, IsSystemType=true, IsActive=true }
            );

            // CrossPlantTransfer
            builder.Entity<CrossPlantTransferLine>()
                .HasKey(l => new { l.CrossPlantTransferId, l.LineNo });

            builder.Entity<CrossPlantTransferLine>()
                .HasOne(l => l.CrossPlantTransfer)
                .WithMany(d => d.Lines)
                .HasForeignKey(l => l.CrossPlantTransferId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<CrossPlantTransfer>()
                .HasIndex(d => new { d.PlantId, d.TransferDate });

            builder.Entity<CrossPlantTransfer>()
                .HasIndex(d => new { d.ToPlantId, d.TransferDate });
        }
    }
}
