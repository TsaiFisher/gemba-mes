namespace SmartFactory.MES.Modules.RPT.Models
{
    public class DashboardKpi
    {
        public decimal MonthlyRevenue       { get; set; }
        public decimal MonthlyPurchase      { get; set; }
        public decimal InventoryValue       { get; set; }
        public decimal WorkOrderOnTimeRate  { get; set; }
        public int     OpenWorkOrders       { get; set; }
        public int     OverdueAP            { get; set; }  // placeholder until FIN
        public List<MonthlyTrendPoint> RevenueTrend  { get; set; } = new();
        public List<MonthlyTrendPoint> PurchaseTrend { get; set; } = new();
    }

    public record MonthlyTrendPoint(string Month, decimal Amount);
}
