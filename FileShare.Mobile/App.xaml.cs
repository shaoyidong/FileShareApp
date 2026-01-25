using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace FileShare.Mobile;

public partial class App : Microsoft.Maui.Controls.Application
{
	public App()
	{
		InitializeComponent();       
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