using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using SmartFactory.MES.Api.Mobile;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Mobile;

/// <summary>推播訂閱的 SSRF／濫用防護：只收白名單推播服務、金鑰格式正確，且每人訂閱數有上限。</summary>
public class MobilePushSubscribeTests : TestBase
{
    private static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static readonly string ValidP256dh = B64Url([0x04, .. Enumerable.Range(1, 64).Select(i => (byte)i)]);
    private static readonly string ValidAuth = B64Url(Enumerable.Range(1, 16).Select(i => (byte)i).ToArray());

    private MobilePushController Controller(string userId = "push-user", int? max = null)
    {
        var values = new Dictionary<string, string?>();
        if (max != null) values["WebPush:MaxSubscriptionsPerUser"] = max.ToString();
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var user = new Mock<IUserContext>();
        user.SetupGet(x => x.UserId).Returns(userId);
        return new MobilePushController(_context, user.Object, config, Mock.Of<IPushNotificationService>());
    }

    [Fact]
    public async Task Subscribe_LegitimateProviderEndpoint_IsStored()
    {
        var result = await Controller().Subscribe(new SubscribeRequest(
            "https://fcm.googleapis.com/fcm/send/abc", ValidP256dh, ValidAuth, "UA"));
        Assert.IsType<OkObjectResult>(result);
        Assert.Single(_context.PushSubscriptions);
    }

    [Theory]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://intranet.local/push")]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc")]
    [InlineData("https://fcm.googleapis.com.attacker.example/x")]
    public async Task Subscribe_NonProviderEndpoint_IsRejectedWithoutWrite(string endpoint)
    {
        var result = await Controller().Subscribe(new SubscribeRequest(endpoint, ValidP256dh, ValidAuth, null));
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_context.PushSubscriptions);
    }

    [Theory]
    [InlineData("AAAA", null)]          // p256dh 長度錯
    [InlineData(null, "AAAA")]          // auth 長度錯
    [InlineData("not base64!!", null)]
    public async Task Subscribe_MalformedKeys_AreRejected(string? p256dh, string? auth)
    {
        var result = await Controller().Subscribe(new SubscribeRequest(
            "https://fcm.googleapis.com/fcm/send/abc", p256dh ?? ValidP256dh, auth ?? ValidAuth, null));
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_context.PushSubscriptions);
    }

    [Fact]
    public async Task Subscribe_OverPerUserLimit_IsRejected_ButRenewingOwnSubscriptionStillWorks()
    {
        for (var i = 0; i < 2; i++)
            _context.PushSubscriptions.Add(new PushSubscriptionRecord
            {
                UserId = "push-user", Endpoint = $"https://fcm.googleapis.com/fcm/send/{i}",
                P256dhKey = ValidP256dh, AuthKey = ValidAuth
            });
        await _context.SaveChangesAsync();

        var over = await Controller(max: 2).Subscribe(new SubscribeRequest(
            "https://fcm.googleapis.com/fcm/send/new", ValidP256dh, ValidAuth, null));
        Assert.IsType<BadRequestObjectResult>(over);

        var renew = await Controller(max: 2).Subscribe(new SubscribeRequest(
            "https://fcm.googleapis.com/fcm/send/0", ValidP256dh, ValidAuth, "new UA"));
        Assert.IsType<OkObjectResult>(renew);
        Assert.Equal(2, _context.PushSubscriptions.Count());
    }

    [Theory]
    [InlineData("https://attacker.example/phish")]
    [InlineData("//attacker.example/phish")]
    public async Task SendTest_ExternalUrl_IsRejectedBeforeSending(string url)
    {
        // 先有一筆訂閱：否則「沒有訂閱」本身就回 400，測不到網址檢查。
        _context.PushSubscriptions.Add(new PushSubscriptionRecord
            { UserId = "push-user", Endpoint = "https://fcm.googleapis.com/fcm/send/t", P256dhKey = ValidP256dh, AuthKey = ValidAuth });
        await _context.SaveChangesAsync();
        var push = new Mock<IPushNotificationService>();
        var user = new Mock<IUserContext>();
        user.SetupGet(x => x.UserId).Returns("push-user");
        var controller = new MobilePushController(_context, user.Object,
            new ConfigurationBuilder().Build(), push.Object)
        {
            Url = new Microsoft.AspNetCore.Mvc.Routing.UrlHelper(new ActionContext(
                new Microsoft.AspNetCore.Http.DefaultHttpContext(), new Microsoft.AspNetCore.Routing.RouteData(),
                new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()))
        };
        var result = await controller.SendTest(new PushTestRequest("t", "b", url));
        Assert.IsType<BadRequestObjectResult>(result);
        push.VerifyNoOtherCalls();
    }
}
