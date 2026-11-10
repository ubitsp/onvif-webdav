using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OnvifWebDav.Config;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.Media;
using OnvifWebDav.Net;
using OnvifWebDav.Onvif;
using OnvifWebDav.Pipeline;
using OnvifWebDav.Quark;
using OnvifWebDav.Storage;
using OnvifWebDav.WebDav;

namespace OnvifWebDav.Probe;

/// <summary>
/// 无界面诊断工具。没有摄像头、没有 NAS 的时候，靠它把录制管线 / WebDAV / 索引全验一遍。
/// 用法见 PrintHelp()。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        var cmd = args[0].ToLowerInvariant();
        var o = ParseArgs(args.Skip(1).ToArray());

        try
        {
            return cmd switch
            {
                "ffmpeg-selftest" => await FfmpegSelftestAsync(o).ConfigureAwait(false),
                "mock-dav" => await MockDavAsync(o).ConfigureAwait(false),
                "dav-test" => await DavTestAsync(o).ConfigureAwait(false),
                "device" => await DeviceAsync(o).ConfigureAwait(false),
                "caps" => await CapsAsync(o).ConfigureAwait(false),
                "parse-caps" => ParseCapsFile(o),
                "retention-test" => await RetentionTestAsync(o).ConfigureAwait(false),
                "ring-test" => RingTest(o),
                "ring-info" => RingInfo(o),
                "spool-test" => SpoolTest(o),
                "upload-race" => await UploadRaceAsync(o).ConfigureAwait(false),
                "quark-cdp" => await QuarkCdpAsync(o).ConfigureAwait(false),
                "quark-probe" => await QuarkProbeRunner.RunAsync(o).ConfigureAwait(false),
                "quark-selftest" => await QuarkSelfTestRunner.RunAsync(o).ConfigureAwait(false),
                "quark-capture" => await QuarkCaptureRunner.RunAsync(o).ConfigureAwait(false),
                "quark-storage" => await QuarkStorageTestRunner.RunAsync(o).ConfigureAwait(false),
                "quark-page" => await QuarkPageProbeRunner.RunAsync(o).ConfigureAwait(false),
                "quark-launch" => await QuarkLaunchProbeRunner.RunAsync(o).ConfigureAwait(false),
                "quark-ls" => await QuarkListRunner.RunAsync(o).ConfigureAwait(false),
                "events" => await EventsAsync(o).ConfigureAwait(false),
                "notify-test" => await NotifyTestAsync(o).ConfigureAwait(false),
                "parse-notify" => ParseNotifyFile(o),
                "stream" => await StreamAsync(o).ConfigureAwait(false),
                "e2e" => await E2EAsync(o).ConfigureAwait(false),
                "help" or "-h" or "--help" => ShowHelp(),
                _ => Unknown(cmd),
            };
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("已取消。");
            return 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine("✘ " + ex.Message);
            return 1;
        }
    }

    private static int ShowHelp()
    {
        PrintHelp();
        return 0;
    }

    private static int Unknown(string cmd)
    {
        Console.WriteLine($"未知命令：{cmd}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            OnvifWebDav.Probe —— 无界面诊断工具

              ffmpeg-selftest [--work <目录>] [--dav-url <URL>] [--dav-user u] [--dav-pass p] [--dav-root /onvif]
                  用 ffmpeg 合成测试画面，跑通「分段 → 合成 MP4 → 缩略图」全流程，不需要摄像头。
                  给了 --dav-url 就顺带把结果传上去并写索引。

              e2e --dav-url <URL> [--dav-user u] [--dav-pass p] [--dav-root /onvif]
                  端到端：造片 → 上传 → 写索引 → 读回来核对。

              mock-dav [--port 8089] [--dir <目录>] [--user dav] [--pass dav] [--redirect] [--no-head]
                  起迷你 WebDAV。--redirect 模拟 GET 302 直链，--no-head 模拟 HEAD 405，
                  两个都对应 aliyundrive-webdav 的真实行为。
                  起一个迷你 WebDAV 服务端（Basic 认证），给 dav-test / e2e 用。

              dav-test --url <URL> [--user u] [--pass p] [--root /onvif]
                  验证认证方式、建目录、上传、MOVE、PROPFIND。

              device --host <IP> [--port 2020] --user <账号> --pass <密码>
                  连接摄像头：型号、固件、时钟偏差、profile、RTSP 地址。

              spool-test [--dir <目录>]
                  本地队列状态机回归：LoadJobs 绝不能把 Uploading 改成 Pending
                  （那会让两个上传 worker 抢同一个任务，先完成的删掉目录，另一个报 FileNotFoundException）。
                  崩溃恢复只允许在队列启动时做一次。

              upload-race --url <URL> [--user u] [--pass p] [--root /onvif] [--jobs 4]
                  并发上传压测：2 个 worker 抢 N 个任务，断言每个任务只上传一次。

              quark-cdp [--port 9222] [--show]
                  阶段 0.2：从夸克浏览器的调试端口读 Cookie 和 UA，不发任何外网请求。
                  需要浏览器以 --remote-debugging-port=9222 启动并已登录 pan.quark.cn。
                  --show 会打印完整 Cookie（属账号凭证，别外传）。

              quark-probe [--via cdp] [--ua browser|client] [--base https://drive-pc.quark.cn] [--port 9222]
                          [--method GET|POST] [--matrix]
                  阶段 0.3/0.4：只发两个只读请求（member 配额、file/sort 根目录），
                  判断 .NET 的 TLS 指纹会不会被夸克拦。
                  --via cdp 改为在夸克页面内用 fetch 发（指纹即浏览器，可绕过直连被拦）。
                  --matrix 扫 host × method 全组合，一次找出可用组合（推荐先用这个）。
                  注意：夸克的查询接口只接受 GET，POST 会返回 405。

              quark-selftest [--write] [--port 9222] [--base https://drive-pc.quark.cn]
                  验证夸克的写链路。不带 --write 只做只读复核（member + file/sort）。
                  带 --write 会建一个临时目录，传 15 字节，读回比对，然后彻底删除。
                  上传走 upload/pre → OSS 分片 PUT → upload/finish 三步。

              quark-storage [--bigmb 20] [--port 9222] [--base URL]
                  用 IRemoteStorage 契约测夸克后端：建目录 → 小文件 → 读回 → 列目录
                  → 下载 → 20MB 多分片 → 配额 → 映射表重建 → 删除。

              quark-ls [--path /] [--depth 2] [--root /onvif] [--port 9222]
                  列夸克网盘目录（递归）。排查"日志说上传成功但网盘看不到"时先跑这个。

              quark-launch [--port 9222] [--exe <quark.exe 路径>]
                  诊断能否自动拉起夸克客户端，以及为什么拉不起来。
                  只查不动：不启动、不杀任何进程。

              quark-page [--port 9222]
                  检测夸克页面里存了哪些认证信息（document.cookie / localStorage /
                  sessionStorage / IndexedDB）。只打印名字和长度，不打印值。

              quark-capture [--seconds 240] [--port 9222]
                  在夸克页面里 hook fetch/XHR，抓网页版上传时的真实请求参数。
                  先跑本命令，然后在网页里手动上传一个小文件，会打印 URL 和 body。
                  --ua client 换成 Electron 客户端 UA 再试一次。
                  配合 mock-dav 使用。

              ring-info [--dir <目录>] [--limit-mb 2048] [--keep 600] [--mbps 4]
                  检查环形目录能不能用（内存盘路径填这里先验证）：
                  解析后的实际路径、是否回落到默认目录、所在卷剩余空间、
                  按码率算能存多久、实际生效的字节上限。

              ring-test [--dir <目录>]
                  环形缓冲保护回归测试（不需要摄像头、不需要 ffmpeg）：
                  造一批假 TS 段，模拟"上一段还在合成、下一段已经开始录"，
                  验证释放上一段的保护不会把下一段的段删掉。
                  改 SegmentRing 之后必跑，退出码非 0 即失败。

              retention-test --url <URL> [--user u] [--pass p] [--root /onvif] [--seed 3] [--target-free-gb 9999]
                  清理逻辑演练：先造 N 天的假录像（各 200KB），再按目标剩余空间从最旧的一天开始删。
                  建议先对 mock-dav 跑，确认删除顺序符合预期再上真云盘。

              parse-caps --file <GetServices响应.xml>
                  离线验证能力解析：把抓到的 GetServices 响应存成文件丢进来，
                  直接输出服务清单与 Profile 推断（不用连摄像头）。

              caps --host <IP> [--port 2020] --user <账号> --pass <密码>
                  设备能力全量：GetServices 服务清单、Profile 推断（S/T/G/M）、
                  事件能力（PullPoint / WSSubscriptionPolicy）、快照与 RTSP 支持。

              events --host <IP> [--port 2020] --user <账号> --pass <密码> [--seconds 60]
                  ★ 关键验证：先打 Events 能力集，再用 PullPoint 订阅并统计事件。

              parse-notify --file <xml文件>
                  离线验证 Notify 解析：把抓到的原始 XML 丢进来，看能不能解析出 IsMotion。
                  从 Wireshark / 设备日志里捞到报文时用这个，不用连摄像头。

              notify-test --host <IP> [--port 2020] --user <账号> --pass <密码>
                          [--seconds 120] [--listen-port 9099]
                  验证 Basic Notification（WS-BaseNotification Subscribe，设备主动推给我们）。
                  本机起一个 /onvif/notify 接收端点，订阅后把收到的原始 XML 和解析结果都打出来。
                  设备支持 WSSubscriptionPolicySupport 时这条链路比轮询更实时。

              stream --host <IP> --user <账号> --pass <密码>
                  探测码流编码（H.264 / H.265）与分辨率。
            """);
    }

    // ---------------- 命令实现 ----------------

    private static async Task<int> FfmpegSelftestAsync(Dictionary<string, string> o)
    {
        var work = o.GetValueOrDefault("work")
                   ?? Path.Combine(Path.GetTempPath(), "owd-selftest-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(work);

        var locator = new FfmpegLocator();
        if (!locator.IsAvailable)
        {
            Console.WriteLine(locator.MissingMessage);
            return 1;
        }

        Console.WriteLine($"ffmpeg：{locator.FfmpegPath}");

        var log = new AppLog(work);
        var source = Path.Combine(work, "source.mp4");

        Console.WriteLine("1/4 用 testsrc2 造 20 秒测试视频…");
        var make = await FfmpegRunner.RunAsync(locator.FfmpegPath!,
            $"-hide_banner -y -loglevel error -f lavfi -i testsrc2=size=640x360:rate=15 -t 20 " +
            // 必须固定 GOP：分段是按关键帧切的，GOP 太长就切不出 2 秒的段（真实摄像头同理）
            $"-c:v libx264 -pix_fmt yuv420p -g 30 -keyint_min 30 -sc_threshold 0 {FfmpegArgs.Q(source)}",
            TimeSpan.FromSeconds(120)).ConfigureAwait(false);

        if (!make.Ok) return Fail("生成测试视频失败：" + make.StdErr);
        Console.WriteLine($"   ✔ {new FileInfo(source).Length / 1024} KB");

        var ringDir = Path.Combine(work, "ring");
        Directory.CreateDirectory(ringDir);
        foreach (var f in Directory.GetFiles(ringDir)) File.Delete(f);

        Console.WriteLine("2/4 按环形缓冲的方式切成 2 秒 TS 段…");
        var seg = await FfmpegRunner.RunAsync(locator.FfmpegPath!,
            $"-hide_banner -y -loglevel error -i {FfmpegArgs.Q(source)} -map 0:v:0 -c copy " +
            $"-f segment -segment_format mpegts -segment_time 2 -reset_timestamps 1 " +
            $"{FfmpegArgs.Q(Path.Combine(ringDir, "seg%03d.ts"))}",
            TimeSpan.FromSeconds(120)).ConfigureAwait(false);

        var segments = Directory.GetFiles(ringDir, "*.ts").OrderBy(f => f).ToList();
        if (!seg.Ok || segments.Count == 0) return Fail("分段失败：" + seg.StdErr);
        Console.WriteLine($"   ✔ {segments.Count} 个分段");

        Console.WriteLine("3/4 合成可 seek 的 MP4…");
        var composer = new ClipComposer(locator, log);
        var outMp4 = Path.Combine(work, "clip.mp4");
        var compose = await composer.ComposeAsync(segments, work, outMp4, false, false,
            CancellationToken.None).ConfigureAwait(false);

        if (!compose.Ok) return Fail("合成失败：" + compose.Error);
        Console.WriteLine($"   ✔ {Path.GetFileName(outMp4)} {new FileInfo(outMp4).Length / 1024} KB " +
                          $"{compose.Info?.VideoCodec} {compose.Info?.Width}x{compose.Info?.Height} " +
                          $"{compose.Info?.DurationSeconds:F1}s");

        Console.WriteLine("4/4 生成缩略图…");
        var thumb = Path.Combine(work, "thumb.jpg");
        var thumbPath = await composer.MakeThumbnailAsync(outMp4, thumb, CancellationToken.None)
            .ConfigureAwait(false);
        Console.WriteLine(thumbPath is null ? "   ✘ 缩略图失败" : $"   ✔ {new FileInfo(thumb).Length / 1024} KB");

        // ---------- 附加 A：音频正常（AAC）应直接 copy，不能重编码 ----------
        Console.WriteLine();
        Console.WriteLine("附加 A：源音频是 AAC 时应 -c:a copy（不重编码）…");

        var aacDir = Path.Combine(work, "aac");
        Directory.CreateDirectory(aacDir);
        foreach (var f in Directory.GetFiles(aacDir)) File.Delete(f);

        var mkAac = await FfmpegRunner.RunAsync(locator.FfmpegPath!,
            $"-hide_banner -y -loglevel error -i {FfmpegArgs.Q(source)} " +
            "-f lavfi -i sine=frequency=440:sample_rate=16000 -t 6 " +
            "-map 0:v:0 -map 1:a:0 -c:v copy -c:a aac " +
            "-f segment -segment_format mpegts -segment_time 2 -reset_timestamps 1 " +
            $"{FfmpegArgs.Q(Path.Combine(aacDir, "a%03d.ts"))}",
            TimeSpan.FromSeconds(120)).ConfigureAwait(false);

        var aacSegs = Directory.GetFiles(aacDir, "*.ts").OrderBy(f => f).ToList();
        if (!mkAac.Ok || aacSegs.Count == 0) return Fail("生成带音频分段失败：" + mkAac.StdErr);

        var aacOut = Path.Combine(work, "clip-audio.mp4");
        var aacResult = await composer.ComposeAsync(aacSegs, work, aacOut, false, true,
            CancellationToken.None).ConfigureAwait(false);

        if (!aacResult.Ok) return Fail("带音频合成失败：" + aacResult.Error);

        Console.WriteLine($"   ✔ {new FileInfo(aacOut).Length / 1024} KB 音频={aacResult.Info?.AudioCodec} " +
                          $"{aacResult.Info?.AudioChannels}声道 {aacResult.Info?.AudioSampleRate}Hz");
        if (aacResult.Info?.HasAudio != true)
            return Fail("源是 AAC 却没保留音轨 —— 检查 DecideAudio 的 copy 分支");

        // ---------- 附加 B：复现线上报错（首段声明了音频但没有音频包）----------
        Console.WriteLine();
        Console.WriteLine("附加 B：首段只有视频、PMT 却声明了音频（线上那个 0 channels 报错）…");

        var gapDir = Path.Combine(work, "audiogap");
        Directory.CreateDirectory(gapDir);
        foreach (var f in Directory.GetFiles(gapDir)) File.Delete(f);

        // 音频整体延后 5 秒：前两个 2 秒段里一个音频包都没有，但 PMT 里有音频 PID
        var mkGap = await FfmpegRunner.RunAsync(locator.FfmpegPath!,
            $"-hide_banner -y -loglevel error -i {FfmpegArgs.Q(source)} " +
            "-itsoffset 5 -f lavfi -i sine=frequency=440:sample_rate=16000 -t 10 " +
            "-map 0:v:0 -map 1:a:0 -c:v copy -c:a aac " +
            "-f segment -segment_format mpegts -segment_time 2 -reset_timestamps 1 " +
            $"{FfmpegArgs.Q(Path.Combine(gapDir, "g%03d.ts"))}",
            TimeSpan.FromSeconds(120)).ConfigureAwait(false);

        var gapSegs = Directory.GetFiles(gapDir, "*.ts").OrderBy(f => f).Take(2).ToList();
        if (!mkGap.Ok || gapSegs.Count < 2) return Fail("生成首段无音频的分段失败：" + mkGap.StdErr);

        var gapOut = Path.Combine(work, "clip-gap.mp4");
        var gapResult = await composer.ComposeAsync(gapSegs, work, gapOut, false, true,
            CancellationToken.None).ConfigureAwait(false);

        if (!gapResult.Ok)
            return Fail("首段无音频时整段合成失败了（这正是要修的问题）：" + gapResult.Error);

        Console.WriteLine($"   ✔ 合成成功 {new FileInfo(gapOut).Length / 1024} KB " +
                          $"{gapResult.Info?.DurationSeconds:F1}s 音频={gapResult.Info?.HasAudio}");

        var davUrl = o.GetValueOrDefault("dav-url");
        if (!string.IsNullOrEmpty(davUrl))
        {
            Console.WriteLine("附加：上传到 WebDAV…");
            var cfg = new WebDavConfig
            {
                Url = davUrl,
                Username = o.GetValueOrDefault("dav-user", ""),
                Password = o.GetValueOrDefault("dav-pass", ""),
                RootPath = o.GetValueOrDefault("dav-root", "/onvif"),
                TimeoutSeconds = 60,
            };

            using var dav = new WebDavClient(cfg, log);
            var test = await dav.TestAsync(CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine("   " + (test.Ok ? "✔ " : "✘ ") + test.Message);
            if (!test.Ok) return 1;

            var job = new JobRecord
            {
                Id = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_selftest",
                CameraId = "cam1",
                StartUtc = DateTime.UtcNow.AddSeconds(-20),
                EndUtc = DateTime.UtcNow,
                VideoCodec = compose.Info?.VideoCodec ?? "",
                HasAudio = false,
            };
            var date = DateTime.Now.Date;
            var remote = $"{RemoteClipIndex.ClipsDir("cam1", date)}/{job.Id}.mp4";

            await dav.PutFileAsync(remote, outMp4, null, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine("   ✔ 已上传 " + remote);

            var index = new RemoteClipIndex(dav, log);
            await index.AddAsync(new ClipInfo
            {
                Id = job.Id,
                CameraId = "cam1",
                StartUtc = job.StartUtc,
                EndUtc = job.EndUtc,
                DurationMs = (int)(compose.Info?.DurationSeconds * 1000 ?? 0),
                SizeBytes = new FileInfo(outMp4).Length,
                Path = remote,
                Thumb = "",
                VideoCodec = job.VideoCodec,
            }, CancellationToken.None).ConfigureAwait(false);

            var clips = await index.GetDayAsync("cam1", date, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"   ✔ 索引里现在有 {clips.Count} 条记录");
        }

        Console.WriteLine();
        Console.WriteLine("全部通过 ✅   产物目录：" + work);
        return 0;
    }

    private static async Task<int> E2EAsync(Dictionary<string, string> o)
    {
        var url = o.GetValueOrDefault("dav-url");
        if (string.IsNullOrEmpty(url))
        {
            Console.WriteLine("需要 --dav-url");
            return 1;
        }

        o["dav-url"] = url;
        return await FfmpegSelftestAsync(o).ConfigureAwait(false);
    }

    private static async Task<int> MockDavAsync(Dictionary<string, string> o)
    {
        var root = o.GetValueOrDefault("dir")
                   ?? Path.Combine(Path.GetTempPath(), "owd-mockdav");
        var port = int.Parse(o.GetValueOrDefault("port", "8089"));
        var user = o.GetValueOrDefault("user", "dav");
        var pass = o.GetValueOrDefault("pass", "dav");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(port));
        builder.Logging.ClearProviders();

        var app = builder.Build();
        var redirect = o.ContainsKey("redirect") && o["redirect"] != "false";
        var noHead = o.ContainsKey("no-head") && o["no-head"] != "false";
        var quotaGb = long.TryParse(o.GetValueOrDefault("quota-gb", "0"), out var qg) ? qg : 0;
        var server = new MockDavServer(root, user, pass, redirect, noHead, quotaGb * 1024L * 1024 * 1024);
        app.Run(server.HandleAsync);

        await app.StartAsync().ConfigureAwait(false);

        Console.WriteLine($"mock WebDAV 已启动： http://localhost:{port}");
        Console.WriteLine($"  根目录：{root}");
        Console.WriteLine($"  账号：{user} / {pass}");
        if (redirect) Console.WriteLine("  模拟 GET 302 直链（等价于 aliyundrive-webdav 默认 --redirect）");
        if (noHead) Console.WriteLine("  模拟 HEAD 405（阿里云盘等后端不支持 HEAD）");
        if (quotaGb > 0) Console.WriteLine($"  模拟配额 {quotaGb} GB（PROPFIND 根返回 quota-*-bytes）");
        Console.WriteLine("  支持：OPTIONS PROPFIND MKCOL PUT GET HEAD MOVE DELETE（仅 Basic）");
        Console.WriteLine("  Ctrl+C 结束");

        await app.WaitForShutdownAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> DavTestAsync(Dictionary<string, string> o)
    {
        var url = o.GetValueOrDefault("url");
        if (string.IsNullOrEmpty(url))
        {
            Console.WriteLine("需要 --url");
            return 1;
        }

        var cfg = new WebDavConfig
        {
            Url = url,
            Username = o.GetValueOrDefault("user", ""),
            Password = o.GetValueOrDefault("pass", ""),
            RootPath = o.GetValueOrDefault("root", "/onvif"),
            TimeoutSeconds = 60,
        };

        var log = new AppLog(Path.Combine(Path.GetTempPath(), "owd-probe-logs"));
        using var dav = new WebDavClient(cfg, log);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await dav.TestAsync(cts.Token).ConfigureAwait(false);
        sw.Stop();

        Console.WriteLine((result.Ok ? "✔ " : "✘ ") + result.Message);
        Console.WriteLine();

        if (result.Steps is { Count: > 0 })
        {
            Console.WriteLine("步骤：");
            foreach (var step in result.Steps) Console.WriteLine("  " + step);
            Console.WriteLine();
        }

        Console.WriteLine($"  认证 {result.AuthScheme} ｜ MOVE {(result.SupportsMove ? "支持" : "不支持")} ｜ " +
                          $"PROPFIND {(result.SupportsPropfind ? "支持" : "不支持")}");

        return result.Ok ? 0 : 1;
    }

    /// <summary>
    /// 设备能力全量：服务清单 + Profile 推断 + 事件/快照/RTSP 支持。
    /// Profile G 的判定只看一件事：服务列表里有没有 ver10/replay/wsdl。
    /// </summary>
    /// <summary>
    /// 清理逻辑演练：造 N 天假录像 → 跑 RetentionPruner → 看删了哪些天。
    /// 删除是不可逆的，上真环境前先用这个在 mock 上确认行为。
    /// </summary>
    private static async Task<int> RetentionTestAsync(Dictionary<string, string> o)
    {
        var url = o.GetValueOrDefault("url");
        if (string.IsNullOrEmpty(url))
        {
            Console.WriteLine("需要 --url");
            return 1;
        }

        var cfg = new WebDavConfig
        {
            Url = url,
            Username = o.GetValueOrDefault("user", ""),
            Password = o.GetValueOrDefault("pass", ""),
            RootPath = o.GetValueOrDefault("root", "/onvif"),
            TimeoutSeconds = 60,
        };

        var camId = o.GetValueOrDefault("cam", "cam1");
        var seed = int.TryParse(o.GetValueOrDefault("seed", "3"), out var sd) ? Math.Clamp(sd, 1, 30) : 3;
        var targetGb = double.TryParse(o.GetValueOrDefault("target-free-gb", "9999"), out var tg) ? tg : 9999;
        var targetBytes = (long)(targetGb * 1024 * 1024 * 1024);

        var log = new AppLog(Path.Combine(Path.GetTempPath(), "owd-probe-logs"));
        using var dav = new WebDavClient(cfg, log);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var index = new RemoteClipIndex(dav, log);
        var work = Path.Combine(Path.GetTempPath(), "owd-retention-seed");
        Directory.CreateDirectory(work);

        var fakeClip = Path.Combine(work, "clip.mp4");
        if (!File.Exists(fakeClip) || new FileInfo(fakeClip).Length != 200 * 1024)
            File.WriteAllBytes(fakeClip, new byte[200 * 1024]);

        Console.WriteLine($"造 {seed} 天假录像（每天 1 段 × 200KB）…");
        var today = DateTime.Today;

        for (var i = seed - 1; i >= 0; i--)
        {
            var day = today.AddDays(-i);
            var stem = $"120000_{i:0000}";
            var remoteClip = $"{RemoteClipIndex.ClipsDir(camId, day)}/{stem}.mp4";

            await dav.PutFileAsync(remoteClip, fakeClip, null, cts.Token).ConfigureAwait(false);

            await index.AddAsync(new ClipInfo
            {
                Id = stem,
                CameraId = camId,
                StartUtc = day.AddHours(12).ToUniversalTime(),
                EndUtc = day.AddHours(12).AddSeconds(30).ToUniversalTime(),
                DurationMs = 30_000,
                SizeBytes = new FileInfo(fakeClip).Length,
                Path = remoteClip,
                VideoCodec = "h264",
            }, cts.Token).ConfigureAwait(false);

            Console.WriteLine($"  · {day:yyyy-MM-dd} → {remoteClip}");
        }

        var before = await index.GetDatesAsync(camId, cts.Token).ConfigureAwait(false);
        var quotaBefore = await dav.GetQuotaAsync(cts.Token).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine($"清理前：{before.Count} 天，配额 {quotaBefore?.Describe() ?? "未知"}");
        Console.WriteLine($"目标：剩余达到 {targetGb} GB");
        Console.WriteLine();

        var pruner = new RetentionPruner();
        var result = await pruner.PruneOldestAsync(dav, camId, targetBytes, log, cts.Token).ConfigureAwait(false);

        var after = await index.GetDatesAsync(camId, cts.Token).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine($"删除结果：{result.DaysDeleted} 天 / {result.ClipsDeleted} 段，" +
                          $"达到目标={result.ReachedTarget}");
        Console.WriteLine($"配额：{result.After?.Describe() ?? "未知"}");
        Console.WriteLine($"剩余天数：{after.Count}（{string.Join("、", after.Select(d => d.ToString("yyyy-MM-dd")))}）");

        return 0;
    }

    /// <summary>
    /// 环形缓冲保护回归测试。纯本地、无需摄像头。
    ///
    /// 复现的正是"第二段起坏掉"的那个场景：段 A 正在合成（保护还在），段 B 已经开始录（又设了一个保护），
    /// 然后 A 收尾释放自己的保护。旧实现是 ReleaseAll()，会把 B 的保护一起清掉，
    /// 于是 B 的早期分段被 Trim 删掉 —— 合出来就是没有开头的残片。
    /// </summary>
    private static int RingTest(Dictionary<string, string> o)
    {
        var dir = o.GetValueOrDefault("dir")
                  ?? Path.Combine(Path.GetTempPath(), "owd-ringtest-" + DateTime.Now.ToString("HHmmss"));

        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);

        const int segmentSeconds = 2;
        var now = DateTime.Now;
        var failures = new List<string>();

        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"  {(ok ? "✔" : "✘")} {name}" + (detail.Length > 0 ? "：" + detail : ""));
            if (!ok) failures.Add(name + " → " + detail);
        }

        try
        {
            // 造 60 秒的假段（每 2 秒一个），文件名就是段起始时间
            var firstSegmentStart = now.AddSeconds(-60);
            for (var i = 0; i <= 30; i++)
            {
                var start = firstSegmentStart.AddSeconds(i * segmentSeconds);
                var path = Path.Combine(dir, start.ToString("yyyyMMdd_HHmmss") + ".ts");
                File.WriteAllBytes(path, new byte[1024]);
            }

            var ring = new SegmentRing(dir, segmentSeconds);

            // 段 A：8 秒预录，起点 now-8
            var jobAStart = now.AddSeconds(-8);
            var tokenA = ring.ProtectFrom(jobAStart);

            // 段 B：A 还没合成完，B 已经开始录，起点 now-4
            var jobBStart = now.AddSeconds(-4);
            var tokenB = ring.ProtectFrom(jobBStart);

            Check("两个片段同时持有保护", ring.ActiveProtections == 2, $"{ring.ActiveProtections} 个");

            // A 收尾：释放自己的令牌（旧代码在这里 ReleaseAll，会把 B 的保护也清掉）
            ring.Release(tokenA);
            Check("释放 A 后 B 的保护仍在", ring.ActiveProtections == 1, $"{ring.ActiveProtections} 个");

            // Trim 按 PreRoll(8)+15 = 23 秒淘汰。B 需要的段从 now-4 开始，必须留住。
            ring.Trim(23);

            var collectedB = ring.Collect(jobBStart, now.AddSeconds(-segmentSeconds));
            var earliestB = collectedB.Count > 0 ? collectedB.Min(x => x.StartLocal) : DateTime.MaxValue;

            Check("B 的分段没被 Trim 删掉", collectedB.Count > 0, $"{collectedB.Count} 段");
            Check("B 保留到了自己的起点", earliestB <= jobBStart.AddSeconds(segmentSeconds),
                $"最早一段 {earliestB:HH:mm:ss}，需要 ≤ {jobBStart.AddSeconds(segmentSeconds):HH:mm:ss}");

            // B 也收尾后，保护应归零，此时 Trim 才会真正回收旧段
            ring.Release(tokenB);
            Check("全部释放后保护归零", ring.ActiveProtections == 0, $"{ring.ActiveProtections} 个");

            ring.Trim(23);
            var afterTrim = ring.Collect(firstSegmentStart, now);
            Check("保护释放后旧段被回收", afterTrim.Count < 30, $"剩 {afterTrim.Count} 段");

            // ---------- 场景 B：大窗口 + 按字节兜底 ----------
            Console.WriteLine();
            Console.WriteLine("场景 B：环形保留放大到 10 分钟，再加磁盘上限兜底");

            var dir2 = dir + "-big";
            Directory.CreateDirectory(dir2);

            // 造 10 分钟（300 段 × 2 秒），每段 4KB ≈ 1.2MB
            var bigStart = now.AddSeconds(-600);
            for (var i = 0; i < 300; i++)
            {
                var start = bigStart.AddSeconds(i * segmentSeconds);
                File.WriteAllBytes(Path.Combine(dir2, start.ToString("yyyyMMdd_HHmmss") + ".ts"),
                    new byte[4096]);
            }

            var ring2 = new SegmentRing(dir2, segmentSeconds);
            var firstBytes = ring2.TotalBytes();
            Check("初始占用统计正确", firstBytes > 300 * 4096 * 0.9, $"{firstBytes / 1024}KB");

            // 保留 600 秒 = 整个窗口都不该删
            ring2.Trim(600, 50 * 1024 * 1024);
            Check("窗口内不误删", ring2.Collect(bigStart, now).Count >= 290,
                $"剩 {ring2.Collect(bigStart, now).Count} 段");

            // 收紧到 200KB：按字节从最旧删，但受保护的不能动
            var protect2 = ring2.ProtectFrom(now.AddSeconds(-30));
            ring2.Trim(600, 200 * 1024);

            var afterBytes = ring2.TotalBytes();
            var protectedSegs = ring2.Collect(now.AddSeconds(-30), now).Count;

            Console.WriteLine($"  · 收紧后 {afterBytes / 1024}KB，受保护窗口 {protectedSegs} 段");
            Check("按字节淘汰生效", afterBytes <= 260 * 1024, $"{afterBytes / 1024}KB");
            Check("受保护的分段一个都没删", protectedSegs >= 10, $"{protectedSegs} 段");

            ring2.Release(protect2);
            ring2.Trim(600, 100 * 1024);
            Check("释放后能继续压到上限以下", ring2.TotalBytes() <= 130 * 1024,
                $"{ring2.TotalBytes() / 1024}KB");

            try { Directory.Delete(dir2, true); } catch { }

            // ---------- 场景 C：GOP 很长（12 秒一段），短窗口不能丢录像 ----------
            Console.WriteLine();
            Console.WriteLine("场景 C：摄像头 GOP 12 秒（配置 2 秒也切不出短段）时，7 秒窗口仍要切到内容");

            var dir3 = dir + "-gop";
            Directory.CreateDirectory(dir3);

            // 7 个段，间隔 12 秒；最后一个当成"正在写"
            for (var i = 0; i < 7; i++)
            {
                var start = now.AddSeconds(-72 + i * 12);
                File.WriteAllBytes(Path.Combine(dir3, start.ToString("yyyyMMdd_HHmmss") + ".ts"),
                    new byte[2048]);
            }

            var ring3 = new SegmentRing(dir3, 2);
            Check("观测到真实段长 12s", ring3.ObservedSegmentSeconds() == 12,
                $"{ring3.ObservedSegmentSeconds()}s");

            // 7 秒窗口，且起点落在两段之间：旧实现（容差 = 配置的 2 秒）会返回空
            var winStart = now.AddSeconds(-61);
            var winEnd = now.AddSeconds(-54);
            var gopSegs = ring3.Collect(winStart, winEnd);

            Check("短窗口仍能切到内容", gopSegs.Count > 0, $"{gopSegs.Count} 段");
            Check("包含窗口末尾的段已写完", ring3.IsSegmentClosed(winEnd), "");
            Check("正在写的那段不算已写完", !ring3.IsSegmentClosed(now.AddSeconds(1)), "");

            try { Directory.Delete(dir3, true); } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ✘ 异常：" + ex);
            failures.Add("异常 " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine();

        if (failures.Count == 0)
        {
            Console.WriteLine("✅ 环形缓冲保护逻辑正常（令牌制生效）");
            return 0;
        }

        Console.WriteLine("✘ 失败项：" + string.Join("；", failures));
        return 1;
    }

    /// <summary>
    /// 环形目录检查。填内存盘路径先跑一遍，确认程序真的会用那个盘、容量够不够。
    /// 不需要摄像头、不需要 ffmpeg。
    /// </summary>
    /// <summary>
    /// 阶段 0.2：只读 CDP 的 Cookie 和 UA，**不发任何外网请求**。
    /// 回答"程序到底能不能拿到夸克的有效会话"，是后续所有工作的前提。
    /// </summary>
    private static async Task<int> QuarkCdpAsync(Dictionary<string, string> o)
    {
        var port = int.TryParse(o.GetValueOrDefault("port", "9222"), out var p) && p > 0 ? p : 9222;
        var show = o.ContainsKey("show");
        var log = new AppLog(Path.Combine(Path.GetTempPath(), "owd-probe-logs"));

        using var cdp = new QuarkCdpClient(port, log);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Console.WriteLine($"连接 CDP 127.0.0.1:{port} …");

        var info = await cdp.TryGetBrowserInfoAsync(cts.Token).ConfigureAwait(false);
        if (info is null)
        {
            Console.WriteLine("✘ 连不上调试端口。请确认夸克浏览器是这样启动的：");
            Console.WriteLine($"    \"<夸克安装目录>\\quark.exe\" --remote-debugging-port={port}");
            Console.WriteLine("  （如果浏览器已经在运行，先完全退出再带参数启动）");
            return 1;
        }

        Console.WriteLine("✔ 浏览器：" + info.Browser);
        Console.WriteLine("  UA：" + info.UserAgent);

        var session = await cdp.TryGetSessionAsync(cts.Token).ConfigureAwait(false);
        if (session is null)
        {
            Console.WriteLine();
            Console.WriteLine("✘ 拿不到夸克 Cookie。常见原因：");
            Console.WriteLine("  1) 浏览器里没登录 pan.quark.cn");
            Console.WriteLine("  2) 登录的是另一个浏览器配置（多账号/隐私窗口）");
            Console.WriteLine("  3) 页面还没加载完，稍等几秒重试");
            return 1;
        }

        var names = session.Cookie
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Contains('='))
            .Select(x => x[..x.IndexOf('=')])
            .ToList();

        Console.WriteLine();
        Console.WriteLine($"✔ Cookie 共 {names.Count} 项，拼接长度 {session.Cookie.Length}");
        Console.WriteLine("  名称：" + string.Join(", ", names));

        var hasPuus = names.Contains("__puus");
        var hasPus = names.Contains("__pus");

        Console.WriteLine($"  __puus：{(hasPuus ? "✔ 有" : "✘ 缺 —— 多数 API 会返回未登录")}");
        Console.WriteLine($"  __pus ：{(hasPus ? "✔ 有" : "— 没有")}");

        Console.WriteLine();

        if (!hasPuus)
        {
            Console.WriteLine("✘ 缺少 __puus，先别往下走。去浏览器里重新登录夸克网盘再试。");
            return 1;
        }

        if (show)
        {
            Console.WriteLine("完整 Cookie（属于账号凭证，别贴到聊天或提交进仓库）：");
            Console.WriteLine(session.Cookie);
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine("需要复制完整 Cookie 手工填进配置时，加 --show 参数。");
        }

        Console.WriteLine("✅ CDP 通道可用（阶段 0.2 通过）");
        return 0;
    }

    private static int RingInfo(Dictionary<string, string> o)
    {
        var configured = o.GetValueOrDefault("dir");
        var limitMb = int.TryParse(o.GetValueOrDefault("limit-mb", "2048"), out var lm) ? lm : 2048;
        var keep = int.TryParse(o.GetValueOrDefault("keep", "600"), out var kp) ? kp : 600;
        var mbps = double.TryParse(o.GetValueOrDefault("mbps", "4"), out var mb) && mb > 0 ? mb : 4;

        var log = new AppLog(Path.Combine(Path.GetTempPath(), "owd-probe-logs"));
        var spoolRoot = Path.Combine(Path.GetTempPath(), "owd-ringinfo-root");
        var spool = new LocalSpool(spoolRoot, log, configured);

        Console.WriteLine("环形目录解析：");
        Console.WriteLine($"  配置值      : {(string.IsNullOrWhiteSpace(configured) ? "（空，用默认）" : configured)}");
        Console.WriteLine($"  实际生效    : {spool.RingRoot}");
        Console.WriteLine($"  自定义目录  : {(spool.RingUsingCustomDir ? "是" : "否（默认目录）")}");

        if (spool.RingFallbackReason is { } reason)
            Console.WriteLine($"  ⚠ 已回落原因: {reason}");

        var configuredButIgnored = !string.IsNullOrWhiteSpace(configured) && !spool.RingUsingCustomDir;

        var free = spool.RingFreeBytes();
        var total = spool.RingTotalBytesOnVolume();

        if (free < 0)
        {
            Console.WriteLine("  卷空间      : 取不到（UNC 路径或权限不足）");
        }
        else
        {
            Console.WriteLine($"  卷容量      : 剩余 {free / 1024.0 / 1024 / 1024:F2} GB / 共 {total / 1024.0 / 1024 / 1024:F1} GB");
        }

        // 实际生效上限 = min(配置上限, (剩余+当前占用)*0.9)，当前占用按 0 估
        var configuredBytes = (long)limitMb * 1024 * 1024;
        var effective = free >= 0 ? (long)Math.Min(configuredBytes, (free * 0.9)) : configuredBytes;

        Console.WriteLine();
        Console.WriteLine("容量估算：");
        Console.WriteLine($"  配置上限    : {limitMb} MB");
        Console.WriteLine($"  实际生效    : {effective / 1024 / 1024} MB");

        var bytesPerSecond = mbps * 1024 * 1024 / 8;
        var keepBytes = (long)(bytesPerSecond * keep);
        Console.WriteLine($"  码率 {mbps}Mbps 时，保留 {keep} 秒需要 {(long)(keepBytes / 1024.0 / 1024):F0} MB");

        var canHoldSeconds = effective / bytesPerSecond;
        Console.WriteLine($"  生效上限能存: {canHoldSeconds:F0} 秒（{canHoldSeconds / 60:F1} 分钟）");

        // 留 15% 余量：环形几乎写满时 ffmpeg 会因写入失败退出，比"少保留几分钟"严重得多
        if (canHoldSeconds < keep * 1.15)
        {
            Console.WriteLine();
            Console.WriteLine($"⚠ 容量偏紧：想保留 {keep} 秒，实际能存 {canHoldSeconds:F0} 秒，" +
                              $"建议至少 {keep * 1.15:F0} 秒的余量。");
            Console.WriteLine("  请调小「环形保留」或换更大的盘（1GB 内存盘按 4Mbps 只够约 30 分钟）。");
            return 1;
        }

        Console.WriteLine();

        if (configuredButIgnored)
        {
            Console.WriteLine("✘ 你填的目录没生效，实际写到了默认目录。上面有回落原因。");
            return 1;
        }

        Console.WriteLine("✅ 环形目录可用");
        return 0;
    }

    /// <summary>
    /// 本地队列状态机回归。不需要网络。
    /// 复现的是：上传中任务被 LoadJobs 重置成 Pending → 被第二个 worker 重复认领。
    /// </summary>
    private static int SpoolTest(Dictionary<string, string> o)
    {
        var root = o.GetValueOrDefault("dir")
                   ?? Path.Combine(Path.GetTempPath(), "owd-spooltest-" + DateTime.Now.ToString("HHmmss"));

        if (Directory.Exists(root)) Directory.Delete(root, true);

        var failures = new List<string>();

        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"  {(ok ? "✔" : "✘")} {name}" + (detail.Length > 0 ? "：" + detail : ""));
            if (!ok) failures.Add(name);
        }

        try
        {
            var log = new AppLog(Path.Combine(root, "logs"));
            var spool = new LocalSpool(Path.Combine(root, "spool"), log);

            var job = spool.CreateJob("cam1", DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow);
            File.WriteAllBytes(job.ClipFile, new byte[1024]);
            job.State = JobState.Uploading;   // 模拟"某个 worker 正在传"
            spool.SaveJob(job);

            var first = spool.LoadJobs().FirstOrDefault(j => j.Id == job.Id);
            Check("LoadJobs 不动 Uploading 状态", first?.State == JobState.Uploading,
                $"读到 {first?.State}");

            var second = spool.LoadJobs().FirstOrDefault(j => j.Id == job.Id);
            Check("再读一次还是 Uploading", second?.State == JobState.Uploading, $"读到 {second?.State}");

            var recovered = spool.RecoverInterruptedUploads();
            Check("启动时崩溃恢复生效", recovered == 1, $"恢复 {recovered} 个");

            var third = spool.LoadJobs().FirstOrDefault(j => j.Id == job.Id);
            Check("恢复后变回 Pending", third?.State == JobState.Pending, $"读到 {third?.State}");

            // 恢复操作只能生效一次：已经是 Pending 的不该被重复计数
            Check("重复调用不再计数", spool.RecoverInterruptedUploads() == 0, "");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ✘ 异常：" + ex);
            failures.Add("异常");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();

        if (failures.Count == 0)
        {
            Console.WriteLine("✅ 本地队列状态机正常");
            return 0;
        }

        Console.WriteLine("✘ 失败项：" + string.Join("；", failures));
        return 1;
    }

    /// <summary>
    /// 并发上传压测：两个 worker 抢 N 个任务，每个任务只允许上传一次。
    /// 旧实现下第二个 worker 会把"上传中"重置成"待上传"从而重复认领，
    /// 表现为 FileNotFoundException + 重复 PUT。配合 mock-dav 使用。
    /// </summary>
    private static async Task<int> UploadRaceAsync(Dictionary<string, string> o)
    {
        var url = o.GetValueOrDefault("url");
        if (string.IsNullOrEmpty(url))
        {
            Console.WriteLine("需要 --url（先起 mock-dav）");
            return 1;
        }

        var jobCount = int.TryParse(o.GetValueOrDefault("jobs", "4"), out var n) ? Math.Clamp(n, 2, 12) : 4;
        var root = Path.Combine(Path.GetTempPath(), "owd-uploadrace-" + DateTime.Now.ToString("HHmmss"));
        if (Directory.Exists(root)) Directory.Delete(root, true);

        // AppLog 会在传入目录下再建 logs/ 子目录，这里传 root
        var log = new AppLog(root);
        var spool = new LocalSpool(Path.Combine(root, "spool"), log);
        var status = new AppStatus();

        for (var i = 0; i < jobCount; i++)
        {
            var start = DateTime.UtcNow.AddMinutes(-jobCount + i);
            var job = spool.CreateJob("cam1", start, start.AddSeconds(20));
            File.WriteAllBytes(job.ClipFile, new byte[64 * 1024]);
            job.State = JobState.Pending;
            spool.SaveJob(job);
        }

        var cfg = new WebDavConfig
        {
            Url = url,
            Username = o.GetValueOrDefault("user", ""),
            Password = o.GetValueOrDefault("pass", ""),
            RootPath = o.GetValueOrDefault("root", "/onvif"),
            TimeoutSeconds = 60,
        };

        await using var queue = new UploadQueue(spool, () => new WebDavClient(cfg, log), log, status,
            moveSupportProvider: () => false)
        {
            Parallelism = 2,
        };

        Console.WriteLine($"并发上传 {jobCount} 个任务（2 个 worker）→ {url}");
        queue.Start();

        var deadline = DateTime.Now.AddSeconds(90);
        while (DateTime.Now < deadline)
        {
            if (!Directory.EnumerateDirectories(spool.JobsRoot).Any()) break;
            await Task.Delay(200).ConfigureAwait(false);
        }

        await queue.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        var left = Directory.Exists(spool.JobsRoot)
            ? Directory.EnumerateDirectories(spool.JobsRoot).Count()
            : 0;

        var logFile = Directory.Exists(Path.Combine(root, "logs"))
            ? Directory.GetFiles(Path.Combine(root, "logs"), "*.log").FirstOrDefault()
            : null;

        Console.WriteLine($"  日志：{logFile ?? "(没有日志文件)"}");

        var logText = logFile is null ? "" : File.ReadAllText(logFile);

        var errors = CountOccurrences(logText, "[ERROR]");

        Console.WriteLine();
        Console.WriteLine($"  剩余未传：{left}");
        Console.WriteLine($"  上传成功计数（status.TodayClips）：{status.TodayClips}");
        Console.WriteLine($"  日志中 ERROR 行：{errors}");

        try { Directory.Delete(root, true); } catch { }

        var ok = left == 0 && status.TodayClips == jobCount && errors == 0;

        Console.WriteLine();
        if (ok)
        {
            Console.WriteLine($"✅ {jobCount} 个任务各上传一次，无异常");
            return 0;
        }

        Console.WriteLine("✘ 失败：");
        if (left != 0) Console.WriteLine($"   还有 {left} 个任务没传完");
        if (status.TodayClips != jobCount)
            Console.WriteLine($"   上传次数 {status.TodayClips} ≠ 任务数 {jobCount}（重复上传）");
        if (errors > 0) Console.WriteLine($"   日志里有 {errors} 条 ERROR（多半是 FileNotFoundException 重复认领）");
        return 1;
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += token.Length;
        }

        return count;
    }

    private static int ParseCapsFile(Dictionary<string, string> o)
    {
        var file = o.GetValueOrDefault("file");
        if (string.IsNullOrEmpty(file) || !File.Exists(file))
        {
            Console.WriteLine("需要 --file <GetServices响应.xml>");
            return 1;
        }

        XDocument doc;
        try { doc = XDocument.Parse(File.ReadAllText(file)); }
        catch (Exception ex)
        {
            Console.WriteLine("✘ XML 解析失败：" + ex.Message);
            return 1;
        }

        var caps = OnvifDeviceOps.ParseServices(doc);

        Console.WriteLine(caps.Describe());
        Console.WriteLine();
        Console.WriteLine("服务明细：");
        foreach (var svc in caps.Services)
            Console.WriteLine($"  · {svc.ShortName}  →  {svc.XAddr}");

        Console.WriteLine();
        Console.WriteLine(caps.ProfileG
            ? "✅ 支持 Profile G（服务列表里有 replay）"
            : "❌ 不支持 Profile G（服务列表里没有 ver10/replay/wsdl）");

        return caps.Services.Count > 0 ? 0 : 1;
    }

    private static async Task<int> CapsAsync(Dictionary<string, string> o)
    {
        var cam = BuildCamera(o);
        if (string.IsNullOrEmpty(cam.Host))
        {
            Console.WriteLine("需要 --host");
            return 1;
        }

        var log = new AppLog(Path.Combine(Path.GetTempPath(), "owd-probe-logs"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var onvif = await OnvifCamera.ConnectAsync(cam, log, cts.Token).ConfigureAwait(false);

        Console.WriteLine("✔ " + onvif.DeviceInfo);
        Console.WriteLine($"   时钟偏差 {onvif.ClockOffset.TotalSeconds:F0} 秒");
        Console.WriteLine($"   Media  {onvif.MediaUri}");
        Console.WriteLine($"   Events {onvif.EventsUri}");
        Console.WriteLine();

        if (onvif.Capabilities is { } caps)
        {
            Console.WriteLine(caps.Describe());
            Console.WriteLine();
            Console.WriteLine("服务明细：");
            foreach (var svc in caps.Services)
                Console.WriteLine($"  · {svc.ShortName}  →  {svc.XAddr}");

            Console.WriteLine();
            Console.WriteLine(caps.ProfileG
                ? "✅ 支持 Profile G：设备端有 replay（录像回放）服务，可以用 SD 卡录像做检索回放。\n" +
                  "   ⚠ 但 Profile G 只提供「回放地址」，前提是设备自己在录像（插卡 + 开录像）。\n" +
                  "   本项目用的是 WebDAV 自建录像，不依赖它。"
                : "❌ 不支持 Profile G：服务列表里没有 ver10/replay/wsdl。\n" +
                  "   → 设备端没有可检索的历史录像，必须由本程序自己录（这正是当前方案）。");
        }
        else
        {
            Console.WriteLine("⚠ 设备没有返回 GetServices（老固件常见），只能用 GetCapabilities 的结果：");
            Console.WriteLine($"   Events {onvif.EventsUri}");
            Console.WriteLine("   这种情况下 Profile 无法判定，但没有 replay 服务基本可以认为不支持 G。");
        }

        Console.WriteLine();
        Console.WriteLine("媒体 Profile：");
        foreach (var p in onvif.Profiles) Console.WriteLine("  · " + p);

        var rtsp = await onvif.GetRtspUrlAsync(cts.Token).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine("RTSP：" + MaskPassword(rtsp));

        if (!onvif.SnapshotSupported)
            Console.WriteLine("提示：ONVIF 声明不支持快照（SnapshotUri=false），抓图不可用，不影响录像。");

        return 0;
    }

    private static async Task<int> DeviceAsync(Dictionary<string, string> o)
    {
        var cam = BuildCamera(o);
        if (string.IsNullOrEmpty(cam.Host))
        {
            Console.WriteLine("需要 --host");
            return 1;
        }

        var log = new AppLog(Path.Combine(Path.GetTempPath(), "owd-probe-logs"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        using var onvif = await OnvifCamera.ConnectAsync(cam, log, cts.Token).ConfigureAwait(false);

        Console.WriteLine("✔ 连接成功");
        Console.WriteLine("  型号：" + onvif.DeviceInfo);
        Console.WriteLine($"  时钟偏差：{onvif.ClockOffset.TotalSeconds:F0} 秒");
        if (Math.Abs(onvif.ClockOffset.TotalSeconds) > 30)
            Console.WriteLine("  ⚠ 时间差过大，ONVIF 鉴权可能失败：请开 NTP 或关闭 Time Verification");

        Console.WriteLine("  profile：");
        foreach (var p in onvif.Profiles) Console.WriteLine("    · " + p);

        var rtsp = await onvif.GetRtspUrlAsync(cts.Token).ConfigureAwait(false);
        Console.WriteLine("  RTSP：" + MaskPassword(rtsp));
        return 0;
    }

    private static async Task<int> StreamAsync(Dictionary<string, string> o)
    {
        var cam = BuildCamera(o);
        var log = new AppLog(Path.Combine(Path.GetTempPath(), "owd-probe-logs"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        using var onvif = await OnvifCamera.ConnectAsync(cam, log, cts.Token).ConfigureAwait(false);
        var rtsp = await onvif.GetRtspUrlAsync(cts.Token).ConfigureAwait(false);

        var locator = new FfmpegLocator();
        if (!locator.IsAvailable)
        {
            Console.WriteLine(locator.MissingMessage);
            return 1;
        }

        var composer = new ClipComposer(locator, log);
        var info = await composer.ProbeStreamAsync(rtsp, cts.Token).ConfigureAwait(false);

        if (info is null)
        {
            Console.WriteLine("✘ ffprobe 没取到信息（RTSP 连不上？）");
            return 1;
        }

        Console.WriteLine($"编码：{info.VideoCodec}  分辨率：{info.Width}x{info.Height}  " +
                          $"音频：{(info.HasAudio ? info.AudioCodec : "无")}");

        if (info.IsHevc)
            Console.WriteLine("⚠ H.265！浏览器基本放不了。建议在摄像头网页把主码流改成 H.264，" +
                              "或打开「H.265 自动转码」。");

        return 0;
    }

    /// <summary>
    /// Basic Notification 验证：本机起回调端点 → wsnt:Subscribe → 等设备 POST 过来。
    /// 这是"设备支持 WSSubscriptionPolicySupport"时最该验证的一条链路。
    /// </summary>
    private static async Task<int> NotifyTestAsync(Dictionary<string, string> o)
    {
        var cam = BuildCamera(o);
        if (string.IsNullOrEmpty(cam.Host))
        {
            Console.WriteLine("需要 --host");
            return 1;
        }

        var listenPort = int.Parse(o.GetValueOrDefault("listen-port", "9099"));
        var seconds = int.Parse(o.GetValueOrDefault("seconds", "120"));
        var log = new AppLog(Path.Combine(Path.GetTempPath(), "owd-probe-logs"));

        using var hardCts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 120));
        using var onvif = await OnvifCamera.ConnectAsync(cam, log, hardCts.Token).ConfigureAwait(false);
        Console.WriteLine("✔ 已连接：" + onvif.DeviceInfo);
        Console.WriteLine("  Events 服务地址：" + onvif.EventsUri);

        var client = new OnvifEventClient(onvif.Soap, log);

        Console.WriteLine();
        Console.WriteLine("== 1) Events 服务能力 ==");
        EventServiceCapabilities? caps = null;
        try
        {
            caps = await client.GetCapabilitiesAsync(onvif.EventsUri, hardCts.Token).ConfigureAwait(false);
            Console.WriteLine("  " + caps);
            if (caps.RawXml.Length > 0)
                Console.WriteLine("  原始：" + Shorten(caps.RawXml, 500));

            if (!caps.SubscriptionPolicySupport)
                Console.WriteLine("  ⚠ 设备报告 WSSubscriptionPolicySupport=false，Subscribe 可能会被拒（仍然试一次）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ✘ 取能力集失败（继续尝试 Subscribe）：" + ex.Message);
        }

        Console.WriteLine();
        Console.WriteLine("== 2) 起本地回调端点 ==");
        var hub = new NotifyHub();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(listenPort));
        builder.Logging.ClearProviders();

        var web = builder.Build();
        web.MapPost("/onvif/notify", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var xml = await reader.ReadToEndAsync(ctx.RequestAborted).ConfigureAwait(false);
            hub.Push(xml);
            Console.WriteLine($"  ← 收到推送 {xml.Length} 字节");
            return Results.Ok();
        });

        await web.StartAsync().ConfigureAwait(false);
        var localIp = LocalAddress.Preferred();
        await using (web.ConfigureAwait(false))
        {
            if (localIp is null)
            {
                Console.WriteLine("  ✘ 没有局域网 IP，设备无法回调");
                return 1;
            }

            var consumer = $"http://{localIp}:{listenPort}/onvif/notify";
            Console.WriteLine($"  监听 0.0.0.0:{listenPort}，回调地址 {consumer}");

            Console.WriteLine();
            Console.WriteLine("== 3) Subscribe（WS-BaseNotification）==");
            EventSubscription sub;
            try
            {
                sub = await client.SubscribeBasicAsync(onvif.EventsUri, onvif.DeviceUri, consumer,
                    TimeSpan.FromSeconds(seconds + 60), hardCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  ✘ 订阅失败：" + ex.Message);
                return 1;
            }

            Console.WriteLine($"  ✔ 订阅成功，订阅管理器 {sub.Address}");
            Console.WriteLine($"  到期时间 {sub.TerminationUtc.ToLocalTime():HH:mm:ss}");

            Console.WriteLine();
            Console.WriteLine($"== 4) 等事件 {seconds} 秒（现在到镜头前走动）==");

            var total = 0;
            var motion = 0;
            var rawPrinted = 0;
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            var nextRenew = DateTime.UtcNow.AddSeconds(40);

            while (DateTime.UtcNow < deadline)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(hardCts.Token);
                readCts.CancelAfter(TimeSpan.FromSeconds(2));

                string? xml = null;
                try { xml = await hub.ReadAsync(readCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }

                if (DateTime.UtcNow >= nextRenew)
                {
                    try
                    {
                        await client.RenewAsync(sub, TimeSpan.FromSeconds(seconds + 60), hardCts.Token)
                            .ConfigureAwait(false);
                        Console.WriteLine("  ↻ 已续订");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  ⚠ 续订失败：" + ex.Message);
                    }

                    nextRenew = DateTime.UtcNow.AddSeconds(40);
                }

                if (xml is null) continue;

                if (rawPrinted < 2)
                {
                    rawPrinted++;
                    Console.WriteLine("  ---- 原始 Notify ----");
                    Console.WriteLine("  " + Shorten(xml, 1500));
                    Console.WriteLine("  ---------------------");
                }

                var events = OnvifEventClient.ParseNotify(xml);
                if (events.Count == 0)
                {
                    Console.WriteLine("  ⚠ 收到 Notify 但解析不出 NotificationMessage");
                    continue;
                }

                foreach (var e in events)
                {
                    total++;
                    if (e.IsMotion) motion++;
                    Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] {e.Topic}  IsMotion={e.IsMotion}  Init={e.Initialized}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"=== 汇总：收到 {hub.Received} 条推送，解析出 {total} 条事件，运动 {motion} 次 ===");

            try { await client.UnsubscribeAsync(sub, CancellationToken.None).ConfigureAwait(false); }
            catch { }

            if (hub.Received == 0)
            {
                Console.WriteLine("✘ 一条推送都没收到。可能原因：");
                Console.WriteLine("  1) 设备认为回调地址不可达（检查 Windows 防火墙是否放行了 " + listenPort + " 端口）");
                Console.WriteLine("  2) 设备要求特定的 ConsumerReference 格式或 Digest 认证");
                Console.WriteLine("  3) 设备根本没启用 ONVIF 报警上报");
                return 1;
            }

            if (motion == 0)
            {
                Console.WriteLine("⚠ 收到推送但没有 IsMotion=true —— 事件通道通了，只是运动检测没触发。");
                return 1;
            }

            Console.WriteLine("✅ Basic Notification 链路可用（推送模式）。");
            return 0;
        }
    }

    private static async Task<int> EventsAsync(Dictionary<string, string> o)
    {
        var cam = BuildCamera(o);
        var seconds = int.Parse(o.GetValueOrDefault("seconds", "60"));
        var log = new AppLog(Path.Combine(Path.GetTempPath(), "owd-probe-logs"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 30));

        using var onvif = await OnvifCamera.ConnectAsync(cam, log, cts.Token).ConfigureAwait(false);
        Console.WriteLine("✔ 已连接：" + onvif.DeviceInfo);

        try
        {
            var probe = new OnvifEventClient(onvif.Soap, log);
            var caps = await probe.GetCapabilitiesAsync(onvif.EventsUri, cts.Token).ConfigureAwait(false);
            Console.WriteLine("  Events 能力：" + caps);
            if (caps.SubscriptionPolicySupport)
                Console.WriteLine("  → 支持 Basic Notification，可试试：notify-test（推送比轮询更实时）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ⚠ 取 Events 能力集失败：" + ex.Message);
        }

        Console.WriteLine($"用 PullPoint 订阅运动事件，持续 {seconds} 秒。现在请到摄像头前面走动一下……");

        var total = 0;
        var motionOn = 0;
        var topics = new HashSet<string>();

        await using var source = new OnvifEventMotionSource(onvif, log);

        source.RawEvent += e =>
        {
            total++;
            topics.Add(e.Topic);
            Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] {e.Topic}  IsMotion={e.IsMotion}  Init={e.Initialized}");
        };

        source.MotionStarted += t => motionOn++;
        source.MotionEnded += _ => { };

        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, limit.Token);

        try
        {
            await source.RunAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        Console.WriteLine();
        Console.WriteLine($"=== 汇总：{seconds} 秒内收到 {total} 条事件，运动开始 {motionOn} 次 ===");

        foreach (var t in topics) Console.WriteLine("  topic：" + t);

        if (total == 0)
        {
            Console.WriteLine("✘ 一条事件都没有 → 这套方案的事件源要换（快照差分），或者摄像头没开报警上报。");
            return 1;
        }

        if (motionOn == 0)
        {
            Console.WriteLine("⚠ 收到事件但没有任何 IsMotion=true → 检查运动检测是否启用、灵敏度是否太低。");
            return 1;
        }

        Console.WriteLine("✅ 事件可用。");
        return 0;
    }

    // ---------------- 辅助 ----------------

    private static CameraConfig BuildCamera(Dictionary<string, string> o) => new()
    {
        Id = "cam1",
        Name = "probe",
        Host = o.GetValueOrDefault("host", ""),
        OnvifPort = int.TryParse(o.GetValueOrDefault("port", "2020"), out var p) ? p : 2020,
        Username = o.GetValueOrDefault("user", "admin"),
        Password = o.GetValueOrDefault("pass", ""),
        Stream = o.GetValueOrDefault("stream", "main"),
        RtspUrl = o.GetValueOrDefault("rtsp", ""),
        RtspPort = int.TryParse(o.GetValueOrDefault("rtsp-port", "554"), out var rp) ? rp : 554,
    };

    private static int ParseNotifyFile(Dictionary<string, string> o)
    {
        var file = o.GetValueOrDefault("file");
        if (string.IsNullOrEmpty(file) || !File.Exists(file))
        {
            Console.WriteLine("需要 --file <xml文件>");
            return 1;
        }

        var xml = File.ReadAllText(file);
        var events = OnvifEventClient.ParseNotify(xml);

        Console.WriteLine($"文件 {file}（{xml.Length} 字符）解析出 {events.Count} 条事件：");
        foreach (var e in events)
        {
            Console.WriteLine($"  {e.Topic}");
            Console.WriteLine($"    IsMotion={e.IsMotion}  Initialized={e.Initialized}  " +
                              $"设备时间={e.UtcTime.ToLocalTime():HH:mm:ss}");
        }

        return events.Count > 0 ? 0 : 1;
    }

    private static string Shorten(string text, int max)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (flat.Contains("  ", StringComparison.Ordinal)) flat = flat.Replace("  ", " ");
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static string MaskPassword(string rtsp)
    {
        if (!Uri.TryCreate(rtsp, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.UserInfo))
            return rtsp;
        var b = new UriBuilder(u) { Password = "******" };
        return b.Uri.ToString();
    }

    private static int Fail(string message)
    {
        Console.WriteLine("✘ " + message);
        return 1;
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                dict["_"] = a;
                continue;
            }

            var key = a[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                dict[key] = args[++i];
            else
                dict[key] = "true";
        }

        return dict;
    }
}
