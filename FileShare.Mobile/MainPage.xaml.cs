using FileShare.Mobile.ViewModels;

namespace FileShare.Mobile;

public partial class MainPage : ContentPage
{
    public MainPage(MainPageViewModel viewModel)
    {
        InitializeComponent();
#if IOS
        this.SafeAreaEdges = SafeAreaEdges.All;
#endif
        // 使用依赖注入获取MainPageViewModel实例
        BindingContext = viewModel;
    }
    
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        
        //// 确保Handler已创建后再设置BindingContext
        //if (Handler != null && BindingContext == null)
        //{
        //    BindingContext = Handler?.MauiContext?.Services.GetRequiredService<MainPageViewModel>();
        //}
    }
    
    // 移除OnDisappearing中的Dispose调用，避免导航时释放资源
    // 资源释放将在应用关闭时处理
}
