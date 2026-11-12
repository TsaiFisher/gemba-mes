using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SmartFactory.MES.Middleware;
using SmartFactory.MES.Services;
using System.Security.Claims;
using Xunit;

namespace SmartFactory.MES.Tests.Middleware
{
    /// <summary>
    /// CSP nonce：每個請求一個不可預測的值，且必須是可直接放進標頭與屬性的 base64url。
    /// 2026-10-20 總檢時發現 Core 沒有任何測試（CSP 是全站 inline script 放行的唯一依據）。
    /// </summary>
    public class CspNonceMiddlewareTests
    {
        private static async Task<(string? Nonce, bool NextCalled)> RunAsync()
        {
            var called = false;
            var mw = new CspNonceMiddleware(_ => { called = true; return Task.CompletedTask; });
            var ctx = new DefaultHttpContext();
            await mw.InvokeAsync(ctx);
            return (ctx.Items[CspNonceMiddleware.ItemKey] as string, called);
        }

        [Fact]
        public async Task SetsBase64UrlNonce_Of128Bits_AndCallsNext()
        {
            var (nonce, called) = await RunAsync();

            Assert.True(called);
            Assert.NotNull(nonce);
            Assert.Equal(22, nonce!.Length);                          // 16 bytes → 22 字元（去掉 ==）
            Assert.Matches("^[A-Za-z0-9_-]+$", nonce);               // 無 + / =：可直接放進標頭與屬性
        }

        [Fact]
        public async Task NonceDiffersPerRequest()
        {
            var nonces = new HashSet<string>();
            for (int i = 0; i < 50; i++) nonces.Add((await RunAsync()).Nonce!);
            Assert.Equal(50, nonces.Count);
        }
    }

    /// <summary>
    /// 使用者偏好中介層：把目前廠別、角色、組織路徑寫進 DbContext——全域廠別過濾與資料權限都靠這三個值。
    /// 漏寫任何一個，查詢會靜默看到別廠／別人的資料或什麼都看不到。
    /// </summary>
    public class UserPreferenceMiddlewareTests : TestBase
    {
        private static Mock<IUserContext> User(string? plant, string? role, string? path)
        {
            var u = new Mock<IUserContext>();
            u.SetupGet(x => x.PlantId).Returns(plant);
            u.SetupGet(x => x.ActiveRoleId).Returns(role);
            u.SetupGet(x => x.ActiveRolePath).Returns(path);
            u.SetupGet(x => x.UserId).Returns("U1");
            return u;
        }

        private static DefaultHttpContext Http(bool authenticated, Mock<ILogger<UserPreferenceMiddleware>> logger)
        {
            var services = new ServiceCollection().AddSingleton(logger.Object).BuildServiceProvider();
            return new DefaultHttpContext
            {
                RequestServices = services,
                User = authenticated
                    ? new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "U1") }, "Test"))
                    : new ClaimsPrincipal(new ClaimsIdentity()),
            };
        }

        [Fact]
        public async Task CopiesPlantRoleAndPath_ToDbContext_AndCallsNext()
        {
            var called = false;
            var mw = new UserPreferenceMiddleware(_ => { called = true; return Task.CompletedTask; });
            var logger = new Mock<ILogger<UserPreferenceMiddleware>>();

            await mw.InvokeAsync(Http(true, logger), User("P1", "R9", "/HQ/A").Object, _context);

            Assert.True(called);
            Assert.Equal(("P1", "R9", "/HQ/A"), (_context.CurrentPlantId, _context.CurrentRoleId, _context.CurrentRolePath));
            logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
        }

        /// <summary>已登入卻沒有廠別要留下警告（資料會被廠別過濾全部擋掉，畫面只會是一片空白）。</summary>
        [Theory]
        [InlineData(true, 1)]
        [InlineData(false, 0)]   // 未登入（登入頁等）沒有廠別是正常的
        public async Task MissingPlant_WarnsOnlyWhenAuthenticated(bool authenticated, int warnings)
        {
            var mw = new UserPreferenceMiddleware(_ => Task.CompletedTask);
            var logger = new Mock<ILogger<UserPreferenceMiddleware>>();

            await mw.InvokeAsync(Http(authenticated, logger), User(null, null, null).Object, _context);

            Assert.Null(_context.CurrentPlantId);
            logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Exactly(warnings));
        }
    }
}
