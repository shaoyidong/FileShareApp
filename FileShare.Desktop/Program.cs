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
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
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
                // 合成模式不覆盖，沿用 Avalonia 的默认优先级：
                //   WinUIComposition → DirectComposition → RedirectionSurface。
                // 显式写数组的唯一作用是调整优先级；本项目没有必须把某个模式提前的理由，
                // 交给运行时按系统能力自动挑更省心（也避免以后新增模式时漏配）。
                //
                // 各模式的能力边界（决定实际命中哪一个）：
                //   WinUIComposition   Win10 1803+，唯一支持亚克力 / Mica 的模式；
                //   DirectComposition  Win8+，纯 COM 调用，不依赖 WinRT；
                //   RedirectionSurface 兜底（Win7、无 GPU、ANGLE 初始化失败后的软件渲染）。
                //
                // 已知噪音：WinUIComposition 的 ICompositor5.RequestCommitAsync 在合成器提交超时时
                // 会打一条 Error，随后自行 force-trigger 下一个 tick —— 已自愈，不是应用异常。
                // 之前把 DirectComposition 提到首位就是为了绕开它；改回默认顺序后 Win10 1803+
                // 会走 WinUIComposition，这条日志可能在极端情况下重新出现（实测 5 次冷启动未复现）。
                builder = builder.With(new Win32PlatformOptions());
            }

            return builder;
        }

        private static void ExtractAndLoadNativeLibs()
        {
            var dataDirectory = AppPaths.GetDataDirectory();

            // 原生库加载的日志统一记在这一个文件里（含异常），不再单独生成 NativeLibError.txt
            var logPath = Path.Combine(dataDirectory, "load_log.txt");

            try
            {
                // 原生库是应用每次启动都要用的运行时依赖，不是临时文件：
                // 统一放在应用自己的数据目录下（Windows 为 %APPDATA%\<应用名>，
                // Linux 为 ~/.config/<应用名>），既不会被系统清理临时目录时删掉，
                // 也不会把一堆 .so 散落在程序安装目录里。
                var nativeDir = Path.Combine(dataDirectory, "native");
                Directory.CreateDirectory(nativeDir);

                var assembly = Assembly.GetExecutingAssembly();

                // 获取所有嵌入式资源
                var allResourceNames = assembly.GetManifestResourceNames();

                // 查找当前平台的Native库
                var nativeResources = allResourceNames
                    .Where(name => name.EndsWith(".dll") || name.EndsWith(".so") || name.EndsWith(".dylib"))
                    .ToList();

                // 定义在Linux上需要排除提取的库（使用纯文件名）
                var excludedOnLinux = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "libe_sqlite3.so"   // 走系统 libsqlite3，解压自带版本反而会和系统库冲突
                };

                var allLoaded = TryExtractAndLoadInto(
                    nativeDir, logPath, assembly, allResourceNames, nativeResources, excludedOnLinux);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // native/ 不在 .NET 默认探测路径里，P/Invoke 按名找库会落空，
                    // 挂一个兜底解析器把它们指过去。
                    RegisterUnixNativeLibraryFallback(nativeDir);
                }

                if (!allLoaded)
                {
                    Console.Error.WriteLine($"[Native] 部分原生库加载失败，详见 {logPath}");
                }
            }
            catch (Exception ex)
            {
                AppendNativeErrorToLoadLog(logPath, ex);
                throw;
            }
        }

        /// <summary>
        /// 把原生库加载异常追加到 <c>load_log.txt</c> 末尾——不单独生成 NativeLibError.txt，
        /// 排查时只需要看这一个文件。
        /// </summary>
        /// <remarks>
        /// 这里发生在 Serilog 初始化之前，所以不能用应用日志；数据目录都写不了时退到 stderr，
        /// 至少让控制台/终端能看到原因。
        /// </remarks>
        private static void AppendNativeErrorToLoadLog(string logPath, Exception ex)
        {
            var content = $"\n===== FAILED at {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\n" +
                          $"{ex}\n{ex.StackTrace}\n";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, content);
            }
            catch
            {
                try
                {
                    Console.Error.WriteLine(content);
                }
                catch
                {
                    // 连 stderr 都写不了就没别的办法了
                }
            }
        }

        /// <summary>
        /// 把嵌入式原生库解压到 <paramref name="nativeDir"/> 并逐个按全路径加载，
        /// 过程写进 <paramref name="logPath"/>。
        /// </summary>
        /// <returns>全部加载成功返回 true；有库加载失败返回 false（应用仍会继续启动，交由上层回退软件渲染）。</returns>
        private static bool TryExtractAndLoadInto(
            string nativeDir,
            string logPath,
            Assembly assembly,
            string[] allResourceNames,
            List<string> nativeResources,
            HashSet<string> excludedOnLinux)
        {
            File.WriteAllText(logPath, $"Starting extraction at {DateTime.Now}\n");
            File.AppendAllText(logPath, $"Native dir: {nativeDir}\n");
            File.AppendAllText(logPath, $"Total embedded resources: {allResourceNames.Length}\n");

            foreach (var name in allResourceNames)
            {
                File.AppendAllText(logPath, $"Resource: {name}\n");
            }

            File.AppendAllText(logPath, $"Found {nativeResources.Count} native resources\n");

            // Linux 上 SQLite 用系统库：先建好软链，后面跳过解压时才有可加载的目标
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                SqliteLibraryLinker.EnsureSystemSqliteLink(nativeDir);
            }

            var extractionTimer = Stopwatch.StartNew();
            int written = 0, skipped = 0;
            var allLoaded = true;

            foreach (var resourceName in nativeResources)
            {
                var fileName = resourceName;
                var targetPath = Path.Combine(nativeDir, fileName);

                File.AppendAllText(logPath, $"Processing: {fileName}\n");

                bool shouldSkipExtract = false;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && excludedOnLinux.Contains(fileName))
                {
                    shouldSkipExtract = true;
                    File.AppendAllText(logPath, $"Skipping extraction for {fileName} on Linux, expecting system-provided library.\n");
                }

                // 提取文件：先比对，只有内容不一致才写。
                // - 不能像早先那样「目标存在就跳过」：升级后目录里会留着旧版本，
                //   明明换了原生库却仍按旧行为运行（曾因此把 ANGLE 的问题排查带偏）。
                // - 也不必每次盲目覆盖：长度先比（O(1)），长度相同再按 64KB 分块比内容。
                //   读比写便宜（写要建文件、落脏页、还可能被杀软扫一遍），内容一致时连一次写都不做。
                if (!shouldSkipExtract)
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream == null)
                    {
                        File.AppendAllText(logPath, $"ERROR: Stream is null for {resourceName}\n");
                        allLoaded = false;
                        continue;
                    }

                    if (HasSameContent(stream, targetPath))
                    {
                        skipped++;
                        File.AppendAllText(logPath, $"Up-to-date, skipped: {fileName}\n");
                    }
                    else
                    {
                        try
                        {
                            stream.Position = 0;
                            using (var fileStream = File.Create(targetPath))
                            {
                                stream.CopyTo(fileStream);
                            }

                            written++;
                            File.AppendAllText(logPath, $"Extracted: {fileName} ({stream.Length} bytes)\n");
                        }
                        catch (IOException ex)
                        {
                            // 常见原因：文件被另一个进程（或上一次没退干净的实例）占用。
                            // 这时不展开终止启动，继续尝试用磁盘上已有的那份。
                            File.AppendAllText(logPath, $"WARN cannot overwrite {fileName}: {ex.Message}\n");
                        }
                    }
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
                        allLoaded = false;
                        File.AppendAllText(logPath, $"ERROR loading {fileName}: {ex.Message}\n");
                    }
                }
            }

            extractionTimer.Stop();
            File.AppendAllText(logPath,
                $"Extraction finished in {extractionTimer.ElapsedMilliseconds} ms " +
                $"(written={written}, skipped={skipped})\n");
            File.AppendAllText(logPath,
                allLoaded ? "All native libraries loaded\n" : "Some native libraries FAILED to load\n");

            return allLoaded;
        }

        /// <summary>
        /// 判断磁盘上的文件与嵌入资源内容是否完全一致。
        /// <para>先比长度（O(1)，最常见的「换了新版本」和「文件不完整」都会在这一步判定），
        /// 长度相同才按 64KB 分块比内容，一旦发现差异立刻返回。</para>
        /// <para>为什么不直接覆盖：读比写便宜得多，写要建文件、落脏页、还可能被杀软扫一遍；
        /// 绝大多数启动场景内容都没变，省掉写入能少一次全量 I/O，也避免无谓地刷新文件时间戳。</para>
        /// </summary>
        private static bool HasSameContent(Stream source, string targetPath)
        {
            try
            {
                var info = new FileInfo(targetPath);
                if (!info.Exists || info.Length != source.Length)
                    return false;

                // 长度已经对上，逐块比对。顺序读 + 64KB 缓冲，命中页缓存时开销很小。
                const int BufferSize = 64 * 1024;
                using var file = new FileStream(
                    targetPath, FileMode.Open, FileAccess.Read, System.IO.FileShare.Read,
                    BufferSize, FileOptions.SequentialScan);

                var sourceBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                var fileBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    while (true)
                    {
                        var read = source.Read(sourceBuffer, 0, BufferSize);
                        if (read == 0)
                            return true;   // 源读完了，长度又一致 → 内容一致

                        var fileRead = 0;
                        while (fileRead < read)
                        {
                            var n = file.Read(fileBuffer, fileRead, read - fileRead);
                            if (n == 0)
                                return false;

                            fileRead += n;
                        }

                        if (!sourceBuffer.AsSpan(0, read).SequenceEqual(fileBuffer.AsSpan(0, read)))
                            return false;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(sourceBuffer);
                    ArrayPool<byte>.Shared.Return(fileBuffer);
                }
            }
            catch (IOException)
            {
                // 读不了（被占用 / 权限）就当作不一致，交给后续写入逻辑处理并记录 WARN。
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Linux 上给原生库解析兜底。
        /// <para>库被放在 <c>&lt;应用目录&gt;/native</c> 而不是应用根目录，.NET 的默认探测路径
        /// （应用目录 + deps.json 里登记的位置）不会包含它，托管代码里的
        /// <c>[DllImport("libSkiaSharp")]</c> 这类按名加载会落空。这里挂
        /// <see cref="AssemblyLoadContext.ResolvingUnmanagedDll"/>，在默认解析失败后按
        /// 「原名 / 加 .so / 加 lib 前缀」三种形式到该目录里找，找到就按全路径加载。</para>
        /// <para>只在 Linux 注册；Windows 上原生库仍然放在应用目录，走系统默认解析。</para>
        /// </summary>
        private static void RegisterUnixNativeLibraryFallback(string nativeDir)
        {
            // 同一个库名只会解析一次，把句柄缓存下来：
            // 运行时对每个 P/Invoke 桩都会问一次，不缓存会重复做文件探测（SQLite 有上百个入口）。
            var resolved = new Dictionary<string, IntPtr>(StringComparer.Ordinal);

            AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, libraryName) =>
            {
                lock (resolved)
                {
                    if (resolved.TryGetValue(libraryName, out var cached))
                        return cached;
                }

                foreach (var candidate in EnumerateNativeNameCandidates(libraryName))
                {
                    var path = Path.Combine(nativeDir, candidate);
                    if (!File.Exists(path))
                        continue;

                    if (!NativeLibrary.TryLoad(path, out var handle))
                        continue;

                    lock (resolved)
                    {
                        resolved[libraryName] = handle;
                    }

                    Console.WriteLine($"[Native] {libraryName} -> {path}");
                    return handle;
                }

                // 交给运行时按默认逻辑报错
                return IntPtr.Zero;
            };
        }

        /// <summary>按 P/Invoke 里写的名字推出可能的文件名（Unix 的 .so 命名习惯）。</summary>
        private static IEnumerable<string> EnumerateNativeNameCandidates(string libraryName)
        {
            yield return libraryName;

            if (libraryName.EndsWith(".so", StringComparison.Ordinal))
                yield break;

            yield return libraryName + ".so";
            yield return "lib" + libraryName + ".so";
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
                sp.GetRequiredService<Application>(),
                sp.GetRequiredService<ILogger<TrayService>>()));

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
