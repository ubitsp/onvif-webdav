using System.Net;
using System.Text;
using System.Text.Json;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.Quark;

namespace OnvifWebDav.Probe;

/// <summary>
/// 阶段 0.3 / 0.4：验证「能不能真的调通夸克 API」。
///
/// 只发两个只读请求（member 配额、file/sort 根目录），不发任何写操作。
/// 目标是回答一个致命问题：.NET 的 SChannel TLS 指纹会不会被夸克拦掉。
///  · 直连通 → 走 HttpClient 主路径
///  · 直连被拦但浏览器内 fetch 通 → 走 CDP 传输（UseBrowserTransport=true）
///  · 两个都不通 → 方案终止
/// </summary>
internal static class QuarkProbeRunner
{
    private const string PanOrigin = "https://pan.quark.cn";
    private const string PanReferer = "https://pan.quark.cn/";

    /// <summary>
    /// quark 客户端的 UA。有两个候选，都试一遍能看出服务端在意哪一个：
    ///  · browser：CDP 从夸克浏览器拿到的真实 UA
    ///  · client ：社区实现里用的 Electron 客户端 UA
    /// </summary>
    private const string ClientUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "quark-cloud-drive/2.5.20 Chrome/100.0.4896.160 Electron/18.3.5.4-b478491100 Safari/537.36 " +
        "Channel/pckk_other_ch";

    public static async Task<int> RunAsync(Dictionary<string, string> o)
    {
        var port = int.TryParse(o.GetValueOrDefault("port", "9222"), out var p) && p > 0 ? p : 9222;
        var baseUrl = (o.GetValueOrDefault("base") ?? "https://drive-pc.quark.cn").TrimEnd('/');
        var method = (o.GetValueOrDefault("method") ?? "GET").ToUpperInvariant();
        var matrix = o.ContainsKey("matrix");
        var viaBrowser = o.ContainsKey("via") &&
                         string.Equals(o["via"], "cdp", StringComparison.OrdinalIgnoreCase);
        var uaChoice = (o.GetValueOrDefault("ua") ?? "browser").ToLowerInvariant();

        var log = new AppLog(Path.Combine(Path.GetTempPath(), "owd-probe-logs"));
        using var cdp = new QuarkCdpClient(port, log);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Console.WriteLine("== 1) 取会话 ==");

        var session = await cdp.TryGetSessionAsync(cts.Token).ConfigureAwait(false);
        if (session is null)
        {
            Console.WriteLine("✘ 拿不到 Cookie，先跑 quark-cdp 确认浏览器调试端口和登录状态");
            return 1;
        }

        var ua = uaChoice == "client" ? ClientUa : session.UserAgent;
        var uaLabel = uaChoice == "client" ? "Electron 客户端" : "浏览器（CDP）";

        Console.WriteLine($"✔ Cookie {session.Cookie.Split(';').Length} 项，长度 {session.Cookie.Length}");
        Console.WriteLine($"  UA（{uaLabel}）：{Shorten(ua, 90)}");

        Console.WriteLine();
        Console.WriteLine($"== 2) 传输方式：{(viaBrowser ? "浏览器内 fetch（CDP）" : "HttpClient 直连")} ==");

        var memberPath = "/1/clouddrive/member?pr=ucpro&fr=pc&uc_param_str=&fetch_subscribe=true&_ch=home&fetch_identity=true";
        var sortPath = "/1/clouddrive/file/sort?pr=ucpro&fr=pc&pdir_fid=0&_page=1&_size=10&_fetch_total=1&_fetch_sub_dirs=0&_sort=file_type:asc,updated_at:desc,";

        // ---- 矩阵扫描：host × method 全试，只打 member，找出可用组合 ----
        if (matrix)
        {
            Console.WriteLine("== 矩阵扫描：host × method（只打 member）==");

            string? goodHost = null;
            var goodMethod = "";

            foreach (var host in new[] { "https://drive-pc.quark.cn", "https://drive.quark.cn" })
            {
                foreach (var m in new[] { "GET", "POST" })
                {
                    var r = await SendAsync(cdp, host, memberPath, m, session, ua, viaBrowser, cts.Token)
                        .ConfigureAwait(false);

                    Console.WriteLine($"  {host,-32} {m,-4} → HTTP {r.HttpStatus}" +
                                      (r.EnvelopeStatus >= 0 ? $" 信封 {r.EnvelopeStatus}/code {r.EnvelopeCode}" : "") +
                                      (r.Ok ? "  ✔" : "  ✘") +
                                      (r.EnvelopeMessage.Length > 0 ? $"  {Shorten(r.EnvelopeMessage, 60)}" : ""));

                    if (r.Ok && goodHost is null)
                    {
                        goodHost = host;
                        goodMethod = m;
                    }
                }
            }

            Console.WriteLine();

            if (goodHost is null)
            {
                Console.WriteLine("✘ 矩阵里没有一个组合成功。");
                Console.WriteLine("  说明不是 host/method 问题，而是传输层或会话被拦。");
                Console.WriteLine("  下一步跑：probe quark-probe --via cdp --matrix");
                return 1;
            }

            Console.WriteLine($"✅ 可用组合：{goodHost} + {goodMethod}");
            baseUrl = goodHost;
            method = goodMethod;
            Console.WriteLine();
        }

        var member = await SendAsync(cdp, baseUrl, memberPath, method, session, ua, viaBrowser, cts.Token)
            .ConfigureAwait(false);
        var sort = await SendAsync(cdp, baseUrl, sortPath, method, session, ua, viaBrowser, cts.Token)
            .ConfigureAwait(false);

        // ---------------- 输出 ----------------

        Console.WriteLine();
        Console.WriteLine("== 3) /1/clouddrive/member（配额）==");
        DumpResult(member);

        Console.WriteLine();
        Console.WriteLine("== 4) /1/clouddrive/file/sort（根目录）==");
        DumpResult(sort);

        // ---------------- 结论 ----------------

        Console.WriteLine();
        Console.WriteLine("== 结论 ==");

        var ok = member.Ok && sort.Ok;

        if (ok)
        {
            Console.WriteLine("✅ 两个只读请求都通过");
            Console.WriteLine(viaBrowser
                ? "   → 浏览器内 fetch 可用。若直连（不带 --via）失败，配置里把 UseBrowserTransport 设为 true。"
                : "   → HttpClient 直连可用。配置里 UseBrowserTransport 保持 false。");

            DumpCapacity(member);
            DumpRootEntries(sort);

            Console.WriteLine();
            Console.WriteLine("下一步：probe quark-selftest --write 跑一次真实写入（建目录 → 传 15 字节 → 读回 → 删）。");
            return 0;
        }

        Console.WriteLine("✘ 未通过。诊断信息：");
        foreach (var line in member.Diagnostics.Concat(sort.Diagnostics))
            Console.WriteLine("   " + line);

        if (!viaBrowser)
        {
            Console.WriteLine();
            Console.WriteLine("若上面出现 TLS/连接重置/持续 403，说明 .NET 的 TLS 指纹被拦。");
            Console.WriteLine("下一步跑：probe quark-probe --via cdp");
            Console.WriteLine("  （用浏览器自身发请求，指纹就是浏览器的，通常能绕过）");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("浏览器内 fetch 也不通，说明问题不在传输层：");
            Console.WriteLine("  · 检查 Cookie 是否过期（重新登录夸克网盘）");
            Console.WriteLine("  · 检查 base URL 是否正确（当前 " + baseUrl + "）");
            Console.WriteLine("  · 若返回 412/非 JSON，可能是风控要求额外参数，方案需要重新评估");
        }

        return 1;
    }

    // ---------------- 传输分发 ----------------

    private static Task<ProbeResult> SendAsync(QuarkCdpClient cdp, string baseUrl, string path, string method,
        QuarkCdpClient.Session session, string ua, bool viaBrowser, CancellationToken ct) =>
        viaBrowser
            ? SendViaBrowserAsync(cdp, baseUrl, path, method, session, ua, ct)
            : SendDirectAsync(baseUrl, path, method, session, ua, ct);

    // ---------------- HttpClient 直连 ----------------

    private sealed class ProbeResult
    {
        public bool Ok;
        public int HttpStatus;
        public int EnvelopeStatus = -1;
        public int EnvelopeCode = -1;
        public string EnvelopeMessage = "";
        public List<string> DataFields = new();
        public string RawBody = "";
        public List<string> Diagnostics = new();
        public JsonElement? Data;
    }

    private static async Task<ProbeResult> SendDirectAsync(string baseUrl, string path, string method,
        QuarkCdpClient.Session session, string ua, CancellationToken ct)
    {
        var result = new ProbeResult();

        try
        {
            // 关键：不复用连接池，避免上一次失败的半死连接污染判断
            using var handler = new HttpClientHandler
            {
                UseProxy = false,
                AllowAutoRedirect = true,
            };

            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

            var verb = string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
                ? HttpMethod.Post
                : HttpMethod.Get;

            using var req = new HttpRequestMessage(verb, baseUrl + path);

            // Cookie / UA 都必须用 TryAddWithoutValidation：
            // HttpClient 默认会拒绝某些"由客户端库管理"的头，直接 Add 会抛 InvalidOperationException。
            req.Headers.TryAddWithoutValidation("Cookie", session.Cookie);
            req.Headers.TryAddWithoutValidation("User-Agent", ua);
            req.Headers.TryAddWithoutValidation("Referer", PanReferer);
            req.Headers.TryAddWithoutValidation("Origin", PanOrigin);
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");

            // GET 不带 body；只有 POST 才需要 JSON 空对象
            if (verb == HttpMethod.Post)
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

            result.HttpStatus = (int)resp.StatusCode;
            result.RawBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // 记录服务端是否下发了新 cookie（滚动续期的证据）
            if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                var names = setCookies
                    .Select(v => v.Split('=', 2)[0].Trim())
                    .Where(n => n.Length > 0)
                    .Distinct()
                    .ToList();

                if (names.Count > 0)
                    result.Diagnostics.Add($"[信息] 服务端下发了 Set-Cookie：{string.Join(", ", names)}（滚动续期可用）");
            }

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                result.Diagnostics.Add("[失败] HTTP 401 —— Cookie 无效或已过期");
                return result;
            }

            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                result.Diagnostics.Add("[失败] HTTP 403 —— 可能是风控/WAF 拦截（重点看是否为 TLS 指纹）");
                return result;
            }

            if (resp.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                result.Diagnostics.Add("[失败] HTTP 412 —— 典型的设备/环境校验未通过");
                return result;
            }

            ParseEnvelope(result);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            result.Diagnostics.Add("[失败] 请求超时（20 秒）—— 连接被挂起，常表现为风控静默丢包");
        }
        catch (HttpRequestException ex)
        {
            // TLS 握手失败、连接被重置都会落到这里
            result.Diagnostics.Add($"[失败] 传输层异常：{ex.Message}");
            if (ex.InnerException is not null)
                result.Diagnostics.Add($"    内层：{ex.InnerException.Message}");
        }
        catch (Exception ex)
        {
            result.Diagnostics.Add($"[失败] {ex.GetType().Name}：{ex.Message}");
        }

        return result;
    }

    // ---------------- 浏览器内 fetch（CDP） ----------------

    private static async Task<ProbeResult> SendViaBrowserAsync(QuarkCdpClient cdp, string baseUrl,
        string path, string method, QuarkCdpClient.Session session, string ua, CancellationToken ct)
    {
        var result = new ProbeResult();

        var url = baseUrl + path;
        var verb = method.ToUpperInvariant() == "POST" ? "POST" : "GET";

        // 在浏览器里跑 fetch：Cookie 走 credentials:'include' 自动带上，
        // TLS 指纹就是浏览器自己的，绕开 SChannel 指纹差异。
        var js = $$"""
            (async () => {
              try {
                const opt = { method: {{JsonSerializer.Serialize(verb)}}, credentials: 'include' };
                if ({{JsonSerializer.Serialize(verb)}} === 'POST') {
                  opt.headers = { 'Content-Type': 'application/json' };
                  opt.body = '{}';
                }
                const r = await fetch({{JsonSerializer.Serialize(url)}}, opt);
                const text = await r.text();
                return JSON.stringify({ status: r.status, body: text });
              } catch (e) {
                return JSON.stringify({ status: -1, body: '', error: String(e) });
              }
            })()
            """;

        var reply = await cdp.EvaluateInPageAsync(js, ct).ConfigureAwait(false);

        if (reply is null)
        {
            result.Diagnostics.Add("[失败] 无法在浏览器里执行 fetch（可能没有打开的夸克页面）");
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;

            result.HttpStatus = root.TryGetProperty("status", out var s) ? s.GetInt32() : -1;

            if (result.HttpStatus == -1)
            {
                var err = root.TryGetProperty("error", out var e) ? e.GetString() : "未知";
                result.Diagnostics.Add("[失败] 浏览器内 fetch 异常：" + err);
                return result;
            }

            result.RawBody = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            ParseEnvelope(result);
        }
        catch (Exception ex)
        {
            result.Diagnostics.Add("[失败] 解析浏览器返回失败：" + ex.Message);
        }

        return result;
    }

    // ---------------- 信封解析 ----------------

    /// <summary>
    /// 夸克统一信封：{"status":200,"code":0,"data":{...},"message":""}
    /// status 和 code 都要判，历史上出现过 status=200 但 code!=0 的情况。
    /// </summary>
    private static void ParseEnvelope(ProbeResult result)
    {
        if (string.IsNullOrWhiteSpace(result.RawBody))
        {
            result.Diagnostics.Add("[失败] 响应体为空");
            return;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(result.RawBody);
        }
        catch (JsonException)
        {
            // 非 JSON 通常意味着被 WAF 拦了，返回了 HTML 错误页
            result.Diagnostics.Add("[失败] 响应不是 JSON（多半是网关/WAF 的 HTML 错误页）");
            result.Diagnostics.Add("    前 200 字符：" + Shorten(result.RawBody, 200));
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;

            result.EnvelopeStatus = Int(root, "status");
            result.EnvelopeCode = Int(root, "code");
            result.EnvelopeMessage = root.TryGetProperty("message", out var m)
                ? m.GetString() ?? ""
                : "";

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                result.Data = data.Clone();
                result.DataFields = data.EnumerateObject().Select(x => x.Name).ToList();
            }

            // 200 且 code==0 视为成功；有些接口只回 status 不回 code
            result.Ok = result.HttpStatus is >= 200 and < 300
                        && result.EnvelopeStatus is 200 or -1
                        && result.EnvelopeCode is 0 or -1;

            if (!result.Ok)
            {
                result.Diagnostics.Add(
                    $"[失败] HTTP {result.HttpStatus}，信封 status={result.EnvelopeStatus} code={result.EnvelopeCode}" +
                    (result.EnvelopeMessage.Length > 0 ? $" message={result.EnvelopeMessage}" : ""));

                if (result.EnvelopeCode == 31001 || result.EnvelopeMessage.Contains("登录", StringComparison.Ordinal))
                    result.Diagnostics.Add("    → code 31001 / 提示登录：会话已失效，需要重新登录夸克网盘");
            }
        }
    }

    private static int Int(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return -1;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n : -1,
            JsonValueKind.String => int.TryParse(v.GetString(), out var s) ? s : -1,
            _ => -1,
        };
    }

    // ---------------- 输出辅助 ----------------

    private static void DumpResult(ProbeResult r)
    {
        Console.WriteLine($"  HTTP {(r.HttpStatus == 0 ? "(未发出)" : r.HttpStatus.ToString())}" +
                          (r.EnvelopeStatus >= 0 ? $"｜信封 status={r.EnvelopeStatus}" : "") +
                          (r.EnvelopeCode >= 0 ? $" code={r.EnvelopeCode}" : "") +
                          (r.Ok ? "  ✔" : "  ✘"));

        if (r.DataFields.Count > 0)
            Console.WriteLine("  data 字段：" + string.Join(", ", r.DataFields.Take(15)) +
                              (r.DataFields.Count > 15 ? " …" : ""));

        foreach (var d in r.Diagnostics)
            Console.WriteLine("  " + d);
    }

    private static void DumpCapacity(ProbeResult member)
    {
        if (member.Data is not { } data) return;

        var used = Long(data, "use_capacity");
        var total = Long(data, "total_capacity");

        if (used < 0 || total < 0) return;

        const double gb = 1024d * 1024 * 1024;
        Console.WriteLine();
        Console.WriteLine($"容量：已用 {used / gb:F2} GB / 总 {total / gb:F2} GB" +
                          $"（剩余 {(total - used) / gb:F2} GB）");
    }

    private static void DumpRootEntries(ProbeResult sort)
    {
        if (sort.Data is not { } data) return;
        if (!data.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return;

        var count = list.GetArrayLength();
        Console.WriteLine($"根目录首屏条目：{count} 项");

        foreach (var item in list.EnumerateArray().Take(5))
        {
            var name = item.TryGetProperty("file_name", out var n) ? n.GetString() : "?";
            var isDir = item.TryGetProperty("dir", out var d) && d.ValueKind == JsonValueKind.True;
            Console.WriteLine($"  · {(isDir ? "[目录] " : "")}{name}");
        }
    }

    private static long Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return -1;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n : -1,
            JsonValueKind.String => long.TryParse(v.GetString(), out var s) ? s : -1,
            _ => -1,
        };
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
