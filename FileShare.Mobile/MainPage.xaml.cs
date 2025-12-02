using FileShare.Mobile.ViewModels;

namespace FileShare.Mobile;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
        
        // 设置DataContext
        BindingContext = new MainPageViewModel();
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
