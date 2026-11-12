using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV.Services
{
    public class MaterialStagingService : IMaterialStagingService
    {
        private readonly ApplicationDbContext _db;
        private readonly IInventoryReservationService _reservations;
        private readonly IInventoryPostingService _posting;
        private readonly IDateTimeService _dt;

        public MaterialStagingService(ApplicationDbContext db, IInventoryReservationService reservations,
            IInventoryPostingService posting, IDateTimeService dt)
        {
            _db = db;
            _reservations = reservations;
            _posting = posting;
            _dt = dt;
        }

        public async Task ConfirmAsync(MaterialStagingOrder order, string confirmedBy, CancellationToken ct = default)
        {
            if (order.Status != "Draft")
                throw new InvalidOperationException(PzText.Get("Inv_StagingCannotConfirmFmt", order.StagingId, order.Status));

            var plantId = order.PlantId
                ?? throw new InvalidOperationException(PzText.Get("Inv_StagingNoPlantFmt", order.StagingId));

            // 載入明細（若尚未載入）
            if (!_db.Entry(order).Collection(o => o.Lines).IsLoaded)
                await _db.Entry(order).Collection(o => o.Lines).LoadAsync(ct);

            if (!order.Lines.Any())
                throw new InvalidOperationException(PzText.Get("Inv_StagingNoLinesConfirmFmt", order.StagingId));

            // 預檢可用量（硬預留）：依品號彙總需求，任一不足立即擋單，於建立任何預留與改狀態「之前」拋出，
            // 確保失敗時不殘留預留、狀態維持 Draft（不依賴 DB 交易回滾，InMemory 測試亦正確）。
            // inventory.md §一「負庫存控管」：硬預留不得超過廠級可用量（在手 − Active 預留）。
            // 工單備料單：MRP 展開時已為該工單鎖住在手庫存，那筆預留同時被廠級可用量扣掉。
            // 不加回會讓「MRP 鎖料成功的工單」永遠備不了料；加回後於下方確實把該預留轉移給備料單，
            // 不會同一批料鎖兩次（mfg-domain.md §21.5／inventory.md §11-4）。
            // 逐行認工單（而非只看表頭）：生產批來源的備料單一張橫跨多張工單，
            // 只認表頭會把整批的 MRP 預留都算到同一張工單頭上，其餘工單的預留既加不回也扣不掉。
            string? ResolveLineWorkOrder(MaterialStagingLine l) =>
                !string.IsNullOrEmpty(l.SourceWorkOrderId) ? l.SourceWorkOrderId
                : order.SourceType == "WorkOrder" ? order.SourceId
                : null;

            foreach (var grp in order.Lines.GroupBy(l => l.ProductCode))
            {
                var required  = grp.Sum(l => l.RequiredQty);
                var available = await _reservations.GetPlantAvailableQtyAsync(plantId, grp.Key, ct);

                foreach (var woId in grp.Select(ResolveLineWorkOrder)
                                        .Where(w => !string.IsNullOrEmpty(w))
                                        .Distinct())
                    available += await _reservations.GetReservedBySourceAsync(
                        ReservationSourceType.WorkOrderMaterial, woId!, grp.Key, ct);

                if (required > available)
                    throw new InvalidOperationException(
                        PzText.Get("Inv_StagingInsufficientFmt", order.StagingId, grp.Key, required, available));
            }

            // 在 transaction 內建立所有 Reservation，任一可用量不足立即中止
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var requiredDate = DateTime.UtcNow.AddDays(1); // 備料無特定需求日，以明日為 placeholder
                foreach (var line in order.Lines)
                {
                    var reservation = await _reservations.ReserveAsync(
                        plantId,
                        line.ProductCode,
                        line.RequiredQty,
                        ReservationSourceType.MaterialStagingOrder,
                        order.StagingId,
                        line.LineNo,
                        requiredDate,
                        ct);

                    line.ReservationId = reservation.Id;

                    // 預留轉移：備料單接手鎖量後，等額扣掉該行所屬工單的 MRP 預留，
                    // 否則同一批料被鎖兩次，會誤縮其他工單的可用量。
                    var lineWorkOrderId = ResolveLineWorkOrder(line);
                    if (!string.IsNullOrEmpty(lineWorkOrderId))
                        await _reservations.ReduceBySourceAsync(
                            ReservationSourceType.WorkOrderMaterial, lineWorkOrderId,
                            line.ProductCode, line.RequiredQty, ct);
                }

                order.Status      = "Confirmed";
                order.ConfirmedBy = confirmedBy;
                order.ConfirmedAt = DateTime.UtcNow;
                order.UpdatedAt   = DateTime.UtcNow;

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        public async Task StageAsync(MaterialStagingOrder order, string stagedBy, CancellationToken ct = default)
        {
            if (order.Status != "Confirmed")
                throw new InvalidOperationException(PzText.Get("Inv_StagingPickNotConfirmedFmt", order.StagingId, order.Status));

            var plantId = order.PlantId
                ?? throw new InvalidOperationException(PzText.Get("Inv_StagingNoPlantFmt", order.StagingId));

            if (string.IsNullOrEmpty(order.StagingLocationCode))
                throw new InvalidOperationException(PzText.Get("Inv_StagingNoLocationFmt", order.StagingId));

            if (!_db.Entry(order).Collection(o => o.Lines).IsLoaded)
                await _db.Entry(order).Collection(o => o.Lines).LoadAsync(ct);

            var lines = order.Lines.OrderBy(l => l.LineNo).ToList();
            if (lines.Count == 0)
                throw new InvalidOperationException(PzText.Get("Inv_StagingNoLinesPickFmt", order.StagingId));

            // 來源儲位是揀料當下才知道的，缺一行都不能過帳——半套移庫會讓帳面料件散在兩處，
            // 之後領料要從哪個儲位扣就對不起來了。
            var missing = lines.Where(l => string.IsNullOrEmpty(l.FromLocationCode))
                               .Select(l => l.LineNo)
                               .ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    PzText.Get("Inv_StagingLinesNoSourceFmt", order.StagingId, string.Join(PzText.Get("Common_ListSeparator"), missing)));

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                await _posting.PostSamePlantTransferAsync(new PostSamePlantTransferRequest
                {
                    PlantId      = plantId,
                    DocNo        = order.StagingId,
                    // 異動日以廠別當地日為準，與 INV 其他 9 支單據一致（UTC 會讓
                    // UTC+8 廠別在當地凌晨備料時把異動記到前一天）。
                    MovementDate = _dt.GetLocalDate(),
                    CreatedBy    = stagedBy,
                    Lines = lines.Select(line => new TransferLine
                    {
                        LineNo           = line.LineNo,
                        ProductCode      = line.ProductCode,
                        FromLocationCode = line.FromLocationCode ?? "",
                        ToLocationCode   = order.StagingLocationCode!,
                        StockQty         = line.RequiredQty - line.PickedQty,
                        UomCode          = line.UomCode ?? "",
                        StockUomCode     = line.StockUomCode ?? line.UomCode ?? "",
                        // 過帳量累加回 PickedQty（而非直接覆寫）：分批揀料時前幾次已移的量必須保留
                        SetQtyPutAway    = qty => line.PickedQty += qty,
                    }).ToList(),
                    SetDocPosted = () =>
                    {
                        order.Status      = "Completed";
                        order.CompletedAt = DateTime.UtcNow;
                    },
                }, ct);

                order.StagedBy  = stagedBy;
                order.StagedAt  = DateTime.UtcNow;
                order.UpdatedAt = DateTime.UtcNow;

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        public async Task VoidAsync(MaterialStagingOrder order, CancellationToken ct = default)
        {
            if (order.Status is not ("Draft" or "Confirmed"))
                throw new InvalidOperationException(PzText.Get("Inv_StagingCannotVoidFmt", order.StagingId, order.Status));

            if (order.Status == "Confirmed")
            {
                await _reservations.ReleaseBySourceAsync(
                    ReservationSourceType.MaterialStagingOrder, order.StagingId, ct);
            }

            order.Status    = "Void";
            order.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }
}
