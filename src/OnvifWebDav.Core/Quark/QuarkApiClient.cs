using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OnvifWebDav.Diagnostics;

namespace OnvifWebDav.Quark;

/// <summary>
/// 夸克网盘 API 客户端（原生 HttpClient，不使用浏览器传输）。
///
/// 阶段 0 的探测结论（见 AGENTS.md 第 53~57 条）：
///  · 查询/写入接口的动词基本都是 <b>GET</b>，带 body 的写接口是 POST；
///    对只读接口用 POST 会返回 405，且这个 405 极易被误判成风控。
///  · drive.quark.cn 与 drive-pc.quark.cn 等价，统一用后者。
///  · 响应里的 Set-Cookie: __puus 是滚动令牌，必须合并回会话，否则几小时后失效。
///
/// 本类只做「协议层」：拼 URL、带 Cookie、解信封。不含任何业务语义。
/// </summary>
public sealed class QuarkApiClient : IDisposable
{
    public const string DefaultBaseUrl = "https://drive-pc.quark.cn";

    private const string PanOrigin = "https://pan.quark.cn";
    private const string PanReferer = "https://pan.quark.cn/";

    /// <summary>所有接口都必须带的固定参数，少了会被当成未知客户端。</summary>
    private const string FixedQuery = "pr=ucpro&fr=pc&uc_param_str=";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly AppLog? _log;
    private readonly ConcurrentDictionary<string, string> _cookies = new(StringComparer.Ordinal);
    private readonly string _userAgent;

    /// <summary>每次响应的 Set-Cookie 都会更新计数，用于判断会话是否还活着。</summary>
    private int _cookieRefreshCount;

    private long _requestSeq;

    public QuarkApiClient(QuarkCdpClient.Session session, AppLog? log = null, string? baseUrl = null)
    {
        BaseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');
        _log = log;
        _userAgent = session.UserAgent;
        LoadCookies(session.Cookie);

        // 不复用外部 handler：夸克的 Cookie 我们自己管，禁掉自动 Cookie 容器避免和手拼冲突
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
        };

        _http = new HttpClient(handler) { Timeout = DefaultTimeout };
    }

    public string BaseUrl { get; }

    /// <summary>当前 Cookie 串（含已合并的滚动令牌）。用于落盘/续用。</summary>
    public string CurrentCookie => string.Join("; ", _cookies.Select(kv => $"{kv.Key}={kv.Value}"));

    public string UserAgent => _userAgent;

    /// <summary>服务端下发过几次新 Cookie。大于 0 说明会话在滚动续期。</summary>
    public int CookieRefreshCount => Volatile.Read(ref _cookieRefreshCount);

    /// <summary>读单个 Cookie 值。用来取 __uid 做账号指纹。</summary>
    public string? ReadCookie(string name) => _cookies.TryGetValue(name, out var v) ? v : null;

    // ==================== 通用请求 ====================

    /// <summary>
    /// 发一个业务请求并解信封，返回 data 节点。
    /// 失败抛 <see cref="QuarkApiException"/>，调用方自己决定是否吞掉。
    /// </summary>
    public async Task<JsonNode?> SendAsync(HttpMethod method, string pathAndQuery, JsonNode? body,
        CancellationToken ct, string? absoluteUrl = null)
    {
        var url = absoluteUrl ?? BaseUrl + pathAndQuery;

        using var req = new HttpRequestMessage(method, url);

        // 这些头必须 TryAddWithoutValidation：HttpClient 会拒绝对"库自己管"的头直接 Add
        req.Headers.TryAddWithoutValidation("Cookie", CurrentCookie);
        req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        req.Headers.TryAddWithoutValidation("Referer", PanReferer);
        req.Headers.TryAddWithoutValidation("Origin", PanOrigin);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");

        if (body is not null)
        {
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        else if (method == HttpMethod.Post || method == HttpMethod.Put)
        {
            // 部分 POST 接口即使无参也要求有 JSON body
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        var seq = Interlocked.Increment(ref _requestSeq);

        HttpResponseMessage resp;
        string text;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new QuarkApiException($"请求超时（{DefaultTimeout.TotalSeconds:F0} 秒）：{method} {pathAndQuery}", -1, 0);
        }

        using (resp)
        {
            AbsorbSetCookie(resp);

            var httpStatus = (int)resp.StatusCode;

            if (text.Length == 0)
                throw new QuarkApiException($"响应体为空：{method} {pathAndQuery}", -1, httpStatus);

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                // 非 JSON = 网关/WAF 的 HTML 错误页（或 302 落到登录页）
                throw new QuarkApiException(
                    $"响应不是 JSON（HTTP {httpStatus}），多半是网关/WAF 拦截：" +
                    $"{(text.Length > 200 ? text[..200] : text)}", -1, httpStatus);
            }

            if (root is not JsonObject obj)
                throw new QuarkApiException($"响应不是 JSON 对象：{method} {pathAndQuery}", -1, httpStatus);

            var status = ReadInt(obj, "status");
            var code = ReadInt(obj, "code");
            var message = obj["message"]?.GetValue<string>() ?? "";

            // 405 是最容易误判的一种：请求已到业务层，只是动词不对
            if (httpStatus == 405 || status == 405)
            {
                throw new QuarkApiException(
                    $"HTTP 405：{method} {pathAndQuery} 的动词不被接受。" +
                    "夸克的只读接口只接受 GET，别用 POST。" +
                    (message.Length > 0 ? $"（服务端：{message}）" : ""),
                    -1, httpStatus);
            }

            if (httpStatus is < 200 or >= 300)
            {
                throw new QuarkApiException(
                    $"HTTP {httpStatus}：{method} {pathAndQuery}" +
                    (message.Length > 0 ? $"，服务端：{message}" : ""),
                    code, httpStatus);
            }

            // status 和 code 都要判：历史上出现过 status=200 但 code!=0
            var envelopeOk = status is 200 or -1 && code is 0 or -1;
            if (!envelopeOk)
            {
                var hint = code == 31001 || message.Contains("登录", StringComparison.Ordinal)
                    ? "（会话失效，需要重新从浏览器取 Cookie）"
                    : "";

                throw new QuarkApiException(
                    $"业务失败：status={status} code={code}" +
                    (message.Length > 0 ? $" message={message}" : "") + hint,
                    code, httpStatus);
            }

            _log?.Info("quark", $"#{seq} {method} {pathAndQuery} → {httpStatus}" +
                               (CookieRefreshCount > 0 ? $"（已续期 {CookieRefreshCount} 次）" : ""));

            return obj["data"];
        }
    }

    /// <summary>GET 便捷封装。</summary>
    public Task<JsonNode?> GetAsync(string pathAndQuery, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, pathAndQuery, null, ct);

    /// <summary>POST 便捷封装（带 body 的写接口）。</summary>
    public Task<JsonNode?> PostAsync(string pathAndQuery, JsonNode? body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, pathAndQuery, body, ct);

    // ==================== 业务封装 ====================

    /// <summary>会员/配额信息。</summary>
    public Task<JsonNode?> GetMemberAsync(CancellationToken ct) =>
        GetAsync($"/1/clouddrive/member?{FixedQuery}&fetch_subscribe=true&_ch=home&fetch_identity=true", ct);

    /// <summary>列目录（fid=0 是根目录）。</summary>
    public Task<JsonNode?> ListAsync(string pdirFid, int page, int size, CancellationToken ct) =>
        GetAsync($"/1/clouddrive/file/sort?{FixedQuery}" +
                 $"&pdir_fid={Uri.EscapeDataString(pdirFid)}" +
                 $"&_page={page}&_size={size}&_fetch_total=1&_fetch_sub_dirs=0" +
                 "&_sort=file_type:asc,updated_at:desc", ct);

    /// <summary>在父目录下建同名文件夹，返回新目录的 fid。</summary>
    public async Task<string> CreateFolderAsync(string pdirFid, string name, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["pdir_fid"] = pdirFid,
            ["file_name"] = name,
            ["dir_path"] = "",
            ["dir_path_file_name"] = "",
        };

        var data = await PostAsync($"/1/clouddrive/file?{FixedQuery}", body, ct).ConfigureAwait(false);
        var fid = data?["fid"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(fid))
            throw new QuarkApiException($"建目录成功但没返回 fid：{name}", -1, 200);

        return fid;
    }

    /// <summary>删除（action_type=2 进回收站，=40 彻底删除）。注意回收站不释放配额时间。</summary>
    public async Task DeleteAsync(IEnumerable<string> fids, int actionType, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["action_type"] = actionType,
            ["filelist"] = new JsonArray(fids.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            ["exclude_fids"] = new JsonArray(),
        };

        await PostAsync($"/1/clouddrive/file/delete?{FixedQuery}", body, ct).ConfigureAwait(false);
    }

    /// <summary>上传预处理：拿到 task_id / upload_id / obj_key / bucket / auth_info。</summary>
    /// <remarks>
    /// format_type 必须传**完整 MIME**（"video/mp4"），不是扩展名 —— 抓包确认过。
    /// 时间戳字段让网盘保留原始修改时间，录像文件按时间排序时很有用。
    /// </remarks>
    public async Task<JsonNode> UploadPreAsync(string pdirFid, string fileName, long size,
        DateTime? modifiedUtc, CancellationToken ct)
    {
        var epochMs = new DateTimeOffset((modifiedUtc ?? DateTime.UtcNow).ToUniversalTime())
            .ToUnixTimeMilliseconds();

        var body = new JsonObject
        {
            ["ccp_hash_update"] = true,
            // 必须 false。开启并行分片后，服务端要求先建立 hash context，
            // 第 2 片起会直接 400 "NoHashContext / Has No Hash Context in Parallel Multipart file"。
            // 顺序上传虽然慢一点，但不用那套额外协议。
            ["parallel_upload"] = false,
            ["pdir_fid"] = pdirFid,
            ["dir_name"] = "",
            ["size"] = size,
            ["file_name"] = fileName,
            ["format_type"] = GuessMime(fileName),
            ["l_created_at"] = epochMs,
            ["l_updated_at"] = epochMs,
        };

        var data = await PostAsync($"/1/clouddrive/file/upload/pre?{FixedQuery}", body, ct).ConfigureAwait(false)
                   ?? throw new QuarkApiException("upload/pre 没有返回 data", -1, 200);

        return data;
    }

    /// <summary>取直传签名。auth_meta 是 OSS 的 string-to-sign，见 <see cref="BuildPutAuthMeta"/>。</summary>
    /// <remarks>
    /// 请求体只有三个字段：task_id、auth_info、auth_meta。
    /// 之前把 auth_meta 当对象传（{bucket, object_key, callback}）会得到 500 inner error ——
    /// 服务端拿它直接做签名串比对，结构错了就崩。
    /// </remarks>
    public async Task<JsonNode> UploadAuthAsync(string taskId, string authInfo, string authMeta,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["task_id"] = taskId,
            ["auth_info"] = authInfo,
            ["auth_meta"] = authMeta,
        };

        var data = await PostAsync($"/1/clouddrive/file/upload/auth?{FixedQuery}", body, ct).ConfigureAwait(false)
                   ?? throw new QuarkApiException("upload/auth 没有返回 data", -1, 200);

        return data;
    }

    /// <summary>上传完成后确认。抓包确认只需要 obj_key 和 task_id，不用传 fid/md5/size。</summary>
    public Task<JsonNode?> UploadFinishAsync(string objKey, string taskId, CancellationToken ct) =>
        PostAsync($"/1/clouddrive/file/upload/finish?{FixedQuery}", new JsonObject
        {
            ["obj_key"] = objKey,
            ["task_id"] = taskId,
        }, ct);

    /// <summary>上报文件哈希（服务端据此秒传/去重）。失败不影响上传本身。</summary>
    public Task<JsonNode?> UpdateHashAsync(string taskId, string md5, string sha1, CancellationToken ct) =>
        PostAsync($"/1/clouddrive/file/update/hash?{FixedQuery}", new JsonObject
        {
            ["task_id"] = taskId,
            ["md5"] = md5,
            ["sha1"] = sha1,
        }, ct);

    /// <summary>取下载地址。返回的 URL 自带签名，但下载时仍要带 Cookie/UA/Referer。</summary>
    public async Task<string?> GetDownloadUrlAsync(string fid, CancellationToken ct)
    {
        var data = await PostAsync($"/1/clouddrive/file/download?{FixedQuery}",
            new JsonObject { ["fids"] = new JsonArray(fid) }, ct).ConfigureAwait(false);

        return data is JsonArray { Count: > 0 } arr ? arr[0]?["download_url"]?.GetValue<string>() : null;
    }

    // ==================== OSS 直传 ====================

    /// <summary>夸克网页用 aliyun-sdk-js 发请求，签名串里的 user-agent 必须跟真实请求头一致。</summary>
    public const string OssUserAgent = "aliyun-sdk-js/1.0.0 Chrome 144.0.0.0 on Windows 10 64-bit";

    /// <summary>OSS 要求的 GMT 日期格式：Tue, 10 Nov 2026 13:54:41 GMT</summary>
    public static string OssDate(DateTime utcNow) =>
        utcNow.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'",
            System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// 构造分片 PUT 的 auth_meta（OSS V1 string-to-sign 的原文）：<c>
    /// PUT
    /// (Content-MD5 空行)
    /// video/mp4
    /// Tue, 10 Nov 2026 13:54:41 GMT
    /// x-oss-date:Tue, 10 Nov 2026 13:54:41 GMT
    /// x-oss-user-agent:aliyun-sdk-js/1.0.0 ...
    /// /ul-sz/{obj_key}?partNumber=1&amp;uploadId={upload_id}</c>
    ///
    /// ⚠ 最后一行是 CanonicalizedResource，要**带 bucket 前缀**，但实际 PUT 的 URL 里不带。
    /// ⚠ date 必须和后面 PUT 时发的 <c>x-oss-date</c> 完全一致，否则签名对不上。
    /// </summary>
    public static string BuildPutAuthMeta(string contentType, string bucket, string objKey, string uploadId,
        int partNumber, string date)
    {
        return
            "PUT\n" +
            "\n" +
            contentType + "\n" +
            date + "\n" +
            "x-oss-date:" + date + "\n" +
            "x-oss-user-agent:" + OssUserAgent + "\n" +
            "/" + bucket + "/" + objKey.TrimStart('/') +
            "?partNumber=" + partNumber + "&uploadId=" + uploadId;
    }

    /// <summary>
    /// 构造「合并分片」请求的 auth_meta。比 PUT 多两行：Content-MD5 和 x-oss-callback。
    ///
    /// 为什么要合并：即使文件只有一个分片，夸克也要一次 POST 到
    /// <c>?uploadId=</c> 触发 OSS 的 CompleteMultipartUpload 并**回调夸克服务端**；
    /// 少了这一步，upload/finish 会回 "request cpp error[complete file failed!]"。
    /// </summary>
    /// <param name="contentMd5">CompleteMultipartUpload 那段 XML 的 MD5，base64 编码。</param>
    /// <param name="callbackBase64">pre 返回的 callback 对象转 JSON 后的 base64。</param>
    public static string BuildMergeAuthMeta(string contentMd5, string callbackBase64, string bucket, string objKey,
        string uploadId, string date)
    {
        return
            "POST\n" +
            contentMd5 + "\n" +
            "application/xml\n" +
            date + "\n" +
            // 自定义头必须按字典序排列：x-oss-callback < x-oss-date < x-oss-user-agent
            "x-oss-callback:" + callbackBase64 + "\n" +
            "x-oss-date:" + date + "\n" +
            "x-oss-user-agent:" + OssUserAgent + "\n" +
            "/" + bucket + "/" + objKey.TrimStart('/') +
            "?uploadId=" + uploadId;
    }

    /// <summary>拼 CompleteMultipartUpload 的 XML body。ETag 必须带双引号，否则 OSS 报 InvalidPart。</summary>
    public static string BuildCompleteXml(IEnumerable<(int PartNumber, string ETag)> parts)
    {
        var sb = new StringBuilder("<CompleteMultipartUpload>");
        foreach (var (number, etag) in parts)
        {
            var quoted = etag.StartsWith('"') ? etag : $"\"{etag}\"";
            sb.Append("<Part><PartNumber>").Append(number).Append("</PartNumber>")
              .Append("<ETag>").Append(quoted).Append("</ETag></Part>");
        }

        return sb.Append("</CompleteMultipartUpload>").ToString();
    }

    /// <summary>直传分片的目标地址。
    /// pre 里给的 upload_url（http://pds.quark.cn）是裸域，DNS 根本不存在；
    /// 真实形态是 <c>https://{bucket}.pds.quark.cn/{obj_key}</c>，
    /// 抓包证实它 CNAME 到了 cn-shenzhen 的 OSS bucket。
    /// </summary>
    public static string BuildPartUrl(string bucket, string objKey, string uploadId, int partNumber) =>
        $"https://{bucket}.pds.quark.cn/{objKey.TrimStart('/')}" +
        $"?partNumber={partNumber}&uploadId={Uri.EscapeDataString(uploadId)}";

    /// <summary>按扩展名猜 MIME。format_type 传错不会报错，但网盘里文件类型会显示成"未知"。</summary>
    public static string GuessMime(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".ts" => "video/mp2t",
        ".mkv" => "video/x-matroska",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".txt" => "text/plain",
        ".json" => "application/json",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    // ==================== Cookie 维护 ====================

    private void LoadCookies(string cookieHeader)
    {
        foreach (var part in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;

            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (name.Length > 0) _cookies[name] = value;
        }
    }

    /// <summary>
    /// 把响应的 Set-Cookie 合并回会话。
    /// __puus 是滚动令牌，不合并的话几小时后就失效 —— 这是夸克方案能长期跑的关键。
    /// </summary>
    private void AbsorbSetCookie(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var values)) return;

        var changed = false;

        foreach (var v in values)
        {
            var semi = v.IndexOf(';');
            var pair = semi >= 0 ? v[..semi] : v;
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;

            var name = pair[..eq].Trim();
            var value = pair[(eq + 1)..].Trim();
            if (name.Length == 0) continue;

            // 值为空表示服务端要求清除
            if (value.Length == 0) _cookies.TryRemove(name, out _);
            else _cookies[name] = value;

            changed = true;
        }

        if (changed) Interlocked.Increment(ref _cookieRefreshCount);
    }

    private static int ReadInt(JsonObject obj, string name)
    {
        var node = obj[name];
        if (node is null) return -1;

        try
        {
            return node.GetValueKind() switch
            {
                JsonValueKind.Number => node.GetValue<int>(),
                JsonValueKind.String => int.TryParse(node.GetValue<string>(), out var n) ? n : -1,
                _ => -1,
            };
        }
        catch
        {
            return -1;
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>夸克 API 调用失败。Code 是信封里的 code，HttpStatus 是传输层状态码。</summary>
public sealed class QuarkApiException : Exception
{
    public QuarkApiException(string message, int code, int httpStatus) : base(message)
    {
        Code = code;
        HttpStatus = httpStatus;
    }

    public int Code { get; }
    public int HttpStatus { get; }

    /// <summary>会话失效（需要重新从浏览器取 Cookie）。</summary>
    public bool IsSessionExpired => Code == 31001 || Message.Contains("登录", StringComparison.Ordinal);
}
