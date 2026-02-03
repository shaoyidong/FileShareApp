using CommunityToolkit.Mvvm.Messaging.Messages;
using System;
using System.Collections.Generic;
using System.Text;

namespace FileShare.Mobile.Messages
{
    // 消息类，用于传递APK路径
    public class AppSelectedMessage : ValueChangedMessage<string>
    {
        public AppSelectedMessage(string apkPath) : base(apkPath)
        {
        }
    }
}
