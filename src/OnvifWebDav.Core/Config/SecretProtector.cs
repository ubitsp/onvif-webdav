using System.Runtime.InteropServices;
using System.Text;

namespace OnvifWebDav.Config;

/// <summary>
/// 用 Windows DPAPI（CryptProtectData，当前用户作用域）保护密码。
/// 不走 System.Security.Cryptography.ProtectedData 包：Core 目标是 net9.0，
/// 该类型只在 Microsoft.WindowsDesktop.App 引用包里，Core 拿不到。P/Invoke 零依赖。
/// 注意：CurrentUser 作用域 → 换 Windows 用户后密文解不开，属预期行为（返回空串，不抛异常）。
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OnvifWebDav.v1");
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>加密为 Base64；空串原样返回空串，失败也返回空串（调用方按"没填密码"处理）。</summary>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";

        byte[] data = Encoding.UTF8.GetBytes(plain);
        GCHandle hData = GCHandle.Alloc(data, GCHandleType.Pinned);
        GCHandle hEntropy = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var outBlob = new DATA_BLOB();
        try
        {
            var inBlob = new DATA_BLOB { cbData = data.Length, pbData = hData.AddrOfPinnedObject() };
            var entBlob = new DATA_BLOB { cbData = Entropy.Length, pbData = hEntropy.AddrOfPinnedObject() };

            if (!CryptProtectData(ref inBlob, "OnvifWebDav", ref entBlob, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                return "";

            try { return Convert.ToBase64String(ReadBlob(outBlob)); }
            finally { LocalFree(outBlob.pbData); }
        }
        catch
        {
            return "";
        }
        finally
        {
            hData.Free();
            hEntropy.Free();
        }
    }

    /// <summary>解不开（例如换了 Windows 用户）返回空串，不抛异常。</summary>
    public static string Unprotect(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return "";

        byte[] data;
        try { data = Convert.FromBase64String(base64); }
        catch { return ""; }

        GCHandle hData = GCHandle.Alloc(data, GCHandleType.Pinned);
        GCHandle hEntropy = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var outBlob = new DATA_BLOB();
        try
        {
            var inBlob = new DATA_BLOB { cbData = data.Length, pbData = hData.AddrOfPinnedObject() };
            var entBlob = new DATA_BLOB { cbData = Entropy.Length, pbData = hEntropy.AddrOfPinnedObject() };

            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                return "";

            try { return Encoding.UTF8.GetString(ReadBlob(outBlob)); }
            finally { LocalFree(outBlob.pbData); }
        }
        catch
        {
            return "";
        }
        finally
        {
            hData.Free();
            hEntropy.Free();
        }
    }

    private static byte[] ReadBlob(DATA_BLOB blob)
    {
        if (blob.cbData <= 0 || blob.pbData == IntPtr.Zero) return Array.Empty<byte>();
        var result = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, result, 0, blob.cbData);
        return result;
    }
}
