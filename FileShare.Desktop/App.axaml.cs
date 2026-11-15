using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FileShare.Core.Services;
using FileShare.Desktop.Services;
using FileShare.Desktop.Services.Dialogs;
using FileShare.Desktop.Services.Navigation;
using FileShare.LocalSend;
using FileShare.Desktop.ViewModels;
using FileShare.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileShare.Desktop
{
    public partial class App : Application
    {
        private readonly IServiceProvider? _serviceProvider;
        private readonly ILogger<App> _logger;

        /// <summary>运行时构造：容器由 Program 在启动时经 <see cref="AppBuilder.Configure{T}(Func{T})"/> 注入。</summary>
        public App(IServiceProvider? serviceProvider)
        {
            _serviceProvider = serviceProvider;
            _logger = _serviceProvider?.GetService<ILogger<App>>()
                   ?? throw new InvalidOperationException("ServiceProvider not set");
        }

        /// <summary>设计器 / 预览器使用的无参构造。</summary>
        public App() : this(null)
        {
        }

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);

            // 如果是设计时（预览器），直接返回，不执行依赖注入的代码
            if (Design.IsDesignMode)
            {
                return;
            }           

            // 应用用户上次选择的主题。必须在窗口创建前完成，避免首帧闪烁。
            try
            {
                var dataDirectory = FileShare.Core.Services.AppPaths.GetDataDirectory();
                var uiPreferences = _serviceProvider?.GetService<IUiPreferencesService>();

                // 语言包 / 主题包都来自外部 JSON 文件（发布后可直接编辑 / 新增），
                // 必须在任何视图加载之前初始化
                FileShare.Desktop.Localization.Loc.Initialize(dataDirectory);
                FileShare.Desktop.Localization.Loc.SetLanguage(uiPreferences?.Current.LanguageCode ?? "zh-CN");
                FileShare.Desktop.Theming.ThemePackLoader.Initialize(dataDirectory);

                // 关于窗口内容同样来自外部配置：内置默认 -> 数据目录 about.json -> 远程 JSON。
                // 后台拉取失败会静默沿用旧内容，不阻塞启动。
                FileShare.Desktop.Services.AboutContentService.Initialize(dataDirectory);
                _ = FileShare.Desktop.Services.AboutContentService.RefreshAsync();

                var themeService = _serviceProvider?.GetService<IThemeService>();
                // 摘除旧版静态声明残留的皮肤实例（窗口创建前完成）
                themeService?.InitializeFromApplication(this);
                
                themeService?.Apply(uiPreferences?.Current.SkinId ?? "win7");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "应用界面皮肤失败，沿用默认皮肤");
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    _logger.LogCritical(ex, "Unhandled AppDomain exception");
                else
                    _logger.LogCritical("Unhandled AppDomain exception (non-Exception object): {Obj}", e.ExceptionObject);               
            };

            Dispatcher.UIThread.UnhandledException += (s, e) =>
            {
                e.Handled = true;
                _logger.LogError(e.Exception, "Unhandled UI thread exception");
            };

            // 3. (可选) 过滤特定UI异常
            //Dispatcher.UIThread.UnhandledExceptionFilter += (s, e) =>
            //{
            //    if (e.Exception is OperationCanceledException)
            //    {
            //        e.RequestCatch = false;
            //    }
            //};

            // 4. 捕获未观察到的Task异常
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                _logger.LogError(e.Exception, "Unobserved task exception");
                e.SetObserved();
            };

            // 5. (可选) 如果使用ReactiveUI
            // RxApp.DefaultExceptionHandler = Observer.Create<Exception>(ex => {
            //     logger.LogError(ex, "ReactiveUI exception");
            // });
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
                // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
                DisableAvaloniaDataAnnotationValidation();

                var provider = _serviceProvider
                    ?? throw new InvalidOperationException("IServiceProvider not injected");

                // 先解析 RegionManager：它在构造时订阅 Region.HostRegistered，
                // 必须早于 MainWindow 的 XAML 加载（区域附加属性在 InitializeComponent 时触发注册）
                var regionManager = provider.GetRequiredService<IRegionManager>();

                // MainViewModel 由容器构造（原手工 new + 静态 App.MainViewModel 已移除）
                var viewModel = provider.GetRequiredService<MainViewModel>();
                var serviceManager = provider.GetRequiredService<IFileShareServiceManager>();

                // 网页分享 / LocalSend 互通（按设置启动，失败只记日志）
                try
                {
                    provider.GetRequiredService<IWebShareHost>().Apply();
                    provider.GetRequiredService<ILocalSendInteropHost>().Apply();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "启动网页分享 / LocalSend 互通失败");
                }

                var mainWindow = provider.GetRequiredService<MainWindow>();
                mainWindow.DataContext = viewModel;
                desktop.MainWindow = mainWindow;

                var trayService = provider.GetRequiredService<ITrayService>();

                // 启动导航到主界面（区域内容初始为空，必须显式导航）
                regionManager.RequestNavigate(NavigationPages.MainRegion, NavigationPages.Main);

                desktop.ShutdownRequested += (s, e) =>
                {
                    trayService.IsExiting = true;

                    serviceManager.StopServicesAsync();
                };

                desktop.Exit += (s, e) =>
                {
                    trayService.Dispose();
                };

                // 冒烟自检：`FileShare.Desktop.exe --smoke` 会自动把三个页面各渲染一遍后退出，
                // 用于在没有人工点击的环境里验证 XAML / 皮肤 / 依赖注入是否正常。
                if (desktop.Args?.Contains("--smoke") == true)
                {
                    RunSmokeTest(desktop, viewModel, provider);
                }
            }

            base.OnFrameworkInitializationCompleted();
        }

        [UnconditionalSuppressMessage("Trim", "IL2026")]
        private static void DisableAvaloniaDataAnnotationValidation()
        {
            // Get an array of plugins to remove
            var dataValidationPluginsToRemove =
                BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

            // remove each entry found
            foreach (var plugin in dataValidationPluginsToRemove)
            {
                BindingPlugins.DataValidators.Remove(plugin);
            }
        }

        /// <summary>
        /// 自检：主界面 → 历史 → 设置 → 按主题 id 切换（winxp → win7）→ 重载主题包
        /// → 恢复原主题 → 回到主界面，然后以 0 退出。
        /// 用于验证 AOT / TrimMode=full 下编译期皮肤实例与 JSON 配色包都能正常工作。
        /// </summary>
        private static void RunSmokeTest(
            IClassicDesktopStyleApplicationLifetime desktop, MainViewModel viewModel, IServiceProvider provider)
        {
            var themeService = provider.GetService<IThemeService>();
            var originalTheme = themeService?.Current ?? "win7";
            var step = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (_, _) =>
            {
                step++;
                switch (step)
                {
                    case 1:
                        viewModel.ShowHistoryCommand.Execute(null);
                        break;
                    case 2:
                        viewModel.ShowSettingsCommand.Execute(null);
                        break;
                    case 3:
                        themeService?.Apply("winxp");
                        LogSkinState("winxp");
                        break;
                    case 4:
                        themeService?.Apply("win7");
                        LogSkinState("win7");
                        break;
                    case 5:
                        var loaded = themeService?.ReloadThemes() ?? 0;
                        LogSkinState($"reload({loaded})");
                        break;
                    case 6:
                        // 恢复用户原本的主题，避免冒烟自检改写偏好；经区域导航回到主界面
                        themeService?.Apply(originalTheme);
                        LogSkinState("Restored");
                        provider.GetRequiredService<IRegionManager>()
                            .RequestNavigate(NavigationPages.MainRegion, NavigationPages.Main);
                        break;
                    case 7:
                        // DialogService 弹窗自检：先开内容弹窗（UserControl + DialogWindow 宿主，
                        // 按名称 / 按 VM 类型打开），再开关于窗口并让它保持活跃，
                        // 供 case 8 截图 + case 9 验证失活收起
                        var dialogs = provider.GetRequiredService<IDialogService>();
                        dialogs.Show(SendTextViewModel.DialogKeys.DialogName);
                        // 非激活内容弹窗（ShowActivated=false 由 VM 配置宿主）：验证宿主配置路径
                        dialogs.Show<MessageReceivedViewModel>(new DialogParameters
                        {
                            [MessageReceivedViewModel.DialogKeys.Message] = "冒烟自检：文本消息内容弹窗"
                        });
                        dialogs.Show<AboutViewModel>();
                        LogSkinState("about-shown");
                        break;
                    case 8:
                        // 先给关于窗口截图存盘，再模拟「点击主窗口」（激活主窗口 → 关于窗失活应自动收起）
                        CaptureAboutWindow(desktop);
                        desktop.MainWindow?.Activate();
                        break;
                    case 9:
                        // 回归验证：主窗口不能被最小化/隐藏（Avalonia#4868 家族），关于窗应已收起
                        var leftover = desktop.Windows.OfType<Views.AboutWindow>().FirstOrDefault();
                        var mainAfterActivate = desktop.MainWindow;
                        LogSkinState(
                            $"after-activate: about-closed={leftover is null}; main-state=" +
                            (mainAfterActivate is null
                                ? "none"
                                : $"{mainAfterActivate.WindowState},visible={mainAfterActivate.IsVisible}"));
                        leftover?.Close();

                        // 关闭内容弹窗宿主（冒烟里打开的 SendTextDialog），保持退出干净
                        foreach (var host in desktop.Windows.OfType<DialogWindow>().ToList())
                        {
                            host.Close();
                        }

                        break;
                    default:
                        timer.Stop();
                        desktop.Shutdown(0);
                        break;
                }
            };
            timer.Start();
        }

        /// <summary>冒烟自检用：给关于窗口截图存盘（保持窗口打开，由后续步骤模拟点击主窗口触发失活关闭）。</summary>
        private static void CaptureAboutWindow(IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                var about = desktop.Windows.OfType<Views.AboutWindow>().FirstOrDefault();
                if (about is null)
                {
                    LogSkinState("about-missing");
                    return;
                }

                var size = new PixelSize(Math.Max(1, (int)about.Bounds.Width), Math.Max(1, (int)about.Bounds.Height));
                using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
                bitmap.Render(about);
                bitmap.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fs-about-smoke.png"));
                LogSkinState("about-captured");
            }
            catch (Exception ex)
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fs-skin-smoke.txt"),
                    $"about-capture-failed: {ex.GetType().Name} {ex.Message}\n");
            }
        }

        /// <summary>冒烟自检用：记录当前生效的皮肤数量与主背景色，便于验证切换是否真的生效。</summary>
        private static void LogSkinState(string step)
        {
            try
            {
                var app = Application.Current;
                if (app is null) return;

                var skinCount = app.Styles.Count(s =>
                    s is FileShare.Desktop.Themes.Win7Skin
                        or FileShare.Desktop.Themes.WinXPSkin
                        or FileShare.Desktop.Themes.WinXPOliveSkin
                    || s.GetType() == typeof(Avalonia.Styling.Styles));
                var color = app.TryGetResource("FsWindowBrush", null, out var value) &&
                            value is Avalonia.Media.ISolidColorBrush brush
                    ? brush.Color.ToString()
                    : "none";

                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fs-skin-smoke.txt"),
                    $"{step}: skins={skinCount} brush={color}\n");
            }
            catch { /* 自检日志失败不影响运行 */ }
        }
    }
}
