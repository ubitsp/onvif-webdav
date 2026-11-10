using System.Text;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.Quark;
using OnvifWebDav.Storage;

namespace OnvifWebDav.Probe;

/// <summary>
/// 验证"同名写入是覆盖还是产生副本"。
///
/// 背景：夸克的 file/upload/pre 遇到同名文件不会覆盖，finish 会生成
/// `name(1).ext`。而所有按路径读的地方都按名字精确匹配，于是永远读到最初那份。
/// 索引文件每天被覆盖几十次，表现就是"一天只有一个视频"。
///
/// 这个命令就是那条 bug 的回归测试：同一个文件连写三次，
/// 目录里必须始终只有 1 个文件，且内容是最新那次。
/// </summary>
internal static class QuarkOverwriteRunner
{
    public static async Task<int> RunAsync(Dictionary<string, string> o)
    {
        var port = int.TryParse(o.GetValueOrDefault("port", "9222"), out var p) && p > 0 ? p : 9222;
        var log = new AppLog(Path.Combine(Path.GetTempPath(), "owd-probe-logs"));
        using var cdp = new QuarkCdpClient(port, log);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var session = await cdp.TryGetSessionAsync(cts.Token).ConfigureAwait(false);
        if (session is null)
        {
            Console.WriteLine("✘ 拿不到会话。先跑 quark-cdp 确认客户端在跑且已登录。");
            return 1;
        }

        using var api = new QuarkApiClient(session, log);
        using var storage = new QuarkStorage(api, log, null, null);

        var dir = "/owd-overwrite-" + DateTime.Now.ToString("MMddHHmmss");
        var file = dir + "/dup.json";
        var failures = 0;

        try
        {
            Console.WriteLine($"== 测试目录 {dir} ==");
            await storage.EnsureDirectoryAsync(dir, cts.Token).ConfigureAwait(false);
            Console.WriteLine("  ✔ 目录已建");

            for (var round = 1; round <= 3; round++)
            {
                var content = $"{{\"round\":{round},\"items\":[{string.Join(",", Enumerable.Range(1, round))}]}}";

                await storage.PutStringAsync(file, content, null, cts.Token).ConfigureAwait(false);

                var (back, version) = await storage.GetStringWithEtagAsync(file, cts.Token).ConfigureAwait(false);

                // 列表要看目录里到底有几个文件 —— 副本就是在这里暴露的。
                // 注意刚写完列表可能还没索引到（夸克的目录索引是异步的），
                // 所以列到 0 个要重试，不能直接判失败。
                List<RemoteEntry> entries = new();

                for (var attempt = 0; attempt < 4; attempt++)
                {
                    entries = await storage.ListAsync(dir, 1, cts.Token).ConfigureAwait(false);
                    if (entries.Count > 0) break;

                    await Task.Delay(600, cts.Token).ConfigureAwait(false);
                }
                var names = string.Join(", ", entries.Select(e => e.Name));

                Console.WriteLine();
                Console.WriteLine($"第 {round} 次写入：{content}");
                Console.WriteLine($"  读回     ：{back}");
                Console.WriteLine($"  版本号   ：{version}");
                Console.WriteLine($"  目录文件 ：{entries.Count} 个（{names}）");

                if (back != content)
                {
                    Console.WriteLine($"  ✘ 内容不是最新的（期望 {content}）");
                    failures++;
                }

                if (entries.Count != 1)
                {
                    Console.WriteLine($"  ✘ 产生了副本！目录里应该有 1 个文件，实际 {entries.Count} 个");
                    failures++;
                }

                if (failures == 0) Console.WriteLine("  ✔ 覆盖正常");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"✘ {ex.GetType().Name}：{ex.Message}");
            failures++;
        }
        finally
        {
            Console.WriteLine();
            Console.WriteLine("== 清理 ==");

            try
            {
                await storage.DeleteAsync(dir, cts.Token).ConfigureAwait(false);
                Console.WriteLine($"  ✔ 已删除 {dir}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠ 清理失败，请手工删除 {dir}：{ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "✅ 覆盖语义正常：同名写入不产生副本，读到的永远是最新内容"
            : $"✘ {failures} 项异常 —— 覆盖语义仍然有问题");

        return failures == 0 ? 0 : 1;
    }
}
