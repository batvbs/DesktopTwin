using System;
using System.Security.Cryptography;
using DesktopTwin.Models;

namespace DesktopTwin.Services;

/// <summary>
/// 生成并校验 MCP 使用的随机令牌与管道名。
/// </summary>
internal static class McpSecretStore
{
    private const int TokenBytes = 32;
    private const int PipeBytes = 16;

    /// <summary>
    /// 确保设置里有一组可用于认证的随机值。
    /// </summary>
    /// <returns>是否修改了设置。</returns>
    public static bool Ensure(McpSettings settings)
    {
        var changed = false;

        if (!IsSafeToken(settings.AccessToken))
        {
            settings.AccessToken = CreateBase64Url(TokenBytes);
            changed = true;
        }

        if (!IsSafePipeName(settings.PipeName))
        {
            settings.PipeName = "DesktopTwin.AgentControl." + CreateHex(PipeBytes);
            changed = true;
        }

        if (settings.Port is < 0 or > 65535)
        {
            settings.Port = McpSettings.DefaultPort;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 生成一段 URL 安全的随机字符串。
    /// </summary>
    public static string CreateBase64Url(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// 生成小写十六进制随机字符串。
    /// </summary>
    public static string CreateHex(int byteCount)
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();
    }

    /// <summary>
    /// 判断令牌长度和字符集是否适合直接放进 URL。
    /// </summary>
    public static bool IsSafeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 32 || token.Length > 128)
        {
            return false;
        }

        foreach (var ch in token)
        {
            if (!IsBase64UrlCharacter(ch))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 判断管道名是否只包含 Windows 命名管道允许且便于命令行传递的字符。
    /// </summary>
    public static bool IsSafePipeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 16 || name.Length > 200)
        {
            return false;
        }

        foreach (var ch in name)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBase64UrlCharacter(char ch)
    {
        return (ch >= 'a' && ch <= 'z')
            || (ch >= 'A' && ch <= 'Z')
            || (ch >= '0' && ch <= '9')
            || ch == '-'
            || ch == '_';
    }
}
