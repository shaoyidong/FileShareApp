using Avalonia;
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

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);

            var logger = ServiceProvider?.GetService<ILogger<App>>()
                    ?? throw new InvalidOperationException("ServiceProvider not set");

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

                // 创建 MainViewModel
                var viewModel = new MainViewModel(
                    serviceManager,
                    dialogService,
                    desktop,
                    syncContext,
                    provider.GetRequiredService<ILoggerFactory>()
                );
               
                var mainWindow = provider.GetRequiredService<MainWindow>();
                mainWindow.DataContext = viewModel;
                desktop.MainWindow = mainWindow;

                base.OnFrameworkInitializationCompleted();
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
    }
}