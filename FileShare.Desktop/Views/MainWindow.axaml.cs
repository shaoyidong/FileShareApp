using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FileShare.Desktop.ViewModels;
using System;

namespace FileShare.Desktop.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            //// …Ë÷√DataContext
            //if (Application.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime appLifetime)
            //{
            //    DataContext = new MainWindowViewModel(appLifetime);
            //}
        }      

        protected override void OnClosed(EventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.Dispose();
            }
            base.OnClosed(e);            
        }

    }
}