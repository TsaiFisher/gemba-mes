using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>工序用料 — RoutingOperation 子表，三層複合 PK</summary>
    public class RoutingBom
    {
        [Required, MaxLength(30)]
        public string RoutingId { get; set; } = string.Empty;

        public int OperationSeq { get; set; }

        public int BomSeq { get; set; }

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,6)")]
        public decimal Quantity { get; set; }

        [Required, MaxLength(20)]
        public string UomCode { get; set; } = string.Empty;

        /// <summary>
        /// 損耗率 %（ScrapRate）：加工過程中消失、無殘值的耗損。與 <see cref="OffcutRate"/> 併用時
        /// 採全外加法（投入量 = 用量 × (1 + OffcutRate/100 + ScrapRate/100)）——兩者同等視為
        /// 以淨需求為基準往上加的固定比例，見 <see cref="ApplyLossRates"/>。
        /// </summary>
        [Column(TypeName = "decimal(8,4)")]
        public decimal ScrapRate { get; set; } = 0M;

        /// <summary>
        /// 下角率 %（OffcutRate）：投入料裁切/加工後產生的可回收下腳料比率（如板材/皮革/布料邊料），
        /// 與純損耗不同——下角料有殘值，僅拉高投入量以確保扣除下角料後仍取得足量良品，
        /// 目前不建下角料品號、不自動入庫、不影響成本（純粹的投入量計算參數）。
        /// 全外加法：投入量 = 用量 × (1 + OffcutRate/100 + ScrapRate/100)，見 <see cref="ApplyLossRates"/>。
        /// </summary>
        [Column(TypeName = "decimal(8,4)")]
        public decimal OffcutRate { get; set; } = 0M;

        /// <summary>
        /// 發料方式（三層 Fallback：RoutingBom → Product → PlantSetting）。
        /// null 表示依下層 fallback 決定，mfg-domain.md §3.1 / §17.3。
        /// </summary>
        public MaterialIssueMethod? IssueMethod { get; set; }

        /// <summary>後沖時機（IssueMethod = Backflush 時生效，預設 AtCompletion）。</summary>
        public BackflushTiming BackflushTiming { get; set; } = BackflushTiming.AtCompletion;

        public RoutingOperation? Operation { get; set; }

        /// <summary>
        /// 依下角率／損耗率把「用量」換算為「投入量」（MRP 展開、多階 BOM 展開共用同一口徑）。
        /// 採全外加法：投入量 = 用量 × (1 + OffcutRate/100 + ScrapRate/100)——與既有 ScrapRate 單獨
        /// 使用時的公式（× (1+ScrapRate/100)）完全相容，OffcutRate=0 時結果不變，不影響既有已設定
        /// 損耗率的用料行為。（另一種「全還原法」÷(1−OffcutRate%)×(1−ScrapRate%) 為 SAP/Oracle 慣例，
        /// 但會使既有損耗率的投入量計算結果改變，本系統經評估選擇相容既有行為的全外加法。）
        /// </summary>
        public static decimal ApplyLossRates(decimal baseQty, decimal offcutRate, decimal scrapRate)
            => baseQty * (1 + offcutRate / 100m + scrapRate / 100m);
    }

    /// <summary>發料方式（mfg-domain.md §3.1 三層 Fallback）。</summary>
    public enum MaterialIssueMethod
    {
        Push      = 0,  // 發料：投產後倉庫整批推出
        Pull      = 1,  // 領料：生產單位依需申請
        Backflush = 2   // 後沖：完工時系統自動倒算扣除
    }

    /// <summary>
    /// 後沖時機（IssueMethod = Backflush 時生效）。
    ///
    /// ⚠️ <see cref="PerReport"/> **目前未實作分次沖**：報工時不會產生任何庫存異動，
    /// 實際行為與 <see cref="AtCompletion"/> 相同（完工時一次沖）。因為 CalcBackflushQty
    /// 取的是最終完工量，兩者的**總量相同**，成本與庫存都正確，差別只在期中 WIP
    /// 沒有時間分布。設成 PerReport 不會算錯錢，但也不會得到分次沖的效果。
    /// </summary>
    public enum BackflushTiming
    {
        AtCompletion = 0,  // WO 全部完工時一次沖（預設）
        PerReport    = 1   // 每次報工時按完工比例分次沖（未實作，實際同 AtCompletion）
    }
}
