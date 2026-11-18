using System.Net;
using System.Net.Sockets;

namespace Mutter.Core.Services;

/// <summary>
/// 出站连接的目标地址策略：链路本地（含云元数据）一律拒绝；回环与私网放行，
/// 因为本地推理服务与自建 SearXNG 是明确支持的能力。
/// 既用于保存时的字面 URL 校验，也用于真正建连时的解析结果校验——只校验 URL 文本挡不住
/// DNS rebinding（校验时解析到公网，建连时改指 169.254.169.254）。
/// </summary>
public static class OutboundAddressPolicy
{
    // 云厂商的 IPv6 元数据端点使用 ULA 地址，不受 IsIPv6LinkLocal 覆盖；
    // 只封这两个固定地址，私网 ULA（fd00::/8 的其余部分）仍放行，供本地推理服务使用。
    private static readonly IPAddress[] Ipv6MetadataAddresses =
        [IPAddress.Parse("fd00:ec2::254"), IPAddress.Parse("fd20:ce::254")];

    private static readonly IPNetwork[] NonPublicV4 = [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.0.2.0/24"), IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("224.0.0.0/3")
    ];
    private static readonly IPNetwork GlobalV6 = IPNetwork.Parse("2000::/3");
    private static readonly IPNetwork[] NonPublicV6 = [
        IPNetwork.Parse("2001::/23"), IPNetwork.Parse("2001:db8::/32"), IPNetwork.Parse("2002::/16"),
        IPNetwork.Parse("3fff::/20")
    ];

    /// <summary>
    /// 默认直连并固定已校验的 DNS 结果；开启系统代理后由代理解析目标，地址策略只能覆盖代理本身，
    /// 因此这个开关默认关闭，且只应在信任代理时打开。
    /// </summary>
    public static SocketsHttpHandler CreateHandler(
        bool allowRedirect = true, Func<bool>? allowSystemProxy = null, IWebProxy? systemProxy = null) => new()
    {
        AllowAutoRedirect = allowRedirect,
        ConnectCallback = ConnectAsync,
        Proxy = new SwitchableProxy(allowSystemProxy ?? (() => false), systemProxy ?? HttpClient.DefaultProxy)
    };

    // 来源页来自搜索结果，需要比管理员配置的模型或搜索端点更严格的出站限制。
    public static SocketsHttpHandler CreatePublicWebHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        MaxResponseDrainSize = 0,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = (context, ct) => ConnectFilteredAsync(context, IsPublicWebAddress, ct)
    };

    public static bool IsPublicWebAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? !NonPublicV4.Any(network => network.Contains(address))
            : GlobalV6.Contains(address) && !NonPublicV6.Any(network => network.Contains(address));
    }

    /// <summary>按当前设置决定是否走系统代理；设置改变后下一次请求立即生效，无需重启。</summary>
    private sealed class SwitchableProxy(Func<bool> allowSystemProxy, IWebProxy systemProxy) : IWebProxy
    {
        public ICredentials? Credentials
        {
            get => systemProxy.Credentials;
            set { }
        }

        public Uri? GetProxy(Uri destination) =>
            allowSystemProxy() ? systemProxy.GetProxy(destination) : null;

        public bool IsBypassed(Uri host) =>
            !allowSystemProxy() || systemProxy.IsBypassed(host);
    }

    public static bool IsBlockedHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.TrimEnd('.');
        if (host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase)
            || host.Equals("metadata", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IsBlockedAddress(address);
    }

    public static bool IsBlockedAddress(IPAddress address)
    {
        // IPv4-mapped IPv6（::ffff:169.254.169.254）先还原成 IPv4 再判断，否则会绕过链路本地检查。
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 169 && bytes[1] == 254) return true; // 169.254.0.0/16（含 169.254.169.254）
            if (bytes[0] == 100 && bytes[1] == 100 && bytes[2] == 100 && bytes[3] == 200) return true; // 阿里云元数据
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6
            && Ipv6MetadataAddresses.Contains(address))
            return true;
        return address.IsIPv6LinkLocal;
    }

    /// <summary>过滤解析结果：保留所有非禁止地址；全部被拒时返回空数组。</summary>
    public static IPAddress[] FilterAllowed(IPAddress[] addresses) =>
        addresses.Where(address => !IsBlockedAddress(address)).ToArray();

    /// <summary>
    /// 供 <see cref="SocketsHttpHandler.ConnectCallback"/> 使用：在真正建立 TCP 连接前解析域名，
    /// 过滤掉链路本地与云元数据地址，全部命中时放弃连接。校验与建连用的是同一份 DNS 结果，
    /// 不存在“校验时解析到公网、建连时改指内网”的空隙。
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        if (IsBlockedHost(context.DnsEndPoint.Host))
            throw new HttpRequestException("目标为链路本地或云元数据地址，已被安全策略拒绝");
        return await ConnectFilteredAsync(context, address => !IsBlockedAddress(address), ct);
    }

    private static async ValueTask<Stream> ConnectFilteredAsync(SocketsHttpConnectionContext context,
        Func<IPAddress, bool> isAllowed, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var allowed = addresses.Where(isAllowed).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException("目标地址解析到不允许访问的地址");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
