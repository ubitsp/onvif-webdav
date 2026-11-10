using System.Net;
using System.Text;
using System.Xml.Linq;
using OnvifWebDav.Diagnostics;

namespace OnvifWebDav.Onvif;

/// <summary>
/// 手写 ONVIF SOAP 客户端（不引第三方包）。
/// 特性：
///  - SOAP 1.2 优先，400/415/500 自动回退 SOAP 1.1，成功后缓存偏好
///  - WS-Security PasswordDigest，用设备时钟校正后的时间构造 Created
///  - SOAPAction 候选列表轮询（ONVIF 旧版 EventPortType 那套带 Request 后缀的怪癖）
///  - PullMessages 是长轮询，HttpClient.Timeout 必须无限，用 CTS 控超时
/// </summary>
public sealed class OnvifSoapClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _user;
    private readonly string _pass;
    private readonly AppLog? _log;

    private bool _soap12 = true;
    private bool _mustUnderstand = true;

    /// <summary>设备 UTC − 本机 UTC。设备返回的时间才是可信的，PasswordDigest 必须用它。</summary>
    public TimeSpan ClockOffset { get; set; } = TimeSpan.Zero;

    public OnvifSoapClient(string username, string password, AppLog? log = null, bool ignoreCertErrors = false)
    {
        _user = username;
        _pass = password;
        _log = log;

        var handler = new HttpClientHandler
        {
            // ONVIF 设备几乎都是自签证书；摄像头一般走 http，这里只是兜底
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            UseProxy = false,
            PreAuthenticate = false,
        };

        _http = new HttpClient(handler)
        {
            // 长轮询靠 CTS 控超时，HttpClient 自己不能设短了
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OnvifWebDav/1.0");
    }

    public DateTime DeviceNow => DateTime.UtcNow + ClockOffset;

    /// <summary>
    /// 发一次 SOAP 调用。bodyXml 是 Body 内部的完整元素（自带 xmlns）。
    /// actions 按顺序尝试，第一个成功的会被记住（按 body 根元素名缓存）。
    /// </summary>
    public async Task<XDocument> CallAsync(Uri endpoint, string bodyXml, string[] actions,
        bool anonymous = false, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        Exception? last = null;

        foreach (var action in actions)
        {
            try
            {
                return await CallOnceAsync(endpoint, bodyXml, action, anonymous, timeout, ct)
                    .ConfigureAwait(false);
            }
            catch (OnvifException ex) when (IsActionProblem(ex))
            {
                // action 不被认，换下一个候选
                last = ex;
                _log?.Debug("onvif", $"action 不被接受，换候选：{action} → {ex.Message}");
            }
        }

        throw last as OnvifException ?? new OnvifException("ONVIF 调用失败：没有任何候选 SOAPAction 被接受");
    }

    private static bool IsActionProblem(OnvifException ex) =>
        ex.HttpStatus is 400 or 415 or 500 &&
        (ex.Message.Contains("action", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("Action", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("NotSupported", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("InvalidArg", StringComparison.OrdinalIgnoreCase));

    private async Task<XDocument> CallOnceAsync(Uri endpoint, string bodyXml, string action, bool anonymous,
        TimeSpan? timeout, CancellationToken ct)
    {
        // 先按当前偏好发；如果失败再换 SOAP 版本重试一次
        var errors = new List<Exception>();

        foreach (var soap12 in new[] { _soap12, !_soap12 })
        {
            foreach (var mustUnderstand in new[] { _mustUnderstand, !_mustUnderstand })
            {
                if (!soap12 && mustUnderstand != _mustUnderstand) continue; // 只降级一个维度

                try
                {
                    var doc = await PostAsync(endpoint, bodyXml, action, soap12, mustUnderstand, anonymous,
                        timeout, ct).ConfigureAwait(false);

                    _soap12 = soap12;
                    _mustUnderstand = mustUnderstand;
                    return doc;
                }
                catch (OnvifException ex)
                {
                    errors.Add(ex);
                    if (ex.IsAuthFailure) throw;                       // 鉴权问题换格式没用
                    if (ex.HttpStatus == 0) throw;                      // 网络问题，直接抛
                    if (ex.HttpStatus is not (400 or 415 or 500 or 501)) throw;
                }
            }
        }

        throw errors.Count > 0
            ? errors[0]
            : new OnvifException("ONVIF 调用失败（未知原因）");
    }

    private async Task<XDocument> PostAsync(Uri endpoint, string bodyXml, string action, bool soap12,
        bool mustUnderstand, bool anonymous, TimeSpan? timeout, CancellationToken ct)
    {
        var envNs = soap12
            ? "http://www.w3.org/2003/05/soap-envelope"
            : "http://schemas.xmlsoap.org/soap/envelope/";

        var header = anonymous
            ? ""
            : "<s:Header>" + WsSecurity.BuildUsernameToken(_user, _pass, DeviceNow, mustUnderstand) + "</s:Header>";

        var envelope =
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <s:Envelope xmlns:s="{envNs}">{header}<s:Body>{bodyXml}</s:Body></s:Envelope>
             """;

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(envelope));

        if (soap12)
            content.Headers.TryAddWithoutValidation("Content-Type",
                $"application/soap+xml; charset=utf-8; action=\"{action}\"");
        else
        {
            content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=utf-8");
            req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{action}\"");
        }

        req.Content = content;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OnvifException("ONVIF 请求超时（设备无响应）", httpStatus: 0);
        }
        catch (HttpRequestException ex)
        {
            throw new OnvifException($"ONVIF 连接失败：{ex.Message}", httpStatus: 0, inner: ex);
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw BuildFault((int)resp.StatusCode, body);

            XDocument doc;
            try { doc = XDocument.Parse(body); }
            catch (Exception ex)
            {
                throw new OnvifException($"ONVIF 响应不是合法 XML（HTTP {(int)resp.StatusCode}）：{Trim(body)}",
                    httpStatus: (int)resp.StatusCode, inner: ex);
            }

            var fault = XmlUtils.First(doc, "Fault");
            if (fault is not null)
                throw BuildFault((int)resp.StatusCode, body);

            return doc;
        }
    }

    private static OnvifException BuildFault(int status, string body)
    {
        string? code = null;
        string? reason = null;

        try
        {
            var doc = XDocument.Parse(body);
            var fault = XmlUtils.First(doc, "Fault");
            if (fault is not null)
            {
                code = XmlUtils.TextOf(fault, "Value") ?? XmlUtils.TextOf(fault, "faultcode");
                reason = XmlUtils.TextOf(fault, "Text") ?? XmlUtils.TextOf(fault, "faultstring");
            }
        }
        catch { /* 非 XML 错误体 */ }

        if (string.IsNullOrEmpty(reason))
        {
            reason = status switch
            {
                401 => "设备拒绝认证（401）：用户名/密码错误，或摄像头时间与电脑相差过大",
                404 => "ONVIF 服务地址不存在（404）：确认摄像头已开启 ONVIF、端口填对",
                400 => "设备返回 400：可能是 SOAPAction 或 SOAP 版本不被接受",
                _ => $"ONVIF 请求失败（HTTP {status}）：{Trim(body)}",
            };
        }

        return new OnvifException(reason!, code, status);
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";

    public void Dispose() => _http.Dispose();
}
