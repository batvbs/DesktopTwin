using System;

namespace DesktopTwin.Interop;

/// <summary>
/// 跟踪当前 ActiveX 实例已成功应用的缩放设置，并合并异步重排。
/// 与期望值分开保存，保证 COM 写入失败或控件重建后仍可重试。
/// </summary>
internal sealed class RdpSmartSizingController(
    Action<bool> apply,
    Action<Action> schedule,
    Action refresh)
{
    private bool? _appliedValue;
    private bool _refreshPending;
    private int _generation;

    internal bool Apply(bool enabled)
    {
        if (_appliedValue == enabled)
        {
            return false;
        }

        // 写入失败时 COM 状态可能不确定，不能继续信任上一次的缓存。
        _appliedValue = null;
        apply(enabled);
        _appliedValue = enabled;
        return true;
    }

    internal void ScheduleRefresh()
    {
        if (_refreshPending)
        {
            return;
        }

        _refreshPending = true;
        var generation = _generation;
        try
        {
            schedule(() =>
            {
                if (generation != _generation)
                {
                    return;
                }

                _refreshPending = false;
                try
                {
                    refresh();
                }
                catch
                {
                    _appliedValue = null;
                    throw;
                }
            });
        }
        catch
        {
            _refreshPending = false;
            _appliedValue = null;
            throw;
        }
    }

    internal void Reset()
    {
        _generation++;
        _appliedValue = null;
        _refreshPending = false;
    }
}
