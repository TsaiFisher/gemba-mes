using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Modules._Shared.ReferenceValidation;
using SmartFactory.MES.Services;
using MfgM = SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Validation
{
    /// <summary>
    /// 參照欄位檢查已正式啟用：正式程式路徑的存檔會被擋，而且呼叫端拿到看得懂的訊息而不是 500。
    /// 驗的是實際接線（ReferenceValidationModule → Core 的 IModuleSaveChangesInterceptor 擴充點 → 全站例外 Filter），
    /// 單元測試驗不到。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ReferenceValidationActivationTests
    {
        internal const string Plant = "P1";
        private const string MissingProduct = "REFACT-NO-SUCH";

        private readonly AuthedScheduleFactory _factory;

        public ReferenceValidationActivationTests(AuthedScheduleFactory factory) => _factory = factory;

        internal static MfgM.WorkOrder BadWorkOrder() => new()
        {
            WorkOrderId = $"REFACT-{Guid.NewGuid():N}"[..20], ProductionBatchId = "REFACT-B", ProductCode = MissingProduct,
            RoutingId = "", Quantity = 1, Status = MfgM.WorkOrderStatus.Planned, PlantId = Plant, CreatedByRoleId = "1",
        };

        [Fact]
        public async Task 正式路徑存檔_參照不存在_整批不寫入並丟出檢查例外()
        {
            using var scope = _factory.Services.CreateScope();
            // 有 HttpContext＝請求處理中的正式程式路徑（測試種子補主檔的攔截器只在沒有 HttpContext 時作用）
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CurrentPlantId = Plant;
            var wo = BadWorkOrder();
            db.WorkOrders.Add(wo);

            var ex = await Assert.ThrowsAsync<ReferenceValidationException>(() => db.SaveChangesAsync());

            Assert.Contains(ex.Violations, v => v.Value == MissingProduct && v.Problem == ReferenceProblem.NotFound);
            using var verify = _factory.Services.CreateScope();
            Assert.Null(await verify.ServiceProvider.GetRequiredService<ApplicationDbContext>().WorkOrders.FindAsync(wo.WorkOrderId));
        }

        [Theory]
        [InlineData("/RefValProbe/Save", true, HttpStatusCode.OK)]                // 側欄面板 AJAX
        [InlineData("/api/mobile/refval-probe/save", false, HttpStatusCode.BadRequest)]   // 手機 API
        public async Task HTTP請求存檔被擋_回友善訊息而非500(string url, bool ajax, HttpStatusCode expected)
        {
            using var app = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
                s.AddControllers().AddApplicationPart(typeof(RefValProbeController).Assembly)));
            var client = app.CreateClient();
            if (ajax) client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");

            var resp = await client.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();

            Assert.True(resp.StatusCode == expected, $"{(int)resp.StatusCode}: {body[..Math.Min(400, body.Length)]}");
            using var json = JsonDocument.Parse(body);
            Assert.False(json.RootElement.GetProperty("success").GetBoolean());
            var message = ajax
                ? json.RootElement.GetProperty("message").GetString()
                : json.RootElement.GetProperty("error").GetProperty("message").GetString();
            Assert.Contains(MissingProduct, message);
            if (!ajax)
                Assert.Equal(ReferenceValidationExceptionFilter.MobileErrorCode,
                    json.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    /// <summary>只為 <see cref="ReferenceValidationActivationTests"/> 存在（MVC 只認頂層 public 類別為控制器）：在請求中存一筆參照不存在品號的工單。</summary>
    public sealed class RefValProbeController(ApplicationDbContext db) : Controller
    {
        [HttpGet("/RefValProbe/Save")]
        [HttpGet("/api/mobile/refval-probe/save")]
        [PermissionGateExempt("integration-test probe")]
        public async Task<IActionResult> Save()
        {
            db.CurrentPlantId = ReferenceValidationActivationTests.Plant;
            db.WorkOrders.Add(ReferenceValidationActivationTests.BadWorkOrder());
            await db.SaveChangesAsync();
            return Ok();
        }
    }
}
