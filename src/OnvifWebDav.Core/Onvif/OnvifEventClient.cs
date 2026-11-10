using System.Xml.Linq;
using OnvifWebDav.Diagnostics;

namespace OnvifWebDav.Onvif;

/// <summary>订阅句柄。PullPoint 和 Basic Notification 都用它（背后都是 WS-BaseNotification 的 SubscriptionManager）。</summary>
public sealed record EventSubscription(Uri Address, DateTime CreatedUtc, DateTime TerminationUtc, bool IsPullPoint)
{
    public bool IsExpiring => DateTime.UtcNow > TerminationUtc.AddSeconds(-5);
}

/// <summary>
/// Events 服务能力（GetServiceCapabilities）。
/// WSSubscriptionPolicySupport=true 说明设备支持 Basic Notification：我们注册一个回调地址，设备主动 POST 事件过来，
/// 比 PullPoint 轮询更实时，也不用一直挂着长轮询。
/// </summary>
public sealed record EventServiceCapabilities(
    bool PullPointSupport,
    bool SubscriptionPolicySupport,
    bool PausableSupport,
    string RawXml)
{
    public override string ToString() =>
        $"PullPoint={(PullPointSupport ? "支持" : "不支持")} / " +
        $"WSSubscriptionPolicy={(SubscriptionPolicySupport ? "支持" : "不支持")} / " +
        $"Pausable={(PausableSupport ? "支持" : "不支持")}";
}

public sealed class OnvifEventClient
{
    public const string EventsNs = "http://www.onvif.org/ver10/events/wsdl";
    public const string WsnNs = "http://docs.oasis-open.org/wsn/b-2";
    public const string WsaNs = "http://www.w3.org/2005/08/addressing";

    private static readonly string[] CapabilityActions =
        { EventsNs + "/GetServiceCapabilities" };

    private static readonly string[] CreateActions =
    {
        EventsNs + "/Event/CreatePullPointSubscription",
        EventsNs + "/EventPortType/CreatePullPointSubscriptionRequest",
        EventsNs + "/CreatePullPointSubscription",
    };

    private static readonly string[] PullActions =
    {
        EventsNs + "/PullPointSubscription/PullMessagesRequest",
        EventsNs + "/Event/PullMessages",
        EventsNs + "/PullMessages",
    };

    /// <summary>WS-BaseNotification 的 Subscribe。ONVIF 设备的 SOAPAction 至少有这两种写法。</summary>
    private static readonly string[] SubscribeActions =
    {
        "http://docs.oasis-open.org/wsn/bw-2/NotificationProducer/SubscribeRequest",
        EventsNs + "/Event/Subscribe",
        EventsNs + "/Subscribe",
    };

    private static readonly string[] RenewActions =
        { "http://docs.oasis-open.org/wsn/bw-2/SubscriptionManager/RenewRequest" };

    private static readonly string[] UnsubscribeActions =
        { "http://docs.oasis-open.org/wsn/bw-2/SubscriptionManager/UnsubscribeRequest" };

    /// <summary>运动事件 topic 候选（按 TP-Link 上的命中概率排序）。</summary>
    public static readonly string[] MotionTopics =
    {
        "CellMotionDetector/Motion",
        "MotionRegionDetector/Motion",
        "VideoSource/MotionAlarm",
        "FieldDetector/ObjectsInside",
        "Device/Trigger/DigitalInput",
    };

    private readonly OnvifSoapClient _soap;
    private readonly AppLog? _log;

    public OnvifEventClient(OnvifSoapClient soap, AppLog? log = null)
    {
        _soap = soap;
        _log = log;
    }

    // ---------------- 能力探测 ----------------

    public async Task<EventServiceCapabilities> GetCapabilitiesAsync(Uri eventsUri, CancellationToken ct)
    {
        var body = $"""<tev:GetServiceCapabilities xmlns:tev="{EventsNs}"/>""";
        var doc = await _soap.CallAsync(eventsUri, body, CapabilityActions, ct: ct).ConfigureAwait(false);

        var caps = XmlUtils.First(doc, "Capabilities")
                   ?? throw new OnvifException("设备没有返回 Events 服务能力集（可能不支持 Events 服务）");

        bool Flag(string name) => XmlUtils.ToBool(XmlUtils.Attr(caps, name));

        var result = new EventServiceCapabilities(
            PullPointSupport: Flag("WSPullPointSupport"),
            SubscriptionPolicySupport: Flag("WSSubscriptionPolicySupport"),
            PausableSupport: Flag("WSPausableSubscriptionManagerInterfaceSupport"),
            RawXml: caps.ToString());

        _log?.Info("events", "Events 能力：" + result);
        return result;
    }

    // ---------------- PullPoint ----------------

    public async Task<EventSubscription> CreatePullPointAsync(Uri eventsUri, Uri deviceUri, TimeSpan termination,
        CancellationToken ct)
    {
        var seconds = Math.Max(30, (int)termination.TotalSeconds);

        string WithTermination =
            $"""
             <tev:CreatePullPointSubscription xmlns:tev="{EventsNs}">
               <tev:InitialTerminationTime>PT{seconds}S</tev:InitialTerminationTime>
             </tev:CreatePullPointSubscription>
             """;

        string WithoutTermination =
            $"""<tev:CreatePullPointSubscription xmlns:tev="{EventsNs}"/>""";

        XDocument doc;
        try
        {
            doc = await _soap.CallAsync(eventsUri, WithTermination, CreateActions, ct: ct).ConfigureAwait(false);
        }
        catch (OnvifException)
        {
            // 部分固件不认 InitialTerminationTime
            doc = await _soap.CallAsync(eventsUri, WithoutTermination, CreateActions, ct: ct).ConfigureAwait(false);
        }

        var address = XmlUtils.TextOf(XmlUtils.First(doc, "SubscriptionReference"), "Address")
                      ?? XmlUtils.TextOf(doc, "Address");

        if (!Uri.TryCreate(address, UriKind.Absolute, out var addr))
            throw new OnvifException("设备没有返回 PullPoint 订阅地址");

        var rewritten = RewriteAddress(addr, deviceUri);
        var terminationUtc = ParseTermination(doc, seconds);

        _log?.Info("events", $"PullPoint 订阅成功：{rewritten}（设备原地址 {addr}，到期 {terminationUtc:HH:mm:ss}）");

        return new EventSubscription(rewritten, DateTime.UtcNow, terminationUtc, IsPullPoint: true);
    }

    public async Task<List<OnvifMotionEvent>> PullAsync(EventSubscription sub, TimeSpan wait,
        int limit, CancellationToken ct)
    {
        var body = $"""
                    <tev:PullMessages xmlns:tev="{EventsNs}">
                      <tev:Timeout>PT{Math.Max(1, (int)wait.TotalSeconds)}S</tev:Timeout>
                      <tev:MessageLimit>{Math.Clamp(limit, 1, 100)}</tev:MessageLimit>
                    </tev:PullMessages>
                    """;

        var doc = await _soap.CallAsync(sub.Address, body, PullActions,
            timeout: wait + TimeSpan.FromSeconds(15), ct: ct).ConfigureAwait(false);

        var result = new List<OnvifMotionEvent>();

        foreach (var nm in XmlUtils.All(doc, "NotificationMessage"))
        {
            var evt = ParseNotification(nm);
            if (evt is not null) result.Add(evt);
        }

        return result;
    }

    // ---------------- Basic Notification ----------------

    /// <summary>
    /// WS-BaseNotification 订阅：给设备一个回调地址，它主动 POST Notify 过来。
    /// 需要设备 WSSubscriptionPolicySupport=true（这台 TP-Link 就是）。
    /// </summary>
    public async Task<EventSubscription> SubscribeBasicAsync(Uri eventsUri, Uri deviceUri, string consumerAddress,
        TimeSpan termination, CancellationToken ct)
    {
        var seconds = Math.Max(60, (int)termination.TotalSeconds);
        var consumer = XmlUtils.Esc(consumerAddress);

        // 标注了 WSSubscriptionPolicySupport 的设备通常要求带 SubscriptionPolicy
        var withPolicy =
            $"""
             <wsnt:Subscribe xmlns:wsnt="{WsnNs}" xmlns:wsa="{WsaNs}" xmlns:tev="{EventsNs}">
               <wsnt:ConsumerReference><wsa:Address>{consumer}</wsa:Address></wsnt:ConsumerReference>
               <wsnt:InitialTerminationTime>PT{seconds}S</wsnt:InitialTerminationTime>
               <wsnt:SubscriptionPolicy><tev:SubscriptionPolicy/></wsnt:SubscriptionPolicy>
             </wsnt:Subscribe>
             """;

        var withoutPolicy =
            $"""
             <wsnt:Subscribe xmlns:wsnt="{WsnNs}" xmlns:wsa="{WsaNs}">
               <wsnt:ConsumerReference><wsa:Address>{consumer}</wsa:Address></wsnt:ConsumerReference>
               <wsnt:InitialTerminationTime>PT{seconds}S</wsnt:InitialTerminationTime>
             </wsnt:Subscribe>
             """;

        XDocument doc;
        try
        {
            doc = await _soap.CallAsync(eventsUri, withPolicy, SubscribeActions, ct: ct).ConfigureAwait(false);
            _log?.Info("events", "Subscribe 带 SubscriptionPolicy 版本被接受");
        }
        catch (OnvifException ex)
        {
            _log?.Warn("events", $"带 SubscriptionPolicy 的 Subscribe 失败，去掉重试：{ex.Message}");
            doc = await _soap.CallAsync(eventsUri, withoutPolicy, SubscribeActions, ct: ct).ConfigureAwait(false);
        }

        var reference = XmlUtils.First(doc, "SubscriptionReference");
        var address = XmlUtils.TextOf(reference, "Address") ?? XmlUtils.TextOf(doc, "Address");

        if (!Uri.TryCreate(address, UriKind.Absolute, out var addr))
            throw new OnvifException("设备没有返回订阅管理器地址（Subscribe 响应里没有 SubscriptionReference/Address）");

        var rewritten = RewriteAddress(addr, deviceUri);
        var terminationUtc = ParseTermination(doc, seconds);

        _log?.Info("events",
            $"Basic Notification 订阅成功：回调 {consumerAddress}，管理器 {rewritten}（到期 {terminationUtc:HH:mm:ss}）");

        return new EventSubscription(rewritten, DateTime.UtcNow, terminationUtc, IsPullPoint: false);
    }

    /// <summary>解析设备 POST 过来的 Notify（完整 SOAP 信封或 Body 片段都行）。</summary>
    public static List<OnvifMotionEvent> ParseNotify(string xml)
    {
        var result = new List<OnvifMotionEvent>();
        if (string.IsNullOrWhiteSpace(xml)) return result;

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return result; }

        foreach (var nm in XmlUtils.All(doc, "NotificationMessage"))
        {
            var evt = ParseNotification(nm);
            if (evt is not null) result.Add(evt);
        }

        return result;
    }

    // ---------------- 公共 ----------------

    private static DateTime ParseTermination(XDocument doc, int fallbackSeconds)
    {
        var termText = XmlUtils.TextOf(doc, "TerminationTime");
        return DateTime.TryParse(termText, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal |
            System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : DateTime.UtcNow.AddSeconds(fallbackSeconds);
    }

    /// <summary>
    /// 关键坑：设备常返回 http://localhost/onvif/events_service 或主机名，
    /// 保留 path，只把 Scheme/Host/Port 换回我们实际能连的地址。
    /// </summary>
    private static Uri RewriteAddress(Uri deviceReported, Uri deviceUri)
    {
        var builder = new UriBuilder(deviceReported) { Scheme = deviceUri.Scheme };

        // 只有"设备返回的 IP 正好就是我们连的那个 IP"时，才保留它声明的 host+port
        // （有些机型把各服务放在不同端口上，比如 device_service 在 80、订阅管理器在 2020）。
        // 其余情况——localhost、主机名、别的网卡 IP——一律换成配置里那个一定连得通的地址。
        var sameAddress = System.Net.IPAddress.TryParse(deviceReported.Host, out var reportedIp)
                          && !System.Net.IPAddress.IsLoopback(reportedIp)
                          && string.Equals(deviceReported.Host, deviceUri.Host, StringComparison.OrdinalIgnoreCase);

        if (!sameAddress)
        {
            builder.Host = deviceUri.Host;
            builder.Port = deviceUri.Port;
        }

        if (string.IsNullOrEmpty(builder.Path) || builder.Path == "/")
            builder.Path = "/onvif/event_service";

        return builder.Uri;
    }

    /// <summary>把一条 NotificationMessage 解析成运动事件；不是运动类事件返回 null。</summary>
    public static OnvifMotionEvent? ParseNotification(XElement notificationMessage)
    {
        var topic = XmlUtils.TextOf(notificationMessage, "Topic") ?? "";

        // 坑：Notify 里有两个 Message —— 外层 wsnt:Message 包装，内层 tt:Message 才带 UtcTime。
        // 直接取第一个 "Message" 会拿到包装元素，UtcTime 丢失后事件时间就被错误地当成"现在"。
        var message = XmlUtils.All(notificationMessage, "Message")
                          .FirstOrDefault(m => m.Attribute("UtcTime") is not null)
                      ?? XmlUtils.First(notificationMessage, "Message");

        var utcAttr = XmlUtils.Attr(message, "UtcTime") ?? XmlUtils.Attr(notificationMessage, "UtcTime");

        var utcTime = DateTime.TryParse(utcAttr, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal |
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTime.UtcNow;

        var initialized = false;
        var items = new List<(string Name, string Value)>();

        foreach (var si in XmlUtils.All(notificationMessage, "SimpleItem"))
            items.Add((XmlUtils.Attr(si, "Name") ?? "", XmlUtils.Attr(si, "Value") ?? ""));

        foreach (var dataEl in XmlUtils.All(notificationMessage, "Data"))
        {
            if (string.Equals(XmlUtils.Attr(dataEl, "PropertyOperation"), "Initialized",
                    StringComparison.OrdinalIgnoreCase))
                initialized = true;
        }

        if (items.Count == 0 && !topic.Contains("Motion", StringComparison.OrdinalIgnoreCase))
            return null;

        var isMotionTopic = MotionTopics.Any(t => topic.Contains(t, StringComparison.OrdinalIgnoreCase));

        // TP-Link 用 IsMotion，其它固件可能是 State / Active / MotionDetected
        var stateItem = items.FirstOrDefault(i =>
            i.Name.Equals("IsMotion", StringComparison.OrdinalIgnoreCase) ||
            i.Name.Equals("Motion", StringComparison.OrdinalIgnoreCase) ||
            i.Name.Equals("MotionDetected", StringComparison.OrdinalIgnoreCase) ||
            i.Name.Equals("Active", StringComparison.OrdinalIgnoreCase) ||
            i.Name.Equals("State", StringComparison.OrdinalIgnoreCase) ||
            i.Name.Equals("LogicalState", StringComparison.OrdinalIgnoreCase));

        if (stateItem.Name is null)
        {
            if (!isMotionTopic) return null;
            return new OnvifMotionEvent(utcTime, topic, true, null, initialized, notificationMessage.ToString());
        }

        bool isMotion = XmlUtils.ToBool(stateItem.Value);
        if (!isMotion && !isMotionTopic) return null;

        return new OnvifMotionEvent(utcTime, topic, isMotion, null, initialized, notificationMessage.ToString());
    }

    public async Task RenewAsync(EventSubscription sub, TimeSpan termination, CancellationToken ct)
    {
        var seconds = Math.Max(30, (int)termination.TotalSeconds);
        var body = $"""
                    <wsnt:Renew xmlns:wsnt="{WsnNs}">
                      <wsnt:TerminationTime>PT{seconds}S</wsnt:TerminationTime>
                    </wsnt:Renew>
                    """;
        await _soap.CallAsync(sub.Address, body, RenewActions, ct: ct).ConfigureAwait(false);
    }

    public async Task UnsubscribeAsync(EventSubscription sub, CancellationToken ct)
    {
        var body = $"""<wsnt:Unsubscribe xmlns:wsnt="{WsnNs}"/>""";
        await _soap.CallAsync(sub.Address, body, UnsubscribeActions,
            timeout: TimeSpan.FromSeconds(5), ct: ct).ConfigureAwait(false);
    }
}
