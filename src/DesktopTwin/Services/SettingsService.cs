using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using DesktopTwin.Models;

namespace DesktopTwin.Services;

/// <summary>
/// 设置读写服务。
///
/// 写盘采用「延迟合并 + 显式冲刷」策略：界面上的每次开关都会调用 <see cref="SaveSoon"/>，
/// 800 毫秒内的连续改动只写一次盘；需要立刻落盘的场景（例如在分身里启动新进程前）调用 <see cref="Flush"/>。
/// </summary>
public sealed class SettingsService : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(800);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _syncRoot = new();
    private readonly Timer _saveTimer;
    private bool _savePending;
    private bool _disposed;
    private bool _migrated;

    /// <summary>
    /// 创建设置服务并立即加载已有配置。
    /// </summary>
    /// <param name="settingsDirectory">
    /// 设置目录。默认 <c>%AppData%\DesktopTwin</c>；传入其他路径主要用于测试。
    /// </param>
    public SettingsService(string? settingsDirectory = null)
    {
        SettingsDirectory = settingsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopTwin");
        SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
        _saveTimer = new Timer(OnSaveTimerElapsed, state: null, Timeout.Infinite, Timeout.Infinite);
        Current = Load();

        // 迁移过就立刻写回，否则每次启动都要再迁一遍，而且用户看到的是被临时改过的值。
        if (_migrated)
        {
            SaveSoon();
        }
    }

    /// <summary>
    /// 当前生效的设置对象。界面直接改它的属性，然后调用 <see cref="SaveSoon"/>。
    /// </summary>
    public AppSettings Current { get; }

    /// <summary>
    /// 设置文件所在目录。
    /// </summary>
    public string SettingsDirectory { get; }

    /// <summary>
    /// 设置文件完整路径。
    /// </summary>
    public string SettingsPath { get; }

    /// <summary>
    /// 标记有改动，稍后合并写盘。
    /// </summary>
    public void SaveSoon()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _savePending = true;
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// 立即写盘，用于「改完马上就要被别的进程读到」的场景。
    /// </summary>
    public void Flush()
    {
        lock (_syncRoot)
        {
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _savePending = false;
            WriteToDisk();
        }
    }

    /// <summary>
    /// 停止计时器并做最后一次写盘。
    /// </summary>
    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            if (_savePending)
            {
                WriteToDisk();
                _savePending = false;
            }

            _saveTimer.Dispose();
        }
    }

    private void OnSaveTimerElapsed(object? state)
    {
        lock (_syncRoot)
        {
            if (_disposed || !_savePending)
            {
                return;
            }

            _savePending = false;
            WriteToDisk();
        }
    }

    /// <summary>
    /// 把 0.2.3 测试版留下的「单账户密码」搬进多账户密码表。
    ///
    /// 只做一次：搬完就把旧字段清空。这样从测试版升级过来的用户不用重新输一遍密码，
    /// 之后这份数据就只剩新结构。
    /// </summary>
    /// <returns>有改动时返回 true。</returns>
    private static bool MigrateLegacyPassword(AppSettings settings)
    {
        var automation = settings.Automation;
        if (automation is null || string.IsNullOrWhiteSpace(automation.LegacyAutoLoginPassword))
        {
            return false;
        }

        var account = string.IsNullOrWhiteSpace(automation.AutoLoginUserName)
            ? Environment.UserName
            : automation.AutoLoginUserName.Trim();

        // 已经有这个账户的记录了就以新的为准，不覆盖用户后来设置过的密码。
        var hasEntry = false;
        foreach (var key in automation.AutoLoginPasswords.Keys)
        {
            if (string.Equals(key, account, StringComparison.OrdinalIgnoreCase))
            {
                hasEntry = true;
                break;
            }
        }

        if (!hasEntry)
        {
            automation.AutoLoginPasswords[account] = automation.LegacyAutoLoginPassword!;
            AppLog.Info($"已把旧版保存的密码迁移到账户「{account}」名下。");
        }

        automation.LegacyAutoLoginPassword = null;
        return true;
    }

    /// <summary>
    /// 反序列化出来的密码表换成「不区分大小写」的字典。
    ///
    /// System.Text.Json 重建 Dictionary 时用的是默认比较器，属性上声明的
    /// OrdinalIgnoreCase 只对「新建的实例」有效——从文件读回来的那份会变成区分大小写，
    /// 于是「存的是 batvb、查的是 Batvb」就取不到密码。这里读出来就统一换掉。
    /// </summary>
    private static void NormalizePasswordTable(AppSettings settings)
    {
        var source = settings.Automation?.AutoLoginPasswords;
        if (source is null)
        {
            if (settings.Automation is not null)
            {
                settings.Automation.AutoLoginPasswords = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
            }

            return;
        }

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                normalized[pair.Key] = pair.Value;
            }
        }

        settings.Automation!.AutoLoginPasswords = normalized;
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings { SettingsVersion = AppSettings.CurrentVersion };
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions)
                ?? new AppSettings { SettingsVersion = AppSettings.CurrentVersion };

            NormalizePasswordTable(settings);
            _migrated = Migrate(settings) | MigrateLegacyPassword(settings);
            return settings;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or JsonException)
        {
            AppLog.Warn($"读取设置失败，已回退到默认设置：{SettingsPath}", exception);
            return new AppSettings { SettingsVersion = AppSettings.CurrentVersion };
        }
    }

    /// <summary>
    /// 把旧版本写下的设置迁移到当前默认值。
    ///
    /// 默认值一改，旧文件里存的仍是按旧默认值记下的值——用户升级后看不到任何变化。
    /// 所以凡是「改默认值」都要在这里补一条迁移，并把 <see cref="AppSettings.CurrentVersion"/> 加一。
    /// </summary>
    /// <returns>有改动时返回 true，调用方据此立刻写回。</returns>
    private static bool Migrate(AppSettings settings)
    {
        var changed = false;

        // 反序列化后可能为 null（JSON 里显式写了 "Session": null），先补齐再谈迁移。
        // 这一步不能放在版本判断之后：已是当前版本的文件照样可能带着 null。
        if (settings.Session is null)
        {
            settings.Session = new SessionSettings();
            changed = true;
        }

        if (settings.Automation is null)
        {
            settings.Automation = new AutomationSettings();
            changed = true;
        }

        if (settings.SettingsVersion >= AppSettings.CurrentVersion)
        {
            return changed;
        }

        // v1：画面显示比例默认值由「自适应缩放」改为 1:1。
        if (settings.SettingsVersion < 1)
        {
            settings.Session.SmartSizing = false;
        }

        settings.SettingsVersion = AppSettings.CurrentVersion;
        return true;
    }

    private void WriteToDisk()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            // 先写临时文件再替换，避免写到一半被中断后留下损坏的 settings.json。
            var temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Current, SerializerOptions));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"保存设置失败：{SettingsPath}", exception);
        }
    }
}
