using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules.INV.Services
{
    /// <summary>
    /// 庫存預留（指定庫存）服務 — 供 MRP 展開鎖定庫存、查詢廠級可用量。
    /// mfg-domain.md §10-6（實體鎖定）。
    /// </summary>
    public interface IInventoryReservationService
    {
        /// <summary>
        /// 廠級可用量 = Σ Inventory.QtyOnHand − Σ Active 「在手(OnHand)」預留量（不綁儲位）。
        /// 供 MRP Fstock 判斷某品號在該廠尚有多少可滿足需求。
        /// 供給端在途(Supply) peg 不在此扣減（它對應的是尚未入庫的量）。
        /// </summary>
        Task<decimal> GetPlantAvailableQtyAsync(string plantId, string productCode, CancellationToken ct = default);

        /// <summary>
        /// <see cref="GetPlantAvailableQtyAsync"/> 的批次版，並把組成拆開回傳（顯示用）：
        /// 在手（不含待驗倉）、有效在手預留、可用（＝在手 − 預留，下限 0）。口徑與單筆版逐字相同，
        /// 單元測試釘住兩者一致。清單內沒有庫存也沒有預留的品號回 0 而不是缺席。
        /// </summary>
        Task<IReadOnlyDictionary<string, PlantAvailability>> GetPlantAvailabilityAsync(
            string plantId, IReadOnlyCollection<string> productCodes, CancellationToken ct = default);

        /// <summary>
        /// 可用在途量（mfg-domain.md §24.4）＝ Σ(已核准未結案 PO 行 StockQty − ReceivedStockQty〔庫存單位〕，到貨日 ≤ byDate)
        /// − Σ(已 peg 之 Active Supply 預留)。供投產（Release）判斷缺料能否以在途補足。
        /// </summary>
        Task<decimal> GetAvailableInTransitAsync(
            string plantId, string productCode, DateOnly byDate, CancellationToken ct = default);

        /// <summary>
        /// 可用委外在途量（mfg-domain.md §24.4）＝ Σ(已確認未作廢委外採購單行 Qty − 已收量，
        /// 委外應交日 ≤ byDate) − Σ(已 peg 之 Active 委外 Supply 預留)。已收量取 Posted 委外進貨明細之 ReceiptQty。
        /// 供投產（Release）判斷缺料能否以委外在途補足。
        /// </summary>
        Task<decimal> GetAvailableOutsourceInTransitAsync(
            string plantId, string productCode, DateOnly byDate, CancellationToken ct = default);

        /// <summary>
        /// 供給端在途 peg（mfg-domain.md §24.4）：對某 PO／委外在途量建立 Category=Supply 的 Active 預留，
        /// 鎖定供給給特定需求（避免多張工單重複計同一批在途）。立即持久化。
        /// </summary>
        Task<InventoryReservation> PegSupplyAsync(
            string plantId, string productCode, decimal qty,
            ReservationSourceType sourceType, string sourceDocId, int? sourceLineSeq, DateTime requiredDate,
            string supplyDocType, string supplyDocId, int? supplyLineNo, DateOnly? expectedArrivalDate,
            CancellationToken ct = default);

        /// <summary>
        /// 到貨入庫時把供給端 peg 轉為在手預留／沖銷（mfg-domain.md §24.9(3)）。
        /// 依供給來源單(supplyDocType/supplyDocId/lineNo) 找到 Active Supply 預留，
        /// 將其 Category 轉為 OnHand（在手已實際入庫，預留改鎖在手）。以 xmin 並發保護。
        /// 回傳轉換筆數。
        /// </summary>
        Task<int> ConvertSupplyOnArrivalAsync(
            string supplyDocType, string supplyDocId, int? supplyLineNo, CancellationToken ct = default);

        /// <summary>
        /// 入庫回沖（mfg-domain.md §24.9(3)）：進貨單作廢／取消確認、收貨數量回退時，
        /// 把先前因到貨而由 Supply 轉為 OnHand 的 peg 還原為 Supply（依供給來源單
        /// supplyDocType/supplyDocId/lineNo 比對，僅還原仍 Active 的 OnHand peg；已消耗者不還原）。
        /// 為 <see cref="ConvertSupplyOnArrivalAsync"/> 的反向操作。回傳還原筆數。
        /// </summary>
        Task<int> RevertSupplyOnUnreceiveAsync(
            string supplyDocType, string supplyDocId, int? supplyLineNo, CancellationToken ct = default);

        /// <summary>
        /// 某來源單自身已鎖住的 Active「在手(OnHand)」預留量（依 SourceType + SourceDocId + 品號彙總）。
        /// 用途：<see cref="GetPlantAvailableQtyAsync"/> 扣掉「所有」Active 預留，其中也包含本單自己的預留；
        /// 判斷「本單是否備齊」時必須把自身預留加回，否則 MRP 一旦成功為該工單鎖住庫存，
        /// 該工單反而永遠被自己的預留判為缺料（mfg-domain.md §21.5／§24.2「可用＝本單預留＋自由量」）。
        /// </summary>
        Task<decimal> GetReservedBySourceAsync(
            ReservationSourceType sourceType, string sourceDocId, string productCode,
            CancellationToken ct = default);

        /// <summary>
        /// 某工單「自己已鎖住」的 Active 在手預留量（品號別）：
        /// MRP 展開為該工單鎖的 <see cref="ReservationSourceType.WorkOrderMaterial"/> 預留
        /// ＋ 來源為該工單的備料單（<see cref="ReservationSourceType.MaterialStagingOrder"/>，
        /// MaterialStagingOrder.SourceType="WorkOrder" 且 SourceId=workOrderId）預留。
        /// 備料單確認時會把工單預留轉移給備料單（見 <see cref="ReduceBySourceAsync"/>），
        /// 兩者同屬「為這張工單保留的量」，判斷本工單備齊與否時一律視為已覆蓋。
        /// </summary>
        Task<decimal> GetReservedForWorkOrderAsync(
            string workOrderId, string productCode, CancellationToken ct = default);

        /// <summary>
        /// 依來源單扣減 Active 在手預留量（依 Id 由舊到新逐筆扣，歸零者轉 Released）。
        /// 用於預留「轉移」：備料單確認時把工單的 MRP 預留轉給備料單，
        /// 避免同一批料被鎖兩次而侵蝕其他工單的可用量。回傳實際扣減量（不足時只扣得到的部分）。
        /// </summary>
        Task<decimal> ReduceBySourceAsync(
            ReservationSourceType sourceType, string sourceDocId, string productCode, decimal qty,
            CancellationToken ct = default);

        /// <summary>
        /// 領料過帳時消耗該工單保留的在手預留量（發料已實際扣掉在手庫存，預留必須等額退場，
        /// 否則同一批料被「庫存減少」與「預留仍在」重複扣減，全廠可用量長期低估）。
        /// 消耗順序：先扣該工單的備料單預留（備料確認時已由工單預留轉移過去），
        /// 不足部分再扣工單自身的 <see cref="ReservationSourceType.WorkOrderMaterial"/> 預留。
        /// 回傳實際消耗量（預留不足時只消耗得到的部分，不拋例外——超領／未預留的補領屬正常業務）。
        /// </summary>
        Task<decimal> ConsumeForWorkOrderAsync(
            string workOrderId, string productCode, decimal qty, CancellationToken ct = default);

        /// <summary>
        /// 釋放該工單所有備料單來源的 Active 預留（工單完工／結案時呼叫）。
        /// 工單自身的 <see cref="ReservationSourceType.WorkOrderMaterial"/> 預留由呼叫端另行釋放；
        /// 備料單接手的那部分若不一併釋放，會在工單結案後永久殘留。回傳釋放筆數。
        /// </summary>
        Task<int> ReleaseWorkOrderStagingAsync(string workOrderId, CancellationToken ct = default);

        /// <summary>建立一筆 Active 預留（MRP 展開鎖定庫存）。立即持久化，使後續同品號需求看得到扣減。</summary>
        Task<InventoryReservation> ReserveAsync(
            string plantId, string productCode, decimal qty,
            ReservationSourceType sourceType, string sourceDocId, int? sourceLineSeq, DateTime requiredDate,
            CancellationToken ct = default);

        /// <summary>釋放某來源單的所有 Active 預留（MRP 重跑、需求取消、提早投產重算用）。回傳釋放筆數。</summary>
        Task<int> ReleaseBySourceAsync(ReservationSourceType sourceType, string sourceDocId, CancellationToken ct = default);

        /// <summary>
        /// 精確釋放單一明細的預留（mfg-domain.md §21.1）。
        /// 用於 SO/IO 結案時，依 MrpRunLine 的 (MrpRunId, LineSeq) 釋放對應庫存預留。
        /// </summary>
        Task<int> ReleaseBySourceLineAsync(
            ReservationSourceType sourceType, string sourceDocId, int lineSeq,
            CancellationToken ct = default);

        /// <summary>
        /// 出貨不得拿走保留給其他需求的成品（mfg-domain.md §24.2a）：每個品號要出的量
        /// ≤ 廠級在手（不含待驗倉）− 其他需求的在手預留；本單訂單列自己的預留可以出。
        /// 預留只在廠級，不落儲位——出貨揀貨看的儲位庫存不知道這些預留，所以要在過帳前另外擋。
        /// 不足時擲 <see cref="InvalidOperationException"/>，訊息寫出品號、要出量、可出量與保留給其他需求的量。
        /// </summary>
        Task EnsureShipmentWithinFreeStockAsync(string plantId,
            IEnumerable<(string ProductCode, string? SalesOrderId, int? SoLineNo, decimal Qty)> lines,
            CancellationToken ct = default);

        /// <summary>
        /// 出貨過帳：消耗該銷貨訂單列（經 MRP 需求列）為成品保留的在手預留，由舊到新逐筆扣（mfg-domain.md §24.2a）。
        /// 不消耗的話，在手已出、預留仍掛著，同一批成品對廠級可用量重複扣減。回傳實際消耗量。
        /// </summary>
        Task<decimal> ConsumeForSalesOrderLineAsync(
            string salesOrderId, int lineNo, string productCode, decimal qty, CancellationToken ct = default);

        /// <summary>出貨作廢：把出貨時消耗的量重新鎖回該銷貨訂單列（最新一次 MRP 需求列）；訂單列已結案則不鎖。</summary>
        Task<decimal> RestoreForSalesOrderLineAsync(
            string plantId, string salesOrderId, int lineNo, string productCode, decimal qty, CancellationToken ct = default);
    }

    /// <summary>廠級可用量的組成（庫存單位）。</summary>
    public sealed record PlantAvailability(decimal OnHand, decimal Reserved)
    {
        public decimal Available => Math.Max(0m, OnHand - Reserved);
    }
}
