
using FileShare.Core.Services;
using Microsoft.Extensions.Logging;

namespace FileShare.Mobile;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly IFileShareServiceManager _serviceManager;
    private readonly ILogger _logger;
    private bool _isShuttdown = false;
    public App(IFileShareServiceManager serviceManager, ILoggerFactory loggerFactory)
	{
		InitializeComponent();
        _serviceManager = serviceManager;
        _logger = loggerFactory.CreateLogger<App>();
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            if (_isShuttdown) return;            
            _serviceManager.StopServicesAsync();
            _isShuttdown = true;
        };
    }

	protected override Window CreateWindow(IActivationState? activationState)
    {
        // 显式创建根 Window，根 Page 使用 AppShell（Shell 内已包含 MainPage）
        var window = new Window(new AppShell());

        // 仅在桌面平台设置最小尺寸
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            window.MinimumHeight = 600;
            window.MinimumWidth = 800;
        }

        window.Destroying += (s, e) =>
        {
            if (_isShuttdown) return;
            _serviceManager.StopServicesAsync();
            _isShuttdown = true;
        };

        return window;
    }    

    protected override void OnSleep()
    {
        base.OnSleep();
    }

    protected override void OnResume()
    {
        base.OnResume();
    }
    
}