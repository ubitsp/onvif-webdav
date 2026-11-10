namespace OnvifWebDav.Storage;

/// <summary>一条录像的元数据。落盘到 WebDAV 的每日索引 JSON 里，也是网页列表的数据源。</summary>
public sealed class ClipInfo
{
    public string Id { get; set; } = "";
    public string CameraId { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public int DurationMs { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>相对 WebDAV 根目录的路径。</summary>
    public string Path { get; set; } = "";
    public string Thumb { get; set; } = "";
    public string VideoCodec { get; set; } = "";
    public bool HasAudio { get; set; }
    public double PeakMotion { get; set; }
    public int EventCount { get; set; }

    /// <summary>超长事件被强制切片时，指向上一段。</summary>
    public string? ContinuedFrom { get; set; }

    /// <summary>合成时检测到的分段缺口数（0 = 时间连续）。&gt;0 表示这段画面中间会跳。</summary>
    public int GapCount { get; set; }

    /// <summary>触发这段录像的事件 topic（去重）。空数组 = 老索引或记录时没取到。</summary>
    public List<string> Topics { get; set; } = new();

    public DateTime StartLocal => StartUtc.ToLocalTime();
    public DateTime EndLocal => EndUtc.ToLocalTime();
    public string DateKey => StartLocal.ToString("yyyy-MM-dd");
}

public sealed class DailyIndex
{
    public int Version { get; set; } = 1;
    public string CameraId { get; set; } = "";
    public string Date { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<ClipInfo> Clips { get; set; } = new();
}

public sealed record DaySummary(string Date, int Count, int TotalSeconds, string FirstTime, string LastTime);

public sealed record CameraInfo(string Id, string Name, bool Enabled);

public sealed record DeleteOldResult(int Deleted, int Failed, long FreedBytes);
