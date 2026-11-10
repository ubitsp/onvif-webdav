using System.Diagnostics;
using System.Net.Http;
using OnvifWebDav.Diagnostics;

namespace OnvifWebDav.Quark;

/// <summary>
/// 把夸克客户端以「调试模式」拉起来。
///
/// 为什么需要：夸克是单实例应用，**已经运行的进程会直接忽略新的命令行参数**。
/// 所以想拿到 CDP 端口，必须：先完全退出 → 再带 --remote-debugging-port 启动。
/// 手动做这件事很烦，这里自动化掉。
///
/// ⚠ 能自动做的只有"进程没在跑"这一种情况。
/// 如果夸克已经在跑（用户平时就这么用），我们**不会**去杀它 —— 那会丢用户正在做的事，
/// 只能在日志/界面里明确提示"请完全退出后重试"。
/// </summary>
public static class QuarkLauncher
{
    /// <summary>
    /// 找 quark.exe。优先级：配置里填的 → 运行中的进程 → 常见安装路径。
    ///
    /// ⚠ 绿色版（解压到任意目录那种）**只能靠前两条**：它不在 Program Files，
    /// 也不在 %LOCALAPPDATA%\Programs，硬猜路径永远猜不到。
    /// </summary>
    public static string? TryFindExe(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        // 1) 已经在跑的进程 —— 最可靠（说明路径确实存在）
        foreach (var name in new[] { "quark", "Quark" })
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
                catch
                {
                    // 跨位数/权限不足时读不到 MainModule，忽略
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        // 2) 常见安装路径
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var candidates = new[]
        {
            Path.Combine(localAppData, "Programs", "Quark", "quark.exe"),
            Path.Combine(localAppData, "Quark", "quark.exe"),
            Path.Combine(localAppData, "Programs", "quark", "quark.exe"),
            Path.Combine(programFiles, "Quark", "quark.exe"),
            Path.Combine(programFilesX86, "Quark", "quark.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>有没有夸克进程在跑。跑着的话自动启动一定是无效的（单实例）。</summary>
    public static bool HasRunningProcess()
    {
        foreach (var name in new[] { "quark", "Quark" })
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length == 0) continue;

                foreach (var p in procs) p.Dispose();
                return true;
            }
            catch
            {
                // 查不到就当没在跑，交给后面的端口探测兜底
            }
        }

        return false;
    }

    /// <summary>带调试端口启动。返回 false 表示进程没起来（路径不对/权限不足）。</summary>
    public static bool Launch(string exePath, int port, AppLog? log = null)
    {
        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                Arguments = $"--remote-debugging-port={port}",
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
            };

            using var p = Process.Start(psi);
            return p is not null;
        }
        catch (Exception ex)
        {
            log?.Error("quark", $"启动夸克失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 等到调试端口真的能应答。
    ///
    /// 只等进程存在是不够的：Electron 启动到 DevTools 端口就绪要几秒，
    /// 客户端冷启动（尤其要加载网盘首页）实测 3~8 秒，这里给到 timeout。
    /// </summary>
    public static async Task<bool> WaitForPortAsync(int port, TimeSpan timeout, AppLog? log = null,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;

        using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(3),
        };

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var resp = await http.GetAsync($"http://127.0.0.1:{port}/json/version", ct)
                    .ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    log?.Info("quark", $"调试端口 {port} 已就绪");
                    return true;
                }
            }
            catch
            {
                // 还没起来，继续等
            }

            await Task.Delay(600, ct).ConfigureAwait(false);
        }

        log?.Error("quark", $"等了 {timeout.TotalSeconds:F0} 秒，调试端口 {port} 仍不可用");
        return false;
    }
}
