using FileShare.Core.Models;
using FileShare.Core.Services;

namespace FileShare.Core.Tests.Services;

/// <summary>
/// 设置模型与 JSON 持久化测试。
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void Normalize_FixesInvalidValues()
    {
        var settings = new AppSettings
        {
            DeviceName = "   ",
            TransferPort = 80,
            SaveDirectory = "   ",
            FavoriteDeviceIds = new List<string> { "a", "a", " ", "b" }
        };

        settings.Normalize();

        Assert.Equal(Environment.MachineName, settings.DeviceName);
        Assert.Equal(5237, settings.TransferPort);
        Assert.Null(settings.SaveDirectory);
        Assert.Equal(new[] { "a", "b" }, settings.FavoriteDeviceIds);
    }

    [Fact]
    public void Normalize_TruncatesLongDeviceName()
    {
        var settings = new AppSettings { DeviceName = new string('x', 200) };

        settings.Normalize();

        Assert.Equal(64, settings.DeviceName.Length);
    }

    [Fact]
    public void ToggleFavorite_AddsAndRemovesDevice()
    {
        var settings = new AppSettings();

        Assert.False(settings.IsFavorite("device-1"));
        Assert.True(settings.ToggleFavorite("device-1"));
        Assert.True(settings.IsFavorite("device-1"));
        Assert.False(settings.ToggleFavorite("device-1"));
        Assert.False(settings.IsFavorite("device-1"));
        Assert.False(settings.IsFavorite(null));
        Assert.False(settings.ToggleFavorite(""));
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var settings = new AppSettings { DeviceName = "origin" };
        settings.FavoriteDeviceIds.Add("device-1");

        var clone = settings.Clone();
        clone.DeviceName = "changed";
        clone.FavoriteDeviceIds.Add("device-2");

        Assert.Equal("origin", settings.DeviceName);
        Assert.Single(settings.FavoriteDeviceIds);
    }

    [Fact]
    public void JsonAppSettingsService_PersistsAndReloads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fileshare-settings-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");

        try
        {
            var service = new JsonAppSettingsService(path);

            service.Update(settings =>
            {
                settings.DeviceName = "我的电脑";
                settings.DeviceType = DeviceType.Mobile;
                settings.TransferPort = 5300;
                settings.TlsEnabled = false;
                settings.AutoAcceptFromFavorites = true;
                settings.SaveDirectory = @"D:\接收";
                settings.Skin = AppSkin.WinXP;
                settings.ToggleFavorite("peer-id");
            });

            Assert.True(File.Exists(path));

            var reloaded = new JsonAppSettingsService(path).Current;
            Assert.Equal("我的电脑", reloaded.DeviceName);
            Assert.Equal(DeviceType.Mobile, reloaded.DeviceType);
            Assert.Equal(5300, reloaded.TransferPort);
            Assert.False(reloaded.TlsEnabled);
            Assert.True(reloaded.AutoAcceptFromFavorites);
            Assert.Equal(@"D:\接收", reloaded.SaveDirectory);
            Assert.Equal(AppSkin.WinXP, reloaded.Skin);
            Assert.True(reloaded.IsFavorite("peer-id"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void JsonAppSettingsService_UsesDefaultsWhenFileIsCorrupted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fileshare-settings-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{ this is not json ");

            var service = new JsonAppSettingsService(path);

            Assert.Equal(5237, service.Current.TransferPort);
            Assert.False(service.Reload());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
