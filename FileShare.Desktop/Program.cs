using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Win32;
using FileShare.Core.Network.Discovery;
using FileShare.Core.Network.Tls;
using FileShare.Core.Services;
using FileShare.Desktop.Helpers;
using FileShare.Desktop.Services;
using Shyd.Prism.Notifications;
using CommunityToolkit.Mvvm.Messaging;
using Shyd.Prism.Dialogs;
using Shyd.Prism.Regions;
using FileShare.LocalSend;
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
using System.Threading;

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
            // 第零步：单实例检测——必须在任何初始化（本地库提取、日志、IoC、端口占用）之前。
            // 第二个实例在这里就弹原生提示并退出，绝不往下走，避免抢端口 / 重复托盘图标。
            // using 让守卫活到 Main 结束：正常 Shutdown、启动异常 thrown 两条路径都会释放 Mutex。
            // 崩溃 / 强杀则由 OS 在进程终止时回收句柄，不留下需要清理的状态文件。
            using var singleInstance = new SingleInstanceGuard();
            try
            {
                if (!singleInstance.TryAcquire())
                {
                    SingleInstanceNotice.Show();
                    return;
                }
            }
            catch (Exception ex)
            {
                // 检测本身出意外时放行启动（可用性优先），仅写 stderr。
                Console.Error.WriteLine($"[SingleInstance] 检测失败，放行启动: {ex.Message}");
            }

            // 第一步：先加载所有本地库
            ExtractAndLoadNativeLibs();

            var serviceProvider = BuildServiceProvider();
            var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
            // 第二步：再构建Avalonia应用（容器通过 App 构造函数注入，不经静态变量）
            try
            {
                BuildAvaloniaApp(serviceProvider)
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
        public static AppBuilder BuildAvaloniaApp(IServiceProvider? serviceProvider = null)
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
            var builder = AppBuilder.Configure(() => new App(serviceProvider))
                .UsePlatformDetect()
                .With(fontOptions);

            if (OperatingSystem.IsWindows())
            {
                // 默认首选 WinUIComposition；它的 ICompositor5.RequestCommitAsync 在合成器
                // 提交超时时会打一条 Error 并自行 force-trigger next tick（已自愈，不是应用异常）。
                // 本项目未使用任何亚克力 / 模糊 / 透明特效，把 DirectComposition 提到首位
                // 功能等价且更轻；后两项保留 Avalonia 原有的完整回退能力。
                builder = builder.With(new Win32PlatformOptions
                {
                    CompositionMode = new[]
                    {
                        Win32CompositionMode.DirectComposition,
                        Win32CompositionMode.WinUIComposition,
                        Win32CompositionMode.RedirectionSurface
                    }
                });
            }

            return builder;
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

                File.AppendAllText(logPath, $"Found {nativeResources.Count} native resources\n");

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
            // 1. 检测操作系统
            string os;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                os = "win";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                os = "linux";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                os = "osx";
            else
                os = "unknown";

            // 2. 检测当前进程架构（而非操作系统架构）
            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => "x86",
                Architecture.X64 => "x64",
                Architecture.Arm => "arm",
                Architecture.Arm64 => "arm64",
                _ => "unknown"
            };

            return $"{os}-{arch}";            
        }

        public static IServiceProvider BuildServiceProvider()
        {
            // 1. 准备路径
            var fileShareDir = AppPaths.GetDataDirectory();
            Directory.CreateDirectory(fileShareDir);
            var databasePath = AppPaths.GetDatabaseFilePath();
            var logDir = AppPaths.GetLogDirectory();

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
           
            services.Configure<DiscoveryOptions>(_ => { });

            // 用户设置（设备名 / 端口 / TLS / 保存目录 / 收藏 / 皮肤）
            var settingsPath = Path.Combine(fileShareDir, "settings.json");
            services.AddSingleton<IAppSettingsService>(sp =>
                new JsonAppSettingsService(settingsPath, sp.GetRequiredService<ILoggerFactory>()));
            // 桌面端 UI 偏好（皮肤）单独持久化，不放进 Core 的 AppSettings
            services.AddSingleton<IUiPreferencesService>(sp => new JsonUiPreferencesService(
                AppPaths.GetDataDirectory(),
                sp.GetRequiredService<ILoggerFactory>()));
            services.AddSingleton<IThemeService, ThemeService>();
            services.AddSingleton<IAboutContentService, AboutContentService>();

            // LocalSend 适配层通过 UI 桥反向注入通知 / 文案，适配层本身不依赖 Avalonia
            services.AddSingleton<ILocalSendUiBridge>(sp => new LocalSendUiBridge(
                sp.GetRequiredService<IToastService>(),
                sp.GetRequiredService<IAppSettingsService>(),
                sp.GetRequiredService<IDialogService>(),
                sp.GetRequiredService<IMessenger>(),
                sp.GetRequiredService<IClassicDesktopStyleApplicationLifetime>(),
                sp.GetRequiredService<SynchronizationContext>()));
            services.AddSingleton<IAppUiBridge>(sp => sp.GetRequiredService<ILocalSendUiBridge>());

            // 通知卡片（懒解析 lifetime：解析发生在 App 初始化之后）
            services.AddSingleton<ILocalSendInteropHost>(sp => new LocalSendInteropHost(
                sp.GetRequiredService<IAppSettingsService>(),
                sp.GetRequiredService<IPlatformDirectoryService>(),
                sp.GetRequiredService<IDatabaseService>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<ILocalSendUiBridge>()));

            services.AddSingleton<IWebShareHost>(sp => new WebShareHost(
                sp.GetRequiredService<IAppSettingsService>(),
                sp.GetRequiredService<IPlatformDirectoryService>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<IToastService>(),
                sp.GetRequiredService<IDatabaseService>(),
                sp.GetRequiredService<IAppUiBridge>()));

            // 通知卡片：用应用侧皮肤主题化的 AppToastWindow（DynamicResource 绑定 Fs* 资源，随皮肤换肤）；
            // 内容注入与点击行为由框架的 ToastWindowBase 负责，工厂只需提供窗口
            services.AddSingleton<IToastService>(_ => new ToastService(
                () => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow,
                () => new Views.AppToastWindow()));

            // 数据库
            services.AddSingleton<IDatabaseService>(sp =>
                new DatabaseService(databasePath, sp.GetRequiredService<ILoggerFactory>()));

            // 平台服务
            services.AddSingleton<IPlatformDirectoryService, DesktopDirectoryService>();

            // Avalonia 生命周期与 UI 线程上下文（App 初始化时才就绪，工厂在解析时才取）
            services.AddSingleton<IClassicDesktopStyleApplicationLifetime>(sp =>
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                ?? throw new InvalidOperationException("ApplicationLifetime 未初始化"));
            services.AddSingleton<SynchronizationContext>(sp =>
                SynchronizationContext.Current ?? new SynchronizationContext());

            // 平台对话框（选择器 / MsBox）
            services.AddSingleton<IPlatformDialogService>(sp => new PlatformDialogService(
                sp.GetRequiredService<IClassicDesktopStyleApplicationLifetime>()));

            // Avalonia Application（需要 UI 宿主的服务懒解析；组合根之外不再用 Application.Current）
            services.AddSingleton<Application>(_ =>
                Application.Current ?? throw new InvalidOperationException("Application 未初始化"));

            // 剪贴板 / 系统外壳（平台实现，ViewModel 只依赖接口）
            services.AddSingleton<IClipboardService, DesktopClipboardService>();
            services.AddSingleton<IShellService, DesktopShellService>();

            // 托盘（MainViewModel / MainWindow / SettingsViewModel 都注入使用）
            services.AddSingleton<ITrayService>(sp => new TrayService(                
                sp.GetRequiredService<Application>()));

            // 外部协议传输事件总线（解耦协议桥与主 ViewModel，避免循环依赖）
            // 用 CommunityToolkit 的 WeakReferenceMessenger：订阅者对消息总线是弱引用，
            // 视图模型忘记退订也不会被长期持有；框架层已移除自研 EventAggregator。
            services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

            // 主 ViewModel：完全由容器构造（原 App 手工 new + 静态 App.MainViewModel 已移除）
            services.AddSingleton<MainViewModel>();

            // 核心服务管理器
            services.AddSingleton<IFileShareServiceManager>(sp =>
            {
                var dirSvc = sp.GetRequiredService<IPlatformDirectoryService>();
                var dbSvc = sp.GetRequiredService<IDatabaseService>();                
                var fac = sp.GetRequiredService<ILoggerFactory>();
                var settings = sp.GetRequiredService<IAppSettingsService>().Current;

                // 发现方式、组播地址/端口等都来自用户设置（修改后需重启生效）
                var discOpt = DiscoveryOptions.FromSettings(settings);

                return new FileShareServiceManager(
                    dirSvc,
                    dbSvc,
                    settings.DeviceName,
                    settings.DeviceType,
                    transferPort: settings.TransferPort,
                    discoveryOptions: discOpt,
                    tlsEnabled: settings.TlsEnabled,
                    loggerFactory: fac,
                    conflictPolicy: settings.ConflictPolicy,
                    uploadLimitKBps: settings.UploadLimitKBps);
            });            

            // 注册视图（供区域导航 / ViewLocator 类机制从 DI 解析）
            services.AddTransient<MainWindow>();            

            // 区域导航（仿 Prism RegionManager）：显式泛型注册页面，无反射扫描，NativeAOT 安全
            // 主界面 VM 必须传工厂走单例：否则扩展方法会把 MainViewModel 重注册为 Transient，
            // App 启动（MainWindow.DataContext）与区域导航各解析出一个实例，服务被启动两遍、
            // 事件重复订阅，从历史/设置返回主界面还会再造新实例重启服务（日志双打的根因）
            services.AddPageForNavigation<MainView, MainViewModel>(
                NavigationPages.Main, sp => sp.GetRequiredService<MainViewModel>());
            services.AddPageForNavigation<HistoryView, HistoryViewModel>(NavigationPages.History);
            services.AddPageForNavigation<SettingsView, SettingsViewModel>(NavigationPages.Settings);

            // 弹窗（仿 Prism DialogService）：显式泛型注册，无反射扫描，NativeAOT 安全
            // SendText / ReceiveRequest / MessageReceived 是 UserControl 内容弹窗
            // （放入 DialogWindow 宿主，仿 ShowDialog；MessageReceived 的不抢焦点由 VM 配置宿主）；
            // About 保留 Window 形态以维持无边框浮动卡片的自定义 chrome（行为同 ShowDialogWindow）
            services.AddDialog<SendTextView, SendTextViewModel>(SendTextViewModel.DialogKeys.DialogName);
            services.AddDialog<ReceiveRequestView, ReceiveRequestViewModel>();
            services.AddDialog<MessageReceivedView, MessageReceivedViewModel>();
            services.AddDialog<AboutWindow, AboutViewModel>(activateExisting: true);

            // 弹窗服务：懒取主窗口作默认 owner（解析发生在 App 初始化之后，仿 ToastService）
            services.AddSingleton<IDialogService>(sp => new DialogService(
                sp.GetRequiredService<DialogRegistry>(),
                sp,
                () => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow));

            // 构建并返回容器
            return services.BuildServiceProvider();
        }
    }
}
