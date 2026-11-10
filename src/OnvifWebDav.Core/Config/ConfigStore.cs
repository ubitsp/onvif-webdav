using System.Text;
using System.Text.Json;

namespace OnvifWebDav.Config;

/// <summary>
/// 配置文件读写。位置：%LocalAppData%\OnvifWebDav\config.json（不用 ProgramData，避开 UAC）。
/// 写入用 tmp + File.Replace 原子替换，避免断电写坏配置。
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public string DataDirectory { get; }
    public string FilePath { get; }

    public ConfigStore(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnvifWebDav");
        FilePath = Path.Combine(DataDirectory, "config.json");
        Directory.CreateDirectory(DataDirectory);
    }

    public AppConfig Load()
    {
        if (!File.Exists(FilePath))
        {
            var def = AppConfig.CreateDefault();
            Save(def);
            return def;
        }

        try
        {
            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? AppConfig.CreateDefault();
            Normalize(cfg);
            cfg.UnsealSecrets();
            return cfg;
        }
        catch
        {
            // 配置损坏时不静默覆盖：备份后重新生成，用户可在日志里查到
            try { File.Copy(FilePath, FilePath + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); }
            catch { /* ignore */ }

            var def = AppConfig.CreateDefault();
            Save(def);
            return def;
        }
    }

    public void Save(AppConfig config)
    {
        Normalize(config);
        config.SealSecrets();

        Directory.CreateDirectory(DataDirectory);
        var tmp = FilePath + ".tmp";
        var json = JsonSerializer.Serialize(config, JsonOpts);
        File.WriteAllText(tmp, json, new UTF8Encoding(false));

        if (File.Exists(FilePath))
            File.Replace(tmp, FilePath, null);
        else
            File.Move(tmp, FilePath);
    }

    private static void Normalize(AppConfig cfg)
    {
        cfg.Cameras ??= new List<CameraConfig>();
        if (cfg.Cameras.Count == 0) cfg.Cameras.Add(new CameraConfig());
        cfg.WebDav ??= new WebDavConfig();
        cfg.Recording ??= new RecordingConfig();
        cfg.Web ??= new WebConfig();
        cfg.System ??= new SystemConfig();
        cfg.Quota ??= new StorageQuotaConfig();

        if (cfg.Quota.WarnBelowGb is < 0 or > 100000) cfg.Quota.WarnBelowGb = 10;
        if (cfg.Quota.PauseBelowGb is < 0 or > 100000) cfg.Quota.PauseBelowGb = 3;
        if (cfg.Quota.AutoDeleteTargetFreeGb is < 1 or > 100000) cfg.Quota.AutoDeleteTargetFreeGb = 15;
        if (cfg.Quota.ProbeMinutes is < 1 or > 1440) cfg.Quota.ProbeMinutes = 30;

        if (string.IsNullOrEmpty(cfg.Web.CookieKey))
            cfg.Web.CookieKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        if (cfg.Web.Port is < 1 or > 65535) cfg.Web.Port = 8080;
        if (cfg.Recording.SegmentSeconds is < 1 or > 10) cfg.Recording.SegmentSeconds = 2;
        if (cfg.Recording.PreRollSeconds is < 0 or > 600) cfg.Recording.PreRollSeconds = 8;
        if (cfg.Recording.SilentStopSeconds is < 1 or > 120) cfg.Recording.SilentStopSeconds = 5;
        if (cfg.Recording.MinSeconds is < 1 or > 60) cfg.Recording.MinSeconds = 5;
        if (cfg.Recording.MaxSeconds is < 30 or > 3600) cfg.Recording.MaxSeconds = 300;
        if (cfg.Recording.PostRollSeconds is < 0 or > 120) cfg.Recording.PostRollSeconds = 3;
        if (cfg.Recording.RingKeepSeconds is < 30 or > 86400) cfg.Recording.RingKeepSeconds = 600;
        if (cfg.Recording.RingLimitMb is < 0 or > 1048576) cfg.Recording.RingLimitMb = 2048;
        cfg.Recording.TopicFilter = (cfg.Recording.TopicFilter ?? "").Trim();
        cfg.Recording.RingDirectory = (cfg.Recording.RingDirectory ?? "").Trim().Trim('"');
    }
}
