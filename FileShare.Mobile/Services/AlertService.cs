using System;
using System.Collections.Generic;
using System.Text;

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
    }
}
