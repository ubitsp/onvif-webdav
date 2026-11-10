using System.Text;

namespace OnvifWebDav.WebDav;

public static class WebDavPath
{
    /// <summary>把若干段拼成 /a/b/c，去掉多余斜杠。</summary>
    public static string Join(params string?[] parts)
    {
        var list = new List<string>();
        foreach (var p in parts)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            foreach (var seg in p.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (seg == ".") continue;
                list.Add(seg);
            }
        }
        return "/" + string.Join('/', list);
    }

    /// <summary>逐段 URI 编码（保留斜杠）。中文目录名必须走这里。</summary>
    public static string Encode(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', segments.Select(Uri.EscapeDataString));
    }

    /// <summary>取父目录，例如 /a/b/c.json → /a/b。</summary>
    public static string Parent(string path)
    {
        var p = path.TrimEnd('/');
        var idx = p.LastIndexOf('/');
        return idx <= 0 ? "/" : p[..idx];
    }

    public static string FileName(string path)
    {
        var p = path.TrimEnd('/');
        var idx = p.LastIndexOf('/');
        return idx < 0 ? p : p[(idx + 1)..];
    }

    public static string DirOf(string relPath) => Parent(relPath);

    /// <summary>让文件名只含 ASCII 安全字符（中文只出现在网页显示层）。</summary>
    public static string SafeSegment(string raw, string fallback = "x")
    {
        var sb = new StringBuilder();
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) && ch < 128 || ch is '-' or '_' or '.')
                sb.Append(ch);
            else
                sb.Append('_');
        }
        var s = sb.ToString().Trim('_', '.');
        return string.IsNullOrEmpty(s) ? fallback : s;
    }
}
