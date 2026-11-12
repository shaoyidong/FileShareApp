using Avalonia.Logging;
using Serilog;
using SeriLogLevel = Serilog.Events.LogEventLevel;
using System;
using System.Diagnostics;
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
            // Avalonia 的 layout pass 每次打 2 行 Information：扫描期间 UI 每 250ms 刷新一次，
            // 这些行全部在 UI 线程上过 Serilog 的锁，和后台探测日志串行竞争，压掉后 UI 线程零日志开销。
            // 绑定错误 / 渲染异常仍在 Warning / Error 级别，不受影响。
            .MinimumLevel.Override("Avalonia", SeriLogLevel.Warning)
            .MinimumLevel.Information()
            .WriteTo.File(
                path: System.IO.Path.Combine(logDirectory, "fileshare-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

        // Debug sink 走 OutputDebugString：接了调试器时每行毫秒级且与后台探测日志争同一把锁；
        // 没接调试器时它几乎免费但也没人看。只在调试器附加时启用。
        if (Debugger.IsAttached)
        {
            config = config.WriteTo.Debug(
                outputTemplate: "[{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        }

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
