using SmartFactory.MES.Modules.PUR.ViewModels;

namespace SmartFactory.MES.Modules.PUR.Services;

/// <summary>
/// OTD 一筆事實：一張「應該在 <paramref name="DueDate"/> 交到」的單，結果如何。
///
/// <para><paramref name="ReceiptDate"/> 為 <c>null</c>＝**到 <paramref name="AsOf"/> 為止還沒交清**。
/// 這一種以前根本不存在——母體只有已收到的貨，拖著不交的單完全不進分母
/// （見 <c>IOtdFactService</c> 的說明）。</para>
/// </summary>
/// <param name="AsOf">未交時用來算遲延天數的基準日（廠別當地日）。</param>
/// <remarks>
/// <c>readonly record struct</c> 而非 <c>record</c>：R5b 掃 <c>Modules.*</c> 下所有 <c>class</c>
/// 的非可空實值型別屬性並要求雙語驗證資源鍵。<c>AsOf</c> 是計算用的基準日、不是表單欄位，
/// 替它造一個資源鍵等於造假欄位名（同 <c>ReportScreenBinding</c> 的處理）。
/// </remarks>
public readonly record struct OtdFact(string SupplierCode, DateOnly? ReceiptDate, DateOnly DueDate, DateOnly AsOf)
{
    /// <summary>逾期天數；≤ 0 表示準時。未交清者以 <see cref="AsOf"/> 計，且會持續增加。</summary>
    public int OverdueDays => (ReceiptDate ?? AsOf).DayNumber - DueDate.DayNumber;

    /// <summary>準時＝已交清且不晚於預交日。未交清一律不算準時，即使還沒到期也不該進母體。</summary>
    public bool IsOnTime => ReceiptDate.HasValue && OverdueDays <= 0;

    /// <summary>到 <see cref="AsOf"/> 為止仍未交清。</summary>
    public bool IsOutstanding => !ReceiptDate.HasValue;
}

/// <summary>
/// OTD 統計聚合（純函式，pur-domain.md §5.3/§5.5）：
/// 月份趨勢（OTD Rate 折線圖資料）＋廠商延誤排名（逾期筆數/平均/最大逾期天數）。
/// </summary>
public static class OtdAggregator
{
    public static (List<OtdMonthRowVm> Months, List<SupplierDeliveryRateRowVm> Suppliers) Compute(
        IReadOnlyCollection<OtdFact> facts)
    {
        // 依**預交日**分月，不是收貨日：母體是「這個月應該交的單」，未交的也沒有收貨日可分。
        var months = facts
            .GroupBy(f => new { f.DueDate.Year, f.DueDate.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g =>
            {
                var s = Stats(g);
                return new OtdMonthRowVm
                {
                    YearMonth      = $"{g.Key.Year:D4}-{g.Key.Month:D2}",
                    TotalLines     = s.Total,
                    OnTimeLines    = s.OnTime,
                    AvgOverdueDays = s.AvgOverdue,
                    MaxOverdueDays = s.MaxOverdue,
                };
            })
            .ToList();

        // 廠商延誤排名（§5.5）：逾期筆數多者在前，其次平均逾期天數
        var suppliers = facts
            .GroupBy(f => f.SupplierCode)
            .Select(g =>
            {
                var s = Stats(g);
                return new SupplierDeliveryRateRowVm
                {
                    SupplierCode   = g.Key,
                    TotalLines     = s.Total,
                    OnTimeLines    = s.OnTime,
                    AvgOverdueDays = s.AvgOverdue,
                    MaxOverdueDays = s.MaxOverdue,
                };
            })
            .OrderByDescending(r => r.LateLines)
            .ThenByDescending(r => r.AvgOverdueDays)
            .ThenBy(r => r.SupplierCode)
            .ToList();

        return (months, suppliers);
    }

    private static (int Total, int OnTime, decimal AvgOverdue, int MaxOverdue) Stats(
        IEnumerable<OtdFact> facts)
    {
        int total = 0, onTime = 0, max = 0, late = 0;
        long lateSum = 0;
        foreach (var f in facts)
        {
            total++;
            if (f.IsOnTime) { onTime++; continue; }
            // 未交清者 d 以 AsOf 計，天數會隨時間持續增加——那正是要呈現的事實。
            var d = f.OverdueDays;
            late++;
            lateSum += d;
            if (d > max) max = d;
        }
        var avg = late == 0 ? 0m : Math.Round((decimal)lateSum / late, 1);
        return (total, onTime, avg, max);
    }
}
