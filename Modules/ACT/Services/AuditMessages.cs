namespace SmartFactory.MES.Modules.ACT.Services
{
    /// <summary>
    /// 稽核發現描述的訊息鍵（<c>ProjectZAuditResource</c> 的鍵名）。
    ///
    /// <para><b>為什麼不直接存句子</b>：<c>DataAuditFinding.ErrorDescription</c> 是**寫進資料庫**的，
    /// 由每日背景排程產生。背景服務沒有 UI 語系，句子在產生當下就定死了——使用者把介面切成英文，
    /// 稽核明細那一欄仍然是中文，而且**已經存進去的列永遠改不了**。
    /// 所以持久化的是「鍵＋參數」，句子在顯示當下才組出來。</para>
    ///
    /// <para><b>參數一律是已格式化的字串</b>：小數位數與日期格式屬於「這筆異常的事實」，
    /// 不該隨顯示語系變動（<c>{0:F4}</c> 這種格式指示詞若留在模板裡，
    /// 兩種語言各自寫一次就會各自漂）。呼叫端自己 <c>ToString("F4")</c>／<c>ToString("yyyy-MM-dd")</c>，
    /// 模板只保留 <c>{0}</c>。</para>
    ///
    /// <para><b>語氣差異是刻意的</b>：中文句尾的「——…」是給現場人員的處置提示，
    /// 英文改寫成整句而非逐字對譯，因為逐字翻出來的破折號子句在英文讀起來不像指示。</para>
    ///
    /// <para>鍵的完整性由 <c>AuditMessageResourceTests</c> 守門：這裡的每一個常數，
    /// zh-TW／en-US／中性三份 resx 都必須有值，且參數個數三份一致。</para>
    /// </summary>
    public static class AuditMessages
    {
        public const string PoOverReceipt = "PoOverReceipt";
        public const string GrIrOverInvoice = "GrIrOverInvoice";
        public const string GrIrAging = "GrIrAging";
        public const string OutsourceNegBalance = "OutsourceNegBalance";
        public const string JournalUnbalanced = "JournalUnbalanced";
        public const string JournalDuplicate = "JournalDuplicate";
        public const string InvRollforward = "InvRollforward";
        public const string InvRollforwardNoLot = "InvRollforwardNoLot";
        public const string SoOverShipment = "SoOverShipment";
        public const string GsNoArInvoice = "GsNoArInvoice";
        public const string GsNoArInvoicePreOpen = "GsNoArInvoicePreOpen";
        public const string BackflushMismatch = "BackflushMismatch";
        public const string WoMaterialNotIssued = "WoMaterialNotIssued";
        public const string SalesReturnCostBasis = "SalesReturnCostBasis";
        public const string FifoLayerMismatch = "FifoLayerMismatch";
        public const string CustomerSuppliedWrongLocation = "CustomerSuppliedWrongLocation";
        public const string ArInvoiceUnreversed = "ArInvoiceUnreversed";
        public const string ApInvoiceUnreversed = "ApInvoiceUnreversed";
        public const string PoReceivedDrift = "PoReceivedDrift";
        public const string PoReceivedOrphan = "PoReceivedOrphan";
        public const string SoShippedDrift = "SoShippedDrift";
        public const string SoShippedOrphan = "SoShippedOrphan";
        public const string WipCostNotFinal = "WipCostNotFinal";
        public const string WipSnapshotMissing = "WipSnapshotMissing";
        public const string WipSnapshotStale = "WipSnapshotStale";
        public const string WipSnapshotNegative = "WipSnapshotNegative";
        public const string WipVoidLeakage = "WipVoidLeakage";
        public const string WipIssuedNegative = "WipIssuedNegative";
        public const string WipIdentityDrift = "WipIdentityDrift";
        public const string ErpSyncLagAp = "ErpSyncLagAp";
        public const string ErpSyncLagApWithError = "ErpSyncLagApWithError";
        public const string ErpSyncLagAr = "ErpSyncLagAr";
        public const string ErpSyncLagArWithError = "ErpSyncLagArWithError";
        public const string WoCostMaterialLeg = "WoCostMaterialLeg";
        public const string WoCostConversionLeg = "WoCostConversionLeg";
        public const string OrphanStockMovement = "OrphanStockMovement";
        public const string OrphanJournalEntry = "OrphanJournalEntry";
        public const string OrphanInvoiceAp = "OrphanInvoiceAp";
        public const string OrphanInvoiceAr = "OrphanInvoiceAr";
        public const string InvGlSubledgerResidual = "InvGlSubledgerResidual";
        public const string InvGlDocDiff = "InvGlDocDiff";
        public const string InvGlWipNegative = "InvGlWipNegative";
        public const string InvGlWipClosed = "InvGlWipClosed";
        public const string InvGlWipVoided = "InvGlWipVoided";
    }
}
