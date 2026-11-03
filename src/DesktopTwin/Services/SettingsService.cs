using System;
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

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            return settings ?? new AppSettings();
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or JsonException)
        {
            AppLog.Warn($"读取设置失败，已回退到默认设置：{SettingsPath}", exception);
            return new AppSettings();
        }
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
