using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.PUR.Models;

namespace SmartFactory.MES.Modules.PUR.Services;

/// <summary>TotalScore 加權平均權重（系統參數 srm_weight_*；未設定用預設 30/30/20/20）</summary>
public sealed record SrmWeights(decimal Quality, decimal Delivery, decimal Price, decimal Service)
{
    public static readonly SrmWeights Default = new(30, 30, 20, 20);
}

/// <summary>
/// 供應商評分卡計算（純函式，pur-domain.md §6.2）：
/// 自動分數公式、加權總分、結果門檻、資格狀態映射、評核期間換算。
/// </summary>
public static class VendorEvaluationCalculator
{
    /// <summary>評核期間：季度 1–4＝該季；null＝整個年度。</summary>
    public static (DateOnly From, DateOnly To) PeriodOf(int year, int? quarter)
    {
        if (quarter is >= 1 and <= 4)
        {
            var from = new DateOnly(year, (quarter.Value - 1) * 3 + 1, 1);
            return (from, from.AddMonths(3).AddDays(-1));
        }
        return (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
    }

    /// <summary>QualityScore = (1 − 不良量/進貨量) × 100；期間無進貨＝null（無資料，UI 以滿分帶入）。</summary>
    public static decimal? QualityScore(decimal receiptQty, decimal ngQty)
    {
        if (receiptQty <= 0) return null;
        var score = (1 - ngQty / receiptQty) * 100;
        return Math.Round(Math.Clamp(score, 0, 100), 2);
    }

    /// <summary>DeliveryScore = OTD Rate × 100；期間無到料事實＝null。</summary>
    public static decimal? DeliveryScore(int totalLines, int onTimeLines)
    {
        if (totalLines <= 0) return null;
        return Math.Round((decimal)onTimeLines / totalLines * 100, 2);
    }

    /// <summary>
    /// 加權平均總分；權重總和 ≤ 0 時退回預設權重。
    ///
    /// <para><b>算不出來的項目不計入加權</b>（<c>null</c>＝期間無進貨／無到料事實），
    /// 權重在剩下的項目之間按比例重新分配。舊版把 <c>null</c> 當 100 分帶進來，
    /// 於是「沒有往來」被當成「表現完美」——而且完全看不出差別。</para>
    ///
    /// <para>價格與服務是人工填寫、一定有值，所以總分永遠算得出來；但兩個自動項
    /// 都無資料時，這個分數其實只反映人工評價，呼叫端要把那件事顯示出來
    /// （<c>VendorEvaluation.QualityHasData</c> / <c>DeliveryHasData</c>）。</para>
    /// </summary>
    public static decimal Total(SrmWeights w, decimal? quality, decimal? delivery, decimal price, decimal service)
    {
        var sum = w.Quality + w.Delivery + w.Price + w.Service;
        if (sum <= 0) { w = SrmWeights.Default; sum = 100; }

        decimal weighted = price * w.Price + service * w.Service;
        decimal divisor  = w.Price + w.Service;

        if (quality is { } q)  { weighted += q * w.Quality;  divisor += w.Quality; }
        if (delivery is { } d) { weighted += d * w.Delivery; divisor += w.Delivery; }

        // 權重全落在無資料的項目上（例如只設了品質與交期權重）：沒有任何可用依據，回 0
        // 而不是除以零，讓它落到 Fail 並要求填寫後續處理。
        if (divisor <= 0) return 0m;

        return Math.Round(weighted / divisor, 2);
    }

    /// <summary>TotalScore ≥ 80 → Pass；60 ≤ TotalScore &lt; 80 → Conditional；&lt; 60 → Fail。</summary>
    public static EvaluationResult ResultOf(decimal totalScore) => totalScore switch
    {
        >= 80 => EvaluationResult.Pass,
        >= 60 => EvaluationResult.Conditional,
        _     => EvaluationResult.Fail,
    };

    /// <summary>評核結果 → Company.QualificationStatus（§6.1 自動更新）。</summary>
    public static SupplierQualificationStatus StatusOf(EvaluationResult result) => result switch
    {
        EvaluationResult.Pass        => SupplierQualificationStatus.Qualified,
        EvaluationResult.Conditional => SupplierQualificationStatus.Probation,
        _                            => SupplierQualificationStatus.Suspended,
    };
}
