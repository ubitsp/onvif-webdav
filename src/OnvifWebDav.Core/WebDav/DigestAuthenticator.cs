using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OnvifWebDav.WebDav;

/// <summary>
/// RFC 7616 HTTP Digest 客户端（MD5 / SHA-256，qop=auth）。
/// Nextcloud 默认也接受 Basic，但自建 nginx-dav / Apache mod_dav 常常只认 Digest，所以两套都要有。
/// </summary>
public sealed class DigestAuthenticator
{
    private static readonly Regex ParamRegex = new(
        "(?<k>[A-Za-z0-9_-]+)\\s*=\\s*(?:\"(?<v>[^\"]*)\"|(?<v>[^,\\s]+))",
        RegexOptions.Compiled);

    private string _realm = "";
    private string _nonce = "";
    private string _opaque = "";
    private string _algorithm = "MD5";
    private string _qop = "auth";
    private int _nc;

    public bool IsReady => !string.IsNullOrEmpty(_nonce);

    public bool TryParseChallenge(string? wwwAuthenticateHeader)
    {
        if (string.IsNullOrEmpty(wwwAuthenticateHeader)) return false;

        var idx = wwwAuthenticateHeader.IndexOf("Digest", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        var challenge = wwwAuthenticateHeader[(idx + "Digest".Length)..];

        foreach (Match m in ParamRegex.Matches(challenge))
        {
            var k = m.Groups["k"].Value.ToLowerInvariant();
            var v = m.Groups["v"].Value;

            switch (k)
            {
                case "realm": _realm = v; break;
                case "nonce": _nonce = v; break;
                case "opaque": _opaque = v; break;
                case "algorithm": _algorithm = string.IsNullOrEmpty(v) ? "MD5" : v; break;
                case "qop":
                    var options = v.Split(',', StringSplitOptions.TrimEntries);
                    _qop = options.Contains("auth") ? "auth" : options[0];
                    break;
            }
        }

        _nc = 0;
        return !string.IsNullOrEmpty(_nonce);
    }

    private bool UseSha256 => _algorithm.Contains("SHA-256", StringComparison.OrdinalIgnoreCase)
                              || _algorithm.Contains("SHA256", StringComparison.OrdinalIgnoreCase);

    public string BuildAuthorizationHeader(string username, string password, string method, string requestUri)
    {
        var cnonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        _nc++;

        var nc = _nc.ToString("x8");

        string H(string s) => UseSha256
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant()
            : Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

        var ha1 = H($"{username}:{_realm}:{password}");
        var ha2 = H($"{method}:{requestUri}");

        var response = string.IsNullOrEmpty(_qop)
            ? H($"{ha1}:{_nonce}:{ha2}")
            : H($"{ha1}:{_nonce}:{nc}:{cnonce}:{_qop}:{ha2}");

        var sb = new StringBuilder();
        sb.Append($"Digest username=\"{username}\", realm=\"{_realm}\", nonce=\"{_nonce}\", uri=\"{requestUri}\", ");
        sb.Append($"algorithm={_algorithm}, response=\"{response}\"");

        if (!string.IsNullOrEmpty(_qop))
            sb.Append($", qop={_qop}, nc={nc}, cnonce=\"{cnonce}\"");

        if (!string.IsNullOrEmpty(_opaque))
            sb.Append($", opaque=\"{_opaque}\"");

        return sb.ToString();
    }
}
