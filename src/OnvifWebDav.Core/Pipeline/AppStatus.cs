using OnvifWebDav.Diagnostics;
using OnvifWebDav.Storage;
using OnvifWebDav.WebDav;

namespace OnvifWebDav.Pipeline;

public enum WorkerState
{
    Stopped,
    Connecting,
    Watching,
    Recording,
    Finalizing,
    Uploading,
    Paused,
    Error,
}

/// <summary>运行状态。UI 用定时器轮询，不走跨线程事件，省得一堆 BeginInvoke。</summary>
public sealed class AppStatus
{
    private readonly object _gate = new();

    private WorkerState _state = WorkerState.Stopped;
    private string _message = "未启动";
    private string? _lastError;
    private string _codec = "";
    private int _queueCount;
    private long _spoolBytes;
    private DateTime? _lastMotion;
    private DateTime? _lastUpload;
    private int _todayClips;
    private bool _ffmpegAlive;
    private long _eventCount;
    private string _lastTopic = "";
    private string _eventMode = "";
    private double _ringMb;
    private string _ringPath = "";
    private StorageQuota? _quota;

    public WorkerState State { get => _state; set { lock (_gate) _state = value; } }
    public string Message { get => _message; set { lock (_gate) _message = value; } }
    public string? LastError { get => _lastError; set { lock (_gate) _lastError = value; } }
    public string Codec { get => _codec; set { lock (_gate) _codec = value; } }
    public int QueueCount { get => _queueCount; set { lock (_gate) _queueCount = value; } }
    public long SpoolBytes { get => _spoolBytes; set { lock (_gate) _spoolBytes = value; } }
    public DateTime? LastMotion { get => _lastMotion; set { lock (_gate) _lastMotion = value; } }
    public DateTime? LastUpload { get => _lastUpload; set { lock (_gate) _lastUpload = value; } }
    public int TodayClips { get => _todayClips; set { lock (_gate) _todayClips = value; } }
    public bool FfmpegAlive { get => _ffmpegAlive; set { lock (_gate) _ffmpegAlive = value; } }
    public long EventCount { get => _eventCount; set { lock (_gate) _eventCount = value; } }
    public string LastTopic { get => _lastTopic; set { lock (_gate) _lastTopic = value; } }

    /// <summary>当前事件获取方式：BasicNotification（推送）/ PullPoint（轮询）。</summary>
    public string EventMode { get => _eventMode; set { lock (_gate) _eventMode = value; } }

    /// <summary>本地环形缓冲当前占用（MB）。"一直录、按事件切"时用它看磁盘压力。</summary>
    public double RingMb { get => _ringMb; set { lock (_gate) _ringMb = value; } }

    /// <summary>环形缓冲的实际目录（可能被回落成默认目录，界面要能看出来）。</summary>
    public string RingPath { get => _ringPath; set { lock (_gate) _ringPath = value; } }

    /// <summary>最近一次查询到的云端配额（服务端不支持时为 null）。</summary>
    public StorageQuota? Quota { get => _quota; set { lock (_gate) _quota = value; } }

    public string StateText => _state switch
    {
        WorkerState.Stopped => "已停止",
        WorkerState.Connecting => "连接中",
        WorkerState.Watching => "监听中",
        WorkerState.Recording => "录制中",
        WorkerState.Finalizing => "合成中",
        WorkerState.Uploading => "上传中",
        WorkerState.Paused => "已暂停",
        WorkerState.Error => "故障",
        _ => _state.ToString(),
    };
}
