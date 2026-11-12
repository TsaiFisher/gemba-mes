using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>單一物料的備齊狀態（mfg-domain.md §21.5）。</summary>
    public enum MaterialLineReadiness
    {
        Ready = 0,             // 可用量足夠（或 Backflush 後沖、開工前不要求）
        PendingSubassembly = 1,// 下階半成品工單尚未完工
        Shortage = 2           // 可用量不足
    }

    public record MaterialReadinessLine(
        string ProductCode,
        decimal RequiredQty,
        decimal AvailableQty,
        MaterialLineReadiness State);

    public record MaterialReadinessResult(
        MaterialReadinessStatus Overall,
        IReadOnlyList<MaterialReadinessLine> Lines);

    /// <summary>
    /// 物料備齊確認閘口（mfg-domain.md §21.5）：工單釋出且核准後、開工前，
    /// 逐料確認可用量（廠級＝在手 − Active 預留）與下階半成品完工狀態，
    /// 並把整體 <see cref="MaterialReadinessStatus"/> 寫回 WorkOrder。
    /// </summary>
    public interface IWorkOrderMaterialReadinessService
    {
        Task<MaterialReadinessResult> CheckAsync(string workOrderId, CancellationToken ct = default);
    }
}
