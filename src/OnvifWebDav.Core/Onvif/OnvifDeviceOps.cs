using System.Xml.Linq;

namespace OnvifWebDav.Onvif;

/// <summary>Device + Media 服务。只取我们需要的 6 个操作。</summary>
public static class OnvifDeviceOps
{
    public const string DeviceNs = "http://www.onvif.org/ver10/device/wsdl";
    public const string MediaNs = "http://www.onvif.org/ver10/media/wsdl";
    public const string SchemaNs = "http://www.onvif.org/ver10/schema";

    public static readonly string[] GetSystemDateAndTimeActions =
        { DeviceNs + "/GetSystemDateAndTime" };

    public static readonly string[] GetDeviceInformationActions =
        { DeviceNs + "/GetDeviceInformation" };

    public static readonly string[] GetCapabilitiesActions =
        { DeviceNs + "/GetCapabilities" };

    public static readonly string[] GetServicesActions =
    {
        DeviceNs + "/GetServices",
    };

    public static readonly string[] GetProfilesActions =
    {
        MediaNs + "/GetProfiles",
        "http://www.onvif.org/ver20/media/wsdl/GetProfiles",
    };

    public static readonly string[] GetStreamUriActions =
    {
        MediaNs + "/GetStreamUri",
        "http://www.onvif.org/ver20/media/wsdl/GetStreamUri",
    };

    public static readonly string[] GetSnapshotUriActions =
    {
        MediaNs + "/GetSnapshotUri",
        "http://www.onvif.org/ver20/media/wsdl/GetSnapshotUri",
    };

    /// <summary>GetSystemDateAndTime 必须匿名发，很多固件带 Security 头会直接 400。</summary>
    public static async Task<DateTime> GetDeviceUtcAsync(OnvifSoapClient client, Uri deviceUri, CancellationToken ct)
    {
        var body = $"""<tds:GetSystemDateAndTime xmlns:tds="{DeviceNs}"/>""";
        var doc = await client.CallAsync(deviceUri, body, GetSystemDateAndTimeActions,
            anonymous: true, ct: ct).ConfigureAwait(false);

        var utc = XmlUtils.First(doc, "UTCDateTime")
                  ?? throw new OnvifException("设备未返回 UTC 时间（GetSystemDateAndTime 响应格式不认识）");

        int Get(string name, int fallback = 0)
        {
            var raw = XmlUtils.TextOf(utc, name);
            return int.TryParse(raw, out var v) ? v : fallback;
        }

        try
        {
            return new DateTime(Get("Year"), Get("Month", 1), Get("Day", 1),
                Get("Hour"), Get("Minute"), Get("Second"), DateTimeKind.Utc);
        }
        catch
        {
            throw new OnvifException("设备返回的 UTC 时间不合法（请检查摄像头时间设置）");
        }
    }

    public static async Task<OnvifDeviceInfo> GetDeviceInformationAsync(OnvifSoapClient client, Uri deviceUri,
        CancellationToken ct)
    {
        var body = $"""<tds:GetDeviceInformation xmlns:tds="{DeviceNs}"/>""";
        var doc = await client.CallAsync(deviceUri, body, GetDeviceInformationActions, ct: ct)
            .ConfigureAwait(false);

        string T(string n) => XmlUtils.TextOf(doc, n) ?? "";

        return new OnvifDeviceInfo(T("Manufacturer"), T("Model"), T("FirmwareVersion"),
            T("SerialNumber"), T("HardwareId"));
    }

    public static async Task<(Uri? MediaXAddr, Uri? EventsXAddr)> GetCapabilitiesAsync(
        OnvifSoapClient client, Uri deviceUri, CancellationToken ct)
    {
        var body = $"""
                    <tds:GetCapabilities xmlns:tds="{DeviceNs}"><tds:Category>All</tds:Category></tds:GetCapabilities>
                    """;
        var doc = await client.CallAsync(deviceUri, body, GetCapabilitiesActions, ct: ct)
            .ConfigureAwait(false);

        Uri? Find(string serviceName)
        {
            var svc = XmlUtils.First(doc, serviceName);
            var xaddr = svc is null ? null : XmlUtils.TextOf(svc, "XAddr");
            return Uri.TryCreate(xaddr, UriKind.Absolute, out var u) ? u : null;
        }

        return (Find("Media"), Find("Events"));
    }

    /// <summary>
    /// GetServices 是判断"设备到底支持哪些 ONVIF Profile"最权威的一手资料：
    /// 它直接列出设备实现的服务命名空间，Profile G 必须有 ver10/replay/wsdl。
    /// 比 GetCapabilities 全 —— 有些设备 Capabilities 里不写 Events，但 Services 里列了。
    /// </summary>
    public static async Task<DeviceCapabilities> GetServicesAsync(OnvifSoapClient client, Uri deviceUri,
        CancellationToken ct)
    {
        var body = $"""
                    <tds:GetServices xmlns:tds="{DeviceNs}">
                      <tds:IncludeCapability>true</tds:IncludeCapability>
                    </tds:GetServices>
                    """;

        var doc = await client.CallAsync(deviceUri, body, GetServicesActions, ct: ct).ConfigureAwait(false);

        var caps = ParseServices(doc);

        if (caps.Services.Count == 0)
            throw new OnvifException("设备没有返回任何服务声明（GetServices 响应为空）");

        return caps;
    }

    /// <summary>
    /// 纯解析：把 GetServices 的响应 XML 变成 DeviceCapabilities。
    /// 抽出来是为了能离线验证（把抓到的报文存成文件直接跑），不用连设备。
    /// </summary>
    public static DeviceCapabilities ParseServices(XDocument doc)
    {
        var services = new List<OnvifServiceInfo>();

        foreach (var svc in XmlUtils.All(doc, "Service"))
        {
            var ns = XmlUtils.TextOf(svc, "Namespace") ?? "";
            if (string.IsNullOrEmpty(ns)) continue;

            var xaddrText = XmlUtils.TextOf(svc, "XAddr");
            Uri? xaddr = Uri.TryCreate(xaddrText, UriKind.Absolute, out var u) ? u : null;

            var major = XmlUtils.TextOf(svc, "Major") ?? "";
            var minor = XmlUtils.TextOf(svc, "Minor") ?? "";
            var version = major.Length == 0 ? "" : (minor.Length == 0 ? major : $"{major}.{minor}");

            var capsXml = XmlUtils.First(svc, "Capabilities")?.ToString() ?? "";

            services.Add(new OnvifServiceInfo(ns, xaddr, version, capsXml));
        }

        // SnapshotUri / RTSPStreaming 这些开关藏在各服务的 Capabilities 属性里，按属性名全局找一遍
        var snapshot = FindBoolAttr(services, "SnapshotUri");
        var rtsp = FindBoolAttr(services, "RTSPStreaming");
        var noRtsp = FindBoolAttr(services, "NoRTSPStreaming");

        // 事件能力在 GetServices 的同一个响应里就带了（tev:Capabilities 的属性），
        // 不用再单独发一次 GetServiceCapabilities —— 少一次往返，离线解析也能看到。
        var eventsService = services.FirstOrDefault(s =>
            s.Namespace.Contains("ver10/events/wsdl", StringComparison.OrdinalIgnoreCase));

        return new DeviceCapabilities
        {
            Services = services,
            Events = eventsService is null ? null : ParseEventCapabilities(eventsService),
            // 注意：ver10 的 NoRTSPStreaming 在海思系固件上经常填反（这台就是 true，但 RTSP 实际可用），
            // 所以 ver20 的 RTSPStreaming 优先，两者都没有才乐观认为支持。
            SnapshotSupported = snapshot ?? true,
            RtspStreaming = rtsp ?? (noRtsp is { } nr ? !nr : true),
            AudioSources = FindIntAttr(services, "AudioSources") ?? 0,
            AnalyticsRuleSupport = FindBoolAttr(services, "RuleSupport") ?? false,
        };
    }

    private static EventServiceCapabilities? ParseEventCapabilities(OnvifServiceInfo eventsService)
    {
        if (string.IsNullOrEmpty(eventsService.CapabilitiesXml)) return null;

        try
        {
            var el = XElement.Parse(eventsService.CapabilitiesXml);

            var capsEl = el.DescendantsAndSelf().FirstOrDefault(e =>
                e.Attribute("WSPullPointSupport") is not null ||
                e.Attribute("WSSubscriptionPolicySupport") is not null);

            if (capsEl is null) return null;

            return new EventServiceCapabilities(
                PullPointSupport: XmlUtils.ToBool(XmlUtils.Attr(capsEl, "WSPullPointSupport")),
                SubscriptionPolicySupport: XmlUtils.ToBool(XmlUtils.Attr(capsEl, "WSSubscriptionPolicySupport")),
                PausableSupport: XmlUtils.ToBool(
                    XmlUtils.Attr(capsEl, "WSPausableSubscriptionManagerInterfaceSupport")),
                RawXml: capsEl.ToString());
        }
        catch
        {
            return null;
        }
    }

    private static bool? FindBoolAttr(IReadOnlyList<OnvifServiceInfo> services, string attributeName)
    {
        foreach (var svc in services)
        {
            if (string.IsNullOrEmpty(svc.CapabilitiesXml)) continue;

            try
            {
                var el = XElement.Parse(svc.CapabilitiesXml);
                var value = el.DescendantsAndSelf()
                    .Select(e => e.Attribute(attributeName)?.Value)
                    .FirstOrDefault(v => v is not null);

                if (value is not null) return XmlUtils.ToBool(value);
            }
            catch { /* 某个服务的能力 XML 解析不了就跳过 */ }
        }

        return null;
    }

    private static int? FindIntAttr(IReadOnlyList<OnvifServiceInfo> services, string attributeName)
    {
        foreach (var svc in services)
        {
            if (string.IsNullOrEmpty(svc.CapabilitiesXml)) continue;

            try
            {
                var el = XElement.Parse(svc.CapabilitiesXml);
                var value = el.DescendantsAndSelf()
                    .Select(e => e.Attribute(attributeName)?.Value)
                    .FirstOrDefault(v => v is not null);

                if (value is not null && int.TryParse(value, out var n)) return n;
            }
            catch { }
        }

        return null;
    }

    public static async Task<List<OnvifProfile>> GetProfilesAsync(OnvifSoapClient client, Uri mediaUri,
        CancellationToken ct)
    {
        var body = $"""<trt:GetProfiles xmlns:trt="{MediaNs}"/>""";
        var doc = await client.CallAsync(mediaUri, body, GetProfilesActions, ct: ct).ConfigureAwait(false);

        var list = new List<OnvifProfile>();
        foreach (var p in XmlUtils.All(doc, "Profiles"))
        {
            var token = XmlUtils.Attr(p, "token") ?? "";
            var name = XmlUtils.TextOf(p, "Name") ?? token;
            var vec = XmlUtils.First(p, "VideoEncoderConfiguration");
            var encoding = vec is null ? "" : XmlUtils.TextOf(vec, "Encoding") ?? "";
            var res = vec is null ? null : XmlUtils.First(vec, "Resolution");
            int W = int.TryParse(res is null ? null : XmlUtils.TextOf(res, "Width"), out var w) ? w : 0;
            int H = int.TryParse(res is null ? null : XmlUtils.TextOf(res, "Height"), out var h) ? h : 0;
            var rate = vec is null ? null : XmlUtils.First(vec, "RateControl");
            int fps = 0;
            if (rate is not null &&
                double.TryParse(XmlUtils.TextOf(rate, "FrameRateLimit"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var fr))
                fps = (int)Math.Round(fr);

            list.Add(new OnvifProfile(token, name, encoding, W, H, fps));
        }

        return list;
    }

    public static async Task<string?> GetStreamUriAsync(OnvifSoapClient client, Uri mediaUri, string profileToken,
        CancellationToken ct)
    {
        var body = $"""
                    <trt:GetStreamUri xmlns:trt="{MediaNs}" xmlns:tt="{SchemaNs}">
                      <trt:StreamSetup>
                        <tt:Stream>RTP-Unicast</tt:Stream>
                        <tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport>
                      </trt:StreamSetup>
                      <trt:ProfileToken>{XmlUtils.Esc(profileToken)}</trt:ProfileToken>
                    </trt:GetStreamUri>
                    """;
        var doc = await client.CallAsync(mediaUri, body, GetStreamUriActions, ct: ct).ConfigureAwait(false);
        return XmlUtils.TextOf(XmlUtils.First(doc, "MediaUri"), "Uri");
    }

    public static async Task<string?> GetSnapshotUriAsync(OnvifSoapClient client, Uri mediaUri, string profileToken,
        CancellationToken ct)
    {
        var body = $"""
                    <trt:GetSnapshotUri xmlns:trt="{MediaNs}">
                      <trt:ProfileToken>{XmlUtils.Esc(profileToken)}</trt:ProfileToken>
                    </trt:GetSnapshotUri>
                    """;
        var doc = await client.CallAsync(mediaUri, body, GetSnapshotUriActions, ct: ct).ConfigureAwait(false);
        return XmlUtils.TextOf(doc, "Uri");
    }
}
