namespace SmartFactory.MES.Modules.ACT.Services
{
    /// <summary>單筆資料稽核異常明細（business_data_flow_reconciliation.md §4）。</summary>
    public class AuditFailureDetail
    {
        /// <summary>稽核類型代碼，例如 PO_OVER_RECEIPT / GRIR_AGING / INV_ROLLFORWARD。</summary>
        public string AuditType { get; set; } = string.Empty;

        /// <summary>異常單據號或聚合鍵（如 PO 單號、品號@儲位）。</summary>
        public string DocId { get; set; } = string.Empty;

        /// <summary>異常品號（無品號維度的稽核為 null）。</summary>
        public string? ProductCode { get; set; }

        /// <summary>異常所屬廠別。</summary>
        public string? PlantId { get; set; }

        /// <summary>一致性差額（數量或金額，依 AuditType 而定）。</summary>
        public decimal DifferenceAmount { get; set; }

        /// <summary>
        /// 錯誤描述（含期望值與實際值），**正體中文固定值**。
        /// 由 <see cref="MessageKey"/>＋<see cref="MessageArgs"/> 以中性資源組出，
        /// 不是另一份手寫句子——改字只改 resx。給關帳失敗訊息、日誌與舊資料相容用。
        /// 要跟著使用者語系走的顯示，請用 <see cref="IAuditMessageFormatter.Format"/>。
        /// </summary>
        public string ErrorDescription { get; set; } = string.Empty;

        /// <summary>訊息鍵（<see cref="AuditMessages"/> 的常數）。</summary>
        public string MessageKey { get; set; } = string.Empty;

        /// <summary>訊息參數，**已格式化的字串**（見 <see cref="AuditMessages"/>）。</summary>
        public IReadOnlyList<string> MessageArgs { get; set; } = [];

        /// <summary>
        /// 逐筆覆寫嚴重度；<c>null</c> 表示照 <see cref="AuditSeverityRules"/> 的型別規則走。
        ///
        /// 存在的理由是「同一種異常，落在期間治理範圍外時不該擋關帳」——那不是型別層級的判斷，
        /// 是逐筆的日期判斷，型別對照表表達不了。目前唯一的使用者是
        /// <c>AuditShipmentInvoiceGapAsync</c>：早於該廠最早會計期間的出貨單。
        /// 覆寫成提示而**不是**直接從結果裡濾掉——那些單據仍然是真的沒有發票，
        /// 該讓人在稽核報表上看得到，只是不能用它擋住一個它本來就管不到的期間。
        /// </summary>
        public bool? BlockingOverride { get; set; }

        /// <summary>是否擋關帳；預設由 <see cref="AuditSeverityRules"/> 依 AuditType 判定，
        /// <see cref="BlockingOverride"/> 有值時以它為準。</summary>
        public bool IsBlocking => BlockingOverride ?? AuditSeverityRules.IsBlocking(AuditType);
    }

    /// <summary>
    /// 稽核嚴重度規則：哪些 AuditType 只是提示、不擋關帳。
    ///
    /// 分界線是「錯的是本系統的帳，還是對外介面的狀態」。前者不修就不能結帳；
    /// 後者帳本身完整且平衡，擋住關帳只是把一個外部系統的可用性變成本系統的死結。
    ///
    /// 刻意用「型別 → 嚴重度」的靜態對照，而不是在 DataAuditFindings 加欄位：
    /// 嚴重度是規則不是事實，它會隨判斷改變；存進歷史紀錄反而會讓舊資料與新規則打架。
    /// </summary>
    public static class AuditSeverityRules
    {
        /// <summary>
        /// 只提示、不擋關帳的稽核類型。
        ///
        /// ERP_SYNC_LAG：AP/AR 發票逾 24 小時未拋轉至外部 ERP。列為提示的理由是實測出來的——
        /// 全庫只有 FINModule 那一行 DI 註冊用到 <c>IErpSyncService</c>，沒有任何 controller、
        /// 排程或背景服務呼叫過 <c>MarkSyncedAsync</c>。發票的 ErpSyncStatus 生下來是 Pending
        /// 就永遠是 Pending，於是每張發票在開立 24 小時後都變成一筆關帳阻擋，且每天只增不減。
        /// 掛成硬閘的實際效果不是「守住拋轉」，而是「關帳這件事在任何部署上都做不到」——
        /// 2026-09-06 在 dev 庫實測 ADM 廠 25 筆異常裡有 19 筆是它。
        ///
        /// INV_GL_DOC_DIFF：總帳存貨對明細帳的「找來源」明細。擋關帳的是同廠的 INV_GL_SUBLEDGER（殘差），
        /// 明細只是指路；兩者都擋會讓同一個差額算兩次，修掉殘差前「還要修幾筆」也對不起來。
        ///
        /// 唯一的解除辦法 <c>MarkExcludedAsync</c> 同樣沒有畫面入口，所以連逐張排除都做不到。
        /// 之後若真的接上 ERP 橋接，該補的是「拋轉失敗要看得到、要能重送」，
        /// 而不是把關帳綁在一個外部系統的回應上。
        /// </summary>
        public static readonly IReadOnlySet<string> AdvisoryAuditTypes =
            new HashSet<string>(StringComparer.Ordinal) { "ERP_SYNC_LAG", "INV_GL_DOC_DIFF" };

        /// <summary>該稽核類型是否擋關帳（未列入提示清單者一律擋）。</summary>
        public static bool IsBlocking(string auditType) => !AdvisoryAuditTypes.Contains(auditType);
    }

    /// <summary>資料稽核執行結果。</summary>
    public class DataAuditResult
    {
        /// <summary>
        /// 是否通過。只看擋關帳的那些——提示類異常照樣列在 <see cref="Failures"/> 裡給人看，
        /// 但不能讓它決定關不關得成。
        /// </summary>
        public bool IsPassed => !Failures.Any(f => f.IsBlocking);

        public IList<AuditFailureDetail> Failures { get; } = new List<AuditFailureDetail>();

        /// <summary>擋關帳的異常（關帳失敗訊息只該報這些，否則數字會與「還要修幾筆」對不起來）。</summary>
        public IList<AuditFailureDetail> BlockingFailures
            => Failures.Where(f => f.IsBlocking).ToList();
    }

    /// <summary>
    /// 業務資料流一致性稽核服務（business_data_flow_reconciliation.md §3/§4）。
    /// 所有查詢皆 IgnoreQueryFilters + 明確 plantId 過濾（RPT 慣例）；
    /// plantId 傳 null 時稽核全部廠別。
    /// 數量容忍值 0.0001（NUMERIC(18,4) 最小單位）、金額容忍值 0.01（本位幣最小精度）。
    /// </summary>
    public interface IDataAuditService
    {
        /// <summary>月結前稽核（§4）：執行全部檢核，任何異常即不通過；由 ClosePeriodAsync 呼叫擋關帳。</summary>
        /// <summary>
        /// 關帳前稽核。**必須帶 plantId**：期間依廠別分開之後，甲廠關帳不該被乙廠的異常擋住
        /// （原本傳 plantId: null 等於全公司一起稽核）。
        /// </summary>
        Task<DataAuditResult> RunPreClosingAuditAsync(string periodId, string plantId, CancellationToken ct = default);

        /// <summary>執行全部稽核檢核（日常診斷用）。</summary>
        Task<DataAuditResult> RunAllAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>§3.1 採購超收：累計有效收料（收料−退貨）不得大於採購量；排除草稿/作廢 GR。</summary>
        Task<List<AuditFailureDetail>> AuditPoOverReceiptAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>§3.2 GR/IR：發票數量溢開（任何時點）＋收料超過 30 天金額未核銷（排除作廢發票）。</summary>
        Task<List<AuditFailureDetail>> AuditGrIrAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>§3.3 委外在外原料平衡：發料(OO Posted)−退料(OR Posted)−理論消耗(收貨×RoutingBom 用量)不得為負。</summary>
        Task<List<AuditFailureDetail>> AuditOutsourceBalanceAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>§3.4 傳票借貸平衡：每張 JournalEntry ΣDebit 必須等於 ΣCredit。</summary>
        Task<List<AuditFailureDetail>> AuditJournalBalanceAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>重複立帳：一對一來源單據（APPayment/ARReceipt/WorkOrderCompletion/LandedCostAllocation）不得產生多張自動傳票。</summary>
        Task<List<AuditFailureDetail>> AuditDuplicateJournalAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>庫存滾動平衡：Σ(StockMovement.StockQty×Direction) 依（廠別,品號,儲位,批號）須等於 Inventory.QtyOnHand。</summary>
        Task<List<AuditFailureDetail>> AuditInventoryRollForwardAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>
        /// 總帳存貨科目對庫存明細帳（金額）：見 <see cref="InventoryGlReconciler"/> 的對帳式。
        /// <list type="bullet">
        ///   <item><c>INV_GL_SUBLEDGER</c>（廠）：扣除在製、客供料、借出後仍有殘差。</item>
        ///   <item><c>INV_GL_DOC_DIFF</c>（單據，提示）：殘差不為 0 時，列出總帳與異動價值不等、又不屬於調整項的單據。</item>
        ///   <item><c>INV_GL_WIP_NEGATIVE</c>／<c>INV_GL_WIP_CLOSED</c>（工單）：在製為負、或結案／作廢後仍留在製。</item>
        /// </list>
        /// </summary>
        Task<List<AuditFailureDetail>> AuditInventoryGlSubledgerAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>
        /// §3.15 FIFO 成本層總量必須等於在手量（逐廠逐品號，只查 FIFO 計價的品號）。
        ///
        /// 為什麼需要：這個不變量是 FIFO 計價的地基——出庫要從成本層扣，層若比在手少，
        /// 之後出庫會扣到不存在的層（或被守門擋下而過不了帳）；層若比在手多，
        /// 多出來的成本永遠留在帳上，出庫時還會用到早該消失的舊層單價。
        ///
        /// 為什麼沒有別的地方會叫：`INV_ROLLFORWARD` 是**數量型**稽核，只看
        /// `Inventories.QtyOnHand` 與異動加總對不對得上——成本層根本不在它的視野裡；
        /// 而傳票逐張借貸平衡，因為錯的是「用哪個單價」不是「有沒有記帳」。
        /// 2026-09-05 人工驗收 4-7 在 dev 庫實測到 3 個品號對不上（最大 600 個），
        /// 形狀正是「作廢只沖數量不沖成本」（W3-F2，已修）會留下的殘留。
        ///
        /// 刻意以**廠級**（不分儲位）比對：成本層以儲位為鍵，而備料移到暫存儲位
        /// 只搬數量不搬層，逐儲位比會產生一整批不是缺陷的差異。
        /// </summary>
        Task<List<AuditFailureDetail>> AuditFifoLayerBalanceAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>
        /// 客供異動（`CI`／`CR`）落在不屬客供倉的儲位：移動平均的成本池是「全廠×品號」，
        /// 客供數量會進到均價分母、讓自有料單價偏低並擴散到之後每一張出貨的 COGS。
        /// 隔離維度取 `WarehouseType.CustomerSupplied`。**分批號不管用**——分母是全廠同品號
        /// 加總，批號不在成本池的鍵裡。⚠️ 2026-09-24 訂正：原本這句連「分品號」一起寫成
        /// 「不管用」是**寫過頭了**——成本池的鍵 `(PlantId, ProductCode)` 本來就含品號，
        /// 換品號確實隔得開（manual-test-plan §13.9 記的是「可行但那是繞路」）。
        /// 不取品號的理由是代價不是做不到：客供與自有本來就是同一個物料，只是所有權不同，
        /// 分品號要為它維護兩套主檔、兩套 BOM，而全庫沒有替代料機制。處置是把該批庫存調撥到客供倉。
        ///
        /// 與過帳守門的分工：`InventoryPostingService` ③-b 擋未來，本稽核找過去的遺留。
        ///
        /// 已掛進 `RunAllAsync`（2026-09-22）＝沒調倉就不准關帳。業主確認現行資料
        /// 沒有客供單，所以不存在「既有部署被擋在月結當天」的風險。
        /// </summary>
        Task<List<AuditFailureDetail>> AuditCustomerSuppliedLocationAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>§1.2-1 銷售超出貨：累計有效出貨（出貨−退回）不得大於訂單量；排除草稿/作廢 GS。</summary>
        Task<List<AuditFailureDetail>> AuditSoOverShipmentAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>§1.2-2 出貨確認未開立 AR 發票（超過 1 天；排除作廢發票）。</summary>
        Task<List<AuditFailureDetail>> AuditShipmentInvoiceGapAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>§3.8 Backflush 後沖消耗核對：已入庫工單的 IO 後沖淨額（DocNo=工單+"-BF"）須等於 RequiredQty×(完工量/工單量)。不分後沖時機——結案時的累計應沖量與時機無關。</summary>
        Task<List<AuditFailureDetail>> AuditBackflushConsumptionAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>§3.9-1 PO 反正規化漂移：PurchaseOrderLine.ReceivedQty 須等於非作廢 GR 明細 ReceiptQty 彙總。</summary>
        /// <summary>採購明細「已收數量」應為多少（非作廢進貨單加總）；稽核與自動修正共用。</summary>
        Task<Dictionary<(string PurchaseOrderId, int LineNo), decimal>> ExpectedPoReceivedQtyAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>訂單明細「已出數量」應為多少（非作廢出貨單加總）；稽核與自動修正共用。</summary>
        Task<Dictionary<(string SalesOrderId, int LineNo), decimal>> ExpectedSoShippedQtyAsync(string? plantId = null, CancellationToken ct = default);

        Task<List<AuditFailureDetail>> AuditPoReceivedQtyDriftAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>§3.9-2 SO 反正規化漂移：SalesOrderLine.ShippedQty 須等於非作廢 GS 明細 ShipQty 彙總。</summary>
        Task<List<AuditFailureDetail>> AuditSoShippedQtyDriftAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>§3.10 WIP 期末快照一致性（僅關帳前）：期間完工成本已翻 Final、在製工單必有本期快照、
        /// 快照不指向非在製工單、分量不得為負。由 RunPreClosingAuditAsync 帶入關帳期間呼叫。</summary>
        Task<List<AuditFailureDetail>> AuditWipSnapshotConsistencyAsync(string periodId, string? plantId = null, CancellationToken ct = default);

        /// <summary>§3.5 ERP 拋轉滯留：已確立超過 24 小時仍 Pending/Failed 的 APInvoice 與 ARInvoice
        /// （排除作廢/Excluded；AR 為 §9 P-3 D3 最小範圍擴大）。</summary>
        Task<List<AuditFailureDetail>> AuditErpSyncLagAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>§3.11 逐工單成本回溯（P-2）：
        /// 材料腿＝ActualMaterial vs 庫存異動子帳淨成本（後沖 DocNo=工單-BF ＋ Push/Pull 發退料單
        /// 的 Production movement，依方向淨計）——生產物料異動不落日記帳，故核子帳；
        /// 人工/製費腿＝Labor+Overhead vs 吸收科目（5110/5120）淨額，歸集完工吸收分錄
        /// （WorkOrderCompletion）＋期末重估調整逐工單拆行（JournalLine.SourceRef=工單號，D2-A），
        /// Preliminary/Final 皆核。</summary>
        Task<List<AuditFailureDetail>> AuditWoCostJournalDriftAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>
        /// §3.12 應領未領（P-6）：已入庫（Closed）工單中，Push／Pull 用料**一顆都沒領**
        /// （<c>IssuedQty − ReturnedQty ≤ 0</c> 而 <c>RequiredQty &gt; 0</c>）的情形。
        ///
        /// 為什麼需要：報工完全不看領料，報滿即自動結案、工單自動完工，於是備料單留在
        /// Draft（自動建單填不出倉儲，本來就要人補）也照樣能完工入庫，成品以「只有人工製費」
        /// 的成本進倉、存貨低估。而所有既有防線都攔不到——投產料況硬閘（§24）看的是**可用量**
        /// 而非已領量（料還在倉庫裡當然夠）、報工守門只比派工量、§3.8 只管 Backflush 料、
        /// WIP 恆等式兩腿都是 0 故完全一致、傳票沒有材料那一張所以每張都平衡。
        ///
        /// 門檻刻意訂在「完全沒領」而不是「領得比 BOM 少」：超領不設上限（不良重投與耗損補發
        /// 屬正常製造場景），少領同理也可能是正常的；唯獨一顆都沒領而工單完工，
        /// 必定是漏了發料這個動作。Backflush 料不在範圍內——它本來就不領，由 §3.8 負責。
        /// </summary>
        Task<List<AuditFailureDetail>> AuditWorkOrderMaterialIssueAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>
        /// §3.13 銷退入庫計價偏離（P-2）：銷貨退回入庫的單位成本 ≠ 來源出貨異動實際扣掉的單位成本。
        ///
        /// 為什麼需要：銷退單上沒有成本欄位（畫面帶的是**售價**），入庫成本只能由系統自己找。
        /// 找錯的時候三件事同時發生而沒有任何守門會叫——存貨科目與庫存明細帳**一起**被灌成售價
        /// （兩邊一致地錯，對帳對不出來）、反向沖回的銷貨成本大於當初認列的、移動平均被帶偏
        /// 使**後續每一次出貨**的成本都跟著錯。借貸平衡稽核不會叫（兩種寫法各自都平衡），
        /// 數量型的 INV_ROLLFORWARD 也不會叫（數量完全正確）。
        ///
        /// 計價來源已修（見 <c>SalesReturnCostBasis</c>），故新單不會再偏；本稽核針對的是
        /// **既有資料**——已過帳的傳票不能改寫，只能列出來讓人決定怎麼調整。
        /// 未連結來源出貨的手工退回行不在範圍（無從比對）。
        /// </summary>
        Task<List<AuditFailureDetail>> AuditSalesReturnCostBasisAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>
        /// 退貨已過帳，但來源發票的應收／應付**沒有被回沖**。
        ///
        /// 為什麼需要：<c>ReverseForReturnedQtyAsync</c> 對已收款／已付款的發票是刻意不動的
        /// ——改已結清發票的金額會讓憑證與稅務申報前後不一致，正解是另開折讓單。
        /// 但「該回沖而沒回沖」這件事本身必須有人追：貨已經退回來了，錢卻還掛在帳上，
        /// 公司實際上欠客戶（或廠商欠公司）一筆錢，而總帳每一張傳票都平衡、
        /// 應收帳齡看起來也正常，沒有任何既有檢查在比這件事。
        ///
        /// 判定方式刻意**看資料而不是看呼叫結果**：比對「發票明細目前的數量」與
        /// 「出貨量 − 已退回量」，差額即為未回沖。這樣連「根本沒呼叫到回沖服務」
        /// 的路徑（例如將來新增的第五個退貨入口）也一起蓋到。
        ///
        /// 掛進 <c>RunAllAsync</c> 的意思是**未處理就不准關帳**：折讓單開了、
        /// 或發票被作廢重開之後，這一筆自然消失。
        /// </summary>
        Task<List<AuditFailureDetail>> AuditUnreversedReturnSubledgerAsync(string? plantId = null, CancellationToken ct = default);

        /// <summary>
        /// 孤兒過帳：過帳資料所指向的來源單據已不存在。
        ///
        /// StockMovement 與 JournalEntry 對單據都是**弱關聯**（DocNo / SourceDocId 字串，無外鍵，
        /// 見 ApplicationDbContextExtensions StockMovement 只建索引不建 FK），因此資料庫層
        /// 不會阻止「單據被刪除後留下庫存異動與傳票」。守門 100% 依賴各 Controller 的
        /// Delete/Save 檢查（QtyPutAway &gt; 0 / Status != Draft / 下游拋轉量），任一路徑漏寫
        /// 都會靜默留下爛帳且無任何約束報錯——本稽核即為該情境的最後一道防線。
        ///
        /// 判定採**反向集合比對**而非 TypeCode 映射：TypeCode 取自使用者可維護的
        /// InventoryTransactionType 且跨單據重複（SalesReturn 與 GoodsReceipt 都用 "GR"），
        /// 不是可靠的型別鍵；改以「單號是否存在於任一單據表」判定，不需維護映射表。
        /// </summary>
        Task<List<AuditFailureDetail>> AuditOrphanPostingAsync(string? plantId = null, CancellationToken ct = default);
    }
}
