using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Api.Mobile;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Mobile
{
    /// <summary>
    /// 機台狀態異動的**廠別界線**。
    ///
    /// <para><b>守的是什麼</b>：<see cref="WorkCenterMachine"/> 沒有 <c>PlantId</c> 欄位、
    /// 也不是 <c>IPlantEntity</c>（主鍵是 <c>WorkCenterCode</c>+<c>MachineCode</c>），
    /// 所以 <c>ApplicationDbContext</c> 的**全域廠別過濾攔不到它**——廠別掛在它的
    /// <see cref="WorkCenter"/> 上。查詢少寫一個條件，別廠的機台碼照樣查得到，
    /// 而端點接著會做兩件事：把 <c>machine.MachineStatus</c> 改掉（動到**別廠的機台主檔**），
    /// 以及用當下情境的 <c>PlantId</c> 開一張異動單。兩件都會成功、都不會報錯。</para>
    ///
    /// <para><b>為什麼這條路徑真的走得到</b>：離線佇列不記廠別（記錄只有 url/method/body），
    /// 廠別是 flush 當下由伺服端決定的。所以「離線報修 → 切廠 → 恢復連線自動同步」
    /// 就是一條完整的觸發路徑，而且中間沒有任何一步需要使用者做錯事。</para>
    ///
    /// <para>⚠️ 這裡**直接建構 controller** 而不是走 HTTP：本測試要控制的是「登入者當下在哪個廠」，
    /// 而那是使用者資料列上的 <c>ActivePlantId</c>——用 HTTP 就得改測試帳號的欄位，
    /// 那會影響共用測試庫裡其他人的測試。<c>IUserContext</c> 是這個事實唯一的讀取點，
    /// 換掉它就等於換掉「我在哪個廠」，比改資料乾淨。</para>
    /// </summary>
    [Collection("Integration")]
    public class MobileMachineStatusPlantScopeTests
    {
        // 共用測試庫，一律用專屬前綴，收尾照前綴清。
        private const string Prefix     = "ZZMSPS";
        private const string PlantMine  = Prefix + "-A";   // 登入者當下所在的廠
        private const string PlantOther = Prefix + "-B";   // 機台實際所在的廠
        private const string WcMine     = Prefix + "-WCA";
        private const string WcOther    = Prefix + "-WCB";
        private const string MachineMine  = Prefix + "-MA";
        private const string MachineOther = Prefix + "-MB";
        private const string RoleId     = "1";

        private readonly TestWebApplicationFactory _factory;

        public MobileMachineStatusPlantScopeTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed();
        }

        [Fact]
        public async Task 別廠的機台碼查不到_而且不會動到那台機台的狀態()
        {
            using var scope = NewScope(out var db);
            var controller = NewController(scope, db, PlantMine);

            var result = await controller.MachineStatusEvent(MachineOther, Req("DI"));

            var notFound = Assert.IsType<NotFoundObjectResult>(result);
            Assert.Equal("MACHINE_NOT_FOUND",
                Assert.IsType<MobileErrorResult>(notFound.Value).Error?.Code);

            // 🚨 最重要的一條：別廠機台的**主檔狀態**必須原封不動。
            // 少了這條，一個「照樣改狀態但回 404」的實作也會通過上面的斷言。
            using var verify = NewScope(out var vdb);
            var machine = await vdb.WorkCenterMachines.IgnoreQueryFilters()
                .SingleAsync(m => m.WorkCenterCode == WcOther && m.MachineCode == MachineOther);
            Assert.Equal(MachineStatus.Available, machine.MachineStatus);

            // 也不該留下任何異動單明細。
            Assert.False(await vdb.MachineStatusOrderLines.IgnoreQueryFilters()
                .AnyAsync(l => l.MachineCode == MachineOther),
                "跨廠的請求被擋下了，卻仍留下機台異動單明細。");
        }

        [Fact]
        public async Task 同廠的機台照樣異動得了()
        {
            // 沒有這一支的話，一個「永遠回 404」的實作也會讓上面那支變綠——
            // 而那種實作的症狀是「機台狀態整個功能壞掉」，比原本的缺陷更嚴重。
            using var scope = NewScope(out var db);
            var controller = NewController(scope, db, PlantMine);

            var result = await controller.MachineStatusEvent(MachineMine, Req("DI"));

            Assert.IsType<OkObjectResult>(result);

            using var verify = NewScope(out var vdb);
            var machine = await vdb.WorkCenterMachines.IgnoreQueryFilters()
                .SingleAsync(m => m.WorkCenterCode == WcMine && m.MachineCode == MachineMine);
            Assert.Equal(MachineStatus.BreakDown, machine.MachineStatus);
        }

        [Fact]
        public async Task 讀取端與寫入端的廠別條件一致()
        {
            // 讀得到卻寫不進去（或反過來）會變成「畫面查得到這台機台，按下去說找不到」，
            // 這種回報查不出原因，因為兩支端點各自看起來都正常。
            using var scope = NewScope(out var db);
            var controller = NewController(scope, db, PlantMine);

            Assert.IsType<NotFoundObjectResult>(await controller.GetMachine(MachineOther));
            Assert.IsType<OkObjectResult>(await controller.GetMachine(MachineMine));
        }

        [Fact]
        public async Task 斷線補送的事件_記發生時間而不是送達時間()
        {
            // 10:00 報修、10:15 才連上：停機要從 10:00 算起，不是 10:15（mobile-pwa.md §5.2）
            using var scope = NewScope(out var db);
            var controller = NewController(scope, db, PlantMine);
            var req = Req("DI") with { ClientTimestamp = DateTime.UtcNow.AddMinutes(-15) };
            controller.ControllerContext.HttpContext.Request.Headers[MobileOccurredAt.SentAtHeader] = DateTime.UtcNow.ToString("O");

            Assert.IsType<OkObjectResult>(await controller.MachineStatusEvent(MachineMine, req));

            var at = await LastEventAtAsync(MachineMine);
            Assert.InRange(at, DateTime.UtcNow.AddMinutes(-15.5), DateTime.UtcNow.AddMinutes(-14.5));
        }

        [Fact]
        public async Task 補送的事件不早於這台機台上一筆事件_停機區間不會算反()
        {
            using (var scope = NewScope(out var db))
                Assert.IsType<OkObjectResult>(await NewController(scope, db, PlantMine).MachineStatusEvent(MachineMine, Req("DI")));
            var first = await LastEventAtAsync(MachineMine);

            using (var scope = NewScope(out var db))
            {
                var controller = NewController(scope, db, PlantMine);
                controller.ControllerContext.HttpContext.Request.Headers[MobileOccurredAt.SentAtHeader] = DateTime.UtcNow.ToString("O");
                var queued = Req("HO") with { ClientTimestamp = DateTime.UtcNow.AddMinutes(-20) };
                Assert.IsType<OkObjectResult>(await controller.MachineStatusEvent(MachineMine, queued));
            }

            Assert.Equal(first, await LastEventAtAsync(MachineMine));
        }

        private async Task<DateTime> LastEventAtAsync(string machineCode)
        {
            using var verify = NewScope(out var vdb);
            return await vdb.MachineStatusOrderLines.IgnoreQueryFilters()
                .Where(l => l.MachineCode == machineCode)
                .Select(l => l.Order!.EventAt).OrderByDescending(t => t).FirstAsync();
        }

        // ── 輔助 ────────────────────────────────────────────────────────────

        private static MachineStatusEventRequest Req(string eventType) =>
            new(EventType: eventType, Note: null, SourceDocId: null,
                ClientId: $"{Prefix}-{Guid.NewGuid():N}", ClientTimestamp: DateTime.UtcNow);

        private IServiceScope NewScope(out ApplicationDbContext db)
        {
            var scope = _factory.Services.CreateScope();
            db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // 無 HTTP 時 Current* 預設是 null，全域過濾會把所有資料濾掉。
            db.CurrentPlantId = PlantMine;
            db.CurrentRoleId  = RoleId;
            return scope;
        }

        private static MobileMfgController NewController(
            IServiceScope scope, ApplicationDbContext db, string plantId)
        {
            var sp = scope.ServiceProvider;
            var controller = new MobileMfgController(
                db,
                new FixedPlantUserContext(plantId),
                sp.GetRequiredService<IReportEtaNotifier>(),
                sp.GetRequiredService<ISamplingPlanService>(),
                new AllowAllMobilePermission(),
                sp.GetRequiredService<IProductionCostAccrualService>(),
                sp.GetRequiredService<IStagingSourceService>(),
                sp.GetRequiredService<IFpiGateService>(),
                sp.GetRequiredService<IMfgSimplifiedModeService>(),
                sp.GetRequiredService<IOperationSequenceGuard>());

            // MachineStatusEvent 的成功路徑會用 HttpContext.RequestServices 取
            // IToolingShotService（保養完成歸零打次），沒有 ControllerContext 會 NRE。
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = sp },
            };
            return controller;
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // 每次重建，讓測試可重跑（上一輪可能已把 MachineMine 改成 BreakDown）。
            var lines = db.MachineStatusOrderLines.IgnoreQueryFilters()
                .Where(l => l.MachineCode == MachineMine || l.MachineCode == MachineOther).ToList();
            if (lines.Count > 0)
            {
                var orderIds = lines.Select(l => l.OrderId).Distinct().ToList();
                db.MachineStatusOrderLines.RemoveRange(lines);
                db.MachineStatusOrders.RemoveRange(db.MachineStatusOrders.IgnoreQueryFilters()
                    .Where(o => orderIds.Contains(o.OrderId)));
                db.SaveChanges();
            }

            var machines = db.WorkCenterMachines.IgnoreQueryFilters()
                .Where(m => m.WorkCenterCode == WcMine || m.WorkCenterCode == WcOther).ToList();
            if (machines.Count > 0) { db.WorkCenterMachines.RemoveRange(machines); db.SaveChanges(); }

            foreach (var (wc, plant) in new[] { (WcMine, PlantMine), (WcOther, PlantOther) })
            {
                if (!db.WorkCenters.IgnoreQueryFilters().Any(w => w.WorkCenterCode == wc))
                    db.WorkCenters.Add(new WorkCenter
                    {
                        WorkCenterCode = wc,
                        WorkCenterName = wc,
                        PlantId = plant,
                    });
            }
            db.SaveChanges();

            db.WorkCenterMachines.AddRange(
                new WorkCenterMachine
                {
                    WorkCenterCode = WcMine, MachineCode = MachineMine,
                    MachineName = MachineMine, MachineStatus = MachineStatus.Available,
                },
                new WorkCenterMachine
                {
                    WorkCenterCode = WcOther, MachineCode = MachineOther,
                    MachineName = MachineOther, MachineStatus = MachineStatus.Available,
                });
            db.SaveChanges();
        }

        /// <summary>只有廠別是變數的測試用 <see cref="IUserContext"/>。</summary>
        private sealed class FixedPlantUserContext : IUserContext
        {
            public FixedPlantUserContext(string plantId) => PlantId = plantId;

            public string? PlantId { get; }
            public string? TimeZoneId     => "Asia/Taipei";
            public string? Language       => "zh-TW";
            public string? UserId         => "admin";
            public string? ActiveRoleId   => RoleId;
            public string? ActiveRolePath => null;

            // ⚠️ 空清單在這支介面上的意思是「不限廠＝全部」，不是「一個廠都沒有」。
            // 這裡照既有慣例回空，因為本測試的廠別界線由 PlantId 決定，與這支無關。
            public IReadOnlyList<string> AllowedPlantIds => new List<string>();
        }

        private sealed class AllowAllMobilePermission : IMobilePermissionService
        {
            public Task<IEnumerable<SmartFactory.MES.Models.MobileModule>> GetAllowedModulesAsync(string roleId)
                => Task.FromResult(Enumerable.Empty<SmartFactory.MES.Models.MobileModule>());
            public Task<bool> CanExecuteAsync(string roleId, string moduleKey, string actionKey)
                => Task.FromResult(true);
            public Task<IEnumerable<string>> GetAllowedActionKeysAsync(string roleId, string moduleKey)
                => Task.FromResult(Enumerable.Empty<string>());
        }
    }
}
