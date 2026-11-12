using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    [Collection("Integration")]
    public class AuthTests
    {
        private readonly TestWebApplicationFactory _factory;

        public AuthTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Theory]
        [InlineData("/Organization")]
        [InlineData("/User")]
        [InlineData("/Role")]
        public async Task Get_ProtectedEndpoints_RedirectsToLogin(string url)
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("http://localhost/Identity/Account/Login",
                response.Headers.Location!.ToString());
        }
    }
}
