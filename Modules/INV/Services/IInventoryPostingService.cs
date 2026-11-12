using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules.INV.Services
{
    /// <summary>
    /// 庫存過帳引擎。負責 LocationDetail → StockMovement + Inventory 更新。
    /// 支援完整模式（逐筆過帳）與簡化模式（一步到位）。
    /// </summary>
    public interface IInventoryPostingService
    {
        /// <summary>
        /// 完整模式：過帳單一 LocationDetail（倉庫每次儲存時呼叫）。
        /// 產生 StockMovement + 更新 Inventory + 更新 Line.QtyPutAway。
        /// 所有 Line 完成 → DocStatus 自動轉 Posted。
        /// </summary>
        Task PostLineDetailAsync(PostLineDetailRequest request, CancellationToken ct = default);

        /// <summary>
        /// 簡化模式：自動用 DefaultLocationCode 為所有 Line 建立 LocationDetail 並過帳。
        /// DocStatus 直接設為 Posted。
        /// </summary>
        Task PostDocSimplifiedAsync(PostDocSimplifiedRequest request, CancellationToken ct = default);

        /// <summary>
        /// 作廢已過帳單據：產生反向 StockMovement 沖銷，DocStatus 設為 Void。
        /// </summary>
        Task VoidDocAsync(VoidDocRequest request, CancellationToken ct = default);

        /// <summary>同廠調撥過帳：每行產生 WP（撥出）＋ WI（撥入）一對異動，含期間鎖檢查。</summary>
        Task PostSamePlantTransferAsync(PostSamePlantTransferRequest request, CancellationToken ct = default);

        /// <summary>跨廠調撥發出：產生 XP（撥出）異動，含期間鎖檢查。</summary>
        Task PostCrossPlantShipAsync(PostCrossPlantShipRequest request, CancellationToken ct = default);

        /// <summary>跨廠調撥收貨：產生 XI（撥入）異動，成本從既有 XP 記錄取得，含期間鎖檢查。</summary>
        Task PostCrossPlantReceiveAsync(PostCrossPlantReceiveRequest request, CancellationToken ct = default);

        /// <summary>依呼叫端已篩選的 StockMovement 清單產生反向沖銷，含期間鎖檢查。</summary>
        Task VoidByMovementsAsync(IReadOnlyList<StockMovement> movements, VoidByMovementsRequest request, CancellationToken ct = default);
    }

    /// <summary>完整模式過帳請求</summary>
    public class PostLineDetailRequest
    {
        public required string PlantId { get; set; }
        public required string DocNo { get; set; }
        public required int LineNo { get; set; }
        public required string ProductCode { get; set; }
        public required string LocationCode { get; set; }
        public string LotNo { get; set; } = "";
        /// <summary>過帳的法定數量（庫存單位）。庫存/成本/沖銷/上架皆以此為準。</summary>
        public required decimal Qty { get; set; }
        /// <summary>原始業務單位數量（採購/銷售單位）。僅供 StockMovement 忠實留痕；未提供時以 Qty 視之。</summary>
        public decimal? BusinessQty { get; set; }
        public required string TypeCode { get; set; }
        public string? TypeCategory { get; set; }
        public required DateOnly MovementDate { get; set; }
        public string UomCode { get; set; } = "";
        public string StockUomCode { get; set; } = "";
        public decimal? UnitPrice { get; set; }
        /// <summary>
        /// 入庫時併入單位成本、但**不再產生分錄**的既有存貨價值（本幣總額，非單價）。
        /// 語意與 <see cref="SimplifiedPostLine.CarriedInCost"/> 完全相同，只是走完整模式
        /// （收貨確認 → 倉庫上架）這條路：委外收成品在完整模式下同樣要把在外原料的價值
        /// 併回成品，否則那筆料錢會永久留在存貨科目而沒有任何實體庫存承接它。
        /// 分批上架時由呼叫端按本次上架量攤分。
        /// </summary>
        public decimal CarriedInCost { get; set; }
        /// <summary>
        /// <see cref="UnitPrice"/> 的交易幣兌本位幣匯率（本幣單據為 1）。
        ///
        /// 庫存成本、移動平均與 FIFO 成本層都沒有幣別欄位——它們的計價幣別只能有一種，
        /// 就是本位幣。外幣採購若把交易幣單價直接當成本記進去，存貨（1401）會是外幣數字、
        /// 而應付認列端（2152 借方）是本幣，暫估應付結構上永遠沖不平，且金額差得離譜到
        /// 不會有人懷疑是「單位」問題。故單價一律在計價前乘上本欄換算成本幣。
        /// </summary>
        public decimal ExchangeRate { get; set; } = 1m;
        public string? CreatedBy { get; set; }
        public string? SourceDocId { get; set; }
        public int? SourceLineNo { get; set; }

        /// <summary>回寫用：過帳後更新 Line.QtyPutAway 的 callback</summary>
        public required Func<decimal, Task> UpdateQtyPutAwayAsync { get; set; }

        /// <summary>回寫用：檢查所有 Line 是否完成，若完成則更新 DocStatus 的 callback</summary>
        public required Func<Task<bool>> CheckAllLinesCompleteAsync { get; set; }

        /// <summary>回寫用：設定 DocStatus = Posted 的 callback</summary>
        public required Action SetDocPosted { get; set; }
    }

    /// <summary>簡化模式過帳請求（每個 Line 一筆）</summary>
    public class PostDocSimplifiedRequest
    {
        public required string PlantId { get; set; }
        public required string DocNo { get; set; }
        public required DateOnly MovementDate { get; set; }
        public required string TypeCode { get; set; }
        public string? TypeCategory { get; set; }
        public string? CreatedBy { get; set; }
        public required List<SimplifiedPostLine> Lines { get; set; }

        /// <summary>
        /// 工單的發退料（來源是工單的發退料單）：料只是進出在製品、仍在存貨科目裡，**不產生分錄**，
        /// 由完工入庫結算（mfg-domain.md §16.3／§16.4b）。一般工單的 <c>Production</c> 類別本來就不入帳；
        /// 這個旗標是給重工工單的 RI／RR 用的——同樣的異動類別在庫存模組的重工發退料單（不屬於任何工單、
        /// 料發出去就不會回來）要入「重工損失」，只看類別分不出兩者。
        /// </summary>
        public bool IsWorkOrderMaterial { get; set; }

        /// <summary>回寫用：設定 DocStatus = Posted 的 callback</summary>
        public required Action SetDocPosted { get; set; }
    }

    public class SimplifiedPostLine
    {
        public required int LineNo { get; set; }
        public required string ProductCode { get; set; }
        public required string LocationCode { get; set; }
        public required string WhCode { get; set; }
        public string LotNo { get; set; } = "";
        /// <summary>過帳的法定數量（庫存單位）。庫存/成本/沖銷皆以此為準。</summary>
        public required decimal Qty { get; set; }
        /// <summary>原始業務單位數量（採購/銷售單位）。僅供 StockMovement 忠實留痕；未提供時以 Qty 視之。</summary>
        public decimal? BusinessQty { get; set; }
        public string UomCode { get; set; } = "";
        public string StockUomCode { get; set; } = "";
        public decimal? UnitPrice { get; set; }
        /// <summary>同 <see cref="PostLineDetailRequest.ExchangeRate"/>：單價的兌本位幣匯率。</summary>
        public decimal ExchangeRate { get; set; } = 1m;
        public string? SourceDocId { get; set; }
        public int? SourceLineNo { get; set; }

        /// <summary>覆蓋文件層級的 Direction（+1 或 -1）。用於調整單（IA）每行可獨立指定方向。</summary>
        public int? DirectionOverride { get; set; }

        /// <summary>
        /// 入庫時併入單位成本、但**不再產生分錄**的既有存貨價值（本幣總額，非單價）。
        ///
        /// 用於「價值早就在存貨科目裡、只是換了個形態」的入庫。目前唯一的來源是委外收貨：
        /// 委外發料屬 Outsource 類，庫存數量出而總帳不動（<c>BuildJournalLines</c> 回空），
        /// 也就是說在外原料的價值一直留在存貨科目；收回成品時若只認加工費，這筆料錢就
        /// 再也沒有落點——庫存明細帳合計會永久少於總帳存貨科目，差額正好是被加工掉的原料，
        /// 而成品帶著低估的單位成本出貨，COGS 跟著低估（mfg-domain.md §19.5：
        /// 委外工單成本 = 材料 + 加工費）。
        ///
        /// 併入的是**存貨計價**（移動均價、StockMovement.UnitCost），分錄金額仍只認
        /// UnitPrice 的部分——重複入帳會讓存貨科目憑空長大，且應付只欠加工費。
        /// </summary>
        public decimal CarriedInCost { get; set; }

        /// <summary>回寫用：設定 Line.QtyPutAway 的 callback</summary>
        public required Action<decimal> SetQtyPutAway { get; set; }
    }

    /// <summary>作廢請求</summary>
    public class VoidDocRequest
    {
        public required string PlantId { get; set; }
        public required string DocNo { get; set; }
        public string? Reason { get; set; }
        public string? CreatedBy { get; set; }

        /// <summary>回寫用：設定 DocStatus = Void 的 callback</summary>
        public required Action SetDocVoid { get; set; }
    }

    // ── Transfer 過帳 ────────────────────────────────────────────────────────

    /// <summary>同廠調撥過帳請求（WP 撥出 + WI 撥入）</summary>
    public class PostSamePlantTransferRequest
    {
        public required string PlantId { get; set; }
        public required string DocNo { get; set; }
        public required DateOnly MovementDate { get; set; }
        public string? CreatedBy { get; set; }
        public required List<TransferLine> Lines { get; set; }
        public required Action SetDocPosted { get; set; }
    }

    public class TransferLine
    {
        public required int LineNo { get; set; }
        public required string ProductCode { get; set; }
        public required string FromLocationCode { get; set; }
        public required string ToLocationCode { get; set; }
        /// <summary>撥出批號；空字串＝不追蹤批號</summary>
        public string LotNo { get; set; } = "";
        /// <summary>
        /// 撥入批號；null／空白＝與撥出相同（一般調撥不換批號）。
        /// 換批號用於既有庫存裝箱／換箱（container-tracking.md §9.4）：數量與單位成本原樣帶過去。
        /// </summary>
        public string? ToLotNo { get; set; }
        public required decimal StockQty { get; set; }
        public string UomCode { get; set; } = "";
        public string StockUomCode { get; set; } = "";
        public required Action<decimal> SetQtyPutAway { get; set; }
    }

    /// <summary>跨廠調撥發出請求（XP 撥出，Status → InTransit）</summary>
    public class PostCrossPlantShipRequest
    {
        public required string PlantId { get; set; }
        public required string DocNo { get; set; }
        public required DateOnly MovementDate { get; set; }
        public string? CreatedBy { get; set; }
        public required List<CrossPlantShipLine> Lines { get; set; }
        public required Action SetDocInTransit { get; set; }
    }

    public class CrossPlantShipLine
    {
        public required int LineNo { get; set; }
        public required string ProductCode { get; set; }
        public required string FromLocationCode { get; set; }
        /// <summary>批號（空字串＝不追蹤）；兩廠相同</summary>
        public string LotNo { get; set; } = "";
        public required decimal StockQty { get; set; }
        public string UomCode { get; set; } = "";
        public string StockUomCode { get; set; } = "";
        public required Action<decimal> SetQtyShipped { get; set; }
    }

    /// <summary>跨廠調撥收貨請求（XI 撥入，Status → Posted）。UnitCost 由服務從 XP 記錄取得。</summary>
    public class PostCrossPlantReceiveRequest
    {
        public required string ToPlantId { get; set; }
        public required string DocNo { get; set; }
        public required DateOnly MovementDate { get; set; }
        public string? CreatedBy { get; set; }
        public required List<CrossPlantReceiveLine> Lines { get; set; }
        public required Action SetDocPosted { get; set; }
    }

    public class CrossPlantReceiveLine
    {
        public required int LineNo { get; set; }
        public required string ProductCode { get; set; }
        public required string ToLocationCode { get; set; }
        /// <summary>批號（空字串＝不追蹤）；與撥出相同</summary>
        public string LotNo { get; set; } = "";
        public required decimal StockQty { get; set; }
        public string UomCode { get; set; } = "";
        public string StockUomCode { get; set; } = "";
        public required Action<decimal> SetQtyReceived { get; set; }
    }

    /// <summary>依已載入的 StockMovement 清單執行反向沖銷</summary>
    public class VoidByMovementsRequest
    {
        public required DateOnly VoidDate { get; set; }
        public string? Reason { get; set; }
        public string? CreatedBy { get; set; }
        /// <summary>回寫用：完成沖銷後設定 DocStatus 的 callback。為 null 時（如部分撤回）略過。</summary>
        public Action? SetDocVoid { get; set; }
    }
}
