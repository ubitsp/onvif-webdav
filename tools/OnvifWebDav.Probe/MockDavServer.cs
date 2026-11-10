using System.Text;
using Microsoft.AspNetCore.Http;

namespace OnvifWebDav.Probe;

/// <summary>
/// 极简 WebDAV 服务器，只为在没有 NAS 的情况下验证 WebDavClient：
/// OPTIONS / PROPFIND / MKCOL / PUT / GET / HEAD / MOVE / DELETE + Basic 认证。
/// 不支持 Digest / Lock（那部分要在真服务端上验）。
/// </summary>
internal sealed class MockDavServer
{
    private readonly string _root;
    private readonly string _user;
    private readonly string _pass;
    private readonly bool _redirectDownload;
    private readonly bool _noHead;
    private readonly long _quotaBytes;

    private const string CdnPrefix = "/cdn";

    public MockDavServer(string root, string user, string pass, bool redirectDownload = false,
        bool noHead = false, long quotaBytes = 0)
    {
        _quotaBytes = quotaBytes;
        _root = Path.GetFullPath(root);
        _user = user;
        _pass = pass;
        _redirectDownload = redirectDownload;
        _noHead = noHead;
        Directory.CreateDirectory(_root);
    }

    public async Task HandleAsync(HttpContext ctx)
    {
        // 模拟阿里云盘 CDN：直链地址不需要认证（真实环境是带签名的 URL）
        if (ctx.Request.Path.StartsWithSegments(CdnPrefix))
        {
            var cdnRel = ctx.Request.Path.Value![CdnPrefix.Length..].TrimStart('/');
            var cdnFull = Path.GetFullPath(Path.Combine(_root, Uri.UnescapeDataString(cdnRel)));

            if (!cdnFull.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || !File.Exists(cdnFull))
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength = new FileInfo(cdnFull).Length;
            await ctx.Response.SendFileAsync(cdnFull).ConfigureAwait(false);
            return;
        }

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_user}:{_pass}"));
        if (!string.Equals(ctx.Request.Headers.Authorization.ToString(), expected, StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"mock-dav\"";
            return;
        }

        var relPath = Uri.UnescapeDataString(ctx.Request.Path.Value ?? "/").Trim('/');
        var full = Path.GetFullPath(Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar)));

        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = 403;
            return;
        }

        var method = ctx.Request.Method.ToUpperInvariant();
        Console.WriteLine($"  [{method}] /{relPath}");

        switch (method)
        {
            case "OPTIONS":
                ctx.Response.Headers.Allow = "OPTIONS, PROPFIND, MKCOL, PUT, GET, HEAD, MOVE, DELETE";
                ctx.Response.Headers["DAV"] = "1";
                ctx.Response.StatusCode = 200;
                break;

            case "PROPFIND":
                await PropFindAsync(ctx, relPath, full).ConfigureAwait(false);
                break;

            case "MKCOL":
                if (Directory.Exists(full)) ctx.Response.StatusCode = 405;
                else
                {
                    Directory.CreateDirectory(full);
                    ctx.Response.StatusCode = 201;
                }
                break;

            case "PUT":
                var parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                await using (var fs = File.Create(full))
                    await ctx.Request.Body.CopyToAsync(fs).ConfigureAwait(false);
                ctx.Response.StatusCode = 201;
                break;

            case "GET":
            case "HEAD":
                if (!File.Exists(full))
                {
                    ctx.Response.StatusCode = 404;
                    break;
                }

                // 模拟 aliyundrive-webdav：HEAD 直接 405
                if (_noHead && method == "HEAD")
                {
                    ctx.Response.StatusCode = 405;
                    break;
                }

                // 模拟 aliyundrive-webdav 的 --redirect 默认行为：302 到 CDN 直链
                if (_redirectDownload && method == "GET")
                {
                    var location = $"{ctx.Request.Scheme}://{ctx.Request.Host}{CdnPrefix}/{relPath}";
                    Console.WriteLine($"  [302] /{relPath} → {location}");
                    ctx.Response.StatusCode = 302;
                    ctx.Response.Headers.Location = location;
                    break;
                }
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentLength = new FileInfo(full).Length;
                ctx.Response.ContentType = "application/octet-stream";
                if (method == "GET")
                    await ctx.Response.SendFileAsync(full).ConfigureAwait(false);
                break;

            case "MOVE":
                var dest = ctx.Request.Headers["Destination"].ToString();
                if (string.IsNullOrEmpty(dest))
                {
                    ctx.Response.StatusCode = 400;
                    break;
                }
                var destRel = Uri.UnescapeDataString(new Uri(dest).AbsolutePath).Trim('/');
                var destFull = Path.GetFullPath(Path.Combine(_root,
                    destRel.Replace('/', Path.DirectorySeparatorChar)));

                if (!destFull.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = 403;
                    break;
                }

                if (!File.Exists(full))
                {
                    ctx.Response.StatusCode = 404;
                    break;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
                if (File.Exists(destFull)) File.Delete(destFull);
                File.Move(full, destFull);
                ctx.Response.StatusCode = 201;
                break;

            case "DELETE":
                if (File.Exists(full)) File.Delete(full);
                else if (Directory.Exists(full)) Directory.Delete(full, true);
                ctx.Response.StatusCode = 204;
                break;

            default:
                ctx.Response.StatusCode = 405;
                break;
        }
    }

    private async Task PropFindAsync(HttpContext ctx, string relPath, string full)
    {
        if (!Directory.Exists(full) && !File.Exists(full))
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        var depth = ctx.Request.Headers["Depth"].ToString();
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="utf-8"?><D:multistatus xmlns:D="DAV:">""");

        string? quotaProps = null;
        if (_quotaBytes > 0 && relPath.Length == 0)
        {
            long used = 0;
            try
            {
                used = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                    .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            }
            catch { }

            var available = Math.Max(0, _quotaBytes - used);
            quotaProps = $"<D:quota-used-bytes>{used}</D:quota-used-bytes>" +
                         $"<D:quota-available-bytes>{available}</D:quota-available-bytes>";
        }

        AppendEntry(sb, "/" + relPath, full, Directory.Exists(full), quotaProps);

        if (Directory.Exists(full) && depth != "0")
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(full))
            {
                var name = Path.GetFileName(entry);
                AppendEntry(sb, "/" + (relPath.Length > 0 ? relPath + "/" : "") + name,
                    entry, Directory.Exists(entry));
            }
        }

        sb.Append("</D:multistatus>");

        ctx.Response.StatusCode = 207;
        ctx.Response.ContentType = "application/xml; charset=utf-8";
        await ctx.Response.WriteAsync(sb.ToString(), Encoding.UTF8).ConfigureAwait(false);
    }

    private static void AppendEntry(StringBuilder sb, string href, string localPath, bool isCollection,
        string? extraProps = null)
    {
        var encoded = string.Join('/', href.Split('/').Select(Uri.EscapeDataString));
        var length = isCollection ? 0 : new FileInfo(localPath).Length;
        var lastMod = File.GetLastWriteTimeUtc(localPath).ToString("R");

        sb.Append("<D:response>");
        sb.Append("<D:href>").Append(System.Security.SecurityElement.Escape(encoded)).Append("</D:href>");
        sb.Append("<D:propstat><D:prop>");
        sb.Append(isCollection
            ? "<D:resourcetype><D:collection/></D:resourcetype>"
            : "<D:resourcetype/>");
        if (!isCollection) sb.Append("<D:getcontentlength>").Append(length).Append("</D:getcontentlength>");
        sb.Append("<D:getlastmodified>").Append(lastMod).Append("</D:getlastmodified>");
        if (extraProps is not null) sb.Append(extraProps);
        sb.Append("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>");
        sb.Append("</D:response>");
    }
}
