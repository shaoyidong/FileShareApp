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
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;

namespace FileShare.Desktop
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {                
                // 记录异常
                Debug.WriteLine(e.ExceptionObject.ToString());
            };

            Dispatcher.UIThread.UnhandledException += (s, e) =>
            {
                // 处理UI线程异常
                e.Handled = true;
                Debug.WriteLine(e.Exception.ToString());
            };
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
                // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
                DisableAvaloniaDataAnnotationValidation();
                
                // 创建数据库服务实例
                var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var fileShareDirectory = Path.Combine(appDataDirectory, "FileShare");
                var databasePath = Path.Combine(fileShareDirectory, "fileshare.db");
                var databaseService = new DatabaseService(databasePath);

                // 创建日志工厂（Debug 配置输出到调试器，Release 不附加提供程序即静默）
                using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
                {
#if DEBUG
                    builder.AddDebug();
                    builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);
#else
                    builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
#endif
                });

                // 创建服务管理器实例
                var serviceManager = new FileShareServiceManager(
                    new DesktopDirectoryService(),
                    databaseService,
                    Environment.MachineName,
                    FileShare.Core.Models.DeviceType.Desktop,
                    loggerFactory: loggerFactory);
                
                // 创建对话框服务实例
                var dialogService = new DialogService(desktop);
                
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(serviceManager, dialogService, desktop, SynchronizationContext.Current??new SynchronizationContext()),
                };
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