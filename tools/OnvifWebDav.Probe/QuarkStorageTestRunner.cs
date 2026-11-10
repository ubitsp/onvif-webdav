using System.Diagnostics;
using System.Text;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.Quark;
using OnvifWebDav.Storage;

namespace OnvifWebDav.Probe;

/// <summary>
/// 用 IRemoteStorage 契约测夸克后端。
///
/// 和 quark-selftest 的区别：那个测的是裸协议（pre/auth/PUT/finish 各步骤），
/// 这个测的是**抽象层**——UploadQueue 和 RemoteClipIndex 真正会调的那些方法，
/// 包括多分片（20MB）、按路径反查 fid、映射表落盘后的二次访问。
/// </summary>
internal static class QuarkStorageTestRunner
{
    public static async Task<int> RunAsync(Dictionary<string, string> o)
    {
        var port = int.TryParse(o.GetValueOrDefault("port", "9222"), out var p) && p > 0 ? p : 9222;
        var baseUrl = o.GetValueOrDefault("base");
        var bigMb = int.TryParse(o.GetValueOrDefault("bigmb", "20"), out var mb) && mb > 0 ? mb : 20;

        var work = Path.Combine(Path.GetTempPath(), "owd-quark-storage-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(work);

        var log = new AppLog(Path.Combine(work, "logs"));
        using var cdp = new QuarkCdpClient(port, log);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));

        Console.WriteLine("== 1) 取会话 ==");

        var session = await cdp.TryGetSessionAsync(cts.Token).ConfigureAwait(false);
        if (session is null)
        {
            Console.WriteLine("✘ 拿不到 Cookie。先跑 quark-cdp 确认浏览器调试端口和登录状态。");
            return 1;
        }

        Console.WriteLine($"✔ Cookie {session.Cookie.Split(';').Length} 项");

        using var api = new QuarkApiClient(session, log, baseUrl);

        // 映射表放临时目录，测试不污染正式数据目录
        var mapFile = Path.Combine(work, "quark-fids.json");
        // --root 传了就在网盘里多套一层，用来验证"配置根目录"的拼接
        var rootPath = o.GetValueOrDefault("root");
        using var storage = new QuarkStorage(api, log, mapFile, rootPath);

        Console.WriteLine($"  DisplayName={storage.DisplayName}  SupportsMove={storage.SupportsMove}  " +
                          $"QuotaReleasesImmediately={storage.QuotaReleasesImmediately}");
        Console.WriteLine($"  fid 映射表：{mapFile}");
        Console.WriteLine($"  根目录：{(string.IsNullOrWhiteSpace(rootPath) ? "/" : rootPath)}");
        Console.WriteLine();

        var root = "/owd-storage-test-" + DateTime.Now.ToString("MMdd-HHmmss");
        var failures = 0;

        try
        {
            // ---- 2) 建目录 ----

            Console.WriteLine($"== 2) EnsureDirectoryAsync：{root}/sub ==");
            var sw = Stopwatch.StartNew();
            await storage.EnsureDirectoryAsync(root + "/sub", cts.Token).ConfigureAwait(false);
            Console.WriteLine($"  ✔ {sw.ElapsedMilliseconds} ms");

            // 再调一次，应该命中缓存不再请求
            sw.Restart();
            await storage.EnsureDirectoryAsync(root + "/sub", cts.Token).ConfigureAwait(false);
            Console.WriteLine($"  ✔ 二次调用 {sw.ElapsedMilliseconds} ms（应接近 0）");

            // ---- 3) 上传小文件 ----

            Console.WriteLine();
            Console.WriteLine("== 3) PutFileAsync：小文件 ==");

            const string smallText = "quark-storage-ok";
            var smallLocal = Path.Combine(work, "hello.txt");
            // 注意用无 BOM 的 UTF8：Encoding.UTF8 会写入 3 字节 BOM，长度和内容比对都会偏
            await File.WriteAllTextAsync(smallLocal, smallText, new UTF8Encoding(false), cts.Token)
                .ConfigureAwait(false);

            var smallRemote = root + "/sub/hello.txt";
            sw.Restart();
            await storage.PutFileAsync(smallRemote, smallLocal, null, cts.Token).ConfigureAwait(false);
            Console.WriteLine($"  ✔ {sw.ElapsedMilliseconds} ms，{new FileInfo(smallLocal).Length} 字节");

            // ---- 4) 读回 ----

            Console.WriteLine();
            Console.WriteLine("== 4) GetStringWithEtagAsync ==");

            var (content, version) = await storage.GetStringWithEtagAsync(smallRemote, cts.Token)
                .ConfigureAwait(false);

            if (content == smallText)
                Console.WriteLine($"  ✔ 内容一致，版本号={version}");
            else
            {
                Console.WriteLine($"  ✘ 内容不一致：期望“{smallText}”，实际“{content}”");
                failures++;
            }

            // 不存在的路径要返回 (null, null) 而不是抛异常
            var (missing, missingVersion) = await storage.GetStringWithEtagAsync(root + "/sub/nope.txt", cts.Token)
                .ConfigureAwait(false);

            if (missing is null && missingVersion is null) Console.WriteLine("  ✔ 不存在的文件返回 (null, null)");
            else
            {
                Console.WriteLine("  ✘ 不存在的文件应该返回 (null, null)");
                failures++;
            }

            // ---- 5) HeadLength ----

            Console.WriteLine();
            Console.WriteLine("== 5) HeadLengthAsync ==");

            var len = await storage.HeadLengthAsync(smallRemote, cts.Token).ConfigureAwait(false);
            var expectedLen = new FileInfo(smallLocal).Length;

            if (len == expectedLen) Console.WriteLine($"  ✔ {len} 字节");
            else
            {
                Console.WriteLine($"  ✘ 期望 {expectedLen}，实际 {len}");
                failures++;
            }

            // ---- 6) PutString ----

            Console.WriteLine();
            Console.WriteLine("== 6) PutStringAsync ==");

            const string noteJson = "{\"camera\":\"cam1\",\"clips\":3}";
            await storage.PutStringAsync(root + "/sub/note.json", noteJson, null, cts.Token)
                .ConfigureAwait(false);

            var (noteBack, _) = await storage.GetStringWithEtagAsync(root + "/sub/note.json", cts.Token)
                .ConfigureAwait(false);

            if (noteBack == noteJson) Console.WriteLine("  ✔ 写入并读回一致");
            else
            {
                Console.WriteLine($"  ✘ 读回不一致：“{noteBack}”");
                failures++;
            }

            // ---- 7) 列目录 ----

            Console.WriteLine();
            Console.WriteLine("== 7) ListAsync ==");

            // 夸克的目录索引是异步的：刚 finish 的文件要过一两秒才出现在 sort 结果里。
            // 这里刻意等一下 —— 不是接口慢，是索引延迟，等不到就会少看到文件。
            await Task.Delay(1500, cts.Token).ConfigureAwait(false);

            var entries = await storage.ListAsync(root + "/sub", 1, cts.Token).ConfigureAwait(false);
            Console.WriteLine($"  返回 {entries.Count} 项：");

            foreach (var e in entries)
                Console.WriteLine($"    · {e.Name}（{(e.IsCollection ? "目录" : e.Length + " 字节")}，{e.LastModified:HH:mm:ss}）");

            if (entries.Count != 2)
            {
                Console.WriteLine($"  ✘ 期望 2 项，实际 {entries.Count}");
                failures++;
            }

            // ---- 8) 下载到本地 ----

            Console.WriteLine();
            Console.WriteLine("== 8) DownloadToFileAsync ==");

            var downloaded = Path.Combine(work, "downloaded.txt");
            await storage.DownloadToFileAsync(smallRemote, downloaded, null, cts.Token).ConfigureAwait(false);

            var backText = await File.ReadAllTextAsync(downloaded, cts.Token).ConfigureAwait(false);

            if (backText == smallText) Console.WriteLine("  ✔ 下载内容一致");
            else
            {
                Console.WriteLine($"  ✘ 下载内容不一致：“{backText}”");
                failures++;
            }

            // ---- 9) 多分片上传 ----

            Console.WriteLine();
            Console.WriteLine($"== 9) PutFileAsync：{bigMb} MB（验证多分片）==");

            var bigLocal = Path.Combine(work, "big.bin");
            await CreateRandomFileAsync(bigLocal, bigMb * 1024L * 1024L, cts.Token).ConfigureAwait(false);
            var bigSize = new FileInfo(bigLocal).Length;

            var reported = 0L;
            var progress = new Progress<long>(v => reported = v);

            sw.Restart();
            await storage.PutFileAsync(root + "/sub/big.bin", bigLocal, progress, cts.Token)
                .ConfigureAwait(false);
            var elapsed = sw.Elapsed;

            Console.WriteLine($"  ✔ {elapsed.TotalSeconds:F1}s，{bigSize / (1024d * 1024):F1} MB，" +
                              $"平均 {bigSize / 1024d / 1024 / Math.Max(0.001, elapsed.TotalSeconds):F2} MB/s，" +
                              $"进度回调最后值 {reported / (1024d * 1024):F1} MB");

            var bigLen = await storage.HeadLengthAsync(root + "/sub/big.bin", cts.Token).ConfigureAwait(false);

            if (bigLen == bigSize) Console.WriteLine($"  ✔ 远端长度一致：{bigLen} 字节");
            else
            {
                Console.WriteLine($"  ✘ 远端长度不一致：期望 {bigSize}，实际 {bigLen}");
                failures++;
            }

            // ---- 10) 配额 ----

            Console.WriteLine();
            Console.WriteLine("== 10) GetQuotaAsync ==");

            var quota = await storage.GetQuotaAsync(cts.Token).ConfigureAwait(false);
            Console.WriteLine("  " + (quota?.Describe() ?? "（后端未返回配额）"));

            // ---- 11) 映射表落盘后的二次访问 ----

            Console.WriteLine();
            Console.WriteLine("== 11) 映射表重建（模拟进程重启）==");

            Console.WriteLine($"  {mapFile} 存在={File.Exists(mapFile)}，" +
                              $"{new FileInfo(mapFile).Length} 字节");

            // 用同一张映射表新建实例，此时不该再列目录找 fid
            using (var storage2 = new QuarkStorage(new QuarkApiClient(session, log, baseUrl), log, mapFile,
                       rootPath))
            {
                sw.Restart();
                var len2 = await storage2.HeadLengthAsync(smallRemote, cts.Token).ConfigureAwait(false);

                if (len2 == new FileInfo(smallLocal).Length)
                    Console.WriteLine($"  ✔ 新实例读到长度 {len2}，耗时 {sw.ElapsedMilliseconds} ms");
                else
                {
                    Console.WriteLine($"  ✘ 新实例长度不对：{len2}");
                    failures++;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"✘ {ex.GetType().Name}：{ex.Message}");

            if (ex is QuarkApiException qe && qe.IsSessionExpired)
                Console.WriteLine("  → 会话失效，重新登录 pan.quark.cn 后重跑 quark-cdp");

            failures++;
        }
        finally
        {
            // ---- 12) 清理 ----

            Console.WriteLine();
            Console.WriteLine("== 12) 清理 ==");

            try
            {
                await storage.DeleteAsync(root, cts.Token).ConfigureAwait(false);
                Console.WriteLine($"  ✔ 已删除 {root}（进回收站，配额不会立刻回来）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠ 清理失败，请手工删除 {root}：{ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("== 结论 ==");

        if (failures > 0)
        {
            Console.WriteLine($"✘ {failures} 项未通过");
            return 1;
        }

        Console.WriteLine("✅ IRemoteStorage 契约全部通过");
        Console.WriteLine($"   会话续期：{api.CookieRefreshCount} 次");
        return 0;
    }

    /// <summary>生成随机内容的大文件。用重复块填充，避免 Random 太慢。</summary>
    private static async Task CreateRandomFileAsync(string path, long size, CancellationToken ct)
    {
        var block = new byte[1024 * 1024];
        Random.Shared.NextBytes(block);

        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            1024 * 1024, useAsync: true);

        long written = 0;
        while (written < size)
        {
            var n = (int)Math.Min(block.Length, size - written);
            await fs.WriteAsync(block.AsMemory(0, n), ct).ConfigureAwait(false);
            written += n;
        }
    }
}
