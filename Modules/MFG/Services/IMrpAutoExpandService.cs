namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>自動展開的結果（mfg-domain.md §25.5）。</summary>
    /// <param name="Triggered">是否實際建立並展開了一張 MRP 執行單。</param>
    /// <param name="MrpRunId">建立的執行單號（未觸發時為 null）。</param>
    /// <param name="LineCount">納入的訂單需求列數。</param>
    /// <param name="Error">展開失敗時的訊息；成功為 null。</param>
    // readonly record struct（非 class）：R5b 只掃 public class 的隱含必填值型別屬性，
    // 結果型 DTO 用 struct 可免去為 Triggered/LineCount 補多語鍵的雜訊（同 FpiGateResult / OperationSequenceCheck）。
    public readonly record struct MrpAutoExpandResult(bool Triggered, string? MrpRunId, int LineCount, string? Error);

    /// <summary>
    /// 訂單核准後自動展開 MRP（mfg-domain.md §25.5，僅簡化模式生效）。
    ///
    /// **以狀態判定、不以事件判定**：每次呼叫都重算「已核准訂單中尚未展開的剩餘量」
    /// （沿用 MRP 執行畫面「可匯入訂單列」的同一口徑），沒有剩餘就什麼都不做。
    /// 因此重複呼叫安全，也不需要在每一條核准路徑上都掛鉤——漏掛一條只會延後，不會遺漏。
    /// 這是刻意避開「多條完成路徑各掛一次、漏掉其中一條就靜默少資料」的老問題。
    ///
    /// **不處理訂單「修改」**：維持 §21.6 只標 `IsDirty` 提示人工重跑。
    /// 重跑有 §10-5 的 L1/L2/L3 足跡分級閘門，自動觸發會踩到已投產工單與已收貨採購單。
    /// </summary>
    public interface IMrpAutoExpandService
    {
        /// <summary>
        /// 掃描已核准訂單的未展開剩餘量，建立一張 MRP 執行單並立即展開。
        /// 簡化模式未開啟、或無任何未展開需求時，回傳 <c>Triggered = false</c> 且不異動資料。
        /// 展開失敗不拋出——訂單核准本身已經成功，不該因為後續自動展開失敗而讓使用者以為沒存檔；
        /// 失敗原因以 <c>Error</c> 回報給呼叫端提示。
        /// </summary>
        Task<MrpAutoExpandResult> ExpandApprovedOrdersAsync(CancellationToken ct = default);
    }
}
