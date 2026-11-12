using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Api.Mobile;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using MfgM = SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Mobile
{
    /// <summary>
    /// 手機上工頁的備料／架機四支（<c>dispatches/{id}/staging</c>、<c>staging-order</c>、
    /// <c>setup/start</c>、<c>setup/done</c>）。2026-09-28 盤點時這四支 0 支測試。
    ///
    /// <para>備料單的展開邏輯本身已由 <c>StagingSourceServiceTests</c> 單元測試涵蓋，這裡只驗
    /// 手機端的接線：狀態轉移、clientId 去重、錯誤代碼。</para>
    ///
    /// <para><b>與桌機對齊</b>：備料完成後要不要進「等待首件」，桌機 <c>DoneSetup</c> 走
    /// <c>IFpiGateService</c>（要求首件<b>且尚未通過</b>），手機曾只看工序旗標——已通過首件的派工
    /// 再備料一次就被打回等待首件，而開工只放行可開工狀態，品管得重做一次首件。</para>
    /// </summary>
    [Collection("Integration")]
    public class MobileMfgSetupTests
    {
        private const string Plant = "ZZMSU";

        private readonly TestWebApplicationFactory _factory;
        private readonly MobileMfgTestKit.Ids _ids = MobileMfgTestKit.Ids.For("MSU", Guid.NewGuid().ToString("N")[..8]);

        public MobileMfgSetupTests(TestWebApplicationFactory factory) => _factory = factory;

        // ── 開始備料 ─────────────────────────────────────────────────────────

        [Fact]
        public async Task 開始備料_派工進備料中並建一筆進行中的備料紀錄()
        {
            Seed(MfgM.DispatchStatus.Scheduled);

            Assert.IsType<OkObjectResult>(await SetupStartAsync());

            Assert.Equal(MfgM.DispatchStatus.SetupInProgress, (await LoadDispatchAsync()).Status);
            var setup = Assert.Single(await LoadSetupsAsync());
            Assert.Equal(MfgM.ProductionSetupStatus.Open, setup.Status);
        }

        [Fact]
        public async Task 開始備料同一個clientId重送_只建一筆()
        {
            Seed(MfgM.DispatchStatus.Scheduled);
            var clientId = MobileMfgTestKit.NewClientId();

            Assert.IsType<OkObjectResult>(await SetupStartAsync(clientId));
            // 沒走去重的話第二次會撞 SETUP_ALREADY_OPEN，佇列會把它當失敗
            Assert.IsType<OkObjectResult>(await SetupStartAsync(clientId));

            Assert.Single(await LoadSetupsAsync());
        }

        [Fact]
        public async Task 備料中再按開始備料_拒絕且不重複建紀錄()
        {
            // 一般機台進了備料中就不在可開始備料的狀態，會先被狀態檢查擋下；
            // SETUP_ALREADY_OPEN 只有生產線（允許多人同時備料）同一人重按才走得到
            Seed(MfgM.DispatchStatus.ReadyToStart);
            Assert.IsType<OkObjectResult>(await SetupStartAsync());

            MobileMfgTestKit.AssertError<BadRequestObjectResult>(await SetupStartAsync(), "INVALID_STATUS");
            Assert.Single(await LoadSetupsAsync());
        }

        [Fact]
        public async Task 生產中的派工_不可開始備料()
        {
            Seed(MfgM.DispatchStatus.InProgress);

            MobileMfgTestKit.AssertError<BadRequestObjectResult>(await SetupStartAsync(), "INVALID_STATUS");
            Assert.Empty(await LoadSetupsAsync());
        }

        // ── 作業員＝登入者本人（2026-09-29 安全盤點）──────────────────────────
        // 原本 req.OperatorId ?? 登入者：前端不送，但伺服器照收，改過的前端就能用別人的名義上工。

        [Fact]
        public async Task 開始備料帶別人的作業員代號_拒絕且不建紀錄()
        {
            Seed(MfgM.DispatchStatus.Scheduled);

            MobileMfgTestKit.AssertError<ObjectResult>(await SetupStartAsync(operatorId: "someone-else"), "OPERATOR_MISMATCH");

            Assert.Empty(await LoadSetupsAsync());
            Assert.Equal(MfgM.DispatchStatus.Scheduled, (await LoadDispatchAsync()).Status);
        }

        [Fact]
        public async Task 開始備料帶本人代號_照常並記在本人名下()
        {
            Seed(MfgM.DispatchStatus.Scheduled);

            Assert.IsType<OkObjectResult>(await SetupStartAsync(operatorId: "admin"));

            Assert.Equal("admin", Assert.Single(await LoadSetupsAsync()).OperatorId);
        }

        [Fact]
        public async Task 上工帶別人的作業員代號_拒絕且不建上工紀錄()
        {
            Seed(MfgM.DispatchStatus.ReadyToStart);

            MobileMfgTestKit.AssertError<ObjectResult>(await StartSessionAsync(operatorId: "someone-else"), "OPERATOR_MISMATCH");

            Assert.Empty(await LoadSessionsAsync());
            Assert.Equal(MfgM.DispatchStatus.ReadyToStart, (await LoadDispatchAsync()).Status);
        }

        // ── 上工改填的機台／模具（2026-09-29 安全盤點）────────────────────────
        // 原本 req.MachineId ?? 派工機台、req.MoldId 照收：打錯字、別廠或不存在的代號都寫進上工紀錄。

        [Fact]
        public async Task 上工改填別的工作中心或不存在的機台_拒絕且不建上工紀錄()
        {
            Seed(MfgM.DispatchStatus.ReadyToStart);
            var (_, otherWcMachine) = SeedWorkCenterMachines();

            MobileMfgTestKit.AssertError<BadRequestObjectResult>(
                await StartSessionAsync(machineId: otherWcMachine), "MACHINE_NOT_ALLOWED");
            MobileMfgTestKit.AssertError<BadRequestObjectResult>(
                await StartSessionAsync(machineId: "MSU-NO-SUCH"), "MACHINE_NOT_ALLOWED");

            Assert.Empty(await LoadSessionsAsync());
            Assert.Equal(MfgM.DispatchStatus.ReadyToStart, (await LoadDispatchAsync()).Status);
        }

        [Fact]
        public async Task 上工改填同工作中心的機台_照常並記在該機台且派工改掛該機台()
        {
            Seed(MfgM.DispatchStatus.ReadyToStart);
            var (sameWcMachine, _) = SeedWorkCenterMachines();

            Assert.IsType<OkObjectResult>(await StartSessionAsync(machineId: sameWcMachine));

            Assert.Equal(sameWcMachine, Assert.Single(await LoadSessionsAsync()).MachineCode);
            // 派工進生產中即凍結、重跑排程不改機台；不改掛的話甘特與排程佔用都會算在原機台
            Assert.Equal(sameWcMachine, (await LoadDispatchAsync()).MachineCode);
        }

        // ── 機台狀態閘口（MfgMachineStatusGate，預設 Warning）────────────────────

        [Fact]
        public async Task 上工_閘口Block且機台故障停機_拒絕且不建上工紀錄()
        {
            Seed(MfgM.DispatchStatus.ReadyToStart);
            var (machine, _) = SeedWorkCenterMachines(SmartFactory.MES.Modules.BAS.Models.MachineStatus.BreakDown);
            await SetGateAsync("Block");
            try
            {
                var err = MobileMfgTestKit.AssertError<BadRequestObjectResult>(
                    await StartSessionAsync(machineId: machine), "MACHINE_UNAVAILABLE");
                // 直接呼叫 Controller 時文化是測試進程的（en），比對資源鍵而非中文片段
                PzAssert.Message("Mob_MachineUnavailableFmt", err.Error!.Message);
                Assert.Contains(SmartFactory.MES.Modules._Shared.PzText.Get("Mob_MachineStatus_BreakDown"), err.Error!.Message);
            }
            finally { await SetGateAsync(null); }

            Assert.Empty(await LoadSessionsAsync());
            Assert.Equal(MfgM.DispatchStatus.ReadyToStart, (await LoadDispatchAsync()).Status);
        }

        [Fact]
        public async Task 上工_未設定閘口預設警示_照常上工並帶回機台狀態()
        {
            Seed(MfgM.DispatchStatus.ReadyToStart);
            var (machine, _) = SeedWorkCenterMachines(SmartFactory.MES.Modules.BAS.Models.MachineStatus.Maintenance);

            var ok   = Assert.IsType<OkObjectResult>(await StartSessionAsync(machineId: machine));
            var data = Assert.IsType<MobileOkResult>(ok.Value).Data;

            Assert.Equal("Maintenance", ReadProperty<string>(data, "MachineWarning"));
            Assert.Single(await LoadSessionsAsync());
        }

        [Fact]
        public async Task 上工帶排程以外的模具_拒絕()
        {
            Seed(MfgM.DispatchStatus.ReadyToStart);

            MobileMfgTestKit.AssertError<BadRequestObjectResult>(
                await StartSessionAsync(moldId: "MSU-OTHER-MOLD"), "TOOLING_MISMATCH");

            Assert.Empty(await LoadSessionsAsync());
        }

        // ── 備料完成 ─────────────────────────────────────────────────────────

        [Fact]
        public async Task 備料完成_不需首件_派工可開工且備料紀錄關閉()
        {
            Seed(MfgM.DispatchStatus.Scheduled);
            Assert.IsType<OkObjectResult>(await SetupStartAsync());

            Assert.IsType<OkObjectResult>(await SetupDoneAsync());

            Assert.Equal(MfgM.DispatchStatus.ReadyToStart, (await LoadDispatchAsync()).Status);
            var setup = Assert.Single(await LoadSetupsAsync());
            Assert.Equal(MfgM.ProductionSetupStatus.Closed, setup.Status);
            Assert.NotNull(setup.SetupEndAt);
        }

        [Fact]
        public async Task 備料完成_需首件且尚未通過_進等待首件()
        {
            Seed(MfgM.DispatchStatus.Scheduled, requiresFpi: true);
            Assert.IsType<OkObjectResult>(await SetupStartAsync());

            Assert.IsType<OkObjectResult>(await SetupDoneAsync());

            Assert.Equal(MfgM.DispatchStatus.PendingFPI, (await LoadDispatchAsync()).Status);
        }

        [Fact]
        public async Task 備料完成_需首件但已通過_直接可開工()
        {
            // 首件通過後派工回到可開工；此時再備料一次（換料、重新架機）不該把它打回等待首件
            Seed(MfgM.DispatchStatus.ReadyToStart, requiresFpi: true, fpiPassed: true);
            Assert.IsType<OkObjectResult>(await SetupStartAsync());

            Assert.IsType<OkObjectResult>(await SetupDoneAsync());

            Assert.Equal(MfgM.DispatchStatus.ReadyToStart, (await LoadDispatchAsync()).Status);
        }

        [Fact]
        public async Task 不在備料中的派工_不可備料完成()
        {
            Seed(MfgM.DispatchStatus.Scheduled);

            MobileMfgTestKit.AssertError<BadRequestObjectResult>(await SetupDoneAsync(), "INVALID_STATUS");
            Assert.Equal(MfgM.DispatchStatus.Scheduled, (await LoadDispatchAsync()).Status);
        }

        // ── 備料單 ───────────────────────────────────────────────────────────

        [Fact]
        public async Task 開備料單_帶入未發料用料_之後查詢看得到單且不再列待備()
        {
            Seed(MfgM.DispatchStatus.Scheduled, materials: true);

            var ok = Assert.IsType<OkObjectResult>(await CreateStagingOrderAsync());
            var stagingId = ReadProperty<string>(Assert.IsType<MobileOkResult>(ok.Value).Data, "StagingId");

            var staging = Assert.IsType<MobileOkResult>(
                Assert.IsType<OkObjectResult>(await GetStagingAsync(_ids.DispatchId)).Value).Data;
            var orders  = ReadProperty<System.Collections.IEnumerable>(staging, "Orders").Cast<object>().ToList();
            var pending = ReadProperty<System.Collections.IEnumerable>(staging, "Pending").Cast<object>().ToList();

            var order = Assert.Single(orders);
            Assert.Equal(stagingId, ReadProperty<string>(order, "StagingId"));
            Assert.Equal(1, ReadProperty<int>(order, "LineCount"));   // 已全數發料的那一行不帶入
            Assert.Empty(pending);   // 已開單的量要扣掉，否則現場會再開一張把同一批料鎖兩次
        }

        [Fact]
        public async Task 開備料單同一個clientId重送_只開一張()
        {
            Seed(MfgM.DispatchStatus.Scheduled, materials: true);
            var clientId = MobileMfgTestKit.NewClientId();

            Assert.IsType<OkObjectResult>(await CreateStagingOrderAsync(clientId));
            Assert.IsType<OkObjectResult>(await CreateStagingOrderAsync(clientId));

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(1, await db.Set<MaterialStagingOrder>().IgnoreQueryFilters()
                .CountAsync(o => o.SourceType == "Dispatch" && o.SourceId == _ids.DispatchId));
        }

        [Fact]
        public async Task 沒有待備料的用料_開單回明確錯誤()
        {
            Seed(MfgM.DispatchStatus.Scheduled);

            MobileMfgTestKit.AssertError<BadRequestObjectResult>(await CreateStagingOrderAsync(), "NO_PENDING_MATERIAL");
        }

        [Fact]
        public async Task 查備料_派工不存在回404()
        {
            MobileMfgTestKit.AssertError<NotFoundObjectResult>(await GetStagingAsync("MSU-NO-SUCH"), "DISPATCH_NOT_FOUND");
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        private async Task<IActionResult> SetupStartAsync(string? clientId = null, string? operatorId = null)
        {
            using var scope = _factory.Services.CreateScope();
            return await MobileMfgTestKit.MakeController(scope, Plant).SetupStart(_ids.DispatchId,
                new SetupStartRequest(operatorId, clientId ?? MobileMfgTestKit.NewClientId(), DateTime.UtcNow, ToolingCode: null));
        }

        private async Task<IActionResult> StartSessionAsync(
            string? machineId = null, string? moldId = null, string? operatorId = null)
        {
            using var scope = _factory.Services.CreateScope();
            return await MobileMfgTestKit.MakeController(scope, Plant).StartSession(new SessionStartRequest(
                _ids.DispatchId, machineId, moldId, operatorId, MobileMfgTestKit.NewClientId(), DateTime.UtcNow));
        }

        private async Task<List<MfgM.ProductionSession>> LoadSessionsAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.ProductionSessions.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.DispatchId == _ids.DispatchId).ToListAsync();
        }

        /// <summary>
        /// 建兩個本廠工作中心各一台機台，並把派工工序掛到第一個。回傳（同工作中心機台, 別的工作中心機台）。
        /// 代號帶實例後綴：WorkCenterCode 是全域主鍵，測試庫又跨 worktree 共用。
        /// </summary>
        /// <summary>本類別專用廠別的機台閘口覆寫；傳 null 清掉覆寫（回到繼承全域）。一律經服務寫，才會清快取。</summary>
        private async Task SetGateAsync(string? mode)
        {
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<SmartFactory.MES.Modules.BAS.Services.IPlantParamService>();
            if (mode == null) await svc.ClearAsync(ProjectZSystemParamKeys.MfgMachineStatusGate, Plant);
            else await svc.SetAsync(ProjectZSystemParamKeys.MfgMachineStatusGate, Plant, mode);
        }

        private (string SameWc, string OtherWc) SeedWorkCenterMachines(
            SmartFactory.MES.Modules.BAS.Models.MachineStatus status = SmartFactory.MES.Modules.BAS.Models.MachineStatus.Available)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            string wcA = $"MSUA-{suffix}", wcB = $"MSUB-{suffix}";
            string mA  = $"MSUMA-{suffix}", mB = $"MSUMB-{suffix}";

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            foreach (var (wc, m) in new[] { (wcA, mA), (wcB, mB) })
            {
                db.WorkCenters.Add(new SmartFactory.MES.Modules.BAS.Models.WorkCenter
                {
                    WorkCenterCode = wc, WorkCenterName = wc, PlantId = Plant,
                });
                db.WorkCenterMachines.Add(new SmartFactory.MES.Modules.BAS.Models.WorkCenterMachine
                {
                    WorkCenterCode = wc, MachineCode = m, MachineName = m, MachineStatus = status,
                });
            }
            var op = db.WorkOrderOperations.IgnoreQueryFilters().Single(o => o.WorkOrderId == _ids.WoId && o.OperationSeq == 10);
            op.WorkCenterId = wcA;
            db.SaveChanges();
            return (mA, mB);
        }

        private async Task<IActionResult> SetupDoneAsync()
        {
            using var scope = _factory.Services.CreateScope();
            return await MobileMfgTestKit.MakeController(scope, Plant).SetupDone(_ids.DispatchId,
                new SetupDoneRequest(MobileMfgTestKit.NewClientId(), DateTime.UtcNow));
        }

        private async Task<IActionResult> CreateStagingOrderAsync(string? clientId = null)
        {
            using var scope = _factory.Services.CreateScope();
            return await MobileMfgTestKit.MakeController(scope, Plant).CreateStagingOrder(_ids.DispatchId,
                new SetupDoneRequest(clientId ?? MobileMfgTestKit.NewClientId(), DateTime.UtcNow));
        }

        private async Task<IActionResult> GetStagingAsync(string dispatchId)
        {
            using var scope = _factory.Services.CreateScope();
            return await MobileMfgTestKit.MakeController(scope, Plant).GetStaging(dispatchId);
        }

        private async Task<MfgM.Dispatch> LoadDispatchAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.Dispatches.IgnoreQueryFilters().AsNoTracking().FirstAsync(d => d.DispatchId == _ids.DispatchId);
        }

        private async Task<List<MfgM.ProductionSetup>> LoadSetupsAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.ProductionSetups.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.DispatchId == _ids.DispatchId).ToListAsync();
        }

        private static T ReadProperty<T>(object? obj, string name) =>
            (T)obj!.GetType().GetProperty(name)!.GetValue(obj)!;

        private void Seed(MfgM.DispatchStatus status, bool requiresFpi = false, bool fpiPassed = false, bool materials = false)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            MobileMfgTestKit.SeedDispatch(db, Plant, _ids, status, requiresFpi: requiresFpi);

            if (fpiPassed)
                db.InspectionOrders.Add(new InspectionOrder
                {
                    PlantId = Plant, InspectionType = "FPI", OrderType = InspectionOrderType.FPI,
                    SourceDocId = _ids.DispatchId, ProductCode = "MOBKIT-P", Status = "Pass",
                });

            if (materials)
            {
                db.Set<MfgM.DispatchMaterial>().Add(new MfgM.DispatchMaterial
                {
                    DispatchId = _ids.DispatchId, BomSeq = 10, ProductCode = "MSU-MAT-A",
                    RequiredQty = 50, IssuedQty = 20, UomCode = "PCS",
                });
                db.Set<MfgM.DispatchMaterial>().Add(new MfgM.DispatchMaterial
                {
                    DispatchId = _ids.DispatchId, BomSeq = 20, ProductCode = "MSU-MAT-B",
                    RequiredQty = 30, IssuedQty = 30, UomCode = "PCS",   // 已全數發料
                });
            }
            db.SaveChanges();
        }
    }
}
