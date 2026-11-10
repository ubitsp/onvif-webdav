namespace OnvifWebDav.Pipeline;

/// <summary>
/// Basic Notification 的入口：设备会 POST 事件到我们的 HTTP 端点，
/// 端点把原始 XML 丢进这里，运动事件源从队列里取。
/// 用无界 Channel，加一点背压保护（超过上限丢最旧的并以计数告警）。
/// </summary>
public sealed class NotifyHub
{
    private readonly System.Threading.Channels.Channel<string> _channel =
        System.Threading.Channels.Channel.CreateBounded<string>(
            new System.Threading.Channels.BoundedChannelOptions(512)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

    private long _received;
    private DateTime _lastReceivedUtc = DateTime.MinValue;

    public long Received => Interlocked.Read(ref _received);
    public DateTime LastReceivedUtc => _lastReceivedUtc;

    /// <summary>设备 POST 过来一条 Notify 时调用（可能在 Kestrel 线程）。</summary>
    public void Push(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return;
        Interlocked.Increment(ref _received);
        _lastReceivedUtc = DateTime.UtcNow;
        _channel.Writer.TryWrite(xml);
    }

    /// <summary>阻塞读一条；取消时抛 OperationCanceledException。</summary>
    public ValueTask<string> ReadAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);
}
