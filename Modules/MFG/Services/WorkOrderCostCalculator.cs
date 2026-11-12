using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 工單標準成本計算（mfg-domain.md §16.2/§16.3）。純函式、不接 DB，供工單釋出（Release）
    /// 時一次計算並快照，亦供單元測試（§12.2「工單釋出後標準成本」）。
    /// </summary>
    public static class WorkOrderCostCalculator
    {
        /// <summary>
        /// 依工單數量、用料（UnitCost 快照）與工序工時 + 工作中心費率，算出標準三成本。
        /// 工時換算：每道工序 (SetupTime + UnitTime × qty) 依 TimeUnit（秒/分/時）折算為小時，
        /// 再乘工作中心人工/機台費率。費率查無（工作中心未設）以 0 計。結果四捨五入至 4 位。
        /// </summary>
        public static (decimal Material, decimal Labor, decimal Overhead) ComputeStandard(
            decimal quantity,
            IEnumerable<WorkOrderMaterial> materials,
            IEnumerable<WorkOrderOperation> operations,
            IReadOnlyDictionary<string, (decimal Labor, decimal Machine)> workCenterRates)
        {
            decimal material = materials.Sum(m => m.RequiredQty * m.UnitCost);

            decimal labor = 0m, overhead = 0m;
            foreach (var op in operations)
            {
                // 準備工時單位（SetupTimeUnit）可與單件工時（TimeUnit）不同，分別換算為分鐘再折小時。
                var minutes = OperationTimeUnitExtensions.ToMinutes(op.SetupTime, op.SetupTimeUnit)
                            + OperationTimeUnitExtensions.ToMinutes(op.UnitTime * quantity, op.TimeUnit);
                var hours = minutes / 60m;
                if (workCenterRates.TryGetValue(op.WorkCenterId, out var rate))
                {
                    labor    += hours * rate.Labor;
                    overhead += hours * rate.Machine;
                }
            }

            return (
                Math.Round(material, 4, MidpointRounding.AwayFromZero),
                Math.Round(labor, 4, MidpointRounding.AwayFromZero),
                Math.Round(overhead, 4, MidpointRounding.AwayFromZero));
        }
    }
}
