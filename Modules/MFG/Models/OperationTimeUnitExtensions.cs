namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>
    /// 工序時間單位（<see cref="OperationTimeUnit"/>）換算工具。
    /// 所有「以工序原單位儲存的時間值 ↔ 分鐘」的換算集中於此，
    /// 避免各服務／報表各自寫死換算方向而分歧（曾發生 Second 誤用 ×60 造成 3600 倍誤差）。
    /// </summary>
    public static class OperationTimeUnitExtensions
    {
        /// <summary>把「以 <paramref name="unit"/> 為單位」的時間值換算為分鐘。</summary>
        public static decimal ToMinutes(decimal value, OperationTimeUnit unit) => unit switch
        {
            OperationTimeUnit.Second => value / 60m,
            OperationTimeUnit.Hour   => value * 60m,
            _                        => value,          // Minute（預設）
        };

        /// <summary>把「分鐘」的時間值換算回「以 <paramref name="unit"/> 為單位」（<see cref="ToMinutes"/> 的逆運算）。</summary>
        public static decimal FromMinutes(decimal minutes, OperationTimeUnit unit) => unit switch
        {
            OperationTimeUnit.Second => minutes * 60m,
            OperationTimeUnit.Hour   => minutes / 60m,
            _                        => minutes,        // Minute（預設）
        };
    }
}
