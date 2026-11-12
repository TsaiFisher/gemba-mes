namespace SmartFactory.MES.Modules.INV.Models
{
    /// <summary>單據狀態（庫存相關單據共用）</summary>
    public enum DocStatus
    {
        /// <summary>草稿（業務可修改）</summary>
        Draft = 0,

        /// <summary>已確認（等待倉庫作業）</summary>
        Confirmed = 1,

        /// <summary>在途（僅跨廠調撥使用：來源廠已出庫、目的廠尚未入庫）</summary>
        InTransit = 2,

        /// <summary>已過帳（庫存已異動完成）</summary>
        Posted = 3,

        /// <summary>已作廢（以反向 StockMovement 沖銷）</summary>
        Void = 4
    }
}
