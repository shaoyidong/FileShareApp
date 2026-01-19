using FileShare.Mobile.ViewModels;

namespace FileShare.Mobile;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
        
        // 使用依赖注入获取MainPageViewModel实例
        BindingContext = Handler?.MauiContext?.Services.GetRequiredService<MainPageViewModel>();
    }
    
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        
        // 确保Handler已创建后再设置BindingContext
        if (Handler != null && BindingContext == null)
        {
            BindingContext = Handler?.MauiContext?.Services.GetRequiredService<MainPageViewModel>();
        }
    }
    
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        
        // 释放资源
        if (BindingContext is MainPageViewModel viewModel)
        {
            viewModel.Dispose();
        }
    }
}
