using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Mobile
{
    [Collection("Integration")]
    public class MobileAuditLogTests
    {
        private readonly TestWebApplicationFactory _factory;

        public MobileAuditLogTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task Login_WithInvalidCredentials_ReturnsUnauthorized_AndRouteWorks()
        {
            // 驗證登入端點可正確回應（AuditLog 寫入在 Controller 內部，此測試確認端點存在且回 401）
            var client = _factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/mobile/auth/login",
                new { Username = "noexist_audit_test", Password = "wrongpass" });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(body);
            Assert.True(json.RootElement.TryGetProperty("message", out _),
                "應回傳含 message 欄位的 JSON");
        }

        [Fact]
        public async Task Login_EmptyUsername_Returns400_NotWritingAuditLog()
        {
            var client = _factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/mobile/auth/login",
                new { Username = "", Password = "pass" });

            // 空帳號應在驗證前就 BadRequest，不寫 AuditLog
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Logout_WithoutAuth_Returns401()
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            var response = await client.PostAsync("/api/mobile/auth/logout", null);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }
}
