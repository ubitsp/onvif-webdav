using System.Text.Json;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.Quark;

namespace OnvifWebDav.Probe;

/// <summary>
/// 检测「夸克页面里到底存了什么认证信息」。
///
/// 起因：用户机器上跑的是夸克网盘客户端（uccd://cloud.quark），而 Cookie 是从
/// browser 级 Storage.getCookies 拿的，域名是 quark.cn —— 说明 Cookie 并不挂在
/// uccd 这个 origin 下。那么页面自己（document.cookie / localStorage）能不能作为
/// 取会话的来源，就需要实测。
///
/// 只打印名字和长度，不打印值 —— 这些等价于账号凭证。
/// </summary>
internal static class QuarkPageProbeRunner
{
    private const string InspectJs = """
        (() => {
          const out = {
            url: location.href,
            origin: String(location.origin),
            protocol: String(location.protocol),
            hasDocumentCookie: typeof document !== 'undefined',
            cookieLen: 0,
            cookieNames: [],
            local: [],
            session: [],
            idb: []
          };

          try {
            const c = (typeof document !== 'undefined' && document.cookie) || '';
            out.cookieLen = c.length;
            out.cookieNames = c.split(';').map(s => s.trim().split('=')[0]).filter(Boolean);
          } catch (e) { out.cookieError = String(e); }

          try {
            for (let i = 0; i < localStorage.length; i++) {
              const k = localStorage.key(i);
              const v = localStorage.getItem(k) || '';
              out.local.push({ k: k, len: v.length });
            }
          } catch (e) { out.localError = String(e); }

          try {
            for (let i = 0; i < sessionStorage.length; i++) {
              const k = sessionStorage.key(i);
              const v = sessionStorage.getItem(k) || '';
              out.session.push({ k: k, len: v.length });
            }
          } catch (e) { out.sessionError = String(e); }

          // indexedDB 是异步的，这里只同步列名字（够判断有没有）
          try {
            if (indexedDB && indexedDB.databases) {
              return indexedDB.databases().then(list => {
                out.idb = (list || []).map(d => d.name + ' v' + d.version);
                return JSON.stringify(out);
              }).catch(() => JSON.stringify(out));
            }
          } catch (e) { out.idbError = String(e); }

          return JSON.stringify(out);
        })()
        """;

    public static async Task<int> RunAsync(Dictionary<string, string> o)
    {
        var port = int.TryParse(o.GetValueOrDefault("port", "9222"), out var p) && p > 0 ? p : 9222;
        var log = new AppLog(Path.Combine(Path.GetTempPath(), "owd-probe-logs"));

        using var cdp = new QuarkCdpClient(port, log);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // 先看 Cookie 挂在哪个域。这一步决定"页面能不能自己读到 Cookie"
        Console.WriteLine("== 0) Cookie 归属域（browser 级 Storage.getCookies）==");

        var cookies = await cdp.DescribeCookiesAsync(cts.Token).ConfigureAwait(false);

        if (cookies is null || cookies.Count == 0)
        {
            Console.WriteLine("  ✘ 一个都没取到 —— 要么没登录，要么域过滤规则不对");
        }
        else
        {
            foreach (var g in cookies.GroupBy(c => c.Domain).OrderByDescending(g => g.Count()))
                Console.WriteLine($"  {g.Key,-18} {g.Count(),3} 项   值长度合计 {g.Sum(c => c.ValueLength)}");

            Console.WriteLine($"  合计 {cookies.Count} 项（已按 quark.cn 域过滤）");

            // 关键：__puus 是不是唯一一份。如果多个域各有一份且长度不同，
            // 去重规则（取域名最具体）选错就会拿到对不上的令牌。
            var keyNames = new[] { "__puus", "__pus", "ctoken", "__ktd" };

            foreach (var name in keyNames)
            {
                var hits = cookies.Where(c => c.Name == name).ToList();

                if (hits.Count == 0)
                {
                    Console.WriteLine($"  {name,-8}✘ 缺失");
                    continue;
                }

                var desc = string.Join(" ｜ ", hits.Select(h => $"{h.Domain}({h.ValueLength})"));
                var flag = hits.Count > 1
                    ? (hits.Select(h => h.ValueLength).Distinct().Count() > 1
                        ? "  ⚠ 多份且长度不同，去重可能取错"
                        : "  （多份，长度一致）")
                    : "";

                Console.WriteLine($"  {name,-8}{hits.Count} 份：{desc}{flag}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("== 1) 选中哪个页面 ==");

        var raw = await cdp.EvaluateInPageAsync(InspectJs, cts.Token).ConfigureAwait(false);

        if (raw is null)
        {
            Console.WriteLine("✘ 找不到夸克页面（网页版或客户端都没开着）");
            return 1;
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        Console.WriteLine("  URL      ：" + Str(root, "url"));
        Console.WriteLine("  origin   ：" + Str(root, "origin"));
        Console.WriteLine("  protocol ：" + Str(root, "protocol"));
        Console.WriteLine();

        Console.WriteLine("== 2) document.cookie（页面自己能不能看到 Cookie）==");

        if (root.TryGetProperty("cookieError", out var ce))
        {
            Console.WriteLine("  ✘ 读取异常：" + ce.GetString());
        }
        else
        {
            var len = root.TryGetProperty("cookieLen", out var cl) ? cl.GetInt32() : 0;
            Console.WriteLine($"  长度 {len} 字符");

            if (root.TryGetProperty("cookieNames", out var names) && names.ValueKind == JsonValueKind.Array)
            {
                var list = names.EnumerateArray().Select(x => x.GetString()).Where(x => x is { Length: > 0 }).ToList();

                if (list.Count == 0) Console.WriteLine("  （空 —— HttpOnly 的凭证不会出现在这里）");
                else Console.WriteLine("  名称：" + string.Join(", ", list.Take(30)));
            }
        }

        Console.WriteLine();
        Console.WriteLine("== 3) localStorage ==");
        DumpStorage(root, "local", "localError");

        Console.WriteLine();
        Console.WriteLine("== 4) sessionStorage ==");
        DumpStorage(root, "session", "sessionError");

        Console.WriteLine();
        Console.WriteLine("== 5) IndexedDB ==");

        if (root.TryGetProperty("idb", out var idb) && idb.ValueKind == JsonValueKind.Array)
        {
            var list = idb.EnumerateArray().Select(x => x.GetString()).ToList();
            Console.WriteLine(list.Count == 0 ? "  （无）" : "  " + string.Join("\n  ", list!));
        }
        else
        {
            Console.WriteLine("  （查询失败或浏览器不支持 databases()）");
        }

        Console.WriteLine();
        Console.WriteLine("== 结论 ==");
        Console.WriteLine("  Cookie 走的是 browser 级 Storage.getCookies（域 quark.cn），");
        Console.WriteLine("  页面的 document.cookie 通常为空 —— 关键凭证是 HttpOnly。");
        Console.WriteLine("  所以取值入口应该保持 browser 级，页面只用来做补充探测。");

        return 0;
    }

    private static void DumpStorage(JsonElement root, string arrayName, string errorName)
    {
        if (root.TryGetProperty(errorName, out var err))
        {
            Console.WriteLine("  ✘ 读取异常：" + err.GetString());
            return;
        }

        if (!root.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine("  （无）");
            return;
        }

        var items = arr.EnumerateArray().ToList();
        Console.WriteLine($"  {items.Count} 项");

        foreach (var it in items.Take(40))
        {
            var key = it.TryGetProperty("k", out var k) ? k.GetString() : "?";
            var len = it.TryGetProperty("len", out var l) ? l.GetInt32() : 0;
            Console.WriteLine($"    · {key}（{len} 字符）");
        }

        if (items.Count > 40) Console.WriteLine($"    …还有 {items.Count - 40} 项");
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
