using System.Text.Json.Serialization;
using OnvifWebDav.Storage;

namespace OnvifWebDav.Config;

public enum RecordingMode
{
    /// <summary>常驻录制 2 秒 TS 分段到环形缓冲，事件到达时把预录段一起保留（推荐）。</summary>
    RingBuffer = 0,

    /// <summary>没事件不起 ffmpeg，最省 CPU/磁盘，但没有预录。</summary>
    TriggerOnly = 1,
}

public enum EventMode
{
    /// <summary>看设备能力：支持 WSSubscriptionPolicy 就用推送（Basic Notification），否则轮询。</summary>
    Auto = 0,

    /// <summary>强制 PullPoint 轮询。</summary>
    PullPoint = 1,

    /// <summary>强制 Basic Notification 推送（设备会 POST 到本机网页服务的 /onvif/notify）。</summary>
    Basic = 2,
}

public sealed class CameraConfig
{
    public string Id { get; set; } = "cam1";
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "大门";

    /// <summary>IP 或主机名。</summary>
    public string Host { get; set; } = "192.168.1.64";

    /// <summary>ONVIF 端口。TP-Link 固件 1.6+ 为 80，旧版为 2020；测试时两个都会试。</summary>
    public int OnvifPort { get; set; } = 2020;

    public string Username { get; set; } = "admin";

    /// <summary>DPAPI 加密后的 Base64（落盘用）。</summary>
    public string PasswordEnc { get; set; } = "";

    /// <summary>明文密码。只存在于内存，不序列化。</summary>
    [JsonIgnore]
    public string Password { get; set; } = "";

    /// <summary>留空则从 ONVIF GetStreamUri 自动获取。</summary>
    public string RtspUrl { get; set; } = "";

    /// <summary>main / sub，用于自动拼接 TP-Link 默认 RTSP 地址时的兜底。</summary>
    public string Stream { get; set; } = "main";

    public string SnapshotUrl { get; set; } = "";

    public int RtspPort { get; set; } = 554;

    /// <summary>
    /// 事件获取方式。Auto = 设备支持 WSSubscriptionPolicySupport 就走推送，否则轮询。
    /// 推送需要网页服务在本机跑着（设备要 POST 回来），网页起不来时自动退回轮询。
    /// </summary>
    public EventMode EventMode { get; set; } = EventMode.Auto;

    public string GetPassword() => Password;

    public string StreamPath => string.Equals(Stream, "sub", StringComparison.OrdinalIgnoreCase) ? "stream2" : "stream1";
}

public sealed class WebDavConfig
{
    /// <summary>例如 https://cloud.example.com/remote.php/dav/files/user 或 http://192.168.1.10:5005</summary>
    public string Url { get; set; } = "";

    public string Username { get; set; } = "";
    public string PasswordEnc { get; set; } = "";

    [JsonIgnore]
    public string Password { get; set; } = "";

    /// <summary>根目录，例如 /onvif-录像 或 /dav/onvif。</summary>
    public string RootPath { get; set; } = "/onvif";

    public bool IgnoreCertErrors { get; set; }

    /// <summary>
    /// 是否走系统代理。默认 false（局域网 NAS 不该走代理）；
    /// 如果公司网络必须通过代理才能访问外网 WebDAV，把这个打开。
    /// </summary>
    public bool UseSystemProxy { get; set; }

    /// <summary>
    /// 服务端是否支持 MOVE（.part → 正式名 的原子提交）。
    /// 点过「测试 WebDAV」后会自动记住；阿里云盘这类不支持 MOVE 的后端能因此省掉一次完整的重复上传。
    /// null = 还没测过。
    /// </summary>
    public bool? SupportsMove { get; set; }

    public int TimeoutSeconds { get; set; } = 100;
}

/// <summary>
/// 云端配额守卫。阿里云盘只有 105G，录像只增不减必然撑爆，
/// 所以在每次上传前看一眼剩余空间：够就传，不够就告警/暂停/（可选）删最旧的腾地方。
/// </summary>
/// <summary>
/// 按时间的录像保留策略。
///
/// 和「云端空间守卫」是两套互补的规则：
///  · 空间守卫看**剩余容量**，不够了就删最旧的腾地方
///  · 保留策略看**日期**，超过 N 天的一律删掉（不管空间还剩多少）
/// 两个都开也没问题，谁先触发谁先删。
/// </summary>
public sealed class RetentionConfig
{
    /// <summary>
    /// 保留天数。0 = 永久保留（关闭按时间清理）。
    /// 注意是"整天"粒度：设 7 天表示保留今天在内的最近 7 天。
    /// </summary>
    public int KeepDays { get; set; }

    /// <summary>每天几点执行清理（本地时间 0~23）。默认凌晨 3 点，避开白天录像高峰。</summary>
    public int CheckHour { get; set; } = 3;
}

/// <summary>录像存到哪里。两个后端共用 IRemoteStorage 抽象。</summary>
public enum StorageBackend
{
    WebDav = 0,
    Quark = 1,
}

/// <summary>
/// 夸克网盘后端。
///
/// 和 WebDAV 最大的不同：它没有账号密码，只有**浏览器会话 Cookie**。
/// Cookie 靠 CDP 从夸克浏览器里捞（浏览器必须带 --remote-debugging-port 启动），
/// 捞到之后可以缓存起来重复用 —— 服务端每次响应都会下发新的 __puus，会话会自己续下去。
/// </summary>
public sealed class QuarkConfig
{
    public string BaseUrl { get; set; } = "https://drive-pc.quark.cn";

    /// <summary>夸克浏览器的调试端口。要和启动参数 --remote-debugging-port 一致。</summary>
    public int CdpPort { get; set; } = 9222;

    /// <summary>
    /// quark.exe 的完整路径。
    /// **绿色版/自定义安装位置必须填** —— 这类安装不在任何标准路径下，程序没法自己猜。
    /// 第一次成功从运行中的进程反查到之后会自动记在这里。
    /// </summary>
    public string ExePath { get; set; } = "";

    /// <summary>根目录（空 = 网盘根）。录像会放在这下面。</summary>
    public string RootPath { get; set; } = "/onvif";

    /// <summary>DPAPI 加密后的 Cookie（落盘用）。有它就不必每次启动都开浏览器。</summary>
    public string CookieEnc { get; set; } = "";

    /// <summary>Cookie 是什么时候取的（UTC）。太旧的会话优先重新捞一次。</summary>
    public DateTime CookieAtUtc { get; set; }

    /// <summary>取 Cookie 时浏览器报的 UA。上传签名里的 x-oss-user-agent 必须和它一致。</summary>
    public string UserAgent { get; set; } = "";

    /// <summary>明文 Cookie。只存在于内存，不序列化。</summary>
    [JsonIgnore]
    public string Cookie { get; set; } = "";
}

public sealed class StorageQuotaConfig
{    public bool Enabled { get; set; } = true;

    /// <summary>剩余空间低于这个值（GB）就把状态标黄并告警。</summary>
    public int WarnBelowGb { get; set; } = 10;

    /// <summary>剩余空间低于这个值（GB）就停止上传，本地积压等人工处理。0 = 不暂停。</summary>
    public int PauseBelowGb { get; set; } = 3;

    /// <summary>暂停之前先自动删掉最旧的录像腾空间（不可逆，默认关）。</summary>
    public bool AutoDeleteOldest { get; set; }

    /// <summary>自动删除的目标：至少腾出这么多剩余空间（GB）。</summary>
    public int AutoDeleteTargetFreeGb { get; set; } = 15;

    /// <summary>多久真正查一次配额（分钟）。两次上传之间用缓存值，避免每次都发 PROPFIND。</summary>
    public int ProbeMinutes { get; set; } = 30;
}

public sealed class RecordingConfig
{
    public RecordingMode Mode { get; set; } = RecordingMode.RingBuffer;

    /// <summary>
    /// 环形缓冲保留窗口（秒）。事件到达时从这个窗口里切片段，默认 600 = 10 分钟。
    /// 想"几乎一直有录像可切"就把这个值放大（30 分钟约 700MB @3Mbps）。
    /// 注意：这是**本地**缓冲，不会上传，云端仍然只存切出来的片段。
    /// </summary>
    public int RingKeepSeconds { get; set; } = 600;

    /// <summary>
    /// 环形缓冲目录。空 = 数据目录下的 spool\ring。
    /// 想写内存盘就填盘符路径（如 R://onvif-ring），连续录像就不再碰物理盘；
    /// 只有事件片段会落到 spool\jobs（上传成功的重试队列，必须持久）。
    /// </summary>
    public string RingDirectory { get; set; } = "";

    /// <summary>
    /// 环形缓冲目录的磁盘硬上限（MB）。0 = 只按时间淘汰。
    /// 必须有这一条：环形目录不算在 SpoolLimitGb 里，Trim 出问题会静默吃满盘。
    /// 实际生效值还会被「所在卷剩余空间」压一层，内存盘 1GB 时不会按 2048MB 硬撑。
    /// </summary>
    public int RingLimitMb { get; set; } = 2048;

    /// <summary>预录秒数（仅 RingBuffer 模式有效）。环形窗口够大时可以调到 30~60 秒。</summary>
    public int PreRollSeconds { get; set; } = 8;

    /// <summary>运动停止后继续录多久（片段尾部保底内容）。</summary>
    public int SilentStopSeconds { get; set; } = 5;

    /// <summary>
    /// 片尾余量（秒）。静默判定再多等这么久才收尾，把"人走出画面"和事件延迟一起兜住。
    /// 实际尾部 = SilentStopSeconds + PostRollSeconds。
    /// </summary>
    public int PostRollSeconds { get; set; } = 3;

    /// <summary>
    /// 只对这些事件 topic 触发录制（逗号/分号分隔的子串匹配，不区分大小写）。
    /// 空 = 所有运动事件都录。例如只想录人形：填 PersonDetector。
    /// </summary>
    public string TopicFilter { get; set; } = "";

    public int MinSeconds { get; set; } = 5;

    /// <summary>单段最长秒数，超过就强制切片，防止雨雪噪点无限录。</summary>
    public int MaxSeconds { get; set; } = 300;

    /// <summary>分段长度（TS），同时是预录的最小粒度。</summary>
    public int SegmentSeconds { get; set; } = 2;

    /// <summary>源是 H.265 时是否强制转 H.264（浏览器才能播）。</summary>
    public bool TranscodeToH264 { get; set; }

    /// <summary>探到 H.265 时自动转码（否则网页多半放不了，宁可花点 CPU）。</summary>
    public bool AutoTranscodeHevc { get; set; } = true;

    public bool RecordAudio { get; set; } = true;

    /// <summary>判定"这段结束"所需的无事件时长：静默 + 片尾。</summary>
    public int TailSeconds => Math.Max(0, SilentStopSeconds) + Math.Max(0, PostRollSeconds);

    /// <summary>
    /// 环形窗口实际要保留的长度。至少覆盖"预录 + 静默 + 片尾 + 几段冗余"，
    /// 否则切出来的必然是残片 —— 用户把 RingKeepSeconds 调小时自动兜住。
    /// </summary>
    public int EffectiveRingKeepSeconds =>
        Math.Max(RingKeepSeconds, PreRollSeconds + TailSeconds + SegmentSeconds * 4);
}

public sealed class WebConfig
{
    public int Port { get; set; } = 8080;

    /// <summary>DPAPI 加密后的访问密码；为空表示不需要登录。</summary>
    public string PasswordEnc { get; set; } = "";

    [JsonIgnore]
    public string Password { get; set; } = "";

    /// <summary>Cookie 签名密钥（随机 Base64），首次生成后固定。</summary>
    public string CookieKey { get; set; } = "";

    /// <summary>本地播放缓存上限（GB），0 表示不留缓存。</summary>
    public int CacheGb { get; set; } = 2;

    /// <summary>登录有效期（天）。</summary>
    public int SessionDays { get; set; } = 30;
}

public sealed class SystemConfig
{
    public bool AutoStart { get; set; }
    public string LogLevel { get; set; } = "Info";

    /// <summary>本地待上传积压上限（GB）。</summary>
    public int SpoolLimitGb { get; set; } = 20;
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public List<CameraConfig> Cameras { get; set; } = new();
    public WebDavConfig WebDav { get; set; } = new();
    public RecordingConfig Recording { get; set; } = new();
    public WebConfig Web { get; set; } = new();
    public SystemConfig System { get; set; } = new();
    public StorageQuotaConfig Quota { get; set; } = new();
    public RetentionConfig Retention { get; set; } = new();
    public QuarkConfig Quark { get; set; } = new();

    /// <summary>当前用哪个存储后端。切换后要重启录像服务。</summary>
    public StorageBackend StorageBackend { get; set; } = StorageBackend.WebDav;

    public bool UseQuark => StorageBackend == StorageBackend.Quark;

    public CameraConfig? PrimaryCamera =>
        Cameras.FirstOrDefault(c => c.Enabled) ?? Cameras.FirstOrDefault();

    public static AppConfig CreateDefault()
    {
        var cfg = new AppConfig();
        cfg.Cameras.Add(new CameraConfig());
        cfg.Web.CookieKey = Convert.ToBase64String(
            global::System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        return cfg;
    }

    /// <summary>把内存中的明文密码加密进 *Enc 字段（保存前调用）。</summary>
    public void SealSecrets()
    {
        foreach (var cam in Cameras)
            cam.PasswordEnc = SecretProtector.Protect(cam.Password);

        WebDav.PasswordEnc = SecretProtector.Protect(WebDav.Password);
        Web.PasswordEnc = SecretProtector.Protect(Web.Password);

        // 夸克的 Cookie 等价于账号凭证，落盘必须加密
        if (Quark.Cookie.Length > 0)
        {
            Quark.CookieEnc = SecretProtector.Protect(Quark.Cookie);
            Quark.CookieAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>把 *Enc 解密到内存明文（加载后调用）。</summary>
    public void UnsealSecrets()
    {
        foreach (var cam in Cameras)
            cam.Password = SecretProtector.Unprotect(cam.PasswordEnc);

        WebDav.Password = SecretProtector.Unprotect(WebDav.PasswordEnc);
        Web.Password = SecretProtector.Unprotect(Web.PasswordEnc);
        Quark.Cookie = SecretProtector.Unprotect(Quark.CookieEnc);
    }
}
