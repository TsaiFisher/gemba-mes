namespace SmartFactory.MES.Services
{
    /// <summary>
    /// 標記「本端點刻意不做權限閘門」，並強制寫下理由。
    ///
    /// 之所以用 Attribute 而不是註解，是要讓「不擋人」這個決定留在決定它的那一行旁邊，
    /// 改動端點時看得見；部署專案的一致性規則亦以此作為豁免依據。
    ///
    /// 只適用於兩種情形：
    ///   (a) 回傳的內容完全不隨角色而異，且不含業務資料（純設定／演算法查表）；
    ///   (b) 查詢範圍已被「登入者自己」鎖死（<c>WHERE UserId = 目前使用者</c>）。
    /// 只要「換一個角色來呼叫就該看到不同結果」，就不該用這個標記，請補真正的閘門。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class PermissionGateExemptAttribute : Attribute
    {
        public PermissionGateExemptAttribute(string reason) => Reason = reason;

        /// <summary>豁免理由（會被人讀，不是形式參數）。</summary>
        public string Reason { get; }
    }

    /// <summary><see cref="IPermissionService"/> 的補充判斷。</summary>
    public static class PermissionServiceExtensions
    {
        /// <summary>
        /// 任一 <paramref name="menuKeys"/> 可讀即放行。
        ///
        /// 給「被多張畫面共用的查詢端點」用。這類端點若鎖死單一 MenuKey，對於
        /// 「有 A 畫面但沒有 B 畫面」的角色會**靜默壞掉**——前端 fetch 收到 403 後
        /// 通常只是下拉沒資料／欄位帶不出來，不會有錯誤訊息，而且開發與測試都是
        /// 全權限帳號，永遠重現不出來。所以共用端點要列出所有合法呼叫端的畫面鍵。
        ///
        /// 反過來也不能因此就不擋：列出來的鍵是「白名單」，不在名單上的角色仍然被擋。
        /// </summary>
        public static async Task<bool> CanReadAny(
            this IPermissionService permissions, params string[] menuKeys)
        {
            foreach (var key in menuKeys)
                if (await permissions.CanRead(key)) return true;
            return false;
        }
    }
}
