using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Maui.ApplicationModel;
using Manifest = Android.Manifest;

namespace FileShare.Mobile.Platforms.Android
{
    [SupportedOSPlatform("android23.0")]
    public class InstallPackagePermission : Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions
        {
            get
            {
                // 返回 Android 权限字符串，isRuntime 设为 false 表示这是一个安装时权限或特殊权限
                return new[] { (Manifest.Permission.RequestInstallPackages, false) };
                 // 直接使用权限字符串，避免引用 Android.Manifest 常量
                //return new[] { ("android.permission.REQUEST_INSTALL_PACKAGES", false) };
            }
        }
    }
}
