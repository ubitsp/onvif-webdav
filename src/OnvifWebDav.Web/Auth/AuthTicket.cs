using System.Security.Cryptography;
using System.Text;

namespace OnvifWebDav.Web.Auth;

/// <summary>
/// 极简 Cookie 鉴权：值 = "过期Unix时间戳.HMAC签名"，密钥存在配置里。
/// 不引 ASP.NET DataProtection（它要落密钥文件到磁盘、还要配目录），一行 HMAC 就够局域网用了。
/// </summary>
public static class AuthTicket
{
    public const string CookieName = "owd_auth";

    private static byte[] Key(string base64Key)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64Key);
            return bytes.Length >= 16 ? bytes : SHA256.HashData(Encoding.UTF8.GetBytes(base64Key));
        }
        catch
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(base64Key));
        }
    }

    public static string Issue(string base64Key, DateTime expiresLocal)
    {
        var exp = new DateTimeOffset(expiresLocal).ToUnixTimeSeconds().ToString();
        return exp + "." + Sign(base64Key, exp);
    }

    public static bool Validate(string base64Key, string? cookieValue)
    {
        if (string.IsNullOrWhiteSpace(cookieValue)) return false;

        var parts = cookieValue.Split('.');
        if (parts.Length != 2) return false;

        if (!long.TryParse(parts[0], out var exp)) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.Now) return false;

        var expected = Sign(base64Key, parts[0]);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[1]));
    }

    private static string Sign(string base64Key, string payload)
    {
        var mac = HMACSHA256.HashData(Key(base64Key), Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(mac).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>常数时间比较密码，避免时序侧信道（局域网场景其实无所谓，顺手）。</summary>
    public static bool PasswordEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length) return false;
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
