using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using OnvifWebDav.Config;
using OnvifWebDav.Diagnostics;

namespace OnvifWebDav.WebDav;

public sealed record DavEntry(
    string Href, bool IsCollection, long Length, DateTimeOffset? LastModified, string? ETag)
{
    public string Name => Uri.UnescapeDataString(WebDavPath.FileName(Href.TrimEnd('/')));
}

/// <summary>云端配额（RFC 4331）。aliyundrive-webdav 支持，但有些服务端不返回。</summary>
public sealed record StorageQuota(long? UsedBytes, long? AvailableBytes, DateTime CheckedUtc)
{
    private const double Gb = 1024d * 1024d * 1024d;

    public long? TotalBytes => UsedBytes is { } u && AvailableBytes is { } a ? u + a : null;
    public double UsedGb => UsedBytes is { } u ? u / Gb : -1;
    public double AvailableGb => AvailableBytes is { } a ? a / Gb : -1;
    public double TotalGb => TotalBytes is { } t ? t / Gb : -1;

    public string Describe()
    {
        if (AvailableBytes is null && UsedBytes is null) return "未知";

        var parts = new List<string>(3);
        if (TotalBytes is not null) parts.Add($"总 {TotalGb:F0} GB");
        if (UsedBytes is not null) parts.Add($"已用 {UsedGb:F1} GB");
        if (AvailableBytes is not null) parts.Add($"剩余 {AvailableGb:F1} GB");

        return string.Join(" / ", parts) + $"（{CheckedUtc.ToLocalTime():HH:mm} 查询）";
    }
}

/// <summary>测试的单个步骤，用于精确定位"卡在哪一步"。</summary>
public sealed record DavTestStep(string Name, bool Ok, string Detail)
{
    public override string ToString() => (Ok ? "✔ " : "✘ ") + Name + (Detail.Length > 0 ? "：" + Detail : "");
}

public sealed record WebDavTestResult(
    bool Ok, string Message, string AuthScheme, long ElapsedMs, bool SupportsMove, bool SupportsPropfind,
    IReadOnlyList<DavTestStep>? Steps = null, StorageQuota? Quota = null);

public sealed class WebDavException : Exception
{
    public int Status { get; }
    public WebDavException(string message, int status, Exception? inner = null) : base(message, inner) => Status = status;
}

/// <summary>
/// 手写 WebDAV 客户端。只实现我们需要的 6 个方法：
/// OPTIONS / MKCOL / PUT / GET / HEAD / PROPFIND / MOVE / DELETE。
/// 认证：先 Basic（省一次往返），401 且服务器要 Digest 时自动切 RFC7616 Digest 并重试一次。
/// </summary>
public sealed class WebDavClient : IDisposable
{
    private enum AuthKind { Unknown, Basic, Digest, None }

    private static readonly XNamespace Dav = "DAV:";

    private readonly WebDavConfig _cfg;
    private readonly AppLog? _log;
    private readonly HttpClient _http;
    private readonly DigestAuthenticator _digest = new();
    private AuthKind _auth = AuthKind.Unknown;
    private readonly HttpClient _redirectHttp;

    /// <summary>本进程已经确认存在的目录，避免每次上传都全量 MKCOL（阿里云盘有 API 限流）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _ensuredDirs =
        new(StringComparer.OrdinalIgnoreCase);

    public WebDavClient(WebDavConfig cfg, AppLog? log = null)
    {
        _cfg = cfg;
        _log = log;

        var handler = new HttpClientHandler
        {
            UseProxy = cfg.UseSystemProxy,
            AllowAutoRedirect = false,
            PreAuthenticate = false,
            ServerCertificateCustomValidationCallback = cfg.IgnoreCertErrors
                ? (_, _, _, _) => true
                : null,
        };

        _http = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OnvifWebDav/1.0");

        // 专门用于跟随 302 的客户端：不带认证头、允许自动重定向。
        // aliyundrive-webdav 默认 --redirect，GET 文件会 302 到阿里云盘 CDN 的签名地址，
        // 那个地址不需要（也不能带）我们的 Basic 认证头，且通常还会再跳一次。
        var redirectHandler = new HttpClientHandler
        {
            UseProxy = cfg.UseSystemProxy,
            AllowAutoRedirect = true,
            PreAuthenticate = false,
            ServerCertificateCustomValidationCallback = cfg.IgnoreCertErrors
                ? (_, _, _, _) => true
                : null,
        };

        _redirectHttp = new HttpClient(redirectHandler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _redirectHttp.DefaultRequestHeaders.UserAgent.ParseAdd("OnvifWebDav/1.0");

        // 我们默认就先发 Basic，所以有用户名时直接标成 Basic（仅用于界面显示）
        _auth = string.IsNullOrEmpty(cfg.Username) ? AuthKind.None : AuthKind.Basic;
    }

    public string AuthSchemeName => _auth switch
    {
        AuthKind.Basic => "Basic",
        AuthKind.Digest => "Digest-" + "RFC7616",
        AuthKind.None => "无",
        _ => "未知",
    };

    /// <param name="includesRoot">true 表示 relativePath 已经是"从主机根算起"的完整路径（建根目录时用）。</param>
    private Uri BuildUri(string relativePath, bool includesRoot = false)
    {
        var full = includesRoot
            ? WebDavPath.Join(relativePath)
            : WebDavPath.Join(_cfg.RootPath, relativePath);

        var encoded = WebDavPath.Encode(full).TrimStart('/');
        return new Uri(_cfg.Url.TrimEnd('/') + "/" + encoded);
    }

    private static void ApplyAuthHeader(HttpRequestMessage req, string header) =>
        req.Headers.TryAddWithoutValidation("Authorization", header);

    private string BasicHeader() => "Basic " + Convert.ToBase64String(
        Encoding.UTF8.GetBytes($"{_cfg.Username}:{_cfg.Password}"));

    /// <summary>
    /// 统一发送入口。contentFactory 每次重试重新创建（HttpContent 只能发一次）。
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativePath,
        Func<HttpContent?>? contentFactory, Action<HttpRequestMessage>? configure,
        TimeSpan timeout, CancellationToken ct, bool includesRoot = false)
    {
        var uri = BuildUri(relativePath, includesRoot);
        HttpResponseMessage? response = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var req = new HttpRequestMessage(method, uri);
            req.Headers.ExpectContinue = false;

            if (_auth == AuthKind.Digest && _digest.IsReady && !string.IsNullOrEmpty(_cfg.Username))
                ApplyAuthHeader(req, _digest.BuildAuthorizationHeader(_cfg.Username, _cfg.Password,
                    method.Method, uri.PathAndQuery));
            else if (!string.IsNullOrEmpty(_cfg.Username))
                ApplyAuthHeader(req, BasicHeader());

            configure?.Invoke(req);

            var content = contentFactory?.Invoke();
            if (content is not null) req.Content = content;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                response?.Dispose();
                response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new WebDavException($"WebDAV 请求超时（{method} {relativePath}）", 0);
            }
            catch (HttpRequestException ex)
            {
                throw new WebDavException($"WebDAV 连接失败：{ex.Message}", 0, ex);
            }

            if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

            // 401：尝试从 challenge 切认证方式，只重试一次
            var challenge = response.Headers.WwwAuthenticate.ToString();
            if (attempt == 1) return response;

            if (_digest.TryParseChallenge(challenge))
            {
                _auth = AuthKind.Digest;
                _log?.Debug("webdav", "服务器要求 Digest 认证，已切换");
            }
            else if (challenge.Contains("Basic", StringComparison.OrdinalIgnoreCase))
            {
                _auth = AuthKind.Basic;
                _log?.Debug("webdav", "服务器要求 Basic 认证，已切换");
            }
            else
            {
                return response;
            }
        }

        return response!;
    }

    private TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(_cfg.TimeoutSeconds, 10, 600));
    private TimeSpan TransferTimeout => TimeSpan.FromSeconds(Math.Clamp(_cfg.TimeoutSeconds * 10, 120, 3600));

    // ---------------- 基础操作 ----------------

    public async Task EnsureCollectionsAsync(string relativeDir, CancellationToken ct)
    {
        // 先把「配置里那个根目录」本身建出来 —— 否则下面的子目录 MKCOL 全部 409。
        // 老版本从来没建过 root（Nextcloud 上 root 一般已存在所以没暴露），
        // 阿里云盘/新建的云盘目录上就会直接卡死。
        await EnsureRootAsync(ct).ConfigureAwait(false);

        // 注意：这里传的是「相对根目录」的路径，BuildUri 会再拼一次 RootPath。
        // 早期版本把 root 也拼进来，结果变成 /onvif/onvif/... 全部 409。
        var segments = relativeDir.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        var acc = "";
        foreach (var seg in segments)
        {
            acc += "/" + seg;

            // 已经建过的目录直接跳过。阿里云盘这类网盘后端每个 MKCOL 都是一次真实 API 调用，
            // 一段录像 6~8 层目录 × 每天几百段 = 上万次多余请求，必然被限流。
            if (_ensuredDirs.ContainsKey(acc)) continue;

            using var resp = await SendAsync(new HttpMethod("MKCOL"), acc, null, null, Timeout, ct)
                .ConfigureAwait(false);

            // 201 新建；405 已存在；200/204 也算成功
            if (resp.StatusCode is HttpStatusCode.Created or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.OK or HttpStatusCode.NoContent)
            {
                _ensuredDirs[acc] = 0;
                continue;
            }

            if (resp.StatusCode == HttpStatusCode.Conflict)
                continue; // 父级刚被别的进程建好；不写缓存，下次还会重试

            throw new WebDavException(
                $"创建目录失败 {acc} → HTTP {(int)resp.StatusCode}（{await SafeBody(resp, ct)}）",
                (int)resp.StatusCode);
        }
    }

    /// <summary>确保配置的 RootPath 存在（逐层 MKCOL）。</summary>
    private async Task EnsureRootAsync(CancellationToken ct)
    {
        var root = WebDavPath.Join(_cfg.RootPath);
        if (string.IsNullOrEmpty(root) || root == "/") return;

        var acc = "";
        foreach (var seg in root.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            acc += "/" + seg;
            var key = "ROOT::" + acc;
            if (_ensuredDirs.ContainsKey(key)) continue;

            using var resp = await SendAsync(new HttpMethod("MKCOL"), acc, null, null, Timeout, ct,
                includesRoot: true).ConfigureAwait(false);

            if (resp.StatusCode is HttpStatusCode.Created or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.OK or HttpStatusCode.NoContent)
            {
                _ensuredDirs[key] = 0;
                continue;
            }

            if (resp.StatusCode == HttpStatusCode.Conflict) continue;

            throw new WebDavException(
                $"创建根目录失败 {acc} → HTTP {(int)resp.StatusCode}（{await SafeBody(resp, ct)}）",
                (int)resp.StatusCode);
        }
    }

    public async Task PutFileAsync(string relativePath, string localFile, IProgress<long>? progress,
        CancellationToken ct)
    {
        var local = new FileInfo(localFile);
        if (!local.Exists) throw new WebDavException($"本地文件不存在：{localFile}", 0);

        await EnsureCollectionsAsync(WebDavPath.DirOf(relativePath).TrimStart('/'), ct).ConfigureAwait(false);

        using var resp = await SendAsync(HttpMethod.Put, relativePath, () =>
        {
            var fs = File.OpenRead(localFile);
            var content = new ProgressStreamContent(fs, progress, ct);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = local.Length;
            return content;
        }, null, TransferTimeout, ct).ConfigureAwait(false);

        if (resp.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.NoContent)
            return;

        throw new WebDavException(
            $"上传失败 {relativePath} → HTTP {(int)resp.StatusCode}（{await SafeBody(resp, ct)}）",
            (int)resp.StatusCode);
    }

    public async Task<bool> MoveAsync(string fromRel, string toRel, bool overwrite, CancellationToken ct)
    {
        var destination = BuildUri(toRel).AbsoluteUri;

        using var resp = await SendAsync(new HttpMethod("MOVE"), fromRel, null, req =>
        {
            req.Headers.TryAddWithoutValidation("Destination", destination);
            req.Headers.TryAddWithoutValidation("Overwrite", overwrite ? "T" : "F");
        }, Timeout, ct).ConfigureAwait(false);

        return resp.StatusCode is HttpStatusCode.Created or HttpStatusCode.NoContent or HttpStatusCode.OK;
    }

    /// <summary>
    /// 发 GET 并处理 3xx：aliyundrive-webdav 返回 302 指向阿里云盘 CDN 的签名地址。
    /// etag 只从原始响应取（CDN 不会回我们的 ETag）。
    /// </summary>
    private async Task<(HttpResponseMessage Response, string? ETag)> GetContentAsync(string relativePath,
        CancellationToken ct)
    {
        var resp = await SendAsync(HttpMethod.Get, relativePath, null, null, TransferTimeout, ct)
            .ConfigureAwait(false);

        var etag = resp.Headers.ETag?.Tag;

        if (!IsRedirect(resp.StatusCode)) return (resp, etag);

        var location = resp.Headers.Location;
        if (location is null)
        {
            resp.Dispose();
            throw new WebDavException($"服务端返回 {(int)resp.StatusCode} 但没有给出 Location", (int)resp.StatusCode);
        }

        var target = location.IsAbsoluteUri ? location : new Uri(BuildUri(relativePath), location);
        var code = (int)resp.StatusCode;
        resp.Dispose();

        _log?.Debug("webdav", $"GET {relativePath} → {code} 重定向到 {target.Host}，改用无认证客户端跟随");

        try
        {
            var followed = await _redirectHttp.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!followed.IsSuccessStatusCode)
            {
                followed.Dispose();
                throw new WebDavException(
                    $"跟随重定向后下载失败（{target.Host} → HTTP {(int)followed.StatusCode}）",
                    (int)followed.StatusCode);
            }

            return (followed, etag);
        }
        catch (HttpRequestException ex)
        {
            throw new WebDavException(
                $"跟随重定向到 {target.Host} 失败：{ex.Message}" + Environment.NewLine +
                "如果是阿里云盘 WebDAV，可给服务端加 --no-redirect 让它中转下载。", 0, ex);
        }
    }

    private static bool IsRedirect(HttpStatusCode code) => code is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// 查账号级配额（RFC 4331）。必须发到 WebDAV 根，不是我们的 RootPath —— 配额是整个账号的。
    /// 返回 null 表示服务端不支持或查询失败（调用方按"未知"处理，不阻断）。
    /// </summary>
    public async Task<StorageQuota?> GetQuotaAsync(CancellationToken ct)
    {
        const string body = """
                            <?xml version="1.0" encoding="utf-8"?>
                            <d:propfind xmlns:d="DAV:">
                              <d:prop>
                                <d:quota-available-bytes/>
                                <d:quota-used-bytes/>
                              </d:prop>
                            </d:propfind>
                            """;

        try
        {
            using var resp = await SendAsync(new HttpMethod("PROPFIND"), "",
                () => new StringContent(body, new UTF8Encoding(false), "application/xml"),
                req => req.Headers.TryAddWithoutValidation("Depth", "0"),
                Timeout, ct, includesRoot: true).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                _log?.Debug("webdav", $"查配额失败：HTTP {(int)resp.StatusCode}");
                return null;
            }

            var xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseQuota(xml);
        }
        catch (Exception ex)
        {
            _log?.Debug("webdav", "查配额异常：" + ex.Message);
            return null;
        }
    }

    /// <summary>纯解析，便于离线验证。</summary>
    public static StorageQuota? ParseQuota(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;

        try
        {
            var doc = XDocument.Parse(xml);
            long? used = null, available = null;

            foreach (var el in doc.Descendants())
            {
                var name = el.Name.LocalName;
                if (name.Equals("quota-used-bytes", StringComparison.OrdinalIgnoreCase))
                    used = ParseLong(el.Value);
                else if (name.Equals("quota-available-bytes", StringComparison.OrdinalIgnoreCase))
                    available = ParseLong(el.Value);
            }

            return used is null && available is null
                ? null
                : new StorageQuota(used, available, DateTime.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    private static long? ParseLong(string? text) =>
        long.TryParse(text?.Trim(), out var v) && v >= 0 ? v : null;

    /// <summary>
    /// 取远端文件长度。先试 HEAD，拿不到再回退 PROPFIND 的 getcontentlength。
    /// 有些服务端（aliyundrive-webdav 就是）HEAD 返回 405、或者 200 但不给 Content-Length、
    /// 甚至对 HEAD 也发 302，所以不能把 HEAD 当唯一手段。
    /// </summary>
    public async Task<long?> HeadLengthAsync(string relativePath, CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Head, relativePath, null, null, Timeout, ct)
                .ConfigureAwait(false);

            if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentLength is { } len)
                return len;
        }
        catch (Exception ex)
        {
            _log?.Debug("webdav", $"HEAD {relativePath} 失败，改用 PROPFIND：{ex.Message}");
        }

        try
        {
            var entries = await PropFindAsync(relativePath, 0, ct).ConfigureAwait(false);
            var self = entries.FirstOrDefault(e => !e.IsCollection && e.Length > 0)
                       ?? entries.FirstOrDefault(e => !e.IsCollection);

            return self is { Length: > 0 } ? self.Length : null;
        }
        catch (Exception ex)
        {
            _log?.Debug("webdav", $"PROPFIND 取长度也失败：{ex.Message}");
            return null;
        }
    }

    public async Task<(string? Content, string? ETag)> GetStringWithEtagAsync(string relativePath, CancellationToken ct)
    {
        var (resp, etag) = await GetContentAsync(relativePath, ct).ConfigureAwait(false);
        using var _ = resp;

        if (resp.StatusCode == HttpStatusCode.NotFound) return (null, null);
        if (!resp.IsSuccessStatusCode)
            throw new WebDavException($"读取失败 {relativePath} → HTTP {(int)resp.StatusCode}",
                (int)resp.StatusCode);

        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (text, etag);
    }

    /// <summary>返回 false 表示 ETag 冲突（412），调用方需要重读合并。</summary>
    public async Task<bool> PutStringAsync(string relativePath, string content, string? ifMatch,
        CancellationToken ct)
    {
        await EnsureCollectionsAsync(WebDavPath.DirOf(relativePath).TrimStart('/'), ct).ConfigureAwait(false);

        using var resp = await SendAsync(HttpMethod.Put, relativePath, () =>
        {
            var sc = new StringContent(content, new UTF8Encoding(false), "application/json");
            return sc;
        }, req =>
        {
            if (!string.IsNullOrEmpty(ifMatch))
                req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }, Timeout, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.PreconditionFailed) return false;

        if (resp.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.NoContent)
            return true;

        throw new WebDavException(
            $"写入失败 {relativePath} → HTTP {(int)resp.StatusCode}（{await SafeBody(resp, ct)}）",
            (int)resp.StatusCode);
    }

    public async Task DownloadToFileAsync(string relativePath, string localFile, IProgress<long>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);

        var (resp, _) = await GetContentAsync(relativePath, ct).ConfigureAwait(false);
        using var _respGuard = resp;

        if (!resp.IsSuccessStatusCode)
            throw new WebDavException($"下载失败 {relativePath} → HTTP {(int)resp.StatusCode}",
                (int)resp.StatusCode);

        var tmp = localFile + ".part";
        await using (var fs = File.Create(tmp))
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
                progress?.Report(total);
            }
        }

        if (File.Exists(localFile)) File.Delete(localFile);
        File.Move(tmp, localFile);
    }

    public async Task<List<DavEntry>> PropFindAsync(string relativePath, int depth, CancellationToken ct)
    {
        var body = """
                   <?xml version="1.0" encoding="utf-8"?>
                   <d:propfind xmlns:d="DAV:">
                     <d:prop>
                       <d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:getetag/>
                     </d:prop>
                   </d:propfind>
                   """;

        using var resp = await SendAsync(new HttpMethod("PROPFIND"), relativePath, () =>
                new StringContent(body, new UTF8Encoding(false), "application/xml"),
            req => req.Headers.TryAddWithoutValidation("Depth", depth.ToString()),
            Timeout, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.NotFound) return new List<DavEntry>();
        if (!resp.IsSuccessStatusCode)
            throw new WebDavException($"列目录失败 {relativePath} → HTTP {(int)resp.StatusCode}",
                (int)resp.StatusCode);

        var xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var list = new List<DavEntry>();

        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var r in doc.Descendants(Dav + "response"))
            {
                var href = r.Element(Dav + "href")?.Value ?? "";
                var prop = r.Descendants(Dav + "prop").FirstOrDefault();

                var isCollection = prop?.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null;

                long length = 0;
                var lenText = prop?.Element(Dav + "getcontentlength")?.Value;
                if (!string.IsNullOrEmpty(lenText)) long.TryParse(lenText, out length);

                DateTimeOffset? lastMod = null;
                var lm = prop?.Element(Dav + "getlastmodified")?.Value;
                if (DateTimeOffset.TryParse(lm, out var dto)) lastMod = dto;

                var etag = prop?.Element(Dav + "getetag")?.Value;

                list.Add(new DavEntry(href, isCollection, length, lastMod, etag));
            }
        }
        catch (Exception ex)
        {
            _log?.Warn("webdav", $"PROPFIND 响应解析失败：{ex.Message}");
        }

        return list;
    }

    public async Task DeleteAsync(string relativePath, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Delete, relativePath, null, null, Timeout, ct)
            .ConfigureAwait(false);

        if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NotFound) return;

        throw new WebDavException($"删除失败 {relativePath} → HTTP {(int)resp.StatusCode}",
            (int)resp.StatusCode);
    }

    private static async Task<string> SafeBody(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            body = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return body.Length <= 200 ? body : body[..200] + "…";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>界面上"测试连接"用：一次性把认证、建目录、上传、MOVE、PROPFIND 全试一遍。</summary>
    public async Task<WebDavTestResult> TestAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var steps = new List<DavTestStep>();
        var move = false;
        var propfind = false;
        StorageQuota? quota = null;

        void Add(string name, bool ok, string detail = "") => steps.Add(new DavTestStep(name, ok, detail));

        WebDavTestResult Result(bool ok, string message) =>
            new(ok, message, AuthSchemeName, sw.ElapsedMilliseconds, move, propfind, steps.ToArray(), quota);

        if (string.IsNullOrWhiteSpace(_cfg.Url))
        {
            Add("地址", false, "未填写");
            return Result(false, "请先填写 WebDAV 地址");
        }

        var probe = $".selftest/probe-{DateTime.Now:HHmmss}.txt";
        const string probeContent = "onvif-webdav ok";
        var deleted = false;

        try
        {
            // 1) 列根目录
            try
            {
                var entries = await PropFindAsync("", 0, ct).ConfigureAwait(false);
                propfind = entries.Count > 0;
                Add("PROPFIND 根目录", true, propfind ? $"{entries.Count} 项" : "返回空列表");
            }
            catch (Exception ex)
            {
                Add("PROPFIND 根目录", false, ex.Message);
            }

            if (!propfind)
            {
                // 根目录可能还不存在，尝试创建
                try
                {
                    await EnsureCollectionsAsync("", ct).ConfigureAwait(false);
                    Add("MKCOL 根目录", true);
                }
                catch (Exception ex)
                {
                    Add("MKCOL 根目录", false, ex.Message);
                }
            }

            // 2) 建测试目录
            try
            {
                await EnsureCollectionsAsync(".selftest", ct).ConfigureAwait(false);
                Add("MKCOL .selftest", true);
            }
            catch (Exception ex)
            {
                Add("MKCOL .selftest", false, ex.Message);
                return Result(false, "建目录失败：" + ex.Message);
            }

            // 3) 上传
            try
            {
                await PutStringAsync(probe, probeContent, null, ct).ConfigureAwait(false);
                Add("PUT 上传", true, $"{probeContent.Length} 字节");
            }
            catch (Exception ex)
            {
                Add("PUT 上传", false, ex.Message);
                return Result(false, "上传失败：" + ex.Message);
            }

            // 4) 取长度：尽力而为，拿不到不判失败。
            //    aliyundrive-webdav 这类后端 HEAD 可能 405 / 不给 Content-Length / 同样 302。
            var len = await HeadLengthAsync(probe, ct).ConfigureAwait(false);
            if (len is null)
                Add("取文件长度", false, "HEAD 和 PROPFIND 都给不出长度（不影响使用，跳过校验）");
            else if (len != probeContent.Length)
                Add("取文件长度", false, $"期望 {probeContent.Length} / 实际 {len}（可能异步落盘，以回读为准）");
            else
                Add("取文件长度", true, $"{len} 字节");

            // 5) 回读：这一步才是关键，验证下载链路（含 302 直链）
            try
            {
                var (text, _) = await GetStringWithEtagAsync(probe, ct).ConfigureAwait(false);
                if (text == probeContent)
                {
                    Add("GET 回读", true, "内容一致");
                }
                else
                {
                    Add("GET 回读", false, $"内容不一致（期望 {probeContent.Length} 字节，实际 {text?.Length ?? 0}）");
                    return Result(false,
                        "上传能通但下载取不到正确内容 —— 网页播放会黑屏。如果服务端是阿里云盘 WebDAV，" +
                        "确认本机能访问外网，或给服务端加 --no-redirect 让它中转下载。");
                }
            }
            catch (Exception ex)
            {
                Add("GET 回读", false, ex.Message);
                return Result(false, "下载失败：" + ex.Message);
            }

            // 6) MOVE：不支持不算失败（上传会降级成直接 PUT）
            try
            {
                move = await MoveAsync(probe, probe + ".moved", true, ct).ConfigureAwait(false);
                Add("MOVE", move, move ? "支持" : "不支持，上传将直接 PUT 正式文件名");
            }
            catch (Exception ex)
            {
                move = false;
                Add("MOVE", false, ex.Message);
            }

            // 7) 清理
            try
            {
                await DeleteAsync(move ? probe + ".moved" : probe, ct).ConfigureAwait(false);
                deleted = true;
                Add("DELETE 清理", true);
            }
            catch (Exception ex)
            {
                Add("DELETE 清理", false, ex.Message + "（测试文件可能残留）");
            }

            // 8) 顺带查一次配额（拿不到不影响结论）
            quota = await GetQuotaAsync(ct).ConfigureAwait(false);
            Add("查配额", quota is not null,
                quota is null ? "服务端不返回配额信息（不影响使用）" : quota.Describe());

            sw.Stop();

            var msg = $"连接正常（{AuthSchemeName}，往返 {sw.ElapsedMilliseconds} ms）：" +
                      $"上传✔ 下载✔ MOVE {(move ? "✔" : "✘ 将直接 PUT")}" +
                      (deleted ? "" : " ｜ 清理失败，可能有残留测试文件");

            return Result(true, msg);
        }
        catch (WebDavException ex)
        {
            sw.Stop();
            var hint = ex.Status == 401
                ? "认证失败：请检查用户名/密码（Nextcloud 用应用密码；阿里云盘用启动参数里的 --auth-user/--auth-password）。"
                : ex.Message;
            return Result(false, hint);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Result(false, "测试失败：" + ex.Message);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _redirectHttp.Dispose();
    }

    /// <summary>带进度上报的流（大文件上传时界面能看到进度）。</summary>
    private sealed class ProgressStreamContent : HttpContent
    {
        private readonly Stream _source;
        private readonly IProgress<long>? _progress;
        private readonly CancellationToken _ct;

        public ProgressStreamContent(Stream source, IProgress<long>? progress, CancellationToken ct)
        {
            _source = source;
            _progress = progress;
            _ct = ct;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await _source.ReadAsync(buffer, _ct).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), _ct).ConfigureAwait(false);
                total += read;
                _progress?.Report(total);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            if (_source.CanSeek)
            {
                length = _source.Length - _source.Position;
                return true;
            }
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _source.Dispose();
            base.Dispose(disposing);
        }
    }
}
