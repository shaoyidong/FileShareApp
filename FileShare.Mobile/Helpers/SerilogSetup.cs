using Microsoft.Extensions.Logging;
using Serilog;
using SeriLogLevel = Serilog.Events.LogEventLevel;
using System.IO;

namespace FileShare.Mobile.Helpers;

/// <summary>
/// Serilog 日志配置：统一 Microsoft.Extensions.Logging 到 Serilog。
/// 对齐 Desktop 项目的 SerilogSetup，提供 Debug 输出 + 按天滚动文件。
/// </summary>
public static class SerilogSetup
{
    /// <summary>
    /// 创建 Serilog Logger（Debug 输出 + 按天滚动文件）
    /// </summary>
    /// <param name="logDirectory">日志文件目录</param>
    public static Serilog.ILogger CreateLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);

        var config = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("App", "FileShare.Mobile")
#if DEBUG
            .MinimumLevel.Override("Microsoft", SeriLogLevel.Information)
#else
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", SeriLogLevel.Warning)
#endif
            .WriteTo.Debug(outputTemplate: "[{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: Path.Combine(logDirectory, "fileshare-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

        return config.CreateLogger();
    }   
}
