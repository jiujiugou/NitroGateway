using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NitroGateway.Desktop.Services;

/// <summary>
/// ADR-037 S5：本机用户级数据保护（DPAPI CryptProtectData，CurrentUser 作用域）。
/// 用 P/Invoke 而非新增 NuGet 依赖；加密结果仅本机当前用户可解，
/// 配置文件拷到其他机器/用户需重新输入 Token。
/// </summary>
internal static class DpapiProtector
{
    /// <summary>禁止弹出 UI 提示（守护进程/服务场景友好）。</summary>
    private const int CryptProtectUiForbidden = 0x1;

    /// <summary>加密 UTF-8 明文并返回 Base64。</summary>
    public static string Protect(string plain)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var input = Alloc(plainBytes);
        try
        {
            var output = default(DataBlob);
            try
            {
                if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output))
                    throw new CryptographicException(Marshal.GetLastWin32Error());
                return Convert.ToBase64String(ToBytes(output));
            }
            finally
            {
                Free(output);
            }
        }
        finally
        {
            Free(input);
        }
    }

    /// <summary>解密 Base64 密文回 UTF-8 明文；密文损坏/跨用户时抛异常（由调用方兜底）。</summary>
    public static string Unprotect(string encryptedBase64)
    {
        var encrypted = Convert.FromBase64String(encryptedBase64);
        var input = Alloc(encrypted);
        try
        {
            var output = default(DataBlob);
            try
            {
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output))
                    throw new CryptographicException(Marshal.GetLastWin32Error());
                return Encoding.UTF8.GetString(ToBytes(output));
            }
            finally
            {
                Free(output);
            }
        }
        finally
        {
            Free(input);
        }
    }

    private static DataBlob Alloc(byte[] bytes)
    {
        var blob = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
        return blob;
    }

    private static byte[] ToBytes(DataBlob blob)
    {
        var bytes = new byte[blob.Size];
        Marshal.Copy(blob.Data, bytes, 0, blob.Size);
        return bytes;
    }

    private static void Free(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
            Marshal.FreeHGlobal(blob.Data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);
}
