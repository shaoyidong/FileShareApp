using Avalonia;
using Avalonia.Media;
using System;

namespace FileShare.Desktop
{
    internal sealed class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args) => BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
        {
            var fontOptions = new FontManagerOptions();

            // 根据操作系统设置不同的默认字体
            if (OperatingSystem.IsWindows())
            {
                fontOptions.DefaultFamilyName = "Microsoft YaHei"; // Windows 上的微软雅黑
                fontOptions.FontFallbacks = new[]
                {
            new FontFallback { FontFamily = "SimSun" },    // 宋体
            new FontFallback { FontFamily = "Arial" }
        };
            }
            else if (OperatingSystem.IsLinux())
            {
                fontOptions.DefaultFamilyName = "Noto Sans CJK SC"; // Linux 上的思源黑体
                fontOptions.FontFallbacks = new[]
                {
            new FontFallback { FontFamily = "WenQuanYi Micro Hei" }, // 文泉驿微米黑
            new FontFallback { FontFamily = "DejaVu Sans" }
        };
            }
            else
            {
                // 其他操作系统的默认配置
                fontOptions.DefaultFamilyName = "PingFang SC"; // macOS 上的苹方
            }
           return  AppBuilder.Configure<App>()
               .UsePlatformDetect()
               .WithInterFont()
               .LogToTrace()
       // 配置字体选项，解决Linux下中文显示问题
                .With(fontOptions);
        }
    }
}
