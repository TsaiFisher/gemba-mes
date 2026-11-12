using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared.ReferenceValidation;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// 整合測試專用：**測試程式直接寫入種子資料**時，替參照到、但還不存在的主檔補一筆最小資料
    /// （品號、途程、機台、往來廠商、單位…），讓參照欄位檢查放行。
    ///
    /// <para>為什麼需要：參照欄位檢查（<see cref="ReferenceValidationInterceptor"/>）啟用前寫的測試，
    /// 種子只建單據、不建它引用的主檔（例如派工指到機台 MOBKIT-M，卻沒有這台機台）。
    /// 那些測試驗的是別的行為，主檔與它們無關；逐一補主檔是 38 個類別的機械性改動。</para>
    ///
    /// <para>界線：</para>
    /// <list type="bullet">
    /// <item><b>只在沒有 HttpContext 時作用</b>——也就是測試程式自己 <c>SaveChanges</c>。經由 HTTP 進到
    ///   正式程式碼的存檔照常檢查，不會被這裡放過。</item>
    /// <item><b>只補「不存在」</b>：主檔存在但屬他廠或已停用，代表種子本身不一致，照常擋下、要修測試。</item>
    /// <item>補的主檔一律啟用、屬參照它的那筆資料的廠別（明細列取 CurrentPlantId）。</item>
    /// </list>
    /// 排在參照檢查攔截器之前（同一個 IModuleSaveChangesInterceptor 清單、插在最前面）。
    /// </summary>
    public sealed class SeedMasterStubInterceptor(IHttpContextAccessor http) : SaveChangesInterceptor, IModuleSaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (http.HttpContext == null && eventData.Context is ApplicationDbContext db)
                StubMissingAsync(db).GetAwaiter().GetResult();
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (http.HttpContext == null && eventData.Context is ApplicationDbContext db)
                await StubMissingAsync(db, cancellationToken);
            return result;
        }

        private static async Task StubMissingAsync(ApplicationDbContext db, CancellationToken ct = default)
        {
            // 補上的主檔本身也會被檢查（例如補了品號，品號的庫存單位也要存在）：最多補三輪
            for (var round = 0; round < 3; round++)
            {
                var missing = (await ReferenceValidator.ValidateAsync(db, ct))
                    // 會計期間主鍵含廠別：「屬他廠」就是本廠還沒有這一期
                    .Where(v => v.Problem == ReferenceProblem.NotFound
                                || (v.Problem == ReferenceProblem.OtherPlant && v.Master == ReferenceMaster.AccountingPeriod))
                    // 主鍵不含廠別的主檔只補一筆（同一個品號可能被兩個廠的單據參照）
                    .Select(v => (v.Master, v.Value, Plant: PlantOf(db, v)))
                    .DistinctBy(x => (x.Master, x.Value, PlantScoped(x.Master) ? x.Plant : null))
                    .ToList();
                if (missing.Count == 0) return;
                foreach (var (master, value, plant) in missing) Add(db, master, value, plant);
            }
        }

        /// <summary>主鍵含廠別（同代碼各廠各一筆）的主檔：依廠別各補一筆。其餘主鍵全域，只補一筆、廠別取第一個參照者。</summary>
        private static bool PlantScoped(ReferenceMaster m) => m is
            ReferenceMaster.Machine or ReferenceMaster.MachineOrLine or ReferenceMaster.AccountingPeriod;

        /// <summary>參照這個值的那筆資料的廠別；明細列（不分廠）取 CurrentPlantId。</summary>
        private static string? PlantOf(ApplicationDbContext db, ReferenceViolation v)
        {
            var raw = v.Master == ReferenceMaster.StorageLocation ? v.Value.Split('/', 2)[1] : v.Value;
            var entry = db.ChangeTracker.Entries().FirstOrDefault(e =>
                e.Metadata.ClrType.Name == v.Entity
                && e.State is EntityState.Added or EntityState.Modified
                && e.Property(v.Property).CurrentValue as string == raw);
            return (entry?.Entity as IPlantEntity)?.PlantId is { Length: > 0 } p ? p : db.CurrentPlantId;
        }

        private static void Add(ApplicationDbContext db, ReferenceMaster master, string value, string? plant)
        {
            switch (master)
            {
                case ReferenceMaster.Uom:
                    db.Add(new Uom { UomCode = value, UomName = value, DecimalPlaces = 4 }); break;
                case ReferenceMaster.Currency:
                    db.Add(new Currency { CurrencyCode = value, CurrencyName = value }); break;
                case ReferenceMaster.Product:
                    db.Add(new Product { ProductCode = value, ProductName = value, StockUom = "PCS" }); break;
                case ReferenceMaster.Company:
                    db.Add(new Company { CompanyCode = value, CompanyName = value, IsSupplier = true, IsCustomer = true }); break;
                case ReferenceMaster.Plant:
                    db.Add(new Plant { Id = value, Name = value }); break;
                case ReferenceMaster.Routing:
                    db.Add(new Routing { RoutingId = value, PlantId = plant }); break;
                case ReferenceMaster.WorkCenter:
                    db.Add(new WorkCenter { WorkCenterCode = value, WorkCenterName = value, PlantId = plant }); break;
                case ReferenceMaster.Machine or ReferenceMaster.MachineOrLine:
                    // 機台掛在同名的工作中心下（廠別經由工作中心）
                    // 工作中心代碼上限 20 字：太長就改用雜湊（同廠同機台每次得到同一個代碼）
                    var wc = $"SW-{plant}-{value}";
                    if (wc.Length > 20)
                        wc = "SW-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                            System.Text.Encoding.UTF8.GetBytes(wc)))[..12];
                    db.Add(new WorkCenter { WorkCenterCode = wc, WorkCenterName = wc, PlantId = plant });
                    db.Add(new WorkCenterMachine { WorkCenterCode = wc, MachineCode = value, MachineName = value });
                    break;
                case ReferenceMaster.Warehouse:
                    db.Add(new Warehouse { WhCode = value, WhName = value, PlantId = plant }); break;
                case ReferenceMaster.AccountingPeriod when value.Length == 6 && int.TryParse(value, out var ym):
                    var start = new DateOnly(ym / 100, ym % 100, 1);
                    db.Add(new Modules.ACT.Models.AccountingPeriod
                    {
                        PeriodId = value, PlantId = plant, Year = start.Year, Month = start.Month,
                        StartDate = start, EndDate = start.AddMonths(1).AddDays(-1),
                    });
                    break;
                case ReferenceMaster.StorageLocation:
                    var parts = value.Split('/', 2);
                    db.Add(new StorageLocation { WhCode = parts[0], LocationCode = parts[1], LocationName = parts[1] }); break;
                default:
                    throw new NotSupportedException(
                        $"Test seed references missing {master} '{value}'; seed it explicitly or extend {nameof(SeedMasterStubInterceptor)}.");
            }
        }
    }

    public static class SeedMasterStubServiceCollectionExtensions
    {
        /// <summary>註冊 <see cref="SeedMasterStubInterceptor"/>，排在所有模組攔截器（含參照檢查）之前。</summary>
        public static void AddSeedMasterStubs(this IServiceCollection services)
        {
            services.AddHttpContextAccessor();
            services.Insert(0, ServiceDescriptor.Scoped<IModuleSaveChangesInterceptor, SeedMasterStubInterceptor>());
        }
    }
}
