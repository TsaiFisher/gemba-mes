using System.Net;
using System.Net.Sockets;

namespace SmartFactory.MES.Services;

/// <summary>Validate both stored subscriptions and the actual address selected for each connection.</summary>
public sealed class PushEndpointPolicy
{
    private static readonly string[] Defaults =
        ["fcm.googleapis.com", "updates.push.services.mozilla.com", "web.push.apple.com", "*.notify.windows.com"];
    private readonly string[] _hosts;

    public PushEndpointPolicy(IConfiguration config) =>
        _hosts = config.GetSection("WebPush:AllowedEndpointHosts").Get<string[]>() ?? Defaults;

    public bool IsAllowed(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        uri.HostNameType == UriHostNameType.Dns && endpoint!.Length <= 2048 &&
        _hosts.Any(pattern => MatchesHost(uri.IdnHost, pattern));

    private static bool MatchesHost(string host, string pattern)
    {
        // Wildcards match subdomains only, with an explicit DNS label boundary.
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
            return pattern.Length > 3 && host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (b[0] & 0xe0) == 0x20 &&
                !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || (b[2] == 0x0d && b[3] == 0xb8))) &&
                !(b[0] == 0x20 && b[1] == 0x02) && !(b[0] == 0x3f && b[1] == 0xff);
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        return !(b[0] is 0 or 10 or 127 || b[0] >= 224 ||
            (b[0] == 100 && b[1] is >= 64 and <= 127) ||
            (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31) ||
            (b[0] == 192 && (b[1] == 168 || (b[1] == 0 && b[2] is 0 or 2))) ||
            (b[0] == 198 && (b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100))) ||
            (b[0] == 203 && b[1] == 0 && b[2] == 113));
    }

    public HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (context, ct) =>
        {
            if (context.DnsEndPoint.Port != 443 ||
                !IsAllowed("https://" + context.DnsEndPoint.Host + "/"))
                throw new HttpRequestException("Push destination is not allowed.");
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            if (addresses.Length == 0 || addresses.Any(x => !IsPublicAddress(x)))
                throw new HttpRequestException("Push destination resolved to a non-public address.");
            // Connect to the checked IP directly: do not resolve the name a second time.
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, 443), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new HttpRequestException("Push destination could not be reached.");
        }
    }) { Timeout = TimeSpan.FromSeconds(15) };
}
