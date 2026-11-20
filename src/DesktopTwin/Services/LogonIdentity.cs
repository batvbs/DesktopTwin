using System;

namespace DesktopTwin.Services;

/// <summary>登录身份的统一解析规则，供密码验证、RDP 与凭据框共用。</summary>
internal readonly record struct LogonIdentity(string UserName, string? Domain)
{
    internal static LogonIdentity ParseConfigured(string configured, string defaultDomain, string? upnDomain)
    {
        var name = configured.Trim();
        var separator = name.IndexOf('\\');
        if (separator > 0 && separator < name.Length - 1)
        {
            return new(name[(separator + 1)..], name[..separator]);
        }

        // 裸账户名始终相对于当前 Windows 域；本地账户可显式写 .\用户 或 机器名\用户。
        return new(name, name.Contains('@') ? upnDomain : defaultDomain);
    }
}
