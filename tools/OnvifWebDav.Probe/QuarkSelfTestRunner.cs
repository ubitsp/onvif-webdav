using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.Quark;

namespace OnvifWebDav.Probe;

/// <summary>
/// 阶段 1 前置：验证夸克的「写」链路能不能跑通。
///
/// 只读接口通过（quark-probe）不代表能写 —— 上传要走
/// upload/pre → OSS 分片 PUT → upload/finish 三步，参数名和 OSS 域名都得实测。
///
/// 流程：建目录 → 传 15 字节 → 读回比对 → 彻底删除。
/// 任何一步失败就停在那步、打印原始响应，然后仍会尽力清理已建的目录。
/// </summary>
internal static class QuarkSelfTestRunner
{
    private const string TestPayload = "owd-selftest-15";

    public static async Task<int> RunAsync(Dictionary<string, string> o)
    {
        var port = int.TryParse(o.GetValueOrDefault("port", "9222"), out var p) && p > 0 ? p : 9222;
        var baseUrl = o.GetValueOrDefault("base");
        var doWrite = o.ContainsKey("write");

        var log = new AppLog(Path.Combine(Path.GetTempPath(), "owd-probe-logs"));
        using var cdp = new QuarkCdpClient(port, log);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));

        Console.WriteLine("== 1) 取会话 ==");

        var session = await cdp.TryGetSessionAsync(cts.Token).ConfigureAwait(false);
        if (session is null)
        {
            Console.WriteLine("✘ 拿不到 Cookie。先跑 quark-cdp 确认浏览器调试端口和登录状态。");
            return 1;
        }

        Console.WriteLine($"✔ Cookie {session.Cookie.Split(';').Length} 项");

        using var api = new QuarkApiClient(session, log, baseUrl);

        Console.WriteLine($"  base：{api.BaseUrl}");
        Console.WriteLine();

        // ---------------- 2) 只读复核 ----------------

        Console.WriteLine("== 2) 只读复核（member + file/sort）==");
        try
        {
            var member = await api.GetMemberAsync(cts.Token).ConfigureAwait(false);
            var used = member?["use_capacity"]?.GetValue<long>() ?? -1;
            var total = member?["total_capacity"]?.GetValue<long>() ?? -1;
            const double gb = 1024d * 1024 * 1024;
            Console.WriteLine($"  ✔ 容量 已用 {used / gb:F2} GB / 总 {total / gb:F2} GB");

            var sort = await api.ListAsync("0", 1, 5, cts.Token).ConfigureAwait(false);
            Console.WriteLine($"  ✔ 根目录返回 {sort?["list"]?.AsArray().Count ?? 0} 项");
        }
        catch (QuarkApiException ex)
        {
            Fail("只读复核失败", ex);
            return 1;
        }

        if (!doWrite)
        {
            Console.WriteLine();
            Console.WriteLine("（只读模式。要验证写链路，加 --write）");
            return 0;
        }

        // ---------------- 3) 写链路 ----------------

        var folderName = "owd-selftest-" + DateTime.Now.ToString("MMdd-HHmmss");
        string? dirFid = null;
        var failed = false;

        try
        {
            Console.WriteLine();
            Console.WriteLine($"== 3) 建目录：/{folderName} ==");

            dirFid = await api.CreateFolderAsync("0", folderName, cts.Token).ConfigureAwait(false);
            Console.WriteLine($"  ✔ fid={dirFid}");

            var payload = Encoding.UTF8.GetBytes(TestPayload);
            var fileName = "hello.txt";

            // ---- 4) 上传预处理 ----

            Console.WriteLine();
            Console.WriteLine($"== 4) upload/pre（{TestPayload.Length} 字节）==");

            var pre = await api.UploadPreAsync(dirFid, fileName, payload.Length, null, cts.Token)
                .ConfigureAwait(false);

            var uploadId = pre["upload_id"]?.GetValue<string>();
            var objKey = pre["obj_key"]?.GetValue<string>();
            var bucket = pre["bucket"]?.GetValue<string>();
            var authInfo = pre["auth_info"]?.GetValue<string>();
            var taskId = pre["task_id"]?.GetValue<string>();

            Console.WriteLine($"  task_id={taskId}");
            Console.WriteLine($"  bucket={bucket}");
            Console.WriteLine($"  upload_id={uploadId}");
            Console.WriteLine($"  obj_key={objKey}");

            if (uploadId is null || objKey is null || bucket is null || authInfo is null || taskId is null)
            {
                Fail("upload/pre 缺少必要字段", null);
                failed = true;
                return 1;
            }

            // ---- 5) 取签名 ----

            // 签名用的 date 必须和后面 PUT 发的 x-oss-date 完全一致
            var contentType = QuarkApiClient.GuessMime(fileName);
            var date = QuarkApiClient.OssDate(DateTime.UtcNow);
            var authMeta = QuarkApiClient.BuildPutAuthMeta(contentType, bucket, objKey, uploadId, 1, date);

            Console.WriteLine();
            Console.WriteLine("== 5) upload/auth ==");
            Console.WriteLine("  auth_meta（string-to-sign 原文）：");
            Console.WriteLine(IndentText(authMeta, 4));

            var auth = await api.UploadAuthAsync(taskId, authInfo, authMeta, cts.Token).ConfigureAwait(false);

            Console.WriteLine("  响应：" + auth.ToJsonString());

            var authKey = FindAuthKey(auth);
            if (authKey is null)
            {
                Console.WriteLine("  ✘ 响应里找不到签名串（期望字段 auth_key）");
                failed = true;
                return 1;
            }

            Console.WriteLine($"  ✔ 签名长度 {authKey.Length}");

            // ---- 6) 分片 PUT ----

            Console.WriteLine();
            Console.WriteLine("== 6) 分片 PUT ==");

            var partUrl = QuarkApiClient.BuildPartUrl(bucket, objKey, uploadId, 1);
            Console.WriteLine("  " + Shorten(partUrl, 150));

            var etag = await PutPartAsync(partUrl, authKey, date, contentType, payload, cts.Token)
                .ConfigureAwait(false);

            if (etag is null)
            {
                Fail("分片 PUT 失败", null);
                failed = true;
                return 1;
            }

            Console.WriteLine($"  ✔ ETag={etag}");

            // ---- 7) 合并分片（带回调）----

            Console.WriteLine();
            Console.WriteLine("== 7) 合并分片（CompleteMultipartUpload + 回调）==");

            try
            {
                await api.UpdateHashAsync(taskId,
                    Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant(),
                    Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant(),
                    cts.Token).ConfigureAwait(false);

                Console.WriteLine("  ✔ 已上报 md5/sha1");
            }
            catch (QuarkApiException ex)
            {
                Console.WriteLine("  ⚠ 上报哈希失败（不影响上传本身）：" + Shorten(ex.Message, 100));
            }

            var completeXml = QuarkApiClient.BuildCompleteXml(new[] { (1, etag) });
            var xmlBytes = Encoding.UTF8.GetBytes(completeXml);
            var contentMd5 = Convert.ToBase64String(MD5.HashData(xmlBytes));

            // 回调内容必须和 pre 返回的 callback 完全一致，夸克靠它把 OSS 事件关联回任务
            var callbackJson = (pre["callback"]?.DeepClone() ?? new JsonObject()).ToJsonString();
            var callbackB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(callbackJson));

            var mergeDate = QuarkApiClient.OssDate(DateTime.UtcNow);
            var mergeMeta = QuarkApiClient.BuildMergeAuthMeta(contentMd5, callbackB64, bucket, objKey, uploadId,
                mergeDate);

            var mergeAuth = await api.UploadAuthAsync(taskId, authInfo, mergeMeta, cts.Token).ConfigureAwait(false);
            var mergeKey = FindAuthKey(mergeAuth);

            if (mergeKey is null)
            {
                Console.WriteLine("  ✘ 合并签名里找不到 auth_key");
                failed = true;
                return 1;
            }

            var merged = await CompleteMultipartAsync(bucket, objKey, uploadId, mergeKey, mergeDate,
                contentMd5, callbackB64, xmlBytes, cts.Token).ConfigureAwait(false);

            if (!merged)
            {
                failed = true;
                return 1;
            }

            // ---- 8) finish ----

            Console.WriteLine();
            Console.WriteLine("== 8) upload/finish ==");

            var finish = await api.UploadFinishAsync(objKey, taskId, cts.Token).ConfigureAwait(false);

            Console.WriteLine("  原始响应：");
            Console.WriteLine(Indent(finish, 4));

            // finish 只回 obj_key/task_id 也行；真正要读回时按名字在目录里反查 fid
            var fileFid = finish?["fid"]?.GetValue<string>();
            Console.WriteLine($"  ✔ finish 完成" + (fileFid is null ? "" : $"，fid={fileFid}"));

            // ---- 9) 读回 ----

            Console.WriteLine();
            Console.WriteLine("== 9) 读回校验 ==");

            // finish 不一定回 fid，那就按文件名在目录里反查 ——
            // 这正是后续「路径 → fid 映射」要用的实际做法
            fileFid ??= await FindFidByNameAsync(api, dirFid, fileName, cts.Token).ConfigureAwait(false);

            if (fileFid is null)
            {
                Console.WriteLine("  ⚠ 目录里找不到刚上传的文件，跳过读回");
            }
            else
            {
                var dl = await api
                    .PostAsync("/1/clouddrive/file/download?pr=ucpro&fr=pc&uc_param_str=",
                        new JsonObject { ["fids"] = new JsonArray(fileFid) }, cts.Token)
                    .ConfigureAwait(false);

                var dlUrl = dl?.AsArray() is { Count: > 0 } arr ? arr[0]?["download_url"]?.GetValue<string>() : null;

                if (dlUrl is null)
                {
                    Console.WriteLine("  ⚠ 没拿到 download_url：");
                    Console.WriteLine(Indent(dl, 4));
                }
                else
                {
                    Console.WriteLine($"  下载地址：{Shorten(dlUrl, 110)}");
                    var back = await DownloadTextAsync(api, dlUrl, cts.Token).ConfigureAwait(false);

                    if (back == TestPayload)
                    {
                        Console.WriteLine($"  ✔ 内容一致：“{back}”");
                    }
                    else
                    {
                        Console.WriteLine($"  ✘ 内容不一致：期望“{TestPayload}”，实际“{back}”");
                        failed = true;
                    }
                }
            }
        }
        catch (QuarkApiException ex)
        {
            Fail("写链路中断", ex);
            failed = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✘ {ex.GetType().Name}：{ex.Message}");
            failed = true;
        }
        finally
        {
            // ---------------- 10) 清理 ----------------

            Console.WriteLine();
            Console.WriteLine("== 10) 清理 ==");

            if (dirFid is null)
            {
                Console.WriteLine("  （没有建过目录，无需清理）");
            }
            else
            {
                try
                {
                    // action_type=40 是彻底删除；=2 只是进回收站，会白占配额
                    await api.DeleteAsync(new[] { dirFid }, 40, cts.Token).ConfigureAwait(false);
                    Console.WriteLine($"  ✔ 已彻底删除目录 fid={dirFid}");
                }
                catch (QuarkApiException ex)
                {
                    Console.WriteLine($"  ⚠ 清理失败（请手工删除 /{folderName}）：{ex.Message}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("== 结论 ==");

        if (failed)
        {
            Console.WriteLine("✘ 写链路未完全通过，看上面停在哪一步。");
            return 1;
        }

        Console.WriteLine("✅ 写链路全部通过：建目录 → 上传 → 读回 → 删除");
        Console.WriteLine($"   会话续期：{api.CookieRefreshCount} 次 Set-Cookie");
        Console.WriteLine();
        Console.WriteLine("下一步：把 QuarkApiClient 接进 IRemoteStorage 抽象（阶段 1）。");
        return 0;
    }

    // ---------------- OSS 分片 PUT ----------------

    /// <summary>
    /// 把一个分片 PUT 到 bucket.pds.quark.cn。
    ///
    /// 三个头必须严格对齐 auth_meta 里的签名内容，否则 OSS 报 SignatureDoesNotMatch：
    ///  · Authorization   ← upload/auth 返回的签名
    ///  · x-oss-date      ← 和 auth_meta 第 4/5 行完全相同的字符串
    ///  · Content-Type    ← 和 auth_meta 第 3 行相同
    /// </summary>
    private static async Task<string?> PutPartAsync(string url, string authKey, string date, string contentType,
        byte[] payload, CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };

            using var req = new HttpRequestMessage(HttpMethod.Put, url);
            req.Headers.TryAddWithoutValidation("Authorization", authKey);
            req.Headers.TryAddWithoutValidation("x-oss-date", date);
            req.Headers.TryAddWithoutValidation("x-oss-user-agent", QuarkApiClient.OssUserAgent);

            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            req.Content = content;

            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"  ✘ HTTP {(int)resp.StatusCode}：{Shorten(body, 400)}");
                return null;
            }

            return resp.Headers.ETag?.Tag ?? "(无 ETag)";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✘ {ex.GetType().Name}：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// POST 到 <c>?uploadId=</c> 触发 OSS 合并分片，并带上回调头。
    ///
    /// 这一步是 finish 能成功的前提：夸克服务端靠 OSS 的回调才知道文件真的传完了。
    /// 不做合并直接 finish，会回 "request cpp error[complete file failed!]"。
    /// </summary>
    private static async Task<bool> CompleteMultipartAsync(string bucket, string objKey, string uploadId,
        string authKey, string date, string contentMd5, string callbackB64, byte[] xml, CancellationToken ct)
    {
        var url = $"https://{bucket}.pds.quark.cn/{objKey.TrimStart('/')}" +
                  $"?uploadId={Uri.EscapeDataString(uploadId)}";

        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("Authorization", authKey);
            req.Headers.TryAddWithoutValidation("x-oss-date", date);
            req.Headers.TryAddWithoutValidation("x-oss-user-agent", QuarkApiClient.OssUserAgent);
            req.Headers.TryAddWithoutValidation("x-oss-callback", callbackB64);

            var content = new ByteArrayContent(xml);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
            content.Headers.TryAddWithoutValidation("Content-MD5", contentMd5);
            req.Content = content;

            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"  ✘ HTTP {(int)resp.StatusCode}：{Shorten(body, 400)}");
                return false;
            }

            Console.WriteLine("  ✔ 合并成功，OSS 回调已触发");
            Console.WriteLine("    " + Shorten(body, 300));
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✘ {ex.GetType().Name}：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 按文件名在目录里反查 fid。
    /// finish 不保证返回 fid，而且后续「路径 → fid」映射也要靠这个 ——
    /// 夸克没有稳定的路径标识，只有 fid，且 fid 不随改名变化。
    /// </summary>
    private static async Task<string?> FindFidByNameAsync(QuarkApiClient api, string pdirFid, string name,
        CancellationToken ct)
    {
        var sort = await api.ListAsync(pdirFid, 1, 50, ct).ConfigureAwait(false);

        if (sort?["list"] is not JsonArray list) return null;

        foreach (var item in list)
        {
            var fileName = item?["file_name"]?.GetValue<string>();
            if (string.Equals(fileName, name, StringComparison.Ordinal))
                return item?["fid"]?.GetValue<string>();
        }

        return null;
    }

    /// <summary>从 auth 响应里挖出签名串。字段名没有文档，按常见叫法挨个试。</summary>
    private static string? FindAuthKey(JsonNode? auth)
    {
        if (auth is not JsonObject obj) return null;

        foreach (var name in new[] { "auth_key", "authorization", "Authorization", "signature", "token" })
        {
            if (obj[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                return s;
        }

        // 兜底：取最长的那条字符串（签名串通常最长）
        return obj
            .Select(kv => kv.Value is JsonValue v2 && v2.TryGetValue<string>(out var s2) ? s2 : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .OrderByDescending(s => s!.Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// 下载校验。
    /// download_url 自带签名参数，但 pds 的下载域还会校验 Cookie/UA/Referer，
    /// 裸 HttpClient（默认 UA）会被 403 挡掉 —— 必须把会话头一起带上。
    /// </summary>
    private static async Task<string?> DownloadTextAsync(QuarkApiClient api, string url, CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Cookie", api.CurrentCookie);
            req.Headers.TryAddWithoutValidation("User-Agent", api.UserAgent);
            req.Headers.TryAddWithoutValidation("Referer", "https://pan.quark.cn/");

            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"  ✘ 下载 HTTP {(int)resp.StatusCode}：{Shorten(text, 200)}");
                return null;
            }

            return text;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✘ 下载失败：{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    // ---------------- 输出辅助 ----------------

    /// <summary>把 JSON 节点缩进打印；敏感字段（auth_info/auth_key）截断。</summary>
    private static string Indent(JsonNode? node, int spaces)
    {
        if (node is null) return new string(' ', spaces) + "(null)";

        var text = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var lines = text.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            // auth_info 是签名串，整段打出来没意义还占屏
            if (line.Contains("\"auth_info\"", StringComparison.Ordinal) && line.Length > 120)
                line = line[..80] + "…（已截断）";

            lines[i] = new string(' ', spaces) + line.TrimEnd();
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>把多行文本逐行缩进。auth_meta 是 \n 分隔的 7 行签名原文，要看清楚。</summary>
    private static string IndentText(string text, int spaces)
    {
        var pad = new string(' ', spaces);
        return string.Join(Environment.NewLine, text.Split('\n').Select(l => pad + l));
    }

    private static void Fail(string what, QuarkApiException? ex)
    {
        Console.WriteLine($"  ✘ {what}" + (ex is null ? "" : $"：{ex.Message}"));

        if (ex?.IsSessionExpired == true)
            Console.WriteLine("    → 会话失效，重新登录 pan.quark.cn 后重跑 quark-cdp");
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
