using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using Font = Microsoft.Maui.Font;

namespace FileShare.Mobile.Services
{
    public class AlertService : IAlertService
    {
        public async Task DisplayAlertAsync(string title, string message, string accept)
        {
            // Get the current MainPage (ensure it’s not null)
            var mainPage = Application.Current?.Windows?.FirstOrDefault()?.Page;
            if (mainPage == null)
                throw new InvalidOperationException("MainPage is not set. Ensure the app has a MainPage.");

            await mainPage.DisplayAlertAsync(title, message, accept);
        }

        public async Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel)
        {
            var mainPage = Application.Current?.Windows?.FirstOrDefault()?.Page;
            if (mainPage == null)
                throw new InvalidOperationException("MainPage is not set. Ensure the app has a MainPage."); 
            return await mainPage.DisplayAlertAsync(title, message, accept, cancel);
        }

        public async Task DisplayToastAsync(string message,double textSize = 14)
        {
            var toast = Toast.Make(message,textSize:textSize);
            await toast.Show();
        }

        public async Task DisplaySnackbarAsync(string message, Action? action = null,
        string actionButtonText = "OK",
        TimeSpan? duration = null)
        {

            //var snackbarOptions = new SnackbarOptions
            //{
            //    BackgroundColor = Color.FromArgb("#FF3300"),
            //    TextColor = Colors.White,
            //    ActionButtonTextColor = Colors.Yellow,
            //    CornerRadius = new CornerRadius(0),
            //    Font = Font.SystemFontOfSize(18),
            //    ActionButtonFont = Font.SystemFontOfSize(14)
            //};

            var snackbar = Snackbar.Make(message,action,actionButtonText,duration);

            await snackbar.Show();
        }
    }
}
