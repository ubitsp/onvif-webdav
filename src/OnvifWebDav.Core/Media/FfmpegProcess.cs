using System.Diagnostics;
using System.Text;

namespace OnvifWebDav.Media;

/// <summary>
/// 长跑型 ffmpeg 进程（环形录制 / 触发录制）的封装。
/// 关键点：
///  - 不能加 -nostdin，否则没法用 'q' 优雅停止（Kill 会把最后一段写坏）
///  - 用 -progress pipe:1 的 out_time= 做心跳；RTSP 静默断流时 ffmpeg 会"活着不出帧"，只能靠心跳发现
///  - 路径含空格/中文，全部走参数列表 + 双引号
/// </summary>
public sealed class FfmpegProcess : IAsyncDisposable
{
    private readonly Process _process = new();
    private readonly StringBuilder _recentErr = new();
    private DateTime _lastHeartbeat = DateTime.UtcNow;
    private readonly object _gate = new();

    public event Action<string>? StdErrLine;
    public event Action<TimeSpan>? Progress;

    public bool IsAlive
    {
        get
        {
            try { return !_process.HasExited; }
            catch { return false; }
        }
    }

    public int ExitCode
    {
        get { try { return _process.HasExited ? _process.ExitCode : -1; } catch { return -1; } }
    }

    /// <summary>距离上一次心跳多久。超过 20s 视为静默卡死。</summary>
    public TimeSpan HeartbeatAge
    {
        get { lock (_gate) return DateTime.UtcNow - _lastHeartbeat; }
    }

    public string RecentStdErr
    {
        get { lock (_gate) return _recentErr.ToString(); }
    }

    public void Start(string exePath, string arguments, string workingDirectory)
    {
        _process.StartInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (_gate)
            {
                _recentErr.AppendLine(e.Data);
                if (_recentErr.Length > 16000) _recentErr.Remove(0, _recentErr.Length - 12000);
            }
            StdErrLine?.Invoke(e.Data);
        };

        _process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            // -progress pipe:1 输出 key=value，其中 out_time_ms= 每 0.5s 一次
            if (e.Data.StartsWith("out_time_ms=", StringComparison.Ordinal) &&
                long.TryParse(e.Data.AsSpan("out_time_ms=".Length), out var ms) && ms >= 0)
            {
                lock (_gate) _lastHeartbeat = DateTime.UtcNow;
                Progress?.Invoke(TimeSpan.FromMilliseconds(ms));
            }
            else if (e.Data.StartsWith("out_time=", StringComparison.Ordinal))
            {
                lock (_gate) _lastHeartbeat = DateTime.UtcNow;
            }
        };

        _process.Start();
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();

        lock (_gate) _lastHeartbeat = DateTime.UtcNow;
    }

    /// <summary>发 'q' 让 ffmpeg 正常收尾；超时再杀进程树。</summary>
    public async Task StopGracefullyAsync(TimeSpan? timeout = null)
    {
        if (!IsAlive) return;

        try
        {
            await _process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
            _process.StandardInput.Close();
        }
        catch { /* 进程可能已经没了 */ }

        var wait = timeout ?? TimeSpan.FromSeconds(5);
        try
        {
            using var cts = new CancellationTokenSource(wait);
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            // 落到下面 Kill
        }

        try { _process.Kill(entireProcessTree: true); } catch { }
    }

    public async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return ExitCode;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopGracefullyAsync().ConfigureAwait(false);
        _process.Dispose();
    }
}

/// <summary>一次性 ffmpeg/ffprobe 调用（合成、缩略图、探测）。</summary>
public static class FfmpegRunner
{
    public sealed record RunResult(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => ExitCode == 0;
    }

    public static async Task<RunResult> RunAsync(string exePath, string arguments, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            },
        };

        p.Start();
        var stdOutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = p.StandardError.ReadToEndAsync(ct);

        try { p.StandardInput.Close(); } catch { }

        var wait = timeout ?? TimeSpan.FromMinutes(3);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(wait);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return new RunResult(-1, "", "执行超时");
        }

        return new RunResult(p.ExitCode, await stdOutTask.ConfigureAwait(false),
            await stdErrTask.ConfigureAwait(false));
    }
}
