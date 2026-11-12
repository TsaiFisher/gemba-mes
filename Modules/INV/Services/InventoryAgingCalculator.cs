using SmartFactory.MES.Modules.INV.ViewModels;

namespace SmartFactory.MES.Modules.INV.Services
{
    /// <summary>
    /// 存貨庫齡分桶計算（roadmap-fin-cst-sub.md §5.3）。純函式，依呆滯天數把在手庫存金額
    /// 歸入固定區間（0–30 / 31–60 / 61–90 / 91–180 / 181–360 / 360+），並算各區間占比。
    /// </summary>
    public static class InventoryAgingCalculator
    {
        // 依上界升冪排列；分類取「第一個上界 ≥ 呆滯天數」的區間（負值歸入首桶，9999 從未異動歸入 360+）。
        private static readonly (int Upper, string Label)[] Buckets =
        {
            (30,           "0–30"),
            (60,           "31–60"),
            (90,           "61–90"),
            (180,          "91–180"),
            (360,          "181–360"),
            (int.MaxValue, "360+"),
        };

        /// <summary>
        /// 將 (呆滯天數, 庫存金額) 明細彙總為六個庫齡區間列（固定回傳六列，空區間金額/筆數為 0）。
        /// </summary>
        public static List<InventoryAgingRowVm> Bucketize(IEnumerable<(int IdleDays, decimal Value)> items)
        {
            var counts = new int[Buckets.Length];
            var values = new decimal[Buckets.Length];

            foreach (var it in items)
            {
                int idx = 0;
                while (idx < Buckets.Length - 1 && it.IdleDays > Buckets[idx].Upper) idx++;
                counts[idx]++;
                values[idx] += it.Value;
            }

            decimal total = values.Sum();

            var rows = new List<InventoryAgingRowVm>(Buckets.Length);
            for (int i = 0; i < Buckets.Length; i++)
            {
                rows.Add(new InventoryAgingRowVm
                {
                    BucketLabel    = Buckets[i].Label,
                    LowerBoundDays = i == 0 ? 0 : Buckets[i - 1].Upper + 1,
                    ItemCount      = counts[i],
                    InventoryValue = values[i],
                    ValuePercent   = total == 0 ? 0m : Math.Round(values[i] / total * 100, 2),
                });
            }
            return rows;
        }
    }
}
