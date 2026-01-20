using System;
using System.Collections.Generic;
using System.Text;

namespace FileShare.Mobile.Services
{
    public interface IAlertService
    {
        // For alerts with "OK" only (no cancellation)
        Task DisplayAlertAsync(string title, string message, string accept);

        // For confirmation alerts (with "Accept" and "Cancel" buttons)
        Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel);
    }
}
