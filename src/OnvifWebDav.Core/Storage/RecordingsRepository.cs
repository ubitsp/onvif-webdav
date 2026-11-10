using System.Text.Json;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.WebDav;

namespace OnvifWebDav.Storage;

/// <summary>网页端读录像的统一入口：索引 + 本地缓存 + WebDAV 全部收在这里，Web 层不碰细节。</summary>
public sealed class RecordingsRepository
{
    private readonly Func<WebDavClient?> _davFactory;
    private readonly MediaCache _cache;
    private readonly AppLog _log;
    private WebDavClient? _dav;

    private readonly object _indexGate = new();

    /// <summary>
    /// 每日索引的短 TTL 缓存。**必须有**：网页里每张缩略图、每个视频 Range 请求
    /// 都会走 FindClipAsync → GetClipsAsync → 读一次远端索引 + 可能一次 PROPFIND。
    /// 一页 20 个片段 = 几十次 WebDAV 往返，阿里云盘立刻 500/403。
    /// </summary>
    private readonly Dictionary<string, (DateTime At, List<ClipInfo> Clips)> _indexCache = new();

    /// <summary>读失败后的冷却时间，避免服务端出错时被反复捶。</summary>
    private readonly Dictionary<string, DateTime> _indexFailUntil = new();

    private static readonly TimeSpan IndexCacheTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IndexFailTtl = TimeSpan.FromSeconds(30);

    public RecordingsRepository(Func<WebDavClient?> davFactory, MediaCache cache, AppLog log)
    {
        _davFactory = davFactory;
        _cache = cache;
        _log = log;
    }

    private WebDavClient Dav => _dav ??= _davFactory()
        ?? throw new InvalidOperationException("WebDAV 尚未配置");

    public async Task<List<DateTime>> GetDatesAsync(string cameraId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cameraId)) return new List<DateTime>();
        var index = new RemoteClipIndex(Dav, _log);
        return await index.GetDatesAsync(cameraId, ct).ConfigureAwait(false);
    }

    public async Task<List<ClipInfo>> GetClipsAsync(string cameraId, DateTime date, CancellationToken ct)
    {
        var key = cameraId + "|" + date.ToString("yyyy-MM-dd");

        lock (_indexGate)
        {
            if (_indexCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < IndexCacheTtl)
                return hit.Clips;

            // 上次刚失败过：直接用旧数据（或空），别再打 WebDAV
            if (_indexFailUntil.TryGetValue(key, out var until) && DateTime.UtcNow < until)
                return _indexCache.TryGetValue(key, out var stale) ? stale.Clips : new List<ClipInfo>();
        }

        try
        {
            var index = new RemoteClipIndex(Dav, _log);
            var clips = await index.GetDayAsync(cameraId, date, ct).ConfigureAwait(false);

            lock (_indexGate)
            {
                _indexCache[key] = (DateTime.UtcNow, clips);
                _indexFailUntil.Remove(key);
            }

            return clips;
        }
        catch (Exception ex)
        {
            lock (_indexGate)
            {
                _indexFailUntil[key] = DateTime.UtcNow.Add(IndexFailTtl);
                _log.Warn("repo", $"读 {key} 索引失败，{IndexFailTtl.TotalSeconds:F0}s 内不再重试：{ex.Message}");
                return _indexCache.TryGetValue(key, out var stale) ? stale.Clips : new List<ClipInfo>();
            }
        }
    }

    /// <summary>新片段上传完、或用户手动刷新时清掉缓存。</summary>
    public void InvalidateDay(string cameraId, DateTime date)
    {
        var key = cameraId + "|" + date.ToString("yyyy-MM-dd");

        lock (_indexGate)
        {
            _indexCache.Remove(key);
            _indexFailUntil.Remove(key);
        }
    }

    public async Task<List<DaySummary>> GetDaySummariesAsync(string cameraId, int maxDays, CancellationToken ct)
    {
        var dates = await GetDatesAsync(cameraId, ct).ConfigureAwait(false);
        var result = new List<DaySummary>();

        foreach (var d in dates.Take(Math.Max(1, maxDays)))
        {
            try
            {
                var clips = await GetClipsAsync(cameraId, d, ct).ConfigureAwait(false);
                if (clips.Count == 0) continue;

                result.Add(new DaySummary(
                    d.ToString("yyyy-MM-dd"),
                    clips.Count,
                    (int)clips.Sum(c => c.DurationMs) / 1000,
                    clips.Min(c => c.StartLocal).ToString("HH:mm"),
                    clips.Max(c => c.EndLocal).ToString("HH:mm")));
            }
            catch (Exception ex)
            {
                _log.Warn("repo", $"读取 {d:yyyy-MM-dd} 失败：{ex.Message}");
            }
        }

        return result;
    }

    public async Task<ClipInfo?> FindClipAsync(string cameraId, DateTime date, string clipId, CancellationToken ct)
    {
        var clips = await GetClipsAsync(cameraId, date, ct).ConfigureAwait(false);
        return clips.FirstOrDefault(c => c.Id == clipId);
    }

    /// <summary>返回本地可播放的 mp4 路径；远端 404 返回 null。</summary>
    public async Task<string?> GetVideoFileAsync(ClipInfo clip, CancellationToken ct) =>
        await _cache.EnsureAsync(clip.CameraId, clip.Id, clip.Path, isVideo: true, Dav, ct).ConfigureAwait(false);

    public async Task<string?> GetThumbFileAsync(ClipInfo clip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clip.Thumb)) return null;
        return await _cache.EnsureAsync(clip.CameraId, clip.Id, clip.Thumb, isVideo: false, Dav, ct)
            .ConfigureAwait(false);
    }

    /// <summary>重置底层 WebDAV 客户端（配置变更后调用）。</summary>
    public void ResetClient()
    {
        try { _dav?.Dispose(); } catch { }
        _dav = null;
    }
}
