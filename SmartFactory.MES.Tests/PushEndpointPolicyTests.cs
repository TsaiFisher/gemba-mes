using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests;

public class PushEndpointPolicyTests : TestBase
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(x => x.Key, x => (string?)x.Value)).Build();

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc", true)]
    [InlineData("https://a.notify.windows.com/push", true)]
    [InlineData("https://web.push.apple.com/abc", true)]
    [InlineData("http://fcm.googleapis.com/abc", false)]
    [InlineData("https://fcm.googleapis.com:8443/abc", false)]
    [InlineData("https://fcm.googleapis.com.evil.example/abc", false)]
    [InlineData("https://evilnotify.windows.com/abc", false)]
    [InlineData("https://user:pass@fcm.googleapis.com/abc", false)]
    [InlineData("https://127.0.0.1/abc", false)]
    [InlineData("https://[::1]/abc", false)]
    public void Endpoint_UsesStrictProviderAllowlist(string endpoint, bool allowed) =>
        Assert.Equal(allowed, new PushEndpointPolicy(Config()).IsAllowed(endpoint));

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void Connection_RejectsNonPublicAddresses(string address, bool allowed) =>
        Assert.Equal(allowed, PushEndpointPolicy.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task StoredUntrustedSubscription_IsSkippedBeforeNetworkOrEncryption()
    {
        _context.PushSubscriptions.Add(new PushSubscriptionRecord
            { UserId = "review", Endpoint = "https://127.0.0.1/push", P256dhKey = "invalid", AuthKey = "invalid" });
        await _context.SaveChangesAsync();
        using var service = new PushNotificationService(_context, Config(), NullLogger<PushNotificationService>.Instance);
        await service.SendToUserAsync("review", "title", "body");
        Assert.Single(_context.PushSubscriptions);
    }
}
