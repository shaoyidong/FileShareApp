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
using Serilog;
using Serilog.Extensions.Logging;
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

                // 配置 Serilog：Debug 输出 + 按天滚动文件；并桥接 Core(Microsoft.Extensions.Logging) 与 Avalonia 内部日志
                var serilogLogger = FileShare.Desktop.Logging.SerilogSetup.CreateLogger(Path.Combine(fileShareDirectory, "logs"));
                FileShare.Desktop.Logging.SerilogSetup.RouteAvaloniaToSerilog(serilogLogger);
                Log.Logger = serilogLogger;

                // 通过 Microsoft.Extensions.Logging.Abstractions 接口把 Serilog 注入 Core（保持 Core 不直接依赖 Serilog）
                using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
                {
                    builder.AddSerilog(serilogLogger, dispose: true);
                });

                // TLS 加密传输配置：启用自签名证书 + 指纹 TOFU 信任策略。
                // 证书与指纹库持久化到应用数据目录，对同样启用 TLS 的对端自动升级到 SslStream，未启用者保持裸 TCP。
                var tlsOptions = new FileShare.Core.Network.Tls.TlsOptions
                {
                    Enabled = true,
                    CertificateDirectory = Path.Combine(fileShareDirectory, "tls"),
                    FingerprintStorePath = Path.Combine(fileShareDirectory, "tls", "fingerprints.txt")
                };

                // 创建服务管理器实例（启用 TLS 加密 + mDNS 发现补充）
                var serviceManager = new FileShareServiceManager(
                    new DesktopDirectoryService(),
                    databaseService,
                    Environment.MachineName,
                    FileShare.Core.Models.DeviceType.Desktop,
                    loggerFactory: loggerFactory,
                    tlsOptions: tlsOptions,
                    enableMdns: true);
                
                // 创建对话框服务实例
                var dialogService = new DialogService(desktop);

                // 应用退出时刷新 Serilog 缓冲
                desktop.ShutdownRequested += (s, e) => Log.CloseAndFlush();
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