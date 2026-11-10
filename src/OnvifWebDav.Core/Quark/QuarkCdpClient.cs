using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OnvifWebDav.Diagnostics;

namespace OnvifWebDav.Quark;

/// <summary>
/// 从夸克浏览器的调试端口（CDP）读取网盘会话 Cookie 与 User-Agent。
///
/// 为什么走 CDP 而不是读 Cookie 数据库：
///  · Chromium 的 Cookies SQLite 是加密的（v10/v20），要解密还得处理 DPAPI/AES-GCM；
///  · CDP 的 Storage.getCookies 返回的是明文，且不需要碰文件锁。
///
/// 为什么取 UA 也走这里：夸克会校验 UA 里的 QuarkPC 标识，手抄容易出错，
/// /json/version 里本来就带着完整的真实 UA。
/// </summary>
public sealed class QuarkCdpClient : IDisposable
{
    private const string CdpHost = "http://127.0.0.1";

    private readonly int _port;
    private readonly AppLog? _log;
    private readonly HttpClient _http;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _cached;
    private DateTime _cachedUntilUtc;

    public QuarkCdpClient(int port = 9222, AppLog? log = null)
    {
        _port = port is > 0 and <= 65535 ? port : 9222;
        _log = log;

        // 连本机调试端口，绝不能走系统代理（否则请求会被代理吞掉）
        _http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
    }

    /// <summary>浏览器基本信息（来自 /json/version）。</summary>
    public sealed record BrowserInfo(string UserAgent, string Browser, string WebSocketUrl);

    /// <summary>一次取到的会话。Source 用于界面提示是"来自浏览器"还是"手填兜底"。</summary>
    public sealed record Session(string Cookie, string UserAgent, DateTime AtUtc, string Source)
    {
        public bool FromBrowser => Source == "cdp";
    }

    /// <summary>读 /json/version。浏览器没起调试端口时返回 null（不抛）。</summary>
    public async Task<BrowserInfo?> TryGetBrowserInfoAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{CdpHost}:{_port}/json/version", ct)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;

            var ua = root.TryGetProperty("User-Agent", out var u) ? u.GetString() ?? "" : "";
            var browser = root.TryGetProperty("Browser", out var b) ? b.GetString() ?? "" : "";
            var ws = root.TryGetProperty("webSocketDebuggerUrl", out var w) ? w.GetString() ?? "" : "";

            if (string.IsNullOrEmpty(ws)) return null;

            return new BrowserInfo(ua, browser, ws);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Debug("quark", $"读取 CDP /json/version 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 取当前浏览器里的夸克网盘 Cookie + UA。
    /// 浏览器没开调试端口 / 没登录网盘 / 拿不到 __puus 时返回 null。
    /// </summary>
    public async Task<Session?> TryGetSessionAsync(CancellationToken ct)
    {
        var info = await TryGetBrowserInfoAsync(ct).ConfigureAwait(false);
        if (info is null) return null;

        var cookies = await TryGetCookiesAsync(info.WebSocketUrl, ct).ConfigureAwait(false);
        if (cookies is null) return null;

        var cookieHeader = BuildCookieHeader(cookies);
        if (cookieHeader.Length == 0)
        {
            _log?.Warn("quark", "CDP 连上了，但没找到 quark.cn 的 Cookie —— 浏览器里可能没登录夸克网盘");
            return null;
        }

        if (!cookies.Any(c => c.Name == "__puus"))
            _log?.Warn("quark", "Cookie 里缺少 __puus，夸克 API 很可能会返回未登录");

        // 只打名字和长度，绝不打值
        _log?.Info("quark",
            $"CDP 取到 {cookies.Count} 个 Cookie（{string.Join(", ", cookies.Select(c => c.Name).Take(8))}" +
            $"{(cookies.Count > 8 ? " …" : "")}），长度 {cookieHeader.Length}");

        return new Session(cookieHeader, info.UserAgent, DateTime.UtcNow, "cdp");
    }

    /// <summary>
    /// 调试用：列出 Cookie 的名称/域/路径/值长度，**不含值**。
    ///
    /// 用来回答"Cookie 到底挂在哪个域下" —— 客户端是 uccd://cloud.quark 这种自定义协议，
    /// 而 Cookie 却在 quark.cn 域，两者对不上，所以页面的 document.cookie 一定是空的。
    /// </summary>
    public async Task<List<(string Name, string Domain, string Path, int ValueLength)>?> DescribeCookiesAsync(
        CancellationToken ct)
    {
        var info = await TryGetBrowserInfoAsync(ct).ConfigureAwait(false);
        if (info is null) return null;

        var cookies = await TryGetCookiesAsync(info.WebSocketUrl, ct, dedupe: false).ConfigureAwait(false);
        return cookies?.Select(c => (c.Name, c.Domain, c.Path, c.Value.Length)).ToList();
    }

    /// <summary>
    /// 带缓存的取会话。缓存期内直接返回上次结果，避免每 6 小时的上传都去握手一次。
    /// 单飞：并发调用只会有一次真正握手。
    /// </summary>
    public async Task<Session?> GetSessionAsync(TimeSpan cacheFor, CancellationToken ct)
    {
        if (_cached is not null && DateTime.UtcNow < _cachedUntilUtc)
            return _cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null && DateTime.UtcNow < _cachedUntilUtc)
                return _cached;

            var session = await TryGetSessionAsync(ct).ConfigureAwait(false);

            if (session is not null)
            {
                _cached = session;
                _cachedUntilUtc = DateTime.UtcNow + (cacheFor > TimeSpan.Zero ? cacheFor : TimeSpan.FromMinutes(20));
            }

            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 在夸克页面里执行一段 JS（用于 fetch），返回 JS 表达式的字符串结果。
    ///
    /// 为什么需要它：.NET 的 SChannel TLS 指纹与 Chromium 的 BoringSSL 不同，
    /// 万一夸克按指纹拦截，就让浏览器自己发请求 —— 指纹、Cookie、来源都天然正确。
    ///
    /// 要求有打开着的夸克页面（网页版标签页或客户端窗口）；没有则返回 null。
    /// </summary>
    public async Task<string?> EvaluateInPageAsync(string expression, CancellationToken ct)
    {
        var page = await FindQuarkPageAsync(ct).ConfigureAwait(false);
        if (page is null)
        {
            _log?.Warn("quark", "没有可用的夸克页面（网页版 pan.quark.cn 或客户端 uccd://cloud.quark），无法在页面内执行");
            return null;
        }

        _log?.Debug("quark", "在页面内执行 JS：" + page.Url);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        var request = JsonSerializer.Serialize(new
        {
            id = 1,
            method = "Runtime.evaluate",
            @params = new
            {
                expression,
                awaitPromise = true,
                returnByValue = true,
            },
        });

        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.Zero;

        try
        {
            await ws.ConnectAsync(new Uri(page.WebSocketUrl), timeoutCts.Token).ConfigureAwait(false);

            var payload = Encoding.UTF8.GetBytes(request);
            await ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true,
                timeoutCts.Token).ConfigureAwait(false);

            var text = await ReceiveFullMessageAsync(ws, timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(text)) return null;

            using var doc = JsonDocument.Parse(text);

            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : err.ToString();
                _log?.Warn("quark", "页面内执行失败：" + msg);
                return null;
            }

            if (!doc.RootElement.TryGetProperty("result", out var result)) return null;

            // 页面里抛异常时 value 不存在，只有 exceptionDetails
            if (result.TryGetProperty("exceptionDetails", out var ex))
            {
                var desc = ex.TryGetProperty("text", out var t) ? t.GetString() : ex.ToString();
                _log?.Warn("quark", "页面内 JS 异常：" + desc);
                return null;
            }

            if (result.TryGetProperty("result", out var inner) &&
                inner.TryGetProperty("value", out var value) &&
                value.ValueKind == JsonValueKind.String)
                return value.GetString();

            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.Warn("quark", "页面内执行超时（30 秒）");
            return null;
        }
        catch (Exception ex)
        {
            _log?.Warn("quark", "页面内执行失败：" + ex.Message);
            return null;
        }
    }

    private sealed record PageInfo(string Url, string WebSocketUrl);

    /// <summary>从 /json/list 里挑一个夸克页面目标（网页版或客户端）。</summary>
    private async Task<PageInfo?> FindQuarkPageAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{CdpHost}:{_port}/json/list", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            PageInfo? best = null;
            var bestScore = 0;

            foreach (var target in doc.RootElement.EnumerateArray())
            {
                var type = Str(target, "type");
                var url = Str(target, "url");
                var wsUrl = Str(target, "webSocketDebuggerUrl");

                if (type != "page" || wsUrl.Length == 0) continue;

                var score = QuarkPageScore(url);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new PageInfo(url, wsUrl);
                }
            }

            return best;
        }
        catch (Exception ex)
        {
            _log?.Debug("quark", "读取 /json/list 失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 判断一个 page target 像不像"夸克的页面"，返回优先级分值（0 = 不是）。
    ///
    /// 有两种形态，都必须认：
    ///  · 网页版 <c>https://pan.quark.cn/...</c>（在普通浏览器里登录时）
    ///  · 客户端 <c>uccd://cloud.quark/clouddrive/renderer/index.html</c>（夸克网盘 Electron 客户端）
    ///
    /// ⚠ 客户端的 URL 里是 <c>cloud.quark</c>，**不含 "quark.cn"** ——
    /// 早期只判断 Contains("quark.cn")，一旦用户从网页版切到客户端，
    /// 页面内执行就静默失效（取 Cookie 不受影响，那是 browser 级域）。
    /// </summary>
    private static int QuarkPageScore(string url)
    {
        // 网页版优先：同源就是 pan.quark.cn，页面内 fetch 不会撞 CORS
        if (url.Contains("pan.quark.cn", StringComparison.OrdinalIgnoreCase)) return 100;

        // 客户端主窗口
        if (url.StartsWith("uccd://cloud.quark/clouddrive", StringComparison.OrdinalIgnoreCase)) return 90;

        // 客户端其它窗口（pdf 预览、vip 页等），能用就行
        if (url.StartsWith("uccd://", StringComparison.OrdinalIgnoreCase)
            && url.Contains("quark", StringComparison.OrdinalIgnoreCase)) return 50;

        // 兜底：普通浏览器里的 quark.cn 子域
        if (url.Contains("quark.cn", StringComparison.OrdinalIgnoreCase)) return 40;

        return 0;
    }

    /// <summary>API 返回未登录时调用，强制下次重新握手。</summary>
    public void Invalidate()
    {
        _cached = null;
        _cachedUntilUtc = DateTime.MinValue;
        _log?.Debug("quark", "已清除 CDP 会话缓存");
    }

    // ---------------- 内部实现 ----------------

    private sealed record CookieItem(string Name, string Value, string Domain, string Path);

    /// <summary>
    /// 连 browser 级别的 WebSocket，发 Storage.getCookies。
    /// 注意：必须用 /devtools/browser/... 而不是 /devtools/page/...，
    /// 因为 Storage 是浏览器级域，page 通道上调用会报 -32601 method not found。
    /// </summary>
    private async Task<List<CookieItem>?> TryGetCookiesAsync(string wsUrl, CancellationToken ct, bool dedupe = true)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.Zero;

        try
        {
            await ws.ConnectAsync(new Uri(wsUrl), timeoutCts.Token).ConfigureAwait(false);

            const string request = """{"id":1,"method":"Storage.getCookies","params":{}}""";
            var payload = Encoding.UTF8.GetBytes(request);
            await ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true,
                timeoutCts.Token).ConfigureAwait(false);

            var text = await ReceiveFullMessageAsync(ws, timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(text))
            {
                _log?.Debug("quark", "Storage.getCookies 没有返回内容");
                return null;
            }

            return ParseCookies(text, dedupe);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.Debug("quark", "CDP Cookie 请求超时");
            return null;
        }
        catch (Exception ex)
        {
            _log?.Debug("quark", $"CDP WebSocket 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 收一条完整 CDP 消息。Cookie 多的时候一条消息会被拆成多帧，
    /// 只读第一帧会得到半截 JSON，解析必然失败。
    /// </summary>
    private static async Task<string> ReceiveFullMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        using var ms = new MemoryStream();

        while (true)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            ms.Write(buffer, 0, result.Count);

            // 8MB 是个防呆上限：正常 cookie 响应不会超过几百 KB
            if (ms.Length > 8 * 1024 * 1024) break;

            if (result.EndOfMessage) break;
        }

        if (ms.Length == 0) return "";
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private List<CookieItem>? ParseCookies(string json, bool dedupe = true)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : err.ToString();
                _log?.Warn("quark", "CDP 返回错误：" + msg);
                return null;
            }

            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("cookies", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<CookieItem>();

            foreach (var c in arr.EnumerateArray())
            {
                var name = Str(c, "name");
                if (string.IsNullOrEmpty(name)) continue;

                var domain = Str(c, "domain");
                if (!IsQuarkDomain(domain)) continue;

                list.Add(new CookieItem(name, Str(c, "value"), domain, Str(c, "path")));
            }

            if (!dedupe)
                return list.OrderBy(c => c.Name, StringComparer.Ordinal)
                    .ThenByDescending(c => c.Domain.Length).ToList();

            // 同名 cookie 可能来自不同子域/路径，取 domain 最具体、path 最长的那个。
            // ⚠ 这个规则是有意的：夸克把 __puus 挂在 .quark.cn 下，
            // 但 pan.quark.cn 也可能存一份 —— 两者若不同，取更具体的那个才对得上。
            return list
                .GroupBy(c => c.Name, StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(c => c.Domain.Length)
                               .ThenByDescending(c => c.Path.Length)
                               .First())
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            _log?.Debug("quark", "解析 CDP Cookie 失败：" + ex.Message);
            return null;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>只接受 quark.cn 及其子域，避免把别的站点凭证一起带出去。</summary>
    private static bool IsQuarkDomain(string domain)
    {
        var d = domain.TrimStart('.');
        return d.Equals("quark.cn", StringComparison.OrdinalIgnoreCase)
               || d.EndsWith(".quark.cn", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCookieHeader(IReadOnlyList<CookieItem> cookies) =>
        string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
