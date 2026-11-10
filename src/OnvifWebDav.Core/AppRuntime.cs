using OnvifWebDav.Config;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.Media;
using OnvifWebDav.Pipeline;
using OnvifWebDav.Quark;
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
        Recordings = new RecordingsRepository(CreateStorage, Cache, Log);
        Upload = new UploadQueue(Spool, CreateStorage, Log, Status,
            () => StorageSupportsMove, Quota, () => Config.Quota);

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

    /// <summary>
    /// 按当前配置创建存储后端。
    /// 夸克分支依赖会话，没拿到时返回 null（上传会积压但不会崩）。
    /// </summary>
    public IRemoteStorage? CreateStorage()
    {
        if (Config.UseQuark)
        {
            var session = _quarkSession;

            if (session is null)
            {
                Log.Warn("storage", "夸克会话还没拿到，上传会一直积压。先到「存储」页点「从浏览器获取 Cookie」。");
                return null;
            }

            return new QuarkStorage(
                new QuarkApiClient(session, Log, Config.Quark.BaseUrl),
                Log,
                Path.Combine(Store.DataDirectory, "quark-fids.json"),
                Config.Quark.RootPath);
        }

        return CreateDavClient();
    }

    /// <summary>当前后端的 MOVE 能力。夸克不支持，走"直接写最终路径"的分支。</summary>
    public bool? StorageSupportsMove => Config.UseQuark ? false : Config.WebDav.SupportsMove;

    /// <summary>夸克会话是否已就绪（UI 用来决定能不能开始录像）。</summary>
    public bool QuarkSessionReady => _quarkSession is not null;

    /// <summary>会话来源："cdp" = 刚从浏览器取，"cache" = 用的本地缓存。UI 用来显示状态。</summary>
    public string? QuarkSessionSource => _quarkSession?.Source;

    /// <summary>会话是什么时候取的。</summary>
    public DateTime? QuarkSessionAtUtc => _quarkSession?.AtUtc;

    /// <summary>一句话描述会话状态，直接丢给界面显示。</summary>
    public string QuarkSessionDescribe()
    {
        var s = _quarkSession;

        if (s is null) return "未获取（上传会积压）";

        var when = s.AtUtc.ToLocalTime().ToString("MM-dd HH:mm");

        return s.Source switch
        {
            "cdp" => $"已从浏览器获取（{when}）",
            "cache" => $"本地缓存（{when} 取的）",
            _ => $"已就绪（{when}）",
        };
    }

    private QuarkCdpClient.Session? _quarkSession;

    /// <summary>缓存里没存 UA 时的兜底值。要和网页版一致，否则签名里的 x-oss-user-agent 对不上。</summary>
    private const string DefaultBrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/144.0.0.0 Safari/537.36 QuarkPC/7.4.7.1036";

    /// <summary>
    /// 取得夸克会话：先试本地缓存的 Cookie（快，不用开浏览器），失效才去 CDP 捞。
    ///
    /// 缓存能长期用有依据：每次 API 响应服务端都会 Set-Cookie: __puus 滚动续期，
    /// 只要程序在跑就一直有效。真正会失效的是"好几天没启动 + 夸克主动踢掉"。
    /// </summary>
    public async Task<bool> RefreshQuarkSessionAsync(CancellationToken ct, bool forceBrowser = false)
    {
        // 1) 缓存优先
        if (!forceBrowser && Config.Quark.Cookie.Length > 0)
        {
            var ua = string.IsNullOrWhiteSpace(Config.Quark.UserAgent)
                ? DefaultBrowserUa
                : Config.Quark.UserAgent;

            var cached = new QuarkCdpClient.Session(Config.Quark.Cookie, ua, Config.Quark.CookieAtUtc, "cache");

            if (await ValidateQuarkSessionAsync(cached, ct).ConfigureAwait(false))
            {
                _quarkSession = cached;
                Log.Info("storage", "夸克：使用本地缓存的 Cookie（无需浏览器）");
                return true;
            }

            Log.Warn("storage", "夸克：本地缓存的 Cookie 已失效，改从浏览器获取");
        }

        // 2) 先确保调试端口可用 —— 客户端没开就自动用调试模式拉起来
        if (!await EnsureQuarkDebugPortAsync(ct).ConfigureAwait(false)) return false;

        // 3) CDP 捞
        try
        {
            using var cdp = new QuarkCdpClient(Config.Quark.CdpPort, Log);
            var session = await cdp.TryGetSessionAsync(ct).ConfigureAwait(false);

            if (session is null)
            {
                Log.Error("storage",
                    $"夸克：拿不到 Cookie。请用 \"quark.exe --remote-debugging-port={Config.Quark.CdpPort}\" " +
                    "启动浏览器并登录 pan.quark.cn");
                return false;
            }

            if (!await ValidateQuarkSessionAsync(session, ct).ConfigureAwait(false))
            {
                Log.Error("storage", "夸克：从浏览器拿到的 Cookie 调用 member 失败（可能未登录或会话过期）");
                return false;
            }

            _quarkSession = session;
            Config.Quark.Cookie = session.Cookie;
            Config.Quark.UserAgent = session.UserAgent;
            Config.Quark.CookieAtUtc = session.AtUtc;

            Log.Info("storage", $"夸克：已从浏览器取到会话（{session.Cookie.Split(';').Length} 项 Cookie）");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("storage", "夸克：获取会话异常：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 确保夸克的调试端口可用。
    ///
    /// 三种情况：
    ///  · 端口已经通了 → 直接用
    ///  · 客户端没在跑 → 自动用 --remote-debugging-port 拉起来，等端口就绪
    ///  · 客户端在跑但没开端口 → **不能自动处理**。夸克是单实例，新进程会忽略参数，
    ///    而杀进程会丢用户正在做的事，只能提示他手动退出。
    /// </summary>
    private async Task<bool> EnsureQuarkDebugPortAsync(CancellationToken ct)
    {
        var port = Config.Quark.CdpPort;

        using (var probe = new QuarkCdpClient(port, Log))
        {
            if (await probe.TryGetBrowserInfoAsync(ct).ConfigureAwait(false) is not null) return true;
        }

        if (QuarkLauncher.HasRunningProcess())
        {
            Log.Error("storage",
                "夸克客户端正在运行，但没有开调试端口。夸克是单实例应用，新参数会被直接忽略 —— " +
                "请完全退出夸克（托盘图标也要右键退出）后重试。");
            return false;
        }

        var exe = QuarkLauncher.TryFindExe(Config.Quark.ExePath);

        if (exe is null)
        {
            Log.Error("storage",
                "找不到 quark.exe。绿色版请到「存储」页把程序路径填上（形如 " +
                "E:////Quark////quark.exe），或手动启动：" +
                $"\"<夸克安装目录>\\quark.exe\" --remote-debugging-port={port}");
            return false;
        }

        // 反查到的路径记进配置，下次就算进程没在跑也能直接启动
        if (string.IsNullOrWhiteSpace(Config.Quark.ExePath))
        {
            Config.Quark.ExePath = exe;
            Log.Info("storage", "已记住夸克程序路径：" + exe);
        }

        Log.Info("storage", $"夸克未运行，正在以调试模式启动：{exe} --remote-debugging-port={port}");

        if (!QuarkLauncher.Launch(exe, port, Log))
        {
            Log.Error("storage", "夸克启动失败（路径不对或权限不足）");
            return false;
        }

        return await QuarkLauncher.WaitForPortAsync(port, TimeSpan.FromSeconds(30), Log, ct)
            .ConfigureAwait(false);
    }

    /// <summary>调一次 member 验证会话真能用（光有 Cookie 不等于没过期）。</summary>
    private async Task<bool> ValidateQuarkSessionAsync(QuarkCdpClient.Session session, CancellationToken ct)
    {
        try
        {
            using var api = new QuarkApiClient(session, Log, Config.Quark.BaseUrl);
            await api.GetMemberAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (QuarkApiException ex)
        {
            Log.Warn("storage", "夸克会话校验失败：" + ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("storage", "夸克会话校验异常：" + ex.Message);
            return false;
        }
    }

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;

        // 夸克后端要先拿到会话，否则 CreateStorage 一直返回 null，上传全积压
        if (Config.UseQuark && _quarkSession is null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await RefreshQuarkSessionAsync(cts.Token).ConfigureAwait(false);
        }

        // 停止过一次之后队列的 CTS 已经取消，必须换新实例，否则起来就立刻退出
        if (Upload.IsStopped)
            Upload = new UploadQueue(Spool, CreateStorage, Log, Status,
            () => StorageSupportsMove, Quota, () => Config.Quota);

        Upload.Start();

        var camera = Config.PrimaryCamera;
        if (camera is null || !camera.Enabled)
        {
            Status.State = WorkerState.Stopped;
            Status.Message = "没有启用的摄像头";
            Log.Warn("runtime", "没有启用的摄像头，录像功能不会启动");
            return;
        }

        Log.Info("storage", Config.UseQuark
            ? $"存储后端：夸克网盘（根 {(string.IsNullOrWhiteSpace(Config.Quark.RootPath) ? "/" : Config.Quark.RootPath)}）" +
              $"，会话{(QuarkSessionReady ? "已就绪" : "未就绪")}"
            : $"存储后端：WebDAV（{(string.IsNullOrWhiteSpace(Config.WebDav.Url) ? "地址未填" : Config.WebDav.Url)}）");

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
