using SmartFactory.MES.Modules.ACT.Models;

namespace SmartFactory.MES.Modules.ACT.Services
{
    public interface INrvAssessmentService
    {
        /// <summary>執行 IAS 2 NRV 評估批次：對所有在庫品號計算減損損失</summary>
        Task<IList<InventoryNrvAssessment>> RunAssessmentAsync(
            string periodId, string plantId,
            decimal sellingCostRatio = 0.02m,
            CancellationToken ct = default);
    }
}
