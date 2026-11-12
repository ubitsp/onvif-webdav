using OnvifWebDav.Storage;

namespace OnvifWebDav.Probe;

/// <summary>
/// 内存版 IRemoteStorage，专供 Probr 的离线测试用。
///
/// 存在的意义：保留策略的删除是不可逆操作，必须能验证；
/// 而跑真实后端要开夸克客户端、要联网、还会真的删数据。
/// 这个替身让 retention-days 测试随时能跑，且绝对不会碰到真实录像。
/// </summary>
internal sealed class InMemoryStorage : IRemoteStorage
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirs = new(StringComparer.Ordinal) { "/" };

    public string DisplayName => "内存（测试用）";
    public bool SupportsMove => true;
    public bool QuotaReleasesImmediately => true;

    /// <summary>当前内存里的文件路径，测试用来自查。</summary>
    public IReadOnlyCollection<string> FilePaths => _files.Keys;

    public Task EnsureDirectoryAsync(string relativeDir, CancellationToken ct)
    {
        var key = Key(relativeDir);
        _dirs.Add(key);

        // 父目录也要存在，否则 ListAsync 拼不出来
        var parent = ParentOf(key);
        while (parent != "/")
        {
            _dirs.Add(parent);
            parent = ParentOf(parent);
        }

        _dirs.Add("/");

        return Task.CompletedTask;
    }

    public async Task PutFileAsync(string relativePath, string localFile, IProgress<long>? progress,
        CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(localFile, ct).ConfigureAwait(false);
        var key = Key(relativePath);
        _files[key] = text;

        await EnsureDirectoryAsync(ParentOf(key), ct).ConfigureAwait(false);
        progress?.Report(text.Length);
    }

    public Task<bool> MoveAsync(string fromRel, string toRel, bool overwrite, CancellationToken ct)
    {
        var from = Key(fromRel);
        var to = Key(toRel);

        if (!_files.TryGetValue(from, out var content)) return Task.FromResult(false);
        if (!overwrite && _files.ContainsKey(to)) return Task.FromResult(false);

        _files.Remove(from);
        _files[to] = content;
        _dirs.Add(ParentOf(to));

        return Task.FromResult(true);
    }

    public Task<StorageQuota?> GetQuotaAsync(CancellationToken ct) =>
        Task.FromResult<StorageQuota?>(null);

    public Task<long?> HeadLengthAsync(string relativePath, CancellationToken ct)
    {
        var key = Key(relativePath);
        return Task.FromResult(_files.TryGetValue(key, out var c) ? (long?)c.Length : null);
    }

    public Task<(string? Content, string? ETag)> GetStringWithEtagAsync(string relativePath,
        CancellationToken ct)
    {
        var key = Key(relativePath);

        if (!_files.TryGetValue(key, out var content))
            return Task.FromResult<(string?, string?)>((null, null));

        // 版本号必须随内容变 —— RemoteClipIndex 靠它做乐观锁
        return Task.FromResult<(string?, string?)>((content, $"mem:{content.Length}:{content.GetHashCode():X8}"));
    }

    public Task<bool> PutStringAsync(string relativePath, string content, string? ifMatch, CancellationToken ct)
    {
        var key = Key(relativePath);
        _files[key] = content;

        var parent = ParentOf(key);
        _dirs.Add(parent);
        while (parent != "/")
        {
            parent = ParentOf(parent);
            _dirs.Add(parent);
        }

        _dirs.Add("/");
        return Task.FromResult(true);
    }

    public async Task DownloadToFileAsync(string relativePath, string localFile, IProgress<long>? progress,
        CancellationToken ct)
    {
        var key = Key(relativePath);

        if (!_files.TryGetValue(key, out var content))
            throw new FileNotFoundException("内存里没有这个文件", relativePath);

        await File.WriteAllTextAsync(localFile, content, ct).ConfigureAwait(false);
        progress?.Report(content.Length);
    }

    public Task<List<RemoteEntry>> ListAsync(string relativePath, int depth, CancellationToken ct)
    {
        var dir = Key(relativePath);
        if (depth <= 0) return Task.FromResult(new List<RemoteEntry>());

        var prefix = dir == "/" ? "/" : dir + "/";
        var result = new List<RemoteEntry>();

        // 直接子目录
        foreach (var d in _dirs.Where(x => x != dir && x.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var rest = d[prefix.Length..];
            if (rest.Contains('/')) continue; // 孙目录不直接列

            result.Add(new RemoteEntry(prefix + rest, true, 0, null, null, d));
        }

        // 直接子文件
        foreach (var (path, content) in _files)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var rest = path[prefix.Length..];
            if (rest.Contains('/')) continue;

            result.Add(new RemoteEntry(path, false, content.Length, null, null, path));
        }

        return Task.FromResult(result);
    }

    public Task DeleteAsync(string relativePath, CancellationToken ct)
    {
        var key = Key(relativePath);

        _files.Remove(key);

        // 目录要连同子孙一起删（和真实后端行为一致）
        var prefix = key == "/" ? "/" : key + "/";

        foreach (var f in _files.Keys.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _files.Remove(f);

        foreach (var d in _dirs.Where(x => x == key || x.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _dirs.Remove(d);

        return Task.CompletedTask;
    }

    public void Dispose() { }

    private static string Key(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";

        var segs = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != ".");

        var joined = string.Join('/', segs);
        return joined.Length == 0 ? "/" : "/" + joined;
    }

    private static string ParentOf(string key)
    {
        var k = key.TrimEnd('/');
        var i = k.LastIndexOf('/');
        return i <= 0 ? "/" : k[..i];
    }
}
