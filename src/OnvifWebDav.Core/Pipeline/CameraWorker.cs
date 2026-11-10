using OnvifWebDav.Config;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.Media;
using OnvifWebDav.Onvif;
using OnvifWebDav.Storage;

namespace OnvifWebDav.Pipeline;

/// <summary>
/// 单路摄像头的完整编排：连接 → 订阅事件 → 触发录制 → 合成 → 入队上传，断了自动重连。
/// 两种录制模式共用同一套事件处理，区别只在于 ffmpeg 是常驻还是按需拉起。
/// </summary>
public sealed class CameraWorker : IAsyncDisposable
{
    private readonly CameraConfig _camera;
    private readonly Func<AppConfig> _config;
    private readonly AppLog _log;
    private readonly FfmpegLocator _ffmpeg;
    private readonly LocalSpool _spool;
    private readonly UploadQueue _upload;
    private readonly AppStatus _status;
    private readonly ClipComposer _composer;
    private readonly NotifyHub? _notifyHub;
    private readonly Func<string?>? _consumerUrlProvider;

    private CancellationTokenSource? _cts;
    private Task? _runTask;

    /// <summary>环形缓冲的常驻录制进程，或触发式录制里"最新那个"进程（心跳/状态用它）。</summary>
    private FfmpegProcess? _recorder;

    /// <summary>
    /// 触发式录制的进程按 jobId 分开存。
    /// 单个字段不够用：上一段在收尾（'q' 之后要几秒才退出）时下一段已经开始，两个进程会同时存在。
    /// 用单字段的话，上一段的收尾会把新进程停掉 —— 新 MP4 没写 moov，直接变成放不了的文件。
    /// </summary>
    private readonly Dictionary<string, FfmpegProcess> _triggerRecorders = new();

    private SegmentRing? _ring;

    /// <summary>环形目录只在本次进程第一次启动时清空。窗口是分钟级，每次重连都清会整段丢掉。</summary>
    private bool _ringCleaned;

    private string _rtsp = "";

    private readonly object _gate = new();
    private JobRecord? _job;
    private DateTime _jobStartLocal;
    private DateTime _lastMotionLocal;
    private bool _finalizing;
    private volatile bool _paused;

    /// <summary>上一段真正结束（endLocal）的时刻。只用来发现"新片段回看进了上一段"。</summary>
    private DateTime? _lastFinalizedEndLocal;

    /// <summary>环形缓冲超上限告警的节流时间。</summary>
    private DateTime _lastRingWarnLocal = DateTime.MinValue;

    /// <summary>GOP 过长只在本次会话里提示一次。</summary>
    private bool _gopWarned;

    public CameraWorker(CameraConfig camera, Func<AppConfig> config, AppLog log, FfmpegLocator ffmpeg,
        LocalSpool spool, UploadQueue upload, AppStatus status,
        NotifyHub? notifyHub = null, Func<string?>? consumerUrlProvider = null)
    {
        _notifyHub = notifyHub;
        _consumerUrlProvider = consumerUrlProvider;
        _camera = camera;
        _config = config;
        _log = log;
        _ffmpeg = ffmpeg;
        _spool = spool;
        _upload = upload;
        _status = status;
        _composer = new ClipComposer(ffmpeg, log);
    }

    private RecordingConfig Rec => _config().Recording;

    public bool IsRunning => _runTask is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;

        if (!_ffmpeg.IsAvailable)
        {
            _status.State = WorkerState.Error;
            _status.LastError = "找不到 ffmpeg";
            _status.Message = _ffmpeg.MissingMessage;
            _log.Error("worker", _ffmpeg.MissingMessage);
            return;
        }

        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Pause()
    {
        _paused = true;
        _status.State = WorkerState.Paused;
        _status.Message = "已暂停（不再触发录制）";
        _log.Info("worker", "用户暂停录制");
    }

    public void Resume()
    {
        _paused = false;
        _status.Message = "已恢复";
        _log.Info("worker", "用户恢复录制");
    }

    public bool IsPaused => _paused;

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            OnvifCamera? camera = null;
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sessionToken = sessionCts.Token;

            try
            {
                _status.State = WorkerState.Connecting;
                _status.Message = $"正在连接 {_camera.Host} …";
                _status.LastError = null;

                camera = await OnvifCamera.ConnectAsync(_camera, _log, sessionToken).ConfigureAwait(false);
                _rtsp = await camera.GetRtspUrlAsync(sessionToken).ConfigureAwait(false);

                var probe = await _composer.ProbeStreamAsync(_rtsp, sessionToken).ConfigureAwait(false);
                if (probe is not null)
                {
                    _status.Codec = $"{probe.VideoCodec} {probe.Width}x{probe.Height}";
                    _log.Info("worker", $"码流：{_status.Codec} 音频={probe.HasAudio}");

                    if (probe.IsHevc && !Rec.AutoTranscodeHevc && !Rec.TranscodeToH264)
                        _log.Warn("worker",
                            "主码流是 H.265，浏览器多半放不了。请在摄像头网页把编码改成 H.264，" +
                            "或在设置里打开「H.265 自动转码」。");
                }
                else
                {
                    _status.Codec = "未知（ffprobe 没取到）";
                }

                if (Rec.Mode == RecordingMode.RingBuffer)
                    StartRingRecorder();

                attempt = 0;
                _status.State = WorkerState.Watching;
                _status.Message = Rec.Mode == RecordingMode.RingBuffer
                    ? $"监听中（环形缓冲 {Rec.PreRollSeconds}s 预录）"
                    : "监听中（触发式录制，无预录）";

                using var tickerCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
                var ticker = Task.Run(() => TickerAsync(sessionCts, tickerCts.Token), tickerCts.Token);

                try
                {
                    var consumerUrl = _consumerUrlProvider?.Invoke();
                    var built = await MotionSourceFactory.CreateAsync(_camera, camera, _notifyHub, consumerUrl,
                        _log, sessionToken).ConfigureAwait(false);

                    _status.EventMode = built.ModeName;
                    _status.Message = $"已订阅（{built.ModeName}）";

                    await using var source = built.Source;
                    source.MotionStarted += OnMotionStarted;
                    source.MotionEnded += OnMotionEnded;

                    await source.RunAsync(sessionToken).ConfigureAwait(false);
                }
                finally
                {
                    tickerCts.Cancel();
                    try { await ticker.ConfigureAwait(false); } catch { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // 会话自身被取消（看门狗判定卡死），走重连
                _log.Warn("worker", "会话被看门狗中断，准备重连");
            }
            catch (Exception ex) when (ex is OnvifException { Message: var m } && m.Contains("取消", StringComparison.Ordinal))
            {
                // 停止过程中的取消噪音，不刷 ERROR
                _log.Info("worker", "会话已取消");
            }
            catch (Exception ex)
            {
                _status.LastError = ex.Message;
                _status.State = WorkerState.Error;
                _status.Message = "连接异常：" + ex.Message;
                _log.Error("worker", "会话异常", ex);
            }
            finally
            {
                await StopRecorderAsync().ConfigureAwait(false);
                camera?.Dispose();
                await AbortSessionAsync().ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested) break;

            var delaySeconds = attempt switch { 0 => 5, 1 => 15, 2 => 60, _ => 180 };
            attempt++;
            _status.Message = $"{delaySeconds} 秒后重连摄像头…";
            _log.Info("worker", $"摄像头会话结束，{delaySeconds}s 后重连");

            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        _status.State = WorkerState.Stopped;
        _status.Message = "已停止";
        _status.FfmpegAlive = false;
    }

    // ---------------- 录制管线 ----------------

    private void StartRingRecorder()
    {
        _ring = new SegmentRing(_spool.RingDir(_camera.Id), Rec.SegmentSeconds);

        // 注意：这里**不能**每个会话都 CleanAll。
        // 环形窗口默认 10 分钟，摄像头一次 RTSP 抖动就会走这里的重连分支，
        // 每次都清空等于把整段缓冲丢掉 —— 崩溃残留交给 AppRuntime 启动时的 CleanRing()。
        if (!_ringCleaned)
        {
            _ring.CleanAll();
            _ringCleaned = true;
            _log.Info("rec", $"环形缓冲初始化（清空上次残留，保留窗口 {Rec.EffectiveRingKeepSeconds}s）");
        }

        var pattern = Path.Combine(_ring.DirPath, "%Y%m%d_%H%M%S.ts");
        var args = FfmpegArgs.RingRecord(_rtsp, pattern, Rec.SegmentSeconds, Rec.RecordAudio);

        _recorder = new FfmpegProcess();
        _recorder.StdErrLine += line => _log.Debug("ffmpeg", line);
        _recorder.Start(_ffmpeg.FfmpegPath!, args, _spool.Root);

        _status.FfmpegAlive = true;
        _log.Info("rec",
            $"环形缓冲录制已启动（段长 {Rec.SegmentSeconds}s，保留 {Rec.EffectiveRingKeepSeconds}s，" +
            $"上限 {Rec.RingLimitMb}MB，目录 {_ring.DirPath}）");
    }

    private void EnsureTriggerRecording()
    {
        if (Rec.Mode != RecordingMode.TriggerOnly) return;

        JobRecord? job;
        lock (_gate) job = _job;
        if (job is null) return;

        // 只看"这个片段自己"有没有进程。别的片段的进程还活着不关这一段的事，
        // 它有自己的收尾流程，会用自己的 jobId 去停。
        lock (_gate)
        {
            if (_triggerRecorders.TryGetValue(job.Id, out var existing) && existing.IsAlive) return;
        }

        // 触发式录制是直接写 MP4 的：如果码流是 H.265，不转码就等于生成一个网页放不了的文件
        var hevc = _status.Codec.Contains("hevc", StringComparison.OrdinalIgnoreCase);
        var forceH264 = Rec.TranscodeToH264 || (Rec.AutoTranscodeHevc && hevc);

        var args = FfmpegArgs.TriggerRecord(_rtsp, job.ClipFile, Rec.RecordAudio, forceH264);

        var proc = new FfmpegProcess();
        proc.StdErrLine += line => _log.Debug("ffmpeg", line);
        proc.Start(_ffmpeg.FfmpegPath!, args, job.Dir);

        lock (_gate)
        {
            _triggerRecorders[job.Id] = proc;
            _recorder = proc;
        }

        _status.FfmpegAlive = true;
        _log.Info("rec", $"触发式录制已启动（{job.Id}）");
    }

    /// <summary>停掉某个片段自己的录制进程。别的片段的进程绝不碰。</summary>
    private async Task StopTriggerRecorderAsync(string jobId)
    {
        FfmpegProcess? proc;
        lock (_gate)
        {
            if (!_triggerRecorders.Remove(jobId, out proc)) return;
            if (ReferenceEquals(_recorder, proc)) _recorder = null;
        }

        try { await proc.StopGracefullyAsync().ConfigureAwait(false); }
        catch { }
        finally
        {
            try { await proc.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    private async Task StopRecorderAsync()
    {
        List<FfmpegProcess> leftovers;

        lock (_gate)
        {
            leftovers = _triggerRecorders.Values.ToList();
            _triggerRecorders.Clear();

            if (_recorder is not null) leftovers.Add(_recorder);
            _recorder = null;
        }

        foreach (var proc in leftovers.Distinct())
        {
            try { await proc.StopGracefullyAsync().ConfigureAwait(false); }
            catch { }
            finally
            {
                try { await proc.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }

        _status.FfmpegAlive = false;
    }

    // ---------------- 事件处理 ----------------

    private void OnMotionStarted(OnvifMotionEvent e)
    {
        var local = e.UtcTime.ToLocalTime();
        var now = DateTime.Now;

        // 事件时间可能因为设备时钟偏差跑到未来或过去，夹一下
        if (local > now.AddSeconds(5)) local = now;
        if (local < now.AddMinutes(-2)) local = now;

        _status.LastMotion = local;
        _status.EventCount++;

        // 事件类型过滤：不匹配的只记状态，不建片段、也不延长正在录的片段。
        if (!TopicAllowed(e.Topic))
        {
            _log.Debug("rec", $"事件被过滤（{e.Topic}）");
            return;
        }

        _lastMotionLocal = local;
        _status.State = WorkerState.Recording;

        lock (_gate)
        {
            if (_job is null)
            {
                var preRoll = Rec.Mode == RecordingMode.RingBuffer ? Math.Max(0, Rec.PreRollSeconds) : 0;
                _jobStartLocal = local.AddSeconds(-preRoll);
                _job = _spool.CreateJob(_camera.Id, _jobStartLocal.ToUniversalTime(), local.ToUniversalTime());
                _job.EventCount = 0;
                _job.PeakMotion = 1.0;

                // 保护令牌挂在 job 上，不挂在 worker 字段上：
                // 上一段还在合成时这里就会新建下一段，两份保护必须互不干扰。
                _job.RingToken = _ring?.ProtectFrom(_jobStartLocal) ?? 0;

                _log.Info("rec", $"运动开始，新建片段（预录 {preRoll}s）{TopicSuffix(e.Topic)}");

                // 上一段刚结束就来了新事件时，预录会把起点拉回上一段的窗口里（重复几秒内容）。
                // 这不是错误，但"连续两段内容重叠"很容易被当成 bug，所以留一条痕。
                if (_lastFinalizedEndLocal is { } lastEnd && _jobStartLocal < lastEnd)
                    _log.Info("rec",
                        $"新片段起点 {_jobStartLocal:HH:mm:ss} 早于上一段结束 {lastEnd:HH:mm:ss}，" +
                        $"重叠 {(lastEnd - _jobStartLocal).TotalSeconds:F0}s（预录回看导致，属正常）");
            }

            _job.EventCount++;

            if (!string.IsNullOrWhiteSpace(e.Topic) && !_job.Topics.Contains(e.Topic))
                _job.Topics.Add(e.Topic);
        }

        EnsureTriggerRecording();
    }

    private void OnMotionEnded(OnvifMotionEvent e)
    {
        var local = e.UtcTime.ToLocalTime();
        if (local > DateTime.Now.AddSeconds(5)) local = DateTime.Now;
        if (local > _lastMotionLocal) _lastMotionLocal = local;
    }

    /// <summary>
    /// 事件 topic 过滤。配置为空 = 全部放行，否则按逗号/分号分隔的子串匹配（不区分大小写）。
    /// 典型用法：只留人形事件 → 填 PersonDetector。
    /// </summary>
    private bool TopicAllowed(string topic)
    {
        var filter = Rec.TopicFilter;
        if (string.IsNullOrWhiteSpace(filter)) return true;
        if (string.IsNullOrEmpty(topic)) return false;

        foreach (var raw in filter.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            if (part.Length > 0 && topic.Contains(part, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string TopicSuffix(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return "";

        // topic 很长（tns1:RuleEngine/CellMotionDetector/Motion），日志里只留最后两段
        var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "｜" + (parts.Length <= 2 ? topic : string.Join('/', parts[^2..]));
    }

    private async Task TickerAsync(CancellationTokenSource session, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                // ffmpeg 静默卡死检测（进程活着但不出帧是 RTSP 最常见的无声故障）
                if (_recorder is { } proc)
                {
                    if (!proc.IsAlive)
                    {
                        _log.Warn("rec", "ffmpeg 已退出，取消会话并重连");
                        _status.FfmpegAlive = false;
                        CancelQuietly(session);
                        return;
                    }

                    if (proc.HeartbeatAge > TimeSpan.FromSeconds(20))
                    {
                        _log.Warn("rec", $"ffmpeg 超过 {proc.HeartbeatAge.TotalSeconds:F0}s 无心跳，判定卡死，重启");
                        CancelQuietly(session);
                        return;
                    }
                }

                if (_paused) continue;

                TrimRing();
                await EvaluateSessionAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// 按时间淘汰 + 按字节兜底，并把占用报到状态里。
    /// 字节上限会被「所在卷的可用空间」再压一层：内存盘只有 1GB 时，
    /// 配置里写 2048MB 也不会硬撑到把盘塞满（塞满的表现是 ffmpeg 写失败退出，很难排查）。
    /// </summary>
    private void TrimRing()
    {
        if (_ring is null) return;

        var current = _ring.TotalBytes();
        var configured = Rec.RingLimitMb > 0 ? (long)Rec.RingLimitMb * 1024 * 1024 : 0L;
        var limitBytes = configured;

        var free = _spool.RingFreeBytes();
        if (free >= 0)
        {
            // 剩余 + 当前占用 = 这个卷能给环形的总预算，留 10% 余量给文件系统开销
            var budget = (long)((free + current) * 0.9);
            limitBytes = limitBytes > 0 ? Math.Min(limitBytes, budget) : budget;

            if (configured > 0 && budget < configured * 0.9 &&
                DateTime.Now - _lastRingWarnLocal > TimeSpan.FromMinutes(10))
            {
                _lastRingWarnLocal = DateTime.Now;
                _log.Warn("rec",
                    $"环形目录 {_ring.DirPath} 空间不足，按 {budget / 1024 / 1024}MB 保留" +
                    $"（配置 {Rec.RingLimitMb}MB）。调小「环形保留」或换更大的盘。");
            }
        }

        _ring.Trim(Rec.EffectiveRingKeepSeconds, limitBytes);

        var bytes = _ring.TotalBytes();
        _status.RingMb = Math.Round(bytes / 1024.0 / 1024.0, 1);
        _status.RingPath = _ring.DirPath;

        // 受保护的段加起来就超上限时删不动，只能告警（说明预录/静默配得太大）
        if (limitBytes > 0 && bytes > limitBytes * 1.1 &&
            DateTime.Now - _lastRingWarnLocal > TimeSpan.FromMinutes(5))
        {
            _lastRingWarnLocal = DateTime.Now;
            _log.Warn("rec",
                $"环形缓冲 {bytes / 1024 / 1024}MB 超过上限 {limitBytes / 1024 / 1024}MB 且删不动" +
                "（正在录/正在合成的段受保护）。请调小预录或调大上限。");
        }
    }

    private async Task EvaluateSessionAsync(CancellationToken ct)
    {
        JobRecord? job;
        DateTime startLocal, endLocal;
        bool forceSplit;

        lock (_gate)
        {
            if (_job is null || _finalizing) return;

            var now = DateTime.Now;
            var silence = (now - _lastMotionLocal).TotalSeconds;
            var duration = (now - _jobStartLocal).TotalSeconds;

            // 尾部 = 静默 + 片尾余量。等够这么久才收尾，"人走出画面"和事件延迟才都在里面。
            var tail = Rec.TailSeconds;

            var tooLong = duration >= Rec.MaxSeconds;
            var quiet = silence >= tail;

            if (!tooLong && !quiet) return;

            job = _job;
            _job = null;
            _finalizing = true;
            forceSplit = tooLong;

            // 起点必须在锁内捕获成局部变量并一路传下去。
            // 之前用的是 _jobStartLocal 字段，合成要花 1~30 秒，这期间新事件会把字段覆盖成下一段的起点，
            // Collect 就会用错误窗口切段 —— 头部被削、甚至切出空列表把整段丢掉。
            startLocal = _jobStartLocal;

            endLocal = _lastMotionLocal.AddSeconds(tail);
            var minEnd = startLocal.AddSeconds(Rec.MinSeconds);
            if (endLocal < minEnd) endLocal = minEnd;
            if (endLocal > now) endLocal = now;

            _lastFinalizedEndLocal = endLocal;
        }

        try
        {
            await FinalizeAsync(job, startLocal, endLocal, forceSplit, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _finalizing = false;
        }
    }

    private async Task FinalizeAsync(JobRecord job, DateTime startLocal, DateTime endLocal, bool forceSplit,
        CancellationToken ct)
    {
        _status.State = WorkerState.Finalizing;
        _status.Message = "正在合成录像…";
        _log.Info("rec", $"片段结束：{startLocal:HH:mm:ss} → {endLocal:HH:mm:ss}，" +
                         $"环形保护 {_ring?.ActiveProtections ?? 0} 个" +
                         (forceSplit ? "（超时强制切片）" : ""));

        try
        {
            if (Rec.Mode == RecordingMode.RingBuffer)
            {
                // 实际段长由摄像头 GOP 决定，可能远大于配置的段长（线上是 12 秒 vs 配置 2 秒）。
                // 等待超时、连续性判定、残片判定都必须按它来算。
                var observed = _ring?.ObservedSegmentSeconds() ?? Rec.SegmentSeconds;
                if (!_gopWarned && observed > Rec.SegmentSeconds * 2)
                {
                    _gopWarned = true;
                    _log.Warn("rec",
                        $"实际分段长度 {observed}s，远大于配置的 {Rec.SegmentSeconds}s —— " +
                        "摄像头 I 帧间隔（GOP）太长，切片只能落在关键帧上。" +
                        "建议到摄像头网页把 I 帧间隔调到 1~2 秒（否则预录精度会明显变差）。");
                }

                // 等"包含 endLocal 的那一段"写完：判据是已出现起点更晚的分段。
                // 旧写法要求"完整段起点 >= endLocal"，段长 12 秒时要多等一整段才成立，超时就直接放弃了。
                var deadline = DateTime.Now.AddSeconds(observed * 2 + 15);
                while (DateTime.Now < deadline)
                {
                    if (_ring is null || _ring.IsSegmentClosed(endLocal)) break;
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }

                var windowSec = Math.Max(0, (endLocal - startLocal).TotalSeconds);
                var segments = _ring?.Collect(startLocal, endLocal) ?? new List<RingSegment>();

                if (segments.Count == 0)
                {
                    _log.Warn("rec",
                        $"窗口 {startLocal:HH:mm:ss}–{endLocal:HH:mm:ss} 内没有可用分段，放弃这个片段");
                    _spool.DeleteJob(job);
                    return;
                }

                // 只剩一两段而窗口很长 = 残片。阈值按真实段长算：
                // 段长 12 秒时"1 段"是正常的，不能当残片丢掉。
                if (segments.Count <= 1 && windowSec >= observed * 2.5)
                {
                    _log.Error("rec",
                        $"窗口 {windowSec:F0}s 只收到 {segments.Count} 段" +
                        $"（按 {observed}s/段 期望约 {ExpectedSegments(windowSec, observed)} 段），" +
                        "判定为残片，放弃这个片段");
                    _spool.DeleteJob(job);
                    return;
                }

                CheckContinuity(job, segments, startLocal, windowSec, observed);

                var probe = await _composer.ProbeAsync(segments[0].Path, ct).ConfigureAwait(false);
                var transcode = Rec.TranscodeToH264 || (probe?.IsHevc == true && Rec.AutoTranscodeHevc);

                if (probe?.IsHevc == true && transcode)
                    _log.Info("rec", "源是 H.265，按设置转成 H.264（有 CPU 开销）");

                var result = await _composer.ComposeAsync(segments.Select(x => x.Path).ToList(), job.Dir,
                    job.ClipFile, transcode, Rec.RecordAudio, ct).ConfigureAwait(false);

                if (!result.Ok)
                {
                    _log.Error("rec", "合成失败：" + result.Error);
                    _status.LastError = result.Error;
                    _spool.DeleteJob(job);
                    return;
                }

                job.VideoCodec = result.Info?.VideoCodec ?? "";
                job.HasAudio = result.Info?.HasAudio ?? false;
                job.EndUtc = result.Info is { DurationSeconds: > 0.2 } infoDur
                    ? job.StartUtc.AddSeconds(infoDur.DurationSeconds)
                    : endLocal.ToUniversalTime();
                job.SizeBytes = new FileInfo(job.ClipFile).Length;

                await _composer.MakeThumbnailAsync(job.ClipFile, job.ThumbFile, ct).ConfigureAwait(false);
            }
            else
            {
                // 只停这个片段自己的进程：此刻下一段可能已经在录了
                await StopTriggerRecorderAsync(job.Id).ConfigureAwait(false);

                if (!File.Exists(job.ClipFile) || new FileInfo(job.ClipFile).Length == 0)
                {
                    _log.Warn("rec", "触发式录制没产出文件，放弃这个片段");
                    _spool.DeleteJob(job);
                    return;
                }

                var info = await _composer.ProbeAsync(job.ClipFile, ct).ConfigureAwait(false);
                job.VideoCodec = info?.VideoCodec ?? "";
                job.HasAudio = info?.HasAudio ?? false;
                job.EndUtc = info is { DurationSeconds: > 0.2 }
                    ? job.StartUtc.AddSeconds(info.DurationSeconds)
                    : endLocal.ToUniversalTime();
                job.SizeBytes = new FileInfo(job.ClipFile).Length;

                await _composer.MakeThumbnailAsync(job.ClipFile, job.ThumbFile, ct).ConfigureAwait(false);
            }

            job.State = JobState.Pending;
            _spool.SaveJob(job);

            var limit = (long)_config().System.SpoolLimitGb * 1024 * 1024 * 1024;
            if (_spool.EnforceQuota(limit))
            {
                _status.Message = "本地积压超过上限，已暂停录制，请检查 WebDAV 连接";
                _log.Error("spool", "本地积压超过上限，暂停录制");
                _paused = true;
                _spool.DeleteJob(job);
                return;
            }

            _upload.Enqueue(job);
            _status.TodayClips++;
            _status.SpoolBytes = _spool.TotalJobBytes();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("rec", "合成流程异常", ex);
            _status.LastError = ex.Message;
            _spool.DeleteJob(job);
        }
        finally
        {
            // 只释放自己这个令牌。之前是 ReleaseAll()，会把"下一段已经设上的保护"一并清掉，
            // 于是下一段的早期分段被 Trim 删掉，合出来就是没有开头的残片。
            _ring?.Release(job.RingToken);

            if (!_paused)
            {
                _status.State = WorkerState.Watching;
                _status.Message = "监听中";
            }
        }
    }

    private static int ExpectedSegments(double windowSeconds, int segmentSeconds) =>
        Math.Max(1, (int)Math.Round(windowSeconds / Math.Max(1, segmentSeconds)));

    /// <summary>
    /// 分段连续性检查。环形缓冲的 Trim 是唯一会删段的角色，一旦保护判错（或保护被误清），
    /// 就会出现"中间少了几个段"——合成出来能播，但画面会跳。这里只告警不拦，
    /// 因为缺口两边的内容对用户仍然有价值。
    /// </summary>
    private void CheckContinuity(JobRecord job, IReadOnlyList<RingSegment> segments, DateTime startLocal,
        double windowSec, int observedSegmentSeconds)
    {
        // 缺口阈值必须按真实段长算：段长 12 秒时，间隔 12.x 秒是正常的，不是缺口
        var gapLimit = TimeSpan.FromSeconds(Math.Max(observedSegmentSeconds * 1.5, 12));
        var gaps = 0;
        var worstGap = TimeSpan.Zero;

        for (var i = 1; i < segments.Count; i++)
        {
            var gap = segments[i].StartLocal - segments[i - 1].StartLocal;
            if (gap <= gapLimit) continue;
            gaps++;
            if (gap > worstGap) worstGap = gap;
        }

        var headLostSec = (segments[0].StartLocal - startLocal).TotalSeconds;
        var headLost = headLostSec > Math.Max(observedSegmentSeconds * 2.5, 10);

        if (gaps == 0 && !headLost) return;

        job.GapCount = gaps;
        _log.Warn("rec",
            $"分段不连续：{segments.Count} 段覆盖 {windowSec:F0}s" +
            $"（按 {observedSegmentSeconds}s/段 期望约 {ExpectedSegments(windowSec, observedSegmentSeconds)} 段）" +
            (headLost ? $"，头部缺 {headLostSec:F0}s" : "") +
            (gaps > 0 ? $"，中间缺 {gaps} 处（最大 {worstGap.TotalSeconds:F0}s）" : "") +
            "；合成结果会跳帧。若频繁出现，检查环形缓冲的 Trim/保护逻辑或摄像头断流。");
    }

    /// <summary>会话结束时收尾：把正在录的片段尽量保存下来，别直接丢。</summary>
    private async Task AbortSessionAsync()
    {
        JobRecord? job;
        DateTime startLocal, endLocal;

        lock (_gate)
        {
            job = _job;
            _job = null;
            startLocal = _jobStartLocal;
            endLocal = DateTime.Now;
        }

        if (job is null) return;

        _log.Warn("rec", "会话中断，尝试保存已录到的部分");
        try
        {
            await FinalizeAsync(job, startLocal, endLocal, false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("rec", "中断收尾失败：" + ex.Message);
            _spool.DeleteJob(job);
        }
    }

    private static void CancelQuietly(CancellationTokenSource cts)
    {
        try { cts.Cancel(); } catch { }
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }

        if (_runTask is not null)
        {
            try { await _runTask.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false); }
            catch { }
        }

        await StopRecorderAsync().ConfigureAwait(false);
        _status.State = WorkerState.Stopped;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}
