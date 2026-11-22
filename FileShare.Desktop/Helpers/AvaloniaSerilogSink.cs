using Avalonia.Logging;
using Serilog;
using SeriLogLevel = Serilog.Events.LogEventLevel;
using System;

namespace FileShare.Desktop.Helpers;


/// <summary>
/// Avalonia 日志到 Serilog 的桥接 Sink。
/// 实现 Avalonia.Logging.ILogSink，将所有 Avalonia 日志事件转发到 Serilog。
/// </summary>
internal sealed class AvaloniaSerilogSink : ILogSink
{
    /// <summary>
    /// WinUIComposition 后端合成器提交超时后，Avalonia 会自行 force-trigger next tick 恢复，
    /// 属于已自愈的内部保护性日志，不是应用异常。精确降级这一条为 Warning，
    /// 避免日志里出现误导性的 [ERR]；其他 Error 一律原样保留。
    /// </summary>
    private const string CompositorCommitTimeoutMessage =
        "ICompositor5.RequestCommitAsync timed out, force-triggering next tick";

    /// <summary>
    /// Avalonia 11.3.7+ 原生 Popup 回归（上游 issue #19892）：点击 ComboBox 下拉 / 菜单项后，
    /// PopupRoot 在本手势的输入事件处理完之前被销毁，PlatformImpl 置空；随后到达的尾巴
    /// 原始输入事件被 Avalonia 丢弃并只打这一条 Warning，不抛异常、Click/Command 已执行。
    /// 仅精确过滤这一条 Warning；其他警告和错误一律原样保留。
    /// </summary>
    private const string NullPlatformImplInputMessage =
        "PlatformImpl is null, couldn't handle input.";

    /// <summary>
    /// Linux 上没有 StatusNotifierWatcher 时（WSL / 无桌面面板 / GNOME 未装 AppIndicator 扩展），
    /// Avalonia 建托盘图标会抛 DBus 异常并打一条 Error + 完整堆栈；而托盘本来就用不了。
    /// 应用侧已经做到「没有桌面宿主就不创建托盘图标」，但桌面标识存在、watcher 却缺席的环境
    /// （例如设了 XDG_CURRENT_DESKTOP 的精简桌面）仍会走到这里，所以再精确过滤这一条。
    /// 只匹配这一点，其他 D-Bus 错误照旧保留。
    /// </summary>
    private const string StatusNotifierUnavailableMessage = "org.kde.StatusNotifierWatcher";

    private readonly ILogger _logger;

    public AvaloniaSerilogSink(ILogger logger)
    {
        _logger = logger.ForContext("Source", "Avalonia");
        _logger = logger.ForContext("SourceContext", "Avalonia");
    }

    public bool IsEnabled(LogEventLevel level, string area)
    {
        // 仅捕获 Information 及以上，避免 Avalonia Verbose/Debug 过于刷屏
#if DEBUG
        return level >= LogEventLevel.Information;
#else
        return level >= LogEventLevel.Warning;
#endif

    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        => Log(level, area, source, messageTemplate, Array.Empty<object?>());

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        // 精确过滤：仅 Warning + 完全匹配这一条消息；Error / 其他 Warning 不受影响
        if (level == LogEventLevel.Warning &&
            string.Equals(messageTemplate, NullPlatformImplInputMessage, StringComparison.Ordinal))
        {
            return;
        }

        // 精确过滤：仅 Error + 消息里带了 StatusNotifierWatcher 名（托盘宿主不存在）
        if (level == LogEventLevel.Error &&
            messageTemplate.Contains(StatusNotifierUnavailableMessage, StringComparison.Ordinal))
        {
            return;
        }

        var log = _logger.ForContext("Area", area);
        if (source != null)
        {
            log = log.ForContext("SourceObject", source.GetType().Name);
        }
        // Avalonia 的 messageTemplate 使用 {Prop} 结构化占位符，与 Serilog 兼容
        var effectiveLevel = level == LogEventLevel.Error
            && messageTemplate.Contains(CompositorCommitTimeoutMessage, StringComparison.Ordinal)
                ? LogEventLevel.Warning
                : level;
        log.Write(MapLevel(effectiveLevel), messageTemplate, propertyValues);
    }

    public void Log<T0>(LogEventLevel level, string area, object? source, string messageTemplate, T0? propertyValue0)
        => Log(level, area, source, messageTemplate, new object?[] { propertyValue0 });

    public void Log<T0, T1>(LogEventLevel level, string area, object? source, string messageTemplate, T0? propertyValue0, T1? propertyValue1)
        => Log(level, area, source, messageTemplate, new object?[] { propertyValue0, propertyValue1 });

    public void Log<T0, T1, T2>(LogEventLevel level, string area, object? source, string messageTemplate, T0? propertyValue0, T1? propertyValue1, T2? propertyValue2)
        => Log(level, area, source, messageTemplate, new object?[] { propertyValue0, propertyValue1, propertyValue2 });

    private static SeriLogLevel MapLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => SeriLogLevel.Verbose,
        LogEventLevel.Debug => SeriLogLevel.Debug,
        LogEventLevel.Information => SeriLogLevel.Information,
        LogEventLevel.Warning => SeriLogLevel.Warning,
        LogEventLevel.Error => SeriLogLevel.Error,
        LogEventLevel.Fatal => SeriLogLevel.Fatal,
        _ => SeriLogLevel.Information,
    };
}
