namespace SmartFactory.MES.Modules.INV.ViewModels;

public class StockShortageRowVm
{
    public string  ProductCode  { get; init; } = "";
    public string  ProductName  { get; init; } = "";
    public string  UomCode      { get; init; } = "";
    public decimal SafetyStock  { get; init; }
    /// <summary>在手量（不含待驗倉）</summary>
    public decimal QtyOnHand    { get; init; }
    /// <summary>有效在手預留（InventoryReservation，與 MRP 同口徑）</summary>
    public decimal QtyReserved  { get; init; }
    /// <summary>目前可用＝在手 − 預留，下限 0</summary>
    public decimal QtyAvailable { get; init; }
    /// <summary>目前缺口＝安全存量 − 目前可用</summary>
    public decimal ShortageQty  => SafetyStock - QtyAvailable;

    /// <summary>採購在途：截止日前到貨、已核准未結案 PO 行未收量，扣除已被需求認領的量（與投產料閘同一函式）</summary>
    public decimal QtyIncomingPo  { get; init; }
    /// <summary>委外在途：截止日前應交、已確認委外採購單未收量，扣除已被需求認領的量（與投產料閘同一函式）</summary>
    public decimal QtyIncomingOpo { get; init; }
    /// <summary>預計可用＝目前可用＋採購在途＋委外在途</summary>
    public decimal QtyProjected => QtyAvailable + QtyIncomingPo + QtyIncomingOpo;
    /// <summary>預計缺口＝安全存量 − 預計可用，下限 0（0 表示在途已補足）</summary>
    public decimal ProjectedShortageQty => Math.Max(0m, SafetyStock - QtyProjected);
}
