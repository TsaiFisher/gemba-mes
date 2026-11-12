using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Api.Mobile
{
    /// <summary>
    /// 手機端檢驗（IPQC／FPI）送出時的逐項判定與合格判定。
    ///
    /// <para>🚨 **規格上下限、是否符合、合格與否一律由伺服器決定。** 2026-09-29 安全盤點時發現：
    /// 手機量測頁的 LSL／USL 是可以改的輸入框，送出時連同量測值一起帶上來，伺服器照單全收——
    /// FPI 端點連「是否符合」都直接採用手機送的布林值。等於把規格放寬、或把超規格的量測值
    /// 標成符合，就能讓首件放行、製程檢驗判合格，而檢驗單上看起來完全正常。</para>
    ///
    /// <para>規則：</para>
    /// <list type="bullet">
    /// <item>量測項目對得到規格（先比規格項目 ID，再比名稱）→ 名稱／單位／上下限／目標值／σ 全取規格，
    ///       手機只提供量測值。</item>
    /// <item>對不到 → 視為檢驗員臨時加的自訂項目，上下限採用送來的值（本來就沒有別的來源）。</item>
    /// <item>「是否符合」一律由量測值與上下限重算。</item>
    /// <item>任一項不符合就判不合格；此時不良數至少記 1，否則檢驗單會出現「不良 0、狀態 Fail」的自相矛盾。</item>
    /// </list>
    /// </summary>
    internal static class MobileInspectionJudge
    {
        /// <summary>判定依據的一個規格項目（來源可以是規格主檔，也可以是既有待檢單上的快照）。</summary>
        public sealed record SpecRef(
            string? SpecItemId, int SeqNo, string ItemName, string? UomCode,
            decimal? LowerSpec, decimal? TargetValue, decimal? UpperSpec, decimal? TargetStdDev);

        /// <summary>手機送來的一個量測項目。</summary>
        public sealed record Measured(
            string? SpecItemId, int SeqNo, string? ItemName, string? UomCode,
            decimal? LowerSpec, decimal? TargetValue, decimal? UpperSpec, decimal? MeasuredValue);

        /// <summary>
        /// 找本廠適用的 IPQC／FPI 規格：品號專屬優先，沒有才用通用規格（ProductCode 為 null）。
        /// 只帶啟用中的規格項目。
        /// </summary>
        public static async Task<InspectionSpec?> FindSpecAsync(ApplicationDbContext db, string plantId, string? productCode)
        {
            InspectionSpec? spec = null;
            if (!string.IsNullOrEmpty(productCode))
                spec = await db.InspectionSpecs
                    .Include(s => s.Items.Where(i => i.IsActive).OrderBy(i => i.SeqNo))
                    .AsNoTracking()
                    .Where(s => s.PlantId == plantId && s.IsActive && s.ProductCode == productCode)
                    .FirstOrDefaultAsync();

            return spec ?? await db.InspectionSpecs
                .Include(s => s.Items.Where(i => i.IsActive).OrderBy(i => i.SeqNo))
                .AsNoTracking()
                .Where(s => s.PlantId == plantId && s.IsActive && s.ProductCode == null)
                .FirstOrDefaultAsync();
        }

        public static IReadOnlyList<SpecRef> FromSpec(InspectionSpec? spec) =>
            spec?.Items.Select(i => new SpecRef(i.SpecItemId, i.SeqNo, i.ItemName, i.UomCode,
                i.LowerSpec, i.TargetValue, i.UpperSpec, i.TargetStdDev)).ToList()
            ?? (IReadOnlyList<SpecRef>)Array.Empty<SpecRef>();

        /// <summary>對應的規格項目；對不到回 null（＝自訂項目）。</summary>
        public static SpecRef? Match(Measured m, IReadOnlyList<SpecRef> refs)
        {
            if (!string.IsNullOrEmpty(m.SpecItemId))
            {
                var byId = refs.FirstOrDefault(r => r.SpecItemId == m.SpecItemId);
                if (byId != null) return byId;
            }
            var name = m.ItemName?.Trim();
            return string.IsNullOrEmpty(name)
                ? null
                : refs.FirstOrDefault(r => string.Equals(r.ItemName.Trim(), name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>依規格建立量測明細；規格欄位以伺服器為準，「是否符合」與 3σ 警告在這裡重算。</summary>
        public static InspectionItem BuildItem(Measured m, SpecRef? spec)
        {
            var lower  = spec != null ? spec.LowerSpec   : m.LowerSpec;
            var upper  = spec != null ? spec.UpperSpec   : m.UpperSpec;
            var target = spec != null ? spec.TargetValue : m.TargetValue;
            var sd     = spec?.TargetStdDev;
            return new InspectionItem
            {
                SeqNo           = spec?.SeqNo ?? m.SeqNo,
                ItemName        = spec?.ItemName ?? m.ItemName ?? "",
                UomCode         = spec != null ? spec.UomCode : m.UomCode,
                LowerSpec       = lower,
                TargetValue     = target,
                UpperSpec       = upper,
                TargetStdDev    = sd,
                MeasuredValue   = m.MeasuredValue,
                IsConforming    = IsConforming(m.MeasuredValue, lower, upper),
                HasSigmaWarning = HasSigmaWarning(m.MeasuredValue, target, sd),
            };
        }

        public static bool? IsConforming(decimal? measured, decimal? lower, decimal? upper) =>
            measured.HasValue
                ? (lower == null || measured >= lower) && (upper == null || measured <= upper)
                : null;

        public static bool HasSigmaWarning(decimal? measured, decimal? target, decimal? stdDev) =>
            measured.HasValue && target.HasValue && stdDev.HasValue
            && Math.Abs(measured.Value - target.Value) > 3 * stdDev.Value;

        /// <summary>
        /// 合格判定（MIL-STD-1916 Ac=0）：不良數為 0 且沒有任何量測項目不符合才合格。
        /// 回傳要存的不良數：小數進位；有量測不符合但填 0 時記 1。
        /// </summary>
        public static (bool Passed, int FailQty) Judge(decimal failQty, IEnumerable<InspectionItem> items)
        {
            var fail = (int)Math.Ceiling(failQty);
            if (fail == 0 && items.Any(i => i.IsConforming == false)) fail = 1;
            return (fail == 0, fail);
        }

        /// <summary>抽樣數必須大於 0；不良數不可為負，也不可多於實際抽樣數。</summary>
        public static bool IsValidQty(decimal actualSampleQty, decimal failQty) =>
            actualSampleQty > 0 && failQty >= 0 && failQty <= actualSampleQty;
    }
}
