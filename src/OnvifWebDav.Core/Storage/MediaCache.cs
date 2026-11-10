using OnvifWebDav.Diagnostics;
using OnvifWebDav.WebDav;

namespace OnvifWebDav.Storage;

/// <summary>
/// 网页播放用的本地 LRU 缓存。
/// 为什么不直接把 WebDAV 流透传给浏览器：不可 seek，手机上拖进度条必失败。
/// 局域网带宽足够，落地一次换来流畅拖动。
/// </summary>
public sealed class MediaCache
{
    private readonly string _dir;
    private readonly AppLog? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, SemaphoreSlim> _locks = new();

    private long _limitBytes;

    public MediaCache(string directory, long limitBytes, AppLog? log = null)
    {
        _dir = directory;
        _log = log;
        _limitBytes = Math.Max(0, limitBytes);
        Directory.CreateDirectory(_dir);
    }

    public void SetLimit(long bytes)
    {
        _limitBytes = Math.Max(0, bytes);
        if (_limitBytes == 0) Clear();
    }

    public string VideoPath(string cameraId, string clipId) =>
        Path.Combine(_dir, "video", $"{Safe(cameraId)}_{Safe(clipId)}.mp4");

    public string ThumbPath(string cameraId, string clipId) =>
        Path.Combine(_dir, "thumb", $"{Safe(cameraId)}_{Safe(clipId)}.jpg");

    private static string Safe(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return new string(chars);
    }

    /// <summary>拿到本地文件路径；缓存没有就从 WebDAV 下。返回 null 表示远端 404。</summary>
    public async Task<string?> EnsureAsync(string cameraId, string clipId, string remotePath, bool isVideo,
        WebDavClient dav, CancellationToken ct)
    {
        var local = isVideo ? VideoPath(cameraId, clipId) : ThumbPath(cameraId, clipId);
        if (File.Exists(local) && new FileInfo(local).Length > 0)
        {
            TryTouch(local);
            return local;
        }

        if (_limitBytes == 0) return null;

        var key = local.ToLowerInvariant();
        SemaphoreSlim gate;
        lock (_locks)
        {
            if (!_locks.TryGetValue(key, out gate!))
            {
                gate = new SemaphoreSlim(1, 1);
                _locks[key] = gate;
            }
        }

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(local) && new FileInfo(local).Length > 0) return local;

            await dav.DownloadToFileAsync(remotePath, local, null, ct).ConfigureAwait(false);
            _log?.Debug("cache", $"已缓存 {Path.GetFileName(local)} {new FileInfo(local).Length / 1024}KB");

            await TrimAsync().ConfigureAwait(false);
            return local;
        }
        catch (WebDavException ex) when (ex.Status == 404)
        {
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private static void TryTouch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }
    }

    /// <summary>按最后访问时间淘汰，超限就删最旧的直到降到 80%。</summary>
    public Task TrimAsync() => TrimCoreAsync();

    private async Task TrimCoreAsync()
    {
        if (_limitBytes <= 0) return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var files = new DirectoryInfo(_dir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.LastAccessTimeUtc)
                .ToList();

            var total = files.Sum(f => f.Length);
            if (total <= _limitBytes) return;

            var target = (long)(_limitBytes * 0.8);
            foreach (var f in files)
            {
                if (total <= target) break;
                try
                {
                    var len = f.Length;
                    f.Delete();
                    total -= len;
                }
                catch { }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Clear()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(_dir))
                try { Directory.Delete(d, true); } catch { }
            foreach (var f in Directory.EnumerateFiles(_dir))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }
}
