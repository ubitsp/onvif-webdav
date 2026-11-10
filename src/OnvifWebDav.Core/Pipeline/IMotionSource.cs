using OnvifWebDav.Config;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.Onvif;

namespace OnvifWebDav.Pipeline;

/// <summary>
/// 运动事件源。两种实现：
///  · PullPoint（轮询）：兼容性最好，但要一直挂着长轮询
///  · Basic Notification（推送）：设备支持 WSSubscriptionPolicySupport 时更实时，我们只负责收
/// 如果两种都拿不到事件，还可以再写一个"快照差分"实现换上，CameraWorker 不用改。
/// </summary>
public interface IMotionSource : IAsyncDisposable
{
    /// <summary>运动开始。带完整事件（时间 + topic），上层要按事件类型过滤就靠它。</summary>
    event Action<OnvifMotionEvent>? MotionStarted;

    /// <summary>运动结束。部分固件不推 false，靠静默超时兜底即可。</summary>
    event Action<OnvifMotionEvent>? MotionEnded;

    /// <summary>收到任何原始事件（含非运动）时触发，给"事件自检"页用。</summary>
    event Action<OnvifMotionEvent>? RawEvent;

    /// <summary>阻塞运行直到取消。内部负责订阅、续订、断线重连。</summary>
    Task RunAsync(CancellationToken ct);
}

/// <summary>PullPoint 轮询实现。</summary>
public sealed class OnvifEventMotionSource : IMotionSource
{
    private readonly OnvifCamera _camera;
    private readonly AppLog? _log;
    private readonly List<string> _seenTopics = new();

    public event Action<OnvifMotionEvent>? MotionStarted;
    public event Action<OnvifMotionEvent>? MotionEnded;
    public event Action<OnvifMotionEvent>? RawEvent;

    public IReadOnlyList<string> SeenTopics => _seenTopics;

    public OnvifEventMotionSource(OnvifCamera camera, AppLog? log = null)
    {
        _camera = camera;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var client = new OnvifEventClient(_camera.Soap, _log);
        var sub = await client.CreatePullPointAsync(_camera.EventsUri, _camera.DeviceUri,
            TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);

        var subscribedAt = DateTime.UtcNow;
        var nextRenew = DateTime.UtcNow.AddSeconds(40);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (DateTime.UtcNow >= nextRenew)
                {
                    await client.RenewAsync(sub, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                    nextRenew = DateTime.UtcNow.AddSeconds(40);
                    _log?.Debug("events", "PullPoint 订阅已续订");
                }

                var batch = await client.PullAsync(sub, TimeSpan.FromSeconds(8), 20, ct).ConfigureAwait(false);
                if (batch.Count == 0) continue;

                foreach (var e in batch)
                {
                    RecordTopic(e.Topic);
                    if (ShouldSkip(e, subscribedAt)) continue;
                    Dispatch(e);
                }
            }
        }
        finally
        {
            try { await client.UnsubscribeAsync(sub, CancellationToken.None).ConfigureAwait(false); }
            catch { /* 设备可能已经断了 */ }
        }
    }

    private void RecordTopic(string topic)
    {
        if (string.IsNullOrEmpty(topic) || _seenTopics.Contains(topic)) return;
        _seenTopics.Add(topic);
        _log?.Info("events", $"首次收到 topic：{topic}");
    }

    private static bool ShouldSkip(OnvifMotionEvent e, DateTime subscribedAt)
    {
        // 订阅瞬间设备会 dump 一批初始状态，一律不当作运动
        if (e.Initialized) return true;
        return DateTime.UtcNow - subscribedAt < TimeSpan.FromSeconds(3);
    }

    private void Dispatch(OnvifMotionEvent e)
    {
        RawEvent?.Invoke(e);
        if (e.IsMotion) MotionStarted?.Invoke(e);
        else MotionEnded?.Invoke(e);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Basic Notification（WS-BaseNotification Subscribe）实现。
/// 设备主动 POST 到我们的回调地址，NotifyHub 负责把 XML 送进来。
/// </summary>
public sealed class BasicNotificationMotionSource : IMotionSource
{
    private readonly OnvifEventClient _client;
    private readonly NotifyHub _hub;
    private readonly EventSubscription _sub;
    private readonly AppLog? _log;
    private readonly DateTime _subscribedAt = DateTime.UtcNow;
    private readonly HashSet<string> _seenTopics = new();

    public event Action<OnvifMotionEvent>? MotionStarted;
    public event Action<OnvifMotionEvent>? MotionEnded;
    public event Action<OnvifMotionEvent>? RawEvent;

    private BasicNotificationMotionSource(OnvifEventClient client, NotifyHub hub, EventSubscription sub,
        AppLog? log)
    {
        _client = client;
        _hub = hub;
        _sub = sub;
        _log = log;
    }

    public static async Task<BasicNotificationMotionSource> CreateAsync(OnvifEventClient client,
        OnvifCamera camera, NotifyHub hub, string consumerUrl, TimeSpan termination, AppLog? log,
        CancellationToken ct)
    {
        var sub = await client.SubscribeBasicAsync(camera.EventsUri, camera.DeviceUri, consumerUrl,
            termination, ct).ConfigureAwait(false);

        return new BasicNotificationMotionSource(client, hub, sub, log);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // 续订放后台，主循环专心收推送
        var renewTask = RenewLoopAsync(ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var xml = await _hub.ReadAsync(ct).ConfigureAwait(false);

                var events = OnvifEventClient.ParseNotify(xml);
                if (events.Count == 0)
                {
                    _log?.Debug("events", "收到 Notify 但没有可识别的 NotificationMessage");
                    continue;
                }

                foreach (var e in events)
                {
                    if (!string.IsNullOrEmpty(e.Topic) && _seenTopics.Add(e.Topic))
                        _log?.Info("events", $"首次收到 topic（推送）：{e.Topic}");

                    RawEvent?.Invoke(e);

                    if (e.Initialized || DateTime.UtcNow - _subscribedAt < TimeSpan.FromSeconds(3)) continue;

                    if (e.IsMotion) MotionStarted?.Invoke(e);
                    else MotionEnded?.Invoke(e);
                }
            }
        }
        finally
        {
            try { await renewTask.ConfigureAwait(false); } catch { }

            try { await _client.UnsubscribeAsync(_sub, CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }
    }

    private async Task RenewLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(40));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await _client.RenewAsync(_sub, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                _log?.Debug("events", "Basic Notification 订阅已续订");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // 续订失败 = 订阅快过期了，直接结束让上层重建
            _log?.Warn("events", "续订失败，订阅可能已失效：" + ex.Message);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed record MotionSourceResult(IMotionSource Source, string ModeName, EventServiceCapabilities? Capabilities);

/// <summary>
/// 按设备能力选事件源：支持 WSSubscriptionPolicy 就走推送，否则退回 PullPoint；推送订阅失败也会自动退回。
/// </summary>
public static class MotionSourceFactory
{
    public static async Task<MotionSourceResult> CreateAsync(CameraConfig camera, OnvifCamera onvif,
        NotifyHub? hub, string? consumerUrl, AppLog? log, CancellationToken ct)
    {
        EventServiceCapabilities? caps = null;

        try
        {
            var probe = new OnvifEventClient(onvif.Soap, log);
            caps = await probe.GetCapabilitiesAsync(onvif.EventsUri, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.Warn("events", "取 Events 能力集失败（按未知处理，走默认策略）：" + ex.Message);
        }

        var wantBasic = camera.EventMode switch
        {
            EventMode.Basic => true,
            EventMode.PullPoint => false,
            _ => caps?.SubscriptionPolicySupport == true,
        };

        if (wantBasic && !string.IsNullOrEmpty(consumerUrl) &&
            Uri.TryCreate(consumerUrl, UriKind.Absolute, out var cbUri) &&
            Net.LocalAddress.IsIp(camera.Host) &&
            !Net.LocalAddress.SameSubnet24(cbUri.Host, camera.Host))
        {
            log?.Warn("events",
                $"回调地址 {consumerUrl} 与摄像头 {camera.Host} 不在同一网段，" +
                "设备很可能连不回来。如果一直收不到推送，请确认这台电脑有和摄像头同网段的网卡，或改用轮询。");
        }

        if (wantBasic && (hub is null || string.IsNullOrEmpty(consumerUrl)))
        {
            log?.Warn("events", "网页服务没起来或取不到本机局域网地址，收不到推送，退回 PullPoint");
            wantBasic = false;
        }

        if (wantBasic)
        {
            try
            {
                var client = new OnvifEventClient(onvif.Soap, log);
                var source = await BasicNotificationMotionSource.CreateAsync(client, onvif, hub!,
                    consumerUrl!, TimeSpan.FromSeconds(120), log, ct).ConfigureAwait(false);

                return new MotionSourceResult(source, "BasicNotification（推送）", caps);
            }
            catch (Exception ex)
            {
                log?.Warn("events", $"Basic Notification 订阅失败，退回 PullPoint：{ex.Message}");
            }
        }

        if (caps is { PullPointSupport: false } && camera.EventMode != EventMode.PullPoint)
            log?.Warn("events", "设备报告不支持 PullPoint，但仍尝试一次");

        return new MotionSourceResult(new OnvifEventMotionSource(onvif, log), "PullPoint（轮询）", caps);
    }
}
