using System;
using System.IO;

namespace DesktopTwin.Services;

/// <summary>
/// 「连接成功后自动启动 + 以管理员权限启动」的一次性授权。
///
/// 为什么需要它：自动启动 + 提权组合，等价于「只要编辑 settings.json 就能在本机管理员
/// 上下文里执行任意程序」——settings.json 是普通用户就能改的，这条路径等于白送提权。
/// 但用户同时勾选这两个开关本身就是明确意图，一味拒绝也不对。
///
/// 折中：用户在界面上确认一次，程序把「这次确认」与<strong>具体的目标程序路径与参数</strong>
/// 绑定后存下来（DPAPI 加密，只在本机当前账户下可解）。之后：
/// - 目标不变 → 自动以管理员权限启动（符合用户预期）；
/// - 目标被改 → 凭据对不上 → 回到「跳过并提示」，不会因为改了一行 json 就执行别的程序。
/// </summary>
internal static class ElevatedLaunchConsent
{
    /// <summary>
    /// 签发一份授权。
    /// </summary>
    /// <param name="targetPath">被授权的程序路径。</param>
    /// <param name="arguments">被授权的命令行参数。</param>
    /// <returns>DPAPI 加密后的 Base64；加密失败返回 null。</returns>
    public static string? Issue(string targetPath, string arguments)
    {
        return DpapiProtector.Protect(BuildPayload(targetPath, arguments));
    }

    /// <summary>
    /// 校验授权是否仍然对得上当前的目标程序。
    /// </summary>
    /// <param name="consent">已保存的授权（DPAPI 加密的 Base64）。</param>
    /// <param name="targetPath">当前目标程序路径。</param>
    /// <param name="arguments">当前参数。</param>
    public static bool IsValid(string? consent, string targetPath, string arguments)
    {
        if (string.IsNullOrWhiteSpace(consent))
        {
            return false;
        }

        var plain = DpapiProtector.Unprotect(consent);
        if (string.IsNullOrEmpty(plain))
        {
            return false;
        }

        return string.Equals(plain, BuildPayload(targetPath, arguments), StringComparison.Ordinal);
    }

    private static string BuildPayload(string targetPath, string arguments)
    {
        // 路径比较不区分大小写（Windows 语义），参数保持原样；
        // NUL 作为分隔符，避免「路径 + 参数」拼接后出现歧义。
        var normalizedPath = Path.GetFullPath(targetPath).ToLowerInvariant();
        return normalizedPath + "\0" + (arguments ?? string.Empty);
    }
}
