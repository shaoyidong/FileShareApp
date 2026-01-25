using System;
using Microsoft.Maui.Devices;

namespace FileShare.Mobile.Helpers
{
    /// <summary>
    /// 设备类型辅助类，用于判断是否为平板设备。
    /// 逻辑：
    /// 1. 优先使用 DeviceInfo.Idiom == DeviceIdiom.Tablet。
    /// 2. 否则根据屏幕最小边的 dp 值判断，threshold = 600 dp。
    /// </summary>
    public static class DeviceTypeHelper
    {
        private const double TabletDpThreshold = 600.0;

        public static bool IsTablet()
        {
            try
            {
                // 优先使用 Idiom（更可靠且跨平台）
                if (DeviceInfo.Idiom == DeviceIdiom.Tablet)
                    return true;

                // 使用屏幕尺寸作为备用判断（宽或高的较小值达到阈值则视为平板）
                var display = DeviceDisplay.MainDisplayInfo;
                // display.Width / display.Density => dp 宽度
                double widthDp = display.Width / display.Density;
                double heightDp = display.Height / display.Density;
                double smallestDp = Math.Min(widthDp, heightDp);

                return smallestDp >= TabletDpThreshold;
            }
            catch
            {
                // 任何异常时返回 false（保守判断）
                return false;
            }
        }
    }
}