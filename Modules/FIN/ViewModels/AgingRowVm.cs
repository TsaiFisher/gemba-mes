namespace SmartFactory.MES.Modules.FIN.ViewModels
{
    public class AgingRowVm
    {
        public string PartyCode  { get; set; } = string.Empty;  // 廠商或客戶代號
        public decimal Current   { get; set; }   // 未到期
        public decimal Days30    { get; set; }   // 1-30 天
        public decimal Days60    { get; set; }   // 31-60 天
        public decimal Days90    { get; set; }   // 61-90 天
        public decimal Days90Plus { get; set; }  // 90 天以上
        public decimal Total     { get; set; }   // 合計
    }
}
