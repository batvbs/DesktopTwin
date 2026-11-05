using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopTwin.Services;

/// <summary>
/// 用 Windows 的 DPAPI 加密/解密字符串。
///
/// 选 DPAPI 而不是自带 AES 密钥：DPAPI 的密钥由 Windows 按「当前用户」派生并托管，
/// 我们不需要自己保存任何密钥。代价是密文只能被同一个 Windows 账户解开——
/// 换用户、换机器都解不出来，这正是保存「本机登录密码」想要的语义。
///
/// 没有引入 System.Security.Cryptography.ProtectedData 包，直接调用 crypt32：
/// 少一个依赖，而且 <c>CryptProtectData</c> 的行为和包里的封装完全一致。
/// </summary>
internal static class DpapiProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    /// <summary>
    /// 加密字符串，返回 Base64 密文。失败时返回 null。
    /// </summary>
    internal static string? Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return null;
        }

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var input = new DataBlob();
        var output = new DataBlob();
        var inputHandle = IntPtr.Zero;

        try
        {
            inputHandle = Marshal.AllocHGlobal(plainBytes.Length);
            Marshal.Copy(plainBytes, 0, inputHandle, plainBytes.Length);
            input.Data = inputHandle;
            input.Size = plainBytes.Length;

            if (!CryptProtectData(
                    ref input,
                    description: null,
                    optionalEntropy: IntPtr.Zero,
                    reserved: IntPtr.Zero,
                    promptStruct: IntPtr.Zero,
                    flags: CryptProtectUiForbidden,
                    out output))
            {
                AppLog.Warn($"DPAPI 加密失败，Win32 错误 {Marshal.GetLastPInvokeError()}。");
                return null;
            }

            var cipher = new byte[output.Size];
            Marshal.Copy(output.Data, cipher, 0, output.Size);
            return Convert.ToBase64String(cipher);
        }
        catch (Exception exception) when (exception is OutOfMemoryException
                                              or ArgumentException)
        {
            AppLog.Warn("DPAPI 加密异常。", exception);
            return null;
        }
        finally
        {
            if (inputHandle != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputHandle);
            }

            if (output.Data != IntPtr.Zero)
            {
                _ = LocalFree(output.Data);
            }
        }
    }

    /// <summary>
    /// 解密 Base64 密文。失败（密文损坏、换了用户或机器）时返回 null。
    /// </summary>
    internal static string? Unprotect(string? cipherBase64)
    {
        if (string.IsNullOrWhiteSpace(cipherBase64))
        {
            return null;
        }

        byte[] cipher;
        try
        {
            cipher = Convert.FromBase64String(cipherBase64);
        }
        catch (FormatException)
        {
            AppLog.Warn("保存的密码不是合法的 Base64，已忽略。");
            return null;
        }

        var input = new DataBlob();
        var output = new DataBlob();
        var inputHandle = IntPtr.Zero;

        try
        {
            inputHandle = Marshal.AllocHGlobal(cipher.Length);
            Marshal.Copy(cipher, 0, inputHandle, cipher.Length);
            input.Data = inputHandle;
            input.Size = cipher.Length;

            if (!CryptUnprotectData(
                    ref input,
                    out _,
                    optionalEntropy: IntPtr.Zero,
                    reserved: IntPtr.Zero,
                    promptStruct: IntPtr.Zero,
                    flags: CryptProtectUiForbidden,
                    out output))
            {
                AppLog.Warn(
                    $"DPAPI 解密失败，Win32 错误 {Marshal.GetLastPInvokeError()}。"
                    + "换过 Windows 账户或机器后需要重新保存密码。");
                return null;
            }

            var plain = new byte[output.Size];
            Marshal.Copy(output.Data, plain, 0, output.Size);
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            if (inputHandle != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputHandle);
            }

            if (output.Data != IntPtr.Zero)
            {
                _ = LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        internal int Size;
        internal IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
