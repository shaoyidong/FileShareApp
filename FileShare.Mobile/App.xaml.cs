using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace FileShare.Mobile;

public partial class App : Microsoft.Maui.Controls.Application
{
	public App()
	{
		InitializeComponent();
		// 使用AppShell作为主页面，而不是在CreateWindow中直接设置
		MainPage = new AppShell();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = base.CreateWindow(activationState);
		
		// 配置窗口大小和行为
		if (window != null)
		{
			window.MinimumHeight = 600;
			window.MinimumWidth = 800;
			
			// 配置iOS平台特定设置（在AppShell中设置）
			if (window.Page != null)
			{
				window.Page.On<iOS>().SetUseSafeArea(true);
			}
		}
		
		return window;
	}
}