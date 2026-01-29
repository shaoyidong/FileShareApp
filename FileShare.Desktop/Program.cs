using Avalonia;
using Avalonia.Media;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FileShare.Desktop
{
    internal sealed class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            // 第一步：先加载所有本地库
            ExtractAndLoadNativeLibs();

            // 第二步：再构建Avalonia应用
            try
            {
                BuildAvaloniaApp()
                    .StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                // 记录详细的错误信息
                var logPath = Path.Combine(Path.GetTempPath(), "AvaloniaStartupError.txt");
                File.WriteAllText(logPath, $"""
            Error: {ex.Message}
            StackTrace: {ex.StackTrace}
            InnerException: {ex.InnerException?.Message}
            InnerStackTrace: {ex.InnerException?.StackTrace}
            """);
                throw;
            }
        }

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

        private static void ExtractAndLoadNativeLibs()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "FlieShare_NativeLibs", GetCurrentRuntimeIdentifier());
            Directory.CreateDirectory(tempDir);

            var assembly = Assembly.GetExecutingAssembly();
            // 计算当前运行时预期的RID，作为资源查找的“后缀”
            var currentRid = GetCurrentRuntimeIdentifier();

            // 获取程序集中所有嵌入的资源名
            var allResourceNames = assembly.GetManifestResourceNames();

            // 筛选出位于我们约定的“runtimes/{rid}/native/”路径下的资源
            // 资源名格式通常是：<默认命名空间>.runtimes.<rid>.native.<文件名>
            foreach (var resourceName in allResourceNames)
            {
                if (resourceName.Contains($".runtimes.{currentRid}.native."))
                {
                    // 从资源名中提取出原始文件名
                    var fileName = resourceName.Substring(resourceName.LastIndexOf('.') + 1);
                    var targetPath = Path.Combine(tempDir, fileName);

                    if (!File.Exists(targetPath))
                    {
                        using var resourceStream = assembly.GetManifestResourceStream(resourceName);
                        using var fileStream = File.OpenWrite(targetPath);
                        resourceStream!.CopyTo(fileStream);
                    }
                    NativeLibrary.Load(targetPath);
                }
            }
        }

        /// <summary>
        /// 获取当前运行环境的 .NET 运行时标识符 (RID)。
        /// 这是匹配嵌入资源的关键。
        /// </summary>
        private static string GetCurrentRuntimeIdentifier()
        {
            string os = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "win",
                Architecture.X86 => "win",
                Architecture.Arm => "win", // Windows Arm32 非常罕见，通常与Arm64合并处理
                Architecture.Arm64 => "win",
                _ => "unknown"
            };

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                os = "linux";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                os = "osx";
            }

            string arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X86 => "x86",
                Architecture.X64 => "x64",
                Architecture.Arm => "arm",
                Architecture.Arm64 => "arm64",
                _ => "unknown"
            };

            // 组合成标准的RID，例如：win-x64, linux-arm, osx-arm64
            return $"{os}-{arch}";
        }
    }
}
