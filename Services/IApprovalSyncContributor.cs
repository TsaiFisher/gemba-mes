using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// 簽核中央同步擴充點：當 ApprovalService.ProcessApproval 完成終局狀態
    /// （Approved / Rejected / ReturnedForRevision）時，會逐一呼叫已註冊的 Contributor。
    /// 顧問模組為自家業務文件實作此介面 + DI 註冊（AddSingleton/AddScoped）即可享有中央同步。
    /// 相對於 Controller 端 sync-on-read，這個機制即時且無重複碼。
    /// </summary>
    public interface IApprovalSyncContributor
    {
        /// <summary>處理哪一種 DocumentType（與 SubmitForApproval 傳入的 documentType 對應）</summary>
        string DocumentType { get; }

        /// <summary>回寫業務文件狀態。`finalStatus` 為終局 ApprovalStatus；不處理 Pending。</summary>
        Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default);
    }
}
