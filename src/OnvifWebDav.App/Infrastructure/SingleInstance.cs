namespace OnvifWebDav.App.Infrastructure;

/// <summary>
/// 单实例：第二次启动不重复跑（否则两个进程抢同一个 RTSP 流、抢同一个端口），
/// 而是通知已有实例把窗口显示出来。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private Thread? _listener;

    public bool IsFirst { get; }

    public SingleInstance()
    {
        var key = "OnvifWebDav_" + Math.Abs(AppContext.BaseDirectory.ToLowerInvariant().GetHashCode());
        var mutexName = @"Local\" + key;
        var eventName = @"Local\" + key + "_show";

        _mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        IsFirst = createdNew;

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
    }

    /// <summary>第二个实例调用：叫醒第一个实例显示窗口。</summary>
    public void SignalExisting()
    {
        try { _showEvent.Set(); } catch { }
    }

    /// <summary>第一个实例调用：后台等唤醒信号。</summary>
    public void ListenForShow(Action onShow)
    {
        if (!IsFirst) return;

        _listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!_showEvent.WaitOne(TimeSpan.FromSeconds(2))) continue;
                    onShow();
                }
                catch
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "single-instance-listener",
        };

        _listener.Start();
    }

    public void Dispose()
    {
        try { _listener?.Interrupt(); } catch { }
        try { if (IsFirst) _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
        _showEvent.Dispose();
    }
}
