using System.Net;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    [Collection("Integration")]
    public class HealthCheckTests
    {
        private readonly TestWebApplicationFactory _factory;

        public HealthCheckTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task Get_HealthEndpoint_ReturnsOk()
        {
            var client = _factory.CreateClient();
            var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task Get_HealthEndpoint_ReturnsJsonContentType()
        {
            var client = _factory.CreateClient();
            var response = await client.GetAsync("/health");
            Assert.NotNull(response.Content.Headers.ContentType);
            Assert.Equal("application/json", response.Content.Headers.ContentType.MediaType);
        }

        [Fact]
        public async Task Get_HealthEndpoint_ContainsStatusField()
        {
            var client = _factory.CreateClient();
            var response = await client.GetAsync("/health");
            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("status", content, StringComparison.OrdinalIgnoreCase);
        }
    }
}
