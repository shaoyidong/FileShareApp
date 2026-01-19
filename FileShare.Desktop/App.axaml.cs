using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FileShare.Desktop.Services;
using FileShare.Desktop.ViewModels;
using FileShare.Desktop.Views;
using System;
using System.Diagnostics;
using System.Linq;

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
                
                // 创建服务管理器实例
                var serviceManager = new FileShare.Core.Services.FileShareServiceManager(
                    new FileShare.Core.Services.DesktopDirectoryService(),
                    Environment.MachineName,
                    FileShare.Core.Models.DeviceType.Desktop);
                
                // 创建对话框服务实例
                var dialogService = new DialogService(desktop);
                
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(serviceManager, dialogService, desktop),
                };
            }

            base.OnFrameworkInitializationCompleted();
        }

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