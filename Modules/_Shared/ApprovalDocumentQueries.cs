using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules._Shared
{
    /// <summary>
    /// 簽核單據的共用查詢。
    /// </summary>
    public static class ApprovalDocumentQueries
    {
        /// <summary>
        /// 從資料庫讀單據當前的簽核狀態（單據不存在時回 <c>null</c>）。
        ///
        /// Save 的「審核中不得修改」守衛**必須**用這個，不可用 vm 帶回的 ApprovalStatus：
        /// 各 _Form 的簽核狀態是 hidden 欄位，而且刻意放在 <c>&lt;fieldset disabled&gt;</c>
        /// 外面（disabled 的欄位瀏覽器不送，放裡面存檔會少欄位），所以它是使用者可改的。
        /// 自行組一個 POST 把 PendingApproval 改成 Draft 就能改掉一張審核中的單，
        /// 存檔後狀態又被 <c>ResolveSaveStatusAsync</c> 寫回 PendingApproval，
        /// 而簽核申請那筆記錄完全沒動——簽核人核准的是他沒看過的內容，事後也看不出來改過。
        ///
        /// 刻意做成 <see cref="ApplicationDbContext"/> 的擴充方法，而不是 ModuleControllerBase
        /// 的 protected 方法：base 的 <c>Db</c> 走 <c>HttpContext.RequestServices</c> 延遲解析，
        /// 而各 Controller 的單元測試是直接 new 出 controller、不帶 HttpContext 的，
        /// 掛在 base 上會讓那些測試全部倒在 <c>GetRequiredService</c>（已實測 7 支）。
        /// 用 Controller 自己注入的 context 則單元測試原樣可跑，也讀得到測試自己種的資料。
        ///
        /// 走一般查詢（吃廠別與資料權限兩層全域過濾器），因此看不到的單據回 null。
        /// 那不是漏洞：守衛放行後，更新路徑同樣查不到那張單，會以「找不到單據」失敗。
        ///
        /// 刻意不用 <c>FindAsync</c>：那會把實體放進 identity map，
        /// 後續 Update 路徑若重建同鍵實體再 <c>Update()</c> 會撞「已被追蹤」。
        /// 這裡只投影出一個列舉值，不帶回實體。
        /// </summary>
        public static async Task<DocumentApprovalStatus?> LoadApprovalStatusAsync<T>(
            this ApplicationDbContext db, string? id)
            where T : class, IApprovalDocument
        {
            if (string.IsNullOrWhiteSpace(id)) return null;   // 新增單據，無既有狀態可讀

            var key = db.Model.FindEntityType(typeof(T))?.FindPrimaryKey();
            if (key == null || key.Properties.Count != 1)
                throw new InvalidOperationException(
                    $"{typeof(T).Name} 不是單一主鍵，無法以 LoadApprovalStatusAsync 讀取簽核狀態。");

            var keyName = key.Properties[0].Name;
            return await db.Set<T>().AsNoTracking()
                .Where(e => EF.Property<string>(e, keyName) == id)
                .Select(e => (DocumentApprovalStatus?)EF.Property<DocumentApprovalStatus>(
                    e, nameof(IApprovalDocument.ApprovalStatus)))
                .FirstOrDefaultAsync();
        }
    }
}
