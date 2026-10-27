using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FileShare.Core.Services;
using FileShare.Desktop.Services;
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
        public static IServiceProvider? ServiceProvider { get; internal set; }

        /// <summary>系统托盘服务（在主窗口创建时初始化）。</summary>
        public static TrayService? TrayService { get; private set; }

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);

            // 如果是设计时（预览器），直接返回，不执行依赖注入的代码
            if (Design.IsDesignMode)
            {
                return;
            }

            var logger = ServiceProvider?.GetService<ILogger<App>>()
                    ?? throw new InvalidOperationException("ServiceProvider not set");

            // 应用用户上次选择的皮肤（Win7 / WinXP）。必须在窗口创建前完成，避免首帧闪烁。
            try
            {
                var uiPreferences = ServiceProvider?.GetService<IUiPreferencesService>();

                // 语言必须在任何视图加载之前设置
                FileShare.Desktop.Localization.Loc.SetLanguage(uiPreferences?.Current.Language ?? FileShare.Desktop.Localization.AppLanguage.ZhCN);
                var themeService = ServiceProvider?.GetService<IThemeService>();
                if (themeService is not null)
                {
                    themeService.Apply(uiPreferences?.Current.Skin ?? AppSkin.Win7);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "应用界面皮肤失败，沿用默认皮肤");
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    logger.LogCritical(ex, "Unhandled AppDomain exception");
                else
                    logger.LogCritical("Unhandled AppDomain exception (non-Exception object): {Obj}", e.ExceptionObject);               
            };

            Dispatcher.UIThread.UnhandledException += (s, e) =>
            {
                e.Handled = true;
                logger.LogError(e.Exception, "Unhandled UI thread exception");
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
                logger.LogError(e.Exception, "Unobserved task exception");
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

                var provider = ServiceProvider ?? throw new InvalidOperationException("ServiceProvider not initialized");
                DataTemplates.Add(new ViewLocator(provider));

                // 解析 MainViewModel 的依赖（除了 DialogService）
                // 因为 DialogService 需要 desktop，我们手动创建并传入
                var serviceManager = provider.GetRequiredService<FileShareServiceManager>();
                var dialogService = new DialogService(desktop);
                var syncContext = SynchronizationContext.Current ?? new SynchronizationContext();

                // 托盘服务必须在拿到 lifetime 之后创建；MainViewModel 需要它来恢复窗口
                TrayService = new TrayService(desktop);

                // 创建 MainViewModel
                var viewModel = new MainViewModel(
                    serviceManager,
                    dialogService,
                    desktop,
                    syncContext,
                    provider.GetRequiredService<ILoggerFactory>(),
                    provider.GetRequiredService<IAppSettingsService>(),
                    provider.GetRequiredService<IThemeService>(),
                    provider.GetRequiredService<IToastService>()
                );
                // 网页分享（按设置决定是否启动；失败只记录日志，不影响主流程）
                if (TrayService is not null)
                {
                    try
                    {
                        provider.GetRequiredService<IWebShareHost>().Apply();
                    }
                    catch (Exception ex)
                    {
                        provider.GetRequiredService<ILogger<App>>().LogWarning(ex, "启动网页分享失败");
                    }
                }


                var mainWindow = provider.GetRequiredService<MainWindow>();
                mainWindow.DataContext = viewModel;
                desktop.MainWindow = mainWindow;              

                desktop.ShutdownRequested += (s, e) =>
                {
                    if (TrayService is not null)
                    {
                        TrayService.IsExiting = true;
                    }

                    serviceManager.StopServicesAsync();
                };

                desktop.Exit += (s, e) =>
                {
                    TrayService?.Dispose();
                    TrayService = null;
                };

                // 冒烟自检：`FileShare.Desktop.exe --smoke` 会自动把三个页面各渲染一遍后退出，
                // 用于在没有人工点击的环境里验证 XAML / 皮肤 / 依赖注入是否正常。
                if (desktop.Args?.Contains("--smoke") == true)
                {
                    RunSmokeTest(desktop, viewModel);
                }
            }

            base.OnFrameworkInitializationCompleted();
        }

        [UnconditionalSuppressMessage("Trim", "IL2026")]
        private void DisableAvaloniaDataAnnotationValidation()
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
        /// 逐页渲染自检：主界面 → 历史 → 设置 → 回到主界面，然后以 0 退出。
        /// </summary>
        private static void RunSmokeTest(IClassicDesktopStyleApplicationLifetime desktop, MainViewModel viewModel)
        {
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
                        viewModel.CurrentView = viewModel;
                        break;
                    default:
                        timer.Stop();
                        desktop.Shutdown(0);
                        break;
                }
            };
            timer.Start();
        }
    }
}
