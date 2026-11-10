namespace OnvifWebDav.Web.Auth;

/// <summary>
/// 全站鉴权中间件：静态文件、接口、视频流一律拦截。
/// 放行清单只有登录页、favicon，避免"忘记给某个接口加鉴权"这种低级漏洞。
/// </summary>
public sealed class AuthMiddleware
{
    private readonly RequestDelegate _next;

    public AuthMiddleware(RequestDelegate next) => _next = next;

    private static readonly string[] AnonymousPaths =
    {
        "/login", "/favicon.ico", "/app.css", "/app.js", "/login.css", "/login.js",
        // 摄像头 POST 推送事件用，设备不会带我们的 Cookie，必须放行
        "/onvif/notify",
    };

    public async Task InvokeAsync(HttpContext ctx, AppRuntime runtime)
    {
        var cfg = runtime.Config.Web;

        // 没设密码 = 敞开
        if (string.IsNullOrEmpty(cfg.Password))
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        var path = ctx.Request.Path.Value ?? "/";
        if (AnonymousPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        var cookie = ctx.Request.Cookies[AuthTicket.CookieName];
        if (AuthTicket.Validate(cfg.CookieKey, cookie))
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/thumb/", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "未登录或登录已过期" }).ConfigureAwait(false);
            return;
        }

        ctx.Response.Redirect("/login?returnUrl=" + Uri.EscapeDataString(path));
    }
}
