namespace SmartFactory.MES.Modules.RPT.Models
{
    /// <summary>
    /// 委外加工對帳與損耗分析表（roadmap-fin-cst-sub.md §5.3）。
    /// 依委外廠商彙總已確認/已過帳之委外收貨：加工費、超耗扣款（Confirm 時以發料時點 AVCO 逐料算出）、
    /// 準時率，作為委外結帳與供應商績效評估依據。
    /// </summary>
    public class OutsourcePerformanceReport
    {
        public List<OutsourcePerformanceRow> Rows { get; init; } = new();

        public int     TotalReceiptCount  => Rows.Sum(r => r.ReceiptCount);
        public int     TotalOnTimeCount   => Rows.Sum(r => r.OnTimeCount);
        public int     TotalEvaluableCount => Rows.Sum(r => r.EvaluableCount);
        public decimal TotalReceivedQty   => Rows.Sum(r => r.ReceivedQty);
        public decimal TotalProcessingFee => Rows.Sum(r => r.ProcessingFee);
        public decimal TotalScrapPenalty  => Rows.Sum(r => r.ScrapPenalty);

        public decimal TotalScrapPenaltyRate => TotalProcessingFee == 0 ? 0
            : Math.Round(TotalScrapPenalty / TotalProcessingFee * 100, 2);
        public decimal TotalOnTimeRate => TotalEvaluableCount == 0 ? 0
            : Math.Round((decimal)TotalOnTimeCount / TotalEvaluableCount * 100, 2);
    }

    public class OutsourcePerformanceRow
    {
        public string  SupplierCode  { get; init; } = "";
        public string  SupplierName  { get; init; } = "";
        /// <summary>收貨筆數</summary>
        public int     ReceiptCount  { get; init; }
        /// <summary>準時收貨筆數（收貨日 ≤ 委外採購單預交日）</summary>
        public int     OnTimeCount   { get; init; }
        /// <summary>可評估準時率之筆數（委外採購單有預交日者）</summary>
        public int     EvaluableCount { get; init; }
        /// <summary>收回成品量（Σ 收貨明細 ReceiptQty）</summary>
        public decimal ReceivedQty   { get; init; }
        /// <summary>加工費（Σ 收貨明細 LineAmount，未稅委外幣）</summary>
        public decimal ProcessingFee { get; init; }
        /// <summary>超耗扣款（Σ 收貨明細 ExcessScrapPenalty，發料時點 AVCO 逐料計算）</summary>
        public decimal ScrapPenalty  { get; init; }

        /// <summary>超耗扣款佔加工費比率%</summary>
        public decimal ScrapPenaltyRate => ProcessingFee == 0 ? 0
            : Math.Round(ScrapPenalty / ProcessingFee * 100, 2);
        /// <summary>準時率%（分母為有預交日之筆數）</summary>
        public decimal OnTimeRate => EvaluableCount == 0 ? 0
            : Math.Round((decimal)OnTimeCount / EvaluableCount * 100, 2);
    }
}
