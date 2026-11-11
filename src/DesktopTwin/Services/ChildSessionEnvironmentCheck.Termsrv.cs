using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Threading;
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

    /// <summary>
    /// sfc /verifyfile 同一时刻只跑一个。
    ///
    /// 修复完成后的同步复核很容易和后台校验撞上：两个进程读同一个文件只是白烧 CPU 和磁盘。
    /// </summary>
    private static readonly SemaphoreSlim SfcGate = new(1, 1);
    private static TermsrvFileState _termsrvState = new(false, false, false, Array.Empty<string>());
    private static DateTime _termsrvStateAt = DateTime.MinValue;

    /// <summary>
    /// 校验世代号。作废缓存时自增，后台校验完成时对不上就把结果丢掉——
    /// 否则「缓存失效前开始、失效后才结束」的那次校验会把旧结论写回来，
    /// 用户点「重新检测」等于白点，还要再等五分钟。
    /// </summary>
    private static int _termsrvGeneration;
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
                var generation = _termsrvGeneration;

                // 列表同步写回：校验要跑几秒，这期间修复侧来读 GetTermsrvState 时
                // 走的是「校验中」分支，返回的就是 _termsrvState.Backups——
                // 不写回的话它拿到的还是上一轮的旧列表。
                _termsrvState = _termsrvState with { Backups = backups };

                _ = Task.Run(() =>
                {
                    // 这一轮在排队期间可能已被作废（用户点了重新检测、或修复后复核过）。
                    // 让它继续跑没有意义：它只会占住 sfc 闸门，让真正需要结论的那轮
                    // 等满超时、最后拿到「未验证」。
                    lock (TermsrvGate)
                    {
                        if (generation != _termsrvGeneration)
                        {
                            AppLog.Info("本轮 termsrv.dll 校验在开始前已过期，跳过。");
                            return;
                        }
                    }

                    TermsrvFileState result;
                    try
                    {
                        result = VerifyTermsrv(termsrvPath, backups);
                    }
                    catch (Exception exception) when (exception is IOException
                                                          or UnauthorizedAccessException
                                                          or SecurityException
                                                          or InvalidOperationException
                                                          or System.ComponentModel.Win32Exception)
                    {
                        AppLog.Warn("校验 termsrv.dll 失败。", exception);
                        result = new TermsrvFileState(File.Exists(termsrvPath), false, false, backups);
                    }
                    catch (Exception exception)
                    {
                        // 没预料到的异常也要把「校验中」放下：否则 _termsrvVerifyRunning 会一直为 true，
                        // 这一项就永久停在「未验证」，再也不会出结论。
                        AppLog.Error("校验 termsrv.dll 出现未预期异常。", exception);
                        result = new TermsrvFileState(File.Exists(termsrvPath), false, false, backups);
                    }

                    lock (TermsrvGate)
                    {
                        if (generation != _termsrvGeneration)
                        {
                            // 这轮已经被作废（用户点了重新检测，或修复后复核过）：结果丢掉，
                            // 也不能动「正在校验」标志——那个标志归最新的那轮负责。
                            AppLog.Info("丢弃过期的 termsrv.dll 校验结果。");
                            return;
                        }

                        _termsrvState = result;
                        _termsrvStateAt = DateTime.UtcNow;
                        _termsrvVerifyRunning = false;
                    }
                });
            }

            if (_termsrvVerifyRunning)
            {
                // 校验正在进行（首次校验，或 5 分钟 TTL 到期后的重新校验）：手上这份结论
                // 已经不是当前系统的事实了，不能当成新结论交出去——否则面板会先按缓存渲染
                // 一条问题，等校验完成再把它撤掉。
                return new TermsrvFileState(File.Exists(termsrvPath), false, false, _termsrvState.Backups);
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
    /// 是否正在校验 termsrv.dll。界面据此避免把「暂时没结论」当成「已解决」。
    /// </summary>
    internal static bool IsTermsrvVerifying
    {
        get
        {
            lock (TermsrvGate)
            {
                return _termsrvVerifyRunning;
            }
        }
    }

    /// <summary>
    /// 清掉缓存，让下一次读取重新校验。
    ///
    /// 用户点「重新检测」时要调：结论默认缓存 5 分钟，用户在别处把文件修好了，
    /// 不清缓存就会一直显示旧警告和一个点了也修不动的「修复」按钮。
    /// </summary>
    internal static void InvalidateTermsrvCache()
    {
        lock (TermsrvGate)
        {
            _termsrvStateAt = DateTime.MinValue;

            // 作废正在跑的那轮：它的结论是失效之前的事实，不能写回来。
            // 同时放下标志，让下一次读取能立刻发起新的校验。
            _termsrvGeneration++;
            _termsrvVerifyRunning = false;
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

        // 这次是权威结论：先用新世代作废在途的后台校验，免得它跑完后把旧诊断写回缓存。
        // 同时记下世代，校验期间用户点「重新检测」时可以据此放弃写缓存。
        int generation;
        lock (TermsrvGate)
        {
            generation = ++_termsrvGeneration;
        }

        var result = VerifyTermsrv(termsrvPath, backups);

        lock (TermsrvGate)
        {
            if (generation != _termsrvGeneration)
            {
                // 期间被作废过：缓存里已经有更新的结论在跑或已写入，这次结果只回给调用方，
                // 不再覆盖缓存——否则旧结论会把用户刚触发的那次重检结果挤掉。
                return result;
            }

            _termsrvState = result;
            _termsrvStateAt = DateTime.UtcNow;
            _termsrvVerifyRunning = false;
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

    /// <summary>
    /// 现在可用的备份列表。
    ///
    /// 修复前直接取，而不是用 <see cref="GetTermsrvState"/> 里的 <c>Backups</c>：
    /// 用户可能在上一轮检测之后才放入备份，而缓存里的那份列表是旧的——
    /// 于是修复会跳过新备份直接跑 sfc，白绕一大圈。
    /// </summary>
    internal static IReadOnlyList<string> CollectBackupsNow()
    {
        return CollectTermsrvBackups(Environment.GetFolderPath(Environment.SpecialFolder.System));
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
        // 排队等前一个跑完，而不是同时起两个。等不到（前一个卡住）就照跑：
        // 校验本身不该因为排队而失败，多一个进程也比拿不到结论强。
        if (!SfcGate.Wait(TimeSpan.FromMinutes(3.5)))
        {
            // 上一轮校验还没结束：另起一个只会让两个 sfc 抢同一个文件。
            // 上限比 sfc 自己的 3 分钟强杀更长，所以正常情况下等不到这里；
            // 真等到了说明前一个进程卡死，返回 -1 让上层按「未验证」处理，不拿旧结论凑数。
            AppLog.Warn("等待上一次 sfc 校验超时，本次跳过校验。");
            return -1;
        }

        try
        {
            return RunSfcVerifyFileCore(path);
        }
        finally
        {
            SfcGate.Release();
        }
    }

    private static int RunSfcVerifyFileCore(string path)
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
