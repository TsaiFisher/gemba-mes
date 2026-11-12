using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.RPT.Models
{
    /// <summary>
    /// 產品邊際貢獻分析：以產品變動成本占比（取自工單實際成本要素）還原變動成本，
    /// 計算邊際貢獻＝營收 − 變動成本，供產能閒置時之接單/報價利潤評估。
    /// </summary>
    public class ContributionMarginReport
    {
        public List<ContributionMarginRow> Rows { get; set; } = new();

        // 目前套用的變動成本分類（供畫面顯示；非驗證欄位）
        [NotMapped] public bool MaterialVariable { get; set; } = true;
        [NotMapped] public bool LaborVariable    { get; set; } = true;
        [NotMapped] public bool OverheadVariable { get; set; } = false;

        [NotMapped] public decimal TotalRevenue      => Rows.Sum(r => r.Revenue);
        [NotMapped] public decimal TotalVariableCost => Rows.Sum(r => r.VariableCost);
        [NotMapped] public decimal TotalContribution => Rows.Sum(r => r.ContributionMargin);
        [NotMapped] public decimal TotalContributionRate => TotalRevenue == 0 ? 0m
            : Math.Round(TotalContribution / TotalRevenue * 100m, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>單一產品的邊際貢獻列。</summary>
    public class ContributionMarginRow
    {
        public string ProductCode { get; init; } = "";
        public string ProductName { get; init; } = "";

        public decimal Revenue { get; init; }

        /// <summary>銷貨成本（混合 AVCO）。</summary>
        public decimal Cost { get; init; }

        /// <summary>變動成本占比（0..1）；無工單成本要素資料時為 null（以整體平均或 0 代入）。</summary>
        public decimal VariableRatio { get; init; }

        /// <summary>此產品是否有工單成本要素可推占比（false = 用整體平均/未知）。</summary>
        [NotMapped] public bool HasCostBasis { get; init; }

        [NotMapped] public decimal VariableCost       => Math.Round(Cost * VariableRatio, 4, MidpointRounding.AwayFromZero);
        [NotMapped] public decimal ContributionMargin => Revenue - VariableCost;
        [NotMapped] public decimal ContributionRate   => Revenue == 0 ? 0m
            : Math.Round(ContributionMargin / Revenue * 100m, 2, MidpointRounding.AwayFromZero);
    }
}
