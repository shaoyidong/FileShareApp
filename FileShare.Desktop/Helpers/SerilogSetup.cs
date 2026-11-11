using Avalonia.Logging;
using Serilog;
using SeriLogLevel = Serilog.Events.LogEventLevel;
using System;
using FileShare.Desktop.Helpers;

namespace FileShare.Desktop.Helpers;

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
