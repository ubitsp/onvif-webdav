using System.Security.Cryptography;
using System.Text;

namespace OnvifWebDav.Onvif;

/// <summary>
/// WS-Security UsernameToken + PasswordDigest。
/// digest = Base64( SHA1( nonce || UTF8(created) || UTF8(password) ) )
/// 注意：摄像头网页里那个 "Digest 算法 MD5/SHA256" 是 HTTP 层 Digest，跟这里完全是两回事。
/// </summary>
public static class WsSecurity
{
    private const string WsseNs = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private const string WsuNs = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    private const string PasswordDigestType =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest";
    private const string Base64BinaryType =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary";

    /// <summary>生成 Security 头片段（非完整 Envelope）。</summary>
    /// <param name="createdUtc">必须用设备时钟校正后的时间，否则密码摘要在设备侧算不出来。</param>
    /// <param name="mustUnderstand">个别固件不认 mustUnderstand="1"，失败时由调用方降级重试。</param>
    public static string BuildUsernameToken(string username, string password, DateTime createdUtc,
        bool mustUnderstand = true)
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        var created = createdUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture);

        var createdBytes = Encoding.UTF8.GetBytes(created);
        var pwdBytes = Encoding.UTF8.GetBytes(password);

        var buffer = new byte[nonce.Length + createdBytes.Length + pwdBytes.Length];
        Buffer.BlockCopy(nonce, 0, buffer, 0, nonce.Length);
        Buffer.BlockCopy(createdBytes, 0, buffer, nonce.Length, createdBytes.Length);
        Buffer.BlockCopy(pwdBytes, 0, buffer, nonce.Length + createdBytes.Length, pwdBytes.Length);

        var digest = Convert.ToBase64String(SHA1.HashData(buffer));

        var mu = mustUnderstand ? " s:mustUnderstand=\"1\"" : "";
        return $"""
            <wsse:Security xmlns:wsse="{WsseNs}" xmlns:wsu="{WsuNs}"{mu}>
              <wsse:UsernameToken>
                <wsse:Username>{XmlUtils.Esc(username)}</wsse:Username>
                <wsse:Password Type="{PasswordDigestType}">{digest}</wsse:Password>
                <wsse:Nonce EncodingType="{Base64BinaryType}">{Convert.ToBase64String(nonce)}</wsse:Nonce>
                <wsu:Created>{created}</wsu:Created>
              </wsse:UsernameToken>
            </wsse:Security>
            """;
    }
}
