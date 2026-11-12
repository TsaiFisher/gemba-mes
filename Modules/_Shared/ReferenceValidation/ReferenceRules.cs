using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.QCR.Models;
using M = SmartFactory.MES.Modules._Shared.ReferenceValidation.ReferenceMaster;

namespace SmartFactory.MES.Modules._Shared.ReferenceValidation
{
    /// <summary>
    /// 參照欄位規則的集中登錄。盤點與分批計畫見 <c>.claude/reviews/reference-validation-inventory.md</c>。
    ///
    /// <para>用集中登錄而不是在實體上加 attribute：Core 擁有的實體不能從 ProjectZ 加 attribute，
    /// 集中登錄兩邊都管得到，也能一眼看出哪些欄位還沒登錄。</para>
    ///
    /// <para>每個參照欄位非「規則」即「豁免」——守門測試 R41 掃 EF snapshot 對帳，
    /// 新增參照欄位卻沒登錄會讓 build 紅。<b>豁免的判準</b>：值不是使用者從下拉選的，而是系統
    /// 從已檢查過的來源帶入（途程→工單→派工、BOM→工單用料、快照、彙總），或是主從關係的父鍵。
    /// 這些列若也驗「仍啟用」，主檔一停用，排程、MRP、月結就會整批失敗。豁免以 <see cref="ReferenceExemptAttribute"/> 宣告在本類別上。</para>
    /// </summary>
    // 主從關係的父鍵：子列隨父檔建立，父檔存在才建得出來
    [ReferenceExempt(typeof(RoutingBom), nameof(RoutingBom.RoutingId), "父鍵（途程用料隨途程建立）")]
    [ReferenceExempt(typeof(RoutingOperation), nameof(RoutingOperation.RoutingId), "父鍵（途程工序隨途程建立）")]
    [ReferenceExempt(typeof(InstrumentSerial), nameof(InstrumentSerial.InstrumentCode), "父鍵（序號隨量具建立）")]

    // 系統從已檢查過的來源帶入
    [ReferenceExempt(typeof(Dispatch), nameof(Dispatch.ToolingCode), "由途程工序帶入，於途程維護時檢查")]
    [ReferenceExempt(typeof(ProductionSetup), nameof(ProductionSetup.ToolingCode), "沿用派工指定的模具")]
    [ReferenceExempt(typeof(WorkOrderOperation), nameof(WorkOrderOperation.WorkCenterId), "由途程工序展開")]
    [ReferenceExempt(typeof(WorkOrderMaterial), nameof(WorkOrderMaterial.ProductCode), "由途程用料展開")]
    [ReferenceExempt(typeof(WorkOrderMaterial), nameof(WorkOrderMaterial.UomCode), "由途程用料展開")]
    [ReferenceExempt(typeof(DispatchMaterial), nameof(DispatchMaterial.ProductCode), "由工單用料展開")]
    [ReferenceExempt(typeof(DispatchMaterial), nameof(DispatchMaterial.UomCode), "由工單用料展開")]
    [ReferenceExempt(typeof(MrpRunLine), nameof(MrpRunLine.ProductCode), "由已核准訂單匯入")]
    [ReferenceExempt(typeof(ProductionBatch), nameof(ProductionBatch.ProductCode), "由 MRP 需求列展開")]
    [ReferenceExempt(typeof(OutsourceDemand), nameof(OutsourceDemand.ProductCode), "由 MRP 展開")]
    [ReferenceExempt(typeof(MachineStatusOrderLine), nameof(MachineStatusOrderLine.WorkCenterCode), "取自機台主檔所屬工作中心")]

    // 快照、彙總、物化結果
    [ReferenceExempt(typeof(WorkCalendarSegment), nameof(WorkCalendarSegment.WorkCenterId), "由工作日曆批次展開物化")]
    [ReferenceExempt(typeof(WorkCalendarSegment), nameof(WorkCalendarSegment.MachineCode), "由工作日曆批次展開物化")]
    [ReferenceExempt(typeof(WipSnapshot), nameof(WipSnapshot.PeriodId), "月結快照")]
    [ReferenceExempt(typeof(MrpPurchaseVarianceRecord), nameof(MrpPurchaseVarianceRecord.ProductCode), "MRP 重跑對帳結果")]

    // 系統依登入者填入
    [ReferenceExempt(typeof(ProductionSession), nameof(ProductionSession.OperatorId), "系統填入目前登入者")]
    [ReferenceExempt(typeof(ProductionSetup), nameof(ProductionSetup.OperatorId), "系統填入目前登入者")]

    // 主從父鍵（批 2）
    [ReferenceExempt(typeof(CompanyContact), nameof(CompanyContact.CompanyCode), "父鍵（聯絡人隨往來廠商建立）")]

    // 畫面沒有這個欄位（只在 ViewModel 綁定或從未提供輸入）
    [ReferenceExempt(typeof(SalesOrder), nameof(SalesOrder.DepartmentCode), "畫面無此欄位")]
    [ReferenceExempt(typeof(SalesOrder), nameof(SalesOrder.SalesRepCode), "畫面無此欄位")]
    [ReferenceExempt(typeof(Company), nameof(Company.SalesPersonId), "畫面無此欄位")]
    [ReferenceExempt(typeof(InventoryTransactionType), nameof(InventoryTransactionType.SourceTypeCode), "畫面為自由文字（交易類型的來源分類）")]
    // 庫存帳、成本層、預留：由過帳／預留服務產生，值取自已檢查過的單據
    [ReferenceExempt(typeof(Inventory), nameof(Inventory.ProductCode), "庫存帳，由過帳產生")]
    [ReferenceExempt(typeof(Inventory), nameof(Inventory.LocationCode), "庫存帳，由過帳產生")]
    [ReferenceExempt(typeof(InventoryCostLayer), nameof(InventoryCostLayer.ProductCode), "成本層，由過帳產生")]
    [ReferenceExempt(typeof(InventoryCostLayer), nameof(InventoryCostLayer.LocationCode), "成本層，由過帳產生")]
    [ReferenceExempt(typeof(InventoryCostLayerConsumption), nameof(InventoryCostLayerConsumption.ProductCode), "成本層耗用，由過帳產生")]
    [ReferenceExempt(typeof(InventoryCostLayerConsumption), nameof(InventoryCostLayerConsumption.LocationCode), "成本層耗用，由過帳產生")]
    [ReferenceExempt(typeof(InventoryReservation), nameof(InventoryReservation.ProductCode), "預留，由 MRP／投產產生")]
    [ReferenceExempt(typeof(ContainerUnit), nameof(ContainerUnit.ContainerCode), "建立時由 ContainerUnitService 檢查種類啟用；之後裝箱／清空不應因種類停用而失敗")]
    [ReferenceExempt(typeof(ContainerUnit), nameof(ContainerUnit.ProductCode), "裝箱時沿用入庫品號，由系統綁定")]
    [ReferenceExempt(typeof(ContainerUnitHistory), nameof(ContainerUnitHistory.ProductCode), "歷程，由系統產生")]
    [ReferenceExempt(typeof(StockMovement), nameof(StockMovement.ProductCode), "異動帳，由過帳產生")]
    [ReferenceExempt(typeof(StockMovement), nameof(StockMovement.LocationCode), "異動帳，由過帳產生")]
    [ReferenceExempt(typeof(StockMovement), nameof(StockMovement.UomCode), "異動帳，由過帳產生")]
    [ReferenceExempt(typeof(StockMovement), nameof(StockMovement.StockUomCode), "異動帳，由過帳產生")]
    [ReferenceExempt(typeof(LandedCostAllocationLine), nameof(LandedCostAllocationLine.ProductCode), "到岸成本分攤，取自收料明細")]
    [ReferenceExempt(typeof(LandedCostAllocationLine), nameof(LandedCostAllocationLine.LocationCode), "到岸成本分攤，取自收料明細")]

    // 盤點：範圍條件是區間端點（不必是實際代碼），盤點明細由範圍展開
    [ReferenceExempt(typeof(InventoryCountSheet), nameof(InventoryCountSheet.ProdCodeFrom), "盤點範圍條件（區間端點）")]
    [ReferenceExempt(typeof(InventoryCountSheet), nameof(InventoryCountSheet.ProdCodeTo), "盤點範圍條件（區間端點）")]
    [ReferenceExempt(typeof(InventoryCountSheet), nameof(InventoryCountSheet.WhCodeFrom), "盤點範圍條件（區間端點）")]
    [ReferenceExempt(typeof(InventoryCountSheet), nameof(InventoryCountSheet.WhCodeTo), "盤點範圍條件（區間端點）")]
    [ReferenceExempt(typeof(InventoryCountSheet), nameof(InventoryCountSheet.LocCodeFrom), "盤點範圍條件（區間端點）")]
    [ReferenceExempt(typeof(InventoryCountSheet), nameof(InventoryCountSheet.LocCodeTo), "盤點範圍條件（區間端點）")]
    [ReferenceExempt(typeof(InventoryCountItem), nameof(InventoryCountItem.ProductCode), "由盤點範圍展開")]
    [ReferenceExempt(typeof(InventoryCountItem), nameof(InventoryCountItem.WhCode), "由盤點範圍展開")]
    [ReferenceExempt(typeof(InventoryCountItem), nameof(InventoryCountItem.LocationCode), "由盤點範圍展開")]

    // QCR：由收料／報工自動建立，或系統填入檢驗人
    [ReferenceExempt(typeof(InspectionOrder), nameof(InspectionOrder.ProductCode), "由收料／報工自動建立")]
    [ReferenceExempt(typeof(InspectionOrder), nameof(InspectionOrder.InspectorId), "系統填入目前登入者")]
    [ReferenceExempt(typeof(InspectionItem), nameof(InspectionItem.UomCode), "由檢驗規格項目帶入")]
    [ReferenceExempt(typeof(InspectionSpec), nameof(InspectionSpec.SpecCode), "規格自身代碼，不是參照")]
    [ReferenceExempt(typeof(MobileIqcRecord), nameof(MobileIqcRecord.ProductCode), "取自收料單")]
    [ReferenceExempt(typeof(MobileIqcRecord), nameof(MobileIqcRecord.InspectorId), "系統填入目前登入者")]
    // 月結與稽核的計算結果
    [ReferenceExempt(typeof(JournalEntry), nameof(JournalEntry.PeriodId), "依分錄日期換算；可否入帳由期間鎖把關")]
    [ReferenceExempt(typeof(MonthEndChecklist), nameof(MonthEndChecklist.PeriodId), "月結檢核清單，由月結建立")]
    [ReferenceExempt(typeof(EclAllowance), nameof(EclAllowance.PeriodId), "月結呆帳評估結果")]
    [ReferenceExempt(typeof(FxRevaluationEntry), nameof(FxRevaluationEntry.PeriodId), "月結外幣重評結果")]
    [ReferenceExempt(typeof(FxRevaluationEntry), nameof(FxRevaluationEntry.CurrencyCode), "月結外幣重評結果")]
    [ReferenceExempt(typeof(InventoryNrvAssessment), nameof(InventoryNrvAssessment.PeriodId), "月結淨變現價值評估結果")]
    [ReferenceExempt(typeof(InventoryNrvAssessment), nameof(InventoryNrvAssessment.ProductCode), "月結淨變現價值評估結果")]
    [ReferenceExempt(typeof(DataAuditFinding), nameof(DataAuditFinding.ProductCode), "資料稽核發現（記錄的正是可能有問題的值）")]
    public static class ReferenceRules
    {
        public static readonly IReadOnlyList<ReferenceRule> Rules = new List<ReferenceRule>
        {
            // ── MFG：途程 ─────────────────────────────────────────────────────
            new(typeof(Routing), nameof(Routing.ProductCode), M.Product),
            new(typeof(RoutingBom), nameof(RoutingBom.ProductCode), M.Product),
            new(typeof(RoutingBom), nameof(RoutingBom.UomCode), M.Uom),
            new(typeof(RoutingOperation), nameof(RoutingOperation.WorkCenterId), M.WorkCenter),
            new(typeof(RoutingOperation), nameof(RoutingOperation.UomCode), M.Uom),
            new(typeof(RoutingOperation), nameof(RoutingOperation.ToolingCode), M.Tooling),
            new(typeof(RoutingOperation), nameof(RoutingOperation.ContainerInCode), M.Container),
            new(typeof(RoutingOperation), nameof(RoutingOperation.ContainerOutCode), M.Container),

            // ── MFG：工單、派工、現場 ─────────────────────────────────────────
            new(typeof(WorkOrder), nameof(WorkOrder.ProductCode), M.Product),
            new(typeof(WorkOrder), nameof(WorkOrder.RoutingId), M.Routing),
            new(typeof(WorkOrder), nameof(WorkOrder.OutsourceSupplierCode), M.Company),
            // 換機台、拆分派工、上工改選機台都會寫這欄；排程寫入的也一定是可用機台
            new(typeof(Dispatch), nameof(Dispatch.MachineCode), M.MachineOrLine),
            new(typeof(ProductionSession), nameof(ProductionSession.MachineCode), M.MachineOrLine),
            new(typeof(ProductionSession), nameof(ProductionSession.ToolingCode), M.Tooling),
            new(typeof(ProductionReport), nameof(ProductionReport.ContainerCode), M.Container),
            new(typeof(ProductionReport), nameof(ProductionReport.NgReasonCode), M.CodeEntry, Category: "ng_reason"),
            new(typeof(ProductionReportContainer), nameof(ProductionReportContainer.ContainerCode), M.Container),
            // 廢棄機台不接受狀態事件：否則對廢棄機台送「修復完成」會把它改回可用＝復活
            new(typeof(MachineStatusOrderLine), nameof(MachineStatusOrderLine.MachineCode), M.Machine),

            // ── MFG：產能 ─────────────────────────────────────────────────────
            new(typeof(WorkCalendar), nameof(WorkCalendar.ShiftGroupCode), M.ShiftGroup),
            new(typeof(WorkCalendar), nameof(WorkCalendar.WorkCenterId), M.WorkCenter),
            new(typeof(WorkCalendar), nameof(WorkCalendar.MachineCode), M.Machine),
            new(typeof(PlannedDowntime), nameof(PlannedDowntime.WorkCenterId), M.WorkCenter),
            new(typeof(PlannedDowntime), nameof(PlannedDowntime.MachineCode), M.Machine),
            // 月結也會自動建立（含當月中途停用的工作中心），故不驗啟用
            new(typeof(DepartmentActualCost), nameof(DepartmentActualCost.WorkCenterCode), M.WorkCenter, CheckActive: false),
            new(typeof(DepartmentActualCost), nameof(DepartmentActualCost.PeriodId), M.AccountingPeriod),

            // ── MFG：委外 ─────────────────────────────────────────────────────
            new(typeof(OutsourcePurchaseOrder), nameof(OutsourcePurchaseOrder.SupplierCode), M.Company),
            new(typeof(OutsourcePurchaseOrderLine), nameof(OutsourcePurchaseOrderLine.ProductCode), M.Product),
            new(typeof(OutsourcePurchaseOrderLine), nameof(OutsourcePurchaseOrderLine.UomCode), M.Uom),
            new(typeof(OutsourcePurchaseOrderLine), nameof(OutsourcePurchaseOrderLine.CurrencyCode), M.Currency),
            new(typeof(OutsourceQuotation), nameof(OutsourceQuotation.SupplierCode), M.Company),
            new(typeof(OutsourceQuotation), nameof(OutsourceQuotation.ProductCode), M.Product),
            new(typeof(OutsourceQuotation), nameof(OutsourceQuotation.RoutingId), M.Routing),
            new(typeof(OutsourceQuotation), nameof(OutsourceQuotation.CurrencyCode), M.Currency),
            new(typeof(OutsourceQuotation), nameof(OutsourceQuotation.UomCode), M.Uom),

            // ── MFG：領退料單 ─────────────────────────────────────────────────
            new(typeof(MaterialSlip), nameof(MaterialSlip.TargetSupplierCode), M.Company),

            // ── QCR：量具序號（保管部門／保管人 2026-10-02 由自由文字改為下拉）──────
            new(typeof(InstrumentSerial), nameof(InstrumentSerial.DepartmentCode), M.Organization),
            new(typeof(InstrumentSerial), nameof(InstrumentSerial.CustodianId), M.User),
            new(typeof(MaterialSlipLine), nameof(MaterialSlipLine.ProductCode), M.Product),
            new(typeof(MaterialSlipLine), nameof(MaterialSlipLine.UomCode), M.Uom),
            new(typeof(MaterialSlipLine), nameof(MaterialSlipLine.FromWarehouseCode), M.Warehouse),
            new(typeof(MaterialSlipLine), nameof(MaterialSlipLine.FromLocationCode), M.StorageLocation,
                WarehouseProperty: nameof(MaterialSlipLine.FromWarehouseCode)),
            new(typeof(MaterialSlipLine), nameof(MaterialSlipLine.ToWarehouseCode), M.Warehouse),
            new(typeof(MaterialSlipLine), nameof(MaterialSlipLine.ToLocationCode), M.StorageLocation,
                WarehouseProperty: nameof(MaterialSlipLine.ToWarehouseCode)),

            // ── BAS：主檔之間的參照 ─────────────────────────────────────────
            new(typeof(Company), nameof(Company.CurrencyCode), M.Currency),
            new(typeof(Company), nameof(Company.TaxCode), M.Tax),
            new(typeof(Product), nameof(Product.CategoryCode), M.ProductCategory),
            new(typeof(Product), nameof(Product.StockUom), M.Uom),
            new(typeof(Product), nameof(Product.PurchaseUom), M.Uom),
            new(typeof(Product), nameof(Product.SalesUom), M.Uom),
            new(typeof(Product), nameof(Product.RoutingId), M.Routing),
            new(typeof(ProductCategory), nameof(ProductCategory.DefaultWarehouseCode), M.Warehouse),
            new(typeof(ProductCategory), nameof(ProductCategory.DefaultLocationCode), M.StorageLocation, WarehouseProperty: nameof(ProductCategory.DefaultWarehouseCode)),
            new(typeof(ProductCategory), nameof(ProductCategory.DefaultStockUomCode), M.Uom),
            new(typeof(ShiftGroupDay), nameof(ShiftGroupDay.Shift1Code), M.Shift),
            new(typeof(ShiftGroupDay), nameof(ShiftGroupDay.Shift2Code), M.Shift),
            new(typeof(ShiftGroupDay), nameof(ShiftGroupDay.Shift3Code), M.Shift),
            new(typeof(ShiftGroupDay), nameof(ShiftGroupDay.Shift4Code), M.Shift),
            new(typeof(WorkCenter), nameof(WorkCenter.DepartmentId), M.Organization),
            new(typeof(WorkCenter), nameof(WorkCenter.ShiftGroupCode), M.ShiftGroup),
            new(typeof(WorkCenter), nameof(WorkCenter.StagingWhCode), M.Warehouse),
            new(typeof(WorkCenter), nameof(WorkCenter.StagingLocationCode), M.StorageLocation, WarehouseProperty: nameof(WorkCenter.StagingWhCode)),

            // ── ORD ──────────────────────────────────────────────────────────
            // 出庫類（出貨、內部訂單）的倉庫儲位只驗存在與廠別：停用中的儲位仍要能把剩餘庫存出清
            new(typeof(SalesOrder), nameof(SalesOrder.CompanyCode), M.Company),
            new(typeof(SalesOrder), nameof(SalesOrder.TaxCode), M.Tax),
            new(typeof(SalesOrder), nameof(SalesOrder.TradeTermCode), M.CodeEntry, Category: "trade_term"),
            new(typeof(SalesOrderLine), nameof(SalesOrderLine.ProductCode), M.Product),
            new(typeof(SalesOrderLine), nameof(SalesOrderLine.UomCode), M.Uom),
            new(typeof(SalesOrderLine), nameof(SalesOrderLine.StockUomCode), M.Uom, CheckActive: false),
            new(typeof(SalesOrderLine), nameof(SalesOrderLine.TaxCode), M.Tax),
            new(typeof(SalesOrderLine), nameof(SalesOrderLine.CurrencyCode), M.Currency),
            new(typeof(GoodsShipment), nameof(GoodsShipment.CompanyCode), M.Company),
            new(typeof(GoodsShipmentLine), nameof(GoodsShipmentLine.ProductCode), M.Product),
            new(typeof(GoodsShipmentLine), nameof(GoodsShipmentLine.UomCode), M.Uom),
            new(typeof(GoodsShipmentLine), nameof(GoodsShipmentLine.StockUomCode), M.Uom, CheckActive: false),
            new(typeof(GoodsShipmentLine), nameof(GoodsShipmentLine.TaxCode), M.Tax),
            new(typeof(GoodsShipmentLine), nameof(GoodsShipmentLine.CurrencyCode), M.Currency),
            new(typeof(GoodsShipmentLine), nameof(GoodsShipmentLine.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(GoodsShipmentLine), nameof(GoodsShipmentLine.LocationCode), M.StorageLocation, WarehouseProperty: nameof(GoodsShipmentLine.WhCode), CheckActive: false),
            new(typeof(GoodsShipmentLocationDetail), nameof(GoodsShipmentLocationDetail.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(GoodsShipmentLocationDetail), nameof(GoodsShipmentLocationDetail.LocationCode), M.StorageLocation, WarehouseProperty: nameof(GoodsShipmentLocationDetail.WhCode), CheckActive: false),
            new(typeof(InternalOrder), nameof(InternalOrder.DepartmentCode), M.Organization),
            new(typeof(InternalOrderLine), nameof(InternalOrderLine.ProductCode), M.Product),
            new(typeof(InternalOrderLine), nameof(InternalOrderLine.UomCode), M.Uom),
            new(typeof(InternalOrderLine), nameof(InternalOrderLine.StockUomCode), M.Uom, CheckActive: false),
            new(typeof(InternalOrderLine), nameof(InternalOrderLine.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(InternalOrderLine), nameof(InternalOrderLine.LocationCode), M.StorageLocation, WarehouseProperty: nameof(InternalOrderLine.WhCode), CheckActive: false),
            new(typeof(InternalOrderLocationDetail), nameof(InternalOrderLocationDetail.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(InternalOrderLocationDetail), nameof(InternalOrderLocationDetail.LocationCode), M.StorageLocation, WarehouseProperty: nameof(InternalOrderLocationDetail.WhCode), CheckActive: false),
            new(typeof(SalesReturn), nameof(SalesReturn.CompanyCode), M.Company),
            new(typeof(SalesReturnLine), nameof(SalesReturnLine.ProductCode), M.Product),
            new(typeof(SalesReturnLine), nameof(SalesReturnLine.UomCode), M.Uom),
            new(typeof(SalesReturnLine), nameof(SalesReturnLine.StockUomCode), M.Uom, CheckActive: false),
            new(typeof(SalesReturnLine), nameof(SalesReturnLine.TaxCode), M.Tax),
            new(typeof(SalesReturnLine), nameof(SalesReturnLine.CurrencyCode), M.Currency),
            new(typeof(SalesReturnLine), nameof(SalesReturnLine.WhCode), M.Warehouse),
            new(typeof(SalesReturnLine), nameof(SalesReturnLine.LocationCode), M.StorageLocation, WarehouseProperty: nameof(SalesReturnLine.WhCode)),
            new(typeof(SalesReturnLocationDetail), nameof(SalesReturnLocationDetail.WhCode), M.Warehouse),
            new(typeof(SalesReturnLocationDetail), nameof(SalesReturnLocationDetail.LocationCode), M.StorageLocation, WarehouseProperty: nameof(SalesReturnLocationDetail.WhCode)),

            // ── PUR ──────────────────────────────────────────────────────────
            new(typeof(PurchaseRequestLine), nameof(PurchaseRequestLine.ProductCode), M.Product),
            new(typeof(PurchaseRequestLine), nameof(PurchaseRequestLine.UomCode), M.Uom),
            new(typeof(PurchaseRequestLine), nameof(PurchaseRequestLine.StockUomCode), M.Uom, CheckActive: false),
            new(typeof(PurchaseOrder), nameof(PurchaseOrder.SupplierCode), M.Company),
            new(typeof(PurchaseOrder), nameof(PurchaseOrder.TradeTermCode), M.CodeEntry, Category: "trade_term"),
            new(typeof(PurchaseOrderLine), nameof(PurchaseOrderLine.ProductCode), M.Product),
            new(typeof(PurchaseOrderLine), nameof(PurchaseOrderLine.UomCode), M.Uom),
            new(typeof(PurchaseOrderLine), nameof(PurchaseOrderLine.StockUomCode), M.Uom, CheckActive: false),
            new(typeof(PurchaseOrderLine), nameof(PurchaseOrderLine.TaxCode), M.Tax),
            new(typeof(PurchaseOrderLine), nameof(PurchaseOrderLine.CurrencyCode), M.Currency),
            new(typeof(GoodsReceipt), nameof(GoodsReceipt.SupplierCode), M.Company),
            new(typeof(GoodsReceiptLine), nameof(GoodsReceiptLine.ProductCode), M.Product),
            new(typeof(GoodsReceiptLine), nameof(GoodsReceiptLine.UomCode), M.Uom),
            new(typeof(GoodsReceiptLine), nameof(GoodsReceiptLine.StockUomCode), M.Uom, CheckActive: false),
            new(typeof(GoodsReceiptLine), nameof(GoodsReceiptLine.TaxCode), M.Tax),
            new(typeof(GoodsReceiptLine), nameof(GoodsReceiptLine.CurrencyCode), M.Currency),
            new(typeof(GoodsReceiptLine), nameof(GoodsReceiptLine.WhCode), M.Warehouse),
            new(typeof(GoodsReceiptLine), nameof(GoodsReceiptLine.LocationCode), M.StorageLocation, WarehouseProperty: nameof(GoodsReceiptLine.WhCode)),
            new(typeof(GoodsReceiptLocationDetail), nameof(GoodsReceiptLocationDetail.WhCode), M.Warehouse),
            new(typeof(GoodsReceiptLocationDetail), nameof(GoodsReceiptLocationDetail.LocationCode), M.StorageLocation, WarehouseProperty: nameof(GoodsReceiptLocationDetail.WhCode)),
            new(typeof(GoodsReturn), nameof(GoodsReturn.SupplierCode), M.Company),
            new(typeof(GoodsReturnLine), nameof(GoodsReturnLine.ProductCode), M.Product),
            new(typeof(GoodsReturnLine), nameof(GoodsReturnLine.UomCode), M.Uom),
            new(typeof(GoodsReturnLine), nameof(GoodsReturnLine.StockUomCode), M.Uom, CheckActive: false),
            new(typeof(GoodsReturnLine), nameof(GoodsReturnLine.TaxCode), M.Tax),
            new(typeof(GoodsReturnLine), nameof(GoodsReturnLine.CurrencyCode), M.Currency),
            new(typeof(GoodsReturnLine), nameof(GoodsReturnLine.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(GoodsReturnLine), nameof(GoodsReturnLine.LocationCode), M.StorageLocation, WarehouseProperty: nameof(GoodsReturnLine.WhCode), CheckActive: false),
            new(typeof(GoodsReturnLocationDetail), nameof(GoodsReturnLocationDetail.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(GoodsReturnLocationDetail), nameof(GoodsReturnLocationDetail.LocationCode), M.StorageLocation, WarehouseProperty: nameof(GoodsReturnLocationDetail.WhCode), CheckActive: false),
            new(typeof(OutsourceReceipt), nameof(OutsourceReceipt.SupplierCode), M.Company),
            new(typeof(OutsourceReceipt), nameof(OutsourceReceipt.CurrencyCode), M.Currency),
            new(typeof(OutsourceReceiptLine), nameof(OutsourceReceiptLine.ProductCode), M.Product),
            new(typeof(OutsourceReceiptLine), nameof(OutsourceReceiptLine.UomCode), M.Uom),
            new(typeof(OutsourceReceiptLine), nameof(OutsourceReceiptLine.StockUomCode), M.Uom, CheckActive: false),
            new(typeof(OutsourceReceiptLine), nameof(OutsourceReceiptLine.TaxCode), M.Tax),
            new(typeof(OutsourceReceiptLine), nameof(OutsourceReceiptLine.CurrencyCode), M.Currency),
            new(typeof(OutsourceReceiptLine), nameof(OutsourceReceiptLine.WhCode), M.Warehouse),
            new(typeof(OutsourceReceiptLine), nameof(OutsourceReceiptLine.LocationCode), M.StorageLocation, WarehouseProperty: nameof(OutsourceReceiptLine.WhCode)),
            new(typeof(OutsourceReceiptLocationDetail), nameof(OutsourceReceiptLocationDetail.WhCode), M.Warehouse),
            new(typeof(OutsourceReceiptLocationDetail), nameof(OutsourceReceiptLocationDetail.LocationCode), M.StorageLocation, WarehouseProperty: nameof(OutsourceReceiptLocationDetail.WhCode)),
            new(typeof(VendorPriceList), nameof(VendorPriceList.SupplierCode), M.Company),
            new(typeof(VendorPriceList), nameof(VendorPriceList.ProductCode), M.Product),
            new(typeof(VendorPriceList), nameof(VendorPriceList.CurrencyCode), M.Currency),
            // 評鑑對象可以是已停用的供應商（回顧停用前的表現）
            new(typeof(VendorEvaluation), nameof(VendorEvaluation.SupplierCode), M.Company, CheckActive: false),

            // ── INV ──────────────────────────────────────────────────────────
            // 出庫／調整類的倉庫儲位只驗存在與廠別（停用儲位仍可出清）；移入端要求仍啟用。
            // 跨廠調撥的目的倉庫屬於目的廠，不比本廠。
            new(typeof(StockTransfer), nameof(StockTransfer.FromWhCode), M.Warehouse, CheckActive: false),
            new(typeof(StockTransfer), nameof(StockTransfer.ToWhCode), M.Warehouse),
            new(typeof(StockTransferLine), nameof(StockTransferLine.ProductCode), M.Product),
            new(typeof(StockTransferLine), nameof(StockTransferLine.UomCode), M.Uom),
            new(typeof(StockTransferLine), nameof(StockTransferLine.StockUomCode), M.Uom, CheckActive: false),
            new(typeof(StockTransferLine), nameof(StockTransferLine.FromWhCode), M.Warehouse, CheckActive: false),
            new(typeof(StockTransferLine), nameof(StockTransferLine.FromLocationCode), M.StorageLocation, WarehouseProperty: nameof(StockTransferLine.FromWhCode), CheckActive: false),
            new(typeof(StockTransferLine), nameof(StockTransferLine.ToWhCode), M.Warehouse),
            new(typeof(StockTransferLine), nameof(StockTransferLine.ToLocationCode), M.StorageLocation, WarehouseProperty: nameof(StockTransferLine.ToWhCode)),
            new(typeof(CrossPlantTransfer), nameof(CrossPlantTransfer.FromWhCode), M.Warehouse, CheckActive: false),
            new(typeof(CrossPlantTransfer), nameof(CrossPlantTransfer.ToPlantId), M.Plant),
            new(typeof(CrossPlantTransfer), nameof(CrossPlantTransfer.ToWhCode), M.Warehouse, CheckPlant: false),
            new(typeof(CrossPlantTransferLine), nameof(CrossPlantTransferLine.ProductCode), M.Product),
            new(typeof(CrossPlantTransferLine), nameof(CrossPlantTransferLine.UomCode), M.Uom),
            new(typeof(CrossPlantTransferLine), nameof(CrossPlantTransferLine.StockUomCode), M.Uom, CheckActive: false),
            new(typeof(CrossPlantTransferLine), nameof(CrossPlantTransferLine.FromWhCode), M.Warehouse, CheckActive: false),
            new(typeof(CrossPlantTransferLine), nameof(CrossPlantTransferLine.FromLocationCode), M.StorageLocation, WarehouseProperty: nameof(CrossPlantTransferLine.FromWhCode), CheckActive: false),
            new(typeof(CrossPlantTransferLine), nameof(CrossPlantTransferLine.ToWhCode), M.Warehouse, CheckPlant: false),
            new(typeof(CrossPlantTransferLine), nameof(CrossPlantTransferLine.ToLocationCode), M.StorageLocation, WarehouseProperty: nameof(CrossPlantTransferLine.ToWhCode), CheckPlant: false),
            new(typeof(StockAdjustment), nameof(StockAdjustment.AdjustReasonCode), M.CodeEntry, Category: "PZ.AdjustReason"),
            new(typeof(StockAdjustment), nameof(StockAdjustment.DepartmentCode), M.Organization),
            new(typeof(StockAdjustmentLine), nameof(StockAdjustmentLine.ProductCode), M.Product),
            new(typeof(StockAdjustmentLine), nameof(StockAdjustmentLine.UomCode), M.Uom),
            new(typeof(StockAdjustmentLine), nameof(StockAdjustmentLine.StockUomCode), M.Uom, CheckActive: false),
            new(typeof(StockAdjustmentLine), nameof(StockAdjustmentLine.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(StockAdjustmentLine), nameof(StockAdjustmentLine.LocationCode), M.StorageLocation, WarehouseProperty: nameof(StockAdjustmentLine.WhCode), CheckActive: false),
            new(typeof(StockAdjustmentLocationDetail), nameof(StockAdjustmentLocationDetail.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(StockAdjustmentLocationDetail), nameof(StockAdjustmentLocationDetail.LocationCode), M.StorageLocation, WarehouseProperty: nameof(StockAdjustmentLocationDetail.WhCode), CheckActive: false),
            new(typeof(ScrapDoc), nameof(ScrapDoc.ScrapReasonCode), M.CodeEntry, Category: "PZ.ScrapReason"),
            new(typeof(ScrapDoc), nameof(ScrapDoc.DepartmentCode), M.Organization),
            new(typeof(ScrapDocLine), nameof(ScrapDocLine.ProductCode), M.Product),
            new(typeof(ScrapDocLine), nameof(ScrapDocLine.UomCode), M.Uom),
            new(typeof(ScrapDocLine), nameof(ScrapDocLine.StockUomCode), M.Uom, CheckActive: false),
            new(typeof(ScrapDocLine), nameof(ScrapDocLine.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(ScrapDocLine), nameof(ScrapDocLine.LocationCode), M.StorageLocation, WarehouseProperty: nameof(ScrapDocLine.WhCode), CheckActive: false),
            new(typeof(ScrapDocLocationDetail), nameof(ScrapDocLocationDetail.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(ScrapDocLocationDetail), nameof(ScrapDocLocationDetail.LocationCode), M.StorageLocation, WarehouseProperty: nameof(ScrapDocLocationDetail.WhCode), CheckActive: false),
            new(typeof(LoanDocLine), nameof(LoanDocLine.ProductCode), M.Product),
            new(typeof(LoanDocLine), nameof(LoanDocLine.UomCode), M.Uom),
            new(typeof(LoanDocLine), nameof(LoanDocLine.StockUomCode), M.Uom, CheckActive: false),
            new(typeof(LoanDocLine), nameof(LoanDocLine.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(LoanDocLine), nameof(LoanDocLine.LocationCode), M.StorageLocation, WarehouseProperty: nameof(LoanDocLine.WhCode), CheckActive: false),
            new(typeof(LoanDocLocationDetail), nameof(LoanDocLocationDetail.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(LoanDocLocationDetail), nameof(LoanDocLocationDetail.LocationCode), M.StorageLocation, WarehouseProperty: nameof(LoanDocLocationDetail.WhCode), CheckActive: false),
            new(typeof(SampleDocLine), nameof(SampleDocLine.ProductCode), M.Product),
            new(typeof(SampleDocLine), nameof(SampleDocLine.UomCode), M.Uom),
            new(typeof(SampleDocLine), nameof(SampleDocLine.StockUomCode), M.Uom, CheckActive: false),
            new(typeof(SampleDocLine), nameof(SampleDocLine.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(SampleDocLine), nameof(SampleDocLine.LocationCode), M.StorageLocation, WarehouseProperty: nameof(SampleDocLine.WhCode), CheckActive: false),
            new(typeof(SampleDocLocationDetail), nameof(SampleDocLocationDetail.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(SampleDocLocationDetail), nameof(SampleDocLocationDetail.LocationCode), M.StorageLocation, WarehouseProperty: nameof(SampleDocLocationDetail.WhCode), CheckActive: false),
            new(typeof(OutsourceMaterialDoc), nameof(OutsourceMaterialDoc.SupplierCode), M.Company),
            new(typeof(OutsourceMaterialDocLine), nameof(OutsourceMaterialDocLine.ProductCode), M.Product),
            new(typeof(OutsourceMaterialDocLine), nameof(OutsourceMaterialDocLine.UomCode), M.Uom),
            new(typeof(OutsourceMaterialDocLine), nameof(OutsourceMaterialDocLine.StockUomCode), M.Uom, CheckActive: false),
            new(typeof(OutsourceMaterialDocLine), nameof(OutsourceMaterialDocLine.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(OutsourceMaterialDocLine), nameof(OutsourceMaterialDocLine.LocationCode), M.StorageLocation, WarehouseProperty: nameof(OutsourceMaterialDocLine.WhCode), CheckActive: false),
            new(typeof(OutsourceMaterialDocLocationDetail), nameof(OutsourceMaterialDocLocationDetail.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(OutsourceMaterialDocLocationDetail), nameof(OutsourceMaterialDocLocationDetail.LocationCode), M.StorageLocation, WarehouseProperty: nameof(OutsourceMaterialDocLocationDetail.WhCode), CheckActive: false),
            new(typeof(ReworkMaterialDoc), nameof(ReworkMaterialDoc.WorkCenterCode), M.WorkCenter),
            new(typeof(ReworkMaterialDocLine), nameof(ReworkMaterialDocLine.ProductCode), M.Product),
            new(typeof(ReworkMaterialDocLine), nameof(ReworkMaterialDocLine.UomCode), M.Uom),
            new(typeof(ReworkMaterialDocLine), nameof(ReworkMaterialDocLine.StockUomCode), M.Uom, CheckActive: false),
            new(typeof(ReworkMaterialDocLine), nameof(ReworkMaterialDocLine.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(ReworkMaterialDocLine), nameof(ReworkMaterialDocLine.LocationCode), M.StorageLocation, WarehouseProperty: nameof(ReworkMaterialDocLine.WhCode), CheckActive: false),
            new(typeof(ReworkMaterialDocLocationDetail), nameof(ReworkMaterialDocLocationDetail.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(ReworkMaterialDocLocationDetail), nameof(ReworkMaterialDocLocationDetail.LocationCode), M.StorageLocation, WarehouseProperty: nameof(ReworkMaterialDocLocationDetail.WhCode), CheckActive: false),
            new(typeof(MaterialStagingOrder), nameof(MaterialStagingOrder.StagingWhCode), M.Warehouse),
            new(typeof(MaterialStagingOrder), nameof(MaterialStagingOrder.StagingLocationCode), M.StorageLocation, WarehouseProperty: nameof(MaterialStagingOrder.StagingWhCode)),
            new(typeof(MaterialStagingLine), nameof(MaterialStagingLine.ProductCode), M.Product),
            new(typeof(MaterialStagingLine), nameof(MaterialStagingLine.UomCode), M.Uom),
            new(typeof(MaterialStagingLine), nameof(MaterialStagingLine.StockUomCode), M.Uom, CheckActive: false),
            new(typeof(MaterialStagingLine), nameof(MaterialStagingLine.FromWarehouseCode), M.Warehouse, CheckActive: false),
            new(typeof(MaterialStagingLine), nameof(MaterialStagingLine.FromLocationCode), M.StorageLocation, WarehouseProperty: nameof(MaterialStagingLine.FromWarehouseCode), CheckActive: false),
            new(typeof(LandedCostAllocation), nameof(LandedCostAllocation.CurrencyCode), M.Currency),

            // ── QCR（Models 命名空間，ProjectZ 擁有）─────────────────────────
            // 不合格品所在倉庫儲位只驗存在與廠別（記錄現況，不是移入）
            new(typeof(NonConformanceReport), nameof(NonConformanceReport.ProductCode), M.Product),
            new(typeof(NonConformanceReport), nameof(NonConformanceReport.UomCode), M.Uom),
            new(typeof(NonConformanceReport), nameof(NonConformanceReport.DefectCode), M.DefectCode),
            new(typeof(NonConformanceReport), nameof(NonConformanceReport.WhCode), M.Warehouse, CheckActive: false),
            new(typeof(NonConformanceReport), nameof(NonConformanceReport.LocationCode), M.StorageLocation, WarehouseProperty: nameof(NonConformanceReport.WhCode), CheckActive: false),
            new(typeof(InspectionSpec), nameof(InspectionSpec.ProductCode), M.Product),
            new(typeof(InspectionSpec), nameof(InspectionSpec.SupplierCode), M.Company),
            new(typeof(InspectionSpecItem), nameof(InspectionSpecItem.UomCode), M.Uom),

            // ── FIN ──────────────────────────────────────────────────────────
            new(typeof(APInvoice), nameof(APInvoice.SupplierCode), M.Company),
            new(typeof(APInvoice), nameof(APInvoice.CurrencyCode), M.Currency),
            new(typeof(APInvoiceLine), nameof(APInvoiceLine.ProductCode), M.Product),
            new(typeof(APInvoiceLine), nameof(APInvoiceLine.UomCode), M.Uom),
            new(typeof(APInvoiceLine), nameof(APInvoiceLine.TaxCode), M.Tax),
            new(typeof(APInvoiceLine), nameof(APInvoiceLine.AccountCode), M.ChartOfAccount),
            new(typeof(ARInvoice), nameof(ARInvoice.CustomerCode), M.Company),
            new(typeof(ARInvoice), nameof(ARInvoice.CurrencyCode), M.Currency),
            new(typeof(ARInvoiceLine), nameof(ARInvoiceLine.ProductCode), M.Product),
            new(typeof(ARInvoiceLine), nameof(ARInvoiceLine.UomCode), M.Uom),
            new(typeof(ARInvoiceLine), nameof(ARInvoiceLine.TaxCode), M.Tax),
            new(typeof(ARInvoiceLine), nameof(ARInvoiceLine.AccountCode), M.ChartOfAccount),
            new(typeof(APDebitNote), nameof(APDebitNote.SupplierCode), M.Company),
            new(typeof(APDebitNote), nameof(APDebitNote.CurrencyCode), M.Currency),
            new(typeof(APDebitNoteLine), nameof(APDebitNoteLine.ProductCode), M.Product),
            new(typeof(APDebitNoteLine), nameof(APDebitNoteLine.UomCode), M.Uom),
            new(typeof(ARCreditNote), nameof(ARCreditNote.CustomerCode), M.Company),
            new(typeof(ARCreditNote), nameof(ARCreditNote.CurrencyCode), M.Currency),
            new(typeof(ARCreditNoteLine), nameof(ARCreditNoteLine.ProductCode), M.Product),
            new(typeof(ARCreditNoteLine), nameof(ARCreditNoteLine.UomCode), M.Uom),
            new(typeof(APPayment), nameof(APPayment.SupplierCode), M.Company),
            new(typeof(APPayment), nameof(APPayment.CurrencyCode), M.Currency),
            new(typeof(APPayment), nameof(APPayment.BankAccountId), M.BankAccount),
            new(typeof(ARReceipt), nameof(ARReceipt.CustomerCode), M.Company),
            new(typeof(ARReceipt), nameof(ARReceipt.CurrencyCode), M.Currency),
            new(typeof(ARReceipt), nameof(ARReceipt.BankAccountId), M.BankAccount),
            new(typeof(BankAccount), nameof(BankAccount.CurrencyCode), M.Currency),

            // ── ACT ──────────────────────────────────────────────────────────
            // 分錄的科目與幣別：自動過帳（收料、出貨、月結…）也寫這裡，只驗存在不驗啟用——
            // 否則某個科目一停用，所有業務單據的過帳都會在存檔時失敗
            new(typeof(JournalLine), nameof(JournalLine.AccountCode), M.ChartOfAccount, CheckActive: false),
            new(typeof(JournalLine), nameof(JournalLine.CurrencyCode), M.Currency, CheckActive: false),

            // ── Core 實體（規則登錄在這裡即生效，Core 檔案不必改）────────────────
            // 角色可指派到多個廠（RolePlantAssignment），所屬部門不比本廠
            new(typeof(ApplicationRole), nameof(ApplicationRole.OrganizationUnitId), M.Organization, CheckPlant: false),
        };

        /// <summary>豁免清單，取自類別上的 <see cref="ReferenceExemptAttribute"/>。</summary>
        public static readonly IReadOnlyList<ReferenceExemptAttribute> Exemptions =
            typeof(ReferenceRules).GetCustomAttributes(typeof(ReferenceExemptAttribute), false)
                .Cast<ReferenceExemptAttribute>().ToList();

        private static readonly Dictionary<Type, IReadOnlyList<ReferenceRule>> ByEntity =
            Rules.GroupBy(r => r.Entity).ToDictionary(g => g.Key, g => (IReadOnlyList<ReferenceRule>)g.ToList());

        public static IReadOnlyList<ReferenceRule> For(Type entity) =>
            ByEntity.TryGetValue(entity, out var rules) ? rules : Array.Empty<ReferenceRule>();
    }
}
