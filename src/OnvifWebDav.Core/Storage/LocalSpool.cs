using System.Text.Json;
using System.Text.Json.Serialization;
using OnvifWebDav.Diagnostics;

namespace OnvifWebDav.Storage;

public enum JobState { Pending, Uploading, Uploaded, Dead }

/// <summary>待上传任务。目录即队列，不引数据库。</summary>
public sealed class JobRecord
{
    public string Id { get; set; } = "";
    public string CameraId { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public int EventCount { get; set; }
    public double PeakMotion { get; set; }
    public string? ContinuedFrom { get; set; }
    public JobState State { get; set; } = JobState.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string VideoCodec { get; set; } = "";
    public bool HasAudio { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>合成时发现的分段缺口数（0 = 时间连续）。写进索引，方便回看时判断画面跳帧的原因。</summary>
    public int GapCount { get; set; }

    /// <summary>触发这段录像的事件 topic（去重）。按事件类型过滤/回查都靠它。</summary>
    public List<string> Topics { get; set; } = new();

    /// <summary>环形缓冲的保护令牌。进程内有效，不能落盘（重启后 ring 是全新的）。</summary>
    [JsonIgnore] public long RingToken { get; set; }

    [JsonIgnore] public string Dir { get; set; } = "";
    [JsonIgnore] public string ClipFile => Path.Combine(Dir, "clip.mp4");
    [JsonIgnore] public string ThumbFile => Path.Combine(Dir, "thumb.jpg");
    [JsonIgnore] public string MetaFile => Path.Combine(Dir, "job.json");
}

/// <summary>
/// 本地临时区：
///   spool/ring/{camId}/   环形缓冲的 TS 段（常驻录制时才有）
///   spool/jobs/{jobId}/   合成好的待上传录像（clip.mp4 + thumb.jpg + job.json）
/// 上传成功后整个目录删掉 —— 需求是"只存 WebDAV"。
/// </summary>
public sealed class LocalSpool
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AppLog? _log;

    public string Root { get; }

    /// <summary>环形缓冲目录。可指向内存盘（RAM disk），切换用 SetRingRoot()。</summary>
    public string RingRoot { get; private set; }

    /// <summary>默认环形目录（数据目录下的 spool\ring），自定义目录不可用时回落到这里。</summary>
    public string RingRootDefault => Path.Combine(Root, "ring");

    /// <summary>当前是否用的是自定义目录（用户填了内存盘路径）。</summary>
    public bool RingUsingCustomDir { get; private set; }

    /// <summary>自定义目录不可用时的原因（界面/日志要给用户看）。</summary>
    public string? RingFallbackReason { get; private set; }

    public string JobsRoot => Path.Combine(Root, "jobs");
    public string CacheRoot => Path.Combine(Root, "cache");

    public LocalSpool(string root, AppLog? log = null, string? ringDirectory = null)
    {
        Root = root;
        _log = log;
        RingRoot = RingRootDefault;

        Directory.CreateDirectory(RingRootDefault);
        Directory.CreateDirectory(JobsRoot);
        Directory.CreateDirectory(CacheRoot);

        SetRingRoot(ringDirectory);
    }

    /// <summary>
    /// 切换环形缓冲目录，返回实际生效的路径。
    /// 配置了但不可用（盘符不存在 / 内存盘还没挂载 / 没权限）时自动回落到默认目录，绝不因此让录像停掉。
    /// </summary>
    public string SetRingRoot(string? configured)
    {
        var target = string.IsNullOrWhiteSpace(configured) ? RingRootDefault : configured.Trim();

        try
        {
            Directory.CreateDirectory(target);

            RingRoot = target;
            RingFallbackReason = null;
            RingUsingCustomDir = !string.Equals(
                Path.GetFullPath(target).TrimEnd('\\'),
                Path.GetFullPath(RingRootDefault).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);

            _log?.Info("spool", $"环形目录：{target}" + (RingUsingCustomDir ? "（自定义，检查是否内存盘）" : ""));
            return target;
        }
        catch (Exception ex)
        {
            RingRoot = RingRootDefault;
            RingUsingCustomDir = false;

            // 盘符不存在时 .NET 抛的是 "Access is denied"，对用户毫无指导意义
            var reason = ex.Message;
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(target));
                if (!string.IsNullOrEmpty(root) && !Directory.Exists(root))
                    reason = $"盘符 {root} 不存在（内存盘没挂载？）";
            }
            catch { }

            RingFallbackReason = reason;

            try { Directory.CreateDirectory(RingRootDefault); } catch { }

            _log?.Error("spool",
                $"环形目录 {target} 不可用（{reason}），已回落到 {RingRootDefault}。" +
                "如果这是内存盘，请确认盘符存在、已挂载。");
            return RingRoot;
        }
    }

    /// <summary>环形目录所在卷的剩余字节；取不到（UNC/权限）返回 -1。</summary>
    public long RingFreeBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(RingRoot));
            if (string.IsNullOrEmpty(root)) return -1;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>环形目录所在卷的总字节；取不到返回 -1。</summary>
    public long RingTotalBytesOnVolume()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(RingRoot));
            if (string.IsNullOrEmpty(root)) return -1;
            return new DriveInfo(root).TotalSize;
        }
        catch
        {
            return -1;
        }
    }

    public string RingDir(string cameraId)
    {
        var dir = Path.Combine(RingRoot, WebDav.WebDavPath.SafeSegment(cameraId));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public JobRecord CreateJob(string cameraId, DateTime startUtc, DateTime endUtc)
    {
        var id = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}";
        var dir = Path.Combine(JobsRoot, id);
        Directory.CreateDirectory(dir);

        return new JobRecord
        {
            Id = id,
            CameraId = cameraId,
            StartUtc = startUtc,
            EndUtc = endUtc,
            Dir = dir,
        };
    }

    public void SaveJob(JobRecord job)
    {
        try
        {
            var json = JsonSerializer.Serialize(job, JsonOpts);
            File.WriteAllText(job.MetaFile, json, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _log?.Warn("spool", $"写 job.json 失败：{ex.Message}");
        }
    }

    public IReadOnlyList<JobRecord> LoadJobs()
    {
        var list = new List<JobRecord>();
        if (!Directory.Exists(JobsRoot)) return list;

        foreach (var dir in Directory.EnumerateDirectories(JobsRoot))
        {
            var meta = Path.Combine(dir, "job.json");
            try
            {
                if (!File.Exists(meta))
                {
                    // 没有 job.json 不代表是垃圾：可能正在合成（目录刚建、还没写元数据）。
                    // 只有超过 1 小时的历史目录才当崩溃残留清掉。
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > TimeSpan.FromHours(1))
                        TryDeleteDir(dir);
                    continue;
                }

                var job = JsonSerializer.Deserialize<JobRecord>(File.ReadAllText(meta), JsonOpts);
                if (job is null) continue;

                job.Dir = dir;

                // 注意：这里**不能**把 Uploading 重置成 Pending。
                // 运行期间磁盘上出现 Uploading 是"某个 worker 正在传"，不是崩溃残留。
                // 重置它会让第二个 worker 抢同一个任务，先完成的删掉本地目录，另一个就 FileNotFoundException。
                // 崩溃恢复只能在启动时做一次 —— 见 RecoverInterruptedUploads()。

                if (!File.Exists(job.ClipFile))
                {
                    TryDeleteDir(dir);
                    continue;
                }

                list.Add(job);
            }
            catch (Exception ex)
            {
                _log?.Warn("spool", $"读取任务失败 {dir}：{ex.Message}");
                TryDeleteDir(dir);
            }
        }

        return list.OrderBy(j => j.CreatedUtc).ToList();
    }

    /// <summary>
    /// 把"上次进程崩在上传中"的任务重置回 Pending。**只能在队列启动时调用一次**。
    /// 运行期间调用会破坏正在进行的上传（见 LoadJobs 里的注释）。
    /// </summary>
    public int RecoverInterruptedUploads()
    {
        var count = 0;

        foreach (var job in LoadJobs())
        {
            if (job.State != JobState.Uploading) continue;

            job.State = JobState.Pending;
            SaveJob(job);
            count++;
        }

        return count;
    }

    public long TotalJobBytes()
    {
        if (!Directory.Exists(JobsRoot)) return 0;
        try
        {
            return Directory.EnumerateFiles(JobsRoot, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>超过配额时先删最旧的 Dead 任务；仍超则返回 true（调用方应暂停录制并告警）。</summary>
    public bool EnforceQuota(long limitBytes)
    {
        if (limitBytes <= 0) return false;
        if (TotalJobBytes() <= limitBytes) return false;

        foreach (var job in LoadJobs().Where(j => j.State == JobState.Dead).OrderBy(j => j.CreatedUtc))
        {
            TryDeleteDir(job.Dir);
            _log?.Warn("spool", $"本地积压超限，已删除失败任务 {job.Id}");
            if (TotalJobBytes() <= limitBytes) return false;
        }

        return TotalJobBytes() > limitBytes;
    }

    public void DeleteJob(JobRecord job) => TryDeleteDir(job.Dir);

    public void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception ex)
        {
            _log?.Warn("spool", $"删除目录失败 {dir}：{ex.Message}");
        }
    }

    /// <summary>启动时清理环形缓冲的孤儿段（强杀进程后会留下）。</summary>
    public void CleanRing()
    {
        if (!Directory.Exists(RingRoot)) return;

        foreach (var dir in Directory.EnumerateDirectories(RingRoot))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.ts"))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }
    }

    public void CleanCache()
    {
        try
        {
            if (Directory.Exists(CacheRoot)) Directory.Delete(CacheRoot, true);
        }
        catch { }
        Directory.CreateDirectory(CacheRoot);
    }
}
