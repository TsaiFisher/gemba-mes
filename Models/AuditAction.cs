namespace SmartFactory.MES.Models
{
    /// <summary>
    /// 稽核日誌 <see cref="AuditLog.Action"/> 欄位的單一來源。
    ///
    /// <para><b>在此之前這個欄位沒有來源，只有一堆字面值</b>：寫入端各寫各的
    /// （<c>AuditInterceptor</c> 直接寫 EF 的 <c>EntityState.ToString()</c>、登入／登出頁
    /// 與手機 API 各自手打字串），顯示端又各自列舉 if/else。兩邊對不上時沒有任何訊號——
    /// 列表頁的 if 鏈只認 Added／Modified／Deleted 又<b>沒有 else</b>，於是
    /// Login／LoginFailed／Logout／Login2faRequired 那幾百筆的「操作」欄位一直是空白格；
    /// 而 <c>MobilePermissionController</c> 手寫的 <c>"Update"</c>（全站其他地方叫
    /// <c>"Modified"</c>）連篩選下拉都選不到，等於那兩筆稽核查不出來。</para>
    ///
    /// <para>因此新增一種 Action 的完整步驟是：在此登錄常數 → 加進 <see cref="All"/> →
    /// 在 <see cref="ResourceKey"/> 與 <see cref="BadgeCss"/> 補對應 → 補
    /// <c>Resources/Views/AuditLog/_ActionBadge.*.resx</c> 的譯文。
    /// 漏掉任何一步都會被 <c>AuditActionGuardTests</c> 擋下。</para>
    /// </summary>
    public static class AuditAction
    {
        // ── 資料異動 ──
        // 值必須與 EF 的 EntityState 名稱逐字相同：AuditInterceptor 由 EntityState 映射而來，
        // 且既有資料表裡存的就是這三個字串，改名會讓歷史紀錄查不到。
        public const string Added = "Added";
        public const string Modified = "Modified";
        public const string Deleted = "Deleted";

        // ── 身分驗證 ──
        public const string Login = "Login";
        public const string LoginFailed = "LoginFailed";
        public const string Login2faRequired = "Login2faRequired";
        public const string Logout = "Logout";

        /// <summary>
        /// 全部已知值，並兼作顯示順序：篩選下拉由此產生，各 View 的 badge 也由此判讀。
        /// </summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            Added,
            Modified,
            Deleted,
            Login,
            LoginFailed,
            Login2faRequired,
            Logout,
        };

        /// <summary>
        /// 在地化資源鍵。資料異動三種沿用既有的 Create／Update／Delete 鍵，
        /// <b>刻意不跟著 EntityState 改名</b>——那三個鍵在多個 resx 已有譯文，
        /// 改名只會讓既有譯文全部失效。未知值回傳 <c>null</c>，由呼叫端原樣顯示字串本身。
        /// </summary>
        public static string? ResourceKey(string? action) => action switch
        {
            Added => "Create",
            Modified => "Update",
            Deleted => "Delete",
            Login => "Login",
            LoginFailed => "LoginFailed",
            Login2faRequired => "Login2faRequired",
            Logout => "Logout",
            _ => null,
        };

        /// <summary>
        /// badge 的 Bootstrap 樣式。未知值給淡色而不是空字串——
        /// 看得見的陌生字串才有人會去追，消失的格子沒有人會發現。
        /// </summary>
        public static string BadgeCss(string? action) => action switch
        {
            Added => "bg-success",
            Modified => "bg-warning text-dark",
            Deleted => "bg-danger",
            Login => "bg-primary",
            LoginFailed => "bg-danger",
            Login2faRequired => "bg-warning text-dark",
            Logout => "bg-secondary",
            _ => "bg-light text-dark",
        };
    }
}
