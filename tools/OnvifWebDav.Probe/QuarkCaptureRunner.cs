using System.Text.Json;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.Quark;

namespace OnvifWebDav.Probe;

/// <summary>
/// 抓夸克网页版上传时的真实请求。
///
/// 为什么需要它：`/1/clouddrive/file/upload/auth` 要求一个 `auth_meta` 参数，
/// 结构官方没公开，按 PDS 语义猜出来的四种形态全部 500 inner error。
/// 与其继续猜，不如在页面里 hook fetch/XHR，让网页自己上传一次，把参数原样录下来。
///
/// 用法：先跑本命令（注入 hook），然后**在夸克网页里手动上传任意一个文件**，
/// 命令会把命中的请求连 URL 带 body 打出来，抓到 upload/auth 后自动结束。
/// </summary>
internal static class QuarkCaptureRunner
{
    /// <summary>
    /// 同时 hook fetch 和 XHR：夸克用的是 axios，底层可能是 XHR；
    /// 但新版页面大量用 fetch，两个都挂才不漏。
    /// 记录上限 200 条，避免长时间挂机把页面内存撑爆。
    /// </summary>
    private const string HookJs = """
        (() => {
          if (window.__owdHooked) return 'already';
          window.__owdHooked = true;
          window.__owdLog = [];

          const push = (o) => {
            try {
              const log = window.__owdLog;
              if (log.length > 200) log.shift();
              log.push(o);
            } catch (e) {}
          };

          const of = window.fetch;
          window.fetch = function (input, init) {
            try {
              let url = '';
              let method = 'GET';
              if (typeof input === 'string') url = input;
              else if (input && input.url) { url = input.url; method = input.method || 'GET'; }

              if (init && init.method) method = init.method;

              let body = null;
              if (init && typeof init.body === 'string') body = init.body;
              else if (init && init.body) body = '[binary ' + (init.body.size || '?') + ' bytes]';

              push({ k: 'fetch', m: String(method).toUpperCase(), u: String(url), b: body });
            } catch (e) {}
            return of.apply(this, arguments);
          };

          const xop = XMLHttpRequest.prototype.open;
          const xse = XMLHttpRequest.prototype.send;

          XMLHttpRequest.prototype.open = function (m, u) {
            this.__owd = { m: String(m || 'GET').toUpperCase(), u: String(u || '') };
            return xop.apply(this, arguments);
          };

          XMLHttpRequest.prototype.send = function (b) {
            try {
              const r = this.__owd || {};
              let body = null;
              if (typeof b === 'string') body = b;
              else if (b) body = '[binary]';
              push({ k: 'xhr', m: r.m, u: r.u, b: body });
            } catch (e) {}
            return xse.apply(this, arguments);
          };

          return 'ok';
        })()
        """;

    /// <summary>只保留业务请求，静态资源和埋点刷屏没意义。</summary>
    private static bool IsInteresting(string url) =>
        url.Contains("/1/clouddrive/", StringComparison.Ordinal) ||
        url.Contains("upload", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("oss", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(Dictionary<string, string> o)
    {
        var port = int.TryParse(o.GetValueOrDefault("port", "9222"), out var p) && p > 0 ? p : 9222;
        var seconds = int.TryParse(o.GetValueOrDefault("seconds", "240"), out var s) && s > 0 ? s : 240;

        var log = new AppLog(Path.Combine(Path.GetTempPath(), "owd-probe-logs"));
        using var cdp = new QuarkCdpClient(port, log);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

        Console.WriteLine("== 注入请求 hook ==");

        var install = await cdp.EvaluateInPageAsync(HookJs, cts.Token).ConfigureAwait(false);
        if (install is null)
        {
            Console.WriteLine("✘ 注入失败：浏览器里没有打开的 quark.cn 页面");
            return 1;
        }

        Console.WriteLine($"  ✔ hook 已就位（{install}）");
        Console.WriteLine();
        Console.WriteLine(">>> 现在请在夸克网页里上传任意一个小文件（几 KB 就行）<<<");
        Console.WriteLine($"    最多等 {seconds} 秒，抓到 upload/auth 后自动结束。");
        Console.WriteLine();

        var foundAuth = false;
        var authIdleRounds = 0;

        while (!cts.IsCancellationRequested)
        {
            string? raw;
            try
            {
                raw = await cdp
                    .EvaluateInPageAsync("JSON.stringify((window.__owdLog||[]).splice(0,50))", cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (raw is not null && raw != "[]")
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);

                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        var url = item.TryGetProperty("u", out var u) ? u.GetString() ?? "" : "";
                        if (!IsInteresting(url)) continue;

                        var kind = item.TryGetProperty("k", out var k) ? k.GetString() : "?";
                        var method = item.TryGetProperty("m", out var m) ? m.GetString() : "?";
                        var body = item.TryGetProperty("b", out var b) && b.ValueKind == JsonValueKind.String
                            ? b.GetString()
                            : null;

                        // 打印时把 query 留着、路径截短，主要是 body 有价值
                        Console.WriteLine($"[{kind}] {method} {Shorten(url, 150)}");

                        if (!string.IsNullOrWhiteSpace(body))
                            Console.WriteLine("      body: " + body);

                        if (url.Contains("upload/auth", StringComparison.Ordinal))
                        {
                            foundAuth = true;
                            authIdleRounds = 0;
                        }
                    }
                }
                catch (JsonException)
                {
                    // 页面刷新导致 hook 丢失时会拿到乱七八糟的值，忽略即可
                }
            }

            // 抓到 auth 后再多收两轮，把紧随其后的 PUT 也录下来
            if (foundAuth)
            {
                authIdleRounds++;
                if (authIdleRounds > 6) break;
            }

            await Task.Delay(700, cts.Token).ConfigureAwait(false);
        }

        Console.WriteLine();

        if (!foundAuth)
        {
            Console.WriteLine("✘ 没抓到 upload/auth。可能原因：");
            Console.WriteLine("  1) 这段时间里没有真的上传文件");
            Console.WriteLine("  2) 页面在上传前刷新过，hook 被清掉了（重跑本命令即可）");
            Console.WriteLine("  3) 夸克改用了 Web Worker / Service Worker 发请求（hook 覆盖不到）");
            return 1;
        }

        Console.WriteLine("✅ 抓到 upload/auth 请求，按上面 body 里的 auth_meta 结构实现即可。");
        return 0;
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
