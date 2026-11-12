using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 模具打次累計與保養排程（mfg-domain.md §14.1 第二階段）。
    /// 桌機（DispatchController.CreateReport）與手機（MobileMfgController.CreateReport）
    /// 報工共用；只掛載變更於呼叫端的 DbContext，不自行 SaveChanges（與報工同交易）。
    /// </summary>
    public interface IToolingShotService
    {
        /// <summary>
        /// 報工回寫打次：AccumulatedShots += (QtyOk + QtyNg) / CavityCount。
        /// 首次跨越 PmIntervalShots 門檻時，自動建立 HI 保養停機單
        /// （機台轉 Maintenance，套用 §13.4 排程副作用）。
        /// 無 ToolingCode / 無穴數（夾治具）/ 未設保養間隔者僅累計或跳過，不觸發。
        /// </summary>
        Task AccumulateAsync(Dispatch dispatch, decimal qtyTotal, DateTime now);

        /// <summary>
        /// 保養完成重置：HO 事件且 SourceDocId 為模具代號時，將該模具
        /// AccumulatedShots 歸零，開始下一個保養週期。其餘事件為 no-op。
        /// </summary>
        Task ResetOnMaintenanceClosedAsync(MachineEventType eventType, string? sourceDocId, DateTime now);
    }

    public class ToolingShotService : IToolingShotService
    {
        private readonly ApplicationDbContext _db;
        private readonly IUserContext _userContext;
        private readonly IStringLocalizer _localizer;

        public ToolingShotService(ApplicationDbContext db, IUserContext userContext,
            IStringLocalizerFactory localizerFactory)
        {
            _db = db;
            _userContext = userContext;
            _localizer = localizerFactory.Create(typeof(ProjectZSharedResource));
        }

        public async Task AccumulateAsync(Dispatch dispatch, decimal qtyTotal, DateTime now)
        {
            if (string.IsNullOrEmpty(dispatch.ToolingCode) || qtyTotal <= 0) return;

            var tooling = await _db.Toolings
                .FirstOrDefaultAsync(t => t.ToolingCode == dispatch.ToolingCode);
            if (tooling == null || tooling.CavityCount is not > 0) return;

            var before = tooling.AccumulatedShots;
            tooling.AccumulatedShots = before + qtyTotal / tooling.CavityCount.Value;
            tooling.UpdatedAt = now;

            // 只在「本次回寫首次跨越門檻」時觸發一次；已逾期未保養者不重複開單
            //（畫面警示持續顯示，直到 HO 重置）。
            if (tooling.PmIntervalShots is not > 0) return;
            var threshold = tooling.PmIntervalShots.Value;
            if (before >= threshold || tooling.AccumulatedShots < threshold) return;

            // 委外派工無機台：僅累計與畫面警示，不建停機單
            if (string.IsNullOrEmpty(dispatch.MachineCode)) return;
            // 廠別取自派工（下面開的異動單也是用 dispatch.PlantId），不是取自使用者情境：
            // 這支是被回寫流程呼叫的，跟著單走才不會因為呼叫者當下在哪個廠而改變結果。
            // 條件本身不可省——WorkCenterMachine 不是 IPlantEntity，全域過濾攔不到，
            // 而下面會改 machine.MachineStatus，改錯就是動到別廠的機台主檔。
            var machine = await _db.WorkCenterMachines
                .FirstOrDefaultAsync(m => m.MachineCode == dispatch.MachineCode
                                       && m.WorkCenter!.PlantId == dispatch.PlantId);
            if (machine == null) return;

            var orderId = Guid.NewGuid().ToString("N")[..12].ToUpper();
            _db.MachineStatusOrders.Add(new MachineStatusOrder
            {
                OrderId     = orderId,
                EventType   = MachineEventType.HI,
                EventAt     = now,
                RequestedBy = _userContext.UserId,
                Note        = _localizer["ToolingPmAutoOrderNote", tooling.ToolingCode].Value,
                PlantId     = dispatch.PlantId,
                CreatedAt   = now,
                UpdatedAt   = now
            });
            _db.MachineStatusOrderLines.Add(new MachineStatusOrderLine
            {
                OrderId        = orderId,
                LineSeq        = 1,
                MachineCode    = machine.MachineCode,
                WorkCenterCode = machine.WorkCenterCode,
                TargetStatus   = MachineStatus.Maintenance,
                SourceDocId    = tooling.ToolingCode
            });
            machine.MachineStatus = MachineStatus.Maintenance;

            // §13.4 排程副作用（與機台狀態異動同一份實作）：已排退回待排、已投入轉暫停（原因＝機台停用，
            // 含觸發本次回寫的派工本身——模具到期即停，保養完成機台恢復可用時自動恢復）。
            // 呼叫端可能剛把觸發本次回寫的派工改成 Closed（未存檔），共用實作以記憶體狀態複核，不會改回去。
            await DispatchHold.ApplyMachineDownAsync(_db, machine.MachineCode, dispatch.PlantId, now);
        }

        public async Task ResetOnMaintenanceClosedAsync(MachineEventType eventType, string? sourceDocId, DateTime now)
        {
            if (eventType != MachineEventType.HO || string.IsNullOrEmpty(sourceDocId)) return;

            var tooling = await _db.Toolings
                .FirstOrDefaultAsync(t => t.ToolingCode == sourceDocId);
            if (tooling == null) return;

            tooling.AccumulatedShots = 0;
            tooling.UpdatedAt = now;
        }
    }
}
