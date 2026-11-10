using System.Net.Http.Headers;
using OnvifWebDav.Config;
using OnvifWebDav.Diagnostics;

namespace OnvifWebDav.Onvif;

/// <summary>
/// 一路摄像头的 ONVIF 会话门面：解析端口、校时、拿能力集、拿 profile、拿 RTSP/快照地址。
/// UI 的测试按钮、Probe 和 CameraWorker 都用它，避免三处重复踩同样的坑。
/// </summary>
public sealed class OnvifCamera : IDisposable
{
    public OnvifSoapClient Soap { get; }
    public Uri DeviceUri { get; }
    public Uri MediaUri { get; private set; }
    public Uri EventsUri { get; private set; }
    public OnvifDeviceInfo? DeviceInfo { get; private set; }
    public List<OnvifProfile> Profiles { get; private set; } = new();

    /// <summary>设备能力（服务列表 / Profile 推断 / 事件能力 / 快照支持）。取不到时为 null。</summary>
    public DeviceCapabilities? Capabilities { get; private set; }

    /// <summary>ONVIF 是否声明支持 GetSnapshotUri。声明 false 时不要去试，直接给中文提示。</summary>
    public bool SnapshotSupported => Capabilities?.SnapshotSupported ?? true;
    public TimeSpan ClockOffset => Soap.ClockOffset;

    private readonly CameraConfig _cfg;
    private readonly AppLog? _log;

    private OnvifCamera(CameraConfig cfg, OnvifSoapClient soap, Uri deviceUri, AppLog? log)
    {
        _cfg = cfg;
        Soap = soap;
        DeviceUri = deviceUri;
        MediaUri = deviceUri;
        EventsUri = deviceUri;
        _log = log;
    }

    /// <summary>
    /// 连接并完成握手：尝试端口（配置端口 → 80 → 2020）→ 匿名取时间 → 校正时钟 → 取能力/型号/profile。
    /// 失败抛 OnvifException（中文原因）。
    /// </summary>
    public static async Task<OnvifCamera> ConnectAsync(CameraConfig cfg, AppLog? log,
        CancellationToken ct = default)
    {
        var ports = new List<int>();
        foreach (var p in new[] { cfg.OnvifPort, 80, 2020 })
            if (p > 0 && !ports.Contains(p)) ports.Add(p);

        OnvifException? last = null;

        foreach (var port in ports)
        {
            var deviceUri = new Uri($"http://{cfg.Host}:{port}/onvif/device_service");
            var soap = new OnvifSoapClient(cfg.Username, cfg.GetPassword(), log);

            try
            {
                var deviceUtc = await OnvifDeviceOps.GetDeviceUtcAsync(soap, deviceUri, ct).ConfigureAwait(false);
                soap.ClockOffset = deviceUtc - DateTime.UtcNow;

                var skew = soap.ClockOffset;
                if (Math.Abs(skew.TotalSeconds) > 30)
                    log?.Warn("onvif",
                        $"设备与本机时间相差 {skew.TotalSeconds:F0} 秒。ONVIF 鉴权可能失败，" +
                        "请在摄像头网页开启 NTP 或关闭\"时间校验/Time Verification\"。");

                var cam = new OnvifCamera(cfg, soap, deviceUri, log) { };

                // ① GetServices：最权威的服务清单（Profile G 就看这里有没有 replay）
                DeviceCapabilities? caps = null;
                try
                {
                    caps = await OnvifDeviceOps.GetServicesAsync(soap, deviceUri, ct).ConfigureAwait(false);
                    log?.Info("onvif", "服务：" + string.Join("、", caps.Services.Select(x => x.ShortName)) +
                                       "｜Profile 推断：" + caps.ProfileSummary);
                }
                catch (OnvifException ex)
                {
                    log?.Warn("onvif", $"GetServices 失败（老固件可能不支持，改用 GetCapabilities）：{ex.Message}");
                }

                // ② 服务地址优先取服务清单里的 XAddr，取不到再退回 GetCapabilities
                Uri? media = caps?.Find("ver10/media/wsdl")?.XAddr ?? caps?.Find("ver20/media/wsdl")?.XAddr;
                Uri? events = caps?.Find("ver10/events/wsdl")?.XAddr;

                if (media is null || events is null)
                {
                    try
                    {
                        var (m, e) = await OnvifDeviceOps.GetCapabilitiesAsync(soap, deviceUri, ct)
                            .ConfigureAwait(false);
                        media ??= m;
                        events ??= e;
                    }
                    catch (OnvifException ex)
                    {
                        log?.Warn("onvif", $"GetCapabilities 失败，改用 device_service：{ex.Message}");
                    }
                }

                cam.MediaUri = Rewrite(media, deviceUri) ?? deviceUri;
                cam.EventsUri = Rewrite(events, deviceUri) ?? cam.MediaUri;

                // ③ 事件能力：GetServices 里没带的话（老固件 IncludeCapability 被忽略）再单独问一次
                if (caps is not null)
                {
                    if (caps.Events is null)
                    {
                        try
                        {
                            var evClient = new OnvifEventClient(soap, log);
                            caps.Events = await evClient.GetCapabilitiesAsync(cam.EventsUri, ct)
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            log?.Warn("onvif", "取 Events 服务能力失败：" + ex.Message);
                        }
                    }

                    cam.Capabilities = caps;
                }

                cam.DeviceInfo = await OnvifDeviceOps.GetDeviceInformationAsync(soap, deviceUri, ct)
                    .ConfigureAwait(false);
                cam.Profiles = await OnvifDeviceOps.GetProfilesAsync(soap, cam.MediaUri, ct)
                    .ConfigureAwait(false);

                log?.Info("onvif",
                    $"已连接 {cam.DeviceInfo}（端口 {port}，时钟偏差 {skew.TotalSeconds:F0}s，profile {cam.Profiles.Count} 个）");

                return cam;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户主动停止/退出时的取消，不是故障：原样抛出，别包装成 OnvifException 记成 ERROR
                soap.Dispose();
                throw;
            }
            catch (OnvifException ex)
            {
                last = ex;
                soap.Dispose();
                log?.Warn("onvif", $"端口 {port} 连接失败：{ex.Message}");
            }
            catch (OperationCanceledException)
            {
                // 内部超时导致的取消（非外部 ct），当作一次普通的连接失败
                soap.Dispose();
                last = new OnvifException($"端口 {port} 连接超时", httpStatus: 0);
                log?.Warn("onvif", last.Message);
            }
            catch (Exception ex)
            {
                soap.Dispose();
                last = new OnvifException($"端口 {port} 连接异常：{ex.Message}", inner: ex);
                log?.Warn("onvif", last.Message);
            }
        }

        throw last ?? new OnvifException("无法连接摄像头 ONVIF 服务");
    }

    /// <summary>设备返回的 xaddr 常带 localhost / 主机名，统一换成用户配置的地址。</summary>
    private static Uri? Rewrite(Uri? xaddr, Uri deviceUri)
    {
        if (xaddr is null) return null;
        var b = new UriBuilder(xaddr)
        {
            Scheme = deviceUri.Scheme,
            Host = deviceUri.Host,
            Port = xaddr.Port > 0 ? xaddr.Port : deviceUri.Port,
        };
        if (string.IsNullOrEmpty(b.Path) || b.Path == "/") b.Path = "/onvif/device_service";
        return b.Uri;
    }

    /// <summary>主码流 = 名字带 main 的 profile；否则第一个。子码流 = 名字带 sub 的；否则最后一个。</summary>
    public OnvifProfile? PickProfile()
    {
        if (Profiles.Count == 0) return null;
        bool wantSub = string.Equals(_cfg.Stream, "sub", StringComparison.OrdinalIgnoreCase);

        var match = Profiles.FirstOrDefault(p =>
            p.Name.Contains(wantSub ? "sub" : "main", StringComparison.OrdinalIgnoreCase) ||
            p.Token.Contains(wantSub ? "sub" : "main", StringComparison.OrdinalIgnoreCase));

        if (match is not null) return match;
        return wantSub ? Profiles[^1] : Profiles[0];
    }

    /// <summary>取 RTSP 地址：配置里手填优先；否则 ONVIF；ONVIF 失败退回 TP-Link 默认路径。带凭据。</summary>
    public async Task<string> GetRtspUrlAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_cfg.RtspUrl))
            return WithCredentials(_cfg.RtspUrl.Trim(), _cfg.Username, _cfg.GetPassword());

        var profile = PickProfile();
        if (profile is not null)
        {
            try
            {
                var uri = await OnvifDeviceOps.GetStreamUriAsync(Soap, MediaUri, profile.Token, ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(uri))
                    return WithCredentials(uri!, _cfg.Username, _cfg.GetPassword());
            }
            catch (OnvifException ex)
            {
                _log?.Warn("onvif", $"GetStreamUri 失败，退回默认 RTSP 路径：{ex.Message}");
            }
        }

        // TP-Link 默认：rtsp://user:pass@ip:554/stream1（子码流 stream2）
        return $"rtsp://{Uri.EscapeDataString(_cfg.Username)}:{Uri.EscapeDataString(_cfg.GetPassword())}" +
               $"@{_cfg.Host}:{_cfg.RtspPort}/{_cfg.StreamPath}";
    }

    public async Task<string?> GetSnapshotUriAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_cfg.SnapshotUrl))
            return _cfg.SnapshotUrl.Trim();

        var profile = PickProfile();
        if (profile is null) return null;

        try
        {
            return await OnvifDeviceOps.GetSnapshotUriAsync(Soap, MediaUri, profile.Token, ct)
                .ConfigureAwait(false);
        }
        catch (OnvifException ex)
        {
            _log?.Warn("onvif", $"GetSnapshotUri 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>抓一帧 JPEG（走 ONVIF SnapshotUri + HTTP Digest/Basic）。</summary>
    public async Task<byte[]> GetSnapshotJpegAsync(CancellationToken ct = default)
    {
        if (!SnapshotSupported)
            throw new OnvifException(
                "这台摄像头在 ONVIF 层面声明不支持快照（SnapshotUri=false），抓图功能不可用。" +
                "录像和网页播放不受影响。");

        var uri = await GetSnapshotUriAsync(ct).ConfigureAwait(false)
                  ?? throw new OnvifException("摄像头没有提供快照地址（GetSnapshotUri 为空）");

        using var handler = new HttpClientHandler { UseProxy = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        var basic = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{_cfg.Username}:{_cfg.GetPassword()}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            resp.Dispose();
            throw new OnvifException("快照地址要求认证且不接受 Basic（可能是 Digest），请改用子码流截图", httpStatus: 401);
        }

        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public static string WithCredentials(string rtsp, string user, string pass)
    {
        if (!Uri.TryCreate(rtsp, UriKind.Absolute, out var u)) return rtsp;
        if (!string.IsNullOrEmpty(u.UserInfo)) return rtsp;

        var b = new UriBuilder(u)
        {
            UserName = user,
            Password = pass,
        };
        return b.Uri.ToString();
    }

    public void Dispose() => Soap.Dispose();
}
