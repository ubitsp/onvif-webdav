using System.Xml.Linq;

namespace OnvifWebDav.Onvif;

public sealed class OnvifException : Exception
{
    public string? FaultCode { get; }
    public int HttpStatus { get; }

    public OnvifException(string message, string? faultCode = null, int httpStatus = 0, Exception? inner = null)
        : base(message, inner)
    {
        FaultCode = faultCode;
        HttpStatus = httpStatus;
    }

    public bool IsAuthFailure =>
        HttpStatus == 401 ||
        (FaultCode?.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (FaultCode?.Contains("FailedAuthentication", StringComparison.OrdinalIgnoreCase) ?? false);
}

public sealed record OnvifDeviceInfo(
    string Manufacturer, string Model, string FirmwareVersion, string SerialNumber, string HardwareId)
{
    public override string ToString() =>
        $"{Manufacturer} {Model} / 固件 {FirmwareVersion} / SN {SerialNumber}";
}

public sealed record OnvifProfile(
    string Token, string Name, string Encoding, int Width, int Height, int Fps)
{
    public override string ToString() =>
        $"{Name}（{Token}）{Encoding} {Width}x{Height}@{Fps}";
}

public sealed record OnvifMotionEvent(
    DateTime UtcTime, string Topic, bool IsMotion, string? SourceToken, bool Initialized, string RawXml);

/// <summary>GetServices 返回的一条服务声明。</summary>
public sealed record OnvifServiceInfo(string Namespace, Uri? XAddr, string Version, string CapabilitiesXml)
{
    /// <summary>给界面看的短名，例如 "media(ver10) 2.20"。</summary>
    public string ShortName
    {
        get
        {
            var name = Namespace switch
            {
                _ when Namespace.Contains("ver10/device/wsdl", StringComparison.OrdinalIgnoreCase) => "device",
                _ when Namespace.Contains("ver10/media/wsdl", StringComparison.OrdinalIgnoreCase) => "media(ver10)",
                _ when Namespace.Contains("ver20/media/wsdl", StringComparison.OrdinalIgnoreCase) => "media(ver20)",
                _ when Namespace.Contains("ver10/events/wsdl", StringComparison.OrdinalIgnoreCase) => "events",
                _ when Namespace.Contains("ver10/replay/wsdl", StringComparison.OrdinalIgnoreCase) => "replay",
                _ when Namespace.Contains("ver20/analytics/wsdl", StringComparison.OrdinalIgnoreCase) => "analytics",
                _ when Namespace.Contains("ver20/imaging/wsdl", StringComparison.OrdinalIgnoreCase) => "imaging",
                _ when Namespace.Contains("ver20/ptz/wsdl", StringComparison.OrdinalIgnoreCase) => "ptz",
                _ when Namespace.Contains("ver10/deviceIO/wsdl", StringComparison.OrdinalIgnoreCase) => "deviceIO",
                _ => Namespace
            };

            return string.IsNullOrEmpty(Version) ? name : $"{name} {Version}";
        }
    }
}

/// <summary>
/// 设备能力汇总（来自 GetServices + GetCapabilities + Events 的 GetServiceCapabilities）。
/// Profile 判定是"按服务声明推断"，不是 ONVIF 官方认证结论 —— 认证还要看编码、OSD、HTTPS 等细节要求。
/// </summary>
public sealed class DeviceCapabilities
{
    public IReadOnlyList<OnvifServiceInfo> Services { get; set; } = Array.Empty<OnvifServiceInfo>();
    public EventServiceCapabilities? Events { get; set; }

    /// <summary>ONVIF 层面是否声明支持 GetSnapshotUri（很多机型声明 false，实际另有 HTTP 快照接口）。</summary>
    public bool SnapshotSupported { get; set; } = true;

    /// <summary>是否声明支持 RTSP 流（ver20 的 RTSPStreaming 或 ver10 的 NoRTSPStreaming 取反）。</summary>
    public bool RtspStreaming { get; set; } = true;

    public int AudioSources { get; set; }
    public bool AnalyticsRuleSupport { get; set; }

    public bool Has(string namespaceFragment) =>
        Services.Any(s => s.Namespace.Contains(namespaceFragment, StringComparison.OrdinalIgnoreCase));

    public OnvifServiceInfo? Find(string namespaceFragment) =>
        Services.FirstOrDefault(s => s.Namespace.Contains(namespaceFragment, StringComparison.OrdinalIgnoreCase));

    // ---- Profile 推断（只按服务命名空间） ----
    // Profile S：设备 + 媒体服务（ver10）+ 流式能力
    // Profile T：媒体服务 ver20（H.265/OSD 等能力要求无法仅凭服务列表确认，这里只表示"具备前提"）
    // Profile G：必须有录像回放服务（ver10/replay/wsdl），核心是 FindRecordings / GetReplayUri
    // Profile M：分析服务 ver20 + 元数据
    public bool ProfileS => Has("ver10/media/wsdl") && Has("ver10/device/wsdl");
    public bool ProfileT => Has("ver20/media/wsdl") && Has("ver10/device/wsdl");
    public bool ProfileG => Has("ver10/replay/wsdl");
    public bool ProfileM => Has("ver20/analytics/wsdl");

    public IReadOnlyList<string> Profiles
    {
        get
        {
            var list = new List<string>(4);
            if (ProfileS) list.Add("S");
            if (ProfileT) list.Add("T");
            if (ProfileG) list.Add("G");
            if (ProfileM) list.Add("M");
            return list;
        }
    }

    public string ProfileSummary => Profiles.Count == 0 ? "未识别" : string.Join(" / ", Profiles);

    /// <summary>给界面/命令行直接展示的多行说明。</summary>
    public string Describe()
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("服务：" + (Services.Count == 0
            ? "（设备未返回 GetServices）"
            : string.Join("、", Services.Select(s => s.ShortName))));

        sb.AppendLine("Profile 推断：" + ProfileSummary +
                      (ProfileG
                          ? "（含 G，可用设备端录像检索）"
                          : "（不支持 Profile G —— 设备端没有录像回放服务，必须自建录像）"));

        sb.AppendLine("事件：" + (Events?.ToString() ?? "未取到能力集"));
        sb.AppendLine("快照 GetSnapshotUri：" + (SnapshotSupported ? "支持" : "不支持（UI 已禁用抓图）"));
        sb.AppendLine("RTSP 流：" + (RtspStreaming ? "支持" : "声明不支持（以 ffprobe 实测为准）"));

        if (AudioSources > 0) sb.AppendLine("音频输入：" + AudioSources + " 路");
        if (AnalyticsRuleSupport) sb.AppendLine("分析规则：支持（运动检测走 RuleEngine 上报）");

        return sb.ToString().TrimEnd();
    }
}

/// <summary>设备返回的 ONVIF XML 命名空间/版本差异极大，统一用 LocalName 匹配，不依赖命名空间。</summary>
public static class XmlUtils
{
    public static IEnumerable<XElement> All(XContainer root, string localName) =>
        root.Descendants().Where(e => e.Name.LocalName == localName);

    public static XElement? First(XContainer root, string localName) =>
        root.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);

    public static string? Attr(XElement? e, string name) => e?.Attribute(name)?.Value;

    public static string? TextOf(XContainer? e, string localName) =>
        e is null ? null : All(e, localName).FirstOrDefault()?.Value;

    /// <summary>ONVIF 布尔值花样很多：true/1/True/on/ON。</summary>
    public static bool ToBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        return v.Equals("true", StringComparison.OrdinalIgnoreCase)
               || v.Equals("1", StringComparison.Ordinal)
               || v.Equals("on", StringComparison.OrdinalIgnoreCase)
               || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public static string Esc(string? s) =>
        System.Security.SecurityElement.Escape(s ?? "") ?? "";
}
