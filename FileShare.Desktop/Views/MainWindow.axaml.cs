using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FileShare.Desktop.ViewModels;
using FileShare.Desktop.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;
using System;
using Avalonia.Media;
using Avalonia.Layout;

namespace FileShare.Desktop.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            //// ����DataContext
            //if (Application.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime appLifetime)
            //{
            //    DataContext = new MainViewModel(appLifetime);
            //}
            
            // 注册消息处理器
            WeakReferenceMessenger.Default.Register<ConfirmationMessage>(this, HandleConfirmationMessage);
        }      

        protected override void OnClosed(EventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.Dispose();
            }
            
            // 取消注册消息处理器
            WeakReferenceMessenger.Default.Unregister<ConfirmationMessage>(this);
            
            base.OnClosed(e);            
        }
        
        private async void HandleConfirmationMessage(object recipient, ConfirmationMessage message)
        {
            // 在Avalonia中，我们需要创建一个自定义的确认对话框
            var dialog = new Window
            {
                Title = message.Title,
                Width = 400,
                Height = 200,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,                
            };
            
            // 创建对话框内容
            var panel = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 20
            };
            
            // 添加消息文本
            var messageText = new TextBlock
            {
                Text = message.Message,
                TextWrapping = TextWrapping.Wrap
            };
            panel.Children.Add(messageText);
            
            // 添加按钮容器
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10
            };
            
            // 添加取消按钮
            var cancelButton = new Button
            {
                Content = "否",
                Width = 80
            };
            cancelButton.Click += (s, e) =>
            {               
                message.CompletionSource.SetResult(false);
                dialog.Close();
            };
            buttonPanel.Children.Add(cancelButton);
            
            // 添加确认按钮
            var confirmButton = new Button
            {
                Content = "是",
                Width = 80
            };
            confirmButton.Click += (s, e) =>
            {               
                message.CompletionSource.SetResult(true);
                dialog.Close();
            };
            buttonPanel.Children.Add(confirmButton);
            
            panel.Children.Add(buttonPanel);
            
            dialog.Content = panel;
            
            // 显示对话框
            await dialog.ShowDialog(this);
        }

    }
}