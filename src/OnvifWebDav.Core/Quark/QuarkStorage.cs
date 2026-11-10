using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.Quark;

namespace OnvifWebDav.Storage;

/// <summary>
/// 夸克网盘后端。
///
/// 与 WebDAV 的结构性差异（都来自实测，见 AGENTS.md 第 53~64 条）：
///
/// 1. **没有路径，只有 fid**。夸克的 API 全部按 fid 操作，找文件必须列父目录比对名字。
///    所以这里维护一张「相对路径 → fid」的映射表并落盘；映射失效时列目录自愈。
/// 2. **没有 ETag**。乐观并发靠合成版本号 <c>q:{fid}:{ticks}:{size}</c>，
///    它不是服务端契约，只能保证"内容变了版本号一定变"，不能用来做 If-Match。
/// 3. **没有原子改名**。夸克有 move 接口但没验证过，<see cref="SupportsMove"/> 先给 false，
///    让 UploadQueue 走"直接写最终路径"的分支。
/// 4. **删除只进回收站**，配额不会立刻回来（<see cref="QuotaReleasesImmediately"/> = false）。
/// </summary>
public sealed class QuarkStorage : IRemoteStorage
{
    /// <summary>
    /// 分片大小。pre 响应里**没有** part_size，只能自己定。
    /// OSS 要求除最后一片外每片 ≥ 100KB，取 8MB 兼顾"片段少"和"单请求别太大"。
    /// 录像片段通常 5~30MB，也就是 1~4 片。
    /// </summary>
    private const int PartSizeBytes = 8 * 1024 * 1024;

    private const int ListPageSize = 100;

    private readonly QuarkApiClient _api;
    private readonly AppLog? _log;
    private readonly string? _mapFile;
    private readonly HttpClient _downloadHttp;

    /// <summary>映射表的读写都要串行，否则两个 worker 同时建同一个目录会建出两份。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Dictionary<string, string> _dirFids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fileFids = new(StringComparer.Ordinal);

    /// <summary>本进程已确认存在的目录，避免每次上传都列一遍目录。</summary>
    private readonly HashSet<string> _ensuredDirs = new(StringComparer.Ordinal);

    /// <summary>
    /// 刚上传完的文件元信息。
    /// **必须有**：夸克的目录列表是异步索引的，finish 返回后立刻列目录**看不到**刚传的文件，
    /// 要过一两秒才出现。没有这层缓存，上传完紧接着的 HeadLength / 读回全部失败。
    /// </summary>
    private readonly Dictionary<string, StatResult> _statCache = new(StringComparer.Ordinal);

    /// <summary>账号指纹。换账号（Cookie 里的 __uid 变了）时映射表必须整体作废。</summary>
    private readonly string _uid;

    /// <summary>
    /// 配置里的根目录（"/onvif" 或 "/"）。
    /// IRemoteStorage 的约定是"路径相对存储根"，拼根这一步由各实现自己做 ——
    /// WebDAV 在 BuildUri 里拼，夸克在这里拼。
    /// </summary>
    private readonly string _rootPrefix;

    private bool _mapLoaded;

    public QuarkStorage(QuarkApiClient api, AppLog? log = null, string? mapFile = null, string? rootPath = null)
    {
        _api = api;
        _log = log;
        _mapFile = mapFile;
        _uid = api.ReadCookie("__uid") ?? "";
        _rootPrefix = PathKey(rootPath ?? "/");

        _downloadHttp = new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
        })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
    }

    // ==================== IRemoteStorage 元信息 ====================

    public string DisplayName => "夸克网盘";

    /// <summary>夸克有 move 接口但没验证过，先老实说不支持，走降级分支。</summary>
    public bool SupportsMove => false;

    /// <summary>删除只是进回收站，配额要等回收站清理才回来。</summary>
    public bool QuotaReleasesImmediately => false;

    // ==================== 目录 ====================

    public async Task EnsureDirectoryAsync(string relativeDir, CancellationToken ct)
    {
        var key = KeyOf(relativeDir);
        if (key == "/") return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureDirectoryLockedAsync(key, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>调用方必须已持有 _gate。</summary>
    private async Task<string> EnsureDirectoryLockedAsync(string key, CancellationToken ct)
    {
        await EnsureMapLoadedAsync(ct).ConfigureAwait(false);

        if (_dirFids.TryGetValue(key, out var known)) return known;

        // 逐段往上建：/a/b/c 要先有 /a 和 /a/b
        var segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var acc = "";
        var parentFid = "0";

        foreach (var seg in segments)
        {
            acc += "/" + seg;

            if (_dirFids.TryGetValue(acc, out var existing))
            {
                parentFid = existing;
                continue;
            }

            // 先按名字找（可能是上次进程建的、映射表丢了）
            var found = await FindChildFidAsync(parentFid, seg, expectDir: true, ct).ConfigureAwait(false);

            if (found is null)
            {
                found = await _api.CreateFolderAsync(parentFid, seg, ct).ConfigureAwait(false);
                _log?.Info("quark", $"建目录 {acc} → {found}");
            }

            _dirFids[acc] = found;
            parentFid = found;
        }

        _ensuredDirs.Add(key);
        await SaveMapAsync(ct).ConfigureAwait(false);

        return parentFid;
    }

    /// <summary>找父目录下叫 name 的子项 fid。找不到返回 null。</summary>
    private async Task<string?> FindChildFidAsync(string parentFid, string name, bool expectDir,
        CancellationToken ct)
    {
        var list = await ListRawAsync(parentFid, ct).ConfigureAwait(false);

        foreach (var item in list)
        {
            var fileName = item?["file_name"]?.GetValue<string>();
            if (!string.Equals(fileName, name, StringComparison.Ordinal)) continue;

            var isDir = item?["dir"]?.GetValue<bool>() ?? false;
            if (isDir != expectDir) continue;

            return item?["fid"]?.GetValue<string>();
        }

        return null;
    }

    /// <summary>列目录（自动翻页）。</summary>
    private async Task<List<JsonNode?>> ListRawAsync(string pdirFid, CancellationToken ct)
    {
        var result = new List<JsonNode?>();
        var page = 1;

        while (true)
        {
            var data = await _api.ListAsync(pdirFid, page, ListPageSize, ct).ConfigureAwait(false);

            if (data?["list"] is not JsonArray list || list.Count == 0) break;

            foreach (var item in list) result.Add(item);

            // _fetch_total=1 时会给 total；拿不到就靠"这页没满"判断结束
            var total = data["_total"]?.GetValue<int>() ?? -1;
            if (list.Count < ListPageSize) break;

            if (total >= 0 && result.Count >= total) break;

            page++;
            if (page > 200) break; // 防御：目录异常大时别无限翻
        }

        return result;
    }

    // ==================== 上传 ====================

    public async Task PutFileAsync(string relativePath, string localFile, IProgress<long>? progress,
        CancellationToken ct)
    {
        var key = KeyOf(relativePath);
        var dir = ParentOf(key);
        var name = NameOf(key);

        var fileInfo = new FileInfo(localFile);
        if (!fileInfo.Exists) throw new FileNotFoundException("本地文件不存在", localFile);

        string fid;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var parentFid = await EnsureDirectoryLockedAsync(dir, ct).ConfigureAwait(false);
            fid = await UploadLockedAsync(parentFid, name, localFile, fileInfo, progress, ct).ConfigureAwait(false);

            _fileFids[key] = fid;

            // 立刻记进 stat 缓存：接下来几百毫秒内列目录都看不到这个文件
            _statCache[key] = new StatResult(fid, fileInfo.Length,
                new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero));

            await SaveMapAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        _log?.Info("quark", $"上传完成 {key} → fid={fid}");
    }

    /// <summary>调用方必须已持有 _gate。</summary>
    private async Task<string> UploadLockedAsync(string parentFid, string name, string localFile,
        FileInfo info, IProgress<long>? progress, CancellationToken ct)
    {
        var size = info.Length;
        var contentType = QuarkApiClient.GuessMime(name);

        // 1) 预处理：拿 task_id / upload_id / obj_key / bucket / auth_info
        var pre = await _api.UploadPreAsync(parentFid, name, size, info.LastWriteTimeUtc, ct).ConfigureAwait(false);

        var taskId = Require(pre, "task_id");
        var uploadId = Require(pre, "upload_id");
        var objKey = Require(pre, "obj_key");
        var bucket = Require(pre, "bucket");
        var authInfo = Require(pre, "auth_info");

        // 2) 逐片直传。每片都要单独取签名，因为 auth_meta 里的 partNumber 不同
        var partCount = (int)Math.Max(1, (size + PartSizeBytes - 1) / PartSizeBytes);
        var etags = new List<(int PartNumber, string ETag)>(partCount);
        var uploaded = 0L;

        await using (var fs = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                         PartSizeBytes, useAsync: true))
        {
            for (var partNumber = 1; partNumber <= partCount; partNumber++)
            {
                ct.ThrowIfCancellationRequested();

                var offset = (long)(partNumber - 1) * PartSizeBytes;
                var length = (int)Math.Min(PartSizeBytes, size - offset);

                var buffer = new byte[length];
                fs.Seek(offset, SeekOrigin.Begin);

                var read = 0;
                while (read < length)
                {
                    var n = await fs.ReadAsync(buffer.AsMemory(read, length - read), ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    read += n;
                }

                if (read != length)
                    throw new IOException($"读取本地文件不完整：{localFile} 偏移 {offset}，期望 {length} 实读 {read}");

                var date = QuarkApiClient.OssDate(DateTime.UtcNow);
                var meta = QuarkApiClient.BuildPutAuthMeta(contentType, bucket, objKey, uploadId, partNumber, date);
                var auth = await _api.UploadAuthAsync(taskId, authInfo, meta, ct).ConfigureAwait(false);
                var authKey = FindAuthKey(auth)
                              ?? throw new QuarkApiException($"第 {partNumber} 片取签名失败", -1, 200);

                var url = QuarkApiClient.BuildPartUrl(bucket, objKey, uploadId, partNumber);
                var etag = await PutPartAsync(url, authKey, date, contentType, buffer, ct).ConfigureAwait(false)
                           ?? throw new QuarkApiException($"第 {partNumber} 片上传失败", -1, 0);

                etags.Add((partNumber, etag));

                uploaded += length;
                progress?.Report(uploaded);
            }
        }

        // 3) 报哈希（用来秒传/去重）。失败不影响上传
        try
        {
            var md5 = await HashFileAsync(localFile, MD5.Create, ct).ConfigureAwait(false);
            var sha1 = await HashFileAsync(localFile, SHA1.Create, ct).ConfigureAwait(false);
            await _api.UpdateHashAsync(taskId, md5, sha1, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Warn("quark", "上报 md5/sha1 失败（不影响上传）：" + ex.Message);
        }

        // 4) 合并分片 + 回调。少了这步 finish 必回 complete file failed
        var completeXml = QuarkApiClient.BuildCompleteXml(etags);
        var xmlBytes = Encoding.UTF8.GetBytes(completeXml);
        var contentMd5 = Convert.ToBase64String(MD5.HashData(xmlBytes));

        var callbackJson = (pre["callback"]?.DeepClone() ?? new JsonObject()).ToJsonString();
        var callbackB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(callbackJson));

        var mergeDate = QuarkApiClient.OssDate(DateTime.UtcNow);
        var mergeMeta = QuarkApiClient.BuildMergeAuthMeta(contentMd5, callbackB64, bucket, objKey, uploadId,
            mergeDate);

        var mergeAuth = await _api.UploadAuthAsync(taskId, authInfo, mergeMeta, ct).ConfigureAwait(false);
        var mergeKey = FindAuthKey(mergeAuth)
                       ?? throw new QuarkApiException("合并分片取签名失败", -1, 200);

        await CompleteMultipartAsync(bucket, objKey, uploadId, mergeKey, mergeDate, contentMd5, callbackB64,
            xmlBytes, ct).ConfigureAwait(false);

        // 5) 确认。返回值才是最终 fid —— pre 里那个 fid 不是最终值
        var finish = await _api.UploadFinishAsync(objKey, taskId, ct).ConfigureAwait(false);

        var fid = finish?["fid"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(fid))
        {
            // finish 偶尔不回 fid，按名字再查一次
            fid = await FindChildFidAsync(parentFid, name, expectDir: false, ct).ConfigureAwait(false);
        }

        return fid ?? throw new QuarkApiException($"上传完成但拿不到 fid：{name}", -1, 200);
    }

    /// <summary>PUT 一个分片。三个头必须和 auth_meta 里的签名内容逐字对齐。</summary>
    private static async Task<string?> PutPartAsync(string url, string authKey, string date, string contentType,
        byte[] payload, CancellationToken ct)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        using var req = new HttpRequestMessage(HttpMethod.Put, url);
        req.Headers.TryAddWithoutValidation("Authorization", authKey);
        req.Headers.TryAddWithoutValidation("x-oss-date", date);
        req.Headers.TryAddWithoutValidation("x-oss-user-agent", QuarkApiClient.OssUserAgent);

        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        req.Content = content;

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new QuarkApiException($"分片 PUT 失败 HTTP {(int)resp.StatusCode}：{Clip(body, 300)}",
                -1, (int)resp.StatusCode);
        }

        return resp.Headers.ETag?.Tag;
    }

    /// <summary>POST 到 ?uploadId= 触发 OSS 合并，并让 OSS 回调夸克。</summary>
    private static async Task CompleteMultipartAsync(string bucket, string objKey, string uploadId,
        string authKey, string date, string contentMd5, string callbackB64, byte[] xml, CancellationToken ct)
    {
        var url = $"https://{bucket}.pds.quark.cn/{objKey.TrimStart('/')}" +
                  $"?uploadId={Uri.EscapeDataString(uploadId)}";

        using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Authorization", authKey);
        req.Headers.TryAddWithoutValidation("x-oss-date", date);
        req.Headers.TryAddWithoutValidation("x-oss-user-agent", QuarkApiClient.OssUserAgent);
        req.Headers.TryAddWithoutValidation("x-oss-callback", callbackB64);

        var content = new ByteArrayContent(xml);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        content.Headers.TryAddWithoutValidation("Content-MD5", contentMd5);
        req.Content = content;

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new QuarkApiException($"合并分片失败 HTTP {(int)resp.StatusCode}：{Clip(body, 300)}",
                -1, (int)resp.StatusCode);
        }
    }

    // ==================== 改名 ====================

    /// <summary>不支持。返回 false 让调用方走"直接写最终路径"的分支。</summary>
    public Task<bool> MoveAsync(string fromRel, string toRel, bool overwrite, CancellationToken ct) =>
        Task.FromResult(false);

    // ==================== 配额 ====================

    public async Task<StorageQuota?> GetQuotaAsync(CancellationToken ct)
    {
        var member = await _api.GetMemberAsync(ct).ConfigureAwait(false);
        if (member is null) return null;

        var used = ReadLong(member, "use_capacity");
        var total = ReadLong(member, "total_capacity");

        if (used < 0 && total < 0) return null;

        long? available = used >= 0 && total >= 0 ? Math.Max(0, total - used) : null;

        return new StorageQuota(used < 0 ? null : used, available, DateTime.UtcNow);
    }

    // ==================== 读 ====================

    public async Task<long?> HeadLengthAsync(string relativePath, CancellationToken ct)
    {
        var entry = await StatAsync(relativePath, ct).ConfigureAwait(false);
        return entry is null ? null : entry.Length;
    }

    public async Task<(string? Content, string? ETag)> GetStringWithEtagAsync(string relativePath,
        CancellationToken ct)
    {
        var entry = await StatAsync(relativePath, ct).ConfigureAwait(false);

        if (entry is null) return (null, null);

        var fid = entry.Fid;
        var url = await _api.GetDownloadUrlAsync(fid, ct).ConfigureAwait(false);
        if (url is null) return (null, null);

        var text = await GetStringWithSessionAsync(url, ct).ConfigureAwait(false);

        // 合成版本号：夸克没有 ETag，但"内容变了版本号必须变"这条要成立
        var version = $"q:{fid}:{entry.UpdatedAt?.UtcTicks ?? 0}:{entry.Length}";

        return (text, version);
    }

    public async Task<bool> PutStringAsync(string relativePath, string content, string? ifMatch,
        CancellationToken ct)
    {
        // 夸克不支持条件写（没有 If-Match），只能覆盖。ifMatch 仅在语义上保留。
        var temp = Path.Combine(Path.GetTempPath(), "owd-quark-" + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            await File.WriteAllTextAsync(temp, content, Encoding.UTF8, ct).ConfigureAwait(false);
            await PutFileAsync(relativePath, temp, null, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    public async Task DownloadToFileAsync(string relativePath, string localFile, IProgress<long>? progress,
        CancellationToken ct)
    {
        var entry = await StatAsync(relativePath, ct).ConfigureAwait(false)
                    ?? throw new FileNotFoundException($"远端不存在：{relativePath}");

        var url = await _api.GetDownloadUrlAsync(entry.Fid, ct).ConfigureAwait(false)
                  ?? throw new QuarkApiException($"拿不到下载地址：{relativePath}", -1, 200);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        // 下载域会校验会话头，只带签名参数会被 403
        req.Headers.TryAddWithoutValidation("Cookie", _api.CurrentCookie);
        req.Headers.TryAddWithoutValidation("User-Agent", _api.UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", "https://pan.quark.cn/");

        using var resp = await _downloadHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new QuarkApiException($"下载失败 HTTP {(int)resp.StatusCode}：{relativePath}",
                -1, (int)resp.StatusCode);

        var dir = Path.GetDirectoryName(localFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(localFile, FileMode.Create, FileAccess.Write, FileShare.None,
                         81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long total = 0;
            int n;

            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                total += n;
                progress?.Report(total);
            }
        }
    }

    // ==================== 列表 ====================

    public async Task<List<RemoteEntry>> ListAsync(string relativePath, int depth, CancellationToken ct)
    {
        // relKey 保持调用方视角（不含配置根），Href 用它 —— 否则调用方拿到的路径会莫名多一层
        var relKey = PathKey(relativePath);
        var key = _rootPrefix == "/" ? relKey : PathKey(_rootPrefix + relKey);

        string? fid;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureMapLoadedAsync(ct).ConfigureAwait(false);

            // 列目录**不能**顺手建目录 —— 探测不存在的日期目录时会把整棵树建满
            fid = key == "/"
                ? "0"
                : _dirFids.TryGetValue(key, out var known)
                    ? known
                    : await FindChildFidAsync(
                        await TryResolveDirFidAsync(ParentOf(key), ct).ConfigureAwait(false) ?? "0",
                        NameOf(key), expectDir: true, ct).ConfigureAwait(false);

            if (fid is not null && key != "/") _dirFids[key] = fid;
        }
        finally
        {
            _gate.Release();
        }

        if (fid is null || depth <= 0) return new List<RemoteEntry>();

        var items = await ListRawAsync(fid, ct).ConfigureAwait(false);
        var result = new List<RemoteEntry>(items.Count);

        foreach (var item in items)
        {
            if (item is null) continue;

            var name = item["file_name"]?.GetValue<string>() ?? "";
            if (name.Length == 0) continue;

            var isDir = item["dir"]?.GetValue<bool>() ?? false;
            var itemFid = item["fid"]?.GetValue<string>();
            var size = ReadLong(item, "size");
            var updated = ReadLong(item, "updated_at");

            result.Add(new RemoteEntry(
                Href: (relKey == "/" ? "" : relKey) + "/" + name,
                IsCollection: isDir,
                Length: size < 0 ? 0 : size,
                LastModified: updated > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(updated) : null,
                ETag: null,
                Id: itemFid));
        }

        // 顺手把 fid 记进映射表：后续按路径操作就不用再列一遍目录。
        // 这里不落盘（列目录太频繁），让 PutFile/Delete 时统一保存。
        if (result.Count > 0)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var e in result)
                {
                    if (e.Id is null) continue;

                    if (e.IsCollection) _dirFids[e.Href] = e.Id;
                    else _fileFids[e.Href] = e.Id;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        return result;
    }

    /// <summary>解析已存在的目录 fid，不创建。找不到返回 null。</summary>
    private async Task<string?> TryResolveDirFidAsync(string key, CancellationToken ct)
    {
        if (key == "/" || string.IsNullOrEmpty(key)) return "0";

        await EnsureMapLoadedAsync(ct).ConfigureAwait(false);

        if (_dirFids.TryGetValue(key, out var known)) return known;

        var segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var acc = "";
        var parentFid = "0";

        foreach (var seg in segments)
        {
            acc += "/" + seg;

            if (_dirFids.TryGetValue(acc, out var cached))
            {
                parentFid = cached;
                continue;
            }

            var found = await FindChildFidAsync(parentFid, seg, expectDir: true, ct).ConfigureAwait(false);
            if (found is null) return null;

            _dirFids[acc] = found;
            parentFid = found;
        }

        return parentFid;
    }

    private sealed record StatResult(string Fid, long Length, DateTimeOffset? UpdatedAt);

    /// <summary>
    /// 按路径找文件元信息。目录当文件查会返回 null。
    ///
    /// 两条路径：
    ///  · 本进程刚上传过的，直接命中 <see cref="_statCache"/>（列表还没索引完，查也是白查）
    ///  · 其它情况列父目录，找不到时**重试** —— 夸克的目录索引有延迟
    /// </summary>
    private async Task<StatResult?> StatAsync(string relativePath, CancellationToken ct)
    {
        var key = KeyOf(relativePath);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_statCache.TryGetValue(key, out var cached)) return cached;
        }
        finally
        {
            _gate.Release();
        }

        var dirKey = ParentOf(key);
        var name = NameOf(key);

        // 3 次 × 500ms：覆盖刚上传（列表未索引）和刚删除（列表未移除）两种滞后
        for (var attempt = 0; attempt < 3; attempt++)
        {
            string? dirFid;

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await EnsureMapLoadedAsync(ct).ConfigureAwait(false);

                // 注意这里**不能**调 ListAsync：它接收的是"相对根"的路径，会再拼一次根，
                // 而 dirKey 已经含根了。直接按目录 fid 查。
                dirFid = dirKey == "/"
                    ? "0"
                    : _dirFids.TryGetValue(dirKey, out var k)
                        ? k
                        : await TryResolveDirFidAsync(dirKey, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            if (dirFid is not null)
            {
                var items = await ListRawAsync(dirFid, ct).ConfigureAwait(false);

                foreach (var item in items)
                {
                    if (item is null) continue;
                    if (item["dir"]?.GetValue<bool>() == true) continue;
                    if (!string.Equals(item["file_name"]?.GetValue<string>(), name, StringComparison.Ordinal))
                        continue;

                    var fid = item["fid"]?.GetValue<string>();
                    if (fid is null) continue;

                    var size = ReadLong(item, "size");
                    var updated = ReadLong(item, "updated_at");

                    var stat = new StatResult(fid, size < 0 ? 0 : size,
                        updated > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(updated) : null);

                    await _gate.WaitAsync(ct).ConfigureAwait(false);
                    try { _statCache[key] = stat; }
                    finally { _gate.Release(); }

                    return stat;
                }
            }

            if (attempt < 2) await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return null;
    }

    // ==================== 删除 ====================

    public async Task DeleteAsync(string relativePath, CancellationToken ct)
    {
        var key = KeyOf(relativePath);

        string? fid;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureMapLoadedAsync(ct).ConfigureAwait(false);

            if (_fileFids.TryGetValue(key, out var knownFile)) fid = knownFile;
            else if (_dirFids.TryGetValue(key, out var knownDir)) fid = knownDir;
            else
            {
                var parentFid = await TryResolveDirFidAsync(ParentOf(key), ct).ConfigureAwait(false);
                if (parentFid is null) return; // 父目录都没有，自然不存在

                var name = NameOf(key);
                fid = await FindChildFidAsync(parentFid, name, expectDir: false, ct).ConfigureAwait(false)
                      ?? await FindChildFidAsync(parentFid, name, expectDir: true, ct).ConfigureAwait(false);
            }

            if (fid is null) return;

            // action_type=2 进回收站。这里刻意不用 40（彻底删除）：
            // 录像轮转是常规操作，误删要能捞回来，而且 40 的删除权限在部分账号上会被拒。
            await _api.DeleteAsync(new[] { fid }, 2, ct).ConfigureAwait(false);

            _fileFids.Remove(key);
            _statCache.Remove(key);
            RemoveDirSubtree(key);
            await SaveMapAsync(ct).ConfigureAwait(false);
        }
        catch (QuarkApiException ex) when (ex.Message.Contains("不存在", StringComparison.Ordinal))
        {
            // 已经没了，当作成功
            _log?.Info("quark", $"删除时发现已不存在：{key}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RemoveDirSubtree(string key)
    {
        var prefix = key.TrimEnd('/') + "/";

        foreach (var k in _dirFids.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _dirFids.Remove(k);

        foreach (var k in _fileFids.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _fileFids.Remove(k);

        foreach (var k in _statCache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _statCache.Remove(k);

        _dirFids.Remove(key);
        _ensuredDirs.RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal));
        _ensuredDirs.Remove(key);
    }

    // ==================== 映射持久化 ====================

    private sealed class MapFile
    {
        public int Version { get; set; } = 1;
        public string Uid { get; set; } = "";
        public Dictionary<string, string> Dirs { get; set; } = new();
        public Dictionary<string, string> Files { get; set; } = new();
    }

    private async Task EnsureMapLoadedAsync(CancellationToken ct)
    {
        if (_mapLoaded) return;
        _mapLoaded = true;

        if (string.IsNullOrWhiteSpace(_mapFile) || !File.Exists(_mapFile)) return;

        try
        {
            var json = await File.ReadAllTextAsync(_mapFile, ct).ConfigureAwait(false);
            var map = JsonSerializer.Deserialize<MapFile>(json);

            if (map is null) return;

            // 换了账号，旧 fid 全部作废（fid 只在账号内有效）
            if (!string.IsNullOrWhiteSpace(map.Uid) && map.Uid != _uid)
            {
                _log?.Warn("quark", $"映射表的账号指纹变了（{map.Uid} → {_uid}），已作废重建");
                return;
            }

            foreach (var (k, v) in map.Dirs) _dirFids[k] = v;
            foreach (var (k, v) in map.Files) _fileFids[k] = v;

            _log?.Info("quark", $"已载入 fid 映射：{_dirFids.Count} 个目录 / {_fileFids.Count} 个文件");
        }
        catch (Exception ex)
        {
            _log?.Warn("quark", "载入 fid 映射失败，将按名字重新解析：" + ex.Message);
        }
    }

    private async Task SaveMapAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_mapFile)) return;

        try
        {
            var map = new MapFile
            {
                Uid = _uid,
                Dirs = new Dictionary<string, string>(_dirFids),
                Files = new Dictionary<string, string>(_fileFids),
            };

            var dir = Path.GetDirectoryName(_mapFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 先写临时文件再替换，避免写一半断电把映射表写坏
            var temp = _mapFile + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(map,
                new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);

            File.Move(temp, _mapFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.Warn("quark", "保存 fid 映射失败（不影响本次上传）：" + ex.Message);
        }
    }

    // ==================== 工具 ====================

    /// <summary>规范化成 "/a/b/c"：折叠重复斜杠、去末尾斜杠、补前导斜杠。</summary>
    internal static string PathKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";

        var segments = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != ".");

        var joined = string.Join('/', segments);
        return joined.Length == 0 ? "/" : "/" + joined;
    }

    /// <summary>相对路径 → 映射表/API 用的绝对 key（含配置根目录）。</summary>
    private string KeyOf(string relativePath)
    {
        var p = PathKey(relativePath);
        return _rootPrefix == "/" ? p : PathKey(_rootPrefix + p);
    }

    private static string ParentOf(string key)
    {
        var k = key.TrimEnd('/');
        var idx = k.LastIndexOf('/');
        return idx <= 0 ? "/" : k[..idx];
    }

    private static string NameOf(string key)
    {
        var k = key.TrimEnd('/');
        var idx = k.LastIndexOf('/');
        return idx < 0 ? k : k[(idx + 1)..];
    }

    private static string Require(JsonNode? node, string field) =>
        node?[field]?.GetValue<string>()
        ?? throw new QuarkApiException($"upload/pre 响应缺少 {field}", -1, 200);

    /// <summary>从 auth 响应里挖签名串。字段名没文档，按常见叫法试，最后兜底取最长的字符串。</summary>
    private static string? FindAuthKey(JsonNode? auth)
    {
        if (auth is not JsonObject obj) return null;

        foreach (var name in new[] { "auth_key", "authorization", "Authorization", "signature", "token" })
        {
            if (obj[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                return s;
        }

        return obj.Select(kv => kv.Value is JsonValue v2 && v2.TryGetValue<string>(out var s2) ? s2 : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .OrderByDescending(s => s!.Length)
            .FirstOrDefault();
    }

    private async Task<string> GetStringWithSessionAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Cookie", _api.CurrentCookie);
        req.Headers.TryAddWithoutValidation("User-Agent", _api.UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", "https://pan.quark.cn/");

        using var resp = await _downloadHttp.SendAsync(req, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new QuarkApiException($"下载失败 HTTP {(int)resp.StatusCode}", -1, (int)resp.StatusCode);

        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 分块算哈希。录像片段几十 MB，一次性读进内存算 md5+sha1 会瞬间多占一倍内存，
    /// 而这台机器已经在用内存盘了。
    /// </summary>
    private static async Task<string> HashFileAsync(string path, Func<HashAlgorithm> factory, CancellationToken ct)
    {
        using var hash = factory();
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, useAsync: true);

        var buffer = new byte[81920];
        int n;

        while ((n = await fs.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            hash.TransformBlock(buffer, 0, n, null, 0);

        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        return Convert.ToHexString(hash.Hash!).ToLowerInvariant();
    }

    private static long ReadLong(JsonNode? node, string field)
    {
        var v = node?[field];
        if (v is null) return -1;

        try
        {
            return v.GetValueKind() switch
            {
                JsonValueKind.Number => v.GetValue<long>(),
                JsonValueKind.String => long.TryParse(v.GetValue<string>(), out var n) ? n : -1,
                _ => -1,
            };
        }
        catch
        {
            return -1;
        }
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    public void Dispose()
    {
        _downloadHttp.Dispose();
        _gate.Dispose();
        _api.Dispose();
    }
}
