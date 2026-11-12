using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// 簽核相關 HTTP 端點整合測試：
    /// - 驗證所有 Approval / DocumentApprovalConfig 路由均受授權保護
    /// - 未登入時重導至 Login 頁面
    /// </summary>
    [Collection("Integration")]
    public class ApprovalIntegrationTests
    {
        private readonly TestWebApplicationFactory _factory;

        public ApprovalIntegrationTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
        }

        // ── Approval 端點授權保護 ─────────────────────────────────────────────────

        [Theory]
        [InlineData("/Approval/PendingIndex")]
        [InlineData("/Approval/AllRequests")]
        public async Task Get_ApprovalEndpoints_Unauthenticated_RedirectsToLogin(string url)
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

        [Theory]
        [InlineData("/Approval/Process/nonexistent")]
        [InlineData("/Approval/History/nonexistent")]
        public async Task Get_ApprovalDetailEndpoints_Unauthenticated_RedirectsToLogin(string url)
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

        // ── DocumentApprovalConfig 端點授權保護 ──────────────────────────────────

        [Theory]
        [InlineData("/DocumentApprovalConfig")]
        [InlineData("/DocumentApprovalConfig/Create")]
        [InlineData("/DocumentApprovalConfig/Edit/nonexistent")]
        public async Task Get_DocumentApprovalConfigEndpoints_Unauthenticated_RedirectsToLogin(string url)
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

        // ── ApprovalTemplate 端點授權保護 ─────────────────────────────────────────

        [Theory]
        [InlineData("/ApprovalTemplate")]
        [InlineData("/ApprovalTemplate/Create")]
        [InlineData("/ApprovalTemplate/Edit/nonexistent")]
        public async Task Get_ApprovalTemplateEndpoints_Unauthenticated_RedirectsToLogin(string url)
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

        // ── POST 端點授權保護（無 CSRF token，應重導或 400）────────────────────────

        [Theory]
        [InlineData("/Approval/Process/R1")]
        [InlineData("/DocumentApprovalConfig/Delete/C1")]
        [InlineData("/ApprovalTemplate/Delete/T1")]
        public async Task Post_ApprovalEndpoints_Unauthenticated_ReturnsRedirectOrBadRequest(string url)
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

            var response = await client.PostAsync(url, new FormUrlEncodedContent([]));

            Assert.True(
                response.StatusCode == HttpStatusCode.Redirect ||
                response.StatusCode == HttpStatusCode.BadRequest,
                $"Expected Redirect or BadRequest, got {response.StatusCode} for {url}");
        }
    }
}
