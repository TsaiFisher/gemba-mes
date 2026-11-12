using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// MIL-STD-1916 VL 切換規則實作（含選配跳批）。
    ///
    /// 狀態機（SamplingState）：
    ///   Normal    ──(連續合格 ≥ PassesToReduce)──→ Reduced
    ///   Reduced   ──(連續合格 ≥ PassesToSkipLot, SkipLotEnabled)──→ SkipLot
    ///   Tightened ──(連續合格 ≥ PassesToNormal)──→ Normal
    ///   Any state ──(任何不合格)──→ Tightened
    ///   SkipLot   ──(任何不合格)──→ Tightened（同步退回）
    ///
    /// Phase H：依 InspectionSpec.SamplingScope 決定計數邊界：
    ///   SpecLevel    → 單一 InspectionSpec 全局計數器（原有行為）
    ///   MachineLevel → 以 (SpecId, machineCode) 分區，存於 InspectionSpecSamplingCounter
    ///   ToolingLevel → 以 (SpecId, toolingCode) 分區，存於 InspectionSpecSamplingCounter
    /// </summary>
    public class SamplingPlanService : ISamplingPlanService
    {
        private readonly ApplicationDbContext _db;

        public SamplingPlanService(ApplicationDbContext db) => _db = db;

        public async Task<SamplingDecision> GetDecisionAsync(
            string? productCode, string? supplierCode, string inspectionType,
            decimal lotSize, string? plantId,
            string? machineCode = null, string? toolingCode = null,
            CancellationToken ct = default)
        {
            var spec = await FindSpecAsync(productCode, supplierCode, plantId, ct);
            if (spec == null)
                return DefaultDecision(lotSize);

            var (state, lotCtr, passCount) = await GetScopedStateAsync(spec, machineCode, toolingCode, ct);

            // ── 跳批判斷 ──────────────────────────────────────────────────────
            if (spec.SkipLotEnabled && state == "SkipLot")
            {
                lotCtr++;
                await SetScopedStateAsync(spec, machineCode, toolingCode, state, passCount, lotCtr, ct);

                if (lotCtr % spec.SkipLotFrequency != 0)
                    return new SamplingDecision(false, spec.NormalVl, 0, "SkipLot", "跳批：依頻率規則本批免抽");
            }

            int effectiveVl = state switch
            {
                "Tightened" => spec.TightenedVl,
                "Reduced"   => spec.ReducedVl,
                _           => spec.NormalVl
            };
            effectiveVl = Math.Clamp(effectiveVl, 1, 7);

            var level = (MilStd1916Service.VerificationLevel)effectiveVl;
            int n = MilStd1916Service.GetSampleSize(lotSize, level);

            return new SamplingDecision(true, effectiveVl, n, state);
        }

        public async Task RecordResultAsync(
            string? productCode, string? supplierCode, string? plantId,
            bool passed,
            string? machineCode = null, string? toolingCode = null,
            CancellationToken ct = default)
        {
            var spec = await FindSpecAsync(productCode, supplierCode, plantId, ct);
            if (spec == null) return;

            // 累加器（InspectionSpec/Counter 已掛 shadow xmin）的 read-modify-write：
            // 並發同 spec+scope 檢驗會 lost-update → 以「重讀最新值重算」樂觀重試收斂。
            // 注意：本服務常被 caller 在尚有未存 dispatch/order 變更時呼叫，故 SaveChanges 會連帶
            // flush 那些實體。重試只能針對自身累加器 Entry.ReloadAsync，不可 ChangeTracker.Clear()。
            const int maxAttempts = 5;
            for (int attempt = 1; ; attempt++)
            {
                var (state, lotCtr, passCount) = await GetScopedStateAsync(spec, machineCode, toolingCode, ct);

                if (passed)
                {
                    passCount++;

                    switch (state)
                    {
                        case "Tightened" when passCount >= spec.PassesToNormal:
                            state     = "Normal";
                            passCount = 0;
                            break;

                        case "Normal" when passCount >= spec.PassesToReduce:
                            state     = "Reduced";
                            passCount = 0;
                            break;

                        case "Reduced" when spec.SkipLotEnabled && passCount >= spec.PassesToSkipLot:
                            state     = "SkipLot";
                            lotCtr    = 0;
                            passCount = 0;
                            break;
                    }
                }
                else
                {
                    passCount = 0;
                    lotCtr    = 0;
                    state     = "Tightened";
                }

                try
                {
                    await SetScopedStateAsync(spec, machineCode, toolingCode, state, passCount, lotCtr, ct);
                    return;
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    // 僅重試「本服務自身累加器」的並發；若有任一失敗 Entry 不屬於累加器
                    // （例如 caller 連帶 flush 的 dispatch xmin 衝突），交回上層以結構化 409 處理。
                    var ownEntries = ex.Entries
                        .Where(e => e.Entity is InspectionSpec or InspectionSpecSamplingCounter)
                        .ToList();

                    if (ownEntries.Count == 0 || ownEntries.Count != ex.Entries.Count || attempt >= maxAttempts)
                        throw;

                    foreach (var e in ownEntries)
                        await e.ReloadAsync(ct);   // 重讀最新值 + xmin，下一輪以最新狀態重算單筆增量
                }
            }
        }

        // ── SamplingScope 分區輔助 ────────────────────────────────────────────

        private async Task<(string state, int lotCtr, int passCount)> GetScopedStateAsync(
            InspectionSpec spec, string? machineCode, string? toolingCode, CancellationToken ct)
        {
            if (IsSpecLevel(spec, machineCode, toolingCode))
                return (spec.SamplingState, spec.LotCounter, spec.ConsecutivePasses);

            var key = GetScopeKey(spec, machineCode, toolingCode);
            var counter = await _db.InspectionSpecSamplingCounters
                .FirstOrDefaultAsync(c => c.SpecId == spec.SpecId && c.ScopeKey == key, ct);

            if (counter == null)
                return ("Normal", 0, 0);

            return (counter.SamplingState, counter.LotCounter, counter.ConsecutivePasses);
        }

        private async Task SetScopedStateAsync(
            InspectionSpec spec, string? machineCode, string? toolingCode,
            string state, int passCount, int lotCtr, CancellationToken ct)
        {
            if (IsSpecLevel(spec, machineCode, toolingCode))
            {
                spec.SamplingState     = state;
                spec.ConsecutivePasses = passCount;
                spec.LotCounter        = lotCtr;
                spec.UpdatedAt         = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                return;
            }

            var key = GetScopeKey(spec, machineCode, toolingCode);
            var counter = await _db.InspectionSpecSamplingCounters
                .FirstOrDefaultAsync(c => c.SpecId == spec.SpecId && c.ScopeKey == key, ct);

            if (counter == null)
            {
                counter = new InspectionSpecSamplingCounter
                {
                    SpecId   = spec.SpecId,
                    ScopeKey = key
                };
                _db.InspectionSpecSamplingCounters.Add(counter);
            }

            counter.SamplingState     = state;
            counter.ConsecutivePasses = passCount;
            counter.LotCounter        = lotCtr;
            counter.UpdatedAt         = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        private static bool IsSpecLevel(InspectionSpec spec, string? machineCode, string? toolingCode)
        {
            if (spec.SamplingScope == null || spec.SamplingScope == SamplingScope.SpecLevel)
                return true;
            if (spec.SamplingScope == SamplingScope.MachineLevel && string.IsNullOrEmpty(machineCode))
                return true;
            if (spec.SamplingScope == SamplingScope.ToolingLevel && string.IsNullOrEmpty(toolingCode))
                return true;
            return false;
        }

        private static string GetScopeKey(InspectionSpec spec, string? machineCode, string? toolingCode)
        {
            return spec.SamplingScope switch
            {
                SamplingScope.MachineLevel => machineCode ?? "",
                SamplingScope.ToolingLevel => toolingCode ?? "",
                _                          => ""
            };
        }

        // ── 私有輔助 ──────────────────────────────────────────────────────────

        private async Task<InspectionSpec?> FindSpecAsync(
            string? productCode, string? supplierCode, string? plantId, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(productCode) && !string.IsNullOrEmpty(supplierCode))
            {
                var exact = await _db.InspectionSpecs.FirstOrDefaultAsync(
                    s => s.PlantId == plantId && s.IsActive
                      && s.ProductCode == productCode && s.SupplierCode == supplierCode, ct);
                if (exact != null) return exact;
            }

            if (!string.IsNullOrEmpty(productCode))
            {
                var byProduct = await _db.InspectionSpecs.FirstOrDefaultAsync(
                    s => s.PlantId == plantId && s.IsActive
                      && s.ProductCode == productCode && s.SupplierCode == null, ct);
                if (byProduct != null) return byProduct;
            }

            return await _db.InspectionSpecs.FirstOrDefaultAsync(
                s => s.PlantId == plantId && s.IsActive
                  && s.ProductCode == null && s.SupplierCode == null, ct);
        }

        private static SamplingDecision DefaultDecision(decimal lotSize)
        {
            var level = MilStd1916Service.VerificationLevel.IV;
            int n = MilStd1916Service.GetSampleSize(lotSize, level);
            return new SamplingDecision(true, (int)level, n, "Normal");
        }
    }
}
