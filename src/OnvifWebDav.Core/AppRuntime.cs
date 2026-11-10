using OnvifWebDav.Config;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.Media;
using OnvifWebDav.Pipeline;
using OnvifWebDav.Storage;
using OnvifWebDav.WebDav;

namespace OnvifWebDav;

/// <summary>
/// 运行时容器：把配置、日志、ffmpeg、spool、缓存、上传队列、摄像头 worker 组装起来。
/// WinForms 和网页端都通过它拿东西，谁都不直接 new。
/// </summary>
public sealed class AppRuntime : IAsyncDisposable
{
    public ConfigStore Store { get; }
    public AppConfig Config { get; private set; }
    public AppLog Log { get; }
    public FfmpegLocator Ffmpeg { get; }
    public LocalSpool Spool { get; }
    public MediaCache Cache { get; private set; }
    public RecordingsRepository Recordings { get; private set; }
    public AppStatus Status { get; } = new();

    /// <summary>Basic Notification 的收件箱：设备 POST 到网页 /onvif/notify 后塞进来。</summary>
    public NotifyHub NotifyHub { get; } = new();

    /// <summary>云端配额缓存，上传前用它判断要不要停。</summary>
    public QuotaMonitor Quota { get; } = new();

    /// <summary>网页服务是否在跑。没跑就没法当推送的回调接收方。</summary>
    public bool WebRunning { get; set; }

    /// <summary>
    /// 给摄像头用的回调地址（它必须能连回来）；网页没起来或没有局域网 IP 时为 null。
    /// 会优先选和摄像头同网段的本机网卡 —— 多网卡机器上挑错网卡是推送模式最常见的死因。
    /// </summary>
    public string? ConsumerUrl =>
        WebRunning
            ? Net.LocalAddress.ConsumerUrl(Config.Web.Port, Config.PrimaryCamera?.Host)
            : null;
    public UploadQueue Upload { get; private set; }
    public CameraWorker? Worker { get; private set; }
    public string DataDirectory => Store.DataDirectory;

    private bool _started;

    public AppRuntime(ConfigStore? store = null)
    {
        Store = store ?? new ConfigStore();
        Config = Store.Load();
        Log = new AppLog(Store.DataDirectory);
        Log.MinLevel = Config.System.LogLevel switch
        {
            "Debug" => LogLevel.Debug,
            "Warn" => LogLevel.Warn,
            "Error" => LogLevel.Error,
            _ => LogLevel.Info,
        };

        Ffmpeg = new FfmpegLocator();
        Spool = new LocalSpool(Path.Combine(Store.DataDirectory, "spool"), Log, Config.Recording.RingDirectory);
        Status.RingPath = Spool.RingRoot;

        Cache = new MediaCache(Path.Combine(Spool.CacheRoot, "media"), CacheBytes(), Log);
        Recordings = new RecordingsRepository(CreateDavClient, Cache, Log);
        Upload = new UploadQueue(Spool, CreateDavClient, Log, Status,
            () => Config.WebDav.SupportsMove, Quota, () => Config.Quota);

        if (!Ffmpeg.IsAvailable)
            Log.Error("runtime", Ffmpeg.MissingMessage);

        // 环形缓冲现在保留分钟级窗口，崩溃残留会占着几百 MB。
        // CameraWorker 只在本次进程第一次启动时清空（重连不能清，否则整段缓冲丢失），
        // 所以"上次崩溃留下的孤儿段"必须在这里清。
        try
        {
            Spool.CleanRing();
            Log.Info("runtime", "已清理环形缓冲的上次残留");
        }
        catch (Exception ex)
        {
            Log.Warn("runtime", "清理环形缓冲残留失败：" + ex.Message);
        }

        Log.Info("runtime", $"数据目录：{Store.DataDirectory}");
        if (Ffmpeg.FfmpegPath is not null) Log.Info("runtime", "ffmpeg：" + Ffmpeg.FfmpegPath);
    }

    private long CacheBytes() => Math.Max(512L, (long)Config.Web.CacheGb) * 1024 * 1024;

    /// <summary>每次调用都按当前配置新建（配置改了密码/地址立刻生效）。</summary>
    public WebDavClient? CreateDavClient()
    {
        if (string.IsNullOrWhiteSpace(Config.WebDav.Url)) return null;
        return new WebDavClient(Config.WebDav, Log);
    }

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;

        // 停止过一次之后队列的 CTS 已经取消，必须换新实例，否则起来就立刻退出
        if (Upload.IsStopped)
            Upload = new UploadQueue(Spool, CreateDavClient, Log, Status,
            () => Config.WebDav.SupportsMove, Quota, () => Config.Quota);

        Upload.Start();

        var camera = Config.PrimaryCamera;
        if (camera is null || !camera.Enabled)
        {
            Status.State = WorkerState.Stopped;
            Status.Message = "没有启用的摄像头";
            Log.Warn("runtime", "没有启用的摄像头，录像功能不会启动");
            return;
        }

        Worker = new CameraWorker(camera, () => Config, Log, Ffmpeg, Spool, Upload, Status,
            NotifyHub, () => ConsumerUrl);
        Worker.Start();
        Log.Info("runtime", $"录像服务已启动（{camera.Name} {camera.Host}）");
    }

    public async Task StopAsync()
    {
        if (!_started) return;
        _started = false;

        Log.Info("runtime", "正在停止录像服务…");

        if (Worker is not null)
        {
            await Worker.StopAsync().ConfigureAwait(false);
            await Worker.DisposeAsync().ConfigureAwait(false);
            Worker = null;
        }

        await Upload.StopAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        Status.State = WorkerState.Stopped;
        Status.Message = "已停止";
    }

    /// <summary>保存配置并热重载（worker 全重启，缓存上限立刻生效）。</summary>
    public async Task ApplyConfigAsync(AppConfig config)
    {
        var wasStarted = _started;

        await StopAsync().ConfigureAwait(false);

        Config = config;
        Store.Save(Config);
        Log.MinLevel = Config.System.LogLevel switch
        {
            "Debug" => LogLevel.Debug,
            "Warn" => LogLevel.Warn,
            "Error" => LogLevel.Error,
            _ => LogLevel.Info,
        };

        Recordings.ResetClient();
        Cache.SetLimit(CacheBytes());

        // 环形目录可能被改成别的盘（内存盘），热重载时一起切
        Spool.SetRingRoot(Config.Recording.RingDirectory);
        Status.RingPath = Spool.RingRoot;

        await Upload.DisposeAsync().ConfigureAwait(false);
        Upload = new UploadQueue(Spool, CreateDavClient, Log, Status,
            () => Config.WebDav.SupportsMove, Quota, () => Config.Quota);

        if (wasStarted) await StartAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        try { await Upload.DisposeAsync().ConfigureAwait(false); } catch { }
    }
}
