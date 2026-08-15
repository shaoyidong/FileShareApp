using Avalonia;
using Avalonia.Media;
using FileShare.Core.Network.Discovery;
using FileShare.Core.Network.Tls;
using FileShare.Core.Services;
using FileShare.Desktop.Helpers;
using FileShare.Desktop.ViewModels;
using FileShare.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
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

            var serviceProvider = BuildServiceProvider();
            App.ServiceProvider = serviceProvider;           
            var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
            // 第二步：再构建Avalonia应用
            try
            {
                BuildAvaloniaApp()
                    .StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Application terminated unexpectedly during startup");               
                throw;
            }
            finally
            {
                Log.CloseAndFlush();
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
                    // 确保系统SQLite软链接存在
                    SqliteLibraryLinker.EnsureSystemSqliteLink();
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

                // 定义在Linux上需要排除提取的库（使用纯文件名）
                var excludedOnLinux = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "libe_sqlite3.so"   // 请根据实际资源名调整，如果资源名包含路径，这里只需文件名
                };

                foreach (var resourceName in nativeResources)
                {
                    var fileName = resourceName;
                    var targetPath = Path.Combine(tempDir, fileName);

                    File.AppendAllText(logPath, $"Processing: {fileName}\n");

                    bool shouldSkipExtract = false;
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && excludedOnLinux.Contains(fileName))
                    {
                        shouldSkipExtract = true;
                        File.AppendAllText(logPath, $"Skipping extraction for {fileName} on Linux, expecting system-provided library.\n");
                    }

                    // 提取文件
                    if (!shouldSkipExtract && !File.Exists(targetPath))
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

        public static IServiceProvider BuildServiceProvider()
        {
            // 1. 准备路径
            var appDataDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var fileShareDir = Path.Combine(appDataDir, "FileShare");
            Directory.CreateDirectory(fileShareDir);
            var databasePath = Path.Combine(fileShareDir, "fileshare.db");
            var certDir = Path.Combine(fileShareDir, "tls");
            var fingerprintPath = Path.Combine(certDir, "fingerprints.txt");
            var logDir = Path.Combine(fileShareDir, "logs");

            // 2. 初始化 Serilog
            var serilogLogger = SerilogSetup.CreateLogger(logDir);
            SerilogSetup.RouteAvaloniaToSerilog(serilogLogger);
            Log.Logger = serilogLogger;

            // 3. 配置 DI 容器
            var services = new ServiceCollection();

            // 日志
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddSerilog(serilogLogger, dispose: true);
            });

            // 配置选项
            var options = new TlsOptions
            {
                Enabled = true,
                CertificateDirectory = certDir,
                FingerprintStorePath = fingerprintPath
            };
            services.AddSingleton(options);
            services.Configure<DiscoveryOptions>(_ => { });

            // 数据库
            services.AddSingleton<IDatabaseService>(sp =>
                new DatabaseService(databasePath));

            // 平台服务
            services.AddSingleton<IPlatformDirectoryService, DesktopDirectoryService>();

            // 核心服务管理器
            services.AddSingleton<FileShareServiceManager>(sp =>
            {
                var dirSvc = sp.GetRequiredService<IPlatformDirectoryService>();
                var dbSvc = sp.GetRequiredService<IDatabaseService>();
                var tlsOpt = sp.GetRequiredService<TlsOptions>();
                var discOpt = sp.GetRequiredService<IOptions<DiscoveryOptions>>().Value;
                var fac = sp.GetRequiredService<ILoggerFactory>();
                return new FileShareServiceManager(
                    dirSvc,
                    dbSvc,
                    Environment.MachineName,
                    Core.Models.DeviceType.Desktop,
                    tlsOptions: tlsOpt,
                    discoveryOptions: discOpt,
                    loggerFactory: fac);
            });

            // 注册视图（以便 ViewLocator 能从 DI 解析）
            services.AddTransient<MainWindow>();
            services.AddTransient<MainView>();
            services.AddTransient<HistoryView>(); 

            // 构建并返回容器
            return services.BuildServiceProvider();
        }
    }
}
