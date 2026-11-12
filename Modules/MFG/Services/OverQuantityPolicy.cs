using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>超量判定結果。<see cref="Allowed"/>＝可以存；否則 <see cref="Message"/> 是給畫面看的原因。</summary>
    public sealed record OverQtyCheck(bool Allowed, string? Message = null, bool BySupervisor = false);

    /// <summary>
    /// 超量生產（報工）與超量收貨（委外）的單一判定（2026-10-12，mfg-domain.md §4.6）。
    ///
    /// <para>規則：累計量 ≤ 計畫量 → 照收；≤ 計畫量 ×（1＋容許率）→ 照收；再超過依廠別參數
    /// Block（拒收）或 Supervisor（角色有「超量核准」作業權限者可收，其他人擋下並說明）。</para>
    ///
    /// <para>「主管」＝角色在「手機模組權限」被授予超量核准（投產報工模組 <see cref="ReportApproveKey"/>、
    /// 倉儲作業模組 <see cref="ReceiveApproveKey"/>）。桌機與手機用同一個判斷，避免兩邊各自定義「誰是主管」。
    /// 這兩個鍵刻意不吃「新作業鍵自動回補」——回補會讓所有有報工權限的人都變主管。</para>
    /// </summary>
    public interface IOverQuantityPolicy
    {
        Task<OverQtyCheck> CheckProductionAsync(string? plantId, string? roleId,
            decimal planned, decimal done, decimal adding, CancellationToken ct = default);

        Task<OverQtyCheck> CheckOutsourceReceiptAsync(string? plantId, string? roleId,
            decimal ordered, decimal received, decimal adding, CancellationToken ct = default);

        /// <summary>超過委外採購單數量的部分是否付加工費（未設定＝付，與原本行為相同）。</summary>
        Task<bool> PayOverReceiptFeeAsync(string? plantId, CancellationToken ct = default);
    }

    public class OverQuantityPolicy : IOverQuantityPolicy
    {
        public const string MfgModuleKey     = "Mfg";
        public const string InvModuleKey     = "Inv";
        public const string ReportApproveKey  = "OverReportApprove";
        public const string ReceiveApproveKey = "OverReceiveApprove";

        private readonly IPlantParamService _params;
        private readonly IMobilePermissionService _perm;

        public OverQuantityPolicy(IPlantParamService plantParams, IMobilePermissionService perm)
        {
            _params = plantParams;
            _perm   = perm;
        }

        public Task<OverQtyCheck> CheckProductionAsync(string? plantId, string? roleId,
            decimal planned, decimal done, decimal adding, CancellationToken ct = default)
            => CheckAsync(plantId, roleId, planned, done, adding,
                ProjectZSystemParamKeys.MfgOverReportTolerancePct, ProjectZSystemParamKeys.MfgOverReportAction,
                defaultAction: "Block", MfgModuleKey, ReportApproveKey,
                blockFmt: "Mfg_OverReportBlockFmt", supervisorFmt: "Mfg_OverReportNeedSupervisorFmt", ct);

        public Task<OverQtyCheck> CheckOutsourceReceiptAsync(string? plantId, string? roleId,
            decimal ordered, decimal received, decimal adding, CancellationToken ct = default)
            => CheckAsync(plantId, roleId, ordered, received, adding,
                ProjectZSystemParamKeys.MfgOutsourceOverReceiptTolerancePct, ProjectZSystemParamKeys.MfgOutsourceOverReceiptAction,
                defaultAction: "Supervisor", InvModuleKey, ReceiveApproveKey,
                blockFmt: "Mfg_OverReceiptBlockFmt", supervisorFmt: "Mfg_OverReceiptNeedSupervisorFmt", ct);

        public async Task<bool> PayOverReceiptFeeAsync(string? plantId, CancellationToken ct = default)
            => !string.Equals((await _params.GetAsync(ProjectZSystemParamKeys.MfgOutsourceOverReceiptFee, plantId, ct))?.Trim(),
                "NoPay", StringComparison.OrdinalIgnoreCase);

        private async Task<OverQtyCheck> CheckAsync(string? plantId, string? roleId,
            decimal planned, decimal done, decimal adding, string tolKey, string actionKey, string defaultAction,
            string moduleKey, string approveKey, string blockFmt, string supervisorFmt, CancellationToken ct)
        {
            var total = done + adding;
            if (total <= planned) return new(true);

            var tol   = await TolerancePctAsync(tolKey, plantId, ct);
            var limit = planned * (1m + tol / 100m);
            if (total <= limit) return new(true);

            var action = (await _params.GetAsync(actionKey, plantId, ct))?.Trim();
            if (string.IsNullOrEmpty(action)) action = defaultAction;

            if (string.Equals(action, "Supervisor", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(roleId) && await _perm.CanExecuteAsync(roleId, moduleKey, approveKey))
                    return new(true, BySupervisor: true);
                return new(false, PzText.Get(supervisorFmt, Q(total), Q(planned), Q(limit), Q(tol)));
            }
            return new(false, PzText.Get(blockFmt, Q(total), Q(planned), Q(limit), Q(tol)));
        }

        private async Task<decimal> TolerancePctAsync(string key, string? plantId, CancellationToken ct)
        {
            var raw = await _params.GetAsync(key, plantId, ct);
            return decimal.TryParse(raw, System.Globalization.NumberStyles.Number,
                       System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 0m;
        }

        /// <summary>訊息裡的數量：去掉 DB 小數尺度的尾零（9.000000 → 9）。</summary>
        private static string Q(decimal d) => d.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    }
}
