using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OnvifWebDav.Onvif;
using OnvifWebDav.Pipeline;
using OnvifWebDav.Storage;
using OnvifWebDav.Web.Auth;

namespace OnvifWebDav.Web;

/// <summary>
/// 在 WinForms 进程里托管网页。只 Build 不 Run，由 App 决定什么时候启动/停止。
/// 站点结构：登录 + 日期选择 + 24 小时时间轴 + Range 播放（MP4 走本地缓存再落地输出，手机才能拖进度）。
/// </summary>
public static class WebHostFactory
{
    public static WebApplication Build(AppRuntime runtime, int port)
    {
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = wwwroot,
            EnvironmentName = Environments.Production,
        });

        // 只用内存配置：不要读 appsettings.json，避免和 config.json 两份配置打架
        builder.Configuration.Sources.Clear();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(port);
            options.AddServerHeader = false;
        });

        builder.Services.AddSingleton(runtime);
        builder.Logging.ClearProviders();

        var app = builder.Build();

        app.UseMiddleware<AuthMiddleware>();

        MapAuth(app, runtime, wwwroot);
        MapApi(app, runtime);
        MapMedia(app, runtime);

        app.UseDefaultFiles();
        app.UseStaticFiles();

        return app;
    }

    // ---------------- 登录 ----------------

    private static void MapAuth(WebApplication app, AppRuntime runtime, string wwwroot)
    {
        string LoginPage()
        {
            var file = Path.Combine(wwwroot, "login.html");
            return File.Exists(file) ? File.ReadAllText(file) : "<h1>缺少 login.html</h1>";
        }

        app.MapGet("/login", (HttpContext ctx) =>
        {
            var cfg = runtime.Config.Web;
            // 没设密码直接进站
            if (string.IsNullOrEmpty(cfg.Password)) return Results.Redirect("/");
            return Results.Content(LoginPage().Replace("{{ERROR}}", ""), "text/html; charset=utf-8");
        });

        app.MapPost("/login", async (HttpContext ctx) =>
        {
            var cfg = runtime.Config.Web;
            var form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            var password = form["password"].ToString();
            var returnUrl = SanitizeReturnUrl(form["returnUrl"].ToString());

            if (string.IsNullOrEmpty(cfg.Password) || AuthTicket.PasswordEquals(cfg.Password, password))
            {
                var expires = DateTime.Now.AddDays(Math.Clamp(cfg.SessionDays, 1, 365));
                ctx.Response.Cookies.Append(AuthTicket.CookieName,
                    AuthTicket.Issue(cfg.CookieKey, expires),
                    new CookieOptions
                    {
                        HttpOnly = true,
                        // 局域网是 http，绝对不能设 Secure，否则 Cookie 根本不生效
                        Secure = false,
                        SameSite = SameSiteMode.Lax,
                        Expires = expires,
                        IsEssential = true,
                        Path = "/",
                    });

                runtime.Log.Info("web", $"登录成功，来自 {ctx.Connection.RemoteIpAddress}");
                return Results.Redirect(returnUrl);
            }

            runtime.Log.Warn("web", $"登录失败，来自 {ctx.Connection.RemoteIpAddress}");
            return Results.Content(LoginPage().Replace("{{ERROR}}", "密码不正确"),
                "text/html; charset=utf-8");
        });

        app.MapPost("/logout", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(AuthTicket.CookieName);
            return Results.Redirect("/login");
        });
    }

    private static string SanitizeReturnUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "/";
        if (!raw.StartsWith('/')) return "/";
        if (raw.StartsWith("//")) return "/";
        return raw;
    }

    // ---------------- 接口 ----------------

    private static void MapApi(WebApplication app, AppRuntime runtime)
    {
        app.MapGet("/api/cameras", () => runtime.Config.Cameras
            .Where(c => c.Enabled)
            .Select(c => new CameraInfo(c.Id, c.Name, c.Enabled))
            .ToList());

        app.MapGet("/api/dates", async (string? cameraId, CancellationToken ct) =>
        {
            var cam = ResolveCamera(runtime, cameraId);
            if (cam is null) return Results.BadRequest(new { error = "没有可用的摄像头" });

            if (string.IsNullOrWhiteSpace(runtime.Config.WebDav.Url))
                return Results.Ok(Array.Empty<string>());

            try
            {
                var dates = await runtime.Recordings.GetDatesAsync(cam.Id, ct).ConfigureAwait(false);
                return Results.Ok(dates.Select(d => d.ToString("yyyy-MM-dd")));
            }
            catch (Exception ex)
            {
                runtime.Log.Warn("web", $"列日期失败：{ex.Message}");
                return Results.Ok(Array.Empty<string>());
            }
        });

        app.MapGet("/api/clips", async (string? cameraId, string? date, CancellationToken ct) =>
        {
            var cam = ResolveCamera(runtime, cameraId);
            if (cam is null) return Results.BadRequest(new { error = "没有可用的摄像头" });
            if (!TryParseDate(date, out var day)) return Results.BadRequest(new { error = "date 格式应为 yyyy-MM-dd" });

            try
            {
                var clips = await runtime.Recordings.GetClipsAsync(cam.Id, day, ct).ConfigureAwait(false);
                return Results.Ok(clips.Select(c => new
                {
                    id = c.Id,
                    startUtc = c.StartUtc,
                    endUtc = c.EndUtc,
                    start = c.StartLocal.ToString("HH:mm:ss"),
                    end = c.EndLocal.ToString("HH:mm:ss"),
                    startMinute = c.StartLocal.Hour * 60 + c.StartLocal.Minute + c.StartLocal.Second / 60.0,
                    endMinute = c.EndLocal.Hour * 60 + c.EndLocal.Minute + c.EndLocal.Second / 60.0,
                    durationSec = Math.Max(0, c.DurationMs / 1000),
                    sizeMb = Math.Round(c.SizeBytes / 1024.0 / 1024.0, 1),
                    codec = c.VideoCodec,
                    hasAudio = c.HasAudio,
                    continuedFrom = c.ContinuedFrom,
                    gapCount = c.GapCount,
                    topics = c.Topics,
                    mediaUrl = $"/media/{Uri.EscapeDataString(c.CameraId)}/{day:yyyy-MM-dd}/{Uri.EscapeDataString(c.Id)}",
                    thumbUrl = $"/thumb/{Uri.EscapeDataString(c.CameraId)}/{day:yyyy-MM-dd}/{Uri.EscapeDataString(c.Id)}",
                }));
            }
            catch (Exception ex)
            {
                runtime.Log.Warn("web", $"列录像失败：{ex.Message}");
                return Results.Ok(Array.Empty<object>());
            }
        });

        app.MapGet("/api/status", () =>
        {
            var s = runtime.Status;
            return new
            {
                state = s.StateText,
                stateCode = (int)s.State,
                message = s.Message,
                codec = s.Codec,
                queue = s.QueueCount,
                spoolMb = Math.Round(s.SpoolBytes / 1024.0 / 1024.0, 1),
                lastMotion = s.LastMotion?.ToString("HH:mm:ss"),
                lastUpload = s.LastUpload?.ToString("HH:mm:ss"),
                todayClips = s.TodayClips,
                ffmpegAlive = s.FfmpegAlive,
                webDavConfigured = !string.IsNullOrWhiteSpace(runtime.Config.WebDav.Url),
                eventMode = s.EventMode,
                pushReceived = runtime.NotifyHub.Received,
                ringMb = Math.Round(s.RingMb, 1),
                ringPath = s.RingPath,
                quotaText = s.Quota?.Describe(),
                quotaAvailableGb = s.Quota?.AvailableGb is { } ag && ag >= 0 ? Math.Round(ag, 1) : (double?)null,
                error = s.LastError,
            };
        });

        app.MapGet("/api/snapshot", async (string? cameraId, CancellationToken ct) =>
        {
            var cam = ResolveCamera(runtime, cameraId);
            if (cam is null) return Results.NotFound();

            try
            {
                using var onvif = await OnvifCamera.ConnectAsync(cam, runtime.Log, ct).ConfigureAwait(false);
                var jpeg = await onvif.GetSnapshotJpegAsync(ct).ConfigureAwait(false);
                return Results.Bytes(jpeg, "image/jpeg");
            }
            catch (Exception ex)
            {
                return Results.Problem("抓快照失败：" + ex.Message);
            }
        });

        // ONVIF Basic Notification 的接收端点：设备把事件 POST 到这里。
        // 必须免鉴权（设备不会带 Cookie），并且原样读 body 交给 NotifyHub。
        app.MapPost("/onvif/notify", async (HttpContext ctx) =>
        {
            string xml;
            using (var reader = new StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8))
                xml = await reader.ReadToEndAsync(ctx.RequestAborted).ConfigureAwait(false);

            runtime.NotifyHub.Push(xml);
            runtime.Log.Debug("notify", $"收到设备推送 {xml.Length} 字节");

            // WS-BaseNotification 只要求 200，不要返回 body
            return Results.Ok();
        });

        app.MapGet("/health", () => Results.Ok(new { ok = true, time = DateTime.Now }));
    }

    // ---------------- 媒体 ----------------

    private static void MapMedia(WebApplication app, AppRuntime runtime)
    {
        app.MapGet("/media/{cameraId}/{date}/{clipId}",
            async (string cameraId, string date, string clipId, CancellationToken ct) =>
            {
                if (!TryParseDate(date, out var day)) return Results.BadRequest();

                var clip = await runtime.Recordings.FindClipAsync(cameraId, day, clipId, ct)
                    .ConfigureAwait(false);
                if (clip is null) return Results.NotFound();

                var path = await runtime.Recordings.GetVideoFileAsync(clip, ct).ConfigureAwait(false);
                if (path is null) return Results.NotFound();

                return Results.File(path, "video/mp4", enableRangeProcessing: true,
                    lastModified: new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
            });

        app.MapGet("/thumb/{cameraId}/{date}/{clipId}",
            async (string cameraId, string date, string clipId, CancellationToken ct) =>
            {
                if (!TryParseDate(date, out var day)) return Results.BadRequest();

                var clip = await runtime.Recordings.FindClipAsync(cameraId, day, clipId, ct)
                    .ConfigureAwait(false);
                if (clip is null) return Results.NotFound();

                var path = await runtime.Recordings.GetThumbFileAsync(clip, ct).ConfigureAwait(false);
                if (path is null) return Results.NotFound();

                return Results.File(path, "image/jpeg");
            });
    }

    private static OnvifWebDav.Config.CameraConfig? ResolveCamera(AppRuntime runtime, string? cameraId)
    {
        if (string.IsNullOrWhiteSpace(cameraId))
            return runtime.Config.PrimaryCamera;

        return runtime.Config.Cameras.FirstOrDefault(c =>
            string.Equals(c.Id, cameraId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseDate(string? text, out DateTime date) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd", null,
            System.Globalization.DateTimeStyles.None, out date);
}
