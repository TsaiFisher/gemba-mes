namespace SmartFactory.MES.Modules.QCR.Services
{
    /// <summary>IQC 待檢明細（來源 = Confirmed 進貨單／待驗中的委外收貨且尚未檢驗的明細，已套用抽樣計畫決策）。</summary>
    public record IqcPendingLine(
        string   GoodsReceiptId,
        string?  TypeCode,
        DateOnly ReceiptDate,
        int      LineNo,
        string   ProductCode,
        decimal  Qty,
        string?  UomCode,
        int      VerificationLevel,
        int      RecommendedSampleSize,
        string   SamplingState,
        // 來源單別：GoodsReceipt（一般進貨）／OutsourceReceipt（委外收貨，2026-10-12）。
        // GoodsReceiptId 欄位兩者共用（單號前綴不同：PE-／OR-），舊呼叫端不必改。
        string   SourceKind = IqcSourceKinds.GoodsReceipt);

    public static class IqcSourceKinds
    {
        public const string GoodsReceipt     = "GoodsReceipt";
        public const string OutsourceReceipt = "OutsourceReceipt";
    }

    /// <summary>IQC 檢驗結果登錄輸入（手機與桌機共用）。</summary>
    public record IqcSubmitInput(
        string  GoodsReceiptId,
        int     LineNo,
        string  ProductCode,
        decimal LotSize,
        decimal SampleQty,
        decimal FailQty,
        int     VerificationLevel,
        string? Remarks);

    /// <summary>
    /// IQC 登錄結果。Success = false 時 ErrorCode 供呼叫端決定 HTTP 狀態碼 / 在地化訊息。
    /// ErrorCode：INVALID_REQUEST（缺進貨單號）/ INVALID_QTY（抽樣數 ≤ 0）/ IQC_ALREADY_DONE（重複檢驗）。
    /// </summary>
    public record IqcSubmitOutcome(
        bool    Success,
        string? ErrorCode,
        string  Message,
        string  RecordId = "",
        string  InspectionOrderId = "",
        string  Result = "",
        int     SampleSize = 0,
        decimal FailQty = 0,
        int     VerificationLevel = 0);

    /// <summary>
    /// IQC 進料檢驗共用服務（MIL-STD-1916，Ac=0）。
    /// 手機（api/mobile/inv/iqc）與桌機（QCR/InspectionOrder）共用同一套抽樣、判定、
    /// VL 切換回寫與品質隔離放行邏輯，避免兩端算法漂移。
    /// </summary>
    public interface IIqcInspectionService
    {
        /// <summary>取得本廠 IQC 待檢明細（已扣除跳批與已檢驗者）。</summary>
        Task<IReadOnlyList<IqcPendingLine>> GetPendingLinesAsync(string plantId, CancellationToken ct = default);

        /// <summary>
        /// 登錄 IQC 檢驗結果：建立 InspectionOrder + MobileIqcRecord、更新 VL 切換狀態，
        /// 合格時把待驗倉庫存放行至進貨明細指定倉別。
        /// </summary>
        Task<IqcSubmitOutcome> SubmitAsync(IqcSubmitInput input, string plantId, CancellationToken ct = default);
    }
}
