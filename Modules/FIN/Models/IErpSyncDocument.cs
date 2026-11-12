using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.FIN.Models
{
    /// <summary>
    /// 可拋轉 ERP 的財務單據（§9 P-3，reconciliation-decisions.md D3）。
    /// 在 Core 的 IErpSyncEntity（狀態機欄位）之上補足泛型化 ErpSyncService 所需的
    /// 金額（尾差比對）與 UpdatedAt（異動時間回寫）——Core 介面不動，於 FIN 模組擴充。
    /// </summary>
    public interface IErpSyncDocument : IErpSyncEntity
    {
        /// <summary>單據總額（與 ERP 傳票金額做尾差比對）。</summary>
        decimal TotalAmount { get; }

        DateTime UpdatedAt { get; set; }
    }
}
