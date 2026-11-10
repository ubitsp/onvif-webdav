using OnvifWebDav.Diagnostics;
using OnvifWebDav.Quark;

namespace OnvifWebDav.Probe;

/// <summary>
/// 诊断"能不能自动把夸克拉起来"。
///
/// 查三件事，**不动任何进程**（不会启动也不会杀）：
///  1. 现在有没有夸克在跑
///  2. 能不能定位到 quark.exe（配置值 → 运行中进程 → 常见路径）
///  3. 调试端口通不通
///
/// 这三条决定了 AppRuntime.EnsureQuarkDebugPortAsync 会走哪个分支。
/// </summary>
internal static class QuarkLaunchProbeRunner
{
    public static async Task<int> RunAsync(Dictionary<string, string> o)
    {
        var port = int.TryParse(o.GetValueOrDefault("port", "9222"), out var p) && p > 0 ? p : 9222;
        var configured = o.GetValueOrDefault("exe");

        var log = new AppLog(Path.Combine(Path.GetTempPath(), "owd-probe-logs"));

        Console.WriteLine("== 1) 夸克进程 ==");

        var running = QuarkLauncher.HasRunningProcess();
        Console.WriteLine(running ? "  ✔ 正在运行" : "  · 没有运行");
        Console.WriteLine(running
            ? "    → 如果端口不通，自动启动是无效的（单实例会忽略新参数），必须先完全退出"
            : "    → 端口不通时可以直接自动启动");

        Console.WriteLine();
        Console.WriteLine("== 2) 定位 quark.exe ==");

        if (!string.IsNullOrWhiteSpace(configured))
            Console.WriteLine($"  配置值：{configured}（存在={File.Exists(configured)}）");
        else
            Console.WriteLine("  配置值：（空）");

        var exe = QuarkLauncher.TryFindExe(configured);

        if (exe is null)
        {
            Console.WriteLine("  ✘ 找不到。绿色版请手动填路径。");
        }
        else
        {
            Console.WriteLine($"  ✔ {exe}");
            Console.WriteLine($"    版本信息：{Describe(exe)}");
        }

        Console.WriteLine();
        Console.WriteLine($"== 3) 调试端口 {port} ==");

        string? version = null;

        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                Timeout = TimeSpan.FromSeconds(3),
            };

            using var resp = await http.GetAsync($"http://127.0.0.1:{port}/json/version")
                .ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
                version = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch
        {
            // 端口没开，正常
        }

        if (version is null)
        {
            Console.WriteLine("  ✘ 不通");
        }
        else
        {
            var browser = Extract(version, "Browser");
            Console.WriteLine($"  ✔ 通：{browser}");
        }

        Console.WriteLine();
        Console.WriteLine("== 结论 ==");

        if (version is not null)
            Console.WriteLine("✅ 现在就能取会话（端口已就绪）");
        else if (running)
            Console.WriteLine("⚠ 夸克在跑但端口不通 —— 需要你完全退出夸克（托盘也退）后重试，程序无法接管已运行的实例");
        else if (exe is not null)
            Console.WriteLine("✅ 可以直接自动启动：端口不通 → 用这个路径拉起 → 等端口就绪");
        else
            Console.WriteLine("✘ 既没运行、也找不到 exe —— 只能手动填路径或手动启动");

        return 0;
    }

    private static string Describe(string exe)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            return info.FileVersion ?? info.ProductVersion ?? "（无）";
        }
        catch
        {
            return "（读不到）";
        }
    }

    private static string Extract(string json, string key)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }
}
