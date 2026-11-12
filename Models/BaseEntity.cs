namespace SmartFactory.MES.Models
{
    public interface IPlantEntity
    {
        string? PlantId { get; set; }
    }

    public interface IDataAuthEntity
    {
        string? CreatedByRoleId { get; set; }
        string? OrganizationUnitPath { get; set; }
    }

    /// <summary>
    /// ERP 憑證同步狀態（§5.4）。
    /// </summary>
    public enum ErpSyncStatus
    {
        Pending  = 0,   // 待拋轉
        Synced   = 1,   // 已成功同步，取得 ErpVoucherNo
        Failed   = 2,   // 拋轉失敗（可重試）
        Excluded = 3    // 手動排除，不再拋轉
    }

    /// <summary>
    /// 需要拋轉至外部 ERP 的實體（§2.1, §5.4）。
    /// APInvoice、StockMovement 等財務/庫存單據實作此介面。
    /// </summary>
    public interface IErpSyncEntity
    {
        ErpSyncStatus ErpSyncStatus   { get; set; }
        string?       ErpVoucherNo    { get; set; }   // ERP 回寫的傳票號
        DateTime?     ErpSyncTime     { get; set; }   // 成功拋轉時間
        string?       ErpErrorMessage { get; set; }   // 最後一次失敗訊息
        int           ErpRetryCount   { get; set; }   // 累計重試次數
    }

    public abstract class BaseEntity : IPlantEntity, IDataAuthEntity
    {
        public int Id { get; set; }
        public string? PlantId { get; set; }
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
