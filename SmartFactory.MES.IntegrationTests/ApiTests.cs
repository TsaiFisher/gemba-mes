using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    [Collection("Integration")]
    public class ApiTests
    {
        private readonly TestWebApplicationFactory _factory;

        public ApiTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Theory]
        // /api/workorders 是尚未實作的 WorkOrder API（不在任何分支），原資料會
        // 回 404 而非 401，使本安全測試恆紅。改指向實際存在且 [Authorize] 的
        // WarehouseApiController（/api/warehouse/*），仍確實驗證「未登入存取 API 應 401」。
        [InlineData("/api/warehouse/locations")]
        public async Task Get_ApiEndpoints_WithoutAuth_ReturnsUnauthorized(string url)
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Get_SwaggerUi_InDevelopment_ReturnsOkOrNotFound()
        {
            var client = _factory.CreateClient();
            var response = await client.GetAsync("/swagger/index.html");
            Assert.True(
                response.StatusCode == HttpStatusCode.OK ||
                response.StatusCode == HttpStatusCode.NotFound,
                $"Unexpected status: {response.StatusCode}");
        }
    }
}
