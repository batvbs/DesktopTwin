using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DesktopTwin.Services;

/// <summary>
/// termsrv.dll 的完整性判定。
///
/// 为什么不用数字签名：实测本机（Win11）的 termsrv.dll 是**目录签名**（catalog），
/// PE 里没有内嵌签名（SecurityDir 为 0），WinVerifyTrust 只会返回 TRUST_E_NOSIGNATURE，
/// 拿它当「被改过」的证据会 100% 误报。
///
/// 改用 sfc 自己的校验：<c>sfc /verifyfile=</c> 拿系统组件库里的副本做比对，
/// 这才是「这个系统文件有没有被动过」的权威答案，而且和后面的修复动作一致。
///
/// 三个关键约束：
/// 1. sfc 非提权运行也返回退出码 1（提示必须以管理员运行），所以**必须先判提权**，
///    否则会把「没权限」当成「文件被改」；
/// 2. sfc 要 1~3 秒，不能放在面板每 2 秒一次的轮询里同步执行，改为后台跑 + 缓存；
/// 3. 判定不出来（没提权、sfc 缺失、超时）时返回「未验证」，**不报问题**——宁可漏报，不可误报。
/// </summary>
public static partial class ChildSessionEnvironmentCheck
{
    /// <summary>termsrv.dll 的状态。</summary>
    /// <param name="Exists">文件是否存在。</param>
    /// <param name="Intact">是否与系统组件库一致。仅在 <paramref name="Verified"/> 为 true 时有意义。</param>
    /// <param name="Verified">是否成功完成校验。</param>
    /// <param name="Backups">同目录下疑似备份文件名。</param>
    internal sealed record TermsrvFileState(
        bool Exists,
        bool Intact,
        bool Verified,
        IReadOnlyList<string> Backups);

    private static readonly object TermsrvGate = new();
    private static TermsrvFileState _termsrvState = new(false, false, false, Array.Empty<string>());
    private static DateTime _termsrvStateAt = DateTime.MinValue;
    private static bool _termsrvVerifyRunning;

    /// <summary>已验证结果缓存 5 分钟；未验证的 30 秒后重试。</summary>
    private static readonly TimeSpan TermsrvVerifiedTtl = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan TermsrvUnverifiedTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 取 termsrv.dll 的状态。第一次调用会返回「未验证」并在后台开始校验，
    /// 下一轮轮询（2 秒后）就能拿到结果。
    /// </summary>
    internal static TermsrvFileState GetTermsrvState()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var termsrvPath = Path.Combine(system32, "termsrv.dll");

        lock (TermsrvGate)
        {
            var ttl = _termsrvState.Verified ? TermsrvVerifiedTtl : TermsrvUnverifiedTtl;
            var stale = DateTime.UtcNow - _termsrvStateAt >= ttl;

            if (stale && !_termsrvVerifyRunning)
            {
                _termsrvVerifyRunning = true;
                var backups = CollectTermsrvBackups(system32);

                _ = Task.Run(() =>
                {
                    TermsrvFileState result;
                    try
                    {
                        result = VerifyTermsrv(termsrvPath, backups);
                    }
                    catch (Exception exception) when (exception is IOException
                                                          or UnauthorizedAccessException
                                                          or InvalidOperationException
                                                          or System.ComponentModel.Win32Exception)
                    {
                        AppLog.Warn("校验 termsrv.dll 失败。", exception);
                        result = new TermsrvFileState(File.Exists(termsrvPath), false, false, backups);
                    }

                    lock (TermsrvGate)
                    {
                        _termsrvState = result;
                        _termsrvStateAt = DateTime.UtcNow;
                        _termsrvVerifyRunning = false;
                    }
                });
            }

            if (_termsrvState.Exists || _termsrvState.Verified)
            {
                return _termsrvState;
            }

            // 还没跑过：先给出「文件在不在」这个廉价结论。
            var backupsNow = _termsrvState.Backups;
            return new TermsrvFileState(File.Exists(termsrvPath), false, false, backupsNow);
        }
    }

    /// <summary>
    /// 立即重新校验一次（修复完成后用），同步等待结果。
    /// </summary>
    internal static TermsrvFileState VerifyTermsrvNow()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var termsrvPath = Path.Combine(system32, "termsrv.dll");
        var backups = CollectTermsrvBackups(system32);

        var result = VerifyTermsrv(termsrvPath, backups);

        lock (TermsrvGate)
        {
            _termsrvState = result;
            _termsrvStateAt = DateTime.UtcNow;
        }

        return result;
    }

    private static TermsrvFileState VerifyTermsrv(string termsrvPath, IReadOnlyList<string> backups)
    {
        if (!File.Exists(termsrvPath))
        {
            return new TermsrvFileState(false, false, false, backups);
        }

        if (!IsElevated())
        {
            // sfc 非提权必然失败并返回 1，当成「被改过」会误报。
            return new TermsrvFileState(true, false, false, backups);
        }

        var exitCode = RunSfcVerifyFile(termsrvPath);
        return exitCode switch
        {
            0 => new TermsrvFileState(true, true, true, backups),
            1 => new TermsrvFileState(true, false, true, backups),
            _ => new TermsrvFileState(true, false, false, backups)
        };
    }

    private static IReadOnlyList<string> CollectTermsrvBackups(string system32)
    {
        var backups = new List<string>();
        foreach (var name in TermsrvBackupNames)
        {
            if (File.Exists(Path.Combine(system32, name)))
            {
                backups.Add(name);
            }
        }

        return backups;
    }

    /// <summary>
    /// 运行 <c>sfc /verifyfile=</c>，返回退出码；跑不起来返回 -1。
    /// </summary>
    private static int RunSfcVerifyFile(string path)
    {
        try
        {
            var sfc = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "sfc.exe");

            if (!File.Exists(sfc))
            {
                return -1;
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = sfc,
                Arguments = "/verifyfile=" + path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return -1;
            }

            // 输出是 UTF-16/本地化文本，不值得解析；只要它跑完，看退出码即可。
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException
                                                      or System.ComponentModel.Win32Exception)
                {
                    // 进程刚好退出。
                }

                return -1;
            }

            return process.ExitCode;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or IOException)
        {
            AppLog.Warn("运行 sfc /verifyfile 失败。", exception);
            return -1;
        }
    }

    /// <summary>
    /// 当前进程是否以管理员身份运行。
    /// </summary>
    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
