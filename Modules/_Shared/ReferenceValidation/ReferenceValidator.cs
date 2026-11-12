using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.QCR.Models;

namespace SmartFactory.MES.Modules._Shared.ReferenceValidation
{
    /// <summary>參照欄位指向的主檔。</summary>
    public enum ReferenceMaster
    {
        Product, Company, Uom, Currency, Tax, Container, Instrument,
        Warehouse, StorageLocation, WorkCenter, Machine, Tooling, Routing, ShiftGroup,
        AccountingPeriod, CodeEntry, Shift, ProductCategory, Plant, DefectCode, ChartOfAccount, BankAccount, User,
        /// <summary>組織單位（部門）。廠別可為空，沿上層繼承。</summary>
        Organization,
        /// <summary>機台，或未登錄線別的生產線工作中心（排程把整條線當單一資源，MachineCode 存工作中心代碼）。</summary>
        MachineOrLine,
    }

    /// <summary>
    /// 一條參照規則：<paramref name="Entity"/>.<paramref name="Property"/> 的值必須是 <paramref name="Master"/> 中
    /// 存在、屬本廠（分廠主檔）、仍可用的一筆。
    /// <para><paramref name="Category"/>：代碼維護（CodeEntry）的類別。
    /// <paramref name="WarehouseProperty"/>：儲位所屬倉庫取自同一筆資料的哪個欄位（儲位主鍵是倉庫＋儲位）。
    /// <paramref name="CheckActive"/>：false＝只驗存在與廠別，不驗啟用（系統也會寫入、且不該因主檔停用而失敗者，例如月結）。
    /// <paramref name="CheckPlant"/>：false＝不比本廠（跨廠調撥的目的倉庫屬於目的廠）。</para>
    /// </summary>
    public readonly record struct ReferenceRule(
        Type Entity, string Property, ReferenceMaster Master,
        string? Category = null, string? WarehouseProperty = null, bool CheckActive = true, bool CheckPlant = true);

    /// <summary>
    /// 刻意不檢查的參照欄位與理由（守門測試 R41 要求每個參照欄位非規則即豁免）。
    /// 以屬性宣告在 <see cref="ReferenceRules"/> 上：理由是給開發者看的說明，不是使用者訊息。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class ReferenceExemptAttribute(Type entity, string property, string reason) : Attribute
    {
        public Type Entity { get; } = entity;
        public string Property { get; } = property;
        public string Reason { get; } = reason;
    }

    public enum ReferenceProblem { NotFound, OtherPlant, Inactive }

    public readonly record struct ReferenceViolation(
        string Entity, string Property, ReferenceMaster Master, string Value, ReferenceProblem Problem);

    /// <summary>既有資料盤點的一筆結果：<paramref name="Count"/> 列資料帶著同一個不合格的值。</summary>
    public readonly record struct ReferenceAuditFinding(ReferenceViolation Violation, string? Plant, int Count);

    /// <summary>存檔時參照欄位不通過檢查。訊息已在地化，可直接顯示給使用者。</summary>
    public sealed class ReferenceValidationException : Exception
    {
        public IReadOnlyList<ReferenceViolation> Violations { get; }

        public ReferenceValidationException(IReadOnlyList<ReferenceViolation> violations)
            : base(string.Join("\n", violations.Select(ReferenceValidator.Describe)))
            => Violations = violations;
    }

    /// <summary>
    /// 下拉參照欄位的伺服器端有效性檢查：存檔前掃 ChangeTracker 的新增／修改實體，
    /// 依 <see cref="ReferenceRules"/> 確認每個參照值存在、屬本廠、仍可用。
    ///
    /// <para><b>只驗有變更的值</b>（2026-10-02 定案）：新增列全驗；修改列只驗目前值與原始值不同的欄位。
    /// 舊單據引用的主檔後來停用了，不影響修改這張單的其他欄位。⚠️ 不看 <c>IsModified</c>——
    /// Identity 的 Update 會把整列標成已修改，必須比值。</para>
    ///
    /// <para><b>同一次存檔中新增的主檔視為存在</b>（例如匯入時先建品號再建明細）。</para>
    ///
    /// <para><b>廠別</b>：實體本身有 PlantId 就比它；明細列（本身不分廠）比 DbContext 的 CurrentPlantId；
    /// 兩者皆空（背景作業）時略過廠別、只驗存在與可用。</para>
    ///
    /// <para>空值不在此檢查——必填與否是另一件事，由各自的驗證處理。</para>
    /// </summary>
    public static class ReferenceValidator
    {
        private sealed record Request(ReferenceRule Rule, string Entity, string Key, string Display, string? Plant, int Count = 1);
        private sealed record MasterRow(string Key, string? PlantId, bool Usable);

        public static async Task EnsureValidAsync(ApplicationDbContext db, CancellationToken ct = default)
        {
            var violations = await ValidateAsync(db, ct);
            if (violations.Count > 0) throw new ReferenceValidationException(violations);
        }

        public static async Task<IReadOnlyList<ReferenceViolation>> ValidateAsync(ApplicationDbContext db, CancellationToken ct = default)
        {
            var requests = new List<Request>();
            foreach (var entry in db.ChangeTracker.Entries())
            {
                if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
                var rules = ReferenceRules.For(entry.Metadata.ClrType);
                if (rules.Count == 0) continue;

                var plant = (entry.Entity as IPlantEntity)?.PlantId is { Length: > 0 } p ? p : db.CurrentPlantId;
                foreach (var rule in rules)
                {
                    var value = Current(entry, rule.Property);
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    var wh = rule.WarehouseProperty == null ? null : Current(entry, rule.WarehouseProperty);
                    if (entry.State == EntityState.Modified
                        && !Changed(entry, rule.Property)
                        && (rule.WarehouseProperty == null || !Changed(entry, rule.WarehouseProperty)))
                        continue;

                    var key = rule.Master switch
                    {
                        ReferenceMaster.StorageLocation => $"{wh}|{value}",
                        ReferenceMaster.CodeEntry => $"{rule.Category}|{value}",
                        _ => value,
                    };
                    var display = rule.Master == ReferenceMaster.StorageLocation ? $"{wh}/{value}" : value;
                    requests.Add(new Request(rule, entry.Metadata.ClrType.Name, key, display, plant));
                }
            }
            if (requests.Count == 0) return Array.Empty<ReferenceViolation>();

            return (await CheckAsync(db, requests, includeAddedInThisSave: true, ct)).Select(x => x.Violation).ToList();
        }

        /// <summary>
        /// 既有資料盤點（上線前／啟用前用）：把每條規則套到資料庫裡**現有的每一列**，回報哪些值
        /// 現在修改那筆資料時會被擋下。與存檔檢查共用同一套主檔查詢與判定，結果不會兩套標準。
        /// <para>唯讀：只查詢，不改任何資料。<see cref="ReferenceProblem.Inactive"/> 在舊單據上屬正常
        /// （主檔後來停用了，只驗變更值所以不影響修改其他欄位），要處理的是 NotFound 與 OtherPlant。</para>
        /// <para>本身沒有 PlantId 的明細列無從得知所屬廠別，只驗存在與可用（存檔時則比當下廠別）。</para>
        /// </summary>
        public static async Task<IReadOnlyList<ReferenceAuditFinding>> AuditAsync(ApplicationDbContext db, CancellationToken ct = default)
        {
            var requests = new List<Request>();
            foreach (var group in ReferenceRules.Rules.GroupBy(r => r.Entity))
            {
                var entityType = db.Model.FindEntityType(group.Key);
                if (entityType == null) continue;
                var hasPlant = typeof(IPlantEntity).IsAssignableFrom(group.Key);
                foreach (var rule in group)
                {
                    var load = typeof(ReferenceValidator).GetMethod(nameof(DistinctValuesAsync),
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                        .MakeGenericMethod(group.Key);
                    var values = await (Task<List<StoredValue>>)load.Invoke(null, [db, rule, hasPlant, ct])!;
                    foreach (var v in values)
                    {
                        var key = rule.Master switch
                        {
                            ReferenceMaster.StorageLocation => $"{v.Warehouse}|{v.Value}",
                            ReferenceMaster.CodeEntry => $"{rule.Category}|{v.Value}",
                            _ => v.Value,
                        };
                        var display = rule.Master == ReferenceMaster.StorageLocation ? $"{v.Warehouse}/{v.Value}" : v.Value;
                        requests.Add(new Request(rule, group.Key.Name, key, display, v.Plant, v.Count));
                    }
                }
            }

            return (await CheckAsync(db, requests, includeAddedInThisSave: false, ct))
                .Select(x => new ReferenceAuditFinding(x.Violation, x.Request.Plant, x.Request.Count))
                .OrderBy(f => f.Violation.Problem == ReferenceProblem.Inactive)
                .ThenBy(f => f.Violation.Entity, StringComparer.Ordinal)
                .ThenBy(f => f.Violation.Property, StringComparer.Ordinal)
                .ThenBy(f => f.Violation.Value, StringComparer.Ordinal)
                .ToList();
        }

        private sealed record StoredValue(string Value, string? Warehouse, string? Plant, int Count);

        private static async Task<List<StoredValue>> DistinctValuesAsync<T>(
            ApplicationDbContext db, ReferenceRule rule, bool hasPlant, CancellationToken ct) where T : class
        {
            var rows = await db.Set<T>().IgnoreQueryFilters().AsNoTracking()
                .Select(e => new
                {
                    Value = EF.Property<string?>(e, rule.Property),
                    Warehouse = rule.WarehouseProperty == null ? null : EF.Property<string?>(e, rule.WarehouseProperty),
                    Plant = hasPlant ? EF.Property<string?>(e, nameof(IPlantEntity.PlantId)) : null,
                })
                .Where(x => x.Value != null && x.Value != "")
                .GroupBy(x => new { x.Value, x.Warehouse, x.Plant })
                .Select(g => new { g.Key.Value, g.Key.Warehouse, g.Key.Plant, Count = g.Count() })
                .ToListAsync(ct);
            return rows.Where(r => !string.IsNullOrWhiteSpace(r.Value))
                .Select(r => new StoredValue(r.Value!, r.Warehouse, string.IsNullOrEmpty(r.Plant) ? null : r.Plant, r.Count))
                .ToList();
        }

        private static async Task<List<(Request Request, ReferenceViolation Violation)>> CheckAsync(
            ApplicationDbContext db, List<Request> requests, bool includeAddedInThisSave, CancellationToken ct)
        {
            var violations = new List<(Request, ReferenceViolation)>();
            foreach (var group in requests.GroupBy(r => r.Rule.Master))
            {
                var keys = group.Select(r => r.Key).Distinct().ToList();
                var loaded = await LoadAsync(db, group.Key, keys, ct);
                var rows = (includeAddedInThisSave ? loaded.Concat(AddedInThisSave(db, group.Key)) : loaded)
                    .ToLookup(r => r.Key, StringComparer.Ordinal);

                foreach (var r in group)
                {
                    var problem = Evaluate(rows[r.Key].ToList(), IsPlantScoped(r.Rule.Master) && r.Rule.CheckPlant ? r.Plant : null);
                    if (problem == ReferenceProblem.Inactive && !r.Rule.CheckActive) problem = null;
                    if (problem != null)
                        violations.Add((r, new ReferenceViolation(r.Entity, r.Rule.Property, r.Rule.Master, r.Display, problem.Value)));
                }
            }
            return violations;
        }

        /// <summary>在地化的單筆說明，例如「機台「M01」已停用或廢棄」。</summary>
        /// <remarks>鍵逐一寫成靜態字串（不用 "RefVal_" + 列舉值 組字），資源鍵守門才看得到每一個鍵。</remarks>
        public static string Describe(ReferenceViolation v)
        {
            var master = v.Master switch
            {
                ReferenceMaster.Product => PzText.Get("RefMaster_Product"),
                ReferenceMaster.Company => PzText.Get("RefMaster_Company"),
                ReferenceMaster.Uom => PzText.Get("RefMaster_Uom"),
                ReferenceMaster.Currency => PzText.Get("RefMaster_Currency"),
                ReferenceMaster.Tax => PzText.Get("RefMaster_Tax"),
                ReferenceMaster.Container => PzText.Get("RefMaster_Container"),
                ReferenceMaster.Instrument => PzText.Get("RefMaster_Instrument"),
                ReferenceMaster.Warehouse => PzText.Get("RefMaster_Warehouse"),
                ReferenceMaster.StorageLocation => PzText.Get("RefMaster_StorageLocation"),
                ReferenceMaster.WorkCenter => PzText.Get("RefMaster_WorkCenter"),
                ReferenceMaster.Machine => PzText.Get("RefMaster_Machine"),
                ReferenceMaster.MachineOrLine => PzText.Get("RefMaster_MachineOrLine"),
                ReferenceMaster.Tooling => PzText.Get("RefMaster_Tooling"),
                ReferenceMaster.Routing => PzText.Get("RefMaster_Routing"),
                ReferenceMaster.ShiftGroup => PzText.Get("RefMaster_ShiftGroup"),
                ReferenceMaster.AccountingPeriod => PzText.Get("RefMaster_AccountingPeriod"),
                ReferenceMaster.CodeEntry => PzText.Get("RefMaster_CodeEntry"),
                ReferenceMaster.Shift => PzText.Get("RefMaster_Shift"),
                ReferenceMaster.Plant => PzText.Get("RefMaster_Plant"),
                ReferenceMaster.ChartOfAccount => PzText.Get("RefMaster_ChartOfAccount"),
                ReferenceMaster.User => PzText.Get("RefMaster_User"),
                ReferenceMaster.BankAccount => PzText.Get("RefMaster_BankAccount"),
                ReferenceMaster.DefectCode => PzText.Get("RefMaster_DefectCode"),
                ReferenceMaster.ProductCategory => PzText.Get("RefMaster_ProductCategory"),
                ReferenceMaster.Organization => PzText.Get("RefMaster_Organization"),
                _ => v.Master.ToString(),
            };
            return v.Problem switch
            {
                ReferenceProblem.NotFound => PzText.Get("RefVal_NotFound", master, v.Value),
                ReferenceProblem.OtherPlant => PzText.Get("RefVal_OtherPlant", master, v.Value),
                _ => PzText.Get("RefVal_Inactive", master, v.Value),
            };
        }

        private static ReferenceProblem? Evaluate(List<MasterRow> rows, string? plant)
        {
            if (rows.Count == 0) return ReferenceProblem.NotFound;
            if (plant != null)
            {
                rows = rows.Where(r => r.PlantId == null || r.PlantId == plant).ToList();
                if (rows.Count == 0) return ReferenceProblem.OtherPlant;
            }
            return rows.Any(r => r.Usable) ? null : ReferenceProblem.Inactive;
        }

        private static bool IsPlantScoped(ReferenceMaster m) => m is
            ReferenceMaster.Warehouse or ReferenceMaster.StorageLocation or ReferenceMaster.WorkCenter
            or ReferenceMaster.Machine or ReferenceMaster.MachineOrLine or ReferenceMaster.Tooling or ReferenceMaster.Routing
            or ReferenceMaster.ShiftGroup or ReferenceMaster.Shift or ReferenceMaster.AccountingPeriod
            or ReferenceMaster.Organization or ReferenceMaster.BankAccount;

        private static string? Current(EntityEntry entry, string property) =>
            entry.Property(property).CurrentValue as string;

        private static bool Changed(EntityEntry entry, string property)
        {
            var p = entry.Property(property);
            return !string.Equals(p.OriginalValue as string, p.CurrentValue as string, StringComparison.Ordinal);
        }

        // ── 主檔查詢：一次存檔每種主檔最多一次查詢；IgnoreQueryFilters，廠別由 Evaluate 自己比 ──

        private static async Task<List<MasterRow>> LoadAsync(
            ApplicationDbContext db, ReferenceMaster master, List<string> keys, CancellationToken ct)
        {
            switch (master)
            {
                case ReferenceMaster.Product:
                    return await db.Set<Product>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.ProductCode))
                        .Select(x => new MasterRow(x.ProductCode, null, x.IsActive)).ToListAsync(ct);
                case ReferenceMaster.Company:
                    return await db.Set<Company>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.CompanyCode))
                        .Select(x => new MasterRow(x.CompanyCode, null, x.IsActive)).ToListAsync(ct);
                case ReferenceMaster.Uom:
                    return await db.Set<Uom>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.UomCode))
                        .Select(x => new MasterRow(x.UomCode, null, x.IsActive)).ToListAsync(ct);
                case ReferenceMaster.Currency:
                    return await db.Set<Currency>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.CurrencyCode))
                        .Select(x => new MasterRow(x.CurrencyCode, null, x.IsActive)).ToListAsync(ct);
                case ReferenceMaster.Tax:
                    return await db.Set<Tax>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.TaxCode))
                        .Select(x => new MasterRow(x.TaxCode, null, x.IsActive)).ToListAsync(ct);
                case ReferenceMaster.Container:
                    return await db.Set<Container>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.ContainerCode))
                        .Select(x => new MasterRow(x.ContainerCode, null, x.IsActive)).ToListAsync(ct);
                case ReferenceMaster.Instrument:
                    return await db.Set<Instrument>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.InstrumentCode))
                        .Select(x => new MasterRow(x.InstrumentCode, null, x.IsActive)).ToListAsync(ct);
                case ReferenceMaster.Warehouse:
                    return await db.Set<Warehouse>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.WhCode))
                        .Select(x => new MasterRow(x.WhCode, x.PlantId, x.IsActive)).ToListAsync(ct);
                case ReferenceMaster.WorkCenter:
                    return await db.Set<WorkCenter>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.WorkCenterCode))
                        .Select(x => new MasterRow(x.WorkCenterCode, x.PlantId, x.IsActive)).ToListAsync(ct);
                case ReferenceMaster.Tooling:
                    return await db.Set<Tooling>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.ToolingCode))
                        .Select(x => new MasterRow(x.ToolingCode, x.PlantId, x.IsActive)).ToListAsync(ct);
                case ReferenceMaster.Routing:
                    return await db.Set<Routing>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.RoutingId))
                        .Select(x => new MasterRow(x.RoutingId, x.PlantId, x.IsActive)).ToListAsync(ct);
                case ReferenceMaster.ShiftGroup:
                    return await db.Set<ShiftGroup>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.GroupCode))
                        .Select(x => new MasterRow(x.GroupCode, x.PlantId, x.IsActive)).ToListAsync(ct);
                case ReferenceMaster.User:
                    // 使用者主檔沒有停用欄位（鎖定是暫時性的登入保護，不代表離職）：只驗存在
                    return await db.Users.AsNoTracking().Where(x => keys.Contains(x.Id))
                        .Select(x => new MasterRow(x.Id, null, true)).ToListAsync(ct);
                case ReferenceMaster.ChartOfAccount:
                    return await db.Set<ChartOfAccount>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.AccountCode))
                        .Select(x => new MasterRow(x.AccountCode, null, x.IsActive)).ToListAsync(ct);
                case ReferenceMaster.BankAccount:
                    return await db.Set<BankAccount>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.AccountId))
                        .Select(x => new MasterRow(x.AccountId, x.PlantId, x.IsActive)).ToListAsync(ct);
                case ReferenceMaster.Plant:
                    // 廠別主檔沒有啟用欄位：只驗存在
                    return await db.Set<Plant>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.Id))
                        .Select(x => new MasterRow(x.Id, null, true)).ToListAsync(ct);
                case ReferenceMaster.DefectCode:
                    return await db.Set<DefectCode>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.Code))
                        .Select(x => new MasterRow(x.Code, null, x.IsActive)).ToListAsync(ct);
                case ReferenceMaster.Shift:
                    return await db.Set<Shift>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.ShiftCode))
                        .Select(x => new MasterRow(x.ShiftCode, x.PlantId, x.IsActive)).ToListAsync(ct);
                case ReferenceMaster.ProductCategory:
                    return await db.Set<ProductCategory>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.CategoryCode))
                        .Select(x => new MasterRow(x.CategoryCode, null, x.IsActive)).ToListAsync(ct);
                case ReferenceMaster.Organization:
                {
                    // 組織單位的廠別可為空、沿上層繼承（同 InternalOrderController.GetEffectivePlantId）；
                    // 表很小，整表載入後在記憶體走上層。繼承到最上層仍為空＝全公司共用單位，任何廠都可用。
                    var units = await db.Set<OrganizationUnit>().IgnoreQueryFilters().AsNoTracking()
                        .Select(x => new { x.Id, x.ParentId, x.PlantId }).ToListAsync(ct);
                    var byId = units.ToDictionary(u => u.Id);
                    string? Effective(string id)
                    {
                        for (var depth = 0; depth < 32 && byId.TryGetValue(id, out var u); depth++)
                        {
                            if (!string.IsNullOrEmpty(u.PlantId)) return u.PlantId;
                            if (string.IsNullOrEmpty(u.ParentId)) return null;
                            id = u.ParentId;
                        }
                        return null;
                    }
                    return keys.Where(byId.ContainsKey).Select(k => new MasterRow(k, Effective(k), true)).ToList();
                }
                case ReferenceMaster.AccountingPeriod:
                    // 主鍵 (PeriodId, PlantId)：同一期間各廠一列，由 Evaluate 依廠別挑
                    return await db.Set<AccountingPeriod>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.PeriodId))
                        .Select(x => new MasterRow(x.PeriodId, x.PlantId, true)).ToListAsync(ct);
                case ReferenceMaster.Machine:
                    // WorkCenterMachine 不是 IPlantEntity，廠別經由工作中心；同代碼可能掛在多個工作中心
                    return await db.Set<WorkCenterMachine>().IgnoreQueryFilters().AsNoTracking().Where(x => keys.Contains(x.MachineCode))
                        .Select(x => new MasterRow(x.MachineCode, x.WorkCenter!.PlantId, x.MachineStatus != MachineStatus.Retired))
                        .ToListAsync(ct);
                case ReferenceMaster.MachineOrLine:
                {
                    var machines = await LoadAsync(db, ReferenceMaster.Machine, keys, ct);
                    var lines = await db.Set<WorkCenter>().IgnoreQueryFilters().AsNoTracking()
                        .Where(x => x.IsProductionLine && keys.Contains(x.WorkCenterCode))
                        .Select(x => new MasterRow(x.WorkCenterCode, x.PlantId, x.IsActive)).ToListAsync(ct);
                    return machines.Concat(lines).ToList();
                }
                case ReferenceMaster.StorageLocation:
                {
                    // 主鍵 (WhCode, LocationCode)，沒有 PlantId（見 R30）：廠別取所屬倉庫
                    var locs = keys.Select(k => k.Split('|', 2)[1]).Distinct().ToList();
                    var rows = await db.Set<StorageLocation>().IgnoreQueryFilters().AsNoTracking()
                        .Where(x => locs.Contains(x.LocationCode))
                        .Select(x => new { x.WhCode, x.LocationCode, x.Warehouse!.PlantId, x.IsActive })
                        .ToListAsync(ct);
                    return rows.Select(x => new MasterRow($"{x.WhCode}|{x.LocationCode}", x.PlantId, x.IsActive))
                        .Where(r => keys.Contains(r.Key)).ToList();
                }
                case ReferenceMaster.CodeEntry:
                {
                    var codes = keys.Select(k => k.Split('|', 2)[1]).Distinct().ToList();
                    var rows = await db.Set<CodeEntry>().IgnoreQueryFilters().AsNoTracking()
                        .Where(x => codes.Contains(x.Code))
                        .Select(x => new { x.Category, x.Code, x.IsActive })
                        .ToListAsync(ct);
                    return rows.Select(x => new MasterRow($"{x.Category}|{x.Code}", null, x.IsActive))
                        .Where(r => keys.Contains(r.Key)).ToList();
                }
                default:
                    throw new NotSupportedException($"Unsupported reference master: {master}");
            }
        }

        /// <summary>同一次存檔中新增、尚未寫入 DB 的主檔。</summary>
        private static IEnumerable<MasterRow> AddedInThisSave(ApplicationDbContext db, ReferenceMaster master)
        {
            IEnumerable<T> Added<T>() where T : class =>
                db.ChangeTracker.Entries<T>().Where(e => e.State == EntityState.Added).Select(e => e.Entity);

            return master switch
            {
                ReferenceMaster.Product => Added<Product>().Select(x => new MasterRow(x.ProductCode, null, x.IsActive)),
                ReferenceMaster.Company => Added<Company>().Select(x => new MasterRow(x.CompanyCode, null, x.IsActive)),
                ReferenceMaster.Uom => Added<Uom>().Select(x => new MasterRow(x.UomCode, null, x.IsActive)),
                ReferenceMaster.Currency => Added<Currency>().Select(x => new MasterRow(x.CurrencyCode, null, x.IsActive)),
                ReferenceMaster.Tax => Added<Tax>().Select(x => new MasterRow(x.TaxCode, null, x.IsActive)),
                ReferenceMaster.Container => Added<Container>().Select(x => new MasterRow(x.ContainerCode, null, x.IsActive)),
                ReferenceMaster.Instrument => Added<Instrument>().Select(x => new MasterRow(x.InstrumentCode, null, x.IsActive)),
                ReferenceMaster.Warehouse => Added<Warehouse>().Select(x => new MasterRow(x.WhCode, x.PlantId ?? db.CurrentPlantId, x.IsActive)),
                ReferenceMaster.WorkCenter => Added<WorkCenter>().Select(x => new MasterRow(x.WorkCenterCode, x.PlantId ?? db.CurrentPlantId, x.IsActive)),
                ReferenceMaster.Tooling => Added<Tooling>().Select(x => new MasterRow(x.ToolingCode, x.PlantId ?? db.CurrentPlantId, x.IsActive)),
                ReferenceMaster.Routing => Added<Routing>().Select(x => new MasterRow(x.RoutingId, x.PlantId ?? db.CurrentPlantId, x.IsActive)),
                ReferenceMaster.ShiftGroup => Added<ShiftGroup>().Select(x => new MasterRow(x.GroupCode, x.PlantId ?? db.CurrentPlantId, x.IsActive)),
                ReferenceMaster.User => Added<ApplicationUser>().Select(x => new MasterRow(x.Id, null, true)),
                ReferenceMaster.ChartOfAccount => Added<ChartOfAccount>().Select(x => new MasterRow(x.AccountCode, null, x.IsActive)),
                ReferenceMaster.BankAccount => Added<BankAccount>().Select(x => new MasterRow(x.AccountId, x.PlantId ?? db.CurrentPlantId, x.IsActive)),
                ReferenceMaster.Plant => Added<Plant>().Select(x => new MasterRow(x.Id, null, true)),
                ReferenceMaster.DefectCode => Added<DefectCode>().Select(x => new MasterRow(x.Code, null, x.IsActive)),
                ReferenceMaster.Shift => Added<Shift>().Select(x => new MasterRow(x.ShiftCode, x.PlantId ?? db.CurrentPlantId, x.IsActive)),
                ReferenceMaster.ProductCategory => Added<ProductCategory>().Select(x => new MasterRow(x.CategoryCode, null, x.IsActive)),
                ReferenceMaster.Organization => Added<OrganizationUnit>().Select(x => new MasterRow(x.Id, x.PlantId, true)),
                ReferenceMaster.AccountingPeriod => Added<AccountingPeriod>().Select(x => new MasterRow(x.PeriodId, x.PlantId ?? db.CurrentPlantId, true)),
                ReferenceMaster.Machine or ReferenceMaster.MachineOrLine =>
                    Added<WorkCenterMachine>().Select(x => new MasterRow(x.MachineCode, null, x.MachineStatus != MachineStatus.Retired)),
                ReferenceMaster.StorageLocation => Added<StorageLocation>().Select(x => new MasterRow($"{x.WhCode}|{x.LocationCode}", null, x.IsActive)),
                ReferenceMaster.CodeEntry => Added<CodeEntry>().Select(x => new MasterRow($"{x.Category}|{x.Code}", null, x.IsActive)),
                _ => Enumerable.Empty<MasterRow>(),
            };
        }
    }
}
