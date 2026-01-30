using Avalonia;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            try
            {
                string tempDir = string.Empty;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    tempDir = AppContext.BaseDirectory;
                }
                else
                {
                    tempDir = Path.Combine(Path.GetTempPath(), "FileShare.Desktop_NativeLibs");
                    Directory.CreateDirectory(tempDir);
                }

                // 记录日志以便调试
                var logPath = Path.Combine(tempDir, "load_log.txt");               

                File.WriteAllText(logPath, $"Starting extraction at {DateTime.Now}\n");                

                var assembly = Assembly.GetExecutingAssembly();
                var currentRid = GetCurrentRuntimeIdentifier();
                File.AppendAllText(logPath, $"Current RID: {currentRid}\n");

                // 获取所有嵌入式资源
                var allResourceNames = assembly.GetManifestResourceNames();
                File.AppendAllText(logPath, $"Total embedded resources: {allResourceNames.Length}\n");

                foreach (var name in allResourceNames)
                {
                    File.AppendAllText(logPath, $"Resource: {name}\n");
                }

                // 查找并加载当前平台的Native库
                var nativeResources = allResourceNames
                    .Where(name => name.EndsWith(".dll") || name.EndsWith(".so") || name.EndsWith(".dylib"))
                    .ToList();

                File.AppendAllText(logPath, $"Found {nativeResources.Count} native resources for {currentRid}\n");

                foreach (var resourceName in nativeResources)
                {
                    var fileName = resourceName;
                    var targetPath = Path.Combine(tempDir, fileName);
                    File.AppendAllText(logPath, $"Processing: {fileName}\n");

                    // 提取文件
                    if (!File.Exists(targetPath))
                    {
                        using var stream = assembly.GetManifestResourceStream(resourceName);
                        if (stream == null)
                        {
                            File.AppendAllText(logPath, $"ERROR: Stream is null for {resourceName}\n");
                            continue;
                        }

                        using var fileStream = File.Create(targetPath);
                        stream.CopyTo(fileStream);
                        File.AppendAllText(logPath, $"Extracted: {fileName} ({stream.Length} bytes)\n");
                    }

                    // 尝试加载DLL
                    if (fileName.EndsWith(".dll") || fileName.EndsWith(".so") || fileName.EndsWith(".dylib"))
                    {
                        try
                        {
                            NativeLibrary.Load(targetPath);
                            File.AppendAllText(logPath, $"Successfully loaded: {fileName}\n");
                        }
                        catch (Exception ex)
                        {
                            File.AppendAllText(logPath, $"ERROR loading {fileName}: {ex.Message}\n");
                        }
                    }
                }              
            }
            catch (Exception ex)
            {
                var errorLog = Path.Combine(Path.GetTempPath(), "NativeLibError.txt");
                File.WriteAllText(errorLog, $"ExtractAndLoadNativeLibs failed: {ex}\n{ex.StackTrace}");
                throw;
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
