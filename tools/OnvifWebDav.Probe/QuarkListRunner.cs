using OnvifWebDav.Diagnostics;
using OnvifWebDav.Quark;
using OnvifWebDav.Storage;

namespace OnvifWebDav.Probe;

/// <summary>
/// 列夸克网盘目录，用来回答"文件到底存不存在、存在哪"。
///
/// 排查"日志说上传成功但网盘里看不到"时，第一件事就是确认远端真相：
/// 是根本没传上去，还是传到了别的目录（根目录拼接错了），还是网盘客户端缓存没刷新。
/// </summary>
internal static class QuarkListRunner
{
    public static async Task<int> RunAsync(Dictionary<string, string> o)
    {
        var port = int.TryParse(o.GetValueOrDefault("port", "9222"), out var p) && p > 0 ? p : 9222;
        var path = o.GetValueOrDefault("path", "/");
        var depth = int.TryParse(o.GetValueOrDefault("depth", "2"), out var d) ? Math.Clamp(d, 0, 4) : 2;
        var rootOpt = o.GetValueOrDefault("root");

        var log = new AppLog(Path.Combine(Path.GetTempPath(), "owd-probe-logs"));
        using var cdp = new QuarkCdpClient(port, log);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var session = await cdp.TryGetSessionAsync(cts.Token).ConfigureAwait(false);
        if (session is null)
        {
            Console.WriteLine("✘ 拿不到会话。先跑 quark-cdp 确认客户端在跑且已登录。");
            return 1;
        }

        using var api = new QuarkApiClient(session, log);
        using var storage = new QuarkStorage(api, log, null, rootOpt);

        Console.WriteLine($"根目录配置：{(string.IsNullOrWhiteSpace(rootOpt) ? "/（网盘根）" : rootOpt)}");
        Console.WriteLine($"开始路径  ：{path}   深度 {depth}");
        Console.WriteLine();

        var total = 0;
        var failed = false;

        await WalkAsync(storage, path, depth, 0, cts.Token,
            onEntry: (indent, e) =>
            {
                total++;
                var size = e.IsCollection ? "" : $"  {FormatSize(e.Length)}";
                var time = e.LastModified?.ToLocalTime().ToString("MM-dd HH:mm") ?? "";

                // 带上 fid：日志里只出现 fid，不带上就没法把"日志里那串"和"哪个目录"对上
                var id = string.IsNullOrEmpty(e.Id) ? "" : $"  fid={e.Id}";

                Console.WriteLine($"{new string(' ', indent * 2)}{(e.IsCollection ? "[目录] " : "       ")}{e.Name}{size}  {time}{id}");
            },
            onError: ex =>
            {
                failed = true;
                Console.WriteLine($"{new string(' ', 2)}✘ {ex.Message}");
            },
            descend: depth > 1).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(total == 0
            ? "（这个目录下什么都没有）"
            : $"共 {total} 项");

        if (failed) Console.WriteLine("⚠ 有目录列举失败，见上面的错误");

        return 0;
    }

    private static async Task WalkAsync(IRemoteStorage storage, string path, int maxDepth, int indent,
        CancellationToken ct, Action<int, RemoteEntry> onEntry, Action<Exception> onError, bool descend)
    {
        List<RemoteEntry> entries;

        try
        {
            entries = await storage.ListAsync(path, 1, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            onError(ex);
            return;
        }

        // 目录在前，文件在后，各自按名字排
        foreach (var e in entries.OrderByDescending(x => x.IsCollection).ThenBy(x => x.Name))
            onEntry(indent, e);

        if (!descend || indent + 1 >= maxDepth) return;

        foreach (var dir in entries.Where(x => x.IsCollection))
        {
            var child = path.TrimEnd('/') + "/" + dir.Name;
            await WalkAsync(storage, child, maxDepth, indent + 1, ct, onEntry, onError, descend)
                .ConfigureAwait(false);
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024:F2} MB",
        _ => $"{bytes / 1024d / 1024 / 1024:F2} GB",
    };
}
