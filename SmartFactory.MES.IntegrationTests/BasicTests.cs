using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    [Collection("Integration")]
    public class BasicTests
    {
        private readonly TestWebApplicationFactory _factory;

        public BasicTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Theory]
        [InlineData("/")]
        [InlineData("/Identity/Account/Login")]
        public async Task Get_EndpointsReturnSuccessAndCorrectContentType(string url)
        {
            var client = _factory.CreateClient();
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            Assert.Equal("text/html; charset=utf-8",
                response.Content.Headers.ContentType!.ToString());
        }
    }
}
