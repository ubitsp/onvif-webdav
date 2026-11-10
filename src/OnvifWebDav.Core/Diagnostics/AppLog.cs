using System.Collections.Concurrent;
using System.Text;

namespace OnvifWebDav.Diagnostics;

public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Source, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss.fff} [{Level.ToString().ToUpperInvariant()}] {Source}: {Message}";
}

/// <summary>
/// 文件滚动日志 + 内存环形缓冲。WinExe 没有控制台，出问题只能靠这个看。
/// 目录：%LocalAppData%\OnvifWebDav\logs\app-yyyyMMdd.log
/// </summary>
public sealed class AppLog
{
    private readonly object _gate = new();
    private readonly string _dir;
    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private const int RecentMax = 2000;

    public LogLevel MinLevel { get; set; } = LogLevel.Info;

    /// <summary>新日志产生时触发（可能在任意线程）。UI 需要自己 BeginInvoke。</summary>
    public event Action<LogEntry>? Appended;

    public AppLog(string dataDirectory)
    {
        _dir = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(_dir);
    }

    public IReadOnlyList<LogEntry> Recent => _recent.ToArray();

    public void Debug(string src, string msg) => Write(LogLevel.Debug, src, msg);
    public void Info(string src, string msg) => Write(LogLevel.Info, src, msg);
    public void Warn(string src, string msg) => Write(LogLevel.Warn, src, msg);
    public void Error(string src, string msg, Exception? ex = null) =>
        Write(LogLevel.Error, src, ex is null ? msg : $"{msg} :: {ex.GetType().Name}: {ex.Message}");

    public void Write(LogLevel level, string src, string msg)
    {
        if (level < MinLevel) return;

        var entry = new LogEntry(DateTime.Now, level, src, msg);
        _recent.Enqueue(entry);
        while (_recent.Count > RecentMax) _recent.TryDequeue(out _);

        lock (_gate)
        {
            try
            {
                var file = Path.Combine(_dir, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, entry + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { /* 日志失败不能影响主流程 */ }
        }

        try { Appended?.Invoke(entry); } catch { /* ignore */ }
    }
}
