using System.Text.Json;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.Quark;
using OnvifWebDav.Storage;

namespace OnvifWebDav.Probe;

/// <summary>
/// 验证据日期清理录像（RetentionPruner.PruneOlderThanAsync）。
///
/// 用**过去日期**（2020-01-xx）造假数据，跑在独立目录里，绝不碰真实录像。
/// 删除是不可逆操作，这个测试要证明两件事：
///  1. 到期的天被完整删掉（片段 + 缩略图 + 索引）
///  2. 没到期的天一根毛都不动
/// </summary>
internal static class RetentionTestRunner
{
    public static async Task<int> RunAsync(Dictionary<string, string> o)
    {
        var port = int.TryParse(o.GetValueOrDefault("port", "9222"), out var p) && p > 0 ? p : 9222;
        var keepDays = int.TryParse(o.GetValueOrDefault("keep", "2"), out var k) && k > 0 ? k : 2;

        // 默认跑在内存里 —— 不依赖夸克客户端、不联网、绝不碰真实录像。
        // 要验真实后端行为时加 --quark（会往网盘里写测试目录，跑完删掉）。
        var useQuark = o.ContainsKey("quark");
        var log = new AppLog(Path.Combine(Path.GetTempPath(), "owd-probe-logs"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        QuarkCdpClient? cdp = null;
        QuarkApiClient? api = null;
        IRemoteStorage storage;
        var rootPrefix = "";

        if (useQuark)
        {
            cdp = new QuarkCdpClient(port, log);
            var session = await cdp.TryGetSessionAsync(cts.Token).ConfigureAwait(false);

            if (session is null)
            {
                Console.WriteLine("✘ 拿不到会话。先跑 quark-cdp 确认客户端在跑且已登录。");
                return 1;
            }

            api = new QuarkApiClient(session, log);
            rootPrefix = "/owd-retention-" + DateTime.Now.ToString("MMddHHmmss");
            storage = new QuarkStorage(api, log, null, rootPrefix);
            Console.WriteLine($"后端：夸克（{rootPrefix}）");
        }
        else
        {
            storage = new InMemoryStorage();
            Console.WriteLine("后端：内存（离线测试）");
        }

        // 造 3 天：2020-01-01 / 02 / 03，每天 2 段
        var days = new[]
        {
            new DateTime(2020, 1, 1),
            new DateTime(2020, 1, 2),
            new DateTime(2020, 1, 3),
        };

        var failures = 0;

        try
        {
            Console.WriteLine($"== 准备测试数据（根 {rootPrefix}）==");

            foreach (var day in days)
            {
                await CreateFakeDayAsync(storage, "cam1", day, cts.Token).ConfigureAwait(false);
                Console.WriteLine($"  ✔ {day:yyyy-MM-dd} 造了 2 段");
            }

            var before = await storage.ListAsync("cam1/index", 1, cts.Token).ConfigureAwait(false);
            Console.WriteLine($"  索引文件：{string.Join(", ", before.Select(e => e.Name))}");

            // ---- 执行清理：保留最近 2 天（即保留 01-02、01-03，删 01-01）----
            var keepFrom = days[^1].AddDays(-(keepDays - 1));

            Console.WriteLine();
            Console.WriteLine($"== 执行清理：保留 {keepDays} 天（{keepFrom:yyyy-MM-dd} 起）==");

            var pruner = new RetentionPruner();
            var result = await pruner
                .PruneOlderThanAsync(storage, "cam1", keepFrom, log, cts.Token)
                .ConfigureAwait(false);

            Console.WriteLine($"  结果：删除 {result.DaysDeleted} 天 / {result.ClipsDeleted} 段");

            // ---- 验证 ----
            Console.WriteLine();
            Console.WriteLine("== 验证 ==");

            var afterIndex = await storage.ListAsync("cam1/index", 1, cts.Token).ConfigureAwait(false);
            var remaining = afterIndex.Select(e => e.Name).OrderBy(n => n).ToList();

            Console.WriteLine($"  剩余索引：{string.Join(", ", remaining)}");

            var expectKept = days.TakeLast(keepDays).Select(d => d.ToString("yyyy-MM-dd") + ".json").ToList();
            var expectGone = days.SkipLast(keepDays).Select(d => d.ToString("yyyy-MM-dd") + ".json").ToList();

            foreach (var name in expectKept)
            {
                if (remaining.Contains(name)) Console.WriteLine($"  ✔ 保留 {name}");
                else
                {
                    Console.WriteLine($"  ✘ {name} 应该保留却没了");
                    failures++;
                }
            }

            foreach (var name in expectGone)
            {
                if (!remaining.Contains(name)) Console.WriteLine($"  ✔ 已删除 {name}");
                else
                {
                    Console.WriteLine($"  ✘ {name} 应该被删却还在");
                    failures++;
                }
            }

            // 片段文件也要确认：删除顺序是 clips → thumbs → index，
            // 所以索引没了但片段还在，就是删漏了
            foreach (var day in days.SkipLast(keepDays))
            {
                var dir = RemoteClipIndex.ClipsDir("cam1", day);
                var left = await storage.ListAsync(dir, 1, cts.Token).ConfigureAwait(false);

                if (left.Count == 0) Console.WriteLine($"  ✔ {day:yyyy-MM-dd} 的片段目录已空");
                else
                {
                    Console.WriteLine($"  ✘ {day:yyyy-MM-dd} 还剩 {left.Count} 个片段文件");
                    failures++;
                }
            }

            // 重复执行应该什么都不做
            Console.WriteLine();
            Console.WriteLine("== 再跑一次（应当无操作）==");

            var again = await pruner
                .PruneOlderThanAsync(storage, "cam1", keepFrom, log, cts.Token)
                .ConfigureAwait(false);

            if (again.DaysDeleted == 0) Console.WriteLine("  ✔ 幂等：第二次没有删任何东西");
            else
            {
                Console.WriteLine($"  ✘ 第二次又删了 {again.DaysDeleted} 天");
                failures++;
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
            Console.WriteLine("== 清理测试数据 ==");

            try
            {
                await storage.DeleteAsync("/", cts.Token).ConfigureAwait(false);
                Console.WriteLine(useQuark ? $"  ✔ 已删除 {rootPrefix}" : "  ✔ 内存已清空");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠ 清理失败：{ex.Message}");
            }

            storage.Dispose();
            api?.Dispose();
            cdp?.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "✅ 保留策略行为正确：到期整天空删、未到期不动、可重复执行"
            : $"✘ {failures} 项不符预期");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>造一天假数据：2 个 mp4 + 2 个 jpg + 1 个索引。</summary>
    private static async Task CreateFakeDayAsync(IRemoteStorage storage, string cameraId, DateTime day,
        CancellationToken ct)
    {
        var clipsDir = RemoteClipIndex.ClipsDir(cameraId, day);
        var thumbsDir = RemoteClipIndex.ThumbsDir(cameraId, day);

        await storage.EnsureDirectoryAsync(clipsDir, ct).ConfigureAwait(false);
        await storage.EnsureDirectoryAsync(thumbsDir, ct).ConfigureAwait(false);

        var clips = new List<ClipInfo>();

        for (var i = 1; i <= 2; i++)
        {
            var stem = $"{day:HHmmss}_{i:D6}";
            var startLocal = day.AddHours(10).AddMinutes(i);

            var clipPath = clipsDir + "/" + stem + ".mp4";
            var thumbPath = thumbsDir + "/" + stem + ".jpg";

            await storage.PutStringAsync(clipPath, "fake-video-" + i, null, ct).ConfigureAwait(false);
            await storage.PutStringAsync(thumbPath, "fake-thumb-" + i, null, ct).ConfigureAwait(false);

            clips.Add(new ClipInfo
            {
                Id = stem,
                CameraId = cameraId,
                StartUtc = startLocal.ToUniversalTime(),
                EndUtc = startLocal.AddSeconds(10).ToUniversalTime(),
                DurationMs = 10_000,
                SizeBytes = 12,
                Path = clipPath,
                Thumb = thumbPath,
                VideoCodec = "h264",
            });
        }

        var index = new DailyIndex
        {
            CameraId = cameraId,
            Date = day.ToString("yyyy-MM-dd"),
            UpdatedUtc = DateTime.UtcNow,
            Clips = clips,
        };

        var json = JsonSerializer.Serialize(index, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        await storage.PutStringAsync(RemoteClipIndex.IndexPath(cameraId, day), json, null, ct)
            .ConfigureAwait(false);
    }
}
