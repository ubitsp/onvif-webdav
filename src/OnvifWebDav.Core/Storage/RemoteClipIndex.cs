using System.Text.Json;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.WebDav;

namespace OnvifWebDav.Storage;

/// <summary>
/// WebDAV 上的每日索引。索引是"文件完整"的唯一权威标记：
/// 上传顺序必须是 文件 → 缩略图 → 索引，索引最后写。
/// 索引有记录但文件 404 → 网页显示"文件缺失"；文件在但索引无记录 → 忽略。
/// </summary>
public sealed class RemoteClipIndex
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly WebDavClient _dav;
    private readonly AppLog? _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>
    /// 索引读失败日志的节流。每个 (摄像头,日期) 最多 2 分钟一条 WARN。
    /// 不加这个会刷屏：网页每张缩略图、每个 Range 请求都会走到这里，
    /// 阿里云盘一返回 500 就是一秒钟几十行。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> WarnAt = new();

    private void WarnThrottled(string key, string message)
    {
        var now = DateTime.UtcNow;

        if (WarnAt.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(2))
        {
            _log?.Debug("index", message);
            return;
        }

        WarnAt[key] = now;
        _log?.Warn("index", message);
    }

    public RemoteClipIndex(WebDavClient dav, AppLog? log = null)
    {
        _dav = dav;
        _log = log;
    }

    public static string IndexPath(string cameraId, DateTime localDate) =>
        $"{cameraId}/index/{localDate:yyyy-MM-dd}.json";

    public static string ClipsDir(string cameraId, DateTime localDate) =>
        $"{cameraId}/clips/{localDate:yyyy/MM/dd}";

    public static string ThumbsDir(string cameraId, DateTime localDate) =>
        $"{cameraId}/thumbs/{localDate:yyyy/MM/dd}";

    public async Task<List<ClipInfo>> GetDayAsync(string cameraId, DateTime localDate, CancellationToken ct)
    {
        var path = IndexPath(cameraId, localDate);

        try
        {
            var (text, _) = await _dav.GetStringWithEtagAsync(path, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var index = JsonSerializer.Deserialize<DailyIndex>(text, JsonOpts);
                if (index?.Clips is { Count: > 0 })
                    return index.Clips.OrderBy(c => c.StartUtc).ToList();
            }
        }
        catch (Exception ex)
        {
            WarnThrottled($"{cameraId}|{localDate:yyyy-MM-dd}",
                $"读索引失败（已回退 PROPFIND，2 分钟内不再重复告警）：{ex.Message}");
        }

        return await RebuildFromFilesAsync(cameraId, localDate, ct).ConfigureAwait(false);
    }

    /// <summary>索引缺失/损坏时，用 PROPFIND 列文件反推（时长未知，按码率估算）。</summary>
    private async Task<List<ClipInfo>> RebuildFromFilesAsync(string cameraId, DateTime localDate,
        CancellationToken ct)
    {
        var dir = ClipsDir(cameraId, localDate);
        var entries = await _dav.PropFindAsync(dir, 1, ct).ConfigureAwait(false);

        var list = new List<ClipInfo>();
        foreach (var e in entries)
        {
            if (e.IsCollection) continue;
            var name = e.Name;
            if (!name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) continue;

            // 文件名格式 HHmmss_xxxx.mp4
            var stem = name[..^4];
            var parts = stem.Split('_');
            if (parts.Length < 1 || parts[0].Length != 6) continue;
            if (!TimeSpan.TryParseExact(parts[0], "hhmmss", null, out var tod)) continue;

            var startLocal = localDate.Date + tod;
            var id = stem;
            var seconds = Math.Max(1, (int)(e.Length / 400_000)); // 约 3.2Mbps 粗估

            list.Add(new ClipInfo
            {
                Id = id,
                CameraId = cameraId,
                StartUtc = startLocal.ToUniversalTime(),
                EndUtc = startLocal.AddSeconds(seconds).ToUniversalTime(),
                DurationMs = seconds * 1000,
                SizeBytes = e.Length,
                Path = $"{dir.TrimStart('/')}/{name}",
                Thumb = $"{ThumbsDir(cameraId, localDate).TrimStart('/')}/{stem}.jpg",
                VideoCodec = "",
                HasAudio = false,
            });
        }

        return list.OrderBy(c => c.StartUtc).ToList();
    }

    /// <summary>列出有索引的日期（PROPFIND index 目录，比逐日探测快得多）。</summary>
    public async Task<List<DateTime>> GetDatesAsync(string cameraId, CancellationToken ct)
    {
        var entries = await _dav.PropFindAsync($"{cameraId}/index", 1, ct).ConfigureAwait(false);

        var dates = new List<DateTime>();
        foreach (var e in entries)
        {
            if (e.IsCollection) continue;
            var name = e.Name;
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            if (DateTime.TryParseExact(name[..^5], "yyyy-MM-dd", null,
                    System.Globalization.DateTimeStyles.None, out var d))
                dates.Add(d);
        }

        return dates.OrderByDescending(d => d).ToList();
    }

    /// <summary>乐观锁追加一条记录：GET(取 ETag) → 合并 → PUT(If-Match)；412 重试。</summary>
    public async Task<bool> AddAsync(ClipInfo clip, CancellationToken ct)
    {
        var localDate = clip.StartLocal.Date;
        var path = IndexPath(clip.CameraId, localDate);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var (text, etag) = await _dav.GetStringWithEtagAsync(path, ct).ConfigureAwait(false);

                DailyIndex index;
                if (string.IsNullOrWhiteSpace(text))
                {
                    index = new DailyIndex { CameraId = clip.CameraId, Date = localDate.ToString("yyyy-MM-dd") };
                    etag = null; // 新建时不带 If-Match
                }
                else
                {
                    try
                    {
                        index = JsonSerializer.Deserialize<DailyIndex>(text, JsonOpts) ?? new DailyIndex();
                    }
                    catch
                    {
                        index = new DailyIndex { CameraId = clip.CameraId, Date = localDate.ToString("yyyy-MM-dd") };
                    }
                }

                index.CameraId = clip.CameraId;
                index.Date = localDate.ToString("yyyy-MM-dd");
                index.UpdatedUtc = DateTime.UtcNow;
                index.Clips.RemoveAll(c => c.Id == clip.Id);
                index.Clips.Add(clip);
                index.Clips = index.Clips.OrderBy(c => c.StartUtc).ToList();

                var json = JsonSerializer.Serialize(index, JsonOpts);

                try
                {
                    if (await _dav.PutStringAsync(path, json, etag, ct).ConfigureAwait(false))
                        return true;
                }
                catch (WebDavException ex) when (ex.Status is 409 or 412)
                {
                    // 412 = ETag 冲突，重读重试
                }

                await Task.Delay(200 * (attempt + 1), ct).ConfigureAwait(false);
            }

            _log?.Warn("index", $"索引写入连续冲突，{clip.Id} 暂未进索引（下次重建会补上）");
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>按天重建索引（自愈）。只列 clips 目录，不改已存在的记录。</summary>
    public async Task<int> RebuildDayAsync(string cameraId, DateTime localDate, CancellationToken ct)
    {
        var existing = await GetDayAsync(cameraId, localDate, ct).ConfigureAwait(false);
        var fromFiles = await RebuildFromFilesAsync(cameraId, localDate, ct).ConfigureAwait(false);

        var existingIds = existing.Select(c => c.Id).ToHashSet();
        var merged = existing.ToList();

        foreach (var clip in fromFiles.Where(c => !existingIds.Contains(c.Id)))
            merged.Add(clip);

        if (merged.Count == existing.Count) return 0;

        var index = new DailyIndex
        {
            CameraId = cameraId,
            Date = localDate.ToString("yyyy-MM-dd"),
            UpdatedUtc = DateTime.UtcNow,
            Clips = merged.OrderBy(c => c.StartUtc).ToList(),
        };

        await _dav.PutStringAsync(IndexPath(cameraId, localDate), JsonSerializer.Serialize(index, JsonOpts),
            null, ct).ConfigureAwait(false);

        return merged.Count - existing.Count;
    }
}
