using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using System;
using System.IO;

namespace FileShare.Desktop.Helpers;


/// <summary>
/// 从 <c>Assets/logo.ico</c> 里取出 PNG 帧作为托盘图标。
/// <para>SkiaSharp 不解码 .ico 容器，但本项目的 ico 第一帧就是 PNG，直接按 ICO 目录取出该帧即可。</para>
/// </summary>
internal static class AppIconLoader
{
    private const string IconUri = "avares://FileShare.Desktop/Assets/logo.ico";

    public static WindowIcon? LoadWindowIcon()
    {
        try
        {
            using var asset = AssetLoader.Open(new Uri(IconUri));
            using var buffer = new MemoryStream();
            asset.CopyTo(buffer);
            var bytes = buffer.ToArray();

            var pngOffset = FindPngFrame(bytes, out var pngLength);
            if (pngOffset >= 0 && pngLength > 0)
            {
                return new WindowIcon(new MemoryStream(bytes, pngOffset, pngLength, writable: false));
            }

            return new WindowIcon(new MemoryStream(bytes, writable: false));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 在 ICO 容器里找到第一个 PNG 帧。
    /// </summary>
    private static int FindPngFrame(byte[] bytes, out int length)
    {
        length = 0;

        if (bytes.Length < 22 || bytes[0] != 0 || bytes[1] != 0 || bytes[2] != 1 || bytes[3] != 0)
        {
            return -1;
        }

        var count = BitConverter.ToUInt16(bytes, 4);
        for (var index = 0; index < count; index++)
        {
            var entry = 6 + index * 16;
            if (entry + 16 > bytes.Length)
            {
                break;
            }

            var size = BitConverter.ToInt32(bytes, entry + 8);
            var offset = BitConverter.ToInt32(bytes, entry + 12);
            if (size <= 8 || offset <= 0 || offset + size > bytes.Length)
            {
                continue;
            }

            if (bytes[offset] == 0x89 && bytes[offset + 1] == 0x50 && bytes[offset + 2] == 0x4E && bytes[offset + 3] == 0x47)
            {
                length = size;
                return offset;
            }
        }

        return -1;
    }
}
