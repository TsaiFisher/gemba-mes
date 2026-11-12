namespace SmartFactory.MES.Modules.INV.ViewModels
{
    /// <summary>庫存餘額查詢結果列（唯讀）。</summary>
    public class InventoryBalanceRowVm
    {
        public string ProductCode  { get; set; } = "";
        public string ProductName  { get; set; } = "";
        public string StockUom     { get; set; } = "";
        public string WhCode       { get; set; } = "";
        public string LocationCode { get; set; } = "";
        public string LocationName { get; set; } = "";
        public string LotNo        { get; set; } = "";
        public decimal QtyOnHand    { get; set; }
        public decimal UnitCost     { get; set; }
    }

    /// <summary>
    /// 品號彙總（全廠）。預留是「廠 × 品號」層級（InventoryReservation 不記儲位），所以只能在這一層顯示；
    /// 原本逐儲位列的預留量／可用量讀的是從沒人寫入的 Inventory.QtyReserved（恆 0），可用量恆等於在手量（2026-10-27 拿掉）。
    /// 口徑與 MRP／投產料閘／缺貨分析相同：IInventoryReservationService.GetPlantAvailabilityAsync。
    /// </summary>
    public class InventoryBalanceProductSummaryVm
    {
        public string ProductCode  { get; set; } = "";
        public string ProductName  { get; set; } = "";
        public string StockUom     { get; set; } = "";
        /// <summary>全廠在手（不含待驗倉）</summary>
        public decimal PlantOnHand    { get; set; }
        /// <summary>全廠有效在手預留</summary>
        public decimal PlantReserved  { get; set; }
        /// <summary>全廠可用＝在手 − 預留，下限 0</summary>
        public decimal PlantAvailable { get; set; }
    }
}
