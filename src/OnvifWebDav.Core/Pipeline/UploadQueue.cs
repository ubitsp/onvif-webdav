using OnvifWebDav.Config;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.Storage;
using OnvifWebDav.WebDav;

namespace OnvifWebDav.Pipeline;

/// <summary>
/// 上传队列。目录即队列（spool/jobs），进程重启后自动恢复。
/// 顺序必须是：视频 → 缩略图 → 索引（索引是"文件完整"的唯一权威标记）。
/// </summary>
public sealed class UploadQueue : IAsyncDisposable
{
    private static readonly int[] BackoffSeconds = { 5, 15, 60, 300, 900, 1800 };
    private const int MaxAttempts = 12;

    private readonly LocalSpool _spool;
    private readonly Func<WebDavClient?> _davFactory;
    private readonly AppLog _log;
    private readonly AppStatus _status;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workers = new();
    private readonly object _gate = new();
    private readonly Func<bool?>? _moveSupportProvider;
    private readonly QuotaMonitor? _quota;
    private readonly Func<StorageQuotaConfig>? _quotaConfigProvider;

    private bool? _supportsMove;
    private bool _pausedByQuota;

    /// <summary>
    /// 本进程正在上传的任务 id。磁盘上的 Uploading 状态不足以判断"谁在传"，
    /// 必须用进程内集合兜住，否则两个 worker 会抢同一个任务重复上传。
    /// </summary>
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _quotaRetryUtc = DateTime.MinValue;
    private DateTime _lastQuotaLogUtc = DateTime.MinValue;

    public int Parallelism { get; init; } = 2;

    public UploadQueue(LocalSpool spool, Func<WebDavClient?> davFactory, AppLog log, AppStatus status,
        Func<bool?>? moveSupportProvider = null,
        QuotaMonitor? quota = null,
        Func<StorageQuotaConfig>? quotaConfigProvider = null)
    {
        _spool = spool;
        _davFactory = davFactory;
        _log = log;
        _status = status;
        _moveSupportProvider = moveSupportProvider;
        _quota = quota;
        _quotaConfigProvider = quotaConfigProvider;
    }

    /// <summary>StopAsync 之后不能再 Start，需要换一个新实例（AppRuntime 负责）。</summary>
    public bool IsStopped => _cts.IsCancellationRequested;

    public void Start()
    {
        // 用上次「测试 WebDAV」的结论做初值：不支持 MOVE 的服务端（如阿里云盘）就直接 PUT 正式名，
        // 省掉一次完整的重复上传（视频几十 MB，双倍流量不划算）。
        if (_supportsMove is null && _moveSupportProvider?.Invoke() is { } known)
        {
            _supportsMove = known;
            if (!known) _log.Info("upload", "已知服务端不支持 MOVE，直接 PUT 正式文件名");
        }

        // 崩溃恢复只在这里做一次：把上次进程崩在"上传中"的任务收回队列。
        var recovered = _spool.RecoverInterruptedUploads();
        if (recovered > 0) _log.Info("upload", $"重置 {recovered} 个上次中断的上传任务");

        var pending = _spool.LoadJobs();
        _log.Info("upload", $"启动上传队列，恢复 {pending.Count} 个待上传任务");

        for (var i = 0; i < Math.Max(1, Parallelism); i++)
            _workers.Add(Task.Run(() => WorkerLoopAsync(_cts.Token)));

        if (pending.Count > 0) Signal();
        _status.QueueCount = pending.Count;
    }

    public void Signal()
    {
        try { _signal.Release(); } catch { }
    }

    public void Enqueue(JobRecord job)
    {
        job.State = JobState.Pending;
        job.Attempts = 0;
        _spool.SaveJob(job);
        _status.QueueCount = CountPending();
        Signal();
        _log.Info("upload", $"排队等待上传：{job.Id}");
    }

    private int CountPending() => _spool.LoadJobs().Count(j => j.State is JobState.Pending or JobState.Uploading);

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            // 因云端空间不足暂停时，等重试时间到再继续，避免每 30 秒空转一次
            if (_pausedByQuota && DateTime.UtcNow < _quotaRetryUtc) continue;

            JobRecord? job;
            lock (_gate)
            {
                // 跳过本进程已经在传的任务 —— 只按 State 判断会在并发下重复认领
                job = _spool.LoadJobs()
                    .FirstOrDefault(j => j.State == JobState.Pending && !_inFlight.Contains(j.Id));

                if (job is not null)
                {
                    // 必须在锁内认领并落盘，否则并发 worker 会抢同一个任务重复上传
                    job.State = JobState.Uploading;
                    _spool.SaveJob(job);
                    _inFlight.Add(job.Id);
                }
            }

            if (job is null) continue;

            try
            {
                await ProcessAsync(job, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Error("upload", $"上传任务 {job.Id} 异常", ex);
                ScheduleRetry(job, ex.Message);
            }
            finally
            {
                lock (_gate) _inFlight.Remove(job.Id);
            }

            var remaining = CountPending();
            _status.QueueCount = remaining;

            // 上传成功会把整个目录删掉。此时如果还有排队任务，补一个信号，
            // 否则要多等 30 秒（WaitAsync 的超时）才会去取下一个 ——
            // 重启后恢复一堆任务时会表现为"一个 30 秒"。
            // 失败/重试的任务目录还在，不发信号，避免把退避时间跳过。
            if (remaining > 0 && !Directory.Exists(job.Dir)) Signal();
        }
    }

    private async Task ProcessAsync(JobRecord job, CancellationToken ct)
    {
        var dav = _davFactory();
        if (dav is null)
        {
            _log.Warn("upload", "WebDAV 未配置，任务保留在本地等待");
            await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            return;
        }

        job.State = JobState.Uploading;
        _spool.SaveJob(job);
        _status.State = WorkerState.Uploading;

        // 片段文件不在了 = 这个任务已经没法传了（被别的 worker 传完后删掉 / 被清理 / 人为删除）。
        // 重试没有意义，重试 12 次只会刷屏。
        if (!File.Exists(job.ClipFile))
        {
            _log.Error("upload", $"任务 {job.Id} 的片段文件已丢失（{job.ClipFile}），丢弃这个任务");
            _spool.DeleteJob(job);
            _status.QueueCount = CountPending();
            return;
        }

        // 云端空间不够就别硬传 —— 传一半失败比不传更糟
        if (!await EnsureSpaceAsync(dav, job, ct).ConfigureAwait(false))
        {
            job.State = JobState.Pending;
            _spool.SaveJob(job);
            _pausedByQuota = true;
            _quotaRetryUtc = DateTime.UtcNow.AddMinutes(5);
            _status.QueueCount = CountPending();
            return;
        }

        _pausedByQuota = false;

        var date = job.StartUtc.ToLocalTime().Date;
        var clipsDir = RemoteClipIndex.ClipsDir(job.CameraId, date);
        var thumbsDir = RemoteClipIndex.ThumbsDir(job.CameraId, date);

        var suffix = job.Id.Length >= 6 ? job.Id[^6..] : Guid.NewGuid().ToString("N")[..6];
        var stem = $"{job.StartUtc.ToLocalTime():HHmmss}_{suffix}";
        var remoteClip = $"{clipsDir}/{stem}.mp4";
        var remoteThumb = $"{thumbsDir}/{stem}.jpg";

        // 1) 视频：先传 .part，校验长度后 MOVE 成正式名（MOVE 不支持就直接传正式名）
        var uploaded = await UploadWithPartAsync(dav, remoteClip, job.ClipFile, ct).ConfigureAwait(false);

        // 2) 缩略图（失败不算致命，网页会显示灰块）
        var hasThumb = false;
        if (File.Exists(job.ThumbFile))
        {
            try
            {
                await dav.PutFileAsync(remoteThumb, job.ThumbFile, null, ct).ConfigureAwait(false);
                hasThumb = true;
            }
            catch (Exception ex)
            {
                _log.Warn("upload", $"缩略图上传失败（忽略）：{ex.Message}");
            }
        }

        // 3) 索引最后写
        var clip = new ClipInfo
        {
            Id = stem,
            CameraId = job.CameraId,
            StartUtc = job.StartUtc,
            EndUtc = job.EndUtc,
            DurationMs = (int)Math.Max(0, (job.EndUtc - job.StartUtc).TotalMilliseconds),
            SizeBytes = new FileInfo(job.ClipFile).Length,
            Path = remoteClip,
            Thumb = hasThumb ? remoteThumb : "",
            VideoCodec = job.VideoCodec,
            HasAudio = job.HasAudio,
            PeakMotion = job.PeakMotion,
            EventCount = job.EventCount,
            ContinuedFrom = job.ContinuedFrom,
            GapCount = job.GapCount,
            Topics = job.Topics.ToList(),
        };

        var index = new RemoteClipIndex(dav, _log);
        var indexed = await index.AddAsync(clip, ct).ConfigureAwait(false);

        if (!indexed)
            _log.Warn("upload", $"{clip.Id} 已上传但索引未写入，稍后由重建任务补上");

        // 4) 只存 WebDAV：本地立刻删
        _spool.DeleteJob(job);

        job.State = JobState.Uploaded;
        _status.LastUpload = DateTime.Now;
        _status.TodayClips++;
        _status.State = WorkerState.Watching;
        _log.Info("upload", $"上传完成：{clip.Id}（{clip.DurationMs / 1000}s，{clip.SizeBytes / 1024 / 1024.0:F1}MB）");
    }

    /// <summary>
    /// 上传前的配额守卫：够就放行；低于告警线只提示；低于暂停线就停传（可选先自动删最旧的）。
    /// 服务端不返回配额时一律放行，不能因为查不到就把功能卡死。
    /// </summary>
    private async Task<bool> EnsureSpaceAsync(WebDavClient dav, JobRecord job, CancellationToken ct)
    {
        var cfg = _quotaConfigProvider?.Invoke();
        if (cfg is null || !cfg.Enabled || _quota is null) return true;

        if (_quota.IsStale(cfg.ProbeMinutes))
            _quota.Set(await dav.GetQuotaAsync(ct).ConfigureAwait(false));

        var current = _quota.Last;
        _status.Quota = current;

        if (current?.AvailableBytes is not { } available) return true;

        var pauseThreshold = QuotaMonitor.GbToBytes(cfg.PauseBelowGb);
        var warnThreshold = QuotaMonitor.GbToBytes(cfg.WarnBelowGb);

        if (cfg.PauseBelowGb > 0 && available < pauseThreshold)
        {
            if (cfg.AutoDeleteOldest)
            {
                _log.Warn("quota",
                    $"云端剩余 {current.AvailableGb:F1} GB，低于 {cfg.PauseBelowGb} GB，开始删除最旧录像腾空间");
                _status.Message = "云端空间不足，正在清理最旧录像…";

                var target = QuotaMonitor.GbToBytes(cfg.AutoDeleteTargetFreeGb);
                var result = await new RetentionPruner()
                    .PruneOldestAsync(dav, job.CameraId, target, _log, ct).ConfigureAwait(false);

                _quota.Set(result.After);
                _status.Quota = result.After;

                if (result.After?.AvailableBytes is { } after && after >= pauseThreshold)
                {
                    _log.Info("quota",
                        $"清理完成（删了 {result.ClipsDeleted} 段 / {result.DaysDeleted} 天），" +
                        $"剩余 {result.After!.AvailableGb:F1} GB，继续上传");
                    return true;
                }

                _log.Error("quota", $"清理后空间仍不足（删了 {result.ClipsDeleted} 段），暂停上传");
            }

            var message = $"云端剩余 {current.AvailableGb:F1} GB，低于 {cfg.PauseBelowGb} GB，已暂停上传";

            if (DateTime.UtcNow - _lastQuotaLogUtc > TimeSpan.FromMinutes(10))
            {
                _lastQuotaLogUtc = DateTime.UtcNow;
                _log.Error("quota", message + "（请清理阿里云盘，或打开「自动删除最旧录像」）");
            }

            _status.Message = message;
            _status.LastError = message;
            return false;
        }

        if (available < warnThreshold)
        {
            var message = $"云端剩余 {current.AvailableGb:F1} GB，建议清理旧录像";

            if (DateTime.UtcNow - _lastQuotaLogUtc > TimeSpan.FromMinutes(30))
            {
                _lastQuotaLogUtc = DateTime.UtcNow;
                _log.Warn("quota", message);
            }

            _status.Message = message;
        }

        return true;
    }

    private async Task<bool> UploadWithPartAsync(WebDavClient dav, string remotePath, string localFile,
        CancellationToken ct)
    {
        var size = new FileInfo(localFile).Length;

        if (_supportsMove != false)
        {
            var part = remotePath + ".part";
            try
            {
                await dav.PutFileAsync(part, localFile, null, ct).ConfigureAwait(false);

                var len = await dav.HeadLengthAsync(part, ct).ConfigureAwait(false);
                if (len is null)
                {
                    // 阿里云盘这类后端可能压根不支持 HEAD，PROPFIND 也不给大小。跳过校验，不影响上传。
                    _log.Debug("upload", "服务端不提供文件长度，跳过 .part 长度校验");
                }
                else if (len < size)
                {
                    _log.Warn("upload", $".part 长度不符（本地 {size} / 远端 {len}），仍尝试 MOVE");
                }

                var moved = await dav.MoveAsync(part, remotePath, true, ct).ConfigureAwait(false);
                if (moved)
                {
                    _supportsMove = true;
                    return true;
                }

                _supportsMove = false;
                _log.Warn("upload", "服务器不支持 MOVE，改为直接 PUT 正式文件名");
                try { await dav.DeleteAsync(part, ct).ConfigureAwait(false); } catch { }
            }
            catch (WebDavException ex) when (ex.Status is 405 or 501 or 403)
            {
                _supportsMove = false;
                _log.Warn("upload", $"MOVE 不被支持（HTTP {ex.Status}），改为直接 PUT");
            }
        }

        await dav.PutFileAsync(remotePath, localFile, null, ct).ConfigureAwait(false);
        return true;
    }

    private void ScheduleRetry(JobRecord job, string error)
    {
        // 本地文件都没了，重试只会一遍遍失败
        if (!File.Exists(job.ClipFile))
        {
            _log.Error("upload", $"任务 {job.Id} 的本地文件已丢失，不再重试");
            _spool.DeleteJob(job);
            return;
        }

        job.Attempts++;
        job.LastError = error;
        job.State = job.Attempts >= MaxAttempts ? JobState.Dead : JobState.Pending;
        _spool.SaveJob(job);

        if (job.State == JobState.Dead)
        {
            _log.Error("upload", $"任务 {job.Id} 连续失败 {job.Attempts} 次，标记为 dead（本地文件保留）");
            return;
        }

        var delay = BackoffSeconds[Math.Min(job.Attempts - 1, BackoffSeconds.Length - 1)];
        _log.Warn("upload", $"任务 {job.Id} 第 {job.Attempts} 次失败：{error}；{delay}s 后重试");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), _cts.Token).ConfigureAwait(false);
                Signal();
            }
            catch { }
        }, _cts.Token);
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        _cts.Cancel();
        try
        {
            await Task.WhenAll(_workers).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        _cts.Dispose();
        _signal.Dispose();
    }
}
