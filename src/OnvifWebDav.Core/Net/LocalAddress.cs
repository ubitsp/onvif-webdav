using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OnvifWebDav.Net;

/// <summary>本机局域网地址：给摄像头当回调地址用（它必须能连到我们）。</summary>
public static class LocalAddress
{
    public static List<string> All()
    {
        var list = new List<string>();

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = addr.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;   // APIPA，摄像头连不回来
                    list.Add(ip);
                }
            }
        }
        catch
        {
            // 拿不到就当没有
        }

        return list.Distinct().ToList();
    }

    /// <summary>优先选典型的私有网段（192.168/10./172.16-31），其次第一个。</summary>
    public static string? Preferred()
    {
        var all = All();
        if (all.Count == 0) return null;

        var preferred = all.FirstOrDefault(ip =>
            ip.StartsWith("192.168.", StringComparison.Ordinal) ||
            ip.StartsWith("10.", StringComparison.Ordinal) ||
            Is172Private(ip));

        return preferred ?? all[0];
    }

    private static bool Is172Private(string ip)
    {
        if (!ip.StartsWith("172.", StringComparison.Ordinal)) return false;
        var parts = ip.Split('.');
        return parts.Length > 1 && int.TryParse(parts[1], out var second) && second is >= 16 and <= 31;
    }

    /// <summary>
    /// 拼出给设备用的回调地址；没有局域网 IP 时返回 null。
    /// <paramref name="peerHost"/> 是摄像头地址：**必须优先选和它同网段的网卡**，
    /// 否则设备收不到我们 POST 出去的回调（多网卡机器上尤其容易挑错，比如挑了 WiFi 的 192.168.0.x）。
    /// </summary>
    public static string? ConsumerUrl(int port, string? peerHost = null, string path = "/onvif/notify")
    {
        var ip = BestFor(peerHost);
        return ip is null ? null : $"http://{ip}:{port}{path}";
    }

    /// <summary>挑一个最可能被 peerHost 访问到的本机地址。</summary>
    public static string? BestFor(string? peerHost)
    {
        var all = All();
        if (all.Count == 0) return null;

        if (peerHost is not null)
        {
            var sameSubnet = all.FirstOrDefault(ip => SameSubnet24(ip, peerHost));
            if (sameSubnet is not null) return sameSubnet;
        }

        return Preferred();
    }

    public static bool IsIp(string? text) => IPAddress.TryParse(text, out _);

    /// <summary>按 /24 比较网段（家用/办公局域网基本都是 /24，够用）。</summary>
    public static bool SameSubnet24(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (!IPAddress.TryParse(a, out var ipa) || !IPAddress.TryParse(b, out var ipb)) return false;

        var x = ipa.GetAddressBytes();
        var y = ipb.GetAddressBytes();
        if (x.Length != 4 || y.Length != 4) return false;

        return x[0] == y[0] && x[1] == y[1] && x[2] == y[2];
    }

    public static bool IsPrivate(string ip) =>
        ip.StartsWith("192.168.", StringComparison.Ordinal) ||
        ip.StartsWith("10.", StringComparison.Ordinal) ||
        Is172Private(ip) ||
        ip.StartsWith("127.", StringComparison.Ordinal);
}
