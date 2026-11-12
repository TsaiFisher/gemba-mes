using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <inheritdoc cref="IMfgSimplifiedModeService"/>
    public class MfgSimplifiedModeService : IMfgSimplifiedModeService
    {
        private readonly IPlantParamService _plantParams;

        /// <summary>完整流程可開工的派工狀態（既有行為，不得因簡化模式而改變）。</summary>
        private static readonly DispatchStatus[] FullModeStartable =
        {
            DispatchStatus.Scheduled, DispatchStatus.ReadyToStart, DispatchStatus.InProgress
        };

        /// <summary>完整流程的現場待辦狀態（既有手機派工清單過濾條件）。</summary>
        private static readonly DispatchStatus[] FullModeActionable =
        {
            DispatchStatus.Scheduled, DispatchStatus.ReadyToStart, DispatchStatus.InProgress
        };

        private static readonly DispatchStatus[] SimplifiedStartable =
        {
            DispatchStatus.Pending, DispatchStatus.Scheduled, DispatchStatus.ReadyToStart, DispatchStatus.InProgress
        };

        private static readonly DispatchStatus[] SimplifiedActionable =
        {
            DispatchStatus.Pending, DispatchStatus.Scheduled, DispatchStatus.ReadyToStart, DispatchStatus.InProgress
        };

        public MfgSimplifiedModeService(IPlantParamService plantParams) => _plantParams = plantParams;

        public async Task<bool> IsEnabledAsync(string? plantId, CancellationToken ct = default) =>
            string.Equals(await _plantParams.GetAsync(ProjectZSystemParamKeys.MfgSimplifiedMode, plantId, ct), "true",
                StringComparison.OrdinalIgnoreCase);

        public async Task<bool> CanStartSessionAsync(DispatchStatus status, string? plantId, CancellationToken ct = default) =>
            (await IsEnabledAsync(plantId, ct) ? SimplifiedStartable : FullModeStartable).Contains(status);

        public async Task<IReadOnlyList<DispatchStatus>> ActionableDispatchStatusesAsync(string? plantId, CancellationToken ct = default) =>
            await IsEnabledAsync(plantId, ct) ? SimplifiedActionable : FullModeActionable;
    }
}
