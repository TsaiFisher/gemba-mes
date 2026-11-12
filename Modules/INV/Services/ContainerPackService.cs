using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV.Services
{
    /// <summary>
    /// 既有庫存裝箱／換箱（container-tracking.md §9.4 第 4 期）。
    ///
    /// 箱子只能綁「一箱一個批號」：沒有批號的散裝庫存、或與別的箱子共用的批號，都不能直接把箱子綁上去
    /// （箱內數量＝批號餘額，共用就會多算）。所以裝箱＝在同一個儲位把這份庫存**換成新批號**，再綁箱。
    /// 換批號走同廠調撥（撥入批號，<see cref="TransferLine.ToLotNo"/>）：數量與單位成本原樣帶過去、
    /// 總帳不動，留下一張可作廢的調撥單——作廢＝貨換回原批號，新批號歸零，箱子自動變空箱。
    /// 換箱（從 A 箱倒到 B 箱）就是來源批號用 A 箱的批號：A 箱的批號歸零，A 自動變空箱。
    /// </summary>
    public interface IContainerPackService
    {
        /// <summary>
        /// 把某儲位某批號（<paramref name="sourceLot"/> 空白＝無批號庫存）的 <paramref name="qty"/> 裝進空箱：
        /// 建一張已過帳的同廠調撥（同儲位、撥入批號＝新批號），並把箱子綁到新批號，同一個交易。
        /// 不能裝（箱子不空、庫存不足、儲位不存在）丟 <see cref="InvalidOperationException"/>，什麼都不寫。
        /// </summary>
        Task<(string TransferId, string LotNo)> PackAsync(string containerUnitId, string productCode,
            string locationCode, string? sourceLot, decimal qty, string? userId, CancellationToken ct = default);
    }

    public class ContainerPackService : IContainerPackService
    {
        private const string TransferDocType = "StockTransfer";

        private readonly ApplicationDbContext _db;
        private readonly IInventoryPostingService _posting;
        private readonly IDocNumberService _docNumbers;
        private readonly IDateTimeService _dt;
        private readonly IUomService _uom;

        public ContainerPackService(ApplicationDbContext db, IInventoryPostingService posting,
            IDocNumberService docNumbers, IDateTimeService dt, IUomService uom)
        {
            _db = db; _posting = posting; _docNumbers = docNumbers; _dt = dt; _uom = uom;
        }

        public async Task<(string TransferId, string LotNo)> PackAsync(string containerUnitId, string productCode,
            string locationCode, string? sourceLot, decimal qty, string? userId, CancellationToken ct = default)
        {
            var plant = _db.CurrentPlantId ?? throw new InvalidOperationException(PzText.Get("Bas_ContainerPackNoPlant"));
            var units = new ContainerUnitService(_db);
            var box = (await units.TakeEmptyForBindingAsync(new[] { (containerUnitId ?? "").Trim().ToUpperInvariant() }, null, null, ct))[0];

            var stockUom = await _db.Products.AsNoTracking()
                .Where(p => p.ProductCode == productCode).Select(p => p.StockUom).FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException(PzText.Get("Bas_ContainerPackProductNotFoundFmt", productCode));
            qty = await _uom.RoundQtyAsync(stockUom, qty);
            if (qty <= 0) throw new InvalidOperationException(PzText.Get("Bas_ContainerPackQtyRequired"));

            var wh = await _db.StorageLocations.ForCurrentPlant(_db).AsNoTracking()
                .Where(l => l.LocationCode == locationCode).Select(l => l.WhCode).FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException(PzText.Get("Bas_ContainerPackLocationNotFoundFmt", locationCode));

            var date = _dt.GetLocalDate();
            var id   = await _docNumbers.NextAsync(TransferDocType, plant, date, ct);
            var lot  = await units.NextLotAsync(productCode, id, ct);
            var from = string.IsNullOrWhiteSpace(sourceLot) ? null : sourceLot.Trim();

            // 不送審：同一儲位換批號，數量、成本、總帳都不變，只是把散裝貨改標成「這一箱」
            var doc = new StockTransfer
            {
                StockTransferId = id, TransferDate = date, FromWhCode = wh, ToWhCode = wh,
                Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
                CreatedBy = userId ?? "", PlantId = plant,
                Remarks = PzText.Get("Bas_ContainerPackRemarkFmt", box.ContainerUnitId),
            };
            var line = new StockTransferLine
            {
                StockTransferId = id, LineNo = 1, ProductCode = productCode,
                Qty = qty, UomCode = stockUom, StockQty = qty, StockUomCode = stockUom,
                FromWhCode = wh, FromLocationCode = locationCode, ToWhCode = wh, ToLocationCode = locationCode,
                LotNo = from, ToLotNo = lot,
            };
            doc.Lines.Add(line);
            _db.StockTransfers.Add(doc);

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _posting.PostSamePlantTransferAsync(new PostSamePlantTransferRequest
            {
                PlantId = plant, DocNo = id, MovementDate = date, CreatedBy = userId,
                Lines = new()
                {
                    new TransferLine
                    {
                        LineNo = 1, ProductCode = productCode,
                        FromLocationCode = locationCode, ToLocationCode = locationCode,
                        LotNo = from ?? "", ToLotNo = lot, StockQty = qty,
                        UomCode = stockUom, StockUomCode = stockUom,
                        SetQtyPutAway = q => { line.QtyPutAway = q; },
                    },
                },
                SetDocPosted = () => { doc.Status = DocStatus.Posted; },
            }, ct);
            units.Bind(box, productCode, lot, qty, id, userId);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return (id, lot);
        }
    }
}
