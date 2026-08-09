using Avalonia.Logging;
using Serilog;
using SeriLogLevel = Serilog.Events.LogEventLevel;
using System;

namespace FileShare.Desktop.Logging;

/// <summary>
/// Serilog 日志配置：统一 Core 的 Microsoft.Extensions.Logging 与 Avalonia 内部日志到 Serilog。
/// </summary>
public static class SerilogSetup
{
    /// <summary>
    /// 创建 Serilog Logger（Debug 输出 + 按天滚动文件）
    /// </summary>
    /// <param name="logDirectory">日志文件目录</param>
    public static ILogger CreateLogger(string logDirectory)
    {
        System.IO.Directory.CreateDirectory(logDirectory);

        var config = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("App", "FileShare.Desktop")
#if DEBUG
            .MinimumLevel.Override("Avalonia", SeriLogLevel.Information)
#else
            .MinimumLevel.Information()
            .MinimumLevel.Override("Avalonia", SeriLogLevel.Warning)
#endif
            .WriteTo.Debug(outputTemplate: "[{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: System.IO.Path.Combine(logDirectory, "fileshare-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

        return config.CreateLogger();
    }

    /// <summary>
    /// 将 Avalonia 内部日志路由到 Serilog
    /// </summary>
    public static void RouteAvaloniaToSerilog(ILogger serilogLogger)
    {
        Avalonia.Logging.Logger.Sink = new AvaloniaSerilogSink(serilogLogger);
    }
}

/// <summary>
/// Avalonia 日志到 Serilog 的桥接 Sink。
/// 实现 Avalonia.Logging.ILogSink，将所有 Avalonia 日志事件转发到 Serilog。
/// </summary>
internal sealed class AvaloniaSerilogSink : ILogSink
{
    private readonly ILogger _logger;

    public AvaloniaSerilogSink(ILogger logger)
    {
        _logger = logger.ForContext("Source", "Avalonia");
    }

    public bool IsEnabled(LogEventLevel level, string area)
    {
        // 仅捕获 Information 及以上，避免 Avalonia Verbose/Debug 过于刷屏
        return level >= LogEventLevel.Information;
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        => Log(level, area, source, messageTemplate, Array.Empty<object?>());

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        var log = _logger.ForContext("Area", area);
        if (source != null)
        {
            log = log.ForContext("SourceObject", source.GetType().Name);
        }
        // Avalonia 的 messageTemplate 使用 {Prop} 结构化占位符，与 Serilog 兼容
        log.Write(MapLevel(level), messageTemplate, propertyValues);
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
