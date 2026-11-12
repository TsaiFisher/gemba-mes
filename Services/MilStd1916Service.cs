using SmartFactory.MES.Modules._Shared;
namespace SmartFactory.MES.Services
{
    /// <summary>
    /// MIL-STD-1916 抽樣計畫查表服務。
    /// 特性：允收數恆為 0（Ac=0, Re=1），樣本數依批量與驗證等級（VL I~VII）決定。
    /// 來源：MIL-STD-1916 Table II（計數值抽樣）。
    /// </summary>
    public static class MilStd1916Service
    {
        /// <summary>
        /// 驗證等級（Verification Level）。
        /// I = 最寬鬆（最少樣本）；VII = 最嚴格（最多樣本）。
        /// 一般 IQC 建議 VL IV；關鍵件/安全件建議 VL VI 或 VII。
        /// </summary>
        public enum VerificationLevel { I = 1, II = 2, III = 3, IV = 4, V = 5, VI = 6, VII = 7 }

        // MIL-STD-1916 Table II — Attribute Sampling
        // 格式：(批量上限, [VL-I, VL-II, VL-III, VL-IV, VL-V, VL-VI, VL-VII])
        private static readonly (int MaxLot, int[] Samples)[] _table =
        {
            (        8, new[] {  2,  2,  2,  2,  3,  5,  8 }),
            (       15, new[] {  2,  2,  2,  3,  5,  8, 13 }),
            (       25, new[] {  2,  2,  2,  3,  5,  8, 13 }),
            (       50, new[] {  2,  2,  3,  5,  8, 13, 20 }),
            (       90, new[] {  2,  2,  3,  5,  8, 13, 20 }),
            (      150, new[] {  2,  3,  5,  8, 13, 20, 32 }),
            (      280, new[] {  2,  3,  5,  8, 13, 20, 32 }),
            (      500, new[] {  2,  3,  5,  8, 13, 20, 32 }),
            (     1200, new[] {  3,  5,  8, 13, 20, 32, 50 }),
            (     3200, new[] {  3,  5,  8, 13, 20, 32, 50 }),
            (    10000, new[] {  5,  8, 13, 20, 32, 50, 80 }),
            (    35000, new[] {  5,  8, 13, 20, 32, 50, 80 }),
            (   150000, new[] {  8, 13, 20, 32, 50, 80,125 }),
            (   500000, new[] {  8, 13, 20, 32, 50, 80,125 }),
            (int.MaxValue, new[] { 13, 20, 32, 50, 80,125,200 }),
        };

        /// <summary>
        /// 依批量與驗證等級查出應抽樣數 n。允收數 Ac 恆為 0。
        /// </summary>
        /// <param name="lotSize">批量</param>
        /// <param name="vl">驗證等級（VerificationLevel）</param>
        /// <returns>應抽樣數 n；若 lotSize 小於 n 則全數檢驗（100%）</returns>
        public static int GetSampleSize(decimal lotSize, VerificationLevel vl)
        {
            var lot = (int)Math.Ceiling(lotSize);
            var vlIndex = (int)vl - 1;  // 0-based
            foreach (var row in _table)
            {
                if (lot <= row.MaxLot)
                    return Math.Min(row.Samples[vlIndex], lot); // 不超過批量本身
            }
            return _table[^1].Samples[vlIndex];
        }

        /// <summary>
        /// 判定：允收數恆為 0，只要 failQty >= 1 即為拒批（Reject）。
        /// </summary>
        public static bool IsAccepted(int failQty) => failQty == 0;

        /// <summary>
        /// 回傳各驗證等級的說明（供 UI 下拉顯示）。
        /// </summary>
        public static IReadOnlyList<(int Value, string Label, string Description)> GetVlOptions() =>
        [
            (1, "VL-I", PzText.Get("Qcr_VlDesc_1")),
            (2, "VL-II", PzText.Get("Qcr_VlDesc_2")),
            (3, "VL-III", PzText.Get("Qcr_VlDesc_3")),
            (4, "VL-IV", PzText.Get("Qcr_VlDesc_4")),
            (5, "VL-V", PzText.Get("Qcr_VlDesc_5")),
            (6, "VL-VI", PzText.Get("Qcr_VlDesc_6")),
            (7, "VL-VII", PzText.Get("Qcr_VlDesc_7")),
        ];
    }
}
