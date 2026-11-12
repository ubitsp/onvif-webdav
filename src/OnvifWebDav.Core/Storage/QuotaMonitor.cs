using OnvifWebDav.Diagnostics;
using OnvifWebDav.WebDav;

namespace OnvifWebDav.Storage;

/// <summary>
/// 配额缓存。真实查询有成本（一次 PROPFIND），所以按 ProbeMinutes 节流；
/// 两次查询之间用缓存值判断，别每次上传都打一遍。
/// </summary>
public sealed class QuotaMonitor
{
    private readonly object _gate = new();
    private StorageQuota? _last;
    private DateTime _lastCheckUtc = DateTime.MinValue;

    public StorageQuota? Last
    {
        get { lock (_gate) return _last; }
    }

    public DateTime LastCheckUtc
    {
        get { lock (_gate) return _lastCheckUtc; }
    }

    public bool IsStale(int probeMinutes) =>
        _last is null || DateTime.UtcNow - _lastCheckUtc > TimeSpan.FromMinutes(Math.Max(1, probeMinutes));

    public void Set(StorageQuota? quota)
    {
        if (quota is null) return;

        lock (_gate)
        {
            _last = quota;
            _lastCheckUtc = DateTime.UtcNow;
        }
    }

    /// <summary>剩余空间是否低于阈值；配额未知时返回 false（不阻断）。</summary>
    public bool Below(long bytes) => _last?.AvailableBytes is { } available && available < bytes;

    public static long GbToBytes(int gb) => (long)gb * 1024 * 1024 * 1024;
}

/// <summary>
/// 保留策略的"执行者"：从最旧的一天开始整天删，直到腾够空间或没有可删的。
/// 删除顺序：录像文件 → 缩略图 → 索引文件。索引最后删，保证中途失败时索引仍然指向真实存在的文件。
/// </summary>
public sealed class RetentionPruner
{
    public sealed record PruneResult(int DaysDeleted, int ClipsDeleted, StorageQuota? After, bool ReachedTarget);

    public async Task<PruneResult> PruneOldestAsync(IRemoteStorage dav, string cameraId, long targetFreeBytes,
        AppLog? log, CancellationToken ct)
    {
        var index = new RemoteClipIndex(dav, log);
        var dates = await index.GetDatesAsync(cameraId, ct);

        if (dates.Count == 0)
        {
            log?.Warn("retention", "云端没有任何录像可删");
            return new PruneResult(0, 0, await dav.GetQuotaAsync(ct), false);
        }

        // 先看一眼当前空间：已经够了就一天都不删。
        // 不加这一步的话会"先删再检查"，白白删掉一天 —— 删除不可逆，这种误删代价太大。
        var initial = await dav.GetQuotaAsync(ct).ConfigureAwait(false);
        if (initial?.AvailableBytes is { } initialFree && initialFree >= targetFreeBytes)
        {
            log?.Info("retention", $"剩余 {initial.AvailableGb:F1} GB 已达标（目标 {targetFreeBytes / 1024d / 1024 / 1024:F1} GB），无需清理");
            return new PruneResult(0, 0, initial, true);
        }

        // 还差多少才达标。用于"配额不会立刻回来"的后端做累计估算。
        var needBytes = initial?.AvailableBytes is { } f0 ? Math.Max(0, targetFreeBytes - f0) : targetFreeBytes;

        // 夸克这类后端删除只进回收站，配额要等回收站清理才回来。
        // 如果还是按"删完查配额看涨没涨"来判断，会永远不达标，然后把所有天的录像全删光。
        var countFreedBytes = !dav.QuotaReleasesImmediately;
        var freedBytes = 0L;

        var days = 0;
        var clips = 0;
        StorageQuota? quota = null;

        // GetDatesAsync 返回的是倒序，删除要从最旧的开始
        foreach (var date in dates.OrderBy(d => d))
        {
            ct.ThrowIfCancellationRequested();

            var list = await index.GetDayAsync(cameraId, date, ct);

            freedBytes += await DeleteClipsAsync(dav, list, log, ct).ConfigureAwait(false);

            // 索引最后删（它是"文件完整"的权威标记）
            try
            {
                await dav.DeleteAsync(RemoteClipIndex.IndexPath(cameraId, date), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log?.Warn("retention", $"删除索引 {date:yyyy-MM-dd} 失败：{ex.Message}");
            }

            days++;
            clips += list.Count;
            log?.Info("retention", $"已删除 {date:yyyy-MM-dd} 的 {list.Count} 段录像");

            // 后端删除后配额不立即释放（夸克进回收站）：按累计删除量判断，别去查配额 ——
            // 查了也不会涨，判断逻辑会一直认为"没达标"，最后把录像删光
            if (countFreedBytes)
            {
                if (freedBytes >= needBytes)
                {
                    log?.Info("retention",
                        $"已删除 {freedBytes / 1024d / 1024 / 1024:F2} GB（目标 {needBytes / 1024d / 1024 / 1024:F2} GB），" +
                        "但本后端删除进回收站，配额要等回收站清理后才真正回来");
                    return new PruneResult(days, clips, quota, true);
                }

                continue;
            }

            quota = await dav.GetQuotaAsync(ct).ConfigureAwait(false);

            if (quota?.AvailableBytes is not { } free)
            {
                // 查不到配额就没法判断"够了没"，删一天就停，避免无脑删光
                log?.Warn("retention", "服务端不返回配额，停止继续删除");
                break;
            }

            if (free >= targetFreeBytes)
                return new PruneResult(days, clips, quota, true);
        }

        return new PruneResult(days, clips, quota, false);
    }

    /// <summary>
    /// 按日期清理：删掉所有早于 <paramref name="keepFromDate"/> 的天。
    ///
    /// 和 <see cref="PruneOldestAsync"/> 的区别：那个是"空间不够了才删，删到够为止"，
    /// 这个是"到日子就删，不管空间还剩多少"。两者互不冲突，都开也行。
    /// </summary>
    public async Task<PruneResult> PruneOlderThanAsync(IRemoteStorage dav, string cameraId,
        DateTime keepFromDate, AppLog? log, CancellationToken ct)
    {
        var index = new RemoteClipIndex(dav, log);
        var dates = await index.GetDatesAsync(cameraId, ct).ConfigureAwait(false);

        // GetDatesAsync 是倒序的，这里按正序删（从最旧的开始）
        var expired = dates
            .Where(d => d.Date < keepFromDate.Date)
            .OrderBy(d => d)
            .ToList();

        if (expired.Count == 0)
        {
            log?.Info("retention",
                $"保留策略：没有需要删除的录像（保留自 {keepFromDate:yyyy-MM-dd} 起，" +
                $"云端共 {dates.Count} 天）");
            return new PruneResult(0, 0, null, true);
        }

        var days = 0;
        var clips = 0;

        foreach (var date in expired)
        {
            ct.ThrowIfCancellationRequested();

            var list = await index.GetDayAsync(cameraId, date, ct).ConfigureAwait(false);

            await DeleteClipsAsync(dav, list, log, ct).ConfigureAwait(false);

            // 索引最后删（它是"文件完整"的权威标记）。
            // 顺序反了的话，中途失败会留下"索引指向已删文件"，网页上一点就报错。
            try
            {
                await dav.DeleteAsync(RemoteClipIndex.IndexPath(cameraId, date), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log?.Warn("retention", $"删除索引 {date:yyyy-MM-dd} 失败：{ex.Message}");
            }

            days++;
            clips += list.Count;
            log?.Info("retention", $"保留策略：已删除 {date:yyyy-MM-dd} 的 {list.Count} 段录像");
        }

        log?.Info("retention", $"保留策略完成：删除 {days} 天共 {clips} 段");
        return new PruneResult(days, clips, null, true);
    }

    /// <summary>删一天里的所有片段（视频 + 缩略图），返回成功删掉的字节数。</summary>
    private static async Task<long> DeleteClipsAsync(IRemoteStorage dav, List<ClipInfo> list,
        AppLog? log, CancellationToken ct)
    {
        var freed = 0L;

        foreach (var clip in list)
        {
            try
            {
                await dav.DeleteAsync(clip.Path, ct).ConfigureAwait(false);
                freed += clip.SizeBytes;
            }
            catch (Exception ex)
            {
                log?.Warn("retention", $"删除 {clip.Path} 失败：{ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(clip.Thumb))
            {
                try { await dav.DeleteAsync(clip.Thumb, ct).ConfigureAwait(false); }
                catch { /* 缩略图丢了不影响 */ }
            }
        }

        return freed;
    }
}
