namespace SmartFactory.MES.Modules._Shared
{
    /// <summary>
    /// 「單據型別 → 該單據 Details 頁網址」的**唯一來源**。
    ///
    /// <para><b>為什麼要有這個類別</b>：這張表原本只以 JS 字面存在於
    /// <c>Views/Approval/_DocDetailsUrls.cshtml</c>，於是只有桌機的兩張簽核清單用得到。
    /// 手機的待簽核清單需要同一份對應，而把它抄成第二份 JS 的下場是慢慢分岔——
    /// 分岔的症狀還特別難看：某個單別在其中一邊點下去沒反應，另一邊正常，
    /// 而「點了沒反應」本來就沒有錯誤訊息（見 <c>PendingIndex.cshtml</c> 的 <c>if (!urlFn) return;</c>）。
    /// 所以改成 C# 一份，Razor 那支 partial 由它產生 JS，手機 API 直接呼叫它。</para>
    ///
    /// <para><b>樣板只有 <c>{id}</c> 一個佔位</b>，刻意不支援更複雜的組法。
    /// 唯一看起來需要特例的是 <c>CodeEntry</c>（DocumentId 是 <c>類別/代碼</c> 的自然鍵，
    /// 原本的 JS 特地 <c>split('/')</c> 再組成兩段路徑）——但把含斜線的 id 直接代進
    /// <c>/CodeEntry/Details/{id}</c> 得到的字串**一模一樣**，所以不需要特例。</para>
    ///
    /// <para>⚠️ <b>DocumentType 不一定等於 Controller 名稱。</b>
    /// <c>NonConformanceReport</c> 的控制器叫 <c>Ncr</c>、<c>MaterialStagingOrder</c> 的叫
    /// <c>MaterialStaging</c>——這兩個正是先前漏掉沒登錄的單別，漏掉的原因就是名字對不上。
    /// 新增單別時請照 <c>ApprovalDocumentLinkCoverageTests</c> 的守門走，不要憑名字推。</para>
    /// </summary>
    public static class ApprovalDocumentLinks
    {
        /// <summary>DocumentType → Details 頁樣板（<c>{id}</c> 會被單號取代）。</summary>
        public static readonly IReadOnlyDictionary<string, string> Templates =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // PUR
                ["PurchaseRequest"]     = "/PurchaseRequest/Details/{id}",
                ["PurchaseOrder"]       = "/PurchaseOrder/Details/{id}",
                ["GoodsReceipt"]        = "/GoodsReceipt/Details/{id}",
                ["GoodsReturn"]         = "/GoodsReturn/Details/{id}",
                ["OutsourceReceipt"]    = "/OutsourceReceipt/Details/{id}",
                ["VendorEvaluation"]    = "/VendorEvaluation/Details/{id}",

                // BAS／Core 主檔
                ["CodeEntry"]           = "/CodeEntry/Details/{id}",
                ["Company"]             = "/Company/Details/{id}",
                ["Product"]             = "/Product/Details/{id}",
                ["Tax"]                 = "/Tax/Details/{id}",

                // INV
                ["ScrapDoc"]            = "/ScrapDoc/Details/{id}",
                ["LoanDoc"]             = "/LoanDoc/Details/{id}",
                ["SampleDoc"]           = "/SampleDoc/Details/{id}",
                ["StockAdjustment"]     = "/StockAdjustment/Details/{id}",
                ["OutsourceMaterial"]   = "/OutsourceMaterial/Details/{id}",
                ["ReworkMaterial"]      = "/ReworkMaterial/Details/{id}",
                ["StockTransfer"]       = "/StockTransfer/Details/{id}",
                ["CrossPlantTransfer"]  = "/CrossPlantTransfer/Details/{id}",
                ["LandedCostAllocation"] = "/LandedCostAllocation/Details/{id}",
                ["MaterialStagingOrder"] = "/MaterialStaging/Details/{id}",

                // ORD
                ["SalesOrder"]          = "/SalesOrder/Details/{id}",
                ["GoodsShipment"]       = "/GoodsShipment/Details/{id}",
                ["SalesReturn"]         = "/SalesReturn/Details/{id}",
                ["InternalOrder"]       = "/InternalOrder/Details/{id}",

                // MFG
                ["WorkOrder"]           = "/WorkOrder/Details/{id}",

                // QCR
                ["NonConformanceReport"] = "/Ncr/Details/{id}",

                // FIN
                ["APInvoice"]           = "/APInvoice/Details/{id}",
                ["ARInvoice"]           = "/ARInvoice/Details/{id}",
                ["APPayment"]           = "/APPayment/Details/{id}",
                ["ARReceipt"]           = "/ARReceipt/Details/{id}",
            };

        /// <summary>
        /// 組出單據的 Details 頁網址；沒有登錄的單別回 <c>null</c>。
        ///
        /// <para>回 <c>null</c> 而不是丟例外：呼叫端是簽核清單，
        /// 為了一個沒登錄的單別讓整張清單開不起來，比少一個連結糟得多。
        /// 但**呼叫端必須把 null 呈現成「這一筆沒有連結」**，不能只是讓點擊靜靜失效——
        /// 桌機原本就是後者，於是那 5 個沒登錄的單別看起來像畫面壞了。</para>
        /// </summary>
        public static string? Resolve(string? documentType, string? documentId)
        {
            if (string.IsNullOrEmpty(documentType) || string.IsNullOrEmpty(documentId)) return null;
            return Templates.TryGetValue(documentType, out var t)
                ? t.Replace("{id}", documentId, StringComparison.Ordinal)
                : null;
        }
    }
}
