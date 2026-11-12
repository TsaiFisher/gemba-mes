using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV.Services
{
    public interface ILandedCostService
    {
        /// <summary>
        /// 確認分攤單：依分攤基準計算各行外包費用，並依策略 (a)「只調在庫餘量均價」回寫存貨成本。
        /// 在庫餘量比例對應的部分資本化（滾入 AVCO / FIFO 未耗用成本層），其餘（已出庫對應）轉費用（PPV，記於明細 ExpensedCost）。
        /// </summary>
        Task<LandedCostAllocation> ConfirmAsync(string allocationId, CancellationToken ct = default);

        /// <summary>
        /// 作廢已確認的分攤單：開反向傳票（借 2153／貸 存貨、貸 5151），並把當初加進均價／成本層的金額扣回。
        /// 確認後該料號儲位有任何出庫（FIFO 品有任何異動）就擋下——加進去的成本已有一部分隨出庫轉走，
        /// 全數從剩下的庫存扣回會讓剩餘存貨低估；這時改開一張反向（負數）分攤單或存貨調整。
        /// </summary>
        Task<LandedCostAllocation> VoidAsync(string allocationId, DateOnly voidDate, string? reason, CancellationToken ct = default);
    }

    public class LandedCostService : ILandedCostService
    {
        private readonly ApplicationDbContext _db;
        private readonly IJournalEntryService _journal;
        private readonly ICurrencyService? _currency;

        public LandedCostService(ApplicationDbContext db, IJournalEntryService journal, ICurrencyService? currency = null)
        {
            _db = db;
            _journal = journal;
            _currency = currency;
        }

        public async Task<LandedCostAllocation> ConfirmAsync(string allocationId, CancellationToken ct = default)
        {
            var alloc = await _db.LandedCostAllocations
                .Include(a => a.Lines)
                .FirstOrDefaultAsync(a => a.LandedCostAllocationId == allocationId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Inv_LandedCostNotFoundFmt", allocationId));

            if (alloc.Status != DocStatus.Draft)
                throw new InvalidOperationException(PzText.Get("Inv_OnlyDraftConfirmable"));
            if (alloc.Lines.Count == 0)
                throw new InvalidOperationException(PzText.Get("Inv_NoLinesCannotConfirm"));

            var orderedLines = alloc.Lines.OrderBy(l => l.LineNo).ToList();
            var inputs = orderedLines
                .Select(l => new LandedCostInputLine
                {
                    PurchaseAmount   = l.PurchaseAmount,
                    Quantity         = l.ReceiptQty,
                    Weight           = l.Weight,
                    OriginalUnitCost = l.OriginalUnitCost,
                })
                .ToList();

            var results = LandedCostAllocator.Allocate(alloc.TotalLandedCost, inputs, alloc.Basis);

            // 分攤單可選外幣（運費、關稅常以外幣計價），但存貨與總帳只有本幣。
            // 原本把外幣金額原數加進均價、分錄 AmountLocal＝Amount——USD 100 進帳成 100 元。
            // 以單據日買匯換算（與進貨、應付同一匯率類型）；查無匯率擋下，不默認 1。
            var rate = await ResolveRateAsync(alloc);

            // 在庫比例以「同一張分攤單裡同料號同儲位批號各行的進貨量合計」為分母：
            // 兩張進貨各 10、在手剩 10，逐行算會兩行都視為全數在庫，把兩份費用都資本化進那 10 個，
            // 存貨高估一倍（2026-10-23 到岸成本隨機序列）。合計分母則各行同比例，比例內資本化、其餘轉費用。
            var receiptQtyByKey = orderedLines
                .GroupBy(l => (l.ProductCode, Loc: l.LocationCode ?? "", Lot: l.LotNo ?? ""))
                .ToDictionary(g => g.Key, g => g.Sum(l => l.ReceiptQty));

            // 必須是 UTC：Inventory.LastUpdatedAt / LandedCostAllocation.UpdatedAt 都是
            // timestamptz 欄位，而 IDateTimeService.Now 回的是「已換算成使用者時區、
            // Kind=Unspecified」的當地時間——Npgsql 拒收，整個確認動作會拋
            // DbUpdateException 而不是靜默存錯時間。全庫其餘 *At 欄位一律存 UTC，此處對齊。
            var now = DateTime.UtcNow;
            decimal totalCapitalized = 0m, totalExpensed = 0m, totalCapitalizedLocal = 0m;
            for (int i = 0; i < orderedLines.Count; i++)
            {
                var line = orderedLines[i];
                var allocated = results[i].AllocatedCost;
                line.AllocatedCost = allocated;

                var inv = await _db.Inventories.FirstOrDefaultAsync(x =>
                    x.PlantId == (alloc.PlantId ?? "") &&
                    x.ProductCode == line.ProductCode &&
                    x.LocationCode == (line.LocationCode ?? "") &&
                    x.LotNo == (line.LotNo ?? ""), ct);

                var qtyOnHand = inv?.QtyOnHand ?? 0m;

                // 作廢回沖用的快照（見 LandedCostAllocationLine.OutflowQtyAtConfirm）
                (line.OutflowQtyAtConfirm, line.MovementCountAtConfirm) = await MovementSnapshotAsync(
                    alloc.PlantId ?? "", line.ProductCode, line.LocationCode ?? "", line.LotNo ?? "", ct);

                // 在庫餘量比例（夾在 0~1，避免他批庫存使比例 > 1）
                var receiptQty = receiptQtyByKey[(line.ProductCode, line.LocationCode ?? "", line.LotNo ?? "")];
                var ratio = receiptQty > 0m
                    ? Math.Min(1m, Math.Max(0m, qtyOnHand / receiptQty))
                    : 0m;

                var capitalized = Math.Round(allocated * ratio, 4, MidpointRounding.AwayFromZero);
                line.CapitalizedCost = capitalized;
                line.ExpensedCost    = allocated - capitalized;   // 已出庫對應 → 轉費用（PPV）
                totalCapitalized += line.CapitalizedCost;
                totalExpensed    += line.ExpensedCost;

                // 明細的分攤／資本化／轉費用維持單據幣別；回寫存貨用本幣
                var capitalizedLocal = Math.Round(capitalized * rate, 4, MidpointRounding.AwayFromZero);
                if (inv != null && qtyOnHand > 0m && capitalized != 0m)
                {
                    totalCapitalizedLocal += capitalizedLocal;
                    var newUnit = Math.Round(inv.UnitCost + capitalizedLocal / qtyOnHand, 6, MidpointRounding.AwayFromZero);
                    inv.UnitCost      = newUnit;
                    inv.LastUpdatedAt = now;
                    line.NewUnitCost  = newUnit;

                    // FIFO 並行表示：將資本化金額平均（每單位）加到未耗用成本層
                    var layers = await _db.InventoryCostLayers
                        .Where(cl => cl.PlantId == (alloc.PlantId ?? "")
                                  && cl.ProductCode == line.ProductCode
                                  && cl.LocationCode == (line.LocationCode ?? "")
                                  && cl.LotNo == (line.LotNo ?? "")
                                  && cl.QtyRemaining > 0m)
                        .ToListAsync(ct);

                    var sumRemaining = layers.Sum(l => l.QtyRemaining);
                    if (sumRemaining > 0m)
                    {
                        var perUnitBump = Math.Round(capitalizedLocal / sumRemaining, 6, MidpointRounding.AwayFromZero);
                        foreach (var layer in layers)
                            layer.UnitCost += perUnitBump;
                    }
                }
                else
                {
                    // 全數已出庫或無在庫：不調存貨，新均價維持原值
                    line.NewUnitCost = inv?.UnitCost ?? line.OriginalUnitCost;
                }
            }

            alloc.Status    = DocStatus.Confirmed;
            alloc.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);

            // 過帳：借 原料存貨（資本化）+ 借 進料費用差異（PPV／已出庫對應）／ 貸 暫估應付運費（GR/IR）。§3.4 / §4.4
            await PostJournalAsync(alloc, totalCapitalized, totalExpensed, totalCapitalizedLocal, rate, ct);
            return alloc;
        }

        public const string SourceDocType = "LandedCostAllocation";
        public const string VoidSourceDocType = "LandedCostAllocation-Void";

        public async Task<LandedCostAllocation> VoidAsync(string allocationId, DateOnly voidDate, string? reason, CancellationToken ct = default)
        {
            var alloc = await _db.LandedCostAllocations
                .Include(a => a.Lines)
                .FirstOrDefaultAsync(a => a.LandedCostAllocationId == allocationId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Inv_LandedCostNotFoundFmt", allocationId));
            if (alloc.Status != DocStatus.Confirmed)
                throw new InvalidOperationException(PzText.Get("Inv_LandedCostVoidConfirmedOnly"));

            var plant = alloc.PlantId ?? "";
            var original = await _db.JournalEntries.IgnoreQueryFilters().Include(e => e.Lines)
                .Where(e => e.SourceDocType == SourceDocType && e.SourceDocId == allocationId && e.PlantId == plant)
                .ToListAsync(ct);
            // 確認時點：原傳票建立時間（無傳票＝金額 0，不動存貨，用單據最後更新時間）
            var confirmedAt = original.Count > 0 ? original.Min(e => e.CreatedAt) : alloc.UpdatedAt;
            // 換算率取原傳票（舊資料未換匯者為 1，正好與當初加進去的數一致）
            var rate = original.SelectMany(e => e.Lines)
                .Where(l => l.AccountCode == StandardAccounts.AccruedFreightCode)
                .Select(l => (decimal?)l.ExchangeRate).FirstOrDefault() ?? 1m;
            if (rate <= 0m) rate = 1m;

            foreach (var line in alloc.Lines.Where(l => l.CapitalizedCost != 0m).OrderBy(l => l.LineNo))
            {
                var loc = line.LocationCode ?? "";
                var lot = line.LotNo ?? "";
                var capitalizedLocal = Math.Round(line.CapitalizedCost * rate, 4, MidpointRounding.AwayFromZero);

                var layers = await _db.InventoryCostLayers
                    .Where(cl => cl.PlantId == plant && cl.ProductCode == line.ProductCode
                              && cl.LocationCode == loc && cl.LotNo == lot && cl.QtyRemaining > 0m)
                    .ToListAsync(ct);
                var isFifo = layers.Count > 0;

                bool movedSince;
                if (line.OutflowQtyAtConfirm is { } outflowAtConfirm && line.MovementCountAtConfirm is { } countAtConfirm)
                {
                    var (outflowNow, countNow) = await MovementSnapshotAsync(plant, line.ProductCode, loc, lot, ct);
                    movedSince = outflowNow > outflowAtConfirm || (isFifo && countNow > countAtConfirm);
                }
                else
                {
                    // 快照欄位上線前確認的舊單：只能以傳票建立時間判斷（時鐘回撥時可能失準）
                    movedSince = await _db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                        .AnyAsync(m => m.PlantId == plant && m.ProductCode == line.ProductCode
                                    && m.LocationCode == loc && m.LotNo == lot
                                    && m.CreatedAt > confirmedAt
                                    && (isFifo || m.Direction == -1), ct);
                }
                if (movedSince)
                    throw new InvalidOperationException(PzText.Get("Inv_LandedCostVoidStockMovedFmt", line.ProductCode, loc));

                var inv = await _db.Inventories.FirstOrDefaultAsync(x =>
                    x.PlantId == plant && x.ProductCode == line.ProductCode && x.LocationCode == loc && x.LotNo == lot, ct);
                if (inv == null || inv.QtyOnHand <= 0m)
                    throw new InvalidOperationException(PzText.Get("Inv_LandedCostVoidStockMovedFmt", line.ProductCode, loc));

                var newUnit = Math.Round(inv.UnitCost - capitalizedLocal / inv.QtyOnHand, 6, MidpointRounding.AwayFromZero);
                if (newUnit < 0m)
                    throw new InvalidOperationException(PzText.Get("Inv_LandedCostVoidStockMovedFmt", line.ProductCode, loc));
                inv.UnitCost = newUnit;
                inv.LastUpdatedAt = DateTime.UtcNow;

                var sumRemaining = layers.Sum(l => l.QtyRemaining);
                if (sumRemaining > 0m)
                {
                    var perUnit = Math.Round(capitalizedLocal / sumRemaining, 6, MidpointRounding.AwayFromZero);
                    foreach (var layer in layers) layer.UnitCost -= perUnit;
                }
            }

            alloc.Status = DocStatus.Void;
            if (!string.IsNullOrWhiteSpace(reason))
                alloc.Remarks = string.IsNullOrWhiteSpace(alloc.Remarks) ? reason : $"{alloc.Remarks}\n{reason}";
            alloc.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            // 反向傳票：原分錄借貸對調（同一來源單號、另立來源類別，原傳票保留）
            var reversal = original.SelectMany(e => e.Lines)
                .Select(l => new JournalLineRequest(
                    l.AccountCode, l.AccountName, Debit: l.Credit, Credit: l.Debit,
                    CurrencyCode: l.CurrencyCode, Amount: l.Amount, ExchangeRate: l.ExchangeRate,
                    AmountLocal: l.AmountLocal, CostCenter: l.CostCenter, SourceRef: l.SourceRef))
                .ToList();
            if (reversal.Count > 0)
                await _journal.CreateAutoEntryAsync(
                    sourceDocType: VoidSourceDocType, sourceDocId: allocationId, entryDate: voidDate, plantId: plant,
                    lines: reversal, description: $"進料費用分攤作廢 {allocationId}", ct: ct);
            return alloc;
        }

        private async Task<(decimal OutflowQty, int Count)> MovementSnapshotAsync(
            string plant, string product, string loc, string lot, CancellationToken ct)
        {
            var q = _db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.PlantId == plant && m.ProductCode == product && m.LocationCode == loc && m.LotNo == lot);
            var outflow = await q.Where(m => m.Direction == -1).SumAsync(m => (decimal?)m.StockQty, ct) ?? 0m;
            return (outflow, await q.CountAsync(ct));
        }

        private async Task<decimal> ResolveRateAsync(LandedCostAllocation alloc)
        {
            if (_currency == null || string.IsNullOrEmpty(alloc.CurrencyCode)) return 1m;
            // 本幣 GetRateAsync 直接回 1
            return await _currency.GetRateAsync(alloc.CurrencyCode, alloc.DocDate, RateType.Buy)
                ?? throw new InvalidOperationException(PzText.Get("Inv_LandedCostRateMissingFmt", alloc.CurrencyCode, alloc.DocDate));
        }

        /// <summary>
        /// 借 存貨（資本化，本幣＝實際加進均價的數）＋借 5151（其餘）／貸 2153（全額）。
        /// 本幣：貸方＝總額×匯率、存貨＝各行資本化本幣合計、5151 取差額，捨入尾差落在費用不落在存貨，
        /// 存貨科目才會與明細帳一分不差。
        /// </summary>
        private async Task PostJournalAsync(LandedCostAllocation alloc, decimal capitalized, decimal expensed,
            decimal capitalizedLocal, decimal rate, CancellationToken ct)
        {
            var total = capitalized + expensed;
            if (total == 0m) return;   // 無金額不產生分錄

            var totalLocal = Math.Round(total * rate, 4, MidpointRounding.AwayFromZero);
            var expensedLocal = totalLocal - capitalizedLocal;
            var currency = alloc.CurrencyCode;
            var lines = new List<JournalLineRequest>();
            if (capitalizedLocal != 0m)
                lines.Add(new JournalLineRequest(
                    StandardAccounts.RawMaterialInventoryCode, StandardAccounts.RawMaterialInventoryName,
                    Debit: capitalizedLocal, Credit: 0m, CurrencyCode: currency, Amount: capitalized, ExchangeRate: rate, AmountLocal: capitalizedLocal));
            if (expensedLocal != 0m)
                lines.Add(new JournalLineRequest(
                    StandardAccounts.LandedCostVarianceCode, StandardAccounts.LandedCostVarianceName,
                    Debit: expensedLocal, Credit: 0m, CurrencyCode: currency, Amount: expensed, ExchangeRate: rate, AmountLocal: expensedLocal));
            lines.Add(new JournalLineRequest(
                StandardAccounts.AccruedFreightCode, StandardAccounts.AccruedFreightName,
                Debit: 0m, Credit: totalLocal, CurrencyCode: currency, Amount: total, ExchangeRate: rate, AmountLocal: totalLocal));

            await _journal.CreateAutoEntryAsync(
                sourceDocType: SourceDocType,
                sourceDocId:   alloc.LandedCostAllocationId,
                entryDate:     alloc.DocDate,
                plantId:       alloc.PlantId ?? "",
                lines:         lines,
                description:   $"進料費用分攤 {alloc.LandedCostAllocationId}",
                ct:            ct);
        }
    }
}
