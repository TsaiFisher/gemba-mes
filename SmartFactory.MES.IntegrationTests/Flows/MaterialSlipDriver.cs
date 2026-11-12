using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 以「使用者按下按鈕」的方式驅動 <see cref="MaterialSlipController"/>。
    ///
    /// 為什麼一定要走真正的 Controller：發退料單的狀態守門、WorkOrderMaterial 累計量回寫、
    /// 預留消耗全都寫在 Controller 動作裡，不在服務層。測試若自己呼叫過帳服務再手動回寫
    /// IssuedQty，驗到的是測試自己的實作，狀態機與回寫的缺陷會全部驗不出來。
    ///
    /// 每個動作各開一個 scope（各自的 DbContext），模擬前端一個動作一個請求；
    /// 共用同一個 DbContext 會讓上一步殘留在追蹤器裡的實體掩蓋「這一步真的存進去了嗎」。
    /// </summary>
    internal sealed class MaterialSlipDriver
    {
        private readonly TestWebApplicationFactory _factory;
        private readonly string _plant, _role;

        public MaterialSlipDriver(TestWebApplicationFactory factory, string plant, string role)
        {
            _factory = factory; _plant = plant; _role = role;
        }

        /// <summary>Controller 動作回傳的 <c>Json(new { success, message })</c>。</summary>
        internal sealed record JsonOutcome(bool Ok, string? Message)
        {
            public void AssertOk(string what)
                => Assert.True(Ok, $"{what} 應成功，實際失敗：{Message}");

            public void AssertRejected(string what)
                => Assert.False(Ok, $"{what} 應被守門擋下，實際卻放行了");
        }

        public Task<JsonOutcome> SubmitAsync(string id)     => InvokeAsync(c => c.Submit(id));
        public Task<JsonOutcome> ApproveAsync(string id)    => InvokeAsync(c => c.Approve(id));
        public Task<JsonOutcome> MarkPickingAsync(string id)=> InvokeAsync(c => c.MarkPicking(id));
        public Task<JsonOutcome> ConfirmAsync(string id)    => InvokeAsync(c => c.Confirm(id));
        public Task<JsonOutcome> IssueAsync(string id)      => InvokeAsync(c => c.Issue(id));
        public Task<JsonOutcome> CancelAsync(string id)     => InvokeAsync(c => c.Cancel(id));

        /// <summary>
        /// 存檔（新增或編輯）。成功時 Controller 回一段 inline script（ContentResult），
        /// 失敗時回 _Form 局部檢視；故以回傳型別判定成敗。
        /// </summary>
        public async Task<bool> SaveAsync(MaterialSlip model)
        {
            using var scope = NewController(out var ctrl);
            return await ctrl.Save(model) is ContentResult;
        }

        /// <summary>來源單號下拉（MaterialSlipController.SourceDocs）回的單號清單。</summary>
        public async Task<List<string>> SourceDocIdsAsync(string type, string category, string? current = null)
        {
            using var scope = NewController(out var ctrl);
            var json = Assert.IsType<JsonResult>(await ctrl.SourceDocs(type, category, current));
            return ((System.Collections.IEnumerable)json.Value!).Cast<object>()
                .Select(o => (string)o.GetType().GetProperty("Id")!.GetValue(o)!).ToList();
        }

        /// <summary>存檔失敗時的錯誤訊息（ModelState）；成功回 null。</summary>
        public async Task<string?> SaveErrorAsync(MaterialSlip model)
        {
            UseRequestCulture();
            using var scope = NewController(out var ctrl);
            if (await ctrl.Save(model) is ContentResult) return null;
            return string.Join(" / ", ctrl.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
        }

        private async Task<JsonOutcome> InvokeAsync(Func<MaterialSlipController, Task<IActionResult>> act)
        {
            UseRequestCulture();
            using var scope = NewController(out var ctrl);
            var json = Assert.IsType<JsonResult>(await act(ctrl));
            var v = json.Value!;
            var ok = v.GetType().GetProperty("success")?.GetValue(v);
            var msg = v.GetType().GetProperty("message")?.GetValue(v)?.ToString();
            return new JsonOutcome(Equals(ok, true), msg);
        }

        /// <summary>
        /// 比照真實請求設定 UI 語系（真實請求由 RequestLocalization 中介層設定；這裡直接 new Controller，沒有經過它）。
        ///
        /// <para>不設的話訊息語系取決於跑測試的那台機器：本機 dotnet CLI 帶 <c>DOTNET_CLI_UI_LANGUAGE=en-US</c>
        /// 讓 testhost 是 en-US，查得到英文訊息；CI runner 是 invariant，而 ProjectZ 的資源檔只有 zh-TW／en-US
        /// 沒有中性版，<c>_localizer[...]</c> 回的是鍵名本身——本機綠、CI 紅（2026-10-16 PR #234）。</para>
        ///
        /// <para>只在呼叫它的 async 方法裡生效（CurrentUICulture 是 AsyncLocal），返回後自動還原。</para>
        /// </summary>
        private static void UseRequestCulture()
            => System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("zh-TW");

        private IServiceScope NewController(out MaterialSlipController ctrl)
        {
            var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CurrentPlantId = _plant;
            db.CurrentRoleId  = _role;

            var sp = new TestControllerServices(scope.ServiceProvider, db, _plant, _role);
            ctrl = new MaterialSlipController(
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>(),
                sp.GetRequiredService<IDocNumberService>(),
                sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IInventoryReservationService>(),
                sp.GetRequiredService<IDateTimeService>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { RequestServices = sp },
                },
            };
            return scope;
        }
    }

    /// <summary>
    /// ModuleControllerBase 的 Db／Permissions／UserContext 是從 HttpContext.RequestServices
    /// 延遲解析的（非建構子注入），故只能用一層 provider 蓋掉這三個，其餘一律回落到真實 scope。
    /// </summary>
    internal sealed class TestControllerServices : IServiceProvider
    {
        private readonly IServiceProvider _inner;
        private readonly ApplicationDbContext _db;
        private readonly IUserContext _user;
        private readonly IPermissionService _perm = new AllowAllPermission();

        public TestControllerServices(IServiceProvider inner, ApplicationDbContext db, string plant, string role)
        {
            _inner = inner; _db = db; _user = new FakeUser(plant, role);
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ApplicationDbContext)) return _db;
            if (serviceType == typeof(IUserContext))         return _user;
            if (serviceType == typeof(IPermissionService))   return _perm;
            return _inner.GetService(serviceType);
        }
    }

    internal sealed class FakeUser : IUserContext
    {
        private readonly string _plant, _role;
        public FakeUser(string plant, string role) { _plant = plant; _role = role; }
        public string? PlantId => _plant;
        public string? TimeZoneId => "Asia/Taipei";
        public string? Language => "zh-TW";
        public string? UserId => "tester";
        public string? ActiveRoleId => _role;
        public string? ActiveRolePath => null;
        public IReadOnlyList<string> AllowedPlantIds => new List<string> { _plant };
    }

    internal sealed class AllowAllPermission : IPermissionService
    {
        public Task<bool> CanViewData(string ownerRoleId) => Task.FromResult(true);
        public IQueryable<T> FilterByPermission<T>(IQueryable<T> query) where T : class, IOwnedByRole => query;
        public Task<bool> CanRead(string menuKey) => Task.FromResult(true);
        public Task<bool> CanCreate(string menuKey) => Task.FromResult(true);
        public Task<bool> CanUpdate(string menuKey) => Task.FromResult(true);
        public Task<bool> CanDelete(string menuKey) => Task.FromResult(true);
        public Task<bool> CanPrint(string menuKey) => Task.FromResult(true);
    }
}
